using System.ComponentModel;

namespace Buddy.Core.Claude;

/// <summary>
/// Finds the Claude Code CLI. A bare name ("claude") is looked up on PATH first; apps started from the desktop
/// often lack the user's shell PATH, so the native installer's folder (~/.local/bin), the old local install
/// (~/.claude/local) and, on Windows, npm's global folder are tried too. On Windows the native installer gives
/// claude.exe and npm gives claude.cmd, so either is accepted whichever one is configured. An explicit path is
/// used as is.
/// </summary>
public static class ClaudeExecutable
{
    public static string Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || Path.IsPathRooted(configured) || configured.Contains('/') || configured.Contains('\\'))
        {
            return configured;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat([Path.Combine(home, ".local", "bin"), Path.Combine(home, ".claude", "local")]);
        if (OperatingSystem.IsWindows())
        {
            folders = folders.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        }

        var names = Candidates(configured).ToList();
        foreach (var folder in folders)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(folder, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return configured;
    }

    /// <summary>
    /// The file names to look for. On Windows Process.Start only adds ".exe" by itself, so the full name is
    /// needed: the configured one first, then the same name as .exe and .cmd (native installer vs npm).
    /// </summary>
    private static IEnumerable<string> Candidates(string configured)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [configured];
        }

        var stem = Path.GetExtension(configured).ToLowerInvariant() is ".exe" or ".cmd" or ".bat"
            ? Path.GetFileNameWithoutExtension(configured)
            : configured;
        return new[] { configured, stem + ".exe", stem + ".cmd" }
            .Where(Path.HasExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A readable explanation when the CLI cannot be started, with what to do.</summary>
    public static string Describe(Exception error, string configured) =>
        error is Win32Exception { NativeErrorCode: 2 } // file not found
            ? $"Claude Code non trovato (`{configured}`). Installalo (https://claude.com/claude-code, poi `claude` e /login "
              + "in un terminale) oppure indica il percorso in Impostazioni → Modelli e percorsi → Eseguibile."
            : error.Message;
}
