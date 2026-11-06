namespace Buddy.Core.Text;

/// <summary>The text after a formatting command, and the selection to show (start, end).</summary>
public readonly record struct FormatResult(string Text, int SelectionStart, int SelectionEnd);

/// <summary>
/// The note editor's tool bar: markdown applied to the selection, as in Obsidian. Inline marks (bold, italic, code,
/// links) wrap the selection, or a placeholder when nothing is selected; line marks (headings, lists, quotes) go at
/// the start of every selected line, and are removed when every line already has them.
/// </summary>
public static class MarkdownFormat
{
    public static readonly string[] Commands = ["bold", "italic", "strike", "code", "h1", "h2", "list", "numbered", "check", "quote", "wikilink", "link", "table", "rule"];

    public static FormatResult Apply(string text, int start, int end, string command)
    {
        text ??= "";
        (start, end) = (Math.Clamp(Math.Min(start, end), 0, text.Length), Math.Clamp(Math.Max(start, end), 0, text.Length));
        var selected = text[start..end];
        return command switch
        {
            "bold" => Wrap(text, start, end, "**", "**", "testo in grassetto"),
            "italic" => Wrap(text, start, end, "*", "*", "testo in corsivo"),
            "strike" => Wrap(text, start, end, "~~", "~~", "testo barrato"),
            "code" when selected.Contains('\n') => Wrap(text, start, end, "```\n", "\n```", "codice"),
            "code" => Wrap(text, start, end, "`", "`", "codice"),
            "wikilink" => Wrap(text, start, end, "[[", "]]", "Nome nota"),
            "link" => Link(text, start, end),
            "h1" => Prefix(text, start, end, _ => "# "),
            "h2" => Prefix(text, start, end, _ => "## "),
            "list" => Prefix(text, start, end, _ => "- "),
            "numbered" => Prefix(text, start, end, i => $"{i + 1}. "),
            "check" => Prefix(text, start, end, _ => "- [ ] "),
            "quote" => Prefix(text, start, end, _ => "> "),
            "table" => Block(text, end, "| Colonna | Colonna |\n|---|---|\n| | |"),
            "rule" => Block(text, end, "---"),
            _ => new FormatResult(text, start, end),
        };
    }

    private static FormatResult Wrap(string text, int start, int end, string before, string after, string placeholder)
    {
        var inner = text[start..end];

        // Already wrapped (the marks are just outside the selection): unwrap.
        if (inner.Length > 0 && start >= before.Length && end + after.Length <= text.Length
            && text[(start - before.Length)..start] == before && text[end..(end + after.Length)] == after)
        {
            var unwrapped = text[..(start - before.Length)] + inner + text[(end + after.Length)..];
            return new FormatResult(unwrapped, start - before.Length, end - before.Length);
        }

        var content = inner.Length > 0 ? inner : placeholder;
        var result = text[..start] + before + content + after + text[end..];
        return new FormatResult(result, start + before.Length, start + before.Length + content.Length);
    }

    /// <summary>"[selection](https://)" with the address selected, ready to be typed or pasted.</summary>
    private static FormatResult Link(string text, int start, int end)
    {
        var label = end > start ? text[start..end] : "testo del link";
        const string url = "https://";
        var result = text[..start] + "[" + label + "](" + url + ")" + text[end..];
        var urlStart = start + label.Length + 3;
        return new FormatResult(result, urlStart, urlStart + url.Length);
    }

    /// <summary>A mark at the start of each line touched by the selection; removed if all of them have it.</summary>
    private static FormatResult Prefix(string text, int start, int end, Func<int, string> mark)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) is var s and >= 0 && start > 0 ? s + 1 : 0;
        var lineEnd = text.IndexOf('\n', end > start && text[end - 1] == '\n' ? end - 1 : end) is var e and >= 0 ? e : text.Length;
        var lines = text[lineStart..lineEnd].Split('\n');

        var all = lines.Select((l, i) => l.StartsWith(mark(i), StringComparison.Ordinal)).All(x => x);
        var changed = lines.Select((l, i) => all ? l[mark(i).Length..] : Strip(l) is var plain ? mark(i) + plain : l);
        var block = string.Join('\n', changed);
        var result = text[..lineStart] + block + text[lineEnd..];
        return new FormatResult(result, lineStart, lineStart + block.Length);
    }

    /// <summary>A line without the marks of the other line commands, so "- " becomes "# " instead of "# - ".</summary>
    private static string Strip(string line)
    {
        foreach (var prefix in new[] { "- [ ] ", "- [x] ", "### ", "## ", "# ", "- ", "> " })
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..];
            }
        }

        var number = line.IndexOf(". ", StringComparison.Ordinal);
        return number > 0 && line[..number].All(char.IsDigit) ? line[(number + 2)..] : line;
    }

    /// <summary>A block on lines of its own after the selection, with blank lines around it.</summary>
    private static FormatResult Block(string text, int at, string block)
    {
        var lineEnd = text.IndexOf('\n', at) is var e and >= 0 ? e : text.Length;
        var before = lineEnd == 0 ? "" : "\n\n";
        var insert = before + block + "\n";
        var result = text[..lineEnd] + insert + text[lineEnd..];
        var blockStart = lineEnd + before.Length;
        return new FormatResult(result, blockStart, blockStart + block.Length);
    }
}
