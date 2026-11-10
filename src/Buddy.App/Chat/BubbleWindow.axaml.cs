using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Buddy.Core;
using Buddy.Core.Chat;
using Microsoft.Extensions.Options;

namespace Buddy.App.Chat;

/// <summary>
/// The speech bubble next to the pet: the frame (header buttons ⛶ and ✕, the tail, its place on screen, full screen)
/// around the <see cref="ChatView"/>, the chat itself.
/// </summary>
public partial class BubbleWindow : Window
{
    private const double DefaultWidth = 380;
    private const double DefaultHeight = 480;

    /// <summary>Saves the size picked by dragging the edges (the tests replace it, to leave the user's state alone).</summary>
    public static Action<double, double> SaveSize { get; set; } = SettingsStore.SaveBubbleSize;

    /// <summary>Reads the remembered size (the tests replace it, so the user's own size does not change them).</summary>
    public static Func<(double Width, double Height)?> LoadSize { get; set; } = SettingsStore.LoadBubbleSize;

    /// <summary>The size next to the pet: the default one, or the one picked by dragging an edge.</summary>
    private double _normalWidth = DefaultWidth;
    private double _normalHeight = DefaultHeight;

    /// <summary>An edge is being dragged: the size it ends at is remembered.</summary>
    private bool _resizing;

    private readonly Avalonia.Threading.DispatcherTimer _saveSize = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private readonly Conversation _conversation;
    private bool _maximized;

    /// <summary>For the XAML previewer (see <see cref="DesignData"/>).</summary>
    public BubbleWindow() : this(DesignData.Conversation, DesignData.Settings)
    {
    }

    public BubbleWindow(Conversation conversation, IOptions<BuddySettings> settings, Buddy.Core.Claude.ClaudeUsage? usage = null)
    {
        InitializeComponent();
        _conversation = conversation;
        Appearance.Apply(this, settings.Value.Appearance);
        RequestedThemeVariant = Appearance.Variant(settings.Value.Appearance); // buttons and boxes readable on a dark chat
        Chat.Attach(conversation, settings.Value, usage);

        conversation.Entries.CollectionChanged += OnEntriesChanged;
        conversation.SideEntries.CollectionChanged += OnEntriesChanged;
        Chat.Header.PointerPressed += OnHeaderPointerPressed;

        if (LoadSize() is var (width, height) && width >= MinWidth && height >= MinHeight)
        {
            (_normalWidth, _normalHeight) = (width, height);
            (Width, Height) = (width, height);
        }

        // The window manager resizes in steps: the size is saved once the dragging has stopped for a moment.
        Resized += (_, _) =>
        {
            if (_resizing && !_maximized)
            {
                _saveSize.Stop();
                _saveSize.Start();
            }
        };
        _saveSize.Tick += (_, _) =>
        {
            _saveSize.Stop();
            _resizing = false;
            (_normalWidth, _normalHeight) = (Width, Height);
            SaveSize(Width, Height);
        };

        // Keys that reach the window without passing through the chat (nothing focused) still work there.
        KeyDown += (_, e) =>
        {
            if (Chat.HandleKey(e))
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                Hide();
            }
            else if (e.Key == Key.F11)
            {
                SetMaximized(!_maximized);
                e.Handled = true;
            }
        };
    }

    /// <summary>Set by the app; null when voice input is disabled.</summary>
    public Voice.VoiceInput? Voice
    {
        get => Chat.Voice;
        set => Chat.Voice = value;
    }

    /// <summary>Lists the models installed in Ollama (set by the app); null hides the local section.</summary>
    public Func<Task<IReadOnlyList<string>>>? LocalModelsSource
    {
        get => Chat.LocalModelsSource;
        set => Chat.LocalModelsSource = value;
    }

    /// <summary>The pet window: the bubble is placed next to it before being shown.</summary>
    public Window? Anchor { get; set; }

    /// <summary>
    /// True when the same chat is in sight elsewhere (the project board's): a permission prompt is answered there,
    /// without opening the bubble too.
    /// </summary>
    public Func<bool>? ChatShownElsewhere { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && Chat is not null)
        {
            Chat.OnShown();
        }
    }

    public void ShowAndFocus()
    {
        ShowNearAnchor();
        Activate();
        Chat.FocusInput();
    }

    /// <summary>Opens the chat with <paramref name="text"/> in the input box, ready to edit and send.</summary>
    public void ShowWithText(string text)
    {
        ShowAndFocus();
        Chat.SetDraft(text);
    }

    /// <summary>A permission prompt must be seen: open the bubble even if it was closed.</summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _conversation.VisibleEntries) && !IsVisible
            && e.NewItems?.OfType<PermissionEntry>().Any() == true && ChatShownElsewhere?.Invoke() != true)
        {
            ShowNearAnchor();
        }
    }

    /// <summary>Places the bubble above the pet, with the tail pointing at it, inside the visible screen area (pixels).</summary>
    public void PositionNear(Window pet)
    {
        // The monitor the pet is on, not the primary one: with several screens the bubble stays next to the pet.
        if (_maximized)
        {
            if (!OperatingSystem.IsWindows())
            {
                return; // maximized by the window manager, which keeps it off the panels
            }

            FillArea(Displays.WorkAreaOf(pet));
            return;
        }

        var area = Displays.WorkAreaOf(pet);

        var scaling = Displays.Scaling(pet);
        var petSize = Displays.SizeInPixels(pet);
        var width = (int)(Width * scaling);
        var height = (int)(Height * scaling);
        var left = pet.Position.X + petSize.Width - width + (int)(6 * scaling);
        var top = pet.Position.Y - height + (int)(26 * scaling);
        Position = new PixelPoint(
            Math.Clamp(left, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    /// <summary>
    /// Full screen for long answers: the bubble fills the work area of the pet's monitor (panels excluded),
    /// messages get wider (up to a readable line length, see <see cref="ChatView"/>) and the tail is hidden. Toggled
    /// again, it goes back next to the pet.
    /// </summary>
    public void SetMaximized(bool maximized)
    {
        _maximized = maximized;
        MaximizeButton.Content = Icons.Of(maximized ? "IconRestore" : "IconMaximize");
        ToolTip.SetTip(MaximizeButton, maximized ? "Riduci (F11 o doppio click sul titolo)" : "Schermo intero (F11 o doppio click sul titolo)");
        Tail.Opacity = maximized ? 0 : 1;
        GripLeft.IsVisible = GripTop.IsVisible = GripCorner.IsVisible = !maximized;

        if (maximized && !OperatingSystem.IsWindows())
        {
            // Under XWayland the screen's working area does not exclude the dock or the top bar, so a window sized
            // by hand ends up under them: let the window manager maximize it instead.
            CanResize = true;
            WindowState = WindowState.Maximized;
        }
        else if (maximized)
        {
            FillArea(Displays.WorkAreaOf(Anchor ?? this));
        }
        else
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }

            Width = _normalWidth;
            Height = _normalHeight;
            if (Anchor is { } pet)
            {
                PositionNear(pet);
            }
        }

        Chat.ScrollToLatest();
        Chat.FocusInput();
    }

    private void FillArea(PixelRect area)
    {
        var scaling = Displays.Scaling(Anchor ?? this);
        Position = area.Position;
        Width = area.Width / scaling;
        Height = area.Height / scaling;
    }

    /// <summary>Positions next to the pet before showing, then again once shown, when its real size is known.</summary>
    private void ShowNearAnchor()
    {
        if (Anchor is { } pet)
        {
            PositionNear(pet);
        }

        Show();
        if (Anchor is { } shownPet)
        {
            PositionNear(shownPet);
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Button)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            SetMaximized(!_maximized);
        }
        else if (!_maximized)
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>Drag on an edge of the bubble: the window manager resizes it, the end size is remembered.</summary>
    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_maximized || sender is not Control { Tag: string tag } || !Enum.TryParse<WindowEdge>(tag, out var edge)
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _resizing = true;
        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => SetMaximized(!_maximized);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing only hides the bubble: the conversation lives as long as the pet (unless the app is exiting).
        if (e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
