using System.Text;
using Buddy.Core.Repo;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Chat;

/// <summary>
/// Instant, deterministic answers (no model involved): task status, today's notes, vault search, changed files,
/// and the prompts for the commit/PR drafts that are then sent to Claude.
/// </summary>
public sealed class AssistantCommands(VaultIndex vault, GitRepo repo, IOptions<BuddySettings> options)
{
    private const int MaxSearchResults = 20;

    private BuddySettings Settings => options.Value;

    /// <summary>Short description of a task for the tooltip and the bubble: "NUSR-1 · title (Da controllare)".</summary>
    public TaskInfo? DescribeTask(string? id)
    {
        if (id is null)
        {
            return null;
        }

        var block = vault.IsAvailable ? vault.FindBlocks(id).FirstOrDefault(b => b.Text.TrimStart().StartsWith('-') || b.Text.StartsWith('#')) : null;
        if (block is null)
        {
            return new TaskInfo(id, null, null);
        }

        var title = block.Text.Split('\n')[0].TrimStart('-', ' ', '#').Replace("[ ]", "").Replace("[x]", "").Trim();
        var separator = title.IndexOf('·');
        if (separator >= 0)
        {
            title = title[(separator + 1)..].Trim();
        }

        var bracket = title.IndexOf(" [", StringComparison.Ordinal);
        if (bracket > 0)
        {
            title = title[..bracket];
        }

        return new TaskInfo(id, title, Path.GetFileNameWithoutExtension(block.RelativePath));
    }

    public string Status(string id)
    {
        var blocks = vault.FindBlocks(id);
        if (blocks.Count == 0)
        {
            return $"Non trovo **{id}** nelle note del progetto.";
        }

        const int maxBlockChars = 900;
        var sb = new StringBuilder();
        foreach (var block in blocks.Take(3))
        {
            var text = block.Text.Length > maxBlockChars ? block.Text[..maxBlockChars] + " …" : block.Text;
            sb.AppendLine($"**{Path.GetFileNameWithoutExtension(block.RelativePath)}** — [apri]({ObsidianUri(block.RelativePath)})")
                .AppendLine()
                .AppendLine(text)
                .AppendLine();
        }

        if (blocks.Count > 3)
        {
            sb.AppendLine($"…e altre {blocks.Count - 3} citazioni (`/cerca {id}`).");
        }

        return sb.ToString().TrimEnd();
    }

    public string Today()
    {
        var sb = new StringBuilder();
        foreach (var relative in Settings.AlwaysIncludedNotes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (vault.Find(relative) is { } note)
            {
                sb.AppendLine($"**{note.Title}** — [apri]({ObsidianUri(note.RelativePath)})").AppendLine().AppendLine(note.Content).AppendLine();
            }
        }

        return sb.Length > 0 ? sb.ToString().TrimEnd() : "Nessuna nota configurata in \"Note sempre incluse\".";
    }

    public string Search(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "Scrivi cosa cercare: `/cerca parole`.";
        }

        var hits = new List<(VaultNote Note, string Line)>();
        foreach (var note in vault.Notes)
        {
            foreach (var line in note.Content.Split('\n'))
            {
                if (words.All(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    hits.Add((note, line.Trim()));
                }
            }
        }

        if (hits.Count == 0)
        {
            return $"Nessun risultato per \"{query}\".";
        }

        var sb = new StringBuilder($"**{hits.Count} risultati** per \"{query}\"\n\n");
        foreach (var (note, line) in hits.Take(MaxSearchResults))
        {
            var text = line.Length > 160 ? line[..160] + "…" : line;
            sb.AppendLine($"- [{note.Title}]({ObsidianUri(note.RelativePath)}): {text.TrimStart('-', ' ')}");
        }

        if (hits.Count > MaxSearchResults)
        {
            sb.AppendLine($"\n…e altri {hits.Count - MaxSearchResults}.");
        }

        return sb.ToString();
    }

    public async Task<string> ChangesAsync()
    {
        if (!repo.IsAvailable)
        {
            return $"Repository non trovato: `{repo.RootPath}`.";
        }

        var changes = await repo.GetChangesAsync();
        return ChangeGroups.ToMarkdown(ChangeGroups.Group(changes, Settings.Repo.Groups), await repo.GetBranchAsync());
    }

    /// <summary>Prompt for Claude with the diff, or null (and a message) when there is nothing to describe.</summary>
    public async Task<(string? Prompt, string? Message)> CommitPromptAsync(string? taskId) =>
        await DraftPromptAsync(Settings.Repo.CommitInstructions, taskId, includeTemplate: false);

    public async Task<(string? Prompt, string? Message)> PullRequestPromptAsync(string? taskId) =>
        await DraftPromptAsync(Settings.Repo.PullRequestInstructions, taskId, includeTemplate: true);

    private async Task<(string? Prompt, string? Message)> DraftPromptAsync(string instructions, string? taskId, bool includeTemplate)
    {
        if (!repo.IsAvailable)
        {
            return (null, $"Repository non trovato: `{repo.RootPath}`.");
        }

        if ((await repo.GetChangesAsync()).Count == 0)
        {
            return (null, "Nessuna modifica rispetto all'ultimo commit.");
        }

        var sb = new StringBuilder();
        sb.AppendLine(instructions.Replace("[ID]", taskId is null ? "[ID]" : $"[{taskId}]"));
        sb.AppendLine("Rispondi solo con il testo pronto da copiare. Non usare strumenti, non eseguire comandi git, non fare commit.");
        if (taskId is not null && DescribeTask(taskId) is { Title: { } title })
        {
            sb.AppendLine($"Task: {taskId} — {title}");
        }

        if (includeTemplate && repo.ReadPullRequestTemplate() is { } template)
        {
            sb.AppendLine().AppendLine("TEMPLATE DELLA PR:").AppendLine(template);
        }

        sb.AppendLine().AppendLine("MODIFICHE:").AppendLine(await repo.GetDiffAsync(maxChars: 40_000));
        return (sb.ToString(), null);
    }

    public string ObsidianUri(string relativePath) =>
        "obsidian://open?path=" + Uri.EscapeDataString(Path.Combine(Settings.ProjectPath, relativePath));
}

public sealed record TaskInfo(string Id, string? Title, string? Note)
{
    public override string ToString() =>
        Title is null ? Id : Note is null ? $"{Id} · {Title}" : $"{Id} · {Title} ({Note})";
}
