using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Buddy.App.Chat;
using Buddy.Core;
using Buddy.Core.Claude;
using Buddy.Core.Context;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.App.Project;

/// <summary>
/// The project window: overview, board, archive and notes of the vault, with a side panel for one item. It follows the
/// vault (a change saved in Obsidian shows up after a moment); items and notes can be edited here too, through
/// <see cref="VaultWriter"/>, which refuses to overwrite a note changed meanwhile. The flow's Claude skills
/// (/lavora, /chiudi, /rifiuta, /valuta, /smista…) are one click away; the app sends them to the chat.
/// </summary>
public partial class ProjectWindow : Window
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private static readonly (string Key, string Label, string Title)[] Views =
    [
        ("overview", "🏠  Panoramica", "Panoramica"),
        ("board", "📋  Bacheca", "Bacheca"),
        ("archive", "🗄  Archivio", "Archivio"),
        ("manualtests", "🧪  Test manuali", "Test manuali"),
        ("webtests", "🌐  Test web", "Test web"),
        ("notes", "📚  Note", "Note"),
    ];

    private readonly VaultIndex _vault;
    private readonly BuddySettings _settings;
    private readonly ClaudeUsage? _usage;
    private readonly ContextMenu _projectMenu;
    private readonly IReadOnlySet<string> _skills;
    private readonly DispatcherTimer _reload;
    private readonly DispatcherTimer _quickNoteDone;
    private ProjectBoard? _board;
    private List<NoteView> _notes = [];
    private List<CardView> _cards = [];
    private string _view = "overview";
    private CardView? _selected;
    private bool _editingCard;
    private NoteView? _editedNote;
    private bool _switchingView;
    private string? _tagFilter;
    private List<ManualDocView> _manualDocs = [];
    private ManualDocView? _shownManual;
    private List<CheckView> _shownChecks = [];
    private List<ManualTestLine> _shownLines = [];
    private readonly Dictionary<string, (DateTime Stamp, Avalonia.Media.Imaging.Bitmap? Image)> _thumbnails = [];

    /// <summary>For the XAML previewer.</summary>
    public ProjectWindow() : this(new VaultIndex(DesignData.Settings), DesignData.Settings)
    {
    }

    public ProjectWindow(VaultIndex vault, IOptions<BuddySettings> settings, ClaudeUsage? usage = null, Avalonia.Media.IImage? mascot = null)
    {
        InitializeComponent();
        _vault = vault;
        _settings = settings.Value;
        _usage = usage;
        Mascot.Source = mascot;
        Mascot.IsVisible = mascot is not null;
        // A context menu, like the pet's: a MenuFlyout on this button did not open on the user's desktop (X11).
        _projectMenu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = ProjectSwitcher };
        ProjectSwitcher.ContextMenu = _projectMenu;
        ProjectSwitcher.Click += OnProjectSwitcherClick;
        if (usage is not null)
        {
            usage.Changed += OnUsageChanged;
            Closed += (_, _) => usage.Changed -= OnUsageChanged;
        }

        ShowUsage();
        _skills = ClaudeSkills.Installed(_settings);

        Appearance.Apply(this, _settings.Appearance, font: false);
        RequestedThemeVariant = Appearance.Variant(_settings.Appearance);

        Appearance.ApplyControls(this, _settings.Appearance);

        var name = string.IsNullOrWhiteSpace(_settings.ActivePreset) ? Path.GetFileName(_settings.ProjectPath.TrimEnd('/', '\\')) : _settings.ActivePreset;
        ProjectName.Text = name;
        ProjectPath.Text = _settings.ProjectPath;
        ToolTip.SetTip(ProjectPath, _settings.ProjectPath);
        Title = $"{name} · Progetto";

        // The vault raises one event per file touched: wait for the burst to end, then read once.
        _reload = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reload.Tick += (_, _) =>
        {
            _reload.Stop();
            if (!IsEditing)
            {
                Reload(); // while editing it waits: rebuilding would throw away what is being typed (AfterEdit reloads)
            }
        };
        _quickNoteDone = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _quickNoteDone.Tick += (_, _) =>
        {
            _quickNoteDone.Stop();
            QuickNoteDone.IsVisible = false;
        };
        _vault.Changed += OnVaultChanged;
        Closed += (_, _) =>
        {
            _vault.Changed -= OnVaultChanged;
            _reload.Stop();
            _run?.Cancel(); // closing the window stops a running test
        };

        Filter.TextChanged += (_, _) => Render();
        NotesList.SelectionChanged += (_, _) => ShowNote(NotesList.SelectedItem as NoteView);
        ManualDocsList.SelectionChanged += (_, _) => ShowManualDoc(ManualDocsList.SelectedItem as ManualDocView);
        Nav.SelectionChanged += (_, _) =>
        {
            if (!_switchingView && Nav.SelectedItem is NavItem item)
            {
                ShowView(item.Key);
            }
        };
        KeyDown += OnWindowKeyDown;
        BoardView.SizeChanged += (_, _) => FitColumns();

        Reload();
        ShowView("overview");
    }

    /// <summary>"Chiedi in chat": the question to put in the chat's input.</summary>
    public event Action<string>? AskRequested;

    /// <summary>A skill button: the command to send to Claude (e.g. "/lavora T-003").</summary>
    public event Action<string>? ClaudeRequested;

    /// <summary>Generates ideas for "💡 Nuove idee" (set by the app: Claude or the local model).</summary>
    public IdeaSource? IdeaSource { get; set; }

    /// <summary>Project switcher: the project to switch to, or null for "Nuovo progetto…".</summary>
    public event Action<string?>? PresetRequested;

    /// <summary>Every card currently shown (board and archive), for the snapshot tool.</summary>
    internal IReadOnlyList<CardView> Cards => _cards;

    internal IReadOnlyList<ScriptView> Scripts => (ScriptsList.ItemsSource as IReadOnlyList<ScriptView>) ?? [];

    internal void CloseProjectMenu() => _projectMenu.Close();

    /// <summary>Opens the project switcher, for the snapshot tool: the open state and the entries.</summary>
    internal string OpenProjectMenu()
    {
        OnProjectSwitcherClick(null, new RoutedEventArgs());
        return $"open={_projectMenu.IsOpen} items={_projectMenu.Items.Count}: "
               + string.Join(", ", _projectMenu.Items.OfType<MenuItem>().Select(i => $"{i.Header}{(i.IsChecked ? " ✓" : "")}"));
    }

    private bool IsEditing => _editingCard || _editedNote is not null;

    public void Reload()
    {
        if (!_vault.IsAvailable)
        {
            _board = null;
            _notes = [];
            Render();
            ViewSubtitle.Text = $"Cartella del progetto non trovata: {_settings.ProjectPath}";
            return;
        }

        _board = ProjectBoard.Load(_vault, _settings.TaskIdPattern);
        var selectedNote = (NotesList.SelectedItem as NoteView)?.Note.RelativePath;
        _notes = _board.Notes.Select(n => new NoteView(n)).ToList();
        Render();

        // Keep the note being read selected across reloads.
        NotesList.SelectedItem = _notes.FirstOrDefault(n => n.Note.RelativePath == selectedNote)
                                 ?? (NotesList.ItemsSource as IEnumerable<NoteView>)?.FirstOrDefault();
    }

    internal void ShowView(string key)
    {
        _view = key;
        OverviewView.IsVisible = key == "overview";
        BoardPage.IsVisible = key == "board";
        ArchiveView.IsVisible = key == "archive";
        NotesView.IsVisible = key == "notes";
        ManualTestsView.IsVisible = key == "manualtests";
        WebTestsView.IsVisible = key == "webtests";
        ViewTitle.Text = Views.First(v => v.Key == key).Title;
        UpdateSubtitle();

        _switchingView = true;
        Nav.SelectedItem = (Nav.ItemsSource as IEnumerable<NavItem>)?.FirstOrDefault(n => n.Key == key);
        _switchingView = false;
    }

    private void OnVaultChanged() => Dispatcher.UIThread.Post(() =>
    {
        _reload.Stop();
        _reload.Start();
    });

    // ---- Rendering --------------------------------------------------------------------------------------------

    private void Render()
    {
        var filter = Filter.Text?.Trim() ?? "";
        var today = DateOnly.FromDateTime(DateTime.Today);
        var columns = _board?.Columns ?? [];

        List<CardView> CardsOf(string key)
        {
            if (columns.FirstOrDefault(c => c.Key == key) is not { } column)
            {
                return [];
            }

            var cards = column.Items.Where(i => i.Matches(filter))
                .Where(i => _tagFilter is null || !Flow.Active.Contains(key) || string.Equals(i.Field("tag"), _tagFilter, StringComparison.OrdinalIgnoreCase))
                .Select(i => new CardView(i, column, today));
            return key switch
            {
                "todo" => cards.OrderBy(c => c.Priority).ThenBy(c => c.Item.Date ?? DateOnly.MaxValue).ToList(),
                "valutare" or "attesa" => cards.OrderBy(c => c.Item.Due ?? DateOnly.MaxValue).ToList(),
                "fatti" or "rifiutati" => cards.OrderByDescending(c => c.Item.Date ?? DateOnly.MinValue).ToList(),
                _ => cards.ToList(),
            };
        }

        var byKey = columns.ToDictionary(c => c.Key, c => CardsOf(c.Key));
        _cards = byKey.Values.SelectMany(c => c).ToList();
        int Total(string key) => columns.FirstOrDefault(c => c.Key == key)?.Items.Count ?? 0;
        List<CardView> Shown(string key) => byKey.GetValueOrDefault(key) ?? [];

        // Board
        BoardColumns.ItemsSource = columns.Where(c => Flow.Active.Contains(c.Key))
            .Select(c => new ColumnView(c, Shown(c.Key), Flow.ColumnActions(c.Key, _skills)))
            .ToList();

        FitColumns();

        // Tag filter bar: every tag on the board, with its count.
        var boardTags = columns.Where(c => Flow.Active.Contains(c.Key)).SelectMany(c => c.Items)
            .Select(i => i.Field("tag")).OfType<string>()
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new TagOption(g.Key, Tags.Brush(g.Key), string.Equals(g.Key, _tagFilter, StringComparison.OrdinalIgnoreCase), g.Count()))
            .ToList();
        TagBar.ItemsSource = boardTags.Count == 0
            ? []
            : boardTags.Prepend(new TagOption("Tutti", (Avalonia.Media.IBrush)Resources["Muted"]!, _tagFilter is null)).ToList();

        // Archive
        var done = Shown("fatti");
        DoneList.ItemsSource = done;
        var cost = columns.FirstOrDefault(c => c.Key == "fatti")?.Items.Sum(i => i.Cost ?? 0) ?? 0;
        var reworks = columns.FirstOrDefault(c => c.Key == "fatti")?.Items.Sum(i => i.Rework) ?? 0;
        DoneSummary.Text = Total("fatti") == 0
            ? "Ancora nessun task chiuso"
            : $"{Total("fatti")} task chiusi · costo Claude {cost.ToString("0.00", Italian)} $ · {reworks} rifacimenti";
        RejectedList.ItemsSource = Shown("rifiutati");
        RejectedSummary.Text = Total("rifiutati") == 0 ? "Nessuna idea scartata" : $"{Total("rifiutati")} idee scartate: Claude non le riproporrà";

        // Overview
        var unlocked = columns.FirstOrDefault(c => c.Key == "attesa")?.Items.Count(i => i.IsDue(today)) ?? 0;
        var overdue = columns.FirstOrDefault(c => c.Key == "valutare")?.Items.Count(i => i.IsDue(today)) ?? 0;
        var urgent = Shown("todo").Count(c => c.Priority == 1);
        Tiles.ItemsSource = columns.Where(c => c.Key != "rifiutati").Select(c => new TileView(
            c.Key,
            $"{c.Icon} {c.Title}",
            c.Items.Count.ToString(CultureInfo.InvariantCulture),
            c.Key switch
            {
                "appunti" when c.Items.Count > 0 => "da smistare",
                "valutare" when overdue > 0 => $"⏰ {overdue} da rivedere",
                "todo" when urgent > 0 => $"🔥 {urgent} priorità alta",
                "attesa" when unlocked > 0 => $"🔓 {unlocked} sbloccati",
                "controllare" when c.Items.Count > 0 => "aspettano te",
                "fatti" when cost > 0 => $"{cost.ToString("0.00", Italian)} $ di Claude",
                _ => "",
            },
            Flow.Accent(c.Key),
            c.Key == "fatti" ? "archive" : "board")).ToList();

        var attention = new List<AttentionView>();
        attention.AddRange(Shown("controllare").Select(c => new AttentionView(c, "🔍 Da rivedere e chiudere")));
        attention.AddRange(Shown("valutare").Where(c => c.Item.IsDue(today)).Select(c => new AttentionView(c, "⏰ Valutazione scaduta")));
        attention.AddRange(Shown("attesa").Where(c => c.Item.IsDue(today)).Select(c => new AttentionView(c, "🔓 Non aspetta più")));
        attention.AddRange(Shown("todo").Where(c => c.Priority == 1).Select(c => new AttentionView(c, "🔥 Priorità alta")));
        AttentionList.ItemsSource = attention;
        var inbox = Total("appunti");
        SortInboxButton.IsVisible = inbox > 0 && _skills.Contains("smista");
        SortInboxButton.Content = $"🗂 Smista {(inbox == 1 ? "l'appunto" : $"i {inbox} appunti")} con Claude";
        NothingUrgent.IsVisible = attention.Count == 0 && !SortInboxButton.IsVisible;

        // Flow bar: how the items spread over the steps, and how much is done.
        var segments = columns.Where(c => c.Items.Count > 0 && c.Key != "rifiutati")
            .Select(c => new SegmentView(c.Title, c.Items.Count, Flow.Accent(c.Key))).ToList();
        FlowBar.ColumnDefinitions.Clear();
        FlowBar.Children.Clear();
        foreach (var (segment, index) in segments.Select((x, i) => (x, i)))
        {
            FlowBar.ColumnDefinitions.Add(new ColumnDefinition(segment.Width));
            var block = new Border { Background = segment.Accent, Margin = new Thickness(index == 0 ? 0 : 2, 0, 0, 0) };
            ToolTip.SetTip(block, segment.Text);
            Grid.SetColumn(block, index);
            FlowBar.Children.Add(block);
        }

        FlowLegend.ItemsSource = segments;
        var openItems = Flow.Active.Where(k => k != "appunti").Sum(Total);
        CompletionText.Text = Total("fatti") + openItems == 0
            ? ""
            : $"{Total("fatti") * 100 / (Total("fatti") + openItems)}% dei task chiusi";
        WeeksChart.ItemsSource = _board is null ? [] : Activity.Weeks(_board, today);

        StatusList.ItemsSource = (_board?.Status ?? []).Select(s => new StatusView(s.Label, s.Value)).ToList();
        StatusPanel.IsVisible = _board?.Status.Count > 0;
        RecentList.ItemsSource = done.Take(3).ToList();
        RecentPanel.IsVisible = done.Count > 0;

        // Notes
        var notes = filter.Length == 0
            ? _notes
            : _notes.Where(n => n.Note.RelativePath.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                || n.Note.Content.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var currentNote = NotesList.SelectedItem;
        NotesList.ItemsSource = notes;
        if (currentNote is NoteView view && notes.Contains(view))
        {
            NotesList.SelectedItem = view;
        }

        RenderTests(filter);

        // Side bar
        var open = Flow.Active.Sum(Total);
        _switchingView = true;
        Nav.ItemsSource = Views.Select(v => new NavItem(v.Key, v.Label, v.Key switch
        {
            "board" when open > 0 => open.ToString(CultureInfo.InvariantCulture),
            "archive" when Total("fatti") + Total("rifiutati") > 0 => (Total("fatti") + Total("rifiutati")).ToString(CultureInfo.InvariantCulture),
            "notes" => _notes.Count.ToString(CultureInfo.InvariantCulture),
            "manualtests" when _manualDocs.Count > 0 => $"{_manualDocs.Count(d => d.IsComplete)}/{_manualDocs.Count}",
            "webtests" when ScriptsList.ItemCount > 0 => ScriptsList.ItemCount.ToString(CultureInfo.InvariantCulture),
            _ => "",
        })).ToList();
        Nav.SelectedItem = (Nav.ItemsSource as IEnumerable<NavItem>)?.FirstOrDefault(n => n.Key == _view);
        _switchingView = false;
        UpdateSubtitle();

        // The item in the side panel, rebuilt with the board.
        if (_selected is { } selected && !_editingCard)
        {
            if (_cards.FirstOrDefault(c => c.SameAs(selected)) is { } again)
            {
                Select(again);
            }
            else if (filter.Length == 0)
            {
                CloseDetail(); // gone from the vault (closed, moved, deleted)
            }
        }
    }

    /// <summary>Columns share the board's width, never narrower than <see cref="ColumnMinWidth"/> (then it scrolls).</summary>
    private void FitColumns()
    {
        var count = (BoardColumns.ItemsSource as IReadOnlyCollection<ColumnView>)?.Count ?? 0;
        if (count > 0 && BoardView.Bounds.Width > 0)
        {
            Resources["ColumnWidth"] = Math.Max(ColumnMinWidth, Math.Floor((BoardView.Bounds.Width - count * 10) / count));
        }
    }

    private const double ColumnMinWidth = 200;

    private void UpdateSubtitle()
    {
        if (_board is null)
        {
            return;
        }

        var today = DateTime.Today.ToString("dddd d MMMM", Italian);
        ViewSubtitle.Text = _view switch
        {
            "overview" => $"{Greeting()} · {today} · cosa c'è da fare e cosa aspetta te",
            "board" => "Il flusso, da sinistra a destra. Click su una voce per i dettagli e le azioni con Claude.",
            "archive" => "Task chiusi e idee scartate",
            "manualtests" => "Le checklist da provare a mano: spunta i casi qui, finiscono nel file come - [x]",
            "webtests" => "Gli script del browser e gli screenshot delle verifiche",
            _ => $"{_notes.Count} note in {_settings.ProjectPath}",
        };
    }

    private string Greeting()
    {
        var hour = DateTime.Now.Hour;
        var hello = hour < 13 ? "Buongiorno" : hour < 18 ? "Buon pomeriggio" : "Buonasera";
        return string.IsNullOrWhiteSpace(_settings.Mentor.UserName) ? hello : $"{hello} {_settings.Mentor.UserName}";
    }

    // ---- Project switcher and Claude usage --------------------------------------------------------------------

    private void OnProjectSwitcherClick(object? sender, RoutedEventArgs e)
    {
        BuildProjectMenu();
        _projectMenu.Open(ProjectSwitcher);
    }

    private void BuildProjectMenu()
    {
        _projectMenu.Items.Clear();
        foreach (var preset in _settings.Presets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var current = string.Equals(preset.Name, _settings.ActivePreset, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = preset.Name,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = current,
            };
            ToolTip.SetTip(item, preset.ProjectPath);
            if (!current)
            {
                item.Click += (_, _) => PresetRequested?.Invoke(preset.Name);
            }

            _projectMenu.Items.Add(item);
        }

        if (_settings.Presets.Count > 0)
        {
            _projectMenu.Items.Add(new Separator());
        }

        var create = new MenuItem { Header = "＋ Nuovo progetto…" };
        create.Click += (_, _) => PresetRequested?.Invoke(null);
        _projectMenu.Items.Add(create);
    }

    private void OnUsageChanged() => Dispatcher.UIThread.Post(ShowUsage);

    private void ShowUsage()
    {
        UsageCard.IsVisible = _usage is not null;
        if (_usage is not { } usage)
        {
            return;
        }

        var today = usage.Day == DateOnly.FromDateTime(DateTime.Today);
        UsageCost.Text = $"{(today ? usage.TodayCost : 0).ToString("0.00", Italian)} $";
        UsageTurns.Text = today && usage.TodayTurns > 0 ? $"{usage.TodayTurns} turni" : "";
        ToolTip.SetTip(UsageCard, usage.Describe().Replace("**", ""));
        var limits = usage.Limits.Select(l => new LimitView(
            l.Label, l.ShortStatus + (l.ResetsAt is { } r ? $" · {r.ToLocalTime():HH:mm}" : ""),
            (l.Utilization ?? 0) * 100, l.IsWarning || l.IsBlocked, l.Utilization is not null)).ToList();
        UsageLimits.ItemsSource = limits;
        UsageNoLimits.IsVisible = limits.Count == 0;
    }

    // ---- Side panel -------------------------------------------------------------------------------------------

    internal void Select(CardView card)
    {
        if (_selected is { } previous && previous != card)
        {
            previous.IsSelected = false;
        }

        _selected = card;
        card.IsSelected = true;
        Detail.DataContext = card;
        Detail.IsVisible = true;

        var actions = card.HasId ? Flow.CardActions(card.Column.Key, _skills).ToList() : [];
        if (card.HasId && card.Column.Key is not ("fatti" or "rifiutati"))
        {
            actions.Add(new FlowAction("💬 Chiedi in chat", "", "", "Apre la chat con una domanda su questa voce"));
        }

        ClaudeActions.ItemsSource = actions;
        ClaudeActionsPanel.IsVisible = actions.Count > 0;

        // Tag picker: the common tags plus any already used in the vault; the item's own is highlighted.
        var used = (_board?.Columns ?? []).SelectMany(c => c.Items).Select(i => i.Field("tag")).OfType<string>();
        DetailTags.ItemsSource = Tags.Defaults.Select(d => d.Name)
            .Concat(used.Where(u => !Tags.Defaults.Any(d => d.Name.Equals(u, StringComparison.OrdinalIgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => new TagOption(t, Tags.Brush(t), string.Equals(t, card.Tag, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        TagRemoveButton.IsVisible = card.HasTag;
        TagPanel.IsVisible = card.Item.Raw.Length > 0;

        var manual = card.Id is { } taskId ? _manualDocs.FirstOrDefault(d => d.TaskId == taskId) : null;
        CardManualTestsButton.IsVisible = manual is not null;
        CardManualTestsButton.Content = manual is null ? "" : $"🧪 Test manuali {manual.Doc.Done}/{manual.Doc.Total}";
    }

    private void CloseDetail()
    {
        if (_selected is { } selected)
        {
            selected.IsSelected = false;
        }

        _selected = null;
        StopEditingCard();
        Detail.IsVisible = false;
    }

    private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && sender is Control { DataContext: CardView card } && !_editingCard)
        {
            if (card == _selected)
            {
                CloseDetail();
            }
            else
            {
                Select(card);
            }
        }
    }

    private void OnCloseDetailClick(object? sender, RoutedEventArgs e) => CloseDetail();

    private async void OnCardActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: FlowAction action } || _selected is not { Id: { } id })
        {
            return;
        }

        if (action.Skill.Length == 0)
        {
            AskRequested?.Invoke($"A che punto è {id}? ");
            return;
        }

        var input = "";
        if (action.Ask is { } question)
        {
            if (await Settings.Dialogs.AskAsync(this, action.Label.TrimEnd('…'), question) is not { } answer)
            {
                return;
            }

            input = answer;
        }

        ClaudeRequested?.Invoke(action.Command.Replace("{id}", id).Replace("{input}", input).Trim());
    }

    private void OnColumnActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: FlowAction action })
        {
            ClaudeRequested?.Invoke(action.Command);
        }
    }

    private void OnTagFilterClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TagOption tag })
        {
            _tagFilter = tag.Name == "Tutti" || tag.IsSelected ? null : tag.Name;
            Render();
        }
    }

    private async void OnTagPickClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TagOption tag })
        {
            await SetTagAsync(tag.IsSelected ? null : tag.Name);
        }
    }

    private async void OnTagOtherClick(object? sender, RoutedEventArgs e)
    {
        if (await Settings.Dialogs.AskAsync(this, "Tag", "Nome del tag (es. sicurezza, performance):", _selected?.Tag ?? "") is { } name)
        {
            await SetTagAsync(name.ToLowerInvariant());
        }
    }

    private async void OnTagRemoveClick(object? sender, RoutedEventArgs e) => await SetTagAsync(null);

    /// <summary>Writes [tag:: name] on the item's first line in its note (null removes it).</summary>
    private async Task SetTagAsync(string? tag)
    {
        if (_selected is not { } card || card.Item.Raw.Length == 0)
        {
            return;
        }

        if (await WriteAsync(() => VaultWriter.ReplaceBlock(FullPath(card.Column.NotePath), card.Item.Raw, ItemFields.Set(card.Item.Raw, "tag", tag))))
        {
            AfterEdit();
        }
    }

    private async void OnIdeasClick(object? sender, RoutedEventArgs e)
    {
        if (IdeaSource is null || _board is null)
        {
            return;
        }

        if (_board.Columns.FirstOrDefault(c => c.Key == "appunti") is not { } inbox)
        {
            await Settings.Dialogs.ShowMessageAsync(this, "Nel vault non c'è la nota «To-do Appunti»: creala in Obsidian e riprova.");
            return;
        }

        var scheda = _vault.Notes.Where(n => n.Title.Equals("Scheda progetto", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.RelativePath.Length).FirstOrDefault()?.Content;
        var window = new IdeasWindow(_board, scheda, FullPath(inbox.NotePath), _settings, IdeaSource);
        window.SortRequested += () => ClaudeRequested?.Invoke("/smista");
        await window.ShowDialog(this);
        AfterEdit();
    }

    private void OnSortInboxClick(object? sender, RoutedEventArgs e) => ClaudeRequested?.Invoke("/smista");

    private void OnTileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TileView tile })
        {
            ShowView(tile.Target);
        }
    }

    // ---- Tests ------------------------------------------------------------------------------------------------

    private void RenderTests(string filter)
    {
        var titles = (_board?.Columns ?? []).SelectMany(c => c.Items).Where(i => i.Id is not null)
            .GroupBy(i => i.Id!).ToDictionary(g => g.Key, g => g.First().Title.Replace("`", ""));

        // Manual tests
        _manualDocs = _vault.IsAvailable
            ? TestNotes.ManualTests(_vault, _settings.TaskIdPattern)
                .Select(d => new ManualDocView(d, d.TaskId is { } id ? titles.GetValueOrDefault(id) : null))
                .Where(d => filter.Length == 0 || d.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                            || d.Doc.Sections.SelectMany(s => s.Lines).Any(l => l.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
                .ToList()
            : [];
        var selectedPath = (ManualDocsList.SelectedItem as ManualDocView)?.Doc.RelativePath ?? _shownManual?.Doc.RelativePath;
        ManualDocsList.ItemsSource = _manualDocs;
        ManualDocsList.SelectedItem = _manualDocs.FirstOrDefault(d => d.Doc.RelativePath == selectedPath) ?? _manualDocs.FirstOrDefault();
        NoManualTests.IsVisible = _manualDocs.Count == 0;
        ((Control)ManualDocsList.Parent!).IsVisible = _manualDocs.Count > 0;
        ManualDocPanel.IsVisible = _manualDocs.Count > 0;
        ShowManualDoc(ManualDocsList.SelectedItem as ManualDocView);

        // Web tests
        var scripts = _vault.IsAvailable ? TestNotes.Scripts(_vault).Select(sc => new ScriptView(sc))
            .Where(sc => filter.Length == 0 || sc.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                         || sc.Covers?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true).ToList() : [];
        ScriptsList.ItemsSource = scripts;
        NoScripts.IsVisible = scripts.Count == 0;

        var shots = TestNotes.Screenshots(_settings.ProjectPath)
            .Where(sh => filter.Length == 0 || sh.Caption.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                         || sh.TaskId?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        ShotGroups.ItemsSource = shots
            .GroupBy(sh => sh.TaskId)
            .Select(g => new ShotGroupView(
                g.Key is { } id ? (titles.GetValueOrDefault(id) is { } title ? $"{id} · {title}" : id) : "Altre immagini",
                g.OrderBy(sh => sh.Step ?? int.MaxValue).ThenBy(sh => sh.Caption).Select(sh => new ShotView(sh, Thumbnail(sh.FullPath))).ToList()))
            .ToList();
        NoShots.IsVisible = shots.Count == 0;
    }

    /// <summary>A small copy of a screenshot, decoded once per file version.</summary>
    private Avalonia.Media.Imaging.Bitmap? Thumbnail(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_thumbnails.TryGetValue(path, out var cached) && cached.Stamp == stamp)
        {
            return cached.Image;
        }

        Avalonia.Media.Imaging.Bitmap? image = null;
        try
        {
            using var stream = File.OpenRead(path);
            image = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 460);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // Not an image Avalonia can read: the caption still shows.
        }

        _thumbnails[path] = (stamp, image);
        return image;
    }

    private void ShowManualDoc(ManualDocView? doc)
    {
        // Same note, same lines (e.g. the reload after ticking a box): keep the view and its scroll position.
        var key = doc is null ? "" : doc.Doc.RelativePath + "\n" + string.Join('\n', doc.Doc.Sections.SelectMany(s => s.Lines).Select(l => l.Raw));
        var shownKey = _shownManual is null ? "" : _shownManual.Doc.RelativePath + "\n" + string.Join('\n', ShownRawLines());
        if (doc is not null && key == shownKey)
        {
            _shownManual = doc;
            return;
        }

        _shownManual = doc;
        if (doc is null)
        {
            return;
        }

        ManualTitle.Text = doc.Title;
        ManualIntro.Markdown = NoteLinks.ToMarkdown(doc.Doc.Intro);
        ManualIntro.IsVisible = doc.Doc.Intro.Length > 0;
        ManualUpdateButton.IsVisible = doc.TaskId is not null && _skills.Contains("test-manuali");
        _shownChecks = [];
        _shownLines = doc.Doc.Sections.SelectMany(s => s.Lines).ToList();
        ManualSections.ItemsSource = doc.Doc.Sections.Select(section =>
        {
            var items = new List<object>();
            var text = new List<string>();
            void Flush()
            {
                if (text.Count > 0 && string.Join('\n', text).Trim() is { Length: > 0 } markdown)
                {
                    items.Add(new ManualTextView(NoteLinks.ToMarkdown(markdown)));
                }

                text.Clear();
            }

            foreach (var line in section.Lines)
            {
                if (line.IsCheck)
                {
                    Flush();
                    var check = new CheckView(line, (c, done) => Tick(doc, c, done));
                    _shownChecks.Add(check);
                    items.Add(check);
                }
                else
                {
                    text.Add(line.Raw);
                }
            }

            Flush();
            var cases = section.Lines.Where(l => l.IsCheck && !l.IsStruck).ToList();
            return new ManualSectionView(section.Title, cases.Count == 0 ? "" : $"{cases.Count(l => l.Checked)}/{cases.Count}", items);
        }).ToList();
        UpdateManualProgress();
        ManualScroller.Offset = new Vector(0, 0);
    }

    /// <summary>The raw lines of the shown checklist as they are now (ticks applied).</summary>
    private IEnumerable<string> ShownRawLines() =>
        _shownLines.Select(l => _shownChecks.FirstOrDefault(c => c.Line.Index == l.Index)?.Line.Raw ?? l.Raw);

    private bool Tick(ManualDocView doc, CheckView check, bool done)
    {
        try
        {
            VaultWriter.SetCheckbox(FullPath(doc.Doc.RelativePath), check.Line.Index, check.Line.Raw, done);
            Dispatcher.UIThread.Post(UpdateManualProgress);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = Settings.Dialogs.ShowMessageAsync(this, ex.Message);
            Reload();
            return false;
        }
    }

    private void UpdateManualProgress()
    {
        var cases = _shownChecks.Where(c => !c.IsStruck).ToList();
        var done = cases.Count(c => c.IsChecked);
        var redo = cases.Count(c => c.IsRedo && !c.IsChecked);
        ManualBar.Value = cases.Count == 0 ? 0 : done * 100.0 / cases.Count;
        ManualBar.Classes.Set("done", cases.Count > 0 && done == cases.Count);
        ManualProgress.Text = cases.Count == 0 ? "nessun caso"
            : $"{done}/{cases.Count} casi" + (redo > 0 ? $" · 🔁 {redo} da rifare" : "") + (done == cases.Count ? " · ✅ completato" : "");
    }

    private void OnManualUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_shownManual?.TaskId is { } id)
        {
            ClaudeRequested?.Invoke($"/test-manuali {id}");
        }
    }

    private void OnManualEditClick(object? sender, RoutedEventArgs e)
    {
        if (_shownManual is { } doc && OpenInNotesView(doc.Doc.RelativePath))
        {
            OnEditNoteClick(sender, e);
        }
    }

    private void OnManualObsidianClick(object? sender, RoutedEventArgs e)
    {
        if (_shownManual is { } doc)
        {
            OpenInObsidian(doc.Doc.RelativePath);
        }
    }

    private void OnCardManualTestsClick(object? sender, RoutedEventArgs e)
    {
        if (_selected?.Id is { } id && _manualDocs.FirstOrDefault(d => d.TaskId == id) is { } doc)
        {
            ShowView("manualtests");
            ManualDocsList.SelectedItem = doc;
        }
    }

    // ---- Running a web test ---------------------------------------------------------------------------------

    private CancellationTokenSource? _run;
    private WebTestResult? _lastRun;
    private WebTestScript? _lastScript;
    private DispatcherTimer? _runClock;

    private async void OnRunScriptClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ScriptView view })
        {
            await RunScriptAsync(view);
        }
    }

    /// <summary>Runs a saved script and records the outcome in the "Test web" note (also used by the snapshot tool).</summary>
    internal async Task RunScriptAsync(ScriptView view)
    {

        if (_run is not null)
        {
            await Settings.Dialogs.ShowMessageAsync(this, "C'è già un test in corso: aspetta che finisca o fermalo.");
            return;
        }

        if (WebTestRun.ScriptsFolder(_vault) is not { } folder || !Directory.Exists(folder))
        {
            await Settings.Dialogs.ShowMessageAsync(this,
                "Non trovo la cartella degli script: nella Scheda progetto, sezione Test web, serve la riga "
                + "«Cartella degli script di test: `~/percorso/`» con una cartella che esiste.");
            return;
        }

        var script = view.Script;
        var command = WebTestRun.CommandOf(script);
        _lastScript = script;
        _lastRun = null;
        RunPanel.IsVisible = true;
        RunPanel.BorderBrush = Flow.Accent("attesa");
        RunTitle.Text = $"▶ {script.Name}";
        RunOutput.Text = $"$ {command}   (in {folder})\n";
        RunStopButton.IsVisible = true;
        RunAskButton.IsVisible = false;
        RunCloseButton.IsVisible = false;
        WebTestsView.Offset = new Vector(0, 0);

        var started = DateTime.Now;
        _runClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runClock.Tick += (_, _) => RunStatus.Text = $"in corso… {(DateTime.Now - started):mm\\:ss}";
        _runClock.Start();
        RunStatus.Text = "in corso…";

        _run = new CancellationTokenSource();
        WebTestResult result;
        try
        {
            result = await WebTestRun.RunAsync(command, folder,
                line => Dispatcher.UIThread.Post(() => AppendRunOutput(line)), _run.Token);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            result = new WebTestResult(WebTestOutcome.Failed, null, null, DateTime.Now - started, [ex.Message]);
            AppendRunOutput(ex.Message);
        }
        finally
        {
            _run.Dispose();
            _run = null;
            _runClock.Stop();
        }

        _lastRun = result;
        ShowRunResult(result);
        if (result.Outcome != WebTestOutcome.Stopped && script.Raw.Length > 0)
        {
            // Record the run in the "Test web" note, as /verifica-web does.
            await WriteAsync(() => VaultWriter.ReplaceBlock(FullPath(script.NotePath), script.Raw,
                WebTestRun.WithLastRun(script.Raw, result, DateOnly.FromDateTime(DateTime.Today))));
            AfterEdit();
        }
    }

    private void AppendRunOutput(string line)
    {
        var text = (RunOutput.Text ?? "") + line + "\n";
        if (text.Length > 200_000)
        {
            text = text[^150_000..]; // keep the end: the summary is what matters
        }

        RunOutput.Text = text;
        RunOutput.CaretIndex = text.Length; // follow the output
    }

    private void ShowRunResult(WebTestResult result)
    {
        var seconds = $"{result.Elapsed.TotalSeconds:0} s";
        (RunStatus.Text, RunPanel.BorderBrush) = result.Outcome switch
        {
            WebTestOutcome.Passed => ($"✅ superato{(result.Passed is { } p ? $" · {p} test" : "")} in {seconds}", Flow.Accent("fatti")),
            WebTestOutcome.Failed => ($"❌ fallito{(result.Failed is > 0 ? $" · {result.Failed} su {(result.Passed ?? 0) + result.Failed}" : "")} in {seconds}", Flow.Accent("rifiutati")),
            _ => ($"■ fermato dopo {seconds}", Flow.Accent("appunti")),
        };
        RunTitle.Text = $"{(result.Outcome == WebTestOutcome.Passed ? "✅" : result.Outcome == WebTestOutcome.Failed ? "❌" : "■")} {_lastScript?.Name}";
        RunStopButton.IsVisible = false;
        RunCloseButton.IsVisible = true;
        RunAskButton.IsVisible = result.Outcome == WebTestOutcome.Failed;
    }

    private void OnStopRunClick(object? sender, RoutedEventArgs e) => _run?.Cancel();

    private void OnCloseRunClick(object? sender, RoutedEventArgs e) => RunPanel.IsVisible = false;

    /// <summary>A failed run goes to Claude with the end of its output (the errors and the summary).</summary>
    private void OnAskAboutRunClick(object? sender, RoutedEventArgs e)
    {
        if (_lastRun is not { } run || _lastScript is not { } script)
        {
            return;
        }

        var tail = string.Join("\n", run.Output.TakeLast(120));
        ClaudeRequested?.Invoke(
            $"Il test web «{script.Name}» ({WebTestRun.CommandOf(script)}) è fallito. Dimmi la causa probabile e se è un problema "
            + $"dell'app o dello script; non modificare niente senza chiedere. Fine dell'output:\n```\n{tail}\n```");
    }

    private void OnWebNoteClick(object? sender, RoutedEventArgs e) => OpenInNotesView("Test web");

    private void OnShotClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ShotView shot })
        {
            Shell.Open(shot.Shot.FullPath);
        }
    }

    // ---- Editing ----------------------------------------------------------------------------------------------

    private string FullPath(string relativePath) => Path.Combine(_settings.ProjectPath, relativePath);

    /// <summary>Runs a write to the vault; a refusal (note changed meanwhile) or an I/O error is shown, not thrown.</summary>
    private async Task<bool> WriteAsync(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Settings.Dialogs.ShowMessageAsync(this, ex.Message);
            return false;
        }
    }

    /// <summary>After an edit: show the result now, plus anything that changed in the vault while editing.</summary>
    private void AfterEdit()
    {
        if (!IsEditing)
        {
            Reload();
        }
    }

    private async void OnQuickNoteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
        var text = string.Join(' ', (QuickNote.Text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return;
        }

        if (_board?.Columns.FirstOrDefault(c => c.Key == "appunti") is not { } inbox)
        {
            await Settings.Dialogs.ShowMessageAsync(this, "Nel vault non c'è la nota «To-do Appunti»: creala in Obsidian e riprova.");
            return;
        }

        // Same format as the notes written by hand: "- 2026-10-26 · text".
        if (await WriteAsync(() => VaultWriter.AppendItem(FullPath(inbox.NotePath), "Appunti", $"- {DateTime.Today:yyyy-MM-dd} · {text}")))
        {
            QuickNote.Text = "";
            QuickNoteDone.IsVisible = true;
            _quickNoteDone.Stop();
            _quickNoteDone.Start();
            AfterEdit();
        }
    }

    internal void BeginEditSelected()
    {
        if (_selected is not { } card || card.Item.Raw.Length == 0)
        {
            return;
        }

        _editingCard = true;
        CardEditor.Text = card.Item.Raw;
        DetailEdit.IsVisible = true;
        DetailRead.IsVisible = false;
        CardEditor.Focus();
    }

    private void StopEditingCard()
    {
        _editingCard = false;
        DetailEdit.IsVisible = false;
        DetailRead.IsVisible = true;
    }

    private void OnEditCardClick(object? sender, RoutedEventArgs e) => BeginEditSelected();

    private void OnCancelCardClick(object? sender, RoutedEventArgs e)
    {
        StopEditingCard();
        AfterEdit();
    }

    private async void OnSaveCardClick(object? sender, RoutedEventArgs e) => await SaveCardAsync();

    private async Task SaveCardAsync()
    {
        if (_selected is not { } card)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CardEditor.Text))
        {
            await Settings.Dialogs.ShowMessageAsync(this, "Il testo è vuoto: per togliere la voce usa 🗑 Elimina.");
            return;
        }

        if (await WriteAsync(() => VaultWriter.ReplaceBlock(FullPath(card.Column.NotePath), card.Item.Raw, CardEditor.Text)))
        {
            StopEditingCard();
            AfterEdit();
        }
    }

    private async void OnDeleteCardClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } card)
        {
            return;
        }

        var name = card.Id is { } id ? $"{id} · {card.Title}" : card.Title;
        if (await Settings.Dialogs.ConfirmAsync(this, $"Tolgo «{name}» da {Path.GetFileNameWithoutExtension(card.Column.NotePath)}?", "Elimina")
            && await WriteAsync(() => VaultWriter.ReplaceBlock(FullPath(card.Column.NotePath), card.Item.Raw, "")))
        {
            CloseDetail();
            AfterEdit();
        }
    }

    /// <summary>Ctrl+Enter saves, Esc cancels.</summary>
    private async void OnCardEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            await SaveCardAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            StopEditingCard();
            AfterEdit();
        }
    }

    // ---- Notes ------------------------------------------------------------------------------------------------

    private void ShowNote(NoteView? note)
    {
        if (_editedNote is not null && note != _editedNote)
        {
            StopEditingNote();
        }

        NoteTitle.Text = note?.Title ?? "";
        NoteViewer.Markdown = note?.Markdown ?? "";
        NoteScroller.Offset = new Vector(0, 0);

        // Notes that link here ([[Title]], [[Folder/Title|alias]]).
        var backlinks = note is null
            ? []
            : _notes.Where(n => n != note && System.Text.RegularExpressions.Regex.IsMatch(n.Note.Content,
                    $@"\[\[(?:[^\]|]*/)?{System.Text.RegularExpressions.Regex.Escape(note.Title)}(?:[#|][^\]]*)?\]\]",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        Backlinks.ItemsSource = backlinks;
        BacklinksPanel.IsVisible = backlinks.Count > 0;
    }

    /// <summary>Shows a note in the notes view, by title (links between notes) or path.</summary>
    private bool OpenInNotesView(string titleOrPath)
    {
        var note = _notes.FirstOrDefault(n => string.Equals(n.Note.RelativePath, titleOrPath, StringComparison.OrdinalIgnoreCase))
                   ?? _notes.FirstOrDefault(n => string.Equals(n.Title, titleOrPath, StringComparison.OrdinalIgnoreCase));
        if (note is null)
        {
            return false;
        }

        Filter.Text = "";
        ShowView("notes");
        NotesList.SelectedItem = note;
        NotesList.ScrollIntoView(note);
        return true;
    }

    private void OnBacklinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: NoteView note })
        {
            NotesList.SelectedItem = note;
            NotesList.ScrollIntoView(note);
        }
    }

    private void OnEditNoteClick(object? sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is not NoteView note)
        {
            return;
        }

        _editedNote = note;
        NoteEditor.Text = note.Note.Content.Replace("\r\n", "\n");
        SetNoteEditing(true);
        NoteEditor.Focus();
    }

    private async void OnSaveNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_editedNote is { } note
            && await WriteAsync(() => VaultWriter.Save(FullPath(note.Note.RelativePath), note.Note.Content, NoteEditor.Text ?? "")))
        {
            StopEditingNote();
        }
    }

    private void OnCancelNoteClick(object? sender, RoutedEventArgs e) => StopEditingNote();

    private void StopEditingNote()
    {
        _editedNote = null;
        SetNoteEditing(false);
        AfterEdit();
    }

    private void SetNoteEditing(bool editing)
    {
        NoteEditor.IsVisible = editing;
        NoteScroller.IsVisible = !editing;
        EditNoteButton.IsVisible = !editing;
        SaveNoteButton.IsVisible = editing;
        CancelNoteButton.IsVisible = editing;
        NotesList.IsEnabled = !editing;
    }

    // ---- Obsidian, links, keys --------------------------------------------------------------------------------

    private void OpenInObsidian(string relativePath) => Shell.Open(Links.Obsidian(FullPath(relativePath)));

    private void OnMarkdownLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is MarkdownView view)
        {
            view.LinkHandler = link => NoteLinks.Target(link) is { } target && OpenInNotesView(target);
        }
    }

    private void OnOpenCardNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is { } card)
        {
            OpenInObsidian(card.Column.NotePath);
        }
    }

    private void OnColumnTitleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ColumnView column })
        {
            OpenInObsidian(column.Column.NotePath);
        }
    }

    private void OnStatusTitleClick(object? sender, RoutedEventArgs e) => OpenInNotesView(ProjectBoard.StatusNote);

    private void OnOpenSelectedNoteClick(object? sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is NoteView note)
        {
            OpenInObsidian(note.Note.RelativePath);
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => Reload();

    private async void OnOpenVaultClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ObsidianLauncher.OpenProject(_settings);
        }
        catch (DirectoryNotFoundException ex)
        {
            await Settings.Dialogs.ShowMessageAsync(this, ex.Message);
        }
    }

    /// <summary>
    /// Esc: leave the edit, then the side panel, then the window. Ctrl+F search, Ctrl+N new appunto, Ctrl+1…4 views,
    /// Ctrl+S save the note being edited.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Escape when _editedNote is not null:
                StopEditingNote();
                break;
            case Key.Escape when _editingCard:
                StopEditingCard();
                AfterEdit();
                break;
            case Key.Escape when Detail.IsVisible:
                CloseDetail();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.F when ctrl:
                Filter.Focus();
                Filter.SelectAll();
                break;
            case Key.I when ctrl:
                OnIdeasClick(null, e);
                break;
            case Key.N when ctrl:
                QuickNote.Focus();
                break;
            case Key.S when ctrl && _editedNote is not null:
                OnSaveNoteClick(null, e);
                break;
            case >= Key.D1 and <= Key.D6 when ctrl:
                ShowView(Views[e.Key - Key.D1].Key);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
