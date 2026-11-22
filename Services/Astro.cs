using System;

namespace NightBoard.Services;

/// <summary>
/// 太阳与月亮的位置。低精度天文算法（Meeus 的简化式），误差：
/// 太阳高度约 0.02°，月亮约 0.3°——看板够用，也不值得为此拉一整套星历。
///
/// 输入当地时间 + 纬度（°N）+ 经度（°E），输出地平高度（°，地平线以上为正）。
/// </summary>
public static class Astro
{
    private const double Rad = Math.PI / 180.0;
    private const double Deg = 180.0 / Math.PI;

    private static double Mod360(double v)
    {
        v %= 360.0;
        return v < 0 ? v + 360.0 : v;
    }

    /// <summary>儒略日。DateTime 按本地时间处理，内部先转 UTC。</summary>
    private static double JulianDay(DateTime local) => local.ToUniversalTime().ToOADate() + 2415018.5;

    /// <summary>自 J2000 起的儒略日数。</summary>
    private static double Days(double jd) => jd - 2451545.0;

    /// <summary>太阳黄经（°）与黄赤交角（°）。</summary>
    private static (double lambda, double eps) SunEcliptic(double d)
    {
        double L = Mod360(280.460 + 0.9856474 * d);
        double g = Mod360(357.528 + 0.9856003 * d) * Rad;
        double lambda = L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g);
        double eps = 23.439 - 0.0000004 * d;
        return (lambda, eps);
    }

    /// <summary>当地恒星时（°）。</summary>
    private static double LocalSidereal(double d, double lon)
    {
        double gmst = Mod360(280.46061837 + 360.98564736629 * d);
        return Mod360(gmst + lon);
    }

    private static double Altitude(double dec, double hourAngle, double lat) =>
        Math.Asin(Math.Sin(lat * Rad) * Math.Sin(dec * Rad) +
                  Math.Cos(lat * Rad) * Math.Cos(dec * Rad) * Math.Cos(hourAngle * Rad)) * Deg;

    /// <summary>太阳地平高度（°）。</summary>
    public static double SunAltitude(DateTime local, double lat, double lon)
    {
        double d = Days(JulianDay(local));
        var (lambda, eps) = SunEcliptic(d);

        double ra = Math.Atan2(Math.Cos(eps * Rad) * Math.Sin(lambda * Rad), Math.Cos(lambda * Rad)) * Deg;
        double dec = Math.Asin(Math.Sin(eps * Rad) * Math.Sin(lambda * Rad)) * Deg;

        double ha = Mod360(LocalSidereal(d, lon) - ra);
        if (ha > 180) ha -= 360;

        return Altitude(dec, ha, lat);
    }

    /// <summary>月亮地平高度（°）。黄道坐标取到主项，误差约 0.3°。</summary>
    public static double MoonAltitude(DateTime local, double lat, double lon)
    {
        double d = Days(JulianDay(local));

        double Lp = Mod360(218.316 + 13.176396 * d);
        double M = Mod360(134.963 + 13.064993 * d) * Rad;
        double F = Mod360(93.272 + 13.229350 * d) * Rad;

        double lambda = (Lp + 6.289 * Math.Sin(M)) * Rad;
        double beta = 5.128 * Math.Sin(F) * Rad;
        double eps = 23.439 * Rad;

        double ra = Math.Atan2(Math.Sin(lambda) * Math.Cos(eps) - Math.Tan(beta) * Math.Sin(eps),
            Math.Cos(lambda)) * Deg;
        double dec = Math.Asin(Math.Sin(beta) * Math.Cos(eps) +
                               Math.Cos(beta) * Math.Sin(eps) * Math.Sin(lambda)) * Deg;

        double ha = Mod360(LocalSidereal(d, lon) - ra);
        if (ha > 180) ha -= 360;

        return Altitude(dec, ha, lat);
    }

    /// <summary>太阳视黄经（°，0–360）。节气就是它每 15° 一个刻度。</summary>
    public static double SunLongitude(DateTime local)
    {
        double d = Days(JulianDay(local));
        var (lambda, _) = SunEcliptic(d);
        return lambda;
    }

    /// <summary>月相相位，0 = 新月，0.25 = 上弦，0.5 = 满月，0.75 = 下弦。</summary>
    public static double MoonPhase(DateTime local)
    {
        double d = Days(JulianDay(local));
        var (lambdaSun, _) = SunEcliptic(d);

        double Lp = Mod360(218.316 + 13.176396 * d);
        double M = Mod360(134.963 + 13.064993 * d) * Rad;
        double lambdaMoon = Lp + 6.289 * Math.Sin(M);

        return Mod360(lambdaMoon - lambdaSun) / 360.0;
    }

    /// <summary>月相照亮比例，0 = 新月，1 = 满月。</summary>
    public static double MoonIllumination(DateTime local)
    {
        double d = Days(JulianDay(local));
        var (lambdaSun, _) = SunEcliptic(d);

        double Lp = Mod360(218.316 + 13.176396 * d);
        double M = Mod360(134.963 + 13.064993 * d) * Rad;
        double lambdaMoon = Lp + 6.289 * Math.Sin(M);

        double diff = Mod360(lambdaMoon - lambdaSun) * Rad;
        return (1 - Math.Cos(diff)) / 2.0;
    }

    /// <summary>下一次满月或新月（取先到的那个）。相位的定义：0 新月、0.5 满月。</summary>
    public static (string Name, DateTime At) NextMoonPhase(DateTime from)
    {
        double prev = MoonPhase(from);

        for (int i = 1; i <= 24 * 45; i++)
        {
            var t = from.AddHours(i);
            double cur = MoonPhase(t);

            if (prev < 0.5 && cur >= 0.5) return ("满月", t);
            if (cur < prev) // 越过 1.0 回到新月
            {
                if (cur <= 0.02 || prev >= 0.98) return ("新月", t);
            }

            prev = cur;
        }

        return ("", from);
    }
}
