using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Buddy.Core.Llm;
using Buddy.Core.Vault;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Context;

/// <summary>A piece of a note found by meaning, with how close it is to the question (cosine, 0–1).</summary>
public sealed record SemanticHit(string RelativePath, string Heading, string Text, double Score)
{
    public string Title => Path.GetFileNameWithoutExtension(RelativePath);
}

/// <summary>
/// Search by meaning over the project's notes: each note is cut at its headings (long sections in ~1000-character
/// pieces), each piece turned into a vector by an Ollama embedding model, and a question finds the closest pieces
/// even without sharing a word. Vectors are cached on disk by the piece's text, so after the first time only the
/// pieces that changed are computed again. Everything fails soft: without Ollama or the model, no hits.
/// </summary>
public sealed class SemanticIndex
{
    private const int MaxChunkChars = 1000;
    private const int MinChunkChars = 40;
    private const int Batch = 32;

    private readonly VaultIndex _vault;
    private readonly IOptions<BuddySettings> _options;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> _embed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, float[]>? _vectors;
    private string? _vectorsModel;

    public SemanticIndex(VaultIndex vault, OllamaClient ollama, IOptions<BuddySettings> options)
        : this(vault, options, ollama.EmbedAsync)
    {
    }

    /// <summary>With a stand-in for the embedding model (tests).</summary>
    internal SemanticIndex(VaultIndex vault, IOptions<BuddySettings> options,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<float[]>>> embed)
    {
        _vault = vault;
        _options = options;
        _embed = embed;
    }

    /// <summary>Where the vectors are kept between runs; null keeps them in memory only.</summary>
    public string? CacheFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Buddy", "embeddings.json");

    /// <summary>True once the vectors of every note are there: searching then costs one embedding.</summary>
    public bool IsReady { get; private set; }

    private LocalAiSettings Settings => _options.Value.LocalAi;

    /// <summary>Computes what is missing, so the first search is quick (called in the background at startup).</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (!Settings.SemanticSearch || !_vault.IsAvailable)
        {
            return;
        }

        try
        {
            await EnsureAsync(Chunks(), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or IOException or OperationCanceledException)
        {
            // Ollama off or the embedding model not pulled: the search by words still works.
        }
    }

    /// <summary>
    /// The pieces closest to <paramref name="query"/>, best first. With <paramref name="onlyIfReady"/> nothing is
    /// computed for the notes (an answer must not wait for the first indexing): no hits until the index is ready.
    /// </summary>
    public async Task<IReadOnlyList<SemanticHit>> SearchAsync(string query, int max = 5, bool onlyIfReady = false,
        CancellationToken cancellationToken = default)
    {
        if (!Settings.SemanticSearch || string.IsNullOrWhiteSpace(query) || !_vault.IsAvailable || (onlyIfReady && !IsReady))
        {
            return [];
        }

        try
        {
            var chunks = Chunks();
            await EnsureAsync(chunks, cancellationToken);
            var queryVector = (await _embed(Settings.EmbeddingModel, ["search_query: " + query], cancellationToken)).FirstOrDefault();
            if (queryVector is null || _vectors is null)
            {
                return [];
            }

            return chunks
                .Select(c => (Chunk: c, Score: _vectors.TryGetValue(c.Hash, out var v) ? Cosine(queryVector, v) : 0))
                .Where(x => x.Score > 0.45)
                .OrderByDescending(x => x.Score)
                .DistinctBy(x => x.Chunk.Hash)
                .Take(max)
                .Select(x => new SemanticHit(x.Chunk.RelativePath, x.Chunk.Heading, x.Chunk.Text, x.Score))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or IOException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return []; // Ollama off or the embedding model not pulled
        }
    }

    private sealed record Chunk(string RelativePath, string Heading, string Text, string Hash);

    /// <summary>The notes cut at their headings, long sections in pieces at paragraph ends.</summary>
    private List<Chunk> Chunks()
    {
        var chunks = new List<Chunk>();
        foreach (var note in _vault.Notes)
        {
            var heading = note.Title;
            var section = new StringBuilder();
            foreach (var line in Vault.ProjectBoard.Body(note.Content).Split('\n'))
            {
                if (line.StartsWith('#'))
                {
                    Flush(note, heading, section, chunks);
                    heading = line.TrimStart('#', ' ').Trim();
                    continue;
                }

                if (section.Length > MaxChunkChars && line.Trim().Length == 0)
                {
                    Flush(note, heading, section, chunks);
                }

                section.AppendLine(line);
            }

            Flush(note, heading, section, chunks);
        }

        return chunks;
    }

    private static void Flush(VaultNote note, string heading, StringBuilder section, List<Chunk> chunks)
    {
        var text = section.ToString().Trim();
        section.Clear();
        for (var start = 0; start < text.Length; start += MaxChunkChars * 2)
        {
            var piece = text.Substring(start, Math.Min(MaxChunkChars * 2, text.Length - start));
            if (piece.Length >= MinChunkChars)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{note.RelativePath}\n{heading}\n{piece}")))[..24];
                chunks.Add(new Chunk(note.RelativePath, heading, piece, hash));
            }
        }
    }

    /// <summary>The vectors of every piece, computing only the missing ones (and saving them).</summary>
    private async Task EnsureAsync(List<Chunk> chunks, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var model = Settings.EmbeddingModel;
            if (_vectors is null || _vectorsModel != model)
            {
                _vectors = Load(model);
                _vectorsModel = model;
            }

            var missing = chunks.Where(c => !_vectors.ContainsKey(c.Hash)).DistinctBy(c => c.Hash).ToList();
            for (var i = 0; i < missing.Count; i += Batch)
            {
                var batch = missing.Skip(i).Take(Batch).ToList();
                var vectors = await _embed(model, batch.Select(c => $"search_document: {Path.GetFileNameWithoutExtension(c.RelativePath)} › {c.Heading}\n{c.Text}").ToList(), cancellationToken);
                if (vectors.Count != batch.Count)
                {
                    return;
                }

                for (var j = 0; j < batch.Count; j++)
                {
                    _vectors[batch[j].Hash] = vectors[j];
                }
            }

            // Pieces no longer in the notes are dropped, so the cache does not grow forever.
            var alive = chunks.Select(c => c.Hash).ToHashSet();
            var dropped = _vectors.Keys.Where(k => !alive.Contains(k)).ToList();
            dropped.ForEach(k => _vectors.Remove(k));
            if (missing.Count > 0 || dropped.Count > 0)
            {
                Save(model);
            }

            IsReady = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record CacheFileData(string Model, Dictionary<string, string> Vectors);

    private Dictionary<string, float[]> Load(string model)
    {
        try
        {
            if (CacheFile is not null && File.Exists(CacheFile)
                && JsonSerializer.Deserialize<CacheFileData>(File.ReadAllText(CacheFile)) is { } data && data.Model == model)
            {
                return data.Vectors.ToDictionary(kv => kv.Key, kv => FromBase64(kv.Value));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            // A broken cache is computed again.
        }

        return [];
    }

    private void Save(string model)
    {
        if (CacheFile is null || _vectors is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var data = new CacheFileData(model, _vectors.ToDictionary(kv => kv.Key, kv => ToBase64(kv.Value)));
            File.WriteAllText(CacheFile + ".tmp", JsonSerializer.Serialize(data));
            File.Move(CacheFile + ".tmp", CacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is a convenience: next time the vectors are computed again.
        }
    }

    private static string ToBase64(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static float[] FromBase64(string text)
    {
        var bytes = Convert.FromBase64String(text);
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    internal static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}
