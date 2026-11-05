using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Buddy.App;

/// <summary>Multi-monitor helpers: the work area of the monitor a window is on, in WPF units.</summary>
public static class Screens
{
    public static Rect WorkAreaOf(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is not { } target)
        {
            return SystemParameters.WorkArea;
        }

        // Screen works in device pixels, windows in WPF units (DPI scaling): convert both ways.
        var center = new Point(window.Left + window.ActualWidth / 2, window.Top + window.ActualHeight / 2);
        var device = target.TransformToDevice.Transform(center);
        var area = Forms.Screen.FromPoint(new System.Drawing.Point((int)device.X, (int)device.Y)).WorkingArea;
        Matrix fromDevice = target.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(area.Left, area.Top));
        var bottomRight = fromDevice.Transform(new Point(area.Right, area.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
