using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// "✏ Modifica" on a card with the same form used to add it: the item read back into a <see cref="NewEntry"/>, and
/// the edited entry written over the item. Only what the form shows is rewritten; everything else on the item (other
/// fields, " · PR #4" parts, 🔁, the date, nested lines the form does not know such as Accordi or Costo) stays as it
/// was, and a form field left untouched keeps its lines exactly as written.
/// </summary>
public static partial class BoardEdits
{
    /// <summary>The columns whose items open in the form; the others (Fatti, Rifiutati) are edited as text.</summary>
    public static readonly IReadOnlySet<string> Editable = new HashSet<string> { "appunti", "valutare", "todo", "attesa", "corso", "controllare" };

    private static readonly string[] TaskLabels = ["Contesto", "Dove", "Fatto quando", "Assegnato da"];
    private static readonly string[] ReviewLabels = ["Cambiato", "File", "Come testare", "Commit"];

    /// <summary>Fields the form writes on the first line; any other field is kept.</summary>
    private static readonly string[] FormFields = ["priorita", "priorità", "priority", "stima", "estimate", "tag", "tipo", "type", "fino"];

    private const string WaitingPrefix = "In attesa di:";

    /// <summary>The form that edits an item of <paramref name="columnKey"/>: appunti, controllare or a task (todo).</summary>
    public static string FormKind(string columnKey) => columnKey switch
    {
        "appunti" => "appunti",
        "controllare" => "controllare",
        _ => "todo",
    };

    /// <summary>The item as the form shows it.</summary>
    public static NewEntry ToEntry(BoardItem item, string columnKey)
    {
        var blocks = DetailBlocks(item.Raw);
        string Detail(string label) => blocks.FirstOrDefault(b => b.Label == label)?.Value ?? "";
        var waitingNote = item.Note is { } n && n.StartsWith(WaitingPrefix, StringComparison.OrdinalIgnoreCase);
        return new NewEntry
        {
            Id = item.Id ?? "",
            Title = item.Title,
            Note = waitingNote ? "" : item.Note ?? "",
            Details = FormKind(columnKey) == "appunti" ? string.Join('\n', blocks.Select(b => b.Text)) : "",
            Tags = item.TagList,
            Priority = item.Field("priorita", "priority") ?? "",
            Estimate = item.Field("stima", "estimate") ?? "",
            Type = item.Field("tipo", "type") ?? "",
            Context = Detail("Contesto"),
            Where = Detail("Dove"),
            DoneWhen = Detail("Fatto quando"),
            AssignedBy = Detail("Assegnato da"),
            Waiting = item.Waiting,
            WaitingFor = waitingNote ? item.Note![WaitingPrefix.Length..].Trim() : "",
            Until = item.Field("fino") is { } until && DateOnly.TryParseExact(until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null,
            Changed = Detail("Cambiato"),
            Files = Detail("File"),
            HowToTest = Detail("Come testare"),
            Commit = Detail("Commit"),
        };
    }

    /// <summary>
    /// The item's new text: <paramref name="after"/> over <paramref name="item"/>. <paramref name="before"/> is what the
    /// form showed (<see cref="ToEntry"/>): a nested field equal to it keeps its original lines.
    /// </summary>
    public static string Apply(BoardItem item, string columnKey, NewEntry before, NewEntry after)
    {
        var kind = FormKind(columnKey);
        var lines = item.Raw.Replace("\r\n", "\n").Split('\n');
        var text = new StringBuilder(PatchFirstLine(item, lines[0], columnKey, before, after) ?? FirstLine(item, lines[0], columnKey, after));

        var blocks = DetailBlocks(item.Raw);
        if (kind == "appunti")
        {
            if (Normalize(before.Details) == Normalize(after.Details))
            {
                blocks.ForEach(b => text.Append('\n').Append(b.Raw));
            }
            else
            {
                if (Detail(null, after.Details) is { } details)
                {
                    text.Append('\n').Append(details);
                }
            }

            return text.ToString();
        }

        var labels = kind == "controllare" ? ReviewLabels : TaskLabels;
        var values = labels.ToDictionary(l => l, l => (Before: Value(before, l), After: Value(after, l)));
        var written = new HashSet<string>();
        foreach (var block in blocks)
        {
            if (block.Label is { } label && values.TryGetValue(label, out var value))
            {
                written.Add(label);
                if (Normalize(value.Before) == Normalize(value.After))
                {
                    text.Append('\n').Append(block.Raw);
                }
                else if (Detail(label, value.After) is { } replaced)
                {
                    text.Append('\n').Append(replaced);
                }
            }
            else
            {
                text.Append('\n').Append(block.Raw);
            }
        }

        foreach (var label in labels.Where(l => !written.Contains(l)))
        {
            if (Detail(label, values[label].After) is { } added)
            {
                text.Append('\n').Append(added);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The first line with only what changed replaced in place (title, note, the form's fields, ⏸), so the rest keeps
    /// its order and spacing; null when a changed part cannot be found as written (then the line is rebuilt).
    /// </summary>
    private static string? PatchFirstLine(BoardItem item, string original, string columnKey, NewEntry before, NewEntry after)
    {
        var line = original.TrimEnd('\r');
        var title = Clean(after.Title);
        if (title != item.Title)
        {
            if (title.Length == 0 || !ReplaceOnce(ref line, item.Title, title))
            {
                return null;
            }
        }

        var oldNote = item.Note ?? "";
        var note = NoteOf(item, columnKey, after);
        if (note != oldNote)
        {
            if (oldNote.Length > 0
                ? !ReplaceOnce(ref line, " — " + oldNote, note.Length > 0 ? " — " + note : "")
                : !ReplaceOnce(ref line, title, $"{title} — {note}"))
            {
                return null;
            }
        }

        foreach (var (aliases, oldValue, newValue) in FieldChanges(item, columnKey, before, after))
        {
            if (Clean(oldValue).Replace("[", "").Replace("]", "") == Clean(newValue).Replace("[", "").Replace("]", ""))
            {
                continue;
            }

            var field = new Regex(@"\s?\[(?:" + string.Join('|', aliases.Select(Regex.Escape)) + @")::[^\]]*\]", RegexOptions.IgnoreCase).Match(line);
            var written = BoardEntries.Field(aliases[0], newValue);
            line = field.Success
                ? line[..field.Index] + written + line[(field.Index + field.Length)..]
                : line + written;
        }

        var waiting = columnKey is "todo" or "attesa" or "corso" ? after.Waiting : item.Waiting;
        if (waiting != item.Waiting)
        {
            line = BoardMoves.SetState(line, waiting ? BoardMoves.Waiting : item.InProgress ? BoardMoves.Working : null, item.Id);
        }

        return line;
    }

    /// <summary>The fields the form of this column writes: names (the first is the one written), before and after.</summary>
    private static IEnumerable<(string[] Aliases, string Before, string After)> FieldChanges(BoardItem item, string columnKey, NewEntry before, NewEntry after)
    {
        var kind = FormKind(columnKey);
        if (kind != "appunti")
        {
            yield return (["priorita", "priorità", "priority"], before.Priority, after.Priority);
            yield return (["stima", "estimate"], before.Estimate, after.Estimate);
        }

        yield return (["tag"], string.Join(", ", before.Tags), string.Join(", ", after.Tags));
        if (kind == "todo")
        {
            yield return (["tipo", "type"], before.Type, after.Type);
            var waiting = columnKey is "todo" or "attesa" or "corso" ? after.Waiting : item.Waiting;
            var until = waiting && BoardEntries.Line(after.WaitingFor).Length == 0 && after.Until is { } day
                ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "";
            yield return (["fino"], item.Field("fino") ?? "", until);
        }
    }

    /// <summary>The note after " — ": what a ⏸ task waits for, or the note as edited.</summary>
    private static string NoteOf(BoardItem item, string columnKey, NewEntry entry)
    {
        var waiting = columnKey is "todo" or "attesa" or "corso" ? entry.Waiting : item.Waiting;
        var wasWaitingNote = item.Note is { } n && n.StartsWith(WaitingPrefix, StringComparison.OrdinalIgnoreCase);
        if (waiting && BoardEntries.Line(entry.WaitingFor) is { Length: > 0 } waitingFor)
        {
            var written = $"{WaitingPrefix} {waitingFor}";
            return wasWaitingNote && Clean(item.Note![WaitingPrefix.Length..]) == Clean(waitingFor) ? item.Note : written;
        }

        return wasWaitingNote ? "" : Clean(entry.Note) == Clean(item.Note ?? "") ? item.Note ?? "" : Clean(entry.Note);
    }

    private static bool ReplaceOnce(ref string line, string oldText, string newText)
    {
        var at = oldText.Length == 0 ? -1 : line.IndexOf(oldText, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        line = line[..at] + newText + line[(at + oldText.Length)..];
        return true;
    }

    /// <summary>"- [ ] ID · ⏸ date · Titolo — nota · PR #4 · 🔁1 [priorita:: P2] … [other:: kept]".</summary>
    private static string FirstLine(BoardItem item, string original, string columnKey, NewEntry entry)
    {
        var bullet = Bullet().Match(original).Value;
        var parts = new List<string>();
        var id = BoardEntries.Line(entry.Id) is { Length: > 0 } newId ? newId : item.Id;
        if (id is not null)
        {
            parts.Add(id);
        }

        if (item.Date is { } date)
        {
            parts.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var waiting = columnKey is "todo" or "attesa" or "corso" ? entry.Waiting : item.Waiting;
        var title = Clean(entry.Title);
        var note = NoteOf(item, columnKey, entry);
        parts.Add(note.Length > 0 ? $"{title} — {note}" : title);
        parts.AddRange(item.Tags);
        if (item.Rework > 0)
        {
            parts.Add($"🔁{item.Rework}");
        }

        var line = new StringBuilder(bullet).Append(string.Join(" · ", parts));
        if (FormKind(columnKey) != "appunti")
        {
            line.Append(BoardEntries.Field("priorita", entry.Priority)).Append(BoardEntries.Field("stima", entry.Estimate));
        }

        line.Append(BoardEntries.TagField(entry.Tags));
        if (FormKind(columnKey) == "todo")
        {
            line.Append(BoardEntries.Field("tipo", entry.Type));
            if (waiting && BoardEntries.Line(entry.WaitingFor).Length == 0 && entry.Until is { } until)
            {
                line.Append(BoardEntries.Field("fino", until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
        }

        var written = FormKind(columnKey) switch
        {
            "appunti" => ["tag"],
            "controllare" => FormFields.Where(f => f is not ("tipo" or "type" or "fino")).ToArray(),
            _ => FormFields,
        };
        foreach (var (key, value) in item.Fields ?? new Dictionary<string, string>())
        {
            if (!written.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                line.Append(BoardEntries.Field(key.Trim(), value));
            }
        }

        var marker = waiting ? BoardMoves.Waiting : item.InProgress ? BoardMoves.Working : null;
        return BoardMoves.SetState(line.ToString(), marker, id);
    }

    /// <summary>A title or note on one line, without " · " (it would split the item's parts).</summary>
    private static string Clean(string text) => BoardEntries.Line(text).Replace(" · ", " - ");

    private static string Value(NewEntry entry, string label) => label switch
    {
        "Contesto" => entry.Context,
        "Dove" => entry.Where,
        "Fatto quando" => entry.DoneWhen,
        "Assegnato da" => entry.AssignedBy,
        "Cambiato" => entry.Changed,
        "File" => entry.Files,
        "Come testare" => entry.HowToTest,
        "Commit" => entry.Commit.Length > 0 && !entry.Commit.Contains('`') ? $"`{BoardEntries.Line(entry.Commit)}`" : entry.Commit,
        _ => "",
    };

    /// <summary>
    /// A form field as nested lines, null when empty. As in markdown, a new line goes on the text above (indented, no
    /// bullet) and a blank line starts a new point; a line written as a list item ("- …", "1. …") stays one.
    /// With a label: "  - Label: value", or "  - Label:" with the text under it (one point per paragraph when there
    /// are several). Without one (an appunto's details): one "  - " point per paragraph.
    /// </summary>
    internal static string? Detail(string? label, string value)
    {
        var paragraphs = Paragraphs(value);
        if (paragraphs.Count == 0)
        {
            return null;
        }

        if (label is not null && paragraphs is [[var only]])
        {
            return $"  - {label}: {only}";
        }

        if (label is not null && paragraphs is [var single])
        {
            return $"  - {label}:" + string.Concat(single.Select(l => "\n    " + l));
        }

        var (head, indent) = label is null ? ("", "  ") : ($"  - {label}:", "    ");
        var points = paragraphs.Select(p =>
            "\n" + indent + (ListMark().IsMatch(p[0]) ? p[0] : "- " + p[0]) + string.Concat(p.Skip(1).Select(l => "\n" + indent + "  " + l)));
        return (head + string.Concat(points)).TrimStart('\n');
    }

    /// <summary>The text's lines, trimmed, grouped by blank lines.</summary>
    private static List<List<string>> Paragraphs(string value)
    {
        var paragraphs = new List<List<string>> { new() };
        foreach (var line in value.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()))
        {
            if (line.Length > 0)
            {
                paragraphs[^1].Add(line);
            }
            else if (paragraphs[^1].Count > 0)
            {
                paragraphs.Add([]);
            }
        }

        return paragraphs.Where(p => p.Count > 0).ToList();
    }

    private static string Normalize(string text) => string.Join('\n', BoardEntries.Lines(text));

    /// <summary>The nested lines of an item, grouped by top-level line, with their "Label:" when they have one.</summary>
    private static List<DetailBlock> DetailBlocks(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n').Skip(1).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        var top = lines.Min(Indent);
        var blocks = new List<List<string>>();
        foreach (var line in lines)
        {
            if (Indent(line) <= top || blocks.Count == 0)
            {
                blocks.Add([line]);
            }
            else
            {
                blocks[^1].Add(line);
            }
        }

        return blocks.Select(block =>
        {
            var head = Unbullet(block[0].Trim());
            var labelMatch = LabelLine().Match(head);
            var label = labelMatch.Success ? labelMatch.Groups["label"].Value.Trim() : null;
            var first = labelMatch.Success ? labelMatch.Groups["value"].Value.Trim() : head;
            // As written: a list item keeps its mark, a plain line is a continuation of the text above.
            var rest = block.Skip(1).Select(l => l.Trim());
            var value = string.Join('\n', new[] { first }.Concat(rest).Where(l => l.Length > 0));
            return new DetailBlock(label, value, string.Join('\n', new[] { head }.Concat(rest).Where(l => l.Length > 0)), string.Join('\n', block));
        }).ToList();
    }

    private static int Indent(string line) => line.TakeWhile(char.IsWhiteSpace).Sum(c => c == '\t' ? 4 : 1);

    private static string Unbullet(string line) => line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ? line[2..] : line;

    /// <param name="Label">"Fatto quando" for "- Fatto quando: …", null for a line without a label.</param>
    /// <param name="Value">The text after the label plus the nested lines, one per line.</param>
    /// <param name="Text">The whole block as text (the appunto's free text).</param>
    /// <param name="Raw">The lines as written.</param>
    private sealed record DetailBlock(string? Label, string Value, string Text, string Raw);

    [GeneratedRegex(@"^[-*] (?:\[[ xX-]\] )?")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(?<label>[\p{L} ]{2,30}):(?:\s+(?<value>.*))?$")]
    private static partial Regex LabelLine();

    [GeneratedRegex(@"^(?:\d+[.)]|[-*]) ")]
    private static partial Regex ListMark();
}
