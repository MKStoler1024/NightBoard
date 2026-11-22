using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NightBoard.Services;

public enum GpuKind { Integrated, Discrete }

/// <summary>Running＝正常，Sleep＝系统把卡关了或挂起，Offline＝已经从系统里消失。</summary>
public enum GpuState { Running, Sleep, Offline }

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

    /// <summary>最近一次枚举时还在系统里。</summary>
    public bool Present { get; set; } = true;

    /// <summary>最近一次读到过计数器（引擎或显存）的时刻。</summary>
    public DateTime LastCounterUtc { get; set; } = DateTime.UtcNow;

    /// <summary>最近一次枚举到它的时刻，用于断连后保留一段时间的卡片。</summary>
    public DateTime LastPresentUtc { get; set; } = DateTime.UtcNow;

    /// <summary>已经不供数（被系统关掉/拔掉），先别显示卡片；读数回来再解除。</summary>
    public bool Suppressed { get; set; }

    /// <summary>读数是否可信：真的空闲还是驱动已经不供数了。</summary>
    public GpuState State
    {
        get
        {
            if (!Present) return GpuState.Offline;

            // 只有独显会整块被系统关掉；核显空转时没有计数器是常态，别误判
            if (Kind == GpuKind.Discrete &&
                (DateTime.UtcNow - LastCounterUtc).TotalSeconds > SleepAfterSeconds)
                return GpuState.Sleep;

            return GpuState.Running;
        }
    }

    /// <summary>
    /// 多久没有计数器就认定显卡被关掉/挂起了。Windows 关掉一块卡之后，
    /// GPU Engine 和 GPU Adapter Memory 的实例会一起消失，所以两个都读不到才算数；
    /// 正常空闲的卡至少还有显存实例在。
    /// </summary>
    public const double SleepAfterSeconds = 45;

    /// <summary>卡片标题用短名字：去掉厂商前缀和 Graphics 之类的废话。</summary>
    public string Label => ShortName(Name);

    public static string ShortName(string name)
    {
        string n = name;
        foreach (var junk in new[] { "(R)", "(TM)", "(r)", "(tm)" })
            n = n.Replace(junk, "");

        foreach (var vendor in new[] { "NVIDIA", "AMD", "Intel(R)", "Intel", "ATI" })
            if (n.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            {
                n = n[vendor.Length..].Trim();
                break;
            }

        foreach (var word in new[] { "GeForce", "Radeon", "Graphics" })
            n = n.Replace(word, "").Trim();

        if (n.EndsWith(" GPU", StringComparison.OrdinalIgnoreCase)) n = n[..^4].Trim();

        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s{2,}", " ").Trim();
        return n.Length == 0 ? name : n;
    }
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
    private const double RescanSeconds = 5;

    /// <summary>卡从枚举里消失后最多再留这么久，之后连卡片一起撤掉。</summary>
    private const double KeepMissingSeconds = 20;

    /// <summary>独显连续多久读不到计数器就认定它被关了，把卡片撤掉。</summary>
    private const double DropAfterSeconds = 60;

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
        // Intel 的 Arc 独显没有显存数据时别被当成核显
        if (vendor == 0x8086)
        {
            if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Graphics", StringComparison.OrdinalIgnoreCase))
                return GpuKind.Discrete;
            return dedMb >= 2048 ? GpuKind.Discrete : GpuKind.Integrated;
        }

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

        // 卡片按「厂商-设备」建，不按 LUID：开关一次显卡后还是同一张卡
        foreach (var d in descs)
        {
            double dedMb = d.DedicatedVideoMemory.ToUInt64() / 1048576.0;
            double sharedMb = d.SharedSystemMemory.ToUInt64() / 1048576.0;

            string baseKey = $"{d.VendorId:X4}-{d.DeviceId:X4}";
            string key = baseKey;
            int dup = 0;
            while (merged.TryGetValue(key, out var exist) &&
                   (exist.Vendor != d.VendorId || exist.Device != d.DeviceId))
                key = $"{baseKey}#{++dup}";

            if (!merged.TryGetValue(key, out var card))
            {
                card = new GpuAdapter
                {
                    Key = key,
                    Name = d.Description,
                    Kind = Classify(d.VendorId, d.Description, dedMb, sharedMb),
                    Vendor = d.VendorId,
                    Device = d.DeviceId,
                    VramTotalMb = dedMb,
                };
                merged[key] = card;
            }

            card.Luids.Add($"{d.AdapterLuid.HighPart:X8}:{d.AdapterLuid.LowPart:X8}");
            card.Present = true;
            card.LastPresentUtc = DateTime.UtcNow;
        }

        // 枚举里没有的卡（拔掉/关掉）短暂留一下，避免一次枚举失败就让布局抖
        foreach (var (key, prev) in old)
        {
            if (merged.ContainsKey(key)) continue;
            if ((DateTime.UtcNow - prev.LastPresentUtc).TotalSeconds < KeepMissingSeconds)
            {
                prev.Present = false;
                merged[key] = prev;
            }
        }

        foreach (var a in merged.Values)
        {
            if (old.TryGetValue(a.Key, out var prev))
            {
                a.Util = prev.Util;
                a.VramUsedMb = prev.VramUsedMb;
                a.LastCounterUtc = prev.LastCounterUtc;
                a.LastPresentUtc = prev.LastPresentUtc;
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            seen.Add(adapter.Key);
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
            seen.Add(adapter.Key);
        }

        var sim = Environment.GetEnvironmentVariable("NB_GPUSLEEP");
        bool simSleep = !string.IsNullOrEmpty(sim);

        foreach (var a in Adapters)
        {
            a.VramUsedMb = vram.GetValueOrDefault(a.Key) / 1048576.0;

            // 调试用：假装独显被系统关掉，验证卡片会消失
            bool simOff = simSleep && a.Kind == GpuKind.Discrete;

            if (!simOff && seen.Contains(a.Key))
            {
                a.LastCounterUtc = DateTime.UtcNow;
                a.Suppressed = false;          // 读数回来了，卡片重新出现
            }

            // 独显连续读不到计数器：Windows 关掉显卡时计数器比设备先消失。
            // 到点就把卡片撤掉，和开机时就没这块卡的表现一致。
            if (a.Kind == GpuKind.Discrete &&
                (DateTime.UtcNow - a.LastCounterUtc).TotalSeconds > DropAfterSeconds)
                a.Suppressed = true;

            if (!a.Present) a.Suppressed = true;
        }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _vram.Dispose();
    }
}
