using System.Text;

namespace Buddy.Core.Chat;

/// <summary>
/// Something the user hands to the buddy with a question: clipboard or dragged text, dropped files, images.
/// Text goes to both models; images only to Claude (the local model cannot see them).
/// </summary>
public sealed record Attachment(string Label, string? Text, IReadOnlyList<string> Files, IReadOnlyList<AttachedImage> Images)
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".config", ".cs", ".vb", ".sql", ".ts", ".js", ".html", ".css", ".scss",
        ".less", ".yml", ".yaml", ".ps1", ".csproj", ".vbproj", ".sqlproj", ".aspx", ".ascx", ".cshtml", ".razor", ".csv",
    };

    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public static Attachment FromText(string text, string label = "testo") => new(label, text, [], []);

    public static Attachment FromImage(byte[] png, string label = "immagine") =>
        new(label, null, [], [new AttachedImage("image/png", Convert.ToBase64String(png))]);

    /// <summary>Reads dropped files: small text files are inlined, images become image blocks, others are listed by path.</summary>
    public static Attachment FromFiles(IEnumerable<string> paths, int maxChars)
    {
        var text = new StringBuilder();
        var files = new List<string>();
        var images = new List<AttachedImage>();

        foreach (var path in paths.Where(File.Exists))
        {
            files.Add(path);
            var extension = Path.GetExtension(path);
            if (ImageTypes.TryGetValue(extension, out var mediaType) && new FileInfo(path).Length < 5_000_000)
            {
                images.Add(new AttachedImage(mediaType, Convert.ToBase64String(File.ReadAllBytes(path))));
            }
            else if (TextExtensions.Contains(extension) && text.Length < maxChars)
            {
                var content = File.ReadAllText(path);
                var room = maxChars - text.Length;
                text.AppendLine($"--- {path} ---")
                    .AppendLine(content.Length > room ? content[..room] + "\n[…tagliato]" : content);
            }
        }

        var label = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} file";
        return new Attachment(label, text.Length > 0 ? text.ToString() : null, files, images);
    }

    /// <summary>Text for the prompt: inlined content plus the paths, so Claude can open the files itself.</summary>
    public string ToPromptText(int maxChars)
    {
        var sb = new StringBuilder();
        if (Files.Count > 0)
        {
            sb.AppendLine("File allegati:");
            foreach (var file in Files)
            {
                sb.AppendLine($"- {file}");
            }
        }

        if (!string.IsNullOrEmpty(Text))
        {
            sb.AppendLine(Text.Length > maxChars ? Text[..maxChars] + "\n[…tagliato]" : Text);
        }

        return sb.ToString().TrimEnd();
    }

    public string Preview
    {
        get
        {
            var parts = new List<string>();
            if (Images.Count > 0) parts.Add(Images.Count == 1 ? "1 immagine" : $"{Images.Count} immagini");
            if (Files.Count > 0) parts.Add(Files.Count == 1 ? Path.GetFileName(Files[0]) : $"{Files.Count} file");
            if (Files.Count == 0 && Text is { } t)
            {
                var line = t.ReplaceLineEndings(" ").Trim();
                parts.Add($"\"{(line.Length > 80 ? line[..80] + "…" : line)}\"");
            }

            return string.Join(" · ", parts);
        }
    }
}

public sealed record AttachedImage(string MediaType, string Base64);
