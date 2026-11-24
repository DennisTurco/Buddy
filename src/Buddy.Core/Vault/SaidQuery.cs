using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// "/chiedi cosa ci siamo detti con Domenico sui test in PROD?": the request to Claude, with what the registry and the calls
/// say on it. The people named in the question (by name or nickname) and its words pick the entries and the calls; a
/// question about nothing in particular gets the latest ones. Claude answers with its sources and may open the notes.
/// </summary>
public static partial class SaidQuery
{
    public const int MaxEntries = 120;
    public const int MaxCalls = 8;
    private const int RecentDays = 120;

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Words that say nothing about the subject (Italian question words and the like).</summary>
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "cosa", "come", "quando", "dove", "perché", "perche", "quale", "quali", "chi", "abbiamo", "siamo", "detto", "detti",
        "dette", "deciso", "decisi", "fatto", "fare", "sono", "stato", "stata", "della", "dello", "delle", "degli", "nella",
        "nelle", "sulla", "sulle", "sugli", "alla", "alle", "agli", "dalla", "dalle", "questo", "questa", "quello", "quella",
        "ancora", "aperto", "aperti", "aperta", "aperte", "tutto", "tutti", "ultimo", "ultima", "ultimi", "settimana", "mese",
        "deve", "devo", "devono", "dire", "detta", "parlato", "sentito", "fatte", "riguardo", "rispetto", "anche", "allora",
    };

    public static string Prompt(string question, IReadOnlyList<SaidEntry> entries, IReadOnlyList<CallTranscript> calls,
        IReadOnlyList<Person> people, string? me, string projectPath, DateOnly today)
    {
        var named = People.Named(people, question).Where(p => !Said.IsMe(p.Name, me)).ToList();
        var words = Regex.Matches(question.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}_-]{3,}")
            .Select(m => m.Value).Where(w => !Stop.Contains(w)).Distinct().ToList();

        bool AboutNamed(string text) => named.Any(p => People.Mentions(p).IsMatch(text));
        bool HasWord(string text) => words.Any(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        int Score(string who, string text) => (AboutNamed(who) ? 3 : 0) + (AboutNamed(text) ? 2 : 0) + words.Count(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));

        var picked = entries
            .Select(e => (Entry: e, Score: Score(e.Who, $"{e.Text} {e.Topic} {e.SourceTitle}")))
            .Where(x => named.Count == 0 && words.Count == 0 ? x.Entry.Date >= today.AddDays(-RecentDays) : x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Entry.Date)
            .Take(MaxEntries).Select(x => x.Entry)
            .OrderByDescending(e => e.Date).ToList();

        var pickedCalls = calls
            .Select(c => (Call: c, Score: (c.People.Any(AboutNamed) ? 3 : 0) + (AboutNamed(c.Content) ? 1 : 0) + (HasWord($"{c.Title} {c.Summary}") ? 2 : 0) + (HasWord(c.Content) ? 1 : 0)))
            .Where(x => named.Count == 0 && words.Count == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Call.Date)
            .Take(MaxCalls).Select(x => x.Call).ToList();

        var prompt = new StringBuilder();
        prompt.AppendLine($"""
            Domanda: {question.Trim()}

            Rispondi usando quello che è stato detto tra colleghi: il registro (informazioni, accordi, impegni, decisioni) e le
            call trascritte qui sotto, con le schede delle persone. Oggi è {today.ToString("dddd d MMMM yyyy", Italian)}.
            - Rispondi in breve e in modo ordinato, prima la risposta e poi i dettagli.
            - Per ogni cosa cita la fonte tra parentesi: (call «titolo», 24 nov) oppure (registro, 24 nov).
            - Distingui quello che è stato detto da quello che deduci; se una cosa non risulta, dillo chiaramente invece di
              indovinare, e suggerisci a chi chiederla.
            - Per gli impegni dì chi deve fare cosa, entro quando, e segnala quelli scaduti.
            - Se ti servono più dettagli puoi leggere le note indicate (percorsi completi), in particolare la trascrizione di una
              call. Non modificare file.
            """);
        if (!string.IsNullOrWhiteSpace(me))
        {
            prompt.AppendLine($"Chi ti fa la domanda è {me.Trim()} («io» nel registro).");
        }

        prompt.AppendLine().AppendLine($"## Registro ({Path.Combine(projectPath, Said.FileName)}) e voci delle call");
        if (picked.Count == 0)
        {
            prompt.AppendLine("(nessuna voce pertinente)");
        }

        foreach (var e in picked)
        {
            var source = e.SourceTitle is { } title && title != Said.Note ? $"call «{title}»" : "registro";
            prompt.AppendLine($"- {e.Date:yyyy-MM-dd} · {(e.Who.Length > 0 ? e.Who : "?")} · {e.KindWord}"
                              + (e.Kind == SaidKind.Impegno ? (e.Done ? " (fatto)" : " (aperto)") : "")
                              + $" · {e.Text}"
                              + (e.Due is { } due ? $" · entro {due:yyyy-MM-dd}" : "")
                              + (e.Topic is { } topic ? $" · #{topic}" : "")
                              + $" · fonte: {source}");
        }

        prompt.AppendLine().AppendLine("## Call");
        if (pickedCalls.Count == 0)
        {
            prompt.AppendLine("(nessuna call pertinente)");
        }

        foreach (var call in pickedCalls)
        {
            prompt.AppendLine().AppendLine($"### «{call.Title}» del {call.Date:yyyy-MM-dd}"
                                           + (call.People.Count > 0 ? $" con {string.Join(", ", call.People)}" : ""))
                .AppendLine($"Nota: {Path.Combine(projectPath, call.RelativePath)}")
                .AppendLine(call.Summary.Length > 0 ? call.Summary.Trim() : "(non ancora riassunta: leggi la nota)");
        }

        if (named.Count > 0)
        {
            prompt.AppendLine().AppendLine("## Schede delle persone citate");
            foreach (var person in named)
            {
                prompt.AppendLine().AppendLine(person.Raw.Trim());
            }
        }

        return prompt.ToString();
    }
}
