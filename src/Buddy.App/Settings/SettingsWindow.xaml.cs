using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Buddy.App.Pet;
using Buddy.Core;
using Buddy.Core.Llm;
using Microsoft.Win32;

namespace Buddy.App.Settings;

/// <summary>
/// Edits a copy of the settings; "Salva e riavvia" writes %APPDATA%\Buddy\appsettings.json and restarts the app,
/// which is simpler and more reliable than re-wiring every component live.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] CommonTools = ["Read", "Glob", "Grep", "WebSearch", "WebFetch"];

    private readonly BuddySettings _settings;
    private readonly OllamaClient _ollama;

    public SettingsWindow(BuddySettings current, OllamaClient ollama)
    {
        InitializeComponent();
        _settings = SettingsStore.Clone(current);
        _ollama = ollama;
        DataContext = _settings;

        FontCombo.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order().ToList();
        BuildThemeRows();
        BuildPetColorRows();

        SystemPrompt.Text = string.IsNullOrWhiteSpace(_settings.Ollama.SystemPrompt) ? LocalAssistant.DefaultSystemPrompt : _settings.Ollama.SystemPrompt;

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
        Interests.Text = string.Join(", ", _settings.Mentor.Interests);
        PersonalityCombo.ItemsSource = Buddy.Core.Mentor.Personalities.All
            .Append(new Buddy.Core.Mentor.Personality(Buddy.Core.Mentor.Personalities.Custom, "Personalizzata", ""))
            .ToList();

        Loaded += async (_, _) => await RefreshModelsAsync();
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

    private void OnPresetSaveCurrentClick(object sender, RoutedEventArgs e)
    {
        var suggested = string.IsNullOrWhiteSpace(_settings.ActivePreset) ? Path.GetFileName(_settings.ProjectPath) : _settings.ActivePreset;
        if (PromptWindow.Ask(this, "Nuovo preset", "Nome del progetto:", suggested) is not { } name)
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

    private void OnPresetLoadClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not ProjectPreset preset)
        {
            return;
        }

        preset.ApplyTo(_settings);
        RefreshBindings();
        LoadProjectLists();
        RefreshPresetList();
    }

    private void OnPresetRenameClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not ProjectPreset preset
            || PromptWindow.Ask(this, "Rinomina preset", "Nuovo nome:", preset.Name) is not { } name)
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

    private void OnPresetDeleteClick(object sender, RoutedEventArgs e)
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
        grid.RowDefinitions.Add(new RowDefinition());

        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
        var box = new TextBox { Text = get(), Width = 110, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = new FontFamily("Consolas") };
        var swatch = new Border { Width = 40, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };

        void Update()
        {
            set(box.Text.Trim());
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

    private void ApplyTheme(Buddy.Core.ThemeColors theme)
    {
        _settings.Appearance.Theme = theme;
        ThemeColors.Children.Clear();
        ThemeColors.RowDefinitions.Clear();
        BuildThemeRows();
    }

    private void OnLightThemeClick(object sender, RoutedEventArgs e) => ApplyTheme(Buddy.Core.ThemeColors.Light());

    private void OnDarkThemeClick(object sender, RoutedEventArgs e) => ApplyTheme(Buddy.Core.ThemeColors.Dark());

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

    private async Task RefreshModelsAsync()
    {
        var current = _settings.Ollama.Model;
        var models = await _ollama.ListModelsAsync(_settings.Ollama.BaseUrl);
        OllamaModels.ItemsSource = models;
        OllamaModels.Text = current;
        OllamaModels.ToolTip = models.Count == 0 ? "Ollama non raggiungibile a questo indirizzo" : null;
    }

    private void OnBrowseProjectClick(object sender, RoutedEventArgs e) =>
        BrowseFolder(_settings.ProjectPath, path => _settings.ProjectPath = path);

    private void OnBrowseClaudeDirClick(object sender, RoutedEventArgs e) =>
        BrowseFolder(_settings.Claude.WorkingDirectory, path => _settings.Claude.WorkingDirectory = path);

    /// <summary>Saves everything with the first-run flag cleared: after the restart the wizard opens again.</summary>
    private void OnRedoFirstRunClick(object sender, RoutedEventArgs e)
    {
        _settings.FirstRunCompleted = false;
        OnSaveClick(sender, e);
    }

    private void OnBrowseJournalClick(object sender, RoutedEventArgs e) =>
        BrowseFolder(_settings.Mentor.JournalPath, path => _settings.Mentor.JournalPath = path);

    private void OnBrowseWhisperClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Modelli Whisper (*.bin)|*.bin|Tutti i file|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            _settings.Voice.ModelPath = dialog.FileName;
            RefreshBindings();
        }
    }

    private void OnBrowseRepoClick(object sender, RoutedEventArgs e) =>
        BrowseFolder(_settings.Repo.Path, path => _settings.Repo.Path = path);

    private void OnBrowseSpriteFolderClick(object sender, RoutedEventArgs e) =>
        BrowseFolder(_settings.Pet.SpriteFolder, path => _settings.Pet.SpriteFolder = path);

    private void OnBrowseClaudeExeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Eseguibili (*.exe;*.cmd)|*.exe;*.cmd|Tutti i file|*.*" };
        if (File.Exists(_settings.Claude.Executable))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_settings.Claude.Executable);
        }

        if (dialog.ShowDialog(this) == true)
        {
            _settings.Claude.Executable = dialog.FileName;
            RefreshBindings();
        }
    }

    private void OnExportSpritesClick(object sender, RoutedEventArgs e)
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
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    private void OnResetPromptClick(object sender, RoutedEventArgs e) => SystemPrompt.Text = LocalAssistant.DefaultSystemPrompt;

    private void BrowseFolder(string current, Action<string> set)
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            set(dialog.FolderName);
            RefreshBindings();
        }
    }

    /// <summary>Plain settings classes do not notify changes: rebind after editing them from code.</summary>
    private void RefreshBindings()
    {
        DataContext = null;
        DataContext = _settings;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        CollectProjectLists();
        _settings.Mentor.Topics = Lines(Topics.Text);
        _settings.Mentor.Interests = Interests.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var prompt = SystemPrompt.Text.Trim();
        _settings.Ollama.SystemPrompt = prompt == LocalAssistant.DefaultSystemPrompt.Trim() ? "" : prompt;

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
            MessageBox.Show(this, $"Non riesco a salvare le impostazioni: {ex.Message}", "Buddy", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
