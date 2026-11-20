using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Buddy.Core.Vault;

/// <summary>A note being edited and not saved yet, as it was at <see cref="SavedAt"/>.</summary>
/// <param name="NotePath">The note's full path.</param>
/// <param name="Original">The note's text when the editing started (to tell whether the note changed since).</param>
/// <param name="Text">What was typed.</param>
public sealed record NoteDraft(string NotePath, string Original, string Text, DateTime SavedAt);

/// <summary>
/// The note editor's drafts, saved while typing so a crash or a shutdown does not lose the text: one JSON file per
/// note in <paramref name="folder"/> (outside the vault, so Obsidian and the flow do not see them). Saving or
/// discarding the edit deletes the draft.
/// </summary>
public sealed class NoteDrafts(string folder)
{
    public void Save(string notePath, string original, string text)
    {
        if (text == original)
        {
            Delete(notePath); // nothing typed, or typed back to the start: nothing to recover
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            var file = FileFor(notePath);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new NoteDraft(notePath, original, text, DateTime.Now)));
            File.Move(temp, file, overwrite: true); // whole or nothing: a crash mid-write keeps the previous draft
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A safety net: if it cannot be written the editor still works.
        }
    }

    /// <summary>The draft of <paramref name="notePath"/>, or null if there is none (or it cannot be read).</summary>
    public NoteDraft? Load(string notePath)
    {
        try
        {
            var file = FileFor(notePath);
            return File.Exists(file) && JsonSerializer.Deserialize<NoteDraft>(File.ReadAllText(file)) is { } draft
                                     && string.Equals(draft.NotePath, notePath, StringComparison.OrdinalIgnoreCase)
                ? draft
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Delete(string notePath)
    {
        try
        {
            File.Delete(FileFor(notePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind: offered again next time, and discarded from there.
        }
    }

    private string FileFor(string notePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(notePath).ToUpperInvariant()));
        return Path.Combine(folder, Convert.ToHexString(hash)[..16] + ".json");
    }
}
