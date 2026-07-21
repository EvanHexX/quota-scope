using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuotaScope;

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x4344;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private readonly Action _onPressed;
    private bool _registered;

    public HotkeyWindow(Action onPressed)
    {
        _onPressed = onPressed;
        CreateHandle(new CreateParams());
    }

    public void Register()
    {
        _registered = RegisterHotKey(Handle, HotkeyId, ModControl | ModAlt, (uint)Keys.U);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            _onPressed();
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
