using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core;

namespace Buddy.App.Settings;

/// <summary>
/// "Nuovo progetto": name, vault folder and (optionally) repository. The conventions of the current project (ID
/// format, commit and PR instructions) are carried over; the flow notes found in the vault become the always-included
/// notes. Everything else can be refined later in Impostazioni.
/// </summary>
public sealed class NewProjectWindow : Window
{
    private readonly BuddySettings _current;
    private readonly TextBox _name = new() { PlaceholderText = "es. Buddy" };
    private readonly TextBox _vault = new() { PlaceholderText = "Cartella delle note del progetto (Obsidian)" };
    private readonly TextBox _repo = new() { PlaceholderText = "Facoltativa: cartella del codice" };
    private readonly TextBlock _found = new() { Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private ProjectPreset? _result;

    public NewProjectWindow(BuddySettings current)
    {
        _current = current;
        Title = "Nuovo progetto";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Classes.Add("form");

        var create = new Button { Content = "Crea e passa al progetto", IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Annulla", IsCancel = true };
        create.Click += (_, _) => Create();
        cancel.Click += (_, _) => Close();
        _vault.TextChanged += (_, _) => DescribeVault();

        var panel = new StackPanel { Margin = new Thickness(18, 14), Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Classes = { "hint" },
            TextWrapping = TextWrapping.Wrap,
            Text = "Ogni progetto ha le sue note e il suo codice. Formato degli ID e istruzioni per commit e PR vengono copiati "
                   + $"da «{(string.IsNullOrWhiteSpace(current.ActivePreset) ? "progetto attuale" : current.ActivePreset)}»; "
                   + "il resto si rifinisce in Impostazioni. Buddy si riavvia sul progetto nuovo.",
        });
        panel.Children.Add(Label("Nome"));
        panel.Children.Add(_name);
        panel.Children.Add(Label("Vault del progetto"));
        panel.Children.Add(Row(_vault, () => _vault.Text, path => _vault.Text = path));
        panel.Children.Add(_found);
        panel.Children.Add(Label("Repository"));
        panel.Children.Add(Row(_repo, () => _repo.Text, path => _repo.Text = path));
        panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Opened += (_, _) => _name.Focus();
        DescribeVault();
    }

    /// <summary>The project to switch to, or null if cancelled.</summary>
    public async Task<ProjectPreset?> ShowAndWaitAsync(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            await ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            Closed += (_, _) => closed.TrySetResult();
            Show();
            await closed.Task;
        }

        return _result;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2) };

    private Control Row(TextBox box, Func<string?> current, Action<string> set)
    {
        var browse = new Button { Content = "Sfoglia…", Margin = new Thickness(6, 4, 0, 4) };
        browse.Click += async (_, _) =>
        {
            if (await Dialogs.PickFolderAsync(this, current()) is { } path)
            {
                set(path);
                if (string.IsNullOrWhiteSpace(_name.Text) && box == _vault)
                {
                    _name.Text = Path.GetFileName(path.TrimEnd('/', '\\'));
                }
            }
        };
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(box);
        return row;
    }

    private void DescribeVault()
    {
        var vault = _vault.Text?.Trim() ?? "";
        if (!Directory.Exists(vault))
        {
            _found.Text = "Le note «Stato attuale», «To-Do» e «Da controllare», se ci sono, finiscono nel contesto del modello locale.";
            return;
        }

        var notes = ProjectPreset.FlowNotes.Where(n => File.Exists(Path.Combine(vault, n))).Select(Path.GetFileNameWithoutExtension).ToList();
        _found.Text = notes.Count > 0
            ? $"✓ Trovate le note del flusso: {string.Join(", ", notes)}."
            : "Nessuna nota del flusso in questa cartella: la bacheca resterà vuota finché non le crei (template del flusso).";
    }

    private void Create()
    {
        var name = _name.Text?.Trim() ?? "";
        var vault = _vault.Text?.Trim() ?? "";
        var repo = _repo.Text?.Trim() ?? "";
        string? problem = name.Length == 0 ? "Scrivi un nome."
            : _current.Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ? $"Esiste già un progetto «{name}»."
            : !Directory.Exists(vault) ? "La cartella del vault non esiste."
            : repo.Length > 0 && !Directory.Exists(repo) ? "La cartella del repository non esiste."
            : null;
        if (problem is not null)
        {
            _error.Text = problem;
            _error.IsVisible = true;
            return;
        }

        _result = ProjectPreset.New(name, vault, repo, _current);
        Close();
    }
}
