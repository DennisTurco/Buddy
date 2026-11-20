using Buddy.Core.Claude;

namespace Buddy.Core.Tests;

public class RecommendedSkillsTests
{
    [Test]
    public void Install_is_global_for_claude_code_copied_and_without_questions()
    {
        var skill = RecommendedSkills.All.Single(s => s.Name == "security-audit");

        Assert.That(skill.InstallArguments, Is.EqualTo(new[]
        {
            "--yes", "skills", "add", "https://github.com/cloudflare/security-audit-skill", "--skill", "security-audit",
            "--agent", "claude-code", "--copy", "--global", "--yes",
        }));
        Assert.That(skill.InstallCommand, Does.StartWith("npx --yes skills add https://github.com/cloudflare/"));
    }

    [Test]
    public void Installed_when_the_skill_folder_is_in_the_users_skills()
    {
        var home = Directory.CreateTempSubdirectory("buddy-recommended-").FullName;
        try
        {
            var skill = RecommendedSkills.All[0];
            Assert.That(RecommendedSkills.IsInstalled(skill, new BuddySettings(), home), Is.False);

            var folder = Path.Combine(home, ".claude", "skills", skill.Name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), "---\nname: " + skill.Name + "\n---\n");

            Assert.That(RecommendedSkills.IsInstalled(skill, new BuddySettings(), home), Is.True);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }
}
