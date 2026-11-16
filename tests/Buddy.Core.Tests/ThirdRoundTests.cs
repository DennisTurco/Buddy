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
    public void People_are_read_from_their_sections_and_a_new_one_is_appended()
    {
        Directory.CreateDirectory(Vault);
        var path = Path.Combine(Vault, "Persone.md");
        File.WriteAllText(path, """
            # Persone

            > ```
            > ## Nome Cognome
            > - **Compito:** esempio
            > ```

            ## Chiara Bianchi

            - **Soprannome:** Chicca, CB
            - **Compito:** DBA
            - **Descrizione:** permessi e migrazioni del database
            - **Note:** meglio la mattina, su Teams
            Non sentirla il venerdì.
            """);

        People.Add(path, People.Format("Marco Verdi", "Il Verde ,", "Product owner", "priorità del backlog", ""));
        Assert.That(File.ReadAllText(path), Does.Contain("## Marco Verdi\n\n- **Soprannome:** Il Verde\n- **Compito:** Product owner"));
        Assert.Throws<IOException>(() => People.Add(path, People.Format("chiara bianchi", "", "", "", "")));

        var people = People.Parse(File.ReadAllText(path));
        Assert.That(people.Select(p => p.Name), Is.EqualTo(new[] { "Chiara Bianchi", "Marco Verdi" }), "the example is not a person");
        Assert.That(people[0].Role, Is.EqualTo("DBA"));
        Assert.That(people[0].Description, Is.EqualTo("permessi e migrazioni del database"));
        Assert.That(people[0].Notes, Is.EqualTo("meglio la mattina, su Teams\nNon sentirla il venerdì."));
        Assert.That(people[1].Notes, Is.Empty);
        Assert.That(people[0].Nicknames, Is.EqualTo(new[] { "Chicca", "CB" }));
        Assert.That(people[0].Display, Is.EqualTo("Chiara Bianchi (Chicca, CB)"));
        Assert.That(people[1].Nicknames, Is.EqualTo(new[] { "Il Verde" }));

        Assert.That(People.RelevantTo(people, "ho scritto a Chiara?").Select(p => p.Name), Is.EqualTo(new[] { "Chiara Bianchi" }));
        Assert.That(People.RelevantTo(people, "la chicca ha risposto?").Select(p => p.Name), Is.EqualTo(new[] { "Chiara Bianchi" }));
        Assert.That(People.RelevantTo(people, "l'ha visto CB?").Select(p => p.Name), Is.EqualTo(new[] { "Chiara Bianchi" }), "short nicknames count too");
        Assert.That(People.RelevantTo(people, "ne parlo con il Verde").Select(p => p.Name), Is.EqualTo(new[] { "Marco Verdi" }), "a nickname of several words");
        Assert.That(People.RelevantTo(people, "con chi parlo dei permessi?"), Has.Count.EqualTo(2));
        Assert.That(People.RelevantTo(people, "a che punto è T-7?"), Is.Empty);
    }

    [Test]
    public void Pasted_and_dropped_images_go_in_Allegati_and_are_embedded_by_name()
    {
        Directory.CreateDirectory(Vault);
        var at = new DateTime(2026, 11, 16, 14, 30, 5);

        Assert.That(NoteImages.SavePasted(Vault, [1, 2, 3], at), Is.EqualTo("![[Pasted image 20261116143005.png]]"));
        Assert.That(NoteImages.SavePasted(Vault, [4], at), Is.EqualTo("![[Pasted image 20261116143005 (2).png]]"), "never overwritten");
        Assert.That(File.ReadAllBytes(Path.Combine(Vault, "Allegati", "Pasted image 20261116143005.png")), Is.EqualTo(new byte[] { 1, 2, 3 }));

        var outside = Path.Combine(_root, "schermata.jpg");
        File.WriteAllBytes(outside, [9]);
        Assert.That(NoteImages.AddFile(Vault, outside), Is.EqualTo("![[schermata.jpg]]"));
        Assert.That(File.Exists(Path.Combine(Vault, "Allegati", "schermata.jpg")), "copied into the vault");

        var inside = Path.Combine(Vault, "Analisi", "schema.png");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllBytes(inside, [7]);
        Assert.That(NoteImages.AddFile(Vault, inside), Is.EqualTo("![[schema.png]]"));
        Assert.That(File.Exists(Path.Combine(Vault, "Allegati", "schema.png")), Is.False, "already in the vault: only linked");

        Assert.That(NoteImages.IsImage("a.PNG") && !NoteImages.IsImage("a.pdf"));
    }

    [Test]
    public void Cards_name_who_they_depend_on_and_the_line_says_when_to_reach_them()
    {
        Directory.CreateDirectory(Vault);
        File.WriteAllText(Path.Combine(Vault, "Persone.md"),
            "# Persone\n\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n- **Compito:** DBA\n- **Note:** meglio la mattina\n");
        File.WriteAllText(Path.Combine(Vault, "To-Do.md"), "# To-Do\n\n## Task\n\n"
            + "- [ ] T-1 · 2026-11-10 · Permessi tabella [chi:: Marco]\n"
            + "- [ ] T-2 · 2026-11-12 · ⏸ Migrazione [chi:: Chicca]\n"
            + "- [ ] T-3 · Senza nessuno\n");
        var settings = Options.Create(new BuddySettings { ProjectPath = Vault });
        using var vault = new VaultIndex(settings);
        var board = ProjectBoard.Load(vault, Pattern);
        var people = People.Parse(File.ReadAllText(Path.Combine(Vault, "Persone.md")));

        var cards = People.OnCards(board, people);

        Assert.That(cards.Select(c => c.Item.Id), Is.EqualTo(new[] { "T-2", "T-1" }), "waiting first, cards without [chi::] left out");
        Assert.That(cards[0].Person?.Name, Is.EqualTo("Chiara Bianchi"), "found by nickname");
        Assert.That(cards[1].Person, Is.Null, "not in the Persone note: only the name");
        var line = People.Line(cards[0], new DateOnly(2026, 11, 16));
        Assert.That(line, Does.Contain("**T-2**").And.Contain("aspetta").And.Contain("**Chiara Bianchi (Chicca)**, DBA").And.Contain("_meglio la mattina_"));
        Assert.That(People.Line(cards[1], new DateOnly(2026, 11, 16)), Does.Contain("da sentire: **Marco**"));

        // Asking about the task brings in the person it waits on.
        var context = new Context.ContextBuilder(vault, settings, TimeProvider.System).Build("a che punto è T-2?");
        Assert.That(context.Text, Does.Contain("PERSONE").And.Contain("meglio la mattina"));
    }

    [Test]
    public void Sentita_writes_the_last_contact_in_the_persons_section()
    {
        Directory.CreateDirectory(Vault);
        var path = Path.Combine(Vault, "Persone.md");
        File.WriteAllText(path, "# Persone\r\n\r\n> ```\r\n> ## Chiara Bianchi\r\n> - **Compito:** esempio\r\n> ```\r\n\r\n"
                                + "## Chiara Bianchi\r\n\r\n- **Soprannome:** Chicca\r\n- **Compito:** DBA\r\n\r\nNon il venerdì.\r\n\r\n"
                                + "## Marco Verdi\r\n\r\n- **Compito:** PO\r\n- **Sentito:** 2026-11-01\r\n");

        Assert.That(People.MarkContacted(path, "chicca", new DateOnly(2026, 11, 16))?.Name, Is.EqualTo("Chiara Bianchi"));
        Assert.That(People.MarkContacted(path, "Marco", new DateOnly(2026, 11, 14))?.Name, Is.EqualTo("Marco Verdi"), "first name");
        Assert.That(People.MarkContacted(path, "Nessuno", new DateOnly(2026, 11, 16)), Is.Null);

        var text = File.ReadAllText(path);
        Assert.That(text, Does.Contain("- **Compito:** DBA\r\n- **Sentita:** 2026-11-16\r\n\r\nNon il venerdì."), "after the fields, line endings kept");
        Assert.That(text, Does.Contain("- **Sentita:** 2026-11-14\r\n").And.Not.Contain("2026-11-01"), "replaced, not added");
        Assert.That(text, Does.Contain("> - **Compito:** esempio\r\n> ```"), "the example is untouched");

        var people = People.Parse(text);
        Assert.That(people[0].LastContact, Is.EqualTo(new DateOnly(2026, 11, 16)));
        Assert.That(people[0].Notes, Is.EqualTo("Non il venerdì."), "the date is not a note");
        Assert.That(people[0].LastContactLabel(new DateOnly(2026, 11, 16)), Is.EqualTo("sentita oggi"));
        Assert.That(people[1].LastContactLabel(new DateOnly(2026, 11, 16)), Is.EqualTo("sentita 2 giorni fa"));
        Assert.That(People.Describe(people[1]), Does.Contain("Sentita l'ultima volta: 2026-11-14"));
    }

    [Test]
    public void Editing_a_person_rewrites_only_their_section_and_keeps_the_last_contact()
    {
        Directory.CreateDirectory(Vault);
        var path = Path.Combine(Vault, "Persone.md");
        File.WriteAllText(path, "# Persone\r\n\r\n## Chiara Bianchi\r\n\r\n- **Compito:** DBA\r\n- **Sentita:** 2026-11-10\r\nNon il venerdì.\r\n\r\n"
                                + "## Marco Verdi\r\n\r\n- **Compito:** PO\r\n");
        var chiara = People.Parse(File.ReadAllText(path)).Single(p => p.Name == "Chiara Bianchi");

        People.Update(path, chiara.Name, chiara.Fields with
        {
            Nicknames = "Chicca",
            Relationship = "Delicato",
            Communication = "per iscritto, con i dati",
            Notes = "Non il venerdì.\nMeglio dopo le 10.",
        });

        var text = File.ReadAllText(path);
        Assert.That(text, Does.Contain("## Chiara Bianchi\r\n\r\n- **Soprannome:** Chicca\r\n- **Compito:** DBA\r\n- **Rapporto:** delicato\r\n"
                                       + "- **Come comunicare:** per iscritto, con i dati\r\n- **Note:**\r\n  - Non il venerdì.\r\n  - Meglio dopo le 10.\r\n"
                                       + "- **Sentita:** 2026-11-10\r\n\r\n## Marco Verdi\r\n\r\n- **Compito:** PO\r\n"));

        var edited = People.Parse(text).Single(p => p.Name == "Chiara Bianchi");
        Assert.That(edited.Relationship, Is.EqualTo("delicato"));
        Assert.That(edited.NeedsCare);
        Assert.That(edited.Communication, Is.EqualTo("per iscritto, con i dati"));
        Assert.That(edited.Notes, Is.EqualTo("- Non il venerdì.\n- Meglio dopo le 10."));
        Assert.That(edited.LastContact, Is.EqualTo(new DateOnly(2026, 11, 10)));
        Assert.That(People.Describe(edited), Does.Contain("Come comunicare: per iscritto").And.Contain("Rapporto: delicato (preparare bene"));
        Assert.That(People.Describe(edited with { Relationship = "critico" }), Does.Contain("Rapporto: critico (molto delicato: solo il necessario"));
        Assert.That((edited with { Relationship = "critico" }).NeedsCare);

        // Renamed: the same section; onto another person's name: refused.
        People.Update(path, "Chiara Bianchi", edited.Fields with { Name = "Chiara B." });
        Assert.That(People.Parse(File.ReadAllText(path)).Select(p => p.Name), Is.EqualTo(new[] { "Chiara B.", "Marco Verdi" }));
        Assert.Throws<IOException>(() => People.Update(path, "Chiara B.", edited.Fields with { Name = "Marco Verdi" }));
        Assert.Throws<IOException>(() => People.Update(path, "Nessuno", edited.Fields));
    }

    [Test]
    public void Lists_in_a_persons_fields_are_read_nested_and_repaired_when_squeezed_on_one_line()
    {
        var people = People.Parse("""
            ## Chiara Sirocchi

            - **Compito:** PO
            - **Descrizione:** - è la PO di tutta l'azienda - lavora a stretto contatto con CPL - si occupa della fatturazione
            - **Note:**
              - per info di configurazioni sulle card
              - dopo la creazione di una card, per avvisarla
            - **Come comunicare:** per iscritto
            Una riga libera.
            """);

        var chiara = people.Single();
        Assert.That(chiara.Description, Is.EqualTo("- è la PO di tutta l'azienda\n- lavora a stretto contatto con CPL\n- si occupa della fatturazione"),
            "saved on one line by the first version of the form: split back");
        Assert.That(chiara.Notes, Is.EqualTo("- per info di configurazioni sulle card\n- dopo la creazione di una card, per avvisarla\nUna riga libera."));
        Assert.That(chiara.Communication, Is.EqualTo("per iscritto"));
        Assert.That(People.Inline(chiara.Description), Is.EqualTo("è la PO di tutta l'azienda; lavora a stretto contatto con CPL; si occupa della fatturazione"));

        // Written back from the form (lines typed with or without the dash): a nested list, read the same way.
        var section = People.Format(chiara.Fields with { Communication = "per iscritto\nbrevi" });
        Assert.That(section, Does.Contain("- **Descrizione:**\n  - è la PO di tutta l'azienda\n  - lavora a stretto contatto con CPL\n"));
        Assert.That(section, Does.Contain("- **Come comunicare:**\n  - per iscritto\n  - brevi\n"));
        Assert.That(section, Does.Contain("- **Compito:** PO\n"), "one line stays inline");
        Assert.That(People.Parse(section).Single().Description, Is.EqualTo(chiara.Description));
        Assert.That(People.Parse("## X\n\n- **Note:** dashboard - ok\n").Single().Notes, Is.EqualTo("dashboard - ok"), "a dash inside a sentence is not a list");
    }

    [Test]
    public void Episodes_are_saved_newest_first_and_updated_after_the_analysis()
    {
        var path = Path.Combine(_root, "Diario", "Episodi.md");
        var first = Episodes.Add(path, new Episode(new DateOnly(2026, 11, 10), "Riunione sul rilascio", ["Chiara Bianchi"],
            "Chicca ha chiesto di rimandare\nIo ho insistito", "Mi sono innervosito", "", "", []));
        var second = Episodes.Add(path, new Episode(new DateOnly(2026, 11, 12), "Riunione sul rilascio", ["Marco Verdi"], "Tutto ok", "", "", "", []));
        var third = Episodes.Add(path, new Episode(new DateOnly(2026, 11, 12), "Riunione sul rilascio", [], "Di nuovo", "", "", "", []));

        Assert.That(third.Title, Is.EqualTo("Riunione sul rilascio (2)"), "same day and title: numbered, so the key stays unique");
        var text = File.ReadAllText(path);
        Assert.That(text, Does.StartWith("# Episodi"));
        Assert.That(text, Does.Contain("## 2026-11-10 · Riunione sul rilascio\n\n- **Persone:** Chiara Bianchi\n- **Cosa è successo:**\n"
                                       + "  - Chicca ha chiesto di rimandare\n  - Io ho insistito\n- **Come ho reagito:** Mi sono innervosito\n"));
        Assert.That(Episodes.Read(path).Select(e => e.Date), Is.EqualTo(new[] { new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 10) }));

        Episodes.Update(path, first.Key, e => e with { AboutMe = "Sotto pressione insiste invece di chiedere il perché.", Changes = ["Chiara Bianchi · rapporto: buono → delicato"] });
        var updated = Episodes.Read(path).Single(e => e.Key == first.Key);
        Assert.That(updated.Facts, Is.EqualTo("- Chicca ha chiesto di rimandare\n- Io ho insistito"));
        Assert.That(updated.AboutMe, Does.StartWith("Sotto pressione"));
        Assert.That(updated.Changes, Is.EqualTo(new[] { "Chiara Bianchi · rapporto: buono → delicato" }));
        Assert.That(Episodes.Read(path).Single(e => e.Key == second.Key).Facts, Is.EqualTo("Tutto ok"), "the others untouched");

        var chiara = People.Parse("## Chiara Bianchi\n\n- **Soprannome:** Chicca\n").Single();
        Assert.That(Episodes.Involving(Episodes.Read(path), chiara).Select(e => e.Key), Is.EqualTo(new[] { first.Key }));
        Assert.That(Episodes.TitleFrom("con Chicca riunione tesa sul rilascio, ho alzato la voce e poi mi sono scusato"),
            Is.EqualTo("con Chicca riunione tesa sul rilascio, ho alzato…"));
    }

    [Test]
    public void The_screenshots_of_an_episode_are_found_and_given_to_the_analysis()
    {
        var folder = Path.Combine(_root, "Diario");
        Directory.CreateDirectory(Path.Combine(folder, "Allegati"));
        File.WriteAllBytes(Path.Combine(folder, "Allegati", "Pasted image 20261116143005.png"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "mail.jpg"), [1]);
        var episode = new Episode(new DateOnly(2026, 11, 16), "Mail tesa", ["Chiara Bianchi"],
            "- Mi ha scritto questa mail\n- ![[Pasted image 20261116143005.png]]", "![](mail.jpg)", "![[sparita.png]] e ![[documento.pdf]]", "", []);

        var images = Episodes.Images(episode, folder);

        Assert.That(images, Is.EqualTo(new[]
        {
            Path.GetFullPath(Path.Combine(folder, "Allegati", "Pasted image 20261116143005.png")),
            Path.GetFullPath(Path.Combine(folder, "mail.jpg")),
        }), "missing files and non-images left out");

        // Saved and read back, the image line stays in its field.
        var path = Path.Combine(folder, "Episodi.md");
        Episodes.Add(path, episode);
        Assert.That(Episodes.Images(Episodes.Read(path).Single(), folder), Has.Count.EqualTo(2));

        var prompt = EpisodeAnalysis.Prompt(episode, [], null, images);
        Assert.That(prompt, Does.Contain("Screenshot dell'episodio").And.Contain("Pasted image 20261116143005.png").And.Contain("Read"));
        Assert.That(EpisodeAnalysis.Prompt(episode, [], null), Does.Not.Contain("Screenshot dell'episodio"));
    }

    [Test]
    public void What_to_remember_from_an_episode_comes_back_with_the_person()
    {
        var path = Path.Combine(_root, "Diario", "Episodi.md");
        Episodes.Add(path, new Episode(new DateOnly(2026, 11, 3), "Prima", ["Chiara Bianchi"], "Vecchio", "", "", "", [], Lesson: "chiedere il perché"));
        Episodes.Add(path, new Episode(new DateOnly(2026, 11, 10), "Senza lezione", ["Chiara Bianchi"], "Niente", "", "", "", []));
        Episodes.Add(path, new Episode(new DateOnly(2026, 11, 12), "Rilascio", ["Chiara Bianchi"], "Tesa", "", "", "", [], Lesson: "mandarle i dati prima"));
        var episodes = Episodes.Read(path);
        Assert.That(File.ReadAllText(path), Does.Contain("- **Da ricordare:** mandarle i dati prima"));

        var chiara = People.Parse("## Chiara Bianchi\n\n- **Soprannome:** Chicca\n").Single();
        Assert.That(Episodes.LessonsFor(episodes, chiara).Select(e => e.Lesson), Is.EqualTo(new[] { "mandarle i dati prima", "chiedere il perché" }));
        Assert.That(Episodes.LessonLine(episodes, chiara), Is.EqualTo("💡 l'ultima volta con Chicca (12 nov): mandarle i dati prima"));
        Assert.That(Episodes.LessonLine(episodes, People.Parse("## Marco Verdi\n").Single()), Is.Empty);

        var findings = EpisodeAnalysis.Parse("""{"modifiche": [], "su_di_me": "", "da_ricordare": "avvisarla prima"}""", []);
        Assert.That(findings!.Lesson, Is.EqualTo("avvisarla prima"));
    }

    [Test]
    public void Preparing_a_conversation_finds_the_person_and_gives_claude_what_is_known()
    {
        var people = People.Parse("## Chiara Bianchi\n\n- **Soprannome:** Chicca\n- **Rapporto:** delicato\n- **Come comunicare:** per iscritto\n\n## Marco Verdi\n");

        Assert.That(ConversationPrep.Split(people, "Chicca rilascio di venerdì"), Is.EqualTo((people[0], "rilascio di venerdì")));
        Assert.That(ConversationPrep.Split(people, "Chiara Bianchi permessi")?.Topic, Is.EqualTo("permessi"), "full name");
        Assert.That(ConversationPrep.Split(people, "Marco")?.Topic, Is.Empty);
        Assert.That(ConversationPrep.Split(people, "Gino qualcosa"), Is.Null);

        var episode = new Episode(new DateOnly(2026, 11, 12), "Rilascio", ["Chiara Bianchi"], "Tesa", "", "", "", [], Lesson: "mandarle i dati prima");
        var prompt = ConversationPrep.Prompt(people[0], "rilascio", [episode], [], "## Come comunico\n- sotto pressione insisto", new DateOnly(2026, 11, 16));
        Assert.That(prompt, Does.Contain("Argomento: rilascio").And.Contain("- **Come comunicare:** per iscritto").And.Contain("DA RICORDARE: mandarle i dati prima")
            .And.Contain("sotto pressione insisto").And.Contain("Primo messaggio").And.Contain("non deve mai citare"));
    }

    [Test]
    public void A_missing_person_is_added_once_with_where_they_came_from()
    {
        Directory.CreateDirectory(Vault);
        var path = Path.Combine(Vault, "Persone.md");
        var today = new DateOnly(2026, 11, 16);

        var gino = People.EnsureExists(path, "Gino Neri", "dal task NUSR-1", today, "sistemista", "gestisce i server di test");
        Assert.That(gino?.Name, Is.EqualTo("Gino Neri"), "the note is created when missing");
        Assert.That(File.ReadAllText(path), Does.StartWith("# Persone").And.Contain(
            "## Gino Neri\n\n- **Compito:** sistemista\n- **Descrizione:** gestisce i server di test\n- **Note:** aggiunta in automatico dal task NUSR-1 (2026-11-16): da completare\n"));

        File.AppendAllText(path, "\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n");
        Assert.That(People.EnsureExists(path, "Gino Neri", "x", today), Is.Null, "already there");
        Assert.That(People.EnsureExists(path, "Chicca", "x", today), Is.Null, "found by nickname");
        Assert.That(People.EnsureExists(path, "Gino", "x", today), Is.Null, "found by first name");
        Assert.That(People.EnsureExists(path, "?", "x", today), Is.Null, "not a name");
        Assert.That(People.Parse(File.ReadAllText(path)), Has.Count.EqualTo(2));

        var findings = EpisodeAnalysis.Parse("""
            {"modifiche": [], "su_di_me": "", "nuove_persone": [
              {"nome": "Laura Rossi", "compito": "commerciale", "descrizione": "segue il cliente CPL"},
              {"nome": "Chicca", "compito": "", "descrizione": ""},
              {"nome": "Laura Rossi", "compito": "", "descrizione": ""}
            ]}
            """, People.Parse(File.ReadAllText(path)));
        Assert.That(findings!.NewPeople, Is.EqualTo(new[] { new NewPerson("Laura Rossi", "commerciale", "segue il cliente CPL") }),
            "people with a card and repeats left out");
    }

    [Test]
    public void Card_changes_set_add_and_remove_and_can_be_undone()
    {
        Directory.CreateDirectory(Vault);
        var path = Path.Combine(Vault, "Persone.md");
        File.WriteAllText(path, "# Persone\n\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n- **Rapporto:** buono\n- **Note:** sentirla al mattino\n");

        Assert.That(People.ApplyChange(path, "Chicca", "rapporto", "imposta", "critico")?.Before, Is.EqualTo("buono"));
        Assert.That(People.ApplyChange(path, "Chicca", "rapporto", "imposta", "pessimo"), Is.Null, "not on the scale");
        Assert.That(People.ApplyChange(path, "Chicca", "note", "aggiungi", "avvisarla prima dei rilasci")?.Before, Is.EqualTo("sentirla al mattino"));
        Assert.That(People.ApplyChange(path, "Chicca", "note", "aggiungi", "avvisarla prima dei rilasci"), Is.Null, "already there");
        Assert.That(People.ApplyChange(path, "Chicca", "come comunicare", "aggiungi", "per iscritto")?.Before, Is.EqualTo(""));
        Assert.That(People.ApplyChange(path, "Chicca", "compito", "imposta", "x"), Is.Null, "only the fields an episode may change");

        var chiara = People.Parse(File.ReadAllText(path)).Single();
        Assert.That(chiara.Relationship, Is.EqualTo("critico"));
        Assert.That(chiara.Notes, Is.EqualTo("- sentirla al mattino\n- avvisarla prima dei rilasci"));
        Assert.That(chiara.Communication, Is.EqualTo("per iscritto"), "a first item stays a plain line");

        // The undo buttons: the value before, or the item taken away; a one-item list goes back to a line.
        var undoRelationship = EpisodeAnalysis.UndoCommand(new CardChange("Chiara Bianchi", "rapporto", "imposta", "critico", true, ""), "buono");
        var undoNote = EpisodeAnalysis.UndoCommand(new CardChange("Chiara Bianchi", "note", "aggiungi", "avvisarla prima dei rilasci", true, ""), "sentirla al mattino");
        foreach (var command in new[] { undoRelationship, undoNote })
        {
            var (person, field, mode, value) = EpisodeAnalysis.ParseCommand(command["/scheda ".Length..])!.Value;
            Assert.That(People.ApplyChange(path, person, field, mode, value), Is.Not.Null, command);
        }

        chiara = People.Parse(File.ReadAllText(path)).Single();
        Assert.That(chiara.Relationship, Is.EqualTo("buono"));
        Assert.That(chiara.Notes, Is.EqualTo("sentirla al mattino"));

        // A list restored by an undo keeps its lines through the command.
        var command2 = EpisodeAnalysis.Command("Chiara Bianchi", "note", "imposta", "- a\n- b");
        Assert.That(EpisodeAnalysis.ParseCommand(command2["/scheda ".Length..])!.Value.Value, Is.EqualTo("- a\n- b"));
    }

    [Test]
    public void The_analysis_answer_is_checked_before_touching_any_card()
    {
        var people = People.Parse("## Chiara Bianchi\n\n- **Soprannome:** Chicca\n\n## Marco Verdi\n");
        var findings = EpisodeAnalysis.Parse("""
            Ecco l'analisi:
            {"modifiche": [
              {"persona": "Chicca", "campo": "rapporto", "modo": "imposta", "valore": "delicato", "importante": true, "motivo": "conflitto aperto"},
              {"persona": "Marco Verdi", "campo": "note", "modo": "aggiungi", "valore": "conosce bene il modulo fatture", "importante": false, "motivo": "ha risolto il blocco"},
              {"persona": "Gino", "campo": "note", "valore": "chi è?", "importante": true},
              {"persona": "Marco Verdi", "campo": "compito", "valore": "capo", "importante": true},
              {"persona": "Marco Verdi", "campo": "rapporto", "valore": "pessimo", "importante": true}
            ], "su_di_me": "Hai chiesto i dati prima di rispondere: ha funzionato."}
            """, people);

        Assert.That(findings, Is.Not.Null);
        Assert.That(findings!.Changes.Select(c => (c.Person, c.Field, c.Important)), Is.EqualTo(new[]
        {
            ("Chiara Bianchi", "rapporto", true), ("Marco Verdi", "note", false),
        }), "unknown people, fields outside the three and values outside the scale are dropped; names resolved by nickname");
        Assert.That(findings.AboutMe, Does.StartWith("Hai chiesto i dati"));
        Assert.That(EpisodeAnalysis.Describe(findings.Changes[0], "buono"), Is.EqualTo("Chiara Bianchi · rapporto: buono → delicato (conflitto aperto)"));
        Assert.That(EpisodeAnalysis.Parse("non è JSON", people), Is.Null);

        var prompt = EpisodeAnalysis.Prompt(new Episode(new DateOnly(2026, 11, 16), "Riunione", ["Chiara Bianchi"], "- fatto uno\n- fatto due", "", "", "", []),
            people.Take(1).ToList(), null);
        Assert.That(prompt, Does.Contain("Cosa è successo: fatto uno; fatto due").And.Contain("## Chiara Bianchi").And.Contain("macroscopici"));
    }

    [Test]
    public void The_mentor_line_flags_someone_waited_on_and_not_heard_for_days()
    {
        Directory.CreateDirectory(Vault);
        File.WriteAllText(Path.Combine(Vault, "Persone.md"), "# Persone\n\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n- **Sentita:** 2026-11-10\n\n"
                                                            + "## Marco Verdi\n\n- **Sentita:** 2026-11-16\n");
        File.WriteAllText(Path.Combine(Vault, "To-Do.md"), "# To-Do\n\n## Task\n\n"
            + "- [ ] T-1 · 2026-11-12 · ⏸ Migrazione [chi:: Chicca]\n- [ ] T-2 · 2026-11-12 · ⏸ Rilascio [chi:: Marco Verdi]\n");
        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = Vault }));
        var cards = People.OnCards(ProjectBoard.Load(vault, Pattern), People.Parse(File.ReadAllText(Path.Combine(Vault, "Persone.md"))));
        var today = new DateOnly(2026, 11, 16);

        Assert.That(cards.Select(c => c.Item.Id), Is.EqualTo(new[] { "T-1", "T-2" }), "the one not heard for longest first");
        Assert.That(People.Line(cards[0], today), Does.Contain("(⚠ sentita 6 giorni fa)"));
        Assert.That(People.Line(cards[1], today), Does.Contain("(sentita oggi)").And.Not.Contain("⚠"));
    }

    [Test]
    public void The_sentita_command_marks_a_person_or_lists_who_was_not_heard()
    {
        Directory.CreateDirectory(Vault);
        File.WriteAllText(Path.Combine(Vault, "Persone.md"), "# Persone\n\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n");
        var settings = Options.Create(new BuddySettings { ProjectPath = Vault });
        using var vault = new VaultIndex(settings);
        var commands = new Chat.AssistantCommands(vault, new GitRepo(settings, TimeProvider.System), settings);
        var today = new DateOnly(2026, 11, 16);

        Assert.That(commands.MarkContacted("", today), Does.Contain("Chiara Bianchi (Chicca)** — mai sentita"));
        Assert.That(commands.MarkContacted("Chicca", today), Does.Contain("hai sentito **Chiara Bianchi (Chicca)** oggi"));
        Assert.That(File.ReadAllText(Path.Combine(Vault, "Persone.md")), Does.Contain("- **Sentita:** 2026-11-16"));
        Assert.That(commands.MarkContacted("Gino", today), Does.Contain("non trovo «Gino»"));
    }

    [Test]
    public void Images_in_Allegati_that_no_note_uses_are_reported()
    {
        Directory.CreateDirectory(Path.Combine(Vault, "Allegati"));
        File.WriteAllBytes(Path.Combine(Vault, "Allegati", "usata.png"), [1]);
        File.WriteAllBytes(Path.Combine(Vault, "Allegati", "Pasted image 1.png"), [1]);
        File.WriteAllBytes(Path.Combine(Vault, "Allegati", "dimenticata.png"), [1]);
        File.WriteAllText(Path.Combine(Vault, "Allegati", "leggimi.txt"), "non un'immagine");
        File.WriteAllText(Path.Combine(Vault, "Nota.md"), "Ecco ![[usata.png]] e ![](Allegati/Pasted%20image%201.png)\n");
        using var vault = new VaultIndex(Options.Create(new BuddySettings { ProjectPath = Vault }));

        Assert.That(VaultHealth.UnusedImages(Vault, vault.Notes), Is.EqualTo(new[] { "dimenticata.png" }));
        var issues = VaultHealth.Check(ProjectBoard.Load(vault, Pattern), NoteGraph.Build(vault.Notes), [], new DateOnly(2026, 11, 16),
            int.MaxValue, projectPath: Vault);
        Assert.That(issues.Select(i => i.Text), Has.Some.Contains("Allegati/dimenticata.png"));
    }

    [Test]
    public void The_buddy_gets_the_people_when_asked_who_to_talk_to()
    {
        Directory.CreateDirectory(Vault);
        File.WriteAllText(Path.Combine(Vault, "Persone.md"),
            "# Persone\n\n## Chiara Bianchi\n\n- **Soprannome:** Chicca\n- **Compito:** DBA\n- **Descrizione:** permessi del database\n- **Note:** meglio la mattina\n");
        var settings = Options.Create(new BuddySettings { ProjectPath = Vault });
        using var vault = new VaultIndex(settings);
        var builder = new Context.ContextBuilder(vault, settings, TimeProvider.System);

        var asked = builder.Build("con chi devo parlare per i permessi?");
        Assert.That(asked.Text, Does.Contain("**Chiara Bianchi** (detto/a Chicca) — DBA").And.Contain("meglio la mattina"));
        Assert.That(builder.Build("la Chicca è in ferie?").Text, Does.Contain("**Chiara Bianchi**"), "asked by nickname");
        Assert.That(asked.Sources.Select(s => s.Label), Does.Contain("Persone.md"));

        Assert.That(builder.Build("a che punto è T-7?").Text, Does.Not.Contain("Chiara"));
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
