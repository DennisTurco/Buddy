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
        /andamento — le ultime settimane in numeri
        /salta — ignora la domanda in sospeso
        """;

    private static readonly string StatePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Buddy", "mentor-state.json");

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
        ILogger<MentorService>? logger = null)
    {
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

            var due = _schedule.Due(Now, _state, _activity.ContinuousActivity, _random);
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
                    Ask(MentorPrompts.CheckOut, "check-out");
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
                    if (await GenerateAsync(MentorPrompts.Pill(Settings, Today, topics, kind)) is { } pill)
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
            : SaveWithClaudeAsync(MentorPrompts.ReviewAnswer(Settings, question, answer, Today)));
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
            : SaveWithClaudeAsync(MentorPrompts.SaveAnswer(Settings, kind, text, answer, Today)));

    private async Task SaveWithClaudeAsync(string prompt)
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
        _conversation.PostMentor(result.Success ? result.Text : $"Non sono riuscito a salvarlo nel diario: {result.Text}");
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
                _conversation.PostMentor($"**Le ultime settimane**\n\n{MentorStats.ToMarkdown(_state, Now)}\n\nUmore e abitudini sono in Andamento.md, aggiornato dalla retro del venerdì.");
                return true;
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

    private static MentorState LoadState()
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
