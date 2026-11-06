using System.Globalization;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// The user's own pages of the project window: each sub-folder of "Pagine/" in the project folder is a page, its notes
/// are the page's cards. Pages and notes are plain markdown in the vault, so Obsidian sees and edits them too.
/// </summary>
public static class CustomPages
{
    public const string Folder = "Pagine";

    /// <summary>The page names (sub-folders of Pagine/), alphabetical.</summary>
    public static IReadOnlyList<string> List(string projectPath)
    {
        var root = Path.Combine(projectPath, Folder);
        return Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith('.')).Order(StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
    }

    /// <summary>The notes of a page, newest first.</summary>
    public static IReadOnlyList<VaultNote> Notes(VaultIndex vault, string page)
    {
        var prefix = $"{Folder}/{page}/";
        return vault.Notes.Where(n => n.RelativePath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => Created(n) ?? DateOnly.MinValue)
            .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A new page: an empty folder under Pagine/. The name must be usable as a folder name.</summary>
    public static string CreatePage(string projectPath, string name)
    {
        name = Clean(name);
        if (name.Length == 0)
        {
            throw new IOException("Il nome della pagina è vuoto o contiene solo caratteri non validi.");
        }

        var path = Path.Combine(projectPath, Folder, name);
        if (Directory.Exists(path))
        {
            throw new IOException($"La pagina «{name}» esiste già.");
        }

        Directory.CreateDirectory(path);
        return name;
    }

    /// <summary>
    /// A note in a page, with the date it was written (and the task, when given) in the front matter; returns its path
    /// relative to the project. A note with the same title gets a number, never overwritten.
    /// </summary>
    public static string CreateNote(string projectPath, string page, string title, string markdown, string? taskId = null, DateOnly? today = null)
    {
        var name = Clean(title);
        if (name.Length == 0)
        {
            name = "Nota " + (today ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var folder = Path.Combine(projectPath, Folder, page);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name + ".md");
        for (var i = 2; File.Exists(file); i++)
        {
            file = Path.Combine(folder, $"{name} ({i}).md");
        }

        var date = (today ?? DateOnly.FromDateTime(DateTime.Today)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var front = $"---\ncreated: {date}\n{(taskId is null ? "" : $"task: {taskId}\n")}---\n\n";
        File.WriteAllText(file, front + $"# {title.Trim()}\n\n{markdown.Trim()}\n".ReplaceLineEndings(Environment.NewLine));
        return Path.GetRelativePath(projectPath, file);
    }

    /// <summary>"created: 2026-11-06" in the front matter, else null.</summary>
    public static DateOnly? Created(VaultNote note) =>
        Regex.Match(note.Content, @"^created:\s*(\d{4}-\d{2}-\d{2})", RegexOptions.Multiline) is { Success: true } m
        && DateOnly.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var date) ? date : null;

    /// <summary>The text of a note without front matter and its first heading, for a card's preview.</summary>
    public static string Body(VaultNote note)
    {
        var text = Regex.Replace(note.Content, @"\A---[\s\S]*?\n---\s*\n", "");
        return Regex.Replace(text, @"\A\s*#\s+[^\n]*\n", "").Trim();
    }

    private static string Clean(string name) =>
        string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
}

/// <summary>One line of the Decisioni note: "- 2026-11-06 · cosa — Perché: motivo · NUSR-2367".</summary>
public sealed record Decision(DateOnly? Date, string What, string? Why, string? TaskId, string Raw);

public static partial class Decisions
{
    public const string Note = "Decisioni";
    public const string Section = "Voci";

    public static IReadOnlyList<Decision> Parse(string content, string taskIdPattern)
    {
        var taskId = new Regex(taskIdPattern);
        var decisions = new List<Decision>();
        foreach (Match match in Line().Matches(StripExamples(content)))
        {
            var text = match.Groups["text"].Value.Trim();
            var why = (string?)null;
            var because = text.IndexOf("— Perché:", StringComparison.OrdinalIgnoreCase);
            if (because >= 0)
            {
                why = text[(because + "— Perché:".Length)..].Trim();
                text = text[..because].Trim();
            }

            // A trailing "· NUSR-2367" names the task; else the first id in the text.
            var id = (string?)null;
            var tail = (why ?? text).LastIndexOf(" · ", StringComparison.Ordinal);
            if (tail >= 0 && taskId.Match((why ?? text)[(tail + 3)..]) is { Success: true } trailing)
            {
                id = trailing.Value;
                if (why is not null)
                {
                    why = why[..tail].Trim();
                }
                else
                {
                    text = text[..tail].Trim();
                }
            }

            id ??= taskId.Match(text) is { Success: true } inText ? inText.Value : null;
            DateOnly? date = DateOnly.TryParse(match.Groups["date"].Value, CultureInfo.InvariantCulture, out var d) ? d : null;
            decisions.Add(new Decision(date, text, why, id, match.Value.Trim()));
        }

        return decisions.OrderByDescending(d => d.Date ?? DateOnly.MinValue).ToList();
    }

    /// <summary>The line to add to the note, in its format.</summary>
    public static string Format(DateOnly date, string what, string why, string? taskId) =>
        $"- {date:yyyy-MM-dd} · {what.ReplaceLineEndings(" ").Trim()} — Perché: {(why.Trim().Length == 0 ? "—" : why.ReplaceLineEndings(" ").Trim())}"
        + (taskId is null ? "" : $" · {taskId}");

    internal static string StripExamples(string content) => Examples().Replace(content, "");

    [GeneratedRegex(@"^- (?<date>\d{4}-\d{2}-\d{2})\s*·\s*(?<text>[^\n]+)$", RegexOptions.Multiline)]
    private static partial Regex Line();

    [GeneratedRegex(@"```[\s\S]*?```|<!--[\s\S]*?-->")]
    private static partial Regex Examples();
}

/// <summary>A term of the Glossario note: "- **Termine** (alias) — significato · [[Nota]]".</summary>
public sealed record GlossaryTerm(string Term, string Meaning, IReadOnlyList<string> Links);

public static partial class Glossary
{
    public const string Note = "Glossario";
    public const string Section = "Termini";

    public static IReadOnlyList<GlossaryTerm> Parse(string content)
    {
        var terms = new List<GlossaryTerm>();
        foreach (Match match in Term().Matches(Decisions.StripExamples(content)))
        {
            var meaning = match.Groups["meaning"].Value.Trim();
            var links = WikiLink().Matches(meaning).Select(l => Path.GetFileName(l.Groups[1].Value.Replace('\\', '/'))).ToList();
            meaning = WikiLink().Replace(meaning, "").Trim().TrimEnd('·').Trim();
            terms.Add(new GlossaryTerm(match.Groups["term"].Value.Trim(), meaning, links));
        }

        return terms.OrderBy(t => t.Term, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string Format(string term, string meaning) => $"- **{term.Trim()}** — {meaning.ReplaceLineEndings(" ").Trim()}";

    [GeneratedRegex(@"^- \*\*(?<term>[^*\n]+)\*\*[^—\n]*—\s*(?<meaning>[^\n]+)$", RegexOptions.Multiline)]
    private static partial Regex Term();

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:[#|][^\]]*)?\]\]")]
    private static partial Regex WikiLink();
}
