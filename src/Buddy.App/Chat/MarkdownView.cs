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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            _viewer.Markdown = Markdown ?? "";
            if (!_enhancePending && (Markdown?.Contains("```") == true || Markdown?.Contains("~~~") == true || Markdown?.Contains("\n    ") == true))
            {
                // The viewer builds its controls on the next layout pass: decorate them right after.
                _enhancePending = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _enhancePending = false;
                    CodeBlocks.Enhance(_viewer, Markdown ?? "");
                }, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    private bool _enhancePending;

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
