using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Claude;

public enum ClaudeLoginState
{
    LoggedIn,
    LoggedOut,

    /// <summary>The check itself failed (CLI missing, old version without `auth status`, timeout).</summary>
    Unknown,
}

/// <summary>Asks the CLI whether the user is logged in (`claude auth status`, JSON output).</summary>
public sealed class ClaudeAuth(IOptions<BuddySettings> options)
{
    public const string LoginHint = """
        Claude Code non è loggato. Apri un terminale (fuori dall'app Claude), lancia `claude`, poi `/login`.
        Controlla con `claude auth status` (deve dire "loggedIn": true) e riprova.
        """;

    public async Task<ClaudeLoginState> CheckAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(ClaudeExecutable.Resolve(options.Value.Claude.Executable))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("auth");
        startInfo.ArgumentList.Add("status");
        ClaudeSession.RemoveInheritedHostVariables(startInfo.Environment);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return ClaudeLoginState.Unknown;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return Parse(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return ClaudeLoginState.Unknown;
        }
    }

    /// <summary>`auth status` exits non-zero when logged out, so only the JSON body is trusted.</summary>
    internal static ClaudeLoginState Parse(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0)
        {
            return ClaudeLoginState.Unknown;
        }

        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            return doc.RootElement.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? loggedIn.GetBoolean() ? ClaudeLoginState.LoggedIn : ClaudeLoginState.LoggedOut
                : ClaudeLoginState.Unknown;
        }
        catch (JsonException)
        {
            return ClaudeLoginState.Unknown;
        }
    }

    /// <summary>Recognises the CLI's authentication failures in an answer or error text.</summary>
    public static bool IsAuthError(string? text) =>
        text is not null
        && (text.Contains("Failed to authenticate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OAuth session expired", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Please run /login", StringComparison.OrdinalIgnoreCase));
}
