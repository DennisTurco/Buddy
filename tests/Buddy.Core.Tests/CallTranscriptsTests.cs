using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class CallTranscriptsTests
{
    private string _vault = "";
    private string _project = "";

    private const string Answer = """
        TITOLO: Rilascio della versione 3.2
        PARTECIPANTI: Chicca, Marco
        TASK: NUSR-12
        ## Riassunto
        Si è deciso di rimandare il rilascio a lunedì.
        ## Decisioni
        - Rilascio lunedì
        ## Azioni
        - [ ] **Marco:** preparare il changelog (entro venerdì)
        - [x] **Chicca:** avvisare il cliente
        ## Punti aperti
        - Nessuno
        ## Trascrizione sistemata
        **Chicca:** Rimandiamo a lunedì?
        **Marco:** Sì, preparo il changelog.
        """;

    [SetUp]
    public void SetUp()
    {
        _vault = Directory.CreateTempSubdirectory("buddy-calls-").FullName;
        Directory.CreateDirectory(Path.Combine(_vault, ".obsidian"));
        _project = Path.Combine(_vault, "Progetti", "Demo");
        Directory.CreateDirectory(_project);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_vault, recursive: true);

    private CallTranscript ReadCall(string relative) =>
        CallTranscripts.Read(new VaultNote(relative, File.ReadAllText(Path.Combine(_project, relative))));

    [Test]
    public void A_new_call_is_saved_as_pasted_and_pending()
    {
        var relative = CallTranscripts.Create(_project, "", new DateOnly(2026, 11, 24), ["Chicca"], null, "eh allora ehm rimandiamo?");

        Assert.That(relative, Is.EqualTo(Path.Combine("Trascrizioni", "2026-11-24 Call.md")));
        var call = ReadCall(relative);
        Assert.That(call.Pending, Is.True);
        Assert.That(call.Date, Is.EqualTo(new DateOnly(2026, 11, 24)));
        Assert.That(call.People, Is.EqualTo(new[] { "Chicca" }));
        Assert.That(CallTranscripts.Original(call.Content), Is.EqualTo("eh allora ehm rimandiamo?"));
    }

    [Test]
    public void Claude_answer_fills_the_note_keeps_the_original_and_renames_a_default_title()
    {
        var relative = CallTranscripts.Create(_project, "", new DateOnly(2026, 11, 24), ["Chicca"], null, "testo grezzo");
        var content = File.ReadAllText(Path.Combine(_project, relative));

        var summary = CallTranscripts.Parse(Answer);
        Assert.That(summary, Is.Not.Null);
        var renamed = CallTranscripts.Apply(_project, relative, content, summary!);

        Assert.That(renamed, Is.EqualTo(Path.Combine("Trascrizioni", "2026-11-24 Rilascio della versione 3.2.md")));
        Assert.That(File.Exists(Path.Combine(_project, relative)), Is.False);
        var call = ReadCall(renamed);
        Assert.Multiple(() =>
        {
            Assert.That(call.Pending, Is.False);
            Assert.That(call.Title, Is.EqualTo("Rilascio della versione 3.2"));
            Assert.That(call.People, Is.EqualTo(new[] { "Chicca", "Marco" }));
            Assert.That(call.Task, Is.EqualTo("NUSR-12"));
            Assert.That(call.Summary, Does.StartWith("Si è deciso"));
            Assert.That((call.Actions, call.OpenActions), Is.EqualTo((2, 1)));
            Assert.That(call.Content, Does.Contain("## Punti aperti\n\n_Nessuno._").Or.Contain("## Punti aperti\r\n\r\n_Nessuno._"));
            Assert.That(call.Content.ReplaceLineEndings("\n"),
                Does.Contain("**Chicca:** Rimandiamo a lunedì?\n\n**Marco:** Sì, preparo il changelog."), "one paragraph per turn");
            Assert.That(CallTranscripts.Original(call.Content), Is.EqualTo("testo grezzo"));
        });
    }

    [Test]
    public void Actions_not_done_and_open_points_become_tasks_with_a_request_for_claude()
    {
        var relative = CallTranscripts.Create(_project, "Sync", new DateOnly(2026, 11, 24), [], "NUSR-12", "testo");
        var answer = Answer.Replace("- Nessuno", "- Chiedere a Tony quali sinonimi usare e controllare le configurazioni di demo");
        var saved = CallTranscripts.Apply(_project, relative, File.ReadAllText(Path.Combine(_project, relative)), CallTranscripts.Parse(answer)!);
        var call = ReadCall(saved);

        Assert.That(call.Tasks, Is.EqualTo(new[]
        {
            new CallTask(true, "Marco: preparare il changelog (entro venerdì)"), // the ticked one is left out, bold marks too
            new CallTask(false, "Chiedere a Tony quali sinonimi usare e controllare le configurazioni di demo"),
        }));

        var request = CallTranscripts.SolveRequest(call, call.Tasks[1], @"C:\vault\Trascrizioni\Sync.md");
        Assert.That(request, Does.Contain("un punto rimasto aperto: «Chiedere a Tony quali sinonimi usare"));
        Assert.That(request, Does.Contain(@"C:\vault\Trascrizioni\Sync.md (task NUSR-12)"));
        Assert.That(request, Does.Contain("non modificare file, non fare commit"));
    }

    [Test]
    public void A_note_edited_while_Claude_worked_is_not_overwritten()
    {
        var relative = CallTranscripts.Create(_project, "Sync", new DateOnly(2026, 11, 24), [], null, "testo");
        var file = Path.Combine(_project, relative);
        var before = File.ReadAllText(file);
        File.AppendAllText(file, "\naggiunta mia");

        Assert.Throws<IOException>(() => CallTranscripts.Apply(_project, relative, before, CallTranscripts.Parse(Answer)!));
        Assert.That(File.ReadAllText(file), Does.EndWith("aggiunta mia"));
    }

    [Test]
    public void A_title_given_by_the_user_is_kept()
    {
        var relative = CallTranscripts.Create(_project, "Sync settimanale", new DateOnly(2026, 11, 24), [], null, "testo");
        var renamed = CallTranscripts.Apply(_project, relative, File.ReadAllText(Path.Combine(_project, relative)), CallTranscripts.Parse(Answer)!);

        Assert.That(renamed, Is.EqualTo(relative));
        Assert.That(ReadCall(renamed).Title, Is.EqualTo("Sync settimanale"));
    }

    [Test]
    public void The_prompt_names_the_user_and_asks_to_attribute_turns_from_the_context()
    {
        var relative = CallTranscripts.Create(_project, "", new DateOnly(2026, 11, 24), ["Dennis", "Emiliano"], null, "allora chiedi a Chiara");
        var call = ReadCall(relative);

        var prompt = CallTranscripts.Prompt(call, CallTranscripts.Original(call.Content), [], [], "Dennis");

        Assert.That(prompt, Does.Contain("Chi ha fatto la call e te la manda è Dennis"));
        Assert.That(prompt, Does.Contain("ricavalo dal contesto"));
        Assert.That(prompt, Does.Contain("mai un nome tirato a indovinare"));
        Assert.That(CallTranscripts.Prompt(call, "x", [], []), Does.Not.Contain("te la manda"), "no name, no line about it");
    }

    [Test]
    public void New_project_terms_go_to_the_Glossario_once_with_a_link_to_the_call()
    {
        var answer = Answer + "\n## Termini nuovi\n- **DemoEnergia** — il DB locale di prova del cliente Energia\n- **PDC** — punto di consegna\n";
        var summary = CallTranscripts.Parse(answer)!;
        Assert.That(summary.NewTerms.Select(t => t.Term), Is.EqualTo(new[] { "DemoEnergia", "PDC" }));
        Assert.That(CallTranscripts.Parse(Answer)!.NewTerms, Is.Empty, "no section, no terms");
        Assert.That(CallTranscripts.Prompt(ReadCall(CallTranscripts.Create(_project, "x", new DateOnly(2026, 11, 24), [], null, "t")), "t", [], []),
            Does.Contain("## Termini nuovi"));

        var glossary = Path.Combine(_project, "Glossario.md");
        File.WriteAllText(glossary, Glossary.Template + Glossary.Format("PDC", "punto di consegna") + "\n");

        var added = Glossary.AddMissing(glossary, summary.NewTerms, "2026-11-24 Sync");

        Assert.That(added, Is.EqualTo(new[] { "DemoEnergia" }), "PDC was there already");
        var terms = Glossary.Parse(File.ReadAllText(glossary));
        Assert.That(terms.Select(t => t.Term), Is.EquivalentTo(new[] { "PDC", "DemoEnergia" }));
        var demo = terms.Single(t => t.Term == "DemoEnergia");
        Assert.That(demo.Meaning, Is.EqualTo("il DB locale di prova del cliente Energia"));
        Assert.That(demo.Links, Is.EqualTo(new[] { "2026-11-24 Sync" }));
        Assert.That(Glossary.AddMissing(glossary, summary.NewTerms), Is.Empty, "a second time adds nothing");
    }

    [Test]
    public void The_notes_for_Claude_go_into_the_prompt_and_stay_in_the_note()
    {
        var relative = CallTranscripts.Create(_project, "Sync", new DateOnly(2026, 11, 24), [], null, "testo grezzo",
            "Ignora la parte sul calcetto.\nIl cliente è Acme.");
        var call = ReadCall(relative);

        Assert.That(call.Notes, Is.EqualTo("Ignora la parte sul calcetto.\nIl cliente è Acme."));
        Assert.That(CallTranscripts.Original(call.Content), Is.EqualTo("testo grezzo"), "the notes are not part of the transcript");
        var prompt = CallTranscripts.Prompt(call, CallTranscripts.Original(call.Content), [], []);
        Assert.That(prompt, Does.Contain("Note dell'utente su questa call"));
        Assert.That(prompt, Does.Contain("Il cliente è Acme."));

        var saved = CallTranscripts.Apply(_project, relative, call.Content, CallTranscripts.Parse(Answer)!);
        Assert.That(ReadCall(saved).Notes, Is.EqualTo(call.Notes), "kept once Claude has cleaned the call");

        var plain = ReadCall(CallTranscripts.Create(_project, "Altra", new DateOnly(2026, 11, 24), [], null, "testo"));
        Assert.That(plain.Notes, Is.Empty);
        Assert.That(CallTranscripts.Prompt(plain, "testo", [], []), Does.Not.Contain("Note dell'utente"));
    }

    [Test]
    public void An_answer_without_a_summary_is_not_accepted() =>
        Assert.That(CallTranscripts.Parse("Non riesco a leggere la trascrizione."), Is.Null);

    [Test]
    public void A_webvtt_export_becomes_speaker_lines()
    {
        const string vtt = """
            WEBVTT

            8f1c2d3e-aaaa-bbbb-cccc-1234567890ab/12-0
            00:00:01.000 --> 00:00:03.000
            <v Chicca Rossi>Allora, partiamo</v>

            00:00:03.000 --> 00:00:05.000
            <v Chicca Rossi>dal rilascio.</v>

            2
            00:00:05.000 --> 00:00:07.000
            <v Marco>Ok, add the flag.</v>
            """;

        Assert.That(CallTranscripts.Normalize(vtt),
            Is.EqualTo("Chicca Rossi: Allora, partiamo dal rilascio.\nMarco: Ok, add the flag."));
    }

    [Test]
    public void Plain_text_is_left_as_it_is() =>
        Assert.That(CallTranscripts.Normalize("  Chicca: ciao\nMarco: ciao  "), Is.EqualTo("Chicca: ciao\nMarco: ciao"));
}
