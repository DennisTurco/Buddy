using Buddy.Core.Chat;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class SaidTests
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);
    private string _root = "";
    private string _project = "";

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("buddy-said-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, ".obsidian"));
        _project = Path.Combine(_root, "Progetti", "Demo");
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_project, "Persone.md"), "# Persone\n\n## Antonio Bianchi\n\n- **Soprannome:** Tony\n- **Ruolo:** analista\n\n## Domenico Nicoli\n\n- **Soprannome:** Mimmo\n");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void A_command_gives_who_what_topic_and_the_day()
    {
        Assert.That(Said.ParseCommand("Tony: mandarmi i sinonimi entro venerdì #CleanEnergy", Tuesday),
            Is.EqualTo(("Tony", "mandarmi i sinonimi", (string?)"CleanEnergy", (DateOnly?)new DateOnly(2026, 11, 27))));
        Assert.That(Said.ParseCommand("io: scrivere a Emiliano entro il 30", Tuesday)?.Due, Is.EqualTo(new DateOnly(2026, 11, 30)));
        Assert.That(Said.ParseCommand("Mimmo: i test in PROD li decide lui", Tuesday)?.Due, Is.Null);
        Assert.That(Said.ParseCommand("nessun chi qui", Tuesday), Is.Null);
    }

    [Test]
    public void The_registry_keeps_entries_newest_first_and_ticks_a_commitment_in_place()
    {
        Said.Add(_project, new SaidEntry(Tuesday.AddDays(-1), "Domenico Nicoli", SaidKind.Info, "i test in PROD li decide lui", "CleanEnergy", null, false, "", 0));
        var commitment = Said.Add(_project, new SaidEntry(Tuesday, "Antonio Bianchi", SaidKind.Impegno, "mandare i sinonimi", null, new DateOnly(2026, 11, 27), false, "", 0));

        var entries = Said.ParseRegistry(File.ReadAllText(Said.PathFor(_project)), Said.FileName);
        Assert.That(entries.Select(e => e.Text), Is.EqualTo(new[] { "mandare i sinonimi", "i test in PROD li decide lui" }));
        Assert.That(entries[0].IsOpenCommitment, Is.True);
        Assert.That(entries[0].Due, Is.EqualTo(new DateOnly(2026, 11, 27)));
        Assert.That(entries[1].Topic, Is.EqualTo("CleanEnergy"));
        Assert.That(commitment.Line, Is.EqualTo(entries[0].Line));

        Said.SetDone(_project, entries[0], done: true);
        Assert.That(File.ReadAllText(Said.PathFor(_project)), Does.Contain("- [x] 2026-11-24 · Antonio Bianchi · impegno · mandare i sinonimi [entro:: 2026-11-27]"));
        Assert.That(Said.ParseRegistry(File.ReadAllText(Said.PathFor(_project)), Said.FileName)[0].Done, Is.True);
    }

    [Test]
    public void A_call_gives_its_decisions_commitments_and_what_to_remember_and_ticks_in_its_note()
    {
        var relative = CallTranscripts.Create(_project, "Data Collector", Tuesday, ["Dennis", "Mimmo"], "NUSR-1", "testo");
        var summary = CallTranscripts.Parse("""
            TITOLO: Data Collector
            PARTECIPANTI: Dennis, Domenico Nicoli
            TASK: -
            ## Riassunto
            Si testa in PROD.
            ## Decisioni
            - Si testa in PROD fino al go live
            ## Azioni
            - [ ] **Domenico Nicoli:** chiarire i sinonimi (entro 2026-11-27)
            - [x] **Dennis:** scrivere a Emiliano
            ## Punti aperti
            - Nessuno
            ## Da ricordare
            - **Domenico Nicoli:** i test in PROD li decide lui
            ## Trascrizione sistemata
            **Dennis:** ok
            """)!;
        Assert.That(summary.Remember, Does.Contain("li decide lui"));
        CallTranscripts.Apply(_project, relative, File.ReadAllText(Path.Combine(_project, relative)), summary);
        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = _project }));

        var all = Said.All(vault, _project);
        Assert.That(all.Select(e => (e.Kind, e.Who, e.Text)), Is.EquivalentTo(new[]
        {
            (SaidKind.Decisione, "", "Si testa in PROD fino al go live"),
            (SaidKind.Impegno, "Domenico Nicoli", "chiarire i sinonimi (entro 2026-11-27)"),
            (SaidKind.Impegno, "Dennis", "scrivere a Emiliano"),
            (SaidKind.Info, "Domenico Nicoli", "i test in PROD li decide lui"),
        }));
        var open = all.Single(e => e.IsOpenCommitment);
        Assert.That(open.Due, Is.EqualTo(new DateOnly(2026, 11, 27)));
        Assert.That(open.SourceTitle, Is.EqualTo("Data Collector"));
        Assert.That(open.Topic, Is.EqualTo("NUSR-1"), "the call's task");

        Said.SetDone(_project, open, done: true);
        Assert.That(File.ReadAllText(Path.Combine(_project, relative)), Does.Contain("- [x] **Domenico Nicoli:** chiarire i sinonimi"));
    }

    [Test]
    public void The_chat_commands_write_the_registry_with_full_names_and_list_commitments()
    {
        var settings = new BuddySettings { ProjectPath = _project, Mentor = { UserName = "Dennis" } };
        using var vault = new VaultIndex(Options.Create(settings));
        var commands = new AssistantCommands(vault, new Buddy.Core.Repo.GitRepo(Options.Create(settings), TimeProvider.System), Options.Create(settings));

        Assert.That(commands.AddSaid(SaidKind.Impegno, "Tony: mandarmi i sinonimi entro 2026-11-20", Tuesday), Does.Contain("**Antonio Bianchi**"));
        Assert.That(commands.AddSaid(SaidKind.Impegno, "io: scrivere a Emiliano entro venerdì", Tuesday), Does.Contain("**Dennis**"));
        Assert.That(commands.AddSaid(SaidKind.Info, "Mimmo: i test in PROD li decide lui #CleanEnergy", Tuesday), Does.Contain("#CleanEnergy"));
        Assert.That(commands.AddSaid(SaidKind.Info, "senza chi", Tuesday), Does.Contain("Scrivi chi l'ha detto"));
        vault.Refresh();

        var list = commands.Commitments("", Tuesday);
        Assert.That(list, Does.Contain("**Tocca a te**").And.Contain("scrivere a Emiliano"));
        Assert.That(list, Does.Contain("**Antonio Bianchi**").And.Contain("⚠ **20 nov** · mandarmi i sinonimi"), "late");
        Assert.That(commands.Commitments("Tony", Tuesday), Does.Contain("mandarmi i sinonimi").And.Not.Contain("Emiliano"));
    }

    [Test]
    public void The_question_picks_what_was_said_by_the_people_and_on_the_words_it_names()
    {
        var people = People.Parse(File.ReadAllText(Path.Combine(_project, "Persone.md")));
        SaidEntry Entry(string who, string text) => new(Tuesday, who, SaidKind.Info, text, null, null, false, Said.FileName, 1, Said.Note);
        var entries = new[]
        {
            Entry("Domenico Nicoli", "i test in PROD li decide lui"),
            Entry("Antonio Bianchi", "i sinonimi sono nella tabella Config"),
            Entry("Laura Rossi", "le ferie si chiedono a fine mese"),
        };

        var prompt = SaidQuery.Prompt("cosa ha detto Mimmo sui test?", entries, [], people, "Dennis", _project, Tuesday);

        Assert.That(prompt, Does.Contain("li decide lui").And.Not.Contain("ferie"));
        Assert.That(prompt, Does.Contain("## Domenico Nicoli"), "the card of who is named");
        Assert.That(prompt, Does.Contain("cita la fonte").And.Contain("Chi ti fa la domanda è Dennis"));
        Assert.That(SaidQuery.Prompt("sinonimi?", entries, [], people, null, _project, Tuesday), Does.Contain("tabella Config").And.Not.Contain("li decide lui"));
    }
}
