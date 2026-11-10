using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Buddy.App.Chat;
using Buddy.App.Project;
using Buddy.Core;
using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Buddy.Core.Context;
using Buddy.Core.Llm;
using Buddy.Core.Repo;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Buddy.App.Tests;

/// <summary>The buddy's chat inside the project board: the same chat as the bubble, on the same conversation.</summary>
public class BoardChatUiTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("buddy-board-chat-").FullName;
        ChatView.HistoryFile = Path.Combine(_root, "history.jsonl");
        ChatView.SaveSettings = _ => { }; // never the user's settings file
        BubbleWindow.LoadSize = () => null; // never the user's window state
        BubbleWindow.SaveSize = (_, _) => { };
    }

    [TearDown]
    public void TearDown()
    {
        ChatView.HistoryFile = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a watcher may still hold the folder for a moment
        }
    }

    private BuddySettings Settings()
    {
        var settings = new BuddySettings
        {
            ProjectPath = Path.Combine(_root, "vault"),
            Ollama = { BaseUrl = "http://127.0.0.1:1" }, // refused at once: the local answer fails fast
            Voice = { Enabled = false },
        };
        Directory.CreateDirectory(settings.ProjectPath);
        return settings;
    }

    private static Conversation Conversation(BuddySettings settings)
    {
        var options = Options.Create(settings);
        var vault = new VaultIndex(options);
        var repo = new GitRepo(options, TimeProvider.System);
        var local = new LocalAssistant(new OllamaClient(new HttpClient(), options), new ContextBuilder(vault, options, TimeProvider.System), options);
        return new Conversation(local, new ClaudeAuth(options), new AssistantCommands(vault, repo, options), repo, options);
    }

    private static async Task SettleAsync(int milliseconds = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string?> Texts(Visual view) =>
        view.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text).ToList();

    [AvaloniaTest]
    public async Task The_board_s_chat_shows_and_sends_in_the_same_conversation_as_the_bubble()
    {
        var settings = Settings();
        var conversation = Conversation(settings);
        conversation.Entries.Add(new ChatEntry(Speaker.User, "come avvio il TaskExecutor?"));
        var bubble = new BubbleWindow(conversation, Options.Create(settings));
        bubble.Show();
        using var vault = new VaultIndex(Options.Create(settings));
        var board = new ProjectWindow(vault, Options.Create(settings)) { Width = 1300, Height = 760 };
        board.AttachChat(conversation);
        board.Show();
        await SettleAsync();

        Assert.That(board.ChatToggle.IsVisible, Is.True, "the board offers its chat");
        Assert.That(board.ChatPanel.IsVisible, Is.False, "closed at first");
        Press(board, Key.J, RawInputModifiers.Control);
        await SettleAsync();
        Assert.That(board.ChatPanel.IsVisible, Is.True, "Ctrl+J opens it");
        Assert.That(board.IsChatOpen, Is.True);
        Assert.That(Texts(board.Chat), Does.Contain("come avvio il TaskExecutor?"), "the messages already there");

        // A message arriving shows in both chats.
        conversation.Entries.Add(new ChatEntry(Speaker.System, "Nota di sistema per entrambe"));
        await SettleAsync();
        Assert.That(Texts(board.Chat), Does.Contain("Nota di sistema per entrambe"));
        Assert.That(Texts(bubble.Chat), Does.Contain("Nota di sistema per entrambe"));

        // "/" suggestions and sending from the board's box.
        board.Chat.Input.Focus();
        board.KeyTextInput("/riprend");
        Dispatcher.UIThread.RunJobs();
        Assert.That(board.Chat.SuggestionsPanel.IsVisible, Is.True);
        Press(board, Key.Tab);
        Assert.That(board.Chat.Input.Text, Is.EqualTo("/riprendi "));
        board.Chat.Input.Text = "";
        board.KeyTextInput("ciao dalla bacheca");
        Press(board, Key.Enter);
        await SettleAsync();
        Assert.That(board.Chat.Input.Text, Is.Empty.Or.Null, "sent");
        Assert.That(conversation.Entries.Any(e => e.Speaker == Speaker.User && e.Text == "ciao dalla bacheca"), Is.True);
        Assert.That(Texts(bubble.Chat), Does.Contain("ciao dalla bacheca"), "the bubble shows it too");

        // ↑ in the bubble brings back what was sent from the board: one history.
        bubble.Chat.Input.Focus();
        Press(bubble, Key.Up);
        Assert.That(bubble.Chat.Input.Text, Is.EqualTo("ciao dalla bacheca"));

        // The mentor's side chat (☕) is the same state in both.
        board.Chat.SideButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(100);
        Assert.That(conversation.ShowingSide, Is.True);
        Assert.That(bubble.Chat.EntriesList.ItemsSource, Is.SameAs(conversation.SideEntries));
        Assert.That(board.Chat.EntriesList.ItemsSource, Is.SameAs(conversation.SideEntries));
        board.Chat.SideButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // Ctrl+F in the chat searches the chat; Esc closes the search, then the chat (not the board).
        board.Chat.Input.Focus();
        Press(board, Key.F, RawInputModifiers.Control);
        Assert.That(board.Chat.SearchBar.IsVisible, Is.True);
        board.KeyTextInput("taskexecutor");
        Dispatcher.UIThread.RunJobs();
        Assert.That(board.Chat.SearchCount.Text, Is.EqualTo("1/1"));
        Press(board, Key.Escape);
        Assert.That(board.Chat.SearchBar.IsVisible, Is.False);
        Press(board, Key.Escape);
        Assert.That(board.ChatPanel.IsVisible, Is.False, "Esc in the chat closes the chat");
        Assert.That(board.IsVisible, Is.True, "the board stays open");

        board.Close();
        bubble.Close();
    }

    [AvaloniaTest]
    public async Task Claude_s_permission_prompt_is_answered_in_the_board_without_opening_the_bubble()
    {
        var settings = Settings();
        var conversation = Conversation(settings);
        var bubble = new BubbleWindow(conversation, Options.Create(settings));
        using var vault = new VaultIndex(Options.Create(settings));
        var board = new ProjectWindow(vault, Options.Create(settings)) { Width = 1300, Height = 760 };
        board.AttachChat(conversation);
        bubble.ChatShownElsewhere = () => board.IsChatOpen;
        board.Show();
        board.ShowChat();
        await SettleAsync();

        using var input = System.Text.Json.JsonDocument.Parse("""{"command":"dotnet test"}""");
        var entry = new PermissionEntry(new PermissionRequest("r1", "Bash", input.RootElement.Clone()));
        conversation.Entries.Add(entry);
        await SettleAsync();

        Assert.That(bubble.IsVisible, Is.False, "the board's chat is in sight: no bubble on top of it");
        var allow = board.Chat.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Sì");
        allow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var decision = await entry.Decision;
        Assert.That(decision.Allow, Is.True);

        // With the board's chat closed, a prompt opens the bubble as before.
        board.HideChat();
        var second = new PermissionEntry(new PermissionRequest("r2", "Bash", input.RootElement.Clone()));
        conversation.Entries.Add(second);
        await SettleAsync();
        Assert.That(bubble.IsVisible, Is.True);

        board.Close();
        bubble.Close();
    }
}
