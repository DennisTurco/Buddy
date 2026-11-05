using System.IO.Pipes;
using Avalonia.Threading;

namespace Buddy.App.Platform;

/// <summary>
/// One buddy per user. The first instance holds a lock file and listens on a local pipe (a Unix socket on Linux);
/// a later launch sends its command there and exits. This is also how the global shortcut works where the desktop
/// owns shortcuts: the user binds one to `Buddy.App --hotkey`, which forwards "hotkey" to the running buddy.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string ShowCommand = "show";
    public const string HotkeyCommand = "hotkey";

    private static readonly string PipeName = $"buddy-{Environment.UserName}";
    private static readonly string LockPath = Path.Combine(SettingsStore.Folder, "instance.lock");

    private readonly FileStream _lock;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream lockFile) => _lock = lockFile;

    /// <summary>Raised on the UI thread with each command sent by a later launch.</summary>
    public event Action<string>? CommandReceived;

    /// <summary>
    /// Becomes the running instance, or returns null if another one holds the lock. After a restart the old
    /// process may still be closing: <paramref name="waitForPrevious"/> retries for a few seconds.
    /// </summary>
    public static SingleInstance? TryAcquire(bool waitForPrevious)
    {
        Directory.CreateDirectory(SettingsStore.Folder);
        var deadline = DateTime.UtcNow.AddSeconds(waitForPrevious ? 10 : 0);
        while (true)
        {
            try
            {
                // FileShare.None is an exclusive lock on Windows and an flock() on Unix: released when the process ends.
                var file = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!OperatingSystem.IsWindows())
                {
                    // A socket left by a buddy that crashed would block the new listener; we own the lock, so it is stale.
                    File.Delete(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName));
                }

                var instance = new SingleInstance(file);
                _ = instance.ListenAsync();
                return instance;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>Sends a command to the running instance. False if none answered.</summary>
    public static bool Send(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(command);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    var command = line.Trim();
                    Dispatcher.UIThread.Post(() => CommandReceived?.Invoke(command));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A client that hung up mid-message, or a stale socket: wait a moment and listen again.
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _lock.Dispose();
    }
}
