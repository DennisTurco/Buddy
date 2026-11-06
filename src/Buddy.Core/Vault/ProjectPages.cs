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

    /// <summary>
    /// Renames a page (its folder). Links written with the folder ([[Pagine/Old/Nota]]) are updated in the project's
    /// notes; links by title alone keep working by themselves. Returns the new name.
    /// </summary>
    public static string RenamePage(string projectPath, string page, string newName)
    {
        newName = Clean(newName);
        var from = Path.Combine(projectPath, Folder, page);
        var to = Path.Combine(projectPath, Folder, newName);
        if (newName.Length == 0)
        {
            throw new IOException("Il nome della pagina è vuoto o contiene solo caratteri non validi.");
        }

        if (!Directory.Exists(from))
        {
            throw new IOException($"La pagina «{page}» non esiste più.");
        }

        if (string.Equals(page, newName, StringComparison.Ordinal))
        {
            return newName;
        }

        if (Directory.Exists(to) && !string.Equals(page, newName, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"La pagina «{newName}» esiste già.");
        }

        Directory.Move(from, to);
        var oldPrefix = $"{Folder}/{page}/";
        foreach (var file in Directory.EnumerateFiles(projectPath, "*.md", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (text.Contains(oldPrefix, StringComparison.Ordinal))
            {
                File.WriteAllText(file, text.Replace(oldPrefix, $"{Folder}/{newName}/", StringComparison.Ordinal));
            }
        }

        return newName;
    }

    /// <summary>Moves a page and its notes to the vault's trash (see <see cref="VaultTrash"/>); returns where.</summary>
    public static string DeletePage(string projectPath, string page)
    {
        var folder = Path.Combine(projectPath, Folder, page);
        if (!Directory.Exists(folder))
        {
            throw new IOException($"La pagina «{page}» non esiste più.");
        }

        return VaultTrash.Move(projectPath, folder);
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

/// <summary>
/// Notes and pages deleted from Buddy are not erased: they go to the ".trash" folder at the vault's root, the same
/// place Obsidian's "Move to Obsidian trash" uses, so they can be recovered from there.
/// </summary>
public static class VaultTrash
{
    public const string Folder = ".trash";

    /// <summary>Moves a file or folder of the project to the trash, with a number when the name is taken; returns the new path.</summary>
    public static string Move(string projectPath, string path)
    {
        var trash = Path.Combine(FlowTemplate.VaultRootOf(projectPath), Folder);
        Directory.CreateDirectory(trash);
        var isFile = File.Exists(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = isFile ? Path.GetExtension(path) : "";
        var target = Path.Combine(trash, name + extension);
        for (var i = 2; File.Exists(target) || Directory.Exists(target); i++)
        {
            target = Path.Combine(trash, $"{name} ({i}){extension}");
        }

        if (isFile)
        {
            File.Move(path, target);
        }
        else
        {
            Directory.Move(path, target);
        }

        return target;
    }
}

/// <summary>Renaming and deleting a single note from Buddy.</summary>
public static class NoteFiles
{
    /// <summary>
    /// Renames a note: the file, its first heading when it repeated the old title, and the links to it in the
    /// project's notes. Returns the new path relative to the project.
    /// </summary>
    public static string Rename(string projectPath, string relativePath, string newTitle)
    {
        var from = Path.Combine(projectPath, relativePath);
        var oldTitle = Path.GetFileNameWithoutExtension(from);
        newTitle = string.Concat(newTitle.Trim().Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
        if (newTitle.Length == 0)
        {
            throw new IOException("Il titolo è vuoto o contiene solo caratteri non validi.");
        }

        if (!File.Exists(from))
        {
            throw new IOException($"La nota «{oldTitle}» non esiste più.");
        }

        var to = Path.Combine(Path.GetDirectoryName(from)!, newTitle + ".md");
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return relativePath;
        }

        if (File.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Esiste già una nota «{newTitle}» in questa cartella.");
        }

        var text = File.ReadAllText(from);
        var heading = new Regex($@"^#\s+{Regex.Escape(oldTitle)}\s*$", RegexOptions.Multiline);
        File.Move(from, to);
        if (heading.IsMatch(text))
        {
            File.WriteAllText(to, heading.Replace(text, $"# {newTitle}", 1));
        }

        foreach (var file in Directory.EnumerateFiles(projectPath, "*.md", SearchOption.AllDirectories))
        {
            VaultWriter.ReplaceLink(file, oldTitle, newTitle);
        }

        return Path.GetRelativePath(projectPath, to);
    }

    /// <summary>Moves a note to the vault's trash; returns where.</summary>
    public static string Delete(string projectPath, string relativePath)
    {
        var path = Path.Combine(projectPath, relativePath);
        if (!File.Exists(path))
        {
            throw new IOException($"La nota «{Path.GetFileNameWithoutExtension(path)}» non esiste più.");
        }

        return VaultTrash.Move(projectPath, path);
    }

    /// <summary>The flow's own notes (To-Do, Scheda progetto…): Buddy and Claude rely on them, so they cannot be renamed or deleted here.</summary>
    public static bool IsFlowNote(string relativePath) =>
        FlowTemplate.ProjectNotes.Contains(Path.GetFileName(relativePath), StringComparer.OrdinalIgnoreCase);
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
