using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Buddy.Core;
using Buddy.Core.Chat;
using Microsoft.Extensions.Options;

namespace Buddy.App.Chat;

public partial class BubbleWindow : Window
{
    private const double NormalWidth = 380;
    private const double NormalHeight = 480;
    private const string MicIdleTip = "Detta (click per iniziare, click per finire). Trascrizione locale con Whisper.";

    private readonly Conversation _conversation;
    private readonly BuddySettings _settings;
    private bool _scrollPending;
    private bool _maximized;

    /// <summary>For the XAML previewer (see <see cref="DesignData"/>).</summary>
    public BubbleWindow() : this(DesignData.Conversation, DesignData.Settings)
    {
    }

    public BubbleWindow(Conversation conversation, IOptions<BuddySettings> settings, Buddy.Core.Claude.ClaudeUsage? usage = null)
    {
        InitializeComponent();
        _conversation = conversation;
        _settings = settings.Value;
        if (usage is not null)
        {
            // Today's Claude cost and the 5-hour limit next to the title; details in the tooltip and with /uso.
            void ShowUsage()
            {
                UsageText.Text = "💳 " + usage.ShortText();
                UsageText.IsVisible = usage.TodayTurns > 0 || usage.Limits.Count > 0;
                ToolTip.SetTip(UsageText, usage.Describe().Replace("**", ""));
            }

            usage.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(ShowUsage);
            ShowUsage();
        }

        Appearance.Apply(this, _settings.Appearance);
        RequestedThemeVariant = Appearance.Variant(_settings.Appearance); // buttons and boxes readable on a dark chat
        EntriesList.ItemsSource = conversation.Entries;

        conversation.Entries.CollectionChanged += OnEntriesChanged;
        conversation.PropertyChanged += OnConversationChanged;

        // Tunnel: Enter must be seen before the text box handles it; the wheel before the markdown viewers eat it.
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        Header.PointerPressed += OnHeaderPointerPressed;
        KeyDown += (_, e) =>
        {
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

        UpdateModeText();
        UpdateTaskStrip();
        UpdateAttachment();
        UpdatePlaceholder();
        MicButton.IsVisible = _settings.Voice.Enabled;
    }

    /// <summary>Set by the app; null when voice input is disabled.</summary>
    public Voice.VoiceInput? Voice { get; set; }

    /// <summary>The pet window: the bubble is placed next to it before being shown.</summary>
    public Window? Anchor { get; set; }

    protected override async void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _maximized && WindowState == WindowState.Maximized)
        {
            FitMessages(Bounds.Width);
        }

        if (change.Property == IsVisibleProperty && IsVisible && _conversation is not null)
        {
            _conversation.AcknowledgeMentor();
            await _conversation.RefreshCurrentTaskAsync();
        }
    }

    public void ShowAndFocus()
    {
        ShowNearAnchor();
        Activate();
        Input.Focus();
        ScrollToEnd();
    }

    /// <summary>Opens the chat with <paramref name="text"/> in the input box, ready to edit and send.</summary>
    public void ShowWithText(string text)
    {
        ShowAndFocus();
        Input.Text = text;
        Input.CaretIndex = text.Length;
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
    /// messages get wider (up to a readable line length) and the tail is hidden. Toggled again, it goes back
    /// next to the pet.
    /// </summary>
    public void SetMaximized(bool maximized)
    {
        _maximized = maximized;
        MaximizeButton.Content = maximized ? "🗗" : "⛶";
        ToolTip.SetTip(MaximizeButton, maximized ? "Riduci (F11 o doppio click sul titolo)" : "Schermo intero (F11 o doppio click sul titolo)");
        Tail.Opacity = maximized ? 0 : 1;

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
                CanResize = false;
            }

            Width = NormalWidth;
            Height = NormalHeight;
            Resources["UserMaxWidth"] = 290.0;
            Resources["AssistantMaxWidth"] = 320.0;
            if (Anchor is { } pet)
            {
                PositionNear(pet);
            }
        }

        ScrollToEnd();
        Input.Focus();
    }

    private void FillArea(PixelRect area)
    {
        var scaling = Displays.Scaling(Anchor ?? this);
        Position = area.Position;
        Width = area.Width / scaling;
        Height = area.Height / scaling;
        FitMessages(Width);
    }

    /// <summary>Long lines are hard to read: messages stop at about 110 characters even on a wide screen.</summary>
    private void FitMessages(double width)
    {
        var text = Math.Min(width - 120, 1000);
        Resources["UserMaxWidth"] = Math.Max(290, text * 0.8);
        Resources["AssistantMaxWidth"] = Math.Max(320, text);
    }

    private void UpdatePlaceholder() =>
        Input.PlaceholderText = _conversation.HasPendingReply
            ? "Rispondi al mentore… (o /salta)"
            : "Scrivi… (/aiuto per i comandi)";

    private async void OnMicClick(object? sender, RoutedEventArgs e)
    {
        if (Voice is not { } voice)
        {
            return;
        }

        if (!voice.IsModelAvailable)
        {
            _conversation.Entries.Add(new ChatEntry(Speaker.System, $"Modello Whisper non trovato: {voice.ModelPath}. Scaricalo (circa 470 MB) da "
                + "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin e mettilo lì, "
                + "oppure scegli un altro modello in Impostazioni → Mentore e voce."));
            return;
        }

        try
        {
            if (!voice.IsRecording)
            {
                voice.StartRecording();
                MicButton.Content = "⏺";
                MicButton.Foreground = Brushes.IndianRed;
                ToolTip.SetTip(MicButton, "Sto ascoltando… click per finire");
                return;
            }

            MicButton.Content = "⏳";
            MicButton.IsEnabled = false;
            var text = await Task.Run(() => voice.StopAndTranscribeAsync());
            if (text.Length > 0)
            {
                // The transcription goes into the box, to be checked before sending.
                Input.Text = string.IsNullOrEmpty(Input.Text) ? text : $"{Input.Text} {text}";
                Input.CaretIndex = Input.Text.Length;
                Input.Focus();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            _conversation.Entries.Add(new ChatEntry(Speaker.System, $"Microfono: {ex.Message}"));
        }
        finally
        {
            if (!voice.IsRecording)
            {
                MicButton.Content = "🎤";
                MicButton.IsEnabled = true;
                MicButton.ClearValue(ForegroundProperty);
                ToolTip.SetTip(MicButton, MicIdleTip);
            }
        }
    }

    private void UpdateTaskStrip()
    {
        var task = _conversation.CurrentTask;
        TaskStrip.IsVisible = task is not null;
        TaskText.Text = task is null ? "" : $"📌 {task}";
        ToolTip.SetTip(TaskText, task?.ToString());
        TaskActionsList.ItemsSource = _conversation.TaskActions;
    }

    private void UpdateAttachment()
    {
        var attachment = _conversation.PendingAttachment;
        AttachmentPanel.IsVisible = attachment is not null;
        AttachmentText.Text = attachment is null ? "" : $"📎 {attachment.Preview}";
        AttachmentImages.ItemsSource = attachment?.Images;
        AttachmentImages.IsVisible = attachment?.Images.Count > 0;
    }

    /// <summary>Adds to what is already attached: a second screenshot does not replace the first.</summary>
    private void Attach(Buddy.Core.Chat.Attachment attachment)
    {
        _conversation.PendingAttachment = Buddy.Core.Chat.Attachment.Combine(_conversation.PendingAttachment, attachment);
        Input.Focus();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Buddy.App.Input.AttachmentReader.FromDrop(e.DataTransfer, _settings.Assistant.MaxAttachmentChars) is { } attachment)
        {
            Attach(attachment);
        }
    }

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Allega immagini o file",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Immagini") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"] },
                Avalonia.Platform.Storage.FilePickerFileTypes.All,
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            Attach(Buddy.Core.Chat.Attachment.FromFiles(paths, _settings.Assistant.MaxAttachmentChars));
        }
    }

    /// <summary>Opens an attached image full size with the desktop's viewer (from a temporary copy).</summary>
    private void OnImageClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not Buddy.Core.Chat.AttachedImage image)
        {
            return;
        }

        var extension = image.MediaType switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", _ => ".png" };
        var path = Path.Combine(Path.GetTempPath(), $"buddy-{(uint)image.Base64.GetHashCode():x8}{extension}");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, Convert.FromBase64String(image.Base64));
        }

        Shell.Open(path);
    }

    /// <summary>Above this a pasted text becomes an attachment instead of filling the input box.</summary>
    private const int LongPasteChars = 2000;

    private const int LongPasteLines = 20;

    /// <summary>
    /// Ctrl+V: an image or files on the clipboard become an attachment (with a preview); a long text (a log, a stack
    /// trace, a file) too, so the input box stays usable; short text is pasted as usual. Decided after reading the
    /// clipboard, so the key is taken over and the text pasted by hand.
    /// The input box accepts line breaks (AcceptsReturn): otherwise Avalonia keeps only the first line of a paste.
    /// </summary>
    private async Task PasteAsync()
    {
        var clipboard = Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } || await clipboard.TryGetBitmapAsync() is not null)
            {
                if (await Buddy.App.Input.AttachmentReader.FromClipboardAsync(clipboard, _settings.Assistant.MaxAttachmentChars) is { } attachment)
                {
                    Attach(attachment);
                    return;
                }
            }

            if (await clipboard.TryGetTextAsync() is { } text)
            {
                var lines = text.Count(c => c == '\n') + 1;
                if (text.Length > LongPasteChars || lines > LongPasteLines)
                {
                    var max = _settings.Assistant.MaxAttachmentChars;
                    Attach(Buddy.Core.Chat.Attachment.FromText(
                        text.Length > max ? text[..max] + "\n[…tagliato]" : text,
                        $"testo incollato ({lines} righe{(text.Length > max ? ", tagliato" : "")})"));
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Clipboard busy: fall back to a normal paste.
        }

        Input.Paste();
    }

    private async void OnTaskActionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is string action)
        {
            await _conversation.RunTaskActionAsync(action);
        }
    }

    private async void OnAttachmentActionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string request)
        {
            // Whatever the user already typed replaces the ready-made question, keeping the chosen model.
            var typed = Input.Text?.Trim() ?? "";
            if (typed.Length > 0)
            {
                request = request[..request.IndexOf(' ')] + " " + typed;
                Input.Clear();
            }

            await _conversation.SendAsync(request);
        }
    }

    private void OnRemoveAttachmentClick(object? sender, RoutedEventArgs e) => _conversation.PendingAttachment = null;

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

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ChatEntry entry in e.NewItems)
            {
                entry.PropertyChanged += (_, _) => ScrollToEnd();

                // A permission prompt must be seen: open the bubble even if it was closed.
                if (entry is PermissionEntry && !IsVisible)
                {
                    ShowNearAnchor();
                }
            }
        }

        ScrollToEnd();
    }

    private void OnConversationChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Conversation.Mode):
                UpdateModeText();
                break;
            case nameof(Conversation.CurrentTask) or nameof(Conversation.TaskActions):
                UpdateTaskStrip();
                break;
            case nameof(Conversation.PendingAttachment):
                UpdateAttachment();
                break;
            case nameof(Conversation.HasPendingReply):
                UpdatePlaceholder();
                break;
        }
    }

    private void UpdateModeText()
    {
        var claude = _conversation.Mode == ChatMode.Claude;
        var name = string.IsNullOrWhiteSpace(_settings.Appearance.Name) ? "Buddy" : _settings.Appearance.Name;
        ModeText.Text = $"{name} · {(claude ? "Claude" : "Locale")}";
        ModeDot.Fill = (IBrush)Resources[claude ? "ClaudeAccent" : "LocalAccent"]!;
    }

    private void ScrollToEnd()
    {
        // Coalesce the many streamed chunks into one scroll per layout pass.
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            Scroller.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = true;
            await PasteAsync();
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var text = Input.Text ?? "";
            var caret = Math.Clamp(Input.CaretIndex, 0, text.Length);
            Input.Text = text.Insert(caret, Environment.NewLine);
            Input.CaretIndex = caret + Environment.NewLine.Length;
            return;
        }

        var message = Input.Text ?? "";
        Input.Clear();
        await _conversation.SendAsync(message);
    }

    /// <summary>The markdown viewers would swallow the mouse wheel; scroll the whole chat instead.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Input.IsPointerOver)
        {
            return;
        }

        Scroller.Offset = Scroller.Offset.WithY(Math.Max(0, Scroller.Offset.Y - e.Delta.Y * 50));
        e.Handled = true;
    }

    private void OnAllowClick(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as PermissionEntry)?.Allow();

    private void OnDenyClick(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as PermissionEntry)?.Deny();

    private async void OnEscalateClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ChatEntry entry)
        {
            await _conversation.EscalateAsync(entry);
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
