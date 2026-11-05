using System.ComponentModel;

namespace Buddy.Core.Claude;

/// <summary>
/// Finds the Claude Code CLI. A bare name ("claude") is looked up on PATH first; apps started from the desktop
/// often lack the user's shell PATH, so the native installer's folder (~/.local/bin) and the old local install
/// (~/.claude/local) are tried too. An explicit path is used as is.
/// </summary>
public static class ClaudeExecutable
{
    public static string Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || Path.IsPathRooted(configured) || configured.Contains('/') || configured.Contains('\\'))
        {
            return configured;
        }

        if (OnPath(configured))
        {
            return configured;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var name = OperatingSystem.IsWindows() && !Path.HasExtension(configured) ? configured + ".exe" : configured;
        return new[]
            {
                Path.Combine(home, ".local", "bin", name),
                Path.Combine(home, ".claude", "local", name),
            }
            .FirstOrDefault(File.Exists) ?? configured;
    }

    /// <summary>A readable explanation when the CLI cannot be started, with what to do.</summary>
    public static string Describe(Exception error, string configured) =>
        error is Win32Exception { NativeErrorCode: 2 } // file not found
            ? $"Claude Code non trovato (`{configured}`). Installalo (https://claude.com/claude-code, poi `claude` e /login "
              + "in un terminale) oppure indica il percorso in Impostazioni → Modelli e percorsi → Eseguibile."
            : error.Message;

    private static bool OnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';').Prepend("")
            : [""];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => extensions.Any(ext => File.Exists(Path.Combine(dir, name + ext))));
    }
}
