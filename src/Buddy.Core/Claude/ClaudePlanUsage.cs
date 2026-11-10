using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Buddy.Core.Claude;

/// <summary>
/// Reads the plan limits the way Claude Code's /usage does: the account's OAuth token from the CLI's credentials file
/// and Anthropic's usage endpoint, which gives the share used and the reset time of the 5-hour and weekly windows.
/// The token is only read, never refreshed: Claude Code keeps it fresh. Results go into <see cref="ClaudeUsage"/>.
/// </summary>
public sealed class ClaudePlanUsage : IDisposable
{
    private readonly ClaudeUsage _usage;
    private readonly TimeProvider _clock;

    public ClaudePlanUsage(ClaudeUsage usage, TimeProvider clock)
    {
        _usage = usage;
        _clock = clock;
        usage.TurnAdded += () => _ = RefreshAsync(); // after a turn the bars move: read them again (at most every 30 s)
    }

    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _busy = new(1, 1);
    private ITimer? _timer;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>After a 429 the endpoint is left alone until then, longer at each refusal (see <see cref="Backoff"/>).</summary>
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    private TimeSpan _backoff = TimeSpan.Zero;

    /// <summary>The next pause after "too many requests": 5 minutes, then doubled, at most an hour.</summary>
    internal static TimeSpan Backoff(TimeSpan previous, TimeSpan? retryAfter)
    {
        var next = previous == TimeSpan.Zero ? TimeSpan.FromMinutes(5) : TimeSpan.FromTicks(Math.Min(previous.Ticks * 2, TimeSpan.FromHours(1).Ticks));
        return retryAfter is { } wait && wait > next ? wait : next;
    }

    internal const string NoLogin = "Limiti non letti: Claude Code non risulta collegato (in un terminale: claude, poi /login).";

    /// <summary>Asks for the limits now and then every <paramref name="every"/>.</summary>
    public void Start(TimeSpan every)
    {
        _timer?.Dispose();

        // Limits read a moment ago (a restart): the first request waits, the endpoint limits how often it is asked.
        var first = _usage.LimitsReadAt is { } read ? read + TimeSpan.FromMinutes(1) - _clock.GetUtcNow() : TimeSpan.Zero;
        _timer = _clock.CreateTimer(_ => _ = RefreshAsync(force: true), null, first > TimeSpan.Zero ? first : TimeSpan.Zero, every);
    }

    /// <summary>Fetches the limits unless it was done less than 30 seconds ago; false when they could not be read.</summary>
    public async Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if ((!force && _clock.GetUtcNow() - _last < MinimumGap) || _clock.GetUtcNow() < _pausedUntil)
        {
            return true;
        }

        if (!await _busy.WaitAsync(0, cancellationToken))
        {
            return true;
        }

        try
        {
            _last = _clock.GetUtcNow();
            if (ReadToken() is not { } token)
            {
                return false; // ReadToken said why
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await Http.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode == 429)
            {
                // Asked too often (several restarts, other tools on the same account): wait longer each time.
                _backoff = Backoff(_backoff, response.Headers.RetryAfter?.Delta);
                _pausedUntil = _clock.GetUtcNow() + _backoff;
                _usage.SetLimitsProblem($"Limiti non aggiornati: troppe richieste a Claude, riprovo alle {_pausedUntil.ToLocalTime():H:mm}.");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _usage.SetLimitsProblem(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "Limiti non letti: il login di Claude Code non è valido (in un terminale: claude, poi /login)."
                    : $"Limiti non letti ora (errore {(int)response.StatusCode}): riprovo più tardi.");
                return false;
            }

            var limits = Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            foreach (var limit in limits)
            {
                _usage.Update(limit);
            }

            _backoff = TimeSpan.Zero;
            if (limits.Count > 0)
            {
                _usage.LimitsRead(_clock.GetUtcNow());
            }

            return limits.Count > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _usage.SetLimitsProblem("Limiti non letti: Claude non risponde (connessione?), riprovo più tardi.");
            return false;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _usage.SetLimitsProblem("Limiti non letti: risposta o credenziali illeggibili, riprovo più tardi.");
            return false;
        }
        finally
        {
            _busy.Release();
        }
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>The CLI's credentials file: CLAUDE_CONFIG_DIR or ~/.claude (on macOS the token is in the Keychain instead).</summary>
    internal static string CredentialsPath()
    {
        var folder = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        return Path.Combine(folder, ".credentials.json");
    }

    private string? ReadToken()
    {
        var path = CredentialsPath();
        if (!File.Exists(path))
        {
            _usage.SetLimitsProblem(NoLogin);
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
            || !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String)
        {
            _usage.SetLimitsProblem(NoLogin);
            return null;
        }

        // An expired token would only get a 401: wait for Claude Code to refresh it.
        if (oauth.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.Number
            && DateTimeOffset.FromUnixTimeMilliseconds(expires.GetInt64()) < _clock.GetUtcNow())
        {
            _usage.SetLimitsProblem("Limiti in attesa: il login di Claude Code va rinnovato (succede da solo alla prossima risposta di Claude).");
            return null;
        }

        return token.GetString();
    }

    /// <summary>
    /// Reads the endpoint's answer: the "limits" list when present (session, weekly for all models, weekly per model),
    /// otherwise the older five_hour / seven_day / seven_day_opus / seven_day_sonnet objects (utilization 0–100).
    /// </summary>
    internal static IReadOnlyList<ClaudeRateLimit> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<ClaudeRateLimit>();

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                var kind = Text(item, "kind");
                var type = kind switch
                {
                    "session" => "five_hour",
                    "weekly_all" => "seven_day",
                    "weekly_scoped" when ScopeName(item) is { } model => "seven_day_" + model.ToLowerInvariant(),
                    _ => null,
                };
                if (type is null)
                {
                    continue;
                }

                var percent = Number(item, "percent");
                var severity = Text(item, "severity") ?? "normal";
                result.Add(new ClaudeRateLimit(type, Status(percent, severity), percent / 100, Date(item, "resets_at"))
                {
                    Model = kind == "weekly_scoped" ? ScopeName(item) : null,
                });
            }
        }

        if (result.Count > 0)
        {
            return result;
        }

        foreach (var type in new[] { "five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet" })
        {
            if (root.TryGetProperty(type, out var window) && window.ValueKind == JsonValueKind.Object)
            {
                var percent = Number(window, "utilization");
                result.Add(new ClaudeRateLimit(type, Status(percent, "normal"), percent / 100, Date(window, "resets_at")));
            }
        }

        return result;
    }

    private static string Status(double? percent, string severity) =>
        percent >= 100 ? "rejected" : severity.Equals("normal", StringComparison.OrdinalIgnoreCase) ? "allowed" : "allowed_warning";

    private static string? ScopeName(JsonElement item) =>
        item.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
        && scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
            ? Text(model, "display_name")
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
