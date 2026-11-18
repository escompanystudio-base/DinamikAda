using System.Windows;
using System.Windows.Media;
using DinamikAda.Core.Config;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>Colours, brushes and fonts for one dock style. Brushes are frozen and safe to share.</summary>
public sealed class DockWidgetPalette
{
    public required DockVisualStyle Style { get; init; }
    public required bool IsLight { get; init; }

    // Dock capsule
    public required Brush PillBackground { get; init; }
    public required Color PillBorderColor { get; init; }

    // Tiles on the dock
    public required Brush TileBackground { get; init; }
    public required Brush TileHoverBackground { get; init; }
    public required Brush TileBorder { get; init; }
    public required Brush Foreground { get; init; }
    public required Brush SecondaryForeground { get; init; }
    public required Brush TertiaryForeground { get; init; }
    public required Brush Track { get; init; }

    // Detail cards
    public required Brush CardBackground { get; init; }
    public required Brush CardBorder { get; init; }
    public required Brush CardRowBackground { get; init; }

    // Semantic colours
    public required Color Accent { get; set; }
    public required Color Positive { get; init; }
    public required Color Negative { get; init; }
    public required Color Warning { get; init; }

    public Brush AccentBrush => Freeze(new SolidColorBrush(Accent));

    public FontFamily UiFont { get; init; } = new("Segoe UI Variable Display, Segoe UI");
    public FontFamily TextFont { get; init; } = new("Segoe UI Variable Text, Segoe UI");
    public FontFamily IconFont { get; init; } = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public FontFamily MonoFont { get; init; } = new("Cascadia Mono, Consolas");

    /// <summary>Colour for a load/usage percentage: accent below 60, warning to 85, negative above.</summary>
    public Color LoadColor(double percent) =>
        percent >= 85 ? Negative : percent >= 60 ? Warning : Accent;

    public static T Freeze<T>(T brush) where T : Freezable
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private static SolidColorBrush B(byte a, byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    public static DockWidgetPalette Create(DockVisualStyle style, Color accent)
    {
        return style switch
        {
            DockVisualStyle.Black => new DockWidgetPalette
            {
                Style = style,
                IsLight = false,
                PillBackground = B(0xFA, 0, 0, 0),
                PillBorderColor = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
                TileBackground = B(0x14, 0xFF, 0xFF, 0xFF),
                TileHoverBackground = B(0x22, 0xFF, 0xFF, 0xFF),
                TileBorder = B(0x18, 0xFF, 0xFF, 0xFF),
                Foreground = B(0xFF, 0xF5, 0xF5, 0xF7),
                SecondaryForeground = B(0xB0, 0xF5, 0xF5, 0xF7),
                TertiaryForeground = B(0x70, 0xF5, 0xF5, 0xF7),
                Track = B(0x2A, 0xFF, 0xFF, 0xFF),
                CardBackground = B(0xFF, 0x0A, 0x0A, 0x0C),
                CardBorder = B(0xFF, 0x2C, 0x2C, 0x30),
                CardRowBackground = B(0x10, 0xFF, 0xFF, 0xFF),
                Accent = accent,
                Positive = Color.FromRgb(0x30, 0xD1, 0x58),
                Negative = Color.FromRgb(0xFF, 0x45, 0x3A),
                Warning = Color.FromRgb(0xFF, 0x9F, 0x0A)
            },
            DockVisualStyle.White => new DockWidgetPalette
            {
                Style = style,
                IsLight = true,
                PillBackground = B(0xF2, 0xF7, 0xF7, 0xF9),
                PillBorderColor = Color.FromArgb(0x30, 0x00, 0x00, 0x00),
                TileBackground = B(0x0E, 0x00, 0x00, 0x00),
                TileHoverBackground = B(0x1A, 0x00, 0x00, 0x00),
                TileBorder = B(0x14, 0x00, 0x00, 0x00),
                Foreground = B(0xFF, 0x1C, 0x1C, 0x1E),
                SecondaryForeground = B(0xB0, 0x1C, 0x1C, 0x1E),
                TertiaryForeground = B(0x78, 0x1C, 0x1C, 0x1E),
                Track = B(0x1E, 0x00, 0x00, 0x00),
                CardBackground = B(0xFF, 0xFB, 0xFB, 0xFD),
                CardBorder = B(0xFF, 0xD8, 0xD8, 0xDE),
                CardRowBackground = B(0x0C, 0x00, 0x00, 0x00),
                Accent = DarkenForLight(accent),
                Positive = Color.FromRgb(0x24, 0x9A, 0x41),
                Negative = Color.FromRgb(0xD7, 0x2C, 0x22),
                Warning = Color.FromRgb(0xC9, 0x6A, 0x00)
            },
            DockVisualStyle.LiquidGlass => new DockWidgetPalette
            {
                Style = style,
                IsLight = false,
                PillBackground = Freeze(new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new(Color.FromArgb(0xB8, 0x2A, 0x30, 0x3C), 0.0),
                        new(Color.FromArgb(0xA6, 0x12, 0x16, 0x20), 0.55),
                        new(Color.FromArgb(0xC0, 0x1C, 0x22, 0x30), 1.0)
                    }, 90)),
                PillBorderColor = Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF),
                TileBackground = B(0x1C, 0xFF, 0xFF, 0xFF),
                TileHoverBackground = B(0x2C, 0xFF, 0xFF, 0xFF),
                TileBorder = B(0x30, 0xFF, 0xFF, 0xFF),
                Foreground = B(0xFF, 0xFF, 0xFF, 0xFF),
                SecondaryForeground = B(0xC0, 0xFF, 0xFF, 0xFF),
                TertiaryForeground = B(0x80, 0xFF, 0xFF, 0xFF),
                Track = B(0x30, 0xFF, 0xFF, 0xFF),
                CardBackground = B(0xF4, 0x16, 0x1A, 0x24),
                CardBorder = B(0xFF, 0x46, 0x50, 0x64),
                CardRowBackground = B(0x14, 0xFF, 0xFF, 0xFF),
                Accent = accent,
                Positive = Color.FromRgb(0x32, 0xD7, 0x4B),
                Negative = Color.FromRgb(0xFF, 0x45, 0x3A),
                Warning = Color.FromRgb(0xFF, 0xB3, 0x40)
            },
            DockVisualStyle.IosGlass => new DockWidgetPalette
            {
                Style = style,
                IsLight = false,
                PillBackground = B(0x9C, 0x5C, 0x60, 0x6E),
                PillBorderColor = Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF),
                TileBackground = B(0x26, 0xFF, 0xFF, 0xFF),
                TileHoverBackground = B(0x38, 0xFF, 0xFF, 0xFF),
                TileBorder = B(0x22, 0xFF, 0xFF, 0xFF),
                Foreground = B(0xFF, 0xFF, 0xFF, 0xFF),
                SecondaryForeground = B(0xD0, 0xFF, 0xFF, 0xFF),
                TertiaryForeground = B(0x90, 0xFF, 0xFF, 0xFF),
                Track = B(0x38, 0xFF, 0xFF, 0xFF),
                CardBackground = B(0xF6, 0x3A, 0x3E, 0x4A),
                CardBorder = B(0xFF, 0x62, 0x68, 0x78),
                CardRowBackground = B(0x1A, 0xFF, 0xFF, 0xFF),
                Accent = accent,
                Positive = Color.FromRgb(0x34, 0xC7, 0x59),
                Negative = Color.FromRgb(0xFF, 0x3B, 0x30),
                Warning = Color.FromRgb(0xFF, 0x95, 0x00)
            },
            _ => new DockWidgetPalette
            {
                Style = DockVisualStyle.Midnight,
                IsLight = false,
                PillBackground = B(0xEF, 0x06, 0x08, 0x0E),
                PillBorderColor = Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF),
                TileBackground = B(0x0E, 0xFF, 0xFF, 0xFF),
                TileHoverBackground = B(0x18, 0xFF, 0xFF, 0xFF),
                TileBorder = B(0x16, 0xFF, 0xFF, 0xFF),
                Foreground = B(0xFF, 0xE4, 0xE4, 0xE7),
                SecondaryForeground = B(0xFF, 0x9C, 0xA3, 0xAF),
                TertiaryForeground = B(0xFF, 0x6B, 0x72, 0x80),
                Track = B(0x22, 0xFF, 0xFF, 0xFF),
                CardBackground = B(0xFF, 0x10, 0x15, 0x20),
                CardBorder = B(0xFF, 0x35, 0x41, 0x58),
                CardRowBackground = B(0x0C, 0xFF, 0xFF, 0xFF),
                Accent = accent,
                Positive = Color.FromRgb(0x30, 0xD1, 0x58),
                Negative = Color.FromRgb(0xFF, 0x45, 0x3A),
                Warning = Color.FromRgb(0xFF, 0x9F, 0x0A)
            }
        };
    }

    private static Color DarkenForLight(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        if (lum < 0.55) return c;
        double k = 0.55 / lum;
        return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }
}
