using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Buddy.App.Project;
using Buddy.Core;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Buddy.App.Tests;

/// <summary>The card's sheet in the side panel: "Cosa manca" for an open task, "Come è stato fatto" for a closed one.</summary>
public class TaskSheetUiTests
{
    [AvaloniaTest]
    public async Task An_open_card_shows_what_is_missing_and_it_can_be_edited()
    {
        var root = Directory.CreateTempSubdirectory("buddy-sheet-ui-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            FlowTemplate.CreateProject(project, "Demo");
            File.AppendAllText(Path.Combine(project, "To-Do.md"), "- [ ] T-091 · Import fatture\n");
            var sheetPath = Path.Combine(project, TaskSheet.RelativePath("T-091"));
            Directory.CreateDirectory(Path.GetDirectoryName(sheetPath)!);
            File.WriteAllText(sheetPath, "# T-091 · Import fatture\n\n## Cosa manca\n\n- WS reali\n");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1400, Height = 700 };
            var requests = new List<string>();
            window.ClaudeRequested += requests.Add;
            window.Show();
            window.ShowView("board");
            await SettleAsync();

            var columns = (IReadOnlyCollection<ColumnView>)window.BoardColumns.ItemsSource!;
            window.Select(columns.Single(c => c.Column.Key == "todo").Cards.Single());
            await SettleAsync();
            Assert.That(window.SheetPanel.IsVisible, Is.True);
            Assert.That(window.SheetTitle.Text, Does.Contain("Cosa manca"));
            Assert.That(window.SheetView.Markdown, Is.EqualTo("- WS reali"));

            window.BeginEditSheet();
            Assert.That(window.SheetEditor.Text, Is.EqualTo("- WS reali"));
            window.SheetEditor.Text = "- WS reali (CPL non risponde)\n- API key di test";
            await window.SaveSheetAsync();
            await SettleAsync();
            Assert.That(TaskSheet.Read(sheetPath, TaskSheet.Missing), Is.EqualTo("- WS reali (CPL non risponde)\n- API key di test"));
            Assert.That(window.SheetEdit.IsVisible, Is.False);
            Assert.That(window.SheetView.Markdown, Does.Contain("API key di test"));

            window.SheetClaudeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Assert.That(requests.Single(), Does.Contain("Task/T-091.md").And.Contain("Cosa manca"));
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaTest]
    public async Task A_closed_card_shows_how_it_was_done()
    {
        var root = Directory.CreateTempSubdirectory("buddy-sheet-ui-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            FlowTemplate.CreateProject(project, "Demo");
            var fatti = Directory.EnumerateFiles(project, "To-Do Fatti.md", SearchOption.AllDirectories).Single();
            File.AppendAllText(fatti, "- [x] T-092 · 2026-11-10 · Import fatture\n");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1400, Height = 700 };
            window.Show();
            window.ShowView("archive");
            await SettleAsync();

            var card = ((IEnumerable<CardView>)window.DoneList.ItemsSource!).Single(c => c.Id == "T-092");
            window.Select(card);
            await SettleAsync();
            Assert.That(window.SheetPanel.IsVisible, Is.True);
            Assert.That(window.SheetTitle.Text, Does.Contain("Come è stato fatto"));
            Assert.That(window.SheetEmpty.IsVisible, Is.True);
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SettleAsync(int milliseconds = 400)
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
