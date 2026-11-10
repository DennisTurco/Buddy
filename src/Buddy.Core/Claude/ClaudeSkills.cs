using System.Text.RegularExpressions;
using Buddy.Core.Vault;

namespace Buddy.Core.Claude;

/// <summary>
/// The Claude Code skills installed for the user (~/.claude/skills) or for the project Claude runs in
/// (&lt;folder&gt;/.claude/skills): the project board offers a skill's button only when the skill is there.
/// </summary>
public static partial class ClaudeSkills
{
    public static IReadOnlySet<string> Installed(BuddySettings settings, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[] { home, settings.Claude.WorkingDirectory, settings.Repo.Path }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.Combine(f, ".claude", "skills"))
            .Where(Directory.Exists);

        return folders
            .SelectMany(Directory.EnumerateDirectories)
            .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Installs in ~/.claude/skills the flow's skills that the user has nowhere yet. They ship inside Buddy as the
    /// template's "Skill/Skill &lt;name&gt;.md" notes (each note is the SKILL.md), or come from the personal template
    /// (<see cref="BuddySettings.FlowTemplatePath"/>). A skill already there, for the user or the project, is never
    /// overwritten. Returns the names installed.
    /// </summary>
    public static IReadOnlyList<string> InstallMissing(BuddySettings settings, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var installed = Installed(settings, home);
        var added = new List<string>();
        foreach (var (path, content) in FlowTemplate.Load(settings.FlowTemplatePath))
        {
            var file = SkillNote().Match(path);
            if (!file.Success)
            {
                continue;
            }

            var name = FrontmatterName().Match(content) is { Success: true } m ? m.Groups[1].Value.Trim() : file.Groups[1].Value;
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || installed.Contains(name))
            {
                continue;
            }

            var folder = Path.Combine(home, ".claude", "skills", name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), content);
            added.Add(name);
        }

        return added;
    }

    [GeneratedRegex(@"^Skill/Skill (.+)\.md$")]
    private static partial Regex SkillNote();

    [GeneratedRegex(@"\A---\r?\n(?:.*\r?\n)*?name:\s*(.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex FrontmatterName();
}
