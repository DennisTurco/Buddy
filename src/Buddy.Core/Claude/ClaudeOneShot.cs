using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Claude;

/// <summary>
/// A single non-interactive Claude Code call (claude -p), separate from the chat session: used by the mentor
/// to write the journal and to prepare pills, questions and the weekly retrospective.
/// Edits are allowed only inside <paramref name="workingDirectory"/> (the CLI denies anything outside it when
/// no one can answer a permission prompt).
/// </summary>
public sealed class ClaudeOneShot(IOptions<BuddySettings> options, ILogger<ClaudeOneShot>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<ClaudeOneShot>.Instance;

    public async Task<OneShotResult> RunAsync(
        string prompt,
        string workingDirectory,
        string? model,
        IEnumerable<string>? readOnlyDirectories = null,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!Directory.Exists(workingDirectory))
        {
            return new OneShotResult(false, $"Cartella non trovata: {workingDirectory}");
        }

        var startInfo = new ProcessStartInfo(ClaudeExecutable.Resolve(settings.Claude.Executable))
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Read anywhere it was given access to; write only under the working directory; anything else that would
        // need a permission prompt is denied automatically (--permission-prompts none).
        foreach (var arg in new[]
                 {
                     "-p", "--output-format", "text", "--permission-prompts", "none",
                     "--allowedTools", "Read", "--allowedTools", "Glob", "--allowedTools", "Grep",
                     "--allowedTools", "Edit(./**)", "--allowedTools", "Write(./**)",
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var dir in readOnlyDirectories?.Where(Directory.Exists) ?? [])
        {
            startInfo.ArgumentList.Add("--add-dir");
            startInfo.ArgumentList.Add(dir);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(model);
        }

        ClaudeSession.RemoveInheritedHostVariables(startInfo.Environment);

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Claude Code non si avvia.");

            // The prompt goes through stdin: no quoting problems and no command-line length limit.
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            var text = (await output).Trim();
            if (process.ExitCode == 0 && !ClaudeAuth.IsAuthError(text))
            {
                _log.LogInformation("claude -p ok in {Folder} ({Chars} caratteri)", workingDirectory, text.Length);
                return new OneShotResult(true, text);
            }

            var stderr = (await error).Trim();
            _log.LogWarning("claude -p fallito (exit {Code}) in {Folder}: {Error}", process.ExitCode, workingDirectory, stderr.Length > 0 ? stderr : text);
            return new OneShotResult(false, ClaudeAuth.IsAuthError(text) ? ClaudeAuth.LoginHint : stderr.Length > 0 ? stderr : text);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException or IOException)
        {
            _log.LogError(ex, "claude -p non eseguito in {Folder}", workingDirectory);
            return new OneShotResult(false, ClaudeExecutable.Describe(ex, settings.Claude.Executable));
        }
    }
}

public sealed record OneShotResult(bool Success, string Text);
