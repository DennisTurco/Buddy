using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>What the user fills in to add an item to the board by hand; which fields count depends on the column.</summary>
public sealed record NewEntry
{
    public string Title { get; init; } = "";

    /// <summary>Free text of an appunto: one nested line per line written.</summary>
    public string Details { get; init; } = "";

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Empty: the next free id (prossimo_id of To-Do).</summary>
    public string Id { get; init; } = "";

    public string Priority { get; init; } = "P2";

    public string Estimate { get; init; } = "1h";

    public string Type { get; init; } = "";

    public string Context { get; init; } = "";

    public string Where { get; init; } = "";

    public string DoneWhen { get; init; } = "";

    public string AssignedBy { get; init; } = "";

    /// <summary>⏸: what the task waits for; when empty, <see cref="Until"/> is used.</summary>
    public string WaitingFor { get; init; } = "";

    public DateOnly? Until { get; init; }

    public string Changed { get; init; } = "";

    public string Files { get; init; } = "";

    public string HowToTest { get; init; } = "";

    public string Commit { get; init; } = "";
}

/// <summary>
/// Board items written by hand, in the same format Claude uses (the flow template's examples): an appunto in
/// To-do Appunti, a task (or ⏸ task) in To-Do, an item in Da controllare. Ids come from the prossimo_id property of
/// To-Do, the single counter of the T-… ids.
/// </summary>
public static partial class BoardEntries
{
    /// <summary>The columns that can be filled in by hand, and the section of their note where items go.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>
    {
        ["appunti"] = "Appunti",
        ["todo"] = "Task",
        ["attesa"] = "Task",
        ["controllare"] = "Voci",
    };

    public static readonly string[] Priorities = ["P1", "P2", "P3"];

    /// <summary>"- 2026-11-06 · Titolo [tag:: bug, ui]" and the details as nested lines.</summary>
    public static string Appunto(NewEntry entry, DateOnly day)
    {
        var text = new StringBuilder($"- {day:yyyy-MM-dd} · {Line(entry.Title)}{TagField(entry.Tags)}");
        foreach (var line in Lines(entry.Details))
        {
            text.Append($"\n  - {line}");
        }

        return text.ToString();
    }

    /// <summary>"- [ ] T-014 · 2026-11-06 · Titolo [priorita:: P2] [stima:: 1h] [tag:: feature]" with Contesto, Dove…</summary>
    public static string Task(NewEntry entry, string id, DateOnly day)
    {
        var text = new StringBuilder($"- [ ] {id} · {day:yyyy-MM-dd} · {Line(entry.Title)}");
        text.Append(Field("priorita", entry.Priority)).Append(Field("stima", entry.Estimate)).Append(TagField(entry.Tags)).Append(Field("tipo", entry.Type));
        AppendDetail(text, "Contesto", entry.Context);
        AppendDetail(text, "Dove", entry.Where);
        AppendDetail(text, "Fatto quando", entry.DoneWhen);
        AppendDetail(text, "Assegnato da", entry.AssignedBy);
        return text.ToString();
    }

    /// <summary>"- [ ] T-015 · ⏸ Titolo — In attesa di: X [priorita:: P3] [stima:: 1h]", or [fino:: date] without X.</summary>
    public static string WaitingTask(NewEntry entry, string id)
    {
        var text = new StringBuilder($"- [ ] {id} · ⏸ {Line(entry.Title)}");
        if (Line(entry.WaitingFor) is { Length: > 0 } waitingFor)
        {
            text.Append($" — In attesa di: {waitingFor}");
        }
        else if (entry.Until is { } until)
        {
            text.Append(Field("fino", until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        text.Append(Field("priorita", entry.Priority)).Append(Field("stima", entry.Estimate)).Append(TagField(entry.Tags));
        AppendDetail(text, "Contesto", entry.Context);
        AppendDetail(text, "Fatto quando", entry.DoneWhen);
        return text.ToString();
    }

    /// <summary>"- [ ] T-001 · Titolo [tag:: …]" with Cambiato, File, Come testare, Commit.</summary>
    public static string ToReview(NewEntry entry, string id)
    {
        var text = new StringBuilder($"- [ ] {id} · {Line(entry.Title)}{TagField(entry.Tags)}");
        AppendDetail(text, "Cambiato", entry.Changed);
        AppendDetail(text, "File", entry.Files);
        AppendDetail(text, "Come testare", entry.HowToTest);
        AppendDetail(text, "Commit", entry.Commit.Length > 0 && !entry.Commit.Contains('`') ? $"`{Line(entry.Commit)}`" : entry.Commit);
        return text.ToString();
    }

    /// <summary>
    /// The next free T-… id: prossimo_id of To-Do, moved past any id already used in the board (a hand-written one
    /// could have taken it).
    /// </summary>
    public static string NextId(string? todoContent, IEnumerable<string?> usedIds)
    {
        var counter = todoContent is not null && NextIdRegex().Match(todoContent) is { Success: true } m ? Number(m.Groups["id"].Value) : 1;
        var highest = usedIds.Select(id => id is not null && TaskIdRegex().IsMatch(id) ? Number(id) : 0).DefaultIfEmpty(0).Max();
        return Format(Math.Max(counter, highest + 1));
    }

    /// <summary>To-Do with prossimo_id set to the id after <paramref name="used"/> (added to the front matter if missing).</summary>
    public static string AdvanceNextId(string todoContent, string used)
    {
        if (!TaskIdRegex().IsMatch(used))
        {
            return todoContent; // a tracker key (ABC-123) does not use the counter
        }

        var next = Format(Number(used) + 1);
        var current = NextIdRegex().Match(todoContent);
        if (current.Success)
        {
            return Number(current.Groups["id"].Value) > Number(used)
                ? todoContent
                : todoContent[..current.Groups["id"].Index] + next + todoContent[(current.Groups["id"].Index + current.Groups["id"].Length)..];
        }

        var newline = todoContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return todoContent.StartsWith("---", StringComparison.Ordinal) && todoContent.IndexOf(newline + "---", 3, StringComparison.Ordinal) is var end and > 0
            ? todoContent[..end] + newline + "prossimo_id: " + next + todoContent[end..]
            : "---" + newline + "prossimo_id: " + next + newline + "---" + newline + newline + todoContent;
    }

    /// <summary>Tags typed in a box: separated by commas or spaces, without "#", no duplicates.</summary>
    public static IReadOnlyList<string> ParseTags(string text) =>
        text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('#').Replace("[", "").Replace("]", ""))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string TagField(IReadOnlyList<string> tags) => tags.Count == 0 ? "" : Field("tag", string.Join(", ", tags));

    private static string Field(string key, string? value) =>
        Line(value ?? "").Replace("[", "").Replace("]", "") is { Length: > 0 } clean ? $" [{key}:: {clean}]" : "";

    private static void AppendDetail(StringBuilder text, string label, string value)
    {
        if (Line(value) is { Length: > 0 } line)
        {
            text.Append($"\n  - {label}: {line}");
        }
    }

    /// <summary>One line: the note's list syntax would break on a newline.</summary>
    private static string Line(string text) => string.Join(' ', Lines(text));

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int Number(string id) => int.Parse(id[2..], CultureInfo.InvariantCulture);

    private static string Format(int n) => $"T-{n:000}";

    [GeneratedRegex(@"^prossimo_id:\s*(?<id>T-\d+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex NextIdRegex();

    [GeneratedRegex(@"^T-\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex TaskIdRegex();
}
