using System.Windows;
using System.Windows.Media.Animation;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>
/// One shared motion language for widgets, cards, the picker and the voice overlay: soft, critically damped
/// movement with no overshoot or wobble. Values glide for about a second so data that updates every second looks
/// continuous, while entrances settle quickly and exits fade out quietly.
/// </summary>
public static class Motion
{
    /// <summary>Soft ease-out (cubic-bezier 0.22, 1, 0.36, 1): fast start, long gentle settle, no overshoot.</summary>
    public static readonly IEasingFunction Soft = Freeze(new CubicBezierEase(0.22, 1.0, 0.36, 1.0));

    /// <summary>Gentle ease-in-out (cubic-bezier 0.45, 0, 0.25, 1) for looping and two-way movement.</summary>
    public static readonly IEasingFunction Gentle = Freeze(new CubicBezierEase(0.45, 0.0, 0.25, 1.0));

    /// <summary>Quiet ease-in for exits (cubic-bezier 0.4, 0, 1, 1).</summary>
    public static readonly IEasingFunction Exit = Freeze(new CubicBezierEase(0.4, 0.0, 1.0, 1.0));

    /// <summary>Live values (rings, bars, charts) glide over this long, about one data interval.</summary>
    public const double ValueMs = 950;
    /// <summary>Elements arriving (cards, tiles, rows).</summary>
    public const double EnterMs = 460;
    /// <summary>Elements leaving.</summary>
    public const double ExitMs = 240;
    /// <summary>Text changing (numbers ticking over).</summary>
    public const double TextMs = 380;

    public static DoubleAnimation To(double to, double ms, IEasingFunction? ease = null, double delayMs = 0) => new(to, TimeSpan.FromMilliseconds(ms))
    {
        EasingFunction = ease ?? Soft,
        BeginTime = TimeSpan.FromMilliseconds(delayMs)
    };

    public static DoubleAnimation FromTo(double from, double to, double ms, IEasingFunction? ease = null, double delayMs = 0) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? Soft,
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };

    private static IEasingFunction Freeze(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }
}

/// <summary>CSS-style cubic-bezier easing (P0 = 0,0 and P3 = 1,1), solved with Newton steps plus bisection.</summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    private double _x1, _y1, _x2, _y2;

    public CubicBezierEase() : this(0.25, 0.1, 0.25, 1.0) { }

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        _x1 = Math.Clamp(x1, 0, 1);
        _y1 = y1;
        _x2 = Math.Clamp(x2, 0, 1);
        _y2 = y2;
        EasingMode = EasingMode.EaseIn; // EaseInCore is the curve itself; EaseIn applies it unchanged.
    }

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double u = SolveX(t);
        return Bezier(u, _y1, _y2);
    }

    private static double Bezier(double u, double p1, double p2)
    {
        double v = 1 - u;
        return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
    }

    private static double BezierDerivative(double u, double p1, double p2)
    {
        double v = 1 - u;
        return 3 * v * v * p1 + 6 * v * u * (p2 - p1) + 3 * u * u * (1 - p2);
    }

    private double SolveX(double x)
    {
        double u = x;
        for (int i = 0; i < 6; i++)
        {
            double err = Bezier(u, _x1, _x2) - x;
            if (Math.Abs(err) < 1e-6) return u;
            double d = BezierDerivative(u, _x1, _x2);
            if (Math.Abs(d) < 1e-6) break;
            u -= err / d;
        }
        double lo = 0, hi = 1;
        u = x;
        for (int i = 0; i < 24; i++)
        {
            double xu = Bezier(u, _x1, _x2);
            if (Math.Abs(xu - x) < 1e-6) break;
            if (xu < x) lo = u; else hi = u;
            u = (lo + hi) / 2;
        }
        return u;
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(_x1, _y1, _x2, _y2);

    protected override void CloneCore(Freezable sourceFreezable)
    {
        base.CloneCore(sourceFreezable);
        Copy((CubicBezierEase)sourceFreezable);
    }

    protected override void CloneCurrentValueCore(Freezable sourceFreezable)
    {
        base.CloneCurrentValueCore(sourceFreezable);
        Copy((CubicBezierEase)sourceFreezable);
    }

    protected override void GetAsFrozenCore(Freezable sourceFreezable)
    {
        base.GetAsFrozenCore(sourceFreezable);
        Copy((CubicBezierEase)sourceFreezable);
    }

    protected override void GetCurrentValueAsFrozenCore(Freezable sourceFreezable)
    {
        base.GetCurrentValueAsFrozenCore(sourceFreezable);
        Copy((CubicBezierEase)sourceFreezable);
    }

    private void Copy(CubicBezierEase s)
    {
        _x1 = s._x1;
        _y1 = s._y1;
        _x2 = s._x2;
        _y2 = s._y2;
    }
}
