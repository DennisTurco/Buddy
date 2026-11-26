using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Buddy.Core.Chat;
using Buddy.Core.Claude;

using Buddy.App.Chat;

namespace Buddy.App.Project;

/// <summary>
/// The buddy's chat inside the board: the same <see cref="Buddy.App.Chat.ChatView"/> as the bubble, on the same conversation,
/// in a panel on the right that opens and closes (💬 Chat, Ctrl+J). Everything sent to the chat from the board
/// (skills, Ctrl+K commands, "Chiedi in chat") goes here while it is open.
/// </summary>
public partial class ProjectWindow
{
    /// <summary>Whether the chat panel was open when the board was last closed (for this run of the buddy).</summary>
    private static bool _chatWasOpen;

    /// <summary>Shows <paramref name="conversation"/> in the board's chat; without it the chat is not offered.</summary>
    public void AttachChat(Conversation conversation, ClaudeUsage? usage = null, Voice.VoiceInput? voice = null,
        Func<Task<IReadOnlyList<string>>>? localModels = null)
    {
        // The board keeps the system font size; the chat uses the one chosen for the bubble.
        if (!string.IsNullOrWhiteSpace(_settings.Appearance.FontFamily))
        {
            Chat.FontFamily = new FontFamily($"{_settings.Appearance.FontFamily}, {FontFamily.Default.Name}");
        }

        Chat.FontSize = Math.Clamp(_settings.Appearance.FontSize, 9, 24);
        Chat.Attach(conversation, _settings, usage);
        Chat.Voice = voice;
        Chat.LocalModelsSource = localModels;
        ChatToggle.IsVisible = true;
        Closed += (_, _) =>
        {
            _chatWasOpen = ChatPanel.IsVisible;
            Chat.Detach();
        };

        if (_chatWasOpen)
        {
            ShowChat(focus: false);
        }
    }

    /// <summary>True while the board's chat is on screen: messages for the chat go there instead of the bubble.</summary>
    public bool IsChatOpen => Chat.IsAttached && ChatPanel.IsVisible && IsVisible;

    /// <summary>Opens the chat panel (if the board has a chat) and, with <paramref name="focus"/>, its input box.</summary>
    public void ShowChat(bool focus = true)
    {
        if (!Chat.IsAttached)
        {
            return;
        }

        var opening = !ChatPanel.IsVisible;
        if (opening && _chatWidthBeforeWiden is null)
        {
            RestoreChatWidth();
        }

        ChatPanel.IsVisible = true;
        UpdateChatToggle();
        if (opening)
        {
            Chat.OnShown();
            Chat.ScrollToLatest();
        }

        if (focus)
        {
            Chat.FocusInput();
        }
    }

    public void HideChat()
    {
        ChatPanel.IsVisible = false;
        UpdateChatToggle();
    }

    /// <summary>"Chiedi in chat" with the board's chat open: the question in its input, ready to edit and send.</summary>
    public void ShowChatWithText(string text)
    {
        ShowChat();
        Chat.SetDraft(text);
    }

    /// <summary>Sends <paramref name="text"/> in the board's chat, opening it.</summary>
    public async Task SendInChatAsync(string text)
    {
        ShowChat();
        await Chat.SendAsync(text);
    }

    /// <summary>Whether the keyboard is in the chat (Esc then closes the chat instead of the board).</summary>
    private bool ChatHasFocus =>
        ChatPanel.IsVisible && FocusManager?.GetFocusedElement() is Avalonia.Visual focused && (focused == Chat || Chat.IsVisualAncestorOf(focused));

    private void ToggleChat()
    {
        if (ChatPanel.IsVisible)
        {
            HideChat();
        }
        else
        {
            ShowChat();
        }
    }

    private void UpdateChatToggle() =>
        ChatToggle.Content = Icons.Label(ChatPanel.IsVisible ? "💬 Chiudi chat" : "💬 Chat");

    private void OnChatToggleClick(object? sender, RoutedEventArgs e) => ToggleChat();

    // ---- Width: dragged on the left edge, or widened with ⤢ -----------------------------------------------------

    private const double ChatMinWidth = 320;
    private const double DefaultChatWidth = 420;

    /// <summary>The width before ⤢ widened it (null while not widened).</summary>
    private double? _chatWidthBeforeWiden;

    private double? _resizeStartX;
    private double _resizeStartWidth;

    /// <summary>At most the window minus room for the page on the left, so the board never vanishes.</summary>
    private double ChatMaxWidth => Math.Max(ChatMinWidth, Bounds.Width - MainGrid.ColumnDefinitions[0].ActualWidth - 360);

    private void RestoreChatWidth() => SetChatWidth(SettingsStore.BoardChatWidth ?? DefaultChatWidth, remember: false);

    private void SetChatWidth(double width, bool remember)
    {
        ChatPanel.Width = Math.Clamp(width, ChatMinWidth, ChatMaxWidth);
        if (remember)
        {
            SettingsStore.BoardChatWidth = ChatPanel.Width;
        }
    }

    private void OnWidenChatClick(object? sender, RoutedEventArgs e)
    {
        if (_chatWidthBeforeWiden is { } before)
        {
            _chatWidthBeforeWiden = null;
            SetChatWidth(before, remember: false);
        }
        else
        {
            _chatWidthBeforeWiden = ChatPanel.Width;
            SetChatWidth(ChatMaxWidth, remember: false);
        }

        UpdateWidenButton();
    }

    private void UpdateWidenButton()
    {
        var widened = _chatWidthBeforeWiden is not null;
        ChatWidenIcon.Data = this.FindResource(widened ? "IconRestore" : "IconMaximize") as Geometry;
        ToolTip.SetTip(ChatWidenButton, widened ? "Riporta la chat alla larghezza di prima" : "Allarga la chat su quasi tutta la finestra");
    }

    private void OnChatResizePressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = ChatPanel.Width;
        e.Pointer.Capture(ChatResizer);
        e.Handled = true;
    }

    private void OnChatResizeMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_resizeStartX is not { } start)
        {
            return;
        }

        // The panel is on the right: dragging the edge to the left makes it wider.
        SetChatWidth(_resizeStartWidth + (start - e.GetPosition(this).X), remember: false);
    }

    private void OnChatResizeReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_resizeStartX is null)
        {
            return;
        }

        _resizeStartX = null;
        e.Pointer.Capture(null);
        _chatWidthBeforeWiden = null; // a width chosen by hand is the new normal
        SettingsStore.BoardChatWidth = ChatPanel.Width;
        UpdateWidenButton();
    }

    private void OnCloseChatClick(object? sender, RoutedEventArgs e) => HideChat();
}
