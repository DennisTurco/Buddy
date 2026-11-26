using System.Text.Json;
using System.Text.Json.Nodes;
using Buddy.Core;
using Buddy.Core.Chat;

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

    public static (double Left, double Top)? LoadPosition() =>
        LoadState() is { Left: { } left, Top: { } top } ? (left, top) : null;

    public static void SavePosition(double left, double top) =>
        SaveState((LoadState() ?? new WindowState()) with { Left = left, Top = top });

    /// <summary>The chat bubble's size chosen by dragging its edges, or null for the default one.</summary>
    public static (double Width, double Height)? LoadBubbleSize() =>
        LoadState() is { BubbleWidth: { } width, BubbleHeight: { } height } ? (width, height) : null;

    public static void SaveBubbleSize(double width, double height) =>
        SaveState((LoadState() ?? new WindowState()) with { BubbleWidth = width, BubbleHeight = height });

    /// <summary>The model the chat was talking to when the app last ran (Claude or local), or null when never saved.</summary>
    public static ChatMode? LoadChatMode() =>
        Enum.TryParse<ChatMode>(LoadState()?.ChatMode, out var mode) ? mode : null;

    public static void SaveChatMode(ChatMode mode) =>
        SaveState((LoadState() ?? new WindowState()) with { ChatMode = mode.ToString() });

    private static WindowState? LoadState()
    {
        try
        {
            return JsonSerializer.Deserialize<WindowState>(File.ReadAllText(StatePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveState(WindowState state)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Position and size are a convenience: losing them is harmless.
        }
    }

    /// <summary>The pet's position, the bubble's size and the chat's model (each missing until first saved).</summary>
    private sealed record WindowState(
        double? Left = null, double? Top = null, double? BubbleWidth = null, double? BubbleHeight = null, string? ChatMode = null);

    /// <summary>Whether the Claude usage card of the project window is folded to one line (a marker file).</summary>
    public static bool UsageFolded
    {
        get => GetFlag("usage-folded");
        set => SetFlag("usage-folded", value);
    }

    /// <summary>Whether the project window's side bar is hidden, to give the page all the width (a marker file).</summary>
    public static bool SidebarCollapsed
    {
        get => GetFlag("sidebar-collapsed");
        set => SetFlag("sidebar-collapsed", value);
    }

    /// <summary>Whether the list of notes in the Note view is hidden, to read the note on all the width (a marker file).</summary>
    public static bool NotesListCollapsed
    {
        get => GetFlag("notes-list-collapsed");
        set => SetFlag("notes-list-collapsed", value);
    }

    /// <summary>The width of the board's chat panel, as last dragged (a small file with the number; null until then).</summary>
    public static double? BoardChatWidth
    {
        get
        {
            try
            {
                var path = Path.Combine(Folder, "board-chat-width");
                return File.Exists(path) && double.TryParse(File.ReadAllText(path), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var width) ? width : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        set
        {
            try
            {
                var path = Path.Combine(Folder, "board-chat-width");
                if (value is { } width)
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(path, Math.Round(width).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A convenience: losing it is harmless.
            }
        }
    }

    /// <summary>Whether a group of pages in the project window's side bar is folded (a marker file per group).</summary>
    public static bool NavGroupClosed(string group) => GetFlag("nav-closed-" + group);

    public static void SetNavGroupClosed(string group, bool closed) => SetFlag("nav-closed-" + group, closed);

    /// <summary>Whether a folder in the notes list is folded (a marker file per folder name).</summary>
    public static bool NotesFolderClosed(string folder) => GetFlag("notes-closed-" + SafeName(folder));

    public static void SetNotesFolderClosed(string folder, bool closed) => SetFlag("notes-closed-" + SafeName(folder), closed);

    /// <summary>A folder name as part of a file name: letters and digits kept, the rest "_" ("" is the project's top).</summary>
    private static string SafeName(string name) =>
        name.Length == 0 ? "_root" : new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

    private static bool GetFlag(string name) => File.Exists(Path.Combine(Folder, name));

    private static void SetFlag(string name, bool value)
    {
        try
        {
            var path = Path.Combine(Folder, name);
            if (value)
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(path, "");
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A convenience: losing it is harmless.
        }
    }
}
