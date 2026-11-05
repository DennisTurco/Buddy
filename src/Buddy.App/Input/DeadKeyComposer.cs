using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Buddy.Core.Text;

namespace Buddy.App.Input;

/// <summary>
/// Accents with a US International keyboard on Linux. There ' ` " ^ ~ are dead keys, which the desktop's input method
/// (IBus) normally turns into é, à… — but an X11 app under Wayland does not reach it, so Avalonia gets the dead key
/// without text and the accent is lost. This composes them for every text box of the app: the dead key is remembered,
/// and the next character typed is combined with it (see <see cref="DeadKeys"/>).
/// A key is treated as dead only when it does not produce its plain symbol, so a plain US layout is unaffected.
/// </summary>
public static class DeadKeyComposer
{
    private static TextBox? _box;
    private static char? _pending;
    private static bool _waitingForKeyText;

    public static void Register()
    {
        InputElement.KeyDownEvent.AddClassHandler<TextBox>(OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.TextInputEvent.AddClassHandler<TextBox>(OnTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.LostFocusEvent.AddClassHandler<TextBox>((box, _) =>
        {
            if (box == _box)
            {
                Clear();
            }
        });
    }

    private static void OnKeyDown(TextBox box, KeyEventArgs e)
    {
        _waitingForKeyText = false;
        if (Accent(e) is { } accent)
        {
            _box = box;
            _pending = accent;
            _waitingForKeyText = true; // a text event for the dead key itself (e.g. "´") is swallowed
            return;
        }

        // Anything that is not a character (backspace, arrows, Esc…) cancels the accent, as the layout does.
        if (_pending is not null && string.IsNullOrEmpty(e.KeySymbol) && e.Key is not (Key.LeftShift or Key.RightShift or Key.CapsLock))
        {
            Clear();
        }
    }

    private static void OnTextInput(TextBox box, TextInputEventArgs e)
    {
        if (_pending is not { } accent || box != _box || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (_waitingForKeyText)
        {
            _waitingForKeyText = false;
            e.Handled = true;
            return;
        }

        e.Text = DeadKeys.Compose(accent, e.Text);
        Clear();
    }

    /// <summary>The accent of a dead key press: the key of ' " ` ~ ^ that did not type its plain symbol.</summary>
    private static char? Accent(KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return null;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        (char Accent, string Plain)? key = e.PhysicalKey switch
        {
            PhysicalKey.Quote => shift ? ('"', "\"") : ('\'', "'"),
            PhysicalKey.Backquote => shift ? ('~', "~") : ('`', "`"),
            PhysicalKey.Digit6 when shift => ('^', "^"),
            _ => null,
        };

        return key is { } k && e.KeySymbol != k.Plain && DeadKeys.IsDeadKey(k.Accent) ? k.Accent : null;
    }

    private static void Clear()
    {
        _box = null;
        _pending = null;
        _waitingForKeyText = false;
    }
}
