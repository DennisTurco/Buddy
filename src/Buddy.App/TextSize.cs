namespace Buddy.App;

/// <summary>
/// The type scale: the only text sizes the windows use, from the smallest label to the big numbers of the overview.
/// In XAML: <c>FontSize="{x:Static app:TextSize.Body}"</c> (xmlns:app="clr-namespace:Buddy.App"). Text with no size set
/// follows the window's, which for the chat is the one chosen in Impostazioni → Aspetto.
/// </summary>
public static class TextSize
{
    /// <summary>Times, counters, axis labels, hints under a value.</summary>
    public const double Caption = 10.5;

    /// <summary>Chips, tags, help text, secondary lines of a card.</summary>
    public const double Small = 11.5;

    /// <summary>Card text, buttons of a toolbar, code.</summary>
    public const double Body = 12.5;

    /// <summary>Lists and titles of small items (a script, a checklist).</summary>
    public const double Base = 14;

    /// <summary>The heading of a section or panel.</summary>
    public const double Heading = 16;

    /// <summary>The title of a page, a note, a task.</summary>
    public const double Title = 20;

    /// <summary>Big numbers (the overview's tiles).</summary>
    public const double Display = 28;
}
