using Buddy.Core.Chat;
using Buddy.Core.Mentor;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class SecondRoundTests
{
    private string _root = "";

    [SetUp]
    public void SetUp() => _root = Directory.CreateTempSubdirectory("buddy-round2-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void The_skills_map_scores_each_topic_from_its_reviews()
    {
        var topics = SkillsMap.Parse("""
            # Pillole

            - 2026-11-01 · Database e SQL · Indici — ripassi: 2026-11-03 ✓, 2026-11-10 ✓
            - 2026-11-02 · Database e SQL · Join — ripassi: 2026-11-04 ~
            - 2026-11-05 · Comunicazione · Errore: prima il fatto — ripassi:
            """);

        var database = topics.Single(t => t.Topic == "Database e SQL");
        Assert.That((database.Pills, database.Right, database.Partial), Is.EqualTo((2, 2, 1)));
        Assert.That(database.Score, Is.EqualTo(2.5 / 3).Within(0.001));
        Assert.That(topics.Single(t => t.Topic == "Comunicazione").Score, Is.Null);
        Assert.That(SkillsMap.ToMarkdown(topics), Does.Contain("| Database e SQL |"));
    }

    [Test]
    public void The_word_report_holds_only_the_cases_done()
    {
        var vaultPath = Path.Combine(_root, "vault");
        Directory.CreateDirectory(Path.Combine(vaultPath, "Test manuali"));
        File.WriteAllText(Path.Combine(vaultPath, "Test manuali", "Test manuali T-003.md"), """
            ---
            task: T-003
            updated: 2026-11-04
            ---

            Checklist per provare a mano. > [!warning] Nota ricostruita dopo un problema.

            ## Preparazione

            - [x] App avviata con **TEST**
            - [ ] Utente di prova

            ## Rollback

            - [ ] Riattivare & ricontrollare
            - [x] ~~Vecchio caso~~
            """);
        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = vaultPath }));
        var doc = TestNotes.ManualTests(vault, new BuddySettings().TaskIdPattern).Single();
        var path = Path.Combine(_root, ManualTestReport.FileName(doc));

        ManualTestReport.Write(doc, "Nascondere le categorie", path, new DateOnly(2026, 11, 6));

        Assert.That(Path.GetFileName(path), Is.EqualTo("Test manuali T-003.docx"));
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.SupersetOf(new[] { "[Content_Types].xml", "word/document.xml", "word/styles.xml", "_rels/.rels" }));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd()); // well-formed
        var text = string.Concat(xml.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
        Assert.That(text, Does.Contain("✔ App avviata con TEST").And.Contain("Casi verificati: 1 su 1 · 6 novembre 2026"));
        Assert.That(text, Does.Not.Contain("Utente di prova").And.Not.Contain("Vecchio caso").And.Not.Contain("Rollback"));
        Assert.That(text, Does.Not.Contain("Checklist per provare").And.Not.Contain("ricostruita"), "the checklist's intro stays out");
        Assert.That(text, Does.Not.Contain("Generato da Buddy"), "no footer");
    }

    [Test]
    public async Task The_timeline_puts_board_notes_and_checklist_in_order()
    {
        var vaultPath = Path.Combine(_root, "vault");
        Directory.CreateDirectory(Path.Combine(vaultPath, "Analisi"));
        File.WriteAllText(Path.Combine(vaultPath, "Da controllare.md"), "# Da controllare\n\n- [ ] 2026-11-05 · T-7 · Login bloccato · PR #12\n");
        File.WriteAllText(Path.Combine(vaultPath, "Analisi", "Analisi T-7.md"), "---\nupdated: 2026-11-01\n---\nAnalisi di T-7");
        var settings = new BuddySettings { ProjectPath = vaultPath };
        using var vault = new VaultIndex(Options.Create(settings));
        var board = ProjectBoard.Load(vault, settings.TaskIdPattern);

        var events = await TaskTimeline.BuildAsync("T-7", board, vault, repo: null, journalPath: null, vaultPath);

        Assert.That(events.Select(e => e.Icon), Is.EqualTo(new[] { "🔎", "🔀", "🔍" }), string.Join(" | ", events));
        Assert.That(events[0].Date, Is.EqualTo(new DateOnly(2026, 11, 1)));
        Assert.That(events.Last().Text, Does.Contain("Da controllare"));
    }

    [Test]
    public void A_broken_link_can_be_pointed_at_the_note_with_the_closest_name()
    {
        Assert.That(Text.Similarity.Closest("Glosario", ["Glossario", "To-Do"]), Is.EqualTo("Glossario"));
        Assert.That(Text.Similarity.Closest("Decisioni tecniche", ["Decisioni", "To-Do"]), Is.EqualTo("Decisioni"));
        Assert.That(Text.Similarity.Closest("Qualcosa di diverso", ["To-Do"]), Is.Null);

        var note = Path.Combine(_root, "Nota.md");
        File.WriteAllText(note, "Vedi [[Glosario]], [[Cartella/Glosario|il glossario]] e [[Glosario#Termini]].\r\n");
        Assert.That(VaultWriter.ReplaceLink(note, "Glosario", "Glossario"), Is.EqualTo(3));
        Assert.That(File.ReadAllText(note), Is.EqualTo("Vedi [[Glossario]], [[Glossario|il glossario]] e [[Glossario#Termini]].\r\n"));
    }

    [Test]
    public void Notes_are_grouped_by_the_task_they_are_about()
    {
        var graph = NoteGraph.Build(
        [
            new VaultNote("Analisi/Analisi NUSR-2367.md", "[[To-Do]]"),
            new VaultNote("Stima.md", "Solo NUSR-2660 qui. [[To-Do]]"),
            new VaultNote("To-Do.md", "NUSR-2367 e NUSR-2660"),
        ], new BuddySettings().TaskIdPattern);

        Assert.That(graph.Nodes.Single(n => n.Title == "Analisi NUSR-2367").TaskId, Is.EqualTo("NUSR-2367"));
        Assert.That(graph.Nodes.Single(n => n.Title == "Stima").TaskId, Is.EqualTo("NUSR-2660"));
        Assert.That(graph.Nodes.Single(n => n.Title == "To-Do").TaskId, Is.Null, "a note about several tasks belongs to none");
    }

    [Test]
    public void Do_not_commit_rules_match_the_end_of_the_path()
    {
        Assert.That(AssistantCommands.MatchesRule("Common/Services/Config/app.Debug.config", "app.Debug.config"), Is.True);
        Assert.That(AssistantCommands.MatchesRule("Common/Services/Config/app.Debug.config", "Config/*.config"), Is.True);
        Assert.That(AssistantCommands.MatchesRule("Common/myapp.Debug.config", "app.Debug.config"), Is.False);
    }

    [Test]
    public async Task The_automatic_model_picks_by_the_kind_of_message_and_the_budget_offers_a_lighter_one()
    {
        var settings = new BuddySettings { ProjectPath = Path.Combine(_root, "vault"), Claude = { AutoModel = true, DailyBudgetUsd = 1 } };
        var conversation = ModelCommandTests.Create(settings);

        Assert.That(conversation.ChooseModel("/lavora NUSR-1", null, null), Is.EqualTo("opus"));
        Assert.That(conversation.ChooseModel("cosa fa questo metodo?", null, null), Is.EqualTo("haiku"));
        Assert.That(conversation.ChooseModel(new string('x', 400), null, null), Is.Null, "a long request keeps the chosen model");

        settings.Claude.AutoModel = false;
        Assert.That(conversation.ChooseModel("/lavora NUSR-1", null, null), Is.Null);

        await conversation.SendAsync("/modello opus");
        conversation.Usage.AddTurn(1.5m, "opus");
        typeof(Conversation).GetMethod("CheckBudget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(conversation, null);
        var hint = conversation.Entries.Last();
        Assert.That(hint.Text, Does.Contain("oltre il budget"));
        Assert.That(hint.Actions.Select(a => a.Command), Is.EqualTo(new[] { "/modello sonnet", "/modello haiku" }));
    }

    [Test]
    public async Task A_quick_note_goes_to_to_do_appunti()
    {
        var vaultPath = Path.Combine(_root, "vault");
        Directory.CreateDirectory(vaultPath);
        File.WriteAllText(Path.Combine(vaultPath, "To-do Appunti.md"), "# To-do Appunti\n\n## Appunti\n\n- 2026-11-01 · vecchio\n");
        var conversation = ModelCommandTests.Create(new BuddySettings { ProjectPath = vaultPath });

        await conversation.SendAsync("/appunto chiamare Marco per il rilascio");

        Assert.That(File.ReadAllText(Path.Combine(vaultPath, "To-do Appunti.md")), Does.Contain($"- {DateTime.Today:yyyy-MM-dd} · chiamare Marco per il rilascio"));
        Assert.That(conversation.Entries.Last().Text, Does.StartWith("📝"));
    }
}
