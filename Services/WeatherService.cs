using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NightBoard.Services;

public sealed record WeatherAlert(string Title, string Type, string Level,
    string Detail = "", DateTime PubTime = default);

/// <summary>逐小时预报的一格。</summary>
public sealed record HourPoint(DateTime Time, double Temp, int Code);

/// <summary>某一天的预报。</summary>
public sealed record DayPoint(DateTime Date, double High, double Low, int Code);

/// <summary>城市搜索结果。</summary>
public sealed record CityCandidate(string Label, string Name, string Key, double Lat, double Lon);

public sealed record WeatherNow(
    double Temp,
    double Feels,
    int Humidity,
    int Code,
    string Text,
    int Aqi,
    string AqiLevel,
    DateTime Sunrise,
    DateTime Sunset,
    double? TomorrowLow,
    double? TomorrowHigh,
    int? TomorrowCode,
    string City,
    IReadOnlyList<WeatherAlert>? Alerts = null)
{
    /// <summary>风向风级，例如「西南风 3级」。</summary>
    public string Wind { get; init; } = "";

    public static readonly WeatherNow Empty = new(
        0, 0, 0, 99, "未知", 0, "", DateTime.MinValue, DateTime.MinValue, null, null, null, "",
        Array.Empty<WeatherAlert>());
}

/// <summary>
/// 小米天气（与 ClassIsland 同源）：先按城市名查 locationKey 和经纬度，
/// 再拉 weather/all。每 10 分钟刷新一次，结果落盘，断网时用上次的数据。
/// </summary>
public static class WeatherService
{
    private const string Host = "https://weatherapi.market.xiaomi.com/wtr-v3";
    private const string Sign = "zUFJoAR2ZVrDy1vF3D07";
    private const string AppKey = "weather20151024";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightBoard");

    private static readonly string CacheFile = Path.Combine(Dir, "weather.json");
    private static readonly string CityFile = Path.Combine(Dir, "city.json");

    private static int _fetching;
    private static DateTime _nextFetch = DateTime.MinValue;
    private static int _failStreak;

    /// <summary>数据是否已经过期（超过 10 分钟没刷新成功）。</summary>
    public static bool IsStale =>
        LastUpdate == DateTime.MinValue || (DateTime.Now - LastUpdate) > TimeSpan.FromMinutes(10);

    /// <summary>「12 分钟前」这样的年龄描述，界面在离线时显示。</summary>
    public static string AgeText
    {
        get
        {
            if (LastUpdate == DateTime.MinValue) return "无数据";
            var span = DateTime.Now - LastUpdate;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
            return $"{(int)span.TotalDays} 天前";
        }
    }

    public static WeatherNow Current { get; private set; } = WeatherNow.Empty;

    /// <summary>逐小时预报（约 23 格）。</summary>
    public static IReadOnlyList<HourPoint> Hourly { get; private set; } = Array.Empty<HourPoint>();

    /// <summary>未来三天。</summary>
    public static IReadOnlyList<DayPoint> Daily { get; private set; } = Array.Empty<DayPoint>();

    /// <summary>手工指定的 locationKey（设置界面选过城市就有）。</summary>
    public static string? KeyOverride { get; set; }
    public static DateTime LastUpdate { get; private set; } = DateTime.MinValue;

    /// <summary>城市名，例如「南岗区」。可用 --city 覆盖。</summary>
    public static string CityName { get; set; } = "南岗区";

    private sealed class CityInfo
    {
        public string Name { get; set; } = "";
        public string Key { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
        public DateTime Saved { get; set; }
    }

    private static CityInfo? _city;

    /// <summary>手工指定的纬度（°N），优先级高于城市查询结果。</summary>
    public static double? LatOverride { get; set; }

    /// <summary>手工指定的经度（°E），优先级同上。</summary>
    public static double? LonOverride { get; set; }

    /// <summary>当前纬度（°N）：手填 &gt; 城市查询 &gt; NaN。</summary>
    public static double Latitude => LatOverride ?? _city?.Lat ?? double.NaN;

    /// <summary>当前经度（°E）：手填 &gt; 城市查询 &gt; NaN。</summary>
    public static double Longitude => LonOverride ?? _city?.Lon ?? double.NaN;

    private static readonly Dictionary<int, string> CodeText = new()
    {
        [0] = "晴", [1] = "多云", [2] = "阴", [3] = "阵雨", [4] = "雷阵雨",
        [5] = "雷阵雨伴冰雹", [6] = "雨夹雪", [7] = "小雨", [8] = "中雨", [9] = "大雨",
        [10] = "暴雨", [11] = "大暴雨", [12] = "特大暴雨", [13] = "阵雪", [14] = "小雪",
        [15] = "中雪", [16] = "大雪", [17] = "暴雪", [18] = "雾", [19] = "冻雨",
        [20] = "沙尘暴", [21] = "小到中雨", [22] = "中到大雨", [23] = "大到暴雨",
        [24] = "暴雨到大暴雨", [25] = "大暴雨到特大暴雨", [26] = "小到中雪", [27] = "中到大雪",
        [28] = "大到暴雪", [29] = "浮尘", [30] = "扬沙", [31] = "强沙尘暴", [32] = "飑",
        [33] = "龙卷风", [34] = "弱高吹雪", [35] = "轻雾", [53] = "霾", [301] = "雨",
        [302] = "雪", [99] = "未知",
    };

    public static string TextOf(int code) => CodeText.TryGetValue(code, out var s) ? s : "未知";

    /// <summary>「西南风 3级」。速度按米每秒折算蒲福风级。</summary>
    private static string WindText(double dirDeg, double kmh)
    {
        if (kmh <= 0 && dirDeg <= 0) return "";

        string[] points = { "北风", "东北风", "东风", "东南风", "南风", "西南风", "西风", "西北风" };
        int idx = (int)Math.Round(((dirDeg % 360) + 360) % 360 / 45.0) % 8;

        double ms = kmh / 3.6;
        int level = ms switch
        {
            < 0.3 => 0, < 1.6 => 1, < 3.4 => 2, < 5.5 => 3, < 8.0 => 4, < 10.8 => 5,
            < 13.9 => 6, < 17.2 => 7, < 20.8 => 8, < 24.5 => 9, < 28.5 => 10, < 32.7 => 11, _ => 12,
        };

        return level <= 0 ? points[idx] : $"{points[idx]} {level}级";
    }

    public static string AqiLevelOf(int aqi) => aqi switch
    {
        <= 50 => "优",
        <= 100 => "良",
        <= 150 => "轻度污染",
        <= 200 => "中度污染",
        <= 300 => "重度污染",
        _ => "严重污染",
    };

    // ------------------------------------------------------------ 启动
    public static void Warmup()
    {
        LoadCity();
        LoadCache();

        Task.Run(async () =>
        {
            await EnsureCityAsync().ConfigureAwait(false);
            await RefreshAsync(force: true).ConfigureAwait(false);
        });
    }

    /// <summary>设置界面改过城市或经纬度之后调用：清掉旧缓存，按新参数重查重拉。</summary>
    public static void Reload()
    {
        _city = null;
        _nextFetch = DateTime.MinValue;

        Task.Run(async () =>
        {
            try { if (File.Exists(CityFile)) File.Delete(CityFile); } catch { }
            await EnsureCityAsync().ConfigureAwait(false);
            await RefreshAsync(force: true).ConfigureAwait(false);
        });
    }

    /// <summary>采样线程每轮调用；到点才真正发请求。</summary>
    public static void Tick()
    {
        if (DateTime.Now < _nextFetch) return;
        _nextFetch = DateTime.Now.AddMinutes(5);
        Task.Run(async () => await RefreshAsync().ConfigureAwait(false));
    }

    /// <summary>手动刷新：点了天气就调这个。</summary>
    public static void RefreshNow()
    {
        _nextFetch = DateTime.Now.AddMinutes(5);
        Task.Run(async () => await RefreshAsync(force: true).ConfigureAwait(false));
    }

    /// <summary>数据更新完的通知，界面可以据此重画。</summary>
    public static event Action? Updated;

    // ------------------------------------------------------------ 城市
    private static void LoadCity()
    {
        try
        {
            if (!File.Exists(CityFile)) return;
            var c = JsonSerializer.Deserialize<CityInfo>(File.ReadAllText(CityFile));
            if (c is { Key.Length: > 0 } && (DateTime.Now - c.Saved).TotalDays < 30)
                _city = c;
        }
        catch { }
    }

    private static async Task<CityInfo?> EnsureCityAsync()
    {
        if (_city != null) return _city;

        // 设置里选过城市：直接用它，不必再查
        if (!string.IsNullOrWhiteSpace(KeyOverride) && LatOverride is double oLat && LonOverride is double oLon)
        {
            var known = new CityInfo
            {
                Name = CityName,
                Key = KeyOverride!,
                Lat = oLat,
                Lon = oLon,
                Saved = DateTime.Now,
            };
            _city = known;
            SaveCity(known);
            return known;
        }

        // 1) 按城市名查
        try
        {
            string url = $"{Host}/location/city/search?name={Uri.EscapeDataString(CityName)}&locale=zh_cn";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var first = doc.RootElement[0];
                var info = new CityInfo
                {
                    Name = Str(first, "name"),
                    Key = Str(first, "locationKey"),
                    Lat = Dbl(first, "latitude"),
                    Lon = Dbl(first, "longitude"),
                    Saved = DateTime.Now,
                };
                if (info.Key.Length > 0)
                {
                    _city = info;
                    SaveCity(info);
                    return info;
                }
            }
        }
        catch { }

        // 2) 退而用 IP 定位
        try
        {
            using var ipDoc = JsonDocument.Parse(
                await Http.GetStringAsync("http://ip-api.com/json/?lang=zh-CN").ConfigureAwait(false));
            double lat = Dbl(ipDoc.RootElement, "lat");
            double lon = Dbl(ipDoc.RootElement, "lon");
            if (lat != 0 || lon != 0)
            {
                string url = $"{Host}/location/city/geo?longitude={lon}&latitude={lat}&locale=zh_cn";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    var info = new CityInfo
                    {
                        Name = Str(first, "name"),
                        Key = Str(first, "locationKey"),
                        Lat = Dbl(first, "latitude"),
                        Lon = Dbl(first, "longitude"),
                        Saved = DateTime.Now,
                    };
                    if (info.Key.Length > 0)
                    {
                        _city = info;
                        SaveCity(info);
                        return info;
                    }
                }
            }
        }
        catch { }

        return _city;
    }

    private static void SaveCity(CityInfo c)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(CityFile, JsonSerializer.Serialize(c));
        }
        catch { }
    }

    // ------------------------------------------------------------ 天气
    private static void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return;
            var w = JsonSerializer.Deserialize<WeatherNow>(File.ReadAllText(CacheFile));
            if (w != null && w.City.Length > 0)
            {
                Current = w;
                LastUpdate = DateTime.Now;
            }
        }
        catch { }
    }

    private static async Task RefreshAsync(bool force = false)
    {
        if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0) return;
        try
        {
            var city = await EnsureCityAsync().ConfigureAwait(false);
            if (city == null) return;

            double lat = Latitude, lon = Longitude;
            if (double.IsNaN(lat) || double.IsNaN(lon)) return;

            long ts = DateTimeOffset.Now.ToUnixTimeSeconds();
            string url = $"{Host}/weather/all?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                         $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                         $"&locationKey={Uri.EscapeDataString(city.Key)}&days=5" +
                         $"&appKey={AppKey}&sign={Sign}&isGlobal=false&locale=zh_cn&ts={ts}";

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
            var root = doc.RootElement;

            var cur = root.TryGetProperty("current", out var c) ? c : default;
            double temp = Dbl(Nested(cur, "temperature"), "value");
            double feels = Dbl(Nested(cur, "feelsLike"), "value");
            int humidity = (int)Dbl(Nested(cur, "humidity"), "value");
            int code = (int)Dbl(cur, "weather");
            if (code == 0 && Str(cur, "weather").Length > 0)
                int.TryParse(Str(cur, "weather"), out code);

            double windDir = 0, windKmh = 0;
            if (cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty("wind", out var wnd))
            {
                windDir = Dbl(Nested(wnd, "direction"), "value");
                windKmh = Dbl(Nested(wnd, "speed"), "value");
            }

            int aqi = 0;
            if (root.TryGetProperty("aqi", out var a))
            {
                string s = Str(a, "aqi");
                if (s.Length > 0) int.TryParse(s, out aqi);
            }

            DateTime rise = DateTime.MinValue, set = DateTime.MinValue;
            double? tmrLow = null, tmrHigh = null;
            int? tmrCode = null;
            var alerts = new List<WeatherAlert>();
            var hourly = new List<HourPoint>();
            var daily = new List<DayPoint>();

            if (root.TryGetProperty("alerts", out var al) && al.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in al.EnumerateArray())
                {
                    string atitle = Str(el, "title");
                    string atype = Str(el, "type");
                    string alevel = Str(el, "level");
                    if (string.IsNullOrEmpty(atitle) && string.IsNullOrEmpty(atype)) continue;
                    DateTime pub = DateTime.MinValue;
                    string pubText = Str(el, "pubTime");
                    if (pubText.Length > 0) DateTime.TryParse(pubText, out pub);

                    alerts.Add(new WeatherAlert(
                        string.IsNullOrEmpty(atitle) ? atype + "预警" : atitle,
                        atype, string.IsNullOrEmpty(alevel) ? "未知" : alevel,
                        Str(el, "detail"), pub));
                }
            }

            if (root.TryGetProperty("forecastDaily", out var fd))
            {
                if (fd.TryGetProperty("sunRiseSet", out var ss) &&
                    ss.TryGetProperty("value", out var v) &&
                    v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                {
                    var today = v[0];
                    rise = Date(today, "from");
                    set = Date(today, "to");
                }

                if (fd.TryGetProperty("temperature", out var tp) &&
                    tp.TryGetProperty("value", out var tv) &&
                    tv.ValueKind == JsonValueKind.Array && tv.GetArrayLength() > 1)
                {
                    var t = tv[1];
                    tmrHigh = Dbl(t, "from");
                    tmrLow = Dbl(t, "to");
                }

                if (fd.TryGetProperty("weather", out var wv) &&
                    wv.TryGetProperty("value", out var wvv) &&
                    wvv.ValueKind == JsonValueKind.Array && wvv.GetArrayLength() > 1)
                {
                    var el = wvv[1];
                    if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int d))
                        tmrCode = d;
                    else if (el.ValueKind == JsonValueKind.Number)
                        tmrCode = (int)el.GetDouble();
                }

                // 逐日（取前三天：今天之后的三天，给"明天/后天/大后天"用）
                if (fd.TryGetProperty("temperature", out var dtp) &&
                    dtp.TryGetProperty("value", out var dtv) && dtv.ValueKind == JsonValueKind.Array)
                {
                    JsonElement dwv = default;
                    if (fd.TryGetProperty("weather", out var dwe) &&
                        dwe.TryGetProperty("value", out var dwvv)) dwv = dwvv;

                    int days = dtv.GetArrayLength();
                    for (int i = 1; i < Math.Min(days, 4); i++)
                    {
                        var t = dtv[i];
                        double hi = Dbl(t, "from"), lo = Dbl(t, "to");

                        int dc = 99;
                        if (dwv.ValueKind == JsonValueKind.Array && i < dwv.GetArrayLength())
                        {
                            var el = dwv[i];
                            JsonElement from = el;
                            if (el.ValueKind == JsonValueKind.Object &&
                                el.TryGetProperty("from", out var fe)) from = fe;

                            if (from.ValueKind == JsonValueKind.String)
                                int.TryParse(from.GetString(), out dc);
                            else if (from.ValueKind == JsonValueKind.Number)
                                dc = (int)from.GetDouble();
                        }

                        daily.Add(new DayPoint(DateTime.Today.AddDays(i), hi, lo, dc));
                    }
                }
            }

            // 逐小时（接口从 pubTime 起给 23 格）
            if (root.TryGetProperty("forecastHourly", out var fh) &&
                fh.TryGetProperty("temperature", out var htp) &&
                htp.TryGetProperty("value", out var htv) && htv.ValueKind == JsonValueKind.Array)
            {
                DateTime start = DateTime.Now;
                string pt = Str(htp, "pubTime");
                if (pt.Length > 0) DateTime.TryParse(pt, out start);
                if (start == DateTime.MinValue) start = DateTime.Now;

                JsonElement hwv = default;
                if (fh.TryGetProperty("weather", out var hwe) &&
                    hwe.TryGetProperty("value", out var hwvv)) hwv = hwvv;

                int n = htv.GetArrayLength();
                for (int i = 0; i < n; i++)
                {
                    double tempH = htv[i].ValueKind == JsonValueKind.Number ? htv[i].GetDouble() : 0;
                    int hc = 99;
                    if (hwv.ValueKind == JsonValueKind.Array && i < hwv.GetArrayLength())
                    {
                        var el = hwv[i];
                        if (el.ValueKind == JsonValueKind.Number) hc = (int)el.GetDouble();
                        else if (el.ValueKind == JsonValueKind.String) int.TryParse(el.GetString(), out hc);
                    }
                    hourly.Add(new HourPoint(start.AddHours(i), tempH, hc));
                }
            }


            // 接口偶尔返回空对象，别把已有数据冲掉
            if (cur.ValueKind != JsonValueKind.Object || temp == 0 && code == 0 && rise == DateTime.MinValue)
                return;

            Current = new WeatherNow(
                temp, feels, humidity, code, TextOf(code),
                aqi, AqiLevelOf(aqi),
                rise, set, tmrLow, tmrHigh, tmrCode, city.Name, alerts);

            Current = Current with { Wind = WindText(windDir, windKmh) };

            Hourly = hourly;
            Daily = daily;
            LastUpdate = DateTime.Now;
            Updated?.Invoke();
            _failStreak = 0;
            _nextFetch = DateTime.Now.AddMinutes(5);

            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(CacheFile, JsonSerializer.Serialize(Current));
            }
            catch { }
        }
        catch (Exception ex)
        {
            // 失败按 1、2、4、8… 分钟退避，最多 30 分钟，避免断网时死循环打请求
            _failStreak = Math.Min(_failStreak + 1, 5);
            _nextFetch = DateTime.Now.AddMinutes(Math.Min(30, Math.Pow(2, _failStreak)));
            CrashLog.Write(ex, "WeatherService.Refresh");
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    /// <summary>按名字搜城市；名字为空就取热门城市。设置界面用它。</summary>
    public static async Task<List<CityCandidate>> SearchCitiesAsync(string name)
    {
        var list = new List<CityCandidate>();
        try
        {
            string url = string.IsNullOrWhiteSpace(name)
                ? $"{Host}/location/city/hots?locale=zh_cn"
                : $"{Host}/location/city/search?name={Uri.EscapeDataString(name.Trim())}&locale=zh_cn";

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string key = Str(el, "locationKey");
                string nm = Str(el, "name");
                string aff = Str(el, "affiliation");
                if (key.Length == 0 || nm.Length == 0) continue;

                list.Add(new CityCandidate(
                    aff.Length > 0 ? $"{nm}（{aff}）" : nm, nm, key,
                    Dbl(el, "latitude"), Dbl(el, "longitude")));
            }
        }
        catch { }

        return list;
    }

    // ------------------------------------------------------------ 小工具
    private static JsonElement Nested(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }

    private static double Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0,
            _ => 0,
        };
    }

    private static DateTime Date(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return DateTime.MinValue;
        if (v.ValueKind != JsonValueKind.String) return DateTime.MinValue;
        return DateTime.TryParse(v.GetString(), out var d) ? d : DateTime.MinValue;
    }
}
