namespace Buddy.Core.Claude;

/// <summary>
/// The Claude Code skills installed for the user (~/.claude/skills) or for the project Claude runs in
/// (&lt;folder&gt;/.claude/skills): the project board offers a skill's button only when the skill is there.
/// </summary>
public static class ClaudeSkills
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
}
