#if WINDOWS
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Buddy.App.Platform;

/// <summary>
/// Notification-area icon on Windows (WinForms NotifyIcon): a menu that works even when the pet is hidden, and
/// Windows notifications (shown as toasts on Windows 10/11) for long Claude tasks. Avalonia's own tray icon has
/// no notifications on Windows, hence WinForms here.
/// </summary>
public sealed class WindowsTrayIcon : ITrayIcon
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _image;

    public WindowsTrayIcon(string name, byte[] iconPng, TrayActions actions)
    {
        _image = ToIcon(iconPng);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Chat", null, (_, _) => actions.OpenChat());
        menu.Items.Add("Mostra/Nascondi Buddy", null, (_, _) => actions.ToggleBuddy());
        menu.Items.Add("Bacheca del progetto", null, (_, _) => actions.OpenProject());
        menu.Items.Add("Impostazioni…", null, (_, _) => actions.OpenSettings());
        menu.Items.Add("Apri i log", null, (_, _) => actions.OpenLogs());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => actions.Exit());

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
                actions.OpenChat();
            }
        };
        _icon.BalloonTipClicked += (_, _) => actions.OpenChat();
    }

    public void Notify(string title, string text, bool success) =>
        _icon.ShowBalloonTip(5000, title, text.Length > 240 ? text[..240] + "…" : text, success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);

    /// <summary>The 32x32 PNG wrapped in an icon handle.</summary>
    private static System.Drawing.Icon ToIcon(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new System.Drawing.Bitmap(stream);
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
#endif
