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

    /// <summary>The message before is by the same speaker: drawn close to it, as one group (see <see cref="ChatGrouping"/>).</summary>
    public bool JoinsPrevious
    {
        get => _joinsPrevious;
        set => Set(ref _joinsPrevious, value);
    }

    private bool _joinsPrevious;

    /// <summary>The message after is by the same speaker: no tail on this bubble, the last of the group has it.</summary>
    public bool JoinsNext
    {
        get => _joinsNext;
        set => Set(ref _joinsNext, value);
    }

    private bool _joinsNext;

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
public sealed class PermissionEntry : ChatEntry
{
    public PermissionEntry(PermissionRequest request)
        : base(Speaker.Claude, request.Summary)
    {
        Request = request;
        Questions = request.ToolName == "AskUserQuestion" ? QuestionView.Parse(request.Input, RefreshCanAnswer) : [];
    }

    private readonly TaskCompletionSource<PermissionDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _outcome;

    public PermissionRequest Request { get; }

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

    /// <summary>Claude's multiple-choice questions (AskUserQuestion), answered in the bubble instead of Sì/No.</summary>
    public IReadOnlyList<QuestionView> Questions { get; }

    public bool IsQuestion => Questions.Count > 0;

    public bool IsPermission => !IsQuestion;

    /// <summary>Every question has a choice or a free answer.</summary>
    public bool CanAnswer => Questions.All(q => q.IsAnswered);

    public void Allow() => Resolve(PermissionDecision.Allowed, "✓ consentito");

    public void Deny() => Resolve(PermissionDecision.Denied(), "✗ rifiutato");

    /// <summary>Sends the answers: the CLI reads them from the "answers" map of the tool input (question → labels).</summary>
    public void Answer()
    {
        if (!IsQuestion || !CanAnswer)
        {
            return;
        }

        var input = System.Text.Json.Nodes.JsonNode.Parse(Request.Input.GetRawText())!.AsObject();
        var answers = new System.Text.Json.Nodes.JsonObject();
        foreach (var question in Questions)
        {
            answers[question.Question] = question.AnswerText;
        }

        input["answers"] = answers;
        Resolve(new PermissionDecision(true, UpdatedInput: input), "✓ " + string.Join(" · ", Questions.Select(q => q.AnswerText)));
    }

    /// <summary>Closes the questions without answering: Claude is told the user chose not to.</summary>
    public void Skip() => Resolve(
        PermissionDecision.Denied("The user chose not to answer these questions. Do not ask them again; ask in plain text if you still need the answer."),
        "✗ nessuna risposta");

    private void RefreshCanAnswer() => RaisePropertyChanged(nameof(CanAnswer));

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

/// <summary>
/// What Claude did between two pieces of text (read a file, ran a command…): consecutive tool calls go in one entry,
/// shown as a single line ("🔧 5 azioni · last one") that opens to the whole list. <see cref="ChatEntry.Text"/> keeps
/// every step, one "🔧 …" line each, so search, copy and the saved conversations still see them all.
/// </summary>
public sealed class ToolActivityEntry : ChatEntry
{
    public const string Mark = "🔧 ";

    private readonly System.Collections.ObjectModel.ObservableCollection<string> _steps = [];
    private bool _isExpanded;

    public ToolActivityEntry(string step)
        : base(Speaker.System)
    {
        Steps = new(_steps);
        Add(step);
    }

    public System.Collections.ObjectModel.ReadOnlyObservableCollection<string> Steps { get; }

    /// <summary>"🔧 Legge Conversation.cs" for one step, "🔧 5 azioni" for more.</summary>
    public string Title => _steps.Count == 1 ? Mark + _steps[0] : $"{Mark}{_steps.Count} azioni";

    /// <summary>"· latest step" next to the title while a list of several is closed (what Claude is doing now).</summary>
    public string Latest => _steps.Count > 1 && !_isExpanded ? "· " + _steps[^1] : "";

    public bool HasMany => _steps.Count > 1;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            Set(ref _isExpanded, value);
            RaisePropertyChanged(nameof(Chevron));
            RaisePropertyChanged(nameof(Latest));
        }
    }

    public string Chevron => _isExpanded ? "▾" : "▸";

    public void Add(string step)
    {
        _steps.Add(step);
        Text = string.Join('\n', _steps.Select(s => Mark + s));
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(Latest));
        RaisePropertyChanged(nameof(HasMany));
    }

    /// <summary>The steps of a saved entry's text, or null when it is not (only) tool calls.</summary>
    public static IReadOnlyList<string>? StepsOf(Speaker speaker, string text)
    {
        if (speaker != Speaker.System)
        {
            return null;
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && lines.All(l => l.StartsWith(Mark, StringComparison.Ordinal))
            ? lines.Select(l => l[Mark.Length..]).ToList()
            : null;
    }
}
