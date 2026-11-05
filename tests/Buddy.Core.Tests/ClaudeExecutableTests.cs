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
    public void Missing_cli_gets_a_hint()
    {
        var text = ClaudeExecutable.Describe(new Win32Exception(2), "claude");
        Assert.That(text, Does.Contain("Claude Code non trovato").And.Contain("Eseguibile"));
    }
}
