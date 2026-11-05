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
    public const string HelpText = """
        Comandi:
        /c [testo] — passa a Claude (e invia il testo)
        /l [testo] — torna al modello locale
        /stato [ID] — scheda del task (senza ID: quello del ramo git)
        /oggi — le note di oggi (Stato attuale…)
        /cerca parole — cerca nel vault
        /modifiche — file modificati, raggruppati per area
        /commit — bozza del messaggio di commit (Claude)
        /pr — bozza di titolo e descrizione della PR (Claude)
        /stop — interrompe la risposta di Claude
        /nuova — chiude la conversazione con Claude e ne apre una nuova
        /mentore — comandi del mentore (/checkin, /checkout, /pillola, /ripasso, /chiacchiera, /retro, /focus, /andamento)
        /aiuto — questo elenco
        Ctrl+Alt+B (o la scorciatoia impostata) apre la chat con gli appunti allegati; puoi anche trascinare file,
        testo o immagini sul personaggio.
        """;

    private readonly ILogger _log;
    private readonly LocalAssistant _local;
    private readonly ClaudeAuth _auth;
    private readonly AssistantCommands _commands;
    private readonly GitRepo _repo;
    private readonly IOptions<BuddySettings> _options;
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
        ILogger<Conversation>? logger = null)
    {
        _log = logger ?? NullLogger<Conversation>.Instance;
        _local = local;
        _auth = auth;
        _commands = commands;
        _repo = repo;
        _options = options;
        _ui = SynchronizationContext.Current;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when a Claude answer that took a while is complete (the UI decides whether to notify).</summary>
    public event Action<LongTurnCompleted>? LongTurnCompleted;

    /// <summary>Raised when the mentor posts a message (the UI may notify if the chat is closed).</summary>
    public event Action<string>? MentorPosted;

    /// <summary>Raised when the user described their approach before delegating (task action, approach).</summary>
    public event Action<string, string>? ApproachGiven;

    /// <summary>Extra "/" commands handled outside the conversation (the mentor's); returns true when handled.</summary>
    public Func<string, string, Task<bool>>? CommandHandler { get; set; }

    private Func<string, Task>? _pendingReply;

    /// <summary>True when the next message answers a question of the mentor.</summary>
    public bool HasPendingReply => _pendingReply is not null;

    /// <summary>Claude or the local model is working: the mentor waits.</summary>
    public bool IsBusy => _claudeBusy || _petState is PetState.Think or PetState.Work or PetState.Alert;

    /// <summary>
    /// Posts a message of the mentor. With <paramref name="onReply"/> the next message the user writes
    /// (not a command) is passed to it instead of the models.
    /// </summary>
    public void PostMentor(string markdown, Func<string, Task>? onReply = null)
    {
        OnUi(() =>
        {
            _pendingReply = onReply;
            Entries.Add(new ChatEntry(Speaker.Mentor, markdown));
            Notify(nameof(HasPendingReply));
            if (_petState == PetState.Idle)
            {
                PetState = PetState.Speak;
            }

            MentorPosted?.Invoke(markdown);
        });
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

        await RefreshCurrentTaskAsync();
        var (command, rest) = SplitCommand(input);

        if (_pendingReply is { } reply && (command is null || command == "/salta"))
        {
            _pendingReply = null;
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

        if (command is not null && CommandHandler is { } handler)
        {
            // The handler may post its answer right away: the user's line is inserted before it.
            var position = Entries.Count;
            if (await handler(command, rest))
            {
                OnUi(() => Entries.Insert(Math.Min(position, Entries.Count), new ChatEntry(Speaker.User, input)));
                return;
            }
        }

        switch (command)
        {
            case "/c":
                Mode = ChatMode.Claude;
                AddSystem(Phrases.SwitchedToClaude);
                if (rest.Length > 0 || attachment is not null)
                {
                    await AskClaudeAsync(rest, attachment);
                }
                else if (_claude is not { IsRunning: true })
                {
                    // Warn about a missing login right away, not after the user has typed the request.
                    await EnsureLoggedInAsync();
                }
                return;

            case "/l":
                Mode = ChatMode.Local;
                AddSystem(Phrases.SwitchedToLocal);
                if (rest.Length > 0 || attachment is not null)
                {
                    await AskLocalAsync(rest, attachment);
                }
                return;

            case "/stato":
                var id = rest.Length > 0 ? rest.ToUpperInvariant() : _currentTask?.Id;
                Instant(input, id is null ? "Nessun task nel nome del ramo: scrivi `/stato ID`." : _commands.Status(id));
                return;

            case "/oggi":
                Instant(input, _commands.Today());
                return;

            case "/cerca":
                Instant(input, _commands.Search(rest));
                return;

            case "/modifiche":
                Instant(input, await _commands.ChangesAsync());
                return;

            case "/commit":
            case "/pr":
                var (prompt, message) = command == "/commit"
                    ? await _commands.CommitPromptAsync(_currentTask?.Id)
                    : await _commands.PullRequestPromptAsync(_currentTask?.Id);
                if (prompt is null)
                {
                    Instant(input, message!);
                    return;
                }

                Mode = ChatMode.Claude;
                await AskClaudeAsync(input, attachment: null, promptOverride: prompt);
                return;

            case "/stop":
                if (_claude is { IsRunning: true } && _claudeBusy)
                {
                    await _claude.InterruptAsync();
                }
                return;

            case "/nuova":
                await CloseClaudeAsync();
                _local.Reset();
                AddSystem(Phrases.NewConversation);
                return;

            case "/aiuto":
                AddSystem(HelpText);
                return;
        }

        // Any other "/" command is a Claude Code command or skill (e.g. /lavora), which only Claude understands.
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

    /// <summary>Sends one of the <see cref="TaskActions"/> (e.g. "/lavora NUSR-1") to Claude.</summary>
    public async Task RunTaskActionAsync(string action)
    {
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
    }

    private ChatEntry UserEntry(string text, Attachment? attachment) =>
        new(Speaker.User, attachment is null ? text : $"{text}\n📎 {attachment.Preview}".Trim());

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

        try
        {
            var attachmentText = attachment?.ToPromptText(_options.Value.Assistant.MaxAttachmentChars);
            await foreach (var delta in _local.AskAsync(question, _currentTask?.Id, attachmentText))
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
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Ollama non risponde");
            OnUi(() => answer.Text = $"Non riesco a parlare con Ollama ({ex.Message}). È avviato?");
            PetState = PetState.Error;
        }
        finally
        {
            OnUi(() => answer.IsStreaming = false);
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

        try
        {
            EnsureClaudeStarted();
            _claudeBusy = true;
            _turnStarted = Stopwatch.GetTimestamp();
            _turnRequest = text;
            PetState = PetState.Work;
            await _claude!.SendAsync(prompt, attachment?.Images);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _claudeBusy = false;
            _log.LogError(ex, "Claude Code non si avvia ({Exe})", _options.Value.Claude.Executable);
            AddSystem($"Non riesco ad avviare Claude Code: {ex.Message}");
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

    private void EnsureClaudeStarted()
    {
        if (_claude is { IsRunning: true })
        {
            return;
        }

        var previousSession = _claude?.SessionId;
        _claude = new ClaudeSession(_options, AskPermissionAsync);
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
                Entries.Add(new ChatEntry(Speaker.System, $"🔧 {tool.Summary}"));
                break;

            case ClaudeTurnCompleted done:
                var lastAnswer = Entries.LastOrDefault(e => e.Speaker == Speaker.Claude && e is not PermissionEntry)?.Text;
                CloseClaudeAnswer();
                _claudeBusy = false;
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

                if (done.CostUsd is { } cost)
                {
                    Entries.Add(new ChatEntry(Speaker.System, $"costo turno: ${cost:0.000}"));
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
                    _claudeBusy = false;
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

        _claudeBusy = false;
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

    /// <summary>Adds the configured greeting, if any; called once at startup.</summary>
    public void Greet() => AddSystem(Phrases.Greeting);

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

    public async ValueTask DisposeAsync() => await CloseClaudeAsync();
}

public sealed record LongTurnCompleted(string Request, string? Answer, bool Success, TimeSpan Elapsed);
