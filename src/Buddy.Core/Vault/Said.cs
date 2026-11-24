using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>What kind of thing was said: a piece of information, an agreement, a commitment, a decision.</summary>
public enum SaidKind
{
    Info,
    Accordo,
    Impegno,
    Decisione,
}

/// <summary>
/// Something said between colleagues: who, what, when and where it is written. <paramref name="Who"/> is who said it or,
/// for a commitment, who has to do it ("io" or the user's name: the user). <paramref name="Source"/> is the note it is in
/// (relative to the project) and <paramref name="Line"/> its line there (1-based), so a commitment can be ticked in place.
/// </summary>
public sealed record SaidEntry(
    DateOnly Date, string Who, SaidKind Kind, string Text, string? Topic, DateOnly? Due, bool Done, string Source, int Line,
    string? SourceTitle = null)
{
    public bool IsOpenCommitment => Kind == SaidKind.Impegno && !Done;

    public bool IsLate(DateOnly today) => IsOpenCommitment && Due is { } due && due < today;

    /// <summary>"accordo", "impegno"…: the word written in the registry.</summary>
    public string KindWord => Said.Word(Kind);

    /// <summary>A key that finds the entry again after a reload: its note and line.</summary>
    public string Key => $"{Source}#{Line}";
}

/// <summary>
/// The registry of what is said between colleagues, for the organisational side of the work: what someone said
/// ("i test in PROD li decide Domenico"), agreements, commitments (who has to do what, by when) and decisions.
/// Two sources, read together: the "Registro" note of the project (one line per entry, written with /detto, /accordo,
/// /impegno or the Registro page) and the calls in Trascrizioni/ (their Decisioni, Azioni and Da ricordare). Nothing is
/// copied from a call: ticking a call's commitment ticks it in the call's note.
/// </summary>
public static partial class Said
{
    public const string Note = "Registro";
    public const string FileName = "Registro.md";
    private const string Section = "Voci";

    public const string Header = """
        ---
        title: Registro
        ---

        # Registro

        Le cose dette tra colleghi: informazioni, accordi, impegni (chi deve fare cosa ed entro quando) e decisioni.
        Le scrive il Buddy (/detto, /accordo, /impegno o la pagina 📒 Registro); si possono scrivere anche a mano, una riga per voce:
        `- [ ] 2026-11-24 · Tony · impegno · mandare i sinonimi [entro:: 2026-11-28] [tema:: CleanEnergy]` (la casella solo per gli impegni).
        Quelle delle call restano nelle loro note in Trascrizioni/: il Buddy le legge insieme a queste.

        ## Voci

        """;

    public static string PathFor(string projectPath) => Path.Combine(projectPath, FileName);

    public static string Word(SaidKind kind) => kind switch
    {
        SaidKind.Accordo => "accordo",
        SaidKind.Impegno => "impegno",
        SaidKind.Decisione => "decisione",
        _ => "info",
    };

    public static SaidKind? KindOf(string word) => word.Trim().ToLowerInvariant() switch
    {
        "info" or "informazione" or "detto" => SaidKind.Info,
        "accordo" => SaidKind.Accordo,
        "impegno" => SaidKind.Impegno,
        "decisione" => SaidKind.Decisione,
        _ => null,
    };

    /// <summary>True when <paramref name="who"/> is the user ("io", "me" or their name).</summary>
    public static bool IsMe(string who, string? me) =>
        who.Trim().ToLowerInvariant() is "io" or "me" || (!string.IsNullOrWhiteSpace(me) && who.Trim().Equals(me.Trim(), StringComparison.CurrentCultureIgnoreCase));

    // ---- Reading ------------------------------------------------------------------------------------------------

    /// <summary>Everything said, from the registry and the calls, newest first.</summary>
    public static IReadOnlyList<SaidEntry> All(VaultIndex vault, string projectPath)
    {
        var entries = new List<SaidEntry>();
        var registry = PathFor(projectPath);
        try
        {
            if (File.Exists(registry))
            {
                entries.AddRange(ParseRegistry(File.ReadAllText(registry), FileName));
            }
        }
        catch (IOException)
        {
            // being written: next refresh
        }

        foreach (var call in CallTranscripts.List(vault))
        {
            entries.AddRange(FromCall(call));
        }

        return entries.OrderByDescending(e => e.Date).ThenBy(e => e.Source, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Line).ToList();
    }

    /// <summary>The entries of the Registro note.</summary>
    public static IReadOnlyList<SaidEntry> ParseRegistry(string content, string relativePath)
    {
        var entries = new List<SaidEntry>();
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = EntryLine().Match(lines[i]);
            if (!match.Success || KindOf(match.Groups["kind"].Value) is not { } kind
                || !DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var text = Field().Replace(match.Groups["text"].Value, m =>
            {
                fields[m.Groups["key"].Value.Trim()] = m.Groups["value"].Value.Trim();
                return "";
            }).Trim();
            entries.Add(new SaidEntry(date, match.Groups["who"].Value.Trim(), kind, text,
                fields.TryGetValue("tema", out var topic) && topic.Length > 0 ? topic : null,
                fields.TryGetValue("entro", out var due) ? ParseDate(due) : null,
                match.Groups["mark"].Value is "x" or "X", relativePath, i + 1, Note));
        }

        return entries;
    }

    /// <summary>
    /// A call's entries: its decisions, its actions as commitments ("- [ ] **Chi:** cosa (entro 2026-11-28)") and what to
    /// remember ("- **Chi:** cosa"), dated with the call and pointing to its lines.
    /// </summary>
    public static IReadOnlyList<SaidEntry> FromCall(CallTranscript call)
    {
        var entries = new List<SaidEntry>();
        var lines = call.Content.ReplaceLineEndings("\n").Split('\n');
        string? section = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.Match(line, @"^##[ \t]+(?<h>.+?)\s*$") is { Success: true } heading)
            {
                section = heading.Groups["h"].Value.Trim().ToLowerInvariant();
                continue;
            }

            if (section is not ("decisioni" or "azioni" or "da ricordare"))
            {
                continue;
            }

            var item = Regex.Match(line, @"^\s*[-*]\s+(?:\[(?<mark>[ xX])\]\s+)?(?<text>.+)$");
            if (!item.Success || Regex.IsMatch(item.Groups["text"].Value, @"^_?(nessun[ao]?|niente)\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var text = item.Groups["text"].Value.Trim();
            var who = "";
            if (Regex.Match(text, @"^\*\*(?<who>[^*]+?):?\*\*:?\s*") is { Success: true } bold)
            {
                who = bold.Groups["who"].Value.Trim().TrimEnd(':');
                text = text[bold.Length..].Trim();
            }

            DateOnly? due = null;
            if (Regex.Match(text, @"\(?\bentro\s+(?:il\s+)?(?<d>\d{4}-\d{2}-\d{2})\)?", RegexOptions.IgnoreCase) is { Success: true } by)
            {
                due = ParseDate(by.Groups["d"].Value);
            }

            var kind = section switch
            {
                "decisioni" => SaidKind.Decisione,
                "azioni" => SaidKind.Impegno,
                _ => SaidKind.Info,
            };
            if (kind == SaidKind.Impegno && !item.Groups["mark"].Success)
            {
                continue; // "- Nessuna" and the like: an action is always a checkbox
            }

            entries.Add(new SaidEntry(call.Date, who == "?" ? "" : who, kind, Regex.Replace(text, @"\*\*|__", ""), call.Task, due,
                item.Groups["mark"].Value is "x" or "X", call.RelativePath, i + 1, call.Title));
        }

        return entries;
    }

    // ---- Writing ------------------------------------------------------------------------------------------------

    /// <summary>The registry line of an entry.</summary>
    public static string Format(SaidEntry entry)
    {
        var line = new StringBuilder(entry.Kind == SaidKind.Impegno ? (entry.Done ? "- [x] " : "- [ ] ") : "- ")
            .Append(CultureInfo.InvariantCulture, $"{entry.Date:yyyy-MM-dd} · {OneLine(entry.Who)} · {entry.KindWord} · {OneLine(entry.Text)}");
        if (entry.Due is { } due)
        {
            line.Append(CultureInfo.InvariantCulture, $" [entro:: {due:yyyy-MM-dd}]");
        }

        if (!string.IsNullOrWhiteSpace(entry.Topic))
        {
            line.Append($" [tema:: {OneLine(entry.Topic)}]");
        }

        return line.ToString();
    }

    /// <summary>Adds an entry at the top of the registry (created when missing). Returns it with its line.</summary>
    public static SaidEntry Add(string projectPath, SaidEntry entry)
    {
        if (entry.Text.Trim().Length == 0)
        {
            throw new IOException("Scrivi cosa è stato detto.");
        }

        var path = PathFor(projectPath);
        var original = File.Exists(path) ? File.ReadAllText(path) : Header;
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.ReplaceLineEndings("\n").Split('\n').ToList();
        var heading = lines.FindIndex(l => Regex.IsMatch(l, $@"^##[ \t]+{Section}\s*$"));
        if (heading < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"## {Section}");
            lines.Add("");
            heading = lines.Count - 2;
        }

        // Newest first: right under the heading (after its blank line).
        var at = heading + 1;
        while (at < lines.Count && lines[at].Trim().Length == 0)
        {
            at++;
        }

        if (at == heading + 1)
        {
            lines.Insert(at++, "");
        }

        lines.Insert(at, Format(entry));
        File.WriteAllText(path, string.Join(newline, lines));
        return entry with { Source = FileName, Line = at + 1, SourceTitle = Note };
    }

    /// <summary>
    /// Ticks (or unticks) a commitment where it is written: the registry or the call's note. Refused (IOException) when the
    /// line is not the same any more (the note changed meanwhile).
    /// </summary>
    public static void SetDone(string projectPath, SaidEntry entry, bool done)
    {
        var path = Path.Combine(projectPath, entry.Source);
        if (!File.Exists(path))
        {
            throw new IOException($"«{entry.Source}» non c'è più.");
        }

        var original = File.ReadAllText(path);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.ReplaceLineEndings("\n").Split('\n');
        var index = entry.Line - 1;
        var words = Regex.Replace(entry.Text, @"\W+", " ").Trim();
        if (index < 0 || index >= lines.Length || !Regex.IsMatch(lines[index], @"^\s*[-*] \[[ xX]\]")
            || !Regex.Replace(Regex.Replace(lines[index], @"\*\*|__", ""), @"\W+", " ").Contains(words, StringComparison.CurrentCultureIgnoreCase))
        {
            throw new IOException($"La nota «{Path.GetFileNameWithoutExtension(entry.Source)}» è cambiata: ricarica e riprova.");
        }

        lines[index] = Regex.Replace(lines[index], @"^(\s*[-*] )\[[ xX]\]", m => m.Groups[1].Value + (done ? "[x]" : "[ ]"));
        File.WriteAllText(path, string.Join(newline, lines));
    }

    // ---- Commands -----------------------------------------------------------------------------------------------

    /// <summary>
    /// "/detto Domenico: i test in PROD li decide lui #CleanEnergy" and the like: who (before the colon), the text, the
    /// topic (#parola) and, for a commitment, the day ("entro venerdì", "entro il 28", "entro 2026-11-28").
    /// Null when there is no "chi:".
    /// </summary>
    public static (string Who, string Text, string? Topic, DateOnly? Due)? ParseCommand(string input, DateOnly today)
    {
        var match = Regex.Match(input.Trim(), @"^(?<who>[^:\n]{1,40}):\s*(?<text>.+)$", RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var text = match.Groups["text"].Value.Trim();
        string? topic = null;
        text = Regex.Replace(text, @"(^|\s)#(?<t>[\p{L}\p{N}_-]+)", m =>
        {
            topic ??= m.Groups["t"].Value;
            return "";
        }).Trim();

        DateOnly? due = null;
        text = Regex.Replace(text, @"\s*\(?\bentro\s+(?:il\s+|la\s+|lo\s+)?(?<d>\d{4}-\d{2}-\d{2}|\+\d+|[\p{L}]+(?:\s+\d{1,2})?|\d{1,2})\)?\s*$", m =>
        {
            due = ParseDay(m.Groups["d"].Value, today);
            return due is null ? m.Value : "";
        }, RegexOptions.IgnoreCase).Trim();

        return text.Length == 0 ? null : (match.Groups["who"].Value.Trim(), text, topic, due);
    }

    /// <summary>"2026-11-28", "venerdì", "domani", "+3", "28" (that day of this month, or the next if past).</summary>
    public static DateOnly? ParseDay(string text, DateOnly today)
    {
        text = text.Trim().ToLowerInvariant();
        if (ParseDate(text) is { } exact)
        {
            return exact;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 1 and <= 31)
        {
            var month = new DateOnly(today.Year, today.Month, 1);
            for (var i = 0; i < 2; i++, month = month.AddMonths(1))
            {
                if (day <= DateTime.DaysInMonth(month.Year, month.Month) && new DateOnly(month.Year, month.Month, day) is var candidate && candidate >= today)
                {
                    return candidate;
                }
            }
        }

        return Reminders.ParseDay(text, today);
    }

    private static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string OneLine(string text) => Regex.Replace(text.ReplaceLineEndings(" "), @"\s+", " ").Trim();

    [GeneratedRegex(@"^\s*[-*]\s+(?:\[(?<mark>[ xX])\]\s+)?(?<date>\d{4}-\d{2}-\d{2})\s*·\s*(?<who>[^·]*?)\s*·\s*(?<kind>[\p{L}]+)\s*·\s*(?<text>.+)$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@"\[(?<key>[^\[\]:]+)::(?<value>[^\]]*)\]")]
    private static partial Regex Field();
}
