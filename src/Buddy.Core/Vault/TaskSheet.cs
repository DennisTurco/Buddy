using System.Text;

namespace Buddy.Core.Vault;

/// <summary>
/// The sheet of a task, one note per id in the "Task" folder ("Task/NUSR-2670.md"), written by Claude and by the user:
/// "Cosa manca" while the task is open (what blocks it, what is left, what can be done now) and "Come è stato fatto" once
/// it is closed (the steps that got it done, short, to repeat a similar task). The rest of the note is left as written.
/// </summary>
public static class TaskSheet
{
    public const string Folder = "Task";

    public const string Missing = "Cosa manca";

    public const string HowDone = "Come è stato fatto";

    /// <summary>"Task/NUSR-2670.md", relative to the vault.</summary>
    public static string RelativePath(string id) =>
        Path.Combine(Folder, string.Concat(id.Split(Path.GetInvalidFileNameChars())) + ".md");

    /// <summary>The text under <c>## heading</c> (trimmed), empty when the note or the section is not there.</summary>
    public static string Section(string content, string heading)
    {
        // The heading written twice (Claude added its own under an empty one): the texts of all of them, in order.
        var lines = Normalize(content).Split('\n');
        return string.Join("\n\n", AllBounds(lines, heading)
            .Select(b => string.Join('\n', lines[(b.Start + 1)..b.End]).Trim())
            .Where(t => t.Length > 0));
    }

    /// <summary>Reads the note's section; empty when the note does not exist yet.</summary>
    public static string Read(string path, string heading) =>
        File.Exists(path) ? Section(File.ReadAllText(path), heading) : "";

    /// <summary>
    /// The note with the section's text replaced (or the section added, "Cosa manca" before "Come è stato fatto");
    /// a note that does not exist yet starts with the id and title.
    /// </summary>
    public static string WithSection(string? content, string id, string title, string heading, string text)
    {
        var body = Normalize(text).Trim();
        var lines = (content is null ? Header(id, title) : Normalize(content)).TrimEnd().Split('\n').ToList();
        foreach (var (s, e) in AllBounds(lines.ToArray(), heading).Skip(1).Reverse())
        {
            lines.RemoveRange(s, e - s); // a repeated heading goes: its text is in the one written now
        }

        var (start, end) = Bounds(lines.ToArray(), heading);
        var block = body.Length == 0 ? new List<string> { "" } : ["", .. body.Split('\n'), ""];
        if (start >= 0)
        {
            lines.RemoveRange(start + 1, end - start - 1);
            lines.InsertRange(start + 1, block);
        }
        else
        {
            // "Cosa manca" goes before "Come è stato fatto" when that one is already there.
            var (howDone, _) = heading == Missing ? Bounds(lines.ToArray(), HowDone) : (-1, 0);
            var at = howDone >= 0 ? howDone : lines.Count;
            var section = new List<string> { $"## {heading}" };
            section.AddRange(block);
            if (at == lines.Count && lines.Count > 0 && lines[^1].Length > 0)
            {
                section.Insert(0, "");
            }

            lines.InsertRange(at, section);
        }

        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    /// <summary>
    /// Saves the section's text. <paramref name="expected"/> is the text it was based on: when the section now holds
    /// something else (Claude or Obsidian changed it meanwhile) nothing is written and <see cref="VaultConflictException"/>
    /// is thrown, so nothing is overwritten.
    /// </summary>
    public static void Write(string path, string id, string title, string heading, string expected, string text)
    {
        var content = File.Exists(path) ? File.ReadAllText(path) : null;
        if (Section(content ?? "", heading) != Normalize(expected).Trim())
        {
            throw new VaultConflictException(Path.GetFileName(path));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var newline = content?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n";
        File.WriteAllText(path, WithSection(content, id, title, heading, text).Replace("\n", newline), new UTF8Encoding(false));
    }

    /// <summary>The request to Claude that writes (or rewrites) the section from the vault, keeping what the user wrote.</summary>
    public static string Request(string id, string heading)
    {
        var path = RelativePath(id).Replace('\\', '/');
        return heading == HowDone
            ? $"Per {id}: scrivi in `{path}`, sotto `## {HowDone}`, lo storico stra riassuntivo di cosa è stato fatto per "
              + "completarlo, come procedura da ripetere per un task simile: passi numerati, una riga ciascuno, con i nomi "
              + "concreti (pagine, parametri, flag, comandi, file) e senza dati segreti. Prendilo dalle note del task, dal "
              + "diario e dai commit. Se la sezione ha già del testo l'ho scritto o corretto io: tienilo e integra solo quello "
              + "che manca. Scrivi sotto il titolo che c'è già, senza aggiungerne un altro uguale, e non toccare il resto della nota."
            : $"Per {id}: scrivi in `{path}`, sotto `## {Missing}`, cosa manca per chiuderlo, stra riassuntivo: gruppi brevi "
              + "(Bloccante, i passi rimasti per ambiente, Ancora da chiarire, Cosa posso fare adesso) con punti di una riga. "
              + "Prendilo da Stato attuale, dalle note e dal \"Fatto quando\" della voce, dicendo di quando è lo stato più "
              + "recente. Se la sezione ha già del testo l'ho scritto o corretto io: è più aggiornato delle note, tienilo e "
              + "togli solo quello che risulta fatto. Scrivi sotto il titolo che c'è già, senza aggiungerne un altro uguale, e non toccare il resto della nota.";
    }

    private static string Header(string id, string title) =>
        $"# {id} · {title.Trim()}\n\nScheda del task: cosa manca finché è aperto, come è stato fatto una volta chiuso.\n";

    /// <summary>The heading line's index and the index where its section ends (next ## or # heading, or the end).</summary>
    private static (int Start, int End) Bounds(string[] lines, string heading) =>
        AllBounds(lines, heading).DefaultIfEmpty((-1, -1)).First();

    /// <summary>Every section titled <c>## heading</c>, first to last.</summary>
    private static List<(int Start, int End)> AllBounds(string[] lines, string heading)
    {
        var found = new List<(int, int)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal)
                && string.Equals(lines[i][3..].Trim(), heading, StringComparison.OrdinalIgnoreCase))
            {
                var end = Array.FindIndex(lines, i + 1, l => l.StartsWith("# ", StringComparison.Ordinal) || l.StartsWith("## ", StringComparison.Ordinal));
                found.Add((i, end < 0 ? lines.Length : end));
            }
        }

        return found;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
