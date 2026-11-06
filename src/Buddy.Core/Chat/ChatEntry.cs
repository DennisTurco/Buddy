using System.ComponentModel;
using System.Runtime.CompilerServices;
using Buddy.Core.Claude;

namespace Buddy.Core.Chat;

public enum Speaker
{
    User,
    Local,
    Claude,
    System,

    /// <summary>Proactive messages of the mentor (check-in, pills, questions).</summary>
    Mentor,
}

public enum PetState
{
    Idle,
    Think,
    Work,
    Alert,
    Error,

    /// <summary>Brief celebration after Claude finished a task.</summary>
    Happy,

    /// <summary>The mentor has something to say: waits until the chat is opened.</summary>
    Speak,
}

public enum ChatMode
{
    Local,
    Claude,
}

/// <summary>One line of the shared chat thread. Text grows while the answer is streamed.</summary>
public class ChatEntry(Speaker speaker, string text = "") : INotifyPropertyChanged
{
    private string _text = text;
    private bool _isStreaming;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Speaker Speaker { get; } = speaker;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Text
    {
        get => _text;
        set
        {
            var wasWaiting = IsWaiting;
            Set(ref _text, value);
            if (wasWaiting != IsWaiting)
            {
                RaisePropertyChanged(nameof(IsWaiting));
            }
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            var wasWaiting = IsWaiting;
            Set(ref _isStreaming, value);
            if (wasWaiting != IsWaiting)
            {
                RaisePropertyChanged(nameof(IsWaiting));
            }
        }
    }

    /// <summary>The answer has started but no text has arrived yet: the bubble shows a "typing" mark.</summary>
    public bool IsWaiting => _isStreaming && _text.Length == 0;

    /// <summary>For a local answer: the question it answered, so the bubble can offer "Chiedi a Claude".</summary>
    public string? Question { get; init; }

    public Attachment? QuestionAttachment { get; init; }

    /// <summary>Images sent with a user message, shown as thumbnails under it.</summary>
    public IReadOnlyList<AttachedImage> Images { get; init; } = [];

    public bool HasImages => Images.Count > 0;

    /// <summary>Buttons under the message: each sends its command to the chat (e.g. "/test-manuali T-3", "/modello sonnet").</summary>
    public IReadOnlyList<ChatAction> Actions
    {
        get => _actions;
        set
        {
            Set(ref _actions, value);
            RaisePropertyChanged(nameof(HasActions));
        }
    }

    private IReadOnlyList<ChatAction> _actions = [];

    public bool HasActions => _actions.Count > 0;

    /// <summary>Pinned: kept in the strip at the top of the chat until unpinned (e.g. the steps to start a service).</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    private bool _isPinned;

    /// <summary>The first line, short, for the pinned strip.</summary>
    public string Headline
    {
        get
        {
            var line = Text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim().TrimStart('#', '*', '-', ' ')).FirstOrDefault(l => l.Length > 0) ?? "";
            line = line.Replace("**", "").Replace("`", "");
            return line.Length > 90 ? line[..90] + "…" : line;
        }
    }

    /// <summary>Matches the search in the chat (Ctrl+F): drawn with a frame.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => Set(ref _isHighlighted, value);
    }

    private bool _isHighlighted;

    /// <summary>A mentor's question still waiting for the answer: the bubble offers "Spiegamelo tu".</summary>
    public bool AwaitsReply
    {
        get => _awaitsReply;
        set => Set(ref _awaitsReply, value);
    }

    private bool _awaitsReply;

    /// <summary>A finished model answer: the bubble offers quick follow-ups (simpler, an example, a summary).</summary>
    public bool CanFollowUp => Speaker is Speaker.Claude or Speaker.Local && this is not PermissionEntry;

    /// <summary>The "Chiedi a Claude" button is shown (local answer, finished, not yet escalated).</summary>
    public bool CanEscalate
    {
        get => _canEscalate;
        set => Set(ref _canEscalate, value);
    }

    private bool _canEscalate;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        RaisePropertyChanged(name);
    }

    protected void RaisePropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A pending "may Claude do this?" question, answered with the buttons in the bubble.</summary>
public sealed class PermissionEntry(PermissionRequest request) : ChatEntry(Speaker.Claude, request.Summary)
{
    private readonly TaskCompletionSource<PermissionDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _outcome;

    public PermissionRequest Request { get; } = request;

    public Task<PermissionDecision> Decision => _decision.Task;

    public bool IsPending => _outcome is null;

    public string? Outcome
    {
        get => _outcome;
        private set
        {
            Set(ref _outcome, value);
            RaisePropertyChanged(nameof(IsPending));
        }
    }

    public void Allow() => Resolve(PermissionDecision.Allowed, "✓ consentito");

    public void Deny() => Resolve(PermissionDecision.Denied(), "✗ rifiutato");

    private void Resolve(PermissionDecision decision, string outcome)
    {
        if (_decision.TrySetResult(decision))
        {
            Outcome = outcome;
        }
    }
}

/// <summary>A button under a chat message: the label shown and the command sent when it is clicked.</summary>
public sealed record ChatAction(string Label, string Command, string? Tip = null);
