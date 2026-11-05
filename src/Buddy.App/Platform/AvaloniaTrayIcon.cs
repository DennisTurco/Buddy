using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Buddy.App.Platform;

/// <summary>
/// Notification-area icon through Avalonia (StatusNotifierItem on Linux: GNOME needs the AppIndicator extension,
/// which Ubuntu ships enabled) and desktop notifications through notify-send; clicking a notification opens the chat.
/// </summary>
public sealed class AvaloniaTrayIcon : ITrayIcon
{
    private readonly TrayIcon _icon;
    private readonly TrayActions _actions;
    private readonly string _iconPath;

    public AvaloniaTrayIcon(string name, byte[] iconPng, TrayActions actions)
    {
        _actions = actions;
        _iconPath = Path.Combine(SettingsStore.Folder, "icon.png");
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllBytes(_iconPath, iconPng);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var menu = new NativeMenu();
        menu.Items.Add(Item("Chat", actions.OpenChat));
        menu.Items.Add(Item("Mostra/Nascondi Buddy", actions.ToggleBuddy));
        menu.Items.Add(Item("Bacheca del progetto", actions.OpenProject));
        menu.Items.Add(Item("Impostazioni…", actions.OpenSettings));
        menu.Items.Add(Item("Apri i log", actions.OpenLogs));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Esci", actions.Exit));

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(new MemoryStream(iconPng)),
            ToolTipText = name,
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => actions.OpenChat();
        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, [_icon]);
        }
    }

    public void Notify(string title, string text, bool success)
    {
        var body = text.Length > 240 ? text[..240] + "…" : text;
        var startInfo = new ProcessStartInfo("notify-send")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "--app-name=Buddy", $"--icon={_iconPath}", $"--urgency={(success ? "normal" : "critical")}",
                     "--action=default=Apri la chat", "--wait", title, body,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return;
                }

                // --wait keeps notify-send alive until the notification closes; it prints the action if clicked.
                var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                if (output.Trim() == "default")
                {
                    Dispatcher.UIThread.Post(_actions.OpenChat);
                }
            }
            catch (Win32Exception)
            {
                // notify-send missing (package libnotify-bin): notifications are a convenience.
            }
        });
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
