using System.Globalization;

namespace Buddy.Core.Vault;

/// <summary>A card to bring back on a day ("[promemoria:: 2026-11-12]" on the item).</summary>
public sealed record Reminder(BoardItem Item, BoardColumn Column, DateOnly Day);

/// <summary>
/// Reminders on the board's cards: the [promemoria:: date] field, set from the side panel. On that day (or later, if the
/// buddy was off) the chat brings the card back with a button to open it.
/// </summary>
public static class Reminders
{
    public const string Field = "promemoria";

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>The reminders due by <paramref name="today"/>, oldest first.</summary>
    public static IReadOnlyList<Reminder> Due(ProjectBoard board, DateOnly today) =>
        board.Columns.Where(c => c.Key is not ("fatti" or "rifiutati"))
            .SelectMany(c => c.Items.Select(i => (Column: c, Item: i)))
            .Select(x => x.Item.Field(Field) is { } text && DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var day)
                ? new Reminder(x.Item, x.Column, day) : null)
            .OfType<Reminder>()
            .Where(r => r.Day <= today)
            .OrderBy(r => r.Day)
            .ToList();

    /// <summary>
    /// A day from what the user types: "2026-11-12", "12/11", "domani", "lunedì", "+3" (days); null when not understood
    /// or when the text is empty (= remove the reminder, handled by the caller).
    /// </summary>
    public static DateOnly? ParseDay(string text, DateOnly today)
    {
        text = text.Trim().ToLowerInvariant();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }

        if (DateOnly.TryParseExact(text, ["d/M", "d/M/yyyy", "d-M", "d.M"], Italian, DateTimeStyles.None, out var short_))
        {
            var day = short_.Year == 1 ? new DateOnly(today.Year, short_.Month, short_.Day) : short_;
            return day < today ? day.AddYears(1) : day;
        }

        if (text is "oggi")
        {
            return today;
        }

        if (text is "domani")
        {
            return today.AddDays(1);
        }

        if (text.StartsWith('+') && int.TryParse(text[1..].Trim().TrimEnd('g', 'i', 'o', 'r', 'n').Trim(), out var days) && days > 0)
        {
            return today.AddDays(days);
        }

        var names = Italian.DateTimeFormat.DayNames.Select(n => n.ToLowerInvariant().Replace("ì", "i")).ToList();
        var index = names.IndexOf(text.Replace("ì", "i"));
        if (index >= 0)
        {
            var ahead = (index - (int)today.DayOfWeek + 7) % 7;
            return today.AddDays(ahead == 0 ? 7 : ahead);
        }

        return null;
    }
}
