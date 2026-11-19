using System;

namespace NightBoard.Services;

/// <summary>
/// 二十四节气：太阳视黄经每 15° 一个节气。
/// 从当前时刻往后找第一个跨过 15° 整数倍的时刻，二分到小时级就够（只显示到日）。
/// </summary>
public static class SolarTerms
{
    /// <summary>按黄经 0°、15°、30°… 排列。</summary>
    private static readonly string[] Names =
    {
        "春分", "清明", "谷雨", "立夏", "小满", "芒种",
        "夏至", "小暑", "大暑", "立秋", "处暑", "白露",
        "秋分", "寒露", "霜降", "立冬", "小雪", "大雪",
        "冬至", "小寒", "大寒", "立春", "雨水", "惊蛰",
    };

    /// <summary>下一个节气（名称 + 日期）。找不到返回空名。</summary>
    public static (string Name, DateTime Date) Next(DateTime from)
    {
        var start = from.Date;
        double prev = Astro.SunLongitude(start);

        for (int day = 1; day <= 40; day++)
        {
            var t = start.AddDays(day);
            double cur = Astro.SunLongitude(t);

            double lo360 = prev;
            double hi360 = cur < prev ? cur + 360 : cur;

            double target = Math.Floor(lo360 / 15) * 15 + 15;
            if (target <= hi360)
            {
                // 二分细化到分钟级
                double a = 0, b = 1;
                for (int i = 0; i < 24; i++)
                {
                    double mid = (a + b) / 2;
                    double lm = Astro.SunLongitude(start.AddDays(day - 1 + mid));
                    if (lm < lo360) lm += 360;
                    if (lm >= target) b = mid;
                    else a = mid;
                }

                var when = start.AddDays(day - 1 + (a + b) / 2);
                int idx = ((int)Math.Round((target % 360) / 15)) % 24;
                return (Names[idx], when.Date);
            }

            prev = cur;
        }

        return ("", start);
    }
}
