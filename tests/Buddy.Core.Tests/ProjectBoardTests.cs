using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class ProjectBoardTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("buddy-board-").FullName;
        File.WriteAllText(Path.Combine(_root, "To-Do.md"), """
            ---
            title: To-Do
            prossimo_id: T-006
            ---

            Task da fare.

            > [!example] Formato
            > ```
            > - [ ] T-001 · 2026-10-14 · Esempio da non mostrare
            > ```

            ## Task

            - [ ] T-003 · 2026-10-15 · Nascondere le categorie 🔁1
              - Contesto: [[Scheda progetto]]
              - Fatto quando: il docente non le vede
            - [ ] T-006 · ⏸ Session replay — In attesa di: rilascio 2.0 [fino:: 2026-11-01]
            """);
        File.WriteAllText(Path.Combine(_root, "Da controllare.md"), """
            > [!example] Formato
            > ```
            > - [ ] T-001 · Titolo
            >   - Cambiato: …
              - Test web: ✅ superato (riga dell'esempio fuori dal callout)
            > ```

            ## Voci
            """);
        File.WriteAllText(Path.Combine(_root, "To-Do Fatti.md"), """
            ## Voci

            - [x] T-002 · 2026-10-15 · Metriche PostHog · PR #4 · costo: 7,22 $ · 🔁0
            """);
        File.WriteAllText(Path.Combine(_root, "To-do Appunti.md"), """
            ## Appunti

            - 2026-10-26 · Attività filtrabili per persona, manca il filtro
            """);
        File.WriteAllText(Path.Combine(_root, "Stato attuale.md"), """
            ---
            title: Stato attuale
            ---

            Orientamento per Claude. **Lo aggiorna Claude**.

            - **Versione:** nessun numero di versione
            - **Prossime priorità:** T-003 nascondere categorie
            """);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void Load_splits_the_flow_notes_into_columns()
    {
        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = _root }));

        var board = ProjectBoard.Load(vault, new BuddySettings().TaskIdPattern);

        Assert.That(board.Columns.Select(c => c.Key), Is.EqualTo(new[] { "appunti", "todo", "attesa", "corso", "controllare", "fatti" }),
            "missing notes (Da valutare, Rifiutati) give no column");
        var column = (string key) => board.Columns.Single(c => c.Key == key).Items;

        var todo = column("todo").Single();
        Assert.That(todo.Id, Is.EqualTo("T-003"), "the example inside the callout is skipped");
        Assert.That(todo.Date, Is.EqualTo(new DateOnly(2026, 10, 15)));
        Assert.That(todo.Title, Is.EqualTo("Nascondere le categorie"));
        Assert.That(todo.Rework, Is.EqualTo(1));
        Assert.That(todo.Details, Is.EqualTo(new[] { "Contesto: [[Scheda progetto]]", "Fatto quando: il docente non le vede" }));

        var waiting = column("attesa").Single();
        Assert.That(waiting.Waiting, Is.True);
        Assert.That(waiting.Title, Is.EqualTo("Session replay"));
        Assert.That(waiting.Note, Is.EqualTo("In attesa di: rilascio 2.0"));
        Assert.That(waiting.Due, Is.EqualTo(new DateOnly(2026, 11, 1)));
        Assert.That(waiting.IsDue(new DateOnly(2026, 11, 5)), Is.True);

        Assert.That(column("controllare"), Is.Empty, "the indented line after a callout belongs to the example");

        var done = column("fatti").Single();
        Assert.That(done.Done, Is.True);
        Assert.That(done.Tags, Is.EqualTo(new[] { "PR #4", "costo: 7,22 $" }));

        var note = column("appunti").Single();
        Assert.That(note.Id, Is.Null);
        Assert.That(note.Date, Is.EqualTo(new DateOnly(2026, 10, 26)));
        Assert.That(note.Title, Does.StartWith("Attività filtrabili"));

        Assert.That(board.Status, Is.EqualTo(new[]
        {
            new StatusLine("Versione", "nessun numero di versione"),
            new StatusLine("Prossime priorità", "T-003 nascondere categorie"),
        }));
        Assert.That(board.Notes, Has.Count.EqualTo(5));
    }

    [Test]
    public void Matches_looks_in_id_title_and_details()
    {
        var item = new BoardItem("T-003", null, "Nascondere le categorie", null, [], ["Fatto quando: il docente non le vede"],
            Done: false, Rejected: false, Waiting: false, Due: null, DueLabel: null, Rework: 0);

        Assert.That(item.Matches("t-003"), Is.True);
        Assert.That(item.Matches("CATEGORIE"), Is.True);
        Assert.That(item.Matches("docente"), Is.True);
        Assert.That(item.Matches("posthog"), Is.False);
    }
}

public class VaultWriterTests
{
    private string _file = "";

    [SetUp]
    public void SetUp() => _file = Path.Combine(Directory.CreateTempSubdirectory("buddy-writer-").FullName, "To-do Appunti.md");

    [TearDown]
    public void TearDown() => Directory.Delete(Path.GetDirectoryName(_file)!, recursive: true);

    [Test]
    public void ReplaceBlock_edits_and_deletes_one_item_keeping_line_endings()
    {
        File.WriteAllText(_file, "## Voci\r\n\r\n- [ ] T-1 · Uno\r\n  - dettaglio\r\n- [ ] T-2 · Due\r\n");

        VaultWriter.ReplaceBlock(_file, "- [ ] T-1 · Uno\n  - dettaglio", "- [ ] T-1 · Uno rivisto\n  - dettaglio nuovo");
        Assert.That(File.ReadAllText(_file), Is.EqualTo("## Voci\r\n\r\n- [ ] T-1 · Uno rivisto\r\n  - dettaglio nuovo\r\n- [ ] T-2 · Due\r\n"));

        VaultWriter.ReplaceBlock(_file, "- [ ] T-1 · Uno rivisto\n  - dettaglio nuovo", "");
        Assert.That(File.ReadAllText(_file), Is.EqualTo("## Voci\r\n\r\n- [ ] T-2 · Due\r\n"));
    }

    [Test]
    public void ReplaceBlock_refuses_when_the_item_changed_meanwhile()
    {
        File.WriteAllText(_file, "- [ ] T-1 · Cambiato in Obsidian\n");

        Assert.Throws<VaultConflictException>(() => VaultWriter.ReplaceBlock(_file, "- [ ] T-1 · Uno", "- [ ] T-1 · Mio"));
        Assert.That(File.ReadAllText(_file), Is.EqualTo("- [ ] T-1 · Cambiato in Obsidian\n"));
    }

    [Test]
    public void AppendItem_goes_at_the_end_of_its_section()
    {
        File.WriteAllText(_file, "---\ntitle: x\n---\n\n## Appunti\n\n- primo\n\n## Altro\n\ntesto\n");

        VaultWriter.AppendItem(_file, "Appunti", "- secondo");
        Assert.That(File.ReadAllText(_file), Is.EqualTo("---\ntitle: x\n---\n\n## Appunti\n\n- primo\n- secondo\n\n## Altro\n\ntesto\n"));

        File.WriteAllText(_file, "## Appunti\n");
        VaultWriter.AppendItem(_file, "Appunti", "- solo");
        Assert.That(File.ReadAllText(_file), Is.EqualTo("## Appunti\n\n- solo\n"));
    }

    [Test]
    public void Save_refuses_when_the_note_changed_meanwhile()
    {
        File.WriteAllText(_file, "nuovo testo\n");

        Assert.Throws<VaultConflictException>(() => VaultWriter.Save(_file, "vecchio testo\n", "mio testo\n"));
        VaultWriter.Save(_file, "nuovo testo\n", "mio testo\n");
        Assert.That(File.ReadAllText(_file), Is.EqualTo("mio testo\n"));
    }
}

public class BoardItemFieldTests
{
    [Test]
    public void Fields_cost_and_skills_are_read()
    {
        var idPattern = new System.Text.RegularExpressions.Regex($"^(?:{new BuddySettings().TaskIdPattern})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var item = ProjectBoard.ParseItems("- [x] T-2 · 2026-10-15 · Fatto · PR #4 · costo: 7,22 $ [priorità:: P1] [stima:: 2h]", idPattern).Single();

        Assert.That(item.Field("priorita"), Is.EqualTo("P1"));
        Assert.That(item.Field("stima"), Is.EqualTo("2h"));
        Assert.That(item.Field("consiglio"), Is.Null);
        Assert.That(item.Cost, Is.EqualTo(7.22m));

        var home = Directory.CreateTempSubdirectory("buddy-home-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude", "skills", "lavora"));
            File.WriteAllText(Path.Combine(home, ".claude", "skills", "lavora", "SKILL.md"), "---\nname: lavora\n---");
            Directory.CreateDirectory(Path.Combine(home, ".claude", "skills", "vuota"));
            Assert.That(Claude.ClaudeSkills.Installed(new BuddySettings(), home), Is.EquivalentTo(new[] { "lavora" }));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}

public class TestNotesTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("buddy-tests-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "Test manuali"));
        Directory.CreateDirectory(Path.Combine(_root, "Test web"));
        File.WriteAllText(Path.Combine(_root, "Test manuali", "Test manuali T-003.md"), """
            ---
            title: Test manuali T-003
            task: T-003
            updated: 2026-11-04
            ---

            <!--
            - [ ] riga del modello, da ignorare
            -->

            Checklist per nascondere le categorie.

            ## 0. Preparazione

            - [x] App avviata
            - [ ] Docente di prova

            ## 2. Caso normale

            - [x] Nascondo una categoria → lo studente non la vede
              - [ ] **Da rifare dopo il 2026-11-05**: anche nel riepilogo
            - [ ] ~~Vecchio caso~~ non più valido

            ## Rollback

            Riattivare la categoria.
            """.ReplaceLineEndings("\r\n"));
        File.WriteAllText(Path.Combine(_root, "Test web.md"), """
            ## Script

            - **t002-posthog** · `t002-posthog.spec.ts` (`npm run test:t002`) — Cosa copre: eventi PostHog · Ultima esecuzione: 2026-10-15 ✅ (2/2)
            """);
        File.WriteAllBytes(Path.Combine(_root, "Test web", "T-002-passo1-esercizi.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "Test web", "schema.png"), [1]);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void Manual_tests_are_read_and_ticked_in_place()
    {
        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = _root }));
        var doc = TestNotes.ManualTests(vault, new BuddySettings().TaskIdPattern).Single();

        Assert.That(doc.TaskId, Is.EqualTo("T-003"));
        Assert.That(doc.Updated, Is.EqualTo(new DateOnly(2026, 11, 4)));
        Assert.That(doc.Intro, Is.EqualTo("Checklist per nascondere le categorie."), "the template comment is skipped");
        Assert.That(doc.Sections.Select(s => s.Title), Is.EqualTo(new[] { "0. Preparazione", "2. Caso normale", "Rollback" }));
        Assert.That((doc.Total, doc.Done, doc.Redo), Is.EqualTo((4, 2, 1)), "struck cases do not count");

        var line = doc.Sections[0].Lines.Single(l => l.IsCheck && !l.Checked);
        VaultWriter.SetCheckbox(Path.Combine(_root, doc.RelativePath), line.Index, line.Raw, done: true);

        var text = File.ReadAllText(Path.Combine(_root, doc.RelativePath));
        Assert.That(text, Does.Contain("- [x] Docente di prova\r\n"), "only the box changes, line endings kept");
        Assert.Throws<VaultConflictException>(() => VaultWriter.SetCheckbox(Path.Combine(_root, doc.RelativePath), line.Index, line.Raw, done: false),
            "the line changed since it was read");
    }

    [Test]
    public void Web_tests_list_scripts_and_screenshots()
    {
        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = _root }));

        var script = TestNotes.Scripts(vault).Single();
        Assert.That(script.Raw, Does.StartWith("- **t002-posthog** ·"));
        Assert.That(script.NotePath, Is.EqualTo("Test web.md"));
        Assert.That(script with { Raw = "", NotePath = "" }, Is.EqualTo(new WebTestScript("t002-posthog", "t002-posthog.spec.ts", "npm run test:t002", "eventi PostHog",
            new DateOnly(2026, 10, 15), true, "(2/2)")));

        var shots = TestNotes.Screenshots(_root);
        Assert.That(shots.Select(s => (s.TaskId, s.Step, s.Caption)), Is.EquivalentTo(new (string?, int?, string)[]
        {
            ("T-002", 1, "esercizi"),
            (null, null, "schema"),
        }));
    }
}

public class ItemFieldsTests
{
    [TestCase("- [ ] T-6 · Titolo", "bug", "- [ ] T-6 · Titolo [tag:: bug]")]
    [TestCase("- [ ] T-6 · Titolo [tag:: bug] [stima:: 2h]", "feature", "- [ ] T-6 · Titolo [tag:: feature] [stima:: 2h]")]
    [TestCase("- [ ] T-6 · Titolo [tag:: bug] [stima:: 2h]", "", "- [ ] T-6 · Titolo [stima:: 2h]")]
    [TestCase("- [ ] T-6 · Titolo\n  - Fatto quando: x", "ticket", "- [ ] T-6 · Titolo [tag:: ticket]\n  - Fatto quando: x")]
    [TestCase("- [ ] T-6 · Titolo [Tag:: bug]", "analisi", "- [ ] T-6 · Titolo [tag:: analisi]")]
    public void Set_adds_replaces_or_removes_a_field_on_the_first_line(string raw, string value, string expected) =>
        Assert.That(ItemFields.Set(raw, "tag", value), Is.EqualTo(expected));
}

public class WebTestRunTests
{
    [Test]
    public void Last_run_is_replaced_keeping_the_remark()
    {
        var line = "- **t002** · `t002.spec.ts` (`npm run test:t002`) — Cosa copre: eventi · Ultima esecuzione: 2026-10-15 ✅ (2/2, senza invio). Nota: x";
        var failed = new WebTestResult(WebTestOutcome.Failed, 1, 1, TimeSpan.FromSeconds(9), []);

        Assert.That(WebTestRun.WithLastRun(line, failed, new DateOnly(2026, 11, 6)),
            Is.EqualTo("- **t002** · `t002.spec.ts` (`npm run test:t002`) — Cosa copre: eventi · Ultima esecuzione: 2026-11-06 ❌ (1/2). Nota: x"));
        Assert.That(WebTestRun.WithLastRun("- **nuovo** · `a.spec.ts`", new WebTestResult(WebTestOutcome.Passed, 3, 0, TimeSpan.Zero, []), new DateOnly(2026, 11, 6)),
            Is.EqualTo("- **nuovo** · `a.spec.ts` · Ultima esecuzione: 2026-11-06 ✅ (3/3)"));
    }

    [Test]
    public async Task Run_streams_output_and_reads_the_playwright_summary()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("bash script");
        }

        var folder = Directory.CreateTempSubdirectory("buddy-run-").FullName;
        try
        {
            var lines = new List<string>();
            var ok = await WebTestRun.RunAsync("printf '\\033[32m  ✓ uno\\033[0m\\n  2 passed (3.1s)\\n'", folder, lines.Add, CancellationToken.None);
            Assert.That(ok.Outcome, Is.EqualTo(WebTestOutcome.Passed));
            Assert.That((ok.Passed, ok.Failed), Is.EqualTo((2, 0)));
            Assert.That(lines, Does.Contain("  ✓ uno"), "colour codes stripped");

            var ko = await WebTestRun.RunAsync("echo '  1 failed'; echo '  1 passed'; exit 1", folder, _ => { }, CancellationToken.None);
            Assert.That((ko.Outcome, ko.Passed, ko.Failed), Is.EqualTo((WebTestOutcome.Failed, 1, 1)));

            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var stopped = await WebTestRun.RunAsync("sleep 20", folder, _ => { }, cancel.Token);
            Assert.That(stopped.Outcome, Is.EqualTo(WebTestOutcome.Stopped));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void Scripts_folder_comes_from_the_scheda()
    {
        var root = Directory.CreateTempSubdirectory("buddy-scheda-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Scheda progetto.md"), "## Test web\n\n- **Cartella degli script di test:** `~/claude-test/progetto/` (Playwright)\n");
            using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = root }));

            Assert.That(WebTestRun.ScriptsFolder(vault),
                Is.EqualTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "claude-test/progetto/")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Scripts_folder_in_the_test_web_note_is_relative_to_the_repo()
    {
        var root = Directory.CreateTempSubdirectory("buddy-scheda-").FullName;
        var repo = Directory.CreateTempSubdirectory("buddy-repo-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "tests", "e2e"));
            File.WriteAllText(Path.Combine(root, "Scheda progetto.md"), "# Scheda\n");
            File.WriteAllText(Path.Combine(root, "Test web.md"), "- **Cartella degli script di test:** `tests/e2e/` nel repo\n");
            using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = root }));

            Assert.That(WebTestRun.ScriptsFolder(vault, "", repo), Is.EqualTo(Path.GetFullPath(Path.Combine(repo, "tests/e2e/"))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(repo, recursive: true);
        }
    }
}

public class IdeaGeneratorTests
{
    [Test]
    public void Prompt_carries_the_project_and_what_not_to_repeat()
    {
        var item = (string title) => new BoardItem(null, null, title, null, [], [], false, false, false, null, null, 0);
        var board = new ProjectBoard(
            [
                new BoardColumn("todo", "Da fare", "📋", "To-Do.md", [item("Filtro per persona")]),
                new BoardColumn("rifiutati", "Rifiutati", "🚫", "To-Do Rifiutati.md", [item("App mobile nativa")]),
            ],
            [new StatusLine("Versione", "0.0.0")],
            []);

        var prompt = IdeaGenerator.BuildPrompt(board, "---\ntitle: x\n---\nApp per esercizi di contabilità", "statistiche per il docente", 5);

        Assert.That(prompt, Does.Contain("esattamente 5 idee"));
        Assert.That(prompt, Does.Contain("«statistiche per il docente»"));
        Assert.That(prompt, Does.Contain("- Filtro per persona"));
        Assert.That(prompt, Does.Contain("## Rifiutati (non riproporre)\n- App mobile nativa").Or.Contain("## Rifiutati (non riproporre)\r\n- App mobile nativa"));
        Assert.That(prompt, Does.Contain("App per esercizi di contabilità"));
        Assert.That(prompt, Does.Not.Contain("title: x"), "front matter left out");
    }

    [Test]
    public void Answer_is_read_as_one_idea_per_list_line()
    {
        var answer = """
            Ecco alcune idee:
            - **Grafico** dei punteggi medi per esercizio nella dashboard docente
            2. Esportare in CSV i risultati di una classe
            * Esportare in CSV i risultati di una classe
            - ok
            Spero siano utili!
            """;

        Assert.That(IdeaGenerator.Parse(answer, 5), Is.EqualTo(new[]
        {
            "Grafico dei punteggi medi per esercizio nella dashboard docente",
            "Esportare in CSV i risultati di una classe",
        }));
        Assert.That(IdeaGenerator.ToAppunto("Esportare in CSV", new DateOnly(2026, 11, 6)), Is.EqualTo("- 2026-11-06 · 💡 Esportare in CSV"));
    }
}
