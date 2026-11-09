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
        ("graph", "🕸  Grafo", "Grafo delle note"),
        ("week", "📅  Settimana", "Settimana"),
        ("costs", "💳  Costi", "Costi di Claude"),
        ("decisions", "⚖  Decisioni", "Decisioni"),
        ("glossary", "📖  Glossario", "Glossario"),
        ("prs", "🔀  PR", "Pull request aperte"),
    ];

    private NoteGraph? _graph;

    private readonly VaultIndex _vault;
    private readonly BuddySettings _settings;
    private readonly ClaudeUsage? _usage;
    private readonly ContextMenu _projectMenu;
    private readonly IReadOnlySet<string> _skills;
    private readonly DispatcherTimer _reload;
    private readonly DispatcherTimer _quickNoteDone;
    private ProjectBoard? _board;
    private readonly DispatcherTimer _notePreview;

    /// <summary>The board as last read from the vault (for tests).</summary>
    internal ProjectBoard? Board => _board;

    private List<NoteView> _notes = [];
    private List<CardView> _cards = [];
    private string _view = "overview";
    private CardView? _selected;
    private bool _editingCard;
    private bool _reloadPending; // the vault changed while a card was being edited
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
            if (_editingCard)
            {
                _reloadPending = true; // rebuilding would throw away the card being typed: done when the editor closes
            }
            else
            {
                Reload(); // a note being edited is kept: the notes list stays as it is until the editor closes
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
        Graph.NoteActivated += OnGraphNoteActivated;
        PageFindBox.TextChanged += (_, _) => UpdatePageFind();
        PageFindBox.AddHandler(KeyDownEvent, OnPageFindKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        NoteEditor.AddHandler(KeyDownEvent, OnNoteEditorKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _notePreview = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _notePreview.Tick += (_, _) =>
        {
            _notePreview.Stop();
            UpdateNotePreview();
        };
        NoteEditor.TextChanged += (_, _) =>
        {
            _notePreview.Stop();
            _notePreview.Start();
        };
        NotesList.SelectionChanged += (_, _) => ShowNote(NotesList.SelectedItem as NoteView);
        ManualDocsList.SelectionChanged += (_, _) =>
        {
            if (!_refillingManualDocs)
            {
                ShowManualDoc(ManualDocsList.SelectedItem as ManualDocView);
            }
        };
        Nav.SelectionChanged += (_, _) =>
        {
            if (!_switchingView && Nav.SelectedItem is NavItem item)
            {
                ShowView(item.Key);
            }
        };
        KeyDown += OnWindowKeyDown;
        BoardView.SizeChanged += (_, _) => FitColumns();
        PageScroller.SizeChanged += (_, _) => FitPageCards();
        SetUpBoardDrag();

        SetUpPalette();

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

        _reloadPending = false;
        _board = ProjectBoard.Load(_vault, _settings.TaskIdPattern);
        var selectedNote = (NotesList.SelectedItem as NoteView)?.Note.RelativePath;
        _notes = _board.Notes.Select(n => new NoteView(n)).ToList();
        _graph = NoteGraph.Build(_board.Notes, _settings.TaskIdPattern);
        ApplyGraph();
        Render();

        // Keep the note being read selected across reloads (while one is edited the list is left alone, see Render).
        if (_editedNote is not null)
        {
            return;
        }

        NotesList.SelectedItem = _notes.FirstOrDefault(n => n.Note.RelativePath == selectedNote)
                                 ?? (NotesList.ItemsSource as IEnumerable<NoteView>)?.FirstOrDefault();
    }

    internal void ShowView(string key)
    {
        if (key == NewPageKey)
        {
            _ = CreatePageAsync(); // "＋ Nuova pagina…": asks the name, then shows the new page
            return;
        }

        _lastRealView = key;
        _view = key;
        GenericView.IsVisible = IsGeneric(key);
        OverviewView.IsVisible = key == "overview";
        BoardPage.IsVisible = key == "board";
        ArchiveView.IsVisible = key == "archive";
        NotesView.IsVisible = key == "notes";
        GraphView.IsVisible = key == "graph";
        if (PageFindBar.IsVisible && !(key is "notes" or "manualtests"))
        {
            ClosePageFind();
        }
        else
        {
            RefreshPageFind();
        }
        if (key == "graph")
        {
            Graph.Resume();
        }

        if (key == "board")
        {
            _ = LoadPullRequestsAsync();
        }
        ManualTestsView.IsVisible = key == "manualtests";
        WebTestsView.IsVisible = key == "webtests";
        ViewTitle.Text = TitleOf(key);
        UpdateSubtitle();
        if (IsGeneric(key))
        {
            _ = RenderPageAsync(key);
        }

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
        if (_view == "board")
        {
            _ = LoadPullRequestsAsync(); // CI of the open PRs on the cards; cached, re-renders when it changes
        }

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
                .Where(i => _tagFilter is null || !Flow.Active.Contains(key) || i.TagList.Contains(_tagFilter, StringComparer.OrdinalIgnoreCase))
                .Select(i => new CardView(i, column, today, PullRequestOf(i)));
            return key switch
            {
                "todo" or "corso" => cards.OrderBy(c => c.Priority).ThenBy(c => c.Item.Date ?? DateOnly.MaxValue).ToList(),
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
        // Da fare also shows the ⏸ tasks, after the others and with a band: In attesa has no column of its own.
        var waiting = columns.FirstOrDefault(c => c.Key == "attesa");
        BoardColumns.ItemsSource = columns.Where(c => Flow.Board.Contains(c.Key))
            .Select(c => c.Key == "todo" && waiting is not null
                ? new ColumnView(c with { Items = c.Items.Concat(waiting.Items).ToList() }, Shown("todo").Concat(Shown("attesa")).ToList(), Flow.ColumnActions(c.Key, _skills))
                : new ColumnView(c, Shown(c.Key), Flow.ColumnActions(c.Key, _skills)))
            .ToList();

        FitColumns();

        // Tag filter bar: every tag on the board, with its count.
        var boardTags = columns.Where(c => Flow.Active.Contains(c.Key)).SelectMany(c => c.Items)
            .SelectMany(i => i.TagList)
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
        RenderHealth(today);
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

        // Notes: not while one is edited, replacing the list would change the selection and close the editor.
        // StopEditingNote reloads.
        if (_editedNote is null)
        {
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
        }

        RenderTests(filter);
        Graph.Search = filter;

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
        })).Concat(CustomPageNav()).ToList();
        Nav.SelectedItem = (Nav.ItemsSource as IEnumerable<NavItem>)?.FirstOrDefault(n => n.Key == _view);
        if (IsGeneric(_view))
        {
            _ = RenderPageAsync(_view); // the vault changed, or the filter: the page follows
        }
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
            "graph" => $"{Graph.NodeCount} note, {Graph.EdgeCount} collegamenti · rotella per lo zoom, trascina per spostarti, click su una nota per aprirla",
            _ when IsGeneric(_view) => ViewSubtitle.Text, // set by the page itself
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

        var actions = card.Item.Raw.Length > 0 ? Flow.StateActions(card.Column.Key).ToList() : [];
        if (card.HasId)
        {
            actions.AddRange(Flow.CardActions(card.Column.Key, _skills));
        }

        if (card.HasId && card.Column.Key is not ("fatti" or "rifiutati"))
        {
            actions.Add(new FlowAction("💬 Chiedi in chat", "", "", "Apre la chat con una domanda su questa voce"));
        }

        ClaudeActions.ItemsSource = actions;
        ClaudeActionsPanel.IsVisible = actions.Count > 0;
        _ = LoadTimelineAsync(card);

        // Tag picker: the common tags plus any already used in the vault; the item's own is highlighted.
        var used = (_board?.Columns ?? []).SelectMany(c => c.Items).SelectMany(i => i.TagList);
        DetailTags.ItemsSource = Tags.Defaults.Select(d => d.Name)
            .Concat(used.Where(u => !Tags.Defaults.Any(d => d.Name.Equals(u, StringComparison.OrdinalIgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => new TagOption(t, Tags.Brush(t), card.Item.TagList.Contains(t, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        TagRemoveButton.IsVisible = card.HasTag;
        TagPanel.IsVisible = card.Item.Raw.Length > 0;

        var manual = card.Id is { } taskId ? _manualDocs.FirstOrDefault(d => d.TaskId == taskId) : null;
        CardManualTestsButton.IsVisible = manual is not null;
        CardManualTestsButton.Content = manual is null ? "" : $"🧪 Test manuali {manual.Doc.Done}/{manual.Doc.Total}";
        _ = UpdateIssueButtonAsync(card);
    }

    // ---- Card → GitHub issue ----------------------------------------------------------------------------------

    /// <summary>Tests give the repository instead of reading the git remote.</summary>
    internal Func<Task<Buddy.Core.Repo.GitHubRepo?>>? GitHubRepoSource { get; set; }

    /// <summary>Tests write the issue instead of the dialog.</summary>
    internal Func<IssueFormWindow, Task<Buddy.Core.Tracker.IssueDraft?>>? ShowIssueForm { get; set; }

    /// <summary>Tests create the issue instead of gh; returns its link.</summary>
    internal Func<Buddy.Core.Tracker.IssueDraft, Task<string>>? CreateIssue { get; set; }

    private Task<Buddy.Core.Repo.GitHubRepo?>? _gitHubRepo;

    /// <summary>The project's GitHub repository (from the git remote, read once), null when it is not on GitHub.</summary>
    private Task<Buddy.Core.Repo.GitHubRepo?> GitHubRepoAsync()
    {
        if (GitHubRepoSource is { } source)
        {
            return source();
        }

        _repo ??= new Buddy.Core.Repo.GitRepo(Options.Create(_settings), TimeProvider.System);
        var repo = _repo;
        return _gitHubRepo ??= Task.Run(async () =>
            repo.IsAvailable ? Buddy.Core.Repo.GitHubLinks.FromRemote(await repo.GetRemoteUrlAsync()) : null);
    }

    /// <summary>
    /// "↗ Issue #12" when the card is tied to an issue (opens it); "✚ Crea issue GitHub" for an open card of a
    /// project on GitHub; hidden otherwise.
    /// </summary>
    private async Task UpdateIssueButtonAsync(CardView card)
    {
        CardIssueButton.IsVisible = false;
        if (card.Item.Raw.Length == 0)
        {
            return;
        }

        if (Buddy.Core.Tracker.IssueFromCard.LinkOf(card.Item) is { } link)
        {
            CardIssueButton.Content = $"↗ Issue {Buddy.Core.Tracker.IssueFromCard.Number(link)}";
            ToolTip.SetTip(CardIssueButton, link);
            CardIssueButton.IsVisible = true;
            return;
        }

        if (card.Column.Key is "fatti" or "rifiutati" || await GitHubRepoAsync() is not { } repository || _selected != card)
        {
            return;
        }

        CardIssueButton.Content = "✚ Crea issue GitHub";
        ToolTip.SetTip(CardIssueButton, $"Crea un'issue in {repository} da questa voce e ci mette il link");
        CardIssueButton.IsVisible = true;
    }

    private async void OnCardIssueClick(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } card)
        {
            return;
        }

        if (Buddy.Core.Tracker.IssueFromCard.LinkOf(card.Item) is { } link)
        {
            Shell.Open(link);
            return;
        }

        await CreateIssueAsync(card);
    }

    /// <summary>The issue proposed from the card, checked in the form, created with gh; the card gets [issue:: link].</summary>
    internal async Task CreateIssueAsync(CardView card)
    {
        if (await GitHubRepoAsync() is not { } repository)
        {
            await Settings.Dialogs.ShowMessageAsync(this, "Il repository di questo progetto non è su GitHub.");
            return;
        }

        var window = new IssueFormWindow(repository.ToString(), Buddy.Core.Tracker.IssueFromCard.Draft(card.Item));
        if (await (ShowIssueForm?.Invoke(window) ?? window.ShowAndWaitAsync(this)) is not { } draft)
        {
            return;
        }

        string url;
        CardIssueButton.IsEnabled = false;
        CardIssueButton.Content = "✚ Creo l'issue…";
        try
        {
            var repoPath = _repo?.RootPath ?? "";
            url = CreateIssue is { } create
                ? await create(draft)
                : await Task.Run(() => Buddy.Core.Tracker.IssueFromCard.CreateAsync(repoPath, draft));
        }
        catch (Buddy.Core.Tracker.TrackerException ex)
        {
            await Settings.Dialogs.ShowMessageAsync(this, $"GitHub non ha creato l'issue: {ex.Message}");
            CardIssueButton.Content = "✚ Crea issue GitHub";
            return;
        }
        finally
        {
            CardIssueButton.IsEnabled = true;
        }

        if (await WriteAsync(() => VaultWriter.ReplaceBlock(FullPath(card.Column.NotePath), card.Item.Raw,
                ItemFields.Set(card.Item.Raw, Buddy.Core.Tracker.IssueFromCard.Field, url))))
        {
            AfterEdit();
        }
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
        if (_reloadPending)
        {
            Reload(); // the vault changed while the card was being edited
        }
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
        if (sender is Control { DataContext: FlowAction { Skill: Flow.StateSkill } state } && _selected is { } selected)
        {
            await MoveCardAsync(selected, state.Command);
            return;
        }

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

    private static readonly HttpClient JiraHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Tests give the trackers instead of reading the settings and the git remote.</summary>
    internal Func<Task<IReadOnlyList<Buddy.Core.Tracker.ITracker>>>? TrackerSource { get; set; }

    /// <summary>Tests pick the cards instead of the dialog.</summary>
    internal Func<TrackerImportWindow, Task<IReadOnlyList<Buddy.Core.Tracker.TrackerIssue>?>>? ShowTrackerImport { get; set; }

    /// <summary>"⬇ Importa": the trackers of this project; one is used at once, with several a menu asks which.</summary>
    private async void OnJiraImportClick(object? sender, RoutedEventArgs e)
    {
        _repo ??= new Buddy.Core.Repo.GitRepo(Microsoft.Extensions.Options.Options.Create(_settings), TimeProvider.System);
        var repo = _repo;
        var trackers = TrackerSource is { } source
            ? await source()
            : await Task.Run(() => Buddy.Core.Tracker.ProjectTrackers.AvailableAsync(_settings, SettingsStore.Folder, JiraHttp, repo));
        switch (trackers.Count)
        {
            case 0:
                await Settings.Dialogs.ShowMessageAsync(this,
                    "Nessun tracker collegato a questo progetto. Le card arrivano da:\n"
                    + "• Jira — indirizzo e token in Impostazioni → Assistente → Jira;\n"
                    + "• issue di GitHub — se il repository è su GitHub (serve gh collegato con «gh auth login»);\n"
                    + "• Azure Boards — token (PAT) in Impostazioni → Assistente → Azure DevOps, progetto dal repository o dalle impostazioni.");
                return;
            case 1:
                await ImportAsync(trackers[0]);
                return;
        }

        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedRight, PlacementTarget = JiraImportButton };
        foreach (var tracker in trackers)
        {
            var item = new MenuItem { Header = $"Da {tracker.Name}" };
            item.Click += async (_, _) => await ImportAsync(tracker);
            menu.Items.Add(item);
        }

        // A ContextMenu, not a MenuFlyout: the flyout does not open on X11.
        JiraImportButton.ContextMenu = menu;
        menu.Open(JiraImportButton);
    }

    /// <summary>The cards assigned to you in <paramref name="tracker"/>, the new ones to pick, the picked ones appended to To-Do.</summary>
    internal async Task ImportAsync(Buddy.Core.Tracker.ITracker tracker)
    {
        if (_board?.Columns.FirstOrDefault(c => c.Key == "todo") is not { } todo)
        {
            await Settings.Dialogs.ShowMessageAsync(this, "Nel vault non c'è la nota «To-Do»: le card importate vanno lì.");
            return;
        }

        IReadOnlyList<Buddy.Core.Tracker.TrackerIssue> all;
        JiraImportButton.IsEnabled = false;
        JiraImportButton.Content = "⬇ Importa…";
        try
        {
            all = await Task.Run(() => tracker.AssignedAsync());
        }
        catch (Exception ex) when (ex is Buddy.Core.Tracker.TrackerException or HttpRequestException or TaskCanceledException or UriFormatException
                                       or InvalidOperationException or System.Text.Json.JsonException)
        {
            await Settings.Dialogs.ShowMessageAsync(this, ex is Buddy.Core.Tracker.TrackerException ? ex.Message : $"Non riesco a leggere {tracker.Name}: {ex.Message}");
            return;
        }
        finally
        {
            JiraImportButton.IsEnabled = true;
            JiraImportButton.Content = "⬇ Importa";
        }

        var fresh = Buddy.Core.Tracker.TrackerImport.New(all, _board);
        var window = new TrackerImportWindow(tracker.Name, fresh, all.Count - fresh.Count);
        if (await (ShowTrackerImport?.Invoke(window) ?? window.ShowAndWaitAsync(this)) is not { Count: > 0 } picked)
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (await WriteAsync(() =>
            {
                foreach (var issue in picked)
                {
                    VaultWriter.AppendItem(FullPath(todo.NotePath), BoardEntries.Sections["todo"], Buddy.Core.Tracker.TrackerImport.ToTask(issue, today));
                }
            }))
        {
            AfterEdit();
        }
    }

    /// <summary>The open PR whose branch or title names the item's task (from the last read, see LoadPullRequestsAsync).</summary>
    private Buddy.Core.Repo.PullRequestInfo? PullRequestOf(BoardItem item) =>
        item.Id is { } id && _prs?.List is { } prs ? prs.FirstOrDefault(p => string.Equals(p.TaskId, id, StringComparison.OrdinalIgnoreCase)) : null;

    private bool _loadingPullRequests;

    /// <summary>
    /// Reads the open PRs and their CI in the background (GitHub or Azure Repos, at most every 3 minutes) and shows
    /// them on the board's cards.
    /// </summary>
    private async Task LoadPullRequestsAsync(bool refresh = false)
    {
        if (_loadingPullRequests || (!refresh && _prs is { } cached && DateTime.Now - cached.At < TimeSpan.FromMinutes(3)))
        {
            return;
        }

        _loadingPullRequests = true;
        try
        {
            _repo ??= new Buddy.Core.Repo.GitRepo(Microsoft.Extensions.Options.Options.Create(_settings), TimeProvider.System);
            var repo = _repo;
            var (list, problem) = PullRequestSource is { } source
                ? await source()
                : await Task.Run(() => Buddy.Core.Tracker.ProjectTrackers.PullRequestsAsync(_settings, SettingsStore.Folder, JiraHttp, repo));
            var changed = _prs is not { } before || !before.List.SequenceEqual(list);
            _prs = (DateTime.Now, list, problem);
            if (changed && list.Count > 0 && _view == "board")
            {
                Render();
            }
        }
        finally
        {
            _loadingPullRequests = false;
        }
    }

    /// <summary>Tests give the PRs instead of asking GitHub or Azure DevOps.</summary>
    internal Func<Task<(IReadOnlyList<Buddy.Core.Repo.PullRequestInfo> List, string? Problem)>>? PullRequestSource { get; set; }

    private async void OnColumnAddClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ColumnView column })
        {
            await AddEntryAsync(column.Column);
        }
    }

    /// <summary>Tests fill in the form instead of showing it as a dialog.</summary>
    internal Func<EntryFormWindow, Task<NewEntry?>>? ShowEntryForm { get; set; }

    /// <summary>"＋" on a column: the form, then the item written in the column's note in the flow's format.</summary>
    internal async Task AddEntryAsync(BoardColumn column)
    {
        if (_board is null || !BoardEntries.Sections.TryGetValue(column.Key, out var section))
        {
            return;
        }

        var todo = _board.Columns.FirstOrDefault(c => c.Key == "todo");
        var todoPath = todo is null ? null : FullPath(todo.NotePath);
        var usedIds = _board.Columns.SelectMany(c => c.Items).Select(i => i.Id);
        var nextId = BoardEntries.NextId(todoPath is not null && File.Exists(todoPath) ? File.ReadAllText(todoPath) : null, usedIds);
        var knownTags = Tags.Defaults.Select(d => d.Name).Concat(_board.Columns.SelectMany(c => c.Items).SelectMany(i => i.TagList));

        var form = new EntryFormWindow(column.Key, nextId, knownTags);
        if (await (ShowEntryForm?.Invoke(form) ?? form.ShowAndWaitAsync(this)) is not { } entry)
        {
            return;
        }

        var id = entry.Id.Length > 0 ? entry.Id : nextId;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var text = column.Key switch
        {
            "appunti" => BoardEntries.Appunto(entry, today),
            "controllare" => BoardEntries.ToReview(entry, id),
            _ when entry.Waiting => BoardEntries.WaitingTask(entry, id),
            _ => BoardEntries.Task(entry, id, today),
        };

        var written = await WriteAsync(() =>
        {
            VaultWriter.AppendItem(FullPath(column.NotePath), section, text);
            if (column.Key != "appunti" && todoPath is not null && File.Exists(todoPath))
            {
                // The id is taken: the counter moves past it, as /smista does.
                var content = File.ReadAllText(todoPath).Replace("\r\n", "\n");
                var advanced = BoardEntries.AdvanceNextId(content, id);
                if (advanced != content)
                {
                    VaultWriter.Save(todoPath, content, advanced);
                }
            }
        });
        if (written)
        {
            AfterEdit();
        }
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
            await ToggleTagAsync(tag.Name);
        }
    }

    private async void OnTagOtherClick(object? sender, RoutedEventArgs e)
    {
        if (await Settings.Dialogs.AskAsync(this, "Tag", "Nome del tag (es. sicurezza, performance):") is { } name)
        {
            await ToggleTagAsync(name.ToLowerInvariant().Trim());
        }
    }

    private async void OnTagRemoveClick(object? sender, RoutedEventArgs e) => await SetTagAsync(null);

    /// <summary>Adds the tag to the item, or removes it when it already has it (an item can have several).</summary>
    private Task ToggleTagAsync(string tag)
    {
        var tags = _selected?.Item.TagList.ToList() ?? [];
        if (tags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)) == 0 && tag.Length > 0)
        {
            tags.Add(tag);
        }

        return SetTagAsync(tags.Count == 0 ? null : string.Join(", ", tags));
    }

    /// <summary>Writes [tag:: names] on the item's first line in its note (null removes it).</summary>
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

        // Replacing the list clears the selection for a moment: those selection changes must not rebuild the
        // checklist (it would jump back to the top after every tick); it is shown once, below.
        _refillingManualDocs = true;
        try
        {
            ManualDocsList.ItemsSource = _manualDocs;
            ManualDocsList.SelectedItem = _manualDocs.FirstOrDefault(d => d.Doc.RelativePath == selectedPath) ?? _manualDocs.FirstOrDefault();
        }
        finally
        {
            _refillingManualDocs = false;
        }
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

        // The same checklist changed (e.g. edited in Obsidian or by Claude): rebuilt, but the reader stays where they were.
        var samePage = doc is not null && _shownManual?.Doc.RelativePath == doc.Doc.RelativePath;
        var offset = ManualScroller.Offset;
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
        if (samePage)
        {
            // After the new items are laid out, or the offset would be clamped to the still-empty content.
            Dispatcher.UIThread.Post(() => ManualScroller.Offset = offset, DispatcherPriority.Loaded);
        }
        else
        {
            ManualScroller.Offset = new Vector(0, 0);
        }

        RefreshPageFind();
    }

    private bool _refillingManualDocs;

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

    /// <summary>
    /// "📄 Esporta in Word": the ticked cases of the checklist in a .docx, saved where the user chooses (Downloads by
    /// default), then shown in its folder, ready to attach to the card.
    /// </summary>
    private async void OnManualExportClick(object? sender, RoutedEventArgs e)
    {
        if (_shownManual is not { } shown)
        {
            return;
        }

        // The checklist as it is now on disk (ticks made here included).
        var doc = TestNotes.ManualTests(_vault, _settings.TaskIdPattern).FirstOrDefault(d => d.RelativePath == shown.Doc.RelativePath) ?? shown.Doc;
        if (ManualTestReport.DoneCases(doc).Count == 0
            && !await Settings.Dialogs.ConfirmAsync(this, "Nessun caso è ancora spuntato: esporto comunque il documento (vuoto)?"))
        {
            return;
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Salva i test fatti in Word",
            SuggestedFileName = ManualTestReport.FileName(doc),
            DefaultExtension = "docx",
            SuggestedStartLocation = Directory.Exists(downloads) ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(downloads)) : null,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Documento Word") { Patterns = ["*.docx"] }],
        });
        if (file is null || Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(file) is not { } path)
        {
            return;
        }

        try
        {
            var title = doc.TaskId is { } id && (_board?.Columns ?? []).SelectMany(c => c.Items).FirstOrDefault(i => i.Id == id) is { } item
                ? item.Title.Replace("`", "")
                : null;
            ManualTestReport.Write(doc, title, path);
            Shell.Open(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Settings.Dialogs.ShowMessageAsync(this, $"Non riesco a salvare il documento: {ex.Message}");
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

        var folder = WebTestRun.ScriptsFolder(_vault, _settings.Repo.Path, _settings.Claude.WorkingDirectory);
        if (folder is null)
        {
            await Settings.Dialogs.ShowMessageAsync(this,
                "Non trovo la cartella degli script: nella Scheda progetto o nella nota Test web serve la riga "
                + "«Cartella degli script di test: `percorso`» (assoluto, con ~, o relativo al repo).");
            return;
        }

        if (!Directory.Exists(folder))
        {
            await Settings.Dialogs.ShowMessageAsync(this,
                $"La cartella degli script indicata nel vault non esiste:\n{folder}\n\n"
                + "Se è nel repo, forse il branch attuale non la contiene ancora (aggiorna il repo) oppure il percorso va corretto.");
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
        if (!_editingCard)
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

    private async void OnEditCardClick(object? sender, RoutedEventArgs e) => await EditSelectedAsync();

    /// <summary>
    /// "✏ Modifica": the same form used to add the item, filled in with it; Fatti and Rifiutati (and "📝 Modifica il
    /// testo" in the form) edit the markdown text instead.
    /// </summary>
    internal async Task EditSelectedAsync()
    {
        if (_selected is not { } card || card.Item.Raw.Length == 0 || _board is null)
        {
            return;
        }

        if (!BoardEdits.Editable.Contains(card.Column.Key))
        {
            BeginEditSelected();
            return;
        }

        var before = BoardEdits.ToEntry(card.Item, card.Column.Key);
        var knownTags = Tags.Defaults.Select(d => d.Name).Concat(_board.Columns.SelectMany(c => c.Items).SelectMany(i => i.TagList));
        var form = new EntryFormWindow(BoardEdits.FormKind(card.Column.Key), card.Id ?? "", knownTags, before,
            canWait: card.Column.Key is "todo" or "attesa" or "corso");
        if (await (ShowEntryForm?.Invoke(form) ?? form.ShowAndWaitAsync(this)) is not { } after)
        {
            if (form.RawRequested)
            {
                BeginEditSelected();
            }

            return;
        }

        var text = BoardEdits.Apply(card.Item, card.Column.Key, before, after);
        if (text != card.Item.Raw && await WriteAsync(() => VaultWriter.ReplaceBlock(FullPath(card.Column.NotePath), card.Item.Raw, text)))
        {
            AfterEdit();
        }
    }

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
        RenameNoteButton.IsVisible = note is not null && CanManageSelectedNote;
        DeleteNoteButton.IsVisible = note is not null && CanManageSelectedNote;
        NoteViewer.Markdown = note?.Markdown ?? "";
        NoteScroller.Offset = new Vector(0, 0);
        RefreshPageFind();

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
        NoteEditorPanel.IsVisible = editing;
        NoteScroller.IsVisible = !editing;
        EditNoteButton.IsVisible = !editing;
        RenameNoteButton.IsVisible = !editing && CanManageSelectedNote;
        DeleteNoteButton.IsVisible = !editing && CanManageSelectedNote;
        if (editing)
        {
            UpdateNotePreview();
        }

        SaveNoteButton.IsVisible = editing;
        CancelNoteButton.IsVisible = editing;
        NotesList.IsEnabled = !editing;
    }

    /// <summary>Flow notes (To-Do, Scheda progetto…) are not renamed or deleted from here: the flow relies on their names.</summary>
    private bool CanManageSelectedNote => NotesList.SelectedItem is NoteView note && !NoteFiles.IsFlowNote(note.Note.RelativePath);

    private async void OnRenameNoteClick(object? sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is not NoteView note
            || await Settings.Dialogs.AskAsync(this, "Rinomina nota", "Nuovo titolo (i link alla nota vengono aggiornati):", note.Title) is not { } title)
        {
            return;
        }

        var renamed = note.Note.RelativePath;
        if (await WriteAsync(() => renamed = NoteFiles.Rename(_settings.ProjectPath, note.Note.RelativePath, title)))
        {
            _vault.Refresh();
            Reload();
            OpenInNotesView(renamed);
        }
    }

    private async void OnDeleteNoteClick(object? sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is not NoteView note
            || !await Settings.Dialogs.ConfirmAsync(this,
                $"Eliminare la nota «{note.Title}»? Finisce nel cestino del vault (.trash), da cui si può recuperare.", "Elimina"))
        {
            return;
        }

        if (await WriteAsync(() => NoteFiles.Delete(_settings.ProjectPath, note.Note.RelativePath)))
        {
            _vault.Refresh();
            Reload();
        }
    }

    // ---- Note editor: tool bar, shortcuts, live preview ------------------------------------------------------

    private void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string command })
        {
            ApplyFormat(command);
        }
    }

    private void ApplyFormat(string command)
    {
        var result = Buddy.Core.Text.MarkdownFormat.Apply(NoteEditor.Text ?? "", NoteEditor.SelectionStart, NoteEditor.SelectionEnd, command);
        NoteEditor.Text = result.Text;
        NoteEditor.SelectionStart = result.SelectionStart;
        NoteEditor.SelectionEnd = result.SelectionEnd;
        NoteEditor.Focus();
    }

    /// <summary>Ctrl+B bold, Ctrl+I italic, Ctrl+E code, Ctrl+K link: while typing they win over the window's shortcuts.</summary>
    private void OnNoteEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        var command = e.Key switch
        {
            Key.B => "bold",
            Key.I => "italic",
            Key.E => "code",
            Key.K => "link",
            _ => null,
        };
        if (command is not null)
        {
            e.Handled = true;
            ApplyFormat(command);
        }
    }

    private void OnNotePreviewToggled(object? sender, RoutedEventArgs e)
    {
        var show = NotePreviewToggle.IsChecked == true;
        NotePreviewPanel.IsVisible = show;
        NoteEditorGrid.ColumnDefinitions[1].Width = new GridLength(show ? 12 : 0);
        NoteEditorGrid.ColumnDefinitions[2].Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        UpdateNotePreview();
    }

    private void UpdateNotePreview()
    {
        if (NotePreviewToggle.IsChecked == true && NoteEditorPanel.IsVisible)
        {
            NotePreview.Markdown = NoteLinks.ToMarkdown(NoteView.StripFrontMatter(NoteEditor.Text ?? ""));
        }
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

    // ---- Task timeline ----------------------------------------------------------------------------------------

    private Buddy.Core.Repo.GitRepo? _repo;

    /// <summary>Fills the side panel's timeline (git and the journal are read in the background).</summary>
    private async Task LoadTimelineAsync(CardView card)
    {
        TimelinePanel.IsVisible = false;
        if (!card.HasId || _board is null || card.Item.Id is not { } id)
        {
            return;
        }

        _repo ??= new Buddy.Core.Repo.GitRepo(Microsoft.Extensions.Options.Options.Create(_settings), TimeProvider.System);
        try
        {
            var board = _board;
            var events = await Task.Run(() => TaskTimeline.BuildAsync(id, board, _vault, _repo, _settings.Mentor.JournalPath, _settings.ProjectPath));
            if (_selected == card)
            {
                TimelineList.ItemsSource = events;
                var estimate = EstimateLine(card, events);
                EstimateText.Text = estimate ?? "";
                EstimateText.IsVisible = estimate is not null;
                TimelinePanel.IsVisible = events.Count > 0 || estimate is not null;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No timeline: the rest of the panel is still useful.
        }
    }

    // ---- Vault health -----------------------------------------------------------------------------------------

    private IReadOnlySet<string>? _elsewhere;

    private void RenderHealth(DateOnly today)
    {
        if (_board is null || _graph is null)
        {
            HealthPanel.IsVisible = false;
            return;
        }

        var checklists = TestNotes.ManualTests(_vault, _settings.TaskIdPattern);
        // The rest of the Obsidian vault is read once per window: links resolved there are not broken.
        _elsewhere ??= VaultHealth.TitlesElsewhere(_settings.ProjectPath);
        var all = VaultHealth.Check(_board, _graph, checklists, today, int.MaxValue, _elsewhere);
        HealthPanel.IsVisible = true;
        HealthList.ItemsSource = all.Take(8).ToList();
        HealthOk.IsVisible = all.Count == 0;
        HealthTitle.Text = all.Count == 0 ? "🩺 Salute del vault"
            : all.Count > 8 ? $"🩺 Salute del vault · {all.Count} cose da sistemare (le prime 8)"
            : $"🩺 Salute del vault · {all.Count} cos{(all.Count == 1 ? "a" : "e")} da sistemare";
    }

    /// <summary>"Crea la nota": an empty note with the linked name, next to the project's notes, then opened.</summary>
    private async void OnCreateMissingNoteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not HealthIssue { MissingTitle: { } title })
        {
            return;
        }

        var relative = title.Replace('\\', '/').Trim('/') + ".md";
        if (await WriteAsync(() => VaultWriter.CreateNote(FullPath(relative), Path.GetFileName(title))))
        {
            AfterEdit();
            OpenInNotesView(relative);
        }
    }

    /// <summary>"Collega a «X»": the broken link in its note now points at the existing note with a near-identical name.</summary>
    private async void OnRelinkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HealthIssue { MissingTitle: { } missing, Suggestion: { } suggestion, Target: { } note }
            && await WriteAsync(() => VaultWriter.ReplaceLink(FullPath(note), missing, suggestion)))
        {
            AfterEdit();
        }
    }

    private void OnHealthIssueClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is HealthIssue { Target: { } target })
        {
            OpenInNotesView(target);
        }
    }

    // ---- Graph ------------------------------------------------------------------------------------------------

    private void ApplyGraph()
    {
        if (_graph is null)
        {
            return;
        }

        Graph.SetGraph(_graph, GraphOrphans.IsChecked == true, GraphMissing.IsChecked == true, GraphByTask.IsChecked == true);
        GraphLegend.ItemsSource = Graph.Legend
            .Select(l => new GraphLegendItem(l.Folder, new Avalonia.Media.SolidColorBrush(l.Color)))
            .ToList();
        UpdateSubtitle();
    }

    private void OnGraphOptionChanged(object? sender, RoutedEventArgs e) => ApplyGraph();

    private void OnGraphFitClick(object? sender, RoutedEventArgs e) => Graph.FitToView();

    private void OnGraphRelayoutClick(object? sender, RoutedEventArgs e) => Graph.Relayout();

    private void OnGraphNoteActivated(string relativePath) => OpenInNotesView(relativePath);

    /// <summary>"🕸" in the note viewer: the graph centred on the note being read.</summary>
    private void OnShowNoteInGraphClick(object? sender, RoutedEventArgs e)
    {
        if (NotesList.SelectedItem is NoteView note)
        {
            ShowView("graph");
            Graph.FocusNote(note.Note.RelativePath);
        }
    }

    /// <summary>"🐞 Bug" / "✨ Idea": where to open the issue, Buddy itself or the project's GitHub repository.</summary>
    private async void OnReportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } button)
        {
            return;
        }

        var kind = tag == "bug" ? Buddy.Core.Repo.IssueKind.Bug : Buddy.Core.Repo.IssueKind.Feature;
        _repo ??= new Buddy.Core.Repo.GitRepo(Microsoft.Extensions.Options.Options.Create(_settings), TimeProvider.System);
        var project = _repo.IsAvailable ? Buddy.Core.Repo.GitHubLinks.FromRemote(await _repo.GetRemoteUrlAsync()) : null;

        var onBuddy = new MenuItem { Header = $"Su Buddy ({Buddy.Core.Repo.GitHubLinks.Buddy})" };
        onBuddy.Click += (_, _) => Shell.Open(Buddy.Core.Repo.GitHubLinks.NewIssueUrl(Buddy.Core.Repo.GitHubLinks.Buddy, kind,
            typeof(ProjectWindow).Assembly.GetName().Version?.ToString(3), System.Runtime.InteropServices.RuntimeInformation.OSDescription));
        var onProject = new MenuItem
        {
            Header = project is null ? "Sul progetto (il repository non è su GitHub)" : $"Sul progetto ({project})",
            IsEnabled = project is not null,
        };
        if (project is not null)
        {
            onProject.Click += (_, _) => Shell.Open(Buddy.Core.Repo.GitHubLinks.NewIssueUrl(project, kind));
        }

        // A ContextMenu, not a MenuFlyout: the flyout does not open on X11.
        var menu = new ContextMenu { Placement = PlacementMode.TopEdgeAlignedLeft, PlacementTarget = button, Items = { onBuddy, onProject } };
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private void OnDonateClick(object? sender, RoutedEventArgs e) => Shell.Open(Buddy.Core.Repo.GitHubLinks.DonationUrl);

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
            case Key.K when ctrl:
                OpenPalette();
                break;
            case Key.Escape when Palette.IsVisible:
                ClosePalette();
                break;
            case Key.Escape when PageFindBar.IsVisible:
                ClosePageFind();
                break;
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
            case Key.F when ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && PageFindAvailable:
                OpenPageFind();
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
            case >= Key.D1 and <= Key.D9 when ctrl:
                ShowView(Views[e.Key - Key.D1].Key);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
