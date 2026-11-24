using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Buddy.App.Pet;
using Buddy.Core;
using Buddy.Core.Chat;

namespace Buddy.App;

/// <summary>
/// The app's logo: an indigo rounded square with a white speech bubble and two eyes, drawn as vectors so it stays sharp
/// from the 16 px of the tray to the 256 px of the README. Impostazioni → Aspetto → Logo can bring back the pixel-art
/// character's face instead (<see cref="AppearanceSettings.Logo"/> = "pixel").
/// </summary>
internal static class Logo
{
    public const string Modern = "moderno";
    public const string Pixel = "pixel";

    private static readonly Color Back = Color.Parse("#3C3489");
    private static readonly Color Bubble = Colors.White;

    /// <summary>True when the settings ask for the pixel-art face instead of the logo.</summary>
    public static bool UsesPixelArt(AppearanceSettings appearance) =>
        string.Equals(appearance.Logo, Pixel, StringComparison.OrdinalIgnoreCase);

    /// <summary>The icon the settings ask for, as PNG of <paramref name="size"/> pixels (window, tray, exported files).</summary>
    public static byte[] Png(AppearanceSettings appearance, PetSprites sprites, int size) =>
        UsesPixelArt(appearance) ? sprites.IconPng(size) : Png(size);

    /// <summary>The image at the top of the board's side bar: the logo, or the character's idle face with pixel art.</summary>
    public static IImage Mascot(AppearanceSettings appearance, PetSprites sprites) =>
        UsesPixelArt(appearance) ? sprites.Frames(PetState.Idle, ChatMode.Local)[0] : Render(132); // 3× its 44 px: sharp on HiDPI

    /// <summary>The vector logo as PNG of <paramref name="size"/> pixels.</summary>
    public static byte[] Png(int size)
    {
        using var bitmap = Render(size);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>The vector logo as a bitmap (the board's header).</summary>
    public static RenderTargetBitmap Render(int size)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(size / 96.0, size / 96.0)))
        {
            Draw(context);
        }

        return bitmap;
    }

    /// <summary>The logo on a 96×96 grid.</summary>
    private static void Draw(DrawingContext context)
    {
        var back = new SolidColorBrush(Back);
        var white = new SolidColorBrush(Bubble);
        context.DrawRectangle(back, null, new RoundedRect(new Rect(0, 0, 96, 96), 22));
        context.DrawRectangle(white, null, new RoundedRect(new Rect(20, 22, 56, 40), 12));
        context.DrawGeometry(white, null, Geometry.Parse("M30 58 L30 74 L46 60 Z")); // the bubble's tail
        context.DrawEllipse(back, null, new Point(39, 42), 5, 5);
        context.DrawEllipse(back, null, new Point(57, 42), 5, 5);
    }
}
