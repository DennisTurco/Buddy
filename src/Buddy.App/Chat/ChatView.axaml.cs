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

/// <summary>
/// The chat with the buddy's AI: messages, search (Ctrl+F), the working strip with ■ Ferma, attachments, "/"
/// suggestions, the input box with 🎤 and 📎, zoom (Ctrl+/-), pins and the model picker. The bubble and the project
/// board host one each, attached to the same <see cref="Conversation"/>: both show the same messages and state.
/// </summary>
public partial class ChatView : UserControl
{
    private const string MicIdleTip = "Detta (click per iniziare, click per finire). Shift+click: appunto diretto in To-do Appunti. Trascrizione locale con Whisper.";

    private Conversation _conversation = null!;
    private BuddySettings _settings = null!;
    private Buddy.Core.Claude.ClaudeUsage? _usage;
    /// <summary>The full panel of the plan limits is open (the header always shows the mini bar).</summary>
    private bool _limitsOpen;
    private readonly DispatcherTimer _workingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>The messages sent, kept across restarts for ↑ (one JSON string per line, the newest last).</summary>
    private static string HistoryPath => HistoryFile ?? Path.Combine(SettingsStore.Folder, "chat-history.jsonl");

    /// <summary>Another file for the history (tests), instead of the one in the user's data folder.</summary>
    internal static string? HistoryFile { get; set; }

    /// <summary>How the chat saves the settings (model, zoom); tests replace it so the user's file is never written.</summary>
    internal static Action<BuddySettings> SaveSettings { get; set; } = SettingsStore.Save;

    /// <summary>The zoom changed in one chat: the others on screen follow.</summary>
    private static event Action? ZoomChanged;

    private const int MaxHistory = 200;

    private static List<string>? _sharedSent;
    private static string? _sharedSentPath;

    /// <summary>One history for every chat on screen: what is sent from the board comes back with ↑ in the bubble.</summary>
    private static List<string> Sent
    {
        get
        {
            if (_sharedSent is null || _sharedSentPath != HistoryPath)
            {
                _sharedSentPath = HistoryPath;
                _sharedSent = LoadHistory();
            }

            return _sharedSent;
        }
    }

    private static List<string> LoadHistory()
    {
        try
        {
            return File.Exists(HistoryPath)
                ? File.ReadLines(HistoryPath).Select(l => System.Text.Json.JsonSerializer.Deserialize<string>(l) ?? "").Where(l => l.Length > 0).TakeLast(MaxHistory).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllLines(HistoryPath, Sent.TakeLast(MaxHistory).Select(s => System.Text.Json.JsonSerializer.Serialize(s)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not worth bothering the user: the history is a convenience
        }
    }

    private bool _scrollPending;

    /// <summary>The chat follows new text only while the user is at the bottom; scrolled up, it stays put.</summary>
    private bool _stickToEnd = true;

    /// <summary>Position in <see cref="Sent"/> while browsing with ↑/↓; -1 when not browsing.</summary>
    private int _historyIndex = -1;

    private string _draft = "";
    private IReadOnlySet<string>? _skills;
    private List<SuggestionView> _suggestions = [];
    private int _suggestion;

    public ChatView()
    {
        InitializeComponent();

        // Tunnel: Enter must be seen before the text box handles it; the wheel before the markdown viewers eat it.
        Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        Input.TextChanged += (_, _) => OnInputTextChanged();
        Scroller.ScrollChanged += OnScrollChanged;
        _workingTimer.Tick += (_, _) => UpdateWorking();
        SearchBox.TextChanged += (_, _) => UpdateSearch();
        MicButton.AddHandler(PointerPressedEvent, (_, e) => _shiftOnMic = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel);
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _zoomSave.Tick += (_, _) =>
        {
            _zoomSave.Stop();
            Save(_conversation, _settings); // saves the whole settings, the zoom with them
        };
        KeyDown += (_, e) => HandleKey(e);
        SizeChanged += (_, e) => FitMessages(e.NewSize.Width);
    }

    /// <summary>The host's own buttons in the header, on the right (the bubble: ⛶ and ✕).</summary>
    public Avalonia.Controls.Controls HeaderButtons => HeaderExtras.Children;

    /// <summary>Whether <see cref="Attach"/> was called (and not undone by <see cref="Detach"/>).</summary>
    public bool IsAttached { get; private set; }

    /// <summary>Shows <paramref name="conversation"/>; the host calls it once, after applying the appearance to its window.</summary>
    public void Attach(Conversation conversation, BuddySettings settings, Buddy.Core.Claude.ClaudeUsage? usage = null)
    {
        _conversation = conversation;
        _settings = settings;
        _usage = usage;
        IsAttached = true;
        if (usage is not null)
        {
            // Today's Claude cost and the 5-hour limit next to the title; details in the tooltip and with /uso.
            usage.Changed += OnUsageChanged;
            ShowUsage();
        }

        EntriesList.ItemsSource = conversation.VisibleEntries;
        conversation.Entries.CollectionChanged += OnEntriesChanged;
        conversation.SideEntries.CollectionChanged += OnEntriesChanged;
        conversation.PropertyChanged += OnConversationChanged;
        SaveChoices(conversation, settings);
        ZoomChanged += OnZoomChanged;

        ApplyZoom();
        UpdateSideButton();
        UpdateModeText();
        UpdateTaskStrip();
        UpdateAttachment();
        UpdatePlaceholder();
        UpdatePinned();
        UpdateWorking();
        MicButton.IsVisible = settings.Voice.Enabled;
    }

    /// <summary>Stops following the conversation (the board's chat, when its window closes).</summary>
    public void Detach()
    {
        if (!IsAttached)
        {
            return;
        }

        IsAttached = false;
        _workingTimer.Stop();
        _zoomSave.Stop();
        ZoomChanged -= OnZoomChanged;
        if (_usage is not null)
        {
            _usage.Changed -= OnUsageChanged;
        }

        _conversation.Entries.CollectionChanged -= OnEntriesChanged;
        _conversation.SideEntries.CollectionChanged -= OnEntriesChanged;
        _conversation.PropertyChanged -= OnConversationChanged;
        foreach (var entry in _conversation.Entries.Concat(_conversation.SideEntries))
        {
            entry.PropertyChanged -= OnEntryChanged;
        }

        CloseSearch();
        EntriesList.ItemsSource = null;
    }

    private void OnUsageChanged() => Dispatcher.UIThread.Post(ShowUsage);

    private void ShowUsage()
    {
        if (_usage is not { } usage)
        {
            return;
        }

        var limits = usage.Limits.Select(Project.LimitView.From).ToList();
        UsageText.Text = usage.ShortText() + (usage.LimitsProblem is { } problem ? "\n⚠ " + problem : "");
        LimitsList.ItemsSource = limits;
        LimitsButton.IsVisible = usage.TodayTurns > 0 || limits.Count > 0;
        LimitsPanel.IsVisible = _limitsOpen && LimitsButton.IsVisible;

        // The mini bar: the first limit with a percentage (the 5-hour one), or just today's cost.
        var first = limits.FirstOrDefault(l => l.HasPercent);
        LimitsMiniBar.IsVisible = first is not null;
        LimitsMiniBar.Value = first?.Percent ?? 0;
        LimitsMiniBar.Classes.Set("warning", first?.IsWarning == true);
        LimitsMiniText.Text = first?.PercentText ?? usage.ShortText();
        ToolTip.SetTip(LimitsButton, usage.Describe().Replace("**", "") + "\n\nClick: " + (_limitsOpen ? "chiudi i limiti" : "tutti i limiti"));
    }

    private void OnZoomChanged() => ApplyZoom();

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e) => ScrollToEnd();

    /// <summary>The chat came into sight: the mentor's news counts as seen and the current task is read again.</summary>
    public async void OnShown()
    {
        if (!IsAttached)
        {
            return;
        }

        _conversation.AcknowledgeMentor();
        await _conversation.RefreshCurrentTaskAsync();
    }

    /// <summary>Puts the keyboard in the input box, with the latest messages in view.</summary>
    public void FocusInput()
    {
        Input.Focus();
        ScrollToEnd();
    }

    /// <summary><paramref name="text"/> in the input box, ready to edit and send.</summary>
    public void SetDraft(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
        Input.Focus();
    }

    /// <summary>Sends <paramref name="text"/> as if typed here, bringing the chat to its latest messages.</summary>
    public async Task SendAsync(string text)
    {
        ScrollToEnd(force: true);
        await _conversation.SendAsync(text);
    }

    /// <summary>Brings the chat back to its latest messages.</summary>
    public void ScrollToLatest() => ScrollToEnd(force: true);

    /// <summary>
    /// The chat's keys: Ctrl+/Ctrl-/Ctrl+0 zoom, Ctrl+F search, Esc closes the search or the suggestions. Also called
    /// by the host for keys that reach its window without passing here (nothing focused). True when handled.
    /// </summary>
    internal bool HandleKey(KeyEventArgs e)
    {
        if (e.Handled || !IsAttached)
        {
            return e.Handled;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key is Key.OemPlus or Key.Add)
        {
            Zoom(ZoomStep);
        }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract)
        {
            Zoom(-ZoomStep);
        }
        else if (ctrl && e.Key is Key.D0 or Key.NumPad0)
        {
            Zoom(0);
        }
        else if (e.Key == Key.F && ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            OpenSearch();
        }
        else if (e.Key == Key.Escape && SearchBar.IsVisible)
        {
            CloseSearch();
        }
        else if (e.Key == Key.Escape && SuggestionsPanel.IsVisible)
        {
            CloseSuggestions();
        }
        else
        {
            return false;
        }

        e.Handled = true;
        return true;
    }

    /// <summary>Long lines are hard to read: messages follow the chat's width but stop at about 110 characters.</summary>
    private void FitMessages(double width)
    {
        // The chat sits 44 px inside the bubble's window; the widths were tuned on the window's.
        var text = Math.Min(width + 44 - 120, 1000);
        Resources["UserMaxWidth"] = Math.Max(290, text * 0.8);
        Resources["AssistantMaxWidth"] = Math.Max(320, text);
    }

    private IBrush Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    // ---- Text size (Ctrl+ / Ctrl- / Ctrl+0) -------------------------------------------------------------------

    private const double ZoomStep = 0.1;
    private const double MinZoom = 0.7;
    private const double MaxZoom = 2.0;

    /// <summary>Zooms the messages by <paramref name="step"/> (0 = back to normal); remembered in the settings.</summary>
    internal void Zoom(double step)
    {
        var zoom = step == 0 ? 1 : Math.Round(Math.Clamp(_settings.Appearance.ChatZoom + step, MinZoom, MaxZoom), 2);
        if (Math.Abs(zoom - _settings.Appearance.ChatZoom) < 0.001 && step != 0)
        {
            return;
        }

        _settings.Appearance.ChatZoom = zoom;
        ZoomChanged?.Invoke(); // every chat on screen (bubble and board) follows
        ScrollToEnd();
        _zoomSave.Stop();
        _zoomSave.Start(); // saved once the keys stop, not at every step
    }

    private void ApplyZoom()
    {
        var zoom = _settings.Appearance.ChatZoom is > 0 and var z ? Math.Clamp(z, MinZoom, MaxZoom) : 1;
        EntriesZoom.LayoutTransform = Math.Abs(zoom - 1) < 0.001 ? null : new Avalonia.Media.ScaleTransform(zoom, zoom);
    }

    private readonly DispatcherTimer _zoomSave = new() { Interval = TimeSpan.FromSeconds(1) };

    // ---- Working strip and stop -------------------------------------------------------------------------------

    private void UpdateWorking()
    {
        var answering = _conversation.IsAnswering;
        WorkingStrip.IsVisible = answering;
        if (!answering)
        {
            _workingTimer.Stop();
            return;
        }

        if (!_workingTimer.IsEnabled)
        {
            _workingTimer.Start();
        }

        var who = _conversation.Answering == Speaker.Claude ? "Claude sta lavorando" : "Il modello locale sta rispondendo";
        var elapsed = _conversation.AnswerStarted is { } started ? DateTimeOffset.Now - started : TimeSpan.Zero;
        WorkingText.Text = elapsed.TotalSeconds < 1
            ? $"⏳ {who}…"
            : $"⏳ {who}… {(elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s" : $"{elapsed.Seconds}s")}";
    }

    private async void OnStopClick(object? sender, RoutedEventArgs e) => await _conversation.StopAsync();

    private async void OnNewConversationClick(object? sender, RoutedEventArgs e)
    {
        await _conversation.SendAsync("/nuova");
        Input.Focus();
    }

    /// <summary>Copies an answer's markdown; the button shows a tick for a moment.</summary>
    private async void OnCopyEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChatEntry entry } button || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(entry.Text);
        var icon = button.Content;
        button.Content = Icons.Of("IconCheck", size: 13);
        await Task.Delay(1200);
        button.Content = icon;
    }

    // ---- Search (Ctrl+F) --------------------------------------------------------------------------------------

    private List<ChatEntry> _hits = [];
    private int _hit = -1;

    private void OpenSearch()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        UpdateSearch();
    }

    private void CloseSearch()
    {
        SearchBar.IsVisible = false;
        foreach (var entry in _hits)
        {
            entry.IsHighlighted = false;
        }

        _hits = [];
        _hit = -1;
        Input.Focus();
    }

    /// <summary>Every message containing the text is outlined; the count shows where the current one is.</summary>
    private void UpdateSearch()
    {
        foreach (var entry in _hits)
        {
            entry.IsHighlighted = false;
        }

        var query = SearchBox.Text?.Trim() ?? "";
        _hits = query.Length == 0 ? [] : _conversation.VisibleEntries.Where(e => e.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        foreach (var entry in _hits)
        {
            entry.IsHighlighted = true;
        }

        _hit = _hits.Count == 0 ? -1 : _hits.Count - 1; // the most recent first, as one usually looks back
        ShowHit();
    }

    private void MoveHit(int step)
    {
        if (_hits.Count == 0)
        {
            return;
        }

        _hit = (_hit + step + _hits.Count) % _hits.Count;
        ShowHit();
    }

    private void ShowHit()
    {
        SearchCount.Text = _hits.Count == 0 ? (string.IsNullOrWhiteSpace(SearchBox.Text) ? "" : "0") : $"{_hit + 1}/{_hits.Count}";
        if (_hit < 0)
        {
            return;
        }

        _stickToEnd = false; // stay on the hit even while an answer streams
        if (EntriesList.ContainerFromItem(_hits[_hit]) is Control container)
        {
            container.BringIntoView();
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Up:
                MoveHit(-1);
                break;
            case Key.Enter:
            case Key.Down:
                MoveHit(1);
                break;
            case Key.Escape:
                CloseSearch();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnSearchPreviousClick(object? sender, RoutedEventArgs e) => MoveHit(-1);

    private void OnSearchNextClick(object? sender, RoutedEventArgs e) => MoveHit(1);

    private void OnSearchCloseClick(object? sender, RoutedEventArgs e) => CloseSearch();

    // ---- Scrolling --------------------------------------------------------------------------------------------

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Only a move of the offset (the user, or ScrollToEnd) changes the decision; content growing keeps it.
        if (e.OffsetDelta.Y != 0)
        {
            _stickToEnd = Scroller.Offset.Y + Scroller.Viewport.Height >= Scroller.Extent.Height - 30;
        }

        JumpToEndButton.IsVisible = !_stickToEnd && Scroller.Extent.Height > Scroller.Viewport.Height + 30;
    }

    private void OnJumpToEndClick(object? sender, RoutedEventArgs e) => ScrollToEnd(force: true);

    // ---- Input: history and command suggestions ---------------------------------------------------------------

    /// <summary>Text put in the box by the buddy (history, completion); the change it raises is not "typing".</summary>
    private string? _setText;

    private void SetInput(string text)
    {
        // Avalonia may raise TextChanged after this returns: the text itself tells it was ours.
        _setText = text;
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    private void OnInputTextChanged()
    {
        if ((Input.Text ?? "") != _setText)
        {
            _historyIndex = -1; // typing ends the browsing of the history
            _setText = null;
        }

        var text = Input.Text ?? "";
        _skills ??= Buddy.Core.Claude.ClaudeSkills.Installed(_settings);
        var matches = text.StartsWith('/') && !text.Contains(' ') && !text.Contains('\n')
            ? _conversation.Commands.Matching(text, _skills, templates: _conversation.Templates)
            : [];
        _suggestions = matches.Select(c => new SuggestionView(c)).ToList();
        _suggestion = 0;
        ShowSuggestions();
    }

    private void ShowSuggestions()
    {
        for (var i = 0; i < _suggestions.Count; i++)
        {
            _suggestions[i].IsSelected = i == _suggestion;
        }

        SuggestionsList.ItemsSource = _suggestions;
        SuggestionsPanel.IsVisible = _suggestions.Count > 0;
    }

    private void CloseSuggestions()
    {
        _suggestions = [];
        ShowSuggestions();
    }

    private void Complete(ChatCommand command)
    {
        SetInput(command.Name + " ");
        CloseSuggestions();
        Input.Focus();
    }

    private void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SuggestionView view)
        {
            Complete(view.Command);
        }
    }

    /// <summary>
    /// ↑/↓ choose a suggestion when the list is open; otherwise ↑ on the first line recalls the messages sent before
    /// (like a terminal) and ↓ goes forward again, back to what was being typed.
    /// </summary>
    private bool HandleNavigationKey(KeyEventArgs e)
    {
        if (_suggestions.Count > 0)
        {
            switch (e.Key)
            {
                case Key.Up:
                    _suggestion = (_suggestion - 1 + _suggestions.Count) % _suggestions.Count;
                    ShowSuggestions();
                    return true;
                case Key.Down:
                    _suggestion = (_suggestion + 1) % _suggestions.Count;
                    ShowSuggestions();
                    return true;
                case Key.Tab:
                case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    Complete(_suggestions[_suggestion].Command);
                    return true;
            }

            return false;
        }

        if (e.KeyModifiers != KeyModifiers.None || Sent.Count == 0)
        {
            return false;
        }

        var text = Input.Text ?? "";
        if (e.Key == Key.Up && (_historyIndex >= 0 || !text[..Math.Clamp(Input.CaretIndex, 0, text.Length)].Contains('\n')))
        {
            if (_historyIndex < 0)
            {
                _draft = text;
                _historyIndex = Sent.Count;
            }

            if (_historyIndex > 0)
            {
                _historyIndex--;
                SetInput(Sent[_historyIndex]);
            }

            return true;
        }

        if (e.Key == Key.Down && _historyIndex >= 0)
        {
            _historyIndex++;
            if (_historyIndex >= Sent.Count)
            {
                _historyIndex = -1;
                SetInput(_draft);
            }
            else
            {
                SetInput(Sent[_historyIndex]);
            }

            return true;
        }

        return false;
    }

    /// <summary>Set by the app; null when voice input is disabled.</summary>
    public Voice.VoiceInput? Voice { get; set; }

    /// <summary>In Claude mode the box says which model will answer and what a turn usually costs with it.</summary>
    private void UpdatePlaceholder()
    {
        var cost = _conversation.Usage.AverageText(_conversation.ClaudeModel);
        Input.PlaceholderText = _conversation.HasPendingReply
            ? "Rispondi al mentore… (o /salta)"
            : _conversation.ShowingSide
                ? "Chiacchiere col mentore… (/chiacchiera, /pillola, /ripasso)"
            : _conversation.Mode == ChatMode.Claude
                ? $"Scrivi a Claude ({Conversation.ClaudeModelLabel(_conversation.ClaudeModel).ToLowerInvariant()}{(cost.Length > 0 ? $", {cost}" : "")})… (/aiuto)"
                : "Scrivi… (/aiuto per i comandi)";
    }

    private bool _micToNote;
    private string _dictationBase = "";

    /// <summary>What was in the box before dictating, followed by the dictated text.</summary>
    private void ShowDictation(string text)
    {
        Input.Text = text.Length == 0 ? _dictationBase
            : _dictationBase.Length == 0 ? text
            : $"{_dictationBase.TrimEnd()} {text}";
        Input.CaretIndex = Input.Text.Length;
    }
    private bool _shiftOnMic;

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
                // Shift+click: what is said goes straight to To-do Appunti, without passing through the chat box.
                _micToNote = _shiftOnMic;
                _dictationBase = Input.Text ?? "";
                if (_settings.Voice.Live)
                {
                    // The text appears in the box while speaking; it is replaced by the final one at the end.
                    voice.StartLive(text => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (voice.IsRecording)
                        {
                            ShowDictation(text);
                        }
                    }));
                }
                else
                {
                    voice.StartRecording();
                }

                MicButton.Content = Icons.Of(_micToNote ? "IconNote" : "IconRecord", size: 15);
                MicButton.Foreground = Brushes.IndianRed;
                ToolTip.SetTip(MicButton, _micToNote ? "Detta l'appunto per To-do Appunti… click per finire" : "Sto ascoltando… click per finire");
                return;
            }

            MicButton.Content = Icons.Of("IconWait", size: 15);
            MicButton.IsEnabled = false;
            var text = await Task.Run(() => _settings.Voice.Live ? voice.StopLiveAsync() : voice.StopAndTranscribeAsync());
            if (_micToNote)
            {
                Input.Text = _dictationBase;
                if (text.Length > 0)
                {
                    await _conversation.SendAsync($"/appunto {text}");
                }
            }
            else
            {
                // The transcription goes into the box, to be checked before sending.
                ShowDictation(text);
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
                MicButton.Content = Icons.Of("IconMic", size: 15);
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
        // Files of the repo: "@path" references in the text for Claude (Shift while dropping attaches them instead).
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && FileReferences.ForRepo(Buddy.App.Input.AttachmentReader.DroppedFiles(e.DataTransfer),
                [_settings.Repo.Path, _settings.Claude.WorkingDirectory]) is { } references)
        {
            var text = Input.Text ?? "";
            var insert = string.Join(' ', references) + " ";
            SetInput(text.Length == 0 || text.EndsWith(' ') ? text + insert : $"{text} {insert}");
            Input.Focus();
            return;
        }

        if (Buddy.App.Input.AttachmentReader.FromDrop(e.DataTransfer, _settings.Assistant.MaxAttachmentChars) is { } attachment)
        {
            Attach(attachment);
        }
    }

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
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
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
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

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _conversation.VisibleEntries))
        {
            return; // the other chat: the ☕ button counts what is new there
        }

        if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove || e.NewItems?.OfType<ChatEntry>().Any(n => n.IsPinned) == true)
        {
            UpdatePinned(); // a conversation reopened, or cleared
        }

        if (e.NewItems is not null)
        {
            foreach (ChatEntry entry in e.NewItems)
            {
                entry.PropertyChanged += OnEntryChanged;
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
                UpdatePlaceholder();
                break;
            case nameof(Conversation.Pinned):
                UpdatePinned();
                break;
            case nameof(Conversation.ClaudeModel) or nameof(Conversation.LocalModel):
                UpdateModeText();
                UpdatePlaceholder();
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
            case nameof(Conversation.IsAnswering):
                UpdateWorking();
                break;
            case nameof(Conversation.ShowingSide):
                EntriesList.ItemsSource = _conversation.VisibleEntries;
                UpdateSideButton();
                UpdateModeText();
                UpdatePlaceholder();
                UpdateSearch();
                ScrollToEnd(force: true);
                break;
            case nameof(Conversation.UnreadSide):
                UpdateSideButton();
                break;
        }
    }

    /// <summary>☕ opens the mentor's side chat (with how many messages are new there), 💬 goes back to the work chat.</summary>
    private void OnSideClick(object? sender, RoutedEventArgs e)
    {
        _conversation.ShowingSide = !_conversation.ShowingSide;
        Input.Focus();
    }

    private void UpdateSideButton()
    {
        var side = _conversation.ShowingSide;
        var unread = _conversation.UnreadSide;
        SideButton.Content = side ? Icons.Of("IconChat") : Icons.Of("IconCoffee", unread > 0 ? unread.ToString() : null);
        if (!side && unread > 0)
        {
            SideButton.Foreground = Brush("MentorAccent");
        }
        else
        {
            SideButton.ClearValue(ForegroundProperty); // the header's muted colour
        }
        ToolTip.SetTip(SideButton, side
            ? "Torna alla chat di lavoro"
            : "Chiacchiere del mentore: pillole, ripassi e chiacchierate, fuori dalla chat di lavoro"
              + (unread > 0 ? $" ({unread} nuovi)" : ""));
    }

    private void UpdateModeText()
    {
        var claude = _conversation.Mode == ChatMode.Claude;
        var name = string.IsNullOrWhiteSpace(_settings.Appearance.Name) ? "Buddy" : _settings.Appearance.Name;
        // Claude or local is said by the dot's colour and the model button: the name alone leaves them room.
        ModeText.Text = _conversation.ShowingSide ? $"{name} · Chiacchiere" : name;
        ToolTip.SetTip(ModeText, _conversation.ShowingSide ? "Chat del mentore" : claude ? "Stai parlando con Claude" : "Stai parlando con il modello locale");
        ModelButton.IsVisible = !_conversation.ShowingSide;
        ModeDot.Fill = Brush(claude ? "ClaudeAccent" : "LocalAccent");
        ModelButton.Content = (claude ? Conversation.ClaudeModelLabel(_conversation.ClaudeModel) : _conversation.LocalModel) + " ▾";
    }

    // ---- Model picker -----------------------------------------------------------------------------------------

    /// <summary>Lists the models installed in Ollama (set by the app); null hides the local section.</summary>
    public Func<Task<IReadOnlyList<string>>>? LocalModelsSource { get; set; }

    private ContextMenu? _modelMenu;

    /// <summary>
    /// The model menu: Claude's aliases, then the local models installed in Ollama. Picking one switches to that
    /// model and its mode; the choice is saved in the settings. A ContextMenu, not a flyout: flyouts did not open on X11.
    /// </summary>
    private async void OnModelClick(object? sender, RoutedEventArgs e)
    {
        var items = new List<Control> { Header("Claude") };
        foreach (var (alias, label) in Conversation.ClaudeModels)
        {
            var current = _conversation.Mode == ChatMode.Claude && string.Equals(_conversation.ClaudeModel, alias, StringComparison.OrdinalIgnoreCase);
            items.Add(Item(label, current, async () => await _conversation.SetClaudeModelAsync(alias)));
        }

        if (!Conversation.ClaudeModels.Any(m => string.Equals(m.Alias, _conversation.ClaudeModel, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(Item(_conversation.ClaudeModel, _conversation.Mode == ChatMode.Claude,
                async () => await _conversation.SetClaudeModelAsync(_conversation.ClaudeModel)));
        }

        items.Add(Item("Scelta automatica (Haiku / Opus)", _conversation.AutoModel, () =>
        {
            _conversation.SetAutoModel(!_conversation.AutoModel);
            return Task.CompletedTask;
        }));

        if (LocalModelsSource is { } source)
        {
            items.Add(new Separator());
            items.Add(Header("Locale (Ollama)"));
            IReadOnlyList<string> local;
            try
            {
                local = await source();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                local = [];
            }

            if (!local.Contains(_conversation.LocalModel, StringComparer.OrdinalIgnoreCase))
            {
                local = [_conversation.LocalModel, .. local];
            }

            foreach (var model in local)
            {
                var current = _conversation.Mode == ChatMode.Local && string.Equals(_conversation.LocalModel, model, StringComparison.OrdinalIgnoreCase);
                items.Add(Item(model, current, () =>
                {
                    _conversation.SetLocalModel(model);
                    return Task.CompletedTask;
                }));
            }

            if (local.Count <= 1)
            {
                items.Add(new MenuItem { Header = "(Ollama non risponde o nessun altro modello installato)", IsEnabled = false });
            }
        }

        _modelMenu?.Close();
        _modelMenu = new ContextMenu
        {
            ItemsSource = items,
            Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft,
            PlacementTarget = ModelButton,
        };
        ModelButton.ContextMenu = _modelMenu;
        _modelMenu.Open(ModelButton);

        static MenuItem Header(string text) => new() { Header = text, IsEnabled = false, FontWeight = FontWeight.SemiBold };

        MenuItem Item(string label, bool current, Func<Task> pick)
        {
            var item = new MenuItem { Header = (current ? "✓ " : "    ") + label };
            item.Click += async (_, _) =>
            {
                await pick();
                Input.Focus();
            };
            return item;
        }
    }

    private void OnLimitsClick(object? sender, RoutedEventArgs e)
    {
        _limitsOpen = !_limitsOpen;
        ShowUsage();
    }

    /// <summary>Click on the open panel of the plan limits: closes it.</summary>
    private void OnLimitsPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _limitsOpen = false;
        ShowUsage();
        e.Handled = true;
    }

    /// <summary>The saved conversations, newest first; picking one reopens it (Claude resumes its session).</summary>
    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        var saved = _conversation.Archive?.List() ?? [];
        var items = new List<Control>();
        if (saved.Count == 0)
        {
            items.Add(new MenuItem { Header = "Nessuna conversazione salvata", IsEnabled = false });
        }

        foreach (var conversation in saved)
        {
            var item = new MenuItem { Header = conversation.Label };
            ToolTip.SetTip(item, $"{conversation.Entries.Count} messaggi");
            item.Click += async (_, _) =>
            {
                if (await _conversation.OpenSavedAsync(conversation))
                {
                    ScrollToEnd(force: true);
                    Input.Focus();
                }
            };
            items.Add(item);
        }

        if (_conversation.Archive is { } archive)
        {
            items.Add(new Separator());
            var folder = new MenuItem { Header = "Apri la cartella" };
            folder.Click += (_, _) =>
            {
                Directory.CreateDirectory(archive.Folder);
                Shell.Open(archive.Folder);
            };
            items.Add(folder);
        }

        var menu = new ContextMenu
        {
            ItemsSource = items,
            Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight,
            PlacementTarget = HistoryButton,
        };
        HistoryButton.ContextMenu = menu;
        menu.Open(HistoryButton);
    }

    /// <summary>
    /// A model picked here or with /modello (and the templates) is remembered for the next start: one listener per
    /// conversation, however many chats show it, so the settings are written once.
    /// </summary>
    private static void SaveChoices(Conversation conversation, BuddySettings settings)
    {
        if (SavingConversations.TryGetValue(conversation, out _))
        {
            return;
        }

        SavingConversations.Add(conversation, settings);
        conversation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Conversation.Templates) or nameof(Conversation.ClaudeModel) or nameof(Conversation.LocalModel))
            {
                Save(conversation, settings);
            }
        };
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Conversation, BuddySettings> SavingConversations = new();

    private static void Save(Conversation conversation, BuddySettings settings)
    {
        try
        {
            SaveSettings(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            conversation.Entries.Add(new ChatEntry(Speaker.System, $"Non riesco a salvare la scelta del modello: {ex.Message}"));
        }
    }

    private void ScrollToEnd() => ScrollToEnd(force: false);

    private void ScrollToEnd(bool force)
    {
        if (force)
        {
            _stickToEnd = true;
        }

        // Coalesce the many streamed chunks into one scroll per layout pass.
        if (_scrollPending || !_stickToEnd)
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

        if (e.Key is Key.Up or Key.Down or Key.Tab or Key.Enter && HandleNavigationKey(e))
        {
            e.Handled = true;
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
        if (message.Trim().Length > 0 && (Sent.Count == 0 || Sent[^1] != message.Trim()))
        {
            Sent.Add(message.Trim());
            SaveHistory();
        }

        _historyIndex = -1;
        Input.Clear();
        ScrollToEnd(force: true); // sending brings the chat back to the latest messages
        await _conversation.SendAsync(message);
    }

    /// <summary>The markdown viewers would swallow the mouse wheel; scroll the whole chat instead.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Zoom(e.Delta.Y > 0 ? ZoomStep : -ZoomStep);
            e.Handled = true;
            return;
        }

        if (Input.IsPointerOver)
        {
            return;
        }

        Scroller.Offset = Scroller.Offset.WithY(Math.Max(0, Scroller.Offset.Y - e.Delta.Y * 50));
        e.Handled = true;
    }

    private void OnAllowClick(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as PermissionEntry)?.Allow();

    private void OnDenyClick(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as PermissionEntry)?.Deny();

    private void OnAnswerClick(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as PermissionEntry)?.Answer();

    private void OnSkipQuestionsClick(object? sender, RoutedEventArgs e) => ((sender as Control)?.DataContext as PermissionEntry)?.Skip();

    // ---- Pins and notes from answers --------------------------------------------------------------------------

    private void UpdatePinned()
    {
        var pinned = _conversation.Pinned;
        PinnedList.ItemsSource = pinned;
        PinnedList.IsVisible = pinned.Count > 0;
    }

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ChatEntry entry)
        {
            _conversation.TogglePin(entry);
        }
    }

    private void OnUnpinClick(object? sender, RoutedEventArgs e) => OnPinClick(sender, e);

    /// <summary>A pinned line: back to the message in the chat.</summary>
    private void OnPinnedClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ChatEntry entry && EntriesList.ContainerFromItem(entry) is Control container)
        {
            _stickToEnd = false;
            container.BringIntoView();
        }
    }

    /// <summary>"💾": asks the title and the page, then writes the answer as a note in Pagine/&lt;page&gt;/.</summary>
    private async void OnSaveAsNoteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChatEntry entry)
        {
            return;
        }

        var pages = Buddy.Core.Vault.CustomPages.List(_settings.ProjectPath);
        var values = await Buddy.App.Settings.Dialogs.FormAsync(TopLevel.GetTopLevel(this) as Window, "Salva come nota",
            new Buddy.App.Settings.Dialogs.Field("Titolo della nota", entry.Headline.TrimEnd('…', '.', ':'), Required: true),
            new Buddy.App.Settings.Dialogs.Field(
                $"Pagina (cartella in Pagine/; esistenti: {(pages.Count == 0 ? "nessuna" : string.Join(", ", pages))})",
                Conversation.AnswersPage));
        if (values is not null)
        {
            _conversation.SaveAsNote(entry, values[0], values[1]);
        }
    }

    /// <summary>A button under a message: the message is the nearest ancestor holding a chat entry.</summary>
    private async void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChatAction action } control)
        {
            return;
        }

        var entry = Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(control)
            .OfType<Control>().Select(c => c.DataContext).OfType<ChatEntry>().FirstOrDefault();
        if (entry is not null)
        {
            ScrollToEnd(force: true);
            await _conversation.RunActionAsync(entry, action);
        }
    }

    private async void OnExplainItClick(object? sender, RoutedEventArgs e)
    {
        ScrollToEnd(force: true);
        await _conversation.ExplainItAsync();
    }

    private async void OnFollowUpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChatEntry entry, Tag: string label }
            && Conversation.FollowUps.FirstOrDefault(f => f.Label == label).Request is { } request)
        {
            ScrollToEnd(force: true);
            await _conversation.FollowUpAsync(entry, request);
        }
    }

    private async void OnEscalateClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ChatEntry entry)
        {
            await _conversation.EscalateAsync(entry);
        }
    }
}
