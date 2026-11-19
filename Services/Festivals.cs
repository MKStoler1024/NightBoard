using System;
using System.Globalization;

namespace NightBoard.Services;

/// <summary>
/// 下一个节假日：公历固定日 + 农历传统节。
/// 农历用 .NET 自带的 ChineseLunisolarCalendar，闰月单独处理（闰月不算过节）。
/// </summary>
public static class Festivals
{
    private static readonly (int Month, int Day, string Name)[] Gregorian =
    {
        (1, 1, "元旦"),
        (5, 1, "劳动节"),
        (10, 1, "国庆节"),
    };

    private static readonly (int Month, int Day, string Name)[] Lunar =
    {
        (1, 1, "春节"),
        (1, 15, "元宵节"),
        (5, 5, "端午节"),
        (8, 15, "中秋节"),
        (9, 9, "重阳节"),
    };

    private static readonly ChineseLunisolarCalendar Cal = new();

    /// <summary>从现在开始往后找最近的一个节日（含今天）。</summary>
    public static (string Name, DateTime Date) Next(DateTime from)
    {
        var start = from.Date;

        for (int i = 0; i <= 400; i++)
        {
            var d = start.AddDays(i);

            foreach (var (m, day, name) in Gregorian)
            {
                if (d.Month == m && d.Day == day) return (name, d);
            }

            if (TryLunar(d, out int lm, out int ld, out bool leap) && !leap)
            {
                foreach (var (m, day, name) in Lunar)
                {
                    if (lm == m && ld == day) return (name, d);
                }
            }
        }

        return ("", start);
    }

    private static bool TryLunar(DateTime date, out int month, out int day, out bool isLeap)
    {
        month = 0;
        day = 0;
        isLeap = false;

        if (date < Cal.MinSupportedDateTime || date > Cal.MaxSupportedDateTime) return false;

        try
        {
            int y = Cal.GetYear(date);
            int m = Cal.GetMonth(date);
            day = Cal.GetDayOfMonth(date);

            int leapMonth = Cal.GetLeapMonth(y);
            isLeap = leapMonth > 0 && m == leapMonth;
            month = leapMonth > 0 && m >= leapMonth ? m - 1 : m;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
