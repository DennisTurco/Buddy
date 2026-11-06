using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core;
using Buddy.Core.Claude;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.App.Settings;

/// <summary>
/// "Nuovo progetto": name, repository and vault folder. The conventions of the current project (ID format, commit and
/// PR instructions) are carried over; the flow notes found in the vault become the always-included notes. When the
/// vault folder has no flow notes (a user without Obsidian), they are created from the flow template
/// (<see cref="FlowTemplate"/>), and Claude can fill in the Scheda progetto from the repository.
/// Everything else can be refined later in Impostazioni.
/// </summary>
public sealed class NewProjectWindow : Window
{
    private readonly BuddySettings _current;
    private readonly TextBox _name = new() { PlaceholderText = "es. Buddy" };
    private readonly TextBox _vault = new() { PlaceholderText = "Cartella delle note del progetto (Obsidian)" };
    private readonly TextBox _repo = new() { PlaceholderText = "Facoltativa: cartella del codice" };
    private readonly TextBox _template = new() { PlaceholderText = "Quello incluso in Buddy" };
    private readonly TextBlock _found = new() { Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _createFlow = new() { Content = "Crea le note del flusso dal template", IsChecked = true };
    private readonly CheckBox _fillWithClaude = new() { Content = "Compila Scheda progetto e Stato attuale con Claude, leggendo il repository", IsChecked = true };
    private readonly TextBlock _status = new() { Classes = { "hint" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _create = new() { Content = "Crea e passa al progetto", IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _cancel = new() { Content = "Annulla", IsCancel = true };
    private bool _vaultEdited;
    private bool _settingVault;
    private bool _working;
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

        _template.Text = current.FlowTemplatePath;
        _create.Click += async (_, _) => await CreateAsync();
        _cancel.Click += (_, _) => Close();
        _vault.TextChanged += (_, _) =>
        {
            _vaultEdited |= !_settingVault;
            DescribeVault();
        };
        _repo.TextChanged += (_, _) => DescribeVault();
        _name.TextChanged += (_, _) => ProposeVault();
        _createFlow.IsCheckedChanged += (_, _) => DescribeVault();

        var panel = new StackPanel { Margin = new Thickness(18, 14), Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Classes = { "hint" },
            TextWrapping = TextWrapping.Wrap,
            Text = "Ogni progetto ha le sue note e il suo codice. Se non hai ancora un vault Obsidian, Buddy lo crea dal template del flusso. "
                   + "Formato degli ID e istruzioni per commit e PR vengono copiati "
                   + $"da «{(string.IsNullOrWhiteSpace(current.ActivePreset) ? "progetto attuale" : current.ActivePreset)}»; "
                   + "il resto si rifinisce in Impostazioni. Buddy si riavvia sul progetto nuovo.",
        });
        panel.Children.Add(Label("Nome"));
        panel.Children.Add(_name);
        panel.Children.Add(Label("Repository"));
        panel.Children.Add(Row(_repo, () => _repo.Text, path => _repo.Text = path));
        panel.Children.Add(Label("Vault del progetto"));
        panel.Children.Add(Row(_vault, () => _vault.Text, path => _vault.Text = path));
        panel.Children.Add(_found);
        panel.Children.Add(_createFlow);
        panel.Children.Add(_fillWithClaude);
        panel.Children.Add(Label("Template del flusso (facoltativo)"));
        panel.Children.Add(Row(_template, () => _template.Text, path => _template.Text = path));
        panel.Children.Add(_status);
        panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_create);
        buttons.Children.Add(_cancel);
        panel.Children.Add(buttons);
        Content = panel;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_working)
            {
                Close();
            }
        };
        Opened += (_, _) => _name.Focus();
        Closing += (_, e) => e.Cancel = _working; // Claude is still writing the notes
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
                if (string.IsNullOrWhiteSpace(_name.Text) && (box == _vault || box == _repo))
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

    /// <summary>Until the user picks a folder, the vault follows the name: Buddy's own vault, one folder per project.</summary>
    private void ProposeVault()
    {
        if (_vaultEdited)
        {
            return;
        }

        _settingVault = true;
        _vault.Text = string.IsNullOrWhiteSpace(_name.Text) ? "" : FlowTemplate.DefaultProjectFolder(_name.Text);
        _settingVault = false;
    }

    private bool VaultHasFlow => _vault.Text?.Trim() is { Length: > 0 } vault && Directory.Exists(vault) && FlowTemplate.HasFlow(vault);

    private void DescribeVault()
    {
        var vault = _vault.Text?.Trim() ?? "";
        var hasFlow = VaultHasFlow;
        _createFlow.IsVisible = !hasFlow;
        _fillWithClaude.IsVisible = !hasFlow;
        _fillWithClaude.IsEnabled = _createFlow.IsChecked == true && !string.IsNullOrWhiteSpace(_repo.Text);

        if (vault.Length == 0)
        {
            _found.Text = "Le note «Stato attuale», «To-Do» e «Da controllare», se ci sono, finiscono nel contesto del modello locale.";
        }
        else if (hasFlow)
        {
            var notes = ProjectPreset.FlowNotes.Where(n => File.Exists(Path.Combine(vault, n))).Select(Path.GetFileNameWithoutExtension).ToList();
            _found.Text = $"✓ Trovate le note del flusso: {string.Join(", ", notes)}.";
        }
        else
        {
            _found.Text = $"Nessuna nota del flusso qui. Vault: {FlowTemplate.VaultRootOf(vault)}"
                          + (Directory.Exists(vault) ? "" : " (la cartella verrà creata)") + ".";
        }
    }

    private async Task CreateAsync()
    {
        if (_result is not null)
        {
            Close(); // "Continua" after Claude could not fill in the notes
            return;
        }

        if (_working)
        {
            return;
        }

        var name = _name.Text?.Trim() ?? "";
        var vault = _vault.Text?.Trim() ?? "";
        var repo = _repo.Text?.Trim() ?? "";
        var template = _template.Text?.Trim() ?? "";
        var createFlow = !VaultHasFlow && _createFlow.IsChecked == true;
        string? problem = name.Length == 0 ? "Scrivi un nome."
            : _current.Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ? $"Esiste già un progetto «{name}»."
            : vault.Length == 0 ? "Scegli la cartella del vault."
            : !Directory.Exists(vault) && !createFlow ? "La cartella del vault non esiste."
            : repo.Length > 0 && !Directory.Exists(repo) ? "La cartella del repository non esiste."
            : template.Length > 0 && !Directory.Exists(template) ? "La cartella del template non esiste."
            : null;
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        _error.IsVisible = false;
        _current.FlowTemplatePath = template;
        if (createFlow)
        {
            try
            {
                var created = FlowTemplate.CreateProject(vault, name, template.Length > 0 ? template : null);
                ShowStatus($"✓ Create {created.Count} note dal template.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ShowError($"Non riesco a creare le note: {ex.Message}");
                return;
            }

            if (_fillWithClaude.IsChecked == true && _fillWithClaude.IsEnabled)
            {
                _working = true;
                _create.IsEnabled = false;
                _cancel.IsEnabled = false;
                ShowStatus("Claude legge il repository e compila Scheda progetto e Stato attuale… (di solito meno di un minuto)");
                var repoSettings = RepoOnlySettings(repo);
                var commits = await new Buddy.Core.Repo.GitRepo(Options.Create(repoSettings), TimeProvider.System).GetRecentCommitsAsync(15);
                var result = await new ClaudeOneShot(Options.Create(_current))
                    .RunAsync(FlowTemplate.FillPrompt(name, repo, commits), vault, "sonnet", [repo]);
                _working = false;
                _cancel.IsEnabled = true;
                if (!result.Success)
                {
                    // The notes are there anyway: the Scheda can be filled in later.
                    ShowError($"Claude non ha compilato le note ({result.Text}). Le trovi vuote: compilale a mano o più tardi con Claude.");
                    _result = ProjectPreset.New(name, vault, repo, _current);
                    _create.Content = "Continua";
                    _create.IsEnabled = true;
                    return;
                }
            }
        }

        _result = ProjectPreset.New(name, vault, repo, _current);
        Close();
    }

    /// <summary>Settings pointing at the new project's repository, only to read its git history.</summary>
    private static BuddySettings RepoOnlySettings(string repo) => new() { Repo = new RepoSettings { Path = repo } };

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.IsVisible = true;
    }
}
