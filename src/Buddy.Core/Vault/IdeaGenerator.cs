using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// "Nuove idee": turns what the user would like to add to the project into a few concrete ideas for To-do Appunti.
/// The prompt carries the project (Scheda, Stato attuale) and what is already there, done or rejected, so the ideas are
/// new; the answer is read as one idea per list line.
/// </summary>
public static partial class IdeaGenerator
{
    private const int MaxScheda = 5000;

    public static string BuildPrompt(ProjectBoard board, string? scheda, string request, int count)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Sei un collaboratore del progetto descritto qui sotto. Proponi esattamente {count} idee concrete in italiano per questa richiesta:");
        prompt.AppendLine();
        prompt.AppendLine($"«{request.Trim()}»");
        prompt.AppendLine();
        prompt.AppendLine("Regole:");
        prompt.AppendLine($"- Rispondi SOLO con {count} righe che iniziano con \"- \", una idea per riga, niente altro (niente titoli, premesse o conclusioni).");
        prompt.AppendLine("- Ogni idea è una frase sola, al massimo 160 caratteri, abbastanza precisa da diventare un task (cosa fare e dove, se si sa).");
        prompt.AppendLine("- Non ripetere task già presenti e non riproporre idee rifiutate (elenchi qui sotto).");
        prompt.AppendLine("- Niente ID, date, priorità o stime: le assegna lo smistamento.");
        prompt.AppendLine("- Non leggere né modificare file: basta il contesto qui sotto.");

        if (board.Status.Count > 0)
        {
            prompt.AppendLine().AppendLine("## Stato attuale");
            foreach (var line in board.Status)
            {
                prompt.AppendLine($"- {line.Label}: {line.Value}");
            }
        }

        void Titles(string heading, IEnumerable<BoardItem> items)
        {
            var list = items.Select(i => i.Title).Distinct().Take(60).ToList();
            if (list.Count > 0)
            {
                prompt.AppendLine().AppendLine($"## {heading}");
                list.ForEach(t => prompt.AppendLine($"- {t}"));
            }
        }

        var columns = board.Columns.ToDictionary(c => c.Key, c => c.Items);
        IEnumerable<BoardItem> Items(params string[] keys) => keys.SelectMany(k => columns.GetValueOrDefault(k) ?? []);
        Titles("Già in programma (non ripetere)", Items("appunti", "valutare", "todo", "attesa", "corso", "controllare"));
        Titles("Già fatti", Items("fatti"));
        Titles("Rifiutati (non riproporre)", Items("rifiutati"));

        if (!string.IsNullOrWhiteSpace(scheda))
        {
            var body = ProjectBoard.Body(scheda);
            prompt.AppendLine().AppendLine("## Scheda del progetto");
            prompt.AppendLine(body.Length > MaxScheda ? body[..MaxScheda] + "\n[…]" : body);
        }

        return prompt.ToString();
    }

    /// <summary>The ideas in an answer: list lines ("- ", "* ", "1. "), cleaned, without duplicates.</summary>
    public static IReadOnlyList<string> Parse(string answer, int max) =>
        answer.Replace("\r\n", "\n").Split('\n')
            .Select(l => ListLine().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups["text"].Value.Replace("**", "").Trim().TrimEnd(';'))
            .Where(t => t.Length >= 8)
            .Select(t => t.Length > 300 ? t[..300].TrimEnd() + "…" : t)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .ToList();

    /// <summary>The appunto line for an idea, in the format of the ones written by hand, marked as generated.</summary>
    public static string ToAppunto(string idea, DateOnly day) =>
        $"- {day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · 💡 {idea}";

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+(?<text>.+)$")]
    private static partial Regex ListLine();
}
