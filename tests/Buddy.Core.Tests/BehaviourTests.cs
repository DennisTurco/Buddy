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

    private static int MentorMessages(Conversation conversation) => conversation.Entries.Count(e => e.Speaker == Speaker.Mentor);

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
