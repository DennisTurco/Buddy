namespace Buddy.Core.Claude;

/// <summary>
/// A Claude Code skill from someone else that Buddy suggests but does not ship: it is installed for the user
/// (~/.claude/skills) with the Skills CLI (<c>npx skills add</c>), so it keeps all its files and can be updated
/// from its source with <c>npx skills update</c>.
/// </summary>
/// <param name="Name">The skill's folder and command name (e.g. "security-audit" → /security-audit).</param>
/// <param name="Description">What it is for, in a sentence, shown in Impostazioni → Comandi.</param>
/// <param name="Source">The repository it is installed from (also the "more info" link).</param>
/// <param name="Author">Who maintains it.</param>
public sealed record RecommendedSkill(string Name, string Description, string Source, string Author)
{
    /// <summary>The npx arguments that install it for the user, for Claude Code only, without questions.</summary>
    public IReadOnlyList<string> InstallArguments =>
        ["--yes", "skills", "add", Source, "--skill", Name, "--agent", "claude-code", "--copy", "--global", "--yes"];

    /// <summary>The same command as one line, to show or copy.</summary>
    public string InstallCommand => "npx " + string.Join(' ', InstallArguments);
}

public static class RecommendedSkills
{
    public static IReadOnlyList<RecommendedSkill> All { get; } =
    [
        new("security-audit",
            "Cerca falle di sicurezza nel codice del progetto: domande e revisioni mirate, o un audit completo con report. Serve Node.js.",
            "https://github.com/cloudflare/security-audit-skill", "Cloudflare"),
    ];

    /// <summary>Whether <paramref name="skill"/> is already there, for the user or for the project (see <see cref="ClaudeSkills.Installed"/>).</summary>
    public static bool IsInstalled(RecommendedSkill skill, BuddySettings settings, string? home = null) =>
        ClaudeSkills.Installed(settings, home).Contains(skill.Name);
}
