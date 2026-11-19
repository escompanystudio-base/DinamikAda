using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Abstractions;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace DinamikAda.App.Views;

/// <summary>
/// Premium island layer: timed morph curves, the segmented tab control's crossfade, the compact pill's live
/// "ears" (tinted waveform, progress ring) and the calm section typography. Kept apart from the main island file so
/// the look and motion rules live in one place.
/// </summary>
public partial class IslandWindow
{
    private const double TabSlideDistance = 14.0;

    private Grid[] _tabPanels = [];
    private int _activeTabIndex = -1;
    private int _tabSlideDirection = 1;
    private readonly SolidColorBrush _compactSpecBrush = new(Color.FromRgb(120, 180, 255));
    private readonly PathGeometry _compactRingGeometry = new();

    /// <summary>
    /// The shell morph (width, height and radii together), content reveal, tab pill and hover lift all run on the
    /// shared Motion curves: Soft for opening (about 520 ms), and a gentle ease-in-out for closing (about 360 ms).
    /// Retargets restart from the current value, so hovering in and out mid-flight never jumps.
    /// </summary>
    private void ConfigureMorphMotion()
    {
        bool reduced = !SystemParameters.ClientAreaAnimation;
        _motion.MorphEnterEase = Motion.Soft.Ease;
        _motion.MorphExitEase = Motion.Gentle.Ease;
        _motion.MorphEnterSeconds = reduced ? 0.16 : 0.52;
        _motion.MorphExitSeconds = reduced ? 0.12 : 0.36;
        CompactRingArc.Data = _compactRingGeometry;
    }

    private void OnNowMediaCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement host && host.ActualWidth > 0 && host.ActualHeight > 0)
        {
            var clip = new RectangleGeometry(new Rect(0, 0, host.ActualWidth, host.ActualHeight), 16, 16);
            clip.Freeze();
            host.Clip = clip;
        }
    }

    private static int TabIndexOf(ExpandedTabId tab) => tab switch
    {
        ExpandedTabId.Now => 0,
        ExpandedTabId.Audio => 1,
        ExpandedTabId.Control => 2,
        ExpandedTabId.Shelf => 3,
        ExpandedTabId.System => 4,
        ExpandedTabId.Widgets => 5,
        ExpandedTabId.AiQuota => 6,
        _ => 0
    };

    /// <summary>
    /// Shows the panel for <paramref name="activeTab"/>. When switching tabs in place, the old panel fades out and
    /// drifts against the direction of travel while the new one arrives from that side (driven per frame from the
    /// tab transition value), so the switch reads as a crossfade rather than a hard swap.
    /// </summary>
    private void ShowTabPanel(ExpandedTabId activeTab, bool snap)
    {
        int next = TabIndexOf(activeTab);
        int prev = _activeTabIndex;
        _activeTabIndex = next;
        if (prev >= 0 && prev != next)
        {
            _tabSlideDirection = next > prev ? 1 : -1;
        }

        bool animateOut = !snap && !_isRenderAuditMode && prev >= 0 && prev != next &&
                          SystemParameters.ClientAreaAnimation &&
                          _tabPanels[prev].Visibility == Visibility.Visible;

        for (int i = 0; i < _tabPanels.Length; i++)
        {
            var panel = _tabPanels[i];
            var shift = (TranslateTransform)panel.RenderTransform;
            if (i == next)
            {
                panel.BeginAnimation(OpacityProperty, null);
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                panel.IsHitTestVisible = true;
                panel.Visibility = Visibility.Visible;
                if (snap || _isRenderAuditMode)
                {
                    panel.Opacity = 1.0;
                    shift.X = 0.0;
                }
            }
            else if (i == prev && animateOut)
            {
                panel.IsHitTestVisible = false;
                var fade = Motion.To(0.0, 170, Motion.Exit);
                var drift = Motion.To(-_tabSlideDirection * TabSlideDistance * 0.6, 240);
                int captured = i;
                fade.Completed += (_, _) =>
                {
                    if (_activeTabIndex == captured) return;
                    var p = _tabPanels[captured];
                    p.Visibility = Visibility.Collapsed;
                    p.BeginAnimation(OpacityProperty, null);
                    ((TranslateTransform)p.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null);
                    p.Opacity = 1.0;
                    ((TranslateTransform)p.RenderTransform).X = 0.0;
                    p.IsHitTestVisible = true;
                };
                panel.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
                shift.BeginAnimation(TranslateTransform.XProperty, drift, HandoffBehavior.SnapshotAndReplace);
            }
            else if (!(panel.HasAnimatedProperties && panel.Visibility == Visibility.Visible && !panel.IsHitTestVisible))
            {
                // Leave a panel that is still fading out alone; everything else is simply hidden.
                panel.BeginAnimation(OpacityProperty, null);
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                panel.Opacity = 1.0;
                shift.X = 0.0;
                panel.IsHitTestVisible = true;
                panel.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Per-frame placement of the incoming tab panel (opacity and a short slide from the travel side).</summary>
    private void ApplyTabPanelFrame()
    {
        if (_activeTabIndex < 0 || _activeTabIndex >= _tabPanels.Length) return;
        var panel = _tabPanels[_activeTabIndex];
        double t = Math.Clamp(_motion.TabContentSlide.Value, 0.0, 1.0);
        panel.Opacity = _motion.TabContentOpacity;
        ((TranslateTransform)panel.RenderTransform).X = _tabSlideDirection * TabSlideDistance * (1.0 - Motion.Soft.Ease(t));
    }

    /// <summary>Tints the compact waveform and the media card glow from the current artwork palette.</summary>
    private void ApplyPremiumPalette(Color primary, bool animate)
    {
        Color lifted = BlendColors(primary, Colors.White, 0.38);
        if (_compactSpecBrush.Color != lifted)
        {
            if (animate)
            {
                _compactSpecBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(lifted, TimeSpan.FromMilliseconds(Motion.EnterMs)) { EasingFunction = Motion.Gentle },
                    HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                _compactSpecBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                _compactSpecBrush.Color = lifted;
            }
        }

        foreach (var bar in new[] { SpecBar0, SpecBar1, SpecBar2, SpecBar3, SpecBar4, SpecBar5, SpecBar6, SpecBar7 })
        {
            if (!ReferenceEquals(bar.Background, _compactSpecBrush))
            {
                bar.Background = _compactSpecBrush;
            }
        }

        SetStop(NowArtGlowStop0, primary, 92);
        SetStop(NowArtGlowStop1, primary, 34);

        void SetStop(GradientStop stop, Color c, byte alpha)
        {
            c.A = alpha;
            if (animate && stop.Color != c)
            {
                stop.BeginAnimation(GradientStop.ColorProperty,
                    new ColorAnimation(c, TimeSpan.FromMilliseconds(Motion.EnterMs)) { EasingFunction = Motion.Gentle },
                    HandoffBehavior.SnapshotAndReplace);
            }
            else
            {
                stop.BeginAnimation(GradientStop.ColorProperty, null);
            }
            stop.Color = c;
        }
    }

    /// <summary>Draws the right-ear ring for a 0..1 value, or hides it.</summary>
    private void UpdateCompactRing(double? progress01, Brush accent)
    {
        if (progress01 is not double raw || !double.IsFinite(raw))
        {
            CompactRingHost.Visibility = Visibility.Collapsed;
            return;
        }

        double v = Math.Clamp(raw, 0.0, 1.0);
        CompactRingHost.Visibility = Visibility.Visible;
        CompactRingArc.Stroke = accent;

        const double size = 16.0, stroke = 2.0;
        double r = (size - stroke) * 0.5;
        var c = new Point(size * 0.5, size * 0.5);
        _compactRingGeometry.Figures.Clear();
        if (v <= 0.001) return;

        double sweep = Math.Min(v, 0.9999) * 2.0 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(sweep), c.Y - r * Math.Cos(sweep));
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > Math.PI, SweepDirection.Clockwise, true));
        _compactRingGeometry.Figures.Add(fig);
    }

    /// <summary>
    /// Section headers read as small caps: uppercase, 10.5 SemiBold, muted grey. Applied after localisation so the
    /// uppercase follows the active language's casing rules.
    /// </summary>
    private void ApplySectionLabelTypography()
    {
        var culture = _orch.Config.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("tr-TR")
            : CultureInfo.InvariantCulture;
        var brush = (Brush)FindResource("IslandSectionLabelBrush");
        var font = (FontFamily)FindResource("UiTextFont");

        TextBlock[] labels =
        [
            NowTimersHeaderText, NowNotificationsHeaderText, AudioDevicesHeaderText, AudioAppsHeaderText,
            ControlBrightnessHeader, ControlPowerHeader, ShelfHeaderTitle, ClipboardHeaderTitle,
            SysProcHeaderText, WidgetsHeaderTitle, SchedPowerHeaderText
        ];
        foreach (var label in labels)
        {
            label.Text = (label.Text ?? string.Empty).ToUpper(culture);
            label.FontFamily = font;
            label.FontSize = 10.5;
            label.FontWeight = FontWeights.SemiBold;
            label.Foreground = brush;
        }
    }
}
