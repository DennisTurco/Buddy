using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Buddy.Core;
using Buddy.Core.Chat;

namespace Buddy.App.Pet;

/// <summary>
/// The pet's frames for each state and chat mode. Custom pixel art comes from <see cref="PetSettings.SpriteFolder"/>
/// (one PNG strip of square frames per state, "claude-&lt;state&gt;.png" for Claude mode); anything missing falls back
/// to the built-in character, drawn from character grids and coloured with the configured body colours.
/// Built-in legend: '.' transparent, K outline, B body, L body highlight, D eye, W eye shine, P cheek.
/// </summary>
public sealed class PetSprites
{
    public const int BuiltInSize = 16;

    private static readonly string[] Open =
    [
        "................",
        "................",
        ".....KKKKKK.....",
        "...KKBBBBBBKK...",
        "..KBBLLBBBBBBK..",
        "..KBLLBBBBBBBK..",
        ".KBBBBBBBBBBBBK.",
        ".KBBDWBBBBDWBBK.",
        ".KBBDDBBBBDDBBK.",
        ".KBPPBBBBBBPPBK.",
        ".KBBBBBKKBBBBBK.",
        ".KBBBBBBBBBBBBK.",
        "..KBBBBBBBBBBK..",
        "...KKBBBBBBKK...",
        "....KK....KK....",
        "................",
    ];

    private static readonly string[] Blink = With(Open, (7, ".KBBBBBBBBBBBBK."), (8, ".KBBKKBBBBKKBBK."));
    private static readonly string[] LookUp = With(Open, (7, ".KBBBDBBBBBDBBK."), (8, ".KBBBBBBBBBBBBK."));
    private static readonly string[] Focused = With(Open, (7, ".KBBKKBBBBKKBBK."), (8, ".KBBDDBBBBDDBBK."), (10, ".KBBBBBBKBBBBBK."));
    private static readonly string[] Surprised = With(Open, (6, ".KBBDDBBBBDDBBK."), (7, ".KBBDWBBBBDWBBK."), (8, ".KBBDDBBBBDDBBK."), (10, ".KBBBBBKKBBBBBK."), (11, ".KBBBBBKKBBBBBK."));
    private static readonly string[] Sad = With(Blink, (10, ".KBBBBBBBBBBBBK."), (11, ".KBBBBKBBKBBBBK."));
    private static readonly string[] StepLeft = With(Open, (14, "...KK......KK..."));
    private static readonly string[] StepRight = With(Open, (13, "...KKBBBBBBKK..."), (14, ".....KK..KK....."));
    private static readonly string[] Joy = With(Open, (7, ".KBBDDBBBBDDBBK."), (8, ".KBDBBDBBDBBDBK."), (10, ".KBBBKBBBBKBBBK."), (11, ".KBBBBKKKKBBBBK."));

    private static readonly Color DefaultLocal = Color.FromRgb(0x7F, 0xD1, 0xAE);
    private static readonly Color DefaultClaude = Color.FromRgb(0xB9, 0x9B, 0xF5);

    private readonly PetSettings _settings;
    private readonly Dictionary<(PetState, ChatMode), IReadOnlyList<IImage>> _cache = [];
    private readonly Dictionary<ChatMode, IReadOnlyList<IImage>> _walkCache = [];

    public PetSprites(PetSettings settings) => _settings = settings;

    /// <summary>Frames of the animation loop for a state; the window cycles through them.</summary>
    public IReadOnlyList<IImage> Frames(PetState state, ChatMode mode)
    {
        if (!_cache.TryGetValue((state, mode), out var frames))
        {
            frames = LoadCustom(state, mode) ?? Bitmaps(BuiltIn(state, mode));
            _cache[(state, mode)] = frames;
        }

        return frames;
    }

    /// <summary>Walking loop: "walk.png" (or "claude-walk.png") from the custom folder, else the built-in steps.</summary>
    public IReadOnlyList<IImage> WalkFrames(ChatMode mode)
    {
        if (!_walkCache.TryGetValue(mode, out var frames))
        {
            frames = LoadCustomFile(mode == ChatMode.Claude ? "claude-walk.png" : "walk.png")
                ?? (mode == ChatMode.Claude ? LoadCustomFile("walk.png") : null)
                ?? (HasCustomFolder ? Frames(PetState.Idle, mode) : Bitmaps(BuiltInWalk(mode)));
            _walkCache[mode] = frames;
        }

        return frames;
    }

    /// <summary>The built-in idle character scaled to <paramref name="size"/> pixels without smoothing, as PNG (icons).</summary>
    public byte[] IconPng(int size)
    {
        var frame = BuiltIn(PetState.Idle, ChatMode.Local)[0];
        var scaled = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Array.Copy(frame, ((y * BuiltInSize / size) * BuiltInSize + x * BuiltInSize / size) * 4, scaled, (y * size + x) * 4, 4);
            }
        }

        return Png(scaled, size, size);
    }

    private bool HasCustomFolder => !string.IsNullOrWhiteSpace(_settings.SpriteFolder) && Directory.Exists(_settings.SpriteFolder);

    private IReadOnlyList<IImage>? LoadCustomFile(string name) =>
        HasCustomFolder && File.Exists(Path.Combine(_settings.SpriteFolder, name)) ? TryLoadStrip(Path.Combine(_settings.SpriteFolder, name)) : null;

    private IReadOnlyList<byte[]> BuiltInWalk(ChatMode mode)
    {
        var body = BodyColor(mode);
        return [Render(StepLeft, body), Render(Open, body), Render(StepRight, body), Render(Open, body)];
    }

    private Color BodyColor(ChatMode mode) => mode == ChatMode.Claude
        ? ParseColor(_settings.ClaudeColor, DefaultClaude)
        : ParseColor(_settings.LocalColor, DefaultLocal);

    /// <summary>Text shown above the head for each state (thought dots, alarm...).</summary>
    public static string Badge(PetState state, int tick) => state switch
    {
        PetState.Think => new string('.', 1 + tick % 3),
        PetState.Work => (tick % 4) switch { 0 => "⚙", 1 => "⚙.", 2 => "⚙..", _ => "⚙..." },
        PetState.Alert => "!",
        PetState.Error => "?",
        PetState.Happy => tick % 2 == 0 ? "♪" : "✓",
        PetState.Speak => tick % 2 == 0 ? "💬" : " 💬",
        _ => "",
    };

    /// <summary>
    /// Writes the built-in frames as PNG strips (idle.png, think.png..., claude-idle.png...) into a folder,
    /// as a starting point for custom pixel art.
    /// </summary>
    public void ExportTemplate(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var mode in Enum.GetValues<ChatMode>())
        {
            foreach (var (name, frames) in Enum.GetValues<PetState>()
                         .Select(s => (FileName(s, mode), BuiltIn(s, mode)))
                         .Append(((mode == ChatMode.Claude ? "claude-" : "") + "walk.png", BuiltInWalk(mode))))
            {
                var width = BuiltInSize * frames.Count;
                var strip = new byte[width * BuiltInSize * 4];
                for (var i = 0; i < frames.Count; i++)
                {
                    for (var y = 0; y < BuiltInSize; y++)
                    {
                        Array.Copy(frames[i], y * BuiltInSize * 4, strip, (y * width + i * BuiltInSize) * 4, BuiltInSize * 4);
                    }
                }

                File.WriteAllBytes(Path.Combine(folder, name), Png(strip, width, BuiltInSize));
            }
        }
    }

    public static string FileName(PetState state, ChatMode mode) =>
        (mode == ChatMode.Claude ? "claude-" : "") + state.ToString().ToLowerInvariant() + ".png";

    private IReadOnlyList<IImage>? LoadCustom(PetState state, ChatMode mode)
    {
        if (!HasCustomFolder)
        {
            return null;
        }

        // Most specific first: the state for this mode, the state for any mode, then idle as a last resort.
        var candidates = new[]
        {
            FileName(state, mode),
            FileName(state, ChatMode.Local),
            FileName(PetState.Idle, mode),
            FileName(PetState.Idle, ChatMode.Local),
        };

        foreach (var name in candidates.Distinct())
        {
            var path = Path.Combine(_settings.SpriteFolder, name);
            if (File.Exists(path) && TryLoadStrip(path) is { Count: > 0 } frames)
            {
                return frames;
            }
        }

        return null;
    }

    private static List<IImage>? TryLoadStrip(string path)
    {
        try
        {
            var image = new Bitmap(path);
            var size = image.PixelSize.Height;
            var count = Math.Max(1, image.PixelSize.Width / size);
            var frames = new List<IImage>(count);
            for (var i = 0; i < count; i++)
            {
                frames.Add(new CroppedBitmap(image, new PixelRect(i * size, 0, size, size)));
            }

            return frames;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private IReadOnlyList<byte[]> BuiltIn(PetState state, ChatMode mode)
    {
        var body = BodyColor(mode);

        byte[] R(string[] grid) => Render(grid, body);

        return state switch
        {
            PetState.Think => [R(LookUp)],
            PetState.Work => [R(Focused), R(Focused), R(Blink)],
            PetState.Alert => [R(Surprised)],
            PetState.Error => [R(Sad)],
            PetState.Happy => [R(Joy)],
            PetState.Speak => [R(Open), R(Open), R(Surprised)],
            _ => [.. Enumerable.Repeat(R(Open), 11), R(Blink)], // one array repeated: one bitmap
        };
    }

    public static Color ParseColor(string? value, Color fallback) =>
        !string.IsNullOrWhiteSpace(value) && Color.TryParse(value.Trim(), out var color) ? color : fallback;

    /// <summary>One built-in frame as BGRA pixels (straight alpha).</summary>
    private static byte[] Render(string[] grid, Color body)
    {
        // Highlight: the body colour mixed halfway towards white.
        var light = Color.FromRgb((byte)((body.R + 255) / 2), (byte)((body.G + 255) / 2), (byte)((body.B + 255) / 2));
        var ink = Color.FromRgb(0x24, 0x1F, 0x36);

        var pixels = new byte[BuiltInSize * BuiltInSize * 4];
        for (var y = 0; y < BuiltInSize; y++)
        {
            for (var x = 0; x < BuiltInSize; x++)
            {
                Color? color = grid[y][x] switch
                {
                    'K' or 'D' => ink,
                    'B' => body,
                    'L' => light,
                    'W' => Colors.White,
                    'P' => Color.FromRgb(0xF4, 0x9A, 0xB4),
                    _ => null,
                };

                if (color is { } c)
                {
                    var i = (y * BuiltInSize + x) * 4;
                    pixels[i] = c.B;
                    pixels[i + 1] = c.G;
                    pixels[i + 2] = c.R;
                    pixels[i + 3] = 0xFF;
                }
            }
        }

        return pixels;
    }

    /// <summary>Bitmaps for built-in frames; a frame repeated in the loop (e.g. eleven "open" for one blink) is one bitmap.</summary>
    private static List<IImage> Bitmaps(IReadOnlyList<byte[]> frames)
    {
        var made = new Dictionary<byte[], IImage>(ReferenceEqualityComparer.Instance);
        return frames.Select(f => made.TryGetValue(f, out var bitmap) ? bitmap : made[f] = ToBitmap(f, BuiltInSize, BuiltInSize)).ToList();
    }

    private static WriteableBitmap ToBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var buffer = bitmap.Lock();
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(pixels, y * width * 4, buffer.Address + y * buffer.RowBytes, width * 4);
        }

        return bitmap;
    }

    private static byte[] Png(byte[] pixels, int width, int height)
    {
        using var bitmap = ToBitmap(pixels, width, height);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static string[] With(string[] source, params (int Row, string Line)[] changes)
    {
        var copy = (string[])source.Clone();
        foreach (var (row, line) in changes)
        {
            copy[row] = line;
        }

        return copy;
    }
}
