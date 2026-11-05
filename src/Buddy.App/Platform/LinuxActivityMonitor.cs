using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Buddy.Core.Mentor;

namespace Buddy.App.Platform;

/// <summary>
/// Activity at the PC for the mentor, on Linux:
/// <list type="bullet">
/// <item>idle time from GNOME (org.gnome.Mutter.IdleMonitor over D-Bus, works on Wayland and X11), else from the
/// X11 screensaver extension (X11 sessions); when neither answers the user counts as always active;</item>
/// <item>busy: another app recording from the microphone (a PipeWire capture stream, via pw-dump) or GNOME
/// "do not disturb" (notification banners off). Full-screen apps are not detected: Wayland does not expose them.</item>
/// </list>
/// </summary>
public sealed partial class LinuxActivityMonitor : IActivityMonitor
{
    private static readonly TimeSpan IdleGap = TimeSpan.FromMinutes(5);

    /// <summary>Recorders this app itself starts for dictation: their capture stream is not "another app".</summary>
    private static readonly HashSet<string> OwnRecorders = new(StringComparer.OrdinalIgnoreCase) { "pw-record", "pw-cat", "arecord", "Buddy.App" };

    private DateTime _activeSince = DateTime.Now;
    private bool _mutterUnavailable;
    private bool _x11Unavailable;

    public TimeSpan ContinuousActivity
    {
        get
        {
            if (IdleTime() >= IdleGap)
            {
                _activeSince = DateTime.Now;
                return TimeSpan.Zero;
            }

            return DateTime.Now - _activeSince;
        }
    }

    public string? BusyReason
    {
        get
        {
            if (MicrophoneUser() is { } app)
            {
                return $"in chiamata (microfono usato da {app})";
            }

            return DoNotDisturb() ? "non disturbare attivo" : null;
        }
    }

    private TimeSpan IdleTime()
    {
        if (!_mutterUnavailable)
        {
            var reply = ProcessRunner.Run("gdbus",
            [
                "call", "--session", "--dest", "org.gnome.Mutter.IdleMonitor",
                "--object-path", "/org/gnome/Mutter/IdleMonitor/Core",
                "--method", "org.gnome.Mutter.IdleMonitor.GetIdletime",
            ]);
            if (reply is not null && ParseIdle(reply) is { } idle)
            {
                return idle;
            }

            _mutterUnavailable = true;
        }

        if (!_x11Unavailable && X11IdleTime() is { } x11)
        {
            return x11;
        }

        _x11Unavailable = true;
        return TimeSpan.Zero;
    }

    /// <summary>"(uint64 231,)" → 231 ms.</summary>
    internal static TimeSpan? ParseIdle(string reply) =>
        IdleReply().Match(reply) is { Success: true } match && ulong.TryParse(match.Groups[1].Value, out var ms)
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    /// <summary>The app recording from the microphone right now, other than the buddy itself.</summary>
    internal static string? MicrophoneUser()
    {
        var dump = ProcessRunner.Run("pw-dump", [], timeoutMs: 4000);
        return dump is null ? null : MicrophoneUser(dump, Environment.ProcessId);
    }

    internal static string? MicrophoneUser(string pwDump, int ownProcessId)
    {
        try
        {
            using var json = JsonDocument.Parse(pwDump);
            foreach (var node in json.RootElement.EnumerateArray())
            {
                if (!node.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (Text(props, "media.class") != "Stream/Input/Audio"
                    || (info.TryGetProperty("state", out var state) && state.GetString() is not ("running" or null)))
                {
                    continue;
                }

                var binary = Text(props, "application.process.binary") ?? "";
                var pid = Text(props, "application.process.id");
                if (OwnRecorders.Contains(binary) || pid == ownProcessId.ToString())
                {
                    continue;
                }

                return Text(props, "application.name") ?? (binary.Length > 0 ? binary : "un'altra app");
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? Text(JsonElement props, string name) =>
        props.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;

    /// <summary>GNOME "do not disturb" turns notification banners off.</summary>
    private static bool DoNotDisturb() =>
        ProcessRunner.Run("gsettings", ["get", "org.gnome.desktop.notifications", "show-banners"])?.Trim() == "false";

    private static TimeSpan? X11IdleTime()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland")
        {
            // Under Wayland the X server only sees input to X11 windows: its idle time would be wrong.
            return null;
        }

        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var info = XScreenSaverAllocInfo();
                if (info == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return XScreenSaverQueryInfo(display, XDefaultRootWindow(display), info) != 0
                        ? TimeSpan.FromMilliseconds(Marshal.PtrToStructure<XScreenSaverInfo>(info).Idle)
                        : null;
                }
                finally
                {
                    XFree(info);
                }
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"uint64\s+(\d+)")]
    private static partial Regex IdleReply();

    [StructLayout(LayoutKind.Sequential)]
    private struct XScreenSaverInfo
    {
        public IntPtr Window;
        public int State;
        public int Kind;
        public ulong Til;
        public ulong Idle;
        public ulong EventMask;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);

    [DllImport("libXss.so.1")]
    private static extern IntPtr XScreenSaverAllocInfo();

    [DllImport("libXss.so.1")]
    private static extern int XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);
}
