using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Buddy.Core.Mentor;
using Buddy.Core.Repo;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

/// <summary>A clock stopped at a given local time (UTC zone, so local = UTC).</summary>
internal sealed class StoppedClock(DateTime local) : TimeProvider
{
    public DateTime Local { get; set; } = local;

    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Local, DateTimeKind.Utc));

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal sealed class IdleActivity : IActivityMonitor
{
    public TimeSpan ContinuousActivity => TimeSpan.Zero;

    public string? BusyReason => null;
}

/// <summary>
/// The behaviours added to the chat and the mentor, on a temporary vault and a stopped clock: nothing touches the
/// user's own files (the mentor's state goes to a temporary file too).
/// </summary>
public class MentorBehaviourTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("buddy-behaviour-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    /// <summary>A Monday near the end of the working day: every random slot of the day has passed.</summary>
    private static readonly DateTime LateMonday = new(2026, 11, 2, 17, 55, 0);

    private BuddySettings Settings(int quietMinutes) => new()
    {
        ProjectPath = Path.Combine(_root, "vault"),
        Ollama = { BaseUrl = "http://127.0.0.1:1" }, // refused at once: a local "answer" that fails fast
        Mentor =
        {
            Enabled = true,
            JournalPath = "", // no journal: no Claude call, the built-in fallback texts are posted
            CheckInTime = "",
            CheckOutTime = "",
            RetroTime = "",
            PillsPerDay = 0,
            ReviewsPerDay = 0,
            ChatsPerDay = 1,
            BreakAfterMinutes = 0,
            QuietAfterChatMinutes = quietMinutes,
        },
    };

    private MentorService Mentor(Conversation conversation, BuddySettings settings, TimeProvider clock)
    {
        var options = Options.Create(settings);
        return new MentorService(conversation, new ClaudeOneShot(options), new VaultIndex(options), new GitRepo(options, clock),
            new IdleActivity(), clock, options, statePath: Path.Combine(_root, "mentor-state.json"));
    }

    [Test]
    public void The_check_out_asks_how_it_went_only_with_people_whose_relationship_needs_care()
    {
        var settings = Settings(quietMinutes: 0);
        Directory.CreateDirectory(settings.ProjectPath);
        File.WriteAllText(Path.Combine(settings.ProjectPath, "Persone.md"), "# Persone\n\n"
            + "## Chiara Bianchi\n\n- **Soprannome:** Chicca\n- **Rapporto:** delicato\n- **Sentita:** 2026-11-02\n\n"
            + "## Marco Verdi\n\n- **Rapporto:** buono\n- **Sentita:** 2026-11-02\n\n"
            + "## Gino Neri\n\n- **Rapporto:** da costruire\n- **Sentita:** 2026-10-20\n\n"
            + "## Piero Gialli\n\n- **Rapporto:** critico\n- **Sentita:** 2026-11-02\n");
        var mentor = Mentor(ModelCommandTests.Create(settings), settings, new StoppedClock(LateMonday));

        var (heard, question) = mentor.PeopleHeardToday(new DateOnly(2026, 11, 2));

        Assert.That(heard, Is.EqualTo(new[] { "Chicca", "Marco Verdi", "Piero Gialli" }));
        Assert.That(question, Does.Contain("**Chiara Bianchi (Chicca)** e **Piero Gialli**").And.Contain("«delicato», «critico»").And.Contain("com'è andata"));
        Assert.That(question, Does.Not.Contain("Marco").And.Not.Contain("Gino"), "a good relationship, or someone not heard today, is not asked about");

        var (_, none) = mentor.PeopleHeardToday(new DateOnly(2026, 11, 3));
        Assert.That(none, Is.Empty);
    }

    [Test]
    public async Task An_episode_told_in_the_chat_is_saved_in_the_journal_even_when_claude_cannot_analyse_it()
    {
        var settings = Settings(quietMinutes: 0);
        settings.Mentor.JournalPath = Path.Combine(_root, "diario");
        settings.Claude.Executable = Path.Combine(_root, "nessun-claude.exe"); // fails at once: no analysis
        Directory.CreateDirectory(settings.ProjectPath);
        Directory.CreateDirectory(settings.Mentor.JournalPath);
        File.WriteAllText(Path.Combine(settings.ProjectPath, "Persone.md"), "# Persone\n\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n");
        var conversation = ModelCommandTests.Create(settings);
        var mentor = Mentor(conversation, settings, new StoppedClock(LateMonday));

        await conversation.SendAsync("/episodio con Chicca riunione tesa sul rilascio, ho alzato la voce");

        var episode = Episodes.Read(Path.Combine(settings.Mentor.JournalPath, "Episodi.md")).Single();
        Assert.That(episode.People, Is.EqualTo(new[] { "Chiara Bianchi" }), "named by nickname in the text");
        Assert.That(episode.Facts, Does.Contain("ho alzato la voce"));
        Assert.That(conversation.Entries.Concat(conversation.SideEntries).Last(e => e.Speaker == Speaker.Mentor).Text,
            Does.Contain("non sono riuscito ad analizzarlo").And.Contain("/episodio-analizza 2026-11-02 · "));
        GC.KeepAlive(mentor);
    }

    [Test]
    public async Task People_named_in_an_episode_or_on_a_card_are_added_to_Persone_when_missing()
    {
        var settings = Settings(quietMinutes: 0);
        settings.Mentor.JournalPath = Path.Combine(_root, "diario");
        settings.Claude.Executable = Path.Combine(_root, "nessun-claude.exe");
        Directory.CreateDirectory(settings.ProjectPath);
        Directory.CreateDirectory(settings.Mentor.JournalPath);
        var people = Path.Combine(settings.ProjectPath, "Persone.md");
        File.WriteAllText(people, "# Persone\n\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n");
        File.WriteAllText(Path.Combine(settings.ProjectPath, "To-Do.md"), "# To-Do\n\n## Task\n\n- [ ] T-1 · ⏸ Permessi [chi:: Gino Neri]\n- [ ] T-2 · Altro [chi:: Chicca]\n");
        var conversation = ModelCommandTests.Create(settings);
        using var vault = new VaultIndex(Options.Create(settings));
        var mentor = new MentorService(conversation, new ClaudeOneShot(Options.Create(settings)), vault, new GitRepo(Options.Create(settings), TimeProvider.System),
            new IdleActivity(), new StoppedClock(LateMonday), Options.Create(settings), statePath: Path.Combine(_root, "mentor-state.json"));

        mentor.AddPeopleFromCards();
        Assert.That(File.ReadAllText(people), Does.Contain("## Gino Neri\n\n- **Note:** aggiunta in automatico dal task T-1 (2026-11-02): da completare"));
        Assert.That(People.Parse(File.ReadAllText(people)), Has.Count.EqualTo(2), "Chicca already has a card");
        mentor.AddPeopleFromCards();
        Assert.That(People.Parse(File.ReadAllText(people)), Has.Count.EqualTo(2), "only once");

        // An episode: the people written in it are added before the analysis (here Claude is not there).
        await mentor.RecordEpisodeAsync(new Episode(new DateOnly(2026, 11, 2), "Riunione", ["Laura Rossi", "Chicca"], "Laura ha chiesto una demo", "", "", "", []));
        Assert.That(People.Parse(File.ReadAllText(people)).Select(p => p.Name), Is.EqualTo(new[] { "Chiara Bianchi", "Gino Neri", "Laura Rossi" }));
        Assert.That(File.ReadAllText(people), Does.Contain("dall'episodio «Riunione»"));
        Assert.That(conversation.Entries.Concat(conversation.SideEntries).Last(e => e.Speaker == Speaker.Mentor).Text, Does.Contain("Aggiunta a Persone**: Laura Rossi"));
    }

    [Test]
    public async Task Sentita_brings_back_what_to_remember_and_prepara_falls_back_to_it_without_claude()
    {
        var settings = Settings(quietMinutes: 0);
        settings.Mentor.JournalPath = Path.Combine(_root, "diario");
        settings.Claude.Executable = Path.Combine(_root, "nessun-claude.exe");
        Directory.CreateDirectory(settings.ProjectPath);
        Directory.CreateDirectory(settings.Mentor.JournalPath);
        File.WriteAllText(Path.Combine(settings.ProjectPath, "Persone.md"), "# Persone\n\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n");
        Episodes.Add(Path.Combine(settings.Mentor.JournalPath, "Episodi.md"),
            new Episode(new DateOnly(2026, 10, 30), "Rilascio", ["Chiara Bianchi"], "Tesa", "", "", "", [], Lesson: "mandarle i dati prima"));
        var conversation = ModelCommandTests.Create(settings);
        var mentor = Mentor(conversation, settings, new StoppedClock(LateMonday));
        string LastText() => conversation.Entries.Concat(conversation.SideEntries).Last(e => e.Speaker != Speaker.User).Text;

        await conversation.SendAsync("/sentita Chicca");
        Assert.That(LastText(), Does.Contain("ti eri ripromesso: _mandarle i dati prima_"));

        await conversation.SendAsync("/prepara Chicca rilascio");
        Assert.That(LastText(), Does.Contain("Non riesco a preparare").And.Contain("💡 l'ultima volta con Chicca"));

        await conversation.SendAsync("/prepara Gino");
        Assert.That(LastText(), Does.Contain("Con chi?"));
        GC.KeepAlive(mentor);
    }

    [Test]
    public async Task The_scheda_command_changes_a_card_from_the_buttons_of_the_analysis()
    {
        var settings = Settings(quietMinutes: 0);
        Directory.CreateDirectory(settings.ProjectPath);
        var people = Path.Combine(settings.ProjectPath, "Persone.md");
        File.WriteAllText(people, "# Persone\n\n## Chiara Bianchi\n\n- **Rapporto:** buono\n");
        var conversation = ModelCommandTests.Create(settings);
        var mentor = Mentor(conversation, settings, new StoppedClock(LateMonday));

        await conversation.SendAsync("/scheda Chiara Bianchi | rapporto | imposta | delicato");

        Assert.That(File.ReadAllText(people), Does.Contain("- **Rapporto:** delicato"));
        Assert.That(conversation.Entries.Concat(conversation.SideEntries).Last(e => e.Speaker == Speaker.Mentor).Text, Does.Contain("aggiornata"));
        GC.KeepAlive(mentor);
    }

    private static int MentorMessages(Conversation conversation) =>
        conversation.Entries.Concat(conversation.SideEntries).Count(e => e.Speaker == Speaker.Mentor);

    [Test]
    public async Task The_mentor_registers_its_commands_and_mentore_lists_them()
    {
        var settings = Settings(quietMinutes: 0);
        var conversation = ModelCommandTests.Create(settings);
        using var mentor = Mentor(conversation, settings, new StoppedClock(LateMonday));

        var names = conversation.Commands.All.Where(c => c.Group == CommandGroup.Mentor).Select(c => c.Name).ToList();
        Assert.That(names, Is.SupersetOf(new[] { "/checkin", "/checkout", "/pillola", "/ripasso", "/chiacchiera", "/retro",
            "/focus", "/andamento", "/competenze", "/obiettivi", "/mentore", "/salta" }));
        Assert.That(conversation.Commands.Find("/lavora")?.Command.Hidden, Is.True);

        await conversation.SendAsync("/mentore");

        var help = conversation.Entries.Concat(conversation.SideEntries).Last(e => e.Speaker == Speaker.Mentor).Text;
        Assert.That(names, Has.All.Matches<string>(name => help.Contains(name)));
        Assert.That(conversation.Entries.Any(e => e.Speaker == Speaker.User && e.Text == "/mentore"), Is.True);
    }

    [Test]
    public async Task Friendly_chats_wait_while_the_user_works_in_the_chat()
    {
        var settings = Settings(quietMinutes: 20);
        Directory.CreateDirectory(settings.ProjectPath);
        var conversation = ModelCommandTests.Create(settings);
        var mentor = Mentor(conversation, settings, new StoppedClock(LateMonday));

        await conversation.SendAsync("domanda di lavoro"); // the local model answers (here: fails) → work in the chat
        Assert.That(conversation.LastWorkActivity, Is.Not.Null);

        await mentor.TickAsync();
        Assert.That(MentorMessages(conversation), Is.EqualTo(0), "the chat is held while working");

        settings.Mentor.QuietAfterChatMinutes = 0;
        await mentor.TickAsync();
        Assert.That(MentorMessages(conversation), Is.EqualTo(1), "it was only waiting, not dropped");
    }

    [Test]
    public async Task A_task_gone_to_review_gets_one_question_once_the_chat_is_quiet()
    {
        var settings = Settings(quietMinutes: 0);
        settings.Mentor.ChatsPerDay = 0;
        Directory.CreateDirectory(settings.ProjectPath);
        var review = Path.Combine(settings.ProjectPath, "Da controllare.md");
        File.WriteAllText(review, "# Da controllare\n\n- [ ] T-1 · Già lì da prima\n");
        var conversation = ModelCommandTests.Create(settings);
        var options = Options.Create(settings);
        using var vault = new VaultIndex(options);
        var mentor = new MentorService(conversation, new ClaudeOneShot(options), vault, new GitRepo(options, TimeProvider.System),
            new IdleActivity(), new StoppedClock(LateMonday), options, statePath: Path.Combine(_root, "mentor-state.json"));

        await mentor.TickAsync();
        Assert.That(MentorMessages(conversation), Is.EqualTo(0), "what is already in review is not asked about");

        File.AppendAllText(review, "- [ ] T-2 · Appena finito\n");
        await WaitUntilAsync(() => vault.Notes.Any(n => n.Content.Contains("T-2")));
        await mentor.TickAsync();

        var question = conversation.Entries.Single(e => e.Speaker == Speaker.Mentor);
        Assert.That(question.Text, Does.Contain("T-2"));
        Assert.That(question.AwaitsReply, Is.True, "the answer goes back to the mentor");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(100);
        }

        Assert.That(condition(), Is.True, "the vault did not see the change");
    }

    [Test]
    public void Off_topic_events_are_pills_reviews_and_chats_only()
    {
        Assert.That(new[] { MentorEvent.Pill, MentorEvent.Review, MentorEvent.Chat }.All(MentorService.IsOffTopic), Is.True);
        Assert.That(new[] { MentorEvent.CheckIn, MentorEvent.CheckOut, MentorEvent.Retro, MentorEvent.Break }.Any(MentorService.IsOffTopic), Is.False);
    }

    [Test]
    public void The_pill_prompt_points_at_the_work_in_progress()
    {
        var prompt = MentorPrompts.Pill(new MentorSettings(), "oggi", ["database"], "tecnica", "- Task: NUSR-2367 · tentativi di login");

        Assert.That(prompt, Does.Contain("NUSR-2367").And.Contain("DIETRO questo lavoro"));
        Assert.That(MentorPrompts.Pill(new MentorSettings(), "oggi", ["database"], "tecnica"), Does.Not.Contain("DIETRO"));
    }
}

public class ConversationArchiveTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("buddy-archive-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task A_saved_conversation_comes_back_with_its_messages()
    {
        var settings = new BuddySettings { ProjectPath = Path.Combine(_root, "vault"), Ollama = { BaseUrl = "http://127.0.0.1:1" } };
        var conversation = ModelCommandTests.Create(settings);
        conversation.Archive = new ConversationArchive(Path.Combine(_root, "conversations"));

        await conversation.SendAsync("/aiuto");
        await conversation.SendAsync("come avvio il TaskExecutor in locale?");
        await conversation.SendAsync("/nuova");
        Assert.That(conversation.Entries.Where(e => e.Speaker != Speaker.System), Is.Empty, "a new conversation starts with an empty chat");

        var saved = conversation.Archive.List();
        Assert.That(saved, Has.Count.EqualTo(1));
        Assert.That(saved[0].Title, Is.EqualTo("come avvio il TaskExecutor in locale?"));

        Assert.That(await conversation.OpenSavedAsync(saved[0]), Is.True);
        Assert.That(conversation.Entries.Any(e => e.Speaker == Speaker.User && e.Text.StartsWith("come avvio")), Is.True);
    }

    [Test]
    public void A_conversation_without_questions_is_not_kept()
    {
        var archive = new ConversationArchive(Path.Combine(_root, "conversations"));
        archive.Save("x", [new ChatEntry(Speaker.System, "Ciao!")], null);

        Assert.That(archive.List(), Is.Empty);
    }
}

public class SmallFeatureTests
{
    [Test]
    public void Repo_files_become_references_and_others_are_attached()
    {
        var repo = Directory.CreateTempSubdirectory("buddy-repo-").FullName;
        try
        {
            var file = Path.Combine(repo, "src", "Login Fallito.vb");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");

            Assert.That(FileReferences.ForRepo([file], [repo]), Is.EqualTo(new[] { "@\"src/Login Fallito.vb\"" }));
            Assert.That(FileReferences.ForRepo([Path.Combine(repo, "shot.png")], [repo]), Is.Null, "images are attached");
            Assert.That(FileReferences.ForRepo([Path.GetTempFileName()], [repo]), Is.Null, "outside the repo");
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Test]
    public void The_average_turn_cost_is_kept_per_model()
    {
        var usage = new ClaudeUsage();
        usage.AddTurn(0.30m, "opus");
        usage.AddTurn(0.50m, "opus");
        Assert.That(usage.AverageTurnCost("opus"), Is.Null, "no estimate on two turns");

        usage.AddTurn(0.40m, "Opus");
        Assert.That(usage.AverageTurnCost("opus"), Is.EqualTo(0.40m));
        Assert.That(usage.AverageTurnCost(""), Is.Null);
    }

    [Test]
    public void Vault_health_finds_broken_links_orphans_and_tasks_without_done_when()
    {
        var root = Directory.CreateTempSubdirectory("buddy-health-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "To-Do.md"), "# To-Do\n\n- [ ] T-1 · Con criterio\n  - Fatto quando: passa il test\n- [ ] T-2 · Senza criterio\n\nVedi [[Glossario]] e [[Manca]].\n");
            File.WriteAllText(Path.Combine(root, "Glossario.md"), "Termini");
            File.WriteAllText(Path.Combine(root, "Sola.md"), "Nessuno mi cita");
            using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = root }));
            var board = ProjectBoard.Load(vault, new BuddySettings().TaskIdPattern);

            var issues = VaultHealth.Check(board, NoteGraph.Build(vault.Notes), [], new DateOnly(2026, 11, 6), int.MaxValue)
                .Select(i => i.Text).ToList();

            Assert.That(issues, Has.Some.Contains("[[Manca]]"));
            Assert.That(issues, Has.Some.Contains("T-2 senza «Fatto quando»"));
            Assert.That(issues, Has.None.Contains("T-1"));
            Assert.That(issues, Has.Some.Contains("Nota isolata: Sola"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Mentor_entries_waiting_for_an_answer_are_flagged_until_answered()
    {
        var conversation = ModelCommandTests.Create(new BuddySettings { ProjectPath = Path.Combine(Path.GetTempPath(), "buddy-missing-vault") });
        string? answer = null;
        conversation.PostMentor("Cosa fa un indice?", reply =>
        {
            answer = reply;
            return Task.CompletedTask;
        });

        var question = conversation.Entries.Single();
        Assert.That(question.AwaitsReply, Is.True);

        conversation.ExplainItAsync().GetAwaiter().GetResult();
        Assert.That(answer, Is.EqualTo(Conversation.ExplainItReply));
        Assert.That(question.AwaitsReply, Is.False);
    }

    [Test]
    public void A_model_answer_after_the_mentor_question_closes_it_and_the_next_message_goes_to_the_model()
    {
        var conversation = ModelCommandTests.Create(new BuddySettings { ProjectPath = Path.Combine(Path.GetTempPath(), "buddy-missing-vault") });
        string? answer = null;
        conversation.PostMentor("Quando dici \"chi vince\", cosa conta per te?", reply =>
        {
            answer = reply;
            return Task.CompletedTask;
        });
        var question = conversation.Entries.Single();

        // Claude answered meanwhile (e.g. asking its own question): the mentor's question is over.
        conversation.Entries.Add(new ChatEntry(Speaker.Claude, "Quale DeepSeek? a) b) c)"));

        Assert.That(conversation.HasPendingReply, Is.False);
        Assert.That(question.AwaitsReply, Is.False, "no more «Spiegamelo tu»");
        conversation.SendAsync("/salta").GetAwaiter().GetResult();
        Assert.That(answer, Is.Null, "the reply meant for Claude never reaches the mentor");
    }

    [Test]
    public void Off_topic_mentor_messages_go_to_the_side_chat_and_only_answers_written_there_reach_them()
    {
        var conversation = ModelCommandTests.Create(new BuddySettings { ProjectPath = Path.Combine(Path.GetTempPath(), "buddy-missing-vault") });
        string? answer = null;
        conversation.PostMentor("Hai letto qualcosa di bello?", reply =>
        {
            answer = reply;
            return Task.CompletedTask;
        }, offTopic: true);

        Assert.That(conversation.Entries, Is.Empty);
        Assert.That(conversation.SideEntries.Single().AwaitsReply, Is.True);
        Assert.That(conversation.UnreadSide, Is.EqualTo(1));
        Assert.That(conversation.HasPendingReply, Is.False, "in the work chat the next message is not an answer to the mentor");

        conversation.ShowingSide = true;
        Assert.That(conversation.UnreadSide, Is.Zero);
        Assert.That(conversation.HasPendingReply, Is.True);
        Assert.That(conversation.VisibleEntries, Is.SameAs(conversation.SideEntries));

        conversation.SendAsync("Sì, un libro sul DDD").GetAwaiter().GetResult();
        Assert.That(answer, Is.EqualTo("Sì, un libro sul DDD"));
        Assert.That(conversation.SideEntries.Last().Text, Is.EqualTo("Sì, un libro sul DDD"));
        Assert.That(conversation.Entries, Is.Empty);

        // Nothing to answer: the side chat says where to write to the models instead of sending it to them.
        conversation.SendAsync("spiegami i record in C#").GetAwaiter().GetResult();
        Assert.That(conversation.SideEntries.Last().Text, Is.EqualTo(Conversation.SideChatHint));
        Assert.That(conversation.Entries, Is.Empty);
    }
}

public class NoteLinkExampleTests
{
    [Test]
    public void Links_in_code_and_comments_are_examples_not_links()
    {
        var targets = NoteGraph.Targets("""
            Vero: [[Glossario]]
            Esempio: `[[Nome nota]]`
            <!-- [[Nota collegata]] -->
            ```
            - [[Analisi ...]]
            ```
            """).ToList();

        Assert.That(targets, Is.EqualTo(new[] { "Glossario" }));
    }
}
