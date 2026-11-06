using System.Globalization;
using System.Text.RegularExpressions;
using Buddy.Core.Repo;

namespace Buddy.Core.Vault;

/// <summary>One step in the life of a task.</summary>
public sealed record TimelineEvent(DateOnly Date, string Icon, string Text);

/// <summary>
/// The life of a task in one list, from what is already around: the board (where it is now, created, cost, PR, back
/// from review), the notes that talk about it (analyses, checklists, screenshots), the journal days that mention it and
/// the commits whose message carries its id.
/// </summary>
public static partial class TaskTimeline
{
    public static async Task<IReadOnlyList<TimelineEvent>> BuildAsync(string id, ProjectBoard board, VaultIndex vault, GitRepo? repo,
        string? journalPath, string projectPath, CancellationToken cancellationToken = default)
    {
        var events = new List<TimelineEvent>();
        var mention = new Regex($@"(?<![\w-]){Regex.Escape(id)}(?!\d)", RegexOptions.IgnoreCase);

        // The board: every column the id appears in (a task moves through the flow notes).
        foreach (var column in board.Columns)
        {
            foreach (var item in column.Items.Where(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                if (item.Date is { } date)
                {
                    var verb = column.Key switch
                    {
                        "fatti" => "Chiuso",
                        "rifiutati" => "Rifiutato",
                        "controllare" => "In «Da controllare»",
                        _ => $"In «{column.Title}»",
                    };
                    events.Add(new TimelineEvent(date, column.Icon, verb + (item.Cost is { } cost ? $" · costo Claude {cost.ToString("0.00", CultureInfo.GetCultureInfo("it-IT"))} $" : "")));
                }

                if (item.Tags.FirstOrDefault(t => t.StartsWith("PR", StringComparison.OrdinalIgnoreCase)) is { } pr && item.Date is { } prDate)
                {
                    events.Add(new TimelineEvent(prDate, "🔀", pr));
                }

                if (item.Rework > 0 && item.Date is { } reworkDate)
                {
                    events.Add(new TimelineEvent(reworkDate, "🔁", $"Tornato indietro dalla revisione {item.Rework} volt{(item.Rework == 1 ? "a" : "e")}"));
                }
            }
        }

        // Notes about the task (outside the flow notes): analyses, checklists, estimates…
        var flowNotes = board.Columns.Select(c => c.NotePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var note in vault.Notes.Where(n => !flowNotes.Contains(n.RelativePath) && (mention.IsMatch(n.Title) || mention.IsMatch(n.Content))))
        {
            var date = Updated(note.Content) ?? FileDate(Path.Combine(projectPath, note.RelativePath));
            if (date is null)
            {
                continue;
            }

            var icon = note.RelativePath.StartsWith("Test manuali", StringComparison.OrdinalIgnoreCase) ? "🧪"
                : note.RelativePath.StartsWith("Analisi", StringComparison.OrdinalIgnoreCase) ? "🔎" : "📄";
            events.Add(new TimelineEvent(date.Value, icon, $"Nota «{note.Title}»"));
        }

        // Screenshots of the browser checks.
        var shots = Path.Combine(projectPath, "Test web");
        if (Directory.Exists(shots))
        {
            foreach (var day in Directory.EnumerateFiles(shots).Where(f => mention.IsMatch(Path.GetFileName(f)))
                         .GroupBy(f => DateOnly.FromDateTime(File.GetLastWriteTime(f))))
            {
                events.Add(new TimelineEvent(day.Key, "🌐", $"{day.Count()} screenshot della verifica nel browser"));
            }
        }

        // Journal days that mention it.
        var diary = string.IsNullOrWhiteSpace(journalPath) ? null : Path.Combine(journalPath, "Diario");
        if (diary is not null && Directory.Exists(diary))
        {
            foreach (var file in Directory.EnumerateFiles(diary, "*.md"))
            {
                if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && mention.IsMatch(SafeRead(file)))
                {
                    events.Add(new TimelineEvent(day, "📓", "Ne hai parlato nel diario"));
                }
            }
        }

        // Commits with the id in the message.
        if (repo is { IsAvailable: true })
        {
            foreach (var (date, subject) in await repo.GetCommitsMentioningAsync(id, cancellationToken))
            {
                events.Add(new TimelineEvent(date, "⎇", $"Commit: {subject}"));
            }
        }

        return events.Distinct().OrderBy(e => e.Date).ThenBy(e => e.Icon, StringComparer.Ordinal).ToList();
    }

    private static DateOnly? Updated(string content) =>
        UpdatedLine().Match(content) is { Success: true } m && DateOnly.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var date) ? date : null;

    private static DateOnly? FileDate(string path) => File.Exists(path) ? DateOnly.FromDateTime(File.GetLastWriteTime(path)) : null;

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"^updated:\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Multiline)]
    private static partial Regex UpdatedLine();
}
