using DinamikAda.Core.Widgets;
using Xunit;

namespace DinamikAda.Tests;

public class VividTileColorTests
{
    /// <summary>Builds a BGRA buffer from horizontal bands of colour (fractions of the height).</summary>
    private static byte[] Bands(int w, int h, params (Rgb Color, double Fraction, byte Alpha)[] bands)
    {
        var buf = new byte[w * h * 4];
        int y0 = 0;
        foreach (var (c, f, a) in bands)
        {
            int rows = (int)Math.Round(h * f);
            for (int y = y0; y < Math.Min(h, y0 + rows); y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                buf[i] = c.B; buf[i + 1] = c.G; buf[i + 2] = c.R; buf[i + 3] = a;
            }
            y0 += rows;
        }
        return buf;
    }

    private static double Dist(Rgb a, Rgb b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    [Fact]
    public void Album_palette_finds_the_dominant_colours_and_ignores_black_and_white()
    {
        var orange = new Rgb(0xE0, 0x70, 0x20);
        var teal = new Rgb(0x20, 0x90, 0x90);
        var buf = Bands(64, 64,
            (new Rgb(0, 0, 0), 0.35, 255),
            (orange, 0.30, 255),
            (new Rgb(255, 255, 255), 0.15, 255),
            (teal, 0.20, 255));
        var pal = AlbumPalette.Extract(buf, 64, 64, 64 * 4);
        Assert.InRange(pal.Count, 2, 3);
        Assert.True(Dist(pal[0], orange) < 30, $"first was {pal[0].Hex}");
        Assert.Contains(pal, c => Dist(c, teal) < 30);
        Assert.DoesNotContain(pal, c => c.Luminance < 0.01 || c.Luminance > 0.9);
    }

    [Fact]
    public void Album_palette_prefers_a_small_vivid_area_over_a_large_grey_one()
    {
        var grey = new Rgb(0x80, 0x80, 0x84);
        var red = new Rgb(0xD8, 0x20, 0x30);
        var pal = AlbumPalette.Extract(Bands(40, 40, (grey, 0.7, 255), (red, 0.3, 255)), 40, 40, 160);
        Assert.True(Dist(pal[0], red) < 30, $"first was {pal[0].Hex}");
    }

    [Fact]
    public void Album_palette_is_empty_for_black_white_or_transparent_art_and_rejects_bad_buffers()
    {
        Assert.Empty(AlbumPalette.Extract(Bands(16, 16, (new Rgb(0, 0, 0), 0.5, 255), (new Rgb(255, 255, 255), 0.5, 255)), 16, 16, 64));
        Assert.Empty(AlbumPalette.Extract(Bands(16, 16, (new Rgb(200, 40, 40), 1.0, 0)), 16, 16, 64));
        Assert.Empty(AlbumPalette.Extract(new byte[10], 16, 16, 64));
        Assert.Empty(AlbumPalette.Extract([], 0, 0, 0));
    }

    [Fact]
    public void Album_palette_is_deterministic()
    {
        var rnd = new Random(7);
        var buf = new byte[32 * 32 * 4];
        rnd.NextBytes(buf);
        for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
        var a = AlbumPalette.Extract(buf, 32, 32, 128);
        var b = AlbumPalette.Extract(buf, 32, 32, 128);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("#FFE680")] // pale yellow
    [InlineData("#BFEFFF")] // pale sky
    [InlineData("#FF8A3D")] // orange
    [InlineData("#20242C")] // already dark
    public void Tile_colours_always_keep_white_text_readable(string hex)
    {
        var c = TileColors.ForWhiteText(Rgb.FromHex(hex));
        Assert.True(Rgb.Contrast(c, TileColors.White) >= TileColors.MinWhiteContrast, $"{hex} → {c.Hex}");
        foreach (var stop in TileColors.GradientFrom([Rgb.FromHex(hex)]))
            Assert.True(Rgb.Contrast(stop, TileColors.White) >= TileColors.MinWhiteContrast - 0.15, stop.Hex);
    }

    [Fact]
    public void Tile_colours_keep_the_hue_and_boost_saturation()
    {
        var src = Rgb.FromHex("#B07040");
        var o = TileColors.ForWhiteText(src);
        var (h0, s0, _) = src.ToHsl();
        var (h1, s1, _) = o.ToHsl();
        Assert.InRange(Math.Abs(h0 - h1), 0, 4);
        Assert.True(s1 > s0);
    }

    [Fact]
    public void Usage_scale_goes_green_amber_red()
    {
        var g = TileColors.Usage(20);
        var a = TileColors.Usage(75);
        var r = TileColors.Usage(97);
        Assert.True(g.G > g.R);
        Assert.True(a.R > 200 && a.G > 150);
        Assert.True(r.R > 200 && r.G < 100);
        Assert.Equal(g, TileColors.Usage(double.NaN));
    }

    [Fact]
    public void Contrast_and_hex_round_trip()
    {
        Assert.Equal(21, Rgb.Contrast(new Rgb(0, 0, 0), TileColors.White), 1);
        Assert.Equal("#3A8DFF", Rgb.FromHex("#3a8dff").Hex);
        var c = Rgb.FromHex("#6A4BC8");
        var (h, s, l) = c.ToHsl();
        Assert.True(Dist(c, Rgb.FromHsl(h, s, l)) <= 2);
    }

    private static readonly DateTime Rise = new(2026, 6, 1, 6, 0, 0);
    private static readonly DateTime Set = new(2026, 6, 1, 20, 0, 0);

    [Theory]
    [InlineData(0, 12, 0, SkyKind.ClearDay)]
    [InlineData(2, 12, 0, SkyKind.ClearDay)]
    [InlineData(0, 19, 15, SkyKind.GoldenHour)]
    [InlineData(1, 19, 50, SkyKind.Sunset)]
    [InlineData(0, 20, 20, SkyKind.Sunset)]
    [InlineData(0, 6, 10, SkyKind.Sunset)]
    [InlineData(0, 6, 40, SkyKind.GoldenHour)]
    [InlineData(0, 23, 0, SkyKind.Night)]
    [InlineData(2, 3, 0, SkyKind.Night)]
    [InlineData(3, 12, 0, SkyKind.Cloudy)]
    [InlineData(3, 19, 50, SkyKind.Cloudy)]
    [InlineData(3, 23, 0, SkyKind.CloudyNight)]
    [InlineData(61, 12, 0, SkyKind.Rain)]
    [InlineData(81, 23, 0, SkyKind.Rain)]
    [InlineData(73, 12, 0, SkyKind.Snow)]
    [InlineData(95, 12, 0, SkyKind.Storm)]
    [InlineData(45, 8, 0, SkyKind.Fog)]
    public void Sky_follows_condition_and_time_of_day(int code, int hour, int minute, SkyKind expected)
    {
        var now = new DateTime(2026, 6, 1, hour, minute, 0);
        Assert.Equal(expected, SkyGradient.Classify(code, isDay: hour is >= 6 and < 20, now, Rise, Set));
    }

    [Fact]
    public void Sky_without_sun_times_uses_the_is_day_flag()
    {
        var noon = new DateTime(2026, 6, 1, 12, 0, 0);
        Assert.Equal(SkyKind.ClearDay, SkyGradient.Classify(0, true, noon));
        Assert.Equal(SkyKind.Night, SkyGradient.Classify(0, false, noon));
    }

    [Fact]
    public void Every_sky_is_dark_enough_for_white_text()
    {
        foreach (var kind in Enum.GetValues<SkyKind>())
        {
            var stops = SkyGradient.Stops(kind);
            Assert.Equal(3, stops.Length);
            Assert.All(stops, s => Assert.True(Rgb.Contrast(s, TileColors.White) >= 3.0, $"{kind} {s.Hex}"));
        }
    }

    [Theory]
    [InlineData(0, true, WeatherIconKind.Sun)]
    [InlineData(0, false, WeatherIconKind.Moon)]
    [InlineData(2, true, WeatherIconKind.SunCloud)]
    [InlineData(3, true, WeatherIconKind.Cloud)]
    [InlineData(63, true, WeatherIconKind.Rain)]
    [InlineData(80, true, WeatherIconKind.SunRain)]
    [InlineData(75, false, WeatherIconKind.Snow)]
    [InlineData(99, true, WeatherIconKind.Storm)]
    [InlineData(48, true, WeatherIconKind.Fog)]
    public void Weather_icon_matches_the_code(int code, bool day, WeatherIconKind expected) =>
        Assert.Equal(expected, SkyGradient.Icon(code, day));

    [Fact]
    public void Service_colours_pick_a_readable_glyph()
    {
        Assert.True(ServiceColors.PrefersDarkGlyph(ServiceColors.For("lemonsqueezy")));
        Assert.False(ServiceColors.PrefersDarkGlyph(ServiceColors.For("stripe")));
        Assert.Equal(ServiceColors.For("unknown-thing"), ServiceColors.For("other"));
    }
}
