using System.Text.RegularExpressions;

namespace Buddy.Core.Chat;

/// <summary>
/// Dynamic quick replies: the model ends its answer with a hidden line of 2–3 things the user might answer
/// (<c>&lt;!-- risposte: Sì, procedi | Fammi un esempio --&gt;</c>); Buddy removes it from the text and shows them as
/// buttons under the message, so answering takes a click.
/// </summary>
public static partial class QuickReplies
{
    public const int Max = 3;
    private const int MaxLength = 60;
    private const string Marker = "<!-- risposte:";

    /// <summary>Added to the models' instructions when quick replies are on.</summary>
    public const string Instruction = """
        Risposte rapide: se il tuo messaggio aspetta una risposta dell'utente (una domanda, una scelta, una conferma),
        chiudilo con UNA riga in questo formato esatto, che l'utente non vede e che diventa dei pulsanti:
        <!-- risposte: prima risposta | seconda risposta | terza risposta -->
        Da 2 a 3 risposte brevi (massimo 6 parole), scritte come le scriverebbe l'utente in prima persona, diverse tra
        loro (es. una conferma, un dubbio, una richiesta di spiegazione). Se il messaggio non chiede niente, non
        aggiungere la riga.
        """;

    /// <summary>The text without the replies line, and the replies it held (empty when there was none).</summary>
    public static (string Text, IReadOnlyList<string> Replies) Extract(string text)
    {
        var match = LineRegex().Match(text ?? "");
        if (!match.Success)
        {
            return (text ?? "", []);
        }

        var replies = match.Groups["replies"].Value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim('"', '«', '»', '`', ' '))
            .Where(r => r.Length is > 0 and <= MaxLength)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(Max)
            .ToList();
        var clean = (text![..match.Index] + text[(match.Index + match.Length)..]).TrimEnd();
        return (clean, replies);
    }

    /// <summary>While the answer is still streaming: the text without a replies line, even half written.</summary>
    public static string Hide(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        var full = LineRegex().Match(text);
        if (full.Success)
        {
            return (text[..full.Index] + text[(full.Index + full.Length)..]).TrimEnd();
        }

        // "<!-- risp…" arriving chunk by chunk: hide from the start of the comment.
        var start = text.LastIndexOf("<!--", StringComparison.Ordinal);
        if (start >= 0 && !text[start..].Contains("-->", StringComparison.Ordinal)
            && (Marker.StartsWith(text[start..].TrimEnd(), StringComparison.OrdinalIgnoreCase)
                || text[start..].StartsWith(Marker, StringComparison.OrdinalIgnoreCase)))
        {
            return text[..start].TrimEnd();
        }

        return text;
    }

    [GeneratedRegex(@"[ \t]*<!--\s*risposte\s*:(?<replies>.*?)-->[ \t]*\n?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LineRegex();
}
