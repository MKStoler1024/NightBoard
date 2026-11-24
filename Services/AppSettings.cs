using System;
using System.IO;
using System.Text.Json;

namespace NightBoard.Services;

/// <summary>
/// 用户设置，落在 %LOCALAPPDATA%\NightBoard\settings.json。
/// 设置界面改的就是这一份；命令行参数仍然能临时压过它。
/// </summary>
public sealed class AppSettings
{
    /// <summary>天气城市名，例如「南岗区」。</summary>
    public string City { get; set; } = "南岗区";

    /// <summary>手工纬度（°N）。空着就按城市名查。</summary>
    public double? Lat { get; set; }

    /// <summary>手工经度（°E）。</summary>
    public double? Lon { get; set; }

    /// <summary>城市搜索选出来的 locationKey（weathercn:xxxx）。</summary>
    public string? Key { get; set; }

    /// <summary>auto / light / dark。</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>倒计时的名字，例如「春节」。留空则不显示。</summary>
    public string CountdownName { get; set; } = "";

    /// <summary>倒计时目标日（yyyy-MM-dd）。</summary>
    public string CountdownDate { get; set; } = "";

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NightBoard");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* 坏了就用默认 */ }

        return new AppSettings();
    }

    public void Save()
    {
        // 先写临时文件再替换：写一半断电也不会把原配置弄成半截 JSON
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(FilePath))
            {
                File.Replace(tmp, FilePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, FilePath);
            }
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write(ex, "AppSettings.Save");
        }
    }

    /// <summary>把设置推给天气服务。</summary>
    public void Apply()
    {
        if (!string.IsNullOrWhiteSpace(City)) WeatherService.CityName = City.Trim();
        WeatherService.LatOverride = Lat;
        WeatherService.LonOverride = Lon;
        WeatherService.KeyOverride = string.IsNullOrWhiteSpace(Key) ? null : Key;
    }
}
