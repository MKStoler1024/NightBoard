using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace NightBoard.Services;

public sealed record CalEvent(string Title, DateTime Start, DateTime End, bool AllDay);

/// <summary>
/// 日程：读「文档」文件夹里的 .ics（不看 ClassIsland）。
/// 只取 VEVENT 里最基本的几项：DTSTART / DTEND / SUMMARY / RRULE。
/// RRULE 支持 DAILY / WEEKLY / MONTHLY / YEARLY 加 INTERVAL、COUNT、UNTIL，
/// 够覆盖日历导出的常见情况；EXDATE、RECURRENCE-ID 这些先不管。
/// </summary>
public static class IcsCalendar
{
    private const int HorizonDays = 31;

    private static readonly List<CalEvent> Items = new();
    private static readonly Dictionary<string, DateTime> Stamps = new();
    private static DateTime _scannedAt = DateTime.MinValue;

    public static IReadOnlyList<CalEvent> Upcoming => Items;

    /// <summary>文档目录（含 OneDrive 的 Documents）。</summary>
    private static IEnumerable<string> Folders()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(docs)) yield return docs;

        string oneDrive = Environment.GetEnvironmentVariable("OneDrive") ?? "";
        if (!string.IsNullOrEmpty(oneDrive))
        {
            string p = Path.Combine(oneDrive, "Documents");
            if (Directory.Exists(p)) yield return p;
        }

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string alt = Path.Combine(user, "OneDrive", "文档");
        if (Directory.Exists(alt)) yield return alt;
    }

    /// <summary>扫一遍；文件没动、又不到一分钟，就什么都不做。</summary>
    public static void Refresh(bool force = false)
    {
        if (!force && (DateTime.Now - _scannedAt).TotalSeconds < 60) return;
        _scannedAt = DateTime.Now;

        var files = new List<string>();
        foreach (var dir in Folders())
        {
            try
            {
                files.AddRange(Directory.GetFiles(dir, "*.ics", SearchOption.TopDirectoryOnly));
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    try { files.AddRange(Directory.GetFiles(sub, "*.ics", SearchOption.TopDirectoryOnly)); }
                    catch { }
                }
            }
            catch { }
        }

        bool changed = force || files.Count != Stamps.Count;
        foreach (var f in files)
        {
            DateTime t;
            try { t = File.GetLastWriteTimeUtc(f); }
            catch { continue; }

            if (!Stamps.TryGetValue(f, out var old) || old != t)
            {
                Stamps[f] = t;
                changed = true;
            }
        }

        if (!changed) return;

        var list = new List<CalEvent>();
        foreach (var f in files)
        {
            try { list.AddRange(ParseFile(f)); }
            catch { }
        }

        Items.Clear();
        Items.AddRange(list.OrderBy(e => e.Start));
    }

    /// <summary>正在进行的优先；否则最近的一件。</summary>
    public static CalEvent? NextEvent(DateTime now)
    {
        CalEvent? ongoing = null;
        CalEvent? next = null;

        foreach (var e in Items)
        {
            bool running = e.AllDay
                ? e.Start.Date <= now.Date && now.Date <= e.End.Date
                : e.Start <= now && now < e.End;

            if (running)
            {
                if (ongoing is null || e.Start < ongoing.Start) ongoing = e;
            }
            else if (e.Start > now)
            {
                if (next is null || e.Start < next.Start) next = e;
            }
        }

        return ongoing ?? next;
    }

    private static IEnumerable<CalEvent> ParseFile(string path)
    {
        string[] raw;
        try { raw = File.ReadAllLines(path, Encoding.UTF8); }
        catch { yield break; }

        // 展开折行
        var lines = new List<string>();
        foreach (var line in raw)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && lines.Count > 0)
                lines[^1] += line.Substring(1);
            else
                lines.Add(line);
        }

        var now = DateTime.Now;
        var horizon = now.Date.AddDays(HorizonDays);

        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) continue;

            string summary = "";
            string dtStart = "", dtEnd = "", rrule = "";

            for (int j = i + 1; j < lines.Count; j++)
            {
                string l = lines[j];
                if (l.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { i = j; break; }

                if (l.StartsWith("SUMMARY", StringComparison.OrdinalIgnoreCase)) summary = AfterColon(l);
                else if (l.StartsWith("DTSTART", StringComparison.OrdinalIgnoreCase)) dtStart = AfterColon(l);
                else if (l.StartsWith("DTEND", StringComparison.OrdinalIgnoreCase)) dtEnd = AfterColon(l);
                else if (l.StartsWith("RRULE", StringComparison.OrdinalIgnoreCase)) rrule = AfterColon(l);
            }

            if (!TryTime(dtStart, out var start, out bool allDay)) continue;
            DateTime end = TryTime(dtEnd, out var e2, out _) ? e2 : (allDay ? start.AddDays(1) : start.AddHours(1));
            TimeSpan span = end > start ? end - start : TimeSpan.FromHours(1);

            foreach (var s in Expand(start, rrule, horizon))
            {
                if (s.Add(span) < now.AddDays(-1)) continue;
                yield return new CalEvent(Unescape(summary), s, s.Add(span), allDay);
            }
        }
    }

    private static string AfterColon(string line)
    {
        int idx = line.IndexOf(':');
        return idx < 0 ? "" : line.Substring(idx + 1).Trim();
    }

    private static bool TryTime(string value, out DateTime when, out bool allDay)
    {
        when = default;
        allDay = false;
        if (string.IsNullOrWhiteSpace(value)) return false;

        value = value.Trim();

        if (value.Length == 8)
        {
            if (!DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out when)) return false;
            allDay = true;
            return true;
        }

        bool utc = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        string v = utc ? value[..^1] : value;

        foreach (var fmt in new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" })
        {
            if (!DateTime.TryParseExact(v, fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out when)) continue;

            if (utc) when = when.ToLocalTime();
            return true;
        }

        return false;
    }

    /// <summary>把一条（可能重复的）事件展开成未来一段时间里的实例。</summary>
    private static IEnumerable<DateTime> Expand(DateTime start, string rrule, DateTime horizon)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            yield return start;
            yield break;
        }

        string freq = "";
        int interval = 1;
        int count = 0;
        DateTime until = DateTime.MaxValue;

        foreach (var part in rrule.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            string k = part[..eq].ToUpperInvariant();
            string v = part[(eq + 1)..];

            switch (k)
            {
                case "FREQ": freq = v.ToUpperInvariant(); break;
                case "INTERVAL": int.TryParse(v, out interval); if (interval < 1) interval = 1; break;
                case "COUNT": int.TryParse(v, out count); break;
                case "UNTIL":
                    if (TryTime(v, out var u, out _)) until = u;
                    break;
            }
        }

        var cur = start;
        int made = 0;

        for (int guard = 0; guard < 400; guard++)
        {
            if (cur > horizon) yield break;
            if (cur > until) yield break;
            if (count > 0 && made >= count) yield break;

            yield return cur;
            made++;

            cur = freq switch
            {
                "DAILY" => cur.AddDays(interval),
                "WEEKLY" => cur.AddDays(7 * interval),
                "MONTHLY" => cur.AddMonths(interval),
                "YEARLY" => cur.AddYears(interval),
                _ => cur.AddYears(200),       // 不认识的规则只留首个实例
            };
        }
    }

    private static string Unescape(string s) => s
        .Replace("\\n", " ").Replace("\\N", " ")
        .Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\")
        .Trim();
}
