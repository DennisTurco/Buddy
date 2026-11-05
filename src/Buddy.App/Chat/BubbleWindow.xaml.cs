using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Buddy.Core.Chat;

namespace Buddy.App.Chat;

public partial class BubbleWindow : Window
{
    private readonly Conversation _conversation;
    private readonly Buddy.Core.BuddySettings _settings;
    private bool _scrollPending;

    public BubbleWindow(Conversation conversation, Microsoft.Extensions.Options.IOptions<Buddy.Core.BuddySettings> settings)
    {
        InitializeComponent();
        _conversation = conversation;
        _settings = settings.Value;
        ApplyAppearance(_settings.Appearance);
        EntriesList.ItemsSource = conversation.Entries;

        conversation.Entries.CollectionChanged += OnEntriesChanged;
        conversation.PropertyChanged += OnConversationChanged;
        Input.TextChanged += (_, _) => Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PreviewMouseWheel += OnPreviewMouseWheel;
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
        MicButton.Visibility = _settings.Voice.Enabled ? Visibility.Visible : Visibility.Collapsed;
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                _conversation.AcknowledgeMentor();
                await _conversation.RefreshCurrentTaskAsync();
            }
        };
    }

    /// <summary>Set by the app; null when voice input is disabled.</summary>
    public Voice.VoiceInput? Voice { get; set; }

    private void UpdatePlaceholder() =>
        Placeholder.Text = _conversation.HasPendingReply
            ? "Rispondi al mentore… (o /salta)"
            : "Scrivi… (/aiuto per i comandi)";

    private async void OnMicClick(object sender, RoutedEventArgs e)
    {
        if (Voice is not { } voice)
        {
            return;
        }

        if (!voice.IsModelAvailable)
        {
            _conversation.Entries.Add(new ChatEntry(Speaker.System, $"Modello Whisper non trovato: {voice.ModelPath}. Impostalo in Impostazioni → Mentore e voce."));
            return;
        }

        try
        {
            if (!voice.IsRecording)
            {
                voice.StartRecording();
                MicButton.Content = "⏺";
                MicButton.Foreground = System.Windows.Media.Brushes.IndianRed;
                MicButton.ToolTip = "Sto ascoltando… click per finire";
                return;
            }

            MicButton.Content = "⏳";
            MicButton.IsEnabled = false;
            var text = await Task.Run(() => voice.StopAndTranscribeAsync());
            if (text.Length > 0)
            {
                // The transcription goes into the box, to be checked before sending.
                Input.Text = Input.Text.Length > 0 ? $"{Input.Text} {text}" : text;
                Input.CaretIndex = Input.Text.Length;
                Input.Focus();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException or System.IO.IOException)
        {
            _conversation.Entries.Add(new ChatEntry(Speaker.System, $"Microfono: {ex.Message}"));
        }
        finally
        {
            if (!voice.IsRecording)
            {
                MicButton.Content = "🎤";
                MicButton.IsEnabled = true;
                MicButton.SetResourceReference(ForegroundProperty, "Ink");
                MicButton.ToolTip = "Detta (click per iniziare, click per finire). Trascrizione locale con Whisper.";
            }
        }
    }

    private void UpdateTaskStrip()
    {
        var task = _conversation.CurrentTask;
        TaskStrip.Visibility = task is null ? Visibility.Collapsed : Visibility.Visible;
        TaskText.Text = task is null ? "" : $"📌 {task}";
        TaskText.ToolTip = task?.ToString();
        TaskActionsList.ItemsSource = _conversation.TaskActions;
    }

    private void UpdateAttachment()
    {
        var attachment = _conversation.PendingAttachment;
        AttachmentPanel.Visibility = attachment is null ? Visibility.Collapsed : Visibility.Visible;
        AttachmentText.Text = attachment is null ? "" : $"📎 {attachment.Preview}";
    }

    private async void OnTaskActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string action)
        {
            await _conversation.RunTaskActionAsync(action);
        }
    }

    private async void OnAttachmentActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string request)
        {
            // Whatever the user already typed replaces the ready-made question, keeping the chosen model.
            var typed = Input.Text.Trim();
            if (typed.Length > 0)
            {
                request = request[..request.IndexOf(' ')] + " " + typed;
                Input.Clear();
            }

            await _conversation.SendAsync(request);
        }
    }

    private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e) => _conversation.PendingAttachment = null;

    /// <summary>Places the bubble above the pet, with the tail pointing at it, inside the visible screen area.</summary>
    public void PositionNear(Window pet)
    {
        // The monitor the pet is on, not the primary one: with several screens the bubble stays next to the pet.
        var area = Screens.WorkAreaOf(pet);
        if (_maximized)
        {
            FillArea(area);
            return;
        }

        var left = pet.Left + pet.ActualWidth - Width + 6;
        var top = pet.Top - Height + 26;
        Left = Math.Clamp(left, area.Left, area.Right - Width);
        Top = Math.Clamp(top, area.Top, area.Bottom - Height);
    }

    /// <summary>Replaces the theme brushes and the font; templates pick them up through DynamicResource.</summary>
    private void ApplyAppearance(Buddy.Core.AppearanceSettings appearance)
    {
        var theme = appearance.Theme;
        var defaults = Buddy.Core.ThemeColors.Light();
        void Brush(string key, string value, string fallback) =>
            Resources[key] = new System.Windows.Media.SolidColorBrush(
                Pet.PetSprites.ParseColor(value, (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback)));

        Brush("Paper", theme.Background, defaults.Background);
        Brush("Frame", theme.Border, defaults.Border);
        Brush("Ink", theme.Text, defaults.Text);
        Brush("Muted", theme.MutedText, defaults.MutedText);
        Brush("UserBubble", theme.UserBubble, defaults.UserBubble);
        Brush("LocalBubble", theme.LocalBubble, defaults.LocalBubble);
        Brush("LocalAccent", theme.LocalAccent, defaults.LocalAccent);
        Brush("ClaudeBubble", theme.ClaudeBubble, defaults.ClaudeBubble);
        Brush("ClaudeAccent", theme.ClaudeAccent, defaults.ClaudeAccent);
        Brush("InputBack", theme.Input, defaults.Input);
        Brush("MentorBubble", theme.MentorBubble, defaults.MentorBubble);
        Brush("MentorAccent", theme.MentorAccent, defaults.MentorAccent);

        if (!string.IsNullOrWhiteSpace(appearance.FontFamily))
        {
            FontFamily = new System.Windows.Media.FontFamily(appearance.FontFamily);
        }

        FontSize = Math.Clamp(appearance.FontSize, 9, 24);
    }

    /// <summary>The pet window: the bubble is placed next to it before being shown.</summary>
    public Window? Anchor { get; set; }

    public void ShowAndFocus()
    {
        ShowNearAnchor();
        Activate();
        Input.Focus();
        ScrollToEnd();
    }

    /// <summary>
    /// Positions on the pet's monitor before showing, then again once shown: with monitors at different DPI the
    /// window size in WPF units is only final after it appeared on that monitor.
    /// </summary>
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
        ModeDot.Fill = (System.Windows.Media.Brush)Resources[claude ? "ClaudeAccent" : "LocalAccent"];
    }

    private void ScrollToEnd()
    {
        // Coalesce the many streamed chunks into one scroll per layout pass.
        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            Scroller.ScrollToEnd();
        });
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var caret = Input.CaretIndex;
            Input.Text = Input.Text.Insert(caret, Environment.NewLine);
            Input.CaretIndex = caret + Environment.NewLine.Length;
            e.Handled = true;
            return;
        }

        e.Handled = true;
        var text = Input.Text;
        Input.Clear();
        await _conversation.SendAsync(text);
    }

    /// <summary>The markdown boxes would swallow the mouse wheel; scroll the whole chat instead.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Input.IsMouseOver)
        {
            return;
        }

        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void OnAllowClick(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as PermissionEntry)?.Allow();

    private async void OnEscalateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatEntry entry)
        {
            await _conversation.EscalateAsync(entry);
        }
    }

    private void OnDenyClick(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as PermissionEntry)?.Deny();

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            SetMaximized(!_maximized);
        }
        else if (!_maximized)
        {
            DragMove();
        }
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => SetMaximized(!_maximized);

    private const double NormalWidth = 380;
    private const double NormalHeight = 480;
    private bool _maximized;

    /// <summary>
    /// Full screen for long answers: the bubble fills the work area of the pet's monitor (taskbar excluded),
    /// messages get wider (up to a readable line length) and the tail is hidden. Toggled again, it goes back
    /// next to the pet.
    /// </summary>
    public void SetMaximized(bool maximized)
    {
        _maximized = maximized;
        MaximizeButton.Content = maximized ? "🗗" : "⛶";
        MaximizeButton.ToolTip = maximized ? "Riduci (F11 o doppio click sul titolo)" : "Schermo intero (F11 o doppio click sul titolo)";
        Tail.Visibility = maximized ? Visibility.Hidden : Visibility.Visible;

        if (maximized)
        {
            FillArea(Screens.WorkAreaOf(Anchor ?? this));
        }
        else
        {
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

    private void FillArea(Rect area)
    {
        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;

        // Long lines are hard to read: messages stop at about 110 characters even on a wide screen.
        var text = Math.Min(area.Width - 120, 1000);
        Resources["UserMaxWidth"] = Math.Max(290, text * 0.8);
        Resources["AssistantMaxWidth"] = Math.Max(320, text);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing only hides the bubble: the conversation lives as long as the pet.
        e.Cancel = true;
        Hide();
    }
}
