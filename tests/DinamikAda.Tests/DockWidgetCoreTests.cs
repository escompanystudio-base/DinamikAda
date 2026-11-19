using DinamikAda.Core.Config;
using DinamikAda.Core.Widgets;
using DinamikAda.Platform.Win32.Windowing;
using DinamikAda.Core.Abstractions;
using Xunit;

namespace DinamikAda.Tests;

public class DockWidgetCoreTests
{
    [Fact]
    public void Catalog_ids_are_unique_and_bilingual()
    {
        var ids = DockWidgetCatalog.All.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(DockWidgetCatalog.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.NameTr));
            Assert.False(string.IsNullOrWhiteSpace(d.NameEn));
            Assert.False(string.IsNullOrWhiteSpace(d.DescriptionTr));
            Assert.InRange(d.WidthUnits, 1.0, 4.0);
        });
        Assert.True(DockWidgetCatalog.All.Count >= 60);
    }

    [Fact]
    public void Sanitize_drops_unknown_and_duplicate_ids_and_caps_count()
    {
        var input = new[] { "cpu", "CPU", "nope", " ram ", "weather" }
            .Concat(DockWidgetCatalog.All.Select(d => d.Id));
        var clean = DockWidgetCatalog.Sanitize(input);
        Assert.Equal("cpu", clean[0]);
        Assert.Equal("ram", clean[1]);
        Assert.Equal("weather", clean[2]);
        Assert.Equal(DockWidgetCatalog.MaxWidgetsOnDock, clean.Count);
        Assert.Equal(clean.Count, clean.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Config_seeds_default_widgets_once_then_respects_empty_list()
    {
        var cfg = new AppConfig();
        cfg.Sanitize();
        Assert.Equal(DockWidgetCatalog.DefaultWidgets, cfg.DockWidgets);
        Assert.True(cfg.DockWidgetsSeeded);

        cfg.DockWidgets = new List<string>();
        cfg.Sanitize();
        Assert.Empty(cfg.DockWidgets);
    }

    [Fact]
    public void Config_normalizes_widget_settings()
    {
        var cfg = new AppConfig
        {
            DockWidgetsSeeded = true,
            CurrencyFrom = " usd!! ",
            CryptoSymbol = "eth/usdt",
            StockSymbols = new() { "aapl", "AAPL", "msft", "nvda", "tsla" },
            WeatherLatitude = 200,
            WeatherLongitude = 10,
            DockTimerMinutes = 0,
            DockTodos = new() { new DockTodoItem { Text = "  call mom " }, new DockTodoItem { Text = "  " } }
        };
        cfg.Sanitize();
        Assert.Equal("USD", cfg.CurrencyFrom);
        Assert.Equal("ETHUSDT", cfg.CryptoSymbol);
        Assert.Equal(new[] { "AAPL", "MSFT", "NVDA" }, cfg.StockSymbols);
        Assert.Null(cfg.WeatherLatitude);
        Assert.Null(cfg.WeatherLongitude);
        Assert.Equal(1, cfg.DockTimerMinutes);
        Assert.Single(cfg.DockTodos);
        Assert.Equal("call mom", cfg.DockTodos[0].Text);
    }

    [Theory]
    [InlineData(0, 12, 0)]
    [InlineData(1, 12, 1)]
    [InlineData(50, 12, 6)]
    [InlineData(51, 12, 7)]
    [InlineData(100, 12, 12)]
    [InlineData(140, 12, 12)]
    public void Lit_cells_round_up_and_clamp(int pct, int cells, int expected)
    {
        Assert.Equal(expected, DockWidgetMath.LitCells(pct, cells));
    }

    [Fact]
    public void Progress_helpers_cover_day_month_year()
    {
        var noon = new DateTime(2026, 11, 16, 12, 0, 0);
        Assert.Equal(0.5, DockWidgetMath.DayProgress(noon), 3);
        Assert.Equal((15.5) / 30.0, DockWidgetMath.MonthProgress(noon), 3);
        Assert.Equal(0.0, DockWidgetMath.YearProgress(new DateTime(2026, 1, 1)), 6);
        Assert.InRange(DockWidgetMath.YearProgress(new DateTime(2026, 12, 31, 23, 59, 0)), 0.999, 1.0);
    }

    [Fact]
    public void Clock_and_duration_formatting()
    {
        var t = new DateTime(2026, 11, 18, 21, 5, 9);
        Assert.Equal("21:05", DockWidgetMath.FormatClock(t, use24h: true));
        Assert.Equal("9:05", DockWidgetMath.FormatClock(t, use24h: false));
        Assert.Equal("PM", DockWidgetMath.AmPm(t));
        Assert.Equal("1sa 27dk", DockWidgetMath.FormatDuration(TimeSpan.FromMinutes(87), "tr"));
        Assert.Equal("1h 27m", DockWidgetMath.FormatDuration(TimeSpan.FromMinutes(87), "en"));
        Assert.Equal("05:03", DockWidgetMath.FormatTimer(TimeSpan.FromSeconds(303)));
        Assert.Equal("1:00:00", DockWidgetMath.FormatTimer(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Sun_progress_is_null_at_night()
    {
        var rise = new DateTime(2026, 11, 18, 7, 0, 0);
        var set = new DateTime(2026, 11, 18, 17, 0, 0);
        Assert.Null(DockWidgetMath.SunProgress(rise.AddHours(-1), rise, set));
        Assert.Equal(0.5, DockWidgetMath.SunProgress(rise.AddHours(5), rise, set)!.Value, 6);
    }

    [Fact]
    public void Normalize_and_hand_angles()
    {
        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, DockWidgetMath.Normalize([2, 4, 6]));
        Assert.Equal(new[] { 0.5, 0.5 }, DockWidgetMath.Normalize([3, 3]));
        var (h, m, s) = DockWidgetMath.HandAngles(new DateTime(2026, 1, 1, 3, 0, 0));
        Assert.Equal(90.0, h, 6);
        Assert.Equal(0.0, m, 6);
        Assert.Equal(0.0, s, 6);
        Assert.Equal("+3", DockWidgetMath.FormatOffset(TimeSpan.FromHours(3)));
        Assert.Equal("-5:30", DockWidgetMath.FormatOffset(TimeSpan.FromMinutes(-330)));
        Assert.Equal("New York", DockWidgetMath.CityFromZoneId("America/New_York"));
    }

    [Fact]
    public void CityFromLocalTimeZone_MapsWindowsZonesAndSkipsCitylessOnes()
    {
        Assert.Equal("Istanbul", DockWidgetMath.CityFromLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time")));
        Assert.Null(DockWidgetMath.CityFromLocalTimeZone(TimeZoneInfo.Utc));
    }

    [Fact]
    public void Screen_time_splits_samples_across_hours_and_days()
    {
        var ledger = new ScreenTimeLedger();
        var end = new DateTime(2026, 11, 18, 10, 0, 10);
        ledger.AddSample("Code", end, 20, AppKindClassifier.Classify("Code"));
        var day = ledger.GetDay(end)!;
        Assert.Equal(10, day.HourSeconds[9], 6);
        Assert.Equal(10, day.HourSeconds[10], 6);
        Assert.Equal(20, day.AppSeconds["code"], 6);
        Assert.Equal(20, day.KindHourSeconds[AppKindClassifier.Work].Sum(), 6);

        // Gaps longer than the max sample gap are capped (sleep/lock must not count).
        ledger.AddSample("chrome", end.AddMinutes(5), 600, AppKindClassifier.Browser);
        Assert.Equal(ScreenTimeLedger.MaxSampleGap.TotalSeconds, ledger.GetDay(end)!.AppSeconds["chrome"], 6);

        var top = ledger.TopApps(end, 5, week: false);
        Assert.Equal("chrome", top[0].App);
    }

    [Fact]
    public void Screen_time_round_trips_and_prunes_old_days()
    {
        var ledger = new ScreenTimeLedger();
        var now = new DateTime(2026, 11, 18, 12, 0, 0);
        ledger.AddSample("old", now.AddDays(-20), 10);
        ledger.AddSample("new", now, 10);
        var restored = ScreenTimeLedger.Deserialize(ledger.Serialize(), now);
        Assert.Null(restored.GetDay(now.AddDays(-20)));
        Assert.Equal(10, restored.GetDay(now)!.AppSeconds["new"], 6);
        Assert.Equal(10, restored.WeekTotals(now)[6], 6);
        Assert.NotNull(ScreenTimeLedger.Deserialize("{not json", now));
    }

    [Theory]
    [InlineData("Code.exe", AppKindClassifier.Work)]
    [InlineData("msedge", AppKindClassifier.Browser)]
    [InlineData("Discord", AppKindClassifier.Chat)]
    [InlineData("Spotify", AppKindClassifier.Media)]
    [InlineData("random", AppKindClassifier.Other)]
    public void App_kind_classifier(string process, string kind)
    {
        Assert.Equal(kind, AppKindClassifier.Classify(process));
    }

    private static readonly MonitorDescriptor Mon = new(0, "\\\\.\\DISPLAY1", "Primary", true, 0, 0, 1920, 1080, 1.0, 60);

    [Fact]
    public void Top_edge_layout_places_host_and_hit_rect_at_the_top()
    {
        var bottom = DockLayoutCalculator.ComputeHostWindowBounds(Mon, 96);
        var top = DockLayoutCalculator.ComputeHostWindowBounds(Mon, 96, atTop: true, topInsetPx: 40);
        Assert.Equal(1080 - bottom.HeightPx, bottom.TopPx);
        Assert.Equal(40, top.TopPx);

        var hit = DockLayoutCalculator.ComputeDockHitRectPx(Mon, 600, 52, atTop: true, topInsetPx: 40);
        Assert.Equal(40, hit.Top);
        Assert.True(hit.Bottom > hit.Top && hit.Bottom < 200);

        Assert.True(DockLayoutCalculator.IsPointInTopRevealZone(500, 10, Mon, 44));
        Assert.False(DockLayoutCalculator.IsPointInTopRevealZone(500, 600, Mon, 44));
        Assert.True(DockLayoutCalculator.ShouldSnapDockToTop(300, Mon));
        Assert.False(DockLayoutCalculator.ShouldSnapDockToTop(700, Mon));
        Assert.True(DockLayoutCalculator.IsPointInDockKeepOpenZone(960, hit.Bottom + 10, hit, 24, 30, atTop: true));
    }
}
