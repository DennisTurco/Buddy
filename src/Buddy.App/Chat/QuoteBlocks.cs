using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Buddy.App.Chat;

/// <summary>
/// Quotes of a rendered markdown (a draft to send, e.g. "> Ciao Wentu! ...") get a copy button after Markdown.Avalonia
/// built them. The copy is the quote's markdown as it was written, without the "&gt;" markers, so it can be pasted
/// as it is into a chat, a ticket or a mail that understands markdown.
/// </summary>
internal static partial class QuoteBlocks
{
    private const string Done = "copyable";

    public static bool HasQuote(string markdown) => QuoteLine().IsMatch(markdown);

    public static void Enhance(Control viewer, string markdown)
    {
        // Only the outer quotes: a quote inside a quote is copied with its parent.
        var blocks = viewer.GetLogicalDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("Blockquote")
                        && !b.GetLogicalAncestors().OfType<Border>().Any(a => a.Classes.Contains("Blockquote")))
            .ToList();

        // The n-th quote in the source is the n-th quote drawn; when they do not line up (a quote the parser read
        // differently) no button is added rather than one that copies the wrong text.
        var sources = Quotes(markdown);
        if (blocks.Count == 0 || blocks.Count != sources.Count)
        {
            return;
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Classes.Contains(Done) || block.Child is not { } content)
            {
                continue;
            }

            block.Classes.Add(Done);
            block.Child = null;
            var panel = new DockPanel();
            var copy = CopyButton(block, sources[i]);
            DockPanel.SetDock(copy, Dock.Top);
            panel.Children.Add(copy);
            panel.Children.Add(content);
            block.Child = panel;
        }
    }

    private static Button CopyButton(Border block, string markdown)
    {
        var copy = new Button
        {
            Content = Icons.Of("IconCopy", "Copia", 12),
            FontSize = TextSize.Caption,
            Padding = new Thickness(6, 0),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            [ToolTip.TipProperty] = "Copia il testo in markdown",
        };
        if (block.TryFindResource("Muted", out var muted) && muted is IBrush mutedBrush)
        {
            copy.Foreground = mutedBrush;
        }

        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, markdown);
                copy.Content = Icons.Of("IconCheck", "Copiato", 12);
                await Task.Delay(1200);
                copy.Content = Icons.Of("IconCopy", "Copia", 12);
            }
        };
        return copy;
    }

    /// <summary>
    /// The outer quotes of a markdown, in order, each with one level of "&gt;" taken off. Lines inside fenced code
    /// are not quotes; a line right after a quote line that is not blank continues it (a "lazy" line).
    /// </summary>
    internal static List<string> Quotes(string markdown)
    {
        var quotes = new List<string>();
        List<string>? current = null;
        string? fence = null;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.TrimStart();
            if (fence is null && current is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = trimmed[..3];
                continue;
            }

            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }

                continue;
            }

            var quote = QuoteLine().Match(raw);
            if (quote.Success)
            {
                current ??= [];
                current.Add(quote.Groups["rest"].Value);
            }
            else if (current is not null && trimmed.Length > 0)
            {
                current.Add(trimmed); // lazy continuation
            }
            else if (current is not null)
            {
                quotes.Add(string.Join('\n', current).Trim('\n'));
                current = null;
            }
        }

        if (current is not null)
        {
            quotes.Add(string.Join('\n', current).Trim('\n'));
        }

        return quotes;
    }

    [GeneratedRegex(@"^ {0,3}> ?(?<rest>.*)$", RegexOptions.Multiline)]
    private static partial Regex QuoteLine();
}
