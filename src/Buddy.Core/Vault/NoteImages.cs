namespace Buddy.Core.Vault;

/// <summary>
/// Images pasted or dropped into a note: saved in the project's "Allegati" folder and embedded the Obsidian way,
/// "![[Pasted image 20261116143005.png]]" (by file name, so Obsidian finds it wherever the vault root is, and Buddy's
/// viewer finds it under the project folder).
/// </summary>
public static class NoteImages
{
    public const string Folder = "Allegati";

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"];

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Saves a pasted image (PNG bytes); returns the embed to put in the note.</summary>
    public static string SavePasted(string projectPath, byte[] png, DateTime now)
    {
        var path = FreePath(projectPath, $"Pasted image {now:yyyyMMddHHmmss}.png");
        File.WriteAllBytes(path, png);
        return Embed(path);
    }

    /// <summary>
    /// An image file for the note: one already under the project folder is only linked, any other is copied into
    /// <see cref="Folder"/> first (a file outside the vault would not show in Obsidian). Returns the embed.
    /// </summary>
    public static string AddFile(string projectPath, string source)
    {
        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(source);
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return Embed(full);
        }

        var target = FreePath(projectPath, Path.GetFileName(full));
        File.Copy(full, target);
        return Embed(target);
    }

    private static string Embed(string path) => $"![[{Path.GetFileName(path)}]]";

    /// <summary>A path in the attachments folder that does not exist yet: "name.png", else "name (2).png"…</summary>
    private static string FreePath(string projectPath, string fileName)
    {
        var folder = Path.Combine(projectPath, Folder);
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var path = Path.Combine(folder, fileName);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        }

        return path;
    }
}
