using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Buddy.Core.Mentor;
using Buddy.Core.Repo;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class ThirdRoundTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("buddy-round3-").FullName;

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a vault watcher may still hold it for a moment
        }
    }

    private string Vault => Path.Combine(_root, "vault");

    private static readonly string Pattern = new BuddySettings().TaskIdPattern;

    [Test]
    public void Your_pages_are_folders_and_their_notes_are_written_with_a_date()
    {
        Directory.CreateDirectory(Vault);
        Assert.That(CustomPages.CreatePage(Vault, "Ricette di debug"), Is.EqualTo("Ricette di debug"));
        Assert.Throws<IOException>(() => CustomPages.CreatePage(Vault, "Ricette di debug"));

        var first = CustomPages.CreateNote(Vault, "Ricette di debug", "Avviare il TaskExecutor", "Argomento **TEST** da VS.", "NUSR-2367", new DateOnly(2026, 11, 6));
        var second = CustomPages.CreateNote(Vault, "Ricette di debug", "Avviare il TaskExecutor", "Altro", today: new DateOnly(2026, 11, 7));

        Assert.That(first.Replace('\\', '/'), Is.EqualTo("Pagine/Ricette di debug/Avviare il TaskExecutor.md"));
        Assert.That(second, Does.EndWith("Avviare il TaskExecutor (2).md"), "never overwritten");
        Assert.That(CustomPages.List(Vault), Is.EqualTo(new[] { "Ricette di debug" }));

        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = Vault }));
        var notes = CustomPages.Notes(vault, "Ricette di debug");
        Assert.That(notes.Select(n => CustomPages.Created(n)), Is.EqualTo(new DateOnly?[] { new(2026, 11, 7), new(2026, 11, 6) }), "newest first");
        Assert.That(CustomPages.Body(notes[1]), Is.EqualTo("Argomento **TEST** da VS."));
        Assert.That(notes[1].Content, Does.Contain("task: NUSR-2367"));
    }

    [Test]
    public void Decisions_are_read_and_a_new_one_goes_first()
    {
        Directory.CreateDirectory(Vault);
        var path = Path.Combine(Vault, "Decisioni.md");
        File.WriteAllText(path, """
            # Decisioni

            > [!example] Formato
            > ```
            > - 2026-10-14 · Cosa si è deciso — Perché: motivo · T-001
            > ```

            ## Voci

            - 2026-11-02 · NUSR-2367: stato 255 per gli account bloccati — Perché: chiamata con Mimmo · NUSR-2367
            - 2026-10-19 · Import per SysopLight col permesso 1120 — Perché: modifica piccola · NUSR-2660
            """);

        var decisions = Decisions.Parse(File.ReadAllText(path), Pattern);
        Assert.That(decisions, Has.Count.EqualTo(2), "the example in the callout is not a decision");
        Assert.That(decisions[0], Is.EqualTo(decisions[0] with
        {
            Date = new DateOnly(2026, 11, 2),
            What = "NUSR-2367: stato 255 per gli account bloccati",
            Why = "chiamata con Mimmo",
            TaskId = "NUSR-2367",
        }));

        VaultWriter.InsertItemFirst(path, Decisions.Section, Decisions.Format(new DateOnly(2026, 11, 6), "Parametro 663 a 0", "fa da interruttore", "NUSR-2367"));
        var after = Decisions.Parse(File.ReadAllText(path), Pattern);
        Assert.That(after[0].What, Is.EqualTo("Parametro 663 a 0"));
        Assert.That(File.ReadAllText(path), Does.Contain("## Voci\n\n- 2026-11-06 · Parametro 663 a 0 — Perché: fa da interruttore · NUSR-2367\n- 2026-11-02"));
    }

    [Test]
    public void Glossary_terms_keep_their_meaning_and_links()
    {
        var terms = Glossary.Parse("""
            ## Termini

            - **Parcheggio** — tabella `FattureInAttesaUtenze`: fatture tenute in attesa · [[Progetti/User/Import fatture nuova sincro]]
            - **NuovaSynchro** (nuova sincro) — import che legge dai WS di MUBI
            """);

        Assert.That(terms.Select(t => t.Term), Is.EqualTo(new[] { "NuovaSynchro", "Parcheggio" }));
        Assert.That(terms[1].Meaning, Is.EqualTo("tabella `FattureInAttesaUtenze`: fatture tenute in attesa"));
        Assert.That(terms[1].Links, Is.EqualTo(new[] { "Import fatture nuova sincro" }));
    }

    [Test]
    public void Dragging_a_card_rewrites_the_notes_and_refuses_what_skills_do()
    {
        Directory.CreateDirectory(Vault);
        File.WriteAllText(Path.Combine(Vault, "To-Do.md"), "# To-Do\n\n## Task\n\n- [ ] T-6 · Titolo da fare [priorita:: P2]\n  - Fatto quando: x\n- [ ] T-7 · Altro\n");
        File.WriteAllText(Path.Combine(Vault, "Da controllare.md"), "# Da controllare\n\n## Voci\n\n- [ ] T-1 · Già lì\n");
        var settings = new BuddySettings { ProjectPath = Vault };
        using var vault = new VaultIndex(Options.Create(settings));
        var board = ProjectBoard.Load(vault, Pattern);
        var todo = board.Columns.Single(c => c.Key == "todo");
        var waiting = board.Columns.Single(c => c.Key == "attesa");
        var review = board.Columns.Single(c => c.Key == "controllare");

        BoardMoves.Move(Vault, todo, todo.Items.Single(i => i.Id == "T-6"), waiting);
        Assert.That(File.ReadAllText(Path.Combine(Vault, "To-Do.md")), Does.Contain("- [ ] T-6 · ⏸ Titolo da fare [priorita:: P2]\n  - Fatto quando: x"));

        BoardMoves.Move(Vault, todo, todo.Items.Single(i => i.Id == "T-7"), review);
        Assert.That(File.ReadAllText(Path.Combine(Vault, "To-Do.md")), Does.Not.Contain("T-7"));
        Assert.That(File.ReadAllText(Path.Combine(Vault, "Da controllare.md")), Does.Contain("- [ ] T-1 · Già lì\n- [ ] T-7 · Altro"));

        Assert.That(BoardMoves.Refusal("todo", "fatti"), Does.Contain("/chiudi"));
        Assert.That(BoardMoves.Refusal("appunti", "todo"), Does.Contain("/smista"));
        Assert.That(BoardMoves.SetWaiting("- [ ] T-6 · ⏸ Titolo", waiting: false, "T-6"), Is.EqualTo("- [ ] T-6 · Titolo"));
    }

    [Test]
    public void Reminder_days_are_understood_and_due_cards_found()
    {
        var friday = new DateOnly(2026, 11, 6);
        Assert.That(Reminders.ParseDay("domani", friday), Is.EqualTo(new DateOnly(2026, 11, 7)));
        Assert.That(Reminders.ParseDay("lunedì", friday), Is.EqualTo(new DateOnly(2026, 11, 9)));
        Assert.That(Reminders.ParseDay("venerdì", friday), Is.EqualTo(new DateOnly(2026, 11, 13)), "the next one, not today");
        Assert.That(Reminders.ParseDay("+3", friday), Is.EqualTo(new DateOnly(2026, 11, 9)));
        Assert.That(Reminders.ParseDay("12/11", friday), Is.EqualTo(new DateOnly(2026, 11, 12)));
        Assert.That(Reminders.ParseDay("2026-12-01", friday), Is.EqualTo(new DateOnly(2026, 12, 1)));
        Assert.That(Reminders.ParseDay("boh", friday), Is.Null);

        Directory.CreateDirectory(Vault);
        File.WriteAllText(Path.Combine(Vault, "To-Do.md"), "## Task\n\n- [ ] T-5 · ⏸ Aspetta il rilascio [promemoria:: 2026-11-06]\n- [ ] T-8 · Dopo [promemoria:: 2026-11-20]\n");
        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = Vault }));
        var due = Reminders.Due(ProjectBoard.Load(vault, Pattern), friday);
        Assert.That(due.Select(r => (r.Item.Id, r.Column.Key)), Is.EqualTo(new[] { ("T-5", "attesa") }));
    }

    [Test]
    public async Task Request_templates_expand_and_are_managed_from_the_chat()
    {
        var settings = new BuddySettings { ProjectPath = Vault, Ollama = { BaseUrl = "http://127.0.0.1:1" } };
        var conversation = ModelCommandTests.Create(settings);

        await conversation.SendAsync("/modelli stack = Spiegami questo stack trace: {testo}");
        Assert.That(settings.Assistant.Templates, Is.EqualTo(new[] { "stack = Spiegami questo stack trace: {testo}" }));
        Assert.That(conversation.ExpandTemplate("/stack", "NullReference in Login"), Is.EqualTo("Spiegami questo stack trace: NullReference in Login"));

        await conversation.SendAsync("/modelli riassunto = Riassumi in 3 righe");
        Assert.That(conversation.ExpandTemplate("/riassunto", "il testo"), Is.EqualTo("Riassumi in 3 righe\n\nil testo"));

        await conversation.SendAsync("/modelli stato = no");
        Assert.That(conversation.Entries.Last().Text, Does.Contain("già un comando"));

        await conversation.SendAsync("/stack errore");
        Assert.That(conversation.Entries.Any(e => e.Speaker == Speaker.User && e.Text.StartsWith("Spiegami questo stack trace: errore")), Is.True,
            "the expanded text goes to the model");

        await conversation.SendAsync("/modelli -stack");
        Assert.That(settings.Assistant.Templates, Has.Count.EqualTo(1));
    }

    [Test]
    public void A_pinned_message_stays_pinned_when_the_conversation_is_reopened()
    {
        var archive = new ConversationArchive(Path.Combine(_root, "conversations"));
        var question = new ChatEntry(Speaker.User, "come avvio il TaskExecutor?");
        var answer = new ChatEntry(Speaker.Claude, "Con TEST negli argomenti.") { IsPinned = true };
        archive.Save("c1", [question, answer], null);

        var saved = archive.List().Single();
        Assert.That(saved.Entries.Single(e => e.Speaker == Speaker.Claude).Pinned, Is.True);
        Assert.That(answer.Headline, Is.EqualTo("Con TEST negli argomenti."));
    }

    [Test]
    public void An_answer_saved_as_a_note_lands_in_a_page_with_the_task()
    {
        Directory.CreateDirectory(Vault);
        var conversation = ModelCommandTests.Create(new BuddySettings { ProjectPath = Vault });
        var answer = new ChatEntry(Speaker.Claude, "1. Argomento TEST\n2. Compila la dll");

        var path = conversation.SaveAsNote(answer, "Avviare il TaskExecutor", "");

        Assert.That(path!.Replace('\\', '/'), Is.EqualTo($"Pagine/{Conversation.AnswersPage}/Avviare il TaskExecutor.md"));
        Assert.That(File.ReadAllText(Path.Combine(Vault, path)), Does.Contain("# Avviare il TaskExecutor").And.Contain("2. Compila la dll"));
    }

    [Test]
    public void Open_pull_requests_are_summed_up_from_gh()
    {
        var prs = PullRequests.Parse("""
            [
              {"number":12,"title":"NUSR-2367 blocco login","headRefName":"NUSR-2367-login","url":"https://github.com/x/y/pull/12","isDraft":false,
               "reviewDecision":"REVIEW_REQUIRED","updatedAt":"2026-11-06T09:00:00Z",
               "statusCheckRollup":[{"conclusion":"SUCCESS"},{"conclusion":"FAILURE"}]},
              {"number":13,"title":"Wiki","headRefName":"wiki","url":"https://github.com/x/y/pull/13","isDraft":true,
               "reviewDecision":"","updatedAt":"2026-11-05T09:00:00Z","statusCheckRollup":[{"status":"IN_PROGRESS"}]}
            ]
            """, Pattern);

        Assert.That(prs.Select(p => (p.Number, p.TaskId, p.Checks)), Is.EqualTo(new[] { (12, (string?)"NUSR-2367", "failure"), (13, null, "pending") }));
        Assert.That(prs[0].ReviewLabel, Is.EqualTo("👀 da rivedere"));
        Assert.That(prs[1].IsDraft, Is.True);
    }

    [Test]
    public void Spending_is_kept_per_day_for_the_pages()
    {
        var usage = new ClaudeUsage();
        usage.AddTurn(0.40m, "opus");
        usage.AddTurn(0.10m, "haiku");

        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.That(usage.Days[today], Is.EqualTo(new ClaudeUsage.ModelStat(2, 0.50m)));
        Assert.That(usage.Models.Keys, Is.EquivalentTo(new[] { "opus", "haiku" }));
    }
}
