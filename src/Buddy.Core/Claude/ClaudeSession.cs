using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Claude;

/// <summary>
/// One live Claude Code conversation, driven through the CLI in headless streaming mode:
/// user messages go in on stdin and events come out on stdout, both as JSON lines.
/// Permission prompts use the CLI's stdio control protocol (--permission-prompt-tool stdio):
/// the CLI sends a "can_use_tool" control_request and waits for our control_response.
/// </summary>
public sealed class ClaudeSession : IAsyncDisposable
{
    private readonly BuddySettings _settings;
    private readonly Func<PermissionRequest, Task<PermissionDecision>> _askPermission;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private bool _streamedTextSinceLastMessage;
    private int _requestCounter;

    public ClaudeSession(IOptions<BuddySettings> options, Func<PermissionRequest, Task<PermissionDecision>> askPermission)
    {
        _settings = options.Value;
        _askPermission = askPermission;
    }

    public event Action<ClaudeEvent>? EventReceived;

    public string? SessionId { get; private set; }

    /// <summary>The model this process runs with, instead of the configured one (automatic choice per message).</summary>
    public string? ModelOverride { get; init; }

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string? resumeSessionId = null)
    {
        if (IsRunning)
        {
            return;
        }

        var claude = _settings.Claude;
        var startInfo = new ProcessStartInfo(ClaudeExecutable.Resolve(claude.Executable))
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(claude.WorkingDirectory) ? _settings.ProjectPath : claude.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in BuildArguments(resumeSessionId ?? SessionId))
        {
            startInfo.ArgumentList.Add(arg);
        }

        RemoveInheritedHostVariables(startInfo.Environment);

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Impossibile avviare Claude Code.");
        _ = Task.Run(() => ReadStdoutAsync(_process));
        _ = Task.Run(() => ReadStderrAsync(_process));
    }

    public Task SendAsync(string text, IReadOnlyList<Chat.AttachedImage>? images = null)
    {
        _streamedTextSinceLastMessage = false;

        // Plain string content for text only; a content-block array when images are attached.
        JsonNode content = text;
        if (images is { Count: > 0 })
        {
            var blocks = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
            foreach (var image in images)
            {
                blocks.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = image.Base64 },
                });
            }

            content = blocks;
        }

        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = null,
        };
        return WriteLineAsync(message);
    }

    /// <summary>Asks the CLI to stop the current turn (same as pressing Esc in the terminal).</summary>
    public Task InterruptAsync() => WriteLineAsync(new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = $"buddy_{Interlocked.Increment(ref _requestCounter)}",
        ["request"] = new JsonObject { ["subtype"] = "interrupt" },
    });

    /// <summary>
    /// When the buddy is itself started from a Claude Code session (e.g. the desktop app or a terminal inside it),
    /// it inherits variables that tell the CLI a host manages its auth and session. The child CLI must behave as a
    /// standalone install and use the user's own login, so those variables are dropped.
    /// </summary>
    internal static void RemoveInheritedHostVariables(IDictionary<string, string?> environment)
    {
        var inherited = environment.Keys
            .Where(k => k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                        || k.StartsWith("CLAUDE_AGENT_SDK_", StringComparison.OrdinalIgnoreCase)
                        || k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
                        || k.Equals("CLAUDE_PID", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in inherited)
        {
            environment.Remove(key);
        }
    }

    internal IEnumerable<string> BuildArguments(string? resumeSessionId)
    {
        var claude = _settings.Claude;
        yield return "-p";
        yield return "--input-format";
        yield return "stream-json";
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";
        yield return "--include-partial-messages";
        yield return "--permission-prompt-tool";
        yield return "stdio";

        foreach (var dir in claude.AdditionalDirectories.Append(_settings.ProjectPath).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return "--add-dir";
            yield return dir;
        }

        foreach (var tool in claude.AllowedTools())
        {
            yield return "--allowedTools";
            yield return tool;
        }

        foreach (var tool in claude.DeniedTools())
        {
            yield return "--disallowedTools";
            yield return tool;
        }

        if (!string.IsNullOrWhiteSpace(claude.PermissionMode) && claude.PermissionMode != "default")
        {
            yield return "--permission-mode";
            yield return claude.PermissionMode.Trim();
        }

        if (_settings.Assistant.QuickReplies)
        {
            yield return "--append-system-prompt";
            yield return Chat.QuickReplies.Instruction;
        }

        var model = ModelOverride ?? claude.Model;
        if (!string.IsNullOrWhiteSpace(model))
        {
            yield return "--model";
            yield return model;
        }

        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            yield return "--resume";
            yield return resumeSessionId;
        }
    }

    private async Task ReadStdoutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                try
                {
                    await HandleLineAsync(line);
                }
                catch (JsonException)
                {
                    // Not a protocol line (e.g. a warning printed by the launcher): ignore.
                }
            }
        }
        finally
        {
            string text;
            try
            {
                await process.WaitForExitAsync();
                text = $"Claude Code terminato (codice {process.ExitCode}).";
            }
            catch (InvalidOperationException)
            {
                text = "Claude Code terminato.";
            }

            Raise(new ClaudeProcessMessage(text, Exited: true));
        }
    }

    private async Task ReadStderrAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                Raise(new ClaudeProcessMessage(line, Exited: false));
            }
        }
    }

    internal async Task HandleLineAsync(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        switch (root.GetString("type"))
        {
            case "system" when root.GetString("subtype") == "init":
                SessionId = root.GetString("session_id");
                Raise(new ClaudeSessionStarted(SessionId ?? "", root.GetString("model") ?? ""));
                break;

            case "stream_event":
                HandleStreamEvent(root.GetProperty("event"));
                break;

            case "assistant":
                HandleAssistantMessage(root.GetProperty("message"));
                break;

            case "result":
                SessionId = root.GetString("session_id") ?? SessionId;
                Raise(new ClaudeTurnCompleted(
                    root.TryGetProperty("is_error", out var isError) && isError.GetBoolean(),
                    root.GetString("result"),
                    root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null,
                    SessionId));
                break;

            case "rate_limit_event" when root.TryGetProperty("rate_limit_info", out var info) && ClaudeRateLimit.Parse(info) is { } limit:
                Raise(new ClaudeRateLimitChanged(limit));
                break;

            case "control_request":
                // Not awaited: the user may take a while to answer, and stdout must keep being read meanwhile.
                _ = HandleControlRequestAsync(root.GetString("request_id")!, root.GetProperty("request").Clone());
                break;
        }

        await Task.CompletedTask;
    }

    private void HandleStreamEvent(JsonElement evt)
    {
        if (evt.GetString("type") == "content_block_delta"
            && evt.TryGetProperty("delta", out var delta)
            && delta.GetString("type") == "text_delta"
            && delta.GetString("text") is { Length: > 0 } text)
        {
            _streamedTextSinceLastMessage = true;
            Raise(new ClaudeTextDelta(text));
        }
    }

    private void HandleAssistantMessage(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (block.GetString("type"))
            {
                // Text normally arrives as deltas; full blocks are only used when nothing was streamed
                // (e.g. synthetic error messages from the CLI).
                case "text" when !_streamedTextSinceLastMessage:
                    Raise(new ClaudeTextDelta(block.GetString("text") ?? ""));
                    break;
                case "tool_use":
                    var name = block.GetString("name") ?? "?";
                    Raise(new ClaudeToolUse(name, ToolSummary.Describe(name, block.GetProperty("input"))));
                    break;
            }
        }

        _streamedTextSinceLastMessage = false;
    }

    private async Task HandleControlRequestAsync(string requestId, JsonElement request)
    {
        if (request.GetString("subtype") != "can_use_tool")
        {
            await WriteControlErrorAsync(requestId, $"Richiesta non supportata dal buddy: {request.GetString("subtype")}");
            return;
        }

        var input = request.TryGetProperty("input", out var i) ? i : default;
        PermissionDecision decision;
        try
        {
            decision = await _askPermission(new PermissionRequest(requestId, request.GetString("tool_name") ?? "?", input));
        }
        catch (Exception ex)
        {
            decision = PermissionDecision.Denied($"Errore nel buddy: {ex.Message}");
        }

        JsonObject response = decision.Allow
            ? new JsonObject
            {
                ["behavior"] = "allow",
                ["updatedInput"] = decision.UpdatedInput?.DeepClone() ?? JsonNode.Parse(input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText()),
            }
            : new JsonObject { ["behavior"] = "deny", ["message"] = decision.Message ?? "Rifiutato." };

        await WriteLineAsync(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId,
                ["response"] = response,
            },
        });
    }

    private Task WriteControlErrorAsync(string requestId, string error) => WriteLineAsync(new JsonObject
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = error },
    });

    private async Task WriteLineAsync(JsonNode node)
    {
        var process = _process ?? throw new InvalidOperationException("Sessione Claude non avviata.");
        await _writeLock.WaitAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(node.ToJsonString());
            await process.StandardInput.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Raise(ClaudeEvent evt) => EventReceived?.Invoke(evt);

    /// <summary>Ends the CLI process. <see cref="SessionId"/> is kept so a new process can resume the conversation.</summary>
    public async Task StopAsync()
    {
        if (_process is not { } process)
        {
            return;
        }

        _process = null;
        try
        {
            process.StandardInput.Close();
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeLock.Dispose();
    }
}

internal static class JsonElementExtensions
{
    public static string? GetString(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
