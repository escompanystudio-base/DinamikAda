using DinamikAda.Platform.Win32.Windowing;
using Xunit;

namespace DinamikAda.Tests;

public sealed class GlassAndMagnificationTests
{
    private static readonly double[] Centers = [23.0, 69.0, 115.0, 161.0, 207.0, 253.0, 299.0];
    private const double Pitch = 46.0;

    [Fact]
    public void GaussianScales_PeakUnderCursor_FallOffSmoothly_AndRestAtOne()
    {
        var idle = DockLayoutCalculator.ComputeGaussianMagnificationScales(null, Centers, Pitch);
        Assert.All(idle, s => Assert.Equal(1.0, s));

        var s = DockLayoutCalculator.ComputeGaussianMagnificationScales(161.0, Centers, Pitch, maxScale: 1.85);
        Assert.Equal(1.85, s[3], precision: 6);
        Assert.InRange(s[2], 1.38, 1.55);
        Assert.InRange(s[1], 1.03, 1.15);
        Assert.Equal(s[2], s[4], precision: 9);
        Assert.Equal(1.0, s[0], precision: 2);

        // Continuous as the cursor glides: no steps between neighbouring cursor positions.
        var a = DockLayoutCalculator.ComputeGaussianMagnificationScales(140.0, Centers, Pitch);
        var b = DockLayoutCalculator.ComputeGaussianMagnificationScales(140.5, Centers, Pitch);
        for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i]) < 0.02);
    }

    [Fact]
    public void Spread_KeepsPointUnderCursorFixed_AndSlotsFromOverlapping()
    {
        double cursor = 161.0;
        var scales = DockLayoutCalculator.ComputeGaussianMagnificationScales(cursor, Centers, Pitch);
        double anchor = DockLayoutCalculator.ComputeMagnificationSpreadBefore(cursor, Centers, scales, Pitch);
        double total = DockLayoutCalculator.ComputeMagnificationSpreadBefore(double.MaxValue, Centers, scales, Pitch);
        Assert.True(anchor > 0 && total > anchor);

        double Shift(double x) => DockLayoutCalculator.ComputeMagnificationSpreadBefore(x, Centers, scales, Pitch) - anchor;
        Assert.Equal(0.0, Shift(cursor), precision: 9);
        Assert.True(Shift(Centers[0]) < 0 && Shift(Centers[^1]) > 0);

        // Adjacent magnified slots exactly touch: centre distance = half their widths summed.
        for (int i = 1; i < Centers.Length; i++)
        {
            double dist = (Centers[i] + Shift(Centers[i])) - (Centers[i - 1] + Shift(Centers[i - 1]));
            Assert.Equal(Pitch * (scales[i] + scales[i - 1]) * 0.5, dist, precision: 6);
        }

        // Elements beyond the app group slide by the whole side growth.
        Assert.Equal(-anchor, Shift(-500.0), precision: 9);
        Assert.Equal(total - anchor, Shift(5000.0), precision: 9);
    }

    [Fact]
    public void BackdropLayout_InsetsAndClipsTheRoundedShape()
    {
        var shape = new GlassPixelRect(100, 900, 700, 960);
        var layout = GlassBackdropLayout.Compute(shape, new GlassPixelRect(0, 0, 1920, 1080), radiusPx: 18, insetPx: 1);
        Assert.Equal(new GlassPixelRect(101, 901, 699, 959), layout.Window);
        Assert.Equal((0, 0, 598, 58, 17), (layout.RegionLeft, layout.RegionTop, layout.RegionWidth, layout.RegionHeight, layout.RadiusPx));

        // Sliding below the monitor: the window is clipped but the region keeps the full rounded shape.
        var sliding = GlassBackdropLayout.Compute(new GlassPixelRect(100, 1050, 700, 1110), new GlassPixelRect(0, 984, 1920, 1080), 18, 1);
        Assert.Equal(1080, sliding.Window.Bottom);
        Assert.Equal(58, sliding.RegionHeight);

        // DWM's 8 px corner fits inside an 18 px rounded capsule once inset by (18 - 8)(1 - 1/sqrt 2) = 2.93 px.
        Assert.Equal(2.93, GlassBackdropWindow.ComputeCornerInset(18, 8), precision: 2);
        Assert.Equal(0.0, GlassBackdropWindow.ComputeCornerInset(6, 8));

        var gone = GlassBackdropLayout.Compute(new GlassPixelRect(100, 1100, 700, 1160), new GlassPixelRect(0, 984, 1920, 1080), 18, 1);
        Assert.True(gone.IsEmpty);
    }
}
