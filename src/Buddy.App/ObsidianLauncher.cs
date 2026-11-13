using Buddy.Core;

namespace Buddy.App;

/// <summary>
/// Opens the project's vault in Obsidian through the obsidian:// URI, landing on the project's first
/// always-included note (e.g. "Stato attuale.md"). Falls back to the file manager if Obsidian is not registered.
/// </summary>
public static class ObsidianLauncher
{
    public static void OpenProject(BuddySettings settings)
    {
        var folder = settings.ProjectPath;
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Cartella del progetto non trovata: {folder}");
        }

        // Obsidian opens a file of a known vault; a bare sub-folder is not enough to pick the vault.
        var target = settings.AlwaysIncludedNotes
            .Select(note => Path.Combine(folder, note))
            .FirstOrDefault(File.Exists)
            ?? Directory.EnumerateFiles(folder, "*.md").FirstOrDefault();

        if (target is not null && Shell.Open($"obsidian://open?path={Uri.EscapeDataString(target)}"))
        {
            return;
        }

        Shell.Open(folder);
    }

    /// <summary>The project's vault folder in the file manager (Explorer on Windows).</summary>
    public static void OpenFolder(BuddySettings settings)
    {
        var folder = settings.ProjectPath;
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Cartella del progetto non trovata: {folder}");
        }

        Shell.Open(folder);
    }
}
