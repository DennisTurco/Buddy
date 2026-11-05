using Buddy.Core.Claude;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Tests;

public class ClaudeUsageTests
{
    [Test]
    public async Task Rate_limit_events_are_parsed()
    {
        var events = new List<ClaudeEvent>();
        await using var session = new ClaudeSession(Options.Create(new BuddySettings()), _ => Task.FromResult(PermissionDecision.Allowed));
        session.EventReceived += events.Add;

        await session.HandleLineAsync("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1793912400,"rateLimitType":"five_hour","utilization":0.83},"session_id":"s1"}""");
        await session.HandleLineAsync("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed"}}""");

        var limit = events.OfType<ClaudeRateLimitChanged>().Single().Limit;
        Assert.That(limit.Type, Is.EqualTo("five_hour"));
        Assert.That(limit.IsWarning, Is.True);
        Assert.That(limit.Utilization, Is.EqualTo(0.83).Within(0.001));
        Assert.That(limit.ResetsAt, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1793912400)));
        Assert.That(limit.ShortStatus, Is.EqualTo("83%"));
    }

    [Test]
    public void Today_total_adds_up_and_survives_a_restart()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("buddy-usage-").FullName, "usage.json");
        try
        {
            var usage = new ClaudeUsage();
            usage.PersistTo(path);
            usage.AddTurn(0.12m);
            usage.AddTurn(0.03m);
            usage.Update(new ClaudeRateLimit("five_hour", "allowed", 0.42, null));

            Assert.That(usage.TodayTurns, Is.EqualTo(2));
            Assert.That(usage.ShortText(), Is.EqualTo("Oggi 0,15 $ · 5 ore 42%"));

            var again = new ClaudeUsage();
            again.PersistTo(path);
            Assert.That(again.TodayCost, Is.EqualTo(0.15m));
            Assert.That(again.Describe(), Does.Contain("2 turni"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
