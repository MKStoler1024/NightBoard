using System;
using System.Collections.Generic;
using System.Globalization;

namespace NightBoard.Services;

/// <summary>
/// 下一个节假日：公历固定日 + 浮动周日（母亲节/父亲节）+ 农历传统节 + 清明。
/// 农历用 .NET 自带的 ChineseLunisolarCalendar，闰月单独处理（闰月不算过节）。
/// 除夕按「农历腊月最后一天」判定，不写死廿九或三十。
/// </summary>
public static class Festivals
{
    private static readonly (int Month, int Day, string Name)[] Gregorian =
    {
        (1, 1, "元旦"),
        (2, 14, "情人节"),
        (3, 8, "妇女节"),
        (3, 12, "植树节"),
        (4, 1, "愚人节"),
        (5, 1, "劳动节"),
        (5, 4, "青年节"),
        (6, 1, "儿童节"),
        (7, 1, "建党节"),
        (8, 1, "建军节"),
        (9, 10, "教师节"),
        (10, 1, "国庆节"),
        (10, 31, "万圣夜"),
        (12, 24, "平安夜"),
        (12, 25, "圣诞节"),
    };

    /// <summary>农历固定日（月, 日, 名称）。闰月不算。</summary>
    private static readonly (int Month, int Day, string Name)[] Lunar =
    {
        (1, 1, "春节"),
        (1, 15, "元宵节"),
        (2, 2, "龙抬头"),
        (5, 5, "端午节"),
        (7, 7, "七夕"),
        (7, 15, "中元节"),
        (8, 15, "中秋节"),
        (9, 9, "重阳节"),
        (12, 8, "腊八节"),
        (12, 23, "小年"),
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

            // 母亲节：五月第二个周日；父亲节：六月第三个周日
            if (d.DayOfWeek == DayOfWeek.Sunday)
            {
                if (d.Month == 5 && (d.Day - 1) / 7 == 1) return ("母亲节", d);
                if (d.Month == 6 && (d.Day - 1) / 7 == 2) return ("父亲节", d);
            }

            if (TryLunar(d, out int lm, out int ld, out bool leap) && !leap)
            {
                foreach (var (m, day, name) in Lunar)
                {
                    if (lm == m && ld == day) return (name, d);
                }

                // 除夕：次日就是农历正月初一
                if (lm == 12 && ld >= 29
                    && TryLunar(d.AddDays(1), out int nm, out int nd, out _)
                    && nm == 1 && nd == 1)
                {
                    return ("除夕", d);
                }
            }

            // 清明既是节气也是节日，用节气算法判定，避免写死 4 月 4 日
            var (termName, termDate) = SolarTerms.Next(d);
            if (termDate.Date == d && termName == "清明") return ("清明节", d);
        }

        return ("", start);
    }

    /// <summary>往后取若干个节日，按时间排序，不重复。</summary>
    public static List<(string Name, DateTime Date)> Upcoming(DateTime from, int count)
    {
        var result = new List<(string Name, DateTime Date)>();
        var cursor = from.Date;

        while (result.Count < count)
        {
            var (name, date) = Next(cursor);
            if (name.Length == 0) break;
            result.Add((name, date));
            cursor = date.Date.AddDays(1);
        }

        return result;
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
