using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>A call's note in Trascrizioni/, as the page shows it.</summary>
public sealed record CallTranscript(
    string RelativePath, string Title, DateOnly Date, IReadOnlyList<string> People, string? Task, bool Done,
    string Summary, int Actions, int OpenActions, string Content)
{
    /// <summary>Saved, but not yet cleaned and summarised by Claude (or the last try failed).</summary>
    public bool Pending => !Done;

    /// <summary>What is left to do: the actions not ticked yet, then the open points (each a button to work on it).</summary>
    public IReadOnlyList<CallTask> Tasks { get; init; } = [];
}

/// <summary>An action not done yet ("- [ ] **Chi:** cosa") or an open point of a call, as plain text.</summary>
public sealed record CallTask(bool IsAction, string Text);

/// <summary>What Claude made of a transcript: the note's sections, plus the title and people it recognised.</summary>
public sealed record CallSummary(
    string Title, IReadOnlyList<string> People, string? Task, string Summary, string Decisions, string Actions, string Open,
    string Transcript)
{
    /// <summary>What is worth remembering ("- **Chi:** informazione"): who decides what, where things are, numbers, limits.</summary>
    public string Remember { get; init; } = "";
}

/// <summary>
/// The transcripts of the user's calls: one note per call in the project's "Trascrizioni/" folder, named
/// "2026-11-24 Titolo.md". A new note keeps the text as pasted (in "## Trascrizione originale", never touched again) and
/// has "stato: da sistemare"; Claude then cleans it and writes Riassunto, Decisioni, Azioni (a checklist), Punti aperti and
/// the cleaned Trascrizione above it, with "stato: sistemata". Plain markdown, so Obsidian reads and edits it too.
/// </summary>
public static partial class CallTranscripts
{
    public const string Folder = "Trascrizioni";
    public const string OriginalHeading = "## Trascrizione originale";
    private const string PendingState = "da sistemare";
    private const string DoneState = "sistemata";

    /// <summary>The calls, newest first (by date, then by file name).</summary>
    public static IReadOnlyList<CallTranscript> List(VaultIndex vault)
    {
        var prefix = Folder + "/";
        return vault.Notes
            .Where(n => n.RelativePath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(Read)
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static CallTranscript Read(VaultNote note)
    {
        var text = note.Content.ReplaceLineEndings("\n");
        var front = FrontMatter().Match(text) is { Success: true } f ? f.Groups["body"].Value : "";
        string Field(string name) =>
            Regex.Match(front, $@"^{name}:[ \t]*(?<v>[^\n]*)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups["v"].Value.Trim() : "";

        var date = DateOnly.TryParseExact(Field("created"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : Regex.Match(note.Title, @"^\d{4}-\d{2}-\d{2}") is { Success: true } named
              && DateOnly.TryParseExact(named.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromName)
                ? fromName : DateOnly.MinValue;
        var title = Regex.Match(text, @"^#[ \t]+(?<t>[^\n]+)$", RegexOptions.Multiline) is { Success: true } h
            ? h.Groups["t"].Value.Trim()
            : Regex.Replace(note.Title, @"^\d{4}-\d{2}-\d{2}\s*", "");
        var sections = Sections(text);
        var actions = sections.TryGetValue("azioni", out var a) ? a : "";
        var open = Regex.Matches(actions, @"^\s*[-*] \[ \]", RegexOptions.Multiline).Count;
        var done = Regex.Matches(actions, @"^\s*[-*] \[[xX]\]", RegexOptions.Multiline).Count;
        var task = Field("task");
        var tasks = Items(actions, @"^\s*[-*] \[ \]\s*(?<t>.+)$").Select(t => new CallTask(true, t))
            .Concat(Items(sections.GetValueOrDefault("punti aperti", ""), @"^\s*[-*]\s+(?<t>.+)$").Select(t => new CallTask(false, t)))
            .ToList();
        return new CallTranscript(note.RelativePath, title, date, SplitPeople(Field("partecipanti")), task.Length > 0 ? task : null,
            !Field("stato").Equals(PendingState, StringComparison.OrdinalIgnoreCase),
            sections.TryGetValue("riassunto", out var s) ? s : "", open + done, open, note.Content) { Tasks = tasks };
    }

    /// <summary>The list items of a section matching <paramref name="pattern"/> (group "t"), as plain text; "Nessuno" left out.</summary>
    private static IEnumerable<string> Items(string section, string pattern) =>
        Regex.Matches(section, pattern, RegexOptions.Multiline)
            .Select(m => Regex.Replace(m.Groups["t"].Value, @"\*\*|__|`", "").Trim())
            .Where(t => t.Length > 0 && !IsNone(t));

    /// <summary>
    /// The request to Claude behind a call's button: work on one action or open point until there is a solution to act
    /// on — the call's note for the context, the code and the vault for the facts, a draft when someone must be asked.
    /// An analysis: nothing is changed, committed or run against data.
    /// </summary>
    public static string SolveRequest(CallTranscript call, CallTask item, string notePath)
    {
        var what = item.IsAction ? "un'azione da fare" : "un punto rimasto aperto";
        return $"""
            Dalla call «{call.Title}» del {call.Date:d MMMM yyyy} c'è {what}: «{item.Text}».
            La nota della call, con riassunto, decisioni e trascrizione, è: {notePath}{(call.Task is { } id ? $" (task {id})" : "")}

            Lavoraci per arrivare a una soluzione concreta:
            1. leggi la nota della call per il contesto (chi ha detto cosa, cosa è stato deciso);
            2. cerca quello che serve nel codice del repository e nelle note del vault: configurazioni, query, script, documentazione,
               e nella nota Persone chi se ne occupa;
            3. dammi la soluzione: cosa hai trovato (con file e righe), cosa fare passo per passo e, se serve chiedere o confermare
               qualcosa a qualcuno, una bozza breve del messaggio da mandargli.
            È un'analisi: non modificare file, non fare commit e non eseguire comandi che cambiano dati, database o ambienti.
            Se manca un'informazione che solo una persona può dare, dillo chiaramente e prepara la domanda.
            """;
    }

    /// <summary>
    /// Saves a new call with the text as given (see <see cref="Normalize"/>): "Trascrizioni/2026-11-24 Titolo.md", never
    /// overwriting another. Returns its path relative to the project.
    /// </summary>
    public static string Create(string projectPath, string title, DateOnly date, IEnumerable<string> people, string? task, string transcript)
    {
        title = OneLine(title);
        if (title.Length == 0)
        {
            title = "Call";
        }

        var file = FreeFile(Path.Combine(projectPath, Folder), date, title, null);
        var text = Format(date, people, task, PendingState, title, null, Normalize(transcript));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text.Replace("\n", Environment.NewLine));
        return Path.GetRelativePath(projectPath, file);
    }

    /// <summary>The text as pasted, kept in the note: what Claude works on.</summary>
    public static string Original(string content)
    {
        var text = content.ReplaceLineEndings("\n");
        var at = Regex.Match(text, $@"^{Regex.Escape(OriginalHeading)}[ \t]*$", RegexOptions.Multiline);
        return at.Success ? text[(at.Index + at.Length)..].Trim('\n') : CustomPagesBody(text);
    }

    /// <summary>
    /// Writes what Claude made of the call into its note: the sections above the original text, the title and people it
    /// found (the ones the user wrote win), "stato: sistemata". A note whose title was the default "Call" is renamed after
    /// Claude's title. Refused (IOException) when the note changed since <paramref name="expected"/> was read: someone
    /// edited it meanwhile. Returns the note's path relative to the project (it may have changed).
    /// </summary>
    public static string Apply(string projectPath, string relativePath, string expected, CallSummary summary)
    {
        var file = Path.Combine(projectPath, relativePath);
        if (!File.Exists(file))
        {
            throw new IOException($"La trascrizione «{Path.GetFileNameWithoutExtension(relativePath)}» non c'è più.");
        }

        var current = File.ReadAllText(file);
        if (current.ReplaceLineEndings("\n") != expected.ReplaceLineEndings("\n"))
        {
            throw new IOException($"La nota «{Path.GetFileNameWithoutExtension(relativePath)}» è cambiata mentre Claude la sistemava: "
                                  + "non la sovrascrivo. Riprova con «🧹 Sistema in sospeso».");
        }

        var call = Read(new VaultNote(relativePath, current));
        var title = call.Title is "" or "Call" && summary.Title.Length > 0 ? OneLine(summary.Title) : call.Title;
        var people = call.People.Concat(summary.People)
            .DistinctBy(p => p.ToLowerInvariant()).ToList();
        var task = call.Task ?? summary.Task;
        var date = call.Date == DateOnly.MinValue ? DateOnly.FromDateTime(DateTime.Today) : call.Date;
        var text = Format(date, people, task, DoneState, title, summary, Original(current));

        var target = file;
        if (title != call.Title)
        {
            target = FreeFile(Path.GetDirectoryName(file)!, date, title, file);
        }

        File.WriteAllText(target, text.Replace("\n", Environment.NewLine));
        if (!string.Equals(target, file, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
        }

        return Path.GetRelativePath(projectPath, target);
    }

    private static string Format(DateOnly date, IEnumerable<string> people, string? task, string state, string title, CallSummary? summary, string original)
    {
        var text = new StringBuilder("---\n")
            .Append(CultureInfo.InvariantCulture, $"created: {date:yyyy-MM-dd}\n")
            .Append("tipo: trascrizione\n")
            .Append(CultureInfo.InvariantCulture, $"stato: {state}\n");
        if (string.Join(", ", people.Select(OneLine).Where(p => p.Length > 0)) is { Length: > 0 } names)
        {
            text.Append(CultureInfo.InvariantCulture, $"partecipanti: {names}\n");
        }

        if (!string.IsNullOrWhiteSpace(task))
        {
            text.Append(CultureInfo.InvariantCulture, $"task: {OneLine(task)}\n");
        }

        text.Append("---\n\n").Append(CultureInfo.InvariantCulture, $"# {title}\n\n");
        if (summary is not null)
        {
            void Section(string heading, string body, string empty)
            {
                text.Append(CultureInfo.InvariantCulture, $"## {heading}\n\n{(body.Trim().Length > 0 ? body.Trim() : empty)}\n\n");
            }

            Section("Riassunto", summary.Summary, "_Nessun riassunto._");
            Section("Decisioni", summary.Decisions, "_Nessuna decisione presa._");
            Section("Azioni", summary.Actions, "_Nessuna azione._");
            Section("Punti aperti", summary.Open, "_Nessuno._");
            Section("Da ricordare", summary.Remember, "_Niente di particolare._");
            Section("Trascrizione", Paragraphs(summary.Transcript), "_Non sistemata._");
        }
        else
        {
            text.Append("_Da sistemare: Claude ne farà riassunto, decisioni e azioni._\n\n");
        }

        return text.Append(OriginalHeading).Append("\n\n").Append(Paragraphs(original)).Append('\n').ToString();
    }

    /// <summary>
    /// One paragraph per line: in markdown a single line break joins the lines, so the turns of a transcript
    /// ("**Chicca:** …", "Marco: …") ran together in one block. Blank lines in between keep each on its own.
    /// </summary>
    public static string Paragraphs(string text) =>
        string.Join("\n\n", text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    // ---- Claude -------------------------------------------------------------------------------------------------

    /// <summary>The request to Claude: clean the transcript and summarise it, in a fixed format (see <see cref="Parse"/>).</summary>
    /// <param name="me">The user's name (who made the call and saved it), so "io" among the participants is a person.</param>
    public static string Prompt(CallTranscript call, string original, IReadOnlyList<Person> people, IReadOnlyList<GlossaryTerm> glossary,
        string? me = null)
    {
        me = string.IsNullOrWhiteSpace(me) ? null : OneLine(me);
        var prompt = new StringBuilder()
            .AppendLine("Sei l'assistente di uno sviluppatore. Qui sotto c'è la trascrizione automatica di una sua call di lavoro (in italiano, "
                        + "a volte con parole inglesi): ha errori di riconoscimento, frasi spezzate, intercalari e ripetizioni.")
            .AppendLine("Non usare strumenti e non scrivere file: rispondi solo con il testo nel formato richiesto.")
            .AppendLine()
            .AppendLine("Fai due cose:")
            .AppendLine("1. Sistemala: correggi le parole riconosciute male (usa i nomi delle persone e i termini del progetto qui sotto), "
                        + "togli intercalari, false partenze e ripetizioni, unisci le battute spezzate della stessa persona, metti la punteggiatura. "
                        + "Non riassumere e non inventare: deve restare quello che è stato detto, solo leggibile. "
                        + "Ogni battuta su una riga sua: «**Nome:** testo».")
            .AppendLine("   Chi parla: la trascrizione di solito non lo dice, ricavalo dal contesto. Indizi: chi viene chiamato per nome "
                        + "(«Chiara, …» lo dice qualcuno che non è Chiara; «chiedi a Mimmo» non lo dice Mimmo), chi fa la domanda e chi risponde, "
                        + "chi racconta cosa ha fatto (confrontalo con ruoli e task), e con due soli partecipanti le battute che si alternano. "
                        + "Metti il nome quando è ragionevolmente chiaro; «**?:**» solo quando resta davvero il dubbio, mai un nome tirato a indovinare. "
                        + "Usa i nomi come nell'elenco delle persone (il nome di battesimo basta).")
            .AppendLine("2. Riassumila per chi non c'era e per ritrovarla tra mesi.")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Data della call: {call.Date:yyyy-MM-dd}.")
            .AppendLine(call.Title is "" or "Call" ? "Titolo: da proporre tu." : $"Titolo dato dall'utente: {call.Title}.")
            .AppendLine(me is null ? "" : $"Chi ha fatto la call e te la manda è {me} (anche «io» tra i partecipanti): è sempre uno di quelli che parlano.")
            .AppendLine(call.People.Count > 0 ? $"Partecipanti indicati: {string.Join(", ", call.People)}." : "Partecipanti: da ricavare dal testo.")
            .AppendLine(call.Task is { } task ? $"Task collegato: {task}." : "");
        if (people.Count > 0)
        {
            prompt.AppendLine("Persone del progetto (nome, soprannomi, ruolo), per riconoscere i nomi storpiati:");
            foreach (var p in people.Take(60))
            {
                prompt.AppendLine(CultureInfo.InvariantCulture,
                    $"- {p.Name}{(p.Nicknames.Count > 0 ? $" ({string.Join(", ", p.Nicknames)})" : "")}{(p.Role.Length > 0 ? $" — {OneLine(p.Role)}" : "")}");
            }
        }

        if (glossary.Count > 0)
        {
            prompt.AppendLine("Termini del progetto (scritti così): " + string.Join(", ", glossary.Take(150).Select(t => t.Term)) + ".");
        }

        prompt.AppendLine()
            .AppendLine("Rispondi ESATTAMENTE in questo formato, senza altro prima o dopo e senza titoli «#» dentro le sezioni:")
            .AppendLine()
            .AppendLine("TITOLO: <titolo breve e specifico della call, max 8 parole>")
            .AppendLine("PARTECIPANTI: <nomi separati da virgola, come nell'elenco delle persone quando ci sono>")
            .AppendLine("TASK: <ID del task di cui si parla, se ce n'è uno chiaro, altrimenti ->")
            .AppendLine("## Riassunto")
            .AppendLine("<3-8 righe: di cosa si è parlato e cosa ne è uscito>")
            .AppendLine("## Decisioni")
            .AppendLine("<elenco «- …» delle decisioni prese, o «- Nessuna»>")
            .AppendLine("## Azioni")
            .AppendLine("<elenco «- [ ] **Chi:** cosa (entro AAAA-MM-GG, solo se una scadenza è stata detta: calcolala dalla data della call)», o «- Nessuna»>")
            .AppendLine("## Punti aperti")
            .AppendLine("<elenco «- …» di domande e dubbi rimasti aperti, o «- Nessuno»>")
            .AppendLine("## Da ricordare")
            .AppendLine("<elenco «- **Chi:** cosa» delle informazioni utili dette, da ritrovare tra mesi: chi decide o si occupa di cosa, dove sta una cosa, vincoli, numeri, nomi di ambienti; o «- Nessuno»>")
            .AppendLine("## Trascrizione sistemata")
            .AppendLine("<la trascrizione sistemata, tutta>")
            .AppendLine()
            .AppendLine("Trascrizione:")
            .AppendLine("<<<")
            .AppendLine(original.Trim())
            .AppendLine(">>>");
        return prompt.ToString();
    }

    /// <summary>Claude's answer in the format of <see cref="Prompt"/>; null when it has no summary (not that format).</summary>
    public static CallSummary? Parse(string answer)
    {
        var text = answer.ReplaceLineEndings("\n").Trim();
        text = Regex.Replace(text, @"\A```[a-z]*\n([\s\S]*?)\n```\z", "$1"); // a reply wrapped in a code block
        string Line(string name) =>
            Regex.Match(text, $@"^\**{name}\**:[ \t]*(?<v>[^\n]*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase) is { Success: true } m
                ? m.Groups["v"].Value.Trim().Trim('*').Trim() : "";

        var sections = Sections(text);
        if (!sections.TryGetValue("riassunto", out var summary) || summary.Length == 0)
        {
            return null;
        }

        string Get(string name) => sections.TryGetValue(name, out var value) && !IsNone(value) ? value : "";
        var task = Line("task");
        return new CallSummary(Line("titolo"), SplitPeople(Line("partecipanti")), task is "" or "-" or "—" ? null : task,
            summary, Get("decisioni"), Get("azioni"), Get("punti aperti"),
            sections.TryGetValue("trascrizione sistemata", out var t) ? t : sections.GetValueOrDefault("trascrizione", "")) { Remember = Get("da ricordare") };
    }

    private static bool IsNone(string value) =>
        Regex.IsMatch(value.Trim(), @"^[-*]?\s*_?(nessun[ao]?|niente)\.?_?$", RegexOptions.IgnoreCase);

    /// <summary>"## Heading" sections, by lower-case heading; the original text is left out.</summary>
    private static Dictionary<string, string> Sections(string text)
    {
        var cut = Regex.Match(text, $@"^{Regex.Escape(OriginalHeading)}[ \t]*$", RegexOptions.Multiline);
        if (cut.Success)
        {
            text = text[..cut.Index];
        }

        var headings = Regex.Matches(text, @"^##[ \t]+(?<h>[^\n]+?)[ \t]*$", RegexOptions.Multiline);
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Index + headings[i].Length;
            var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            sections.TryAdd(headings[i].Groups["h"].Value.Trim().ToLowerInvariant(), text[start..end].Trim());
        }

        return sections;
    }

    // ---- Text as exported --------------------------------------------------------------------------------------

    /// <summary>
    /// A transcript ready to keep: a WebVTT export (Teams, Meet, Zoom) loses its timings and cue numbers and becomes
    /// "Nome: testo" lines, consecutive lines of the same speaker joined; any other text is only trimmed.
    /// </summary>
    public static string Normalize(string transcript)
    {
        var text = transcript.ReplaceLineEndings("\n").Trim();
        if (!text.StartsWith("WEBVTT", StringComparison.Ordinal) && !Regex.IsMatch(text, @"^\d{1,2}:\d{2}(:\d{2})?[.,]\d{3}\s+-->", RegexOptions.Multiline))
        {
            return text;
        }

        var turns = new List<(string Speaker, StringBuilder Text)>();
        foreach (var cue in Regex.Split(text, @"\n\s*\n"))
        {
            var lines = cue.Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("WEBVTT", StringComparison.Ordinal) && !l.StartsWith("NOTE", StringComparison.Ordinal)
                            && !l.Contains("-->", StringComparison.Ordinal) && !Regex.IsMatch(l, @"^(\d+|[0-9a-f]{8}-[0-9a-f-]+(/\d+-\d+)?)$")) // cue numbers and ids
                .ToList();
            if (lines.Count == 0)
            {
                continue;
            }

            var joined = string.Join(' ', lines);
            var speaker = "";
            if (Regex.Match(joined, @"<v\s+(?<n>[^>]+)>") is { Success: true } voice)
            {
                speaker = voice.Groups["n"].Value.Trim();
            }
            else if (Regex.Match(joined, @"^(?<n>[^:<>]{2,40}):\s") is { Success: true } named)
            {
                speaker = named.Groups["n"].Value.Trim();
                joined = joined[named.Length..];
            }

            joined = Regex.Replace(joined, @"<[^>]+>", "").Trim();
            if (joined.Length == 0)
            {
                continue;
            }

            if (turns.Count > 0 && turns[^1].Speaker == speaker)
            {
                turns[^1].Text.Append(' ').Append(joined);
            }
            else
            {
                turns.Add((speaker, new StringBuilder(joined)));
            }
        }

        return string.Join("\n", turns.Select(t => t.Speaker.Length > 0 ? $"{t.Speaker}: {t.Text}" : t.Text.ToString()));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> SplitPeople(string value) =>
        value.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('"', '\'')).Where(p => p.Length > 0 && p != "-").ToList();

    private static string OneLine(string value) => Regex.Replace(value.ReplaceLineEndings(" "), @"\s+", " ").Trim();

    /// <summary>"<date> <title>.md" in <paramref name="folder"/>, numbered when taken (<paramref name="self"/> does not count).</summary>
    private static string FreeFile(string folder, DateOnly date, string title, string? self)
    {
        var name = string.Concat($"{date:yyyy-MM-dd} {title}".Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        var file = Path.Combine(folder, name + ".md");
        for (var i = 2; File.Exists(file) && !string.Equals(file, self, StringComparison.OrdinalIgnoreCase); i++)
        {
            file = Path.Combine(folder, $"{name} ({i}).md");
        }

        return file;
    }

    private static string CustomPagesBody(string text) =>
        Regex.Replace(FrontMatter().Replace(text, ""), @"\A\s*#\s+[^\n]*\n", "").Trim();

    [GeneratedRegex(@"\A---\n(?<body>[\s\S]*?)\n---[ \t]*\n")]
    private static partial Regex FrontMatter();
}
