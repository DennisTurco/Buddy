using System.Text;
using System.Text.RegularExpressions;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Context;

/// <summary>
/// Builds the vault excerpt sent to the local model: today's date, the always-included notes and,
/// for every task id found in the question, the blocks that describe it. Deterministic, no embeddings.
/// </summary>
public sealed class ContextBuilder(
    VaultIndex vault,
    IOptions<BuddySettings> options,
    TimeProvider clock,
    KnowledgeIndex? knowledge = null)
{
    private readonly BuddySettings _settings = options.Value;

    /// <param name="question">The user's question; task ids found in it select the vault blocks.</param>
    /// <param name="currentTaskId">Task of the current branch, used when the question names none.</param>
    /// <param name="attachment">Text the user attached (clipboard, dropped file); it comes first in the context.</param>
    public BuiltContext Build(string question, string? currentTaskId = null, string? attachment = null)
    {
        var budget = new Budget(_settings.MaxContextChars);
        var sb = new StringBuilder();
        sb.AppendLine($"Oggi: {clock.GetLocalNow():dddd d MMMM yyyy}");
        var sources = new List<ContextSource>();

        if (!string.IsNullOrWhiteSpace(attachment))
        {
            // At most half of the budget, so the vault still has room.
            var text = attachment.Length > _settings.MaxContextChars / 2 ? attachment[..(_settings.MaxContextChars / 2)] + "\n[…tagliato]" : attachment;
            budget.TryAppend(sb, $"\n## ALLEGATO\n{text}\n");
            sources.Add(new ContextSource("allegato", null));
        }

        if (currentTaskId is not null)
        {
            sb.AppendLine($"Task del ramo git corrente: {currentTaskId}");
        }

        foreach (var section in knowledge?.Search(question) ?? [])
        {
            if (budget.Used < _settings.MaxContextChars * 0.6
                && budget.TryAppend(sb, $"\n## {section.RelativePath} › {section.Heading}\n{section.Text}\n"))
            {
                sources.Add(new ContextSource($"{section.RelativePath} › {section.Heading}",
                    section.FullPath.Length > 0 ? Links.File(section.FullPath) : null));
            }
        }

        if (!vault.IsAvailable)
        {
            sb.AppendLine($"(Cartella del progetto non trovata: {_settings.ProjectPath})");
            return Finish(sb, sources, budget);
        }

        var ids = FindIds(question).ToList();

        // "la 2367" means the branch task NUSR-2367 when the number matches.
        if (currentTaskId is not null && !ids.Contains(currentTaskId)
            && currentTaskId.Split('-').LastOrDefault() is { Length: > 0 } number
            && Regex.IsMatch(question, $@"(?<![\w-]){Regex.Escape(number)}\b"))
        {
            ids.Add(currentTaskId);
        }

        // No task named: the branch task is background, useful to the model but not a source of the answer.
        var background = ids.Count == 0;
        if (background && currentTaskId is not null)
        {
            ids.Add(currentTaskId);
        }

        foreach (var id in ids)
        {
            foreach (var block in vault.FindBlocks(id))
            {
                if (budget.TryAppend(sb, $"\n## {block.RelativePath} ({id})\n{block.Text}\n"))
                {
                    sources.Add(new ContextSource($"{block.RelativePath} ({id})", Links.Obsidian(Path.Combine(_settings.ProjectPath, block.RelativePath)), background));
                }
            }
        }

        foreach (var relativePath in _settings.AlwaysIncludedNotes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var note = vault.Find(relativePath);
            if (note is not null && budget.TryAppend(sb, $"\n## {note.RelativePath}\n{note.Content}\n"))
            {
                sources.Add(new ContextSource(note.RelativePath, Links.Obsidian(Path.Combine(_settings.ProjectPath, note.RelativePath)), Background: true));
            }
        }

        return Finish(sb, sources, budget);
    }

    /// <summary>
    /// With nothing from the notes, small models fill the gap with generic encouragement: say plainly that the
    /// context is empty, so the answer is "not in the notes" instead.
    /// </summary>
    private static BuiltContext Finish(StringBuilder sb, List<ContextSource> sources, Budget budget)
    {
        if (sources.Count == 0)
        {
            sb.AppendLine(NoNotes);
        }

        return new BuiltContext(sb.ToString(), sources, budget.Truncated);
    }

    public const string NoNotes =
        "(Nessuna nota del progetto riguarda questa domanda: rispondi che nelle note non trovi niente e suggerisci "
        + "di nominare un task per ID o di chiedere a Claude con /c. Non dare consigli generici.)";

    private IEnumerable<string> FindIds(string question) =>
        Regex.Matches(question, _settings.TaskIdPattern, RegexOptions.IgnoreCase)
            .Select(m => m.Value.ToUpperInvariant())
            .Distinct();

    private sealed class Budget(int maxChars)
    {
        private int _used;

        public bool Truncated { get; private set; }

        public int Used => _used;

        public bool TryAppend(StringBuilder sb, string text)
        {
            var left = maxChars - _used;
            if (left <= 200)
            {
                Truncated = true;
                return false;
            }

            if (text.Length > left)
            {
                text = string.Concat(text.AsSpan(0, left), "\n[…tagliato]\n");
                Truncated = true;
            }

            sb.Append(text);
            _used += text.Length;
            return true;
        }
    }
}

public sealed record BuiltContext(string Text, IReadOnlyList<ContextSource> Sources, bool Truncated)
{
    /// <summary>
    /// A "Fonti:" line with one link per note or guideline section picked for this question (task named in it,
    /// matching guideline sections), or null when there are none. Background context is not listed.
    /// </summary>
    public string? SourcesMarkdown()
    {
        var linked = Sources.Where(s => s.Link is not null && !s.Background).DistinctBy(s => s.Link).ToList();
        return linked.Count == 0 ? null : "Fonti: " + string.Join(" · ", linked.Select(s => $"[{s.Label}]({s.Link})"));
    }
}

/// <summary>
/// A piece of the context: the label shown to the user and a link that opens it (Obsidian or file).
/// <paramref name="Background"/>: always added (branch task not named, always-included notes), not shown as a source.
/// </summary>
public sealed record ContextSource(string Label, string? Link, bool Background = false);

public static class Links
{
    public static string Obsidian(string fullPath) => "obsidian://open?path=" + Uri.EscapeDataString(fullPath);

    public static string File(string fullPath) => new Uri(fullPath).AbsoluteUri;
}
