using System.Globalization;
using System.Text.RegularExpressions;
using Buddy.Core.Vault;

namespace Buddy.Core.Mentor;

/// <summary>One row of Andamento.md: the week, the average mood (1-5), the active hours and whether the habit was kept.</summary>
public sealed record WeekTrend(string Week, double? Mood, double? Hours, string Kept)
{
    /// <summary>"sì", "in parte", "no" or "" from the "Mantenuta?" column.</summary>
    public string KeptKind =>
        Kept.Trim().ToLowerInvariant() switch
        {
            var k when k.StartsWith("in parte", StringComparison.Ordinal) || k.StartsWith("parz", StringComparison.Ordinal) => "in parte",
            var k when k.StartsWith("sì", StringComparison.Ordinal) || k.StartsWith("si", StringComparison.Ordinal) || k.StartsWith('✓') || k.StartsWith('✅') => "sì",
            var k when k.StartsWith("no", StringComparison.Ordinal) || k.StartsWith('✗') || k.StartsWith('❌') => "no",
            _ => "",
        };
}

/// <summary>The episodes of one month: those that went smoothly and those that hurt (see <see cref="EpisodeReflection.IsTense"/>).</summary>
public sealed record EpisodeMonth(DateOnly Month, int Calm, int Tense);

/// <summary>The numbers behind the charts of the profile page, read from the journal and the vault.</summary>
public static partial class ProfileTrends
{
    public const string AndamentoFile = "Andamento.md";

    /// <summary>
    /// The weeks in Andamento.md (written by the weekly retro, so the columns are found by their heading, not their
    /// place), oldest first.
    /// </summary>
    public static IReadOnlyList<WeekTrend> ParseAndamento(string content)
    {
        var rows = content.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('|') && l.Count(c => c == '|') >= 3)
            .Select(l => l.Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        if (rows.Count < 2)
        {
            return [];
        }

        var header = rows[0].Select(h => h.ToLowerInvariant()).ToArray();
        int Column(params string[] words) => Array.FindIndex(header, h => words.Any(h.Contains));
        var week = Math.Max(0, Column("settimana", "week"));
        var mood = Column("umore");
        var hours = Array.FindIndex(header, h => Regex.IsMatch(h, @"\bore\b")); // not "umore"
        var kept = Column("mantenut");

        string Cell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : "";
        return rows.Skip(1)
            .Where(r => !r.All(c => c.Trim('-', ':', ' ').Length == 0)) // the |---|---| line
            .Select(r => new WeekTrend(Cell(r, week), Number(Cell(r, mood)), Number(Cell(r, hours)), Cell(r, kept)))
            .Where(w => w.Week.Length > 0)
            .ToList();
    }

    public static IReadOnlyList<WeekTrend> ReadAndamento(string journal)
    {
        try
        {
            var path = Path.Combine(journal, AndamentoFile);
            return journal.Length > 0 && File.Exists(path) ? ParseAndamento(File.ReadAllText(path)) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// The number a cell starts with ("3,5", "3.5 / 5", "12h"), or null: "n.d. (nessun voto 1-5)" has no value, it only
    /// mentions numbers.
    /// </summary>
    private static double? Number(string cell) =>
        FirstNumber().Match(cell) is { Success: true } m
        && double.TryParse(m.Value.TrimStart('*', '~', ' ').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    /// <summary>"2026-W47" → "W47"; anything else as it is, shortened.</summary>
    public static string ShortWeek(string week) =>
        WeekNumber().Match(week) is { Success: true } m ? "W" + m.Groups[1].Value : week.Length > 8 ? week[..8] : week;

    /// <summary>The episodes of the last <paramref name="months"/> months (the current one included), oldest first.</summary>
    public static IReadOnlyList<EpisodeMonth> EpisodesByMonth(IReadOnlyList<Episode> episodes, DateOnly today, int months = 6)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-(months - 1));
        return Enumerable.Range(0, months)
            .Select(i => first.AddMonths(i))
            .Select(month =>
            {
                var inMonth = episodes.Where(e => e.Date.Year == month.Year && e.Date.Month == month.Month).ToList();
                var tense = inMonth.Count(EpisodeReflection.IsTense);
                return new EpisodeMonth(month, inMonth.Count - tense, tense);
            })
            .ToList();
    }

    /// <summary>How many people have each relationship (People.Relationships order), and how many have none written.</summary>
    public static (IReadOnlyList<(string Relationship, int Count)> Counts, int Unset) Relationships(IReadOnlyList<Person> people)
    {
        var counts = People.Relationships
            .Select(r => (r, people.Count(p => p.Relationship.Equals(r, StringComparison.CurrentCultureIgnoreCase))))
            .ToList();
        return (counts, people.Count - counts.Sum(c => c.Item2));
    }

    [GeneratedRegex(@"^\**~?\s*\d+(?:[.,]\d+)?")]
    private static partial Regex FirstNumber();

    [GeneratedRegex(@"W\s*(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex WeekNumber();
}
