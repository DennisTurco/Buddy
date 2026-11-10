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

    /// <summary>Raised after a Claude turn was counted, before <see cref="Changed"/>.</summary>
    public event Action? TurnAdded;

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public event Action? Changed;

    public DateOnly Day { get; private set; }

    /// <summary>When the plan limits were last read from the usage endpoint (kept across restarts).</summary>
    public DateTimeOffset? LimitsReadAt { get; private set; }

    /// <summary>Why the plan limits could not be read just now (no login, too many requests…), or null.</summary>
    public string? LimitsProblem { get; private set; }

    /// <summary>The limits were read from the endpoint now: kept, with the time, for the next start.</summary>
    public void LimitsRead(DateTimeOffset at)
    {
        LimitsReadAt = at;
        LimitsProblem = null;
        Save();
        Changed?.Invoke();
    }

    public void SetLimitsProblem(string? problem)
    {
        if (problem == LimitsProblem)
        {
            return;
        }

        LimitsProblem = problem;
        Changed?.Invoke();
    }

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
            var saved = File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) : null;

            // The averages per model are kept across days; the totals only for today.
            foreach (var (model, stat) in saved?.Models ?? [])
            {
                _models[model] = stat;
            }

            foreach (var (day, stat) in saved?.Days ?? [])
            {
                _days[day] = stat;
            }

            // The last limits read survive a restart, but not a window that has already reset.
            var now = clock.GetUtcNow();
            foreach (var limit in saved?.Limits ?? [])
            {
                if (limit.ResetsAt is not { } reset || reset > now)
                {
                    _limits[limit.Type] = new ClaudeRateLimit(limit.Type, limit.Status, limit.Utilization, limit.ResetsAt) { Model = limit.Model };
                }
            }

            LimitsReadAt = _limits.Count > 0 ? saved?.LimitsReadAt : null;

            if (saved is not null && saved.Day == Today)
            {
                Day = saved.Day;
                TodayCost = saved.Cost;
                TodayTurns = saved.Turns;
                _days.TryAdd(Day, new ModelStat(TodayTurns, TodayCost)); // a file from before the history was kept
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file only loses today's total.
        }
    }

    public void AddTurn(decimal cost, string? model = null)
    {
        lock (_gate)
        {
            var key = ModelKey(model);
            var stat = _models.GetValueOrDefault(key) ?? new ModelStat(0, 0);
            _models[key] = new ModelStat(stat.Turns + 1, stat.Cost + Math.Max(0, cost));
            var today = _days.GetValueOrDefault(Today) ?? new ModelStat(0, 0);
            _days[Today] = new ModelStat(today.Turns + 1, today.Cost + Math.Max(0, cost));

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
        TurnAdded?.Invoke();
        Changed?.Invoke();
    }

    public void Update(ClaudeRateLimit limit)
    {
        lock (_gate)
        {
            // The CLI's rate_limit_event often has no share used: keep the one read from the usage endpoint.
            if (limit.Utilization is null && _limits.TryGetValue(limit.Type, out var known) && known.Utilization is not null
                && (limit.ResetsAt is null || known.ResetsAt is null || Math.Abs((limit.ResetsAt.Value - known.ResetsAt.Value).TotalMinutes) < 5))
            {
                limit = limit with { Utilization = known.Utilization, ResetsAt = limit.ResetsAt ?? known.ResetsAt, Model = known.Model };
            }

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

        if (LimitsProblem is { } problem)
        {
            lines.Add($"⚠ {problem}" + (limits.Count > 0 && LimitsReadAt is { } read ? $" (dati delle {read.ToLocalTime():H:mm})" : ""));
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
            List<SavedLimit> limits;
            lock (_gate)
            {
                limits = _limits.Values.Select(l => new SavedLimit(l.Type, l.Status, l.Utilization, l.ResetsAt, l.Model)).ToList();
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(new Saved(Day, TodayCost, TodayTurns, new Dictionary<string, ModelStat>(_models),
                new Dictionary<DateOnly, ModelStat>(_days.Where(d => d.Key.DayNumber >= Today.DayNumber - KeptDays)), limits, LimitsReadAt), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Saved(DateOnly Day, decimal Cost, int Turns, Dictionary<string, ModelStat>? Models = null,
        Dictionary<DateOnly, ModelStat>? Days = null, List<SavedLimit>? Limits = null, DateTimeOffset? LimitsReadAt = null);

    private sealed record SavedLimit(string Type, string Status, double? Utilization, DateTimeOffset? ResetsAt, string? Model);

    /// <summary>How many days of spending are kept for the Costi and Settimana pages.</summary>
    public const int KeptDays = 120;

    private readonly Dictionary<DateOnly, ModelStat> _days = [];

    /// <summary>Spending per day (cost, turns), the last <see cref="KeptDays"/> days.</summary>
    public IReadOnlyDictionary<DateOnly, ModelStat> Days
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<DateOnly, ModelStat>(_days);
            }
        }
    }

    /// <summary>Turns and cost per model ("opus", "predefinito"…), since the buddy started counting.</summary>
    public IReadOnlyDictionary<string, ModelStat> Models
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, ModelStat>(_models, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public sealed record ModelStat(int Turns, decimal Cost);

    private readonly Dictionary<string, ModelStat> _models = new(StringComparer.OrdinalIgnoreCase);

    private static string ModelKey(string? model) => string.IsNullOrWhiteSpace(model) ? "predefinito" : model.Trim().ToLowerInvariant();

    /// <summary>Average cost of one turn with <paramref name="model"/>, from your own turns; null until there are 3.</summary>
    public decimal? AverageTurnCost(string? model)
    {
        lock (_gate)
        {
            return _models.GetValueOrDefault(ModelKey(model)) is { Turns: >= 3 } stat ? stat.Cost / stat.Turns : null;
        }
    }

    /// <summary>"~0,15 $ a turno" for the model, or "" when there is no estimate yet.</summary>
    public string AverageText(string? model) =>
        AverageTurnCost(model) is { } average ? $"~{average.ToString("0.00", Italian)} $ a turno" : "";
}

/// <summary>
/// One plan limit from Claude Code: <paramref name="Type"/> "five_hour", "seven_day", "seven_day_opus"…,
/// <paramref name="Status"/> "allowed", "allowed_warning" or "rejected", <paramref name="Utilization"/> 0–1 when given.
/// </summary>
public sealed record ClaudeRateLimit(string Type, string Status, double? Utilization, DateTimeOffset? ResetsAt)
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>The model a weekly limit applies to ("Opus", "Fable"…), when the usage endpoint names it.</summary>
    public string? Model { get; init; }

    /// <summary>Same words as Claude Code's usage panel ("5-hour limit", "Weekly · all models").</summary>
    public string Label => Type switch
    {
        "five_hour" => "Limite 5 ore",
        "seven_day" => "Settimana · tutti i modelli",
        _ when Model is { } model => $"Settimana · {model}",
        "seven_day_opus" => "Settimana · Opus",
        "seven_day_sonnet" => "Settimana · Sonnet",
        _ when Type.StartsWith("seven_day_", StringComparison.Ordinal) => "Settimana · " + Capitalize(Type["seven_day_".Length..]),
        _ => Type.Replace('_', ' '),
    };

    /// <summary>"Si azzera alle 17:00" (today) or "Si azzera mar 2:00", like Claude Code's "Resets at 5:00 PM".</summary>
    public string ResetText => ResetsAt is { } reset
        ? reset.ToLocalTime().Date == DateTime.Today
            ? $"Si azzera alle {reset.ToLocalTime():H:mm}"
            : $"Si azzera {reset.ToLocalTime().ToString("ddd H:mm", Italian)}"
        : "";

    /// <summary>"42%" when the share is known, otherwise the short state.</summary>
    public string PercentText => Utilization is { } u ? $"{Math.Round(u * 100):0}%" : ShortStatus;

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

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
