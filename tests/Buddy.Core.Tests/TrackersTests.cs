using System.Net;
using System.Text;
using System.Text.Json;
using Buddy.Core.Repo;
using Buddy.Core.Tracker;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class TrackersTests
{
    /// <summary>Answers by URL fragment, records the requests.</summary>
    private sealed class FakeAzure(params (string UrlPart, string Body)[] answers) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var answer = answers.FirstOrDefault(a => request.RequestUri!.ToString().Contains(a.UrlPart, StringComparison.Ordinal));
            return answer.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") };
        }
    }

    [Test]
    public void GitHub_issues_become_GH_cards_with_priority_and_type_from_labels()
    {
        const string json = """
            [{"number":12,"title":"Crash all'avvio","body":"Si chiude subito","url":"https://github.com/o/r/issues/12",
              "author":{"login":"anna"},"labels":[{"name":"bug"},{"name":"priority: high"}],"milestone":{"dueOn":"2026-12-01T00:00:00Z"}},
             {"number":13,"title":"Tema scuro","body":"","url":"https://github.com/o/r/issues/13","author":{"login":"bob"},"labels":[],"milestone":null}]
            """;
        var issues = GitHubIssues.Parse(json);

        Assert.That(issues.Select(i => i.Key), Is.EqualTo(new[] { "GH-12", "GH-13" }));
        Assert.That(issues[0].Priority, Is.EqualTo("P1"));
        Assert.That(issues[0].Type, Is.EqualTo("bug"));
        Assert.That(issues[0].Reporter, Is.EqualTo("anna"));
        Assert.That(issues[0].Due, Is.EqualTo(new DateOnly(2026, 12, 1)));
        Assert.That(TrackerImport.ToTask(issues[0], new DateOnly(2026, 11, 6)), Does.StartWith("- [ ] GH-12 · 2026-11-06 · Crash all'avvio [priorita:: P1] [tag:: ticket, bug]"));
        Assert.That(issues[1].Priority, Is.Null);
    }

    [TestCase("https://dev.azure.com/acme/Portale%20Web/_git/portale", "https://dev.azure.com/acme", "Portale Web", "portale")]
    [TestCase("https://dennis@dev.azure.com/acme/Portale/_git/api", "https://dev.azure.com/acme", "Portale", "api")]
    [TestCase("https://acme.visualstudio.com/DefaultCollection/Portale/_git/api", "https://acme.visualstudio.com", "Portale", "api")]
    [TestCase("git@ssh.dev.azure.com:v3/acme/Portale/api", "https://dev.azure.com/acme", "Portale", "api")]
    public void The_Azure_project_comes_from_the_remote(string remote, string baseUrl, string project, string repo)
    {
        Assert.That(AzureProject.FromRemote(remote), Is.EqualTo(new AzureProject(baseUrl, project, repo)));
    }

    [Test]
    public void A_GitHub_remote_is_not_Azure_and_settings_can_name_the_project()
    {
        Assert.That(AzureProject.FromRemote("https://github.com/o/r.git"), Is.Null);
        Assert.That(AzureProject.FromSettings(new AzureDevOpsSettings { Organization = "acme" }, "Portale"),
            Is.EqualTo(new AzureProject("https://dev.azure.com/acme", "Portale")));
        Assert.That(AzureProject.FromSettings(new AzureDevOpsSettings(), "Portale"), Is.Null);
    }

    [Test]
    public async Task Azure_work_items_assigned_to_me_are_read_with_WIQL_and_the_PAT()
    {
        var fake = new FakeAzure(
            ("/_apis/wit/wiql", """{"workItems":[{"id":42},{"id":7}]}"""),
            ("/_apis/wit/workitems", """
                {"value":[
                  {"id":7,"fields":{"System.Title":"Login lento","System.WorkItemType":"Task","System.State":"Active","Microsoft.VSTS.Common.Priority":3,
                                    "System.CreatedBy":{"displayName":"Persona B"}}},
                  {"id":42,"fields":{"System.Title":"Errore export","System.WorkItemType":"Bug","System.State":"New","Microsoft.VSTS.Common.Priority":1,
                                     "System.Description":"<div>Il file &egrave; vuoto.<br>Con pi&ugrave; corsi.</div>","Microsoft.VSTS.Scheduling.DueDate":"2026-11-30T00:00:00Z"}}]}
                """));
        var project = new AzureProject("https://dev.azure.com/acme", "Portale Web", "api");

        var items = await new AzureDevOpsClient(new HttpClient(fake), "pat").AssignedAsync(project);

        var (wiql, body) = fake.Requests[0];
        Assert.That(wiql.RequestUri!.AbsoluteUri, Does.StartWith("https://dev.azure.com/acme/Portale%20Web/_apis/wit/wiql"));
        Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(wiql.Headers.Authorization!.Parameter!)), Is.EqualTo(":pat"));
        Assert.That(JsonDocument.Parse(body!).RootElement.GetProperty("query").GetString(), Does.Contain("[System.AssignedTo] = @Me"));
        Assert.That(fake.Requests[1].Request.RequestUri!.Query, Does.Contain("ids=42,7"));

        Assert.That(items.Select(i => i.Key), Is.EqualTo(new[] { "AB-42", "AB-7" }), "in the order of the query (priority)");
        Assert.That(items[0].Description, Is.EqualTo("Il file è vuoto.\nCon più corsi."));
        Assert.That(items[0].Url, Is.EqualTo("https://dev.azure.com/acme/Portale%20Web/_workitems/edit/42"));
        Assert.That(TrackerImport.Priority(items[0].Priority), Is.EqualTo("P1"));
        Assert.That(TrackerImport.Priority(items[1].Priority), Is.EqualTo("P3"));
        Assert.That(items[1].Reporter, Is.EqualTo("Persona B"));
    }

    [Test]
    public async Task Azure_pull_requests_carry_review_and_the_build_policies_as_CI()
    {
        var fake = new FakeAzure(
            ("/_apis/connectionData", """{"authenticatedUser":{"id":"me-1"}}"""),
            ("/pullrequests", """
                {"value":[{"pullRequestId":5,"title":"Export voti","sourceRefName":"refs/heads/feature/AB-42-export","isDraft":false,
                           "creationDate":"2026-11-05T10:00:00Z","repository":{"project":{"id":"p-1"}},"reviewers":[{"vote":10}]}]}
                """),
            ("/_apis/policy/evaluations", """
                {"value":[{"status":"approved","configuration":{"type":{"displayName":"Minimum number of reviewers"}}},
                          {"status":"rejected","configuration":{"type":{"displayName":"Build"}}}]}
                """));
        var project = new AzureProject("https://dev.azure.com/acme", "Portale", "api");

        var prs = await new AzureDevOpsClient(new HttpClient(fake), "pat").PullRequestsAsync(project, @"\b[A-Z]{1,10}-\d+\b");

        Assert.That(fake.Requests[1].Request.RequestUri!.Query, Does.Contain("searchCriteria.creatorId=me-1"));
        var pr = prs.Single();
        Assert.That(pr.TaskId, Is.EqualTo("AB-42"));
        Assert.That(pr.Checks, Is.EqualTo("failure"));
        Assert.That(pr.Review, Is.EqualTo("APPROVED"));
        Assert.That(pr.Url, Is.EqualTo("https://dev.azure.com/acme/Portale/_git/api/pullrequest/5"));
    }

    [Test]
    public void Build_policies_sum_up_like_GitHub_checks()
    {
        static string Checks(params string[] statuses) => AzureDevOpsClient.BuildChecks(JsonDocument.Parse(
            "{\"value\":[" + string.Join(',', statuses.Select(s => $"{{\"status\":\"{s}\",\"configuration\":{{\"type\":{{\"displayName\":\"Build\"}}}}}}")) + "]}").RootElement);
        Assert.That(Checks("approved", "approved"), Is.EqualTo("success"));
        Assert.That(Checks("approved", "running"), Is.EqualTo("pending"));
        Assert.That(Checks("queued", "rejected"), Is.EqualTo("failure"));
        Assert.That(Checks(), Is.EqualTo(""));
    }

    [Test]
    public void A_signin_page_instead_of_JSON_means_a_bad_token()
    {
        var fake = new SigninPage();
        var ex = Assert.ThrowsAsync<TrackerException>(() =>
            new AzureDevOpsClient(new HttpClient(fake), "x").AssignedAsync(new AzureProject("https://dev.azure.com/a", "P")));
        Assert.That(ex!.Message, Does.Contain("token"));
    }

    private sealed class SigninPage : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation) { Content = new StringContent("<html>Sign in</html>") });
    }

    [Test]
    public async Task The_trackers_of_a_project_follow_its_remote_and_tokens()
    {
        var root = Directory.CreateTempSubdirectory("buddy-trackers-").FullName;
        try
        {
            var repoPath = Path.Combine(root, "repo");
            Directory.CreateDirectory(repoPath);
            void Git(params string[] args)
            {
                var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repoPath, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) start.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(start)!;
                p.WaitForExit();
            }

            Git("init", "-q");
            Git("remote", "add", "origin", "https://dev.azure.com/acme/Portale/_git/api");
            var secrets = Path.Combine(root, "secrets");
            var settings = new BuddySettings { Repo = { Path = repoPath } };
            var repo = new GitRepo(Options.Create(settings), TimeProvider.System);
            using var http = new HttpClient();

            Assert.That(await ProjectTrackers.AvailableAsync(settings, secrets, http, repo), Is.Empty, "Azure remote but no token yet");

            AzureToken.Write(secrets, "pat");
            JiraToken.Write(secrets, "tok");
            settings.Jira.BaseUrl = "https://a.atlassian.net";
            var names = (await ProjectTrackers.AvailableAsync(settings, secrets, http, repo)).Select(t => t.Name).ToList();
            Assert.That(names, Is.EqualTo(new[] { "Jira", "Azure Boards (Portale)" }));

            Git("remote", "set-url", "origin", "git@github.com:o/r.git");
            Assert.That((await ProjectTrackers.AvailableAsync(settings, secrets, http, repo)).Select(t => t.Name), Is.EqualTo(new[] { "Jira", "GitHub (issue)" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
