using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Buddy.Core;

namespace Buddy.App;

/// <summary>
/// The Appearance settings (colours, font) applied to a window: they replace the theme brushes of Theme.axaml in the
/// window's resources, so its templates pick them up through DynamicResource.
/// </summary>
internal static class Appearance
{
    public static void Apply(Window window, AppearanceSettings appearance, bool font = true)
    {
        var theme = appearance.Theme;
        var defaults = ThemeColors.Light();
        void Brush(string key, string value, string fallback) =>
            window.Resources[key] = new SolidColorBrush(Pet.PetSprites.ParseColor(value, Color.Parse(fallback)));

        Brush("Paper", theme.Background, defaults.Background);
        Brush("Frame", theme.Border, defaults.Border);
        Brush("Ink", theme.Text, defaults.Text);
        Brush("Muted", theme.MutedText, defaults.MutedText);
        Brush("UserBubble", theme.UserBubble, defaults.UserBubble);
        Brush("LocalBubble", theme.LocalBubble, defaults.LocalBubble);
        Brush("LocalAccent", theme.LocalAccent, defaults.LocalAccent);
        Brush("ClaudeBubble", theme.ClaudeBubble, defaults.ClaudeBubble);
        Brush("ClaudeAccent", theme.ClaudeAccent, defaults.ClaudeAccent);
        Brush("InputBack", theme.Input, defaults.Input);
        Brush("MentorBubble", theme.MentorBubble, defaults.MentorBubble);
        Brush("MentorAccent", theme.MentorAccent, defaults.MentorAccent);

        // Derived for markdown: code background = a touch of ink over the paper, links = the Claude accent pulled
        // towards the ink until it reads well (on a light paper the pastel accent alone is too faint).
        var paper = ((SolidColorBrush)window.Resources["Paper"]!).Color;
        var ink = ((SolidColorBrush)window.Resources["Ink"]!).Color;
        var accent = ((SolidColorBrush)window.Resources["ClaudeAccent"]!).Color;
        window.Resources["CodeBack"] = new SolidColorBrush(Mix(paper, ink, 0.12));

        // Inline code: the same look as a see-through ink, because the markdown viewer paints it over the selection
        // highlight; an opaque background hid the selected `code` while copying.
        window.Resources["InlineCodeBack"] = new SolidColorBrush(ink, 0.12);
        window.Resources["LinkInk"] = new SolidColorBrush(Luminance(paper) < 0.5 ? accent : Mix(accent, ink, 0.45));

        // Code colours readable on the paper: One Dark on a dark one, One Light on a light one.
        var dark = Luminance(paper) < 0.5;
        foreach (var (key, light, night) in new[]
                 {
                     ("SynKeyword", "#A626A4", "#C678DD"), ("SynString", "#3E8E3C", "#98C379"), ("SynComment", "#8A8798", "#7F848E"),
                     ("SynNumber", "#B76B01", "#D19A66"), ("SynType", "#2F6BD9", "#61AFEF"), ("SynTag", "#D2453A", "#E06C75"),
                 })
        {
            window.Resources[key] = new SolidColorBrush(Color.Parse(dark ? night : light));
        }

        if (font)
        {
            // A missing font (e.g. "Segoe UI" on Linux) falls back to the system default.
            if (!string.IsNullOrWhiteSpace(appearance.FontFamily))
            {
                window.FontFamily = new FontFamily($"{appearance.FontFamily}, {FontFamily.Default.Name}");
            }

            window.FontSize = Math.Clamp(appearance.FontSize, 9, 24);
        }

        // Text inside templates (markdown included) inherits the window's ink.
        window.Foreground = (IBrush)window.Resources["Ink"]!;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + ((to.R - from.R) * amount)),
        (byte)(from.G + ((to.G - from.G) * amount)),
        (byte)(from.B + ((to.B - from.B) * amount)));

    private static double Luminance(Color c) => ((0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B)) / 255;

    /// <summary>
    /// Fluent controls in the window's colours: its accent (selection, links, focus) is the system one, often orange on
    /// Linux, and its dark text boxes are black. Uses the chat's Claude colour, the paper for text boxes and green for
    /// ticks and progress.
    /// </summary>
    public static void ApplyControls(Window window, AppearanceSettings appearance)
    {
        var accent = Pet.PetSprites.ParseColor(appearance.Theme.ClaudeAccent, Color.Parse("#B99BF5"));
        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
                     "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3" })
        {
            window.Resources[key] = accent;
        }

        var paper = (IBrush)window.Resources["Paper"]!;
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
        {
            window.Resources[key] = paper;
        }

        window.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(accent);
        var green = new SolidColorBrush(Color.Parse("#5FBF77"));
        foreach (var key in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver",
                     "CheckBoxCheckBackgroundFillCheckedPressed", "CheckBoxCheckBackgroundStrokeChecked",
                     "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed",
                     "ProgressBarForeground" })
        {
            window.Resources[key] = green;
        }
    }

    /// <summary>Dark or light Fluent controls (text boxes, tabs, lists), following the chat's background.</summary>
    public static ThemeVariant Variant(AppearanceSettings appearance)
    {
        var back = Pet.PetSprites.ParseColor(appearance.Theme.Background, Color.Parse(ThemeColors.Light().Background));
        var luminance = (0.299 * back.R + 0.587 * back.G + 0.114 * back.B) / 255;
        return luminance < 0.5 ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
