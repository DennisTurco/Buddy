using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Buddy.App;

/// <summary>
/// Notification-area icon: a menu that works even when the pet is hidden, and Windows notifications
/// (shown as toasts on Windows 10/11) for long Claude tasks.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _image;

    public TrayIcon(string name, BitmapSource sprite, Action openChat, Action openSettings, Action openLogs, Action exit)
    {
        _image = ToIcon(sprite);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Chat", null, (_, _) => openChat());
        menu.Items.Add("Impostazioni…", null, (_, _) => openSettings());
        menu.Items.Add("Apri i log", null, (_, _) => openLogs());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = name.Length > 60 ? name[..60] : name,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                openChat();
            }
        };
        _icon.BalloonTipClicked += (_, _) => openChat();
    }

    public void Notify(string title, string text, bool success) =>
        _icon.ShowBalloonTip(5000, title, text.Length > 240 ? text[..240] + "…" : text, success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);

    /// <summary>Scales the pixel-art frame to 32x32 without smoothing and wraps it in an icon.</summary>
    private static System.Drawing.Icon ToIcon(BitmapSource sprite)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            context.DrawImage(sprite, new System.Windows.Rect(0, 0, 32, 32));
        }

        var target = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[32 * 32 * 4];
        target.CopyPixels(pixels, 32 * 4, 0);

        using var bitmap = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, 32, 32), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        bitmap.UnlockBits(data);

        var handle = bitmap.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
