using Avalonia;
using Avalonia.Controls;
using Buddy.App.Platform;

namespace Buddy.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Development aids run their own short-lived app and do not touch the running buddy.
        var tool = args.Length > 0 && args[0] is "--snapshot" or "--export-icon";

        SingleInstance? instance = null;
        if (!tool)
        {
            instance = SingleInstance.TryAcquire(waitForPrevious: args.Contains("--restarted"));
            if (instance is null)
            {
                // Already running: hand over the request (show the chat, or the global shortcut) and leave.
                SingleInstance.Send(args.Contains("--hotkey") ? SingleInstance.HotkeyCommand : SingleInstance.ShowCommand);
                return 0;
            }
        }

        App.Instance = instance;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            instance?.Dispose();
        }
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
