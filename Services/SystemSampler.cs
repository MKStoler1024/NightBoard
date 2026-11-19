using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;

namespace NightBoard.Services;

public sealed record GpuSample(
    string Key,
    string Name,
    string Kind,
    string Label,
    double Util,
    double VramUsedMb,
    double VramTotalMb,
    IReadOnlyList<double> Hist,
    string State);

public sealed class Snapshot
{
    public double Cpu { get; init; }
    public IReadOnlyList<double> CpuHist { get; init; } = Array.Empty<double>();
    public double MemPct { get; init; }
    public IReadOnlyList<double> MemHist { get; init; } = Array.Empty<double>();
    public double MemUsedGb { get; init; }
    public double MemTotalGb { get; init; }

    public int? BatteryPct { get; init; }
    public bool? BatteryPlugged { get; init; }
    public int? BatterySecs { get; init; }

    public bool AbOnline { get; init; }
    public double AbMemMb { get; init; }
    public double AbCpu { get; init; }
    public string AbUptime { get; init; } = "";
    public string SysUptime { get; init; } = "";

    public IReadOnlyList<GpuSample> Gpus { get; init; } = Array.Empty<GpuSample>();
    public string GpuNote { get; init; } = "";
    public string Host { get; init; } = "";
    public string Quote { get; init; } = "";
    public string QuoteFrom { get; init; } = "";
}

/// <summary>
/// 后台采样。一个线程，1 秒一轮，与任务管理器的刷新节奏一致。
/// 电池和 AstrBot 每 30 秒才查一次——这两样变化慢，没必要每秒摸一遍。
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private const double TickSeconds = 1.0;
    private const int HistLen = 360;          // 360 × 1s = 6 分钟
    private const int SlowEvery = 30;

    private static readonly int[] AstrBotPorts = { 6185, 6200, 8766, 8796, 8797, 8798, 8799 };

    private readonly GpuMonitor _gpu = new();
    private readonly string _gpuFilter;

    // 任务管理器显示的 CPU 利用率是 Processor Utility（含睿频收益），
    // 不是单纯的时间占比，所以这里也用 Utility，数字才对得上。
    private readonly Native.PdhCounter _cpuUtil =
        new(@"\Processor Information(_Total)\% Processor Utility");

    private readonly List<double> _cpuHist = new(HistLen);
    private readonly List<double> _memHist = new(HistLen);
    private readonly Dictionary<string, List<double>> _gpuHist = new(StringComparer.OrdinalIgnoreCase);

    private Thread? _thread;
    private volatile bool _stop;
    private ulong _prevIdle, _prevTotal;             // Utility 不可用时的兜底

    private int? _batPct;
    private bool? _batPlugged;
    private int? _batSecs;

    private bool _abOnline;
    private double _abMemMb, _abCpu;
    private string _abUptime = "";
    private List<Process> _abProcs = new();

    private double _prevAbCpuMs;
    private DateTime _prevAbStamp = DateTime.MinValue;

    public event Action<Snapshot>? Updated;

    public string Host { get; } = Environment.MachineName;

    /// <summary>诊断用：当前这次枚举到的适配器。</summary>
    public IReadOnlyList<GpuAdapter> GpuAdapters => _gpu.Adapters;

    public SystemSampler(string gpuFilter = "all")
    {
        _gpuFilter = gpuFilter;
        Native.CpuUsage(ref _prevIdle, ref _prevTotal);
        Hitokoto.Warmup();
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "nightboard-sampler",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private void Loop()
    {
        int tick = 0;
        while (!_stop)
        {
            var sw = Stopwatch.StartNew();
            try { Emit(tick % SlowEvery == 0); }
            catch { /* 单次失败不能把整块板子拖死 */ }

            tick++;
            double rest = TickSeconds - sw.Elapsed.TotalSeconds;
            if (rest > 0) Thread.Sleep(TimeSpan.FromMilliseconds(rest * 1000));
        }
    }

    private double ReadCpu()
    {
        if (_cpuUtil.Ready)
        {
            var arr = _cpuUtil.Read();
            if (arr.Count > 0)
                return Math.Clamp(arr[0].value, 0, 100);   // 睿频时可能虚标过百
        }
        return Native.CpuUsage(ref _prevIdle, ref _prevTotal);
    }

    private void Emit(bool slow)
    {
        double cpu = ReadCpu();
        var (memPct, memUsed, memTotal) = Native.Memory();

        _gpu.Sample();
        foreach (var a in _gpu.Adapters)
        {
            if (!_gpuHist.TryGetValue(a.Key, out var list))
                _gpuHist[a.Key] = list = new List<double>(HistLen);

            // 关掉/休眠的卡不往曲线里塞 0：留空，曲线在这里断开，
            // 不然看着就像"贴着底的 0%"。
            Push(list, a.State == GpuState.Running ? a.Util : double.NaN);
        }

        foreach (var key in _gpuHist.Keys.ToList())
        {
            if (_gpu.Adapters.All(a => !string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)))
                _gpuHist.Remove(key);
        }

        Push(_cpuHist, Math.Round(cpu, 1));
        Push(_memHist, Math.Round(memPct, 1));

        if (slow)
        {
            (_batPct, _batPlugged, _batSecs) = Native.Battery();
            ScanAstrBot();
            _gpu.Scan();
        }

        var gpus = _gpu.Adapters.Select(a => new GpuSample(
            a.Key,
            a.Name,
            a.Kind == GpuKind.Integrated ? "igpu" : "dgpu",
            a.Label,
            a.Util,
            Math.Round(a.VramUsedMb),
            Math.Round(a.VramTotalMb),
            _gpuHist.TryGetValue(a.Key, out var h) ? h.ToArray() : Array.Empty<double>(),
            a.State switch
            {
                GpuState.Sleep => "sleep",
                GpuState.Offline => "off",
                _ => "run",
            }
        )).ToList();

        if (_gpuFilter != "all")
        {
            var picked = gpus.Where(g => g.Kind == _gpuFilter).ToList();
            if (picked.Count > 0) gpus = picked;
        }

        var (quote, from) = Hitokoto.Current;

        var snap = new Snapshot
        {
            Cpu = Math.Round(cpu, 1),
            CpuHist = _cpuHist.ToArray(),
            MemPct = Math.Round(memPct, 1),
            MemHist = _memHist.ToArray(),
            MemUsedGb = memUsed,
            MemTotalGb = memTotal,
            BatteryPct = _batPct,
            BatteryPlugged = _batPlugged,
            BatterySecs = _batSecs,
            AbOnline = _abOnline,
            AbMemMb = _abMemMb,
            AbCpu = _abCpu,
            AbUptime = _abUptime,
            SysUptime = FormatUptime(Environment.TickCount64 / 1000.0),
            Gpus = gpus,
            GpuNote = _gpu.Note,
            Host = Host,
            Quote = quote,
            QuoteFrom = from,
        };

        Updated?.Invoke(snap);
    }

    private static void Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistLen) list.RemoveRange(0, list.Count - HistLen);
    }

    // ---------------------------------------------------------- AstrBot
    private void ScanAstrBot()
    {
        var found = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) { p.Dispose(); continue; }

                string name = p.ProcessName;
                bool match = name.Contains("astrbot", StringComparison.OrdinalIgnoreCase);

                if (!match && (name.Equals("python", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals("pythonw", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        string? path = p.MainModule?.FileName;
                        match = path != null && path.Contains("astrbot", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { }
                }

                if (!match) { p.Dispose(); continue; }
                found.Add(p);
            }
            catch { p.Dispose(); }
        }

        foreach (var p in found)
        {
            try { p.Refresh(); _ = p.TotalProcessorTime; } catch { }
        }

        foreach (var old in _abProcs)
            if (!found.Any(f => f.Id == old.Id)) old.Dispose();

        double mem = 0, uptime = 0;
        foreach (var p in found)
        {
            try
            {
                mem += p.WorkingSet64 / 1048576.0;
                uptime = Math.Max(uptime, (DateTime.Now - p.StartTime).TotalSeconds);
            }
            catch { }
        }

        int? port = null;
        foreach (int cand in AstrBotPorts)
        {
            try
            {
                using var c = new TcpClient();
                var task = c.ConnectAsync("127.0.0.1", cand);
                if (task.Wait(150) && c.Connected) { port = cand; break; }
            }
            catch { }
        }

        _abProcs = found;
        _abMemMb = Math.Round(mem, 0);
        _abUptime = uptime > 0 ? FormatUptime(uptime) : "";
        _abOnline = found.Count > 0 || port != null;

        try
        {
            double cpuMs = 0;
            foreach (var p in found)
            {
                try { cpuMs += p.TotalProcessorTime.TotalMilliseconds; } catch { }
            }

            if (_prevAbCpuMs > 0 && _prevAbStamp != DateTime.MinValue)
            {
                double wallMs = (DateTime.Now - _prevAbStamp).TotalMilliseconds;
                int cores = Environment.ProcessorCount;
                if (wallMs > 0)
                    _abCpu = Math.Round(Math.Clamp((cpuMs - _prevAbCpuMs) / (wallMs * cores) * 100, 0, 100), 1);
            }
            _prevAbCpuMs = cpuMs;
            _prevAbStamp = DateTime.Now;
        }
        catch { }
    }

    private static string FormatUptime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}天{t.Hours}小时";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}小时{t.Minutes}分";
        return $"{t.Minutes}分";
    }

    public void Dispose()
    {
        _stop = true;
        _gpu.Dispose();
        _cpuUtil.Dispose();
        foreach (var p in _abProcs) p.Dispose();
    }
}
