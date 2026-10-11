using System;
using System.Runtime.InteropServices;

namespace QuotaScope.WinUI;

// Tells when the system's graphics adapters changed (a driver update, an
// adapter disabled and re-enabled) since this process started.
//
// Why: WinUI 3 bug microsoft-ui-xaml#10844. After an NVIDIA driver update the
// running popup came up nearly black (a few divider lines, no text), and a
// settings window opened afterwards in the same process drew only in part and
// flickered between pages. Redrawing or new windows do not recover; a new
// process always draws correctly. A graphics driver reset (Win+Ctrl+Shift+B)
// does not reproduce it. So the tray restarts itself when this says so
// (TrayController.CheckGraphicsAdapters).
//
// How: a DXGI factory created at start answers IsCurrent() == false once the
// adapter set it enumerated is no longer the system's. P/Invoke only, no
// package: CreateDXGIFactory1 and the factory's vtable.
internal sealed class GraphicsAdapters : IDisposable
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    // IUnknown (3) + IDXGIObject (4) + IDXGIFactory (5) + EnumAdapters1 = 13.
    private const int IsCurrentSlot = 13;
    private const int ReleaseSlot = 2;

    // Test-only: report a change, to exercise the restart without a driver
    // update. The instance the restart starts ignores it, so it cannot loop.
    public const string SimulateVariable = "QUOTASCOPE_SIMULATE_ADAPTER_CHANGE";

    private IntPtr _factory;
    private readonly bool _simulate;

    private GraphicsAdapters(IntPtr factory, bool simulate)
    {
        _factory = factory;
        _simulate = simulate;
    }

    // Null when DXGI is not there to ask (then nothing is watched).
    public static GraphicsAdapters? TryCreate(bool allowSimulation)
    {
        var simulate = allowSimulation && Environment.GetEnvironmentVariable(SimulateVariable) == "1";
        try
        {
            var iid = IidFactory1;
            return CreateDXGIFactory1(ref iid, out var factory) >= 0 && factory != IntPtr.Zero
                ? new GraphicsAdapters(factory, simulate)
                : simulate ? new GraphicsAdapters(IntPtr.Zero, true) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool Changed()
    {
        if (_simulate) return true;
        if (_factory == IntPtr.Zero) return false;
        return Slot<IsCurrentFn>(_factory, IsCurrentSlot)(_factory) == 0;
    }

    public void Dispose()
    {
        if (_factory == IntPtr.Zero) return;
        Slot<ReleaseFn>(_factory, ReleaseSlot)(_factory);
        _factory = IntPtr.Zero;
    }

    private static T Slot<T>(IntPtr comObject, int index) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, index * IntPtr.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsCurrentFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // Headless: the factory can be made and, with nothing changed, says so.
    public static bool RunSelfTest()
    {
        using var adapters = TryCreate(allowSimulation: false);
        return adapters is not null && !adapters.Changed();
    }
}
