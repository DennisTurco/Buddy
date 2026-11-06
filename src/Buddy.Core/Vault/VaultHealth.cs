namespace Buddy.Core.Vault;

/// <summary>One thing to tidy in the vault; <paramref name="Target"/> is the note to open (null: nothing to open).</summary>
public sealed record HealthIssue(string Icon, string Text, string? Target)
{
    /// <summary>For a broken link: the title linked, and a note with a near-identical name to link instead.</summary>
    public string? MissingTitle { get; init; }

    public string? Suggestion { get; init; }

    public bool IsBrokenLink => MissingTitle is not null && Target is not null;

    public bool HasSuggestion => IsBrokenLink && Suggestion is not null;
}

/// <summary>
/// Small checks that keep the vault useful: links to notes that do not exist, notes nobody links to, tasks to do
/// without a "Fatto quando" (nobody knows when they are finished) and manual checklists left half done for days.
/// </summary>
public static class VaultHealth
{
    /// <summary>Folders whose notes are not meant to be linked (screenshots, archives…).</summary>
    private static readonly string[] UnlinkedFolders = ["Test web", "Test manuali", "Appunti", "Archivio", "Retro", "Diario"];

    public const int StaleChecklistDays = 7;

    /// <param name="elsewhere">Note titles in the rest of the Obsidian vault: Obsidian resolves links there too.</param>
    public static IReadOnlyList<HealthIssue> Check(ProjectBoard board, NoteGraph graph, IReadOnlyList<ManualTestDoc> checklists, DateOnly today,
        int max = 8, IReadOnlySet<string>? elsewhere = null)
    {
        var issues = new List<HealthIssue>();

        // Links to notes not created: the note that links them is the one to fix.
        foreach (var missing in graph.Nodes.Where(n => n.IsMissing && elsewhere?.Contains(n.Title) != true))
        {
            var from = graph.Edges.Where(e => e.From == missing.Id || e.To == missing.Id)
                .Select(e => e.From == missing.Id ? e.To : e.From)
                .FirstOrDefault();
            var source = from is null ? null : graph.Nodes.FirstOrDefault(n => n.Id == from);
            var titles = graph.Nodes.Where(n => !n.IsMissing).Select(n => n.Title).Concat(elsewhere ?? (IEnumerable<string>)[]);
            issues.Add(new HealthIssue("🔗", $"Link rotto: [[{missing.Title}]]{(source is null ? "" : $" in {source.Title}")}", source?.Id)
            {
                MissingTitle = missing.Title,
                Suggestion = Text.Similarity.Closest(missing.Title, titles),
            });
        }

        // Tasks to do without a "Fatto quando".
        foreach (var item in board.Columns.Where(c => c.Key == "todo").SelectMany(c => c.Items)
                     .Where(i => i.Id is not null && !i.Done && !HasDoneWhen(i)))
        {
            issues.Add(new HealthIssue("🎯", $"{item.Id} senza «Fatto quando»", board.Columns.First(c => c.Key == "todo").NotePath));
        }

        // Checklists started and then left.
        foreach (var doc in checklists.Where(d => d.Total > 0 && d.Done < d.Total && d.Updated is { } updated
                                                  && today.DayNumber - updated.DayNumber >= StaleChecklistDays))
        {
            issues.Add(new HealthIssue("🧪", $"{doc.Title}: {doc.Done}/{doc.Total} casi, ferma da {today.DayNumber - doc.Updated!.Value.DayNumber} giorni", doc.RelativePath));
        }

        // Notes nobody links to (outside the folders that are fine on their own).
        foreach (var orphan in graph.Nodes.Where(n => !n.IsMissing && n.Degree == 0
                                                      && !UnlinkedFolders.Any(f => n.Id.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase))))
        {
            issues.Add(new HealthIssue("🏝", $"Nota isolata: {orphan.Title} (nessun link da o verso altre note)", orphan.Id));
        }

        return issues.Take(max).ToList();
    }

    /// <summary>How many issues there are in all (the list shown is cut at a few).</summary>
    public static int Count(ProjectBoard board, NoteGraph graph, IReadOnlyList<ManualTestDoc> checklists, DateOnly today) =>
        Check(board, graph, checklists, today, int.MaxValue).Count;

    /// <summary>
    /// Titles of the notes in the Obsidian vault around <paramref name="projectPath"/> (the folder holding ".obsidian"),
    /// outside the project; empty when the project is not inside a vault.
    /// </summary>
    public static IReadOnlySet<string> TitlesElsewhere(string projectPath)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vault = Directory.Exists(projectPath) ? new DirectoryInfo(projectPath).Parent : null;
        while (vault is not null && !Directory.Exists(Path.Combine(vault.FullName, ".obsidian")))
        {
            vault = vault.Parent;
        }

        if (vault is null)
        {
            return titles;
        }

        try
        {
            var project = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(vault.FullName, "*.md", SearchOption.AllDirectories))
            {
                if (!file.StartsWith(project, StringComparison.OrdinalIgnoreCase) && !file.Contains(Path.DirectorySeparatorChar + ".", StringComparison.Ordinal))
                {
                    titles.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read: some links may be reported as broken.
        }

        return titles;
    }

    private static bool HasDoneWhen(BoardItem item) =>
        item.Details.Any(d => d.Contains("Fatto quando", StringComparison.OrdinalIgnoreCase))
        || item.Raw.Contains("Fatto quando", StringComparison.OrdinalIgnoreCase)
        || item.Field("fatto quando", "fattoquando", "done") is not null;
}
