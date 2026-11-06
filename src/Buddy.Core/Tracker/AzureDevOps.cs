using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Buddy.Core.Repo;

namespace Buddy.Core.Tracker;

/// <summary>An Azure DevOps project (and, from a git remote, its repository).</summary>
/// <param name="BaseUrl">https://dev.azure.com/org, or https://org.visualstudio.com for old organizations.</param>
public sealed partial record AzureProject(string BaseUrl, string Project, string? Repository = null)
{
    private string Escaped(string part) => Uri.EscapeDataString(part);

    public string ProjectUrl => $"{BaseUrl}/{Escaped(Project)}";

    public string? RepositoryUrl => Repository is null ? null : $"{ProjectUrl}/_git/{Escaped(Repository)}";

    /// <summary>The project of an Azure Repos remote (https, with or without user, old visualstudio.com, ssh v3); null otherwise.</summary>
    public static AzureProject? FromRemote(string? remote)
    {
        var text = remote?.Trim() ?? "";
        if (HttpsRegex().Match(text) is { Success: true } https)
        {
            return new AzureProject($"https://dev.azure.com/{https.Groups["org"].Value}", Unescape(https.Groups["project"].Value), Unescape(https.Groups["repo"].Value));
        }

        if (OldRegex().Match(text) is { Success: true } old)
        {
            return new AzureProject($"https://{old.Groups["org"].Value}.visualstudio.com", Unescape(old.Groups["project"].Value), Unescape(old.Groups["repo"].Value));
        }

        return SshRegex().Match(text) is { Success: true } ssh
            ? new AzureProject($"https://dev.azure.com/{ssh.Groups["org"].Value}", Unescape(ssh.Groups["project"].Value), Unescape(ssh.Groups["repo"].Value))
            : null;
    }

    /// <summary>The project from the settings (organization address and project name); null when they are not filled in.</summary>
    public static AzureProject? FromSettings(AzureDevOpsSettings settings, string? project) =>
        settings.Organization.Trim().TrimEnd('/') is { Length: > 0 } organization && project?.Trim() is { Length: > 0 } name
            ? new AzureProject(organization.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? organization : $"https://dev.azure.com/{organization}", name)
            : null;

    private static string Unescape(string part) => Uri.UnescapeDataString(part).TrimEnd('/');

    [GeneratedRegex(@"^https://(?:[^@/]+@)?dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsRegex();

    [GeneratedRegex(@"^https://(?<org>[^./]+)\.visualstudio\.com/(?:DefaultCollection/)?(?<project>[^/]+)/_git/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex OldRegex();

    [GeneratedRegex(@"^(?:ssh://)?[^@]+@(?:ssh\.dev\.azure\.com|vs-ssh\.visualstudio\.com):(?:22/)?v3/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SshRegex();
}

/// <summary>
/// Azure DevOps through its REST API with a personal access token (scopes: Work Items read, Code read): the work items
/// assigned to the user and their open pull requests with the result of the build policies (the CI).
/// </summary>
public sealed class AzureDevOpsClient(HttpClient http, string token)
{
    public const string Prefix = "AB-";
    private const string Api = "api-version=7.1";

    private static readonly string[] Fields =
    [
        "System.Title", "System.Description", "System.WorkItemType", "System.State", "Microsoft.VSTS.Common.Priority",
        "System.CreatedBy", "Microsoft.VSTS.Scheduling.DueDate",
    ];

    /// <summary>Work items assigned to me in the project and not closed, most important first.</summary>
    public async Task<IReadOnlyList<TrackerIssue>> AssignedAsync(AzureProject project, CancellationToken cancellationToken = default)
    {
        const string wiql = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.AssignedTo] = @Me "
                            + "AND [System.State] NOT IN ('Closed', 'Done', 'Removed', 'Resolved', 'Completed', 'Chiuso', 'Fatto') "
                            + "ORDER BY [Microsoft.VSTS.Common.Priority] ASC, [System.ChangedDate] DESC";
        using var query = await SendAsync(HttpMethod.Post, $"{project.ProjectUrl}/_apis/wit/wiql?$top=50&{Api}",
            new JsonObject { ["query"] = wiql }, cancellationToken);
        var ids = query.RootElement.TryGetProperty("workItems", out var items)
            ? items.EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).Take(50).ToList()
            : [];
        if (ids.Count == 0)
        {
            return [];
        }

        using var details = await SendAsync(HttpMethod.Get,
            $"{project.ProjectUrl}/_apis/wit/workitems?ids={string.Join(',', ids)}&fields={string.Join(',', Fields)}&{Api}", null, cancellationToken);
        return ParseWorkItems(details.RootElement, project).OrderBy(i => ids.IndexOf(int.Parse(i.Key[Prefix.Length..], System.Globalization.CultureInfo.InvariantCulture))).ToList();
    }

    public static IReadOnlyList<TrackerIssue> ParseWorkItems(JsonElement root, AzureProject project)
    {
        if (!root.TryGetProperty("value", out var items))
        {
            return [];
        }

        static string? Text(JsonElement fields, string name) =>
            fields.TryGetProperty(name, out var value) ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.Object when value.TryGetProperty("displayName", out var display) => display.GetString(),
                _ => null,
            } : null;

        return items.EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetInt32();
            var fields = item.GetProperty("fields");
            var due = Text(fields, "Microsoft.VSTS.Scheduling.DueDate") is { } d && DateTimeOffset.TryParse(d, out var date)
                ? DateOnly.FromDateTime(date.UtcDateTime)
                : (DateOnly?)null;
            return new TrackerIssue(
                Prefix + id,
                Text(fields, "System.Title") ?? "",
                Html(Text(fields, "System.Description") ?? ""),
                Text(fields, "Microsoft.VSTS.Common.Priority"),
                Text(fields, "System.WorkItemType"),
                Text(fields, "System.State"),
                Text(fields, "System.CreatedBy"),
                due,
                $"{project.ProjectUrl}/_workitems/edit/{id}");
        }).ToList();
    }

    /// <summary>My open pull requests in the repository, with review and CI (build policies) summed up like GitHub's.</summary>
    public async Task<IReadOnlyList<PullRequestInfo>> PullRequestsAsync(AzureProject project, string taskIdPattern, CancellationToken cancellationToken = default)
    {
        if (project.RepositoryUrl is null)
        {
            return [];
        }

        var organization = project.BaseUrl;
        using var connection = await SendAsync(HttpMethod.Get, $"{organization}/_apis/connectionData", null, cancellationToken);
        var me = connection.RootElement.GetProperty("authenticatedUser").GetProperty("id").GetString();

        using var list = await SendAsync(HttpMethod.Get,
            $"{project.ProjectUrl}/_apis/git/repositories/{Uri.EscapeDataString(project.Repository!)}/pullrequests"
            + $"?searchCriteria.creatorId={me}&searchCriteria.status=active&$top=30&{Api}", null, cancellationToken);
        var prs = list.RootElement.GetProperty("value").EnumerateArray().Select(pr => pr.Clone()).ToList();

        var result = new List<PullRequestInfo>();
        foreach (var pr in prs)
        {
            var number = pr.GetProperty("pullRequestId").GetInt32();
            var projectId = pr.GetProperty("repository").GetProperty("project").GetProperty("id").GetString();
            string checks;
            try
            {
                using var policies = await SendAsync(HttpMethod.Get,
                    $"{project.ProjectUrl}/_apis/policy/evaluations?artifactId={Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{projectId}/{number}")}&api-version=7.1-preview.1",
                    null, cancellationToken);
                checks = BuildChecks(policies.RootElement);
            }
            catch (TrackerException)
            {
                checks = ""; // the token may not read policies: the PR is listed anyway, without CI
            }

            result.Add(ParsePullRequest(pr, checks, project, taskIdPattern));
        }

        return result;
    }

    public static PullRequestInfo ParsePullRequest(JsonElement pr, string checks, AzureProject project, string taskIdPattern)
    {
        var number = pr.GetProperty("pullRequestId").GetInt32();
        var title = pr.GetProperty("title").GetString() ?? "";
        var branch = (pr.TryGetProperty("sourceRefName", out var source) ? source.GetString() ?? "" : "").Replace("refs/heads/", "");
        var votes = pr.TryGetProperty("reviewers", out var reviewers)
            ? reviewers.EnumerateArray().Select(r => r.TryGetProperty("vote", out var v) ? v.GetInt32() : 0).ToList()
            : [];
        var review = votes.Any(v => v <= -5) ? "CHANGES_REQUESTED" : votes.Any(v => v >= 5) ? "APPROVED" : "REVIEW_REQUIRED";
        var taskId = new Regex(taskIdPattern);
        var id = taskId.Match(branch) is { Success: true } inBranch ? inBranch.Value
            : taskId.Match(title) is { Success: true } inTitle ? inTitle.Value : null;
        return new PullRequestInfo(
            number, title, branch, $"{project.RepositoryUrl}/pullrequest/{number}",
            pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            review, checks,
            pr.TryGetProperty("creationDate", out var created) && DateTimeOffset.TryParse(created.GetString(), out var date) ? date : null,
            id?.ToUpperInvariant());
    }

    /// <summary>The build policies of a PR summed up: any rejected → failure, any queued or running → pending, all approved → success.</summary>
    public static string BuildChecks(JsonElement evaluations)
    {
        var builds = evaluations.TryGetProperty("value", out var value)
            ? value.EnumerateArray()
                .Where(e => e.TryGetProperty("configuration", out var c) && c.TryGetProperty("type", out var t)
                            && t.TryGetProperty("displayName", out var n) && n.GetString() == "Build")
                .Select(e => e.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "")
                .Where(s => s != "notApplicable")
                .ToList()
            : [];
        return builds.Count == 0 ? ""
            : builds.Any(s => s is "rejected" or "broken") ? "failure"
            : builds.Any(s => s is "queued" or "running") ? "pending"
            : "success";
    }

    /// <summary>Plain text of an HTML description: tags out, paragraphs and line breaks kept.</summary>
    public static string Html(string html)
    {
        var text = Regex.Replace(html, @"<\s*(br|/p|/div|/li)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", ""));
        return string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{token}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        // Without a valid token Azure DevOps answers 203 with its sign-in page instead of 401.
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation || text.TrimStart().StartsWith('<'))
        {
            throw new TrackerException("Azure DevOps rifiuta l'accesso: controlla il token (PAT) in Impostazioni → Assistente → Azure DevOps.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TrackerException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Azure DevOps rifiuta l'accesso: controlla il token (PAT) in Impostazioni → Assistente → Azure DevOps.",
                HttpStatusCode.Forbidden => "Il token di Azure DevOps non ha i permessi (servono Work Items: lettura e Code: lettura).",
                HttpStatusCode.NotFound => "Organizzazione o progetto di Azure DevOps non trovati: controlla le impostazioni.",
                _ => $"Azure DevOps ha risposto {(int)response.StatusCode}: {Message(text)}",
            });
        }

        return JsonDocument.Parse(text);
    }

    private static string Message(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("message", out var message) ? message.GetString() ?? "" : body;
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] + "…" : body;
        }
    }
}

/// <summary>Azure Boards as a tracker: the work items assigned to the user in the project.</summary>
public sealed class AzureBoardsTracker(AzureDevOpsClient client, AzureProject project) : ITracker
{
    public string Name => $"Azure Boards ({project.Project})";

    public Task<IReadOnlyList<TrackerIssue>> AssignedAsync(CancellationToken cancellationToken = default) =>
        client.AssignedAsync(project, cancellationToken);
}

/// <summary>The Azure DevOps token (PAT), stored like Jira's (see <see cref="SecretFile"/>).</summary>
public static class AzureToken
{
    public const string FileName = "azure-devops-token";

    public static string? Read(string folder) => SecretFile.Read(folder, FileName);

    public static void Write(string folder, string? token) => SecretFile.Write(folder, FileName, token);
}
