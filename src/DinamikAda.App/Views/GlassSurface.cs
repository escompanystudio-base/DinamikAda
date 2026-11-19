using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;
using Microsoft.Win32;

namespace DinamikAda.App.Views;

/// <summary>
/// Whether real glass may be used: the OS supports accent blur and the user's "Transparency effects"
/// switch is on. Re-read when Windows reports a personalisation change.
/// </summary>
internal static class GlassPolicy
{
    private static bool? s_transparency;
    private static bool s_listening;

    public static event EventHandler? Changed;

    public static bool IsEnabled
    {
        get
        {
            if (!GlassBackdropSupport.IsOsSupported) return false;
            if (s_transparency is null)
            {
                s_transparency = GlassBackdropSupport.IsTransparencyEnabled();
                Listen();
            }
            return s_transparency.Value;
        }
    }

    private static void Listen()
    {
        if (s_listening) return;
        s_listening = true;
        var dispatcher = Application.Current?.Dispatcher;
        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
                bool now = GlassBackdropSupport.IsTransparencyEnabled();
                if (now == s_transparency) return;
                s_transparency = now;
                if (dispatcher is null) Changed?.Invoke(null, EventArgs.Empty);
                else dispatcher.BeginInvoke(() => Changed?.Invoke(null, EventArgs.Empty));
            };
        }
        catch
        {
            // Without notifications the switch is simply read once.
        }
    }
}

/// <summary>
/// Puts a <see cref="GlassBackdropWindow"/> behind a rounded WPF element (in any window or popup HWND) and
/// keeps it matched to the element's on-screen bounds, render transforms and effective opacity. Callers
/// <see cref="Sync"/> from their own render loop, or <see cref="TrackFor"/> a short burst after a change
/// (open/close animations, resizes). The backdrop is never recreated, only moved and resized; it shows while the
/// element is at least half opaque (DWM drops the blur on partially transparent layered windows).
/// </summary>
internal sealed class GlassSurface : IDisposable
{
    private readonly FrameworkElement _target;
    private readonly Func<double> _radiusDip;
    private readonly Func<bool> _allowed;
    private readonly GlassBackdropWindow _backdrop;
    private long _trackUntilTicks;
    private bool _tracking;
    private bool _disposed;

    public GlassSurface(FrameworkElement target, GlassBackdropMode mode, Func<double> radiusDip, Func<bool> allowed)
    {
        _target = target;
        _radiusDip = radiusDip;
        _allowed = allowed;
        _backdrop = new GlassBackdropWindow(mode);
    }

    /// <summary>True when composition refused the accent; the caller should keep the solid tint.</summary>
    public bool HasFailed => _backdrop.HasFailed;

    public bool IsShowing => _backdrop.IsVisible;

    /// <summary>Re-syncs whenever the given element's opacity or render transform changes (animations included).</summary>
    public void WatchAnimations(UIElement element, double burstMs = 420)
    {
        void Kick(object? s, EventArgs e) => TrackFor(burstMs);
        DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(UIElement))?.AddValueChanged(element, Kick);
        DependencyPropertyDescriptor.FromProperty(UIElement.RenderTransformProperty, typeof(UIElement))?.AddValueChanged(element, Kick);
        if (element is FrameworkElement fe) fe.SizeChanged += (_, _) => TrackFor(burstMs);
    }

    public void Sync()
    {
        if (_disposed) return;
        if (!_allowed() || !TryMeasure(out var shape, out int radiusPx, out var clip, out byte alpha, out nint hwnd))
        {
            _backdrop.Hide();
            return;
        }
        _backdrop.Update(shape, radiusPx, clip, alpha, hwnd);
    }

    /// <summary>Follows the element every frame for <paramref name="ms"/> (extending any burst already running).</summary>
    public void TrackFor(double ms)
    {
        if (_disposed) return;
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
        if (until > _trackUntilTicks) _trackUntilTicks = until;
        Sync();
        if (!_tracking && _allowed())
        {
            _tracking = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    public void Hide()
    {
        StopTracking();
        _backdrop.Hide();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        Sync();
        if (Stopwatch.GetTimestamp() > _trackUntilTicks) StopTracking();
    }

    private void StopTracking()
    {
        if (!_tracking) return;
        _tracking = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private bool TryMeasure(out GlassPixelRect shape, out int radiusPx, out GlassPixelRect clip, out byte alpha, out nint hwnd)
    {
        shape = default;
        clip = default;
        radiusPx = 0;
        alpha = 0;
        hwnd = 0;
        if (!_target.IsVisible || _target.ActualWidth < 2 || _target.ActualHeight < 2) return false;
        if (PresentationSource.FromVisual(_target) is not HwndSource src || src.Handle == 0 || src.CompositionTarget is null) return false;
        if (!NativeMethods.IsWindowVisible(src.Handle)) return false;

        double opacity = EffectiveOpacity(_target);
        if (opacity < 0.012) return false;

        Point a, b;
        try
        {
            a = _target.PointToScreen(new Point(0, 0));
            b = _target.PointToScreen(new Point(_target.ActualWidth, _target.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        int l = (int)Math.Round(Math.Min(a.X, b.X));
        int t = (int)Math.Round(Math.Min(a.Y, b.Y));
        int r = (int)Math.Round(Math.Max(a.X, b.X));
        int btm = (int)Math.Round(Math.Max(a.Y, b.Y));
        shape = new GlassPixelRect(l, t, r, btm);
        if (shape.IsEmpty) return false;

        double dpi = Math.Max(0.5, src.CompositionTarget.TransformToDevice.M11);
        double renderScale = shape.Width / Math.Max(1.0, _target.ActualWidth * dpi);
        radiusPx = (int)Math.Round(Math.Max(0, _radiusDip()) * dpi * renderScale);

        if (NativeMethods.GetWindowRect(src.Handle, out var wr))
        {
            clip = new GlassPixelRect(wr.Left, wr.Top, wr.Right, wr.Bottom);
        }
        alpha = (byte)Math.Clamp(Math.Round(255.0 * opacity), 0, 255);
        hwnd = src.Handle;
        return true;
    }

    private static double EffectiveOpacity(DependencyObject start)
    {
        double o = 1.0;
        DependencyObject? d = start;
        while (d is not null)
        {
            if (d is UIElement u)
            {
                if (u.Visibility != Visibility.Visible) return 0.0;
                o *= u.Opacity;
                if (o < 0.005) return 0.0;
            }
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }
        return o;
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopTracking();
        _disposed = true;
        _backdrop.Dispose();
    }
}
