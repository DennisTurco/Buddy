using System.ComponentModel;
using Buddy.Core.Claude;

namespace Buddy.Core.Tests;

public class ClaudeExecutableTests
{
    [Test]
    public void Explicit_path_is_used_as_is()
    {
        var path = Path.Combine(Path.GetTempPath(), "nowhere", "claude");
        Assert.That(ClaudeExecutable.Resolve(path), Is.EqualTo(path));
    }

    [Test]
    public void Unknown_name_is_left_unchanged()
    {
        Assert.That(ClaudeExecutable.Resolve("claude-does-not-exist-xyz"), Is.EqualTo("claude-does-not-exist-xyz"));
    }

    [Test]
    public void Name_on_path_resolves_to_its_full_path()
    {
        var dir = Directory.CreateTempSubdirectory("buddy-path-").FullName;
        var name = OperatingSystem.IsWindows() ? "claude-xyz.exe" : "claude-xyz";
        File.WriteAllText(Path.Combine(dir, name), "");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
            // On Windows the configured claude.cmd also finds the native installer's claude.exe.
            var configured = OperatingSystem.IsWindows() ? "claude-xyz.cmd" : "claude-xyz";
            Assert.That(ClaudeExecutable.Resolve(configured), Is.EqualTo(Path.Combine(dir, name)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Missing_cli_gets_a_hint()
    {
        var text = ClaudeExecutable.Describe(new Win32Exception(2), "claude");
        Assert.That(text, Does.Contain("Claude Code non trovato").And.Contain("Eseguibile"));
    }
}
