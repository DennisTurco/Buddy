using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MdXaml;

namespace Buddy.App.Chat;

/// <summary>
/// Read-only, selectable rich text that renders markdown and grows with its content,
/// so many of them can be stacked inside the chat's single ScrollViewer.
/// </summary>
public sealed class MarkdownBox : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownBox),
        new PropertyMetadata("", (d, _) => ((MarkdownBox)d).Render()));

    /// <summary>While true the text is shown plain (cheap on every streamed chunk); markdown is rendered once it turns false.</summary>
    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming), typeof(bool), typeof(MarkdownBox),
        new PropertyMetadata(false, (d, _) => ((MarkdownBox)d).Render()));

    [ThreadStatic]
    private static Markdown? _engine;

    public MarkdownBox()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Background = null;
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        // Font and colour are inherited from the window, so render again once in the visual tree.
        Loaded += (_, _) => Render();
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    private void Render()
    {
        FlowDocument document;
        if (IsStreaming)
        {
            document = new FlowDocument(new Paragraph(new Run(Markdown)));
        }
        else
        {
            _engine ??= new Markdown();
            try
            {
                document = _engine.Transform(Markdown ?? "");
            }
            catch (Exception)
            {
                document = new FlowDocument(new Paragraph(new Run(Markdown)));
            }
        }

        document.PagePadding = new Thickness(0);
        document.FontFamily = FontFamily;
        document.FontSize = FontSize;
        document.Foreground = Foreground;
        WireLinks(document);
        Document = document;
    }

    /// <summary>
    /// Opens links (https://, obsidian://, file paths) with the system handler on a plain click,
    /// whatever command the markdown engine attached to them.
    /// </summary>
    private static void WireLinks(FlowDocument document)
    {
        foreach (var link in Descendants(document).OfType<Hyperlink>())
        {
            var target = link.NavigateUri?.OriginalString ?? link.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            link.Command = null;
            link.Cursor = System.Windows.Input.Cursors.Hand;
            link.ToolTip = target;
            link.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // No handler registered for this kind of link.
                }
            };
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
