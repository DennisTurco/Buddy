using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Buddy.Core.Vault;

/// <summary>
/// Runs a saved browser test script (the "Test web" note) on this machine, without Claude: its command
/// ("npm run test:t002", or "npx playwright test &lt;file&gt;" when none is written) in the scripts folder named in the
/// Scheda progetto. Output lines are streamed; the Playwright summary ("2 passed", "1 failed") gives the outcome.
/// </summary>
public static partial class WebTestRun
{
    /// <summary>The notes that may name the scripts folder, in order: the Scheda progetto, then the Test web note.</summary>
    private static readonly string[] FolderNotes = ["Scheda progetto", "Test web"];

    /// <summary>
    /// "Cartella degli script di test: `~/claude-test/progetto/`" in the Scheda progetto or in the Test web note,
    /// expanded. A relative path ("`tests/e2e/` nel repo") is resolved against <paramref name="baseFolders"/> (the repo,
    /// Claude's folder): the first one where it exists wins, otherwise the first candidate is returned so the caller
    /// can say which path it tried.
    /// </summary>
    public static string? ScriptsFolder(VaultIndex vault, params string?[] baseFolders)
    {
        var path = FolderNotes
            .Select(title => vault.Notes.Where(n => n.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.RelativePath.Length).FirstOrDefault())
            .Select(note => note is null ? null : FolderLine().Match(note.Content))
            .FirstOrDefault(m => m is { Success: true })?.Groups["path"].Value.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (path.StartsWith('~'))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/', '\\'));
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var candidates = baseFolders.Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => Path.GetFullPath(Path.Combine(b!, path)))
            .ToList();
        return candidates.FirstOrDefault(Directory.Exists) ?? candidates.FirstOrDefault() ?? path;
    }

    public static string CommandOf(WebTestScript script) =>
        !string.IsNullOrWhiteSpace(script.Command) ? script.Command
        : !string.IsNullOrWhiteSpace(script.Path) ? $"npx playwright test {script.Path}"
        : $"npx playwright test {script.Name}";

    /// <summary>Runs the command in <paramref name="folder"/>; every output line goes to <paramref name="output"/>.</summary>
    public static async Task<WebTestResult> RunAsync(string command, string folder, Action<string> output, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", command } }
            // A login shell, so npm/npx are found even when the app was started from the desktop (nvm, ~/.local/bin).
            : new ProcessStartInfo("bash") { ArgumentList = { "-lc", command } };
        startInfo.WorkingDirectory = folder;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.Environment["FORCE_COLOR"] = "0";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["CI"] = "1"; // no interactive HTML report server at the end

        var lines = new List<string>();
        void Line(string? text)
        {
            if (text is null)
            {
                return;
            }

            text = Ansi().Replace(text, "");
            lock (lines)
            {
                lines.Add(text);
            }

            output(text);
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            return new WebTestResult(WebTestOutcome.Stopped, null, null, Stopwatch.GetElapsedTime(started), Snapshot(lines));
        }

        process.WaitForExit(); // flushes the redirected streams
        var all = Snapshot(lines);
        var passed = Count(all, "passed");
        var failed = Count(all, "failed") + Count(all, "timed out");
        var outcome = process.ExitCode == 0 && failed == 0 ? WebTestOutcome.Passed : WebTestOutcome.Failed;
        return new WebTestResult(outcome, passed, failed, Stopwatch.GetElapsedTime(started), all);
    }

    /// <summary>
    /// The script's line in the "Test web" note with "Ultima esecuzione: <date> ✅ (2/2)" for this run: replaced where it
    /// is (any remark after it stays), otherwise added at the end of the line.
    /// </summary>
    public static string WithLastRun(string line, WebTestResult result, DateOnly day)
    {
        var mark = result.Outcome == WebTestOutcome.Passed ? "✅" : "❌";
        var total = (result.Passed ?? 0) + (result.Failed ?? 0);
        var counts = total > 0 ? $" ({result.Passed ?? 0}/{total})" : "";
        var run = $"Ultima esecuzione: {day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {mark}{counts}";
        return LastRunPart().IsMatch(line)
            ? LastRunPart().Replace(line, run, 1)
            : $"{line.TrimEnd()} · {run}";
    }

    private static List<string> Snapshot(List<string> lines)
    {
        lock (lines)
        {
            return [.. lines];
        }
    }

    /// <summary>The number before "passed"/"failed" in Playwright's final summary (the last such line).</summary>
    private static int? Count(IReadOnlyList<string> lines, string word)
    {
        var pattern = new Regex($@"^\s*(\d+)\s+{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (pattern.Match(lines[i]) is { Success: true } m)
            {
                return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return word == "passed" ? null : 0;
    }

    [GeneratedRegex(@"Cartella degli script di test:\**\s*`(?<path>[^`]+)`", RegexOptions.IgnoreCase)]
    private static partial Regex FolderLine();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"Ultima esecuzione:\s*\d{4}-\d{2}-\d{2}\s*(?:✅|❌)?(?:\s*\([^)]*\))?")]
    private static partial Regex LastRunPart();
}

public enum WebTestOutcome
{
    Passed,
    Failed,
    Stopped,
}

public sealed record WebTestResult(WebTestOutcome Outcome, int? Passed, int? Failed, TimeSpan Elapsed, IReadOnlyList<string> Output);
