using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QuotaScope.WinUI.Tray;

// Menu model for the native tray context menu (Win32 TrackPopupMenu).
internal sealed record TrayMenuItem(string? Text, Action? Invoke)
{
    public static TrayMenuItem Separator => new(null, null);
    public bool IsSeparator => Text is null;
}

// Thin seam over the tray implementation.
internal interface ITrayIcon : IDisposable
{
    event Action? LeftClicked;
    void SetIcon(System.Drawing.Icon icon);
    void SetTooltip(string text);
    void SetMenu(IReadOnlyList<TrayMenuItem> items);
    void ShowNotification(string title, string message);
    void Show();
}

// Native Shell_NotifyIcon host. Replaces H.NotifyIcon.WinUI, whose internal
// SUBCLASSPROC delegate was garbage-collected while still registered, killing
// the whole app via FailFast ("callback on a garbage collected delegate").
// Every native callback here is rooted in an instance field for the window's
// lifetime. Must be created on the UI thread.
internal sealed class TrayIconHost : ITrayIcon
{
    private const uint WmTrayCallback = 0x8000 + 1; // WM_APP + 1
    private const uint WmLbuttonup = 0x0202;
    private const uint WmRbuttonup = 0x0205;
    private const uint WmContextMenu = 0x007B;

    private readonly WndProcDelegate _wndProc; // GC root
    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private System.Drawing.Icon? _icon;
    private string _tooltip = "";
    private IReadOnlyList<TrayMenuItem> _menuItems = Array.Empty<TrayMenuItem>();
    private bool _added;
    private bool _disposed;

    public event Action? LeftClicked;

    public TrayIconHost()
    {
        _wndProc = WndProc;
        var hInstance = GetModuleHandle(null);
        var className = "QuotaScopeTrayWindow";
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

        // A real (hidden) top-level window, not message-only: the TaskbarCreated
        // broadcast used for explorer-restart recovery is not sent to
        // message-only windows.
        _hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
        }
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    }

    public void SetIcon(System.Drawing.Icon icon)
    {
        var previous = _icon;
        _icon = icon;
        if (_added)
        {
            var data = BuildData(NifIcon);
            data.hIcon = icon.Handle;
            Shell_NotifyIcon(NimModify, ref data);
        }
        previous?.Dispose();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length <= 127 ? text : text[..127];
        if (_added)
        {
            var data = BuildData(NifTip);
            data.szTip = _tooltip;
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items) => _menuItems = items;

    public void ShowNotification(string title, string message)
    {
        if (!_added) return;
        var data = BuildData(NifInfo);
        data.szInfoTitle = title.Length <= 63 ? title : title[..63];
        data.szInfo = message.Length <= 255 ? message : message[..255];
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Show() => AddIcon();

    private void AddIcon()
    {
        if (_disposed) return;
        var data = BuildData(NifMessage | NifIcon | NifTip);
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _icon?.Handle ?? IntPtr.Zero;
        data.szTip = _tooltip;
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    private NotifyIconData BuildData(uint flags)
    {
        return new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayCallback)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLbuttonup:
                    LeftClicked?.Invoke();
                    break;
                case WmRbuttonup:
                case WmContextMenu:
                    ShowContextMenu();
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted: re-add the icon.
            _added = false;
            AddIcon();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var items = _menuItems;
        if (items.Count == 0) return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            uint id = 1;
            var actions = new Dictionary<uint, Action>();
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                    continue;
                }
                if (item.Invoke is not null)
                {
                    actions[id] = item.Invoke;
                }
                AppendMenu(menu, item.Invoke is null ? MfString | MfGrayed : MfString, new UIntPtr(id), item.Text);
                id++;
            }

            // Required so the menu closes when clicking elsewhere.
            SetForegroundWindow(_hwnd);
            GetCursorPos(out var cursor);
            var chosen = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, cursor.X, cursor.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);
            if (chosen != 0 && actions.TryGetValue((uint)chosen, out var action))
            {
                action();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_added)
        {
            var data = BuildData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        DestroyWindow(_hwnd);
        _icon?.Dispose();
        _icon = null;
    }

    // ----- interop -----

    private const uint NimAdd = 0x0;
    private const uint NimModify = 0x1;
    private const uint NimDelete = 0x2;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifInfo = 0x10;
    private const uint MfString = 0x0000;
    private const uint MfGrayed = 0x0001;
    private const uint MfSeparator = 0x0800;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
