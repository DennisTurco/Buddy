using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Llm;

/// <summary>Minimal streaming client for Ollama's /api/chat.</summary>
public sealed class OllamaClient(HttpClient http, IOptions<BuddySettings> options)
{
    private readonly OllamaSettings _settings = options.Value.Ollama;

    /// <summary>
    /// Loads the model into memory without generating anything, so the first real question does not pay
    /// the cold-start cost (about 10 seconds on a laptop). Failures are ignored: Ollama may not be running yet.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new ChatRequest(_settings.Model, [], Stream: false, KeepAlive: _settings.KeepAlive);
            using var response = await http.PostAsync(
                new Uri(new Uri(_settings.BaseUrl), "/api/chat"),
                JsonContent.Create(request, OllamaJson.Default.ChatRequest),
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    /// <summary>Names of the models installed in Ollama, for the settings window. Empty if Ollama is not reachable.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(string? baseUrl = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(new Uri(baseUrl ?? _settings.BaseUrl), "/api/tags"), cancellationToken);
            response.EnsureSuccessStatusCode();
            var tags = await response.Content.ReadFromJsonAsync(OllamaJson.Default.TagsResponse, cancellationToken);
            return tags?.Models.Select(m => m.Name).Order().ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// One whole answer, not streamed: for the short jobs of <see cref="LocalAi"/>. With <paramref name="json"/> Ollama
    /// constrains the output to valid JSON. Low temperature: these are extraction jobs, not conversation.
    /// </summary>
    public async Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, bool json = false, string? model = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest(
            string.IsNullOrWhiteSpace(model) ? _settings.Model : model,
            messages.Select(m => new WireMessage(m.Role, m.Content)).ToList(),
            Stream: false,
            KeepAlive: _settings.KeepAlive,
            Format: json ? "json" : null,
            Options: new GenerationOptions(Temperature: 0.2));
        using var response = await http.PostAsync(new Uri(new Uri(_settings.BaseUrl), "/api/chat"),
            JsonContent.Create(request, OllamaJson.Default.ChatRequest), cancellationToken);
        response.EnsureSuccessStatusCode();
        var chunk = await response.Content.ReadFromJsonAsync(OllamaJson.Default.ChatChunk, cancellationToken);
        if (chunk?.Error is { } error)
        {
            throw new InvalidOperationException($"Ollama: {error}");
        }

        return chunk?.Message?.Content ?? "";
    }

    /// <summary>One vector per text (Ollama /api/embed), for the search by meaning.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync(new Uri(new Uri(_settings.BaseUrl), "/api/embed"),
            JsonContent.Create(new EmbedRequest(model, texts.ToList(), _settings.KeepAlive), OllamaJson.Default.EmbedRequest), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(OllamaJson.Default.EmbedResponse, cancellationToken);
        return result?.Embeddings ?? [];
    }

    public async IAsyncEnumerable<string> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest(
            _settings.Model,
            messages.Select(m => new WireMessage(m.Role, m.Content)).ToList(),
            Stream: true,
            KeepAlive: _settings.KeepAlive);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_settings.BaseUrl), "/api/chat"))
        {
            Content = JsonContent.Create(request, OllamaJson.Default.ChatRequest),
        };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var chunk = JsonSerializer.Deserialize(line, OllamaJson.Default.ChatChunk);
            if (chunk?.Error is { } error)
            {
                throw new InvalidOperationException($"Ollama: {error}");
            }

            if (!string.IsNullOrEmpty(chunk?.Message?.Content))
            {
                yield return chunk.Message.Content;
            }

            if (chunk?.Done == true)
            {
                yield break;
            }
        }
    }

}

internal sealed record ChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<WireMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("keep_alive")] string KeepAlive,
    [property: JsonPropertyName("format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null,
    [property: JsonPropertyName("options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GenerationOptions? Options = null);

internal sealed record GenerationOptions([property: JsonPropertyName("temperature")] double Temperature);

internal sealed record EmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] List<string> Input,
    [property: JsonPropertyName("keep_alive")] string KeepAlive);

internal sealed record EmbedResponse([property: JsonPropertyName("embeddings")] List<float[]>? Embeddings);

internal sealed record WireMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record ChatChunk(
    [property: JsonPropertyName("message")] WireMessage? Message,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record TagsResponse([property: JsonPropertyName("models")] List<TagModel> Models);

internal sealed record TagModel([property: JsonPropertyName("name")] string Name);

[JsonSerializable(typeof(TagsResponse))]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatChunk))]
[JsonSerializable(typeof(EmbedRequest))]
[JsonSerializable(typeof(EmbedResponse))]
internal sealed partial class OllamaJson : JsonSerializerContext;

public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string content) => new("system", content);
    public static LlmMessage User(string content) => new("user", content);
    public static LlmMessage Assistant(string content) => new("assistant", content);
}
