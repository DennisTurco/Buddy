using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
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
    public async Task Copying_a_line_with_inline_code_keeps_the_whole_text()
    {
        var view = new MarkdownView { Markdown = "prima riga\n\nla voce è in `Da valutare` e `To-Do` ok" };
        var window = new Window { Content = view, Width = 800, Height = 200 };
        window.Show();
        await SettleAsync();

        var line = view.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>().Last();
        var origin = line.TranslatePoint(new Point(0, 0), window)!.Value;
        window.MouseDown(new Point(origin.X + 1, origin.Y + 6), MouseButton.Left);
        window.MouseMove(new Point(origin.X + 790, origin.Y + 6));
        window.MouseUp(new Point(origin.X + 790, origin.Y + 6), MouseButton.Left);
        await SettleAsync();
        Press(window, Key.C, RawInputModifiers.Control);
        await SettleAsync();

        var copied = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
        Assert.That(copied, Is.EqualTo("la voce è in Da valutare e To-Do ok"));
    }

    [AvaloniaTest]
    public async Task Claude_s_multiple_choice_question_is_answered_in_the_bubble()
    {
        var settings = Settings();
        var conversation = Conversation(settings);
        var bubble = new BubbleWindow(conversation, Options.Create(settings));
        bubble.Show();
        using var input = System.Text.Json.JsonDocument.Parse("""
            {"questions":[{"question":"Cosa faccio con T-004?","header":"T-004","multiSelect":false,
              "options":[{"label":"Rimanda","description":"tra 30 giorni"},{"label":"Approva","description":"va in To-Do"}]}]}
            """);
        var entry = new PermissionEntry(new PermissionRequest("r1", "AskUserQuestion", input.RootElement.Clone()));
        conversation.Entries.Add(entry);
        await SettleAsync();

        var visuals = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(bubble).ToList();
        Assert.That(visuals.OfType<TextBlock>().Any(t => t.Text == "Cosa faccio con T-004?"), Is.True, "the question is shown");
        var answer = visuals.OfType<Button>().Single(b => b.Content as string == "Rispondi");
        Assert.That(answer.IsEffectivelyEnabled, Is.False, "nothing chosen yet");

        visuals.OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.DataContext is OptionView { Label: "Approva" }).IsChecked = true;
        await SettleAsync(100);
        Assert.That(answer.IsEffectivelyEnabled, Is.True);
        answer.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        var decision = await entry.Decision;
        Assert.That(decision.UpdatedInput!["answers"]!["Cosa faccio con T-004?"]!.GetValue<string>(), Is.EqualTo("Approva"));
    }

    [AvaloniaTest]
    public async Task A_new_project_without_a_vault_gets_one_from_the_template()
    {
        var settings = Settings();
        var window = new Buddy.App.Settings.NewProjectWindow(settings);
        var task = window.ShowAndWaitAsync(null);
        await SettleAsync(100);
        var boxes = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBox>().ToList();
        var (name, vault) = (boxes[0], boxes[2]);

        name.Text = "Demo";
        Dispatcher.UIThread.RunJobs();
        Assert.That(boxes.Select(b => b.PlaceholderText), Has.Some.Contains("Obsidian"));
        Assert.That(vault.Text, Is.EqualTo(Buddy.Core.Vault.FlowTemplate.DefaultProjectFolder("Demo")), "proposed while not edited");

        var folder = Path.Combine(_root, "Vault", "Progetti", "Demo");
        vault.Text = folder;
        Dispatcher.UIThread.RunJobs();
        name.Text = "Demo 2";
        Dispatcher.UIThread.RunJobs();
        Assert.That(vault.Text, Is.EqualTo(folder), "a folder chosen by the user stays");

        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>().First(b => b.Content as string == "Crea e passa al progetto")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(200);

        var preset = await task;
        Assert.That(preset?.ProjectPath, Is.EqualTo(folder));
        Assert.That(File.Exists(Path.Combine(folder, "To-Do.md")), Is.True);
        Assert.That(preset!.AlwaysIncludedNotes, Does.Contain("To-Do.md"));
    }

    [AvaloniaTest]
    public async Task A_mentor_question_shows_its_quick_replies_and_a_click_answers()
    {
        var settings = Settings();
        var conversation = Conversation(settings);
        var bubble = new BubbleWindow(conversation, Options.Create(settings));
        bubble.Show();
        string? answered = null;
        conversation.PostMentor("Quale funzione proveresti con il TDD?\n\n<!-- risposte: Lo sconto | Il login -->", reply =>
        {
            answered = reply;
            return Task.CompletedTask;
        });
        await SettleAsync();

        var entry = conversation.Entries.Last();
        Assert.That(entry.Text, Is.EqualTo("Quale funzione proveresti con il TDD?"));
        var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(bubble).OfType<Button>()
            .Where(b => b.DataContext is ChatAction).ToList();
        Assert.That(buttons.Select(b => ((ChatAction)b.DataContext!).Label), Is.EqualTo(new[] { "Lo sconto", "Il login" }));

        buttons[0].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        Assert.That(answered, Is.EqualTo("Lo sconto"));
        Assert.That(entry.HasActions, Is.False, "the buttons go away once used");
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
    public async Task A_task_added_with_the_form_lands_in_To_Do_with_the_next_id()
    {
        var root = Directory.CreateTempSubdirectory("buddy-entry-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            window.ShowEntryForm = form =>
            {
                form.TitleBox.Text = "Login con SSO";
                form.DoneWhenBox.Text = "si entra con l'account aziendale";
                form.TagButtons.First(b => (string)b.Content! == "feature").IsChecked = true;
                return Task.FromResult<Buddy.Core.Vault.NewEntry?>(form.Entry);
            };
            var todo = window.Board!.Columns.First(c => c.Key == "todo");
            await window.AddEntryAsync(todo);
            await window.AddEntryAsync(todo);

            var content = File.ReadAllText(Path.Combine(project, "To-Do.md"));
            Assert.That(content, Does.Contain("- [ ] T-001 · " + DateTime.Today.ToString("yyyy-MM-dd") + " · Login con SSO [priorita:: P2] [stima:: 1h] [tag:: feature]"));
            Assert.That(content, Does.Contain("- [ ] T-002 · "), "the second one takes the next id");
            Assert.That(content, Does.Contain("prossimo_id: T-003"));
            Assert.That(content, Does.Contain("  - Fatto quando: si entra con l'account aziendale"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task A_page_note_is_edited_with_the_tool_bar_and_renamed_but_flow_notes_are_protected()
    {
        var root = Directory.CreateTempSubdirectory("buddy-editor-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            var note = CustomPages.CreateNote(project, "Ricette", "Avvio", "uno");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            var notesList = window.FindControl<ListBox>("NotesList")!;
            var rename = window.FindControl<Button>("RenameNoteButton")!;
            window.ShowView("notes");
            notesList.SelectedItem = notesList.Items.OfType<NoteView>().First(n => n.Title == "To-Do");
            await Settle();
            Assert.That(rename.IsVisible, Is.False, "To-Do cannot be renamed");

            notesList.SelectedItem = notesList.Items.OfType<NoteView>().First(n => n.Title == "Avvio");
            await Settle();
            Assert.That(rename.IsVisible, Is.True);

            window.FindControl<Button>("EditNoteButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var editor = window.FindControl<TextBox>("NoteEditor")!;
            Assert.That(window.FindControl<Control>("NoteToolbar")!.IsEffectivelyVisible, Is.True);
            var at = editor.Text!.IndexOf("uno", StringComparison.Ordinal);
            editor.SelectionStart = at;
            editor.SelectionEnd = at + 3;
            editor.Focus();
            window.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.B, "b");
            Dispatcher.UIThread.RunJobs();
            Assert.That(editor.Text, Does.Contain("**uno**"));

            await Task.Delay(300);
            await Settle();
            Assert.That(window.FindControl<Buddy.App.Chat.MarkdownView>("NotePreview")!.Markdown, Does.Contain("**uno**"), "live preview");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task Jira_cards_not_on_the_board_are_imported_into_To_Do()
    {
        var root = Directory.CreateTempSubdirectory("buddy-jira-ui-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            Buddy.Core.Vault.VaultWriter.AppendItem(Path.Combine(project, "Da controllare.md"), "Voci", "- [ ] ABC-1 · Già fatto");
            var settings = new BuddySettings { ProjectPath = project, Jira = { BaseUrl = "https://a.atlassian.net" } };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            Buddy.Core.Tracker.TrackerIssue Card(string key, string title) => new(key, title, "", "High", "Story", "To Do", "Persona A", null, $"https://a.atlassian.net/browse/{key}");
            var tracker = new FakeTracker([Card("ABC-1", "Già fatto"), Card("ABC-2", "Nuova"), Card("ABC-3", "Scartata")]);
            IReadOnlyList<string>? offered = null;
            window.ShowTrackerImport = dialog =>
            {
                offered = dialog.Boxes.Select(b => ((TextBlock)((StackPanel)b.Content!).Children[0]).Text!).ToList();
                dialog.Boxes[1].IsChecked = false;
                return Task.FromResult<IReadOnlyList<Buddy.Core.Tracker.TrackerIssue>?>([Card("ABC-2", "Nuova")]);
            };

            await window.ImportAsync(tracker);

            Assert.That(offered, Is.EqualTo(new[] { "ABC-2 · Nuova", "ABC-3 · Scartata" }), "ABC-1 is already on the board");
            var todo = File.ReadAllText(Path.Combine(project, "To-Do.md"));
            Assert.That(todo, Does.Contain("- [ ] ABC-2 · " + DateTime.Today.ToString("yyyy-MM-dd") + " · Nuova [priorita:: P1] [tag:: ticket]"));
            Assert.That(todo, Does.Not.Contain("ABC-3"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task A_card_becomes_a_GitHub_issue_and_keeps_its_link()
    {
        var root = Directory.CreateTempSubdirectory("buddy-issue-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            Buddy.Core.Vault.VaultWriter.AppendItem(Path.Combine(project, "To-Do.md"), "Task",
                "- [ ] T-001 · 2026-11-06 · Export CSV vuoto [priorita:: P1] [tag:: bug]\n  - Fatto quando: il CSV ha i voti");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.GitHubRepoSource = () => Task.FromResult<Buddy.Core.Repo.GitHubRepo?>(new Buddy.Core.Repo.GitHubRepo("o", "r"));
            Buddy.Core.Tracker.IssueDraft? proposed = null, created = null;
            window.ShowIssueForm = form =>
            {
                proposed = form.Issue;
                form.TitleBox.Text = "Export CSV senza righe";
                return Task.FromResult(form.Issue);
            };
            window.CreateIssue = draft =>
            {
                created = draft;
                return Task.FromResult("https://github.com/o/r/issues/42");
            };
            window.Show();
            await Settle();
            window.ShowView("board");
            await Settle();

            var card = window.Cards.Single(c => c.Id == "T-001");
            window.Select(card);
            await Settle();
            var button = window.FindControl<Button>("CardIssueButton")!;
            Assert.That(button.IsVisible, Is.True);
            Assert.That(button.Content, Is.EqualTo("✚ Crea issue GitHub"));

            await window.CreateIssueAsync(card);
            await Settle();

            Assert.That(proposed!.Body, Does.Contain("- **Fatto quando**: il CSV ha i voti"));
            Assert.That(created!.Title, Is.EqualTo("Export CSV senza righe"));
            Assert.That(created.Labels, Is.EqualTo(new[] { "bug" }));
            Assert.That(File.ReadAllText(Path.Combine(project, "To-Do.md")), Does.Contain("[tag:: bug] [issue:: https://github.com/o/r/issues/42]"));
            Assert.That(button.Content, Is.EqualTo("↗ Issue #42"), "the card stays selected and now points to its issue");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task A_card_with_an_open_pull_request_shows_its_CI()
    {
        var root = Directory.CreateTempSubdirectory("buddy-ci-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            Buddy.Core.Vault.VaultWriter.AppendItem(Path.Combine(project, "Da controllare.md"), "Voci", "- [ ] AB-42 · Export voti");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.PullRequestSource = () => Task.FromResult<(IReadOnlyList<Buddy.Core.Repo.PullRequestInfo>, string?)>(
                ([new Buddy.Core.Repo.PullRequestInfo(5, "Export voti", "feature/AB-42", "https://x/pr/5", false, "APPROVED", "failure", null, "AB-42")], null));
            window.Show();
            await Settle();
            window.ShowView("board");
            await Settle();

            var card = window.Cards.Single(c => c.Id == "AB-42");
            Assert.That(card.Chips.First().Text, Is.EqualTo("❌ CI #5 · 👍"));
            Assert.That(card.Chips.First().IsHot, Is.True);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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

/// <summary>A tracker that returns fixed cards.</summary>
internal sealed class FakeTracker(IReadOnlyList<Buddy.Core.Tracker.TrackerIssue> issues) : Buddy.Core.Tracker.ITracker
{
    public string Name => "Finto";

    public Task<IReadOnlyList<Buddy.Core.Tracker.TrackerIssue>> AssignedAsync(CancellationToken cancellationToken = default) => Task.FromResult(issues);
}
