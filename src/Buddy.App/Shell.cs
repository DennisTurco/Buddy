using System.ComponentModel;
using System.Diagnostics;

namespace Buddy.App;

/// <summary>Opens files, folders and links (https://, obsidian://) with the desktop's default handler.</summary>
public static class Shell
{
    public static bool Open(string target)
    {
        try
        {
            // On Linux UseShellExecute goes through xdg-open; on Windows through ShellExecute.
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Shows a file in the file manager: on Windows, Explorer opens its folder with the file selected; elsewhere the
    /// folder opens. A folder opens as itself.
    /// </summary>
    public static bool Reveal(string path)
    {
        if (Directory.Exists(path))
        {
            return Open(path);
        }

        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            try
            {
                var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                explorer.ArgumentList.Add($"/select,{path}");
                Process.Start(explorer)?.Dispose();
                return true;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // the folder, below
            }
        }

        return Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) && Open(folder);
    }
}
