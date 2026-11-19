using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DinamikAda.Core.Voice;
using DinamikAda.Platform.Win32.Interop;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfColor = System.Windows.Media.Color;

namespace DinamikAda.App.Views.Voice;

/// <summary>
/// "Circle to ask" overlay: a frozen frame of one monitor, a glowing lasso that follows the mouse while the chord is
/// held, then a rounded frame around the chosen region and an answer card next to it.
/// The window never activates while the chord is held (WS_EX_NOACTIVATE), so the global hook keeps seeing the
/// modifier key-ups; it becomes focusable only after release, for Esc and the card buttons.
/// It is excluded from screen capture and never writes the screenshot anywhere.
/// </summary>
public sealed class CircleAskOverlay : Window
{
    /// <summary>A frozen monitor frame in physical pixels (top-down BGRA).</summary>
    public sealed record FrozenScreen(byte[] Bgra, int LeftPx, int TopPx, int WidthPx, int HeightPx, double DpiScale, BitmapSource Image);

    private const double CardWidthDip = 380;
    private const double CornerDip = 18;

    private static readonly WpfColor Cyan = WpfColor.FromRgb(0x64, 0xD2, 0xFF);
    private static readonly WpfColor Blue = WpfColor.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly WpfColor Purple = WpfColor.FromRgb(0xBF, 0x5A, 0xF2);

    private readonly FrozenScreen _screen;
    private readonly Func<string, string, string> _t;
    private readonly Grid _root = new() { Background = System.Windows.Media.Brushes.Transparent, ClipToBounds = true };
    private readonly Canvas _canvas = new() { ClipToBounds = false };
    private readonly System.Windows.Shapes.Path _dim;
    private readonly RectangleGeometry _fullGeom = new();
    private readonly RectangleGeometry _holeGeom = new(Rect.Empty, CornerDip, CornerDip);
    private readonly System.Windows.Shapes.Rectangle _vignette;
    private readonly System.Windows.Shapes.Path _glowPath;
    private readonly System.Windows.Shapes.Path _strokePath;
    private readonly System.Windows.Shapes.Path _frame;
    private readonly Ellipse _tip;
    private readonly Border _hint;
    private readonly Border _card;
    private readonly DispatcherTimer _trackTimer;
    private readonly List<AskPoint> _hoverPx = new();
    private readonly List<AskPoint> _strokePx = new();
    private DispatcherTimer? _typeTimer;
    private bool _tracking;
    private bool _interactive;
    private bool _cardShown;
    private nint _hwnd;

    /// <summary>Raised once when the overlay starts closing (Esc, click outside, Kapat or <see cref="Dismiss"/>).</summary>
    public event Action? Dismissed;

    public bool IsDismissed { get; private set; }
    public int WidthPx => _screen.WidthPx;
    public int HeightPx => _screen.HeightPx;
    /// <summary>Physical pixels per DIP on this monitor.</summary>
    public double DpiScale => _screen.DpiScale;

    public CircleAskOverlay(FrozenScreen screen, Func<string, string, string> t)
    {
        _screen = screen;
        _t = t;

        Title = "DinamikAda Circle";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = true;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = screen.LeftPx / screen.DpiScale;
        Top = screen.TopPx / screen.DpiScale;
        Width = screen.WidthPx / screen.DpiScale;
        Height = screen.HeightPx / screen.DpiScale;
        Opacity = 0;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // Frozen frame.
        var image = new System.Windows.Controls.Image { Source = screen.Image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _root.Children.Add(image);

        // Dim layer with a rounded hole that opens around the region.
        _dim = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(WpfColor.FromRgb(0x04, 0x05, 0x08)),
            Opacity = 0.18,
            Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { _fullGeom, _holeGeom } },
            IsHitTestVisible = false
        };
        _root.Children.Add(_dim);

        // Soft animated vignette.
        var vignetteBrush = new RadialGradientBrush
        {
            RadiusX = 0.78,
            RadiusY = 0.78,
            GradientStops =
            {
                new GradientStop(WpfColor.FromArgb(0, 0, 0, 0), 0.55),
                new GradientStop(WpfColor.FromArgb(0x30, 0x10, 0x18, 0x40), 0.8),
                new GradientStop(WpfColor.FromArgb(0x90, 0x02, 0x03, 0x08), 1.0)
            }
        };
        _vignette = new System.Windows.Shapes.Rectangle { Fill = vignetteBrush, IsHitTestVisible = false, Opacity = 0.75 };
        _root.Children.Add(_vignette);

        // Lasso: blurred glow underneath, crisp gradient stroke on top, and a glowing pen tip.
        _glowPath = new System.Windows.Shapes.Path
        {
            Stroke = AccentBrush(spin: true),
            StrokeThickness = 14,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.5,
            Effect = new BlurEffect { Radius = 14, RenderingBias = RenderingBias.Performance },
            IsHitTestVisible = false
        };
        _strokePath = new System.Windows.Shapes.Path
        {
            Stroke = AccentBrush(spin: true),
            StrokeThickness = 4.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 },
            IsHitTestVisible = false
        };
        _tip = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = new RadialGradientBrush(Colors.White, Cyan),
            Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 16, ShadowDepth = 0, Opacity = 1 },
            RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Opacity = 0,
            IsHitTestVisible = false
        };
        _frame = new System.Windows.Shapes.Path
        {
            Data = _holeGeom,
            Stroke = AccentBrush(spin: true),
            StrokeThickness = 3,
            Opacity = 0,
            Effect = new DropShadowEffect { Color = Blue, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.95 },
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_glowPath);
        _canvas.Children.Add(_strokePath);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_tip);
        _root.Children.Add(_canvas);

        _hint = BuildHint();
        _root.Children.Add(_hint);

        _card = new Border
        {
            Width = CardWidthDip,
            CornerRadius = new CornerRadius(CornerDip),
            Padding = new Thickness(18, 16, 18, 14),
            Background = new LinearGradientBrush(WpfColor.FromArgb(0xF4, 0x1C, 0x1E, 0x26), WpfColor.FromArgb(0xF6, 0x0E, 0x0F, 0x14), 90),
            BorderBrush = new LinearGradientBrush(WpfColor.FromArgb(0x55, 0x64, 0xD2, 0xFF), WpfColor.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 45),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 36, ShadowDepth = 8, Opacity = 0.55, Direction = 270 },
            Opacity = 0,
            Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } }
        };
        _canvas.Children.Add(_card);

        Content = _root;

        _trackTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _trackTimer.Tick += (_, _) => SampleCursor();

        SourceInitialized += (_, _) => OnSourceInitialized();
        Loaded += (_, _) =>
        {
            PlaceOnMonitor();
            Fade(this, OpacityProperty, 1, 140);
            StartAmbientAnimations();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Send, PlaceOnMonitor);
        SizeChanged += (_, _) => _fullGeom.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _interactive)
            {
                e.Handled = true;
                Dismiss(quick: false);
            }
        };
        _root.MouseLeftButtonDown += (_, e) =>
        {
            if (!_interactive || IsDismissed) return;
            if (_card.IsVisible && IsInside(_card, e.GetPosition(_card))) return;
            e.Handled = true;
            Dismiss(quick: false);
        };
        Closed += (_, _) =>
        {
            _trackTimer.Stop();
            _typeTimer?.Stop();
        };
    }

    // ---------------------------------------------------------------- capture

    /// <summary>Freezes the monitor under the mouse cursor (physical pixels). Null when capture fails.</summary>
    public static unsafe FrozenScreen? CaptureMonitorUnderCursor()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return null;
        nint hMon = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon == 0) return null;
        var info = new NativeMethods.MONITORINFOEXW { cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW) };
        if (!NativeMethods.GetMonitorInfoW(hMon, ref info)) return null;
        int x = info.rcMonitor.Left, y = info.rcMonitor.Top, w = info.rcMonitor.Width, h = info.rcMonitor.Bottom - info.rcMonitor.Top;
        if (w <= 0 || h <= 0) return null;
        double scale = 1.0;
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-8.1 fallback: 96 DPI.
        }

        byte[]? pixels = CaptureBgra(x, y, w, h);
        if (pixels is null) return null;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        bmp.Freeze();
        return new FrozenScreen(pixels, x, y, w, h, scale, bmp);
    }

    private static byte[]? CaptureBgra(int x, int y, int w, int h)
    {
        nint screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0) return null;
        nint memDc = 0, hBmp = 0, old = 0;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == 0) return null;
            var bmi = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };
            hBmp = NativeMethods.CreateDIBSection(screenDc, ref bmi, 0, out nint bits, 0, 0);
            if (hBmp == 0 || bits == 0) return null;
            old = NativeMethods.SelectObject(memDc, hBmp);
            if (!NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT)) return null;
            int len = w * h * 4;
            var pixels = new byte[len];
            Marshal.Copy(bits, pixels, 0, len);
            for (int i = 3; i < len; i += 4) pixels[i] = 0xFF;
            return pixels;
        }
        finally
        {
            if (old != 0 && memDc != 0) NativeMethods.SelectObject(memDc, old);
            if (hBmp != 0) NativeMethods.DeleteObject(hBmp);
            if (memDc != 0) NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    /// <summary>Crops the frozen frame to <paramref name="regionPx"/> and encodes PNG in memory (long side ≤ 2048 px).</summary>
    public Task<byte[]> CropPngAsync(AskRect regionPx)
    {
        var src = _screen;
        return Task.Run(() =>
        {
            int x = Math.Clamp((int)Math.Floor(regionPx.X), 0, src.WidthPx - 1);
            int y = Math.Clamp((int)Math.Floor(regionPx.Y), 0, src.HeightPx - 1);
            int w = Math.Clamp((int)Math.Ceiling(regionPx.Right) - x, 1, src.WidthPx - x);
            int h = Math.Clamp((int)Math.Ceiling(regionPx.Bottom) - y, 1, src.HeightPx - y);
            BitmapSource bmp = new CroppedBitmap(src.Image, new Int32Rect(x, y, w, h));
            double longSide = Math.Max(w, h);
            if (longSide > 2048)
            {
                double s = 2048 / longSide;
                bmp = new TransformedBitmap(bmp, new ScaleTransform(s, s));
            }
            bmp.Freeze();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        });
    }

    // ---------------------------------------------------------------- window plumbing

    private const int WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        nint ex = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        try { SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE); }
        catch (EntryPointNotFoundException) { }
        PlaceOnMonitor();
    }

    /// <summary>Pins the window to the captured monitor in physical pixels (correct under per-monitor DPI).</summary>
    private void PlaceOnMonitor()
    {
        if (_hwnd == 0 || IsDismissed) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _screen.LeftPx, _screen.TopPx, _screen.WidthPx, _screen.HeightPx,
            NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>After the chord is released: allow focus so Esc and the buttons work.</summary>
    private void BecomeInteractive()
    {
        if (_interactive || _hwnd == 0) return;
        _interactive = true;
        nint ex = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, ex & ~(nint)NativeMethods.WS_EX_NOACTIVATE);
        NativeMethods.SetForegroundWindow(_hwnd);
        Activate();
        Focus();
    }

    private double PxToDip => ActualWidth > 0 ? ActualWidth / _screen.WidthPx : 1.0 / _screen.DpiScale;

    private Rect ToDip(AskRect r)
    {
        double k = PxToDip;
        return new Rect(r.X * k, r.Y * k, Math.Max(0, r.Width * k), Math.Max(0, r.Height * k));
    }

    // ---------------------------------------------------------------- tracking

    /// <summary>Brightens the lasso glow with the speaker's voice while tracking (0..1).</summary>
    public void SetVoiceLevel(double level01)
    {
        if (!_tracking) return;
        double target = 0.55 + 0.45 * Math.Clamp(level01 * 1.6, 0, 1);
        _glowPath.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(90)),
            HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Starts following the mouse (hover path, or the left-button stroke when the button is held).</summary>
    public void BeginTracking()
    {
        _tracking = true;
        _hoverPx.Clear();
        _strokePx.Clear();
        SampleCursor();
        _trackTimer.Start();
    }

    /// <summary>Stops following the mouse and returns the path in screenshot pixels.</summary>
    public IReadOnlyList<AskPoint> StopTracking()
    {
        if (_tracking) SampleCursor();
        _tracking = false;
        _trackTimer.Stop();
        return (_strokePx.Count > 1 ? _strokePx : _hoverPx).ToList();
    }

    private void SampleCursor()
    {
        if (!_tracking || !NativeMethods.GetCursorPos(out var c)) return;
        var p = new AskPoint(
            Math.Clamp(c.X - _screen.LeftPx, 0, _screen.WidthPx - 1),
            Math.Clamp(c.Y - _screen.TopPx, 0, _screen.HeightPx - 1));
        bool button = (GetAsyncKeyState(0x01) & 0x8000) != 0;
        double minGap = 2 * _screen.DpiScale;
        bool changed = Append(_hoverPx, p, minGap);
        if (button) changed |= Append(_strokePx, p, minGap);
        if (changed) RenderLasso(p);
    }

    private static bool Append(List<AskPoint> list, AskPoint p, double minGap)
    {
        if (list.Count > 0)
        {
            var last = list[^1];
            double dx = p.X - last.X, dy = p.Y - last.Y;
            if (dx * dx + dy * dy < minGap * minGap) return false;
        }
        if (list.Count >= 4000) list.RemoveAt(0);
        list.Add(p);
        return true;
    }

    private void RenderLasso(AskPoint tipPx)
    {
        var src = _strokePx.Count > 1 ? _strokePx : _hoverPx;
        double k = PxToDip;
        var smooth = AskGeometry.SmoothChaikin(AskGeometry.Decimate(src, 3 * _screen.DpiScale), 2);
        var geo = new StreamGeometry();
        if (smooth.Count > 1)
        {
            using var ctx = geo.Open();
            ctx.BeginFigure(new WpfPoint(smooth[0].X * k, smooth[0].Y * k), false, false);
            var pts = new List<WpfPoint>(smooth.Count - 1);
            for (int i = 1; i < smooth.Count; i++) pts.Add(new WpfPoint(smooth[i].X * k, smooth[i].Y * k));
            ctx.PolyLineTo(pts, true, true);
        }
        geo.Freeze();
        _strokePath.Data = geo;
        _glowPath.Data = geo;
        Canvas.SetLeft(_tip, tipPx.X * k - _tip.Width / 2);
        Canvas.SetTop(_tip, tipPx.Y * k - _tip.Height / 2);
        if (_tip.Opacity == 0) Fade(_tip, OpacityProperty, 1, 160);
    }

    // ---------------------------------------------------------------- region and cards

    /// <summary>Closes the lasso into a rounded frame around the region and dims everything else.</summary>
    public void ShowRegion(AskRect regionPx)
    {
        BecomeInteractive();
        var target = ToDip(regionPx);
        var start = _strokePath.Data is { Bounds: { IsEmpty: false } b } && b.Width > 4 && b.Height > 4 ? b : target;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _holeGeom.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(start, target, Ms(460)) { EasingFunction = ease });
        Fade(_frame, OpacityProperty, 1, 260);
        Fade(_strokePath, OpacityProperty, 0, 320);
        Fade(_glowPath, OpacityProperty, 0, 260);
        Fade(_tip, OpacityProperty, 0, 160);
        Fade(_hint, OpacityProperty, 0, 200);
        _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0.58, Ms(460)) { EasingFunction = ease });
        Fade(_vignette, OpacityProperty, 0.45, 460);

        // Gentle breathing glow on the frame.
        if (_frame.Effect is DropShadowEffect glow)
        {
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(12, 26, Ms(1300))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = At(460),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
    }

    /// <summary>Shows the shimmering "thinking" card next to the region.</summary>
    public void ShowThinking(AskRect regionPx, string text)
    {
        var shimmer = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0.5),
            EndPoint = new WpfPoint(1, 0.5),
            GradientStops =
            {
                new GradientStop(WpfColor.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(WpfColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(WpfColor.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1.0)
            },
            RelativeTransform = new TranslateTransform()
        };
        ((TranslateTransform)shimmer.RelativeTransform).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-1, 1, Ms(1400)) { RepeatBehavior = RepeatBehavior.Forever });

        var spark = Sparkle(22);
        var title = new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = shimmer,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { spark, title } };
        var body = new StackPanel { Children = { head } };
        foreach (double w in new[] { 1.0, 0.82, 0.6 })
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Height = 9,
                RadiusX = 4.5,
                RadiusY = 4.5,
                Width = (CardWidthDip - 36) * w,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 12, 0, 0),
                Fill = shimmer.Clone(),
                Opacity = 0.22
            };
            ((TranslateTransform)((LinearGradientBrush)bar.Fill).RelativeTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-1, 1, Ms(1400)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = At(120 * (1 - w) * 5) });
            body.Children.Add(bar);
        }
        SetCardContent(body, regionPx);
    }

    /// <summary>Shows the answer card (question on top, answer typing itself in, Kopyala / Kapat).</summary>
    public void ShowAnswer(AskRect regionPx, AskAnswer answer)
    {
        if (IsDismissed) return;
        var body = new StackPanel();
        if (!string.IsNullOrWhiteSpace(answer.Question))
        {
            body.Children.Add(new TextBlock
            {
                Text = "“" + answer.Question.Trim() + "”",
                FontSize = 12.5,
                FontStyle = FontStyles.Italic,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(0xB0, 0xC8, 0xD4, 0xE8)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            body.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 0, 0, 10),
                Background = new LinearGradientBrush(WpfColor.FromArgb(0x60, 0x64, 0xD2, 0xFF), WpfColor.FromArgb(0x00, 0x64, 0xD2, 0xFF), 0)
            });
        }

        var answerText = new TextBlock
        {
            Text = answer.Answer,
            FontSize = 14.5,
            LineHeight = 21,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0xF2, 0xF4, 0xF8)),
            TextWrapping = TextWrapping.Wrap
        };
        double maxScroll = Math.Max(120, ActualHeight - 200);
        var scroll = new ScrollViewer
        {
            Content = answerText,
            MaxHeight = maxScroll,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        body.Children.Add(scroll);

        var copy = CardButton(_t("Kopyala", "Copy"), primary: false);
        var close = CardButton(_t("Kapat", "Close"), primary: true);
        copy.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (TryCopy(answer.Answer) && copy.Child is TextBlock tb)
            {
                tb.Text = _t("Kopyalandı ✓", "Copied ✓");
                var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
                revert.Tick += (_, _) => { revert.Stop(); tb.Text = _t("Kopyala", "Copy"); };
                revert.Start();
            }
        };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismiss(quick: false); };
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { copy, close }
        });

        // Lock the final height before typing so the card never jumps.
        answerText.Measure(new WpfSize(CardWidthDip - 38, double.PositiveInfinity));
        answerText.MinHeight = answerText.DesiredSize.Height;
        SetCardContent(body, regionPx);
        StartTyping(answerText, answer.Answer);
        _card.MouseLeftButtonDown += (_, _) => FinishTyping(answerText, answer.Answer);
    }

    private void SetCardContent(UIElement content, AskRect regionPx)
    {
        _card.Child = content;
        _card.Visibility = Visibility.Visible;
        _card.Measure(new WpfSize(CardWidthDip, double.PositiveInfinity));
        double h = _card.DesiredSize.Height;
        var region = ToDip(regionPx);
        var place = AskGeometry.PlaceCard(new AskRect(region.X, region.Y, region.Width, region.Height),
            CardWidthDip, h, ActualWidth, ActualHeight);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (!_cardShown)
        {
            _cardShown = true;
            Canvas.SetLeft(_card, place.X);
            Canvas.SetTop(_card, place.Y);
            var group = (TransformGroup)_card.RenderTransform;
            var scale = (ScaleTransform)group.Children[0];
            var shift = (TranslateTransform)group.Children[1];
            (double dx, double dy) = place.Side switch
            {
                AskCardSide.Right => (-14.0, 0.0),
                AskCardSide.Left => (14.0, 0.0),
                AskCardSide.Below => (0.0, -14.0),
                AskCardSide.Above => (0.0, 14.0),
                _ => (0.0, 10.0)
            };
            var back = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, Ms(340)) { EasingFunction = back });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, Ms(340)) { EasingFunction = back });
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, Ms(340)) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, Ms(340)) { EasingFunction = ease });
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(240)) { BeginTime = At(180) });
        }
        else
        {
            _card.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(place.X, Ms(300)) { EasingFunction = ease });
            _card.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(place.Y, Ms(300)) { EasingFunction = ease });
            content.Opacity = 0;
            Fade(content, OpacityProperty, 1, 220);
        }
    }

    private void StartTyping(TextBlock target, string full)
    {
        _typeTimer?.Stop();
        if (full.Length == 0) return;
        target.Text = string.Empty;
        double totalMs = Math.Clamp(full.Length * 9.0, 350, 1400);
        int ticks = Math.Max(1, (int)(totalMs / 16));
        int perTick = Math.Max(1, (int)Math.Ceiling(full.Length / (double)ticks));
        int shown = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            shown = Math.Min(full.Length, shown + perTick);
            target.Text = full[..shown];
            if (shown >= full.Length) timer.Stop();
        };
        _typeTimer = timer;
        timer.Start();
    }

    private void FinishTyping(TextBlock target, string full)
    {
        if (_typeTimer is { IsEnabled: true })
        {
            _typeTimer.Stop();
            target.Text = full;
        }
    }

    /// <summary>Fades the overlay out and closes it. Safe to call more than once.</summary>
    public void Dismiss(bool quick)
    {
        if (IsDismissed) return;
        IsDismissed = true;
        _tracking = false;
        _trackTimer.Stop();
        _typeTimer?.Stop();
        try { Dismissed?.Invoke(); }
        catch (Exception ex) { Services.CrashLogger.Log("Voice", ex, "Circle overlay dismiss handler failed."); }
        if (!IsLoaded)
        {
            Close();
            return;
        }
        var fade = new DoubleAnimation(0, Ms(quick ? 120 : 220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        if (!quick && _card.IsVisible && _card.RenderTransform is TransformGroup g && g.Children[0] is ScaleTransform s)
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, Ms(220)));
            s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, Ms(220)));
        }
    }

    // ---------------------------------------------------------------- visuals

    private void StartAmbientAnimations()
    {
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
        _vignette.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0.95, Ms(2400))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = sine
        });
        _glowPath.BeginAnimation(Shape.StrokeThicknessProperty, new DoubleAnimation(11, 19, Ms(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = sine
        });
        var tipScale = (ScaleTransform)_tip.RenderTransform;
        var pulse = new DoubleAnimation(0.8, 1.25, Ms(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine };
        tipScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        tipScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);

        var shift = (TranslateTransform)_hint.RenderTransform;
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-18, 0, Ms(380)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
        _hint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(260)));
    }

    private Border BuildHint()
    {
        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = AccentBrush(spin: false),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 10, ShadowDepth = 0 }
        };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, Ms(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var text = new TextBlock
        {
            Text = _t("Bir şeyin etrafına daire çiz ve sorunu söyle", "Circle something and ask your question"),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0xF4, 0xF6, 0xFA)),
            FontSize = 14,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 28, 16, 0),
            Padding = new Thickness(18, 10, 20, 10),
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(WpfColor.FromArgb(0xE0, 0x16, 0x18, 0x1F)),
            BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Direction = 270 },
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransform = new TranslateTransform(),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } }
        };
    }

    private static FrameworkElement Sparkle(double size)
    {
        var e = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = AccentBrush(spin: true),
            RenderTransformOrigin = new WpfPoint(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Effect = new DropShadowEffect { Color = Purple, BlurRadius = 14, ShadowDepth = 0 },
            VerticalAlignment = VerticalAlignment.Center
        };
        var s = (ScaleTransform)e.RenderTransform;
        var a = new DoubleAnimation(0.75, 1.05, Ms(650))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        return e;
    }

    private static Border CardButton(string label, bool primary)
    {
        var idle = primary
            ? (System.Windows.Media.Brush)new LinearGradientBrush(Blue, Purple, 0)
            : new SolidColorBrush(WpfColor.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        var b = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(14, 6, 14, 7),
            Margin = new Thickness(8, 0, 0, 0),
            Background = idle,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White
            }
        };
        b.MouseEnter += (_, _) => Fade(b, OpacityProperty, 0.82, 120);
        b.MouseLeave += (_, _) => Fade(b, OpacityProperty, 1, 160);
        return b;
    }

    private static LinearGradientBrush AccentBrush(bool spin)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new WpfPoint(0, 0),
            EndPoint = new WpfPoint(1, 1),
            GradientStops = { new GradientStop(Cyan, 0), new GradientStop(Blue, 0.5), new GradientStop(Purple, 1) }
        };
        if (spin)
        {
            var rot = new RotateTransform(0, 0.5, 0.5);
            brush.RelativeTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(6)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        return brush;
    }

    private static bool TryCopy(string text)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        return false;
    }

    private static bool IsInside(FrameworkElement el, WpfPoint p) =>
        p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;

    private static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    private static TimeSpan At(double ms) => TimeSpan.FromMilliseconds(ms);

    private static void Fade(UIElement el, DependencyProperty prop, double to, double ms) =>
        el.BeginAnimation(prop, new DoubleAnimation(to, Ms(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
}
