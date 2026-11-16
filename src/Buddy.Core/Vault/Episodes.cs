using System.Globalization;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// Something that happened at work with some people, as the user tells it: the facts kept apart from the reaction, so
/// the analysis does not mistake one for the other. <paramref name="AboutMe"/> is what the analysis read in it about
/// how the user communicates and reacts (used by the weekly retro for Profilo.md); <paramref name="Changes"/> the
/// changes made to the people's cards because of it; <paramref name="Lesson"/> what to remember next time with those
/// people (from "cosa rifarei diversamente"), brought back when they come up again.
/// </summary>
public sealed record Episode(
    DateOnly Date, string Title, IReadOnlyList<string> People, string Facts, string Reaction, string Outcome,
    string AboutMe, IReadOnlyList<string> Changes, string Raw = "", string Lesson = "")
{
    /// <summary>"2026-11-16 · Titolo": the episode's heading, and how commands refer to it.</summary>
    public string Key => $"{Date:yyyy-MM-dd} · {Title}";
}

/// <summary>
/// The Episodi note: one "## 2026-11-16 · Titolo" section per episode, newest first, with "- **Persone:**",
/// "- **Cosa è successo:**", "- **Come ho reagito:**", "- **Com'è finita:**", "- **Su di me:**" and
/// "- **Modifiche alle schede:**" (the last two written after the analysis). It lives in the user's journal: it is about
/// them, not about the project, and never leaves the vault.
/// </summary>
public static partial class Episodes
{
    public const string FileName = "Episodi.md";

    public const string Header =
        "# Episodi\n\nCose successe al lavoro con le persone del progetto: i fatti, come ho reagito, com'è finita. "
        + "Servono a tenere aggiornate le schede delle Persone e a capire come comunico. Restano private: mai fuori dal vault.\n";

    /// <summary>The note's path: in the journal when there is one, otherwise next to the project's notes.</summary>
    public static string PathFor(BuddySettings settings) =>
        Path.Combine(!string.IsNullOrWhiteSpace(settings.Mentor.JournalPath) ? settings.Mentor.JournalPath : settings.ProjectPath, FileName);

    public static IReadOnlyList<Episode> Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>The episodes, newest first.</summary>
    public static IReadOnlyList<Episode> Parse(string content)
    {
        var text = Decisions.StripExamples(content).ReplaceLineEndings("\n");
        var headings = Heading().Matches(text);
        var episodes = new List<Episode>();
        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Index;
            var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
            var raw = text[start..end].TrimEnd();
            var fields = Fields(raw.Split('\n').Skip(1));
            string Get(string name) => fields.TryGetValue(name, out var value) ? value : "";
            var date = DateOnly.TryParseExact(headings[i].Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : DateOnly.MinValue;
            episodes.Add(new Episode(date, headings[i].Groups["title"].Value.Trim(),
                Get("persone").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                Get("cosa è successo"), Get("come ho reagito"), Get("com'è finita"), Get("su di me"),
                Get("modifiche alle schede").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? l[2..] : l).ToList(),
                raw, Get("da ricordare")));
        }

        return episodes.OrderByDescending(e => e.Date).ToList();
    }

    /// <summary>"- **Name:** value" lines, with the indented lines below a field joined to it (a nested list).</summary>
    private static Dictionary<string, string> Fields(IEnumerable<string> lines)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var line in lines.Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0))
        {
            if (Field().Match(line) is { Success: true } field && !char.IsWhiteSpace(line[0]))
            {
                current = fields[field.Groups["name"].Value.Trim().ToLowerInvariant()] = [];
                if (field.Groups["value"].Value.Trim() is { Length: > 0 } value)
                {
                    current.Add(value);
                }
            }
            else
            {
                current?.Add(line.Trim());
            }
        }

        return fields.ToDictionary(f => f.Key, f => string.Join("\n", f.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An episode's section; multi-line values become a nested list, as in the Persone note.</summary>
    public static string Format(Episode episode)
    {
        var lines = new List<string> { $"## {episode.Date:yyyy-MM-dd} · {episode.Title.ReplaceLineEndings(" ").Trim()}", "" };
        void Add(string label, string value)
        {
            value = value.Trim();
            if (value.Length == 0)
            {
                return;
            }

            if (!Vault.People.IsList(value))
            {
                lines.Add($"- **{label}:** {value}");
                return;
            }

            lines.Add($"- **{label}:**");
            lines.AddRange(Vault.People.AsList(value).Split('\n').Select(item => "  " + item));
        }

        Add("Persone", string.Join(", ", episode.People));
        Add("Cosa è successo", episode.Facts);
        Add("Come ho reagito", episode.Reaction);
        Add("Com'è finita", episode.Outcome);
        Add("Su di me", episode.AboutMe);
        Add("Da ricordare", episode.Lesson);
        if (episode.Changes.Count > 0)
        {
            lines.Add("- **Modifiche alle schede:**");
            lines.AddRange(episode.Changes.Select(c => "  - " + c.ReplaceLineEndings(" ")));
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Adds the episode first (newest on top), creating the note when needed. Returns it as saved.</summary>
    public static Episode Add(string path, Episode episode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var original = File.Exists(path) ? File.ReadAllText(path) : Header;
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = original.ReplaceLineEndings("\n");

        // Two episodes with the same day and title would be one key: number the second.
        var keys = Parse(text).Select(e => e.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var title = episode.Title.Trim();
        for (var n = 2; keys.Contains($"{episode.Date:yyyy-MM-dd} · {title}"); n++)
        {
            title = $"{episode.Title.Trim()} ({n})";
        }

        episode = episode with { Title = title };
        var section = Format(episode);
        var first = Heading().Match(text);
        text = first.Success
            ? text[..first.Index] + section + "\n" + text[first.Index..]
            : text.TrimEnd() + "\n\n" + section;
        File.WriteAllText(path, text.Replace("\n", newline));
        return episode;
    }

    /// <summary>Rewrites one episode (found by its key) with <paramref name="update"/> applied, e.g. after the analysis.</summary>
    public static Episode? Update(string path, string key, Func<Episode, Episode> update)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var original = File.ReadAllText(path);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = original.ReplaceLineEndings("\n");
        var episode = Parse(text).FirstOrDefault(e => e.Key.Equals(key.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (episode is null)
        {
            return null;
        }

        var match = new Regex($@"^##[ \t]+{Regex.Escape(episode.Key)}[ \t]*$", RegexOptions.Multiline).Match(text);
        var next = Heading().Match(text, match.Index + match.Length);
        var end = next.Success ? next.Index : text.Length;
        var trailing = text[match.Index..end];
        trailing = trailing[trailing.TrimEnd('\n').Length..];
        var updated = update(episode);
        File.WriteAllText(path, (text[..match.Index] + Format(updated).TrimEnd('\n') + (trailing.Length > 0 ? trailing : "\n") + text[end..])
            .Replace("\n", newline));
        return updated;
    }

    /// <summary>The episodes a person took part in (by name or nickname), newest first.</summary>
    public static IReadOnlyList<Episode> Involving(IReadOnlyList<Episode> episodes, Person person)
    {
        var mentions = Vault.People.Mentions(person);
        return episodes.Where(e => e.People.Any(p => mentions.IsMatch(p)) || mentions.IsMatch(e.Facts)).ToList();
    }

    /// <summary>
    /// The screenshots of an episode (![[name.png]] or ![](path) in its fields) that exist in <paramref name="folder"/> (the
    /// one of Episodi.md) or its Allegati folder: full paths.
    /// </summary>
    public static IReadOnlyList<string> Images(Episode episode, string folder) =>
        Embed().Matches($"{episode.Facts}\n{episode.Reaction}\n{episode.Outcome}")
            .Select(m => Uri.UnescapeDataString((m.Groups["wiki"].Success ? m.Groups["wiki"].Value : m.Groups["path"].Value).Trim()))
            .Where(NoteImages.IsImage)
            .Select(name => new[] { Path.Combine(folder, name), Path.Combine(folder, NoteImages.Folder, Path.GetFileName(name)) }
                .Select(Path.GetFullPath).FirstOrDefault(File.Exists))
            .OfType<string>()
            .Distinct()
            .ToList();

    /// <summary>
    /// What to remember with a person, from their latest episodes that have a "Da ricordare" (newest first): what the
    /// user wanted to do differently last time.
    /// </summary>
    public static IReadOnlyList<Episode> LessonsFor(IReadOnlyList<Episode> episodes, Person person, int max = 2) =>
        Involving(episodes, person).Where(e => e.Lesson.Trim().Length > 0).Take(max).ToList();

    /// <summary>"💡 l'ultima volta con Chicca (12 nov): mandarle prima i dati", or "" when there is nothing to remember.</summary>
    public static string LessonLine(IReadOnlyList<Episode> episodes, Person person)
    {
        var lesson = LessonsFor(episodes, person, 1).FirstOrDefault();
        var name = person.Nicknames.Count > 0 ? person.Nicknames[0] : person.Name;
        return lesson is null ? ""
            : $"💡 l'ultima volta con {name} ({lesson.Date.ToString("d MMM", CultureInfo.GetCultureInfo("it-IT"))}): {Vault.People.Inline(lesson.Lesson)}";
    }

    /// <summary>A title from free text: its first words.</summary>
    public static string TitleFrom(string text)
    {
        var words = text.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(' ', words.Take(8)).TrimEnd('.', ',', ';', ':');
        return words.Length > 8 ? title + "…" : title;
    }

    [GeneratedRegex(@"^##[ \t]+(?<date>\d{4}-\d{2}-\d{2})[ \t]*·[ \t]*(?<title>[^\n]+?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*[-*]\s*\*\*(?<name>[^*:]+?)\s*:?\s*\*\*\s*:?\s*(?<value>.*)$")]
    private static partial Regex Field();

    [GeneratedRegex(@"!\[\[(?<wiki>[^\]|#]+)(?:[|#][^\]]*)?\]\]|!\[[^\]]*\]\((?<path>[^)\s]+)\)")]
    private static partial Regex Embed();
}
