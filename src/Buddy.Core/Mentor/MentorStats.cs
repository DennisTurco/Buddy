using System.Globalization;
using System.Text;

namespace Buddy.Core.Mentor;

/// <summary>Week-by-week table of the counters kept by the mentor (for /andamento and the weekly retro).</summary>
public static class MentorStats
{
    public static string ToMarkdown(MentorState state, DateTime now, int weeks = 4)
    {
        var keys = Enumerable.Range(0, weeks)
            .Select(i => MentorSchedule.WeekKey(now.AddDays(-7 * i)))
            .Reverse()
            .ToList();

        if (!keys.Any(state.Weeks.ContainsKey))
        {
            return "Ancora nessun dato: l'andamento si riempie con l'uso (attività al PC, focus, check-in, pillole, ripassi).";
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Settimana | Ore attive | Focus | Check-in/out | Pillole | Ripassi | Chiacchiere | Approcci | Pause suggerite |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var key in keys)
        {
            var w = state.Weeks.GetValueOrDefault(key) ?? new WeekStats();
            var hours = (w.ActiveMinutes / 60.0).ToString("0.#", CultureInfo.GetCultureInfo("it-IT"));
            sb.AppendLine($"| {key} | {hours} | {w.FocusBlocks} ({w.FocusMinutes} min) | {w.CheckIns}/{w.CheckOuts} | {w.Pills} | {w.Reviews} | {w.Chats} | {w.Approaches} | {w.BreakReminders} |");
        }

        return sb.ToString().TrimEnd();
    }
}
