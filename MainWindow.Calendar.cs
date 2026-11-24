using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using NightBoard.Controls;
using NightBoard.Services;

namespace NightBoard;

public partial class MainWindow
{

    /// <summary>「距 春节  还有 68 天」。</summary>
    private void UpdateCountdown(DateTime now)
    {
        string name = (_settings.CountdownName ?? "").Trim();
        string dateText = (_settings.CountdownDate ?? "").Trim();

        string sig = name + "|" + dateText + "|" + now.Date.ToString("yyyyMMdd");
        if (sig == _cdSignature) return;
        _cdSignature = sig;

        if (name.Length == 0 || !DateTime.TryParse(dateText, out var target))
        {
            CountdownPanel.IsVisible = false;
            return;
        }

        CountdownPanel.IsVisible = true;
        int days = (target.Date - now.Date).Days;

        CdName.Text = name;

        if (days > 0)
        {
            CdMid.Text = "还有";
            CdValue.Text = days.ToString();
            CdUnit.Text = "天";
        }
        else if (days == 0)
        {
            CdMid.Text = "就是今天";
            CdValue.Text = "";
            CdUnit.Text = "";
        }
        else
        {
            CdMid.Text = "已过";
            CdValue.Text = (-days).ToString();
            CdUnit.Text = "天";
        }
    }


    /// <summary>节日 / 节气 / 下一件日程。</summary>
    private void UpdateCalendar(DateTime now)
    {
        // 节日和节气一天算一次就够（要翻几百天，每秒算太浪费）
        string today = now.ToString("yyyyMMdd");
        if (today != _festDay)
        {
            _festDay = today;
            _festivals = Festivals.Upcoming(now, 2).ToArray();
            _festival = _festivals.Length > 0 ? _festivals[0] : ("", default);
            _moonPhase = Astro.NextMoonPhase(now);
            _term = SolarTerms.Next(now);
            _calSignature = "";
        }

        int fd = _festival.Date == default ? 0 : (_festival.Date.Date - now.Date).Days;
        if (_festival.Name.Length > 0)
        {
            FestName.Text = _festival.Name;
            FestDate.Text = _festival.Date.ToString("MM-dd");
            FestLeft.Text = fd == 0 ? "就是今天" : $"{fd} 天后";
        }
        else
        {
            FestName.Text = "";
            FestDate.Text = "";
            FestLeft.Text = "";
        }

        int td = _term.Date == default ? 0 : (_term.Date.Date - now.Date).Days;
        if (_term.Name.Length > 0)
        {
            TermName.Text = _term.Name;
            TermDate.Text = _term.Date.ToString("MM-dd");
            TermLeft.Text = td == 0 ? "就是今天" : $"{td} 天后";
        }
        else
        {
            TermName.Text = "";
            TermDate.Text = "";
            TermLeft.Text = "";
        }

        var after = _festivals.Length > 1 ? _festivals[1] : ("", default);
        if (after.Item1.Length > 0)
        {
            AfterName.Text = after.Item1;
            AfterDate.Text = after.Item2.ToString("MM-dd");
            int ad = (after.Item2.Date - now.Date).Days;
            AfterLeft.Text = ad == 0 ? "就是今天" : $"{ad} 天后";
        }
        else
        {
            AfterName.Text = "";
            AfterDate.Text = "";
            AfterLeft.Text = "";
        }

        if (_moonPhase.Item1.Length > 0)
        {
            var span = _moonPhase.Item2 - now;
            MoonPhaseLabel.Text = $"下次{_moonPhase.Item1}";
            MoonPhaseDate.Text = _moonPhase.Item2.ToString(span.TotalDays >= 1 ? "MM-dd" : "HH:mm");
            MoonPhaseLeft.Text = span.TotalDays >= 1
                ? $"{(int)span.TotalDays} 天后"
                : $"{Math.Max(1, (int)span.TotalHours)} 小时后";
        }
        else
        {
            MoonPhaseLabel.Text = "下次月相";
            MoonPhaseDate.Text = "";
            MoonPhaseLeft.Text = "";
        }

        var ev = IcsCalendar.NextEvent(now);
        string sig = ev is null ? "" : $"{ev.Title}|{ev.Start:yyyyMMddHHmm}|{ev.End:HHmm}|{now.Hour}{now.Minute / 10}";
        if (sig == _calSignature) return;
        _calSignature = sig;

        if (ev is null)
        {
            EventCard.IsVisible = false;
            return;
        }

        EventCard.IsVisible = true;

        bool running = ev.AllDay ? ev.Start.Date <= now.Date : ev.Start <= now;
        if (running)
        {
            EvTime.Text = ev.AllDay ? "全天" : $"到 {ev.End:HH:mm}";
            EvTitle.Text = ev.Title;
            EvWhen.Text = "进行中";
        }
        else
        {
            var left = ev.Start - now;
            string span = left.TotalHours >= 1
                ? $"{(int)left.TotalHours} 小时 {left.Minutes} 分后"
                : $"{Math.Max(1, left.Minutes)} 分钟后";

            EvTime.Text = ev.Start.Date == now.Date
                ? ev.Start.ToString("HH:mm")
                : ev.Start.ToString("MM-dd HH:mm");
            EvTitle.Text = ev.Title;
            EvWhen.Text = span;
        }
    }


    private void UpdateSun(DateTime now)
    {
        var w = WeatherService.Current;
        Sun.Rise = w.Sunrise;
        Sun.Set = w.Sunset;
        Sun.Now = now;

        string caption;
        if (w.Sunrise == DateTime.MinValue || w.Sunset <= w.Sunrise)
        {
            caption = "";
        }
        else if (now < w.Sunrise)
        {
            caption = $"距天亮 {Fmt(w.Sunrise - now)}";
        }
        else if (now > w.Sunset)
        {
            caption = $"距天亮 {Fmt(w.Sunrise.AddDays(1) - now)}";
        }
        else
        {
            caption = $"距日落 {Fmt(w.Sunset - now)}";
        }

        if (Sun.Caption != caption) Sun.Caption = caption;
    }

}
