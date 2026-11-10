using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Buddy.App.Chat;
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

/// <summary>Claude's tool calls in the chat: one line, opened with a click.</summary>
public class ToolActivityUiTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("buddy-tools-ui-").FullName;
        ChatView.HistoryFile = Path.Combine(_root, "history.jsonl");
        ChatView.SaveSettings = _ => { };
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
        }
    }

    [AvaloniaTest]
    public async Task Many_tool_calls_are_one_line_that_opens_to_the_list()
    {
        var settings = new BuddySettings
        {
            ProjectPath = Path.Combine(_root, "vault"),
            Ollama = { BaseUrl = "http://127.0.0.1:1" },
            Voice = { Enabled = false },
        };
        var options = Options.Create(settings);
        var vault = new VaultIndex(options);
        var repo = new GitRepo(options, TimeProvider.System);
        var conversation = new Conversation(new LocalAssistant(new OllamaClient(new HttpClient(), options),
                new ContextBuilder(vault, options, TimeProvider.System), options),
            new ClaudeAuth(options), new AssistantCommands(vault, repo, options), repo, options);
        var bubble = new BubbleWindow(conversation, options);
        bubble.Show();

        var activity = new ToolActivityEntry("Legge a.cs");
        activity.Add("Legge b.cs");
        activity.Add("Esegue dotnet build");
        conversation.Entries.Add(activity);
        await SettleAsync();

        var toggle = bubble.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Classes.Contains("tools"));
        var steps = ((Visual)toggle.GetVisualParent()!).GetVisualDescendants().OfType<ItemsControl>().Single();
        Assert.That(steps.IsVisible, Is.False, "closed at first");
        Assert.That(toggle.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text),
            Does.Contain("🔧 3 azioni").And.Contain("· Esegue dotnet build"));

        var centre = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), bubble)!.Value;
        bubble.MouseDown(centre, MouseButton.Left);
        bubble.MouseUp(centre, MouseButton.Left);
        await SettleAsync();

        Assert.That(activity.IsExpanded, Is.True);
        Assert.That(steps.IsVisible, Is.True);
        Assert.That(steps.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text),
            Is.EqualTo(new[] { "Legge a.cs", "Legge b.cs", "Esegue dotnet build" }));

        activity.Add("Modifica a.cs");
        await SettleAsync();
        Assert.That(steps.GetVisualDescendants().OfType<SelectableTextBlock>().Count(), Is.EqualTo(4), "a new step joins the open list");
        bubble.Close();
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
}

public class MarkdownListTests
{
    [AvaloniaTest]
    public async Task Bullet_lists_get_a_small_indent_and_dots()
    {
        var view = new MarkdownView { Markdown = "Testo\n\n- uno\n- due\n  - annidato\n\n1. primo" };
        var window = new Window { Content = view, Width = 400, Height = 400 };
        window.Show();
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        var markers = view.GetVisualDescendants().OfType<ColorTextBlock.Avalonia.CTextBlock>()
            .Where(b => b.Classes.Contains("ListMarker"))
            .Select(b => string.Concat(b.Content.OfType<ColorTextBlock.Avalonia.CRun>().Select(r => r.Text))).ToList();
        Assert.That(markers, Is.EqualTo(new[] { "•", "•", "◦", "1." }));
        Assert.That(view.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("List")).Select(g => g.Margin.Left),
            Is.All.LessThan(10), "not the library's 40 px");
        window.Close();
    }
}
