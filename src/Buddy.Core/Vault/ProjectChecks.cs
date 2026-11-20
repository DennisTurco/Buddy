using System.Globalization;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// A check to run on the project now and then with a Claude skill (a security review, the vault's maintenance…).
/// </summary>
/// <param name="Skill">The skill that runs it, without "/" (e.g. "security-audit").</param>
/// <param name="Title">Its name in the Panoramica.</param>
/// <param name="Description">What it does, in a line.</param>
/// <param name="Request">What to ask the skill, after the command.</param>
/// <param name="EveryDays">After how many days it is worth running again.</param>
public sealed record ProjectCheck(string Skill, string Title, string Description, string Request, int EveryDays)
{
    public string Command => "/" + Skill;
}

/// <summary>The last run of a check, from the log note: when, and what came out (as Claude wrote it).</summary>
public sealed record CheckRun(DateOnly Date, string Outcome);

/// <summary>A check with its last run, as the Panoramica shows it.</summary>
public sealed record CheckStatus(ProjectCheck Check, CheckRun? Last, bool Installed, DateOnly Today)
{
    public DateOnly? DueOn => Last?.Date.AddDays(Check.EveryDays);

    /// <summary>Never run, or run longer ago than <see cref="ProjectCheck.EveryDays"/>.</summary>
    public bool IsDue => Installed && (DueOn is not { } due || due <= Today);
}

/// <summary>
/// Periodic checks of the project (Panoramica → Controlli periodici). Their history is the note «Controlli» in the
/// project's vault: one line per run, "- 2026-11-17 · /security-audit · outcome", written by Claude at the end of
/// the run (the request asks for it), so it can be read and edited like any other note.
/// </summary>
public static partial class ProjectChecks
{
    public const string NoteTitle = "Controlli";

    public static IReadOnlyList<ProjectCheck> All { get; } =
    [
        new("security-audit", "Revisione di sicurezza",
            "Cerca falle di sicurezza nel codice cambiato di recente (revisione mirata, non l'audit completo).",
            "fai una revisione di sicurezza mirata del codice cambiato nell'ultimo mese: parti dai commit recenti e "
            + "concentrati su autenticazione, permessi, input dall'esterno e segreti.",
            EveryDays: 30),
        new("manutenzione", "Manutenzione del vault",
            "Controlla le note e i file di istruzioni cresciuti troppo e propone come alleggerirli.",
            "", EveryDays: 30),
    ];

    /// <summary>The full request to send: the command, what to do, and the line to add to the log at the end.</summary>
    public static string Request(ProjectCheck check, DateOnly today) =>
        $"{check.Command} {check.Request}".TrimEnd()
        + $"\n\nAlla fine aggiungi alla nota «{NoteTitle}» del progetto (creala se manca, con il titolo «# {NoteTitle}»), "
        + "in fondo alla sezione «## Registro», una riga con l'esito in una frase breve, così: "
        + $"`- {today:yyyy-MM-dd} · {check.Command} · <esito>`.";

    /// <summary>The last run of each skill in the log note's text (the most recent date wins; later lines win ties).</summary>
    public static IReadOnlyDictionary<string, CheckRun> LastRuns(string? noteContent)
    {
        var runs = new Dictionary<string, CheckRun>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LogLine().Matches(noteContent ?? ""))
        {
            if (!DateOnly.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            var skill = m.Groups["skill"].Value;
            if (!runs.TryGetValue(skill, out var known) || date >= known.Date)
            {
                runs[skill] = new CheckRun(date, m.Groups["outcome"].Value.Trim().Trim('`').Trim());
            }
        }

        return runs;
    }

    /// <summary>Every check with its last run from <paramref name="noteContent"/>; <paramref name="installed"/> are the skills there.</summary>
    public static IReadOnlyList<CheckStatus> Status(string? noteContent, IReadOnlySet<string> installed, DateOnly today)
    {
        var runs = LastRuns(noteContent);
        return All.Select(c => new CheckStatus(c, runs.GetValueOrDefault(c.Skill), installed.Contains(c.Skill), today)).ToList();
    }

    [GeneratedRegex(@"^\s*[-*]\s*`?(?<date>\d{4}-\d{2}-\d{2})\s*·\s*/(?<skill>[\w-]+)`?(?:\s*·\s*(?<outcome>.*?))?\s*$", RegexOptions.Multiline)]
    private static partial Regex LogLine();
}
