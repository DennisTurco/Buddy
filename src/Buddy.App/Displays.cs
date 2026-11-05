using Avalonia;
using Avalonia.Controls;

namespace Buddy.App;

/// <summary>
/// Multi-monitor helpers. Avalonia places windows in device pixels (<see cref="Window.Position"/>) but sizes them
/// in device-independent units: <see cref="SizeInPixels"/> converts with the window's own scaling.
/// </summary>
public static class Displays
{
    /// <summary>The work area (taskbar/panels excluded) of the monitor the window is on, in pixels.</summary>
    public static PixelRect WorkAreaOf(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        return screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
    }

    /// <summary>The window's current size in pixels (its layout size times the monitor scaling).</summary>
    public static PixelSize SizeInPixels(Window window)
    {
        var size = window.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            size = new Size(double.IsNaN(window.Width) ? 0 : window.Width, double.IsNaN(window.Height) ? 0 : window.Height);
        }

        return PixelSize.FromSize(size, Scaling(window));
    }

    public static double Scaling(Window window) =>
        (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary)?.Scaling ?? window.RenderScaling;

    /// <summary>True if the point lies on some monitor, keeping a margin so a saved position is still reachable.</summary>
    public static bool IsOnScreen(Window window, PixelPoint point, int margin = 40) =>
        window.Screens.All.Any(s => s.Bounds.Contains(point) && s.Bounds.Contains(point + new PixelVector(margin, margin)));
}
