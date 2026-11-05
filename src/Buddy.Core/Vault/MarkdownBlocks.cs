namespace Buddy.Core.Vault;

/// <summary>
/// Splits a markdown note into blocks: each heading or top-level list item starts a block,
/// and indented lines below it belong to the same block.
/// </summary>
public static class MarkdownBlocks
{
    public static IEnumerable<MarkdownBlock> Split(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var current = new List<string>();

        foreach (var line in lines)
        {
            if (StartsBlock(line) && current.Count > 0)
            {
                yield return Build(current);
                current.Clear();
            }

            if (current.Count == 0 && string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            current.Add(line);
        }

        if (current.Count > 0)
        {
            yield return Build(current);
        }
    }

    private static bool StartsBlock(string line) =>
        line.StartsWith('#')
        || line.StartsWith("- ", StringComparison.Ordinal)
        || line.StartsWith("* ", StringComparison.Ordinal)
        || (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('>'));

    private static MarkdownBlock Build(List<string> lines) =>
        new(lines[0], string.Join('\n', lines).TrimEnd());
}

public sealed record MarkdownBlock(string FirstLine, string Text);
