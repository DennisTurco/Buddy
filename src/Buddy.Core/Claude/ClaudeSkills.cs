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
        var skillsFolder = UserSkillsFolder(home);
        var manifest = TemplateManifest.Open(skillsFolder);
        foreach (var (path, content) in FlowTemplate.Load(settings.FlowTemplatePath))
        {
            if (SkillName(path, content) is not { } name || installed.Contains(name))
            {
                continue;
            }

            var folder = Path.Combine(skillsFolder, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), content);
            manifest.Record(name, content);
            added.Add(name);
        }

        manifest.Save();
        return added;
    }

    /// <summary>~/.claude/skills: where Buddy installs the flow's skills.</summary>
    internal static string UserSkillsFolder(string home) => Path.Combine(home, ".claude", "skills");

    /// <summary>
    /// The skill a template file installs ("Skill/Skill &lt;name&gt;.md", or the "name:" of its frontmatter); null for
    /// any other file or an unusable name.
    /// </summary>
    internal static string? SkillName(string templatePath, string content)
    {
        var file = SkillNote().Match(templatePath);
        if (!file.Success)
        {
            return null;
        }

        var name = FrontmatterName().Match(content) is { Success: true } m ? m.Groups[1].Value.Trim() : file.Groups[1].Value;
        return name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : name;
    }

    /// <summary>
    /// What each installed skill does: the first sentence of the "description:" in its SKILL.md (empty when missing).
    /// The user's own skill wins over the project's with the same name, as in <see cref="Installed"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Descriptions(BuddySettings settings, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { home, settings.Claude.WorkingDirectory, settings.Repo.Path }
                     .Where(f => !string.IsNullOrWhiteSpace(f))
                     .Select(f => Path.Combine(f, ".claude", "skills"))
                     .Where(Directory.Exists))
        {
            foreach (var skill in Directory.EnumerateDirectories(folder))
            {
                var file = Path.Combine(skill, "SKILL.md");
                if (Path.GetFileName(skill) is { } name && !result.ContainsKey(name) && File.Exists(file))
                {
                    result[name] = FirstSentence(FrontmatterDescription().Match(File.ReadAllText(file)) is { Success: true } m
                        ? m.Groups[1].Value.Trim().Trim('"', '\'')
                        : "");
                }
            }
        }

        return result;
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? text[..end] : text.TrimEnd('.');
    }

    [GeneratedRegex(@"\A---\r?\n(?:.*\r?\n)*?description:\s*(.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex FrontmatterDescription();

    [GeneratedRegex(@"^Skill/Skill (.+)\.md$")]
    private static partial Regex SkillNote();

    [GeneratedRegex(@"\A---\r?\n(?:.*\r?\n)*?name:\s*(.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex FrontmatterName();
}
