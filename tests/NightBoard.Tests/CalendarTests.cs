using System;
using System.Linq;
using NightBoard.Services;
using Xunit;

namespace NightBoard.Tests;

public class FestivalTests
{
    [Fact]
    public void Next_ReturnsDateWithinSearchWindow()
    {
        var from = new DateTime(2026, 11, 24);
        var (name, date) = Festivals.Next(from);

        Assert.False(string.IsNullOrEmpty(name));
        Assert.InRange(date, from.Date, from.Date.AddDays(400));
    }

    [Fact]
    public void Upcoming_IsSortedAndDistinct()
    {
        var from = new DateTime(2026, 11, 24);
        var list = Festivals.Upcoming(from, 4);

        Assert.Equal(4, list.Count);
        for (int i = 1; i < list.Count; i++)
        {
            Assert.True(list[i].Date > list[i - 1].Date, "节日应按时间递增");
        }
    }

    [Fact]
    public void NewYearsEve_IsDayBeforeSpringFestival()
    {
        // 从每年一月开始找，农历腊月最后一天应正好是春节前一天
        for (int year = 2027; year <= 2029; year++)
        {
            var start = new DateTime(year, 1, 1);
            var all = Festivals.Upcoming(start, 40)
                .GroupBy(x => x.Name)
                .ToDictionary(g => g.Key, g => g.First().Date);
            if (all.TryGetValue("春节", out var spring) && all.TryGetValue("除夕", out var eve))
            {
                Assert.Equal(spring.AddDays(-1), eve);
            }
        }
    }

    [Fact]
    public void MothersDay_IsSecondSundayOfMay()
    {
        var start = new DateTime(2027, 4, 1);
        var mothers = Festivals.Upcoming(start, 20).FirstOrDefault(x => x.Name == "母亲节");

        if (mothers.Name is null) return;
        Assert.Equal(DayOfWeek.Sunday, mothers.Date.DayOfWeek);
        Assert.Equal(5, mothers.Date.Month);
        Assert.InRange(mothers.Date.Day, 8, 14);
    }
}

public class SolarTermTests
{
    [Fact]
    public void Next_IsWithinOneMonth()
    {
        var from = new DateTime(2026, 11, 24);
        var (name, date) = SolarTerms.Next(from);

        Assert.False(string.IsNullOrEmpty(name));
        Assert.InRange(date.Date, from.Date, from.Date.AddDays(32));
    }
}

public class AstroTests
{
    [Fact]
    public void MoonPhase_IsNormalised()
    {
        var t = new DateTime(2026, 11, 24, 15, 0, 0);
        double p = Astro.MoonPhase(t);

        Assert.InRange(p, 0.0, 1.0);
    }

    [Fact]
    public void SunAltitude_StaysInPhysicalRange()
    {
        var t = new DateTime(2026, 6, 21, 12, 0, 0);
        double alt = Astro.SunAltitude(t, 45.7, 126.6);

        Assert.InRange(alt, -90.0, 90.0);
    }
}
