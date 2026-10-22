using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace QuotaScope.WinUI;

// Tells when the system's hardware graphics adapters changed (a driver update,
// an adapter disabled and re-enabled) since this process started.
//
// Why: WinUI 3 bug microsoft-ui-xaml#10844. After an NVIDIA driver update the
// running popup came up nearly black (a few divider lines, no text), and a
// settings window opened afterwards in the same process drew only in part and
// flickered between pages. Redrawing or new windows do not recover; a new
// process always draws correctly. A graphics driver reset (Win+Ctrl+Shift+B)
// does not reproduce it. So the tray restarts itself when this says so
// (TrayController.CheckGraphicsAdapters).
//
// How: IDXGIFactory1::IsCurrent() is a cheap first check, but it also turns
// false when only the outputs change (a monitor sleeping or waking) or when a
// virtual adapter comes and goes (an isolation session). Taken alone it
// restarted the app all night, up to every 30 s. So a stale factory only
// counts as a change when the set of hardware adapters, by LUID, differs from
// the one seen at start: a driver update restarts the adapter under a new LUID.
// Anything else just takes a fresh factory as the new baseline.
// P/Invoke only, no package: CreateDXGIFactory1 and the COM vtables.
internal sealed class GraphicsAdapters : IDisposable
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    // IUnknown (3) + IDXGIObject (4) + IDXGIFactory (5): EnumAdapters1 = 12, IsCurrent = 13.
    private const int EnumAdapters1Slot = 12;
    private const int IsCurrentSlot = 13;
    // IUnknown (3) + IDXGIObject (4) + IDXGIAdapter (3): GetDesc1 = 10.
    private const int GetDesc1Slot = 10;
    private const int ReleaseSlot = 2;
    private const int NotFound = unchecked((int)0x887A0002); // DXGI_ERROR_NOT_FOUND
    // DXGI_ADAPTER_DESC1 on x64: Description[128] (256 bytes), four UINT ids,
    // three SIZE_T sizes, then the LUID at 296 and Flags at 304.
    private const int DescSize = 320;
    private const int VendorIdOffset = 256;
    private const int LuidOffset = 296;
    private const int FlagsOffset = 304;
    private const uint SoftwareFlag = 2; // DXGI_ADAPTER_FLAG_SOFTWARE
    // Microsoft's own adapters: Basic Render/Display and Hyper-V synthetic ones.
    private const uint MicrosoftVendorId = 0x1414;

    // Test-only: report a change, to exercise the restart without a driver
    // update. The instance the restart starts ignores it, so it cannot loop.
    public const string SimulateVariable = "QUOTASCOPE_SIMULATE_ADAPTER_CHANGE";

    private IntPtr _factory;
    private HashSet<long> _hardware;
    private readonly bool _simulate;

    private GraphicsAdapters(IntPtr factory, HashSet<long> hardware, bool simulate)
    {
        _factory = factory;
        _hardware = hardware;
        _simulate = simulate;
    }

    // Null when DXGI is not there to ask (then nothing is watched).
    public static GraphicsAdapters? TryCreate(bool allowSimulation)
    {
        var simulate = allowSimulation && Environment.GetEnvironmentVariable(SimulateVariable) == "1";
        try
        {
            var factory = CreateFactory();
            if (factory != IntPtr.Zero) return new GraphicsAdapters(factory, HardwareAdapters(factory), simulate);
            return simulate ? new GraphicsAdapters(IntPtr.Zero, new HashSet<long>(), true) : null;
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
        if (Slot<IsCurrentFn>(_factory, IsCurrentSlot)(_factory) != 0) return false;

        // Stale: find out whether the hardware changed, from a fresh factory.
        var fresh = CreateFactory();
        if (fresh == IntPtr.Zero) return false;
        var now = HardwareAdapters(fresh);
        if (now.SetEquals(_hardware))
        {
            // Outputs or virtual adapters only: watch from here on.
            Release(_factory);
            _factory = fresh;
            return false;
        }
        Release(fresh);
        return true;
    }

    public void Dispose()
    {
        if (_factory == IntPtr.Zero) return;
        Release(_factory);
        _factory = IntPtr.Zero;
    }

    private static IntPtr CreateFactory()
    {
        var iid = IidFactory1;
        return CreateDXGIFactory1(ref iid, out var factory) >= 0 ? factory : IntPtr.Zero;
    }

    // LUIDs of the adapters a WinUI window can be drawn by: not software, not
    // Microsoft's basic or virtual ones.
    private static HashSet<long> HardwareAdapters(IntPtr factory)
    {
        var luids = new HashSet<long>();
        var desc = Marshal.AllocHGlobal(DescSize);
        try
        {
            for (uint index = 0; ; index++)
            {
                var hr = Slot<EnumAdapters1Fn>(factory, EnumAdapters1Slot)(factory, index, out var adapter);
                if (hr == NotFound || hr < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    if (Slot<GetDesc1Fn>(adapter, GetDesc1Slot)(adapter, desc) < 0) continue;
                    var vendor = (uint)Marshal.ReadInt32(desc, VendorIdOffset);
                    var flags = (uint)Marshal.ReadInt32(desc, FlagsOffset);
                    if ((flags & SoftwareFlag) != 0 || vendor == MicrosoftVendorId) continue;
                    luids.Add(Marshal.ReadInt64(desc, LuidOffset));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(desc);
        }
        return luids;
    }

    private static void Release(IntPtr comObject) => Slot<ReleaseFn>(comObject, ReleaseSlot)(comObject);

    private static T Slot<T>(IntPtr comObject, int index) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, index * IntPtr.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsCurrentFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // Headless: the factory can be made, its adapters enumerated twice to the
    // same hardware set, and with nothing changed it says so.
    public static bool RunSelfTest()
    {
        using var adapters = TryCreate(allowSimulation: false);
        if (adapters is null || adapters.Changed()) return false;
        var again = CreateFactory();
        if (again == IntPtr.Zero) return false;
        try
        {
            return HardwareAdapters(again).SetEquals(adapters._hardware);
        }
        finally
        {
            Release(again);
        }
    }
}
