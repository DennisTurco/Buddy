using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Buddy.Core.Context;
using Buddy.Core.Llm;
using Buddy.Core.Mentor;
using Buddy.Core.Repo;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class EpisodeReflectionTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "buddy-reflection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private static Episode Episode(string date, string title, string facts, params string[] people) =>
        new(DateOnly.Parse(date), title, people, facts, "", "", "", []);

    [Test]
    public void The_summary_line_is_taken_out_of_the_reply()
    {
        var (text, summary) = EpisodeReflection.Split("Hai visto che ti aspettavi un grazie.\n\nRIASSUNTO: Mi pesa non essere riconosciuto; la prossima volta lo chiedo io.");
        Assert.That(text, Is.EqualTo("Hai visto che ti aspettavi un grazie."));
        Assert.That(summary, Is.EqualTo("Mi pesa non essere riconosciuto; la prossima volta lo chiedo io."));

        Assert.That(EpisodeReflection.Split("**Riassunto:** «Chiedo prima il perché.»").Summary, Is.EqualTo("Chiedo prima il perché."));
        Assert.That(EpisodeReflection.Split("Cosa ti aspettavi da lei?").Summary, Is.Null);
    }

    [Test]
    public void Only_a_recent_tense_or_recurring_episode_is_offered_and_only_once()
    {
        var today = new DateOnly(2026, 11, 26);
        var tense = Episode("2026-11-25", "Riunione sul rilascio", "Discussione tesa, ho alzato la voce", "Chicca");
        var calm = Episode("2026-11-24", "Caffè con Marco", "Abbiamo parlato del corso", "Marco");
        var old = Episode("2026-11-10", "Litigio sul deploy", "Abbiamo litigato", "Gino");
        var episodes = new[] { tense, calm, old };

        Assert.That(EpisodeReflection.ToOffer(episodes, today, []), Is.EqualTo(tense));
        Assert.That(EpisodeReflection.ToOffer(episodes, today, [tense.Key]), Is.Null, "calm and not recurring; the old one is past its days");
        Assert.That(EpisodeReflection.ToOffer([Episode("2026-11-26", "Oggi", "Litigato")], today, []), Is.Null, "not on the same day: it needs to settle");

        var again = Episode("2026-11-25", "Di nuovo Gino", "Abbiamo parlato del piano", "Gino");
        Assert.That(EpisodeReflection.ToOffer([again, old], today, []), Is.EqualTo(again), "someone already met in another episode");
    }

    [Test]
    public void An_episode_is_found_by_key_date_or_words()
    {
        var a = Episode("2026-11-25", "Riunione sul rilascio", "x", "Chicca");
        var b = Episode("2026-11-20", "Code review con Marco", "y", "Marco");
        Assert.That(EpisodeReflection.Find([a, b], a.Key), Is.EqualTo(a));
        Assert.That(EpisodeReflection.Find([a, b], "2026-11-20"), Is.EqualTo(b));
        Assert.That(EpisodeReflection.Find([a, b], "review marco"), Is.EqualTo(b));
        Assert.That(EpisodeReflection.Find([a, b], "nulla"), Is.Null);
        Assert.That(EpisodeReflection.WantsToStop("basta così, grazie"), Is.True);
        Assert.That(EpisodeReflection.WantsToStop("non basta mai"), Is.False);
    }

    [Test]
    public void The_prompts_ask_to_flag_quotes_of_philosophers()
    {
        Assert.That(EpisodeReflection.SystemPrompt, Does.Contain(Philosophy.QuoteMark + " Citazione"));
        Assert.That(EpisodeReflection.SystemPrompt, Does.Contain("Non inventare frasi"));
    }

    private sealed class Idle : IActivityMonitor
    {
        public TimeSpan ContinuousActivity => TimeSpan.Zero;

        public string? BusyReason => null;
    }

    [Test]
    public async Task A_reflection_asks_one_question_at_a_time_and_saves_what_was_understood()
    {
        var project = Path.Combine(_root, "vault");
        Directory.CreateDirectory(project);
        var settings = new BuddySettings { ProjectPath = project, Mentor = { Enabled = true, JournalPath = "", UserName = "Dennis" } };
        var options = Options.Create(settings);
        Episodes.Add(Episodes.PathFor(settings), Episode("2026-11-25", "Riunione sul rilascio", "Discussione tesa con Chicca", "Chicca") with { Reaction = "Ho alzato la voce." });

        var calls = new List<IReadOnlyList<LlmMessage>>();
        var fillings = new List<string>();
        var replies = new Queue<string>(["Cosa ti aspettavi da Chicca in quel momento?",
            "Ti aspettavi di essere ascoltato prima della decisione.\n📜 Citazione — Epitteto (stoico, I sec.): in sostanza: non ci turbano i fatti ma i giudizi sui fatti.\nRIASSUNTO: Mi pesa decidere senza essere sentito; la prossima volta chiedo di parlarne prima."]);
        var localAi = new LocalAi((messages, json, _) =>
        {
            if (json)
            {
                // The episode's fields from the answers so far: always from the episode as it was, so never added twice.
                fillings.Add(messages.Last().Content);
                return Task.FromResult("""{"come_ho_reagito": "Mi aspettavo che mi chiedesse un parere.", "com_e_finita": "Ho capito che devo chiedere di parlarne prima."}""");
            }

            calls.Add(messages.ToList());
            return Task.FromResult(replies.Dequeue());
        }, options);
        var vault = new VaultIndex(options);
        var repo = new GitRepo(options, TimeProvider.System);
        var conversation = new Conversation(new LocalAssistant(new OllamaClient(new HttpClient(), options), new ContextBuilder(vault, options, TimeProvider.System), options),
            new ClaudeAuth(options), new AssistantCommands(vault, repo, options), repo, options, localAi: localAi);
        using var mentor = new MentorService(conversation, new ClaudeOneShot(options), vault, repo, new Idle(), TimeProvider.System, options,
            statePath: Path.Combine(_root, "mentor-state.json"));

        await conversation.SendAsync("/rifletti riunione");
        Assert.That(conversation.Entries.Last().Text, Is.EqualTo("Cosa ti aspettavi da Chicca in quel momento?"));
        Assert.That(calls[0][0].Content, Does.Contain("Una sola domanda per messaggio").And.Contain("📜 Citazione"));
        Assert.That(calls[0][1].Content, Does.Contain("Discussione tesa con Chicca"));
        Assert.That(calls[0][0].Content, Does.Contain("si chiama Dennis").And.Contain("sono i suoi colleghi, non lui"), "never called by a colleague's name");
        Assert.That(calls[0][1].Content, Does.Contain("Colleghi coinvolti (non sono io): Chicca"));

        await conversation.SendAsync("che mi chiedesse un parere prima di decidere");
        Assert.That(calls[1].Last().Content, Is.EqualTo("che mi chiedesse un parere prima di decidere"), "the answer goes on with the same reflection");
        var closing = conversation.Entries.Last();
        Assert.That(closing.Text, Does.Contain("📜 Citazione — Epitteto").And.Contain("💡 **Da ricordare:** Mi pesa decidere senza essere sentito"));
        Assert.That(closing.Text, Does.Not.Contain("RIASSUNTO"));
        Assert.That(closing.Text, Does.Contain("💾 Aggiornato l'episodio"), "one episode: saved without a button");

        // What came out is in the episode by itself: under what the user had written, which stays as it was.
        var saved = Episodes.Read(Episodes.PathFor(settings)).Single();
        Assert.That(saved.Lesson, Is.EqualTo("Mi pesa decidere senza essere sentito; la prossima volta chiedo di parlarne prima."));
        Assert.That(saved.Reaction, Is.EqualTo("Ho alzato la voce. Mi aspettavo che mi chiedesse un parere."));
        Assert.That(saved.Outcome, Is.EqualTo("Ho capito che devo chiedere di parlarne prima."));
        Assert.That(fillings.Last(), Does.Contain("Lui: che mi chiedesse un parere prima di decidere").And.Contain("Come ho reagito (già scritto): Ho alzato la voce."));
    }
}
