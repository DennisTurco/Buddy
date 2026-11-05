using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace Buddy.App.Platform;

/// <summary>
/// A system-wide keyboard shortcut on Windows (RegisterHotKey). It is bound to a small background thread with its
/// own message loop rather than to a window, so it does not depend on the UI framework; presses are posted to the
/// UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;
    private const int Id = 0xB0DD;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly Thread _thread;
    private uint _threadId;

    private WindowsHotkey(uint modifiers, uint key, Action pressed, TaskCompletionSource<bool> registered)
    {
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            if (!RegisterHotKey(IntPtr.Zero, Id, modifiers | ModNoRepeat, key))
            {
                registered.SetResult(false);
                return;
            }

            registered.SetResult(true);
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmHotkey && message.WParam.ToInt32() == Id)
                {
                    Dispatcher.UIThread.Post(pressed);
                }
            }

            UnregisterHotKey(IntPtr.Zero, Id);
        })
        {
            IsBackground = true,
            Name = "Buddy hotkey",
        };
        _thread.Start();
    }

    /// <summary>Registers e.g. "Ctrl+Alt+B". Returns null with a reason when the text is invalid or the shortcut is taken.</summary>
    public static WindowsHotkey? TryRegister(string? gesture, Action pressed, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        if (!TryParse(gesture, out var modifiers, out var key))
        {
            error = $"Scorciatoia non valida: \"{gesture}\" (esempio: Ctrl+Alt+B).";
            return null;
        }

        var registered = new TaskCompletionSource<bool>();
        var hotkey = new WindowsHotkey(modifiers, key, pressed, registered);
        if (!registered.Task.Wait(TimeSpan.FromSeconds(2)) || !registered.Task.Result)
        {
            error = $"La scorciatoia {gesture} è già usata da un altro programma: cambiala nelle impostazioni.";
            hotkey.Dispose();
            return null;
        }

        return hotkey;
    }

    /// <summary>"Ctrl+Alt+B" → MOD_CONTROL|MOD_ALT and the virtual-key code of B.</summary>
    internal static bool TryParse(string gesture, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows" or "super": modifiers |= ModWin; break;
                default:
                    if (VirtualKey(part) is not { } vk)
                    {
                        return false;
                    }

                    key = vk;
                    break;
            }
        }

        return key != 0 && modifiers != 0;
    }

    private static uint? VirtualKey(string name)
    {
        var upper = name.ToUpperInvariant();
        if (upper.Length == 1 && (char.IsAsciiLetterUpper(upper[0]) || char.IsAsciiDigit(upper[0])))
        {
            return upper[0];
        }

        if (upper.Length == 2 && upper[0] == 'D' && char.IsAsciiDigit(upper[1]))
        {
            return upper[1];
        }

        if (upper.StartsWith('F') && int.TryParse(upper[1..], out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x70 + f - 1);
        }

        return upper switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "PAUSE" => 0x13,
            _ => null,
        };
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
