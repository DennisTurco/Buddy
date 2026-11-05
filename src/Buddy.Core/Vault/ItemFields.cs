using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// Edits a Dataview inline field ([tag:: bug], [priorita:: P1]) on the first line of a flow item, leaving the rest
/// of the item as written.
/// </summary>
public static class ItemFields
{
    /// <summary>
    /// The item <paramref name="raw"/> with <paramref name="key"/> set to <paramref name="value"/>: replaced where it
    /// is, otherwise added at the end of the first line; an empty value removes the field.
    /// </summary>
    public static string Set(string raw, string key, string? value)
    {
        var text = raw.Replace("\r\n", "\n");
        var newline = text.IndexOf('\n');
        var first = newline < 0 ? text : text[..newline];
        var rest = newline < 0 ? "" : text[newline..];
        var field = new Regex($@"\s*\[{Regex.Escape(key)}::[^\]]*\]", RegexOptions.IgnoreCase);
        value = value?.Trim().Replace("]", "").Replace("[", "");

        first = field.IsMatch(first)
            ? field.Replace(first, string.IsNullOrEmpty(value) ? "" : $" [{key}:: {value}]", 1)
            : string.IsNullOrEmpty(value) ? first : $"{first.TrimEnd()} [{key}:: {value}]";
        return first.TrimEnd() + rest;
    }
}
