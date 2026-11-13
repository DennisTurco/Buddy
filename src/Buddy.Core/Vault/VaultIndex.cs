using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Vault;

/// <summary>
/// Read-only view of the markdown notes of the active project folder.
/// Notes are re-read lazily when the folder changes. Writes go through <see cref="VaultWriter"/> (project board only).
/// </summary>
public sealed class VaultIndex : IDisposable
{
    private readonly string _root;
    private readonly FileSystemWatcher? _watcher;
    private readonly Lock _gate = new();
    private IReadOnlyList<VaultNote>? _notes;

    public VaultIndex(IOptions<BuddySettings> settings)
    {
        _root = settings.Value.ProjectPath;
        if (!Directory.Exists(_root))
        {
            return;
        }

        _watcher = new FileSystemWatcher(_root, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
        };
        _watcher.Changed += (_, _) => Invalidate();
        _watcher.Created += (_, _) => Invalidate();
        _watcher.Deleted += (_, _) => Invalidate();
        _watcher.Renamed += (_, _) => Invalidate();
        // Too many changes at once (the queue overflowed): which notes changed is unknown, so read them all again.
        _watcher.Error += (_, _) => Invalidate();
        _watcher.EnableRaisingEvents = true;
    }

    public bool IsAvailable => Directory.Exists(_root);

    public IReadOnlyList<VaultNote> Notes
    {
        get
        {
            lock (_gate)
            {
                return _notes ??= Load();
            }
        }
    }

    public VaultNote? Find(string relativePath) =>
        Notes.FirstOrDefault(n => string.Equals(NormalizeSeparators(n.RelativePath), NormalizeSeparators(relativePath), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Returns every block that mentions <paramref name="id"/>: a top-level list item or heading whose
    /// first line contains the id, together with its nested lines.
    /// </summary>
    public IReadOnlyList<VaultBlock> FindBlocks(string id)
    {
        var pattern = new Regex($@"(?<![\w-]){Regex.Escape(id)}(?!\d)", RegexOptions.IgnoreCase);
        var result = new List<VaultBlock>();
        foreach (var note in Notes)
        {
            result.AddRange(MarkdownBlocks.Split(note.Content)
                .Where(b => pattern.IsMatch(b.FirstLine))
                .Select(b => new VaultBlock(note.RelativePath, b.Text)));
        }

        return result;
    }

    /// <summary>Reads the vault again on next use: after Buddy itself moved files (the watcher may be late, or miss a folder).</summary>
    public void Refresh() => Invalidate();

    /// <summary>A note was added, changed, renamed or deleted. Raised on a background thread.</summary>
    public event Action? Changed;

    private void Invalidate()
    {
        lock (_gate)
        {
            _notes = null;
        }

        Changed?.Invoke();
    }

    private List<VaultNote> Load()
    {
        if (!IsAvailable)
        {
            return [];
        }

        var notes = new List<VaultNote>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories))
        {
            // Hidden folders: Obsidian's .trash (notes deleted from Buddy), .obsidian, .git.
            if (Path.GetRelativePath(_root, path).Split('/', '\\').Any(part => part.StartsWith('.')))
            {
                continue;
            }

            try
            {
                notes.Add(new VaultNote(Path.GetRelativePath(_root, path), File.ReadAllText(path, Encoding.UTF8)));
            }
            catch (IOException)
            {
                // Obsidian may hold the file while saving; it is picked up on the next change event.
            }
        }

        return notes;
    }

    public void Dispose() => _watcher?.Dispose();
}

public sealed record VaultNote(string RelativePath, string Content)
{
    public string Title => Path.GetFileNameWithoutExtension(RelativePath);
}

public sealed record VaultBlock(string RelativePath, string Text);
