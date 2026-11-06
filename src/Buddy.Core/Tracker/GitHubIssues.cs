using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Buddy.Core.Tracker;

/// <summary>
/// The open GitHub issues assigned to the user in the project's repository, read with the gh CLI (read-only:
/// `gh issue list --assignee @me`). On the board an issue #12 is GH-12, so it reads like the other ids and is not
/// imported twice.
/// </summary>
public sealed class GitHubIssues(string repoPath) : ITracker
{
    public const string Prefix = "GH-";

    public string Name => "GitHub (issue)";

    public async Task<IReadOnlyList<TrackerIssue>> AssignedAsync(CancellationToken cancellationToken = default)
    {
        var json = await Cli.RunAsync("gh", repoPath,
            ["issue", "list", "--assignee", "@me", "--state", "open", "--limit", "50", "--json", "number,title,body,labels,url,author,milestone"],
            cancellationToken, missing: "Serve la GitHub CLI (gh) installata e collegata con `gh auth login`.");
        return Parse(json);
    }

    public static IReadOnlyList<TrackerIssue> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(issue =>
        {
            var labels = issue.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Where(n => n.Length > 0).ToList()
                : [];
            var priority = labels.Select(Priority).FirstOrDefault(p => p is not null);
            var type = labels.FirstOrDefault(n => n.Contains("bug", StringComparison.OrdinalIgnoreCase))
                       ?? labels.FirstOrDefault(n => n is "enhancement" or "feature" or "documentation");
            DateOnly? due = issue.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Object
                            && m.TryGetProperty("dueOn", out var d) && d.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(d.GetString(), out var dueOn)
                ? DateOnly.FromDateTime(dueOn.UtcDateTime)
                : null;
            return new TrackerIssue(
                Prefix + issue.GetProperty("number").GetInt32(),
                issue.GetProperty("title").GetString() ?? "",
                issue.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                priority,
                type,
                "open",
                issue.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("login", out var login) ? login.GetString() : null,
                due,
                issue.GetProperty("url").GetString() ?? "");
        }).ToList();
    }

    /// <summary>A label that says the priority: "P1", "priority: high", "priorità alta"…; null otherwise.</summary>
    private static string? Priority(string label)
    {
        var text = label.ToLowerInvariant().Replace("priority", "").Replace("priorità", "").Replace("prio", "").Trim(' ', ':', '-', '/');
        return text is "p1" or "p2" or "p3" or "high" or "highest" or "critical" or "alta" or "medium" or "media" or "low" or "bassa"
            ? TrackerImport.Priority(text == "media" ? "medium" : text)
            : null;
    }
}

/// <summary>Runs a read-only command-line tool (gh) and returns its output; its error becomes a <see cref="TrackerException"/>.</summary>
public static class Cli
{
    public static async Task<string> RunAsync(string tool, string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken,
        string missing, int timeoutSeconds = 30)
    {
        if (!Directory.Exists(workingDirectory))
        {
            throw new TrackerException($"Repository non trovato: {workingDirectory}");
        }

        var startInfo = new ProcessStartInfo(tool)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var message = (await error).Trim();
                throw new TrackerException(message.Length > 0 ? message : $"{tool} non ha risposto.");
            }

            return await output;
        }
        catch (Win32Exception)
        {
            throw new TrackerException(missing);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TrackerException($"{tool} non ha risposto in tempo.");
        }
    }
}
