using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Buddy.Core;
using Microsoft.Win32;

namespace Buddy.App;

/// <summary>
/// Reads and writes the per-user files in %APPDATA%\Buddy: appsettings.json (the settings window's values,
/// which override the defaults next to the exe) and state.json (window position).
/// </summary>
public static class SettingsStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Buddy";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Buddy");

    public static string UserSettingsPath { get; } = Path.Combine(Folder, "appsettings.json");

    private static string StatePath { get; } = Path.Combine(Folder, "state.json");

    /// <summary>Deep copy, so the settings window can edit without touching the live settings.</summary>
    public static BuddySettings Clone(BuddySettings settings) =>
        JsonSerializer.Deserialize<BuddySettings>(JsonSerializer.Serialize(settings))!;

    public static void Save(BuddySettings settings)
    {
        Directory.CreateDirectory(Folder);
        var root = new JsonObject { [BuddySettings.SectionName] = JsonSerializer.SerializeToNode(settings) };
        File.WriteAllText(UserSettingsPath, root.ToJsonString(Json));
        ApplyStartWithWindows(settings.Pet.StartWithWindows);
    }

    public static (double Left, double Top)? LoadPosition()
    {
        try
        {
            var state = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(StatePath));
            return state is null ? null : (state.Left, state.Top);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SavePosition(double left, double top)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new WindowState(left, top)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Position is a convenience: losing it is harmless.
        }
    }

    /// <summary>Adds or removes the per-user "run at logon" entry for this exe.</summary>
    private static void ApplyStartWithWindows(bool enabled)
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

    private sealed record WindowState(double Left, double Top);
}
