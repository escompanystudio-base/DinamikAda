using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DinamikAda.App.Services;
using DinamikAda.App.Services.Voice;
using DinamikAda.Core.Localization;
using DinamikAda.Core.Voice;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;
using DinamikAda.App.Views.DockWidgets;

namespace DinamikAda.App.Views.Voice;

/// <summary>
/// Floating assistant overlay: a glass pill with the mascot, live waveform and status near the bottom centre of the
/// monitor under the cursor. The window never activates (dictation types into the focused app) and is click-through
/// except while a sticky answer bubble is shown.
/// </summary>
public sealed class VoiceOverlayWindow : IVoiceOverlay, IDisposable
{
    private const double WindowW = 660, WindowH = 470;
    private const double BottomOffsetDip = 110;
    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

    private readonly AppOrchestrator _orch;
    private readonly DispatcherTimer _autoHide = new(DispatcherPriority.Normal);
    private Window? _window;
    private VoiceOverlayView? _view;
    private GlassSurface? _pillGlass;
    private GlassSurface? _bubbleGlass;
    private nint _hwnd;
    private bool _clickThrough = true;
    private int _showGeneration;
    private bool _disposed;

    public VoiceOverlayWindow(AppOrchestrator orch)
    {
        _orch = orch;
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); Hide(); };
    }

    private bool Disabled => _disposed || _orch.IsSmokeOrAuditMode;

    public void ShowListening(VoiceMode mode, string hint)
    {
        if (Disabled) return;
        var view = Prepare(reposition: true);
        view.ShowListening(mode, hint);
        Present(clickable: false, autoHide: null);
    }

    public void SetLevel(double level01)
    {
        if (Disabled || _view is null) return;
        _view.SetLevel(level01);
    }

    public void ShowWorking(string text)
    {
        if (Disabled) return;
        var view = Prepare(reposition: !IsShown);
        view.ShowWorking(text);
        Present(clickable: false, autoHide: null);
    }

    public void ShowResult(string title, string text, TimeSpan? autoHide)
    {
        if (Disabled) return;
        var view = Prepare(reposition: !IsShown);
        bool sticky = autoHide is null;
        view.ShowResult(title, text, sticky);
        Present(clickable: sticky, autoHide);
    }

    public void ShowError(string text)
    {
        if (Disabled) return;
        var view = Prepare(reposition: !IsShown);
        view.ShowError(text);
        Present(clickable: false, TimeSpan.FromSeconds(3.5));
    }

    public void Hide()
    {
        _autoHide.Stop();
        if (_window is null || _view is null || !_window.IsVisible) return;
        int gen = ++_showGeneration;
        SetClickThrough(true);
        _view.AnimateOut(() =>
        {
            if (gen != _showGeneration || _window is null) return;
            _window.Hide();
            _view?.Reset();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoHide.Stop();
        _pillGlass?.Dispose();
        _bubbleGlass?.Dispose();
        try { _window?.Close(); } catch (InvalidOperationException) { }
        _window = null;
        _view = null;
    }

    private bool IsShown => _window?.IsVisible == true;

    private VoiceOverlayView Prepare(bool reposition)
    {
        EnsureWindow();
        var cfg = _orch.Config;
        _view!.Lang = cfg.Language;
        _view.ApplyMascot(cfg.Mascot ?? new MascotSettings());
        if (reposition) PositionOnCursorMonitor();
        return _view;
    }

    private void Present(bool clickable, TimeSpan? autoHide)
    {
        _showGeneration++;
        _autoHide.Stop();
        SetClickThrough(!clickable);
        if (!_window!.IsVisible)
        {
            _window.Show();
            _view!.AnimateIn();
        }
        else
        {
            _view!.AnimateIn(onlyIfHidden: true);
        }
        if (autoHide is { } t && t > TimeSpan.Zero)
        {
            _autoHide.Interval = t;
            _autoHide.Start();
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;
        _view = new VoiceOverlayView();
        _view.CloseRequested += (_, _) => Hide();
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
            Title = "DinamikAda Voice",
            Content = _view
        };
        _window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            ApplyExStyle();
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        AttachGlass(_window, _view);
        _window.DpiChanged += (_, _) => _window.Dispatcher.BeginInvoke(PositionOnCursorMonitorKeepMonitor, DispatcherPriority.Render);
        new WindowInteropHelper(_window).EnsureHandle();
    }

    /// <summary>Real blurred glass behind the pill and the answer bubble (when Windows transparency effects are on).</summary>
    private void AttachGlass(Window window, VoiceOverlayView view)
    {
        if (!GlassPolicy.IsEnabled) return;
        bool Allowed() => !Disabled && GlassPolicy.IsEnabled && window.IsVisible;
        _pillGlass = new GlassSurface(view.PillSurface, GlassBackdropMode.Acrylic, () => 32, Allowed);
        _bubbleGlass = new GlassSurface(view.BubbleSurface, GlassBackdropMode.Acrylic, () => 20, Allowed);
        foreach (var (surface, el) in new[] { (_pillGlass, view.PillSurface), (_bubbleGlass, view.BubbleSurface) })
        {
            surface.WatchAnimations(view);
            surface.WatchAnimations(el);
            el.IsVisibleChanged += (_, _) => surface.TrackFor(300);
        }
        view.SetGlass(true);
        window.IsVisibleChanged += (_, _) =>
        {
            if (!window.IsVisible)
            {
                _pillGlass?.Hide();
                _bubbleGlass?.Hide();
                return;
            }
            _pillGlass?.TrackFor(600);
            _bubbleGlass?.TrackFor(600);
            if (_pillGlass is { HasFailed: true }) view.SetGlass(false);
        };
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }
        return 0;
    }

    private void SetClickThrough(bool on)
    {
        if (_clickThrough == on) return;
        _clickThrough = on;
        ApplyExStyle();
    }

    private void ApplyExStyle()
    {
        if (_hwnd == 0) return;
        long ex = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        ex = _clickThrough ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~(long)NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(ex));
    }

    private nint _lastMonitor;

    private void PositionOnCursorMonitor()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) pt = default;
        _lastMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        PlaceOn(_lastMonitor);
    }

    private void PositionOnCursorMonitorKeepMonitor()
    {
        if (_lastMonitor != 0) PlaceOn(_lastMonitor);
    }

    private void PlaceOn(nint monitor)
    {
        if (_hwnd == 0 || monitor == 0) return;
        var info = new NativeMethods.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEXW>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return;
        double scale = 1;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;
        int w = (int)Math.Round(WindowW * scale), h = (int)Math.Round(WindowH * scale);
        var mon = info.rcMonitor;
        var work = info.rcWork;
        int bottom = (int)Math.Min(mon.Bottom - BottomOffsetDip * scale, work.Bottom - 16 * scale);
        int x = mon.Left + (mon.Width - w) / 2;
        int y = bottom - h;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_NOACTIVATE);
    }

    // ---- render audit ----------------------------------------------------------------------------------

    /// <summary>Renders the overlay content (no window) in a still pose for the render audit.</summary>
    public static BitmapSource RenderForAudit(string frame, string lang, MascotSettings mascot, double width = 620, double height = 360)
    {
        var view = new VoiceOverlayView { Instant = true, Lang = lang };
        view.ApplyMascot(mascot);
        view.AnimateIn();
        bool en = lang == "en";
        switch (frame)
        {
            case "working":
                view.ShowListening(VoiceMode.Command, "");
                view.ShowWorking(en ? "Planning your routine…" : "Rutinin hazırlanıyor…");
                break;
            case "result":
                view.ShowResult(en ? "Here's what I found" : "İşte bulduklarım",
                    en ? "That chart shows weekly active users rising 18% after the redesign, with the biggest jump on Tuesday. The dip on Saturday is normal for weekends."
                       : "Bu grafik, yeniden tasarımdan sonra haftalık aktif kullanıcıların %18 arttığını gösteriyor; en büyük sıçrama salı günü. Cumartesi günkü düşüş hafta sonları için olağan.",
                    sticky: true);
                break;
            case "error":
                view.ShowError(en ? "No speech detected. Check your microphone." : "Ses algılanmadı. Mikrofonu kontrol et.");
                break;
            default:
                view.ShowListening(VoiceMode.Dictation, en ? "Listening… release to type" : "Dinliyorum… bırakınca yazarım");
                double[] levels = [0.2, 0.45, 0.8, 0.65, 0.9, 0.7];
                foreach (var l in levels) view.SetLevel(l);
                break;
        }

        var host = new Grid
        {
            Width = width,
            Height = height,
            Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x3A, 0x5C), Color.FromRgb(0x5A, 0x3C, 0x6E), 35)
        };
        host.Children.Add(new Border
        {
            Background = new RadialGradientBrush(Color.FromArgb(0x50, 0x7C, 0xD0, 0xFF), Color.FromArgb(0, 0, 0, 0)) { Center = new Point(0.2, 0.2), GradientOrigin = new Point(0.2, 0.2), RadiusX = 0.7, RadiusY = 0.9 }
        });
        view.VerticalAlignment = VerticalAlignment.Bottom;
        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.Margin = new Thickness(0, 0, 0, 36);
        host.Children.Add(view);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();
        return rtb;
    }
}

/// <summary>The overlay's visual content: pill (listening / working / error) or answer bubble, plus the mascot.</summary>
internal sealed class VoiceOverlayView : Grid
{
    private const double MascotSize = 88;
    private const int BarCount = 14;

    private static readonly Color DictationAccent = Color.FromRgb(0x5A, 0xD2, 0xFF);
    private static readonly Color AskAccent = Color.FromRgb(0xC3, 0x8F, 0xFF);
    private static readonly Color CommandAccent = Color.FromRgb(0xFF, 0xB8, 0x4D);
    private static readonly Color ErrorAccent = Color.FromRgb(0xFF, 0x6B, 0x5E);

    private readonly ScaleTransform _rootScale = new(1, 1);
    private readonly Grid _stage = new();
    private readonly MascotControl _mascot = new() { Width = MascotSize, Height = MascotSize };
    private readonly Border _pill = new();
    private readonly Grid _pillInner = new();
    private readonly Rectangle _pillGlow = new() { IsHitTestVisible = false };
    private readonly TextBlock _label = new();
    private readonly TextBlock _status = new();
    private readonly StackPanel _wave = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScaleTransform[] _bars = new ScaleTransform[BarCount];
    private readonly Border[] _barViews = new Border[BarCount];
    private readonly StackPanel _typing = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _errorIcon = new();
    private readonly Grid _bubbleHost = new();
    private readonly Border _bubble = new();
    private readonly Path _tail = new();
    private readonly ScaleTransform _bubbleScale = new(1, 1);
    private readonly TextBlock _bubbleTitle = new();
    private readonly TextBlock _bubbleText = new();
    private readonly ScrollViewer _bubbleScroll = new();
    private readonly Button _close = new();
    private readonly LinearGradientBrush _shimmer;
    private readonly TranslateTransform _shimmerShift = new();

    private double _level;
    private readonly double[] _barLevel = new double[BarCount];
    private int _tick;
    private VoiceMode _mode = VoiceMode.Dictation;
    private string _lang = "tr";
    private bool _mascotEnabled = true;
    private bool _visibleState;

    public event EventHandler? CloseRequested;

    /// <summary>Skip animations and render final poses (render audit).</summary>
    public bool Instant
    {
        get => !_mascot.AnimationsEnabled;
        set => _mascot.AnimationsEnabled = !value;
    }

    public string Lang
    {
        get => _lang;
        set => _lang = value == "en" ? "en" : "tr";
    }

    private bool Animate => !Instant && SystemParameters.ClientAreaAnimation;

    private string T(string tr, string en) => Strings.Pick(_lang, tr, en);

    public VoiceOverlayView()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, 14);
        RenderTransform = _rootScale;
        RenderTransformOrigin = new Point(0.5, 1);
        Opacity = 0;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var uiFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        var textFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        var iconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        // ---- pill ----
        _pill.CornerRadius = new CornerRadius(32);
        _pill.MinHeight = 64;
        _pill.Width = 424;
        _pill.VerticalAlignment = VerticalAlignment.Bottom;
        _pill.Background = GlassBrush(0xF0);
        _pill.BorderThickness = new Thickness(1);
        _pill.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.5, Color = Colors.Black };

        _pillInner.Children.Add(_pillGlow);
        var topSheen = new Border
        {
            CornerRadius = new CornerRadius(31),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            IsHitTestVisible = false
        };
        _pillInner.Children.Add(topSheen);

        var row = new Grid { Margin = new Thickness(88, 10, 20, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _label.FontFamily = uiFont;
        _label.FontSize = 10.5;
        _label.FontWeight = FontWeights.Bold;
        _label.Margin = new Thickness(0, 0, 0, 2);
        _status.FontFamily = uiFont;
        _status.FontSize = 14.5;
        _status.FontWeight = FontWeights.SemiBold;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xFA));
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;
        texts.Children.Add(_label);
        texts.Children.Add(_status);
        row.Children.Add(texts);

        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new ScaleTransform(1, 0.15);
            _barViews[i] = new Border
            {
                Width = 3.2,
                Height = 28,
                CornerRadius = new CornerRadius(1.6),
                Margin = new Thickness(1.4, 0, 1.4, 0),
                RenderTransform = _bars[i],
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            _wave.Children.Add(_barViews[i]);
        }
        Grid.SetColumn(_wave, 1);
        row.Children.Add(_wave);

        for (int i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(2.5, 0, 2.5, 0), RenderTransform = new TranslateTransform() };
            _typing.Children.Add(dot);
        }
        Grid.SetColumn(_typing, 1);
        row.Children.Add(_typing);

        _errorIcon.Width = 30;
        _errorIcon.Height = 30;
        _errorIcon.CornerRadius = new CornerRadius(15);
        _errorIcon.Background = new SolidColorBrush(Color.FromArgb(0x38, ErrorAccent.R, ErrorAccent.G, ErrorAccent.B));
        _errorIcon.Child = new TextBlock
        {
            Text = "",
            FontFamily = iconFont,
            FontSize = 14,
            Foreground = new SolidColorBrush(ErrorAccent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_errorIcon, 1);
        row.Children.Add(_errorIcon);

        _pillInner.Children.Add(row);
        _pillInner.SizeChanged += (_, e) =>
            _pillInner.Clip = new RectangleGeometry(new Rect(e.NewSize), 31, 31);
        _pill.Child = _pillInner;

        _shimmer = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            RelativeTransform = _shimmerShift,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x99, 0xF4, 0xF2, 0xFA), 0.0),
                new GradientStop(Color.FromArgb(0x99, 0xF4, 0xF2, 0xFA), 0.35),
                new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x99, 0xF4, 0xF2, 0xFA), 0.65),
                new GradientStop(Color.FromArgb(0x99, 0xF4, 0xF2, 0xFA), 1.0)
            }
        };

        // ---- bubble ----
        _bubble.CornerRadius = new CornerRadius(20);
        _bubble.Background = GlassBrush(0xF4, lighter: true);
        _bubble.BorderThickness = new Thickness(1);
        _bubble.Padding = new Thickness(18, 13, 14, 16);
        _bubble.MaxWidth = 440;
        _bubble.MinWidth = 220;
        _bubble.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 10, Direction = 270, Opacity = 0.5, Color = Colors.Black };
        var bubbleGrid = new Grid();
        bubbleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bubbleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        _close.Width = 24;
        _close.Height = 24;
        _close.Cursor = Cursors.Hand;
        _close.Focusable = false;
        _close.Margin = new Thickness(10, -3, 0, 0);
        _close.VerticalAlignment = VerticalAlignment.Top;
        _close.Template = CloseButtonTemplate(iconFont);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        System.Windows.Automation.AutomationProperties.SetName(_close, "Close");
        DockPanel.SetDock(_close, Dock.Right);
        header.Children.Add(_close);
        _bubbleTitle.FontFamily = uiFont;
        _bubbleTitle.FontSize = 12.5;
        _bubbleTitle.FontWeight = FontWeights.SemiBold;
        _bubbleTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _bubbleTitle.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_bubbleTitle);
        bubbleGrid.Children.Add(header);
        _bubbleText.FontFamily = textFont;
        _bubbleText.FontSize = 14;
        _bubbleText.LineHeight = 20.5;
        _bubbleText.TextWrapping = TextWrapping.Wrap;
        _bubbleText.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEC, 0xF6));
        _bubbleScroll.Content = _bubbleText;
        _bubbleScroll.MaxHeight = 250;
        _bubbleScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _bubbleScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _bubbleScroll.Focusable = false;
        if (Application.Current?.TryFindResource("ObsidianScrollViewerStyle") is Style sv) _bubbleScroll.Style = sv;
        Grid.SetRow(_bubbleScroll, 1);
        bubbleGrid.Children.Add(_bubbleScroll);
        _bubble.Child = bubbleGrid;
        _bubble.MouseLeftButtonUp += (_, e) =>
        {
            if (_close.Visibility != Visibility.Visible || IsInScrollBar(e.OriginalSource as DependencyObject)) return;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };

        _tail.Data = Geometry.Parse("M12,0 L0,10 L12,17 Z");
        _tail.Fill = new SolidColorBrush(Color.FromArgb(0xF4, 0x1A, 0x18, 0x24));
        _tail.HorizontalAlignment = HorizontalAlignment.Left;
        _tail.VerticalAlignment = VerticalAlignment.Bottom;
        _tail.Margin = new Thickness(-11, 0, 0, 18);
        _bubbleHost.Children.Add(_bubble);
        _bubbleHost.Children.Add(_tail);
        _bubbleHost.VerticalAlignment = VerticalAlignment.Bottom;
        _bubbleHost.HorizontalAlignment = HorizontalAlignment.Left;
        _bubbleHost.RenderTransform = _bubbleScale;
        _bubbleHost.RenderTransformOrigin = new Point(0, 1);
        _bubbleHost.Visibility = Visibility.Collapsed;

        _mascot.HorizontalAlignment = HorizontalAlignment.Left;
        _mascot.VerticalAlignment = VerticalAlignment.Bottom;
        _mascot.Margin = new Thickness(2, 0, 0, -6);

        _stage.Children.Add(_pill);
        _stage.Children.Add(_bubbleHost);
        _stage.Children.Add(_mascot);
        Children.Add(_stage);

        ApplyAccent(DictationAccent, error: false);
        UpdateMascotLayout();
    }

    public void ApplyMascot(MascotSettings settings)
    {
        _mascotEnabled = settings.Enabled;
        _mascot.Apply(settings);
        _mascotName = string.IsNullOrWhiteSpace(settings.Name) ? "Ada" : settings.Name.Trim();
        UpdateMascotLayout();
    }

    private string _mascotName = "Ada";

    private void UpdateMascotLayout()
    {
        _mascot.Visibility = _mascotEnabled ? Visibility.Visible : Visibility.Collapsed;
        _pill.Width = _mascotEnabled ? 424 : 352;
        if (_pillInner.Children[^1] is Grid row) row.Margin = new Thickness(_mascotEnabled ? 88 : 24, 10, 20, 10);
        _bubbleHost.Margin = new Thickness(_mascotEnabled ? MascotSize + 12 : 0, 0, 0, _mascotEnabled ? 10 : 0);
        _tail.Visibility = _mascotEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowListening(VoiceMode mode, string hint)
    {
        _mode = mode;
        var accent = AccentFor(mode);
        ShowPill();
        ApplyAccent(accent, error: false);
        _label.Text = $"{_mascotName.ToUpperInvariant()}  ·  {ModeName(mode).ToUpper(Culture)}";
        SetStatus(string.IsNullOrWhiteSpace(hint) ? T("Dinliyorum…", "Listening…") : hint, shimmer: false);
        _wave.Visibility = Visibility.Visible;
        _typing.Visibility = Visibility.Collapsed;
        _errorIcon.Visibility = Visibility.Collapsed;
        _level = 0;
        Array.Clear(_barLevel);
        for (int i = 0; i < BarCount; i++) { _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, null); _bars[i].ScaleY = 0.15; }
        _mascot.State = MascotState.Listening;
    }

    public void SetLevel(double level01)
    {
        double v = double.IsFinite(level01) ? Math.Clamp(level01, 0, 1) : 0;
        // Fast attack, slower release, then a soft curve so quiet speech still moves the bars.
        _level = v > _level ? _level + (v - _level) * 0.6 : _level + (v - _level) * 0.22;
        double shaped = Math.Pow(_level, 0.7);
        _mascot.SetLevel(v);
        if (_wave.Visibility != Visibility.Visible) return;
        _tick++;
        for (int i = 0; i < BarCount; i++)
        {
            double pos = (i - (BarCount - 1) / 2.0) / (BarCount / 2.0);
            double envelope = 0.35 + 0.65 * Math.Exp(-pos * pos * 1.6);
            // A slow travelling wave instead of per-tick jitter, so the bars ripple softly.
            double wobble = 0.8 + 0.2 * Math.Sin(_tick * 0.32 - i * 0.55);
            double target = Math.Clamp(0.12 + shaped * envelope * wobble * 1.05, 0.12, 1);
            _barLevel[i] = target;
            if (Animate)
            {
                // Rise quickly with the voice, settle slowly: overlapping glides read as one continuous motion.
                bool rising = target > _bars[i].ScaleY;
                _bars[i].BeginAnimation(ScaleTransform.ScaleYProperty,
                    Motion.To(target, rising ? 150 : 380, rising ? Motion.Soft : Motion.Gentle),
                    HandoffBehavior.SnapshotAndReplace);
            }
            else _bars[i].ScaleY = target;
        }
    }

    public void ShowWorking(string text)
    {
        ShowPill();
        ApplyAccent(AccentFor(_mode), error: false);
        _label.Text = $"{_mascotName.ToUpperInvariant()}  ·  {T("Düşünüyor", "Thinking").ToUpper(Culture)}";
        SetStatus(string.IsNullOrWhiteSpace(text) ? T("Bir saniye…", "One moment…") : text, shimmer: true);
        _wave.Visibility = Visibility.Collapsed;
        _errorIcon.Visibility = Visibility.Collapsed;
        _typing.Visibility = Visibility.Visible;
        StartTyping();
        _mascot.State = MascotState.Working;
    }

    public void ShowResult(string title, string text, bool sticky)
    {
        StopTyping();
        var accent = AccentFor(_mode);
        ApplyAccent(accent, error: false);
        _bubbleTitle.Text = string.IsNullOrWhiteSpace(title) ? _mascotName : title;
        _bubbleTitle.Foreground = new SolidColorBrush(Mix(accent, Colors.White, 0.25));
        _bubbleText.Text = text ?? string.Empty;
        _bubbleScroll.ScrollToTop();
        _close.Visibility = sticky ? Visibility.Visible : Visibility.Collapsed;
        _bubble.Cursor = sticky ? Cursors.Hand : null;
        _bubble.BorderBrush = RimBrush(accent, 0x55);

        bool wasHidden = _bubbleHost.Visibility != Visibility.Visible;
        _bubbleHost.Visibility = Visibility.Visible;
        if (Animate)
        {
            Fade(_pill, 0, 140, () => { if (_bubbleHost.Visibility == Visibility.Visible) _pill.Visibility = Visibility.Collapsed; });
            if (wasHidden)
            {
                var spring = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(620))
                {
                    EasingFunction = Motion.Soft
                };
                _bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, spring);
                _bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, spring);
                _bubbleHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            }
        }
        else
        {
            _pill.Visibility = Visibility.Collapsed;
            _bubbleHost.Opacity = 1;
            _bubbleScale.ScaleX = _bubbleScale.ScaleY = 1;
        }
        _mascot.State = MascotState.Talking;
    }

    public void ShowError(string text)
    {
        ShowPill();
        ApplyAccent(ErrorAccent, error: true);
        _label.Text = $"{_mascotName.ToUpperInvariant()}  ·  {T("Bir sorun var", "Something's off").ToUpper(Culture)}";
        SetStatus(text, shimmer: false);
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xAC));
        _status.TextWrapping = TextWrapping.Wrap;
        _status.MaxHeight = 42;
        _wave.Visibility = Visibility.Collapsed;
        _typing.Visibility = Visibility.Collapsed;
        _errorIcon.Visibility = Visibility.Visible;
        _mascot.State = MascotState.Error;
    }

    internal FrameworkElement PillSurface => _pill;
    internal FrameworkElement BubbleSurface => _bubble;

    /// <summary>Over real blur the pill and bubble use a lighter frosted tint; otherwise the near-solid one.</summary>
    internal void SetGlass(bool active)
    {
        _pill.Background = GlassBrush(active ? (byte)0xB8 : (byte)0xF0);
        _bubble.Background = GlassBrush(active ? (byte)0xC4 : (byte)0xF4, lighter: true);
    }

    public void AnimateIn(bool onlyIfHidden = false)
    {
        if (onlyIfHidden && _visibleState) return;
        _visibleState = true;
        if (!Animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _rootScale.ScaleX = _rootScale.ScaleY = 1;
            return;
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        var pop = new DoubleAnimation(0.82, 1, TimeSpan.FromMilliseconds(360)) { EasingFunction = Motion.Soft };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    public void AnimateOut(Action done)
    {
        _visibleState = false;
        if (!Animate)
        {
            Opacity = 0;
            done();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(190)) { EasingFunction = Motion.Exit };
        fade.Completed += (_, _) => { if (!_visibleState) done(); };
        BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.9, TimeSpan.FromMilliseconds(190)) { EasingFunction = Motion.Exit };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>Back to the empty pill once hidden, so the next session starts clean.</summary>
    public void Reset()
    {
        StopTyping();
        _bubbleHost.Visibility = Visibility.Collapsed;
        _pill.BeginAnimation(OpacityProperty, null);
        _pill.Opacity = 1;
        _pill.Visibility = Visibility.Visible;
        _shimmerShift.BeginAnimation(TranslateTransform.XProperty, null);
        _mascot.Freeze();
    }

    // ---- internals -------------------------------------------------------------------------------------

    private System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.GetCultureInfo(_lang == "en" ? "en-US" : "tr-TR");

    private string ModeName(VoiceMode mode) => mode switch
    {
        VoiceMode.Ask => T("Sor", "Ask"),
        VoiceMode.Command => T("Komut", "Command"),
        _ => T("Dikte", "Dictation")
    };

    private static Color AccentFor(VoiceMode mode) => mode switch
    {
        VoiceMode.Ask => AskAccent,
        VoiceMode.Command => CommandAccent,
        _ => DictationAccent
    };

    private void ShowPill()
    {
        _status.TextWrapping = TextWrapping.NoWrap;
        _status.MaxHeight = double.PositiveInfinity;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF2, 0xFA));
        if (_bubbleHost.Visibility == Visibility.Visible)
        {
            _bubbleHost.Visibility = Visibility.Collapsed;
            _pill.Visibility = Visibility.Visible;
            if (Animate) Fade(_pill, 1, 160, null);
            else { _pill.BeginAnimation(OpacityProperty, null); _pill.Opacity = 1; }
        }
        _pill.Visibility = Visibility.Visible;
        if (_pill.Opacity < 1 && !Animate) _pill.Opacity = 1;
        StopTyping();
    }

    private void SetStatus(string text, bool shimmer)
    {
        _status.Text = text;
        _shimmerShift.BeginAnimation(TranslateTransform.XProperty, null);
        if (shimmer)
        {
            _status.Foreground = _shimmer;
            if (Animate)
                _shimmerShift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever });
            else _shimmerShift.X = -0.1;
        }
    }

    private void ApplyAccent(Color accent, bool error)
    {
        _label.Foreground = new SolidColorBrush(Mix(accent, Colors.White, 0.15));
        _pill.BorderBrush = RimBrush(accent, error ? (byte)0x70 : (byte)0x40);
        _pillGlow.Fill = new RadialGradientBrush
        {
            Center = new Point(0.1, 0.5),
            GradientOrigin = new Point(0.1, 0.5),
            RadiusX = 0.45,
            RadiusY = 1.3,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(error ? (byte)0x55 : (byte)0x40, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)
            }
        };
        var barBrush = new LinearGradientBrush(Mix(accent, Colors.White, 0.55), accent, 90);
        barBrush.Freeze();
        foreach (var b in _barViews) b.Background = barBrush;
        var dotBrush = new SolidColorBrush(Mix(accent, Colors.White, 0.3));
        dotBrush.Freeze();
        foreach (Ellipse d in _typing.Children) d.Fill = dotBrush;
    }

    private void StartTyping()
    {
        for (int i = 0; i < _typing.Children.Count; i++)
        {
            if (_typing.Children[i] is not Ellipse { RenderTransform: TranslateTransform tt } dot) continue;
            if (!Animate)
            {
                tt.Y = i == 1 ? -4 : 0;
                dot.Opacity = i == 1 ? 1 : 0.55;
                continue;
            }
            var hop = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 140) };
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(-5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), Motion.Soft));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), Motion.Exit));
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
            tt.BeginAnimation(TranslateTransform.YProperty, hop);
        }
    }

    private void StopTyping()
    {
        foreach (UIElement d in _typing.Children)
            if (d.RenderTransform is TranslateTransform tt) { tt.BeginAnimation(TranslateTransform.YProperty, null); tt.Y = 0; }
    }

    private static void Fade(UIElement e, double to, double ms, Action? done)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
        if (done is not null) a.Completed += (_, _) => done();
        e.BeginAnimation(OpacityProperty, a);
    }

    private static Brush GlassBrush(byte alpha, bool lighter = false)
    {
        var top = lighter ? Color.FromArgb(alpha, 0x26, 0x23, 0x33) : Color.FromArgb(alpha, 0x1F, 0x1C, 0x2A);
        var bottom = lighter ? Color.FromArgb(alpha, 0x1A, 0x18, 0x24) : Color.FromArgb(alpha, 0x12, 0x11, 0x19);
        var b = new LinearGradientBrush(top, bottom, 90);
        b.Freeze();
        return b;
    }

    private static Brush RimBrush(Color accent, byte alpha)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(alpha, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(0x26, 255, 255, 255), 0.45),
                new GradientStop(Color.FromArgb(0x10, 255, 255, 255), 1)
            }
        };
        b.Freeze();
        return b;
    }

    private static ControlTemplate CloseButtonTemplate(FontFamily iconFont)
    {
        var border = new FrameworkElementFactory(typeof(Border), "bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x1E, 255, 255, 255)));
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetValue(TextBlock.TextProperty, "");
        glyph.SetValue(TextBlock.FontFamilyProperty, iconFont);
        glyph.SetValue(TextBlock.FontSizeProperty, 9.5);
        glyph.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xD8, 0xD5, 0xE4)));
        glyph.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(glyph);
        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), "bg"));
        t.Triggers.Add(hover);
        return t;
    }

    private static bool IsInScrollBar(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ScrollBar) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static Color Mix(Color a, Color b, double t) => MascotControl.Mix(a, b, t);
}
