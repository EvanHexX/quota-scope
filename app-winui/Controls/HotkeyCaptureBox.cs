using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QuotaScope.Hotkeys;
using Windows.System;
using Windows.UI.Core;

namespace QuotaScope.WinUI.Controls;

// Focusable capture control: press a real key combination to produce a
// HotkeyDefinition. Backspace/Delete clears the binding; Esc reverts the text.
internal sealed class HotkeyCaptureBox : TextBox
{
    private string _committedText = "";

    public event Action<HotkeyDefinition>? Captured;
    public event Action? Cleared;

    public HotkeyCaptureBox()
    {
        IsReadOnly = true;
        IsSpellCheckEnabled = false;
        Width = 200;
        PlaceholderText = "Click, then press keys";
        KeyDown += OnCaptureKeyDown;
    }

    public void SetBinding(string text)
    {
        _committedText = text;
        Text = text;
    }

    private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var key = e.Key;

        if (key == VirtualKey.Escape)
        {
            Text = _committedText;
            return;
        }
        if (key is VirtualKey.Back or VirtualKey.Delete)
        {
            _committedText = "";
            Text = "";
            Cleared?.Invoke();
            return;
        }
        if (IsModifierKey(key))
        {
            return; // wait for the non-modifier key
        }

        var modifiers = CurrentModifiers();
        var virtualKey = (uint)key;
        if (modifiers == 0 || !HotkeyDefinition.IsSupportedKey(virtualKey))
        {
            return; // needs at least one modifier and a supported key
        }

        var definition = new HotkeyDefinition(modifiers, virtualKey);
        _committedText = definition.Format();
        Text = _committedText;
        Captured?.Invoke(definition);
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static uint CurrentModifiers()
    {
        uint modifiers = 0;
        if (IsDown(VirtualKey.Control)) modifiers |= HotkeyDefinition.ModControl;
        if (IsDown(VirtualKey.Menu)) modifiers |= HotkeyDefinition.ModAlt;
        if (IsDown(VirtualKey.Shift)) modifiers |= HotkeyDefinition.ModShift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= HotkeyDefinition.ModWin;
        return modifiers;
    }

    private static bool IsDown(VirtualKey key)
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }
}
