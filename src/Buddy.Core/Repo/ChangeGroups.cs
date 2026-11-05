using System.Text;

namespace Buddy.Core.Repo;

/// <summary>Groups changed files by area (e.g. stack) using "Name=prefix1;prefix2" rules, for /modifiche.</summary>
public static class ChangeGroups
{
    public const string Other = "Altro";

    public static IReadOnlyList<(string Group, IReadOnlyList<ChangedFile> Files)> Group(
        IEnumerable<ChangedFile> files, IEnumerable<string> rules)
    {
        var parsed = rules
            .Select(r => r.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
            .Select(p => (Name: p[0].Trim(), Prefixes: p[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize).ToArray()))
            .ToList();

        string GroupOf(ChangedFile file)
        {
            var path = Normalize(file.Path);
            return parsed.FirstOrDefault(g => g.Prefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))).Name ?? Other;
        }

        var order = parsed.Select(g => g.Name).Append(Other).ToList();
        return files
            .GroupBy(GroupOf)
            .OrderBy(g => order.IndexOf(g.Key))
            .Select(g => (g.Key, (IReadOnlyList<ChangedFile>)g.OrderBy(f => f.Path).ToList()))
            .ToList();
    }

    public static string ToMarkdown(IReadOnlyList<(string Group, IReadOnlyList<ChangedFile> Files)> groups, string? branch)
    {
        if (groups.Count == 0)
        {
            return "Nessuna modifica rispetto all'ultimo commit.";
        }

        var total = groups.Sum(g => g.Files.Count);
        var sb = new StringBuilder($"**{total} file modificati**");
        if (branch is not null)
        {
            sb.Append($" sul ramo `{branch}`");
        }

        sb.AppendLine();
        foreach (var (group, files) in groups)
        {
            sb.AppendLine().AppendLine($"**{group}** ({files.Count})");
            foreach (var file in files)
            {
                sb.AppendLine($"- {file.Status}: `{file.Path}`");
            }
        }

        return sb.ToString();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
