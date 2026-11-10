using Buddy.Core.Claude;

namespace Buddy.Core.Tests;

public class PlanLimitsTests
{
    [Test]
    public void Too_many_requests_wait_longer_each_time_up_to_an_hour()
    {
        var waits = new List<TimeSpan>();
        var wait = TimeSpan.Zero;
        for (var i = 0; i < 6; i++)
        {
            wait = ClaudePlanUsage.Backoff(wait, retryAfter: null);
            waits.Add(wait);
        }

        Assert.That(waits.Select(w => w.TotalMinutes), Is.EqualTo(new double[] { 5, 10, 20, 40, 60, 60 }));
        Assert.That(ClaudePlanUsage.Backoff(TimeSpan.Zero, TimeSpan.FromMinutes(15)).TotalMinutes, Is.EqualTo(15), "the server's Retry-After when longer");
    }

    [Test]
    public void The_last_limits_survive_a_restart_but_not_a_window_already_reset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"buddy-limits-{Guid.NewGuid():N}.json");
        try
        {
            var usage = new ClaudeUsage();
            usage.PersistTo(path);
            usage.Update(new ClaudeRateLimit("five_hour", "allowed", 0.42, DateTimeOffset.UtcNow.AddHours(2)));
            usage.Update(new ClaudeRateLimit("seven_day", "allowed", 0.10, DateTimeOffset.UtcNow.AddMinutes(-5)));
            var read = DateTimeOffset.UtcNow;
            usage.LimitsRead(read);

            var again = new ClaudeUsage();
            again.PersistTo(path);
            Assert.That(again.Limits.Select(l => (l.Type, l.Utilization)), Is.EqualTo(new[] { ("five_hour", (double?)0.42) }));
            Assert.That(again.LimitsReadAt, Is.EqualTo(read));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void The_reason_limits_are_missing_is_said_and_cleared_once_read()
    {
        var usage = new ClaudeUsage();
        var changes = 0;
        usage.Changed += () => changes++;

        usage.SetLimitsProblem("Limiti non aggiornati: troppe richieste a Claude, riprovo alle 21:10.");
        usage.SetLimitsProblem("Limiti non aggiornati: troppe richieste a Claude, riprovo alle 21:10.");
        Assert.That(changes, Is.EqualTo(1), "the same problem again changes nothing");
        Assert.That(usage.Describe(), Does.Contain("troppe richieste").And.Not.Contain("login"));

        usage.LimitsRead(DateTimeOffset.UtcNow);
        Assert.That(usage.LimitsProblem, Is.Null);
    }
}
