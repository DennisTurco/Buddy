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

    /// <summary>The face of a greyhound, drawn like an illustration (ink outlines, flat colours).</summary>
    public const string Greyhound = "levriero";

    private static readonly Color Back = Color.Parse("#3C3489");
    private static readonly Color Bubble = Colors.White;

    /// <summary>True when the settings ask for the pixel-art face instead of the logo.</summary>
    public static bool UsesPixelArt(AppearanceSettings appearance) =>
        string.Equals(appearance.Logo, Pixel, StringComparison.OrdinalIgnoreCase);

    /// <summary>The icon the settings ask for, as PNG of <paramref name="size"/> pixels (window, tray, exported files).</summary>
    public static byte[] Png(AppearanceSettings appearance, PetSprites sprites, int size) =>
        UsesPixelArt(appearance) ? sprites.IconPng(size) : Png(size, appearance.Logo);

    /// <summary>The image at the top of the board's side bar: the logo, or the character's idle face with pixel art.</summary>
    public static IImage Mascot(AppearanceSettings appearance, PetSprites sprites) =>
        UsesPixelArt(appearance) ? sprites.Frames(PetState.Idle, ChatMode.Local)[0] : Render(132, appearance.Logo); // 3× its 44 px: sharp on HiDPI

    /// <summary>The vector logo (<paramref name="style"/>: <see cref="Modern"/> or <see cref="Greyhound"/>) as PNG of <paramref name="size"/> pixels.</summary>
    public static byte[] Png(int size, string? style = null)
    {
        using var bitmap = Render(size, style);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>The vector logo as a bitmap (the board's header).</summary>
    public static RenderTargetBitmap Render(int size, string? style = null)
    {
        var greyhound = string.Equals(style, Greyhound, StringComparison.OrdinalIgnoreCase);
        var grid = greyhound ? 256.0 : 96.0;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(size / grid, size / grid)))
        {
            if (greyhound)
            {
                DrawGreyhound(context);
            }
            else
            {
                Draw(context);
            }
        }

        return bitmap;
    }

    private static readonly Color Ink = Color.Parse("#2A1A12");

    private static void Shape(DrawingContext context, string fill, string path, double outline = 0, double opacity = 1) =>
        context.DrawGeometry(new SolidColorBrush(Color.Parse(fill), opacity),
            outline > 0 ? new Pen(new SolidColorBrush(Ink), outline, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round) : null,
            Geometry.Parse(path));

    private static void Stroke(DrawingContext context, string color, string path, double width, double opacity = 1) =>
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse(color), opacity), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
            Geometry.Parse(path));

    private static void Dot(DrawingContext context, string color, double x, double y, double radius, double opacity = 1) =>
        context.DrawEllipse(new SolidColorBrush(Color.Parse(color), opacity), null, new Point(x, y), radius, radius);

    private static GradientStop Stop(double offset, string color, double opacity = 1)
    {
        var c = Color.Parse(color);
        return new GradientStop(Color.FromArgb((byte)Math.Round(opacity * 255), c.R, c.G, c.B), offset);
    }

    /// <summary>A linear gradient between two points of the 256 grid.</summary>
    private static LinearGradientBrush Linear(double x1, double y1, double x2, double y2, params GradientStop[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(x1, y1, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(x2, y2, RelativeUnit.Absolute),
        };
        brush.GradientStops.AddRange(stops);
        return brush;
    }

    /// <summary>A radial gradient filling the shape's bounds, its light offset to (<paramref name="originX"/>, <paramref name="originY"/>).</summary>
    private static RadialGradientBrush Radial(double originX, double originY, params GradientStop[] stops)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(originX, originY, RelativeUnit.Relative),
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        };
        brush.GradientStops.AddRange(stops);
        return brush;
    }

    private static void Fill(DrawingContext context, IBrush brush, string path, double outline = 0, string outlineColor = "#3A2418") =>
        context.DrawGeometry(brush,
            outline > 0 ? new Pen(new SolidColorBrush(Color.Parse(outlineColor)), outline, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round) : null,
            Geometry.Parse(path));

    private const string Head =
        "M128 48 C156 48 176 64 178 90 C180 112 170 130 162 150 C154 172 150 198 146 214 C142 230 114 230 110 214 C106 198 102 172 94 150 C86 130 76 112 78 90 C80 64 100 48 128 48 Z";

    /// <summary>
    /// A fawn greyhound's face on a 256×256 grid, drawn like a soft illustration: long narrow head with some volume,
    /// rose ears folded back, eyes framed by darker fur like make-up (no brows), a greying muzzle with a soft blaze,
    /// the white chest. Thin warm outlines and a few strokes of fur keep it drawn, not photographic.
    /// </summary>
    private static void DrawGreyhound(DrawingContext context)
    {
        // A close-up, filling the square like the icons next to it in the taskbar: the head (ears 40-216, top 46) is
        // enlarged by a third, and the neck runs off the bottom edge like a portrait.
        using var fill = context.PushTransform(
            Matrix.CreateTranslation(-128, -46) * Matrix.CreateScale(1.333, 1.333) * Matrix.CreateTranslation(128, 4));

        // No background: the dog alone, on whatever is behind it (taskbar, tray, title bar).
        // Long neck with gently curved sides, ending in a rounded base like a bust; the white chest; the head's shadow on the neck.
        Fill(context, Linear(64, 0, 192, 0, Stop(0, "#74421F"), Stop(0.5, "#A06234"), Stop(1, "#74421F")),
            "M94 148 C90 176 84 204 78 228 C74 240 72 246 72 250 C102 255 154 255 184 250 C184 246 182 240 178 228 C172 204 166 176 162 148 Z", 3.5);
        Fill(context, Linear(0, 224, 0, 252, Stop(0, "#E9DDCF"), Stop(1, "#D2C2B1")),
            "M114 224 C119 236 121 246 121 252.5 C126 253 132 253 137 252.5 C137 246 139 236 144 224 C138 228 120 228 114 224 Z");
        context.DrawEllipse(Radial(0.5, 0.35, Stop(0, "#3A1E0E", 0.55), Stop(1, "#3A1E0E", 0)), null, new Point(128, 222), 38, 20);

        // Rose ears: small, dark, folded back and out, a lighter inside and a few hairs.
        var ear = Linear(0, 46, 0, 94, Stop(0, "#2E1B12"), Stop(1, "#5A3822"));
        Fill(context, ear, "M96 70 C88 56 76 48 64 48 C54 48 46 54 42 64 C50 62 58 64 62 70 C64 78 66 86 70 92 C80 92 90 86 96 82 Z", 3.5, "#2A1A12");
        Shape(context, "#9A6038", "M90 74 C84 64 76 58 68 58 C70 66 72 76 74 84 C82 84 87 80 90 78 Z", opacity: 0.85);
        Stroke(context, "#7A4A2A", "M64 50 C58 56 56 62 58 66 M72 62 l4 8 M78 64 l3 8", 1.6, 0.8);
        Fill(context, ear, "M160 70 C168 56 180 48 192 48 C202 48 210 54 214 64 C206 62 198 64 194 70 C192 78 190 86 186 92 C176 92 166 86 160 82 Z", 3.5, "#2A1A12");
        Shape(context, "#9A6038", "M166 74 C172 64 180 58 188 58 C186 66 184 76 182 84 C174 84 169 80 166 78 Z", opacity: 0.85);
        Stroke(context, "#7A4A2A", "M192 50 C198 56 200 62 198 66 M184 62 l-4 8 M178 64 l-3 8", 1.6, 0.8);

        // Head: a deep fawn, a touch darker down the muzzle and along the sides (no light spot: the coat is dark).
        Fill(context, Linear(0, 48, 0, 230, Stop(0, "#B57440"), Stop(0.55, "#A9693A"), Stop(1, "#93582E")), Head);
        var sideShade = "#5E3318";
        Fill(context, Linear(78, 0, 178, 0, Stop(0, sideShade, 0.4), Stop(0.22, sideShade, 0), Stop(0.78, sideShade, 0), Stop(1, sideShade, 0.4)), Head);

        // The furrow down the forehead, the cheek muscles.
        Stroke(context, "#7E4A28", "M128 60 C127 72 127 82 128 94", 3, 0.3);
        Stroke(context, "#6E3F1E", "M88 120 C93 135 99 148 104 164 M168 120 C163 135 157 148 152 164", 2, 0.3);

        // His muzzle is not fawn: a grey-brown mask from just under the eyes to the nose, greying (salt and pepper),
        // with the pale blaze down the middle from between the eyes.
        Fill(context, Linear(0, 120, 0, 224, Stop(0, "#6A5448", 0), Stop(0.3, "#6A5448", 0.45), Stop(0.6, "#6E5D54", 0.85), Stop(1, "#5E4F48", 0.95)),
            "M92 118 C98 140 104 160 108 180 C110 198 112 212 116 220 C122 226 134 226 140 220 C144 212 146 198 148 180 C152 160 158 140 164 118 "
            + "C152 124 140 126 128 126 C116 126 104 124 92 118 Z");
        Stroke(context, "#B9ADA4", "M110 150 l2 5 M114 162 l1 5 M110 176 l2 5 M116 186 l1 4 M146 150 l-2 5 M142 162 l-1 5 M146 176 l-2 5 M140 186 l-1 4 "
            + "M118 146 l1 4 M138 146 l-1 4 M114 202 l2 4 M142 202 l-2 4", 1.2, 0.55);
        Fill(context, Linear(0, 108, 0, 206, Stop(0, "#CFC4BA", 0), Stop(0.25, "#CFC4BA", 0.6), Stop(1, "#E6DED6", 0.95)),
            "M128 108 C131 108 134 124 136 146 C138 170 141 190 138 204 L118 204 C115 190 118 170 120 146 C122 124 125 108 128 108 Z");
        // Grey lips around the nose.
        Shape(context, "#9A8C84", "M114 214 C118 226 138 226 142 214 C140 228 116 228 114 214 Z", opacity: 0.8);

        // Fur: short strokes following the coat.
        Stroke(context, "#7A4626", "M110 62 l-3 7 M118 58 l-2 7 M138 58 l2 7 M146 62 l3 7 "
            + "M90 92 l-4 6 M88 128 l3 6 M94 140 l3 6 M100 154 l3 6 M166 92 l4 6 M168 128 l-3 6 M162 140 l-3 6 M156 154 l-3 6 "
            , 1.2, 0.25);

        // No brows: darker fur all around each eye, like make-up, fading into the coat; a darker line hugging the eye.
        var shadow = Radial(0.5, 0.5, Stop(0, "#5A341C", 0.6), Stop(0.6, "#5A341C", 0.3), Stop(1, "#5A341C", 0));
        context.DrawEllipse(shadow, null, new Point(100, 102), 24, 17);
        context.DrawEllipse(shadow, null, new Point(156, 102), 24, 17);
        // The eyeliner hugs the eye and flicks up at the outer corner; the almond itself tilts up towards the outside.
        Shape(context, "#3E2416", "M121 105 C117 95 103 91 90 94 C84 93 79 91 73 88 C77 93 81 98 85 102 C93 111 110 113 121 108 Z", opacity: 0.85);
        Shape(context, "#3E2416", "M135 105 C139 95 153 91 166 94 C172 93 177 91 183 88 C179 93 175 98 171 102 C163 111 146 113 135 108 Z", opacity: 0.85);
        // His eyes are big and round, set in the dark rim: a rounder opening than an almond.
        Shape(context, "#1E120C", "M86 100 C91 91 112 92 117 105 C111 115 93 114 86 100 Z");
        Shape(context, "#1E120C", "M170 100 C165 91 144 92 139 105 C145 115 163 114 170 100 Z");

        // Eyes: an amber-brown iris, the pupil, a catch light and a smaller reflection.
        var iris = Radial(0.45, 0.4, Stop(0, "#B07436"), Stop(0.6, "#7A4420"), Stop(1, "#3E2210"));
        foreach (var x in new[] { 103.0, 153.0 })
        {
            context.DrawEllipse(iris, null, new Point(x, 103.5), 8.5, 8.5);
            Dot(context, "#0C0806", x, 104, 4.4);
            context.DrawEllipse(Brushes.White, null, new Point(x - 3, 100.5), 2.7, 2.2);
            Dot(context, "#FFFFFF", x + 3.2, 107, 1.1, 0.6);
        }


        // The head's outline last, thin and warm, over the shading.
        Fill(context, Brushes.Transparent, Head, 3.5);

        // Nose: rounded, lit from the top left, with its nostrils and the middle line.
        Fill(context, Radial(0.4, 0.3, Stop(0, "#4A3A34"), Stop(1, "#0E0A09")),
            "M110 208 C110 198 146 198 146 208 C146 222 138 230 128 230 C118 230 110 222 110 208 Z", 2.5, "#1A100C");
        Shape(context, "#050303", "M116 213 C118 209 122 209 123 213 C122 216 118 217 116 213 Z M140 213 C138 209 134 209 133 213 C134 216 138 217 140 213 Z");
        Stroke(context, "#050303", "M128 216 L128 228", 1.5, 0.7);
        context.DrawEllipse(new SolidColorBrush(Colors.White, 0.3), null, new Point(121, 204), 6, 2.6);
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
