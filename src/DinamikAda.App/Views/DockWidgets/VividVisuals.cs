using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DinamikAda.Core.Widgets;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>
/// Building blocks for vivid, content-coloured tiles: gradient bodies, white-on-colour text, big bold values with
/// tiny grey labels, glossy app squares and calendar pages. Brushes are frozen.
/// </summary>
public static class Vivid
{
    public static readonly SolidColorBrush OnColor = W.Brush(Colors.White);
    public static readonly SolidColorBrush OnColorSecondary = W.Brush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush OnColorTertiary = W.Brush(Color.FromArgb(0x96, 0xFF, 0xFF, 0xFF));
    public static readonly SolidColorBrush OnColorTrack = W.Brush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    public static Color ToColor(Rgb c, byte alpha = 0xFF) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>A diagonal (top-left → bottom-right) gradient body from three stops.</summary>
    public static Brush Gradient(IReadOnlyList<Rgb> stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i < stops.Count; i++)
            b.GradientStops.Add(new GradientStop(ToColor(stops[i]), stops.Count == 1 ? 0 : (double)i / (stops.Count - 1)));
        return DockWidgetPalette.Freeze(b);
    }

    /// <summary>A vertical sky gradient (top → bottom), slightly angled so it reads as light, not a band.</summary>
    public static Brush Sky(IReadOnlyList<Rgb> stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0.3, 0), EndPoint = new Point(0.7, 1) };
        for (int i = 0; i < stops.Count; i++)
            b.GradientStops.Add(new GradientStop(ToColor(stops[i]), stops.Count == 1 ? 0 : (double)i / (stops.Count - 1)));
        return DockWidgetPalette.Freeze(b);
    }

    /// <summary>Solid accent body with a gentle vertical sheen (battery, quick controls).</summary>
    public static Brush Solid(Color c)
    {
        var rgb = new Rgb(c.R, c.G, c.B);
        var b = new LinearGradientBrush(ToColor(rgb.Lighten(0.06)), ToColor(rgb.Lighten(-0.06)), 90);
        return DockWidgetPalette.Freeze(b);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DockWidgetPalette, DockWidgetPalette> OnColorPalettes = new();

    /// <summary>
    /// The palette for content drawn on a coloured tile body: white text in three strengths, white tracks, and a
    /// warm accent, whatever the dock style.
    /// </summary>
    public static DockWidgetPalette On(DockWidgetPalette p) => OnColorPalettes.GetValue(p, src => new DockWidgetPalette
    {
        Style = src.Style,
        IsLight = false,
        PillBackground = src.PillBackground,
        PillBorderColor = src.PillBorderColor,
        TileBackground = W.Brush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        TileHoverBackground = W.Brush(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF)),
        TileBorder = W.Brush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        Foreground = OnColor,
        SecondaryForeground = OnColorSecondary,
        TertiaryForeground = OnColorTertiary,
        Track = OnColorTrack,
        CardBackground = src.CardBackground,
        CardBorder = src.CardBorder,
        CardRowBackground = src.CardRowBackground,
        Accent = Color.FromRgb(0xFF, 0xD6, 0x5C),
        Positive = Color.FromRgb(0x7D, 0xF5, 0x9A),
        Negative = Color.FromRgb(0xFF, 0x9A, 0x8E),
        Warning = Color.FromRgb(0xFF, 0xD0, 0x6A),
        UiFont = src.UiFont,
        TextFont = src.TextFont,
        IconFont = src.IconFont,
        MonoFont = src.MonoFont
    });

    /// <summary>Value font size: ≈0.46 × tile height.</summary>
    public static double ValueSize(double h) => Math.Max(11, Math.Round(h * 0.46 * 2) / 2);
    /// <summary>Label font size: ≈0.24 × tile height.</summary>
    public static double LabelSize(double h) => Math.Max(7.5, Math.Round(h * 0.24 * 2) / 2);

    /// <summary>Big bold value ("$8,421", "12.5K").</summary>
    public static TextBlock Value(DockWidgetPalette p, string text, double h, Brush? brush = null, double scale = 1.0)
    {
        var t = W.Text(p, text, Math.Max(10, ValueSize(h) * scale), brush ?? p.Foreground, FontWeights.Bold);
        t.LineHeight = t.FontSize * 1.02;
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    /// <summary>Tiny grey label under a value ("Revenue", "followers").</summary>
    public static TextBlock Label(DockWidgetPalette p, string text, double h, Brush? brush = null)
    {
        var t = W.Text(p, text, LabelSize(h), brush ?? p.SecondaryForeground, FontWeights.Medium, ui: false);
        t.LineHeight = t.FontSize * 1.1;
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        return t;
    }

    /// <summary>
    /// A small glossy rounded square in a service colour with a white (or dark, on light colours) glyph: the app
    /// icon on the left of a counter widget.
    /// </summary>
    public static Border AppSquare(DockWidgetPalette p, Rgb color, string glyph, double size)
    {
        bool dark = ServiceColors.PrefersDarkGlyph(color);
        var fg = dark ? W.Brush(Color.FromRgb(0x1A, 0x1A, 0x1E)) : OnColor;
        var body = new LinearGradientBrush(ToColor(color.Lighten(0.07)), ToColor(color.Lighten(-0.07)), 90);
        body.Freeze();
        var radius = new CornerRadius(Math.Round(size * 0.28));
        var grid = new Grid();
        grid.Children.Add(new Border { CornerRadius = radius, Background = body });
        grid.Children.Add(new Border { CornerRadius = radius, Background = SquareGloss });
        grid.Children.Add(new Border { CornerRadius = radius, BorderBrush = SquareRim, BorderThickness = new Thickness(0.7) });
        grid.Children.Add(W.Glyph(p, glyph, Math.Round(size * 0.5), fg));
        return new Border
        {
            Width = size,
            Height = size,
            Child = grid,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
    }

    private static readonly Brush SquareGloss = DockWidgetPalette.Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.5),
            new(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0.7),
            new(Color.FromArgb(0x18, 0x00, 0x00, 0x00), 1.0)
        }, 90));
    private static readonly Brush SquareRim = DockWidgetPalette.Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));

    /// <summary>
    /// Icon square + value/label column: the standard counter layout (no box). Returns the grid and the two texts.
    /// </summary>
    public static (Grid Root, TextBlock Value, TextBlock Label) Counter(DockWidgetPalette p, double h, Rgb color, string glyph,
        string value = "—", string label = "")
    {
        double sq = Math.Round(h * 0.78);
        var square = AppSquare(p, color, glyph, sq);
        var v = Value(p, value, h);
        var l = Label(p, label, h);
        var col = W.Column(v, l);
        col.Margin = new Thickness(Math.Round(h * 0.2), 0, 0, 0);
        var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(square);
        Grid.SetColumn(col, 1);
        g.Children.Add(col);
        return (g, v, l);
    }

    /// <summary>A small calendar page: red weekday strip with a big day number under it.</summary>
    public static Border CalendarPage(DockWidgetPalette p, double size, out TextBlock weekday, out TextBlock day)
    {
        double strip = Math.Round(size * 0.3);
        var radius = Math.Round(size * 0.24);
        weekday = W.Text(p, "", Math.Max(6, Math.Round(size * 0.2)), OnColor, FontWeights.Bold);
        weekday.HorizontalAlignment = HorizontalAlignment.Center;
        day = W.Text(p, "", Math.Max(9, Math.Round(size * 0.48)), W.Brush(Color.FromRgb(0x1C, 0x1C, 0x1E)), FontWeights.Bold);
        day.HorizontalAlignment = HorizontalAlignment.Center;
        day.Margin = new Thickness(0, -size * 0.03, 0, 0);
        var top = new Border
        {
            Height = strip,
            CornerRadius = new CornerRadius(radius, radius, 0, 0),
            Background = DockWidgetPalette.Freeze(new LinearGradientBrush(Color.FromRgb(0xFF, 0x52, 0x47), Color.FromRgb(0xE5, 0x2E, 0x24), 90)),
            Child = weekday
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(top);
        Grid.SetRow(day, 1);
        grid.Children.Add(day);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = DockWidgetPalette.Freeze(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xE9, 0xE9, 0xEE), 90)),
            Child = grid,
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}

/// <summary>
/// Album-art colours for media tiles: decodes the thumbnail to a tiny bitmap, extracts its palette in Core
/// (<see cref="AlbumPalette"/>) and turns it into a readable gradient. Cached per thumbnail reference.
/// </summary>
public sealed class AlbumColors
{
    private byte[]? _bytes;
    private string? _accentKey;
    private Brush? _brush;
    private Rgb[]? _stops;

    /// <summary>Gradient for this track: from the art when present, else from the media accent colours.</summary>
    public (Brush Brush, Rgb[] Stops) Get(byte[]? thumbnail, string? accentHex, string? secondaryHex = null)
    {
        if (thumbnail is { Length: > 0 })
        {
            if (ReferenceEquals(thumbnail, _bytes) && _brush is not null) return (_brush, _stops!);
            var pal = Decode(thumbnail);
            if (pal.Count > 0)
            {
                _bytes = thumbnail;
                _accentKey = null;
                _stops = TileColors.GradientFrom(pal);
                _brush = Vivid.Gradient(_stops);
                return (_brush, _stops);
            }
        }
        string key = (accentHex ?? "") + "|" + (secondaryHex ?? "");
        if (_bytes is null && key == _accentKey && _brush is not null) return (_brush, _stops!);
        _bytes = null;
        _accentKey = key;
        var colors = new List<Rgb>();
        if (TryHex(accentHex, out var a)) colors.Add(a);
        if (TryHex(secondaryHex, out var b)) colors.Add(b);
        else if (colors.Count == 1) colors.Add(Rgb.FromHsl(colors[0].ToHsl().H + 28, colors[0].ToHsl().S, colors[0].ToHsl().L * 0.8));
        _stops = TileColors.GradientFrom(colors);
        _brush = Vivid.Gradient(_stops);
        return (_brush, _stops);
    }

    private static bool TryHex(string? hex, out Rgb c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { c = Rgb.FromHex(hex); return true; }
        catch (FormatException) { return false; }
    }

    private static IReadOnlyList<Rgb> Decode(byte[] bytes)
    {
        try
        {
            using var ms = new System.IO.MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 40;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var buf = new byte[stride * h];
            conv.CopyPixels(buf, stride, 0);
            return AlbumPalette.Extract(buf, w, h, stride);
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// A glossy, slightly 3D weather icon drawn with geometry and gradients: a glowing sun, puffy layered clouds,
/// raindrops, snowflakes, a bolt or a crescent moon. It may draw a little outside its box (the glow and the cloud
/// overhang), so tiles can let it overflow their top edge.
/// </summary>
public sealed class WeatherIcon : FrameworkElement
{
    private WeatherIconKind _kind = WeatherIconKind.SunCloud;

    public WeatherIcon(double size, WeatherIconKind kind = WeatherIconKind.SunCloud)
    {
        Width = size;
        Height = size;
        _kind = kind;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public WeatherIconKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            InvalidateVisual();
        }
    }

    // ---- Shared brushes (unit-box gradients so they scale with each shape) ----------------------

    private static readonly Brush SunGlow = Frozen(new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x8C, 0xFF, 0xC8, 0x3A), 0.0),
            new GradientStop(Color.FromArgb(0x38, 0xFF, 0xA8, 0x20), 0.55),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0x90, 0x10), 1.0)
        }
    });
    private static readonly Brush SunDisc = Frozen(new RadialGradientBrush
    {
        GradientOrigin = new Point(0.35, 0.28),
        Center = new Point(0.42, 0.38),
        RadiusX = 0.7,
        RadiusY = 0.7,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xFF, 0xF7, 0xB8), 0.0),
            new GradientStop(Color.FromRgb(0xFF, 0xD2, 0x2E), 0.45),
            new GradientStop(Color.FromRgb(0xFF, 0x9A, 0x0A), 1.0)
        }
    });
    private static readonly Brush MoonDisc = Frozen(new RadialGradientBrush
    {
        GradientOrigin = new Point(0.3, 0.3),
        Center = new Point(0.4, 0.4),
        RadiusX = 0.75,
        RadiusY = 0.75,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xFF, 0xFB, 0xE6), 0.0),
            new GradientStop(Color.FromRgb(0xFF, 0xE7, 0x9A), 0.5),
            new GradientStop(Color.FromRgb(0xE8, 0xBE, 0x52), 1.0)
        }
    });
    private static readonly Brush MoonGlow = Frozen(new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x50, 0xFF, 0xF1, 0xB8), 0.0),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xF1, 0xB8), 1.0)
        }
    });
    private static readonly Brush CloudLight = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xFF, 0xFF, 0xFF), 0.0),
            new(Color.FromRgb(0xF1, 0xF5, 0xFB), 0.5),
            new(Color.FromRgb(0xC9, 0xD5, 0xE6), 1.0)
        }, 90));
    private static readonly Brush CloudBack = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xE2, 0xE9, 0xF3), 0.0),
            new(Color.FromRgb(0xAE, 0xBC, 0xD1), 1.0)
        }, 90));
    private static readonly Brush CloudRain = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xF2, 0xF5, 0xFA), 0.0),
            new(Color.FromRgb(0xC4, 0xCD, 0xDB), 0.55),
            new(Color.FromRgb(0x95, 0xA3, 0xB8), 1.0)
        }, 90));
    private static readonly Brush CloudStorm = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xD9, 0xDB, 0xE8), 0.0),
            new(Color.FromRgb(0x9C, 0x9F, 0xB8), 0.55),
            new(Color.FromRgb(0x6E, 0x71, 0x8E), 1.0)
        }, 90));
    private static readonly Brush CloudShadow = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x0A, 0x14, 0x30)));
    private static readonly Brush Specular = Frozen(new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), 0.0),
            new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0)
        }
    });
    private static readonly Brush DropFill = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0x9B, 0xE2, 0xFF), 0.0),
            new(Color.FromRgb(0x3A, 0x9C, 0xFF), 0.6),
            new(Color.FromRgb(0x1E, 0x6C, 0xF0), 1.0)
        }, 90));
    private static readonly Brush Bolt = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xFF, 0xF2, 0x8A), 0.0),
            new(Color.FromRgb(0xFF, 0xC4, 0x1E), 0.6),
            new(Color.FromRgb(0xFF, 0x96, 0x00), 1.0)
        }, 90));
    private static readonly Brush Flake = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xFB, 0xFF)));
    private static readonly Brush FogBar = Frozen(new LinearGradientBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x90, 0xE4, 0xEC, 0xF6), 0));

    private static T Frozen<T>(T b) where T : Freezable
    {
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s < 4) return;
        double ox = (ActualWidth - s) / 2, oy = (ActualHeight - s) / 2;
        dc.PushTransform(new TranslateTransform(ox, oy));
        switch (_kind)
        {
            case WeatherIconKind.Sun:
                DrawSun(dc, s * 0.5, s * 0.5, s * 0.30, s);
                break;
            case WeatherIconKind.Moon:
                DrawMoon(dc, s * 0.5, s * 0.48, s * 0.30);
                break;
            case WeatherIconKind.SunCloud:
                DrawSun(dc, s * 0.36, s * 0.36, s * 0.24, s);
                DrawCloud(dc, s * 0.56, s * 0.62, s * 0.78, CloudLight, back: true);
                break;
            case WeatherIconKind.MoonCloud:
                DrawMoon(dc, s * 0.38, s * 0.34, s * 0.23);
                DrawCloud(dc, s * 0.56, s * 0.62, s * 0.78, CloudLight, back: true);
                break;
            case WeatherIconKind.Cloud:
                DrawCloud(dc, s * 0.5, s * 0.56, s * 0.9, CloudLight, back: true);
                break;
            case WeatherIconKind.Rain:
                DrawDrops(dc, s, 3);
                DrawCloud(dc, s * 0.5, s * 0.46, s * 0.88, CloudRain, back: false);
                break;
            case WeatherIconKind.SunRain:
                DrawSun(dc, s * 0.34, s * 0.30, s * 0.21, s);
                DrawDrops(dc, s, 2);
                DrawCloud(dc, s * 0.56, s * 0.50, s * 0.8, CloudRain, back: false);
                break;
            case WeatherIconKind.Snow:
                DrawFlakes(dc, s);
                DrawCloud(dc, s * 0.5, s * 0.46, s * 0.88, CloudLight, back: false);
                break;
            case WeatherIconKind.Storm:
                DrawBolt(dc, s);
                DrawCloud(dc, s * 0.5, s * 0.44, s * 0.88, CloudStorm, back: false);
                break;
            case WeatherIconKind.Fog:
                DrawCloud(dc, s * 0.5, s * 0.42, s * 0.84, CloudLight, back: false);
                for (int i = 0; i < 2; i++)
                {
                    double y = s * (0.74 + i * 0.13), w = s * (i == 0 ? 0.7 : 0.52);
                    dc.DrawRoundedRectangle(FogBar, null, new Rect(s * 0.5 - w / 2 + (i == 0 ? 0 : s * 0.06), y, w, s * 0.075), s * 0.04, s * 0.04);
                }
                break;
        }
        dc.Pop();
    }

    private static void DrawSun(DrawingContext dc, double cx, double cy, double r, double s)
    {
        dc.DrawEllipse(SunGlow, null, new Point(cx, cy), r * 1.85, r * 1.85);
        dc.DrawEllipse(SunDisc, null, new Point(cx, cy), r, r);
        dc.DrawEllipse(Specular, null, new Point(cx - r * 0.32, cy - r * 0.38), r * 0.42, r * 0.28);
    }

    private static void DrawMoon(DrawingContext dc, double cx, double cy, double r)
    {
        dc.DrawEllipse(MoonGlow, null, new Point(cx, cy), r * 1.8, r * 1.8);
        var crescent = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx, cy), r, r),
            new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.32), r * 0.86, r * 0.86));
        crescent.Freeze();
        dc.DrawGeometry(MoonDisc, null, crescent);
        dc.DrawEllipse(Specular, null, new Point(cx - r * 0.45, cy - r * 0.1), r * 0.22, r * 0.32);
    }

    /// <summary>Puffy cloud centred at (cx, cy) on its baseline-ish middle, <paramref name="w"/> wide.</summary>
    private static Geometry CloudShape(double cx, double cy, double w)
    {
        double h = w * 0.30;
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new RectangleGeometry(new Rect(cx - w * 0.44, cy - h * 0.15, w * 0.88, h), h / 2, h / 2));
        g.Children.Add(new EllipseGeometry(new Point(cx - w * 0.20, cy - h * 0.05), w * 0.20, w * 0.20));
        g.Children.Add(new EllipseGeometry(new Point(cx + w * 0.05, cy - h * 0.42), w * 0.27, w * 0.27));
        g.Children.Add(new EllipseGeometry(new Point(cx + w * 0.27, cy + h * 0.05), w * 0.17, w * 0.17));
        g.Freeze();
        return g;
    }

    private static void DrawCloud(DrawingContext dc, double cx, double cy, double w, Brush fill, bool back)
    {
        if (back)
        {
            // A second, darker puff behind for depth.
            var behind = CloudShape(cx + w * 0.16, cy - w * 0.12, w * 0.62);
            dc.DrawGeometry(CloudBack, null, behind);
        }
        var shape = CloudShape(cx, cy, w);
        dc.PushTransform(new TranslateTransform(0, w * 0.045));
        dc.DrawGeometry(CloudShadow, null, shape);
        dc.Pop();
        dc.DrawGeometry(fill, null, shape);
        // Specular highlight on the big top puff, plus a soft rim light along the top.
        dc.DrawEllipse(Specular, null, new Point(cx - w * 0.02, cy - w * 0.25), w * 0.17, w * 0.09);
        dc.DrawEllipse(Specular, null, new Point(cx - w * 0.25, cy - w * 0.08), w * 0.08, w * 0.045);
    }

    private static void DrawDrops(DrawingContext dc, double s, int count)
    {
        double y = s * 0.66;
        for (int i = 0; i < count; i++)
        {
            double x = s * (count == 3 ? 0.32 + i * 0.18 : 0.46 + i * 0.2);
            double yy = y + (i % 2 == 0 ? 0 : s * 0.08);
            double r = s * 0.055;
            var drop = new StreamGeometry();
            using (var c = drop.Open())
            {
                c.BeginFigure(new Point(x + r * 0.5, yy - r * 2.2), true, true);
                c.BezierTo(new Point(x + r * 1.3, yy - r * 0.6), new Point(x + r * 1.1, yy + r), new Point(x, yy + r), true, true);
                c.BezierTo(new Point(x - r * 1.1, yy + r), new Point(x - r * 1.0, yy - r * 0.2), new Point(x + r * 0.5, yy - r * 2.2), true, true);
            }
            drop.Freeze();
            dc.DrawGeometry(DropFill, null, drop);
            dc.DrawEllipse(Specular, null, new Point(x - r * 0.3, yy - r * 0.1), r * 0.3, r * 0.45);
        }
    }

    private static void DrawFlakes(DrawingContext dc, double s)
    {
        var pen = new Pen(Flake, Math.Max(1, s * 0.035)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        (double X, double Y, double R)[] flakes = [(0.32, 0.76, 0.07), (0.52, 0.84, 0.06), (0.70, 0.74, 0.07)];
        foreach (var (fx, fy, fr) in flakes)
        {
            var c = new Point(s * fx, s * fy);
            double r = s * fr;
            for (int k = 0; k < 3; k++)
            {
                double a = Math.PI / 3 * k;
                dc.DrawLine(pen, new Point(c.X - Math.Cos(a) * r, c.Y - Math.Sin(a) * r), new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r));
            }
        }
    }

    private static void DrawBolt(DrawingContext dc, double s)
    {
        var bolt = new StreamGeometry();
        using (var c = bolt.Open())
        {
            c.BeginFigure(new Point(s * 0.54, s * 0.52), true, true);
            c.LineTo(new Point(s * 0.38, s * 0.76), true, true);
            c.LineTo(new Point(s * 0.50, s * 0.76), true, true);
            c.LineTo(new Point(s * 0.42, s * 0.96), true, true);
            c.LineTo(new Point(s * 0.66, s * 0.68), true, true);
            c.LineTo(new Point(s * 0.54, s * 0.68), true, true);
            c.LineTo(new Point(s * 0.62, s * 0.52), true, true);
        }
        bolt.Freeze();
        dc.DrawGeometry(Bolt, null, bolt);
    }
}

/// <summary>
/// A generic provider badge for AI plans: a coloured rounded square with a simple geometric mark (our own marks,
/// not any product's logo).
/// </summary>
public sealed class ProviderBadge : FrameworkElement
{
    private enum Mark { Burst, Knot, Sparkle, Prism, Loop, Wave, Letter }

    private readonly Color _fill;
    private readonly Color _ink;
    private readonly Mark _mark;
    private readonly string _letter;

    public ProviderBadge(string provider, string label, double size)
    {
        Width = size;
        Height = size;
        IsHitTestVisible = false;
        string k = (provider + " " + label).ToLowerInvariant();
        (_fill, _ink, _mark) = k switch
        {
            _ when k.Contains("claude") || k.Contains("anthropic") => (Color.FromRgb(0xD9, 0x77, 0x57), Colors.White, Mark.Burst),
            _ when k.Contains("openai") || k.Contains("chatgpt") || k.Contains("codex") || k.Contains("gpt") => (Color.FromRgb(0xF4, 0xF4, 0xF6), Color.FromRgb(0x14, 0x14, 0x18), Mark.Knot),
            _ when k.Contains("gemini") || k.Contains("google") => (Color.FromRgb(0x3B, 0x7B, 0xF6), Colors.White, Mark.Sparkle),
            _ when k.Contains("cursor") => (Color.FromRgb(0x1E, 0x1F, 0x24), Colors.White, Mark.Prism),
            _ when k.Contains("copilot") || k.Contains("github") => (Color.FromRgb(0x8A, 0x4B, 0xF0), Colors.White, Mark.Loop),
            _ when k.Contains("deepseek") || k.Contains("mistral") => (Color.FromRgb(0x4D, 0x6B, 0xFE), Colors.White, Mark.Wave),
            _ => (Color.FromRgb(0x5E, 0x5C, 0xE6), Colors.White, Mark.Letter)
        };
        _letter = string.IsNullOrWhiteSpace(label) ? "A" : label.Trim()[..1].ToUpperInvariant();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s < 4) return;
        var rect = new Rect(0, 0, s, s);
        double r = s * 0.28;
        var top = new Rgb(_fill.R, _fill.G, _fill.B);
        var body = new LinearGradientBrush(Vivid.ToColor(top.Lighten(0.06)), Vivid.ToColor(top.Lighten(-0.06)), 90);
        body.Freeze();
        dc.DrawRoundedRectangle(body, null, rect, r, r);
        var gloss = new LinearGradientBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), new Point(0, 0), new Point(0, 0.55));
        gloss.Freeze();
        dc.DrawRoundedRectangle(gloss, null, rect, r, r);

        var ink = new SolidColorBrush(_ink);
        ink.Freeze();
        var c = new Point(s / 2, s / 2);
        double m = s * 0.3;
        var pen = new Pen(ink, Math.Max(1.2, s * 0.085)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        switch (_mark)
        {
            case Mark.Burst:
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI / 4 * i + Math.PI / 8;
                    double inner = m * 0.22, outer = i % 2 == 0 ? m : m * 0.78;
                    dc.DrawLine(pen, new Point(c.X + Math.Cos(a) * inner, c.Y + Math.Sin(a) * inner), new Point(c.X + Math.Cos(a) * outer, c.Y + Math.Sin(a) * outer));
                }
                break;
            case Mark.Knot:
            {
                var thin = new Pen(ink, Math.Max(1, s * 0.07));
                thin.Freeze();
                for (int i = 0; i < 3; i++)
                {
                    var e = new EllipseGeometry(c, m * 0.95, m * 0.42) { Transform = new RotateTransform(60 * i, c.X, c.Y) };
                    e.Freeze();
                    dc.DrawGeometry(null, thin, e);
                }
                break;
            }
            case Mark.Sparkle:
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X, c.Y - m), true, true);
                    ctx.QuadraticBezierTo(c, new Point(c.X + m, c.Y), true, true);
                    ctx.QuadraticBezierTo(c, new Point(c.X, c.Y + m), true, true);
                    ctx.QuadraticBezierTo(c, new Point(c.X - m, c.Y), true, true);
                    ctx.QuadraticBezierTo(c, new Point(c.X, c.Y - m), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(ink, null, g);
                break;
            }
            case Mark.Prism:
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X, c.Y - m), false, true);
                    ctx.LineTo(new Point(c.X + m * 0.9, c.Y + m * 0.6), true, true);
                    ctx.LineTo(new Point(c.X - m * 0.9, c.Y + m * 0.6), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
                break;
            }
            case Mark.Loop:
                dc.DrawEllipse(null, pen, c, m * 0.8, m * 0.8);
                dc.DrawEllipse(ink, null, new Point(c.X + m * 0.15, c.Y - m * 0.1), m * 0.22, m * 0.22);
                break;
            case Mark.Wave:
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(c.X - m, c.Y + m * 0.1), false, false);
                    ctx.BezierTo(new Point(c.X - m * 0.5, c.Y - m * 0.7), new Point(c.X, c.Y + m * 0.7), new Point(c.X + m * 0.45, c.Y - m * 0.15), true, true);
                    ctx.LineTo(new Point(c.X + m, c.Y - m * 0.5), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
                break;
            }
            default:
            {
                var tf = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var ft = new FormattedText(_letter, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, s * 0.52, ink,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
                break;
            }
        }
    }
}
