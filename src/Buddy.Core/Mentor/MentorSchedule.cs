using System.Globalization;

namespace Buddy.Core.Mentor;

public enum MentorEvent
{
    CheckIn,
    CheckOut,
    Retro,
    Pill,
    Chat,
    Break,
    Review,
}

/// <summary>Counters of one ISO week, used for the trends in the weekly retro and in /andamento.</summary>
public sealed class WeekStats
{
    /// <summary>Minutes with keyboard/mouse activity during working hours.</summary>
    public int ActiveMinutes { get; set; }
    public int FocusBlocks { get; set; }
    public int FocusMinutes { get; set; }
    public int CheckIns { get; set; }
    public int CheckOuts { get; set; }
    public int Pills { get; set; }
    public int Reviews { get; set; }
    public int Chats { get; set; }
    public int Approaches { get; set; }
    public int Answers { get; set; }
    public int BreakReminders { get; set; }

    /// <summary>Tasks handed to Claude (/lavora) and done with a guide (/guidami).</summary>
    public int Delegated { get; set; }
    public int Guided { get; set; }

    /// <summary>The "try /guidami" nudge was given this week.</summary>
    public bool GuideSuggested { get; set; }
}

/// <summary>What has already happened, persisted between runs (dates in local time).</summary>
public sealed class MentorState
{
    public DateOnly? LastCheckIn { get; set; }
    public DateOnly? LastCheckOut { get; set; }
    public string? LastRetroWeek { get; set; }

    /// <summary>Day the random slots below were planned for.</summary>
    public DateOnly? PlannedDay { get; set; }
    public List<TimeOnly> PillSlots { get; set; } = [];
    public List<TimeOnly> ChatSlots { get; set; } = [];
    public List<TimeOnly> ReviewSlots { get; set; } = [];

    public DateTime? LastBreakReminder { get; set; }

    /// <summary>Active minutes at the PC on <see cref="ActiveDay"/> (for the check-out draft).</summary>
    public int TodayActiveMinutes { get; set; }

    public DateOnly? ActiveDay { get; set; }

    /// <summary>Tasks seen in "Da controllare" (null until the first look: those are not asked about).</summary>
    public List<string>? InReview { get; set; }

    /// <summary>Card reminders already brought back, by item (its id or first line) and the day of the reminder.</summary>
    public Dictionary<string, DateOnly> Reminded { get; set; } = [];

    /// <summary>Tasks just gone to review, waiting for a quiet moment to ask what was learned.</summary>
    public List<string> PendingTaskRetros { get; set; } = [];

    /// <summary>Counters per week ("2026-W45"), the last <see cref="KeptWeeks"/> kept.</summary>
    public Dictionary<string, WeekStats> Weeks { get; set; } = [];

    public const int KeptWeeks = 12;

    public WeekStats Week(DateTime date)
    {
        var key = MentorSchedule.WeekKey(date);
        if (!Weeks.TryGetValue(key, out var stats))
        {
            Weeks[key] = stats = new WeekStats();
            foreach (var old in Weeks.Keys.Order().SkipLast(KeptWeeks).ToList())
            {
                Weeks.Remove(old);
            }
        }

        return stats;
    }
}

/// <summary>
/// Decides which mentor messages are due. Pure and deterministic given the clock, the state and a random source,
/// so it is fully testable; the caller marks events done in <see cref="MentorState"/>.
/// </summary>
public sealed class MentorSchedule(MentorSettings settings)
{
    public IReadOnlyList<MentorEvent> Due(DateTime now, MentorState state, TimeSpan continuousActivity, Random random)
    {
        var due = new List<MentorEvent>();
        if (!settings.Enabled || !IsWorkDay(now))
        {
            return due;
        }

        var today = DateOnly.FromDateTime(now);
        var time = TimeOnly.FromDateTime(now);
        var checkIn = ParseTime(settings.CheckInTime);
        var checkOut = ParseTime(settings.CheckOutTime);
        var dayStart = checkIn ?? new TimeOnly(9, 0);
        var dayEnd = checkOut ?? new TimeOnly(18, 0);

        if (state.PlannedDay != today)
        {
            Plan(state, today, dayStart, dayEnd, random);
        }

        if (checkIn is { } inTime && time >= inTime && time < dayEnd && state.LastCheckIn != today)
        {
            due.Add(MentorEvent.CheckIn);
        }

        if (checkOut is { } outTime && time >= outTime && state.LastCheckOut != today)
        {
            due.Add(MentorEvent.CheckOut);
        }

        var within = time >= dayStart && time < dayEnd;
        if (within && ParseTime(settings.RetroTime) is { } retroTime && time >= retroTime
            && Enum.TryParse<DayOfWeek>(settings.RetroDay, ignoreCase: true, out var retroDay) && now.DayOfWeek == retroDay
            && state.LastRetroWeek != WeekKey(now))
        {
            due.Add(MentorEvent.Retro);
        }

        if (within && state.PillSlots.Count > 0 && time >= state.PillSlots[0])
        {
            due.Add(MentorEvent.Pill);
        }

        if (within && state.ChatSlots.Count > 0 && time >= state.ChatSlots[0])
        {
            due.Add(MentorEvent.Chat);
        }

        if (within && state.ReviewSlots.Count > 0 && time >= state.ReviewSlots[0])
        {
            due.Add(MentorEvent.Review);
        }

        if (within && settings.BreakAfterMinutes > 0 && continuousActivity.TotalMinutes >= settings.BreakAfterMinutes
            && (state.LastBreakReminder is null || now - state.LastBreakReminder.Value >= TimeSpan.FromMinutes(settings.BreakAfterMinutes)))
        {
            due.Add(MentorEvent.Break);
        }

        return due;
    }

    /// <summary>Marks an event as delivered so it is not proposed again.</summary>
    public static void MarkDone(MentorEvent mentorEvent, DateTime now, MentorState state)
    {
        var today = DateOnly.FromDateTime(now);
        switch (mentorEvent)
        {
            case MentorEvent.CheckIn: state.LastCheckIn = today; break;
            case MentorEvent.CheckOut: state.LastCheckOut = today; break;
            case MentorEvent.Retro: state.LastRetroWeek = WeekKey(now); break;
            case MentorEvent.Pill when state.PillSlots.Count > 0: state.PillSlots.RemoveAt(0); break;
            case MentorEvent.Chat when state.ChatSlots.Count > 0: state.ChatSlots.RemoveAt(0); break;
            case MentorEvent.Review when state.ReviewSlots.Count > 0: state.ReviewSlots.RemoveAt(0); break;
            case MentorEvent.Break: state.LastBreakReminder = now; break;
        }
    }

    public static string WeekKey(DateTime date) => $"{ISOWeek.GetYear(date)}-W{ISOWeek.GetWeekOfYear(date):00}";

    public static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private bool IsWorkDay(DateTime now) =>
        settings.WorkDays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(d => Enum.TryParse<DayOfWeek>(d, ignoreCase: true, out var day) && day == now.DayOfWeek);

    /// <summary>Spreads today's pills, chats and reviews at random times, at least half an hour after the check-in.</summary>
    private void Plan(MentorState state, DateOnly today, TimeOnly start, TimeOnly end, Random random)
    {
        var from = start.AddMinutes(30);
        var minutes = Math.Max(1, (int)(end - from).TotalMinutes - 30);

        List<TimeOnly> Slots(int count) =>
            Enumerable.Range(0, Math.Clamp(count, 0, 10)).Select(_ => from.AddMinutes(random.Next(minutes))).Order().ToList();

        state.PlannedDay = today;
        state.PillSlots = Slots(settings.PillsPerDay);
        state.ChatSlots = Slots(settings.ChatsPerDay);
        state.ReviewSlots = Slots(settings.ReviewsPerDay);
    }
}
