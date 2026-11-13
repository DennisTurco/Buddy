using Buddy.Core.Chat;
using Buddy.Core.Context;
using Buddy.Core.Llm;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

/// <summary>The local model's jobs, with a stand-in model that answers what each test needs.</summary>
public class LocalAiTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("buddy-localai-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    /// <summary>A model that answers <paramref name="answer"/> to everything, counting the calls.</summary>
    private sealed class FakeModel(Func<string, string> answer)
    {
        public int Calls { get; private set; }

        public List<string> Requests { get; } = [];

        public Task<string> Complete(IReadOnlyList<LlmMessage> messages, bool json, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add(messages[^1].Content);
            return Task.FromResult(answer(messages[^1].Content));
        }
    }

    private static LocalAi Ai(FakeModel model, BuddySettings? settings = null) =>
        new(model.Complete, Options.Create(settings ?? new BuddySettings()));

    // ---- Router -----------------------------------------------------------------------------------------------

    [TestCase("""{"tipo":"azione","azione":"appunto","testo":"Sentire Chiara"}""", RouteKind.Action, "appunto", "Sentire Chiara")]
    [TestCase("""Ecco: {"tipo":"azione","azione":"stato","testo":"NUSR-2700"} spero vada bene""", RouteKind.Action, "stato", "NUSR-2700")]
    [TestCase("""{"tipo":"claude","azione":"","testo":""}""", RouteKind.Claude, null, "")]
    [TestCase("""{"tipo":"locale"}""", RouteKind.Local, null, "")]
    [TestCase("""{"tipo":"azione","azione":"cancella_tutto","testo":"x"}""", RouteKind.Local, null, "")]
    [TestCase("""{"tipo":"azione","azione":"appunto","testo":""}""", RouteKind.Local, null, "")]
    public async Task The_router_reads_the_model_s_json_and_never_trusts_an_unknown_action(string answer, RouteKind kind, string? action, string text)
    {
        var decision = await Ai(new FakeModel(_ => answer)).RouteAsync("messaggio");

        Assert.That(decision, Is.EqualTo(new RouteDecision(kind, action, text)));
    }

    [Test]
    public async Task No_usable_answer_or_router_off_means_no_decision()
    {
        Assert.That(await Ai(new FakeModel(_ => "non so")).RouteAsync("ciao"), Is.Null);
        Assert.That(await Ai(new FakeModel(_ => throw new HttpRequestException("off"))).RouteAsync("ciao"), Is.Null);

        var model = new FakeModel(_ => """{"tipo":"claude"}""");
        Assert.That(await Ai(model, new BuddySettings { LocalAi = { Router = false } }).RouteAsync("ciao"), Is.Null);
        Assert.That(model.Calls, Is.Zero);
    }

    [Test]
    public async Task A_slow_model_is_given_up_without_blocking_the_chat()
    {
        var ai = new LocalAi(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return "{}";
        }, Options.Create(new BuddySettings())) { Timeout = TimeSpan.FromMilliseconds(50) };

        Assert.That(await ai.RouteAsync("ciao"), Is.Null);
    }

    // ---- Conversation -----------------------------------------------------------------------------------------

    private Conversation Chat(FakeModel model, Action<BuddySettings>? configure = null)
    {
        var vaultPath = Path.Combine(_root, "vault");
        Directory.CreateDirectory(vaultPath);
        File.WriteAllText(Path.Combine(vaultPath, "To-do Appunti.md"), "# To-do Appunti\n\n## Appunti\n\n- 2026-11-01 · vecchio appunto\n- 2026-11-02 · idea per il report\n");
        File.WriteAllText(Path.Combine(vaultPath, "To-Do.md"), "# To-Do\n\n## Task\n\n- [ ] T-001 · 2026-11-01 · Login con SSO [priorita:: P2]\n");
        var settings = new BuddySettings { ProjectPath = vaultPath };
        configure?.Invoke(settings);
        var options = Options.Create(settings);
        var vault = new VaultIndex(options);
        var repo = new Repo.GitRepo(options, TimeProvider.System);
        var local = new LocalAssistant(new OllamaClient(new HttpClient(), options), new ContextBuilder(vault, options, TimeProvider.System), options);
        return new Conversation(local, new Claude.ClaudeAuth(options), new AssistantCommands(vault, repo, options), repo, options,
            localAi: new LocalAi(model.Complete, options));
    }

    [Test]
    public async Task Asking_to_note_something_is_confirmed_before_writing()
    {
        var conversation = Chat(new FakeModel(_ => """{"tipo":"azione","azione":"appunto","testo":"Sentire Chiara per i permessi"}"""));

        await conversation.SendAsync("segnami che devo sentire Chiara per i permessi");

        var answer = conversation.Entries.Last();
        Assert.That(answer.Text, Does.Contain("Sentire Chiara per i permessi"));
        Assert.That(answer.Actions.Select(a => a.Command), Is.EqualTo(new[] { "/appunto Sentire Chiara per i permessi", "/l segnami che devo sentire Chiara per i permessi" }));
        Assert.That(File.ReadAllText(Path.Combine(_root, "vault", "To-do Appunti.md")), Does.Not.Contain("Chiara"), "nothing written before the click");

        await conversation.RunActionAsync(answer, answer.Actions[0]);
        Assert.That(File.ReadAllText(Path.Combine(_root, "vault", "To-do Appunti.md")), Does.Contain("Sentire Chiara per i permessi"));
    }

    [Test]
    public async Task A_status_question_is_answered_by_the_buddy_under_the_user_s_words()
    {
        var conversation = Chat(new FakeModel(_ => """{"tipo":"azione","azione":"stato","testo":"T-001"}"""));

        await conversation.SendAsync("a che punto è la T-001?");

        Assert.That(conversation.Entries.Select(e => e.Text).First(), Is.EqualTo("a che punto è la T-001?"));
        Assert.That(conversation.Entries.Last().Text, Does.Contain("Login con SSO"));
        Assert.That(conversation.Entries.Last().Actions.Single().Command, Is.EqualTo("/l a che punto è la T-001?"));
    }

    [Test]
    public async Task Work_on_the_code_is_offered_to_claude_with_a_button_never_sent_on_its_own()
    {
        var conversation = Chat(new FakeModel(_ => """{"tipo":"claude"}"""));

        await conversation.SendAsync("perché il login lancia NullReferenceException?");

        Assert.That(conversation.Mode, Is.EqualTo(ChatMode.Local));
        Assert.That(conversation.Entries.Last().Actions.Select(a => a.Command).First(), Is.EqualTo("/c perché il login lancia NullReferenceException?"));
    }

    [Test]
    public async Task In_claude_mode_only_a_note_is_caught_and_it_can_still_go_to_claude()
    {
        var conversation = Chat(new FakeModel(_ => """{"tipo":"azione","azione":"appunto","testo":"Rilascio venerdì"}"""));
        conversation.RestoreMode(ChatMode.Claude);

        await conversation.SendAsync("ricordami che il rilascio è venerdì");

        Assert.That(conversation.Entries.Last().Actions.Select(a => a.Command), Is.EqualTo(new[] { "/appunto Rilascio venerdì", "/c ricordami che il rilascio è venerdì" }));
    }

    [Test]
    public async Task Suggerisci_proposes_a_column_for_each_appunto_and_moves_nothing()
    {
        var model = new FakeModel(_ => """{"proposte":[{"n":1,"colonna":"Rifiutare","perche":"vecchio"},{"n":2,"colonna":"da valutare","perche":"è un'idea"},{"n":9,"colonna":"To-Do","perche":"non esiste"}]}""");
        var conversation = Chat(model);
        var before = File.ReadAllText(Path.Combine(_root, "vault", "To-do Appunti.md"));

        await conversation.SendAsync("/suggerisci");

        var table = conversation.Entries.Last().Text;
        Assert.That(model.Requests.Single(), Does.Contain("1. vecchio appunto").And.Contain("2. idea per il report"));
        Assert.That(table, Does.Contain("| 1 | vecchio appunto | **Rifiutare** | vecchio |").And.Contain("**Da valutare**").And.Not.Contain("non esiste"));
        Assert.That(File.ReadAllText(Path.Combine(_root, "vault", "To-do Appunti.md")), Is.EqualTo(before));
    }

    // ---- Small jobs -------------------------------------------------------------------------------------------

    [Test]
    public async Task Pasted_text_becomes_the_card_s_fields_without_unknown_tags_or_priorities()
    {
        var draft = await Ai(new FakeModel(_ => """
            {"titolo":"Tracciamento reclami arricchiti","fatto_quando":"- Il campo Arricchimento è obbligatorio","contesto":"[NUSR-2700](https://x)",
             "dove":"","dettagli":"Quando un cliente contatta di nuovo il call center…","tag":["feature","due parole","UI"],"priorita":"alta","stima":""}
            """)).DraftCardAsync("testo incollato");

        Assert.That(draft!.Title, Is.EqualTo("Tracciamento reclami arricchiti"));
        Assert.That(draft.Context, Is.EqualTo("[NUSR-2700](https://x)"));
        Assert.That(draft.Tags, Is.EqualTo(new[] { "feature", "ui" }));
        Assert.That(draft.Priority, Is.Empty, "only P1-P3");
    }

    [Test]
    public async Task A_dictation_rewritten_too_much_keeps_the_original()
    {
        const string spoken = "ehm allora devo devo chiamare Marco per il rilascio di venerdì";
        Assert.That(await Ai(new FakeModel(_ => "Devo chiamare Marco per il rilascio di venerdì.")).CleanDictationAsync(spoken),
            Is.EqualTo("Devo chiamare Marco per il rilascio di venerdì."));
        Assert.That(await Ai(new FakeModel(_ => "Ok!")).CleanDictationAsync(spoken), Is.Null, "an answer, not a clean-up");
    }

    [Test]
    public async Task Only_long_attachments_are_condensed_and_only_when_it_helps()
    {
        var model = new FakeModel(_ => "System.NullReferenceException at Login.cs:42 — riassunto abbastanza lungo da essere utile");
        var ai = Ai(model, new BuddySettings { LocalAi = { CondenseAboveChars = 100 } });

        Assert.That(await ai.CondenseAsync(new string('x', 50)), Is.Null);
        Assert.That(model.Calls, Is.Zero);
        Assert.That(await ai.CondenseAsync(new string('x', 5000)), Does.StartWith("System.NullReferenceException"));
    }

    [Test]
    public async Task Drafts_stay_local_only_for_small_prompts()
    {
        var model = new FakeModel(_ => "feat: login con SSO\n\n- aggiunto il provider");
        var ai = Ai(model, new BuddySettings { LocalAi = { LocalDraftMaxChars = 100 } });

        Assert.That(await ai.DraftAsync(new string('d', 500)), Is.Null);
        Assert.That(await ai.DraftAsync("diff piccolo"), Does.StartWith("feat: login"));
    }

    [Test]
    public async Task A_title_is_one_short_line()
    {
        Assert.That(await Ai(new FakeModel(_ => "\"Retention dati nel report.\"\naltro")).TitleAsync(["a", "b", "c"]), Is.EqualTo("Retention dati nel report"));
        Assert.That(await Ai(new FakeModel(_ => "x")).TitleAsync(["a"]), Is.Null);
    }

    [Test]
    public void The_archive_keeps_the_ai_title_when_the_conversation_is_saved_again()
    {
        var archive = new ConversationArchive(Path.Combine(_root, "conversations"));
        var entries = new List<ChatEntry> { new(Speaker.User, "come funziona la retention dei dati nel report?") };
        archive.Save("c1", entries, null);

        archive.SetAiTitle("c1", "Retention dati");
        entries.Add(new ChatEntry(Speaker.Local, "risposta"));
        archive.Save("c1", entries, null);

        Assert.That(archive.Find("c1")!.Label, Does.EndWith("· Retention dati"));
    }

    // ---- Search by meaning ------------------------------------------------------------------------------------

    /// <summary>"Meaning" as two topics: data retention and login, with synonyms the words alone would miss.</summary>
    private static Task<IReadOnlyList<float[]>> Embed(string model, IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(t => new[]
        {
            t.Contains("retention", StringComparison.OrdinalIgnoreCase) || t.Contains("conservazione", StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
            t.Contains("login", StringComparison.OrdinalIgnoreCase) || t.Contains("accesso", StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
            0.05f,
        }).ToList());

    [Test]
    public async Task Notes_are_found_by_meaning_and_vectors_are_reused_from_the_cache()
    {
        var vaultPath = Path.Combine(_root, "vault");
        Directory.CreateDirectory(vaultPath);
        File.WriteAllText(Path.Combine(vaultPath, "Decisioni.md"), "# Decisioni\n\n## Dati\n\nLa retention dei reclami è di 5 anni, poi si anonimizza.\n\n## Accesso\n\nIl login passa da SSO aziendale per tutti gli utenti interni.\n");
        var options = Options.Create(new BuddySettings { ProjectPath = vaultPath });
        var embedded = 0;
        Task<IReadOnlyList<float[]>> Counting(string m, IReadOnlyList<string> t, CancellationToken c)
        {
            embedded += t.Count;
            return Embed(m, t, c);
        }

        var cache = Path.Combine(_root, "embeddings.json");
        using var vault = new VaultIndex(options);
        var index = new SemanticIndex(vault, options, Counting) { CacheFile = cache };

        var hits = await index.SearchAsync("per quanto tempo teniamo la conservazione dei dati?");
        Assert.That(hits.First().Heading, Is.EqualTo("Dati"));
        Assert.That(embedded, Is.EqualTo(3), "two pieces and the question");

        embedded = 0;
        var again = new SemanticIndex(vault, options, Counting) { CacheFile = cache };
        Assert.That((await again.SearchAsync("problemi di accesso")).First().Heading, Is.EqualTo("Accesso"));
        Assert.That(embedded, Is.EqualTo(1), "only the question: the notes come from the cache");
    }

    [Test]
    public async Task An_answer_never_waits_for_the_first_indexing()
    {
        var vaultPath = Path.Combine(_root, "vault");
        Directory.CreateDirectory(vaultPath);
        File.WriteAllText(Path.Combine(vaultPath, "Nota.md"), "# Nota\n\nLa retention dei dati è di cinque anni per tutti i reclami.\n");
        var options = Options.Create(new BuddySettings { ProjectPath = vaultPath });
        using var vault = new VaultIndex(options);
        var index = new SemanticIndex(vault, options, Embed) { CacheFile = null };

        Assert.That(await index.SearchAsync("conservazione", onlyIfReady: true), Is.Empty);
        await index.WarmUpAsync();
        Assert.That(await index.SearchAsync("conservazione", onlyIfReady: true), Is.Not.Empty);
    }
}
