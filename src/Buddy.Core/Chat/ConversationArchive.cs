using System.Globalization;
using System.Text.Json;

namespace Buddy.Core.Chat;

/// <summary>A message as saved: who, what, when (images and permission prompts are not kept).</summary>
public sealed record SavedEntry(Speaker Speaker, string Text, DateTimeOffset Timestamp, bool Pinned = false);

/// <summary>One saved conversation: its messages and the Claude session to resume when it is reopened.</summary>
/// <param name="AiTitle">A short title written by the local model, shown instead of the first question.</param>
public sealed record SavedConversation(string Id, string Title, DateTimeOffset Updated, string? ClaudeSessionId, IReadOnlyList<SavedEntry> Entries,
    string? AiTitle = null)
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>"oggi 10:22 · come avvio il TaskExecutor in locale" for the menu.</summary>
    public string Label
    {
        get
        {
            var local = Updated.ToLocalTime();
            var day = local.Date == DateTime.Today ? "oggi"
                : local.Date == DateTime.Today.AddDays(-1) ? "ieri"
                : local.ToString("d MMM", Italian);
            return $"{day} {local:HH:mm} · {AiTitle ?? Title}";
        }
    }
}

/// <summary>
/// The chat's conversations, one JSON file each in a folder (&lt;data&gt;/conversations), so closing the buddy or
/// starting a new one does not lose them. Keeps the most recent <see cref="MaxConversations"/>.
/// </summary>
public sealed class ConversationArchive(string folder)
{
    public const int MaxConversations = 60;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string Folder { get; } = folder;

    /// <summary>A new id, sortable by time.</summary>
    public static string NewId() => DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

    /// <summary>Saves the conversation; one with no message of the user is not worth keeping.</summary>
    public void Save(string id, IEnumerable<ChatEntry> entries, string? claudeSessionId)
    {
        var saved = entries
            .Where(e => e is not PermissionEntry && !string.IsNullOrWhiteSpace(e.Text))
            .Select(e => new SavedEntry(e.Speaker, e.Text, e.Timestamp, e.IsPinned))
            .ToList();
        var firstQuestion = saved.FirstOrDefault(e => e.Speaker == Speaker.User)?.Text;
        if (firstQuestion is null)
        {
            return;
        }

        var title = firstQuestion.ReplaceLineEndings(" ").Trim();
        if (title.Length > 70)
        {
            title = title[..70].TrimEnd() + "…";
        }

        try
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(id);
            var aiTitle = File.Exists(path) ? Load(path)?.AiTitle : null;
            var conversation = new SavedConversation(id, title, DateTimeOffset.Now, claudeSessionId, saved, aiTitle);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(conversation, Json));
            File.Move(path + ".tmp", path, overwrite: true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The archive is a convenience: a failed save must never disturb the chat.
        }
    }

    /// <summary>The saved conversation <paramref name="id"/>, or null.</summary>
    public SavedConversation? Find(string id) => File.Exists(PathOf(id)) ? Load(PathOf(id)) : null;

    /// <summary>Gives a saved conversation the local model's title (kept by later saves).</summary>
    public void SetAiTitle(string id, string title)
    {
        var path = PathOf(id);
        if (Find(id) is not { } conversation)
        {
            return;
        }

        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(conversation with { AiTitle = title }, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A title is a nicety.
        }
    }

    /// <summary>The saved conversations, newest first.</summary>
    public IReadOnlyList<SavedConversation> List(int max = 20)
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(Folder, "*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(Load)
            .OfType<SavedConversation>()
            .OrderByDescending(c => c.Updated)
            .Take(max)
            .ToList();
    }

    private static SavedConversation? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SavedConversation>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string PathOf(string id) => Path.Combine(Folder, $"{id}.json");

    private void Prune()
    {
        foreach (var old in Directory.EnumerateFiles(Folder, "*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(MaxConversations))
        {
            File.Delete(old);
        }
    }
}
