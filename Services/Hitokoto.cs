using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NightBoard.Services;

/// <summary>
/// 一言：每分钟换一条。
///
/// 句子池由三部分组成，按这个顺序排：
///   1. quotes.txt（程序目录或当前目录，一行一句，# 开头是注释）——自己写的优先
///   2. hitokoto.cn 拉回来的（每 3 分钟补一条，落盘缓存，断网接着用）
///   3. 内置的一批，兜底用
///
/// 每分钟推进一格，不重复；新拉到的句子插到下一格，一分钟内就会露面。
/// 所有网络请求走后台线程，绝不阻塞采样。
/// </summary>
public static class Hitokoto
{
    private const string Api =
        "https://v1.hitokoto.cn/?encode=json&c=d&c=i&c=k&c=a&c=b&max_length=25";

    /// <summary>一言的最大显示宽度（字符数），超限截断。可按屏幕实际情况调整。</summary>
    public static int MaxLen { get; set; } = 30;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly string CacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightBoard");

    private static readonly string CacheFile = Path.Combine(CacheDir, "hitokoto.json");

    private static readonly string[] Fallback =
    {
        "夜深了。茶还热着，你也还在。",
        "灯不必太亮，看得清路就够了。",
        "写坏的字，撕了重来就是。",
        "客人都走了，桌子还是热的。",
        "一天里最安静的时候，才听得到自己。",
        "留一盏灯，是给晚归的人看的。",
        "墨要淡，话要少，夜要长。",
        "不必事事有回应。有些事，放着就好。",
        "山不来看我，我便去看山。",
        "算不清的账，明天再算。",
        "睡吧。夜里的活儿，明早做也不迟。",
        "心里有事的时候，就去走两步。",
        "旧的不去，新的也不急。",
        "一日三餐，四季衣裳，够了。",
        "风大，先关窗。",
        "慢一点，茶才出味。",
        "天亮了自然会亮，不用等。",
        "把手里的事做完，比想得多强。",
        "记得喝水。别老是靠咖啡撑着。",
        "今晚的月亮，比昨天圆一点。",
        "把窗关小一点，风进来容易带走热气。",
        "路远的人，别急着赶。",
        "把话留在心里，等它自己长成句子。",
        "有些夜，是用来发呆的。",
    };

    private readonly record struct Item(string Text, string From);

    private static readonly object Gate = new();
    private static readonly List<Item> Pool = new();
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

    private static int _cursor = -1;
    private static int _fresh = -1;
    private static long _minute = -1;
    private static Item _shown;
    private static DateTime _nextFetch = DateTime.MinValue;
    private static int _fetching;
    private static bool _loaded;

    public static (string text, string from) Current
    {
        get
        {
            lock (Gate)
            {
                return _shown.Text.Length > 0 ? (_shown.Text, _shown.From) : (LocalFallback(), "");
            }
        }
    }

    /// <summary>每秒调一次也便宜：一分钟才真正推进一格。</summary>
    public static void Tick()
    {
        long minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        if (minute == _minute) return;
        _minute = minute;

        Advance();
        MaybeFetch();
    }

    private static void Advance()
    {
        lock (Gate)
        {
            if (Pool.Count == 0)
            {
                _shown = new Item(LocalFallback(), "");
                return;
            }

            if (_fresh >= 0 && _fresh < Pool.Count)
            {
                _cursor = _fresh;
                _fresh = -1;
            }
            else
            {
                _cursor = _cursor < 0 ? 0 : (_cursor + 1) % Pool.Count;
            }

            _shown = Pool[_cursor];
        }
    }

    /// <summary>超长时截到 MaxLen，尾部加省略号，避免把左下角撑得过宽。</summary>
    private static string Trim(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxLen) return text;
        return text.Substring(0, MaxLen - 1).TrimEnd() + "…";
    }

    private static string LocalFallback()
    {
        var now = DateTime.Now;
        int seed = now.Year * 10000 + now.Month * 100 + now.Day + now.Minute;
        return Fallback[new Random(seed).Next(Fallback.Length)];
    }

    // ------------------------------------------------------------ 启动
    public static void Warmup()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
        }

        LoadLocalFile();
        LoadCache();

        lock (Gate)
        {
            // 池子最后一定不能是空的
            if (Pool.Count == 0)
                foreach (var line in Fallback)
                    AddLocked(line, "");
        }

        Advance();
        Task.Run(FetchAsync);
    }

    private static void LoadLocalFile()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            try
            {
                string path = Path.Combine(dir, "quotes.txt");
                if (!File.Exists(path)) continue;

                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    if (line.Length > MaxLen) line = Trim(line);
                    lock (Gate) AddLocked(line, "");
                }
                return;     // 找到一份就够，避免两个目录重复
            }
            catch { /* 读不到就当没有 */ }
        }
    }

    private static void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(CacheFile));
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array) return;

            lock (Gate)
            {
                foreach (var it in items.EnumerateArray())
                {
                    string text = it.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
                    string from = it.TryGetProperty("from", out var f) ? (f.GetString() ?? "") : "";
                    if (text.Length == 0) continue;
                    AddLocked(Trim(text), from);
                }
            }
        }
        catch { /* 缓存坏了就当没有 */ }
    }

    private static void SaveCache()
    {
        try
        {
            List<object> items = new();
            lock (Gate)
            {
                // 只存网上拉的，quotes.txt 和内置的没必要重复落盘
                for (int i = Pool.Count - 1; i >= 0 && items.Count < 60; i--)
                {
                    if (Pool[i].From.Length == 0) continue;
                    items.Add(new { text = Pool[i].Text, from = Pool[i].From });
                }
            }
            if (items.Count == 0) return;

            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(new { items }));
        }
        catch { }
    }

    /// <summary>调用方需持有 Gate。</summary>
    private static bool AddLocked(string text, string from)
    {
        if (text.Length == 0) return false;
        if (!Seen.Add(text)) return false;
        Pool.Add(new Item(text, from));
        return true;
    }

    // ------------------------------------------------------------ 拉取
    private static void MaybeFetch()
    {
        if (DateTime.Now < _nextFetch) return;
        _nextFetch = DateTime.Now.AddMinutes(3);
        Task.Run(FetchAsync);
    }

    private static async Task FetchAsync()
    {
        // 同一时刻只允许一个请求在飞
        if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0) return;
        try
        {
            string json = await Http.GetStringAsync(Api).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string text = root.TryGetProperty("hitokoto", out var t) ? (t.GetString() ?? "").Trim() : "";
            if (text.Length == 0) return;
            text = Trim(text);

            string from = root.TryGetProperty("from", out var f) ? (f.GetString() ?? "").Trim() : "";

            bool added;
            lock (Gate)
            {
                added = AddLocked(text, from);
                if (added) _fresh = Pool.Count - 1;
            }

            if (added) SaveCache();
        }
        catch
        {
            // 断网离线都无所谓，池子里还有句子在转。
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }
}
