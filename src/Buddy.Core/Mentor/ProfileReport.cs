using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Buddy.Core.Mentor;

/// <summary>A mark from 1 to 10 on one aspect, with the evidence; <paramref name="Score"/> null when there is not enough to say.</summary>
public sealed record AspectScore(string Aspect, int? Score, string Reason);

/// <summary>Something the user did that went well or not: when (a date, may be ""), what, and why it counts.</summary>
public sealed record Intervention(string When, string What, string Why);

/// <summary>
/// A snapshot of how the user is doing, written by Claude from the journal (profile, episodes, diary, retros): a short
/// portrait, marks on a few aspects (leadership, maturity…), strengths, things to improve, and the interventions that
/// went well and those that did not. Hypotheses from evidence, never diagnoses: the user's to read and correct.
/// </summary>
public sealed record ProfileReport(
    DateOnly Date, string Summary, IReadOnlyList<AspectScore> Scores, IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Improvements, IReadOnlyList<Intervention> Positive, IReadOnlyList<Intervention> Negative)
{
    /// <summary>Two or three small, concrete things to do next, from the things to improve.</summary>
    public IReadOnlyList<string> NextSteps { get; init; } = [];

    /// <summary>What changed since the previous report, in a sentence or two ("" for the first one).</summary>
    public string Since { get; init; } = "";

    /// <summary>The marks that have a value.</summary>
    public IEnumerable<AspectScore> Rated => Scores.Where(s => s.Score is not null);

    /// <summary>The average of the marks given, or null when none.</summary>
    public double? Average => Rated.Any() ? Rated.Average(s => s.Score!.Value) : null;
}

/// <summary>
/// The Resoconto note (in the journal): one "## AAAA-MM-GG" section per report, newest first, so the marks can be
/// compared with the previous ones. The buddy writes it from Claude's JSON answer; Claude only reads the journal.
/// </summary>
public static partial class ProfileReports
{
    public const string FileName = "Resoconto.md";

    public const string Header =
        "# Resoconto\n\nCome stai andando, letto dal mentore nel tuo diario: punti di forza, cose da migliorare, interventi "
        + "riusciti e no, e un voto su alcuni aspetti. Sono ipotesi dai fatti, non giudizi: correggile quando non ti ritrovi. "
        + "Il più recente è in cima.\n";

    /// <summary>The aspects marked, in the order they are shown.</summary>
    public static readonly IReadOnlyList<string> Aspects =
    [
        "Leadership", "Maturità", "Comunicazione", "Ascolto ed empatia", "Collaborazione", "Autonomia",
        "Gestione dello stress", "Affidabilità", "Voglia di imparare",
    ];

    public const int MaxItems = 5;

    public const int MaxInterventions = 6;

    public const int MaxSteps = 3;

    /// <summary>The note's path: in the journal when there is one, otherwise next to the project's notes.</summary>
    public static string PathFor(BuddySettings settings) =>
        Path.Combine(!string.IsNullOrWhiteSpace(settings.Mentor.JournalPath) ? settings.Mentor.JournalPath : settings.ProjectPath, FileName);

    // ---- Prompt and answer ---------------------------------------------------------------------------------------

    /// <summary>
    /// The request to Claude, run in the journal folder: read what is there about the user and answer with JSON only.
    /// <paramref name="previous"/> (the last report) keeps the marks consistent over time.
    /// </summary>
    public static string Prompt(string persona, DateOnly today, ProfileReport? previous)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine(persona.Trim()).AppendLine();
        prompt.AppendLine($"Oggi è {today:yyyy-MM-dd}. Ti chiede un resoconto su di sé: come sta andando come persona e come professionista.");
        prompt.AppendLine($$"""
            Leggi, nella cartella corrente, quello che c'è tra: Profilo.md, Episodi.md (episodi con i colleghi e le righe
            "Su di me"), i diari in Diario/ degli ultimi 45 giorni, le ultime retrospettive in Retro/, Andamento.md, Pillole.md.
            Non modificare né creare file. Poi rispondi SOLO con un oggetto JSON:
            {"sintesi": "...",
             "voti": [{"aspetto": "...", "voto": 1-10 | null, "motivo": "..."}],
             "punti_di_forza": ["..."], "da_migliorare": ["..."],
             "interventi_positivi": [{"quando": "AAAA-MM-GG", "cosa": "...", "perche": "..."}],
             "interventi_negativi": [{"quando": "AAAA-MM-GG", "cosa": "...", "perche": "..."}],
             "prossimi_passi": ["..."], "rispetto_a_prima": "..."}

            Regole:
            - "sintesi": 3-5 frasi, in seconda persona, una visione d'insieme della sua personalità al lavoro: cosa lo muove,
              come lavora, come sta con gli altri, dove sta crescendo. Calda ma onesta.
            - "voti": uno per ciascun aspetto, con questi nomi esatti: {{string.Join(", ", Aspects)}}.
              Voto da 1 a 10 (6 = nella media di uno sviluppatore della sua esperienza, 8 = solido, 10 = esemplare), dato
              SOLO dai fatti letti. "motivo": una frase che cita l'indizio (un episodio, un diario, una retro). Se non ci
              sono abbastanza indizi su un aspetto, "voto": null e nel motivo cosa servirebbe per valutarlo. Non gonfiare i
              voti per gentilezza e non abbassarli per severità: un voto credibile gli serve di più.
            - "punti_di_forza" e "da_migliorare": al massimo {{MaxItems}} ciascuno, una frase concreta per voce, legata a
              comportamenti osservati ("chiede il perché prima di insistere"), non a etichette ("è testardo"). In
              "da_migliorare" scrivi anche come, in breve.
            - "interventi_positivi" e "interventi_negativi": al massimo {{MaxInterventions}} ciascuno, i più significativi e
              recenti: cose che ha fatto o detto lui (in una riunione, con un collega, su un task). "quando": la data se la
              sai, altrimenti "". "cosa": il fatto in una frase. "perche": perché ha funzionato, o cosa avrebbe potuto fare
              diversamente (senza giudicarlo).
            - "prossimi_passi": da 1 a 3 azioni piccole e concrete per le prossime due settimane (al massimo un'ora l'una),
              legate a "da_migliorare" e al suo lavoro: cosa fare e come capire se è riuscita.
            - "rispetto_a_prima": se sotto c'è un resoconto precedente, 1-2 frasi su cosa è cambiato da allora e perché
              (cosa è migliorato, cosa no); altrimenti "".
            - Niente diagnosi, tipi di personalità, etichette psicologiche o giudizi morali; niente salute o vita privata
              oltre a quello che ha scritto lui. Non inventare fatti: se il diario è quasi vuoto, dillo nella sintesi e
              lascia le liste corte.
            """);
        if (previous is not null)
        {
            prompt.AppendLine().AppendLine($"## Resoconto precedente ({previous.Date:yyyy-MM-dd}), per confronto");
            prompt.AppendLine("Tieni i voti coerenti con questi: cambiali solo se i fatti nuovi lo giustificano, e dillo nel motivo.");
            foreach (var score in previous.Scores)
            {
                prompt.AppendLine($"- {score.Aspect}: {(score.Score is { } s ? s + "/10" : "n.d.")}");
            }
        }

        return prompt.ToString();
    }

    /// <summary>The report in Claude's answer, checked: unknown aspects dropped, marks kept in 1-10; null when there is no usable JSON.</summary>
    public static ProfileReport? Parse(string answer, DateOnly date)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(answer[start..(end + 1)]);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            static string Str(JsonElement item, string name) =>
                item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? Clean(v.GetString()!) : "";

            IEnumerable<JsonElement> Array(string name) =>
                root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];

            var given = new Dictionary<string, AspectScore>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var item in Array("voti"))
            {
                if (Aspect(Str(item, "aspetto")) is not { } aspect || given.ContainsKey(aspect))
                {
                    continue;
                }

                int? score = item.TryGetProperty("voto", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
                    ? Math.Clamp((int)Math.Round(d), 1, 10) : null;
                given[aspect] = new AspectScore(aspect, score, Str(item, "motivo"));
            }

            List<string> Items(string name) =>
                Array(name).Where(i => i.ValueKind == JsonValueKind.String).Select(i => Clean(i.GetString()!))
                    .Where(i => i.Length > 0).Distinct().Take(MaxItems).ToList();

            List<Intervention> Interventions(string name) =>
                Array(name).Select(i => new Intervention(Day(Str(i, "quando")), Str(i, "cosa"), Str(i, "perche") is { Length: > 0 } why ? why : Str(i, "perché")))
                    .Where(i => i.What.Length > 0).Take(MaxInterventions).ToList();

            var summary = root.TryGetProperty("sintesi", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.Trim() : "";
            var report = new ProfileReport(date, summary,
                Aspects.Select(a => given.TryGetValue(a, out var g) ? g : new AspectScore(a, null, "")).ToList(),
                Items("punti_di_forza"), Items("da_migliorare"), Interventions("interventi_positivi"), Interventions("interventi_negativi"))
            {
                NextSteps = Items("prossimi_passi").Take(MaxSteps).ToList(),
                Since = root.TryGetProperty("rispetto_a_prima", out var since) && since.ValueKind == JsonValueKind.String ? since.GetString()!.Trim() : "",
            };
            return report.Summary.Length == 0 && !report.Rated.Any() && report.Strengths.Count == 0 && report.Improvements.Count == 0
                ? null : report;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The aspect named (its canonical name), or null when it is not one of <see cref="Aspects"/>.</summary>
    private static string? Aspect(string name) =>
        Aspects.FirstOrDefault(a => a.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase))
        ?? Aspects.FirstOrDefault(a => name.Length >= 4 && a.StartsWith(name.Trim()[..4], StringComparison.CurrentCultureIgnoreCase));

    /// <summary>"2026-11-16", or "" when the text is not a date.</summary>
    private static string Day(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyy-MM-dd") : "";

    /// <summary>One line: no line breaks, no dash that would split the line when read back.</summary>
    private static string Clean(string text) => text.ReplaceLineEndings(" ").Replace(" — ", " - ").Trim();

    // ---- The note ------------------------------------------------------------------------------------------------

    public static string Format(ProfileReport report)
    {
        var text = new StringBuilder($"## {report.Date:yyyy-MM-dd}\n\n");
        text.Append("### In sintesi\n\n").Append(report.Summary.Trim()).Append("\n\n");
        if (report.Since.Trim().Length > 0)
        {
            text.Append("### Rispetto a prima\n\n").Append(report.Since.Trim()).Append("\n\n");
        }

        text.Append("### Voti\n\n");
        foreach (var score in report.Scores)
        {
            text.Append($"- **{score.Aspect}:** {(score.Score is { } s ? s + "/10" : "n.d.")}")
                .Append(score.Reason.Length > 0 ? $" — {score.Reason}" : "").Append('\n');
        }

        void List(string title, IEnumerable<string> lines)
        {
            text.Append($"\n### {title}\n\n");
            var any = false;
            foreach (var line in lines)
            {
                text.Append("- ").Append(line).Append('\n');
                any = true;
            }

            if (!any)
            {
                text.Append("- (niente per ora)\n");
            }
        }

        static string Line(Intervention i) =>
            (i.When.Length > 0 ? $"**{i.When}** " : "") + i.What + (i.Why.Length > 0 ? $" — {i.Why}" : "");

        List("Punti di forza", report.Strengths);
        List("Da migliorare", report.Improvements);
        List("Interventi positivi", report.Positive.Select(Line));
        List("Interventi negativi", report.Negative.Select(Line));
        if (report.NextSteps.Count > 0)
        {
            List("Prossimi passi", report.NextSteps);
        }

        return text.ToString();
    }

    public static IReadOnlyList<ProfileReport> Read(string path)
    {
        try
        {
            return File.Exists(path) ? ParseNote(File.ReadAllText(path)) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>The reports in the note, newest first.</summary>
    public static IReadOnlyList<ProfileReport> ParseNote(string content)
    {
        var text = content.ReplaceLineEndings("\n");
        var headings = Heading().Matches(text);
        var reports = new List<ProfileReport>();
        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Index + headings[i].Length;
            var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            var sections = Sections(text[start..end]);
            if (!DateOnly.TryParseExact(headings[i].Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            string Body(string name) => sections.TryGetValue(name, out var body) ? body : "";
            IEnumerable<string> Bullets(string name) =>
                Body(name).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal))
                    .Select(l => l[2..].Trim()).Where(l => l.Length > 0 && l != "(niente per ora)");

            var given = Bullets("voti").Select(l => ScoreLine().Match(l)).Where(m => m.Success)
                .Select(m => new AspectScore(m.Groups["aspect"].Value.Trim(),
                    int.TryParse(m.Groups["score"].Value, out var s) ? Math.Clamp(s, 1, 10) : null, m.Groups["why"].Value.Trim()))
                .ToList();
            List<Intervention> Interventions(string name) => Bullets(name).Select(l => InterventionLine().Match(l))
                .Select(m => new Intervention(m.Groups["when"].Value.Trim(), m.Groups["what"].Value.Trim(), m.Groups["why"].Value.Trim()))
                .ToList();

            reports.Add(new ProfileReport(date, Body("in sintesi").Trim(), given, Bullets("punti di forza").ToList(),
                Bullets("da migliorare").ToList(), Interventions("interventi positivi"), Interventions("interventi negativi"))
            {
                NextSteps = Bullets("prossimi passi").ToList(),
                Since = Body("rispetto a prima").Trim(),
            });
        }

        return reports.OrderByDescending(r => r.Date).ToList();
    }

    private static Dictionary<string, string> Sections(string text)
    {
        var sections = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        var headings = SubHeading().Matches(text);
        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Index + headings[i].Length;
            var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            sections[headings[i].Groups["title"].Value.Trim()] = text[start..end].Trim();
        }

        return sections;
    }

    /// <summary>Adds the report on top (newest first), replacing one of the same day; creates the note when needed.</summary>
    public static void Add(string path, ProfileReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var original = File.Exists(path) ? File.ReadAllText(path) : Header;
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = original.ReplaceLineEndings("\n");

        var same = new Regex($@"^##[ \t]+{report.Date:yyyy-MM-dd}[ \t]*$", RegexOptions.Multiline).Match(text);
        if (same.Success)
        {
            var next = Heading().Match(text, same.Index + same.Length);
            text = text[..same.Index] + text[(next.Success ? next.Index : text.Length)..];
        }

        var first = Heading().Match(text);
        var section = Format(report);
        text = first.Success
            ? text[..first.Index] + section + "\n" + text[first.Index..]
            : text.TrimEnd() + "\n\n" + section;
        File.WriteAllText(path, text.Replace("\n", newline));
    }

    /// <summary>How a mark moved since the previous report: +2, -1…; null when either is missing.</summary>
    public static int? Change(ProfileReport current, ProfileReport? previous, string aspect)
    {
        var now = current.Scores.FirstOrDefault(s => s.Aspect.Equals(aspect, StringComparison.CurrentCultureIgnoreCase))?.Score;
        var before = previous?.Scores.FirstOrDefault(s => s.Aspect.Equals(aspect, StringComparison.CurrentCultureIgnoreCase))?.Score;
        return now is { } n && before is { } b ? n - b : null;
    }

    [GeneratedRegex(@"^##[ \t]+(?<date>\d{4}-\d{2}-\d{2})[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^###[ \t]+(?<title>[^\n]+?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex SubHeading();

    [GeneratedRegex(@"^\*\*(?<aspect>[^*:]+):?\*\*:?\s*(?:(?<score>\d{1,2})\s*/\s*10|n\.?d\.?)?\s*(?:—\s*(?<why>.*))?$")]
    private static partial Regex ScoreLine();

    [GeneratedRegex(@"^(?:\*\*(?<when>[^*]+)\*\*\s*)?(?<what>.+?)(?:\s+—\s+(?<why>.*))?$")]
    private static partial Regex InterventionLine();
}
