using System.Threading.Channels;
using Buddy.Core.Claude;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

/// <summary>
/// Runs <see cref="ClaudeSession"/> against a fake CLI (Fakes/fake-claude.js) to check the process
/// plumbing and the stdio permission round trip. Requires node on PATH.
/// </summary>
public class ClaudeProcessTests
{
    [TestCase(true, "scritto: ciao")]
    [TestCase(false, "negato: no grazie")]
    public async Task Permission_decision_reaches_the_cli(bool allow, string expected)
    {
        var events = Channel.CreateUnbounded<ClaudeEvent>();
        var asked = new List<PermissionRequest>();
        var settings = Options.Create(new BuddySettings
        {
            ProjectPath = TestContext.CurrentContext.WorkDirectory,
            Claude = new ClaudeSettings { Executable = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fakes", "fake-claude.cmd") },
        });

        await using var session = new ClaudeSession(settings, request =>
        {
            asked.Add(request);
            return Task.FromResult(allow ? PermissionDecision.Allowed : PermissionDecision.Denied("no grazie"));
        });
        session.EventReceived += e => events.Writer.TryWrite(e);
        session.Start();
        await session.SendAsync("ciao");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ClaudeTurnCompleted? done = null;
        var text = "";
        await foreach (var e in events.Reader.ReadAllAsync(timeout.Token))
        {
            if (e is ClaudeTextDelta d) text += d.Text;
            if (e is ClaudeTurnCompleted c) { done = c; break; }
        }

        Assert.That(asked.Single().Summary, Is.EqualTo("Write: hello.txt"));
        Assert.That(text, Is.EqualTo(expected));
        Assert.That(done!.Result, Is.EqualTo(expected));
        Assert.That(session.SessionId, Is.EqualTo("fake-session"));
    }
}
