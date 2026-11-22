using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NightBoard.Services;

/// <summary>Win32 / PDH / DXGI 的薄封装。全部同步调用，单次开销在微秒级。</summary>
internal static unsafe class Native
{
    // ---------------------------------------------------------------- CPU
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    /// <summary>返回自上次调用以来的整体 CPU 占用（%）。</summary>
    public static double CpuUsage(ref ulong prevIdle, ref ulong prevTotal)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        ulong i = idle.Value;
        ulong t = kernel.Value + user.Value;   // kernel 已包含 idle
        ulong di = i - prevIdle;
        ulong dt = t - prevTotal;
        prevIdle = i;
        prevTotal = t;

        if (dt == 0) return 0;
        double usage = (1.0 - (double)di / dt) * 100.0;
        return Math.Clamp(usage, 0, 100);
    }

    // ------------------------------------------------------------- 内存
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static (double pct, double usedGb, double totalGb) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        double total = m.ullTotalPhys / 1073741824.0;
        double used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return (m.dwMemoryLoad, Math.Round(used, 1), Math.Round(total, 1));
    }

    // ------------------------------------------------------------- 电池
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    public static (int? pct, bool? plugged, int? secsLeft) Battery()
    {
        if (!GetSystemPowerStatus(out var s)) return (null, null, null);
        if (s.BatteryFlag == 128) return (null, null, null);        // 无电池

        int? pct = s.BatteryLifePercent == 255 ? null : s.BatteryLifePercent;
        bool plugged = s.ACLineStatus == 1;
        int? left = (!plugged && s.BatteryLifeTime != uint.MaxValue && s.BatteryLifeTime > 0)
            ? (int)s.BatteryLifeTime
            : null;
        return (pct, plugged, left);
    }

    // -------------------------------------------------------------- PDH
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint ERROR_SUCCESS = 0;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufSize, ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>一个 PDH 计数器数组句柄。</summary>
    public sealed class PdhCounter : IDisposable
    {
        private IntPtr _query, _counter;
        public bool Ready { get; }
        public string Error { get; } = "";

        public PdhCounter(string path)
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != ERROR_SUCCESS)
            {
                Error = "PdhOpenQuery 失败";
                return;
            }

            uint rc = PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out _counter);
            if (rc != ERROR_SUCCESS)
            {
                // 少数精简系统上没有英文计数器别名，退回本地化路径
                rc = PdhAddCounterW(_query, path, IntPtr.Zero, out _counter);
            }

            if (rc != ERROR_SUCCESS)
            {
                Error = $"{path} 不可用 (0x{rc:X8})";
                return;
            }

            PdhCollectQueryData(_query);          // 建立基准
            Ready = true;
        }

        public List<(string instance, double value)> Read()
        {
            var list = new List<(string, double)>(64);
            if (!Ready || PdhCollectQueryData(_query) != ERROR_SUCCESS) return list;

            uint size = 0, count = 0;
            uint rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, ref count, IntPtr.Zero);
            if (rc == ERROR_SUCCESS && count == 0) return list;
            if (rc != PDH_MORE_DATA) return list;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, ref count, buf);
                if (rc != ERROR_SUCCESS) return list;

                // PDH_FMT_COUNTERVALUE_ITEM_W { LPWSTR szName; { DWORD CStatus; double; } } —— x64 下 24 字节
                int itemSize = IntPtr.Size + 16;
                for (int i = 0; i < count; i++)
                {
                    byte* p = (byte*)buf + (nint)i * itemSize;
                    IntPtr namePtr = *(IntPtr*)p;
                    double v = *(double*)(p + IntPtr.Size + 8);
                    if (namePtr != IntPtr.Zero)
                        list.Add((Marshal.PtrToStringUni(namePtr) ?? "", v));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }

            return list;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        }
    }

    // ------------------------------------------------------------- DXGI
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDesc1Fn(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ReleaseFn(IntPtr self);

    private static IntPtr Vtbl(IntPtr instance, int index)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
    }

    private static readonly Guid IID_IDXGIFactory1 =
        new("770aae78-f26f-4dba-a829-253c83d1b387");

    public const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    public const uint VENDOR_MICROSOFT = 0x1414;

    /// <summary>枚举所有非软件适配器。失败返回空列表。</summary>
    public static List<DXGI_ADAPTER_DESC1> EnumerateAdapters()
    {
        var result = new List<DXGI_ADAPTER_DESC1>();
        Guid iid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref iid, out IntPtr factory) != 0 || factory == IntPtr.Zero)
            return result;

        try
        {
            var enumFn = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Fn>(Vtbl(factory, 12));
            var releaseFn = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Vtbl(factory, 2));
            var getDescFn = IntPtr.Zero;
            ReleaseFn? adapterRelease = null;

            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    if (enumFn(factory, i, out IntPtr adapter) != 0 || adapter == IntPtr.Zero) break;

                    if (adapterRelease is null)
                    {
                        getDescFn = Vtbl(adapter, 10);
                        adapterRelease = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Vtbl(adapter, 2));
                    }

                    var descFn = Marshal.GetDelegateForFunctionPointer<GetDesc1Fn>(getDescFn);
                    IntPtr pDesc = Marshal.AllocHGlobal(1024);
                    try
                    {
                        if (descFn(adapter, pDesc) == 0)
                        {
                            var desc = Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>(pDesc);
                            if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 &&
                                desc.VendorId != VENDOR_MICROSOFT)
                            {
                                result.Add(desc);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pDesc);
                        adapterRelease(adapter);
                    }
                }
            }
            finally
            {
                releaseFn(factory);
            }
        }
        catch
        {
            // DXGI 不可用时静默降级，UI 会显示「不可用」
        }

        return result;
    }
}
