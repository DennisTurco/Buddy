using System.Text.Json;
using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Buddy.Core.Repo;
using Buddy.Core.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Mentor;

/// <summary>What the mentor needs to know about the user's activity at the PC.</summary>
public interface IActivityMonitor
{
    /// <summary>Continuous time the user has been active at the PC (no idle gap of a few minutes).</summary>
    TimeSpan ContinuousActivity { get; }

    /// <summary>Why the user should not be interrupted right now (in a call, full screen…), or null.</summary>
    string? BusyReason { get; }
}

/// <summary>
/// Runs the mentor: every minute asks <see cref="MentorSchedule"/> what is due and posts it in the chat; answers are
/// written to the journal by Claude (<see cref="ClaudeOneShot"/>), which also replies as a mentor.
/// Also handles the manual commands (/checkin, /checkout, /pillola, /ripasso, /chiacchiera, /retro, /focus,
/// /andamento) and keeps the weekly counters shown in the trends.
/// </summary>
public sealed class MentorService : IDisposable
{
    public const string Commands = """
        Mentore:
        /checkin · /checkout · /pillola · /ripasso · /chiacchiera · /retro — li avvia subito
        /focus [minuti] — blocco di concentrazione (niente messaggi del mentore fino alla fine)
        /andamento — le ultime settimane in numeri (con le competenze)
        /competenze — cosa sai bene e cosa ripassare, dalle pillole
        /obiettivi — gli obiettivi del trimestre (/obiettivi nuovi per sceglierli)
        /salta — ignora la domanda in sospeso
        """;

    private static readonly string DefaultStatePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Buddy", "mentor-state.json");

    /// <summary>Where the schedule and the counters are kept (a test passes its own file).</summary>
    private readonly string StatePath;

    private readonly Conversation _conversation;
    private readonly ClaudeOneShot _claude;
    private readonly VaultIndex _vault;
    private readonly GitRepo _repo;
    private readonly IActivityMonitor _activity;
    private readonly TimeProvider _clock;
    private readonly BuddySettings _settings;
    private readonly ILogger _log;
    private readonly MentorSchedule _schedule;
    private readonly MentorState _state;
    private readonly Random _random = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private ITimer? _timer;
    private DateTime? _focusUntil;
    private string? _lastBusyReason;

    public MentorService(
        Conversation conversation,
        ClaudeOneShot claude,
        VaultIndex vault,
        GitRepo repo,
        IActivityMonitor activity,
        TimeProvider clock,
        IOptions<BuddySettings> options,
        ILogger<MentorService>? logger = null,
        string? statePath = null)
    {
        StatePath = statePath ?? DefaultStatePath;
        _conversation = conversation;
        _claude = claude;
        _vault = vault;
        _repo = repo;
        _activity = activity;
        _clock = clock;
        _settings = options.Value;
        _log = logger ?? NullLogger<MentorService>.Instance;
        _schedule = new MentorSchedule(_settings.Mentor);
        _state = LoadState();

        _conversation.CommandHandler = HandleCommandAsync;
        _conversation.ApproachGiven += (action, approach) => _ = SaveApproachAsync(action, approach);
        _conversation.TaskActionRun += OnTaskDelegated;
    }

    private MentorSettings Settings => _settings.Mentor;

    private bool HasJournal => Directory.Exists(Settings.JournalPath);

    private DateTime Now => _clock.GetLocalNow().DateTime;

    private string Today => Now.ToString("yyyy-MM-dd (dddd)", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));

    private WeekStats Week => _state.Week(Now);

    public void Start()
    {
        if (!Settings.Enabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(Settings.JournalPath) && !HasJournal)
        {
            try
            {
                Directory.CreateDirectory(Settings.JournalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Cartella del diario non creata: {Path}", Settings.JournalPath);
            }
        }

        _timer = _clock.CreateTimer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Counts the active minute, then delivers at most one due event: never while Claude is working, during a focus
    /// block or while the user is busy (call, full screen…); due events simply wait.
    /// </summary>
    internal async Task TickAsync()
    {
        try
        {
            if (_activity.ContinuousActivity > TimeSpan.Zero)
            {
                Week.ActiveMinutes++;
                var day = DateOnly.FromDateTime(Now);
                if (_state.ActiveDay != day)
                {
                    _state.ActiveDay = day;
                    _state.TodayActiveMinutes = 0;
                }

                _state.TodayActiveMinutes++;
            }

            if (_conversation.IsBusy || (_focusUntil is { } until && Now < until))
            {
                SaveState();
                return;
            }

            var busy = Settings.QuietWhenBusy ? _activity.BusyReason : null;
            if (busy != _lastBusyReason)
            {
                _log.LogInformation(busy is null ? "Utente di nuovo libero" : "Mentore in silenzio: {Reason}", busy);
                _lastBusyReason = busy;
            }

            if (busy is not null)
            {
                SaveState();
                return;
            }

            if (_focusUntil is not null)
            {
                _focusUntil = null;
                Ask("Fine del blocco di focus. ⏱ Com'è andato? Cosa ti ha distratto, se qualcosa?", "fine blocco di focus");
                return;
            }

            PostGuideNudge();
            PostReminders();
            TrackTasksInReview();
            if (!IsWorkingInChat && _state.PendingTaskRetros.Count > 0 && Settings.Enabled)
            {
                var task = _state.PendingTaskRetros[0];
                _state.PendingTaskRetros.RemoveAt(0);
                SaveState();
                await TaskRetroAsync(task);
                return;
            }

            var due = _schedule.Due(Now, _state, _activity.ContinuousActivity, _random);

            // Just worked with a model in the chat: still on the task, so the off-topic ones wait (not marked done).
            if (IsWorkingInChat)
            {
                due = due.Where(e => !IsOffTopic(e)).ToList();
            }

            if (due.Count == 0)
            {
                SaveState();
                return;
            }

            var next = due[0];
            MentorSchedule.MarkDone(next, Now, _state);
            SaveState();
            await RunAsync(next);
        }
        catch (Exception ex)
        {
            // A timer callback must never bring the app down.
            _log.LogError(ex, "Errore nel giro del mentore");
        }
    }

    /// <summary>
    /// Notices tasks that arrived in "Da controllare" since the last look and queues a mini-retro for each. The first
    /// look only records what is there, so an existing backlog is not asked about.
    /// </summary>
    internal void TrackTasksInReview()
    {
        if (!_vault.IsAvailable)
        {
            return;
        }

        var inReview = ProjectBoard.Load(_vault, _settings.TaskIdPattern).Columns
            .Where(c => c.Key == "controllare")
            .SelectMany(c => c.Items)
            .Select(i => i.Id)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_state.InReview is { } seen)
        {
            foreach (var id in inReview.Where(id => !seen.Contains(id, StringComparer.OrdinalIgnoreCase)
                                                     && !_state.PendingTaskRetros.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                _log.LogInformation("Mentore: {Task} passato in Da controllare, retro in coda", id);
                _state.PendingTaskRetros.Add(id);
                OfferChecklist(id);
            }
        }

        _state.InReview = inReview;
    }

    // ---- Check-out draft and quarter goals --------------------------------------------------------------------

    /// <summary>
    /// The evening question with a draft of the day already filled in from what the buddy saw — tasks closed or sent to
    /// review, commits, the pill, hours at the PC, the chats — so the answer is a correction, not a blank page.
    /// </summary>
    internal async Task<string> CheckOutWithDraftAsync()
    {
        var today = DateOnly.FromDateTime(Now);
        var lines = new List<string>();
        if (_vault.IsAvailable)
        {
            foreach (var column in ProjectBoard.Load(_vault, _settings.TaskIdPattern).Columns.Where(c => c.Key is "fatti" or "controllare"))
            {
                foreach (var item in column.Items.Where(i => i.Date == today))
                {
                    lines.Add($"{(column.Key == "fatti" ? "✅ chiuso" : "🔍 in revisione")} {item.Id} · {item.Title.Replace("`", "")}");
                }
            }
        }

        if (_repo.IsAvailable)
        {
            try
            {
                var commits = (await _repo.GetLogAsync(TimeSpan.FromDays(1))).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => l.StartsWith(today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    .Select(l => l[11..]).ToList();
                if (commits.Count > 0)
                {
                    lines.Add($"⎇ {commits.Count} commit: {string.Join("; ", commits.Take(3))}{(commits.Count > 3 ? "…" : "")}");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                // no commits in the draft
            }
        }

        var pills = Path.Combine(Settings.JournalPath, "Pillole.md");
        if (File.Exists(pills) && File.ReadLines(pills).FirstOrDefault(l => l.StartsWith($"- {today:yyyy-MM-dd} ·", StringComparison.Ordinal)) is { } pill)
        {
            lines.Add($"💡 pillola: {pill.Split(" · ").Skip(2).FirstOrDefault()?.Split(" — ")[0] ?? pill}");
        }

        if (_state.ActiveDay == today && _state.TodayActiveMinutes >= 30)
        {
            lines.Add($"⌨ circa {_state.TodayActiveMinutes / 60.0:0.#} ore attive al PC");
        }

        var chats = _conversation.Archive?.List(15).Where(c => DateOnly.FromDateTime(c.Updated.LocalDateTime) == today).Select(c => c.Title).Take(4).ToList() ?? [];
        if (chats.Count > 0)
        {
            lines.Add($"💬 in chat: {string.Join("; ", chats)}");
        }

        return lines.Count == 0
            ? MentorPrompts.CheckOut
            : $"Fine giornata. Ecco una **bozza del diario** di oggi, da quello che ho visto:\n\n{string.Join('\n', lines.Select(l => "- " + l))}\n\n"
              + "Correggila o completala: cosa è andato bene, cosa ti ha bloccato o rallentato, cosa faresti diversamente domani, "
              + "e come ti senti da 1 a 5.";
    }

    /// <summary>"## Obiettivi del trimestre" of Profilo.md, or null when it has none yet.</summary>
    private string? QuarterGoals()
    {
        var profile = Path.Combine(Settings.JournalPath, "Profilo.md");
        if (!File.Exists(profile))
        {
            return null;
        }

        var text = File.ReadAllText(profile).ReplaceLineEndings("\n");
        var start = text.IndexOf("## Obiettivi del trimestre", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var body = text[(start + "## Obiettivi del trimestre".Length)..];
        var end = body.IndexOf("\n## ", StringComparison.Ordinal);
        var section = (end < 0 ? body : body[..end]).Trim();
        return section.Length == 0 ? null : section;
    }

    /// <summary>"/obiettivi": shows the quarter's goals, or asks for two or three and has them written in Profilo.md.</summary>
    private void GoalsCommand(string argument)
    {
        var goals = QuarterGoals();
        if (goals is not null && !argument.Equals("nuovi", StringComparison.OrdinalIgnoreCase))
        {
            _conversation.PostMentor($"**Obiettivi del trimestre** (in Profilo.md)\n\n{goals}\n\nLa retro del venerdì misura la settimana su questi. "
                + "Per cambiarli: `/obiettivi nuovi`.");
            return;
        }

        var quarterEnd = new DateOnly(Now.Year, (Now.Month - 1) / 3 * 3 + 3, 1).AddMonths(1).AddDays(-1);
        var question = $"Scegliamo **2 o 3 obiettivi** per questo trimestre (fino al {quarterEnd:d MMMM}). Concreti e verificabili: "
                       + "per esempio «fare da solo con /guidami un task su due», «capire come funziona la nuova sincro fino a spiegarla al team», "
                       + "«stimare con un errore sotto il 30%». Quali sono i tuoi?";
        _conversation.PostMentor(question, answer => answer.Length == 0
            ? Task.CompletedTask
            : SaveWithClaudeAsync(MentorPrompts.SaveQuarterGoals(Settings, Today, quarterEnd, answer), "obiettivi del trimestre", [new(question, answer)]));
    }

    // ---- Skills map and delegation ----------------------------------------------------------------------------

    private string SkillsMarkdown()
    {
        var path = Path.Combine(Settings.JournalPath, "Pillole.md");
        try
        {
            return SkillsMap.ToMarkdown(File.Exists(path) ? SkillsMap.Parse(File.ReadAllText(path)) : []);
        }
        catch (IOException ex)
        {
            return $"Non riesco a leggere Pillole.md: {ex.Message}";
        }
    }

    /// <summary>"Questa settimana: 4 task delegati a Claude, 1 fatto con /guidami." (empty when none).</summary>
    private string DelegationLine()
    {
        var week = Week;
        return week.Delegated + week.Guided == 0 ? ""
            : $"Questa settimana: {week.Delegated} task delegat{(week.Delegated == 1 ? "o" : "i")} a Claude (/lavora), "
              + $"{week.Guided} fatt{(week.Guided == 1 ? "o" : "i")} da te con /guidami.\n\n";
    }

    private string? _guideNudge;

    /// <summary>A task button or /lavora: counted, and maybe a nudge to do the next one with a guide.</summary>
    internal void OnTaskDelegated(string action)
    {
        var (command, argument) = Conversation.SplitCommand(action);
        if (command == "/lavora")
        {
            Week.Delegated++;
            SaveState();
            MaybeSuggestGuide(argument);
        }
        else if (command == "/guidami")
        {
            Week.Guided++;
            SaveState();
        }
    }

    /// <summary>
    /// Three tasks handed to Claude in a week and none done with /guidami: once a week, after the current turn, the
    /// mentor suggests doing the next one by yourself with a guide. Delegating is fine; never practising is not.
    /// </summary>
    private void MaybeSuggestGuide(string task)
    {
        var week = Week;
        if (week.Delegated >= 3 && week.Guided == 0 && !week.GuideSuggested && ClaudeSkills.Installed(_settings).Contains("guidami"))
        {
            week.GuideSuggested = true;
            SaveState();
            _guideNudge = task.Length > 0 ? task.Split(' ')[0] : "";
        }
    }

    private void PostGuideNudge()
    {
        if (_guideNudge is not { } task)
        {
            return;
        }

        _guideNudge = null;
        var actions = task.Length > 0
            ? new[] { new ChatAction($"🧭 /guidami {task}", $"/guidami {task}", "Lo fai tu, con spiegazioni e indizi passo passo") }
            : [];
        _conversation.PostMentor($"Questa settimana hai già delegato {Week.Delegated} task a Claude. Va benissimo per andare veloci, "
            + "ma si impara facendo: il prossimo (o questo, se non è urgente) prova a farlo tu con **/guidami**, io ti accompagno.", actions: actions);
    }

    /// <summary>Cards whose 🔔 reminder is due today (or was missed): brought back once, with a button to look at them.</summary>
    internal void PostReminders()
    {
        if (!_vault.IsAvailable)
        {
            return;
        }

        var today = DateOnly.FromDateTime(Now);
        foreach (var reminder in Reminders.Due(ProjectBoard.Load(_vault, _settings.TaskIdPattern), today))
        {
            var key = reminder.Item.Id ?? reminder.Item.Raw.Split('\n')[0];
            if (_state.Reminded.TryGetValue(key, out var done) && done == reminder.Day)
            {
                continue;
            }

            _state.Reminded[key] = reminder.Day;
            var what = reminder.Item.Id is { } id ? $"**{id}** · {reminder.Item.Title}" : reminder.Item.Title;
            var late = reminder.Day < today ? $" (era per il {reminder.Day:dd/MM})" : "";
            _conversation.Suggest($"🔔 Promemoria{late}: {what.Replace("`", "")} — in «{reminder.Column.Title}».",
                reminder.Item.Id is { } taskId
                    ? [new ChatAction("Scheda del task", $"/stato {taskId}"), new ChatAction("Contesto con Claude", $"/contesto {taskId}")]
                    : []);
        }

        foreach (var stale in _state.Reminded.Where(r => r.Value.DayNumber < today.DayNumber - 60).Select(r => r.Key).ToList())
        {
            _state.Reminded.Remove(stale);
        }

        SaveState();
    }

    /// <summary>
    /// A task just went to review and has no manual checklist yet: offers to have Claude write it from the changes
    /// (the flow's /test-manuali skill), right away since it is part of the work, not a mentor's aside.
    /// </summary>
    private void OfferChecklist(string id)
    {
        var commands = new AssistantCommands(_vault, _repo, Microsoft.Extensions.Options.Options.Create(_settings));
        if (commands.ManualTestProgress(id) is not null || !ClaudeSkills.Installed(_settings).Contains("test-manuali"))
        {
            return;
        }

        _conversation.Suggest($"🔍 {id} è passato in «Da controllare» e non ha ancora i test manuali. Creo la checklist dalle modifiche?",
            new ChatAction("🧪 Crea la checklist", $"/test-manuali {id}", "Claude la scrive in Test manuali/ dal diff del task"));
    }

    /// <summary>One question on the task just finished, saved to the journal with the answer.</summary>
    private async Task TaskRetroAsync(string id)
    {
        var task = new AssistantCommands(_vault, _repo, Microsoft.Extensions.Options.Options.Create(_settings)).DescribeTask(id)?.ToString() ?? id;
        var question = await GenerateAsync(MentorPrompts.TaskRetro(Settings, Today, task))
            ?? $"🎉 {task} è in revisione. Mentre è fresco: cosa ti ha sorpreso di questo task, e cosa rifaresti diversamente?";
        Ask(question, $"fine task {id}");
    }

    /// <summary>The task of the branch and the files changed, for pills tied to the real work (null when unknown).</summary>
    private async Task<string?> CurrentWorkAsync()
    {
        try
        {
            var lines = new List<string>();
            if (_conversation.CurrentTask is { } task)
            {
                lines.Add($"- Task: {task}");
            }

            if (_repo.IsAvailable)
            {
                var files = (await _repo.GetChangesAsync()).Select(f => f.Path).Take(15).ToList();
                if (files.Count > 0)
                {
                    lines.Add($"- File modificati: {string.Join(", ", files)}");
                }
            }

            return lines.Count == 0 ? null : string.Join('\n', lines);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Pills, reviews and friendly chats: nice to have, never while you are on a task.</summary>
    internal static bool IsOffTopic(MentorEvent mentorEvent) => mentorEvent is MentorEvent.Pill or MentorEvent.Review or MentorEvent.Chat;

    private bool IsWorkingInChat =>
        Settings.QuietAfterChatMinutes > 0
        && _conversation.LastWorkActivity is { } last
        && DateTimeOffset.Now - last < TimeSpan.FromMinutes(Settings.QuietAfterChatMinutes);

    private async Task RunAsync(MentorEvent mentorEvent)
    {
        if (!await _busy.WaitAsync(0))
        {
            return;
        }

        try
        {
            _log.LogInformation("Mentore: {Event}", mentorEvent);
            switch (mentorEvent)
            {
                case MentorEvent.CheckIn:
                    Week.CheckIns++;
                    var greeting = await GenerateAsync(MentorPrompts.CheckIn(Settings, Priorities()))
                        ?? $"Buongiorno! 🌅 Priorità di oggi dal vault:\n\n{Priorities()}\n\nSu cosa vuoi concentrarti per rendere la giornata riuscita?";
                    Ask(greeting, "check-in");
                    break;

                case MentorEvent.CheckOut:
                    Week.CheckOuts++;
                    Ask(await CheckOutWithDraftAsync(), "check-out");
                    break;

                case MentorEvent.Chat:
                    Week.Chats++;
                    // Off-topic chats only when there are interests to talk about.
                    var kinds = Settings.Interests.Count > 0 ? MentorPrompts.ChatKinds : MentorPrompts.ChatKinds.Where(k => k != "fuori tema").ToList();
                    var chatKind = kinds[_random.Next(kinds.Count)];
                    var interest = Settings.Interests.Count > 0 ? Settings.Interests[_random.Next(Settings.Interests.Count)] : null;
                    var question = await GenerateAsync(MentorPrompts.Chat(Settings, Today, chatKind, interest))
                        ?? (chatKind == "fuori tema" && interest is not null
                            ? $"Pausa chiacchiera: parlando di {interest}, c'è qualcosa che ti ha colpito ultimamente?"
                            : "Ehi, una curiosità: stai leggendo qualcosa in questo periodo, o c'è qualcosa che stai imparando per conto tuo?");
                    Ask(question, $"chiacchierata ({chatKind})");
                    break;

                case MentorEvent.Pill:
                    var kind = MentorPrompts.PillKinds[_random.Next(MentorPrompts.PillKinds.Count)];
                    var topics = Settings.Topics.Count > 0 ? Settings.Topics : MentorSettings.DefaultTopics;
                    if (await GenerateAsync(MentorPrompts.Pill(Settings, Today, topics, kind, await CurrentWorkAsync())) is { } pill)
                    {
                        Week.Pills++;
                        Ask(pill, "pillola");
                    }
                    break;

                case MentorEvent.Review:
                    await ReviewAsync(manual: false);
                    break;

                case MentorEvent.Retro:
                    _conversation.PostMentor("Preparo la retrospettiva della settimana… 📊");
                    var log = await _repo.GetLogAsync(TimeSpan.FromDays(7));
                    var stats = MentorStats.ToMarkdown(_state, Now);
                    if (await GenerateAsync(MentorPrompts.Retro(Settings, Today, log, stats), readOnly: [_settings.ProjectPath]) is { } retro)
                    {
                        Ask(retro, "retrospettiva");
                    }
                    break;

                case MentorEvent.Break:
                    Week.BreakReminders++;
                    _conversation.PostMentor(MentorPrompts.Break(Settings.BreakAfterMinutes));
                    break;
            }
        }
        finally
        {
            SaveState();
            _busy.Release();
        }
    }

    /// <summary>Asks about the most overdue pill; the answer is graded and logged by Claude in Pillole.md.</summary>
    private async Task ReviewAsync(bool manual)
    {
        var question = await GenerateAsync(MentorPrompts.Review(Settings, Today));
        if (question is null || question.Trim().Trim('.', '"').Equals(MentorPrompts.NothingToReview, StringComparison.OrdinalIgnoreCase))
        {
            if (manual)
            {
                _conversation.PostMentor("Nessuna pillola da ripassare oggi: i ripassi arrivano 2, 7, 21 e 60 giorni dopo. 👍");
            }

            return;
        }

        Week.Reviews++;
        _conversation.PostMentor(question, answer => answer.Length == 0
            ? Task.CompletedTask
            : SaveWithClaudeAsync(MentorPrompts.ReviewAnswer(Settings, question, answer, Today), "ripasso", [new(question, answer)]));
    }

    /// <summary>After the first-run wizard: seeds the profile with the interests and introduces itself.</summary>
    public async Task WelcomeAsync()
    {
        var welcome = await GenerateAsync(MentorPrompts.Welcome(Settings, Today))
            ?? "Piacere! Sono qui per farti crescere: ti farò domande, ti porterò qualche pillola e ti farò compagnia. Per cominciare: cosa ti piacerebbe migliorare nei prossimi mesi?";
        Ask(welcome, "benvenuto");
    }

    /// <summary>Posts a question; the next message the user writes is the answer, saved to the journal.</summary>
    private void Ask(string text, string kind) =>
        _conversation.PostMentor(text, answer => answer.Length == 0
            ? Task.CompletedTask
            : SaveWithClaudeAsync(MentorPrompts.SaveAnswer(Settings, kind, text, answer, Today), kind, [new(text, answer)]));

    /// <summary>How many times the mentor follows up in one dialogue before letting it rest.</summary>
    private const int MaxFollowUps = 5;

    /// <summary>
    /// Runs the prompt (which saves the answer and writes the mentor's reply) and posts the reply. When the reply asks
    /// something, the mentor keeps listening: the next message continues the same dialogue (<paramref name="thread"/>)
    /// instead of going to the models, so "non lo so, dimmelo tu" gets an explanation from the mentor.
    /// </summary>
    private async Task SaveWithClaudeAsync(string prompt, string kind, IReadOnlyList<MentorExchange> thread)
    {
        if (!HasJournal)
        {
            _conversation.PostMentor("Grazie! (Per ricordarmelo imposta la cartella del diario in Impostazioni → Mentore.)");
            return;
        }

        Week.Answers++;
        SaveState();
        _conversation.SetThinking(true);
        var result = await _claude.RunAsync(prompt, Settings.JournalPath, Settings.Model);
        _conversation.SetThinking(false);
        if (!result.Success)
        {
            _conversation.PostMentor($"Non sono riuscito a salvarlo nel diario: {result.Text}");
            return;
        }

        var reply = result.Text;
        if (thread.Count > MaxFollowUps || !MentorPrompts.AsksSomething(reply))
        {
            _conversation.PostMentor(reply);
            return;
        }

        _conversation.PostMentor(reply, answer => answer.Length == 0
            ? Task.CompletedTask
            : SaveWithClaudeAsync(MentorPrompts.FollowUp(Settings, kind, thread, reply, answer, Today), kind,
                [.. thread, new MentorExchange(reply, answer)]));
    }

    private async Task SaveApproachAsync(string action, string approach)
    {
        Week.Approaches++;
        SaveState();
        if (HasJournal)
        {
            await _claude.RunAsync(MentorPrompts.SaveApproach(Today, action, approach), Settings.JournalPath, Settings.Model);
        }
    }

    /// <summary>Runs a generation prompt in the journal folder; null when Claude or the journal is unavailable.</summary>
    private async Task<string?> GenerateAsync(string prompt, IEnumerable<string>? readOnly = null)
    {
        if (!HasJournal)
        {
            return null;
        }

        _conversation.SetThinking(true);
        if (_settings.Assistant.QuickReplies)
        {
            prompt += "\n\n" + Chat.QuickReplies.Instruction;
        }

        var result = await _claude.RunAsync(prompt, Settings.JournalPath, Settings.Model, readOnly);
        _conversation.SetThinking(false);
        return result.Success && result.Text.Length > 0 ? result.Text : null;
    }

    /// <summary>Lines of the always-included notes that talk about priorities, or their beginning.</summary>
    private string Priorities()
    {
        var notes = _settings.AlwaysIncludedNotes.Select(_vault.Find).OfType<VaultNote>().ToList();
        var lines = notes.SelectMany(n => n.Content.Split('\n'))
            .Where(l => l.Contains("priorit", StringComparison.OrdinalIgnoreCase) || l.Contains("In lavorazione", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim())
            .ToList();
        if (lines.Count > 0)
        {
            return string.Join('\n', lines);
        }

        var text = string.Join('\n', notes.Select(n => n.Content));
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    private async Task<bool> HandleCommandAsync(string command, string argument)
    {
        switch (command)
        {
            case "/checkin": await RunAsync(MentorEvent.CheckIn); return true;
            case "/checkout": await RunAsync(MentorEvent.CheckOut); return true;
            case "/pillola": await RunAsync(MentorEvent.Pill); return true;
            case "/chiacchiera": await RunAsync(MentorEvent.Chat); return true;
            case "/retro": await RunAsync(MentorEvent.Retro); return true;
            case "/ripasso":
                if (await _busy.WaitAsync(0))
                {
                    try
                    {
                        await ReviewAsync(manual: true);
                    }
                    finally
                    {
                        SaveState();
                        _busy.Release();
                    }
                }
                return true;
            case "/andamento":
                _conversation.PostMentor($"**Le ultime settimane**\n\n{MentorStats.ToMarkdown(_state, Now)}\n\n{DelegationLine()}"
                    + $"Umore e abitudini sono in Andamento.md, aggiornato dalla retro del venerdì.\n\n**Competenze**\n\n{SkillsMarkdown()}");
                return true;
            case "/obiettivi":
                GoalsCommand(argument);
                return true;
            case "/competenze":
                _conversation.PostMentor($"**Mappa delle competenze** (dalle pillole e dai ripassi)\n\n{SkillsMarkdown()}");
                return true;
            case "/lavora" or "/guidami":
                OnTaskDelegated($"{command} {argument}");
                return false; // still goes to Claude
            case "/focus":
                var minutes = int.TryParse(argument, out var m) && m > 0 ? m : Settings.FocusMinutes;
                _focusUntil = Now.AddMinutes(minutes);
                Week.FocusBlocks++;
                Week.FocusMinutes += minutes;
                SaveState();
                _conversation.PostMentor($"Blocco di focus di {minutes} minuti iniziato (fino alle {_focusUntil:HH:mm}). Niente interruzioni da parte mia. 🎯 Qual è l'unica cosa da portare a termine?");
                return true;
            case "/mentore":
                _conversation.PostMentor(Commands);
                return true;
            default:
                return false;
        }
    }

    private MentorState LoadState()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<MentorState>(File.ReadAllText(StatePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new();
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_state));
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Stato del mentore non salvato");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _busy.Dispose();
    }
}
