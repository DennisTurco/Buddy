using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Markdown.Avalonia;

namespace Buddy.App.Chat;

/// <summary>
/// Selectable markdown for one chat message (Markdown.Avalonia). It grows with its content, so many of them can be
/// stacked inside the chat's single ScrollViewer; links (https://, obsidian://, file paths) open with the desktop's
/// default handler.
/// </summary>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>
    /// The text is still arriving (a streamed answer): it is drawn as markdown a few times a second instead of on
    /// every chunk, so headings, lists and code take their shape as they come without re-laying out each word.
    /// </summary>
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(IsLive));

    private static readonly TimeSpan LiveInterval = TimeSpan.FromMilliseconds(250);

    private Avalonia.Threading.DispatcherTimer? _liveTimer;

    private readonly MarkdownScrollViewer _viewer = new()
    {
        // FluentTheme follows the light/dark theme variant; "Standard" (the default) assumes a light background.
        MarkdownStyleName = "FluentTheme",
        SelectionEnabled = true,
        Padding = new Thickness(0),
    };

    public MarkdownView()
    {
        if (_viewer.Engine is IMarkdownEngine engine)
        {
            engine.HyperlinkCommand = new OpenLinkCommand(this);
        }

        // Theme-aware colours for code, tables and links, after the viewer's own (light-only) style.
        _viewer.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Buddy.App/"))
        {
            Source = new Uri("avares://Buddy.App/Chat/MarkdownTheme.axaml"),
        });
        Content = _viewer;

        // Before the viewer's own copy: see SelectedText.
        AddHandler(KeyDownEvent, OnCopyKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // After the viewer has moved the selection (its handlers run first), before the frame is drawn.
        AddHandler(PointerPressedEvent, (_, _) => SelectionOrder.Fix(_viewer), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, _) => SelectionOrder.Fix(_viewer), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => SelectionOrder.Fix(_viewer), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, _) => SelectionOrder.Fix(_viewer), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// The selected text, rebuilt from each paragraph's selection range. The library's own GetSelectedText is wrong
    /// when a paragraph contains `inline code`: it keeps only the part between the first and the last code span.
    /// </summary>
    internal string SelectedText => string.Join("\n", _viewer.GetLogicalDescendants()
        .OfType<ColorTextBlock.Avalonia.CTextBlock>()
        .Select(block => (Selection: block.Selection, Text: block.Text ?? ""))
        .Where(b => b.Selection is { } s && s.To > s.From && s.To <= b.Text.Length)
        .Select(b => b.Text[b.Selection!.From..b.Selection.To]));

    private async void OnCopyKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        var copy = Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy;
        if (copy?.Any(gesture => gesture.Matches(e)) != true || SelectedText is not { Length: > 0 } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        e.Handled = true;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
    }

    /// <summary>Handles a clicked link instead of the desktop (returns true when handled), e.g. links between notes.</summary>
    public Func<string, bool>? LinkHandler { get; set; }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty && IsLive)
        {
            // Drawn by the timer: at most one rendering per interval while the text grows.
            _liveTimer ??= new Avalonia.Threading.DispatcherTimer(LiveInterval, Avalonia.Threading.DispatcherPriority.Background,
                (_, _) => Render());
            if (!_liveTimer.IsEnabled)
            {
                _liveTimer.Start();
            }
        }
        else if (change.Property == MarkdownProperty || (change.Property == IsLiveProperty && !IsLive))
        {
            _liveTimer?.Stop();
            Render(); // the complete text, at once
        }
    }

    private void Render()
    {
        _liveTimer?.Stop();
        var markdown = Markdown ?? "";
        if (_rendered == markdown)
        {
            return;
        }

        _rendered = markdown;

        _viewer.Markdown = SplitBoldAroundCode(markdown);
        if (!_listsPending && ListLine.IsMatch(markdown))
        {
            _listsPending = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _listsPending = false;
                TidyListMarkers(_viewer);
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        if (!_enhancePending && (markdown.Contains("```") || markdown.Contains("~~~") || markdown.Contains("\n    ")))
        {
            // The viewer builds its controls on the next layout pass: decorate them right after.
            _enhancePending = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _enhancePending = false;
                CodeBlocks.Enhance(_viewer, _viewer.Markdown ?? "");
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private bool _enhancePending;
    private bool _listsPending;
    private string? _rendered;

    private static readonly System.Text.RegularExpressions.Regex BoldWithCode =
        new(@"\*\*(?=\S)([^*\r\n]*?`[^`\r\n]+`[^*\r\n]*?)\*\*");

    private static readonly System.Text.RegularExpressions.Regex CodeSpan = new(@"`[^`\r\n]+`");

    /// <summary>
    /// "**Id 54 – `Key`**" becomes "**Id 54 –** `Key`": the library cuts the text at inline code before it looks for
    /// bold, so a bold span with code inside is left as plain text with its asterisks. The code keeps its own look;
    /// the text around it stays bold. Lines inside fenced code blocks are left as they are.
    /// </summary>
    internal static string SplitBoldAroundCode(string markdown)
    {
        if (!markdown.Contains("**") || !markdown.Contains('`'))
        {
            return markdown;
        }

        var lines = markdown.Split('\n');
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced)
            {
                lines[i] = BoldWithCode.Replace(lines[i], m => SplitBold(m.Groups[1].Value));
            }
        }

        return string.Join('\n', lines);
    }

    private static string SplitBold(string inner)
    {
        var parts = new List<string>();
        var at = 0;
        foreach (System.Text.RegularExpressions.Match code in CodeSpan.Matches(inner))
        {
            AddBold(parts, inner[at..code.Index]);
            parts.Add(code.Value);
            at = code.Index + code.Length;
        }

        AddBold(parts, inner[at..]);
        return string.Join(' ', parts);
    }

    private static void AddBold(List<string> parts, string text)
    {
        if (text.Trim() is { Length: > 0 } t)
        {
            parts.Add("**" + t + "**");
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ListLine =
        new(@"^\s*[-*+]\s", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// The library marks every bullet item with a hollow circle: a dot reads lighter ("•", "◦" one level down). Its
    /// indent, spacing and colour come from MarkdownTheme.axaml.
    /// </summary>
    internal static void TidyListMarkers(Control viewer)
    {
        foreach (var marker in viewer.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>()
                     .Where(b => b.Classes.Contains("ListMarker")))
        {
            if (marker.Content is [ColorTextBlock.Avalonia.CRun { Text: "○" or "●" or "■" or "□" }])
            {
                // A new run, not a new text on the old one: the block lays itself out again only when its content changes.
                var nested = marker.GetLogicalAncestors().OfType<Grid>().Count(g => g.Classes.Contains("List")) > 1;
                marker.Content[0] = new ColorTextBlock.Avalonia.CRun { Text = nested ? "◦" : "•" };
            }
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // The viewer brings its own ScrollViewer: no bars, the chat scrolls as a whole.
        foreach (var scroller in _viewer.GetLogicalDescendants().OfType<ScrollViewer>())
        {
            scroller.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            scroller.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        }
    }

    private sealed class OpenLinkCommand(MarkdownView owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => parameter is string { Length: > 0 };

        public void Execute(object? parameter)
        {
            if (parameter is string target && owner.LinkHandler?.Invoke(target) != true)
            {
                Shell.Open(target);
            }
        }
    }
}
