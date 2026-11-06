using System.Globalization;
using System.Text.Json;

namespace Buddy.Core.Tests;

public class ThemeTests
{
    private static (double R, double G, double B) Rgb(string hex)
    {
        Assert.That(hex, Does.Match("^#[0-9A-Fa-f]{6}$"));
        double C(int i) => int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
        return (C(1), C(3), C(5));
    }

    /// <summary>WCAG relative luminance and contrast ratio.</summary>
    private static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            var (r, g, bl) = Rgb(hex);
            static double Lin(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            return (0.2126 * Lin(r)) + (0.7152 * Lin(g)) + (0.0722 * Lin(bl));
        }

        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Test]
    public void There_are_five_built_in_themes_with_distinct_names()
    {
        Assert.That(ThemeColors.BuiltIn.Select(t => t.Name).Distinct().Count(), Is.EqualTo(5));
    }

    [TestCaseSource(nameof(Themes))]
    public void Every_colour_is_valid_and_text_is_readable_on_every_bubble(string name)
    {
        var theme = ThemeColors.BuiltIn.Single(t => t.Name == name).Create();
        foreach (var back in new[] { theme.Background, theme.UserBubble, theme.LocalBubble, theme.ClaudeBubble, theme.MentorBubble, theme.Input })
        {
            Assert.That(Contrast(theme.Text, back), Is.GreaterThanOrEqualTo(7), $"{name}: text on {back}");
        }

        Assert.That(Contrast(theme.MutedText, theme.Background), Is.GreaterThanOrEqualTo(4.5), $"{name}: secondary text");
        Rgb(theme.LocalAccent);
        Rgb(theme.ClaudeAccent);
        Rgb(theme.MentorAccent);
        Rgb(theme.Border);
    }

    private static IEnumerable<string> Themes() => ThemeColors.BuiltIn.Select(t => t.Name);

    [Test]
    public void A_clone_is_independent_and_the_same_colours_are_recognised()
    {
        var dark = ThemeColors.Dark();
        var copy = dark.Clone();
        Assert.That(copy.SameAs(dark), Is.True);
        copy.Background = "#000000";
        Assert.That(dark.Background, Is.EqualTo("#1F1C2C"));
        Assert.That(copy.SameAs(dark), Is.False);
        Assert.That(new ThemeColors { Background = "#abcdef " }.SameAs(new ThemeColors { Background = "#ABCDEF" }), Is.True);
    }

    [Test]
    public void Saved_themes_survive_the_settings_file()
    {
        var settings = new BuddySettings();
        settings.Appearance.CustomThemes.Add(new NamedTheme { Name = "Mio", Colors = ThemeColors.Sunset() });
        var back = JsonSerializer.Deserialize<BuddySettings>(JsonSerializer.Serialize(settings))!;
        Assert.That(back.Appearance.CustomThemes.Single().Colors.SameAs(ThemeColors.Sunset()), Is.True);
    }
}
