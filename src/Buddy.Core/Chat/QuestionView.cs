using System.ComponentModel;
using System.Text.Json;

namespace Buddy.Core.Chat;

/// <summary>One of Claude's AskUserQuestion questions: the options to pick and a free answer ("Altro").</summary>
public sealed class QuestionView : INotifyPropertyChanged
{
    private readonly Action _changed;
    private string _other = "";

    private QuestionView(string question, string header, bool multiSelect, IEnumerable<(string Label, string Description)> options, Action changed)
    {
        Question = question;
        Header = header;
        MultiSelect = multiSelect;
        _changed = changed;
        Options = options.Select(o => new OptionView(this, o.Label, o.Description)).ToList();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Question { get; }

    public string Header { get; }

    public bool MultiSelect { get; }

    public IReadOnlyList<OptionView> Options { get; }

    /// <summary>Free text written instead of (or, with multiple choice, besides) the options.</summary>
    public string Other
    {
        get => _other;
        set
        {
            if (_other == value)
            {
                return;
            }

            _other = value ?? "";
            if (!MultiSelect && _other.Trim().Length > 0)
            {
                foreach (var option in Options)
                {
                    option.SetSelected(false);
                }
            }

            Changed(nameof(Other));
        }
    }

    public bool IsAnswered => AnswerText.Length > 0;

    /// <summary>What the CLI receives: the chosen labels (comma-separated) and the free answer.</summary>
    public string AnswerText => string.Join(", ", Options.Where(o => o.IsSelected).Select(o => o.Label)
        .Concat(Other.Trim() is { Length: > 0 } other ? [other] : []));

    internal void Select(OptionView chosen, bool selected)
    {
        if (selected && !MultiSelect)
        {
            foreach (var option in Options.Where(o => o != chosen))
            {
                option.SetSelected(false);
            }

            _other = "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Other)));
        }

        chosen.SetSelected(selected);
        Changed(nameof(AnswerText));
    }

    private void Changed(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAnswered)));
        _changed();
    }

    /// <summary>The questions of an AskUserQuestion input; empty when it has none.</summary>
    public static IReadOnlyList<QuestionView> Parse(JsonElement input, Action changed)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        static string Text(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        return questions.EnumerateArray()
            .Where(q => Text(q, "question").Length > 0)
            .Select(q => new QuestionView(
                Text(q, "question"),
                Text(q, "header"),
                q.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True,
                q.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                    ? options.EnumerateArray().Select(o => (Text(o, "label"), Text(o, "description"))).Where(o => o.Item1.Length > 0)
                    : [],
                changed))
            .ToList();
    }
}

/// <summary>An option of a <see cref="QuestionView"/>: a toggle button in the bubble.</summary>
public sealed class OptionView(QuestionView owner, string label, string description) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; } = label;

    public string Description { get; } = description;

    public bool HasDescription => Description.Length > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                owner.Select(this, value);
            }
        }
    }

    internal void SetSelected(bool value)
    {
        if (_isSelected != value)
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
