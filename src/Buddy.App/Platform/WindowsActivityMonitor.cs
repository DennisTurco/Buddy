using System.IO;
using System.Runtime.InteropServices;
using Buddy.Core.Mentor;
using Microsoft.Win32;

namespace Buddy.App.Platform;

/// <summary>
/// Activity at the PC for the mentor, on Windows:
/// <list type="bullet">
/// <item>continuous activity from the time of the last keyboard/mouse input (GetLastInputInfo); an idle gap of
/// <see cref="IdleGap"/> or more starts a new period;</item>
/// <item>whether the user is busy: microphone in use by another app (a call), a full-screen app or presentation,
/// or Windows "do not disturb" (SHQueryUserNotificationState).</item>
/// </list>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsActivityMonitor : IActivityMonitor
{
    private static readonly TimeSpan IdleGap = TimeSpan.FromMinutes(5);

    private const string MicrophoneStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private DateTime _activeSince = DateTime.Now;

    public TimeSpan ContinuousActivity
    {
        get
        {
            var idle = IdleTime();
            if (idle >= IdleGap)
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

            if (SHQueryUserNotificationState(out var state) != 0)
            {
                return null;
            }

            return state switch
            {
                NotificationState.Busy => "app a schermo intero",
                NotificationState.RunningD3DFullScreen => "gioco o video a schermo intero",
                NotificationState.PresentationMode => "presentazione in corso",
                NotificationState.QuietTime => "non disturbare attivo",
                _ => null,
            };
        }
    }

    /// <summary>
    /// The app currently recording from the microphone, other than the buddy itself: Windows keeps, per app,
    /// the time it last started and stopped using it; a stop time of 0 means "in use now".
    /// </summary>
    internal static string? MicrophoneUser()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(MicrophoneStore);
            if (root is null)
            {
                return null;
            }

            foreach (var (key, name) in Apps(root))
            {
                using (key)
                {
                    if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0
                        && key.GetValue("LastUsedTimeStart") is long start && start > 0
                        && !name.Contains("Buddy.App", StringComparison.OrdinalIgnoreCase))
                    {
                        return FriendlyName(name);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static IEnumerable<(RegistryKey Key, string Name)> Apps(RegistryKey root)
    {
        foreach (var name in root.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var nonPackaged = root.OpenSubKey(name);
                foreach (var exe in nonPackaged?.GetSubKeyNames() ?? [])
                {
                    if (nonPackaged!.OpenSubKey(exe) is { } key)
                    {
                        yield return (key, exe);
                    }
                }
            }
            else if (root.OpenSubKey(name) is { } key)
            {
                yield return (key, name);
            }
        }
    }

    /// <summary>"C:#Program Files#…#ms-teams.exe" → "ms-teams"; "MSTeams_8wekyb3d8bbwe" → "MSTeams".</summary>
    private static string FriendlyName(string name)
    {
        var last = Path.GetFileNameWithoutExtension(name.Split('#').Last());
        var underscore = last.IndexOf('_');
        return underscore > 0 ? last[..underscore] : last;
    }

    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time))
            : TimeSpan.Zero;
    }

    private enum NotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out NotificationState state);
}
