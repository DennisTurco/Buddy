using System.Reflection;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using ColorTextBlock.Avalonia;

namespace Buddy.App.Chat;

/// <summary>
/// Works around a selection bug in ColorTextBlock (Markdown.Avalonia): to know which end of a paragraph's selection
/// comes first, it compares the two ends by the hash codes of the text runs they sit in, not by their position. When
/// the selection spans several runs (text, `inline code`, **bold**) the start and the end are swapped at random, and
/// the highlight covers the wrong part (the text before the start instead of after it). The comparison falls back to
/// the position only when both ends have the same run path: sharing one path makes it compare by position. Only the
/// drawing reads that path; the selected range itself (From/To) is already right.
/// </summary>
internal static class SelectionOrder
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo? Begin = typeof(CTextBlock).GetField("_beginSelect", Private);
    private static readonly FieldInfo? End = typeof(CTextBlock).GetField("_endSelect", Private);
    private static readonly FieldInfo? Path = typeof(PhysicalTextPointer).GetField("_path", Private);

    public static void Fix(Control viewer)
    {
        if (Begin is null || End is null || Path is null)
        {
            return; // another version of the library: nothing to fix, or not this way
        }

        foreach (var block in viewer.GetLogicalDescendants().OfType<CTextBlock>())
        {
            if (Begin.GetValue(block) is PhysicalTextPointer begin && End.GetValue(block) is PhysicalTextPointer end
                && !ReferenceEquals(begin, end) && Path.GetValue(begin) is { } path && !ReferenceEquals(path, Path.GetValue(end)))
            {
                Path.SetValue(end, path);
                block.InvalidateVisual();
            }
        }
    }
}
