using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Buddy.Core.Vault;

/// <summary>
/// What Buddy last wrote from the flow template into a folder (the vault's shared copy, ~/.claude/skills): one hash per
/// file. A file whose content still has that hash is Buddy's and a template update may replace it; any other content
/// was edited by hand. Kept in a hidden file, so Obsidian and Claude Code ignore it.
/// </summary>
internal sealed class TemplateManifest
{
    public const string FileName = ".buddy-template.json";

    private readonly string _file;
    private readonly Dictionary<string, string> _hashes;
    private bool _changed;

    private TemplateManifest(string file, Dictionary<string, string> hashes)
    {
        _file = file;
        _hashes = hashes;
    }

    public static TemplateManifest Open(string folder)
    {
        var file = Path.Combine(folder, FileName);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) is { } read)
            {
                hashes = new Dictionary<string, string>(read, StringComparer.Ordinal);
            }
        }
        catch (JsonException)
        {
            // A broken manifest only means every file counts as unknown.
        }

        return new TemplateManifest(file, hashes);
    }

    /// <summary>True when Buddy wrote something for this key (whatever the file holds now).</summary>
    public bool Knows(string key) => _hashes.ContainsKey(key);

    /// <summary>True when the content is exactly what Buddy last wrote for this key.</summary>
    public bool Matches(string key, string content) => _hashes.TryGetValue(key, out var hash) && hash == Hash(content);

    public void Record(string key, string content)
    {
        var hash = Hash(content);
        if (!_hashes.TryGetValue(key, out var old) || old != hash)
        {
            _hashes[key] = hash;
            _changed = true;
        }
    }

    public void Save()
    {
        if (!_changed)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(new SortedDictionary<string, string>(_hashes, StringComparer.Ordinal),
            new JsonSerializerOptions { WriteIndented = true }));
        _changed = false;
    }

    /// <summary>Same text, whatever the line endings (the template may be checked out with CRLF).</summary>
    public static bool Same(string a, string b) => Normalize(a) == Normalize(b);

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(content))));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
