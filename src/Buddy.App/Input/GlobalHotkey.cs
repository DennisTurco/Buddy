using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Buddy.App.Input;

/// <summary>A system-wide keyboard shortcut (RegisterHotKey) bound to a window's message loop.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int Id = 0xB0DD;

    private readonly HwndSource _source;
    private readonly Action _pressed;
    private bool _registered;

    private GlobalHotkey(HwndSource source, Action pressed)
    {
        _source = source;
        _pressed = pressed;
    }

    /// <summary>Registers e.g. "Ctrl+Alt+B" on the window. Returns null with a reason when the text is invalid or the shortcut is taken.</summary>
    public static GlobalHotkey? TryRegister(Window window, string? gesture, Action pressed, out string? error)
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

        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())!;
        var hotkey = new GlobalHotkey(source, pressed);
        source.AddHook(hotkey.Hook);
        hotkey._registered = RegisterHotKey(source.Handle, Id, (uint)modifiers | 0x4000 /* MOD_NOREPEAT */, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (!hotkey._registered)
        {
            error = $"La scorciatoia {gesture} è già usata da un altro programma: cambiala nelle impostazioni.";
            hotkey.Dispose();
            return null;
        }

        return hotkey;
    }

    internal static bool TryParse(string gesture, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key))
                    {
                        return false;
                    }
                    break;
            }
        }

        return key != Key.None && modifiers != ModifierKeys.None;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            _pressed();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, Id);
            _registered = false;
        }

        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
