using Buddy.Core.Mentor;

namespace Buddy.App.Platform;

/// <summary>Notification-area icon with its menu, and desktop notifications.</summary>
public interface ITrayIcon : IDisposable
{
    void Notify(string title, string text, bool success);
}

/// <summary>What the tray menu (and a click on a notification) can do.</summary>
public sealed record TrayActions(Action OpenChat, Action ToggleBuddy, Action OpenProject, Action OpenSettings, Action OpenLogs, Action Exit);

/// <summary>Microphone capture: 16 kHz, mono, signed 16-bit little-endian PCM (what Whisper expects).</summary>
public interface IAudioCapture : IDisposable
{
    bool IsRecording { get; }

    void Start();

    /// <summary>Stops and returns the raw PCM recorded since <see cref="Start"/>.</summary>
    Task<byte[]> StopAsync();
}

/// <summary>
/// The OS-specific pieces, chosen once at startup. Windows keeps the Win32 implementations of the WPF version;
/// Linux uses the freedesktop/GNOME equivalents (D-Bus through gdbus, PipeWire, notify-send, XDG autostart).
/// </summary>
public static class PlatformServices
{
    public static IActivityMonitor CreateActivityMonitor() =>
        OperatingSystem.IsWindows() ? new WindowsActivityMonitor() : new LinuxActivityMonitor();

    public static IAudioCapture CreateAudioCapture()
    {
#if WINDOWS
        return new WindowsAudioCapture();
#else
        return new LinuxAudioCapture();
#endif
    }

    public static ITrayIcon CreateTrayIcon(string name, byte[] iconPng, TrayActions actions)
    {
#if WINDOWS
        return new WindowsTrayIcon(name, iconPng, actions);
#else
        return new AvaloniaTrayIcon(name, iconPng, actions);
#endif
    }

    /// <summary>
    /// Registers the system-wide shortcut. Only Windows lets an app grab a global key; elsewhere (Wayland in
    /// particular) the desktop owns shortcuts, so the user binds one to `Buddy.App --hotkey` (see README) and the
    /// running instance gets it through <see cref="SingleInstance"/>.
    /// </summary>
    public static IDisposable? RegisterHotkey(string? gesture, Action pressed, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return WindowsHotkey.TryRegister(gesture, pressed, out error);
    }

    /// <summary>Adds or removes the per-user "start at login" entry for this app.</summary>
    public static void SetStartAtLogin(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsAutostart.Apply(enabled);
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxAutostart.Apply(enabled);
        }
    }

    /// <summary>The command line that starts this app again (the apphost, or `dotnet Buddy.App.dll`).</summary>
    public static (string FileName, IReadOnlyList<string> Arguments) SelfCommand()
    {
        var process = Environment.ProcessPath ?? "";
        var name = Path.GetFileNameWithoutExtension(process);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (process, [typeof(PlatformServices).Assembly.Location]);
        }

        return (process, []);
    }
}
