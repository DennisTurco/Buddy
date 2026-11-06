using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Buddy.Core.Tracker;
using Buddy.Core.Vault;

namespace Buddy.Core.Tests;

public class JiraTests
{
    private sealed class FakeJira(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private const string CloudAnswer = """
        {"issues":[{"key":"ABC-12","fields":{
          "summary":"Export CSV dei voti","priority":{"name":"High"},"issuetype":{"name":"Bug"},"status":{"name":"To Do"},
          "reporter":{"displayName":"Persona A"},"duedate":"2026-11-20",
          "description":{"type":"doc","content":[
            {"type":"paragraph","content":[{"type":"text","text":"Il file "},{"type":"text","text":"è vuoto."}]},
            {"type":"paragraph","content":[{"type":"text","text":"Succede con più corsi."}]}]}}}]}
        """;

    [Test]
    public async Task Jira_Cloud_is_asked_with_email_and_token_for_my_open_cards_of_the_project()
    {
        var fake = new FakeJira(HttpStatusCode.OK, CloudAnswer);
        var settings = new JiraSettings { BaseUrl = "https://azienda.atlassian.net/", Email = "me@x.it" };

        var issues = await new JiraClient(new HttpClient(fake)).AssignedAsync(settings, "tok", "ABC");

        Assert.That(fake.Request!.RequestUri!.ToString(), Is.EqualTo("https://azienda.atlassian.net/rest/api/3/search/jql"));
        Assert.That(fake.Request.Headers.Authorization!.Scheme, Is.EqualTo("Basic"));
        Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(fake.Request.Headers.Authorization.Parameter!)), Is.EqualTo("me@x.it:tok"));
        var jql = JsonDocument.Parse(fake.RequestBody!).RootElement.GetProperty("jql").GetString();
        Assert.That(jql, Is.EqualTo("assignee = currentUser() AND statusCategory != Done AND project = \"ABC\" ORDER BY priority DESC, updated DESC"));

        var issue = issues.Single();
        Assert.That(issue.Key, Is.EqualTo("ABC-12"));
        Assert.That(issue.Description, Is.EqualTo("Il file è vuoto.\nSuccede con più corsi."));
        Assert.That(issue.Url, Is.EqualTo("https://azienda.atlassian.net/browse/ABC-12"));
        Assert.That(issue.Due, Is.EqualTo(new DateOnly(2026, 11, 20)));
    }

    [Test]
    public async Task Jira_Server_uses_a_personal_access_token_and_api_2()
    {
        var fake = new FakeJira(HttpStatusCode.OK, """{"issues":[{"key":"OPS-1","fields":{"summary":"Backup","description":"testo semplice"}}]}""");
        var settings = new JiraSettings { BaseUrl = "https://jira.azienda.it" };

        var issues = await new JiraClient(new HttpClient(fake)).AssignedAsync(settings, "pat", null);

        Assert.That(fake.Request!.Method, Is.EqualTo(HttpMethod.Get));
        Assert.That(fake.Request.RequestUri!.AbsolutePath, Is.EqualTo("/rest/api/2/search"));
        Assert.That(fake.Request.Headers.Authorization!.ToString(), Is.EqualTo("Bearer pat"));
        Assert.That(issues.Single().Description, Is.EqualTo("testo semplice"));
    }

    [Test]
    public void A_refused_token_says_where_to_fix_it()
    {
        var fake = new FakeJira(HttpStatusCode.Unauthorized, "");
        var ex = Assert.ThrowsAsync<JiraException>(() =>
            new JiraClient(new HttpClient(fake)).AssignedAsync(new JiraSettings { BaseUrl = "https://a.atlassian.net" }, "x", null));
        Assert.That(ex!.Message, Does.Contain("Impostazioni → Jira"));
    }

    [Test]
    public void A_card_becomes_a_ticket_in_To_Do_and_cards_already_in_the_vault_are_skipped()
    {
        var issue = JiraClient.Parse(JsonDocument.Parse(CloudAnswer).RootElement, "https://azienda.atlassian.net").Single();
        var line = TrackerImport.ToTask(issue, new DateOnly(2026, 11, 6));
        Assert.That(line, Does.StartWith("- [ ] ABC-12 · 2026-11-06 · Export CSV dei voti [priorita:: P1] [tag:: ticket, bug]"));
        Assert.That(line, Does.Contain("  - Contesto: [ABC-12](https://azienda.atlassian.net/browse/ABC-12) — Il file è vuoto. Succede con più corsi."));
        Assert.That(line, Does.Contain("  - Fatto quando: da definire"));
        Assert.That(line, Does.Contain("  - Assegnato da: Persona A"));

        var item = ProjectBoard.ParseItems(line, new Regex(@"^(?:[A-Z]{1,10}-\d+)$")).Single();
        var board = new ProjectBoard([new BoardColumn("todo", "Da fare", "", "To-Do.md", [item])], [], []);
        var other = issue with { Key = "ABC-13" };
        Assert.That(TrackerImport.New([issue, other], board).Select(i => i.Key), Is.EqualTo(new[] { "ABC-13" }));
    }

    [TestCase("Highest", "P1")]
    [TestCase("Medium", "P2")]
    [TestCase("Bassa", "P3")]
    [TestCase(null, "P2")]
    public void Priorities_map_to_P1_P3(string? name, string expected) => Assert.That(TrackerImport.Priority(name), Is.EqualTo(expected));

    [Test]
    public void The_token_file_is_private()
    {
        var folder = Directory.CreateTempSubdirectory("buddy-jira-").FullName;
        try
        {
            JiraToken.Write(folder, " segreto ");
            Assert.That(JiraToken.Read(folder), Is.EqualTo("segreto"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.That(File.GetUnixFileMode(Path.Combine(folder, JiraToken.FileName)), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }

            JiraToken.Write(folder, "");
            Assert.That(JiraToken.Read(folder), Is.Null);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
