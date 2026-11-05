using System.Text.Json;

namespace Buddy.Core.Claude;

public abstract record ClaudeEvent;

public sealed record ClaudeSessionStarted(string SessionId, string Model) : ClaudeEvent;

/// <summary>A chunk of assistant text, streamed while Claude writes.</summary>
public sealed record ClaudeTextDelta(string Text) : ClaudeEvent;

/// <summary>Claude started using a tool (shown as a short status line).</summary>
public sealed record ClaudeToolUse(string ToolName, string Summary) : ClaudeEvent;

/// <summary>Claude finished answering the last message.</summary>
public sealed record ClaudeTurnCompleted(bool IsError, string? Result, decimal? CostUsd, string? SessionId) : ClaudeEvent;

/// <summary>The CLI process wrote something to stderr or exited.</summary>
public sealed record ClaudeProcessMessage(string Text, bool Exited) : ClaudeEvent;

/// <summary>Claude asks permission to run a tool; the host answers with a <see cref="PermissionDecision"/>.</summary>
public sealed record PermissionRequest(string RequestId, string ToolName, JsonElement Input)
{
    public string Summary => ToolSummary.Describe(ToolName, Input);
}

public sealed record PermissionDecision(bool Allow, string? Message = null)
{
    public static PermissionDecision Allowed { get; } = new(true);
    /// <summary>
    /// The message reaches Claude as the tool result: it must say this was the user's choice, otherwise Claude
    /// reads it as a tool failure and retries or works around it.
    /// </summary>
    public static PermissionDecision Denied(
        string message = "The user denied this action from the permission prompt. Do not retry it or work around it; ask the user how to proceed.")
        => new(false, message);
}

internal static class ToolSummary
{
    public static string Describe(string toolName, JsonElement input)
    {
        string? Get(string name) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        var detail = toolName switch
        {
            "Write" or "Edit" or "Read" or "NotebookEdit" => Get("file_path"),
            "Bash" or "PowerShell" => Get("command"),
            "Glob" or "Grep" => Get("pattern"),
            "WebFetch" => Get("url"),
            "WebSearch" => Get("query"),
            "Skill" => Get("skill"),
            _ => null,
        };

        if (detail is null)
        {
            return toolName;
        }

        detail = detail.ReplaceLineEndings(" ");
        return $"{toolName}: {(detail.Length > 160 ? detail[..160] + "…" : detail)}";
    }
}
