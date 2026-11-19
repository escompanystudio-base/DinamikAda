namespace DinamikAda.Core.Widgets;

/// <summary>An opaque sRGB colour with the small amount of colour math the vivid widget tiles need.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..];
        if (s.Length != 6) throw new FormatException("Expected #RRGGBB.");
        return new Rgb(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
    }

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    public double Luminance
    {
        get
        {
            static double Lin(byte c)
            {
                double v = c / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);
        }
    }

    /// <summary>WCAG contrast ratio between two colours (1..21).</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = a.Luminance, lb = b.Luminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        double d = max - min;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        static byte B8(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return new Rgb(B8(r + m), B8(g + m), B8(b + m));
    }

    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static byte L(byte x, byte y, double t) => (byte)Math.Round(x + (y - x) * t);
        return new Rgb(L(a.R, b.R, t), L(a.G, b.G, t), L(a.B, b.B, t));
    }

    /// <summary>Same hue with lightness shifted by <paramref name="delta"/> (−1..1).</summary>
    public Rgb Lighten(double delta)
    {
        var (h, s, l) = ToHsl();
        return FromHsl(h, s, l + delta);
    }
}

/// <summary>
/// Colour helpers for vivid, content-coloured widget tiles: album-art palettes, readable tile colours and the
/// green → amber → red usage scale. Pure and unit tested; the WPF layer only turns the results into brushes.
/// </summary>
public static class TileColors
{
    public static readonly Rgb White = new(255, 255, 255);

    /// <summary>Minimum contrast of white text on a vivid tile (3:1 is WCAG AA for large/bold text).</summary>
    public const double MinWhiteContrast = 3.2;

    /// <summary>
    /// Makes a colour suitable as a tile background under white text: saturation boosted slightly, then darkened
    /// (same hue) until white reaches <see cref="MinWhiteContrast"/>.
    /// </summary>
    public static Rgb ForWhiteText(Rgb c, double saturationBoost = 1.12)
    {
        var (h, s, l) = c.ToHsl();
        s = Math.Clamp(s * saturationBoost + (s > 0.08 ? 0.04 : 0), 0, 0.92);
        var o = Rgb.FromHsl(h, s, l);
        int guard = 0;
        while (Rgb.Contrast(o, White) < MinWhiteContrast && guard++ < 60)
        {
            l -= 0.02;
            o = Rgb.FromHsl(h, s, l);
        }
        return o;
    }

    /// <summary>Usage colour: green up to 50 %, through amber at ~75 %, to red at 90 % and above.</summary>
    public static Rgb Usage(double percent)
    {
        var green = new Rgb(0x34, 0xD3, 0x59);
        var amber = new Rgb(0xFF, 0xB0, 0x20);
        var red = new Rgb(0xFF, 0x45, 0x3A);
        if (!double.IsFinite(percent)) return green;
        if (percent <= 50) return green;
        if (percent <= 75) return Rgb.Lerp(green, amber, (percent - 50) / 25);
        if (percent <= 90) return Rgb.Lerp(amber, red, (percent - 75) / 15);
        return red;
    }

    /// <summary>
    /// A three-stop diagonal gradient (top-left light → bottom-right deep) from up to three palette colours, all
    /// readable under white text.
    /// </summary>
    public static Rgb[] GradientFrom(IReadOnlyList<Rgb> palette)
    {
        if (palette.Count == 0) return [new(0x3A, 0x3F, 0x52), new(0x2A, 0x2E, 0x3E), new(0x1C, 0x1F, 0x2B)];
        var a = ForWhiteText(palette[0]);
        var b = palette.Count > 1 ? ForWhiteText(palette[1]) : ForWhiteText(palette[0].Lighten(-0.10));
        var top = ForWhiteText(a.Lighten(0.06));
        var mid = Rgb.Lerp(a, b, 0.5);
        var bottom = ForWhiteText(b.Lighten(-0.08));
        return [top, mid, bottom];
    }
}

/// <summary>
/// Dominant colours of an image (album art) by a small deterministic k-means over a downscaled pixel buffer.
/// Near-black, near-white and transparent pixels are ignored so the palette reflects the artwork, not its borders.
/// </summary>
public static class AlbumPalette
{
    /// <summary>
    /// Extracts up to <paramref name="count"/> dominant colours from a 32-bit BGRA buffer, most prominent first.
    /// Returns an empty list when the image has no usable colour (e.g. pure black and white).
    /// </summary>
    public static IReadOnlyList<Rgb> Extract(ReadOnlySpan<byte> bgra, int width, int height, int stride, int count = 3)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4) return [];
        count = Math.Clamp(count, 1, 5);

        // Sample at most ~48x48 points regardless of the input size.
        int stepX = Math.Max(1, width / 48), stepY = Math.Max(1, height / 48);
        var pts = new List<(double R, double G, double B, double W)>();
        for (int y = 0; y < height; y += stepY)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += stepX)
            {
                int i = row + x * 4;
                byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
                if (a < 128) continue;
                var (_, s, l) = new Rgb(r, g, b).ToHsl();
                if (l < 0.10 || l > 0.93) continue;           // near black / near white
                if (l > 0.82 && s < 0.25) continue;            // washed-out highlights
                // Colourful pixels count more, so a small vivid area beats a large grey one.
                double w = 0.25 + s * s * 1.6;
                pts.Add((r, g, b, w));
            }
        }
        if (pts.Count == 0) return [];

        int k = Math.Min(Math.Max(count + 1, 4), pts.Count);
        var centers = SeedCenters(pts, k);
        var assign = new int[pts.Count];
        for (int iter = 0; iter < 10; iter++)
        {
            bool changed = false;
            for (int i = 0; i < pts.Count; i++)
            {
                int best = Nearest(centers, pts[i]);
                if (best != assign[i] || iter == 0) { changed |= best != assign[i]; assign[i] = best; }
            }
            var sums = new (double R, double G, double B, double W)[k];
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                ref var s = ref sums[assign[i]];
                s.R += p.R * p.W; s.G += p.G * p.W; s.B += p.B * p.W; s.W += p.W;
            }
            for (int c = 0; c < k; c++)
                if (sums[c].W > 0) centers[c] = (sums[c].R / sums[c].W, sums[c].G / sums[c].W, sums[c].B / sums[c].W);
            if (!changed && iter > 0) break;
        }

        var weight = new double[k];
        for (int i = 0; i < pts.Count; i++) weight[assign[i]] += pts[i].W;
        var ranked = Enumerable.Range(0, k)
            .Where(c => weight[c] > 0)
            .Select(c => (Color: new Rgb(B8(centers[c].R), B8(centers[c].G), B8(centers[c].B)), Weight: weight[c]))
            .Select(x => (x.Color, Score: x.Weight * (0.6 + x.Color.ToHsl().S)))
            .OrderByDescending(x => x.Score)
            .ToList();

        var result = new List<Rgb>();
        foreach (var (c, _) in ranked)
        {
            if (result.All(r => Distance(r, c) > 42)) result.Add(c);
            if (result.Count == count) break;
        }
        return result;
    }

    private static byte B8(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    private static double Distance(Rgb a, Rgb b)
    {
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>Farthest-point seeding from the heaviest pixel: deterministic, so the same art gives the same palette.</summary>
    private static (double R, double G, double B)[] SeedCenters(List<(double R, double G, double B, double W)> pts, int k)
    {
        var centers = new (double R, double G, double B)[k];
        int first = 0;
        for (int i = 1; i < pts.Count; i++) if (pts[i].W > pts[first].W) first = i;
        centers[0] = (pts[first].R, pts[first].G, pts[first].B);
        var minD = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++) minD[i] = Sq(pts[i], centers[0]);
        for (int c = 1; c < k; c++)
        {
            int far = 0;
            double best = -1;
            for (int i = 0; i < pts.Count; i++)
            {
                double score = minD[i] * pts[i].W;
                if (score > best) { best = score; far = i; }
            }
            centers[c] = (pts[far].R, pts[far].G, pts[far].B);
            for (int i = 0; i < pts.Count; i++) minD[i] = Math.Min(minD[i], Sq(pts[i], centers[c]));
        }
        return centers;
    }

    private static int Nearest((double R, double G, double B)[] centers, (double R, double G, double B, double W) p)
    {
        int best = 0;
        double bd = double.MaxValue;
        for (int c = 0; c < centers.Length; c++)
        {
            double d = Sq(p, centers[c]);
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    private static double Sq((double R, double G, double B, double W) p, (double R, double G, double B) c)
    {
        double dr = p.R - c.R, dg = p.G - c.G, db = p.B - c.B;
        return dr * dr + dg * dg + db * db;
    }
}

/// <summary>Kinds of sky a weather tile can show.</summary>
public enum SkyKind
{
    ClearDay,
    GoldenHour,
    Sunset,
    Night,
    Cloudy,
    CloudyNight,
    Rain,
    Snow,
    Storm,
    Fog
}

/// <summary>Picks a sky gradient for a WMO weather code and the time of day (pure, unit tested).</summary>
public static class SkyGradient
{
    /// <summary>The sky for these conditions. Golden hour is the hour before sunset or after sunrise; the sunset
    /// glow is the 25 minutes either side of sunset (and of sunrise, as dawn).</summary>
    public static SkyKind Classify(int wmoCode, bool isDay, DateTime now, DateTime? sunrise = null, DateTime? sunset = null)
    {
        if (wmoCode is 95 or 96 or 99) return SkyKind.Storm;
        if (wmoCode is 71 or 73 or 75 or 77 or 85 or 86) return SkyKind.Snow;
        if (wmoCode is 51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82) return SkyKind.Rain;
        if (wmoCode is 45 or 48) return SkyKind.Fog;
        bool overcast = wmoCode == 3;

        if (sunrise is { } rise && sunset is { } set && set > rise)
        {
            double toSet = (now - set).TotalMinutes;
            double toRise = (now - rise).TotalMinutes;
            if (!overcast && (Math.Abs(toSet) <= 25 || Math.Abs(toRise) <= 25)) return SkyKind.Sunset;
            isDay = now > rise && now < set;
            if (!overcast && isDay && (toSet is > -60 and < -25 || toRise is > 25 and < 60)) return SkyKind.GoldenHour;
        }
        if (overcast) return isDay ? SkyKind.Cloudy : SkyKind.CloudyNight;
        return isDay ? SkyKind.ClearDay : SkyKind.Night;
    }

    /// <summary>Three stops, top to bottom, all dark enough for white text.</summary>
    public static Rgb[] Stops(SkyKind kind) => kind switch
    {
        SkyKind.ClearDay => [Rgb.FromHex("#3A8DFF"), Rgb.FromHex("#2F74E6"), Rgb.FromHex("#1F55C2")],
        SkyKind.GoldenHour => [Rgb.FromHex("#C9782A"), Rgb.FromHex("#B5612A"), Rgb.FromHex("#7E4A6E")],
        SkyKind.Sunset => [Rgb.FromHex("#D2553F"), Rgb.FromHex("#B8466A"), Rgb.FromHex("#5B3A8C")],
        SkyKind.Night => [Rgb.FromHex("#2A2F7A"), Rgb.FromHex("#1E2260"), Rgb.FromHex("#121540")],
        SkyKind.Cloudy => [Rgb.FromHex("#61748F"), Rgb.FromHex("#4F6079"), Rgb.FromHex("#3D4C63")],
        SkyKind.CloudyNight => [Rgb.FromHex("#3A4258"), Rgb.FromHex("#2D3447"), Rgb.FromHex("#202636")],
        SkyKind.Rain => [Rgb.FromHex("#4A5D73"), Rgb.FromHex("#3B4B60"), Rgb.FromHex("#2B384A")],
        SkyKind.Snow => [Rgb.FromHex("#5F8FB5"), Rgb.FromHex("#4C7AA3"), Rgb.FromHex("#3A6189")],
        SkyKind.Storm => [Rgb.FromHex("#5A3E8E"), Rgb.FromHex("#443073"), Rgb.FromHex("#2C2050")],
        SkyKind.Fog => [Rgb.FromHex("#6E7686"), Rgb.FromHex("#5A6272"), Rgb.FromHex("#464D5B")],
        _ => [Rgb.FromHex("#3A8DFF"), Rgb.FromHex("#2F74E6"), Rgb.FromHex("#1F55C2")]
    };

    /// <summary>Which icon a weather code draws.</summary>
    public static WeatherIconKind Icon(int wmoCode, bool isDay) => wmoCode switch
    {
        0 => isDay ? WeatherIconKind.Sun : WeatherIconKind.Moon,
        1 or 2 => isDay ? WeatherIconKind.SunCloud : WeatherIconKind.MoonCloud,
        3 => WeatherIconKind.Cloud,
        45 or 48 => WeatherIconKind.Fog,
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 => WeatherIconKind.Rain,
        80 or 81 or 82 => isDay ? WeatherIconKind.SunRain : WeatherIconKind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherIconKind.Snow,
        95 or 96 or 99 => WeatherIconKind.Storm,
        _ => WeatherIconKind.Cloud
    };
}

public enum WeatherIconKind { Sun, Moon, SunCloud, MoonCloud, Cloud, Rain, SunRain, Snow, Storm, Fog }

/// <summary>Generic per-service colours for the small app squares on counter widgets (colours only, no marks).</summary>
public static class ServiceColors
{
    public static Rgb For(string key) => key.ToLowerInvariant() switch
    {
        "stripe" or "stripe-revenue" => Rgb.FromHex("#635BFF"),
        "lemonsqueezy" or "lemonsqueezy-revenue" => Rgb.FromHex("#FFC233"),
        "gumroad" or "gumroad-sales" => Rgb.FromHex("#FF90E8"),
        "polar" or "polar-revenue" => Rgb.FromHex("#2F6BFF"),
        "paddle" or "paddle-revenue" => Rgb.FromHex("#FDDD35"),
        "github" or "github-activity" or "github-notifications" => Rgb.FromHex("#2B3137"),
        "linear" or "linear-issues" => Rgb.FromHex("#5E6AD2"),
        "resend" or "resend-emails" => Rgb.FromHex("#111111"),
        "plausible" or "plausible-visitors" => Rgb.FromHex("#5850EC"),
        "youtube" or "youtube-channel" => Rgb.FromHex("#FF2D2D"),
        "leetcode" or "leetcode-stats" => Rgb.FromHex("#FFA116"),
        "gmail" or "gmail-unread" => Rgb.FromHex("#EA4335"),
        "gcal" or "gcal-next" or "calendar" => Rgb.FromHex("#FF3B30"),
        "deepseek" or "deepseek-balance" => Rgb.FromHex("#4D6BFE"),
        "unsplash" or "unsplash-wallpaper" => Rgb.FromHex("#1A1A1A"),
        "crypto" => Rgb.FromHex("#F7931A"),
        "stock" or "stock-chart" or "stock-watchlist" => Rgb.FromHex("#0A84FF"),
        "screen-time" => Rgb.FromHex("#5E5CE6"),
        "x" or "social" => Rgb.FromHex("#14171A"),
        _ => Rgb.FromHex("#3A3F52")
    };

    /// <summary>True when a dark glyph reads better than a white one on this square.</summary>
    public static bool PrefersDarkGlyph(Rgb c) => Rgb.Contrast(c, TileColors.White) < 2.2;
}
