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

/// <summary>
/// A person of the Persone note: one "## Nome" section with "- **Compito:** …", "- **Descrizione:** …" and
/// "- **Note:** …" lines, "- **Soprannome:** …" (one or more, comma separated) and "- **Sentita:** 2026-11-16" (the
/// last time you talked to them, written by /sentita); "- **Rapporto:** buono" (see <see cref="People.Relationships"/>) and
/// "- **Come comunicare:** …" (what works with them: the buddy and Claude adapt suggestions and drafts to it); any
/// other line joins the notes.
/// </summary>
public sealed record Person(string Name, IReadOnlyList<string> Nicknames, string Role, string Description, string Notes, string Raw,
    DateOnly? LastContact = null, string Relationship = "", string Communication = "")
{
    /// <summary>"Delicato" or "da costruire": a relationship to look after (the mentor asks how it went).</summary>
    public bool NeedsCare => Relationship is "delicato" or "da costruire" or "critico";

    /// <summary>The fields as the add/edit form shows them.</summary>
    public PersonFields Fields => new(Name, string.Join(", ", Nicknames), Role, Description, Notes, Relationship, Communication, LastContact);

    /// <summary>"sentita oggi", "sentita ieri", "sentita 5 giorni fa", "mai sentita".</summary>
    public string LastContactLabel(DateOnly today) => LastContact is not { } day ? "mai sentita"
        : (today.DayNumber - day.DayNumber) switch
        {
            <= 0 => "sentita oggi",
            1 => "sentita ieri",
            var n => $"sentita {n} giorni fa",
        };

    /// <summary>The name with the nicknames: "Mario Rossi (Rox)".</summary>
    public string Display => Nicknames.Count == 0 ? Name : $"{Name} ({string.Join(", ", Nicknames)})";

    /// <summary>The ways the person is called in notes and questions: full name, first or last name, nicknames.</summary>
    public IEnumerable<string> Aliases =>
        Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Prepend(Name).Concat(Nicknames)
            .Where(a => a.Length > 1).Distinct(StringComparer.CurrentCultureIgnoreCase);
}

public static partial class People
{
    public const string Note = "Persone";

    public const string Header =
        "# Persone\n\nLe persone del progetto: cosa fanno, di cosa si occupano e quando conviene sentirle.\n";

    public static IReadOnlyList<Person> Parse(string content)
    {
        var people = new List<Person>();
        var text = Decisions.StripExamples(content).ReplaceLineEndings("\n");
        var headings = Heading().Matches(text);
        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Index;
            var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            var raw = text[start..end].TrimEnd();
            string role = "", relationship = "";
            var nicknames = new List<string>();
            List<string> description = [], communication = [], notes = [];
            List<string>? current = null; // the multi-line field that indented lines below belong to
            DateOnly? lastContact = null;
            foreach (var line in raw.Split('\n').Skip(1).Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0))
            {
                // "  - item" under "- **Descrizione:**": a nested list, the way it is written in Obsidian.
                if (current is not null && char.IsWhiteSpace(line[0]) && !Field().IsMatch(line))
                {
                    current.Add(line.Trim());
                    continue;
                }

                var field = Field().Match(line);
                var value = field.Groups["value"].Value.Trim();
                current = null;
                switch (field.Success ? field.Groups["name"].Value.ToLowerInvariant() : "")
                {
                    case "compito" or "ruolo":
                        role = value;
                        break;
                    case "soprannome" or "soprannomi":
                        nicknames.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    case "descrizione":
                        (current = description).AddRange(ListItems(value));
                        break;
                    case "note":
                        (current = notes).AddRange(ListItems(value));
                        break;
                    case "rapporto":
                        relationship = value.ToLowerInvariant();
                        break;
                    case "come comunicare":
                        (current = communication).AddRange(ListItems(value));
                        break;
                    case "sentita" or "sentito":
                        lastContact = DateOnly.TryParseExact(value, "yyyy-MM-dd",
                            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) ? day : null;
                        break;
                    default:
                        (current = notes).Add(line.Trim()); // free lines below the fields are notes, and so is what is indented under them
                        break;
                }
            }

            people.Add(new Person(headings[i].Groups["name"].Value.Trim(), nicknames, role, string.Join("\n", description),
                string.Join("\n", notes), raw, lastContact, relationship, string.Join("\n", communication)));
        }

        return people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The value written on the field's own line. A list squeezed onto one line ("- a - b - c", as the first version of
    /// the form saved it) is split back into its items.
    /// </summary>
    private static IEnumerable<string> ListItems(string value)
    {
        if (value.Length == 0)
        {
            return [];
        }

        return value.StartsWith("- ", StringComparison.Ordinal) && value.Contains(" - ", StringComparison.Ordinal)
            ? value[2..].Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(i => "- " + i)
            : [value];
    }

    /// <summary>A multi-line value as a markdown list: each line an item ("- …"), whether or not it was typed with the dash.</summary>
    public static string AsList(string value) =>
        string.Join("\n", value.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal) ? "- " + l[2..].Trim() : "- " + l));

    /// <summary>A list on one line, items separated by "; " (for the model's context and the mentor's lines).</summary>
    public static string Inline(string value) =>
        string.Join("; ", value.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal) ? l[2..].Trim() : l));

    /// <summary>True when the value is more than one line, or a list: shown and written as a list.</summary>
    public static bool IsList(string value) =>
        value.Contains('\n') || value.StartsWith("- ", StringComparison.Ordinal) || value.StartsWith("* ", StringComparison.Ordinal);

    /// <summary>The person called <paramref name="name"/>: full name, then nickname, then any alias (first or last name).</summary>
    public static Person? Find(IReadOnlyList<Person> people, string name)
    {
        name = name.Trim();
        return name.Length == 0 ? null
            : people.FirstOrDefault(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
              ?? people.FirstOrDefault(p => p.Nicknames.Contains(name, StringComparer.CurrentCultureIgnoreCase))
              ?? people.FirstOrDefault(p => p.Aliases.Contains(name, StringComparer.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Writes "- **Sentita:** date" in the person's section of the note (replacing the one there, or after the other
    /// fields). Returns the person, or null when nobody in the note is called <paramref name="name"/>.
    /// </summary>
    public static Person? MarkContacted(string path, string name, DateOnly day)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var original = File.ReadAllText(path);
        var newline = NewlineOf(original);
        var text = original.ReplaceLineEndings("\n");
        if (Find(Parse(text), name) is not { } person || SectionOf(text, person.Name) is not var (start, end))
        {
            return null;
        }

        var section = text[start..end];
        var line = $"- **Sentita:** {day:yyyy-MM-dd}";

        var existing = new Regex(@"^\s*[-*]\s*\*\*Sentit[ao]\s*:?\s*\*\*.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (existing.IsMatch(section))
        {
            section = existing.Replace(section, line, 1);
        }
        else
        {
            var body = section.TrimEnd('\n');
            var lines = body.Split('\n').ToList();
            var lastField = lines.FindLastIndex(l => Field().IsMatch(l));
            lines.Insert(lastField >= 0 ? lastField + 1 : lines.Count, line);
            section = string.Join("\n", lines) + section[body.Length..];
        }

        File.WriteAllText(path, (text[..start] + section + text[end..]).Replace("\n", newline));
        return person with { LastContact = day };
    }

    /// <summary>How a relationship can be described: about what to do, not a judgement of the person.</summary>
    public static readonly string[] Relationships = ["ottimo", "buono", "da costruire", "delicato", "critico"];

    /// <summary>The section to add to the note for a new person.</summary>
    public static string Format(string name, string nicknames, string role, string description, string notes) =>
        Format(new PersonFields(name, nicknames, role, description, notes, "", "", null));

    /// <summary>
    /// A person's section, fields in a fixed order. Description, "come comunicare" and notes on several lines become a
    /// nested list under the field ("- **Note:**" then "  - …").
    /// </summary>
    public static string Format(PersonFields person)
    {
        static string One(string value) => value.ReplaceLineEndings(" ").Trim();
        var lines = new List<string> { $"## {One(person.Name)}", "" };
        void Add(string label, string value)
        {
            if (One(value).Length > 0)
            {
                lines.Add($"- **{label}:** {One(value)}");
            }
        }

        // Description, how to communicate and notes: one line stays on the field's line; several lines (or a list)
        // become a nested list under it, as in Obsidian.
        void AddText(string label, string value)
        {
            value = value.Trim();
            if (value.Length == 0)
            {
                return;
            }

            if (!IsList(value))
            {
                lines.Add($"- **{label}:** {value}");
                return;
            }

            lines.Add($"- **{label}:**");
            lines.AddRange(AsList(value).Split('\n').Select(item => "  " + item));
        }

        Add("Soprannome", string.Join(", ", person.Nicknames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        Add("Compito", person.Role);
        AddText("Descrizione", person.Description);
        Add("Rapporto", person.Relationship.ToLowerInvariant());
        AddText("Come comunicare", person.Communication);
        AddText("Note", person.Notes);

        if (person.LastContact is { } last)
        {
            lines.Add($"- **Sentita:** {last:yyyy-MM-dd}");
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Appends a person's section at the end of the note, creating the note when it does not exist.</summary>
    public static void Add(string path, string section)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : Header;
        var name = Heading().Match(section).Groups["name"].Value.Trim();
        if (Parse(existing).Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new IOException($"«{name}» è già nelle {Note}: modifica la sua scheda nella nota.");
        }

        var newline = File.Exists(path) ? NewlineOf(existing) : "\n";
        File.WriteAllText(path, (existing.ReplaceLineEndings("\n").TrimEnd() + "\n\n" + section).Replace("\n", newline));
    }

    /// <summary>
    /// Replaces the section of <paramref name="name"/> with <paramref name="person"/> (the edit form), keeping the rest
    /// of the note and its line endings. Renaming onto another person's name is refused.
    /// </summary>
    public static void Update(string path, string name, PersonFields person)
    {
        var original = File.ReadAllText(path);
        var text = original.ReplaceLineEndings("\n");
        if (SectionOf(text, name) is not var (start, end))
        {
            throw new IOException($"«{name}» non è più nelle {Note}: la nota è cambiata, riaprila.");
        }

        var newName = person.Name.Trim();
        if (!newName.Equals(name, StringComparison.CurrentCultureIgnoreCase)
            && Parse(text).Any(p => p.Name.Equals(newName, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new IOException($"«{newName}» è già nelle {Note}.");
        }

        var section = text[start..end];
        var trailing = section[section.TrimEnd('\n').Length..];
        File.WriteAllText(path, (text[..start] + Format(person).TrimEnd('\n') + (trailing.Length > 0 ? trailing : "\n") + text[end..])
            .Replace("\n", NewlineOf(original)));
    }

    /// <summary>
    /// Adds <paramref name="name"/> to the Persone note when nobody there is called so (by name, nickname, first or last
    /// name), with what is known and a note on where it came from ("dall'episodio …", "dal task NUSR-1"). Creates the note
    /// when missing. Returns the person added, or null when already there or the name is not a name.
    /// </summary>
    public static Person? EnsureExists(string path, string name, string source, DateOnly today, string role = "", string description = "")
    {
        name = name.Trim().Trim('*', '_', '`', '[', ']', '"', '«', '»').Trim();
        if (name.Length < 2 || name.Length > 60 || name.Contains('\n') || !name.Any(char.IsLetter))
        {
            return null;
        }

        var existing = File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        if (Find(existing, name) is not null)
        {
            return null;
        }

        Add(path, Format(new PersonFields(name, "", role, description, $"aggiunta in automatico {source} ({today:yyyy-MM-dd}): da completare", "", "", null)));
        return Find(Parse(File.ReadAllText(path)), name);
    }

    /// <summary>The fields an episode (or the user, from the chat) can change on a card.</summary>
    public static readonly string[] ChangeableFields = ["rapporto", "come comunicare", "note", "ruolo", "si occupa di"];

    /// <summary>
    /// One change to a person's card: <paramref name="mode"/> "imposta" replaces the field, "aggiungi" adds an item to
    /// its list, "togli" removes that item. Returns the person and the field's value before, or null when nobody is
    /// called <paramref name="name"/>, the field is not one of <see cref="ChangeableFields"/> or nothing would change.
    /// </summary>
    public static (Person Person, string Before)? ApplyChange(string path, string name, string field, string mode, string value)
    {
        if (!File.Exists(path) || Find(Parse(File.ReadAllText(path)), name) is not { } person)
        {
            return null;
        }

        field = field.Trim().ToLowerInvariant();
        value = value.Trim();
        var fields = person.Fields;
        var before = field switch
        {
            "rapporto" => fields.Relationship,
            "come comunicare" => fields.Communication,
            "note" => fields.Notes,
            "ruolo" => fields.Role,
            "si occupa di" => fields.Description,
            _ => null,
        };
        if (before is null || (field == "rapporto" && value.Length > 0 && !Relationships.Contains(value.ToLowerInvariant())))
        {
            return null;
        }

        var items = before.Length == 0 ? [] : AsList(before).Split('\n').ToList();
        var item = "- " + (value.StartsWith("- ", StringComparison.Ordinal) ? value[2..].Trim() : value);
        var after = mode.Trim().ToLowerInvariant() switch
        {
            "imposta" => field == "rapporto" ? value.ToLowerInvariant() : value,
            "aggiungi" when field == "ruolo" => value, // one role: adding means replacing
            "aggiungi" when field != "rapporto" && !items.Contains(item, StringComparer.CurrentCultureIgnoreCase) =>
                items.Count == 0 && !IsList(value) ? value : string.Join("\n", items.Append(item)),
            "togli" when field != "rapporto" => string.Join("\n", items.Where(i => !i.Equals(item, StringComparison.CurrentCultureIgnoreCase))),
            _ => null,
        };
        if (after is null || after == before)
        {
            return null;
        }

        // A one-item list goes back to a plain line.
        if (IsList(after) && !after.Contains('\n'))
        {
            after = after[2..];
        }

        Update(path, person.Name, field switch
        {
            "rapporto" => fields with { Relationship = after },
            "come comunicare" => fields with { Communication = after },
            "ruolo" => fields with { Role = after },
            "si occupa di" => fields with { Description = after },
            _ => fields with { Notes = after },
        });
        return (person, before);
    }

    /// <summary>Where the "## Name" section starts and ends ("\n" text). The template's examples are quoted ("> ## …"), so they never match.</summary>
    private static (int Start, int End)? SectionOf(string text, string name)
    {
        var match = new Regex($@"^##[ \t]+{Regex.Escape(name.Trim())}[ \t]*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Match(text);
        if (!match.Success)
        {
            return null;
        }

        var next = Heading().Match(text, match.Index + match.Length);
        return (match.Index, next.Success ? next.Index : text.Length);
    }

    private static string NewlineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// The people a question is about: the ones named in it (full name, first or last name, nickname), or all of them
    /// when the question asks who to talk to ("con chi parlo", "a chi chiedo", "chi devo sentire"…). Empty otherwise.
    /// </summary>
    public static IReadOnlyList<Person> RelevantTo(IReadOnlyList<Person> people, string question)
    {
        var named = Named(people, question);
        return named.Count > 0 ? named : AsksWho().IsMatch(question) ? people : [];
    }

    /// <summary>The people named in <paramref name="text"/> (full name, first or last name, nickname).</summary>
    public static IReadOnlyList<Person> Named(IReadOnlyList<Person> people, string text) =>
        people.Where(p => Mentions(p).IsMatch(text)).ToList();

    /// <summary>The field on a board card naming the person it depends on: "[chi:: Chicca]".</summary>
    public const string CardField = "chi";

    /// <summary>Days without hearing someone a card waits on before the mentor flags it (⚠).</summary>
    public const int StaleContactDays = 3;

    /// <summary>
    /// The open cards with a <see cref="CardField"/>, the ones waiting ("In attesa") first, with the person of the
    /// Persone note it names (by name or nickname), when there is one.
    /// </summary>
    public static IReadOnlyList<PersonOnCard> OnCards(ProjectBoard board, IReadOnlyList<Person> people) =>
        board.Columns.Where(c => c.Key is not ("fatti" or "rifiutati" or "appunti"))
            .SelectMany(c => c.Items.Select(i => (Column: c, Item: i)))
            .Where(x => x.Item.Field(CardField) is not null && !x.Item.Done && !x.Item.Rejected)
            .Select(x =>
            {
                var name = x.Item.Field(CardField)!;
                var person = Find(people, name) ?? Named(people, name).FirstOrDefault();
                return new PersonOnCard(x.Item, x.Column, name, person);
            })
            .OrderBy(w => w.Item.Waiting || w.Column.Key == "attesa" ? 0 : 1)
            .ThenBy(w => w.Person?.LastContact ?? DateOnly.MinValue) // the people not heard for longest first
            .ThenBy(w => w.Item.Date ?? DateOnly.MaxValue)
            .ToList();

    /// <summary>One line for the chat: the card, who it depends on and, from the note, their role and when to reach them.</summary>
    public static string Line(PersonOnCard card, DateOnly today)
    {
        var what = card.Item.Id is { } id ? $"**{id}** · {card.Item.Title}" : card.Item.Title;
        var waiting = card.Item.Waiting || card.Column.Key == "attesa";
        var since = waiting && card.Item.Date is { } d && d < today ? $" da {today.DayNumber - d.DayNumber} giorni" : "";
        var who = card.Person is { } p ? $"**{p.Display}**" + (p.Role.Length > 0 ? $", {p.Role}" : "") : $"**{card.Name}**";
        var line = $"- {what.Replace("`", "")} — {(waiting ? $"aspetta{since}" : "da sentire:")} {who}";
        if (card.Person is not { } person)
        {
            return line;
        }

        // Waiting on someone not heard for a few days: say it, that is the nudge.
        var stale = waiting && (person.LastContact is not { } last || today.DayNumber - last.DayNumber >= StaleContactDays);
        line += $" ({(stale ? "⚠ " : "")}{person.LastContactLabel(today)})";
        return person.Notes.Length > 0 ? $"{line} · _{Inline(person.Notes)}_" : line;
    }

    /// <summary>Matches any way the person is called (whole words, any case): for questions and for notes citing them.</summary>
    public static Regex Mentions(Person person) =>
        new($@"(?<!\w)(?:{string.Join("|", person.Aliases.Where(a => a.Length > 2 || person.Nicknames.Contains(a)).Select(Regex.Escape))})(?!\w)",
            RegexOptions.IgnoreCase);

    /// <summary>One person as a compact block for the local model's context.</summary>
    public static string Describe(Person person)
    {
        var parts = new List<string>
        {
            $"- **{person.Name}**" + (person.Nicknames.Count > 0 ? $" (detto/a {string.Join(", ", person.Nicknames)})" : "")
            + (person.Role.Length > 0 ? $" — {person.Role}" : ""),
        };
        if (person.Description.Length > 0)
        {
            parts.Add($"  Si occupa di: {Inline(person.Description)}");
        }

        if (person.Notes.Length > 0)
        {
            parts.Add($"  Note: {Inline(person.Notes)}");
        }

        if (person.Communication.Length > 0)
        {
            parts.Add($"  Come comunicare: {Inline(person.Communication)}");
        }

        if (person.Relationship.Length > 0)
        {
            parts.Add($"  Rapporto: {person.Relationship}"
                      + (person.Relationship == "critico"
                          ? " (molto delicato: solo il necessario, per iscritto e con i fatti; per un tema importante passare da qualcuno con cui va meglio o dal responsabile)"
                          : person.NeedsCare ? " (preparare bene la conversazione; per un tema delicato, valutare se c'è qualcuno con cui va meglio)" : ""));
        }

        parts.Add(person.LastContact is { } last ? $"  Sentita l'ultima volta: {last:yyyy-MM-dd}" : "  Mai sentita (nessuna data segnata)");
        return string.Join("\n", parts);
    }

    [GeneratedRegex(@"^##[ \t]+(?<name>[^\n#]+?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*[-*]\s*\*\*(?<name>Compito|Ruolo|Soprannom[ei]|Descrizione|Note|Sentit[ao]|Rapporto|Come comunicare)\s*:?\s*\*\*\s*:?\s*(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Field();

    [GeneratedRegex(@"\bchi\b|\bcon chi\b|\ba chi\b|\bsentire\b|\bcontatt|\bcomunic|\bparl(are|o|arne)\b|\breferent|\bpersone?\b|\bscriver(e|gli|le)\b|\bchieder(e|gli|le)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AsksWho();
}

/// <summary>What the add/edit form of a person holds (nicknames comma separated).</summary>
public sealed record PersonFields(
    string Name, string Nicknames, string Role, string Description, string Notes, string Relationship, string Communication, DateOnly? LastContact);

/// <summary>A board card naming the person it depends on ([chi:: …]), with that person's card in the Persone note if any.</summary>
public sealed record PersonOnCard(BoardItem Item, BoardColumn Column, string Name, Person? Person);
