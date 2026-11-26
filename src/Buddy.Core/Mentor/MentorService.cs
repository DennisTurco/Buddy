using System.Text;
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
    /// <summary>Closes /mentore, after the list of the mentor's commands.</summary>
    private const string CommandsFooter = "Pillole, ripassi e chiacchierate stanno in una chat a parte (☕ in alto), fuori da quella di lavoro.";

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

        RegisterCommands();
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
            AddPeopleFromCards();
            PostPeopleOnCards();
            OfferReflection();
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

        var (heard, careQuestion) = PeopleHeardToday(today);
        if (heard.Count > 0)
        {
            lines.Add($"👥 sentite: {string.Join(", ", heard)}");
        }

        return (lines.Count == 0
                   ? MentorPrompts.CheckOut
                   : $"Fine giornata. Ecco una **bozza del diario** di oggi, da quello che ho visto:\n\n{string.Join('\n', lines.Select(l => "- " + l))}\n\n"
                     + "Correggila o completala: cosa è andato bene, cosa ti ha bloccato o rallentato, cosa faresti diversamente domani, "
                     + "e come ti senti da 1 a 5.")
               + careQuestion;
    }

    /// <summary>
    /// The people marked "sentita" today (Persone note) and, for those whose relationship needs care ("critico", "delicato", "da
    /// costruire"), a question for the check-out: how did it go, and is the relationship still what the note says? So the
    /// field stays true, and a difficult relationship is something to work on, not a label.
    /// </summary>
    internal (IReadOnlyList<string> Heard, string CareQuestion) PeopleHeardToday(DateOnly today)
    {
        var note = _vault.IsAvailable
            ? _vault.Notes.Where(n => n.Title.Equals(People.Note, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.RelativePath.Length).FirstOrDefault()
            : null;
        var heard = note is null ? [] : People.Parse(note.Content).Where(p => p.LastContact == today).ToList();
        var care = heard.Where(p => p.NeedsCare).ToList();
        var question = care.Count == 0 ? ""
            : $"\n\n🤝 Oggi hai sentito {string.Join(" e ", care.Select(p => $"**{p.Display}**"))}"
              + $" (rapporto «{string.Join("», «", care.Select(p => p.Relationship).Distinct())}»): com'è andata? Cosa ha funzionato nel modo di comunicare? "
              + "Se il rapporto è cambiato, aggiornalo con ✏ Modifica nella pagina Persone.";
        return (heard.Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name).ToList(), question);
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
    /// Once a day, on a weekday: the open cards that depend on someone ([chi:: Chicca]), the waiting ones first, with
    /// their role and, from the Persone note, when and how to reach them. So "sentire Chicca" is not forgotten.
    /// </summary>
    internal void PostPeopleOnCards()
    {
        var today = DateOnly.FromDateTime(Now);
        if (!_vault.IsAvailable || _state.PeopleNudgedOn == today || today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return;
        }

        _state.PeopleNudgedOn = today;
        SaveState();
        var note = _vault.Notes.Where(n => n.Title.Equals(People.Note, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.RelativePath.Length).FirstOrDefault();
        var cards = People.OnCards(ProjectBoard.Load(_vault, _settings.TaskIdPattern), note is null ? [] : People.Parse(note.Content));
        if (cards.Count == 0)
        {
            return;
        }

        const int Shown = 5;
        // Under each person, what to remember from the last episode with them (once per person).
        var episodes = Episodes.Read(EpisodesPath);
        var reminded = new HashSet<string>();
        string WithLesson(PersonOnCard card) =>
            People.Line(card, today)
            + (card.Person is { } p && reminded.Add(p.Name) && Episodes.LessonLine(episodes, p) is { Length: > 0 } lesson ? $"\n  {lesson}" : "");
        var text = "👥 **Persone da sentire** per le tue card:\n\n" + string.Join("\n", cards.Take(Shown).Select(WithLesson))
                   + (cards.Count > Shown ? $"\n- …e altre {cards.Count - Shown}" : "");
        // "✅ Sentita Chicca" for the first people listed: one click once you have talked to them.
        var contacted = cards.Take(Shown).Select(c => c.Person).OfType<Person>().DistinctBy(p => p.Name).Take(3)
            .Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name)
            .Select(n => new ChatAction($"✅ Sentita {n}", $"/sentita {n}", "Segna che oggi l'hai sentita (pagina Persone)"));
        _conversation.Suggest(text,
            [.. contacted, .. cards[0].Item.Id is { } id ? new[] { new ChatAction($"Scheda di {id}", $"/stato {id}") } : []]);
    }

    // ---- Episodes -----------------------------------------------------------------------------------------------

    private VaultNote? PeopleNote() =>
        _vault.IsAvailable
            ? _vault.Notes.Where(n => n.Title.Equals(People.Note, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.RelativePath.Length).FirstOrDefault()
            : null;

    private string EpisodesPath => Episodes.PathFor(_settings);

    /// <summary>Saves a new episode (newest first in Episodi.md), then analyses it.</summary>
    internal async Task RecordEpisodeAsync(Episode episode)
    {
        Episode saved;
        try
        {
            saved = Episodes.Add(EpisodesPath, episode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _conversation.PostMentor($"Non riesco a salvare l'episodio: {ex.Message}");
            return;
        }

        await AnalyzeEpisodeAsync(saved.Key);
    }

    /// <summary>
    /// Reads an episode with Claude: changes that matter (a macroscopic event) are made on the people's cards at once,
    /// each with a button to undo it; the others are proposed with a button to make them. The changes and what the
    /// episode says about the user are written in the episode (the retro reads them for Profilo.md).
    /// </summary>
    internal async Task AnalyzeEpisodeAsync(string key)
    {
        var episode = Episodes.Read(EpisodesPath).FirstOrDefault(e => e.Key.Equals(key.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (episode is null)
        {
            _conversation.PostMentor($"Non trovo l'episodio «{key.Trim()}» in {Episodes.FileName}.");
            return;
        }

        // The people written in the episode who have no card get one now, so the analysis can already work on them.
        var peopleFile = PeopleFile;
        var added = new List<string>();
        foreach (var name in episode.People)
        {
            if (AddMissingPerson(peopleFile, name, $"dall'episodio «{episode.Title}»") is { } person)
            {
                added.Add(person.Name);
            }
        }

        var all = File.Exists(peopleFile) ? People.Parse(File.ReadAllText(peopleFile)) : [];
        var involved = episode.People.Select(n => People.Find(all, n)).OfType<Person>()
            .Concat(People.Named(all, $"{episode.Facts}\n{episode.Outcome}"))
            .DistinctBy(p => p.Name).ToList();

        var folder = Path.GetDirectoryName(Path.GetFullPath(EpisodesPath))!;
        _conversation.SetThinking(true);
        var result = await _claude.RunAsync(EpisodeAnalysis.Prompt(episode, involved, CommunicationProfile(), Episodes.Images(episode, folder)),
            folder, Settings.Model);
        _conversation.SetThinking(false);
        if (!result.Success || EpisodeAnalysis.Parse(result.Text, all) is not { } findings)
        {
            _conversation.PostMentor($"📓 Episodio salvato in {Episodes.FileName}, ma non sono riuscito ad analizzarlo"
                                     + (result.Success ? "." : $": {result.Text}") + $" Riprova con `/episodio-analizza {episode.Key}`."
                                     + AddedPeopleLine(added));
            return;
        }

        // Colleagues the analysis found in the text or the screenshots, without a card: added with what the episode says.
        foreach (var person in findings.NewPeople)
        {
            if (AddMissingPerson(peopleFile, person.Name, $"dall'episodio «{episode.Title}»", person.Role, person.Description) is { } done)
            {
                added.Add(done.Name);
            }
        }

        var (applied, proposed, actions) = ApplyCardChanges(findings.Changes, peopleFile, "di questo episodio");

        try
        {
            Episodes.Update(EpisodesPath, episode.Key, e => e with
            {
                AboutMe = findings.AboutMe.Length > 0 ? findings.AboutMe : e.AboutMe,
                Lesson = findings.Lesson.Length > 0 ? findings.Lesson : e.Lesson,
                Changes = [.. e.Changes, .. applied.Select(a => $"{Today} (automatica) {a}")],
            });
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Episodio non aggiornato dopo l'analisi");
        }

        var text = new StringBuilder($"📓 **Episodio salvato**: {episode.Title}\n");
        if (applied.Count > 0)
        {
            text.AppendLine().AppendLine("**Schede aggiornate** (evento importante):").AppendLine(string.Join("\n", applied.Select(a => "- " + a)));
        }

        if (proposed.Count > 0)
        {
            text.AppendLine().AppendLine("**Da valutare** (un clic per scriverle):").AppendLine(string.Join("\n", proposed.Select(p => "- " + p)));
        }

        if (applied.Count == 0 && proposed.Count == 0)
        {
            text.AppendLine().AppendLine(involved.Count == 0 && added.Count == 0
                ? "Nessuna persona citata: se c'era qualcuno, scrivilo in «Persone coinvolte»."
                : "Niente da cambiare nelle schede.");
        }

        if (findings.AboutMe.Length > 0)
        {
            text.AppendLine().AppendLine($"🪞 **Su di te:** {findings.AboutMe}");
        }

        if (findings.Lesson.Length > 0)
        {
            text.AppendLine().AppendLine($"💡 **Da ricordare la prossima volta:** {findings.Lesson} (te lo ripropongo quando ti ricapita con loro)");
        }

        if (_conversation.LocalAi is not null)
        {
            actions.Add(new ChatAction("🧭 Riflettiamoci", $"/rifletti {episode.Key}", "Il modello locale ti fa qualche domanda per capire cosa c'era sotto"));
        }

        _conversation.PostMentor(text.ToString().TrimEnd() + AddedPeopleLine(added), actions: actions);
    }

    // ---- Reflections on episodes (local model) --------------------------------------------------------------------

    /// <summary>
    /// "/rifletti [episodio]": the local model goes back over an episode with the user, one question at a time, to find
    /// what is underneath it; with no episode named, over the recent ones, looking for what keeps coming back. It runs
    /// in the chat it was asked from (the side chat for the mentor's own offers) and never leaves the computer.
    /// </summary>
    internal async Task ReflectAsync(string name)
    {
        var offTopic = _conversation.ShowingSide;
        if (_conversation.LocalAi is not { } local)
        {
            _conversation.PostMentor("Per riflettere sugli episodi serve il modello locale (Ollama): attivalo in Impostazioni → AI locale.", offTopic: offTopic);
            return;
        }

        var episodes = Episodes.Read(EpisodesPath);
        if (episodes.Count == 0)
        {
            _conversation.PostMentor("Non ci sono ancora episodi: raccontane uno con `/episodio …` e poi ci ripensiamo insieme.", offTopic: offTopic);
            return;
        }

        string opening;
        Episode? single = null;
        if (name.Trim().Length > 0)
        {
            single = EpisodeReflection.Find(episodes, name);
            if (single is null)
            {
                _conversation.PostMentor($"Non trovo l'episodio «{name.Trim()}». Scrivi la data o qualche parola del titolo (pagina 📓 Episodi).", offTopic: offTopic);
                return;
            }

            opening = EpisodeReflection.Opening(single);
        }
        else
        {
            var recent = EpisodeReflection.Recent(episodes, DateOnly.FromDateTime(Now));
            if (recent.Count < 2)
            {
                single = episodes[0];
                opening = EpisodeReflection.Opening(single);
            }
            else
            {
                opening = EpisodeReflection.PatternOpening(recent);
            }
        }

        var thread = new List<Llm.LlmMessage>
        {
            Llm.LlmMessage.System($"{EpisodeReflection.SystemPrompt}\n{EpisodeReflection.WhoIsWho(Settings.UserName)}\nTono: {Personalities.Describe(Settings)}"),
            Llm.LlmMessage.User(opening),
        };
        await ReflectTurnAsync(new Reflection(local, thread, single?.Key ?? episodes[0].Key, single, offTopic), turns: 0);
    }

    /// <summary>
    /// A reflection under way. <see cref="Original"/>: the episode it is about as it was before (null when it looks at
    /// several), whose fields are filled in from the answers; <see cref="Version"/> keeps an older filling from
    /// overwriting a newer one when the user answers fast.
    /// </summary>
    private sealed class Reflection(Llm.LocalAi local, List<Llm.LlmMessage> thread, string episodeKey, Episode? original, bool offTopic)
    {
        public Llm.LocalAi Local { get; } = local;
        public List<Llm.LlmMessage> Thread { get; } = thread;
        public string EpisodeKey { get; } = episodeKey;
        public Episode? Original { get; } = original;
        public bool OffTopic { get; } = offTopic;
        public int Version;
    }

    /// <summary>One message of the model; when it asks something, the user's answer goes on with the same reflection.</summary>
    private async Task ReflectTurnAsync(Reflection reflection, int turns)
    {
        var (local, thread, episodeKey, offTopic) = (reflection.Local, reflection.Thread, reflection.EpisodeKey, reflection.OffTopic);
        _conversation.SetThinking(true);
        var reply = await local.ChatAsync(thread);
        _conversation.SetThinking(false);
        if (reply is null)
        {
            _conversation.PostMentor("Il modello locale non risponde (Ollama è avviato?). Riprova più tardi con `/rifletti`.", offTopic: offTopic);
            return;
        }

        thread.Add(Llm.LlmMessage.Assistant(reply));
        var (text, summary) = EpisodeReflection.Split(reply);
        if (summary is not null)
        {
            if (reflection.Original is null)
            {
                // Several episodes: which one the summary belongs to is the user's call.
                _conversation.PostMentor($"{text}\n\n💡 **Da ricordare:** {summary}",
                    actions:
                    [
                        new ChatAction("💾 Salva nell'episodio", $"/rifletti-salva {episodeKey} | {summary.ReplaceLineEndings(" ").Replace("|", "/")}",
                            $"Lo scrive come «Da ricordare» in «{episodeKey}»: te lo ripropongo quando ricapita con quelle persone"),
                    ],
                    offTopic: offTopic);
                return;
            }

            // One episode: what came out goes in it by itself (reaction, outcome, what to remember).
            _conversation.SetThinking(true);
            var updated = await FillEpisodeAsync(reflection, summary);
            _conversation.SetThinking(false);
            _conversation.PostMentor($"{text}\n\n💡 **Da ricordare:** {summary}\n\n"
                                     + (updated is not null
                                         ? $"💾 Aggiornato l'episodio «{updated.Title}»: come hai reagito, com'è finita e cosa ricordare. Te lo ripropongo quando ricapita con {(updated.People.Count > 0 ? string.Join(", ", updated.People) : "loro")}."
                                         : $"Non sono riuscito ad aggiornare l'episodio in {Episodes.FileName}."),
                offTopic: offTopic);
            return;
        }

        if (reflection.Original is not null && EpisodeReflection.Exchanges(thread).Count > 0)
        {
            _ = FillEpisodeAsync(reflection, null); // while the user reads and answers the next question
        }

        _conversation.PostMentor(text, answer =>
        {
            if (answer.Length == 0)
            {
                return Task.CompletedTask; // skipped: the reflection rests
            }

            // "basta" closes at once; after MaxTurns answers the model is asked to wrap up after this one.
            var wantsToStop = EpisodeReflection.WantsToStop(answer);
            thread.Add(Llm.LlmMessage.User(wantsToStop ? EpisodeReflection.WrapUp : answer));
            if (!wantsToStop && turns + 1 >= EpisodeReflection.MaxTurns)
            {
                thread.Add(Llm.LlmMessage.User(EpisodeReflection.WrapUp));
            }

            return ReflectTurnAsync(reflection, turns + 1);
        }, offTopic: offTopic);
    }

    /// <summary>
    /// Writes what the answers so far say in the episode's "Come ho reagito" and "Com'è finita" (under what the user had
    /// written), and the summary as "Da ricordare" when there is one. The local model reads the answers; nothing leaves
    /// the PC. Returns the episode as saved, or null when it could not be written.
    /// </summary>
    private async Task<Episode?> FillEpisodeAsync(Reflection reflection, string? summary)
    {
        var version = Interlocked.Increment(ref reflection.Version);
        var original = reflection.Original!;
        (string Reaction, string Outcome)? fields = null;
        using (var json = await reflection.Local.JsonAsync(EpisodeReflection.FieldsPrompt, EpisodeReflection.FieldsInput(original, reflection.Thread)))
        {
            if (json is not null)
            {
                string Str(string name) => json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
                fields = (Str("come_ho_reagito"), Str("com_e_finita"));
            }
        }

        if (version != reflection.Version || (fields is null && summary is null))
        {
            return null; // a newer answer is being read, or the model gave nothing usable
        }

        try
        {
            return Episodes.Update(EpisodesPath, reflection.EpisodeKey, e =>
            {
                // An unusable answer leaves the fields as the last filling wrote them.
                var filled = fields is { } f ? EpisodeReflection.WithFields(e, original, f.Reaction, f.Outcome) : e;
                return summary is null ? filled : filled with { Lesson = summary };
            });
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Episodio non aggiornato dalla riflessione");
            return null;
        }
    }

    /// <summary>"/rifletti-salva data · titolo | frase": the reflection's summary becomes the episode's "Da ricordare".</summary>
    private string SaveReflection(string rest)
    {
        var parts = rest.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            return "Non ho capito cosa salvare.";
        }

        try
        {
            return Episodes.Update(EpisodesPath, parts[0], e => e with { Lesson = parts[1] }) is { } saved
                ? $"💾 Salvato in «{saved.Title}»: te lo ripropongo quando ti ricapita con {(saved.People.Count > 0 ? string.Join(", ", saved.People) : "loro")}."
                : $"Non trovo più l'episodio «{parts[0]}» in {Episodes.FileName}.";
        }
        catch (IOException ex)
        {
            return $"Non riesco a scrivere in {Episodes.FileName}: {ex.Message}";
        }
    }

    /// <summary>
    /// Once a day at most, an episode of one to three days ago that ended badly (or with someone already met in another
    /// episode) gets an offer to think it over, in the side chat. Each episode is offered once.
    /// </summary>
    internal void OfferReflection()
    {
        var today = DateOnly.FromDateTime(Now);
        if (_conversation.LocalAi is null || _state.ReflectionOfferedOn == today)
        {
            return;
        }

        var episode = EpisodeReflection.ToOffer(Episodes.Read(EpisodesPath), today, _state.ReflectionsOffered);
        if (episode is null)
        {
            return;
        }

        _state.ReflectionOfferedOn = today;
        _state.ReflectionsOffered.Add(episode.Key);
        if (_state.ReflectionsOffered.Count > 50)
        {
            _state.ReflectionsOffered.RemoveRange(0, _state.ReflectionsOffered.Count - 50);
        }

        SaveState();
        var when = (today.DayNumber - episode.Date.DayNumber) == 1 ? "ieri" : $"il {episode.Date.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("it-IT"))}";
        _conversation.PostMentor(
            $"📓 Ripensavo all'episodio di {when}, «{episode.Title}». Ti va di ripensarci insieme? Qualche domanda per capire cosa c'era sotto: resta tutto sul tuo PC.",
            actions: [new ChatAction("🧭 Riflettiamo", $"/rifletti {episode.Key}", "Il modello locale ti fa qualche domanda sull'episodio, una alla volta")],
            offTopic: true);
    }

    /// <summary>
    /// The people named on the board's cards ([chi:: Chicca]) who have no card in Persone yet: added, with the task they
    /// come from, and told once in the chat.
    /// </summary>
    internal void AddPeopleFromCards()
    {
        if (!_vault.IsAvailable)
        {
            return;
        }

        var peopleFile = PeopleFile;
        var known = File.Exists(peopleFile) ? People.Parse(File.ReadAllText(peopleFile)) : [];
        var added = new List<string>();
        foreach (var item in ProjectBoard.Load(_vault, _settings.TaskIdPattern).Columns
                     .Where(c => c.Key is not ("fatti" or "rifiutati"))
                     .SelectMany(c => c.Items))
        {
            if (item.Field(People.CardField) is { } name && People.Find(known, name) is null
                && AddMissingPerson(peopleFile, name, item.Id is { } id ? $"dal task {id}" : $"dalla voce «{item.Title}»") is { } person)
            {
                added.Add(person.Name);
                known = [.. known, person];
            }
        }

        if (added.Count > 0)
        {
            _conversation.Suggest(AddedPeopleLine(added).Trim());
        }
    }

    /// <summary>
    /// The changes an analysis found for the people's cards (an episode, a call): the important ones are made at once,
    /// each with a button to undo it; the others become proposals with a button to make them.
    /// </summary>
    private (List<string> Applied, List<string> Proposed, List<ChatAction> Actions) ApplyCardChanges(
        IEnumerable<CardChange> changes, string peopleFile, string source)
    {
        var peoplePath = File.Exists(peopleFile) ? peopleFile : null;
        var applied = new List<string>();
        var actions = new List<ChatAction>();
        var proposed = new List<string>();
        foreach (var change in changes)
        {
            if (change.Important && peoplePath is not null
                && People.ApplyChange(peoplePath, change.Person, change.Field, change.Mode, change.Value) is { } done)
            {
                applied.Add(EpisodeAnalysis.Describe(change, done.Before));
                actions.Add(new ChatAction($"↩ Annulla: {change.Person} · {change.Field}", EpisodeAnalysis.UndoCommand(change, done.Before),
                    $"Rimette la scheda com'era prima {source}"));
            }
            else if (!change.Important)
            {
                proposed.Add(EpisodeAnalysis.Describe(change));
                actions.Add(new ChatAction($"✅ {change.Person} · {change.Field}", EpisodeAnalysis.Command(change.Person, change.Field, change.Mode, change.Value),
                    "Scrive questa modifica nella scheda della persona"));
            }
        }

        return (applied, proposed, actions);
    }

    // ---- Calls ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// After a call's transcript is cleaned (Trascrizioni page): the people who took part get a card when missing and
    /// "Sentita" on the call's day; then Claude reads the summary and the transcript and, like for an episode, changes
    /// their cards at once for what matters (role, responsibilities, an important agreement) and proposes the rest.
    /// </summary>
    internal async Task AnalyzeCallPeopleAsync(string relativePath)
    {
        var file = Path.Combine(_settings.ProjectPath, relativePath.Trim());
        if (!File.Exists(file))
        {
            _conversation.PostMentor($"Non trovo la trascrizione «{Path.GetFileNameWithoutExtension(relativePath)}».");
            return;
        }

        var call = CallTranscripts.Read(new VaultNote(relativePath.Trim(), await File.ReadAllTextAsync(file)));
        var peopleFile = PeopleFile;
        var source = $"dalla call «{call.Title}»";
        var me = Settings.UserName.Trim();
        bool IsMe(string name) => name.Equals("io", StringComparison.OrdinalIgnoreCase) || (me.Length > 0 && name.Equals(me, StringComparison.CurrentCultureIgnoreCase));

        // Who took part: a card when missing, and the day of the call as the last time they were heard.
        var added = new List<string>();
        var heard = new List<string>();
        foreach (var name in call.People.Where(n => !IsMe(n)))
        {
            if (AddMissingPerson(peopleFile, name, source) is { } person)
            {
                added.Add(person.Name);
            }

            try
            {
                var known = File.Exists(peopleFile) ? People.Find(People.Parse(File.ReadAllText(peopleFile)), name) : null;
                if (call.Date != DateOnly.MinValue && known is not null && (known.LastContact is not { } last || last < call.Date)
                    && People.MarkContacted(peopleFile, known.Name, call.Date) is not null)
                {
                    heard.Add(known.Name);
                }
            }
            catch (IOException ex)
            {
                _log.LogWarning(ex, "Sentita non segnata per {Name}", name);
            }
        }

        var all = File.Exists(peopleFile) ? People.Parse(File.ReadAllText(peopleFile)) : [];
        var involved = call.People.Where(n => !IsMe(n)).Select(n => People.Find(all, n)).OfType<Person>()
            .Concat(People.Named(all, call.Content))
            .Where(p => !IsMe(p.Name))
            .DistinctBy(p => p.Name).ToList();

        _conversation.SetThinking(true);
        var result = await _claude.RunAsync(CallPeople.Prompt(call, involved, me), _settings.ProjectPath, Settings.Model);
        _conversation.SetThinking(false);
        var text = new StringBuilder($"🎙 **Persone dalla call** «{call.Title}»\n");
        if (heard.Count > 0)
        {
            text.AppendLine().AppendLine($"✅ Segnat{(heard.Count == 1 ? "a" : "e")} come sentit{(heard.Count == 1 ? "a" : "e")} il {call.Date:d MMM}: {string.Join(", ", heard)}");
        }

        if (!result.Success || EpisodeAnalysis.Parse(result.Text, all) is not { } findings)
        {
            _conversation.PostMentor(text.AppendLine().Append("Non sono riuscito a leggere la call per le schede")
                .Append(result.Success ? "." : $": {result.Text}").Append($" Riprova con `/trascrizione-persone {relativePath.Trim()}`.")
                .ToString() + AddedPeopleLine(added));
            return;
        }

        foreach (var person in findings.NewPeople.Where(p => !IsMe(p.Name)))
        {
            if (AddMissingPerson(peopleFile, person.Name, source, person.Role, person.Description) is { } done)
            {
                added.Add(done.Name);
            }
        }

        var (applied, proposed, actions) = ApplyCardChanges(findings.Changes, peopleFile, "di questa call");
        if (applied.Count > 0)
        {
            text.AppendLine().AppendLine("**Schede aggiornate** (cosa importante):").AppendLine(string.Join("\n", applied.Select(a => "- " + a)));
        }

        if (proposed.Count > 0)
        {
            text.AppendLine().AppendLine("**Da valutare** (un clic per scriverle):").AppendLine(string.Join("\n", proposed.Select(p => "- " + p)));
        }

        if (applied.Count == 0 && proposed.Count == 0 && heard.Count == 0 && added.Count == 0)
        {
            return; // nothing for the people: no message
        }

        if (applied.Count == 0 && proposed.Count == 0)
        {
            text.AppendLine().AppendLine("Niente da cambiare nelle schede.");
        }

        _conversation.PostMentor(text.ToString().TrimEnd() + AddedPeopleLine(added), actions: actions);
    }

    /// <summary>The Persone note's path: the one in the vault, or a new "Persone.md" in the project folder.</summary>
    private string PeopleFile =>
        PeopleNote() is { } note ? Path.Combine(_settings.ProjectPath, note.RelativePath) : Path.Combine(_settings.ProjectPath, People.Note + ".md");

    /// <summary>Adds a person met in an episode or a task to Persone when missing; null when already there or not possible.</summary>
    private Person? AddMissingPerson(string peopleFile, string name, string source, string role = "", string description = "")
    {
        try
        {
            return People.EnsureExists(peopleFile, name, source, DateOnly.FromDateTime(Now), role, description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Persona {Name} non aggiunta a Persone", name);
            return null;
        }
    }

    private static string AddedPeopleLine(IReadOnlyCollection<string> added) =>
        added.Count == 0 ? ""
            : $"\n\n👥 **Aggiunt{(added.Count == 1 ? "a" : "e")} a Persone**: {string.Join(", ", added.Distinct())} "
              + "(scheda da completare: clic sulla persona nella pagina 👥 Persone).";

    /// <summary>
    /// "/prepara Chicca rilascio di venerdì": a plan for the conversation and a draft of the first message, from the
    /// person's card, the last episodes with them (what to remember first), the cards waiting on them and how the user
    /// communicates.
    /// </summary>
    internal async Task PrepareConversationAsync(string rest)
    {
        var people = File.Exists(PeopleFile) ? People.Parse(File.ReadAllText(PeopleFile)) : [];
        if (rest.Trim().Length == 0 || ConversationPrep.Split(people, rest) is not var (person, topic))
        {
            _conversation.PostMentor(people.Count == 0
                ? "Non c'è ancora nessuno nella pagina 👥 Persone: aggiungi la persona e poi `/prepara nome argomento`."
                : $"Con chi? `/prepara Chicca rilascio di venerdì`. Persone: {string.Join(", ", people.Select(p => p.Nicknames.Count > 0 ? p.Nicknames[0] : p.Name))}.");
            return;
        }

        var episodes = Episodes.Involving(Episodes.Read(EpisodesPath), person).Take(3).ToList();
        var cards = _vault.IsAvailable
            ? People.OnCards(ProjectBoard.Load(_vault, _settings.TaskIdPattern), people).Where(c => c.Person?.Name == person.Name).ToList()
            : [];
        var folder = Path.GetDirectoryName(Path.GetFullPath(EpisodesPath))!;
        Directory.CreateDirectory(folder);
        _conversation.SetThinking(true);
        var result = await _claude.RunAsync(
            ConversationPrep.Prompt(person, topic, episodes, cards, CommunicationProfile(), DateOnly.FromDateTime(Now)), folder, Settings.Model);
        _conversation.SetThinking(false);
        if (!result.Success || result.Text.Length == 0)
        {
            var lesson = Episodes.LessonLine(episodes, person);
            _conversation.PostMentor($"Non riesco a preparare la conversazione con Claude: {result.Text}"
                                     + (lesson.Length > 0 ? $"\n\nIntanto: {lesson}" : ""));
            return;
        }

        _conversation.PostMentor($"🗣 **Conversazione con {person.Display}**{(topic.Length > 0 ? $" · {topic}" : "")}\n\n{result.Text}",
            actions: [new ChatAction($"✅ Sentita {(person.Nicknames.Count > 0 ? person.Nicknames[0] : person.Name)}", $"/sentita {person.Name}",
                "Segna che oggi l'hai sentita")]);
    }

    /// <summary>"/scheda Nome | campo | modo | valore": one change to a card (the buttons of an episode's analysis).</summary>
    private string ChangeCardCommand(string rest)
    {
        if (EpisodeAnalysis.ParseCommand(rest) is not var (person, field, mode, value))
        {
            return "Formato: `/scheda Nome | rapporto, come comunicare o note | imposta, aggiungi o togli | valore`.";
        }

        if (PeopleNote() is not { } note)
        {
            return "Nel vault non c'è la nota «Persone».";
        }

        try
        {
            return People.ApplyChange(Path.Combine(_settings.ProjectPath, note.RelativePath), person, field, mode, value) is { } done
                ? $"👥 Scheda di **{done.Person.Display}** aggiornata ({field})."
                : $"Niente da cambiare nella scheda di «{person}» ({field}): forse è già così, o il campo non si può modificare.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Non riesco a scrivere nella nota Persone: {ex.Message}";
        }
    }

    /// <summary>"## Come comunico" of Profilo.md, written by the weekly retro from the episodes; null when there is none.</summary>
    private string? CommunicationProfile()
    {
        var profile = Path.Combine(Settings.JournalPath, "Profilo.md");
        if (!HasJournal || !File.Exists(profile))
        {
            return null;
        }

        var text = File.ReadAllText(profile).ReplaceLineEndings("\n");
        var start = text.IndexOf("## Come comunico", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var end = text.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        return text[start..(end < 0 ? text.Length : end)].Trim();
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
                    Ask(question, $"chiacchierata ({chatKind})", offTopic: true);
                    break;

                case MentorEvent.Pill:
                    var kind = MentorPrompts.PillKinds[_random.Next(MentorPrompts.PillKinds.Count)];
                    var topics = Settings.Topics.Count > 0 ? Settings.Topics : MentorSettings.DefaultTopics;
                    if (await GenerateAsync(MentorPrompts.Pill(Settings, Today, topics, kind, await CurrentWorkAsync())) is { } pill)
                    {
                        Week.Pills++;
                        Ask(pill, "pillola", offTopic: true);
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
                _conversation.PostMentor("Nessuna pillola da ripassare oggi: i ripassi arrivano 2, 7, 21 e 60 giorni dopo. 👍", offTopic: true);
            }

            return;
        }

        Week.Reviews++;
        _conversation.PostMentor(question, answer => answer.Length == 0
            ? Task.CompletedTask
            : SaveWithClaudeAsync(MentorPrompts.ReviewAnswer(Settings, question, answer, Today), "ripasso", [new(question, answer)], offTopic: true),
            offTopic: true);
    }

    /// <summary>After the first-run wizard: seeds the profile with the interests and introduces itself.</summary>
    public async Task WelcomeAsync()
    {
        var welcome = await GenerateAsync(MentorPrompts.Welcome(Settings, Today))
            ?? "Piacere! Sono qui per farti crescere: ti farò domande, ti porterò qualche pillola e ti farò compagnia. Per cominciare: cosa ti piacerebbe migliorare nei prossimi mesi?";
        Ask(welcome, "benvenuto");
    }

    /// <summary>
    /// Posts a question; the next message the user writes is the answer, saved to the journal. Off-topic dialogues
    /// (pills, chats) go to the side chat, so they do not mix with the work.
    /// </summary>
    private void Ask(string text, string kind, bool offTopic = false) =>
        _conversation.PostMentor(text, answer => answer.Length == 0
            ? Task.CompletedTask
            : SaveWithClaudeAsync(MentorPrompts.SaveAnswer(Settings, kind, text, answer, Today), kind, [new(text, answer)], offTopic),
            offTopic: offTopic);

    /// <summary>How many times the mentor follows up in one dialogue before letting it rest.</summary>
    private const int MaxFollowUps = 5;

    /// <summary>
    /// Runs the prompt (which saves the answer and writes the mentor's reply) and posts the reply. When the reply asks
    /// something, the mentor keeps listening: the next message continues the same dialogue (<paramref name="thread"/>)
    /// instead of going to the models, so "non lo so, dimmelo tu" gets an explanation from the mentor.
    /// </summary>
    private async Task SaveWithClaudeAsync(string prompt, string kind, IReadOnlyList<MentorExchange> thread, bool offTopic = false)
    {
        if (!HasJournal)
        {
            _conversation.PostMentor("Grazie! (Per ricordarmelo imposta la cartella del diario in Impostazioni → Mentore.)", offTopic: offTopic);
            return;
        }

        Week.Answers++;
        SaveState();
        _conversation.SetThinking(true);
        var result = await _claude.RunAsync(prompt, Settings.JournalPath, Settings.Model);
        _conversation.SetThinking(false);
        if (!result.Success)
        {
            _conversation.PostMentor($"Non sono riuscito a salvarlo nel diario: {result.Text}", offTopic: offTopic);
            return;
        }

        var reply = result.Text;
        if (thread.Count > MaxFollowUps || !MentorPrompts.AsksSomething(reply))
        {
            _conversation.PostMentor(reply, offTopic: offTopic);
            return;
        }

        _conversation.PostMentor(reply, answer => answer.Length == 0
            ? Task.CompletedTask
            : SaveWithClaudeAsync(MentorPrompts.FollowUp(Settings, kind, thread, reply, answer, Today), kind,
                [.. thread, new MentorExchange(reply, answer)], offTopic),
            offTopic: offTopic);
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

    private bool _reporting;

    /// <summary>
    /// "/resoconto": Claude reads the journal (profile, episodes, diary, retros) and answers with a portrait of the user,
    /// marks on a few aspects, strengths, things to improve and the interventions that went well or not. The buddy
    /// writes it on top of Resoconto.md (the older ones stay, to see the marks move) and posts the gist.
    /// </summary>
    internal async Task ReportAsync()
    {
        if (!HasJournal)
        {
            _conversation.PostMentor("Per il resoconto mi serve la cartella del diario: impostala in Impostazioni → Mentore.");
            return;
        }

        if (_reporting)
        {
            _conversation.PostMentor("Sto già preparando il resoconto: un attimo.");
            return;
        }

        _reporting = true;
        try
        {
            var path = ProfileReports.PathFor(_settings);
            var previous = ProfileReports.Read(path).FirstOrDefault();
            var today = DateOnly.FromDateTime(Now);
            _conversation.PostMentor("🪞 Rileggo diario, profilo ed episodi per il tuo resoconto: ci vuole un minuto.");
            _conversation.SetThinking(true);
            var result = await _claude.RunAsync(ProfileReports.Prompt(MentorPrompts.Persona(Settings), today, previous),
                Settings.JournalPath, Settings.Model, timeout: TimeSpan.FromMinutes(8));
            _conversation.SetThinking(false);
            if (!result.Success || ProfileReports.Parse(result.Text, today) is not { } report)
            {
                _conversation.PostMentor("Non sono riuscito a preparare il resoconto" + (result.Success ? "." : $": {result.Text}") + " Riprova con `/resoconto`.");
                return;
            }

            try
            {
                ProfileReports.Add(path, report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _conversation.PostMentor($"Non riesco a salvare {ProfileReports.FileName}: {ex.Message}");
                return;
            }

            _conversation.PostMentor(ReportMessage(report, previous));
        }
        finally
        {
            _reporting = false;
        }
    }

    /// <summary>The report in the chat, short: the portrait, the marks with how they moved, one strength and one thing to improve.</summary>
    internal static string ReportMessage(ProfileReport report, ProfileReport? previous)
    {
        var text = new StringBuilder("🪞 **Il tuo resoconto**\n\n").AppendLine(report.Summary).AppendLine();
        var rated = report.Rated.ToList();
        if (rated.Count > 0)
        {
            text.AppendLine(string.Join(" · ", rated.Select(s =>
            {
                var change = ProfileReports.Change(report, previous, s.Aspect);
                return $"{s.Aspect} **{s.Score}**" + (change is > 0 ? $" ↑{change}" : change is < 0 ? $" ↓{-change}" : "");
            }))).AppendLine();
        }

        if (report.Strengths.Count > 0)
        {
            text.AppendLine($"💪 {report.Strengths[0]}");
        }

        if (report.Improvements.Count > 0)
        {
            text.AppendLine($"🎯 {report.Improvements[0]}");
        }

        text.AppendLine().Append($"Tutto il resto (interventi riusciti e no, motivi dei voti) è nella pagina 🪞 Il mio profilo e in {ProfileReports.FileName}.");
        return text.ToString();
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

    /// <summary>The mentor's "/" commands, in the conversation's registry (/salta is the conversation's own).</summary>
    private void RegisterCommands()
    {
        var commands = _conversation.Commands;
        void Add(string name, string description, Func<CommandCall, Task> run, string? arguments = null) =>
            commands.Add(new ChatCommand(name, description, arguments, CommandGroup.Mentor), run);

        Add("/checkin", "piano della giornata", _ => RunAsync(MentorEvent.CheckIn));
        Add("/checkout", "com'è andata oggi", _ => RunAsync(MentorEvent.CheckOut));
        Add("/pillola", "una pillola di conoscenza", _ => RunAsync(MentorEvent.Pill));
        Add("/ripasso", "ripasso di una pillola vista giorni fa", async _ =>
        {
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
        });
        Add("/chiacchiera", "due chiacchiere", _ => RunAsync(MentorEvent.Chat));
        Add("/retro", "retrospettiva settimanale", _ => RunAsync(MentorEvent.Retro));
        Add("/focus", "blocco di concentrazione: niente messaggi del mentore fino alla fine", call =>
        {
            var minutes = int.TryParse(call.Rest, out var m) && m > 0 ? m : Settings.FocusMinutes;
            _focusUntil = Now.AddMinutes(minutes);
            Week.FocusBlocks++;
            Week.FocusMinutes += minutes;
            SaveState();
            _conversation.PostMentor($"Blocco di focus di {minutes} minuti iniziato (fino alle {_focusUntil:HH:mm}). Niente interruzioni da parte mia. 🎯 Qual è l'unica cosa da portare a termine?");
            return Task.CompletedTask;
        }, "[minuti]");
        Add("/andamento", "le ultime settimane in numeri (con le competenze)", _ =>
        {
            _conversation.PostMentor($"**Le ultime settimane**\n\n{MentorStats.ToMarkdown(_state, Now)}\n\n{DelegationLine()}"
                + $"Umore e abitudini sono in Andamento.md, aggiornato dalla retro del venerdì.\n\n**Competenze**\n\n{SkillsMarkdown()}");
            return Task.CompletedTask;
        });
        Add("/competenze", "cosa sai bene e cosa ripassare, dalle pillole", _ =>
        {
            _conversation.PostMentor($"**Mappa delle competenze** (dalle pillole e dai ripassi)\n\n{SkillsMarkdown()}", offTopic: true);
            return Task.CompletedTask;
        });
        Add("/obiettivi", "gli obiettivi del trimestre", call =>
        {
            GoalsCommand(call.Rest);
            return Task.CompletedTask;
        }, "[nuovi]");
        Add("/episodio", "racconta un episodio con un collega: aggiorno le schede delle Persone", call =>
        {
            if (call.Rest.Trim().Length == 0)
            {
                _conversation.PostMentor("Raccontami cosa è successo: `/episodio con Chicca riunione tesa sul rilascio, ho alzato la voce…`. "
                                         + "Dalla pagina 📓 Episodi della bacheca puoi anche separare fatti, reazione ed esito.");
                return Task.CompletedTask;
            }

            var text = call.Rest.Trim();
            var people = PeopleNote() is { } note ? People.Named(People.Parse(note.Content), text).Select(p => p.Name).ToList() : [];
            return RecordEpisodeAsync(new Episode(DateOnly.FromDateTime(Now), Episodes.TitleFrom(text), people, text, "", "", "", []));
        }, "cosa è successo");
        Add("/resoconto", "il tuo profilo: punti di forza, da migliorare, interventi riusciti e no, voti", _ => ReportAsync());
        Add("/prepara","prepara una conversazione con una persona: come impostarla e il primo messaggio", call => PrepareConversationAsync(call.Rest),
            "persona [argomento]");
        commands.Add(new ChatCommand("/episodio-analizza", "analizza un episodio salvato", "data · titolo", CommandGroup.Mentor, Hidden: true),
            call => AnalyzeEpisodeAsync(call.Rest));
        Add("/rifletti", "ripensiamo a un episodio (o a cosa si ripete): ti faccio domande con il modello locale", call => ReflectAsync(call.Rest),
            "[episodio]");
        commands.Add(new ChatCommand("/rifletti-salva", "salva quanto capito come «Da ricordare» dell'episodio", "data · titolo | frase", CommandGroup.Mentor, Hidden: true),
            call =>
            {
                _conversation.PostMentor(SaveReflection(call.Rest), offTopic: _conversation.ShowingSide);
                return Task.CompletedTask;
            });
        commands.Add(new ChatCommand("/trascrizione-persone", "aggiorna le schede delle persone da una call", "Trascrizioni/…md", CommandGroup.Mentor, Hidden: true),
            call => AnalyzeCallPeopleAsync(call.Rest));
        commands.Add(new ChatCommand("/scheda", "cambia un campo della scheda di una persona", "Nome | campo | modo | valore", CommandGroup.Mentor, Hidden: true),
            call =>
            {
                _conversation.PostMentor(ChangeCardCommand(call.Rest));
                return Task.CompletedTask;
            });
        Add("/mentore", "i comandi del mentore", _ =>
        {
            _conversation.PostMentor($"Mentore:\n{commands.Help(CommandGroup.Mentor)}\n{CommandsFooter}");
            return Task.CompletedTask;
        });

        // Skills of Claude: the mentor only counts what is delegated, the message still goes to Claude.
        foreach (var skill in new[] { "/lavora", "/guidami" })
        {
            commands.AddConditional(new ChatCommand(skill, "skill di Claude", Group: CommandGroup.Mentor, Hidden: true), call =>
            {
                OnTaskDelegated($"{call.Command} {call.Rest}");
                return Task.FromResult(false);
            });
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
