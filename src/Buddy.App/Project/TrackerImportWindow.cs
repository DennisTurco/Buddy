using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core.Tracker;

namespace Buddy.App.Project;

/// <summary>
/// "⬇ Importa": the cards of a tracker (Jira, GitHub issues, Azure Boards) assigned to you that are not on the board
/// yet, each with a tick (all ticked); the ticked ones are imported into To-Do.
/// </summary>
public sealed class TrackerImportWindow : Window
{
    private readonly List<(CheckBox Box, TrackerIssue Issue)> _rows = [];
    private readonly Button _import = new() { IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
    private IReadOnlyList<TrackerIssue>? _result;

    /// <param name="alreadyThere">How many of your open cards are already on the board.</param>
    public TrackerImportWindow(string tracker, IReadOnlyList<TrackerIssue> issues, int alreadyThere)
    {
        Title = $"Importa da {tracker}";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("form");

        var list = new StackPanel { Spacing = 6 };
        foreach (var issue in issues)
        {
            var details = new StackPanel();
            details.Children.Add(new TextBlock { Text = $"{issue.Key} · {issue.Summary}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            var meta = string.Join(" · ", new[] { issue.Type, issue.Priority, issue.Status, issue.Due is { } due ? $"scade il {due:dd/MM}" : null }.OfType<string>());
            if (meta.Length > 0)
            {
                details.Children.Add(new TextBlock { Text = meta, Classes = { "hint" }, Margin = new Thickness(0) });
            }

            var box = new CheckBox { IsChecked = true, Content = details, HorizontalAlignment = HorizontalAlignment.Stretch };
            box.IsCheckedChanged += (_, _) => UpdateButton();
            _rows.Add((box, issue));
            list.Children.Add(box);
        }

        var intro = new TextBlock
        {
            Classes = { "hint" },
            Text = (issues.Count == 0 ? "Nessuna card nuova da importare." : $"{issues.Count} card assegnate a te non sono ancora nella bacheca. ")
                   + (alreadyThere > 0 ? $"{alreadyThere} ci sono già. " : "")
                   + (issues.Count == 0 ? "" : "Arrivano in To-Do come «ticket», con il link alla card; il «Fatto quando» resta da definire (lo completa /smista)."),
        };

        var cancel = new Button { Content = issues.Count == 0 ? "Chiudi" : "Annulla", IsCancel = true };
        _import.Click += (_, _) => Import();
        cancel.Click += (_, _) => Close();
        _import.IsVisible = issues.Count > 0;
        UpdateButton();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_import);
        buttons.Children.Add(cancel);
        var root = new DockPanel { Margin = new Thickness(18, 14) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(intro);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = list });
        Content = root;
    }

    /// <summary>The ticked cards, or null if cancelled.</summary>
    public async Task<IReadOnlyList<TrackerIssue>?> ShowAndWaitAsync(Window owner)
    {
        await ShowDialog(owner);
        return _result;
    }

    internal IReadOnlyList<CheckBox> Boxes => _rows.Select(r => r.Box).ToList();

    internal void Import()
    {
        _result = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Issue).ToList();
        Close();
    }

    private void UpdateButton()
    {
        var count = _rows.Count(r => r.Box.IsChecked == true);
        _import.Content = count == 1 ? "Importa 1 card" : $"Importa {count} card";
        _import.IsEnabled = count > 0;
    }
}
