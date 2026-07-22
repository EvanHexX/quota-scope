using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using QuotaScope.Hotkeys;

namespace QuotaScope.WinUI.Hotkeys;

// RegisterHotKey host: a message-only window created on the UI thread so
// WM_HOTKEY lands on the dispatcher thread's message loop. The WndProc
// delegate is held in a field to keep it rooted for the window's lifetime.
internal sealed class HotkeyWindow : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _hwnd;
    private readonly HashSet<int> _registered = new();
    private bool _disposed;

    public event Action<int>? Pressed;

    public HotkeyWindow()
    {
        _wndProc = WndProc;
        var hInstance = GetModuleHandle(null);
        var className = "QuotaScopeHotkeyWindow-" + Environment.CurrentManagedThreadId;
        var wndClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = className
        };
        if (RegisterClassEx(ref wndClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
        }

        _hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
        }
    }

    public bool TryRegister(int id, HotkeyDefinition definition, out string error)
    {
        Unregister(id);
        if (RegisterHotKey(_hwnd, id, definition.Modifiers, definition.VirtualKey))
        {
            _registered.Add(id);
            error = string.Empty;
            return true;
        }

        var code = Marshal.GetLastWin32Error();
        error = code == ErrorHotkeyAlreadyRegistered
            ? "This hotkey is already in use by another application."
            : $"Hotkey registration failed (Win32 error {code}).";
        return false;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
        {
            UnregisterHotKey(_hwnd, id);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmHotkey)
        {
            Pressed?.Invoke(wParam.ToInt32());
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _registered)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _registered.Clear();
        DestroyWindow(_hwnd);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
