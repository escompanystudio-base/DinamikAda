using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Notifications;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.App.Views;

/// <summary>Where the toast stack sits: the dock's monitor, the dock capsule rectangle and the screen edge.</summary>
public readonly record struct DockToastAnchor(NativeMethods.RECT DockRectPx, double DpiScale, bool DockAtTop, nint DockHwnd);

/// <summary>
/// Borderless, transparent, topmost, never-activating window that stacks up to three frosted toasts just above the
/// dock (below it when the dock is on the top edge). Toasts slide out from behind the dock, auto-dismiss after six
/// seconds (hover pauses), and clicks work without taking focus from the user's app (WS_EX_NOACTIVATE plus
/// MA_NOACTIVATE). Owned by <see cref="DockWindow"/>; call <see cref="DockWindow.ShowDockToast(DockToastRequest)"/>.
/// </summary>
public sealed class DockToastWindow
{
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

    /// <summary>Card width bounds (DIP).</summary>
    internal const double CardMinW = 300, CardMaxW = 380;
    /// <summary>Room around the cards so the soft shadow is not clipped.</summary>
    private const double SideMarginDip = 36;
    /// <summary>Space reserved beyond the farthest card for its shadow.</summary>
    private const double FarMarginDip = 40;
    /// <summary>Gap between the newest card and the dock capsule, and between cards.</summary>
    internal const double DockGapDip = 12, CardGapDip = 10;
    /// <summary>The window reaches this far over the dock so cards can rise from behind it.</summary>
    private const double DockOverlapDip = 14;
    /// <summary>Entrance rise distance.</summary>
    private const double RiseDip = 12;

    private const double WindowW = CardMaxW + SideMarginDip * 2;
    private const double WindowH = DockOverlapDip + DockGapDip + 3 * 76 + 2 * CardGapDip + FarMarginDip;

    private readonly Func<string> _lang;
    private readonly Func<DockToastAnchor?> _anchor;
    private readonly List<DockToastCard> _cards = new(); // oldest first
    private Window? _window;
    private Canvas? _canvas;
    private nint _hwnd;
    private bool _atTop;
    private bool _closed;

    public DockToastWindow(Func<string> language, Func<DockToastAnchor?> anchor)
    {
        _lang = language;
        _anchor = anchor;
    }

    public int VisibleCount => _cards.Count(c => !c.IsLeaving);

    private static bool MotionOn => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Single place to plug in a real blurred glass backdrop later (acrylic / DWM system backdrop clipped to the card).
    /// Today the window is a per-pixel transparent layered window and each card paints a dark frosted fallback.
    /// </summary>
    public static void ApplyToastBackdrop(Window window)
    {
        _ = window;
    }

    /// <summary>Shows a toast (UI thread). Dedupe and quiet/fullscreen gating are the caller's job.</summary>
    public void Show(DockToastRequest request)
    {
        if (_closed) return;
        EnsureWindow();
        Reposition();

        var existing = _cards.FirstOrDefault(c => !c.IsLeaving && string.Equals(c.Request.Key, request.Key, StringComparison.Ordinal));
        if (existing is not null) Dismiss(existing, animate: false);

        int evict = DockToastPlanner.EvictCountForNew(VisibleCount);
        foreach (var old in _cards.Where(c => !c.IsLeaving).Take(evict).ToList()) Dismiss(old, animate: true);

        var card = new DockToastCard(request, _lang());
        card.CloseRequested += (_, _) => Dismiss(card, animate: true);
        card.ActionInvoked += (_, _) =>
        {
            try { request.Action?.Invoke(); } catch { }
            Dismiss(card, animate: true);
        };
        card.MouseEnter += (_, _) => card.PauseLifetime();
        card.MouseLeave += (_, _) => card.ResumeLifetime();
        card.Expired += (_, _) => Dismiss(card, animate: true);

        _cards.Add(card);
        _canvas!.Children.Add(card);
        card.Measure(new Size(CardMaxW, double.PositiveInfinity));
        double w = Math.Clamp(Math.Ceiling(card.DesiredSize.Width), CardMinW, CardMaxW);
        card.Width = w;
        Canvas.SetLeft(card, Math.Round((WindowW - w) / 2));
        card.Measure(new Size(w, double.PositiveInfinity));

        if (_window!.Visibility != Visibility.Visible)
        {
            _window.Show();
            Reposition();
        }

        // Real blurred glass behind each card (clipped to its rounded shape) when Windows allows it.
        if (GlassPolicy.IsEnabled)
        {
            var glass = new GlassSurface(card, DinamikAda.Platform.Win32.Windowing.GlassBackdropMode.Blur,
                () => 16, () => !card.IsLeaving && card.IsVisible);
            card.Glass = glass;
            card.UseGlassTint();
            glass.TrackFor(Motion.EnterMs + 240);
        }

        double rest = TargetY(card);
        double from = _atTop ? rest - RiseDip : rest + RiseDip;
        card.Slide.Y = from;
        card.Opacity = 0;
        if (MotionOn)
        {
            card.BeginAnimation(UIElement.OpacityProperty, Motion.FromTo(0, 1, 300));
            card.Slide.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(from, rest, Motion.EnterMs));
        }
        else
        {
            card.Opacity = 1;
            card.Slide.Y = rest;
        }
        Restack(except: card);
        card.StartLifetime();
    }

    public void DismissAll()
    {
        foreach (var c in _cards.ToList()) Dismiss(c, animate: false);
    }

    /// <summary>Follows the dock (edge switch, monitor or DPI change).</summary>
    public void Reposition()
    {
        if (_hwnd == 0 || _window is null) return;
        var a = _anchor();
        if (a is not { } anchor) return;

        bool edgeChanged = anchor.DockAtTop != _atTop;
        _atTop = anchor.DockAtTop;
        double scale = Math.Max(1.0, anchor.DpiScale);
        int w = (int)Math.Round(WindowW * scale), h = (int)Math.Round(WindowH * scale);
        int overlap = (int)Math.Round(DockOverlapDip * scale);
        var r = anchor.DockRectPx;
        int x = r.Left + (r.Right - r.Left - w) / 2;
        int y = _atTop ? r.Bottom - overlap : r.Top + overlap - h;
        _window.Width = WindowW;
        _window.Height = WindowH;
        // Sit just under the dock in z-order so a card rising out of the overlap appears from behind the capsule.
        nint after = anchor.DockHwnd != 0 ? anchor.DockHwnd : NativeMethods.HWND_TOPMOST;
        NativeMethods.SetWindowPos(_hwnd, after, x, y, w, h, NativeMethods.SWP_NOACTIVATE);
        if (edgeChanged) Restack(except: null, animate: false);
        foreach (var c in _cards) c.Glass?.TrackFor(120);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        foreach (var c in _cards)
        {
            c.StopLifetime();
            c.Glass?.Dispose();
            c.Glass = null;
        }
        _cards.Clear();
        try { _window?.Close(); } catch { }
        _window = null;
    }

    // ---- stack ---------------------------------------------------------------------------------------------

    private double TargetY(DockToastCard card)
    {
        var live = _cards.Where(c => !c.IsLeaving).Reverse().ToList(); // newest first
        int index = live.IndexOf(card);
        if (index < 0) return card.Slide.Y;
        var heights = live.Select(c => c.DesiredSize.Height).ToList();
        double offset = DockToastPlanner.OffsetFromDock(heights, index, CardGapDip);
        return _atTop
            ? DockOverlapDip + DockGapDip + offset
            : WindowH - DockOverlapDip - DockGapDip - offset - card.DesiredSize.Height;
    }

    private void Restack(DockToastCard? except, bool animate = true)
    {
        foreach (var c in _cards)
        {
            if (c.IsLeaving || ReferenceEquals(c, except)) continue;
            double y = TargetY(c);
            c.Glass?.TrackFor(Motion.EnterMs + 120);
            if (animate && MotionOn) c.Slide.BeginAnimation(TranslateTransform.YProperty, Motion.To(y, Motion.EnterMs));
            else
            {
                c.Slide.BeginAnimation(TranslateTransform.YProperty, null);
                c.Slide.Y = y;
            }
        }
    }

    private void Dismiss(DockToastCard card, bool animate)
    {
        if (card.IsLeaving) return;
        card.IsLeaving = true;
        card.StopLifetime();
        card.Glass?.Hide();
        card.IsHitTestVisible = false;
        Restack(except: null);

        if (!animate || !MotionOn)
        {
            Remove(card);
            return;
        }
        double sink = _atTop ? -6 : 6;
        var fade = Motion.To(0, Motion.ExitMs, Motion.Exit);
        fade.Completed += (_, _) => Remove(card);
        card.Slide.BeginAnimation(TranslateTransform.YProperty, Motion.To(card.Slide.Y + sink, Motion.ExitMs, Motion.Exit));
        card.Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(0.98, Motion.ExitMs, Motion.Exit));
        card.Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(0.98, Motion.ExitMs, Motion.Exit));
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void Remove(DockToastCard card)
    {
        card.Glass?.Dispose();
        card.Glass = null;
        _cards.Remove(card);
        _canvas?.Children.Remove(card);
        if (_cards.Count == 0 && _window is not null && _window.Visibility == Visibility.Visible) _window.Hide();
    }

    // ---- window --------------------------------------------------------------------------------------------

    private void EnsureWindow()
    {
        if (_window is not null) return;
        _canvas = new Canvas { Width = WindowW, Height = WindowH, ClipToBounds = true, Background = null };
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Focusable = false,
            Width = WindowW,
            Height = WindowH,
            Left = -32000,
            Top = -32000,
            Title = "DinamikAda Toasts",
            Content = _canvas
        };
        _window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            long ex = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(ex));
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            ApplyToastBackdrop(_window);
        };
        _window.DpiChanged += (_, _) => _window.Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Render);
        new WindowInteropHelper(_window).EnsureHandle();
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }
        return 0;
    }

    // ---- render audit --------------------------------------------------------------------------------------

    /// <summary>Builds a still card (no window, no timers) for the render audit.</summary>
    internal static FrameworkElement BuildCardForAudit(DockToastRequest request, string lang, double width)
    {
        var card = new DockToastCard(request, lang) { Width = width };
        card.Measure(new Size(width, double.PositiveInfinity));
        return card;
    }
}

/// <summary>One frosted toast: optional icon tile, bold title, grey subtitle, optional pill action and a close ×.</summary>
internal sealed class DockToastCard : Border
{
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly DispatcherTimer _timer;
    private ToastLifetime? _lifetime;

    /// <summary>Real blurred glass behind this card, when enabled.</summary>
    internal GlassSurface? Glass { get; set; }

    /// <summary>Over real glass: a lighter frosted tint and a softer shadow so the blur shows through.</summary>
    public void UseGlassTint()
    {
        Background = Frozen(new SolidColorBrush(Color.FromArgb(0xB8, 0x16, 0x16, 0x1A)));
        if (Effect is DropShadowEffect shadow)
        {
            shadow.Opacity = 0.22;
            shadow.ShadowDepth = 8;
            shadow.BlurRadius = 30;
        }
    }

    public DockToastCard(DockToastRequest request, string lang)
    {
        Request = request;
        Slide = new TranslateTransform();
        Zoom = new ScaleTransform(1, 1);
        RenderTransform = new TransformGroup { Children = { Zoom, Slide } };
        RenderTransformOrigin = new Point(0.5, 0.5);
        MinWidth = DockToastWindow.CardMinW;
        MaxWidth = DockToastWindow.CardMaxW;
        Focusable = false;
        AutomationProperties.SetName(this, request.Title);
        AutomationProperties.SetHelpText(this, request.Subtitle ?? string.Empty);

        // Dark frosted fallback: #16161A at ~92%, white 9% hairline, top highlight, big soft shadow.
        CornerRadius = new CornerRadius(16);
        Background = Frozen(new SolidColorBrush(Color.FromArgb(0xEB, 0x16, 0x16, 0x1A)));
        BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)));
        BorderThickness = new Thickness(1);
        Effect = new DropShadowEffect { BlurRadius = 44, ShadowDepth = 14, Direction = 270, Opacity = 0.45, Color = Colors.Black, RenderingBias = RenderingBias.Quality };

        var root = new Grid();
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(15),
            IsHitTestVisible = false,
            Background = Frozen(new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF), 0),
                    new(Color.FromArgb(0x05, 0xFF, 0xFF, 0xFF), 0.45),
                    new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
                }, new Point(0, 0), new Point(0, 1)))
        });
        // Crisp 1px light line along the top edge, fading toward the sides.
        root.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(18, 0, 18, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Background = Frozen(new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                    new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.5),
                    new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1)
                }, new Point(0, 0), new Point(1, 0)))
        });

        var row = new Grid { Margin = new Thickness(12, 12, 10, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!string.IsNullOrWhiteSpace(request.AccentHex))
        {
            var accent = ParseColor(request.AccentHex!, Color.FromRgb(0x0A, 0x84, 0xFF));
            var tile = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Frozen(new LinearGradientBrush(Lighten(accent, 0.22), Darken(accent, 0.12), new Point(0, 0), new Point(0, 1))),
                BorderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF))),
                BorderThickness = new Thickness(0.8),
                Child = new TextBlock
                {
                    Text = string.IsNullOrEmpty(request.IconGlyph) ? "" : request.IconGlyph,
                    FontFamily = IconFont,
                    FontSize = 17,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(tile, 0);
            row.Children.Add(tile);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        text.Children.Add(new TextBlock
        {
            Text = request.Title,
            FontFamily = DisplayFont,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF6))),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(request.Subtitle))
        {
            text.Children.Add(new TextBlock
            {
                Text = request.Subtitle,
                FontFamily = TextFont,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3))),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (!string.IsNullOrWhiteSpace(request.ActionLabel))
        {
            var pill = HoverFill(new Border
            {
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(13, 5, 13, 6),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = request.ActionLabel,
                    FontFamily = TextFont,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF6)))
                }
            }, rest: 0x1F, hover: 0x2E);
            AutomationProperties.SetName(pill, request.ActionLabel);
            pill.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ActionInvoked?.Invoke(this, EventArgs.Empty);
            };
            Grid.SetColumn(pill, 2);
            row.Children.Add(pill);
        }

        var close = HoverFill(new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 9,
                Foreground = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x94))),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        }, rest: 0x00, hover: 0x16);
        AutomationProperties.SetName(close, DinamikAda.Core.Localization.Strings.Close(lang));
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        Grid.SetColumn(close, 3);
        row.Children.Add(close);

        root.Children.Add(row);
        Child = root;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (_lifetime is null || IsLeaving) return;
            var now = DateTimeOffset.UtcNow;
            if (_lifetime.IsExpired(now)) Expired?.Invoke(this, EventArgs.Empty);
            else if (_lifetime.IsRunning) Arm(_lifetime.RemainingAt(now));
        };
    }

    public DockToastRequest Request { get; }
    public TranslateTransform Slide { get; }
    public ScaleTransform Zoom { get; }
    public bool IsLeaving { get; set; }

    public event EventHandler? CloseRequested;
    public event EventHandler? ActionInvoked;
    public event EventHandler? Expired;

    public void StartLifetime()
    {
        _lifetime = new ToastLifetime(Request.Lifetime ?? DockToastPlanner.DefaultLifetime, DateTimeOffset.UtcNow);
        Arm(_lifetime.RemainingAt(DateTimeOffset.UtcNow));
    }

    public void PauseLifetime()
    {
        if (_lifetime is null) return;
        _lifetime.Pause(DateTimeOffset.UtcNow);
        _timer.Stop();
    }

    public void ResumeLifetime()
    {
        if (_lifetime is null || IsLeaving) return;
        var now = DateTimeOffset.UtcNow;
        _lifetime.Resume(now);
        Arm(_lifetime.RemainingAt(now));
    }

    public void StopLifetime() => _timer.Stop();

    private void Arm(TimeSpan remaining)
    {
        _timer.Stop();
        _timer.Interval = remaining < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : remaining;
        _timer.Start();
    }

    private static Border HoverFill(Border b, byte rest, byte hover)
    {
        var brush = new SolidColorBrush(Color.FromArgb(rest, 0xFF, 0xFF, 0xFF));
        b.Background = brush;
        b.MouseEnter += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Color.FromArgb(hover, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(120)) { EasingFunction = Motion.Soft });
        b.MouseLeave += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Color.FromArgb(rest, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(200)) { EasingFunction = Motion.Soft });
        return b;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        if (f.CanFreeze) f.Freeze();
        return f;
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    private static Color Darken(Color c, double t) => Color.FromRgb(
        (byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));
}
