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

    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set => Set(ref _isStreaming, value);
    }

    /// <summary>For a local answer: the question it answered, so the bubble can offer "Chiedi a Claude".</summary>
    public string? Question { get; init; }

    public Attachment? QuestionAttachment { get; init; }

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
