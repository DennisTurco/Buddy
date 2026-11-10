using System.Text.Json;
using System.Text.Json.Nodes;
using Buddy.Core;

namespace Buddy.App;

/// <summary>
/// Reads and writes the per-user files in the user's app-data folder (%APPDATA%\Buddy on Windows,
/// ~/.config/Buddy on Linux): appsettings.json (the settings window's values, which override the defaults next to
/// the exe) and state.json (window position).
/// </summary>
public static class SettingsStore
{
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
        Platform.PlatformServices.SetStartAtLogin(settings.Pet.StartWithWindows);
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

    private sealed record WindowState(double Left, double Top);

    private static string UsageFoldedPath => Path.Combine(Folder, "usage-folded");

    /// <summary>Whether the Claude usage card of the project window is folded to one line (a marker file).</summary>
    public static bool UsageFolded
    {
        get => File.Exists(UsageFoldedPath);
        set
        {
            try
            {
                if (value)
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(UsageFoldedPath, "");
                }
                else
                {
                    File.Delete(UsageFoldedPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A convenience: losing it is harmless.
            }
        }
    }
}
