using System;
using System.Collections.Generic;
using System.Globalization;

namespace NightBoard.Services;

/// <summary>农历与干支。.NET 自带 ChineseLunisolarCalendar，不用引第三方库。</summary>
public static class Lunar
{
    private static readonly ChineseLunisolarCalendar Cal = new();

    private static readonly string[] TianGan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
    private static readonly string[] DiZhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
    private static readonly string[] ShengXiao = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    private static readonly string[] MonthName =
    {
        "正月", "二月", "三月", "四月", "五月", "六月",
        "七月", "八月", "九月", "十月", "冬月", "腊月"
    };

    private static readonly string[] DayName =
    {
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    };

    /// <summary>返回「农历十月廿九 · 丙午年」这样的短串；失败返回空。</summary>
    public static string Describe(DateTime date)
    {
        try
        {
            int year = Cal.GetYear(date);
            int month = Cal.GetMonth(date);
            int day = Cal.GetDayOfMonth(date);

            int leap = Cal.GetLeapMonth(year);          // 0 表示当年无闰月
            bool isLeap = leap > 0 && month == leap;
            int normalMonth = isLeap ? month - 1 : month;

            if (leap > 0 && month >= leap && !isLeap) normalMonth = month - 1;

            string monthStr = (isLeap ? "闰" : "") + MonthName[(normalMonth - 1 + 12) % 12];
            string dayStr = DayName[(day - 1 + 30) % 30];

            int sexagenary = Cal.GetSexagenaryYear(date);   // 1..60
            string ganZhi = TianGan[(sexagenary - 1) % 10] + DiZhi[(sexagenary - 1) % 12];
            string zodiac = ShengXiao[(sexagenary - 1) % 12];

            return $"农历{monthStr}{dayStr} · {ganZhi}{zodiac}年";
        }
        catch
        {
            return "";
        }
    }
}
