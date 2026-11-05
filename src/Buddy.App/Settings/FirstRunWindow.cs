using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Buddy.Core;
using Buddy.Core.Mentor;

namespace Buddy.App.Settings;

/// <summary>
/// First-run wizard: the user's name, the buddy's name and personality, interests (for the off-topic chats that
/// keep company) and where the mentor keeps the journal. Built in code: a single scrolling form.
/// </summary>
public sealed class FirstRunWindow : Window
{
    private static readonly string[] CommonInterests =
    [
        "Musica", "Film e serie TV", "Libri", "Videogiochi", "Scacchi", "Giochi da tavolo", "Arte", "Fotografia",
        "Sport", "Viaggi", "Cucina", "Scienza", "Tecnologia", "Natura", "Storia", "Anime e manga",
    ];

    private readonly BuddySettings _settings;
    private readonly TextBox _userName = new();
    private readonly TextBox _buddyName = new();
    private readonly List<RadioButton> _personalities = [];
    private readonly RadioButton _customPersonality = new() { Content = "Personalizzata:" };
    private readonly TextBox _customText = new() { TextWrapping = TextWrapping.Wrap, Height = 44, AcceptsReturn = true };
    private readonly List<CheckBox> _interests = [];
    private readonly TextBox _otherInterests = new();
    private readonly TextBox _journal = new();
    private readonly CheckBox _mentor = new() { Content = "Attiva il mentore (check-in, pillole, domande, compagnia)", IsChecked = true };

    public FirstRunWindow(BuddySettings current)
    {
        _settings = SettingsStore.Clone(current);
        Title = "Benvenuto in Buddy";
        Width = 560;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Classes.Add("form");

        var form = new StackPanel { Margin = new Thickness(20) };
        form.Children.Add(new TextBlock { Text = "Ciao! Facciamo conoscenza 👋", FontSize = 20, FontWeight = FontWeight.SemiBold });
        form.Children.Add(Hint("Bastano due minuti. Tutto si può cambiare dopo da Impostazioni."));

        form.Children.Add(Heading("Come ti chiami?"));
        _userName.Text = _settings.Mentor.UserName;
        form.Children.Add(Field(_userName));

        form.Children.Add(Heading("Come vuoi chiamare il tuo buddy?"));
        _buddyName.Text = string.IsNullOrWhiteSpace(_settings.Appearance.Name) ? "Buddy" : _settings.Appearance.Name;
        form.Children.Add(Field(_buddyName));

        form.Children.Add(Heading("Che personalità vuoi che abbia?"));
        foreach (var personality in Personalities.All)
        {
            var radio = new RadioButton
            {
                Content = new TextBlock { Text = $"{personality.Label} — {personality.Tone}", TextWrapping = TextWrapping.Wrap },
                Tag = personality.Key,
                Margin = new Thickness(0, 3, 0, 3),
                IsChecked = _settings.Mentor.Personality == personality.Key,
                GroupName = "personality",
            };
            _personalities.Add(radio);
            form.Children.Add(radio);
        }

        _customPersonality.GroupName = "personality";
        _customPersonality.IsChecked = _settings.Mentor.Personality == Personalities.Custom;
        _customText.Text = _settings.Mentor.CustomPersonality;
        _customText.GotFocus += (_, _) => _customPersonality.IsChecked = true;
        form.Children.Add(_customPersonality);
        form.Children.Add(Field(_customText));

        form.Children.Add(Heading("Cosa ti piace fuori dal lavoro?"));
        form.Children.Add(Hint("Il buddy ogni tanto ti farà qualche domanda su questi argomenti, giusto per farti compagnia."));
        var grid = new WrapPanel();
        foreach (var interest in CommonInterests)
        {
            var box = new CheckBox
            {
                Content = interest,
                Width = 160,
                Margin = new Thickness(0, 3, 0, 3),
                IsChecked = _settings.Mentor.Interests.Contains(interest, StringComparer.OrdinalIgnoreCase),
            };
            _interests.Add(box);
            grid.Children.Add(box);
        }

        form.Children.Add(grid);
        _otherInterests.Text = string.Join(", ", _settings.Mentor.Interests.Where(i => !CommonInterests.Contains(i, StringComparer.OrdinalIgnoreCase)));
        form.Children.Add(Hint("Altro (separati da virgola):"));
        form.Children.Add(Field(_otherInterests));

        form.Children.Add(Heading("Dove tengo il tuo diario?"));
        form.Children.Add(Hint("Diario, profilo e pillole li scrive Claude in questa cartella (per esempio nel tuo vault Obsidian). Il buddy non scrive altrove."));
        _journal.Text = string.IsNullOrWhiteSpace(_settings.Mentor.JournalPath) ? SuggestJournal(_settings.ProjectPath) : _settings.Mentor.JournalPath;
        var browse = new Button { Content = "Sfoglia…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 4, 0, 4) };
        browse.Click += async (_, _) =>
        {
            if (await Dialogs.PickFolderAsync(this, _journal.Text) is { } folder)
            {
                _journal.Text = folder;
            }
        };
        var journalRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        journalRow.Children.Add(browse);
        journalRow.Children.Add(Field(_journal));
        form.Children.Add(journalRow);

        _mentor.IsChecked = _settings.Mentor.Enabled;
        _mentor.Margin = new Thickness(0, 12, 0, 0);
        form.Children.Add(_mentor);

        var start = new Button
        {
            Content = "Iniziamo",
            IsDefault = true,
            Classes = { "accent" },
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(18, 5, 18, 5),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        start.Click += (_, _) => Finish();
        form.Children.Add(start);

        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    /// <summary>Shows the wizard; completes with the chosen settings, or null if it was closed without finishing.</summary>
    public Task<BuddySettings?> ShowAndWaitAsync()
    {
        var closed = new TaskCompletionSource<BuddySettings?>();
        Closed += (_, _) => closed.TrySetResult(_finished ? _settings : null);
        Show();
        Activate();
        return closed.Task;
    }

    private bool _finished;

    private void Finish()
    {
        var mentor = _settings.Mentor;
        mentor.UserName = (_userName.Text ?? "").Trim();
        _settings.Appearance.Name = (_buddyName.Text ?? "").Trim() is { Length: > 0 } name ? name : "Buddy";

        if (_customPersonality.IsChecked == true && (_customText.Text ?? "").Trim().Length > 0)
        {
            mentor.Personality = Personalities.Custom;
            mentor.CustomPersonality = (_customText.Text ?? "").Trim();
        }
        else
        {
            mentor.Personality = _personalities.FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? Personalities.All[0].Key;
        }

        mentor.Interests = _interests.Where(b => b.IsChecked == true).Select(b => (string)b.Content!)
            .Concat((_otherInterests.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        mentor.JournalPath = (_journal.Text ?? "").Trim();
        mentor.Enabled = _mentor.IsChecked == true;
        _settings.FirstRunCompleted = true;
        _finished = true;
        Close();
    }

    /// <summary>"Personale/Buddy" in the root of the Obsidian vault that contains the project folder, if found.</summary>
    internal static string SuggestJournal(string projectPath)
    {
        for (var dir = string.IsNullOrWhiteSpace(projectPath) ? null : new DirectoryInfo(projectPath); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".obsidian")))
            {
                return Path.Combine(dir.FullName, "Personale", "Buddy");
            }
        }

        return Path.Combine(SettingsStore.Folder, "Diario");
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 4) };

    private static TextBlock Hint(string text) =>
        new() { Text = text, Classes = { "hint" }, FontSize = 12, Margin = new Thickness(0, 2, 0, 4) };

    private static TextBox Field(TextBox box)
    {
        box.Margin = new Thickness(0, 4, 0, 4);
        return box;
    }
}
