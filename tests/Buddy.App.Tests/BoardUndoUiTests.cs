using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Buddy.App.Project;
using Buddy.Core;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Buddy.App.Tests;

/// <summary>The board: empty columns as narrow strips, and a card moved by hand can be put back.</summary>
public class BoardUndoUiTests
{
    [AvaloniaTest]
    public async Task A_moved_card_comes_back_with_Annulla_and_empty_columns_are_narrow()
    {
        var root = Directory.CreateTempSubdirectory("buddy-board-undo-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            FlowTemplate.CreateProject(project, "Demo");
            var todoPath = Path.Combine(project, "To-Do.md");
            File.AppendAllText(todoPath, "- [ ] T-090 · Primo task\n");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var window = new ProjectWindow(vault, Options.Create(settings)) { Width = 1400, Height = 700 };
            window.Show();
            window.ShowView("board");
            await SettleAsync();

            var columns = (IReadOnlyCollection<ColumnView>)window.BoardColumns.ItemsSource!;
            var borders = window.BoardColumns.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("column")).ToList();
            var todoBorder = borders.Single(b => b.DataContext is ColumnView { Column.Key: "todo" });
            var emptyBorder = borders.First(b => b.DataContext is ColumnView { IsEmpty: true });
            Assert.That(emptyBorder.Bounds.Width, Is.LessThan(70), "an empty column is a strip");
            Assert.That(todoBorder.Bounds.Width, Is.GreaterThan(300), "the full one takes the room left");

            var before = File.ReadAllText(todoPath);
            var card = columns.Single(c => c.Column.Key == "todo").Cards.Single();
            await window.MoveCardAsync(card, "controllare");
            await SettleAsync();
            Assert.That(File.ReadAllText(todoPath), Does.Not.Contain("T-090"));
            Assert.That(window.UndoToast.IsVisible, Is.True);
            Assert.That(window.UndoText.Text, Does.Contain("T-090"));

            Assert.That(await window.UndoMoveAsync(), Is.True);
            await SettleAsync();
            Assert.That(File.ReadAllText(todoPath), Is.EqualTo(before));
            Assert.That(window.UndoToast.IsVisible, Is.False);
            Assert.That(await window.UndoMoveAsync(), Is.False, "only once");
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

public class CardChipTests
{
    [AvaloniaTest]
    public void A_card_shows_how_far_its_manual_tests_are()
    {
        var root = Directory.CreateTempSubdirectory("buddy-card-chip-").FullName;
        try
        {
            var project = Path.Combine(root, "Progetti", "Demo");
            FlowTemplate.CreateProject(project, "Demo");
            File.AppendAllText(Path.Combine(project, "To-Do.md"), "- [ ] T-090 · Con test\n");
            var settings = new BuddySettings { ProjectPath = project };
            using var vault = new VaultIndex(Options.Create(settings));
            var todo = ProjectBoard.Load(vault, settings.TaskIdPattern).Columns.Single(c => c.Key == "todo");
            var item = todo.Items.Single(i => i.Id == "T-090");
            var today = DateOnly.FromDateTime(DateTime.Today);

            string? Chip((int, int)? manual) =>
                new CardView(item, todo, today, manualTests: manual).Chips.FirstOrDefault(c => c.Text.StartsWith("🧪"))?.Text;
            Assert.That(Chip((3, 11)), Is.EqualTo("🧪 3/11"));
            Assert.That(new CardView(item, todo, today, manualTests: (11, 11)).Chips[0].IsGood, Is.True, "all ticked: green");
            Assert.That(Chip((0, 0)), Is.Null, "an empty document says nothing");
            Assert.That(Chip(null), Is.Null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
