using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NightBoard.Services;

public enum GpuKind { Integrated, Discrete }

public sealed class GpuAdapter
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required GpuKind Kind { get; init; }
    public uint Vendor { get; init; }
    public uint Device { get; init; }
    public double VramTotalMb { get; set; }
    public double VramUsedMb { get; set; }
    public double Util { get; set; }
    public HashSet<string> Luids { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Label => Kind == GpuKind.Integrated ? "核显" : "独显";
}

/// <summary>
/// 显卡采集。要点：PDH 的引擎实例是按 LUID 分的，先按 LUID 归到 DXGI 枚举出的
/// 物理卡上，再逐卡聚合——这样核显 + 独显混合的机器不会把两块卡混成一个数。
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    private static readonly Regex InstRegex = new(
        @"luid_(0x[0-9a-fA-F]+)_(0x[0-9a-fA-F]+)(?:_phys_(\d+))?(?:_eng_(\d+))?(?:_engtype_(\w+))?",
        RegexOptions.Compiled);

    private readonly Native.PdhCounter _engine;
    private readonly Native.PdhCounter _vram;
    private DateTime _lastScan = DateTime.MinValue;
    private const double RescanSeconds = 300;

    public List<GpuAdapter> Adapters { get; private set; } = new();
    public string Note { get; private set; } = "";

    public GpuMonitor()
    {
        _engine = new Native.PdhCounter(@"\GPU Engine(*)\Utilization Percentage");
        _vram = new Native.PdhCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
        Scan(force: true);
    }

    private static GpuKind Classify(uint vendor, string name, double dedMb, double sharedMb)
    {
        if (vendor == 0x10DE) return GpuKind.Discrete;
        if (vendor == 0x8086) return dedMb >= 2048 ? GpuKind.Discrete : GpuKind.Integrated;

        if (dedMb <= 512 && sharedMb > dedMb * 3) return GpuKind.Integrated;
        if (dedMb >= 1024) return GpuKind.Discrete;

        string low = name.ToLowerInvariant();
        string[] igpuHints =
        {
            "uhd graphics", "iris", "vega", "radeon(tm) graphics",
            "780m", "760m", "740m", "680m", "660m", "890m", "880m", "860m"
        };
        return igpuHints.Any(low.Contains) ? GpuKind.Integrated : GpuKind.Discrete;
    }

    public void Scan(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastScan).TotalSeconds < RescanSeconds) return;
        _lastScan = DateTime.UtcNow;

        var descs = Native.EnumerateAdapters();
        if (descs.Count == 0)
        {
            if (Adapters.Count == 0)
                Note = _engine.Ready ? "未检测到图形适配器" : _engine.Error;
            return;
        }

        var old = Adapters.ToDictionary(a => a.Key, a => a, StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, GpuAdapter>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in descs)
        {
            double dedMb = d.DedicatedVideoMemory.ToUInt64() / 1048576.0;
            double sharedMb = d.SharedSystemMemory.ToUInt64() / 1048576.0;

            // 同一块物理卡常被驱动枚举成多个适配器（虚拟显示 / 多输出），按指纹归并
            string fingerprint = $"{d.VendorId:X4}-{d.DeviceId:X4}-{d.SubSysId:X4}-{Math.Round(dedMb)}";
            string luid = $"{d.AdapterLuid.HighPart:X8}:{d.AdapterLuid.LowPart:X8}";

            if (!merged.TryGetValue(fingerprint, out var adapter))
            {
                adapter = new GpuAdapter
                {
                    Key = luid,
                    Name = d.Description,
                    Kind = Classify(d.VendorId, d.Description, dedMb, sharedMb),
                    Vendor = d.VendorId,
                    Device = d.DeviceId,
                    VramTotalMb = dedMb,
                };
                merged[fingerprint] = adapter;
            }

            adapter.Luids.Add(luid);
        }

        foreach (var a in merged.Values)
        {
            if (old.TryGetValue(a.Key, out var prev))
            {
                a.Util = prev.Util;
                a.VramUsedMb = prev.VramUsedMb;
            }
        }

        Adapters = merged.Values
            .OrderBy(a => a.Kind == GpuKind.Discrete ? 0 : 1)
            .ThenBy(a => a.Name)
            .ToList();

        Note = _engine.Ready ? "" : _engine.Error;
    }

    private GpuAdapter? Find(uint high, uint low)
    {
        string key = $"{high:X8}:{low:X8}";
        foreach (var a in Adapters)
            if (a.Luids.Contains(key)) return a;

        string swapped = $"{low:X8}:{high:X8}";
        foreach (var a in Adapters)
            if (a.Luids.Contains(swapped)) return a;

        return null;
    }

    public void Sample()
    {
        if (Adapters.Count == 0)
        {
            Scan(force: true);
            if (Adapters.Count == 0) return;
        }

        // (卡, 物理引擎) -> 占用。同一引擎上多个进程是分摊的，要相加
        var engines = new Dictionary<(string adapter, string engine), double>();

        foreach (var (instance, value) in _engine.Read())
        {
            var m = InstRegex.Match(instance);
            if (!m.Success) continue;

            uint high = Convert.ToUInt32(m.Groups[1].Value, 16);
            uint low = Convert.ToUInt32(m.Groups[2].Value, 16);
            string eng = m.Groups[5].Success ? m.Groups[5].Value : "";

            // 不再按引擎类型白名单过滤：任务管理器的 GPU 利用率取的就是
            // 所有引擎里最忙的那一个，过滤掉反而会对不上。
            if (string.IsNullOrEmpty(eng)) continue;

            var adapter = Find(high, low);
            if (adapter is null) continue;

            var k = (adapter.Key, $"{m.Groups[3].Value}-{eng}");
            engines[k] = engines.GetValueOrDefault(k) + Math.Max(0, value);
        }

        // 每张卡取自己最忙的引擎（与任务管理器口径一致）
        var per = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((key, _), v) in engines)
            per[key] = Math.Max(per.GetValueOrDefault(key), v);

        foreach (var a in Adapters)
            a.Util = Math.Round(Math.Clamp(per.GetValueOrDefault(a.Key), 0, 100), 1);

        // 显存占用（主要给独显看）
        var vram = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in _vram.Read())
        {
            var m = InstRegex.Match(instance);
            if (!m.Success) continue;
            var adapter = Find(
                Convert.ToUInt32(m.Groups[1].Value, 16),
                Convert.ToUInt32(m.Groups[2].Value, 16));
            if (adapter is null) continue;
            vram[adapter.Key] = vram.GetValueOrDefault(adapter.Key) + value;
        }

        foreach (var a in Adapters)
            a.VramUsedMb = vram.GetValueOrDefault(a.Key) / 1048576.0;
    }

    public void Dispose()
    {
        _engine.Dispose();
        _vram.Dispose();
    }
}
