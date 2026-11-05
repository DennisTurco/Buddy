using System.Runtime.CompilerServices;
using System.Text;
using Buddy.Core.Context;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Llm;

/// <summary>
/// Quick answers from the local model, grounded only on the vault excerpt.
/// Keeps a short rolling history so follow-up questions work.
/// </summary>
public sealed class LocalAssistant(OllamaClient ollama, ContextBuilder contextBuilder, IOptions<BuddySettings> options)
{
    private const int MaxHistoryMessages = 8;

    public const string DefaultSystemPrompt = """
        Sei un piccolo assistente da scrivania. Rispondi in italiano, in modo breve (massimo 5 righe).
        Usa SOLO le informazioni nel CONTESTO qui sotto, che viene dalle note del progetto.
        Se la risposta non è nel contesto, dillo chiaramente e suggerisci di chiedere a Claude con /c.
        Non inventare ID, date, nomi di file o stati.
        """;

    private readonly List<LlmMessage> _history = [];

    public BuiltContext? LastContext { get; private set; }

    /// <summary>The configured prompt, or the default one with the personality chosen at first run.</summary>
    private string SystemPrompt =>
        string.IsNullOrWhiteSpace(options.Value.Ollama.SystemPrompt)
            ? $"{DefaultSystemPrompt}\nTono: {Mentor.Personalities.Describe(options.Value.Mentor)}"
            : options.Value.Ollama.SystemPrompt;

    public async IAsyncEnumerable<string> AskAsync(
        string question,
        string? currentTaskId = null,
        string? attachment = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastContext = contextBuilder.Build(question, currentTaskId, attachment);

        var messages = new List<LlmMessage>
        {
            LlmMessage.System($"{SystemPrompt}\nCONTESTO:\n{LastContext.Text}"),
        };
        messages.AddRange(_history);
        messages.Add(LlmMessage.User(question));

        var answer = new StringBuilder();
        await foreach (var delta in ollama.ChatAsync(messages, cancellationToken))
        {
            answer.Append(delta);
            yield return delta;
        }

        _history.Add(LlmMessage.User(question));
        _history.Add(LlmMessage.Assistant(answer.ToString()));
        if (_history.Count > MaxHistoryMessages)
        {
            _history.RemoveRange(0, _history.Count - MaxHistoryMessages);
        }
    }

    public void Reset() => _history.Clear();
}
