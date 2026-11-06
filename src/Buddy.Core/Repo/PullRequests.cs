using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Buddy.Core.Repo;

/// <summary>An open pull request of the user, with its checks and review summed up.</summary>
public sealed record PullRequestInfo(int Number, string Title, string Branch, string Url, bool IsDraft, string Review, string Checks, DateTimeOffset? Updated, string? TaskId)
{
    /// <summary>"✅ CI verde", "❌ CI rossa", "⏳ CI in corso", "" without checks.</summary>
    public string ChecksLabel => Checks switch
    {
        "success" => "✅ CI verde",
        "failure" => "❌ CI rossa",
        "pending" => "⏳ CI in corso",
        _ => "",
    };

    public string ReviewLabel => Review switch
    {
        "APPROVED" => "👍 approvata",
        "CHANGES_REQUESTED" => "✏ modifiche richieste",
        "REVIEW_REQUIRED" => "👀 da rivedere",
        _ => "",
    };
}

/// <summary>
/// The user's open pull requests on GitHub, read with the gh CLI (read-only: `gh pr list`), tied to tasks by the id in
/// the branch name or the title. Nothing is listed when gh is missing, not logged in or the repo is not on GitHub.
/// </summary>
public static class PullRequests
{
    public static async Task<(IReadOnlyList<PullRequestInfo> List, string? Problem)> ListAsync(string repoPath, string taskIdPattern,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(repoPath))
        {
            return ([], $"Repository non trovato: {repoPath}");
        }

        var startInfo = new ProcessStartInfo("gh")
        {
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "pr", "list", "--state", "open", "--author", "@me", "--limit", "30",
                     "--json", "number,title,headRefName,url,isDraft,reviewDecision,updatedAt,statusCheckRollup" })
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var message = (await error).Trim();
                return ([], message.Length > 0 ? message : "gh non ha risposto.");
            }

            return (Parse(await output, taskIdPattern), null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            return ([], "Serve la GitHub CLI (gh) installata e collegata con `gh auth login`.");
        }
        catch (OperationCanceledException)
        {
            return ([], "gh non ha risposto in tempo.");
        }
    }

    internal static IReadOnlyList<PullRequestInfo> Parse(string json, string taskIdPattern)
    {
        var taskId = new Regex(taskIdPattern);
        using var document = JsonDocument.Parse(json);
        var list = new List<PullRequestInfo>();
        foreach (var pr in document.RootElement.EnumerateArray())
        {
            var title = pr.GetProperty("title").GetString() ?? "";
            var branch = pr.GetProperty("headRefName").GetString() ?? "";
            var id = taskId.Match(branch) is { Success: true } inBranch ? inBranch.Value
                : taskId.Match(title) is { Success: true } inTitle ? inTitle.Value : null;
            DateTimeOffset? updated = pr.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(u.GetString(), out var date)
                ? date : null;
            list.Add(new PullRequestInfo(
                pr.GetProperty("number").GetInt32(),
                title,
                branch,
                pr.GetProperty("url").GetString() ?? "",
                pr.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
                pr.TryGetProperty("reviewDecision", out var review) && review.ValueKind == JsonValueKind.String ? review.GetString() ?? "" : "",
                Checks(pr),
                updated,
                id?.ToUpperInvariant()));
        }

        return list.OrderByDescending(p => p.Updated ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>The checks summed up: any failed → failure, any still running → pending, all done → success.</summary>
    private static string Checks(JsonElement pr)
    {
        if (!pr.TryGetProperty("statusCheckRollup", out var rollup) || rollup.ValueKind != JsonValueKind.Array || rollup.GetArrayLength() == 0)
        {
            return "";
        }

        var states = rollup.EnumerateArray().Select(c =>
            (c.TryGetProperty("conclusion", out var conclusion) && conclusion.ValueKind == JsonValueKind.String ? conclusion.GetString() : null)
            ?? (c.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() : null)
            ?? (c.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null)
            ?? "").Select(s => s.ToUpperInvariant()).ToList();
        if (states.Any(s => s is "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED"))
        {
            return "failure";
        }

        return states.Any(s => s is "" or "PENDING" or "IN_PROGRESS" or "QUEUED" or "EXPECTED" or "WAITING") ? "pending" : "success";
    }
}
