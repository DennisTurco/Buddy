using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.App.Pet;
using Buddy.Core;
using Buddy.Core.Llm;
using Buddy.Core.Mentor;

namespace Buddy.App.Settings;

/// <summary>
/// Edits a copy of the settings; "Salva e riavvia" writes the user's appsettings.json and restarts the app,
/// which is simpler and more reliable than re-wiring every component live.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] CommonTools = ["Read", "Glob", "Grep", "WebSearch", "WebFetch"];

    private static readonly (string Key, string Label, string Hint)[] PermissionModes =
    [
        ("default", "Chiedi (tranne gli strumenti qui sotto)",
            "Ogni modifica, comando o strumento non elencato sotto passa dalla richiesta nella nuvoletta."),
        ("acceptEdits", "Modifiche ai file senza chiedere",
            "Claude crea e modifica file da solo; per i comandi (Bash) e gli strumenti non elencati chiede ancora."),
        ("auto", "Automatica (chiede solo per le azioni rischiose)",
            "Claude Code valuta ogni azione da solo e chiede solo quelle rischiose (cancellazioni, push, comandi distruttivi). Serve una versione recente di Claude Code e un piano che la preveda."),
        ("bypassPermissions", "Non chiedere mai",
            "Claude fa tutto senza chiedere, comandi compresi (anche cancellare file o fare push). Usala solo se ti fidi delle richieste che gli fai; le regole \"deny\" delle impostazioni di Claude Code valgono comunque."),
    ];

    private static readonly (string Label, DayOfWeek Day)[] RetroDays =
    [
        ("Lunedì", DayOfWeek.Monday), ("Martedì", DayOfWeek.Tuesday), ("Mercoledì", DayOfWeek.Wednesday),
        ("Giovedì", DayOfWeek.Thursday), ("Venerdì", DayOfWeek.Friday),
    ];

    private readonly BuddySettings _settings;
    private readonly OllamaClient _ollama;
    private readonly List<Personality> _personalities;
    private bool _saved;

    /// <summary>For the XAML previewer (see <see cref="DesignData"/>).</summary>
    public SettingsWindow() : this(DesignData.Settings.Value, DesignData.Ollama)
    {
    }

    public SettingsWindow(BuddySettings current, OllamaClient ollama)
    {
        InitializeComponent();
        _settings = SettingsStore.Clone(current);
        _ollama = ollama;
        DataContext = _settings;

        FontCombo.ItemsSource = FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().Order().ToList();
        BuildThemeRows();
        BuildThemePresets();
        BuildPetColorRows();
        if (Buddy.Core.Tracker.JiraToken.Read(SettingsStore.Folder) is not null)
        {
            JiraTokenBox.PlaceholderText = "Token salvato: lascia vuoto per tenerlo, o scrivine uno nuovo";
        }

        if (Buddy.Core.Tracker.AzureToken.Read(SettingsStore.Folder) is not null)
        {
            AzureTokenBox.PlaceholderText = "Token salvato: lascia vuoto per tenerlo, o scrivine uno nuovo";
        }


        SystemPrompt.Text = string.IsNullOrWhiteSpace(_settings.Ollama.SystemPrompt) ? LocalAssistant.DefaultSystemPrompt : _settings.Ollama.SystemPrompt;

        PermissionModeCombo.ItemsSource = PermissionModes.Select(m => m.Label).ToList();
        PermissionModeCombo.SelectionChanged += (_, _) =>
            PermissionModeHint.Text = PermissionModeCombo.SelectedIndex >= 0 ? PermissionModes[PermissionModeCombo.SelectedIndex].Hint : "";
        PermissionModeCombo.SelectedIndex = Math.Max(0, Array.FindIndex(PermissionModes, m => m.Key == _settings.Claude.PermissionMode));

        var allowed = _settings.Claude.AutoAllowTools;
        AllowRead.IsChecked = allowed.Contains("Read");
        AllowGlob.IsChecked = allowed.Contains("Glob");
        AllowGrep.IsChecked = allowed.Contains("Grep");
        AllowWebSearch.IsChecked = allowed.Contains("WebSearch");
        AllowWebFetch.IsChecked = allowed.Contains("WebFetch");
        ExtraAllowedTools.Text = string.Join(Environment.NewLine, allowed.Except(CommonTools));

        LoadProjectLists();
        RefreshPresetList();
        Topics.Text = string.Join(Environment.NewLine, _settings.Mentor.Topics);
        DoNotCommit.Text = string.Join(Environment.NewLine, _settings.Claude.DoNotCommit);
        Interests.Text = string.Join(", ", _settings.Mentor.Interests);

        _personalities = Personalities.All.Append(new Personality(Personalities.Custom, "Personalizzata", "")).ToList();
        PersonalityCombo.ItemsSource = _personalities;
        PersonalityCombo.SelectedItem = _personalities.FirstOrDefault(p => p.Key == _settings.Mentor.Personality) ?? _personalities[0];

        RetroDayCombo.ItemsSource = RetroDays.Select(d => d.Label).ToList();
        RetroDayCombo.SelectedIndex = Math.Max(0, Array.FindIndex(RetroDays, d => d.Day.ToString() == _settings.Mentor.RetroDay));

        Opened += async (_, _) => await RefreshModelsAsync();
    }

    /// <summary>Shows the window and completes with true once the settings were saved (false if cancelled).</summary>
    public Task<bool> ShowAndWaitAsync()
    {
        var closed = new TaskCompletionSource<bool>();
        Closed += (_, _) => closed.TrySetResult(_saved);
        Show();
        Activate();
        return closed.Task;
    }

    /// <summary>Fills the project text boxes that are edited as one item per line.</summary>
    private void LoadProjectLists()
    {
        AlwaysIncludedNotes.Text = string.Join(Environment.NewLine, _settings.AlwaysIncludedNotes);
        TaskActions.Text = string.Join(Environment.NewLine, _settings.Assistant.TaskActions);
        KnowledgePaths.Text = string.Join(Environment.NewLine, _settings.Repo.KnowledgePaths);
        Groups.Text = string.Join(Environment.NewLine, _settings.Repo.Groups);
    }

    /// <summary>Copies the one-item-per-line text boxes back into the settings.</summary>
    private void CollectProjectLists()
    {
        _settings.AlwaysIncludedNotes = Lines(AlwaysIncludedNotes.Text);
        _settings.Assistant.TaskActions = Lines(TaskActions.Text);
        _settings.Repo.KnowledgePaths = Lines(KnowledgePaths.Text);
        _settings.Repo.Groups = Lines(Groups.Text);
    }

    private void RefreshPresetList()
    {
        PresetList.ItemsSource = null;
        PresetList.ItemsSource = _settings.Presets;
        PresetList.SelectedItem = _settings.Presets.FirstOrDefault(p => p.Name == _settings.ActivePreset);
    }

    private async void OnPresetSaveCurrentClick(object? sender, RoutedEventArgs e)
    {
        var suggested = string.IsNullOrWhiteSpace(_settings.ActivePreset) ? Path.GetFileName(_settings.ProjectPath) : _settings.ActivePreset;
        if (await Dialogs.AskAsync(this, "Nuovo preset", "Nome del progetto:", suggested) is not { } name)
        {
            return;
        }

        CollectProjectLists();
        var preset = ProjectPreset.From(_settings, name);
        var existing = _settings.Presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _settings.Presets[existing] = preset;
        }
        else
        {
            _settings.Presets.Add(preset);
        }

        _settings.ActivePreset = name;
        RefreshPresetList();
    }

    private void OnPresetLoadClick(object? sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not ProjectPreset preset)
        {
            return;
        }

        CollectProjectLists(); // edits in the other tabs belong to the project being left
        _settings.SwitchTo(preset);
        RefreshBindings();
        LoadProjectLists();
        RefreshPresetList();
        ShowPresetHint($"Premi Salva: Buddy si riavvia su «{preset.Name}».");
    }

    private async void OnPresetNewClick(object? sender, RoutedEventArgs e)
    {
        if (await new NewProjectWindow(_settings).ShowAndWaitAsync(this) is not { } preset)
        {
            return;
        }

        CollectProjectLists();
        _settings.SwitchTo(preset);
        RefreshBindings();
        LoadProjectLists();
        RefreshPresetList();
        ShowPresetHint($"Progetto «{preset.Name}» creato. Controlla le altre schede e premi Salva: Buddy si riavvia su di lui.");
    }

    private void ShowPresetHint(string text)
    {
        PresetHint.Text = text;
        PresetHint.IsVisible = true;
    }

    private async void OnPresetRenameClick(object? sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not ProjectPreset preset
            || await Dialogs.AskAsync(this, "Rinomina preset", "Nuovo nome:", preset.Name) is not { } name)
        {
            return;
        }

        if (_settings.ActivePreset == preset.Name)
        {
            _settings.ActivePreset = name;
        }

        preset.Name = name;
        RefreshPresetList();
    }

    private void OnPresetDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not ProjectPreset preset)
        {
            return;
        }

        _settings.Presets.Remove(preset);
        if (_settings.ActivePreset == preset.Name)
        {
            _settings.ActivePreset = "";
        }

        RefreshPresetList();
    }

    private void BuildThemeRows()
    {
        var t = _settings.Appearance.Theme;
        AddColorRow(ThemeColors, "Sfondo", () => t.Background, v => t.Background = v);
        AddColorRow(ThemeColors, "Bordo", () => t.Border, v => t.Border = v);
        AddColorRow(ThemeColors, "Testo", () => t.Text, v => t.Text = v);
        AddColorRow(ThemeColors, "Testo secondario", () => t.MutedText, v => t.MutedText = v);
        AddColorRow(ThemeColors, "Fumetto utente", () => t.UserBubble, v => t.UserBubble = v);
        AddColorRow(ThemeColors, "Fumetto locale", () => t.LocalBubble, v => t.LocalBubble = v);
        AddColorRow(ThemeColors, "Accento locale", () => t.LocalAccent, v => t.LocalAccent = v);
        AddColorRow(ThemeColors, "Fumetto Claude", () => t.ClaudeBubble, v => t.ClaudeBubble = v);
        AddColorRow(ThemeColors, "Accento Claude", () => t.ClaudeAccent, v => t.ClaudeAccent = v);
        AddColorRow(ThemeColors, "Campo di testo", () => t.Input, v => t.Input = v);
        AddColorRow(ThemeColors, "Fumetto mentore", () => t.MentorBubble, v => t.MentorBubble = v);
        AddColorRow(ThemeColors, "Accento mentore", () => t.MentorAccent, v => t.MentorAccent = v);
    }

    private void BuildPetColorRows()
    {
        var p = _settings.Pet;
        AddColorRow(PetColors, "Colore (locale)", () => p.LocalColor, v => p.LocalColor = v);
        AddColorRow(PetColors, "Colore (Claude)", () => p.ClaudeColor, v => p.ClaudeColor = v);
    }

    /// <summary>A label, a #RRGGBB text box and a live swatch, added as a new row of <paramref name="grid"/>.</summary>
    private void AddColorRow(Grid grid, string label, Func<string> get, Action<string> set)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
        var box = new TextBox
        {
            Text = get(),
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontFamily = (FontFamily)this.FindResource("MonoFont")!,
        };
        var swatch = new Border
        {
            Width = 40,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Update()
        {
            set(box.Text?.Trim() ?? "");
            swatch.Background = new SolidColorBrush(PetSprites.ParseColor(box.Text, Colors.Transparent));
        }

        box.TextChanged += (_, _) => Update();
        Update();

        Grid.SetRow(text, row);
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        Grid.SetRow(swatch, row);
        Grid.SetColumn(swatch, 2);
        grid.Children.Add(text);
        grid.Children.Add(box);
        grid.Children.Add(swatch);
    }

    private void ApplyTheme(ThemeColors theme)
    {
        _settings.Appearance.Theme = theme.Clone();
        ThemeColors.Children.Clear();
        ThemeColors.RowDefinitions.Clear();
        BuildThemeRows();
        BuildThemePresets();
    }

    /// <summary>The built-in themes and the saved ones, as buttons with a preview of their colours; the current one is marked.</summary>
    private void BuildThemePresets()
    {
        ThemePresets.Children.Clear();
        var current = _settings.Appearance.Theme;
        var themes = Buddy.Core.ThemeColors.BuiltIn.Select(t => (t.Name, Colors: t.Create(), Custom: false))
            .Concat(_settings.Appearance.CustomThemes.Select(t => (t.Name, t.Colors, Custom: true)));
        string? selectedCustom = null;
        foreach (var (name, colors, custom) in themes)
        {
            var selected = colors.SameAs(current);
            if (selected && custom)
            {
                selectedCustom = name;
            }

            ThemePresets.Children.Add(ThemeButton(name, colors, custom, selected));
        }

        DeleteThemeButton.IsVisible = selectedCustom is not null;
        DeleteThemeButton.Tag = selectedCustom;
    }

    private Button ThemeButton(string name, ThemeColors colors, bool custom, bool selected)
    {
        Border Swatch(string color) => new()
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(PetSprites.ParseColor(color, Colors.Gray)),
            BorderBrush = new SolidColorBrush(PetSprites.ParseColor(colors.Border, Colors.Gray)),
            BorderThickness = new Thickness(1),
        };

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var color in new[] { colors.UserBubble, colors.LocalAccent, colors.ClaudeAccent, colors.MentorAccent })
        {
            swatches.Children.Add(Swatch(color));
        }

        var label = new TextBlock
        {
            Text = (custom ? "★ " : "") + name,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(PetSprites.ParseColor(colors.Text, Colors.Black)),
        };
        var content = new StackPanel();
        content.Children.Add(label);
        content.Children.Add(swatches);
        var button = new Button
        {
            Content = content,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 8),
            MinWidth = 110,
            Background = new SolidColorBrush(PetSprites.ParseColor(colors.Background, Colors.White)),
            BorderBrush = selected ? new SolidColorBrush(PetSprites.ParseColor(colors.ClaudeAccent, Colors.Gray)) : Brushes.Gray,
            BorderThickness = new Thickness(selected ? 3 : 1),
            CornerRadius = new CornerRadius(10),
        };
        // Hovered or pressed it keeps its own colours (Fluent would turn it grey, and the name unreadable).
        var back = (IBrush)button.Background;
        var ink = label.Foreground!;
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
        {
            button.Resources[key] = back;
        }

        foreach (var key in new[] { "ButtonForegroundPointerOver", "ButtonForegroundPressed" })
        {
            button.Resources[key] = ink;
        }

        button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(PetSprites.ParseColor(colors.ClaudeAccent, Colors.Gray));
        ToolTip.SetTip(button, custom ? $"Tema personale «{name}»" : $"Tema {name}");
        button.Click += (_, _) => ApplyTheme(colors);
        return button;
    }

    private async void OnSaveThemeClick(object? sender, RoutedEventArgs e)
    {
        var current = _settings.Appearance.CustomThemes.FirstOrDefault(t => t.Colors.SameAs(_settings.Appearance.Theme))?.Name ?? "";
        if (await Dialogs.AskAsync(this, "Salva tema", "Nome del tema (uno esistente viene sovrascritto):", current) is not { } name)
        {
            return;
        }

        name = name.Trim();
        if (Buddy.Core.ThemeColors.BuiltIn.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            await Dialogs.ShowMessageAsync(this, $"«{name}» è un tema già incluso: scegli un altro nome.");
            return;
        }

        var themes = _settings.Appearance.CustomThemes;
        themes.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        themes.Add(new NamedTheme { Name = name, Colors = _settings.Appearance.Theme.Clone() });
        BuildThemePresets();
    }

    private void OnDeleteThemeClick(object? sender, RoutedEventArgs e)
    {
        if (DeleteThemeButton.Tag is string name)
        {
            _settings.Appearance.CustomThemes.RemoveAll(t => t.Name == name);
            BuildThemePresets();
        }
    }

    private async void OnRefreshModelsClick(object? sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private async Task RefreshModelsAsync()
    {
        var current = _settings.Ollama.Model;
        var models = await _ollama.ListModelsAsync(_settings.Ollama.BaseUrl);
        OllamaModels.ItemsSource = models;
        OllamaModels.Text = current;
        ToolTip.SetTip(OllamaModels, models.Count == 0 ? "Ollama non raggiungibile a questo indirizzo" : null);
    }

    private async void OnBrowseProjectClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderAsync(_settings.ProjectPath, path => _settings.ProjectPath = path);

    private async void OnBrowseClaudeDirClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderAsync(_settings.Claude.WorkingDirectory, path => _settings.Claude.WorkingDirectory = path);

    /// <summary>Saves everything with the first-run flag cleared: after the restart the wizard opens again.</summary>
    private void OnRedoFirstRunClick(object? sender, RoutedEventArgs e)
    {
        _settings.FirstRunCompleted = false;
        OnSaveClick(sender, e);
    }

    private async void OnBrowseJournalClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderAsync(_settings.Mentor.JournalPath, path => _settings.Mentor.JournalPath = path);

    private async void OnBrowseWhisperClick(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.PickFileAsync(this, _settings.Voice.ModelPath, "Modelli Whisper", "*.bin") is { } path)
        {
            _settings.Voice.ModelPath = path;
            RefreshBindings();
        }
    }

    private async void OnBrowseRepoClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderAsync(_settings.Repo.Path, path => _settings.Repo.Path = path);

    private async void OnBrowseSpriteFolderClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderAsync(_settings.Pet.SpriteFolder, path => _settings.Pet.SpriteFolder = path);

    private async void OnBrowseClaudeExeClick(object? sender, RoutedEventArgs e)
    {
        string[] patterns = OperatingSystem.IsWindows() ? ["*.exe", "*.cmd"] : ["claude", "*"];
        if (await Dialogs.PickFileAsync(this, _settings.Claude.Executable, "Eseguibili", patterns) is { } path)
        {
            _settings.Claude.Executable = path;
            RefreshBindings();
        }
    }

    private void OnExportSpritesClick(object? sender, RoutedEventArgs e)
    {
        var folder = string.IsNullOrWhiteSpace(_settings.Pet.SpriteFolder)
            ? Path.Combine(SettingsStore.Folder, "sprites")
            : _settings.Pet.SpriteFolder;

        // Export the built-in art (ignore any custom folder) with the colours currently chosen.
        var builtIn = SettingsStore.Clone(_settings).Pet;
        builtIn.SpriteFolder = "";
        new PetSprites(builtIn).ExportTemplate(folder);

        _settings.Pet.SpriteFolder = folder;
        RefreshBindings();
        Shell.Open(folder);
    }

    private void OnResetPromptClick(object? sender, RoutedEventArgs e) => SystemPrompt.Text = LocalAssistant.DefaultSystemPrompt;

    private async Task BrowseFolderAsync(string current, Action<string> set)
    {
        if (await Dialogs.PickFolderAsync(this, current) is { } path)
        {
            set(path);
            RefreshBindings();
        }
    }

    /// <summary>Plain settings classes do not notify changes: rebind after editing them from code.</summary>
    private void RefreshBindings()
    {
        DataContext = null;
        DataContext = _settings;
    }

    /// <summary>The token typed in the box, or the saved one when the box was left empty.</summary>
    private string? JiraTokenTyped => JiraTokenBox.Text?.Trim() is { Length: > 0 } typed ? typed : Buddy.Core.Tracker.JiraToken.Read(SettingsStore.Folder);

    private async void OnJiraTestClick(object? sender, RoutedEventArgs e)
    {
        if (!_settings.Jira.IsConfigured || JiraTokenTyped is not { } token)
        {
            JiraTestResult.Text = "Scrivi l'indirizzo di Jira e il token.";
            return;
        }

        JiraTestResult.Text = "Provo…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var issues = await new Buddy.Core.Tracker.JiraClient(http).AssignedAsync(_settings.Jira, token, _settings.JiraProject);
            JiraTestResult.Text = issues.Count == 0
                ? "✓ Collegato: nessuna card aperta assegnata a te."
                : $"✓ Collegato: {issues.Count} card aperte assegnate a te (es. {issues[0].Key} · {issues[0].Summary}).";
        }
        catch (Exception ex) when (ex is Buddy.Core.Tracker.JiraException or HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            JiraTestResult.Text = $"✗ {ex.Message}";
        }
    }

    /// <summary>Work items and PRs read with the token typed (or saved), on the project from the settings or the git remote.</summary>
    private async void OnAzureTestClick(object? sender, RoutedEventArgs e)
    {
        var token = AzureTokenBox.Text?.Trim() is { Length: > 0 } typed ? typed : Buddy.Core.Tracker.AzureToken.Read(SettingsStore.Folder);
        var repo = new Buddy.Core.Repo.GitRepo(Microsoft.Extensions.Options.Options.Create(_settings), TimeProvider.System);
        var remote = repo.IsAvailable ? await repo.GetRemoteUrlAsync() : null;
        var project = Buddy.Core.Tracker.AzureProject.FromSettings(_settings.AzureDevOps, _settings.AzureProject)
                      ?? Buddy.Core.Tracker.AzureProject.FromRemote(remote);
        if (token is null || project is null)
        {
            AzureTestResult.Text = token is null ? "Scrivi il token (PAT)." : "Il repository non è su Azure Repos: scrivi organizzazione e progetto.";
            return;
        }

        AzureTestResult.Text = "Provo…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var items = await new Buddy.Core.Tracker.AzureDevOpsClient(http, token).AssignedAsync(project);
            AzureTestResult.Text = $"✓ Collegato a {project.Project}: {items.Count} work item aperti assegnati a te"
                                   + (items.Count > 0 ? $" (es. {items[0].Key} · {items[0].Summary})." : ".");
        }
        catch (Exception ex) when (ex is Buddy.Core.Tracker.TrackerException or HttpRequestException or TaskCanceledException or UriFormatException
                                       or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            AzureTestResult.Text = $"✗ {ex.Message}";
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        CollectProjectLists();
        if (JiraTokenBox.Text?.Trim() is { Length: > 0 } jiraToken)
        {
            Buddy.Core.Tracker.JiraToken.Write(SettingsStore.Folder, jiraToken);
        }

        if (AzureTokenBox.Text?.Trim() is { Length: > 0 } azureToken)
        {
            Buddy.Core.Tracker.AzureToken.Write(SettingsStore.Folder, azureToken);
        }

        _settings.Mentor.Topics = Lines(Topics.Text);
        _settings.Claude.DoNotCommit = Lines(DoNotCommit.Text);
        _settings.Mentor.Interests = (Interests.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (PersonalityCombo.SelectedItem is Personality personality)
        {
            _settings.Mentor.Personality = personality.Key;
        }

        if (RetroDayCombo.SelectedIndex >= 0)
        {
            _settings.Mentor.RetroDay = RetroDays[RetroDayCombo.SelectedIndex].Day.ToString();
        }

        var prompt = SystemPrompt.Text?.Trim() ?? "";
        _settings.Ollama.SystemPrompt = prompt == LocalAssistant.DefaultSystemPrompt.Trim() ? "" : prompt;

        if (PermissionModeCombo.SelectedIndex >= 0)
        {
            _settings.Claude.PermissionMode = PermissionModes[PermissionModeCombo.SelectedIndex].Key;
        }

        var tools = new List<string>();
        if (AllowRead.IsChecked == true) tools.Add("Read");
        if (AllowGlob.IsChecked == true) tools.Add("Glob");
        if (AllowGrep.IsChecked == true) tools.Add("Grep");
        if (AllowWebSearch.IsChecked == true) tools.Add("WebSearch");
        if (AllowWebFetch.IsChecked == true) tools.Add("WebFetch");
        tools.AddRange(Lines(ExtraAllowedTools.Text));
        _settings.Claude.AutoAllowTools = tools.Distinct().ToList();

        // Edits to the project fields also update the active preset, so switching away and back keeps them.
        var active = _settings.Presets.FindIndex(p => p.Name == _settings.ActivePreset);
        if (active >= 0)
        {
            _settings.Presets[active] = ProjectPreset.From(_settings, _settings.ActivePreset);
        }

        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, $"Non riesco a salvare le impostazioni: {ex.Message}");
            return;
        }

        _saved = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private static List<string> Lines(string? text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
