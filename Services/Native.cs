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

    // ------------------------------------------------------------- PnP 显示设备
    /// <summary>设备管理器里的一个显示设备（含已禁用/出问题的）。</summary>
    public sealed record PnpDisplay(string Name, uint VendorId, uint DeviceId, int Problem, string Hwid);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data,
        uint property, out uint regType, byte[]? buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    private const uint DIGCF_PRESENT = 0x02;
    private const uint SPDRP_DEVICEDESC = 0x00;
    private const uint SPDRP_HARDWAREID = 0x01;
    private static readonly Guid GUID_DEVCLASS_DISPLAY = new("4d36e968-e325-11ce-bfc1-08002be10318");

    private static string ReadProp(IntPtr set, ref SP_DEVINFO_DATA data, uint prop)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, prop, out _, null, 0, out uint need);
        if (need == 0) return "";
        var buf = new byte[need];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, prop, out _, buf, need, out _)) return "";
        return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    /// <summary>
    /// 枚举物理显示设备。和 DXGI 不同，这里连"被禁用/掉驱动"的卡都能看到，
    /// 所以开机时独显就是关着的，也能给它留一张写着「已关闭」的卡片。
    /// </summary>
    public static List<PnpDisplay> EnumerateDisplayDevices()
    {
        var list = new List<PnpDisplay>();
        if (!OperatingSystem.IsWindows()) return list;

        Guid guid = GUID_DEVCLASS_DISPLAY;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                string desc = ReadProp(set, ref data, SPDRP_DEVICEDESC);
                string hwid = ReadProp(set, ref data, SPDRP_HARDWAREID).Split('\0')[0];
                if (string.IsNullOrWhiteSpace(hwid)) continue;

                // 只要 PCI 上的真卡；虚拟显示器、基本显示适配器都不算
                if (!hwid.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
                uint vendor = ParseHex(hwid, "VEN_");
                uint device = ParseHex(hwid, "DEV_");
                if (vendor == 0 || vendor == VENDOR_MICROSOFT) continue;
                if (desc.Contains("Basic", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Indirect", StringComparison.OrdinalIgnoreCase)) continue;

                int problem = 0;
                if (CM_Get_DevNode_Status(out _, out uint p, data.DevInst, 0) == 0) problem = (int)p;

                list.Add(new PnpDisplay(desc, vendor, device, problem, hwid));
            }
        }
        catch
        {
            // 枚举失败就当没有 PnP 信息，退回纯 DXGI
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return list;
    }

    private static uint ParseHex(string text, string marker)
    {
        int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + marker.Length + 4 > text.Length) return 0;
        return uint.TryParse(text.Substring(at + marker.Length, 4),
            System.Globalization.NumberStyles.HexNumber, null, out uint v) ? v : 0;
    }

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
