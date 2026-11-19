using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>Circular progress ring with an animated value (0..1). Draws track + arc + optional spark.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SparkAngleProperty = DependencyProperty.Register(
        nameof(SparkAngle), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Angle in degrees (0 = top) of a bright spark dot running round the ring; negative hides it.</summary>
    public double SparkAngle
    {
        get => (double)GetValue(SparkAngleProperty);
        set => SetValue(SparkAngleProperty, value);
    }

    public double StrokeThickness { get; set; } = 3.0;
    public Brush Track { get; set; } = Brushes.DimGray;
    public Brush Fill { get; set; } = Brushes.DeepSkyBlue;
    /// <summary>Gap in degrees at the bottom (0 = full circle, e.g. 90 for a gauge).</summary>
    public double GapDegrees { get; set; }

    private double _target = double.NaN;

    /// <summary>
    /// Glides to <paramref name="value"/> (0..1) with the soft motion curve; retargets smoothly mid-flight, so values
    /// that update every second look continuous. No-op when unchanged.
    /// </summary>
    public void AnimateTo(double value, double ms = Motion.ValueMs)
    {
        value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        if (!double.IsNaN(_target) && Math.Abs(_target - value) < 0.0015) return;
        _target = value;
        BeginAnimation(ValueProperty, Motion.To(value, ms), HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= StrokeThickness * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - StrokeThickness / 2;
        double start = 180 + GapDegrees / 2;          // degrees, 0 = top, clockwise
        double sweepTotal = 360 - GapDegrees;

        var trackPen = new Pen(Track, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (GapDegrees <= 0.01) dc.DrawEllipse(null, trackPen, center, r, r);
        else dc.DrawGeometry(null, trackPen, Arc(center, r, start, sweepTotal));

        double v = Math.Clamp(Value, 0, 1);
        if (v > 0.002)
        {
            var fillPen = new Pen(Fill, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double sweep = sweepTotal * v;
            if (sweep >= 359.9) dc.DrawEllipse(null, fillPen, center, r, r);
            else dc.DrawGeometry(null, fillPen, Arc(center, r, GapDegrees <= 0.01 ? 0 : start, sweep));
        }

        if (SparkAngle >= 0)
        {
            var p = PointOn(center, r, SparkAngle);
            var glow = new RadialGradientBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF));
            dc.DrawEllipse(glow, null, p, StrokeThickness * 2.2, StrokeThickness * 2.2);
            dc.DrawEllipse(Brushes.White, null, p, StrokeThickness * 0.6, StrokeThickness * 0.6);
        }
    }

    public static Point PointOn(Point c, double r, double degFromTop)
    {
        double rad = (degFromTop - 90) * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    public static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(PointOn(c, r, startDeg), false, false);
            ctx.ArcTo(PointOn(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>Horizontal rounded progress bar with an animated value (0..1).</summary>
public sealed class BarGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BarGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Track { get; set; } = Brushes.DimGray;
    public Brush Fill { get; set; } = Brushes.DeepSkyBlue;
    /// <summary>Optional markers drawn as thin ticks at these fractions (0..1).</summary>
    public IReadOnlyList<double>? Markers { get; set; }
    public Brush MarkerBrush { get; set; } = Brushes.White;

    private double _target = double.NaN;

    /// <summary>Glides to <paramref name="value"/> (0..1) with the soft motion curve; no-op when unchanged.</summary>
    public void AnimateTo(double value, double ms = Motion.ValueMs)
    {
        value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        if (!double.IsNaN(_target) && Math.Abs(_target - value) < 0.0015) return;
        _target = value;
        BeginAnimation(ValueProperty, Motion.To(value, ms), HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double rad = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), rad, rad);
        double fw = Math.Clamp(Value, 0, 1) * w;
        if (fw > 0.5) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(fw, h), h), rad, rad);
        if (Markers is not null)
        {
            var pen = new Pen(MarkerBrush, 1);
            foreach (var m in Markers)
            {
                double x = Math.Round(Math.Clamp(m, 0, 1) * w) + 0.5;
                dc.DrawLine(pen, new Point(x, -1), new Point(x, h + 1));
            }
        }
    }
}

/// <summary>
/// Line chart for a short series with an optional area fill and second series.
/// <list type="bullet">
/// <item>The line is a smooth curve (Catmull-Rom), not a polyline.</item>
/// <item>When new data arrives, a rolling window (the old series shifted by one sample) flows left by one step.</item>
/// <item>Other changes morph point by point, and the vertical scale eases too, so the chart never jumps.</item>
/// </list>
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private double[] _from = [];
    private double[] _to = [];
    private double[]? _fromSecond;
    private double[]? _toSecond;
    private bool _shift;
    private (double Min, double Max) _fromScale;
    private (double Min, double Max) _toScale;

    public Brush Stroke { get; set; } = Brushes.DeepSkyBlue;
    public Brush? SecondStroke { get; set; }
    public double StrokeThickness { get; set; } = 1.5;
    public bool FillArea { get; set; } = true;
    /// <summary>When set, values are scaled to [0, Max] instead of min..max (e.g. 100 for percentages).</summary>
    public double? FixedMax { get; set; }
    /// <summary>Draw a dot on the last point.</summary>
    public bool ShowHead { get; set; } = true;
    /// <summary>Smooth curve (default) instead of straight segments.</summary>
    public bool Smooth { get; set; } = true;
    /// <summary>Animate data changes (flow / morph); off draws new data immediately.</summary>
    public bool AnimateChanges { get; set; } = true;

    /// <summary>Transition progress 0..1 between the previous and the current data (animated).</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public void SetValues(IReadOnlyList<double> values, IReadOnlyList<double>? second = null)
    {
        var next = values.ToArray();
        var nextSecond = second?.ToArray();
        if (SameSeries(next, _to) && SameSeries(nextSecond, _toSecond)) return;

        bool hadData = _to.Length >= 2;
        _shift = hadData && IsShiftedByOne(_to, next) && (_toSecond is null || nextSecond is null || IsShiftedByOne(_toSecond, nextSecond));
        _from = _to;
        _fromSecond = _toSecond;
        _fromScale = hadData ? _toScale : Scale(next, nextSecond);
        _to = next;
        _toSecond = nextSecond;
        _toScale = Scale(next, nextSecond);

        if (!AnimateChanges || !hadData)
        {
            BeginAnimation(ProgressProperty, null);
            Progress = 1;
            InvalidateVisual();
            return;
        }
        BeginAnimation(ProgressProperty, Motion.FromTo(0, 1, Motion.ValueMs), HandoffBehavior.SnapshotAndReplace);
    }

    private static bool SameSeries(double[]? a, double[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!(a[i] == b[i] || (double.IsNaN(a[i]) && double.IsNaN(b[i])))) return false;
        return true;
    }

    /// <summary>True when <paramref name="next"/> is <paramref name="prev"/> moved left by one sample (a rolling window).</summary>
    private static bool IsShiftedByOne(double[] prev, double[] next)
    {
        if (prev.Length != next.Length || prev.Length < 4) return false;
        int n = prev.Length;
        for (int i = 0; i < n - 1; i++)
        {
            double a = prev[i + 1], b = next[i];
            double tol = Math.Max(1e-9, Math.Abs(a) * 1e-6);
            if (double.IsFinite(a) != double.IsFinite(b) || (double.IsFinite(a) && Math.Abs(a - b) > tol)) return false;
        }
        return true;
    }

    private (double Min, double Max) Scale(double[] values, double[]? second)
    {
        double max = FixedMax ?? Math.Max(MaxOf(values), second is null ? double.MinValue : MaxOf(second));
        double min = FixedMax.HasValue ? 0 : Math.Min(MinOf(values), second is null ? double.MaxValue : MinOf(second));
        if (FixedMax is null && second is not null) min = Math.Min(min, 0);
        if (max - min < 1e-9) max = min + 1;
        return (min, max);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2 || _to.Length < 2) return;
        double p = Math.Clamp(Progress, 0, 1);
        double min = Lerp(_fromScale.Min, _toScale.Min, p);
        double max = Lerp(_fromScale.Max, _toScale.Max, p);
        if (max - min < 1e-9) max = min + 1;

        dc.PushClip(new RectangleGeometry(new Rect(0, -StrokeThickness * 2, w, h + StrokeThickness * 4)));
        if (_toSecond is { Length: >= 2 } && SecondStroke is not null)
        {
            DrawSeries(dc, Frame(_fromSecond, _toSecond, p, w, h, min, max), SecondStroke, h, fill: false, head: false);
        }
        DrawSeries(dc, Frame(_from, _to, p, w, h, min, max), Stroke, h, FillArea, ShowHead);
        dc.Pop();
    }

    /// <summary>Points for the current animation frame: flowing (rolling window), morphing, or the target series.</summary>
    private Point[] Frame(double[]? from, double[] to, double p, double w, double h, double min, double max)
    {
        int n = to.Length;
        double step = (w - 1) / (n - 1);
        double pad = StrokeThickness * 1.5;
        double Y(double v) => pad + (1 - ((double.IsFinite(v) ? v : min) - min) / (max - min)) * (h - pad * 2);

        if (_shift && from is { Length: > 0 } && from.Length == n && p < 1)
        {
            // Old first sample plus the new series, all sliding left by one step as p goes 0 → 1.
            var pts = new Point[n + 1];
            pts[0] = new Point((0 - p) * step, Y(from[0]));
            for (int i = 0; i < n; i++) pts[i + 1] = new Point((i + 1 - p) * step, Y(to[i]));
            return pts;
        }
        var res = new Point[n];
        bool morph = from is not null && from.Length == n && p < 1;
        for (int i = 0; i < n; i++)
        {
            double v = morph ? Lerp(Finite(from![i], to[i]), to[i], p) : to[i];
            res[i] = new Point(i * step, Y(v));
        }
        return res;
    }

    private void DrawSeries(DrawingContext dc, Point[] pts, Brush stroke, double h, bool fill, bool head)
    {
        if (pts.Length < 2) return;
        double top = StrokeThickness, bottom = h - StrokeThickness;

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(pts[0], false, false);
            AppendCurve(g, pts, top, bottom);
        }
        line.Freeze();

        if (fill && stroke is SolidColorBrush sb)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(pts[0].X, h), true, true);
                g.LineTo(pts[0], false, false);
                AppendCurve(g, pts, top, bottom, stroked: false);
                g.LineTo(new Point(pts[^1].X, h), false, false);
            }
            area.Freeze();
            var c = sb.Color;
            var grad = new LinearGradientBrush(Color.FromArgb(0x4A, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
            grad.Freeze();
            dc.DrawGeometry(grad, null, area);
        }

        var pen = new Pen(stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, line);
        if (head)
        {
            var last = pts[^1];
            if (stroke is SolidColorBrush hb)
            {
                var halo = new SolidColorBrush(Color.FromArgb(0x40, hb.Color.R, hb.Color.G, hb.Color.B));
                halo.Freeze();
                dc.DrawEllipse(halo, null, last, StrokeThickness * 3.0, StrokeThickness * 3.0);
            }
            dc.DrawEllipse(stroke, null, last, StrokeThickness * 1.35, StrokeThickness * 1.35);
        }
    }

    /// <summary>Catmull-Rom spline through the points as cubic Béziers, with control points kept inside the chart.</summary>
    private void AppendCurve(StreamGeometryContext g, Point[] pts, double top, double bottom, bool stroked = true)
    {
        if (!Smooth || pts.Length < 3)
        {
            g.PolyLineTo(pts.Skip(1).ToList(), stroked, true);
            return;
        }
        const double k = 1.0 / 6.0;
        for (int i = 0; i < pts.Length - 1; i++)
        {
            Point p0 = pts[Math.Max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(pts.Length - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) * k, Math.Clamp(p1.Y + (p2.Y - p0.Y) * k, top, bottom));
            var c2 = new Point(p2.X - (p3.X - p1.X) * k, Math.Clamp(p2.Y - (p3.Y - p1.Y) * k, top, bottom));
            g.BezierTo(c1, c2, p2, stroked, true);
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;
    private static double MaxOf(IReadOnlyList<double> v) { double m = double.MinValue; foreach (var x in v) if (double.IsFinite(x) && x > m) m = x; return m == double.MinValue ? 0 : m; }
    private static double MinOf(IReadOnlyList<double> v) { double m = double.MaxValue; foreach (var x in v) if (double.IsFinite(x) && x < m) m = x; return m == double.MaxValue ? 0 : m; }
}

/// <summary>Small factory helpers so tiles stay short and visually consistent.</summary>
public static class W
{
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    public static TextBlock Text(DockWidgetPalette p, string text, double size, Brush? brush = null,
        FontWeight? weight = null, bool mono = false, bool ui = true)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = mono ? p.MonoFont : ui ? p.UiFont : p.TextFont,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = brush ?? p.Foreground,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
    }

    public static TextBlock Glyph(DockWidgetPalette p, string glyph, double size, Brush? brush = null)
    {
        // Segoe Fluent Icons only covers its private-use range; standard symbols (weather) need Segoe UI Symbol.
        bool symbol = glyph.Length > 0 && glyph.All(ch => ch < '');
        return new TextBlock
        {
            Text = glyph,
            FontFamily = symbol ? SymbolFont : p.IconFont,
            FontSize = size,
            Foreground = brush ?? p.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is FrameworkElement fe && i > 0) fe.Margin = new Thickness(spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            sp.Children.Add(children[i]);
        }
        return sp;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static RingGauge Ring(DockWidgetPalette p, double size, double thickness, Color fill)
    {
        return new RingGauge
        {
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            Track = p.Track,
            Fill = DockWidgetPalette.Freeze(new SolidColorBrush(fill)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    public static BarGauge Bar(DockWidgetPalette p, double width, double height, Color fill)
    {
        return new BarGauge
        {
            Width = width,
            Height = height,
            Track = p.Track,
            Fill = DockWidgetPalette.Freeze(new SolidColorBrush(fill)),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static SolidColorBrush Brush(Color c) => DockWidgetPalette.Freeze(new SolidColorBrush(c));
    public static SolidColorBrush Brush(Color c, byte alpha) => DockWidgetPalette.Freeze(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));

    /// <summary>Sets text and plays a short slide-up fade when it changes (numbers ticking over).</summary>
    public static void SetTextAnimated(TextBlock tb, string text, bool animate = true)
    {
        if (string.Equals(tb.Text, text, StringComparison.Ordinal)) return;
        bool hadText = !string.IsNullOrEmpty(tb.Text);
        tb.Text = text;
        if (!animate || !hadText) return;
        if (tb.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
        {
            tt = new TranslateTransform();
            tb.RenderTransform = tt;
        }
        // A soft roll: the new value rises a few pixels into place while fading up, no hard swap.
        double rise = Math.Max(2.5, tb.FontSize * 0.22);
        tt.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(rise, 0, Motion.TextMs), HandoffBehavior.SnapshotAndReplace);
        tb.BeginAnimation(UIElement.OpacityProperty, Motion.FromTo(0.35, 1, Motion.TextMs), HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Soft entrance (tiles added to the dock, card rows): settles into place with no overshoot.</summary>
    public static void PopIn(FrameworkElement el, double delayMs = 0)
    {
        var st = new ScaleTransform(0.9, 0.9);
        var tt = new TranslateTransform(0, 4);
        el.RenderTransformOrigin = new Point(0.5, 0.6);
        el.RenderTransform = new TransformGroup { Children = { st, tt } };
        el.Opacity = 0;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(0.9, 1, Motion.EnterMs, delayMs: delayMs));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(0.9, 1, Motion.EnterMs, delayMs: delayMs));
        tt.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(4, 0, Motion.EnterMs, delayMs: delayMs));
        el.BeginAnimation(UIElement.OpacityProperty, Motion.FromTo(0, 1, Motion.EnterMs * 0.7, delayMs: delayMs));
    }

    /// <summary>Quick scale "tap" feedback.</summary>
    public static void Pulse(FrameworkElement el, double scale = 0.9)
    {
        if (el.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            st = new ScaleTransform(1, 1);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = st;
        }
        // Gentle press: a small dip that eases back, never a snap.
        double target = 1 - (1 - scale) * 0.6;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), Motion.Gentle));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), Motion.Soft));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim, HandoffBehavior.SnapshotAndReplace);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Endless rotation (e.g. a charging spark); returns the clock-free animation so callers can stop it.</summary>
    public static void SpinForever(RotateTransform rt, double secondsPerTurn)
    {
        rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(secondsPerTurn))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    /// <summary>Endless gentle float (weather glyphs).</summary>
    public static void FloatForever(FrameworkElement el, double amplitude = 1.5, double seconds = 2.6)
    {
        var tt = new TranslateTransform();
        el.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-amplitude, amplitude, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.Gentle
        });
    }

    // ---- Cards -------------------------------------------------------------------------------

    /// <summary>Standard card body: header (glyph + title + optional right element) and content.</summary>
    public static FrameworkElement Card(DockWidgetPalette p, string glyph, string title, UIElement content,
        double width = 300, UIElement? headerRight = null)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        if (headerRight is not null)
        {
            DockPanel.SetDock(headerRight, Dock.Right);
            header.Children.Add(headerRight);
        }
        var g = Glyph(p, glyph, 14, p.AccentBrush);
        g.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(g, Dock.Left);
        header.Children.Add(g);
        header.Children.Add(Text(p, title, 15, weight: FontWeights.SemiBold));

        var root = new StackPanel { Width = width };
        root.Children.Add(header);
        root.Children.Add(new Border { Height = 1, Background = p.Track, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(content);
        return root;
    }

    /// <summary>Label on the left, value on the right.</summary>
    public static FrameworkElement CardRow(DockWidgetPalette p, string label, string value, Brush? valueBrush = null, string? glyph = null)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var v = Text(p, value, 12.5, valueBrush ?? p.Foreground, FontWeights.SemiBold);
        DockPanel.SetDock(v, Dock.Right);
        dp.Children.Add(v);
        if (glyph is not null)
        {
            var gl = Glyph(p, glyph, 12, p.SecondaryForeground);
            gl.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(gl, Dock.Left);
            dp.Children.Add(gl);
        }
        dp.Children.Add(Text(p, label, 12.5, p.SecondaryForeground, ui: false));
        return dp;
    }

    /// <summary>Flat pill button for cards and tiles. Clicks don't bubble to the tile.</summary>
    public static Border Button(DockWidgetPalette p, string text, Action onClick, string? glyph = null, bool primary = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Brush fg = primary ? (p.IsLight ? Brushes.White : Brush(Color.FromRgb(0x05, 0x08, 0x10))) : p.Foreground;
        if (glyph is not null)
        {
            var gl = Glyph(p, glyph, 12, fg);
            if (!string.IsNullOrEmpty(text)) gl.Margin = new Thickness(0, 0, 6, 0);
            content.Children.Add(gl);
        }
        if (!string.IsNullOrEmpty(text)) content.Children.Add(Text(p, text, 12, fg, FontWeights.SemiBold));
        Brush bg = primary ? p.AccentBrush : p.CardRowBackground;
        var b = new Border
        {
            Child = content,
            Background = bg,
            BorderBrush = p.TileBorder,
            BorderThickness = new Thickness(primary ? 0 : 0.8),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 6, 10, 6),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 6)
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1.0;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Pulse(b, 0.94);
            onClick();
        };
        return b;
    }

    /// <summary>Round icon-only button used directly on tiles (play/pause, skip…).</summary>
    public static Border IconButton(DockWidgetPalette p, string glyph, double size, Action onClick, Brush? background = null, Brush? foreground = null)
    {
        var b = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = background ?? Brushes.Transparent,
            Child = Glyph(p, glyph, size * 0.46, foreground ?? p.Foreground),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.8;
        b.MouseLeave += (_, _) => b.Opacity = 1.0;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Pulse(b);
            onClick();
        };
        return b;
    }

    public static TextBox Input(DockWidgetPalette p, string placeholder, double width)
    {
        var tb = new TextBox
        {
            Width = width,
            FontFamily = p.TextFont,
            FontSize = 13,
            Foreground = p.Foreground,
            CaretBrush = p.Foreground,
            Background = p.CardRowBackground,
            BorderBrush = p.TileBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = placeholder,
            ToolTip = placeholder
        };
        System.Windows.Automation.AutomationProperties.SetName(tb, placeholder);
        return tb;
    }

    public static string Pct(double v) => ((int)Math.Round(Math.Clamp(v, 0, 100))).ToString(CultureInfo.InvariantCulture) + "%";
}
