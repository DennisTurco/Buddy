using Buddy.Core.Claude;

namespace Buddy.Core.Tests;

public class ClaudeSkillsInstallTests
{
    private string _home = "";

    [SetUp]
    public void SetUp() => _home = Directory.CreateTempSubdirectory("buddy-skills-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_home, true);

    [Test]
    public void Missing_skills_are_installed_from_the_built_in_template()
    {
        var added = ClaudeSkills.InstallMissing(new BuddySettings(), _home);

        Assert.That(added, Does.Contain("smista").And.Contain("lavora"));
        var skill = File.ReadAllText(Path.Combine(_home, ".claude", "skills", "smista", "SKILL.md"));
        Assert.That(skill, Does.StartWith("---").And.Contain("name: smista"));
        Assert.That(ClaudeSkills.Installed(new BuddySettings(), _home), Does.Contain("smista"));
    }

    [Test]
    public void Existing_skill_is_not_overwritten_and_second_run_adds_nothing()
    {
        var mine = Path.Combine(_home, ".claude", "skills", "smista", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "---\nname: smista\n---\nmia versione");

        var added = ClaudeSkills.InstallMissing(new BuddySettings(), _home);

        Assert.That(added, Does.Not.Contain("smista"));
        Assert.That(File.ReadAllText(mine), Does.EndWith("mia versione"));
        Assert.That(ClaudeSkills.InstallMissing(new BuddySettings(), _home), Is.Empty);
    }

    [Test]
    public void Skill_in_the_project_is_not_installed_for_the_user()
    {
        var repo = Path.Combine(_home, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".claude", "skills", "lavora"));
        File.WriteAllText(Path.Combine(repo, ".claude", "skills", "lavora", "SKILL.md"), "---\nname: lavora\n---");
        var settings = new BuddySettings();
        settings.Repo.Path = repo;

        ClaudeSkills.InstallMissing(settings, _home);

        Assert.That(Directory.Exists(Path.Combine(_home, ".claude", "skills", "lavora")), Is.False);
    }
}
