using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Buddy.Core.Vault;

namespace Buddy.Core.Tracker;

/// <summary>A card of a tracker (Jira, GitHub, Azure Boards), as much of it as the board needs; Key is its board id.</summary>
public sealed record TrackerIssue(
    string Key,
    string Summary,
    string Description,
    string? Priority,
    string? Type,
    string? Status,
    string? Reporter,
    DateOnly? Due,
    string Url);

/// <summary>The Jira API token (see <see cref="SecretFile"/>).</summary>
public static class JiraToken
{
    public const string FileName = "jira-token";

    public static string? Read(string folder) => SecretFile.Read(folder, FileName);

    public static void Write(string folder, string? token) => SecretFile.Write(folder, FileName, token);
}

/// <summary>
/// A token (Jira, Azure DevOps), kept out of appsettings.json in a file of its own that only the user can read (on
/// Linux mode 600; on Windows the user's profile folder is already private).
/// </summary>
public static class SecretFile
{
    public static string? Read(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } token ? token : null;
    }

    /// <summary>Saves the token (empty removes it).</summary>
    public static void Write(string folder, string fileName, string? token)
    {
        var path = Path.Combine(folder, fileName);
        if (string.IsNullOrWhiteSpace(token))
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            // Created empty and private first, so the token is never readable by others, not even for a moment.
            using (File.Create(path))
            {
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllText(path, token.Trim());
    }
}

/// <summary>
/// Reads the cards assigned to the user through Jira's REST API: Jira Cloud (*.atlassian.net, email + API token,
/// /rest/api/3/search/jql) or Server/Data Center (personal access token, /rest/api/2/search).
/// </summary>
public sealed class JiraClient(HttpClient http)
{
    private const int MaxResults = 50;
    private static readonly string[] Fields = ["summary", "description", "priority", "issuetype", "status", "reporter", "duedate"];

    public static bool IsCloud(string baseUrl) => baseUrl.Contains(".atlassian.net", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Assigned to me, not done", limited to the project when one is given; or the user's own JQL.</summary>
    public static string Query(JiraSettings settings, string? project) =>
        settings.Jql.Trim() is { Length: > 0 } jql
            ? jql
            : "assignee = currentUser() AND statusCategory != Done"
              + (string.IsNullOrWhiteSpace(project) ? "" : $" AND project = \"{project.Trim().Replace("\"", "")}\"")
              + " ORDER BY priority DESC, updated DESC";

    public async Task<IReadOnlyList<TrackerIssue>> AssignedAsync(JiraSettings settings, string token, string? project, CancellationToken cancellationToken = default)
    {
        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
        var cloud = IsCloud(baseUrl);
        using var request = cloud
            ? new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/rest/api/3/search/jql")
            {
                Content = new StringContent(
                    new JsonObject
                    {
                        ["jql"] = Query(settings, project),
                        ["maxResults"] = MaxResults,
                        ["fields"] = new JsonArray(Fields.Select(f => (JsonNode)f).ToArray()),
                    }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/rest/api/2/search?maxResults={MaxResults}&fields={string.Join(',', Fields)}&jql={Uri.EscapeDataString(Query(settings, project))}");

        request.Headers.Authorization = cloud || settings.Email.Trim().Length > 0
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Email.Trim()}:{token}")))
            : new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new JiraException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Jira rifiuta l'accesso: controlla email e token in Impostazioni → Jira.",
                HttpStatusCode.Forbidden => "Jira non ti permette questa ricerca: controlla i permessi del token.",
                HttpStatusCode.BadRequest => $"Jira non accetta la ricerca (JQL?): {await ErrorAsync(response, cancellationToken)}",
                HttpStatusCode.NotFound => "Indirizzo di Jira non trovato: controlla l'URL in Impostazioni → Jira.",
                _ => $"Jira ha risposto {(int)response.StatusCode}: {await ErrorAsync(response, cancellationToken)}",
            });
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return Parse(json.RootElement, baseUrl);
    }

    /// <summary>The issues of a search response (Cloud or Server: same "issues" array, description as ADF or text).</summary>
    public static IReadOnlyList<TrackerIssue> Parse(JsonElement root, string baseUrl)
    {
        if (!root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        static string? Name(JsonElement fields, string field, string property = "name") =>
            fields.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;

        return issues.EnumerateArray().Select(issue =>
        {
            var key = issue.GetProperty("key").GetString() ?? "";
            var fields = issue.TryGetProperty("fields", out var f) ? f : default;
            var description = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("description", out var d) ? d : default;
            var due = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("duedate", out var dd) && dd.ValueKind == JsonValueKind.String
                      && DateOnly.TryParse(dd.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var date)
                ? date
                : (DateOnly?)null;
            return new TrackerIssue(
                key,
                fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("summary", out var s) ? s.GetString() ?? key : key,
                description.ValueKind switch
                {
                    JsonValueKind.String => description.GetString() ?? "",
                    JsonValueKind.Object => AdfText(description),
                    _ => "",
                },
                fields.ValueKind == JsonValueKind.Object ? Name(fields, "priority") : null,
                fields.ValueKind == JsonValueKind.Object ? Name(fields, "issuetype") : null,
                fields.ValueKind == JsonValueKind.Object ? Name(fields, "status") : null,
                fields.ValueKind == JsonValueKind.Object ? Name(fields, "reporter", "displayName") : null,
                due,
                $"{baseUrl}/browse/{key}");
        }).Where(i => i.Key.Length > 0).ToList();
    }

    /// <summary>The plain text of a Jira Cloud description (Atlassian Document Format), paragraphs on their own lines.</summary>
    public static string AdfText(JsonElement node)
    {
        var text = new StringBuilder();
        void Walk(JsonElement n)
        {
            if (n.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (n.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                text.Append(t.GetString());
            }

            if (n.TryGetProperty("type", out var type) && type.GetString() == "hardBreak")
            {
                text.Append('\n');
            }

            if (n.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in content.EnumerateArray())
                {
                    Walk(child);
                }
            }

            if (type.ValueKind == JsonValueKind.String && type.GetString() is "paragraph" or "heading" or "listItem" or "codeBlock")
            {
                text.Append('\n');
            }
        }

        Walk(node);
        return string.Join('\n', text.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("errorMessages", out var messages) && messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0)
            {
                return string.Join("; ", messages.EnumerateArray().Select(m => m.GetString()));
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 200 ? body[..200] + "…" : body;
    }
}

/// <summary>A failure of a tracker the user can act on (wrong token, address…): the message says what to fix.</summary>
public sealed class JiraException(string message) : TrackerException(message);

public class TrackerException(string message) : Exception(message);

/// <summary>A source of cards assigned to the user: Jira, GitHub issues, Azure Boards.</summary>
public interface ITracker
{
    /// <summary>The name in the board's "⬇ Importa" menu.</summary>
    string Name { get; }

    Task<IReadOnlyList<TrackerIssue>> AssignedAsync(CancellationToken cancellationToken = default);
}

/// <summary>Jira as a tracker: the configured site, token and project.</summary>
public sealed class JiraTracker(JiraClient client, JiraSettings settings, string token, string? project) : ITracker
{
    public string Name => "Jira";

    public Task<IReadOnlyList<TrackerIssue>> AssignedAsync(CancellationToken cancellationToken = default) =>
        client.AssignedAsync(settings, token, project, cancellationToken);
}

/// <summary>Tracker cards as To-Do tasks, in the flow's format, skipping those already in the vault.</summary>
public static class TrackerImport
{
    /// <summary>The cards whose key is not anywhere on the board yet (To-Do, Da controllare, Fatti, Rifiutati…).</summary>
    public static IReadOnlyList<TrackerIssue> New(IEnumerable<TrackerIssue> issues, ProjectBoard board)
    {
        var items = board.Columns.SelectMany(c => c.Items).ToList();
        var known = items.Select(i => i.Id).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A card created from the board (✚ issue) carries the issue's link, not its key.
        var links = items.Select(IssueFromCard.LinkOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return issues.Where(i => !known.Contains(i.Key) && !links.Contains(i.Url)).ToList();
    }

    /// <summary>
    /// "- [ ] ABC-12 · 2026-11-06 · Titolo [priorita:: P1] [tag:: ticket, bug]" with the card's link and the start of
    /// its description as Contesto; "Fatto quando" stays to be defined (/smista or by hand), as the flow asks.
    /// </summary>
    public static string ToTask(TrackerIssue issue, DateOnly today)
    {
        var description = string.Join(' ', issue.Description.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (description.Length > 300)
        {
            description = description[..300].TrimEnd() + "…";
        }

        var tags = new List<string> { "ticket" };
        if (issue.Type is { } type && (type.Contains("bug", StringComparison.OrdinalIgnoreCase) || type.Contains("errore", StringComparison.OrdinalIgnoreCase)))
        {
            tags.Add("bug");
        }

        return BoardEntries.Task(new NewEntry
        {
            Title = issue.Summary,
            Priority = Priority(issue.Priority),
            Estimate = "",
            Tags = tags,
            Context = $"[{issue.Key}]({issue.Url})" + (description.Length > 0 ? $" — {description}" : ""),
            DoneWhen = "da definire",
            AssignedBy = issue.Reporter ?? "",
        }, issue.Key, today);
    }

    /// <summary>Priority names (English or Italian, Jira or labels) or Azure's 1–4 as P1–P3.</summary>
    public static string Priority(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "highest" or "high" or "blocker" or "critical" or "massima" or "alta" or "bloccante" or "critica" or "1" or "p1" => "P1",
        "low" or "lowest" or "minor" or "trivial" or "bassa" or "minima" or "3" or "4" or "p3" => "P3",
        _ => "P2",
    };
}
