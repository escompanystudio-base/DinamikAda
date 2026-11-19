using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Config;
using DinamikAda.Platform.Win32.Windowing;

namespace DinamikAda.App.Views;

/// <summary>
/// Real glass for the dock: a blurred desktop behind the capsule and behind each popup card (via
/// <see cref="GlassSurface"/>), lighter tints while glass is live, glass tiles under app icons, and the
/// capsule's top highlight and inner edge. Audit/smoke runs and "Transparency effects: off" keep the tint fallback.
/// </summary>
public partial class DockWindow
{
    private const double DockPillRadiusDip = 18.0;
    private const double PanelCardRadiusDip = 24.0;
    private const double WidgetCardRadiusDip = 18.0;

    private GlassSurface? _dockGlass;
    private readonly List<(Popup Popup, GlassSurface Surface)> _popupGlass = new(4);
    private bool _glassInitialized;

    /// <summary>Glass is live: not an audit/smoke run, the OS supports it, transparency effects are on, and DWM accepted it.</summary>
    private bool IsDockGlassActive =>
        !_isSmokeOrAuditMode && !_isClosed && GlassPolicy.IsEnabled && _dockGlass is { HasFailed: false };

    private bool CanShowGlass() => !_isSmokeOrAuditMode && !_isClosed && GlassPolicy.IsEnabled;

    private void InitializeGlass()
    {
        if (_glassInitialized || _isSmokeOrAuditMode) return;
        _glassInitialized = true;

        _dockGlass = new GlassSurface(DockPillContainer, GlassBackdropMode.Blur, () => DockPillRadiusDip, CanShowDockGlass);
        DockPillContainer.SizeChanged += (_, _) => SyncDockGlass();
        DockPillContainer.IsVisibleChanged += (_, _) => SyncDockGlass();
        LocationChanged += (_, _) => SyncDockGlass();

        AddPopupGlass(StartLaunchPopup, StartLaunchCard, StartLaunchShell, PanelCardRadiusDip, GlassBackdropMode.Acrylic);
        AddPopupGlass(TrayOverflowPopup, TrayOverflowCard, TrayOverflowCard, PanelCardRadiusDip, GlassBackdropMode.Acrylic);
        AddPopupGlass(ClockCalendarPopup, ClockCalendarCard, ClockCalendarCard, PanelCardRadiusDip, GlassBackdropMode.Acrylic);
        AddPopupGlass(WidgetCardPopup, WidgetCardBorder, WidgetCardBorder, WidgetCardRadiusDip, GlassBackdropMode.Acrylic);

        GlassPolicy.Changed += OnGlassPolicyChanged;
    }

    private bool CanShowDockGlass() => CanShowGlass() && !_isFullscreenHidden && _orch.Config.UseCustomBottomDock;

    private void AddPopupGlass(Popup popup, FrameworkElement card, UIElement animated, double radius, GlassBackdropMode mode)
    {
        var surface = new GlassSurface(card, mode, () => radius, CanShowGlass);
        surface.WatchAnimations(animated);
        if (!ReferenceEquals(animated, card)) surface.WatchAnimations(card);
        popup.Opened += (_, _) => surface.TrackFor(900);
        popup.Closed += (_, _) => surface.Hide();
        _popupGlass.Add((popup, surface));
    }

    private void OnGlassPolicyChanged(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        ApplyDockStyleVisuals();
        SyncDockGlass();
        foreach (var (popup, surface) in _popupGlass)
        {
            if (popup.IsOpen) surface.TrackFor(120);
            else surface.Hide();
        }
    }

    /// <summary>Matches the capsule's blur to its current bounds, edge, slide-out and fade.</summary>
    private void SyncDockGlass()
    {
        if (_dockGlass is null) return;
        _dockGlass.Sync();
        if (_dockGlass.HasFailed && !_glassFailureHandled)
        {
            // DWM refused the accent: fall back to the near-solid tints everywhere.
            _glassFailureHandled = true;
            ApplyDockStyleVisuals();
        }
    }

    private bool _glassFailureHandled;

    /// <summary>Follows the capsule for a short burst (layout changes that settle after this call).</summary>
    private void TrackDockGlass(double ms = 260) => _dockGlass?.TrackFor(ms);

    private void DisposeGlass()
    {
        GlassPolicy.Changed -= OnGlassPolicyChanged;
        _dockGlass?.Dispose();
        _dockGlass = null;
        foreach (var (_, surface) in _popupGlass) surface.Dispose();
        _popupGlass.Clear();
    }

    // ---- Capsule spread (magnification widens the pill) --------------------------------------------

    /// <summary>
    /// Widens the capsule by the magnification spread on each side while keeping the icon row itself still
    /// (the centred capsule is nudged by the difference), so the icon under the cursor never drifts.
    /// Whole-DIP steps keep the row pixel-stable under layout rounding.
    /// </summary>
    private void ApplyCapsuleSpread()
    {
        double l = Math.Round(Math.Max(0.0, _currentSpreadLeft));
        double r = Math.Round(Math.Max(0.0, _currentSpreadRight));
        bool top = IsDockAtTop;
        var host = top ? new Thickness(9 + l, 4, 9 + r, 6) : new Thickness(9 + l, 6, 9 + r, 4);
        if (host == _appliedHostMargin) return;
        _appliedHostMargin = host;
        DockItemsHost.Margin = host;
        double d = r - l;
        DockPillContainer.Margin = top
            ? new Thickness(Math.Max(0, d), 6, Math.Max(0, -d), 0)
            : new Thickness(Math.Max(0, d), 0, Math.Max(0, -d), 6);
        if (!_isSmokeOrAuditMode && IsLoaded) DockPillContainer.UpdateLayout();
    }

    // ---- Tints ---------------------------------------------------------------------------------------

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Capsule fill: a light tint over real blur, or the palette's near-solid fill as the fallback.</summary>
    private Brush DockPillFill(DockWidgetPalette p)
    {
        if (!IsDockGlassActive) return p.PillBackground;
        switch (_orch.Config.DockStyle)
        {
            case DockVisualStyle.White:
                return Frozen(Color.FromArgb(0xB4, 0xF4, 0xF4, 0xF7));
            case DockVisualStyle.Black:
                return Frozen(Color.FromArgb(0x7A, 0x00, 0x00, 0x00));
            case DockVisualStyle.IosGlass:
                return Frozen(Color.FromArgb(0x6A, 0x5C, 0x60, 0x6E));
            case DockVisualStyle.LiquidGlass:
                var g = new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(0x78, 0x2A, 0x30, 0x3C), 0.0),
                    new(Color.FromArgb(0x62, 0x12, 0x16, 0x20), 0.55),
                    new(Color.FromArgb(0x74, 0x1C, 0x22, 0x30), 1.0)
                }, 90);
                g.Freeze();
                return g;
            default:
                return Frozen(Color.FromArgb(0x8C, 0x08, 0x0A, 0x12));
        }
    }

    /// <summary>Top highlight (1px, fading toward the sides) and a faint inner edge for the current style.</summary>
    private void ApplyCapsuleEdges(bool light)
    {
        DockTopHighlightMid0.Color = light ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x4C, 0xFF, 0xFF, 0xFF);
        DockTopHighlightMid1.Color = DockTopHighlightMid0.Color;
        DockInnerEdgeTop.Color = light ? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);
        DockInnerEdgeMid.Color = light ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF);
        DockInnerEdgeBottom.Color = light ? Color.FromArgb(0x12, 0x00, 0x00, 0x00) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>Popup card fills: frosted (~70%) over blur, the near-solid surface otherwise.</summary>
    private void ApplyPopupGlassTints()
    {
        bool glass = IsDockGlassActive;
        var panelFill = Frozen(glass ? Color.FromArgb(0xB4, 0x0F, 0x0F, 0x12) : Color.FromArgb(0xF0, 0x0F, 0x0F, 0x12));
        TrayOverflowCard.Background = panelFill;
        ClockCalendarCard.Background = panelFill;
        StartLaunchCard.Background = panelFill;

        // The Start card's solid shadow plate would hide the blur; over glass the card casts its own shadow.
        StartLaunchShadow.Visibility = glass ? Visibility.Collapsed : Visibility.Visible;
        StartLaunchCard.Effect = glass
            ? new DropShadowEffect { BlurRadius = 48, ShadowDepth = 18, Direction = 270, Opacity = 0.34, Color = Colors.Black, RenderingBias = RenderingBias.Performance }
            : null;
    }

    /// <summary>The widget detail card keeps its palette colour, made frosted while glass is live.</summary>
    private Brush WidgetCardFill(Brush paletteFill)
    {
        if (!IsDockGlassActive || paletteFill is not SolidColorBrush s) return paletteFill;
        byte a = IsLightDock ? (byte)0xD0 : (byte)0xBC;
        return Frozen(Color.FromArgb(a, s.Color.R, s.Color.G, s.Color.B));
    }

    // ---- App icon glass tiles ------------------------------------------------------------------------

    /// <summary>Dark glass squircle (light on the White style) with a crisp top highlight.</summary>
    private Brush AppTileFill()
    {
        var g = IsLightDock
            ? new LinearGradientBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xC4, 0xEA, 0xEA, 0xEF), 90)
            : new LinearGradientBrush(Color.FromArgb(0x66, 0x3C, 0x40, 0x4C), Color.FromArgb(0x66, 0x14, 0x16, 0x1E), 90);
        g.Freeze();
        return g;
    }

    private Brush AppTileEdge()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        if (IsLightDock)
        {
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 0.0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0x00, 0x00, 0x00), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0x00, 0x00, 0x00), 1.0));
        }
        else
        {
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF), 0.0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF), 0.45));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 1.0));
        }
        g.Freeze();
        return g;
    }

    /// <summary>Turns a slot plate into a glass tile: soft hover brightening and a gentle press dip.</summary>
    private Border DecorateAppTile(Border plate, Grid plateGrid, Grid container, int iconSize)
    {
        plate.CornerRadius = new CornerRadius(Math.Round(iconSize * 0.22));
        plate.Background = AppTileFill();
        plate.BorderBrush = AppTileEdge();
        plate.BorderThickness = new Thickness(0.8);

        var hover = new Border
        {
            CornerRadius = new CornerRadius(Math.Max(0, Math.Round(iconSize * 0.22) - 0.8)),
            Background = Frozen(IsLightDock ? Color.FromArgb(0x10, 0, 0, 0) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            Opacity = 0,
            IsHitTestVisible = false
        };
        plateGrid.Children.Insert(0, hover);

        var press = new ScaleTransform(1, 1);
        plate.RenderTransformOrigin = new Point(0.5, 0.5);
        plate.RenderTransform = press;

        container.MouseEnter += (_, _) => hover.BeginAnimation(OpacityProperty, Motion.To(1, 120, Motion.Soft));
        container.MouseLeave += (_, _) =>
        {
            hover.BeginAnimation(OpacityProperty, Motion.To(0, 200, Motion.Soft));
            Release();
        };
        container.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_isSmokeOrAuditMode) return;
            press.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(0.94, 90, Motion.Soft));
            press.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(0.94, 90, Motion.Soft));
        };
        container.PreviewMouseLeftButtonUp += (_, _) => Release();
        container.LostMouseCapture += (_, _) => Release();
        return hover;

        void Release()
        {
            if (_isSmokeOrAuditMode || Math.Abs(press.ScaleX - 1.0) < 0.001 && !press.HasAnimatedProperties) return;
            press.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 240, Motion.Soft));
            press.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 240, Motion.Soft));
        }
    }
}
