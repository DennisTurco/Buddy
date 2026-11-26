using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
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
    public async Task Bold_with_inline_code_inside_is_drawn_bold()
    {
        var view = new MarkdownView { Markdown = "- **Id 54 – `UserApiKey`**: inserire la key.\n- **Id 59 – `Data`** e **solo grassetto**" };
        var window = new Window { Content = view, Width = 800, Height = 200 };
        window.Show();
        await SettleAsync();

        var text = string.Join("\n", view.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>().Select(b => b.Text));
        Assert.That(text, Does.Not.Contain("**"));
        Assert.That(text, Does.Contain("UserApiKey"));
    }

    [AvaloniaTest]
    public async Task A_selection_across_inline_code_keeps_its_start_before_its_end()
    {
        var view = new MarkdownView { Markdown = "Nella tabella `[Configurazione].[ConfigurazioniPortale]` del database del cliente, `uno` e `due` fine." };
        var window = new Window { Content = view, Width = 900, Height = 200 };
        window.Show();
        await SettleAsync();

        var block = view.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>().Last();
        var origin = block.TranslatePoint(new Point(0, 0), window)!.Value;
        const System.Reflection.BindingFlags priv = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (var (from, to) in new[] { (30.0, 700.0), (700.0, 30.0), (5.0, 500.0) })
        {
            window.MouseDown(new Point(origin.X + from, origin.Y + 6), MouseButton.Left);
            window.MouseMove(new Point(origin.X + to, origin.Y + 6));
            window.MouseUp(new Point(origin.X + to, origin.Y + 6), MouseButton.Left);
            await SettleAsync();

            var begin = (ColorTextBlock.Avalonia.PhysicalTextPointer)typeof(ColorTextBlock.Avalonia.CTextBlock).GetField("_beginSelect", priv)!.GetValue(block)!;
            var end = (ColorTextBlock.Avalonia.PhysicalTextPointer)typeof(ColorTextBlock.Avalonia.CTextBlock).GetField("_endSelect", priv)!.GetValue(block)!;
            Assert.That(begin > end, Is.EqualTo(begin.Index > end.Index), $"drag {from}→{to}: the drawing must order the ends by position");
        }
    }

    [AvaloniaTest]
    public async Task Copying_wrapped_lines_and_a_list_keeps_each_line_once_with_its_number()
    {
        var view = new MarkdownView { Markdown = "Ora il parametro 33 è già andato avanti e le fatture utenze della luce sono state saltate, il log diceva `TipiServizio [G]` e così via.\n\n1. rimettere a 0 solo il 33 (il 32 e il 34 non sono filtrati per tipo servizio, quindi li lascerei come sono);\n2. rilanciare il task 24 e controllare che le fatture in `FattureInAttesaUtenze` scendano;\n\nPrima di procedere volevo il tuo ok: ti torna, o c'è qualcosa che mi sfugge?" };
        var window = new Window { Content = view, Width = 300, Height = 800 };
        window.Show();
        await SettleAsync();

        var blocks = view.GetLogicalDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>().ToList();
        var start = blocks.First().TranslatePoint(new Point(0, 2), window)!.Value;
        var last = blocks.Last();
        var end = last.TranslatePoint(new Point(last.Bounds.Width - 1, last.Bounds.Height - 2), window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        await SettleAsync();
        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        await SettleAsync();

        var copied = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
        Assert.That(copied, Is.EqualTo(
            "Ora il parametro 33 è già andato avanti e le fatture utenze della luce sono state saltate, il log diceva TipiServizio [G] e così via.\n"
            + "1. rimettere a 0 solo il 33 (il 32 e il 34 non sono filtrati per tipo servizio, quindi li lascerei come sono);\n"
            + "2. rilanciare il task 24 e controllare che le fatture in FattureInAttesaUtenze scendano;\n"
            + "Prima di procedere volevo il tuo ok: ti torna, o c'è qualcosa che mi sfugge?"));
    }

    [AvaloniaTest]
    public async Task The_settings_tables_switch_rules_and_commands_on_and_off()
    {
        var current = new BuddySettings
        {
            Claude = { AutoAllowTools = ["Read", "Bash(git status:*)"], HeavyCommands = [] },
            Assistant = { DisabledCommands = ["/pillola"] },
        };
        var commands = new[] { new ChatCommand("/oggi", "la giornata"), new ChatCommand("/pillola", "una pillola", Group: CommandGroup.Mentor) };
        var window = new Buddy.App.Settings.SettingsWindow(current, new Buddy.Core.Llm.OllamaClient(new HttpClient(), Options.Create(current)), commands);
        window.Show();
        await SettleAsync();

        var ruleRows = window.FindControl<StackPanel>("RuleRows")!.Children.OfType<Grid>().ToList();
        Assert.That(ruleRows, Has.Count.EqualTo(2), "the old list becomes rows");
        ruleRows[0].Children.OfType<CheckBox>().First().IsChecked = false; // Read off

        var suggested = window.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == "Aggiungi le suggerite");
        suggested.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var commit = window.FindControl<StackPanel>("RuleRows")!.Children.OfType<Grid>()
            .First(g => g.Children.OfType<TextBox>().First().Text == "Bash(git commit:*)");
        commit.Children.OfType<CheckBox>().First().IsChecked = true;

        var commandRows = window.FindControl<StackPanel>("CommandRows")!.Children.OfType<Grid>().Take(2).ToList(); // then the skills installed here
        Assert.That(commandRows.Select(r => r.Children.OfType<CheckBox>().First().IsChecked), Is.EqualTo(new bool?[] { true, false }));
        commandRows[0].Children.OfType<CheckBox>().First().IsChecked = false; // /oggi off
        commandRows[1].Children.OfType<CheckBox>().First().IsChecked = true;  // /pillola on

        window.CollectTables();
        var claude = window.Edited.Claude;
        Assert.That(claude.AutoAllowTools, Is.Empty, "moved into the table");
        Assert.That(claude.AllowedTools(), Is.EqualTo(new[] { "Bash(git status:*)" }));
        Assert.That(claude.DeniedTools(), Is.EqualTo(new[] { "Bash(git commit:*)" }));
        Assert.That(claude.ToolRules.Single(r => r.Tool == "Read").Enabled, Is.False, "kept, switched off");
        Assert.That(window.Edited.Assistant.DisabledCommands, Is.EqualTo(new[] { "/oggi" }));
        Assert.That(current.Claude.ToolRules, Is.Empty, "the live settings change only on save");
        window.Close();
    }

    [Test]
    public void Bold_around_inline_code_is_split_so_the_parser_sees_it()
    {
        Assert.That(MarkdownView.SplitBoldAroundCode("- **Id 54 – `UserApiKey`**: x"), Is.EqualTo("- **Id 54 –** `UserApiKey`: x"));
        Assert.That(MarkdownView.SplitBoldAroundCode("**a `b` c**"), Is.EqualTo("**a** `b` **c**"));
        Assert.That(MarkdownView.SplitBoldAroundCode("**solo** e `code`"), Is.EqualTo("**solo** e `code`"));
        Assert.That(MarkdownView.SplitBoldAroundCode("```\n**a `b`**\n```"), Is.EqualTo("```\n**a `b`**\n```"));
    }

    [Test]
    public void Quotes_are_read_back_as_markdown_without_their_markers()
    {
        var markdown = "Ecco la bozza:\n\n> Ciao! Il log diceva `X [G]`.\n>\n> 1. primo;\n> 2. **secondo**\nlazy\n\nFuori.\n\n```\n> non è una citazione\n```\n\n> seconda\n> > annidata";
        Assert.That(QuoteBlocks.Quotes(markdown), Is.EqualTo(new[]
        {
            "Ciao! Il log diceva `X [G]`.\n\n1. primo;\n2. **secondo**\nlazy",
            "seconda\n> annidata",
        }));
        Assert.That(QuoteBlocks.HasQuote("niente qui"), Is.False);
    }

    [AvaloniaTest]
    public void Bruce_is_the_built_in_character_and_the_slime_is_a_choice()
    {
        // The headless platform does not decode images (every PNG is 1×1 here): what is checked is where frames come from.
        foreach (var state in Enum.GetValues<PetState>())
        {
            foreach (var mode in Enum.GetValues<ChatMode>())
            {
                var name = Buddy.App.Pet.PetSprites.FileName(state, mode);
                Assert.That(Avalonia.Platform.AssetLoader.Exists(new Uri($"avares://Buddy.App/Pet/Bruce/{name}")), Is.True, name);
            }
        }

        var bruce = new Buddy.App.Pet.PetSprites(new PetSettings());
        Assert.That(bruce.Frames(PetState.Idle, ChatMode.Local)[0], Is.InstanceOf<Avalonia.Media.Imaging.CroppedBitmap>(), "Bruce: cut from his strips");
        Assert.That(bruce.WalkFrames(ChatMode.Claude)[0], Is.InstanceOf<Avalonia.Media.Imaging.CroppedBitmap>());

        Assert.That(bruce.RestFrames(ChatMode.Local), Is.Not.Null.And.Not.Empty, "Bruce curls up now and then");

        var slime = new Buddy.App.Pet.PetSprites(new PetSettings { Character = "slime" });
        Assert.That(slime.Frames(PetState.Idle, ChatMode.Local)[0], Is.InstanceOf<Avalonia.Media.Imaging.WriteableBitmap>(), "the slime: drawn from its grids");
        Assert.That(slime.RestFrames(ChatMode.Local), Is.Null, "the slime does not lie down");
    }

    [Test]
    public void Days_are_named_from_today_and_a_new_day_splits_the_group()
    {
        var today = new DateTime(2026, 11, 26);
        Assert.That(DayLabel.Of(today.AddHours(9), today), Is.EqualTo("Oggi"));
        Assert.That(DayLabel.Of(today.AddDays(-1), today), Is.EqualTo("Ieri"));
        Assert.That(DayLabel.Of(new DateTime(2026, 11, 24), today), Is.EqualTo("martedì 24 novembre"));
        Assert.That(DayLabel.Of(new DateTime(2025, 3, 2), today), Is.EqualTo("2 marzo 2025"));

        var yesterday = new ChatEntry(Speaker.User, "ieri") { Timestamp = DateTimeOffset.Now.AddDays(-1) };
        var now = new ChatEntry(Speaker.User, "oggi");
        var entries = new System.Collections.ObjectModel.ObservableCollection<ChatEntry> { yesterday, now };
        ChatGrouping.Track(entries);
        Assert.That(yesterday.StartsDay, Is.True, "a conversation from yesterday opens with its day");
        Assert.That(now.StartsDay, Is.True);
        Assert.That(now.JoinsPrevious, Is.False, "the day line splits the group");

        var later = new ChatEntry(Speaker.User, "ancora oggi");
        entries.Add(later);
        Assert.That(later.StartsDay, Is.False);
        Assert.That(later.JoinsPrevious, Is.True);
    }

    [AvaloniaTest]
    public async Task Quote_in_a_message_gets_a_copy_button()
    {
        var view = new MarkdownView { Markdown = "Bozza:\n\n> Ciao **Wentu**!\n> - uno" };
        var window = new Window { Content = view };
        window.Show();
        await SettleAsync();

        var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Button>().ToList();
        Assert.That(buttons.Select(b => ToolTip.GetTip(b) as string), Is.EqualTo(new[] { "Copia il testo in markdown" }));
        window.Close();
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
        bubble.Chat.Input.Focus();

        Type(bubble, "prima domanda");
        Press(bubble, Key.Enter);
        await SettleAsync();
        Assert.That(bubble.Chat.Input.Text, Is.Empty.Or.Null, "sent");

        Type(bubble, "bozza");
        bubble.Chat.Input.CaretIndex = 0;
        Press(bubble, Key.Up);
        Assert.That(bubble.Chat.Input.Text, Is.EqualTo("prima domanda"));

        Press(bubble, Key.Down);
        Assert.That(bubble.Chat.Input.Text, Is.EqualTo("bozza"), "↓ goes back to what was being typed");
        Assert.That(File.ReadAllText(ChatView.HistoryFile!), Does.Contain("prima domanda"), "kept for the next start");
    }

    [AvaloniaTest]
    public void Typing_a_slash_suggests_commands_and_tab_completes()
    {
        var settings = Settings();
        var bubble = new BubbleWindow(Conversation(settings), Options.Create(settings));
        bubble.Show();
        bubble.Chat.Input.Focus();

        // A command of the chat itself: the mentor's are registered only when the mentor runs.
        Type(bubble, "/riprend");
        Assert.That(bubble.Chat.SuggestionsPanel.IsVisible, Is.True);

        Press(bubble, Key.Tab);
        Assert.That(bubble.Chat.Input.Text, Is.EqualTo("/riprendi "));
        Assert.That(bubble.Chat.SuggestionsPanel.IsVisible, Is.False);
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
        Assert.That((bubble.Chat.EntriesZoom.LayoutTransform as Avalonia.Media.ScaleTransform)?.ScaleX, Is.EqualTo(1.2).Within(0.001));

        Press(bubble, Key.OemMinus, RawInputModifiers.Control);
        Assert.That(settings.Appearance.ChatZoom, Is.EqualTo(1.1).Within(0.001));

        Press(bubble, Key.D0, RawInputModifiers.Control);
        Assert.That(settings.Appearance.ChatZoom, Is.EqualTo(1));
        Assert.That(bubble.Chat.EntriesZoom.LayoutTransform, Is.Null);
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
        Assert.That(bubble.Chat.SearchBar.IsVisible, Is.True);
        Type(bubble, "taskexecutor");

        Assert.That(conversation.Entries.Count(e => e.IsHighlighted), Is.EqualTo(2));
        Assert.That(bubble.Chat.SearchCount.Text, Is.EqualTo("2/2"));

        Press(bubble, Key.Escape);
        Assert.That(bubble.Chat.SearchBar.IsVisible, Is.False);
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
    public async Task Modifica_opens_the_add_form_filled_in_and_saves_only_what_changed()
    {
        var root = Directory.CreateTempSubdirectory("buddy-edit-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            var todoPath = Path.Combine(project, "To-Do.md");
            File.AppendAllText(todoPath, "\n- [ ] T-001 · 2026-11-01 · Login [priorita:: P2] [stima:: 2h] [tag:: feature]\n  - Accordi: 2026-11-02 con Persona A\n  - Fatto quando: si entra\n");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            var todo = window.Board!.Columns.First(c => c.Key == "todo");
            window.Select(new CardView(todo.Items.Single(i => i.Id == "T-001"), todo, DateOnly.FromDateTime(DateTime.Today)));
            string? shownTitle = null;
            window.ShowEntryForm = form =>
            {
                shownTitle = form.TitleBox.Text;
                Assert.That(form.DoneWhenBox.Text, Is.EqualTo("si entra"));
                Assert.That(form.TagButtons.First(b => (string)b.Content! == "feature").IsChecked, Is.True);
                form.TitleBox.Text = "Login con SSO";
                return Task.FromResult<Buddy.Core.Vault.NewEntry?>(form.Entry);
            };
            await window.EditSelectedAsync();

            Assert.That(shownTitle, Is.EqualTo("Login"));
            var content = File.ReadAllText(todoPath);
            Assert.That(content, Does.Contain("- [ ] T-001 · 2026-11-01 · Login con SSO [priorita:: P2] [stima:: 2h] [tag:: feature]\n  - Accordi: 2026-11-02 con Persona A\n  - Fatto quando: si entra\n"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task A_person_is_edited_with_the_form_filled_in_from_their_section()
    {
        var root = Directory.CreateTempSubdirectory("buddy-person-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            var people = Path.Combine(project, "Persone.md");
            File.AppendAllText(people, "\n## Chiara Bianchi\n\n- **Compito:** DBA\n- **Sentita:** 2026-11-10\n\n## Marco Verdi\n\n- **Compito:** PO\n");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            Buddy.Core.Vault.PersonFields? shown = null;
            window.ShowPersonForm = person =>
            {
                shown = person;
                return Task.FromResult<Buddy.Core.Vault.PersonFields?>(person! with { Nicknames = "Chicca", Relationship = "delicato", Communication = "per iscritto" });
            };
            await window.EditPersonAsync("Chiara Bianchi");

            Assert.That(shown?.Role, Is.EqualTo("DBA"), "the form starts from what the note says");
            var text = File.ReadAllText(people);
            Assert.That(text, Does.Contain("## Chiara Bianchi\n\n- **Soprannome:** Chicca\n- **Compito:** DBA\n- **Rapporto:** delicato\n- **Come comunicare:** per iscritto\n- **Sentita:** 2026-11-10\n"));
            Assert.That(text, Does.Contain("## Marco Verdi\n\n- **Compito:** PO\n"), "the others are untouched");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task The_mouse_side_buttons_go_back_and_forward_between_pages()
    {
        var root = Directory.CreateTempSubdirectory("buddy-history-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            window.ShowView("board");
            window.ShowView("people");
            window.ShowView("glossary");
            Assert.That(window.BackCount, Is.EqualTo(3), "overview, board, people");

            window.MouseDown(new Avalonia.Point(600, 400), Avalonia.Input.MouseButton.XButton1);
            window.MouseUp(new Avalonia.Point(600, 400), Avalonia.Input.MouseButton.XButton1);
            Assert.That(window.ViewTitleText, Is.EqualTo("Persone"), "X1: back");

            Assert.That(window.GoBack(), Is.True);
            Assert.That(window.ViewTitleText, Is.EqualTo("Bacheca"));

            window.MouseDown(new Avalonia.Point(600, 400), Avalonia.Input.MouseButton.XButton2);
            window.MouseUp(new Avalonia.Point(600, 400), Avalonia.Input.MouseButton.XButton2);
            Assert.That(window.ViewTitleText, Is.EqualTo("Persone"), "X2: forward");

            window.ShowView("costs");
            Assert.That(window.ForwardCount, Is.Zero, "a new page drops the steps forward, as in a browser");
            Assert.That(window.GoForward(), Is.False);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task A_new_call_transcript_is_saved_then_summarised_by_Claude_on_the_Trascrizioni_page()
    {
        var root = Directory.CreateTempSubdirectory("buddy-call-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            string? prompt = null;
            window.TranscriptSource = (p, _) =>
            {
                prompt = p;
                return Task.FromResult((true, "TITOLO: Rilascio 3.2\nPARTECIPANTI: Marco\nTASK: -\n## Riassunto\nRimandato a lunedì.\n"
                                              + "## Azioni\n- [ ] **Marco:** changelog\n## Trascrizione sistemata\n**Marco:** Rimandiamo."));
            };
            window.ShowCallForm = () => Task.FromResult<string[]?>(["", "2026-11-24", "", "", "", "marco: ehm rimandiamo"]);
            window.ShowView("calls");
            await window.NewCallAsync();
            for (var i = 0; i < 40 && !File.Exists(Path.Combine(project, "Trascrizioni", "2026-11-24 Rilascio 3.2.md")); i++)
            {
                await Settle();
            }

            Assert.That(prompt, Does.Contain("marco: ehm rimandiamo"));
            Assert.That(File.Exists(Path.Combine(project, "Trascrizioni", "2026-11-24 Call.md")), Is.False, "renamed after Claude's title");
            await Settle();
            var cards = (window.PageCards.ItemsSource as IEnumerable<PageCard>)!.ToList();
            Assert.That(cards.Select(c => c.Title), Is.EqualTo(new[] { "Rilascio 3.2" }));
            Assert.That(cards[0].Chips.Select(c => c.Text), Does.Contain("☑ 0/1 azioni").And.Contain("👤 Marco"));
            Assert.That(cards[0].Markdown, Does.Contain("Rimandato a lunedì."));

            // The action still to do is a button: Claude gets the request to work on it.
            Assert.That(cards[0].Actions.Select(a => a.Label), Is.EqualTo(new[] { "🛠 Marco: changelog" }));
            string? toClaude = null;
            window.ClaudeRequested += request => toClaude = request;
            var button = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is PageCardAction);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.That(toClaude, Does.Contain("un'azione da fare: «Marco: changelog»").And.Contain("2026-11-24 Rilascio 3.2.md"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task The_registry_and_the_commitments_pages_write_and_tick_what_was_said()
    {
        var root = Directory.CreateTempSubdirectory("buddy-said-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            var settings = new BuddySettings { ProjectPath = project, Mentor = { UserName = "Dennis" } };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            // "＋ Impegno…" on the Registro page: one line in Registro.md.
            window.ShowSaidForm = _ => Task.FromResult<string[]?>(["Tony", "mandare i sinonimi", "CleanEnergy", "2026-11-27"]);
            window.ShowView("registry");
            await window.NewSaidAsync(Buddy.Core.Vault.SaidKind.Impegno);
            await Settle();
            var registry = File.ReadAllText(Path.Combine(project, "Registro.md"));
            Assert.That(registry, Does.Contain("- [ ] ").And.Contain(" · Tony · impegno · mandare i sinonimi [entro:: 2026-11-27] [tema:: CleanEnergy]"));
            var cards = (window.PageCards.ItemsSource as IEnumerable<PageCard>)!.ToList();
            Assert.That(cards.Single().Markdown, Does.Contain("**Tony**").And.Contain("mandare i sinonimi"));

            // ✅ Impegni: a card for Tony with a button that ticks it in the registry.
            window.ShowView("commitments");
            await Settle();
            cards = (window.PageCards.ItemsSource as IEnumerable<PageCard>)!.ToList();
            Assert.That(cards.Select(c => c.Title), Is.EqualTo(new[] { "Tony" }));
            var tick = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is PageCardAction { Local: not null });
            tick.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert.That(File.ReadAllText(Path.Combine(project, "Registro.md")), Does.Contain("- [x] "));
            cards = (window.PageCards.ItemsSource as IEnumerable<PageCard>)!.ToList();
            Assert.That(cards.Select(c => c.Title), Is.EqualTo(new[] { "Fatti di recente" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task A_new_episode_is_saved_in_the_journal_and_sent_to_the_chat_for_the_analysis()
    {
        var root = Directory.CreateTempSubdirectory("buddy-episode-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            Buddy.Core.Vault.FlowTemplate.CreateProject(project, "Demo");
            var settings = new BuddySettings { ProjectPath = project, Mentor = { JournalPath = Path.Combine(root, "Diario") } };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1200, Height = 700 };
            window.Show();
            await Settle();

            string? sent = null;
            window.ChatCommandRequested += command => sent = command;
            window.ShowEpisodeForm = _ => Task.FromResult<Buddy.Core.Vault.Episode?>(new Buddy.Core.Vault.Episode(
                new DateOnly(2026, 11, 16), "Rilascio rimandato", ["Chiara Bianchi"], "Ha chiesto di rimandare", "", "", "", []));
            await window.NewEpisodeAsync();

            var file = Path.Combine(root, "Diario", "Episodi.md");
            Assert.That(File.ReadAllText(file), Does.Contain("## 2026-11-16 · Rilascio rimandato"));
            Assert.That(sent, Is.EqualTo("/episodio-analizza 2026-11-16 · Rilascio rimandato"));

            // A click on it: the same form, filled in; the analysis' lines are kept, and changed facts are analysed again.
            Buddy.Core.Vault.Episodes.Update(file, "2026-11-16 · Rilascio rimandato", e => e with { AboutMe = "Hai ascoltato.", Changes = ["Chiara · note + x"] });
            Buddy.Core.Vault.Episode? shown = null;
            window.ShowEpisodeForm = e =>
            {
                shown = e;
                return Task.FromResult<Buddy.Core.Vault.Episode?>(e! with { Outcome = "Rimandato di una settimana" });
            };
            sent = null;
            await window.EditEpisodeAsync("2026-11-16 · Rilascio rimandato");

            Assert.That(shown?.Facts, Is.EqualTo("Ha chiesto di rimandare"), "the form starts from the episode");
            var edited = Buddy.Core.Vault.Episodes.Read(file).Single();
            Assert.That(edited.Outcome, Is.EqualTo("Rimandato di una settimana"));
            Assert.That(edited.AboutMe, Is.EqualTo("Hai ascoltato."));
            Assert.That(edited.Changes, Is.EqualTo(new[] { "Chiara · note + x" }));
            Assert.That(sent, Is.EqualTo("/episodio-analizza 2026-11-16 · Rilascio rimandato"));
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

            // A long note: scrolling the editor scrolls the preview to the same point.
            editor.Text = string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"## Riga {i}\ntesto {i}"));
            await Task.Delay(300);
            await Settle();
            var editorScroller = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
            var previewScroller = window.FindControl<ScrollViewer>("NotePreviewScroller")!;
            editorScroller.Offset = editorScroller.Offset.WithY((editorScroller.Extent.Height - editorScroller.Viewport.Height) / 2);
            await Settle();
            var previewRange = previewScroller.Extent.Height - previewScroller.Viewport.Height;
            Assert.That(previewRange, Is.GreaterThan(0));
            Assert.That(previewScroller.Offset.Y / previewRange, Is.EqualTo(0.5).Within(0.02), "preview follows the editor");
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
            Assert.That(button.Content?.ToString(), Is.EqualTo("✚ Crea issue GitHub"));

            await window.CreateIssueAsync(card);
            await Settle();

            Assert.That(proposed!.Body, Does.Contain("- **Fatto quando**: il CSV ha i voti"));
            Assert.That(created!.Title, Is.EqualTo("Export CSV senza righe"));
            Assert.That(created.Labels, Is.EqualTo(new[] { "bug" }));
            Assert.That(File.ReadAllText(Path.Combine(project, "To-Do.md")), Does.Contain("[tag:: bug] [issue:: https://github.com/o/r/issues/42]"));
            Assert.That(button.Content?.ToString(), Is.EqualTo("↗ Issue #42"), "the card stays selected and now points to its issue");
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
