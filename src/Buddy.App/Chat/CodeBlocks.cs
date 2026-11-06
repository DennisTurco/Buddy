using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Buddy.Core.Text;

namespace Buddy.App.Chat;

/// <summary>
/// Code blocks of a rendered markdown, made nicer after Markdown.Avalonia built them: the code is coloured by
/// <see cref="CodeTokenizer"/> (the language comes from the fence, e.g. ```csharp), selectable, scrolls sideways
/// instead of wrapping, and has a header with the language and a copy button.
/// </summary>
internal static partial class CodeBlocks
{
    private const string Done = "enhanced";

    public static void Enhance(Control viewer, string markdown)
    {
        var blocks = viewer.GetLogicalDescendants().OfType<Border>()
            .Concat(viewer.GetVisualDescendants().OfType<Border>())
            .Where(b => b.Classes.Contains("CodeBlock"))
            .Distinct()
            .ToList();
        if (blocks.Count == 0)
        {
            return;
        }

        // Fences in the source, in order: their info string is the language of the n-th block. With indented code
        // blocks around (no fence) the order is not reliable, so the language is left out.
        var fences = FenceLanguages(markdown);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Classes.Contains(Done))
            {
                continue;
            }

            var code = CodeOf(block);
            if (code is null)
            {
                continue;
            }

            var language = fences.Count == blocks.Count ? fences[i] : null;
            block.Classes.Add(Done);
            block.Child = Build(block, code.TrimEnd('\n', '\r'), language);
        }
    }

    private static string? CodeOf(Border block)
    {
        var text = block.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()
                   ?? block.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        return text?.Text ?? text?.Inlines?.Text;
    }

    private static Control Build(Border block, string code, string? language)
    {
        var text = new SelectableTextBlock
        {
            FontFamily = block.TryFindResource("MonoFont", out var font) && font is FontFamily family ? family : FontFamily.Default,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Inlines = [],
        };

        foreach (var token in CodeTokenizer.Tokenize(code, language))
        {
            var run = new Run(token.Text);
            if (token.Kind != TokenKind.Plain && Brush(block, token.Kind) is { } brush)
            {
                run.Foreground = brush;
            }

            if (token.Kind == TokenKind.Comment)
            {
                run.FontStyle = FontStyle.Italic;
            }

            text.Inlines!.Add(run);
        }

        var copy = new Button
        {
            Content = "⧉ Copia",
            FontSize = 10.5,
            Padding = new Thickness(6, 0),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            [ToolTip.TipProperty] = "Copia il codice",
        };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, code);
                copy.Content = "✓ Copiato";
                await Task.Delay(1200);
                copy.Content = "⧉ Copia";
            }
        };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "codice" : language.Trim(),
            FontSize = 10.5,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (block.TryFindResource("Muted", out var muted) && muted is IBrush mutedBrush)
        {
            copy.Foreground = mutedBrush;
            ((TextBlock)header.Children[1]).Foreground = mutedBrush;
        }

        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = text,
        });
        return panel;
    }

    private static IBrush? Brush(StyledElement element, TokenKind kind) =>
        element.TryFindResource(kind switch
        {
            TokenKind.Keyword => "SynKeyword",
            TokenKind.String => "SynString",
            TokenKind.Comment => "SynComment",
            TokenKind.Number => "SynNumber",
            TokenKind.Type => "SynType",
            _ => "SynTag",
        }, out var value) ? value as IBrush : null;

    internal static List<string?> FenceLanguages(string markdown)
    {
        var languages = new List<string?>();
        string? open = null;
        foreach (Match line in FenceLine().Matches(markdown))
        {
            var fence = line.Groups["fence"].Value;
            if (open is null)
            {
                open = fence[..3];
                var info = line.Groups["info"].Value.Trim();
                languages.Add(info.Length == 0 ? null : info.Split(' ', '{')[0]);
            }
            else if (fence.StartsWith(open, StringComparison.Ordinal) && line.Groups["info"].Value.Trim().Length == 0)
            {
                open = null;
            }
        }

        return languages;
    }

    [GeneratedRegex(@"^[ \t>]*(?<fence>`{3,}|~{3,})(?<info>[^\n`]*)$", RegexOptions.Multiline)]
    private static partial Regex FenceLine();
}
