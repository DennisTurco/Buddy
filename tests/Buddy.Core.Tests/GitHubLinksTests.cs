using Buddy.Core.Repo;

namespace Buddy.Core.Tests;

public class GitHubLinksTests
{
    [TestCase("https://github.com/DennisTurco/Buddy.git", "DennisTurco", "Buddy")]
    [TestCase("https://github.com/DennisTurco/Buddy", "DennisTurco", "Buddy")]
    [TestCase("git@github.com:org/my.repo.git", "org", "my.repo")]
    [TestCase("ssh://git@github.com/org/repo/", "org", "repo")]
    public void The_GitHub_repository_comes_from_the_remote(string remote, string owner, string name)
    {
        Assert.That(GitHubLinks.FromRemote(remote), Is.EqualTo(new GitHubRepo(owner, name)));
    }

    [TestCase("https://dev.azure.com/org/project/_git/repo")]
    [TestCase("")]
    [TestCase(null)]
    public void Other_remotes_are_not_GitHub(string? remote)
    {
        Assert.That(GitHubLinks.FromRemote(remote), Is.Null);
    }

    [Test]
    public void A_bug_report_on_Buddy_opens_the_new_issue_page_with_template_version_and_os()
    {
        var url = GitHubLinks.NewIssueUrl(GitHubLinks.Buddy, IssueKind.Bug, "1.0.0", "Ubuntu 26.04");
        Assert.That(url, Does.StartWith("https://github.com/DennisTurco/Buddy/issues/new?labels=bug&title=%5BBug%5D%20&body="));
        var body = Uri.UnescapeDataString(url[(url.IndexOf("body=", StringComparison.Ordinal) + 5)..]);
        Assert.That(body, Does.Contain("## Come riprodurlo").And.Contain("- Buddy 1.0.0").And.Contain("- Ubuntu 26.04"));
    }

    [Test]
    public void A_feature_request_on_the_project_has_no_environment()
    {
        var url = GitHubLinks.NewIssueUrl(new GitHubRepo("org", "repo"), IssueKind.Feature);
        Assert.That(url, Does.Contain("labels=enhancement"));
        Assert.That(Uri.UnescapeDataString(url), Does.Contain("## Cosa vorresti").And.Not.Contain("Ambiente"));
    }
}
