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
}
