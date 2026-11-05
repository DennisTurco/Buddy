using Buddy.Core.Mentor;

namespace Buddy.Core.Tests;

public class MentorScheduleTests
{
    // 2026-11-06 is a Friday, 2026-11-07 a Saturday.
    private static readonly DateTime Friday = new(2026, 11, 6);

    [Test]
    public void Check_in_is_due_once_after_its_time()
    {
        var schedule = new MentorSchedule(new MentorSettings { PillsPerDay = 0, ChatsPerDay = 0, RetroTime = "" });
        var state = new MentorState();

        Assert.That(schedule.Due(Friday.AddHours(8.5), state, TimeSpan.Zero, new Random(1)), Is.Empty);

        var nine = Friday.AddHours(9.1);
        Assert.That(schedule.Due(nine, state, TimeSpan.Zero, new Random(1)), Is.EqualTo(new[] { MentorEvent.CheckIn }));

        MentorSchedule.MarkDone(MentorEvent.CheckIn, nine, state);
        Assert.That(schedule.Due(nine.AddMinutes(5), state, TimeSpan.Zero, new Random(1)), Is.Empty);
    }

    [Test]
    public void Nothing_happens_on_non_working_days()
    {
        var schedule = new MentorSchedule(new MentorSettings());

        Assert.That(schedule.Due(Friday.AddDays(1).AddHours(10), new MentorState(), TimeSpan.FromHours(3), new Random(1)), Is.Empty);
    }

    [Test]
    public void Retro_is_due_on_its_day_once_per_week()
    {
        var schedule = new MentorSchedule(new MentorSettings { PillsPerDay = 0, ChatsPerDay = 0, CheckInTime = "", CheckOutTime = "" });
        var state = new MentorState();
        var at = Friday.AddHours(16.5);

        Assert.That(schedule.Due(at, state, TimeSpan.Zero, new Random(1)), Does.Contain(MentorEvent.Retro));
        MentorSchedule.MarkDone(MentorEvent.Retro, at, state);
        Assert.That(schedule.Due(at.AddMinutes(10), state, TimeSpan.Zero, new Random(1)), Does.Not.Contain(MentorEvent.Retro));
        Assert.That(state.LastRetroWeek, Is.EqualTo("2026-W45"));
    }

    [Test]
    public void Pills_and_chats_are_planned_within_the_working_day()
    {
        var schedule = new MentorSchedule(new MentorSettings { PillsPerDay = 2, ChatsPerDay = 1 });
        var state = new MentorState();

        schedule.Due(Friday.AddHours(7), state, TimeSpan.Zero, new Random(42));

        Assert.That(state.PillSlots, Has.Count.EqualTo(2));
        Assert.That(state.ChatSlots, Has.Count.EqualTo(1));
        Assert.That(state.PillSlots.Concat(state.ChatSlots), Has.All.InRange(new TimeOnly(9, 30), new TimeOnly(17, 30)));

        var afterAll = Friday.Add(state.PillSlots.Max().ToTimeSpan()).AddMinutes(1);
        var due = schedule.Due(afterAll, state, TimeSpan.Zero, new Random(42));
        Assert.That(due, Does.Contain(MentorEvent.Pill));
    }

    [Test]
    public void Break_is_suggested_after_continuous_activity_and_not_repeated_right_away()
    {
        var schedule = new MentorSchedule(new MentorSettings { BreakAfterMinutes = 90, PillsPerDay = 0, ChatsPerDay = 0, CheckInTime = "" });
        var state = new MentorState();
        var at = Friday.AddHours(11);

        Assert.That(schedule.Due(at, state, TimeSpan.FromMinutes(60), new Random(1)), Does.Not.Contain(MentorEvent.Break));
        Assert.That(schedule.Due(at, state, TimeSpan.FromMinutes(95), new Random(1)), Does.Contain(MentorEvent.Break));

        MentorSchedule.MarkDone(MentorEvent.Break, at, state);
        Assert.That(schedule.Due(at.AddMinutes(20), state, TimeSpan.FromMinutes(115), new Random(1)), Does.Not.Contain(MentorEvent.Break));
    }

    [Test]
    public void Disabled_mentor_is_silent() =>
        Assert.That(new MentorSchedule(new MentorSettings { Enabled = false }).Due(Friday.AddHours(10), new MentorState(), TimeSpan.FromHours(5), new Random(1)), Is.Empty);
}
