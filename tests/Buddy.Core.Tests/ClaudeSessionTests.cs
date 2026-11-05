using Buddy.Core.Chat;
using Buddy.Core.Claude;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class ClaudeSessionTests
{
    [Test]
    public async Task Parses_init_deltas_tools_and_result()
    {
        var events = new List<ClaudeEvent>();
        await using var session = NewSession(events);

        await session.HandleLineAsync("""{"type":"system","subtype":"init","session_id":"s1","model":"m"}""");
        await session.HandleLineAsync("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Ciao"}}}""");
        await session.HandleLineAsync("""{"type":"assistant","message":{"content":[{"type":"text","text":"Ciao"},{"type":"tool_use","name":"Read","input":{"file_path":"a.txt"}}]}}""");
        await session.HandleLineAsync("""{"type":"result","is_error":false,"result":"ok","total_cost_usd":0.0123,"session_id":"s1"}""");

        Assert.That(events, Is.EqualTo(new ClaudeEvent[]
        {
            new ClaudeSessionStarted("s1", "m"),
            new ClaudeTextDelta("Ciao"),
            new ClaudeToolUse("Read", "Read: a.txt"),
            new ClaudeTurnCompleted(false, "ok", 0.0123m, "s1"),
        }));
        Assert.That(session.SessionId, Is.EqualTo("s1"));
    }

    [Test]
    public async Task Full_text_block_is_used_when_nothing_was_streamed()
    {
        var events = new List<ClaudeEvent>();
        await using var session = NewSession(events);

        await session.HandleLineAsync("""{"type":"assistant","message":{"content":[{"type":"text","text":"Failed to authenticate"}]}}""");

        Assert.That(events, Is.EqualTo(new ClaudeEvent[] { new ClaudeTextDelta("Failed to authenticate") }));
    }

    [Test]
    public void Arguments_enable_streaming_and_stdio_permissions_and_resume()
    {
        var session = NewSession([]);

        var args = session.BuildArguments("abc").ToList();

        Assert.That(args, Is.SupersetOf(new[] { "-p", "stream-json", "--permission-prompt-tool", "stdio", "--resume", "abc" }));
    }

    [TestCase("ciao", null, "ciao")]
    [TestCase("/c", "/c", "")]
    [TestCase("/C  fai una cosa ", "/c", "fai una cosa")]
    public void SplitCommand_separates_command_and_argument(string input, string? command, string argument)
    {
        Assert.That(Conversation.SplitCommand(input.Trim()), Is.EqualTo((command, argument)));
    }

    private static ClaudeSession NewSession(List<ClaudeEvent> events)
    {
        var session = new ClaudeSession(
            Options.Create(new BuddySettings()),
            _ => Task.FromResult(PermissionDecision.Allowed));
        session.EventReceived += events.Add;
        return session;
    }
}
