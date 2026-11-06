using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>A note in the graph; a missing one is a link target with no file yet (Obsidian's "unresolved" link).</summary>
public sealed record NoteGraphNode(string Id, string Title, string Folder, bool IsMissing)
{
    /// <summary>The task the note is about (in its title, or the id it mentions most), for colouring by task.</summary>
    public string? TaskId { get; internal set; }

    public int Degree { get; internal set; }
}

public sealed record NoteGraphEdge(string From, string To);

/// <summary>
/// The links between the notes of the vault, as Obsidian's graph shows them: wiki links ([[Note]], [[Folder/Note|alias]],
/// [[Note#heading]]) and markdown links to .md files. Links are undirected and counted once per pair; embeds of
/// images and other files are left out.
/// </summary>
public sealed partial class NoteGraph
{
    private NoteGraph(IReadOnlyList<NoteGraphNode> nodes, IReadOnlyList<NoteGraphEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public IReadOnlyList<NoteGraphNode> Nodes { get; }

    public IReadOnlyList<NoteGraphEdge> Edges { get; }

    public static NoteGraph Build(IReadOnlyList<VaultNote> notes, string? taskIdPattern = null)
    {
        var taskId = string.IsNullOrWhiteSpace(taskIdPattern) ? null : new Regex(taskIdPattern);
        var nodes = new Dictionary<string, NoteGraphNode>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            var id = Normalize(note.RelativePath);
            var folder = id.Contains('/') ? id[..id.IndexOf('/')] : "";
            nodes[id] = new NoteGraphNode(id, note.Title, folder, IsMissing: false) { TaskId = taskId is null ? null : TaskOf(note, taskId) };
            byPath[StripExtension(id)] = id;

            // With two notes of the same name the shorter path wins, as in Obsidian.
            if (!byTitle.TryGetValue(note.Title, out var existing) || existing.Length > id.Length)
            {
                byTitle[note.Title] = id;
            }
        }

        var edges = new HashSet<(string, string)>();
        foreach (var note in notes)
        {
            var from = Normalize(note.RelativePath);
            var folder = from.Contains('/') ? from[..from.LastIndexOf('/')] : "";
            foreach (var target in Targets(note.Content))
            {
                var to = Resolve(target, folder, byPath, byTitle);
                if (to is null)
                {
                    var title = Path.GetFileName(target);
                    if (title.Length == 0)
                    {
                        continue;
                    }

                    to = "?" + title;
                    nodes.TryAdd(to, new NoteGraphNode(to, title, "", IsMissing: true));
                }

                if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pair = string.Compare(from, to, StringComparison.OrdinalIgnoreCase) < 0 ? (from, to) : (to, from);
                edges.Add((pair.Item1.ToLowerInvariant(), pair.Item2.ToLowerInvariant()));
            }
        }

        var lookup = nodes.Values.ToDictionary(n => n.Id.ToLowerInvariant());
        var edgeList = new List<NoteGraphEdge>();
        foreach (var (a, b) in edges)
        {
            var na = lookup[a];
            var nb = lookup[b];
            na.Degree++;
            nb.Degree++;
            edgeList.Add(new NoteGraphEdge(na.Id, nb.Id));
        }

        return new NoteGraph(nodes.Values.OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToList(), edgeList);
    }

    /// <summary>
    /// The task a note belongs to: the id in its title (or path), else the only id it mentions. Notes about several
    /// tasks (the flow notes, the index) belong to none.
    /// </summary>
    private static string? TaskOf(VaultNote note, Regex taskId)
    {
        if (taskId.Match(note.RelativePath) is { Success: true } inName)
        {
            return inName.Value.ToUpperInvariant();
        }

        var mentioned = taskId.Matches(note.Content).Select(m => m.Value.ToUpperInvariant()).Distinct().Take(2).ToList();
        return mentioned.Count == 1 ? mentioned[0] : null;
    }

    /// <summary>Link targets in a note, without heading, alias or extension (".md" only: other files are not notes).</summary>
    internal static IEnumerable<string> Targets(string content)
    {
        // Examples in code blocks, `inline code` and <!-- comments --> (templates, "Formato degli script") are not links.
        content = NotLinks().Replace(content, "");

        foreach (Match match in WikiLink().Matches(content))
        {
            if (match.Groups["embed"].Success && HasNonNoteExtension(match.Groups["target"].Value))
            {
                continue;
            }

            var target = match.Groups["target"].Value.Trim();
            if (target.Length > 0 && !HasNonNoteExtension(target))
            {
                yield return StripExtension(Normalize(target));
            }
        }

        foreach (Match match in MarkdownLink().Matches(content))
        {
            var target = Uri.UnescapeDataString(match.Groups["target"].Value.Trim());
            if (!target.Contains("://", StringComparison.Ordinal))
            {
                yield return StripExtension(Normalize(target));
            }
        }
    }

    private static string? Resolve(string target, string fromFolder, Dictionary<string, string> byPath, Dictionary<string, string> byTitle)
    {
        if (byPath.TryGetValue(target.TrimStart('/'), out var exact))
        {
            return exact;
        }

        // A relative markdown link ("../Analisi/Nota.md").
        if (fromFolder.Length > 0 || target.StartsWith('.'))
        {
            var combined = Path.GetFullPath(Path.Combine("/vault", fromFolder, target)).Replace('\\', '/');
            var index = combined.IndexOf("/vault/", StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && byPath.TryGetValue(combined[(index + 7)..], out var relative))
            {
                return relative;
            }
        }

        return byTitle.TryGetValue(Path.GetFileName(target), out var byName) ? byName : null;
    }

    private static bool HasNonNoteExtension(string target)
    {
        var extension = Path.GetExtension(target.Split('#', '|')[0].Trim());
        return extension.Length is > 1 and <= 5 && !extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
               && extension.Skip(1).All(char.IsLetterOrDigit);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();

    private static string StripExtension(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

    [GeneratedRegex(@"<!--[\s\S]*?(?:-->|$)|^[ \t>]*(```|~~~)[\s\S]*?(?:^[ \t>]*\1|\z)|`[^`\n]*`", RegexOptions.Multiline)]
    private static partial Regex NotLinks();

    [GeneratedRegex(@"(?<embed>!)?\[\[(?<target>[^\]\|#\n]*)(?:#[^\]\|\n]*)?(?:\|[^\]\n]*)?\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"(?<!!)\[[^\]\n]*\]\((?<target>[^)\s#]+\.md)(?:#[^)\s]*)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLink();
}
