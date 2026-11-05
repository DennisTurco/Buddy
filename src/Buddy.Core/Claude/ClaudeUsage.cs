using System.Globalization;
using System.Text.Json;

namespace Buddy.Core.Claude;

/// <summary>
/// How much Claude has been used: the cost of today's turns (what the CLI reports; with a Pro/Max plan it is the
/// equivalent API price, not money spent) and the plan limits the CLI reports in its "rate_limit_event" messages
/// (5-hour and weekly windows: status, share used when given, reset time). Today's total survives a restart.
/// </summary>
public sealed class ClaudeUsage(TimeProvider clock)
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ClaudeRateLimit> _limits = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;

    public ClaudeUsage() : this(TimeProvider.System)
    {
    }

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event Action? Changed;

    public DateOnly Day { get; private set; }

    public decimal TodayCost { get; private set; }

    public int TodayTurns { get; private set; }

    public IReadOnlyList<ClaudeRateLimit> Limits
    {
        get
        {
            lock (_gate)
            {
                return _limits.Values.OrderBy(l => l.Type).ToList();
            }
        }
    }

    /// <summary>Where today's total is kept (a small JSON file); loads it if it is today's.</summary>
    public void PersistTo(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { } saved && saved.Day == Today)
            {
                Day = saved.Day;
                TodayCost = saved.Cost;
                TodayTurns = saved.Turns;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file only loses today's total.
        }
    }

    public void AddTurn(decimal cost)
    {
        lock (_gate)
        {
            if (Day != Today)
            {
                Day = Today;
                TodayCost = 0;
                TodayTurns = 0;
            }

            TodayCost += Math.Max(0, cost);
            TodayTurns++;
        }

        Save();
        Changed?.Invoke();
    }

    public void Update(ClaudeRateLimit limit)
    {
        lock (_gate)
        {
            _limits[limit.Type] = limit;
        }

        Changed?.Invoke();
    }

    /// <summary>One line for a status bar, e.g. "Oggi 0,39 $ · 5 ore 42%".</summary>
    public string ShortText()
    {
        var text = $"Oggi {(Day == Today ? TodayCost : 0).ToString("0.00", Italian)} $";
        if (Limits.FirstOrDefault(l => l.Type == "five_hour") is { } five)
        {
            text += $" · 5 ore {five.ShortStatus}";
        }

        return text;
    }

    /// <summary>The full picture as markdown, for the chat (/uso) and tooltips.</summary>
    public string Describe()
    {
        var today = Day == Today;
        var lines = new List<string>
        {
            $"**Oggi**: {(today ? TodayCost : 0).ToString("0.00", Italian)} $ in {(today ? TodayTurns : 0)} turni con Claude "
            + "(con un piano Pro/Max è il prezzo equivalente delle API, non una spesa).",
        };

        var limits = Limits;
        if (limits.Count == 0)
        {
            lines.Add("Limiti del piano: ancora nessuna informazione da Claude Code (arriva con le risposte).");
        }

        foreach (var limit in limits)
        {
            lines.Add($"**{limit.Label}**: {limit.LongStatus}");
        }

        return string.Join("\n", lines);
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new Saved(Day, TodayCost, TodayTurns), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Saved(DateOnly Day, decimal Cost, int Turns);
}

/// <summary>
/// One plan limit from Claude Code: <paramref name="Type"/> "five_hour", "seven_day", "seven_day_opus"…,
/// <paramref name="Status"/> "allowed", "allowed_warning" or "rejected", <paramref name="Utilization"/> 0–1 when given.
/// </summary>
public sealed record ClaudeRateLimit(string Type, string Status, double? Utilization, DateTimeOffset? ResetsAt)
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public string Label => Type switch
    {
        "five_hour" => "Finestra di 5 ore",
        "seven_day" => "Settimana",
        "seven_day_opus" => "Settimana (Opus)",
        "seven_day_sonnet" => "Settimana (Sonnet)",
        _ => Type.Replace('_', ' '),
    };

    public bool IsWarning => Status.Contains("warning", StringComparison.OrdinalIgnoreCase);

    public bool IsBlocked => Status.Equals("rejected", StringComparison.OrdinalIgnoreCase);

    public string ShortStatus => IsBlocked ? "esaurita" : Utilization is { } u ? $"{u * 100:0}%" : IsWarning ? "quasi al limite" : "ok";

    public string LongStatus
    {
        get
        {
            var state = IsBlocked ? "limite raggiunto" : Utilization is { } u ? $"usato {u * 100:0}%" : IsWarning ? "vicino al limite" : "nei limiti";
            return ResetsAt is { } reset ? $"{state} · si azzera {Reset(reset)}" : state;
        }
    }

    private static string Reset(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        return local.Date == DateTime.Today
            ? $"alle {local:HH:mm}"
            : local.ToString("ddd d MMM 'alle' HH:mm", Italian);
    }

    /// <summary>Reads the CLI's "rate_limit_info" object; null when it carries no usable type.</summary>
    public static ClaudeRateLimit? Parse(JsonElement info)
    {
        string? Text(params string[] names) =>
            names.Select(n => info.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(v => v is not null);

        double? Number(params string[] names) =>
            names.Select(n => info.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null).FirstOrDefault(v => v is not null);

        var type = Text("rateLimitType", "rate_limit_type", "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        var utilization = Number("utilization", "used_percentage", "usedPercentage");
        if (utilization > 1)
        {
            utilization /= 100; // some versions report a percentage
        }

        var reset = Number("resetsAt", "resets_at");
        return new ClaudeRateLimit(
            type,
            Text("status") ?? "allowed",
            utilization,
            reset is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)(seconds > 1e11 ? seconds / 1000 : seconds)) : null);
    }
}
