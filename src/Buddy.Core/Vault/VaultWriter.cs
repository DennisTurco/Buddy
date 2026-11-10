using System.Text;

namespace Buddy.Core.Vault;

/// <summary>
/// The few edits the project board makes to the vault: replace one item, add an appunto, save a whole note.
/// Every edit re-reads the file first and refuses to write when the text it was based on is gone (changed in Obsidian
/// meanwhile), so nothing typed elsewhere is overwritten. The file's line endings are kept.
/// </summary>
public static class VaultWriter
{
    /// <summary>Replaces the item block <paramref name="oldText"/> with <paramref name="newText"/> (empty: deletes it).</summary>
    public static void ReplaceBlock(string path, string oldText, string newText)
    {
        var (content, newline) = Read(path);
        var old = Normalize(oldText).TrimEnd();
        var index = content.IndexOf(old, StringComparison.Ordinal);
        if (old.Length == 0 || index < 0)
        {
            throw new VaultConflictException(Path.GetFileName(path));
        }

        var replacement = Normalize(newText).TrimEnd();
        var end = index + old.Length;
        if (replacement.Length == 0 && end < content.Length && content[end] == '\n')
        {
            end++; // deleting: take the line break too, so no empty line is left behind
        }

        Write(path, content[..index] + replacement + content[end..], newline);
    }

    /// <summary>
    /// Appends a list line at the end of the section titled <paramref name="heading"/> (e.g. "Appunti"), or at the end
    /// of the note when it has no such section.
    /// </summary>
    public static void AppendItem(string path, string heading, string line)
    {
        var (content, newline) = Read(path);
        var lines = content.Split('\n').ToList();
        var start = lines.FindIndex(l => l.TrimStart('#', ' ').Trim().Equals(heading, StringComparison.OrdinalIgnoreCase) && l.StartsWith('#'));
        var end = start < 0 ? lines.Count : lines.FindIndex(start + 1, l => l.StartsWith('#')) is var next and >= 0 ? next : lines.Count;

        // After the section's last non-empty line, keeping the blank lines that separate it from the next one.
        var at = end;
        while (at > start + 1 && string.IsNullOrWhiteSpace(lines[at - 1]))
        {
            at--;
        }

        if (start >= 0 && at == start + 1)
        {
            lines.Insert(at++, ""); // heading followed directly by the first item reads badly in some renderers
        }

        lines.Insert(at, Normalize(line).Trim());
        Write(path, string.Join('\n', lines), newline);
    }

    /// <summary>
    /// Puts a list line first in the section titled <paramref name="heading"/> (newest first, as in Decisioni), after
    /// the blank line that follows the heading; at the end of the note when there is no such section.
    /// </summary>
    public const string LinksHeading = "Collegamenti";

    /// <summary>
    /// Adds "- [[<paramref name="link"/>]]" to the note's "## Collegamenti" section, creating the section at the end
    /// when there is none. False when the note already links there (in any form Obsidian understands).
    /// </summary>
    public static bool AddLink(string path, string link, string targetTitle)
    {
        var (content, newline) = Read(path);
        var wanted = new[] { link, targetTitle }.Select(t => t.Replace('\\', '/')).ToList();
        if (NoteGraph.Targets(content).Any(t => wanted.Any(w => string.Equals(t, w, StringComparison.OrdinalIgnoreCase)
                                                             || string.Equals(Path.GetFileName(t), w, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        var line = $"- [[{link}]]";
        var lines = content.Split('\n').ToList();
        if (lines.Any(l => l.StartsWith('#') && l.TrimStart('#', ' ').Trim().Equals(LinksHeading, StringComparison.OrdinalIgnoreCase)))
        {
            AppendItem(path, LinksHeading, line);
            return true;
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        lines.AddRange(["", $"## {LinksHeading}", "", line, ""]);
        Write(path, string.Join('\n', lines), newline);
        return true;
    }

    public static void InsertItemFirst(string path, string heading, string line)
    {
        var (content, newline) = Read(path);
        var lines = content.Split('\n').ToList();
        var start = lines.FindIndex(l => l.StartsWith('#') && l.TrimStart('#', ' ').Trim().Equals(heading, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            lines.Add("");
            lines.Add(Normalize(line).Trim());
        }
        else
        {
            var at = start + 1;
            if (at < lines.Count && string.IsNullOrWhiteSpace(lines[at]))
            {
                at++;
            }
            else
            {
                lines.Insert(at++, "");
            }

            lines.Insert(at, Normalize(line).Trim());
        }

        Write(path, string.Join('\n', lines), newline);
    }

    /// <summary>
    /// Ticks or unticks the checkbox on line <paramref name="index"/> ("- [ ] …" ↔ "- [x] …"), if that line still reads
    /// <paramref name="expected"/>. Only the box changes: the rest of the line and of the note stay as written.
    /// </summary>
    public static void SetCheckbox(string path, int index, string expected, bool done)
    {
        var (content, newline) = Read(path);
        var lines = content.Split('\n');
        if (index < 0 || index >= lines.Length || lines[index] != Normalize(expected))
        {
            throw new VaultConflictException(Path.GetFileName(path));
        }

        var line = lines[index];
        var box = line.IndexOf("[", StringComparison.Ordinal);
        if (box < 0 || box + 2 >= line.Length || line[box + 2] != ']')
        {
            throw new VaultConflictException(Path.GetFileName(path));
        }

        lines[index] = string.Concat(line.AsSpan(0, box + 1), done ? "x" : " ", line.AsSpan(box + 2));
        Write(path, string.Join('\n', lines), newline);
    }

    /// <summary>
    /// Points the links to <paramref name="oldTitle"/> in a note at <paramref name="newTitle"/> instead ([[Old]],
    /// [[Folder/Old|alias]], [[Old#heading]] keep their alias and heading). Returns how many were changed.
    /// </summary>
    public static int ReplaceLink(string path, string oldTitle, string newTitle)
    {
        var (content, newline) = Read(path);
        var count = 0;
        var updated = System.Text.RegularExpressions.Regex.Replace(content,
            $@"\[\[(?:[^\]|#\n]*/)?{System.Text.RegularExpressions.Regex.Escape(oldTitle)}(?=[\]|#])",
            _ =>
            {
                count++;
                return "[[" + newTitle;
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (count > 0)
        {
            Write(path, updated, newline);
        }

        return count;
    }

    /// <summary>Creates an empty note with a title, refusing to overwrite one that exists.</summary>
    public static void CreateNote(string path, string title)
    {
        if (File.Exists(path))
        {
            throw new IOException($"La nota «{Path.GetFileName(path)}» esiste già.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Write(path, $"# {title}\n\n", Environment.NewLine);
    }

    /// <summary>Saves a note edited as a whole, if it still holds <paramref name="expected"/>.</summary>
    public static void Save(string path, string expected, string content)
    {
        var (current, newline) = Read(path);
        if (current != Normalize(expected))
        {
            throw new VaultConflictException(Path.GetFileName(path));
        }

        Write(path, Normalize(content), newline);
    }

    private static (string Content, string Newline) Read(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return (Normalize(text), text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
    }

    private static void Write(string path, string content, string newline)
    {
        // Through a temporary file: Obsidian (or Dropbox) never sees a half-written note.
        var temp = path + ".buddy-tmp";
        File.WriteAllText(temp, content.Replace("\n", newline), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}

/// <summary>The note changed since it was read: the edit is not applied.</summary>
public sealed class VaultConflictException(string note)
    : IOException($"La nota «{note}» è cambiata nel frattempo (in Obsidian?): ricaricala e riprova.");
