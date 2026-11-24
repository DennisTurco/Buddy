using System.Globalization;
using System.Text;

namespace Buddy.Core.Vault;

/// <summary>
/// What happened on one day besides the commits: commitments done (ticked that day) and taken (with their date), the
/// calls held, what was agreed, decided or said (registry and calls), the episodes with colleagues.
/// </summary>
public sealed class DayActivity
{
    public List<SaidEntry> Done { get; } = [];

    public List<SaidEntry> Taken { get; } = [];

    public List<SaidEntry> Said { get; } = [];

    public List<CallTranscript> Calls { get; } = [];

    public List<Episode> Episodes { get; } = [];

    public bool IsEmpty => Done.Count + Taken.Count + Said.Count + Calls.Count + Episodes.Count == 0;
}

/// <summary>
/// The week's activity read from the vault, for the Settimana page and its recap: everything the user did or agreed,
/// not only the commits. A commitment counts on the day it was ticked ([fatto:: …], written when ticked from the Buddy).
/// </summary>
public static class WeekActivity
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static IReadOnlyDictionary<DateOnly, DayActivity> Collect(
        IReadOnlyList<SaidEntry> said, IReadOnlyList<CallTranscript> calls, IReadOnlyList<Episode> episodes, DateOnly from, DateOnly to)
    {
        var days = new Dictionary<DateOnly, DayActivity>();
        DayActivity On(DateOnly day) => days.TryGetValue(day, out var activity) ? activity : days[day] = new DayActivity();
        bool In(DateOnly day) => day >= from && day <= to;

        foreach (var entry in said)
        {
            if (entry.Kind == SaidKind.Impegno)
            {
                if (entry.Done && entry.DoneOn is { } doneOn && In(doneOn))
                {
                    On(doneOn).Done.Add(entry);
                }

                if (In(entry.Date))
                {
                    On(entry.Date).Taken.Add(entry);
                }
            }
            else if (In(entry.Date))
            {
                On(entry.Date).Said.Add(entry);
            }
        }

        foreach (var call in calls.Where(c => In(c.Date)))
        {
            On(call.Date).Calls.Add(call);
        }

        foreach (var episode in episodes.Where(e => In(e.Date)))
        {
            On(episode.Date).Episodes.Add(episode);
        }

        return days;
    }

    /// <summary>
    /// The request behind "🧠 Recap della settimana": Claude writes the recap from <paramref name="week"/> (the week as the
    /// page shows it, day by day) and the commitments still open.
    /// </summary>
    public static string RecapPrompt(DateOnly monday, DateOnly sunday, string week, IReadOnlyList<SaidEntry> open, DateOnly today, string? me)
    {
        var prompt = new StringBuilder($"""
            Scrivi il recap della settimana dal {monday.ToString("d MMMM", Italian)} al {sunday.ToString("d MMMM yyyy", Italian)}
            {(string.IsNullOrWhiteSpace(me) ? "" : $"di {me.Trim()} ")}a partire da quello che è successo giorno per giorno qui sotto (commit, task, impegni,
            call, accordi e decisioni, persone sentite). Non solo il codice: anche il lavoro organizzativo conta.

            Struttura, breve e concreta:
            1. **In sintesi**: 2-3 righe su com'è andata la settimana.
            2. **Portato a termine**: task, impegni chiusi e risultati, raggruppati per argomento (non giorno per giorno).
            3. **Accordi e decisioni**: cosa è stato deciso o concordato e con chi.
            4. **Ancora aperto**: impegni in sospeso (tuoi e degli altri), scaduti per primi.
            5. **Per la prossima settimana**: 2-4 priorità che ne seguono.
            Cita le call per titolo quando servono. Non inventare niente che non sia qui sotto; se ti serve un dettaglio, leggi le
            note citate. Non modificare file.

            ## La settimana
            {week.Trim()}

            ## Impegni ancora aperti (oggi è {today.ToString("d MMMM", Italian)})
            """);
        prompt.AppendLine();
        if (open.Count == 0)
        {
            prompt.AppendLine("(nessuno)");
        }

        foreach (var e in open.OrderBy(e => e.Due ?? DateOnly.MaxValue))
        {
            prompt.AppendLine($"- {(e.Who.Length > 0 ? e.Who : "?")}: {e.Text}"
                              + (e.Due is { } due ? $" · entro {due:yyyy-MM-dd}{(due < today ? " (scaduto)" : "")}" : "")
                              + $" · {(e.SourceTitle is { } title && title != Vault.Said.Note ? $"call «{title}»" : "registro")}");
        }

        return prompt.ToString();
    }
}
