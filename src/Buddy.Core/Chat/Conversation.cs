using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Buddy.Core.Claude;
using Buddy.Core.Llm;
using Buddy.Core.Repo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Chat;

/// <summary>
/// The single chat thread shared by the local model, Claude and the instant commands. Routes each message,
/// keeps the pet state in sync, tracks the current task (from the git branch) and the pending attachment, and
/// turns Claude's permission prompts into entries with buttons.
/// All mutations of <see cref="Entries"/> happen on the synchronization context captured at construction
/// (the WPF dispatcher), so the UI can bind directly.
/// </summary>
public sealed class Conversation : INotifyPropertyChanged, IAsyncDisposable
{
    /// <summary>Closes /aiuto, after the list of commands.</summary>
    private const string HelpFooter = """
        Ctrl+Alt+B (o la scorciatoia impostata) apre la chat con gli appunti allegati; puoi anche trascinare file,
        testo o immagini sul personaggio.
        """;

    private readonly ILogger _log;
    private readonly LocalAssistant _local;
    private readonly ClaudeAuth _auth;
    private readonly AssistantCommands _commands;
    private readonly GitRepo _repo;
    private readonly IOptions<BuddySettings> _options;
    private readonly ClaudeUsage _usage;
    private decimal _sessionCost;
    private readonly SynchronizationContext? _ui;
    private ClaudeSession? _claude;
    private ChatEntry? _claudeAnswer;
    private ChatMode _mode = ChatMode.Local;
    private PetState _petState = PetState.Idle;
    private bool _claudeBusy;
    private long _turnStarted;
    private string _turnRequest = "";
    private Attachment? _pendingAttachment;
    private TaskInfo? _currentTask;
    private CancellationTokenSource? _happyTimer;

    public Conversation(
        LocalAssistant local,
        ClaudeAuth auth,
        AssistantCommands commands,
        GitRepo repo,
        IOptions<BuddySettings> options,
        ILogger<Conversation>? logger = null,
        ClaudeUsage? usage = null)
    {
        _log = logger ?? NullLogger<Conversation>.Instance;
        _local = local;
        _auth = auth;
        _commands = commands;
        _repo = repo;
        _options = options;
        _usage = usage ?? new ClaudeUsage();
        _ui = SynchronizationContext.Current;
        ChatGrouping.Track(Entries);
        ChatGrouping.Track(SideEntries);
        Commands.IsDisabled = command => _options.Value.Assistant.IsDisabled(command);
        RegisterCommands();
    }

    /// <summary>The line for a command switched off in the settings.</summary>
    private static string DisabledText(string command) =>
        $"`{command}` è disattivato: lo riaccendi in Impostazioni → Comandi.";

    /// <summary>The "/" commands: the conversation's, registered here, and the mentor's, registered by it.</summary>
    public CommandRegistry Commands { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a Claude answer that took a while is complete (the UI decides whether to notify).</summary>
    public event Action<LongTurnCompleted>? LongTurnCompleted;

    /// <summary>Raised when the mentor posts a message (the UI may notify if the chat is closed).</summary>
    public event Action<string>? MentorPosted;

    /// <summary>Raised when the user described their approach before delegating (task action, approach).</summary>
    public event Action<string, string>? ApproachGiven;

    /// <summary>A task button was pressed (e.g. "/lavora NUSR-1"): the mentor counts what is delegated.</summary>
    public event Action<string>? TaskActionRun;

    private Func<string, Task>? _pendingReply;

    /// <summary>True when the next message (in the chat on screen) answers a question of the mentor.</summary>
    public bool HasPendingReply => (_showingSide ? _sidePendingReply : _pendingReply) is not null;

    // ---- Side chat: the mentor's off-topic talk (pills, reviews, chats), kept out of the work chat ---------------

    /// <summary>
    /// The mentor's off-topic messages and the answers to them. They live apart from <see cref="Entries"/>, so a pill
    /// or a chat never ends up in the middle of a task, nor does an answer to it go to the models by mistake.
    /// </summary>
    public ObservableCollection<ChatEntry> SideEntries { get; } = [];

    private Func<string, Task>? _sidePendingReply;
    private bool _showingSide;
    private int _unreadSide;

    /// <summary>The side chat is on screen (set by the bubble): what the user writes goes there.</summary>
    public bool ShowingSide
    {
        get => _showingSide;
        set
        {
            if (_showingSide == value)
            {
                return;
            }

            _showingSide = value;
            if (value)
            {
                UnreadSide = 0;
            }

            Notify(nameof(ShowingSide));
            Notify(nameof(HasPendingReply));
        }
    }

    /// <summary>Messages posted in the side chat while it was not on screen.</summary>
    public int UnreadSide
    {
        get => _unreadSide;
        private set { _unreadSide = value; Notify(nameof(UnreadSide)); }
    }

    /// <summary>The messages of the chat on screen.</summary>
    public ObservableCollection<ChatEntry> VisibleEntries => _showingSide ? SideEntries : Entries;

    /// <summary>What the side chat is for, said when the user writes there without a question to answer.</summary>
    public const string SideChatHint =
        "Qui il mentore ti porta pillole, ripassi e chiacchierate, separati dalla chat di lavoro. "
        + "Per scrivere a Claude o al modello locale torna alla chat di lavoro (💬 in alto); "
        + "per cominciare qualcosa qui: /chiacchiera, /pillola o /ripasso (/nuova svuota questa chat).";

    /// <summary>Claude or the local model is working: the mentor waits.</summary>
    public bool IsBusy => _claudeBusy || _petState is PetState.Think or PetState.Work or PetState.Alert;

    private bool ClaudeBusy
    {
        set
        {
            if (_claudeBusy == value)
            {
                return;
            }

            _claudeBusy = value;
            AnswerStarted = value ? DateTimeOffset.Now : _localAnswer is null ? null : AnswerStarted;
            MarkWorkActivity();
            Notify(nameof(IsAnswering));
        }
    }

    private CancellationTokenSource? _localAnswer;

    /// <summary>A model is answering (Claude working or the local model streaming): the bubble offers to stop it.</summary>
    public bool IsAnswering => _claudeBusy || _localAnswer is not null;

    /// <summary>
    /// Last time the user worked in the chat with a model (a message to Claude or the local model, or an answer
    /// finishing). The mentor's off-topic messages wait a while after it.
    /// </summary>
    public DateTimeOffset? LastWorkActivity { get; private set; }

    private void MarkWorkActivity() => LastWorkActivity = DateTimeOffset.Now;

    /// <summary>When the current answer started, for the elapsed time shown while waiting.</summary>
    public DateTimeOffset? AnswerStarted { get; private set; }

    /// <summary>Who is answering right now (for the "working" strip).</summary>
    public Speaker? Answering => _claudeBusy ? Speaker.Claude : _localAnswer is not null ? Speaker.Local : null;

    /// <summary>Claude Code model aliases offered in the bubble ("" = the CLI's default).</summary>
    public static readonly IReadOnlyList<(string Alias, string Label)> ClaudeModels =
    [
        ("", "Predefinito"),
        ("opus", "Opus"),
        ("sonnet", "Sonnet"),
        ("haiku", "Haiku"),
    ];

    /// <summary>The Claude model in use ("" = the CLI's default).</summary>
    public string ClaudeModel => _options.Value.Claude.Model;

    /// <summary>The Ollama model the local answers use.</summary>
    public string LocalModel => _options.Value.Ollama.Model;

    /// <summary>Claude usage and average turn costs, for the hints next to the input.</summary>
    public ClaudeUsage Usage => _usage;

    public static string ClaudeModelLabel(string alias) =>
        ClaudeModels.FirstOrDefault(m => m.Alias.Equals(alias.Trim(), StringComparison.OrdinalIgnoreCase)).Label ?? alias.Trim();

    /// <summary>
    /// Switches Claude's model and Claude mode. A running CLI is stopped and restarted on the next message with the
    /// same session (--resume), so the conversation goes on with the new model. Refused while Claude is working.
    /// </summary>
    public async Task<bool> SetClaudeModelAsync(string model)
    {
        model = model.Trim();
        if (model.Equals("predefinito", StringComparison.OrdinalIgnoreCase) || model.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            model = "";
        }

        if (_claudeBusy)
        {
            AddSystem("Claude sta lavorando: cambia il modello quando ha finito (o premi ■ Ferma).");
            return false;
        }

        var changed = !string.Equals(_options.Value.Claude.Model, model, StringComparison.OrdinalIgnoreCase);
        _options.Value.Claude.Model = model;
        if (changed)
        {
            await StopClaudeProcessAsync();
        }

        Mode = ChatMode.Claude;
        AddSystem($"Ora parli con Claude · modello {ClaudeModelLabel(model).ToLowerInvariant()}"
                  + (changed && _claude is not null ? " (la conversazione continua)" : "")
                  + (_usage.AverageTurnCost(model) is not null ? $" · di solito costa {_usage.AverageText(model)}" : "")
                  + (_usage.AverageTurnCost(model) is >= 0.30m ? " 💸 Per domande veloci basta un modello più leggero." : "."));
        Notify(nameof(ClaudeModel));
        return true;
    }

    /// <summary>The automatic choice of Claude's model per message (saved with the settings, like the model).</summary>
    public bool AutoModel => _options.Value.Claude.AutoModel;

    public void SetAutoModel(bool on)
    {
        _options.Value.Claude.AutoModel = on;
        AddSystem(on
            ? "🤖 Scelta automatica del modello: Haiku per le domande brevi, Opus per /lavora e /analisi, "
              + $"{ClaudeModelLabel(ClaudeModel).ToLowerInvariant()} per il resto."
            : $"Scelta automatica spenta: Claude usa sempre {ClaudeModelLabel(ClaudeModel).ToLowerInvariant()}.");
        Notify(nameof(ClaudeModel));
    }

    /// <summary>Switches the Ollama model (and local mode); the local chat history is kept.</summary>
    public void SetLocalModel(string model)
    {
        model = model.Trim();
        if (model.Length == 0)
        {
            return;
        }

        _options.Value.Ollama.Model = model;
        Mode = ChatMode.Local;
        AddSystem($"Ora parli con il modello locale · {model}.");
        Notify(nameof(LocalModel));
    }

    /// <summary>Stops the answer in progress: the local stream is cancelled, Claude is interrupted (the session stays).</summary>
    public async Task StopAsync()
    {
        try
        {
            _localAnswer?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the local answer just finished
        }

        if (_claude is { IsRunning: true } && _claudeBusy)
        {
            await _claude.InterruptAsync();
        }
    }

    /// <summary>
    /// Posts a message of the mentor. With <paramref name="onReply"/> the next message the user writes
    /// (not a command) is passed to it instead of the models. <paramref name="offTopic"/> messages go to the side
    /// chat (<see cref="SideEntries"/>), and only an answer written there reaches them.
    /// </summary>
    public void PostMentor(string markdown, Func<string, Task>? onReply = null, IReadOnlyList<ChatAction>? actions = null, bool offTopic = false)
    {
        OnUi(() =>
        {
            var entries = offTopic ? SideEntries : Entries;
            ClearAwaitingReply(entries);
            if (offTopic)
            {
                _sidePendingReply = onReply;
            }
            else
            {
                _pendingReply = onReply;
            }

            var entry = new ChatEntry(Speaker.Mentor, markdown) { AwaitsReply = onReply is not null, Actions = actions ?? [] };
            ApplyQuickReplies(entry);
            entries.Add(entry);
            if (offTopic && !_showingSide)
            {
                UnreadSide++;
            }

            Notify(nameof(HasPendingReply));
            if (_petState == PetState.Idle)
            {
                PetState = PetState.Speak;
            }

            MentorPosted?.Invoke(markdown);
            if (!offTopic)
            {
                SaveToArchive();
            }
        });
    }

    /// <summary>Set by the app when a newer build is ready: "/aggiorna" restarts on it.</summary>
    public Action? UpdateRequested { get; set; }

    /// <summary>Set by the app: "/riavvia" closes the buddy and starts it again.</summary>
    public Action? RestartRequested { get; set; }

    /// <summary>Tells in the chat that a newer build of the buddy is ready.</summary>
    public void AnnounceNewerBuild(DateTime built) =>
        AddSystem($"🆕 È pronta una versione più nuova del Buddy (compilata alle {built:HH:mm}): scrivi /aggiorna per riavviare con quella.");

    // ---- Saved conversations ----------------------------------------------------------------------------------

    /// <summary>Where conversations are kept (set by the app); null keeps nothing.</summary>
    public ConversationArchive? Archive { get; set; }

    private string _conversationId = ConversationArchive.NewId();

    /// <summary>The Claude session of a reopened conversation, resumed by the next message to Claude.</summary>
    private string? _resumeSession;

    private void SaveToArchive() => OnUi(() => Archive?.Save(_conversationId, Entries.ToList(), _claude?.SessionId ?? _resumeSession));

    /// <summary>Saves the current conversation and starts an empty one (new Claude session, local history cleared).</summary>
    private async Task StartNewConversationAsync()
    {
        SaveToArchive();
        await CloseClaudeAsync();
        _local.Reset();
        _resumeSession = null;
        _conversationId = ConversationArchive.NewId();
    }

    /// <summary>
    /// Reopens a saved conversation: its messages come back and the next message to Claude resumes its session
    /// (the local model starts from a clean history). Refused while a model is answering.
    /// </summary>
    public async Task<bool> OpenSavedAsync(SavedConversation saved)
    {
        if (IsAnswering)
        {
            AddSystem("Aspetta che finisca la risposta in corso (o premi ■ Ferma) prima di riaprire un'altra conversazione.");
            return false;
        }

        await StartNewConversationAsync();
        _conversationId = saved.Id;
        _resumeSession = saved.ClaudeSessionId;
        OnUi(() =>
        {
            _pendingReply = null;
            Entries.Clear();
            foreach (var entry in saved.Entries)
            {
                // Tool calls come back grouped, also from conversations saved with one line per call.
                if (ToolActivityEntry.StepsOf(entry.Speaker, entry.Text) is { } steps)
                {
                    var activity = Entries.LastOrDefault() as ToolActivityEntry;
                    foreach (var step in steps)
                    {
                        if (activity is null)
                        {
                            Entries.Add(activity = new ToolActivityEntry(step) { Timestamp = entry.Timestamp });
                        }
                        else
                        {
                            activity.Add(step);
                        }
                    }

                    continue;
                }

                Entries.Add(new ChatEntry(entry.Speaker, entry.Text) { Timestamp = entry.Timestamp, IsPinned = entry.Pinned });
            }

            Entries.Add(new ChatEntry(Speaker.System, saved.ClaudeSessionId is null
                ? "Conversazione riaperta."
                : "Conversazione riaperta: il prossimo messaggio a Claude riprende da dove eravate rimasti."));
            Notify(nameof(HasPendingReply));
        });
        return true;
    }

    private void ClearAwaitingReply(ObservableCollection<ChatEntry>? entries = null) => OnUi(() =>
    {
        foreach (var entry in (entries ?? Entries).Where(e => e.AwaitsReply))
        {
            entry.AwaitsReply = false;
        }
    });

    /// <summary>The answer to a mentor's question when the user does not know: the mentor explains instead.</summary>
    public const string ExplainItReply = "Non lo so: spiegamelo tu, con un esempio.";

    /// <summary>"Spiegamelo tu" under the mentor's open question.</summary>
    public Task ExplainItAsync() => HasPendingReply ? SendAsync(ExplainItReply) : Task.CompletedTask;

    /// <summary>Quick follow-ups under a model's answer: the request, sent to the same model.</summary>
    public static readonly IReadOnlyList<(string Label, string Request)> FollowUps =
    [
        ("Più semplice", "Rispiegami la tua ultima risposta in modo più semplice, come a chi non conosce l'argomento."),
        ("Un esempio", "Fammi un esempio concreto di quello che hai appena spiegato, possibilmente legato al mio progetto."),
        ("Riassumi", "Riassumi la tua ultima risposta in 3 righe."),
    ];

    /// <summary>Sends a quick follow-up about <paramref name="answer"/> to the model that wrote it.</summary>
    public async Task FollowUpAsync(ChatEntry answer, string request)
    {
        // An older answer: say which one, by its beginning.
        if (Entries.LastOrDefault(e => e.Speaker == answer.Speaker && e is not PermissionEntry) != answer)
        {
            var start = answer.Text.ReplaceLineEndings(" ").Trim();
            request = request.Replace("la tua ultima risposta", "la tua risposta", StringComparison.Ordinal)
                .Replace("quello che hai appena spiegato", "quello che hai spiegato", StringComparison.Ordinal)
                + $" (la risposta che inizia con «{(start.Length > 120 ? start[..120] + "…" : start)}»)";
        }

        if (answer.Speaker == Speaker.Claude)
        {
            Mode = ChatMode.Claude;
            await AskClaudeAsync(request, attachment: null);
        }
        else
        {
            Mode = ChatMode.Local;
            await AskLocalAsync(request, attachment: null);
        }
    }

    /// <summary>The user opened the chat: the pet stops waving.</summary>
    public void AcknowledgeMentor()
    {
        if (_petState == PetState.Speak)
        {
            PetState = PetState.Idle;
        }
    }

    public void SetThinking(bool thinking) => OnUi(() =>
    {
        if (thinking && _petState is PetState.Idle or PetState.Speak)
        {
            PetState = PetState.Think;
        }
        else if (!thinking && _petState == PetState.Think)
        {
            PetState = PetState.Idle;
        }
    });

    public ObservableCollection<ChatEntry> Entries { get; } = [];

    public ChatMode Mode
    {
        get => _mode;
        private set { _mode = value; Notify(nameof(Mode)); }
    }

    public PetState PetState
    {
        get => _petState;
        private set
        {
            _happyTimer?.Cancel();
            _petState = value;
            Notify(nameof(PetState));
        }
    }

    /// <summary>Attachment that goes with the next message; cleared once sent.</summary>
    public Attachment? PendingAttachment
    {
        get => _pendingAttachment;
        set { _pendingAttachment = value; Notify(nameof(PendingAttachment)); }
    }

    /// <summary>Task of the current git branch, described from the vault.</summary>
    public TaskInfo? CurrentTask
    {
        get => _currentTask;
        private set
        {
            _currentTask = value;
            Notify(nameof(CurrentTask));
            Notify(nameof(TaskActions));
        }
    }

    /// <summary>Buttons for the current task, from the settings' templates with {id} replaced.</summary>
    public IReadOnlyList<string> TaskActions =>
        _currentTask is null
            ? []
            : _options.Value.Assistant.TaskActions.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Replace("{id}", _currentTask.Id)).ToList();

    /// <summary>Re-reads the branch and the vault; cheap, called when the bubble opens and before each message.</summary>
    public async Task RefreshCurrentTaskAsync()
    {
        var id = await _repo.GetBranchTaskIdAsync();
        var info = _commands.DescribeTask(id);
        if (info?.ToString() != _currentTask?.ToString())
        {
            OnUi(() => CurrentTask = info);
        }
    }

    public async Task SendAsync(string input)
    {
        input = input.Trim();
        var attachment = PendingAttachment;
        if (input.Length == 0 && attachment is null)
        {
            return;
        }

        if (_showingSide)
        {
            await SendSideAsync(input);
            return;
        }

        await RefreshCurrentTaskAsync();
        var (command, rest) = SplitCommand(input);

        if (_pendingReply is { } reply && (command is null || command == "/salta"))
        {
            _pendingReply = null;
            ClearAwaitingReply();
            Notify(nameof(HasPendingReply));
            Add(new ChatEntry(Speaker.User, input));
            if (command != "/salta" && !input.Equals("salta", StringComparison.OrdinalIgnoreCase))
            {
                await reply(input);
            }
            else
            {
                await reply("");
            }

            return;
        }

        if (command is not null && _options.Value.Assistant.IsDisabled(command))
        {
            Instant(input, DisabledText(command));
            return;
        }

        if (command is not null && Commands.Find(command) is { } registered
            && await RunAsync(registered, new CommandCall(command, rest, input, attachment)))
        {
            return;
        }

        // One of your request templates: its text goes to the model in use, like a typed message.
        if (command is not null && ExpandTemplate(command, rest) is { } expanded)
        {
            if (Mode == ChatMode.Claude)
            {
                await AskClaudeAsync(expanded, attachment);
            }
            else
            {
                await AskLocalAsync(expanded, attachment);
            }

            return;
        }

        // Any other "/" command is a Claude Code command or skill (e.g. /lavora), which only Claude understands.
        // A typo of a known command ("/chiacchera") is caught here instead of costing a Claude turn.
        var known = Claude.ClaudeSkills.Installed(_options.Value).Concat(Templates.Select(t => t.Name));
        if (command is not null && Commands.Closest(command, known) is { } meant)
        {
            Instant(input, $"Non conosco `{command}`: forse intendevi `{meant}`? (Scrivi `/` per vedere i comandi.)");
            return;
        }

        if (command is not null)
        {
            Mode = ChatMode.Claude;
        }

        if (Mode == ChatMode.Claude)
        {
            await AskClaudeAsync(input, attachment);
        }
        else
        {
            await AskLocalAsync(input, attachment);
        }
    }

    /// <summary>
    /// A message written in the side chat: the answer to the mentor's open question there, or one of the mentor's
    /// commands. Anything else is not for the models from here: a line says where to write it.
    /// </summary>
    private async Task SendSideAsync(string input)
    {
        if (input.Length == 0)
        {
            return; // only an attachment: it waits for the work chat
        }

        var (command, rest) = SplitCommand(input);
        if (_sidePendingReply is { } reply && (command is null || command == "/salta"))
        {
            _sidePendingReply = null;
            ClearAwaitingReply(SideEntries);
            Notify(nameof(HasPendingReply));
            AddSide(new ChatEntry(Speaker.User, input));
            await reply(command == "/salta" || input.Equals("salta", StringComparison.OrdinalIgnoreCase) ? "" : input);
            return;
        }

        if (command == "/nuova")
        {
            OnUi(() =>
            {
                _sidePendingReply = null;
                SideEntries.Clear();
                Notify(nameof(HasPendingReply));
            });
            return;
        }

        if (command is not null && _options.Value.Assistant.IsDisabled(command))
        {
            AddSide(new ChatEntry(Speaker.User, input));
            AddSide(new ChatEntry(Speaker.System, DisabledText(command)));
            return;
        }

        if (command is not null && Commands.Find(command) is { Command.Group: CommandGroup.Mentor } registered
            && await RunMentorCommandAsync(registered, new CommandCall(command, rest, input, null)))
        {
            return;
        }

        AddSide(new ChatEntry(Speaker.User, input));
        AddSide(new ChatEntry(Speaker.System, SideChatHint));
    }

    private void AddSide(ChatEntry entry) => OnUi(() => SideEntries.Add(entry));

    /// <summary>Runs a registered command; false when it let the message go on to the models.</summary>
    private Task<bool> RunAsync(RegisteredCommand registered, CommandCall call) =>
        registered.Command.Group == CommandGroup.Mentor ? RunMentorCommandAsync(registered, call) : registered.Run(call);

    /// <summary>
    /// Runs one of the mentor's commands. The user's line goes before the answer, in the chat the answer went to; an
    /// off-topic answer asked from the work chat (/pillola…) brings the side chat on screen.
    /// </summary>
    private async Task<bool> RunMentorCommandAsync(RegisteredCommand registered, CommandCall call)
    {
        var input = call.Input;
        var (work, side) = (Entries.Count, SideEntries.Count);
        if (!await registered.Run(call))
        {
            return false;
        }

        OnUi(() =>
        {
            var toSide = SideEntries.Count > side ? true : Entries.Count > work ? false : _showingSide;
            var entries = toSide ? SideEntries : Entries;
            entries.Insert(Math.Min(toSide ? side : work, entries.Count), new ChatEntry(Speaker.User, input));
            if (toSide && !_showingSide)
            {
                ShowingSide = true;
            }
        });
        return true;
    }

    /// <summary>The conversation's own "/" commands; the order here is the order of the suggestions and of /aiuto.</summary>
    private void RegisterCommands()
    {
        Commands.Add(new("/c", "passa a Claude (e invia il testo)", "[testo]"), async call =>
        {
            Mode = ChatMode.Claude;
            AddSystem(Phrases.SwitchedToClaude);
            if (call.Rest.Length > 0 || call.Attachment is not null)
            {
                await AskClaudeAsync(call.Rest, call.Attachment);
            }
            else if (_claude is not { IsRunning: true })
            {
                // Warn about a missing login right away, not after the user has typed the request.
                await EnsureLoggedInAsync();
            }
        });

        Commands.Add(new("/l", "torna al modello locale (e invia il testo)", "[testo]"), async call =>
        {
            Mode = ChatMode.Local;
            AddSystem(Phrases.SwitchedToLocal);
            if (call.Rest.Length > 0 || call.Attachment is not null)
            {
                await AskLocalAsync(call.Rest, call.Attachment);
            }
        });

        Commands.Add(new("/stato", "scheda del task (senza ID: quello del ramo git)", "[ID]"), call =>
        {
            var id = call.Rest.Length > 0 ? call.Rest.ToUpperInvariant() : _currentTask?.Id;
            Instant(call.Input, id is null ? "Nessun task nel nome del ramo: scrivi `/stato ID`." : _commands.Status(id));
            return Task.CompletedTask;
        });

        Commands.Add(new("/oggi", "le note di oggi (Stato attuale…)"), call => Done(() => Instant(call.Input, _commands.Today())));
        Commands.Add(new("/cerca", "cerca nel vault", "parole"), call => Done(() => Instant(call.Input, _commands.Search(call.Rest))));
        Commands.Add(new("/modifiche", "file modificati, raggruppati per area"),
            async call => Instant(call.Input, await _commands.ChangesAsync()));
        Commands.Add(new("/commit", "bozza del messaggio di commit (Claude)"), call => DraftAsync(call, commit: true));
        Commands.Add(new("/pr", "bozza di titolo e descrizione della PR (Claude)"), call => DraftAsync(call, commit: false));
        Commands.Add(new("/stop", "interrompe la risposta in corso (anche ■ Ferma)"), _ => StopAsync());
        Commands.Add(new("/nuova", "nuova conversazione; quella attuale resta salvata"), async _ =>
        {
            await StartNewConversationAsync();
            AddSystem(Phrases.NewConversation);
        });
        Commands.Add(new("/uso", "uso di Claude oggi e limiti del piano"), call => Done(() => Instant(call.Input, _usage.Describe())));
        Commands.Add(new("/modello", "mostra o cambia il modello (anche dal nome in alto)", "[opus|sonnet|haiku|locale <nome>]"),
            call => ModelCommandAsync(call.Input, call.Rest));
        Commands.Add(new("/riprendi", "dove ero rimasto: task, ultima conversazione, modifiche, test"), call => ResumeAsync(call.Input));
        Commands.Add(new("/riapri", "riapre una conversazione salvata", "ID", Hidden: true), async call =>
        {
            if (Archive?.List(ConversationArchive.MaxConversations).FirstOrDefault(c => c.Id == call.Rest) is { } toOpen)
            {
                await OpenSavedAsync(toOpen);
            }
            else
            {
                Instant(call.Input, "Non trovo quella conversazione (🕘 per l'elenco).");
            }
        });
        Commands.Add(new("/appunto", "aggiunge una riga a To-do Appunti (anche Shift+🎤)", "testo"),
            call => Done(() => Instant(call.Input, _commands.AddQuickNote(call.Rest))));
        Commands.Add(new("/modelli", "i tuoi modelli di richiesta", "[nome = testo]"),
            call => Done(() => TemplatesCommand(call.Input, call.Rest)));
        Commands.Add(new("/log", "ultimi avvisi ed errori del Buddy (tutto: ultime righe)", "[tutto]"),
            call => Done(() => Instant(call.Input, RecentLog(call.Rest.Equals("tutto", StringComparison.OrdinalIgnoreCase)))));
        Commands.Add(new("/aggiorna", "riavvia con la versione appena compilata"), call => Done(() =>
        {
            if (UpdateRequested is { } update)
            {
                Add(new ChatEntry(Speaker.User, call.Input));
                AddSystem("Riavvio con la versione nuova: ricompilo e riparto tra qualche secondo…");
                SaveToArchive();
                update();
            }
            else
            {
                Instant(call.Input, "Non c'è una versione nuova da usare (o il Buddy non gira dai sorgenti).");
            }
        }));
        Commands.Add(new("/riavvia", "chiude e riapre il Buddy"), call => Done(() =>
        {
            if (RestartRequested is { } restart)
            {
                Add(new ChatEntry(Speaker.User, call.Input));
                AddSystem("Riavvio il Buddy…");
                SaveToArchive();
                restart();
            }
            else
            {
                Instant(call.Input, "Non riesco a riavviare da qui: chiudi e riapri il Buddy.");
            }
        }));
        Commands.Add(new("/aiuto", "questo elenco"), _ => Done(() => AddSystem(HelpText())));

        // Answers the mentor's open question with nothing; reached only when there is none.
        Commands.Add(new("/salta", "ignora la domanda in sospeso", Group: CommandGroup.Mentor), _ => Done(() =>
        {
            const string nothing = "Nessuna domanda da saltare.";
            if (_showingSide)
            {
                AddSide(new ChatEntry(Speaker.System, nothing));
            }
            else
            {
                AddSystem(nothing);
            }
        }));
    }

    private static Task Done(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    /// <summary>"/aiuto": the conversation's commands, the mentor's names, and the shortcuts.</summary>
    internal string HelpText()
    {
        var mentor = Commands.All.Where(c => c.Group == CommandGroup.Mentor && c.Name != "/mentore").Select(c => c.Name).ToList();
        var mentorLine = mentor.Count == 0 ? "" : $"\nMentore: {string.Join(" · ", mentor)} (/mentore per i dettagli)";
        return $"Comandi:\n{Commands.Help(CommandGroup.Chat)}{mentorLine}\n{HelpFooter}";
    }

    /// <summary>"/commit" and "/pr": Claude drafts the message from the diff, warned about the files to leave out.</summary>
    private async Task DraftAsync(CommandCall call, bool commit)
    {
        var (prompt, message) = commit
            ? await _commands.CommitPromptAsync(_currentTask?.Id)
            : await _commands.PullRequestPromptAsync(_currentTask?.Id);
        if (prompt is null)
        {
            Instant(call.Input, message!);
            return;
        }

        // Local config and the like must stay out of the commit: say so, and tell Claude too.
        if (await _commands.DoNotCommitChangesAsync() is { Count: > 0 } excluded)
        {
            var list = string.Join(", ", excluded.Select(p => $"`{p}`"));
            AddSystem($"⚠ Tra le modifiche ci sono file da non committare: {string.Join(", ", excluded)}. "
                      + $"Lasciali fuori dal commit (per annullarli: git checkout -- {string.Join(' ', excluded)}).");
            prompt += $"\n\nQuesti file NON vanno nel commit (configurazione locale): {list}. Non descriverli e "
                      + "ricordami in una riga di escluderli.";
        }

        Mode = ChatMode.Claude;
        await AskClaudeAsync(call.Input, attachment: null, promptOverride: prompt);
    }

    /// <summary>
    /// "/modello" shows the models in use; "/modello sonnet" (or "claude sonnet") picks Claude's, "/modello locale
    /// qwen2.5:7b" the local one.
    /// </summary>
    private async Task ModelCommandAsync(string input, string argument)
    {
        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            Instant(input, $"""
                **Modelli in uso**
                - Claude: **{ClaudeModelLabel(ClaudeModel)}**
                - Locale (Ollama): **{LocalModel}**

                Per cambiarli: click sul nome del modello in alto, oppure `/modello opus|sonnet|haiku|predefinito`
                (Claude) e `/modello locale <nome>` (un modello installato in Ollama).
                """);
            return;
        }

        Add(new ChatEntry(Speaker.User, input));
        var target = parts[0].ToLowerInvariant();
        if (target is "locale" or "l" or "ollama")
        {
            if (parts.Length < 2)
            {
                AddSystem("Scrivi il nome del modello: `/modello locale llama3.2:3b`.");
                return;
            }

            SetLocalModel(parts[1]);
            return;
        }

        await SetClaudeModelAsync(target is "claude" or "c" ? (parts.Length > 1 ? parts[1] : "") : argument);
    }

    /// <summary>Sends one of the <see cref="TaskActions"/> (e.g. "/lavora NUSR-1") to Claude.</summary>
    public async Task RunTaskActionAsync(string action)
    {
        TaskActionRun?.Invoke(action);
        if (!_options.Value.Mentor.AsksApproachFor(action))
        {
            Mode = ChatMode.Claude;
            await AskClaudeAsync(action, attachment: null);
            return;
        }

        // Mentoring: think first, then let Claude review the approach before it starts.
        Add(new ChatEntry(Speaker.User, action));
        PostMentor(Mentor.MentorPrompts.ApproachQuestion, async approach =>
        {
            Mode = ChatMode.Claude;
            if (approach.Length == 0)
            {
                await AskClaudeAsync(action, attachment: null);
                return;
            }

            ApproachGiven?.Invoke(action, approach);
            await AskClaudeAsync($"{action} — con il mio approccio", attachment: null,
                promptOverride: Mentor.MentorPrompts.ApproachForClaude(action, approach));
        });
    }

    /// <summary>"Chiedi a Claude" on a local answer: the same question (and attachment) goes to Claude.</summary>
    public async Task EscalateAsync(ChatEntry localAnswer)
    {
        if (localAnswer.Question is not { } question)
        {
            return;
        }

        localAnswer.CanEscalate = false;
        Mode = ChatMode.Claude;
        AddSystem(Phrases.SwitchedToClaude);
        await AskClaudeAsync(question, localAnswer.QuestionAttachment);
    }

    internal static (string? Command, string Argument) SplitCommand(string input)
    {
        if (!input.StartsWith('/'))
        {
            return (null, input);
        }

        var space = input.IndexOf(' ');
        return space < 0
            ? (input.ToLowerInvariant(), "")
            : (input[..space].ToLowerInvariant(), input[(space + 1)..].Trim());
    }

    private void Instant(string request, string markdown)
    {
        Add(new ChatEntry(Speaker.User, request));
        Add(new ChatEntry(Speaker.Local, markdown));
        SaveToArchive();
    }

    private ChatEntry UserEntry(string text, Attachment? attachment) =>
        new(Speaker.User, attachment is null ? text : $"{text}\n📎 {attachment.Preview}".Trim()) { Images = attachment?.Images ?? [] };

    private async Task AskLocalAsync(string question, Attachment? attachment)
    {
        PendingAttachment = null;
        if (question.Length == 0)
        {
            question = "Spiega brevemente questo allegato.";
        }

        Add(UserEntry(question, attachment));
        var answer = new ChatEntry(Speaker.Local) { IsStreaming = true, Question = question, QuestionAttachment = attachment };
        Add(answer);
        PetState = PetState.Think;

        if (attachment?.Images.Count > 0)
        {
            AddSystem("Il modello locale non vede le immagini: per quelle usa Claude (/c).");
        }

        var stop = new CancellationTokenSource();
        _localAnswer = stop;
        AnswerStarted = DateTimeOffset.Now;
        MarkWorkActivity();
        Notify(nameof(IsAnswering));
        try
        {
            var attachmentText = attachment?.ToPromptText(_options.Value.Assistant.MaxAttachmentChars);
            await foreach (var delta in _local.AskAsync(question, _currentTask?.Id, attachmentText, stop.Token))
            {
                OnUi(() => answer.Text += delta);
            }

            if (_local.LastContext?.SourcesMarkdown() is { } sources)
            {
                OnUi(() => answer.Text += $"\n\n{sources}");
            }

            OnUi(() => answer.CanEscalate = true);
            PetState = PetState.Idle;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            OnUi(() =>
            {
                answer.Text = answer.Text.Length == 0 ? "_(interrotto)_" : answer.Text + " …\n\n_(interrotto)_";
                answer.CanEscalate = true;
            });
            PetState = PetState.Idle;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Ollama non risponde");
            // 404: Ollama is running but the configured model is not installed.
            var model = _options.Value.Ollama.Model;
            var message = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                ? $"Ollama è avviato ma il modello `{model}` non è installato: scaricalo con `ollama pull {model}` "
                  + "oppure scegline uno installato in Impostazioni → Modelli e percorsi."
                : $"Non riesco a parlare con Ollama ({ex.Message}). È avviato?";
            OnUi(() => answer.Text = message);
            PetState = PetState.Error;
        }
        finally
        {
            OnUi(() =>
            {
                ApplyQuickReplies(answer);
                answer.IsStreaming = false;
            });
            _localAnswer = null;
            stop.Dispose();
            if (!_claudeBusy)
            {
                AnswerStarted = null;
            }

            MarkWorkActivity();
            Notify(nameof(IsAnswering));
            SaveToArchive();
        }
    }

    /// <param name="text">What the user typed (shown in the chat).</param>
    /// <param name="promptOverride">Full prompt to send instead of <paramref name="text"/> (e.g. the /commit diff).</param>
    private async Task AskClaudeAsync(string text, Attachment? attachment, string? promptOverride = null)
    {
        if (_claudeBusy)
        {
            AddSystem(Phrases.ClaudeBusy);
            return;
        }

        PendingAttachment = null;
        if (text.Length == 0)
        {
            text = "Guarda questo allegato e dimmi cosa noti.";
        }

        Add(UserEntry(text, attachment));
        if (_claude is not { IsRunning: true } && !await EnsureLoggedInAsync())
        {
            return;
        }

        var prompt = promptOverride ?? text;
        if (attachment is not null)
        {
            prompt += "\n\n" + attachment.ToPromptText(_options.Value.Assistant.MaxAttachmentChars);
        }

        // Automatic choice: a short question goes to a light model, a task skill to the strongest; the CLI restarts on
        // the same session when the model changes.
        var model = ChooseModel(text, attachment, promptOverride);
        if (_claude is { IsRunning: true } running && !string.Equals(running.ModelOverride, model, StringComparison.OrdinalIgnoreCase))
        {
            await StopClaudeProcessAsync();
        }

        if (model is not null && _claude?.ModelOverride != model)
        {
            AddSystem($"🤖 modello automatico: {ClaudeModelLabel(model).ToLowerInvariant()}");
        }

        try
        {
            EnsureClaudeStarted(model);
            ClaudeBusy = true;
            _turnStarted = Stopwatch.GetTimestamp();
            _turnRequest = text;
            PetState = PetState.Work;
            await _claude!.SendAsync(prompt, attachment?.Images);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            ClaudeBusy = false;
            _log.LogError(ex, "Claude Code non si avvia ({Exe})", _options.Value.Claude.Executable);
            AddSystem($"Non riesco ad avviare Claude Code: {Claude.ClaudeExecutable.Describe(ex, _options.Value.Claude.Executable)}");
            PetState = PetState.Error;
        }
    }

    /// <summary>
    /// Checks the CLI login before starting a Claude process. An unknown result (check failed) lets the
    /// session start anyway: a real auth failure is still caught from the CLI's answer.
    /// </summary>
    private async Task<bool> EnsureLoggedInAsync()
    {
        PetState = PetState.Think;
        var state = await _auth.CheckAsync();
        if (state == ClaudeLoginState.LoggedOut)
        {
            AddSystem(ClaudeAuth.LoginHint);
            PetState = PetState.Error;
            return false;
        }

        if (state == ClaudeLoginState.Unknown)
        {
            AddSystem($"Non riesco a verificare il login di Claude Code: controlla che `{_options.Value.Claude.Executable}` "
                + "funzioni da terminale (impostazione Buddy:Claude:Executable). Provo comunque ad avviarlo.");
        }

        PetState = PetState.Idle;
        return true;
    }

    private void EnsureClaudeStarted(string? model = null)
    {
        if (_claude is { IsRunning: true })
        {
            return;
        }

        var previousSession = _claude?.SessionId ?? _resumeSession;
        _claude = new ClaudeSession(_options, AskPermissionAsync) { ModelOverride = model };
        _sessionCost = 0; // the CLI's total_cost_usd counts from the start of each process
        _claude.EventReceived += evt => OnUi(() => HandleClaudeEvent(evt));
        _claude.Start(previousSession);
    }

    private Task<PermissionDecision> AskPermissionAsync(PermissionRequest request)
    {
        var entry = new PermissionEntry(request);
        OnUi(() =>
        {
            CloseClaudeAnswer();
            Entries.Add(entry);
            PetState = PetState.Alert;
        });

        return entry.Decision.ContinueWith(t =>
        {
            OnUi(() => PetState = _claudeBusy ? PetState.Work : PetState.Idle);
            return t.Result;
        }, TaskScheduler.Default);
    }

    private void HandleClaudeEvent(ClaudeEvent evt)
    {
        switch (evt)
        {
            case ClaudeTextDelta delta:
                if (_claudeAnswer is null)
                {
                    _claudeAnswer = new ChatEntry(Speaker.Claude) { IsStreaming = true };
                    Entries.Add(_claudeAnswer);
                }

                _claudeAnswer.Text += delta.Text;
                break;

            case ClaudeToolUse tool:
                CloseClaudeAnswer();
                if (Entries.LastOrDefault() is ToolActivityEntry activity)
                {
                    activity.Add(tool.Summary); // a run of tool calls stays one line in the chat
                }
                else
                {
                    Entries.Add(new ToolActivityEntry(tool.Summary));
                }

                break;

            case ClaudeRateLimitChanged changed:
                var before = _usage.Limits.FirstOrDefault(l => l.Type == changed.Limit.Type);
                _usage.Update(changed.Limit);
                if ((changed.Limit.IsWarning || changed.Limit.IsBlocked) && before?.Status != changed.Limit.Status)
                {
                    Entries.Add(new ChatEntry(Speaker.System, $"⚠ {changed.Limit.Label}: {changed.Limit.LongStatus}"));
                }

                break;

            case ClaudeTurnCompleted done:
                if (Entries.LastOrDefault(e => e.Speaker == Speaker.Claude && e is not PermissionEntry) is { } finalAnswer)
                {
                    ApplyQuickReplies(finalAnswer);
                }

                var lastAnswer = Entries.LastOrDefault(e => e.Speaker == Speaker.Claude && e is not PermissionEntry)?.Text;
                CloseClaudeAnswer();
                ClaudeBusy = false;
                SaveToArchive();
                if (done.IsError)
                {
                    _log.LogWarning("Turno Claude finito con errore: {Result}", done.Result);
                }
                else
                {
                    _log.LogInformation("Turno Claude completato in {Seconds:0}s, costo {Cost}", Stopwatch.GetElapsedTime(_turnStarted).TotalSeconds, done.CostUsd);
                }

                if (done.IsError && ClaudeAuth.IsAuthError(done.Result))
                {
                    // The login expired while the session was open: explain how to fix it and drop the process,
                    // so the next message re-checks the login and starts a fresh CLI (resuming the same session).
                    Entries.Add(new ChatEntry(Speaker.System, ClaudeAuth.LoginHint));
                    _ = StopClaudeProcessAsync();
                }
                else if (done.IsError)
                {
                    Entries.Add(new ChatEntry(Speaker.System, $"⚠ {done.Result}"));
                }

                if (done.CostUsd is { } total)
                {
                    // total_cost_usd is the running total of the CLI process, not of this turn.
                    var cost = total >= _sessionCost ? total - _sessionCost : total;
                    _sessionCost = total;
                    _usage.AddTurn(cost, _claude?.ModelOverride ?? _options.Value.Claude.Model);
                    Entries.Add(new ChatEntry(Speaker.System, $"costo turno: ${cost:0.000} · {_usage.ShortText()}"));
                    CheckBudget();
                }

                if (done.IsError)
                {
                    PetState = PetState.Error;
                }
                else
                {
                    CelebrateBriefly();
                }

                var elapsed = Stopwatch.GetElapsedTime(_turnStarted);
                if (elapsed.TotalSeconds >= _options.Value.Assistant.NotifyAfterSeconds)
                {
                    LongTurnCompleted?.Invoke(new LongTurnCompleted(_turnRequest, done.IsError ? done.Result : lastAnswer, !done.IsError, elapsed));
                }
                break;

            case ClaudeProcessMessage { Exited: true } exited:
                _log.LogWarning("Processo Claude terminato: {Text}", exited.Text);
                CloseClaudeAnswer();
                if (_claudeBusy)
                {
                    ClaudeBusy = false;
                    Entries.Add(new ChatEntry(Speaker.System, exited.Text));
                    PetState = PetState.Error;
                }
                break;

            case ClaudeProcessMessage stderr:
                _log.LogWarning("Claude stderr: {Text}", stderr.Text);
                Entries.Add(new ChatEntry(Speaker.System, stderr.Text));
                break;
        }
    }

    /// <summary>Shows the happy face for a few seconds after Claude finished, then goes back to idle.</summary>
    private void CelebrateBriefly()
    {
        PetState = PetState.Happy;
        var timer = new CancellationTokenSource();
        _happyTimer = timer;
        _ = Task.Delay(TimeSpan.FromSeconds(3), timer.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                OnUi(() =>
                {
                    if (_petState == PetState.Happy)
                    {
                        PetState = PetState.Idle;
                    }
                });
            }
        }, TaskScheduler.Default);
    }

    private void CloseClaudeAnswer()
    {
        if (_claudeAnswer is not null)
        {
            _claudeAnswer.IsStreaming = false;
            _claudeAnswer = null;
        }
    }

    /// <summary>Stops the CLI process but keeps its session id, so the next start resumes the conversation.</summary>
    private async Task StopClaudeProcessAsync()
    {
        if (_claude is { } session)
        {
            await session.StopAsync();
        }
    }

    private async Task CloseClaudeAsync()
    {
        if (_claude is not null)
        {
            await _claude.DisposeAsync();
            _claude = null;
        }

        ClaudeBusy = false;
        PetState = PetState.Idle;
    }

    private void Add(ChatEntry entry) => OnUi(() => Entries.Add(entry));

    private PhraseSettings Phrases => _options.Value.Phrases;

    private void AddSystem(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Add(new ChatEntry(Speaker.System, text));
        }
    }

    /// <summary>
    /// The model for one message when the automatic choice is on: the strongest for the flow's task skills, a light one
    /// for a short plain question, otherwise null (the configured model).
    /// </summary>
    internal string? ChooseModel(string text, Attachment? attachment, string? promptOverride)
    {
        var claude = _options.Value.Claude;
        if (!claude.AutoModel)
        {
            return null;
        }

        var heavy = claude.EffectiveHeavyCommands();
        var (command, _) = SplitCommand(text.Trim());
        if (command is not null && heavy.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            return "opus";
        }

        var plainQuestion = command is null && attachment is null && promptOverride is null && text.Length <= 200 && !text.Contains('\n');
        return plainQuestion ? "haiku" : null;
    }

    private DateOnly? _budgetWarned;

    /// <summary>Once a day, past the budget: says so and offers a lighter model (unless already on the lightest).</summary>
    private void CheckBudget()
    {
        var budget = _options.Value.Claude.DailyBudgetUsd;
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (budget <= 0 || _usage.TodayCost < budget || _budgetWarned == today)
        {
            return;
        }

        _budgetWarned = today;
        var actions = new List<ChatAction>();
        if (!ClaudeModel.Equals("sonnet", StringComparison.OrdinalIgnoreCase) && !ClaudeModel.Equals("haiku", StringComparison.OrdinalIgnoreCase))
        {
            actions.Add(new ChatAction("Passa a Sonnet", "/modello sonnet", "Più economico, va bene per quasi tutto"));
        }

        if (!ClaudeModel.Equals("haiku", StringComparison.OrdinalIgnoreCase))
        {
            actions.Add(new ChatAction("Passa a Haiku", "/modello haiku", "Il più economico, per domande veloci"));
        }

        Suggest($"💸 Oggi hai speso {_usage.TodayCost:0.00} $ con Claude, oltre il budget di {budget:0.##} $ "
                + "(Impostazioni → Modelli e percorsi). Per il resto della giornata conviene un modello più leggero?", [.. actions]);
    }

    /// <summary>Where the log files are (set by the app), for /log.</summary>
    public string? LogFolder { get; set; }

    /// <summary>"/log": the latest warnings and errors of today's log; "/log tutto" the last lines whatever they are.</summary>
    private string RecentLog(bool everything)
    {
        var file = LogFolder is null || !Directory.Exists(LogFolder)
            ? null
            : new DirectoryInfo(LogFolder).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (file is null)
        {
            return "Nessun file di log trovato.";
        }

        List<string> lines;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        }
        catch (IOException ex)
        {
            return $"Non riesco a leggere il log: {ex.Message}";
        }

        var shown = everything
            ? lines.TakeLast(30).ToList()
            : lines.Where(l => l.Contains(" WRN ", StringComparison.Ordinal) || l.Contains(" ERR ", StringComparison.Ordinal)
                               || l.Contains(" CRT ", StringComparison.Ordinal)).TakeLast(15).ToList();
        if (shown.Count == 0)
        {
            return $"Nessun avviso o errore in `{file.Name}` 👍 (`/log tutto` per le ultime righe).";
        }

        var text = string.Join('\n', shown.Select(l => l.Length > 300 ? l[..300] + "…" : l));
        return $"**{(everything ? "Ultime righe" : "Ultimi avvisi ed errori")}** di `{file.Name}`:\n\n```\n{text}\n```";
    }

    // ---- Pins, notes from answers, request templates ---------------------------------------------------------

    /// <summary>Pins or unpins a message (kept in the strip at the top); saved with the conversation.</summary>
    public void TogglePin(ChatEntry entry)
    {
        entry.IsPinned = !entry.IsPinned;
        Notify(nameof(Pinned));
        SaveToArchive();
    }

    /// <summary>The pinned messages, oldest first.</summary>
    public IReadOnlyList<ChatEntry> Pinned => Entries.Where(e => e.IsPinned).ToList();

    /// <summary>The page a saved answer goes to by default (Pagine/Da Claude).</summary>
    public const string AnswersPage = "Da Claude";

    /// <summary>
    /// "💾 Salva come nota": the answer becomes a note in one of your pages (Pagine/&lt;page&gt;/), with the date and the
    /// task of the branch; returns the note's path, or null (and says why in the chat) when it cannot be written.
    /// </summary>
    public string? SaveAsNote(ChatEntry answer, string title, string page)
    {
        try
        {
            var path = Vault.CustomPages.CreateNote(_options.Value.ProjectPath, string.IsNullOrWhiteSpace(page) ? AnswersPage : page.Trim(),
                title, answer.Text, _currentTask?.Id);
            AddSystem($"💾 Salvata nel vault: {path.Replace('\\', '/')} (Bacheca → la pagina «{(string.IsNullOrWhiteSpace(page) ? AnswersPage : page.Trim())}»).");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddSystem($"Non riesco a salvare la nota: {ex.Message}");
            return null;
        }
    }

    /// <summary>The request templates as (name, text), from "nome = testo" lines of the settings.</summary>
    public IReadOnlyList<(string Name, string Text)> Templates =>
        _options.Value.Assistant.Templates
            .Select(t => t.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0)
            .Select(p => (Name: p[0].Trim().TrimStart('/').ToLowerInvariant(), Text: p[1].Trim()))
            .ToList();

    /// <summary>"/nome resto" with a template: the text to send ({testo} replaced by the rest, or the rest appended).</summary>
    internal string? ExpandTemplate(string command, string rest)
    {
        var name = command.TrimStart('/');
        var template = Templates.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (template.Name is null)
        {
            return null;
        }

        return template.Text.Contains("{testo}", StringComparison.OrdinalIgnoreCase)
            ? template.Text.Replace("{testo}", rest, StringComparison.OrdinalIgnoreCase).Trim()
            : rest.Length > 0 ? $"{template.Text}\n\n{rest}" : template.Text;
    }

    /// <summary>"/modelli": lists them; "/modelli nome = testo" adds (or replaces) one; "/modelli -nome" removes it.</summary>
    private void TemplatesCommand(string input, string argument)
    {
        var list = _options.Value.Assistant.Templates;
        if (argument.StartsWith('-') && argument.Length > 1)
        {
            var name = argument[1..].Trim().TrimStart('/');
            var removed = list.RemoveAll(t => t.Split('=', 2)[0].Trim().TrimStart('/').Equals(name, StringComparison.OrdinalIgnoreCase));
            Instant(input, removed > 0 ? $"Tolto il modello `/{name}`." : $"Non c'è un modello `/{name}`.");
            Notify(nameof(Templates));
            return;
        }

        if (argument.Contains('='))
        {
            var (name, text) = (argument.Split('=', 2)[0].Trim().TrimStart('/').ToLowerInvariant(), argument.Split('=', 2)[1].Trim());
            if (name.Length == 0 || name.Contains(' ') || text.Length == 0)
            {
                Instant(input, "Scrivi `/modelli nome = testo` (il nome senza spazi; nel testo `{testo}` è quello che scriverai dopo il comando).");
                return;
            }

            if (Commands.Find("/" + name) is not null)
            {
                Instant(input, $"`/{name}` è già un comando del Buddy: scegli un altro nome.");
                return;
            }

            list.RemoveAll(t => t.Split('=', 2)[0].Trim().TrimStart('/').Equals(name, StringComparison.OrdinalIgnoreCase));
            list.Add($"{name} = {text}");
            Instant(input, $"Salvato: `/{name}` → {text}");
            Notify(nameof(Templates));
            return;
        }

        var templates = Templates;
        Instant(input, templates.Count == 0
            ? "Nessun modello di richiesta. Creane uno: `/modelli stack = Spiegami questo stack trace e la causa probabile: {testo}`, poi usalo con `/stack …`."
            : "**I tuoi modelli di richiesta**\n\n" + string.Join('\n', templates.Select(t => $"- `/{t.Name}` → {t.Text}"))
              + "\n\nAggiungi con `/modelli nome = testo`, togli con `/modelli -nome`.");
    }

    /// <summary>A suggestion of the buddy with buttons (each sends its command), e.g. "passo a Sonnet?" [Sì].</summary>
    public void Suggest(string text, params ChatAction[] actions) =>
        Add(new ChatEntry(Speaker.System, text) { Actions = actions });

    /// <summary>A clicked action button: its command is sent like a typed one; the buttons of that message go away.</summary>
    /// <summary>
    /// Takes the replies proposed by the model out of the message (<see cref="QuickReplies"/>) and shows them as buttons
    /// that send that text; the line is removed even when the setting is off, so it never shows.
    /// </summary>
    private void ApplyQuickReplies(ChatEntry entry)
    {
        var (text, replies) = QuickReplies.Extract(entry.Text);
        if (text == entry.Text)
        {
            return;
        }

        entry.Text = text;
        if (_options.Value.Assistant.QuickReplies && replies.Count > 0)
        {
            entry.Actions = [.. entry.Actions, .. replies.Select(r => new ChatAction(r, r, "Rispondi così (o scrivi la tua risposta)"))];
        }
    }

    public Task RunActionAsync(ChatEntry entry, ChatAction action)
    {
        entry.Actions = [];
        return SendAsync(action.Command);
    }

    /// <summary>Adds the configured greeting, if any; called once at startup.</summary>
    public void Greet()
    {
        AddSystem(Phrases.Greeting);

        // Something to pick up again: offer it right away (the summary itself is built only when asked).
        if (Archive?.List(1).Count > 0)
        {
            Suggest("Vuoi riprendere da dove eri rimasto?", new ChatAction("↩ Dove ero rimasto?", "/riprendi",
                "Ultima conversazione, modifiche da committare e test manuali del task"));
        }
    }

    /// <summary>
    /// "/riprendi": where things stand, without a model — the task of the branch, the last conversation (on this task
    /// if there is one) with a button to reopen it, the changes not committed yet and the manual tests still to do.
    /// </summary>
    private async Task ResumeAsync(string input)
    {
        await RefreshCurrentTaskAsync();
        var task = _currentTask;
        var sb = new System.Text.StringBuilder("**Dove eri rimasto**\n\n");
        var actions = new List<ChatAction>();

        sb.AppendLine(task is null ? "- Nessun task nel nome del ramo git." : $"- 📌 Task: **{task}**");

        var saved = Archive?.List(30) ?? [];
        var last = (task is null ? null : saved.FirstOrDefault(c => c.Title.Contains(task.Id, StringComparison.OrdinalIgnoreCase)
                                                                     || c.Entries.Any(e => e.Text.Contains(task.Id, StringComparison.OrdinalIgnoreCase))))
                   ?? saved.FirstOrDefault(c => c.Id != _conversationId);
        if (last is not null)
        {
            var answer = last.Entries.LastOrDefault(e => e.Speaker is Speaker.Claude or Speaker.Local)?.Text.ReplaceLineEndings(" ").Trim();
            sb.AppendLine($"- 💬 Ultima conversazione: {last.Label}");
            if (answer is { Length: > 0 })
            {
                sb.AppendLine($"  > {(answer.Length > 220 ? answer[..220] + "…" : answer)}");
            }

            actions.Add(new ChatAction("Riapri la conversazione", $"/riapri {last.Id}"));
        }

        if (_repo.IsAvailable)
        {
            var changes = await _repo.GetChangesAsync();
            sb.AppendLine(changes.Count == 0
                ? "- ✅ Nessuna modifica da committare."
                : $"- ✏ {changes.Count} file modificati non ancora committati: {string.Join(", ", changes.Take(5).Select(c => $"`{Path.GetFileName(c.Path)}`"))}{(changes.Count > 5 ? "…" : "")}");
            if (changes.Count > 0)
            {
                actions.Add(new ChatAction("Bozza del commit", "/commit"));
            }
        }

        if (task is not null && _commands.ManualTestProgress(task.Id) is { } tests)
        {
            sb.AppendLine(tests.Done == tests.Total
                ? $"- 🧪 Test manuali: tutti fatti ({tests.Total}/{tests.Total}) ✅"
                : $"- 🧪 Test manuali: {tests.Done}/{tests.Total} fatti, ne mancano {tests.Total - tests.Done}.");
        }

        Add(new ChatEntry(Speaker.User, input));
        Add(new ChatEntry(Speaker.Local, sb.ToString().TrimEnd()) { Actions = actions });
    }

    private void Notify(string name) => OnUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        SaveToArchive();
        await CloseClaudeAsync();
    }
}

public sealed record LongTurnCompleted(string Request, string? Answer, bool Success, TimeSpan Elapsed);
