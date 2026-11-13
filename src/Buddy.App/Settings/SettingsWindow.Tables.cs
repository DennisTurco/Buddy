using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Buddy.Core;
using Buddy.Core.Chat;

namespace Buddy.App.Settings;

/// <summary>
/// The settings' tables: Claude's tool rules (allow / deny, each switched on or off), the extra folders Claude may
/// reach, and the buddy's commands and Claude's skills (on or off, strong model or not). Rows are built in code and
/// read back on save, like the rest of the window.
/// </summary>
public partial class SettingsWindow
{
    private static readonly string[] RuleKinds = ["✅ Consenti", "⛔ Nega"];

    private readonly List<RuleRow> _rules = [];
    private readonly List<CommandRow> _commandRows = [];

    private sealed record RuleRow(Grid Row, CheckBox Enabled, ComboBox Kind, TextBox Tool, TextBox Note);

    private sealed record CommandRow(Grid Row, string Name, string Text, CheckBox Enabled, CheckBox? Heavy);

    // ---- Tool rules -------------------------------------------------------------------------------------------

    private void BuildRuleTable()
    {
        var claude = _settings.Claude;

        // The older plain list becomes rows of the table, switched on as they were.
        var rules = claude.ToolRules.Count > 0 || claude.AutoAllowTools.Count == 0
            ? claude.ToolRules
            : claude.AutoAllowTools.Select(t => new ToolRule { Tool = t, Note = Note(t) }).ToList();
        foreach (var rule in rules)
        {
            AddRuleRow(rule);
        }

        RuleFilter.TextChanged += (_, _) => FilterRules();
        UpdateRuleSummary();
    }

    private static string Note(string tool) =>
        ClaudeSettings.SuggestedRules.FirstOrDefault(r => r.Tool.Equals(tool, StringComparison.OrdinalIgnoreCase))?.Note ?? "";

    private RuleRow AddRuleRow(ToolRule rule)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("56,150,2*,2*,40"), Margin = new Thickness(0, 2) };
        var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center };
        var kind = new ComboBox { ItemsSource = RuleKinds, SelectedIndex = rule.Allow ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 6, 0) };
        var tool = new TextBox { Text = rule.Tool, PlaceholderText = "es. Bash(git status:*)", FontFamily = (Avalonia.Media.FontFamily)this.FindResource("MonoFont")!, Margin = new Thickness(0, 0, 6, 0) };
        var note = new TextBox { Text = rule.Note, PlaceholderText = "a cosa serve", Margin = new Thickness(0, 0, 6, 0) };
        var remove = new Button { Content = new Buddy.App.Chat.IconLabel { Label = "🗑" }, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(remove, "Togli la regola");
        Grid.SetColumn(kind, 1);
        Grid.SetColumn(tool, 2);
        Grid.SetColumn(note, 3);
        Grid.SetColumn(remove, 4);
        grid.Children.AddRange([enabled, kind, tool, note, remove]);

        var row = new RuleRow(grid, enabled, kind, tool, note);
        remove.Click += (_, _) =>
        {
            _rules.Remove(row);
            RuleRows.Children.Remove(grid);
            UpdateRuleSummary();
        };
        enabled.IsCheckedChanged += (_, _) => UpdateRuleSummary();
        kind.SelectionChanged += (_, _) => UpdateRuleSummary();
        _rules.Add(row);
        RuleRows.Children.Add(grid);
        return row;
    }

    private void OnAddRuleClick(object? sender, RoutedEventArgs e)
    {
        RuleFilter.Text = "";
        var row = AddRuleRow(new ToolRule());
        row.Tool.Focus();
        UpdateRuleSummary();
    }

    private void OnAddSuggestedRulesClick(object? sender, RoutedEventArgs e)
    {
        var have = _rules.Select(r => r.Tool.Text?.Trim() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in ClaudeSettings.SuggestedRules.Where(r => !have.Contains(r.Tool)))
        {
            AddRuleRow(new ToolRule { Tool = rule.Tool, Allow = rule.Allow, Note = rule.Note, Enabled = false });
        }

        UpdateRuleSummary();
    }

    private void OnAllRulesClick(object? sender, RoutedEventArgs e)
    {
        var on = (sender as Control)?.Tag as string == "on";
        foreach (var row in _rules.Where(r => r.Row.IsVisible))
        {
            row.Enabled.IsChecked = on;
        }
    }

    private void FilterRules()
    {
        var filter = RuleFilter.Text?.Trim() ?? "";
        foreach (var row in _rules)
        {
            row.Row.IsVisible = filter.Length == 0
                                || (row.Tool.Text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                                || (row.Note.Text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateRuleSummary()
    {
        var on = _rules.Where(r => r.Enabled.IsChecked == true).ToList();
        RuleSummary.Text = _rules.Count == 0
            ? "Nessuna regola: Claude chiede per tutto (secondo la modalità scelta sopra)."
            : $"{_rules.Count} regole: {on.Count(r => r.Kind.SelectedIndex == 0)} consentite e {on.Count(r => r.Kind.SelectedIndex == 1)} negate attive, {_rules.Count - on.Count} spente.";
    }

    private List<ToolRule> CollectRules() => _rules
        .Select(r => new ToolRule
        {
            Tool = r.Tool.Text?.Trim() ?? "",
            Allow = r.Kind.SelectedIndex != 1,
            Enabled = r.Enabled.IsChecked == true,
            Note = r.Note.Text?.Trim() ?? "",
        })
        .Where(r => r.Tool.Length > 0)
        .GroupBy(r => (r.Tool, r.Allow))
        .Select(g => g.First())
        .ToList();

    // ---- Extra folders ----------------------------------------------------------------------------------------

    private readonly List<TextBox> _directories = [];

    private void BuildDirectoryTable()
    {
        foreach (var dir in _settings.Claude.AdditionalDirectories)
        {
            AddDirectoryRow(dir);
        }
    }

    private TextBox AddDirectoryRow(string path)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,40"), Margin = new Thickness(0, 2) };
        var box = new TextBox { Text = path, PlaceholderText = @"C:\percorso\della\cartella", Margin = new Thickness(0, 0, 6, 0) };
        var browse = new Button { Content = "Sfoglia…", Margin = new Thickness(0, 0, 6, 0) };
        var remove = new Button { Content = new Buddy.App.Chat.IconLabel { Label = "🗑" } };
        ToolTip.SetTip(remove, "Togli la cartella");
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(remove, 2);
        grid.Children.AddRange([box, browse, remove]);

        browse.Click += async (_, _) =>
        {
            if (await Dialogs.PickFolderAsync(this, box.Text) is { } local)
            {
                box.Text = local;
            }
        };
        remove.Click += (_, _) =>
        {
            _directories.Remove(box);
            DirectoryRows.Children.Remove(grid);
        };
        _directories.Add(box);
        DirectoryRows.Children.Add(grid);
        return box;
    }

    private void OnAddDirectoryClick(object? sender, RoutedEventArgs e) => AddDirectoryRow("").Focus();

    // ---- Behaviours -------------------------------------------------------------------------------------------

    private readonly List<(Buddy.Core.Claude.Behavior Behavior, CheckBox Check)> _behaviorRows = [];

    private void BuildBehaviorTable()
    {
        GuidedModeProject.Text = string.IsNullOrWhiteSpace(_settings.ActivePreset)
            ? "Vale per il progetto aperto."
            : $"Vale solo per «{_settings.ActivePreset}»: gli altri progetti hanno la loro.";
        foreach (var behavior in Buddy.Core.Claude.Behaviors.All)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("56,230,*,90") };
            var check = new CheckBox { IsChecked = Buddy.Core.Claude.Behaviors.IsOn(_settings, behavior), VerticalAlignment = VerticalAlignment.Top };
            var label = new TextBlock { Text = behavior.Label, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            var description = new TextBlock { Text = behavior.Description, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
            var skill = new TextBlock { Text = behavior.SkillDefault ? "sì" : "no", Classes = { "hint" } };
            Grid.SetColumn(label, 1);
            Grid.SetColumn(description, 2);
            Grid.SetColumn(skill, 3);
            grid.Children.AddRange([check, label, description, skill]);
            _behaviorRows.Add((behavior, check));
            BehaviorRows.Children.Add(grid);
        }
    }

    private void OnResetBehaviorsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var (behavior, check) in _behaviorRows)
        {
            check.IsChecked = behavior.SkillDefault;
        }
    }

    /// <summary>Only the behaviours switched away from the skills' are kept: the rest follows the skills.</summary>
    private void CollectBehaviors() =>
        _settings.Behaviors = _behaviorRows
            .Where(r => (r.Check.IsChecked == true) != r.Behavior.SkillDefault)
            .ToDictionary(r => r.Behavior.Key, r => r.Check.IsChecked == true);

    // ---- Local AI ---------------------------------------------------------------------------------------------

    /// <summary>"Controlla i modelli": whether the chat, jobs and embedding models are installed in Ollama.</summary>
    private async void OnCheckLocalModelsClick(object? sender, RoutedEventArgs e)
    {
        LocalModelsResult.Text = "Controllo…";
        var installed = await _ollama.ListModelsAsync(_settings.Ollama.BaseUrl);
        if (installed.Count == 0)
        {
            LocalModelsResult.Text = $"✗ Ollama non risponde su {_settings.Ollama.BaseUrl}: è avviato?";
            return;
        }

        bool Has(string model) => installed.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase)
                                                     || (!model.Contains(':') && m.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase)));
        var jobs = string.IsNullOrWhiteSpace(_settings.LocalAi.Model) ? _settings.Ollama.Model : _settings.LocalAi.Model;
        var lines = new[] { ("Chat", _settings.Ollama.Model), ("Lavori", jobs), ("Embedding", _settings.LocalAi.EmbeddingModel) }
            .Select(m => Has(m.Item2) ? $"✓ {m.Item1}: {m.Item2}" : $"✗ {m.Item1}: {m.Item2} non installato (ollama pull {m.Item2})");
        LocalModelsResult.Text = string.Join("   ", lines);
    }

    // ---- Commands ---------------------------------------------------------------------------------------------

    private void BuildCommandTable(IReadOnlyList<ChatCommand> commands)
    {
        var heavy = _settings.Claude.EffectiveHeavyCommands();
        foreach (var command in commands)
        {
            AddCommandRow(command.Name, command.Usage, command.Description,
                command.Group == CommandGroup.Mentor ? "Mentore" : "Buddy", heavy: null);
        }

        var own = commands.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var descriptions = Buddy.Core.Claude.ClaudeSkills.Descriptions(_settings);
        foreach (var skill in Buddy.Core.Claude.ClaudeSkills.Installed(_settings).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = "/" + skill;
            if (!own.Contains(name))
            {
                AddCommandRow(name, name, descriptions.GetValueOrDefault(skill) is { Length: > 0 } d ? d : "skill di Claude", "Skill",
                    heavy: heavy.Contains(name, StringComparer.OrdinalIgnoreCase));
            }
        }

        CommandFilter.TextChanged += (_, _) =>
        {
            var filter = CommandFilter.Text?.Trim() ?? "";
            foreach (var row in _commandRows)
            {
                row.Row.IsVisible = filter.Length == 0 || row.Text.Contains(filter, StringComparison.OrdinalIgnoreCase);
            }
        };
        UpdateCommandSummary();
    }

    private void AddCommandRow(string name, string usage, string description, string owner, bool? heavy)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("56,190,*,90,100"), Margin = new Thickness(0, 3) };
        var enabled = new CheckBox { IsChecked = !_settings.Assistant.IsDisabled(name), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = usage, FontFamily = (Avalonia.Media.FontFamily)this.FindResource("MonoFont")!, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        var text = new TextBlock { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var who = new TextBlock { Text = owner, Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        Grid.SetColumn(text, 2);
        Grid.SetColumn(who, 3);
        grid.Children.AddRange([enabled, label, text, who]);

        CheckBox? strong = null;
        if (heavy is { } isHeavy)
        {
            strong = new CheckBox { IsChecked = isHeavy, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(strong, "Con «Scegli il modello da solo», questa skill usa Opus");
            Grid.SetColumn(strong, 4);
            grid.Children.Add(strong);
        }

        enabled.IsCheckedChanged += (_, _) => UpdateCommandSummary();
        _commandRows.Add(new CommandRow(grid, name, $"{usage} {description} {owner}", enabled, strong));
        CommandRows.Children.Add(grid);
    }

    private void OnAllCommandsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _commandRows)
        {
            row.Enabled.IsChecked = true;
        }
    }

    private void UpdateCommandSummary()
    {
        var off = _commandRows.Count(r => r.Enabled.IsChecked != true);
        CommandSummary.Text = $"{_commandRows.Count} comandi, {off} spenti.";
    }

    /// <summary>The copy being edited (for the tests: saving would write the user's settings).</summary>
    internal BuddySettings Edited => _settings;

    internal void CollectTables()
    {
        CollectBehaviors();
        _settings.Claude.ToolRules = CollectRules();
        _settings.Claude.AutoAllowTools = []; // now in the table
        _settings.Claude.AdditionalDirectories = _directories.Select(d => d.Text?.Trim() ?? "").Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (_commandRows.Count == 0)
        {
            return; // opened without the command list: leave what is there
        }

        // Commands not in the table (e.g. a skill not installed now) keep their state.
        var shown = _commandRows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _settings.Assistant.DisabledCommands = _settings.Assistant.DisabledCommands.Where(c => !shown.Contains(c))
            .Concat(_commandRows.Where(r => r.Enabled.IsChecked != true).Select(r => r.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var heavyRows = _commandRows.Where(r => r.Heavy is not null).ToList();
        if (heavyRows.Count > 0)
        {
            var notShown = _settings.Claude.EffectiveHeavyCommands().Where(c => !shown.Contains(c) && c != ClaudeSettings.NoHeavyCommand);
            var heavy = notShown.Concat(heavyRows.Where(r => r.Heavy!.IsChecked == true).Select(r => r.Name)).ToList();
            _settings.Claude.HeavyCommands = heavy.Count > 0 ? heavy : [ClaudeSettings.NoHeavyCommand];
        }
    }
}
