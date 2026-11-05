using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Buddy.Core.Vault;

/// <summary>
/// Read-only view of the markdown notes of the active project folder.
/// Notes are re-read lazily when the folder changes; the buddy never writes to the vault.
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
        Notes.FirstOrDefault(n => string.Equals(n.RelativePath, relativePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));

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

    private void Invalidate()
    {
        lock (_gate)
        {
            _notes = null;
        }
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
