using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// The project's flow notes read as a board: one column per flow note (Appunti, Da valutare, To-Do, In attesa,
/// Da controllare, Fatti, Rifiutati), with the items parsed from their list lines
/// ("- [ ] T-004 · 2026-10-15 · Titolo — Dubbio: … [rivedere:: 2026-10-29]"), plus the "Stato attuale" summary.
/// Notes that do not exist are left out, so a vault without this flow simply gets fewer columns.
/// </summary>
public sealed partial record ProjectBoard(
    IReadOnlyList<BoardColumn> Columns,
    IReadOnlyList<StatusLine> Status,
    IReadOnlyList<VaultNote> Notes)
{
    /// <summary>Flow notes, in the order an item travels through them. Matched by file name, ignoring case.</summary>
    private static readonly (string Key, string Note, string Title, string Icon)[] Flow =
    [
        ("appunti", "To-do Appunti", "Appunti", "📝"),
        ("valutare", "Da valutare", "Da valutare", "🤔"),
        ("todo", "To-Do", "Da fare", "📋"),
        ("attesa", "To-Do", "In attesa", "⏸"),
        ("controllare", "Da controllare", "Da controllare", "🔍"),
        ("fatti", "To-Do Fatti", "Fatti", "✅"),
        ("rifiutati", "To-Do Rifiutati", "Rifiutati", "🚫"),
    ];

    public const string StatusNote = "Stato attuale";

    public static ProjectBoard Load(VaultIndex vault, string taskIdPattern)
    {
        var notes = vault.Notes.OrderBy(n => n.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var idPattern = new Regex($"^(?:{taskIdPattern})$", RegexOptions.IgnoreCase);

        var columns = new List<BoardColumn>();
        foreach (var (key, noteName, title, icon) in Flow)
        {
            var note = FindNote(notes, noteName);
            if (note is null)
            {
                continue;
            }

            var items = ParseItems(note.Content, idPattern).ToList();
            items = key switch
            {
                "todo" => items.Where(i => !i.Waiting).ToList(),
                "attesa" => items.Where(i => i.Waiting).ToList(),
                _ => items,
            };
            columns.Add(new BoardColumn(key, title, icon, note.RelativePath, items));
        }

        var status = FindNote(notes, StatusNote) is { } statusNote ? ParseStatus(statusNote.Content) : [];
        return new ProjectBoard(columns, status, notes);
    }

    private static VaultNote? FindNote(IEnumerable<VaultNote> notes, string name) =>
        notes.Where(n => string.Equals(n.Title, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.RelativePath.Count(c => c is '/' or '\\')) // the project's own note before a namesake deeper down
            .FirstOrDefault();

    /// <summary>The top-level list items of a note, skipping its front matter, callouts (format examples) and code.</summary>
    public static IEnumerable<BoardItem> ParseItems(string content, Regex idPattern)
    {
        foreach (var block in MarkdownBlocks.Split(Body(content)))
        {
            if (Item(block, idPattern) is { } item)
            {
                yield return item;
            }
        }
    }

    /// <summary>"- **Versione:** testo" lines of the status note, as label and markdown value.</summary>
    public static IReadOnlyList<StatusLine> ParseStatus(string content) =>
        Body(content).Split('\n')
            .Select(line => StatusLineRegex().Match(line))
            .Where(m => m.Success)
            .Select(m => new StatusLine(m.Groups["label"].Value.Trim(), m.Groups["value"].Value.Trim()))
            .ToList();

    /// <summary>The note without front matter, callouts and fenced code.</summary>
    public static string Body(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var start = 0;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            start = end < 0 ? 0 : end + 1;
        }

        var kept = new List<string>();
        var inFence = false;
        var inCallout = false;
        foreach (var line in lines.Skip(start))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('>'))
            {
                inCallout = true;
                continue;
            }

            // An indented line right after a callout is a continuation of its example (seen in "Da controllare").
            if (inCallout && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            inCallout = false;
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                kept.Add(line);
            }
        }

        return string.Join('\n', kept);
    }

    private static BoardItem? Item(MarkdownBlock block, Regex idPattern)
    {
        var match = ItemLineRegex().Match(block.FirstLine);
        if (!match.Success)
        {
            return null;
        }

        var mark = match.Groups["mark"].Value;
        var text = match.Groups["text"].Value;

        // Dataview inline fields: [fino:: 2026-11-01], [rivedere:: 2026-10-29].
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = FieldRegex().Replace(text, m =>
        {
            fields[m.Groups["key"].Value.Trim()] = m.Groups["value"].Value.Trim();
            return "";
        }).Trim();

        var rework = 0;
        text = ReworkRegex().Replace(text, m =>
        {
            rework = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            return "";
        }).Trim().TrimEnd('·').TrimEnd();

        string? id = null;
        DateOnly? date = null;
        var waiting = false;
        var rest = new List<string>();
        foreach (var raw in text.Split(" · ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw;
            if (part.StartsWith('⏸'))
            {
                waiting = true;
                part = part.TrimStart('⏸').Trim();
                if (part.Length == 0)
                {
                    continue;
                }
            }

            if (id is null && rest.Count == 0 && idPattern.IsMatch(part))
            {
                id = part.ToUpperInvariant();
            }
            else if (date is null && rest.Count == 0 && DateOnly.TryParseExact(part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                date = parsed;
            }
            else
            {
                rest.Add(part);
            }
        }

        if (rest.Count == 0)
        {
            return null;
        }

        // "Titolo — Dubbio: …": the part after the dash is a note on the item, not its title.
        var title = rest[0];
        string? note = null;
        var dash = title.IndexOf(" — ", StringComparison.Ordinal);
        if (dash > 0)
        {
            note = title[(dash + 3)..].Trim();
            title = title[..dash].Trim();
        }

        DateOnly? due = null;
        string? dueLabel = null;
        foreach (var (key, label) in new[] { ("fino", "fino al"), ("rivedere", "rivedere il") })
        {
            if (fields.TryGetValue(key, out var value)
                && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                due = parsed;
                dueLabel = label;
                break;
            }
        }

        var details = block.Text.Split('\n').Skip(1)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal) ? l[2..] : l)
            .ToList();

        return new BoardItem(id, date, title, note, rest.Skip(1).ToList(), details,
            Done: mark is "x" or "X", Rejected: mark == "-", waiting, due, dueLabel, rework, block.Text, fields);
    }

    [GeneratedRegex(@"^[-*] (?:\[(?<mark>[ xX-])\] )?(?<text>.+)$")]
    private static partial Regex ItemLineRegex();

    [GeneratedRegex(@"\[(?<key>[^\[\]:]+)::(?<value>[^\]]*)\]")]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"🔁\s*(?<n>\d+)")]
    private static partial Regex ReworkRegex();

    [GeneratedRegex(@"^[-*] \*\*(?<label>[^*]+?):?\*\*:?\s*(?<value>.*)$")]
    private static partial Regex StatusLineRegex();
}

public sealed record BoardColumn(string Key, string Title, string Icon, string NotePath, IReadOnlyList<BoardItem> Items);

/// <param name="Tags">Extra " · " parts after the title (e.g. "PR #4", "costo: 7,22 $").</param>
/// <param name="Details">The nested lines, without their bullet.</param>
/// <param name="Due">"fino" (waiting until) or "rivedere" (review by) date, described by <paramref name="DueLabel"/>.</param>
/// <param name="Rework">How many times the item came back from review (🔁).</param>
/// <param name="Fields">Dataview inline fields ([priorita:: P1], [stima:: 2h], [consiglio:: 🟢]…), keys as written.</param>
/// <param name="Raw">The item's lines as written in the note, to edit it in place (<see cref="VaultWriter.ReplaceBlock"/>).</param>
public sealed partial record BoardItem(
    string? Id,
    DateOnly? Date,
    string Title,
    string? Note,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Details,
    bool Done,
    bool Rejected,
    bool Waiting,
    DateOnly? Due,
    string? DueLabel,
    int Rework,
    string Raw = "",
    IReadOnlyDictionary<string, string>? Fields = null)
{
    /// <summary>A field by name, ignoring case and accents typed or not ("priorita" finds "priorità").</summary>
    public string? Field(params string[] names) =>
        Fields?.FirstOrDefault(f => names.Any(n => string.Equals(Plain(f.Key), Plain(n), StringComparison.OrdinalIgnoreCase))).Value is { Length: > 0 } value
            ? value
            : null;

    /// <summary>"costo: 7,22 $" among the tags, in dollars.</summary>
    public decimal? Cost =>
        Tags.Select(t => CostRegex().Match(t)).FirstOrDefault(m => m.Success) is { } match
        && decimal.TryParse(match.Groups["n"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost)
            ? cost
            : null;

    private static string Plain(string text) =>
        new(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

    public bool IsDue(DateOnly today) => Due is { } due && due <= today;

    public bool Matches(string filter) =>
        string.IsNullOrWhiteSpace(filter)
        || new[] { Id, Title, Note }.Concat(Tags).Concat(Details)
            .Any(s => s?.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase) == true);

    [GeneratedRegex(@"costo:\s*(?<n>\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CostRegex();
}

public sealed record StatusLine(string Label, string Value);
