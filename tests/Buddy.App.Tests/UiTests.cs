using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
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

[assembly: AvaloniaTestApplication(typeof(Buddy.App.Tests.TestApp))]

namespace Buddy.App.Tests;

/// <summary>The app's look (Fluent and Theme.axaml), without the buddy itself: no pet, tray or background services.</summary>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

        // The app's assembly must be loaded for its avares:// resources (Theme.axaml) to be found.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(BubbleWindow).TypeHandle);
        Styles.Add(new StyleInclude(new Uri("avares://Buddy.App/")) { Source = new Uri("avares://Buddy.App/Theme.axaml") });
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>
/// The windows' behaviour as the user meets it, driven through the real controls without a screen. Everything runs on
/// temporary folders: the user's vault, chat history and settings are never touched.
/// </summary>
public class UiTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("buddy-ui-").FullName;
        BubbleWindow.HistoryFile = Path.Combine(_root, "history.jsonl");
        BubbleWindow.SaveSettings = _ => { }; // never the user's settings file
    }

    [TearDown]
    public void TearDown()
    {
        BubbleWindow.HistoryFile = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a watcher may still hold the folder for a moment
        }
    }

    private BuddySettings Settings() => new()
    {
        ProjectPath = Path.Combine(_root, "vault"),
        Ollama = { BaseUrl = "http://127.0.0.1:1" }, // refused at once: the local answer fails fast
        Voice = { Enabled = false },
    };

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

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTest]
    public async Task Arrow_up_brings_back_the_message_sent_before()
    {
        var settings = Settings();
        var bubble = new BubbleWindow(Conversation(settings), Options.Create(settings));
        bubble.Show();
        bubble.Input.Focus();

        Type(bubble, "prima domanda");
        Press(bubble, Key.Enter);
        await SettleAsync();
        Assert.That(bubble.Input.Text, Is.Empty.Or.Null, "sent");

        Type(bubble, "bozza");
        bubble.Input.CaretIndex = 0;
        Press(bubble, Key.Up);
        Assert.That(bubble.Input.Text, Is.EqualTo("prima domanda"));

        Press(bubble, Key.Down);
        Assert.That(bubble.Input.Text, Is.EqualTo("bozza"), "↓ goes back to what was being typed");
        Assert.That(File.ReadAllText(BubbleWindow.HistoryFile!), Does.Contain("prima domanda"), "kept for the next start");
    }

    [AvaloniaTest]
    public void Typing_a_slash_suggests_commands_and_tab_completes()
    {
        var settings = Settings();
        var bubble = new BubbleWindow(Conversation(settings), Options.Create(settings));
        bubble.Show();
        bubble.Input.Focus();

        Type(bubble, "/chiacc");
        Assert.That(bubble.SuggestionsPanel.IsVisible, Is.True);

        Press(bubble, Key.Tab);
        Assert.That(bubble.Input.Text, Is.EqualTo("/chiacchiera "));
        Assert.That(bubble.SuggestionsPanel.IsVisible, Is.False);
    }

    [AvaloniaTest]
    public void Ctrl_plus_and_minus_resize_the_messages()
    {
        var settings = Settings();
        var bubble = new BubbleWindow(Conversation(settings), Options.Create(settings));
        bubble.Show();

        Press(bubble, Key.OemPlus, RawInputModifiers.Control);
        Press(bubble, Key.OemPlus, RawInputModifiers.Control);
        Assert.That(settings.Appearance.ChatZoom, Is.EqualTo(1.2).Within(0.001));
        Assert.That((bubble.EntriesZoom.LayoutTransform as Avalonia.Media.ScaleTransform)?.ScaleX, Is.EqualTo(1.2).Within(0.001));

        Press(bubble, Key.OemMinus, RawInputModifiers.Control);
        Assert.That(settings.Appearance.ChatZoom, Is.EqualTo(1.1).Within(0.001));

        Press(bubble, Key.D0, RawInputModifiers.Control);
        Assert.That(settings.Appearance.ChatZoom, Is.EqualTo(1));
        Assert.That(bubble.EntriesZoom.LayoutTransform, Is.Null);
        bubble.Close(); // before the delayed save, which writes the user's settings file
    }

    [AvaloniaTest]
    public void Ctrl_F_finds_and_outlines_the_messages()
    {
        var settings = Settings();
        var conversation = Conversation(settings);
        conversation.Entries.Add(new ChatEntry(Speaker.User, "come avvio il TaskExecutor?"));
        conversation.Entries.Add(new ChatEntry(Speaker.Claude, "Con l'argomento TEST il TaskExecutor apre il form."));
        conversation.Entries.Add(new ChatEntry(Speaker.User, "grazie"));
        var bubble = new BubbleWindow(conversation, Options.Create(settings));
        bubble.Show();

        Press(bubble, Key.F, RawInputModifiers.Control);
        Assert.That(bubble.SearchBar.IsVisible, Is.True);
        Type(bubble, "taskexecutor");

        Assert.That(conversation.Entries.Count(e => e.IsHighlighted), Is.EqualTo(2));
        Assert.That(bubble.SearchCount.Text, Is.EqualTo("2/2"));

        Press(bubble, Key.Escape);
        Assert.That(bubble.SearchBar.IsVisible, Is.False);
        Assert.That(conversation.Entries.Any(e => e.IsHighlighted), Is.False);
    }

    [AvaloniaTest]
    public async Task Ticking_a_manual_test_keeps_the_page_where_it_was()
    {
        var settings = Settings();
        var tests = Path.Combine(settings.ProjectPath, "Test manuali");
        Directory.CreateDirectory(tests);
        var cases = string.Join("\n", Enumerable.Range(1, 80).Select(i => $"- [ ] Caso numero {i} da provare a mano"));
        File.WriteAllText(Path.Combine(tests, "Test manuali T-003.md"), $"---\ntask: T-003\nupdated: 2026-11-04\n---\n\n## Casi\n\n{cases}\n");

        using var vault = new VaultIndex(Options.Create(settings));
        var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
        window.Show();
        window.ShowView("manualtests");
        await SettleAsync();

        window.ManualScroller.Offset = new Vector(0, 600);
        await SettleAsync();
        Assert.That(window.ManualScroller.Offset.Y, Is.EqualTo(600).Within(1), "scrolled down");

        var check = window.FindVisualCheck("Caso numero 30");
        check.IsChecked = true; // as a click does: the box is written to the file
        await SettleAsync(2500); // the vault notices the change and the window reloads

        Assert.That(File.ReadAllText(Path.Combine(tests, "Test manuali T-003.md")), Does.Contain("- [x] Caso numero 30 "));
        Assert.That(window.ManualScroller.Offset.Y, Is.EqualTo(600).Within(1), "still where the user was reading");
    }
}

public class PageFindTests
{
    [AvaloniaTest]
    public async Task Ctrl_F_on_a_note_finds_the_text_in_the_page()
    {
        var root = Directory.CreateTempSubdirectory("buddy-find-").FullName;
        try
        {
            var settings = new BuddySettings { ProjectPath = root };
            File.WriteAllText(Path.Combine(root, "Glossario.md"),
                "# Glossario\n\nIl **TaskExecutor** esegue i task.\n\nAltro paragrafo.\n\nDi nuovo il taskexecutor, in fondo.\n");
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            window.ShowView("notes");
            for (var i = 0; i < 10; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(30);
            }

            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.PageFindBar.IsVisible, Is.True, "Ctrl+F on a note searches the page");

            window.KeyTextInput("taskexecutor");
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.PageFindCount.Text, Is.EqualTo("1/2"));

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.PageFindCount.Text, Is.EqualTo("2/2"));

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.PageFindBar.IsVisible, Is.False);
            Assert.That(window.IsVisible, Is.True, "Esc closed the search, not the window");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

public class PagesAndPaletteTests
{
    private static async Task Settle()
    {
        for (var i = 0; i < 15; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }

    [AvaloniaTest]
    public async Task A_folder_in_Pagine_is_a_page_and_Ctrl_K_finds_its_notes()
    {
        var root = Directory.CreateTempSubdirectory("buddy-pages-").FullName;
        try
        {
            var settings = new BuddySettings { ProjectPath = root };
            CustomPages.CreateNote(root, "Ricette", "Avviare il TaskExecutor", "Argomento TEST da Visual Studio.");
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            Assert.That((window.Nav.ItemsSource as IEnumerable<NavItem>)!.Select(n => n.Key), Does.Contain("page:Ricette").And.Contain("page:+"));
            window.ShowView("page:Ricette");
            await Settle();
            var cards = (window.PageCards.ItemsSource as IEnumerable<PageCard>)!.ToList();
            Assert.That(cards.Select(c => c.Title), Is.EqualTo(new[] { "Avviare il TaskExecutor" }));
            Assert.That(window.ViewTitle.Text, Is.EqualTo("Ricette"));

            window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.That(window.Palette.IsVisible, Is.True);
            window.KeyTextInput("taskexec");
            Dispatcher.UIThread.RunJobs();
            Assert.That((window.PaletteList.SelectedItem as PaletteItem)?.Title, Is.EqualTo("Avviare il TaskExecutor"));

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            await Settle();
            Assert.That(window.Palette.IsVisible, Is.False);
            Assert.That(window.ViewTitle.Text, Is.EqualTo("Note"), "Enter opened the note");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

internal static class ProjectWindowTestExtensions
{
    /// <summary>The check box of a manual test case, by its text.</summary>
    public static CheckBox FindVisualCheck(this ProjectWindow window, string text) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<CheckBox>()
            .First(c => c.DataContext is CheckView view && view.Text.StartsWith(text, StringComparison.Ordinal) && view.Text[text.Length..].StartsWith(' '));
}
