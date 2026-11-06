using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Repo;

/// <summary>
/// Read-only view of the code repository through the git CLI: current branch, task id from the branch name,
/// changed files and diff. Never runs a command that changes the repository.
/// </summary>
public sealed class GitRepo(IOptions<BuddySettings> options, TimeProvider clock)
{
    private static readonly TimeSpan BranchCacheDuration = TimeSpan.FromSeconds(10);

    private readonly BuddySettings _settings = options.Value;
    private (string? Branch, DateTimeOffset At)? _branchCache;

    public string RootPath => string.IsNullOrWhiteSpace(_settings.Repo.Path) ? _settings.Claude.WorkingDirectory : _settings.Repo.Path;

    public bool IsAvailable => Directory.Exists(RootPath);

    /// <summary>Current branch name (cached a few seconds), or null outside a repository.</summary>
    public async Task<string?> GetBranchAsync(CancellationToken cancellationToken = default)
    {
        if (_branchCache is { } cache && clock.GetUtcNow() - cache.At < BranchCacheDuration)
        {
            return cache.Branch;
        }

        var result = await RunAsync(["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        var branch = result.ExitCode == 0 ? result.Output.Trim() : null;
        _branchCache = (branch, clock.GetUtcNow());
        return branch;
    }

    /// <summary>Task id found in the branch name (e.g. NUSR-2367 from "NUSR-2367-Numero-massimo..."), upper-cased.</summary>
    public async Task<string?> GetBranchTaskIdAsync(CancellationToken cancellationToken = default) =>
        TaskIdFrom(await GetBranchAsync(cancellationToken), _settings.TaskIdPattern);

    internal static string? TaskIdFrom(string? branch, string pattern) =>
        branch is null ? null : Regex.Match(branch, pattern, RegexOptions.IgnoreCase) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    /// <summary>Changed files, staged or not, plus untracked ones.</summary>
    public async Task<IReadOnlyList<ChangedFile>> GetChangesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["status", "--porcelain=v1", "--untracked-files=all"], cancellationToken);
        return result.ExitCode == 0 ? ParseStatus(result.Output) : [];
    }

    internal static List<ChangedFile> ParseStatus(string porcelain)
    {
        var files = new List<ChangedFile>();
        foreach (var line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4)
            {
                continue;
            }

            var code = line[..2];
            var path = line[3..].TrimEnd('\r');
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                path = path[(arrow + 4)..];
            }

            files.Add(new ChangedFile(path.Trim('"'), code switch
            {
                "??" => "nuovo",
                _ when code.Contains('D') => "eliminato",
                _ when code.Contains('A') => "aggiunto",
                _ when code.Contains('R') => "rinominato",
                _ => "modificato",
            }));
        }

        return files;
    }

    /// <summary>Diff of tracked changes against HEAD, truncated to <paramref name="maxChars"/>; untracked files are listed by name.</summary>
    public async Task<string> GetDiffAsync(int maxChars, CancellationToken cancellationToken = default)
    {
        var stat = await RunAsync(["diff", "HEAD", "--stat"], cancellationToken);
        var diff = await RunAsync(["diff", "HEAD", "--no-color", "--unified=2"], cancellationToken);
        var untracked = (await GetChangesAsync(cancellationToken)).Where(f => f.Status == "nuovo").Select(f => f.Path).ToList();

        var sb = new StringBuilder();
        sb.AppendLine(stat.Output.Trim());
        if (untracked.Count > 0)
        {
            sb.AppendLine().AppendLine("File nuovi non ancora tracciati:");
            untracked.ForEach(f => sb.AppendLine($"- {f}"));
        }

        sb.AppendLine().AppendLine(diff.Output);
        var text = sb.ToString();
        return text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "\n[…diff tagliato]");
    }

    /// <summary>One line per commit of the current user in the given period (for the weekly retrospective).</summary>
    public async Task<string> GetLogAsync(TimeSpan period, CancellationToken cancellationToken = default)
    {
        var author = (await RunAsync(["config", "user.name"], cancellationToken)).Output.Trim();
        var args = new List<string> { "log", "--all", $"--since={(int)period.TotalDays}.days", "--pretty=format:%ad %s", "--date=short" };
        if (author.Length > 0)
        {
            args.Add($"--author={author}");
        }

        var result = await RunAsync([.. args], cancellationToken);
        return result.Output.Trim() is { Length: > 0 } log ? log : "(nessun commit)";
    }

    /// <summary>Commits on any branch whose message mentions <paramref name="id"/> (date, subject), oldest first.</summary>
    public async Task<IReadOnlyList<(DateOnly Date, string Subject)>> GetCommitsMentioningAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["log", "--all", "-i", $"--grep={id}", "--fixed-strings", "--reverse", "--pretty=format:%ad\t%s", "--date=short", "-n", "50"], cancellationToken);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && DateOnly.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(parts => (DateOnly.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts[1]))
            .ToList();
    }

    /// <summary>Text of the PR template under .github, if any.</summary>
    public string? ReadPullRequestTemplate()
    {
        var github = Path.Combine(RootPath, ".github");
        if (!Directory.Exists(github))
        {
            return null;
        }

        var file = Directory.EnumerateFiles(github, _settings.Repo.PullRequestTemplatePattern, SearchOption.AllDirectories).FirstOrDefault();
        return file is null ? null : File.ReadAllText(file);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return (-1, "");
        }

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = RootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=off");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, "");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await outputTask);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return (-1, "");
        }
    }
}

public sealed record ChangedFile(string Path, string Status);
