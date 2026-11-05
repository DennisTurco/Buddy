using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Buddy.App.Platform;

/// <summary>Windows: the per-user Run key.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Buddy";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled && Environment.ProcessPath is { } exe)
        {
            key.SetValue(RunValue, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}

/// <summary>Linux: an XDG autostart entry, ~/.config/autostart/buddy.desktop.</summary>
internal static class LinuxAutostart
{
    public static string EntryPath { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "buddy.desktop");

    public static void Apply(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(EntryPath);
            return;
        }

        var (file, arguments) = PlatformServices.SelfCommand();
        var exec = string.Join(' ', new[] { file }.Concat(arguments).Select(Quote));
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath, $"""
            [Desktop Entry]
            Type=Application
            Name=Buddy
            Comment=Desk companion and mentor
            Exec={exec}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }

    /// <summary>Desktop-entry quoting: double quotes, with \ " ` $ escaped.</summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
}
