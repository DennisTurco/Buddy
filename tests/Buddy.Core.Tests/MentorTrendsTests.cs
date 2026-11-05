using Buddy.Core.Logging;
using Buddy.Core.Mentor;
using Microsoft.Extensions.Logging;

namespace Buddy.Core.Tests;

public class MentorTrendsTests
{
    private static readonly DateTime Friday = new(2026, 11, 6);

    [Test]
    public void Reviews_are_planned_and_due_like_pills()
    {
        var schedule = new MentorSchedule(new MentorSettings { PillsPerDay = 0, ChatsPerDay = 0, ReviewsPerDay = 1, CheckInTime = "", CheckOutTime = "", RetroTime = "" });
        var state = new MentorState();

        schedule.Due(Friday.AddHours(7), state, TimeSpan.Zero, new Random(3));
        Assert.That(state.ReviewSlots, Has.Count.EqualTo(1));

        var after = Friday.Add(state.ReviewSlots[0].ToTimeSpan()).AddMinutes(1);
        Assert.That(schedule.Due(after, state, TimeSpan.Zero, new Random(3)), Is.EqualTo(new[] { MentorEvent.Review }));

        MentorSchedule.MarkDone(MentorEvent.Review, after, state);
        Assert.That(schedule.Due(after.AddMinutes(1), state, TimeSpan.Zero, new Random(3)), Is.Empty);
    }

    [TestCase("/lavora NUSR-1", true)]
    [TestCase("/LAVORA NUSR-1 autonomo", true)]
    [TestCase("/contesto NUSR-1", false)]
    [TestCase("/analisi NUSR-1", false)]
    [TestCase("/lavorare NUSR-1", false)]
    public void Approach_is_asked_only_for_delegating_commands(string action, bool expected)
    {
        Assert.That(new MentorSettings().AsksApproachFor(action), Is.EqualTo(expected));
    }

    [Test]
    public void Approach_commands_are_configurable_and_can_be_turned_off()
    {
        Assert.That(new MentorSettings { ApproachCommands = "lavora, /guidami" }.AsksApproachFor("/guidami NUSR-1"), Is.True);
        Assert.That(new MentorSettings { AskApproachBeforeDelegating = false }.AsksApproachFor("/lavora NUSR-1"), Is.False);
    }

    [Test]
    public void Week_counters_keep_only_the_last_weeks()
    {
        var state = new MentorState();
        for (var i = 0; i < MentorState.KeptWeeks + 3; i++)
        {
            state.Week(Friday.AddDays(-7 * i)).FocusBlocks++;
        }

        Assert.That(state.Weeks, Has.Count.EqualTo(MentorState.KeptWeeks));
        Assert.That(state.Weeks.ContainsKey("2026-W45"), Is.True);
    }

    [Test]
    public void Trend_table_has_one_row_per_week_oldest_first()
    {
        var state = new MentorState();
        state.Week(Friday).ActiveMinutes = 90;
        state.Week(Friday).FocusBlocks = 2;
        state.Week(Friday).FocusMinutes = 100;
        state.Week(Friday.AddDays(-7)).Reviews = 3;

        var table = MentorStats.ToMarkdown(state, Friday, weeks: 2);
        var rows = table.Split('\n').Skip(2).ToList();

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[0], Does.StartWith("| 2026-W44 |").And.Contain("| 3 |"));
        Assert.That(rows[1], Does.StartWith("| 2026-W45 | 1,5 | 2 (100 min) |"));
    }

    [Test]
    public void Trend_table_explains_when_there_is_no_data()
    {
        Assert.That(MentorStats.ToMarkdown(new MentorState(), Friday), Does.StartWith("Ancora nessun dato"));
    }

    [Test]
    public void File_logger_writes_daily_file_with_exceptions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "buddy-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new FileLoggerProvider(folder);
            var logger = provider.CreateLogger("Buddy.Core.Chat.Conversation");
            logger.LogDebug("non scritto");
            logger.LogWarning("Ollama non risponde");
            logger.LogError(new InvalidOperationException("boom"), "Errore");

            var text = File.ReadAllText(provider.CurrentFile);
            Assert.That(text, Does.Contain("WRN Conversation: Ollama non risponde"));
            Assert.That(text, Does.Contain("ERR Conversation: Errore").And.Contain("InvalidOperationException: boom"));
            Assert.That(text, Does.Not.Contain("non scritto"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
