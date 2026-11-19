using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DinamikAda.App.Services;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Modules.Productivity;

namespace DinamikAda.App.Views;

/// <summary>
/// The dock's own panels and hints: the background apps panel, the clock calendar, the hover tooltip pill,
/// the AI quota pill and its rich tooltip card. One calm language: neutral surfaces, borderless rows, soft motion.
/// </summary>
public partial class DockWindow
{
    private static readonly Uri PanelsDictionaryUri =
        new("pack://application:,,,/DinamikAda.App;component/Themes/DockPanels.xaml", UriKind.Absolute);

    private static readonly Color PanelTitleColor = Color.FromRgb(0xF4, 0xF4, 0xF6);
    private static readonly Color PanelBodyColor = Color.FromRgb(0xD6, 0xD6, 0xDB);
    private static readonly Color PanelSecondaryColor = Color.FromRgb(0x9A, 0x9A, 0xA3);
    private static readonly Color PanelTertiaryColor = Color.FromRgb(0x6E, 0x6E, 0x77);
    private static readonly Color PanelGlyphColor = Color.FromRgb(0xB4, 0xB4, 0xBC);
    private static readonly Color PanelPositive = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color PanelWarning = Color.FromRgb(0xFF, 0xB3, 0x40);
    private static readonly Color PanelNegative = Color.FromRgb(0xFF, 0x6B, 0x61);

    private const double PanelGapBottomDock = 18.0;
    private const double PanelGapTopDock = 26.0;
    private const double TooltipDelayMs = 350.0;
    private const double TooltipFadeMs = 160.0;
    private const int StaggerMaxItems = 12;
    private const double StaggerStepMs = 18.0;

    private readonly HashSet<Popup> _closingPanels = new();
    private ResourceDictionary? _panelResources;
    private DispatcherTimer? _tooltipDelayTimer;
    private long _tooltipHiddenTicks;
    private bool _aiTooltipMode;
    private Border? _islandPlate;
    private FrameworkElement? _islandLens;
    private Ellipse? _islandAiDot;
    private DateTime? _popupAuditNow;
    private IReadOnlyList<MeetingOccurrence>? _popupAuditEvents;
    private string _popupUpcomingSignature = string.Empty;
    private long _trayRefreshSpinStartTicks;
    private bool _trayRefreshSpinning;

    private DateTime PopupNow => _popupAuditNow ?? DateTime.Now;

    /// <summary>Panel motion is off in audit/smoke runs and when Windows animations are turned off.</summary>
    private bool PanelMotion => !_isSmokeOrAuditMode && SystemParameters.ClientAreaAnimation;

    private void InitializePanels()
    {
        try
        {
            // Stock tooltips anywhere in the dock (widgets, buttons) share the dark glass pill.
            _panelResources = new ResourceDictionary { Source = PanelsDictionaryUri };
            if (_panelResources["DockGlassToolTipStyle"] is Style tipStyle) Resources[typeof(ToolTip)] = tipStyle;
        }
        catch
        {
            // Purely cosmetic; the stock tooltip stays if the dictionary cannot load.
        }

        DependencyPropertyDescriptor.FromProperty(OpacityProperty, typeof(Border))
            ?.AddValueChanged(DockTooltipBorder, OnDockTooltipGateChanged);
    }

    // ---- Shared panel motion -------------------------------------------------------------------

    private FrameworkElement? PanelCardOf(Popup popup) =>
        ReferenceEquals(popup, TrayOverflowPopup) ? TrayOverflowCard
        : ReferenceEquals(popup, ClockCalendarPopup) ? ClockCalendarCard
        : null;

    /// <summary>Open and not on its way out.</summary>
    private bool IsPanelVisible(Popup popup) => popup.IsOpen && !_closingPanels.Contains(popup);

    /// <summary>Centres the panel over its dock item and keeps a calm gap from the dock on either edge.</summary>
    private void PositionPanel(Popup popup, Border shell, FrameworkElement anchor)
    {
        shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = shell.DesiredSize;
        Thickness pad = shell.Padding;
        bool top = IsDockAtTop;
        double anchorW = anchor.ActualWidth > 0 ? anchor.ActualWidth : (double.IsNaN(anchor.Width) ? 0 : anchor.Width);
        popup.PlacementTarget = anchor;
        popup.Placement = top ? PlacementMode.Bottom : PlacementMode.Top;
        popup.HorizontalOffset = Math.Round((anchorW - size.Width) * 0.5);
        popup.VerticalOffset = top ? PanelGapTopDock - pad.Top : pad.Bottom - PanelGapBottomDock;
    }

    private void OpenPanel(Popup popup, Border shell, FrameworkElement card, FrameworkElement anchor, Action onReopenedWhileClosing)
    {
        bool wasClosing = _closingPanels.Remove(popup);
        PositionPanel(popup, shell, anchor);
        PlayPanelOpen(card);
        if (!popup.IsOpen)
        {
            popup.IsOpen = true;
        }
        else if (wasClosing)
        {
            // Caught mid-exit: nudge the placement and run the opened logic again.
            popup.HorizontalOffset += 0.01;
            popup.HorizontalOffset -= 0.01;
            onReopenedWhileClosing();
        }
    }

    /// <summary>Opacity 0→1, scale 0.97→1 from the dock edge and a 10 px travel toward the dock.</summary>
    private void PlayPanelOpen(FrameworkElement card)
    {
        if (!PanelMotion)
        {
            ResetPanelVisual(card);
            return;
        }
        bool top = IsDockAtTop;
        double rise = top ? -10 : 10;
        var st = new ScaleTransform(0.97, 0.97);
        var tt = new TranslateTransform(0, rise);
        var tg = new TransformGroup();
        tg.Children.Add(st);
        tg.Children.Add(tt);
        card.RenderTransformOrigin = new Point(0.5, top ? 0.0 : 1.0);
        card.RenderTransform = tg;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(0.97, 1, Motion.EnterMs));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(0.97, 1, Motion.EnterMs));
        tt.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(rise, 0, Motion.EnterMs));
        card.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, 300));
    }

    /// <summary>Fade, scale to 0.98 and sink 6 px, then really close the popup.</summary>
    private void ClosePanel(Popup popup)
    {
        if (!popup.IsOpen || _closingPanels.Contains(popup)) return;
        var card = PanelCardOf(popup);
        if (card is null || !PanelMotion || _isClosed)
        {
            popup.IsOpen = false;
            return;
        }

        _closingPanels.Add(popup);
        double sink = IsDockAtTop ? -6 : 6;
        var st = new ScaleTransform(1, 1);
        var tt = new TranslateTransform(0, 0);
        var tg = new TransformGroup();
        tg.Children.Add(st);
        tg.Children.Add(tt);
        card.RenderTransformOrigin = new Point(0.5, IsDockAtTop ? 0.0 : 1.0);
        card.RenderTransform = tg;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(0.98, Motion.ExitMs, Motion.Exit));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(0.98, Motion.ExitMs, Motion.Exit));
        tt.BeginAnimation(TranslateTransform.YProperty, Motion.To(sink, Motion.ExitMs, Motion.Exit));
        var fade = Motion.To(0, Motion.ExitMs, Motion.Exit);
        fade.Completed += (_, _) =>
        {
            if (!_closingPanels.Remove(popup)) return; // reopened meanwhile
            popup.IsOpen = false;
        };
        card.BeginAnimation(OpacityProperty, fade);
    }

    private static void ResetPanelVisual(FrameworkElement card)
    {
        card.BeginAnimation(OpacityProperty, null);
        card.Opacity = 1;
        card.RenderTransform = Transform.Identity;
    }

    /// <summary>Items rise 6 px and fade in, 18 ms apart; after the first twelve the rest arrive together.</summary>
    private void StaggerIn(IReadOnlyList<UIElement> items, double baseDelayMs = 40)
    {
        if (!PanelMotion)
        {
            foreach (var el in items)
            {
                el.BeginAnimation(OpacityProperty, null);
                el.Opacity = 1;
            }
            return;
        }

        double rise = IsDockAtTop ? -6 : 6;
        for (int i = 0; i < items.Count; i++)
        {
            var el = items[i];
            double delay = baseDelayMs + Math.Min(i, StaggerMaxItems) * StaggerStepMs;
            var tt = new TranslateTransform(0, rise);
            if (el.RenderTransform is TranslateTransform { IsFrozen: false } existing) { tt = existing; tt.Y = rise; }
            else el.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(rise, 0, Motion.EnterMs * 0.8, Motion.Soft, delay));
            var fade = Motion.FromTo(0, 1, 300, Motion.Soft, delay);
            fade.Completed += (_, _) =>
            {
                // Hand the final value back to the element so a later animation removal never drops it to 0.
                el.Opacity = 1;
                el.BeginAnimation(OpacityProperty, null);
            };
            el.Opacity = 0;
            el.BeginAnimation(OpacityProperty, fade);
        }
    }

    private static void FadeTo(UIElement el, double to, double ms)
    {
        el.BeginAnimation(OpacityProperty, Motion.To(to, ms, Motion.Soft));
    }

    private static SolidColorBrush PanelBrush(Color c, byte alpha = 0xFF)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private Color PanelAccent() =>
        ParseAccentColor(_orch.Coordinator.Current.PrimaryActivity?.AccentHex, Color.FromRgb(10, 132, 255));

    /// <summary>Looks keys up in the panel dictionary itself, so detached audit captures resolve them too.</summary>
    private T PanelResource<T>(FrameworkElement scope, string key) where T : class =>
        _panelResources?[key] as T ?? (T)scope.FindResource(key);

    // ---- Background apps panel -----------------------------------------------------------------

    private void ToggleTrayPopup()
    {
        if (_trayChevronBtn is null) return;
        StartLaunchPopup.IsOpen = false;
        ClockCalendarPopup.IsOpen = false;
        if (IsPanelVisible(TrayOverflowPopup))
        {
            ClosePanel(TrayOverflowPopup);
            return;
        }

        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        TrayOverflowCard.Width = Math.Clamp(_activeMonitor.EffectiveWorkWidthPx / scale - 32, 280, 340);
        TrayAppsScroller.MaxHeight = Math.Clamp(_activeMonitor.EffectiveWorkHeightPx / scale - 220, 90, 360);
        TrayPopupStatusText.Visibility = Visibility.Collapsed;
        RebuildTrayOverflowGrid(_cachedTrayItems);
        OpenPanel(TrayOverflowPopup, TrayOverflowShell, TrayOverflowCard, _trayChevronBtn, () => OnTrayPopupOpened(null, EventArgs.Empty));
    }

    private void OnTrayPopupOpened(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        TrayAppsScroller.ScrollToTop();
        SyncCoordinatorState(_orch.Coordinator.Current);
        StaggerIn(TrayVisibleItems());
        if (!_isSmokeOrAuditMode)
        {
            StartPopupLightDismissWatcher();
            _ = RefreshTrayAppsAsync(showLoading: _cachedTrayItems.Count == 0);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (TrayOverflowPopup.IsOpen && !TrayOverflowCard.IsKeyboardFocusWithin)
                    BtnTrayPopupRefresh.Focus();
            });
        }
    }

    private void OnTrayPopupClosed(object? sender, EventArgs e)
    {
        _closingPanels.Remove(TrayOverflowPopup);
        ResetPanelVisual(TrayOverflowCard);
        StopTrayRefreshSpin(immediate: true);
        StopPopupLightDismissWatcherIfIdle();
        ScheduleIntellihideEval();
    }

    private List<UIElement> TrayVisibleItems()
    {
        var list = new List<UIElement>(TrayOverflowGrid.Children.Count + 1);
        foreach (UIElement child in TrayOverflowGrid.Children) list.Add(child);
        if (TrayPopupEmptyState.Visibility == Visibility.Visible) list.Add(TrayPopupEmptyState);
        return list;
    }

    private Task RefreshTrayAppsAsync(bool showLoading = false)
    {
        if (_isClosed || _isSmokeOrAuditMode || _trayActionBusy) return Task.CompletedTask;
        if (_trayRefreshTask is { IsCompleted: false })
        {
            if (showLoading)
            {
                SetTrayStatus(Strings.Pick(_orch.Config.Language, "Uygulamalar kontrol ediliyor…", "Checking apps…"));
                TrayPopupEmptyState.Visibility = Visibility.Collapsed;
            }
            return _trayRefreshTask;
        }
        return _trayRefreshTask = RefreshTrayAppsCoreAsync(showLoading);
    }

    private async Task RefreshTrayAppsCoreAsync(bool showLoading)
    {
        if (TrayOverflowPopup.IsOpen) StartTrayRefreshSpin();
        if (showLoading)
        {
            SetTrayStatus(Strings.Pick(_orch.Config.Language, "Uygulamalar kontrol ediliyor…", "Checking apps…"));
            TrayPopupEmptyState.Visibility = Visibility.Collapsed;
        }
        try
        {
            var items = await Task.Run(() => _appsService.QueryBackgroundTrayApps());
            if (_isClosed) return;
            _cachedTrayItems = items;
            _lastTrayAppCount = items.Count;
            if (_trayActiveDot is not null) _trayActiveDot.Opacity = items.Count > 0 ? 1.0 : 0.0;
            if (TrayOverflowPopup.IsOpen && _activeContextMenu is not { IsOpen: true } && !_trayActionBusy)
            {
                bool wasEmptyShown = TrayPopupEmptyState.Visibility == Visibility.Visible;
                if (!_renderedTrayItems.SequenceEqual(items)) RebuildTrayOverflowGrid(items);
                TrayPopupEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (items.Count == 0 && !wasEmptyShown) StaggerIn([TrayPopupEmptyState], 0);
                TrayPopupStatusText.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception)
        {
            if (!_isClosed && TrayOverflowPopup.IsOpen)
                SetTrayStatus(Strings.Pick(_orch.Config.Language, "Liste yenilenemedi. Yeniden deneyin.", "Could not refresh the list. Try again."), isError: true);
        }
        finally
        {
            if (!_isClosed) StopTrayRefreshSpin(immediate: false);
        }
    }

    /// <summary>The refresh glyph turns slowly while the list is being read.</summary>
    private void StartTrayRefreshSpin()
    {
        if (_trayRefreshSpinning || _isSmokeOrAuditMode) return;
        _trayRefreshSpinning = true;
        _trayRefreshSpinStartTicks = Stopwatch.GetTimestamp();
        double from = TrayRefreshRotate.Angle % 360;
        TrayRefreshRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(from, from + 360, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void StopTrayRefreshSpin(bool immediate)
    {
        if (!_trayRefreshSpinning) return;
        if (immediate)
        {
            _trayRefreshSpinning = false;
            TrayRefreshRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            TrayRefreshRotate.Angle = 0;
            return;
        }

        // Keep at least one visible turn so a fast refresh still reads as feedback, then settle softly upright.
        double elapsedMs = (Stopwatch.GetTimestamp() - _trayRefreshSpinStartTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < 600)
        {
            var wait = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(600 - elapsedMs) };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                StopTrayRefreshSpin(immediate: false);
            };
            wait.Start();
            return;
        }

        _trayRefreshSpinning = false;
        double current = TrayRefreshRotate.Angle;
        double target = Math.Ceiling((current + 30) / 360.0) * 360.0;
        var settle = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(520)) { EasingFunction = Motion.Soft };
        settle.Completed += (_, _) =>
        {
            if (_trayRefreshSpinning) return;
            TrayRefreshRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            TrayRefreshRotate.Angle = 0;
        };
        TrayRefreshRotate.BeginAnimation(RotateTransform.AngleProperty, settle);
    }

    private void SetTrayStatus(string message, bool isError = false)
    {
        TrayPopupStatusText.Text = message;
        TrayPopupStatusText.Foreground = isError
            ? PanelBrush(Color.FromRgb(255, 180, 171))
            : PanelBrush(PanelSecondaryColor);
        TrayPopupStatusText.Visibility = Visibility.Visible;
    }

    private async void OnTrayPopupRefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshTrayAppsAsync(showLoading: true);

    private void OnTrayCardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ClosePanel(TrayOverflowPopup);
        }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = RefreshTrayAppsAsync(showLoading: true);
        }
        else if (e.Key == Key.Down && Keyboard.FocusedElement == BtnTrayPopupRefresh && TrayOverflowGrid.Children.Count > 0)
        {
            e.Handled = true;
            TrayOverflowGrid.Children[0].Focus();
        }
        else if ((e.Key is Key.Down or Key.Up) && Keyboard.FocusedElement is Button row && ReferenceEquals(row.Parent, TrayOverflowGrid))
        {
            int idx = TrayOverflowGrid.Children.IndexOf(row) + (e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            if (idx < 0) BtnTrayPopupRefresh.Focus();
            else if (idx < TrayOverflowGrid.Children.Count) TrayOverflowGrid.Children[idx].Focus();
        }
    }

    /// <summary>
    /// A borderless list rather than an icon grid: background apps have long names ("Wallpaper Engine",
    /// "YouTube Music") that a 4-column grid would cut to "…", the exe name helps tell twins apart, and rows
    /// read top-down with the keyboard. Hover fills the row and swaps the exe name for the row's actions.
    /// </summary>
    private void RebuildTrayOverflowGrid(IReadOnlyList<BackgroundTrayAppItem> items)
    {
        string lang = _orch.Config.Language;
        string? focusedKey = (Keyboard.FocusedElement as Button)?.Tag as string;
        double scrollOffset = TrayAppsScroller.VerticalOffset;
        bool hadRows = TrayOverflowGrid.Children.Count > 0;
        var previousKeys = new HashSet<string>(_renderedTrayItems.Select(i => i.Key), StringComparer.Ordinal);
        _renderedTrayItems = items;
        _lastTrayAppCount = items.Count;
        if (_trayActiveDot is not null) _trayActiveDot.Opacity = items.Count > 0 ? 1.0 : 0.0;

        TrayPopupTitleText.Text = Strings.TrayOverflowHeader(lang);
        TrayPopupTitleText.FontSize = TrayOverflowCard.Width < 330 ? 16 : 18;
        TrayPopupEmptyText.Text = Strings.TrayOverflowEmpty(lang);
        TrayPopupEmptyHint.Text = Strings.Pick(lang, "Her şey sakin.", "All quiet.");
        TrayPopupCountText.Text = items.Count.ToString(CultureInfo.InvariantCulture);
        TrayPopupCountBadge.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnTrayPopupRefresh.ToolTip = Strings.Pick(lang, "Yenile (F5)", "Refresh (F5)");
        AutomationProperties.SetName(BtnTrayPopupRefresh, Strings.Pick(lang, "Uygulama listesini yenile", "Refresh the app list"));
        TrayOverflowGrid.Children.Clear();
        TrayPopupEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var fresh = new List<UIElement>();
        foreach (var item in items)
        {
            var row = BuildTrayRow(item, lang);
            TrayOverflowGrid.Children.Add(row);
            if (focusedKey == item.Key) row.Focus();
            if (!previousKeys.Contains(item.Key)) fresh.Add(row);
        }
        TrayAppsScroller.ScrollToVerticalOffset(scrollOffset);

        // A refresh while open: only rows that are new ease in, the rest stay put (no flash).
        if (TrayOverflowPopup.IsOpen && fresh.Count > 0)
            StaggerIn(fresh, hadRows ? 0 : 20);
    }

    private Button BuildTrayRow(BackgroundTrayAppItem item, string lang)
    {
        var row = new Button
        {
            Tag = item.Key,
            Style = PanelResource<Style>(TrayOverflowCard, "PanelRowButtonStyle"),
            Margin = new Thickness(0, 0, 0, 2)
        };
        AutomationProperties.SetName(row, item.DisplayName);
        AutomationProperties.SetHelpText(row, Strings.TrayOpenApp(lang));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement icon = item.Icon is not null
            ? new Image { Source = item.Icon, Width = 24, Height = 24, Stretch = Stretch.Uniform, SnapsToDevicePixels = true }
            : new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(7),
                Background = PanelBrush(Color.FromRgb(0x24, 0x24, 0x2A)),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.DisplayName) ? "?" : StringInfo.GetNextTextElement(item.DisplayName).ToUpperInvariant(),
                    FontFamily = PanelResource<FontFamily>(TrayOverflowCard, "PanelDisplayFont"),
                    Foreground = PanelBrush(PanelGlyphColor),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = item.DisplayName,
            Margin = new Thickness(12, 0, 8, 1),
            FontFamily = PanelResource<FontFamily>(TrayOverflowCard, "PanelTextFont"),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = PanelBrush(Color.FromRgb(0xEC, 0xEC, 0xF0)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var right = new Grid { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 2);
        string exe = System.IO.Path.GetFileName(item.ExePath) ?? string.Empty;
        var exeText = new TextBlock
        {
            Text = exe,
            MaxWidth = 128,
            FontFamily = PanelResource<FontFamily>(TrayOverflowCard, "PanelTextFont"),
            FontSize = 11,
            Foreground = PanelBrush(PanelTertiaryColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0)
        };
        right.Children.Add(exeText);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0,
            IsHitTestVisible = false
        };
        var iconStyle = PanelResource<Style>(TrayOverflowCard, "PanelIconButtonStyle");
        if (!string.IsNullOrWhiteSpace(item.ExePath))
        {
            var locate = new Button
            {
                Content = "",
                Width = 28,
                Height = 28,
                FontSize = 12,
                Focusable = false,
                Style = iconStyle,
                ToolTip = Strings.TrayOpenLocation(lang)
            };
            AutomationProperties.SetName(locate, Strings.TrayOpenLocation(lang));
            locate.Click += (_, ev) =>
            {
                ev.Handled = true;
                if (DockAppsService.OpenAppFileLocation(item.ExePath)) ClosePanel(TrayOverflowPopup);
                else ReportTrayActionFailure(item, TrayAppActionResult.Failed);
            };
            actions.Children.Add(locate);
        }
        var more = new Button
        {
            Content = "",
            Width = 28,
            Height = 28,
            FontSize = 12,
            Focusable = false,
            Style = iconStyle,
            ToolTip = Strings.Pick(lang, "Diğer seçenekler", "More options")
        };
        AutomationProperties.SetName(more, Strings.Pick(lang, "Diğer seçenekler", "More options"));
        more.Click += (_, ev) =>
        {
            ev.Handled = true;
            ShowTrayItemContextMenu(row, item, lang);
        };
        actions.Children.Add(more);
        right.Children.Add(actions);
        grid.Children.Add(right);
        row.Content = grid;

        void Reveal(bool on)
        {
            FadeTo(exeText, on ? 0 : 1, on ? 120 : 200);
            FadeTo(actions, on ? 1 : 0, on ? 120 : 200);
            actions.IsHitTestVisible = on;
        }
        row.MouseEnter += (_, _) => Reveal(true);
        row.MouseLeave += (_, _) => Reveal(row.IsKeyboardFocusWithin);
        row.IsKeyboardFocusWithinChanged += (_, _) => Reveal(row.IsKeyboardFocusWithin || row.IsMouseOver);

        row.Click += (_, _) => DismissPopupsAndActivateTrayApp(item);
        row.MouseRightButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            ShowTrayItemContextMenu(row, item, lang);
        };
        row.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key != Key.Apps && !(ev.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) return;
            ev.Handled = true;
            ShowTrayItemContextMenu(row, item, lang);
        };
        return row;
    }

    // ---- Clock calendar panel ------------------------------------------------------------------

    private void ToggleClockPopup()
    {
        if (_clockChip is null) return;
        StartLaunchPopup.IsOpen = false;
        TrayOverflowPopup.IsOpen = false;
        if (IsPanelVisible(ClockCalendarPopup))
        {
            ClosePanel(ClockCalendarPopup);
            return;
        }

        double sinceClose = (Stopwatch.GetTimestamp() - _lastPopupClosedTicks) / (double)Stopwatch.Frequency;
        if (_lastPopupClosedTicks != 0 && sinceClose < 0.22 && !ClockCalendarPopup.IsOpen)
        {
            return;
        }

        PrepareClockPanel();
        OpenPanel(ClockCalendarPopup, ClockCalendarShell, ClockCalendarCard, _clockChip, () => OnClockPopupOpened(null, EventArgs.Empty));
    }

    private void PrepareClockPanel()
    {
        _popupRenderedToday = PopupNow.Date;
        _popupDisplayMonth = new DateTime(_popupRenderedToday.Year, _popupRenderedToday.Month, 1);
        _popupUpcomingSignature = string.Empty;
        UpdatePopupLiveClockAndFooter();
        RebuildPopupCalendarGrid();
    }

    private void OnClockPopupOpened(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        SyncCoordinatorState(_orch.Coordinator.Current);
        var sections = new List<UIElement>();
        foreach (UIElement child in ClockCalendarContent.Children) sections.Add(child);
        StaggerIn(sections);
        if (!_isSmokeOrAuditMode)
        {
            _popupClockTimer.Start();
            StartPopupLightDismissWatcher();
        }
    }

    private void OnClockPopupClosed(object? sender, EventArgs e)
    {
        _closingPanels.Remove(ClockCalendarPopup);
        ResetPanelVisual(ClockCalendarCard);
        _popupClockTimer.Stop();
        _lastPopupClosedTicks = Stopwatch.GetTimestamp();
        StopPopupLightDismissWatcherIfIdle();
        ScheduleIntellihideEval();
    }

    private void OnClockCardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                ClosePanel(ClockCalendarPopup);
                break;
            case Key.PageUp:
                e.Handled = true;
                ShiftPopupMonth(-1);
                break;
            case Key.PageDown:
                e.Handled = true;
                ShiftPopupMonth(1);
                break;
            case Key.Home:
                e.Handled = true;
                OnPopupTodayClick(sender, new RoutedEventArgs());
                break;
        }
    }

    private void OnPopupPrevMonthClick(object sender, RoutedEventArgs e) => ShiftPopupMonth(-1);

    private void OnPopupNextMonthClick(object sender, RoutedEventArgs e) => ShiftPopupMonth(1);

    private void OnPopupTodayClick(object sender, RoutedEventArgs e)
    {
        var today = PopupNow.Date;
        int dir = Math.Sign((today.Year - _popupDisplayMonth.Year) * 12 + today.Month - _popupDisplayMonth.Month);
        _popupRenderedToday = today;
        _popupDisplayMonth = new DateTime(today.Year, today.Month, 1);
        RebuildPopupCalendarGrid();
        AnimateMonthChange(dir);
    }

    private void ShiftPopupMonth(int delta)
    {
        _popupDisplayMonth = _popupDisplayMonth.AddMonths(delta);
        RebuildPopupCalendarGrid();
        AnimateMonthChange(delta);
    }

    /// <summary>The new month slides in a few pixels from the side it came from while it fades up.</summary>
    private void AnimateMonthChange(int direction)
    {
        if (!PanelMotion || direction == 0) return;
        PopupDaysShift.BeginAnimation(TranslateTransform.XProperty, Motion.FromTo(direction * 12, 0, Motion.EnterMs));
        PopupDaysGrid.BeginAnimation(OpacityProperty, Motion.FromTo(0.15, 1, 280));
        PopupMonthTitle.BeginAnimation(OpacityProperty, Motion.FromTo(0.3, 1, 260));
    }

    private void UpdatePopupLiveClockAndFooter()
    {
        var now = PopupNow;
        var culture = GetActiveUiCulture();

        PopupClockHm.Text = now.ToString("HH:mm", culture);
        PopupClockSec.Text = now.ToString(":ss", culture);
        PopupDateWeekday.Text = culture.DateTimeFormat.GetDayName(now.DayOfWeek).ToUpper(culture);
        PopupDateFull.Text = now.ToString("d MMMM yyyy", culture);

        if (_popupAuditNow is null && now.Date != _popupRenderedToday)
        {
            _popupRenderedToday = now.Date;
            RebuildPopupCalendarGrid();
        }

        RebuildUpcomingList(now);
    }

    private IReadOnlyList<MeetingOccurrence> GetPopupUpcomingEvents()
    {
        if (_popupAuditEvents is not null) return _popupAuditEvents;
        var meeting = _orch.Calendar.ActiveOrNextMeeting;
        return meeting is null ? [] : [meeting];
    }

    private void RebuildUpcomingList(DateTime now)
    {
        string lang = _orch.Config.Language;
        bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var events = GetPopupUpcomingEvents().Take(3).ToList();
        string signature = lang + "|" + now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + "|" +
            string.Join("|", events.Select(m => m.Summary + m.StartUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        if (string.Equals(signature, _popupUpcomingSignature, StringComparison.Ordinal)) return;
        _popupUpcomingSignature = signature;

        PopupUpcomingLabel.Text = isEn ? "UP NEXT" : "YAKLAŞAN";
        PopupUpcomingList.Children.Clear();
        var textFont = PanelResource<FontFamily>(ClockCalendarCard, "PanelTextFont");

        if (events.Count == 0)
        {
            PopupUpcomingList.Children.Add(new TextBlock
            {
                Text = isEn ? "No events scheduled today" : "Bugün planlı etkinlik yok",
                FontFamily = textFont,
                FontSize = 12,
                Foreground = PanelBrush(PanelTertiaryColor),
                Margin = new Thickness(9, 2, 8, 2)
            });
            return;
        }

        var culture = GetActiveUiCulture();
        var nowUtc = new DateTimeOffset(now).ToUniversalTime();
        foreach (var m in events)
        {
            var startLocal = m.StartUtc.ToLocalTime().DateTime;
            bool isLive = m.StartUtc <= nowUtc && m.EndUtc > nowUtc;
            int durMin = (int)Math.Round((m.EndUtc - m.StartUtc).TotalMinutes);
            string when = isLive
                ? (isEn ? "Now" : "Şimdi")
                : startLocal.Date == now.Date
                    ? (isEn ? "Today" : "Bugün")
                    : startLocal.Date == now.Date.AddDays(1)
                        ? (isEn ? "Tomorrow" : "Yarın")
                        : startLocal.ToString("d MMM", culture);
            string sub = m.IsAllDay
                ? $"{when} · {(isEn ? "all day" : "tüm gün")}"
                : durMin > 0 ? $"{when} · {durMin} {(isEn ? "min" : "dk")}" : when;

            var row = new Button
            {
                Style = PanelResource<Style>(ClockCalendarCard, "PanelRowButtonStyle"),
                Height = 44,
                Padding = new Thickness(8, 0, 8, 0)
            };
            AutomationProperties.SetName(row, $"{m.Summary}, {startLocal.ToString("HH:mm", culture)}");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Color dotColor = isLive ? PanelPositive : PanelAccent();
            grid.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = PanelBrush(dotColor, 0xE6),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 7, 0, 0)
            });
            var time = new TextBlock
            {
                Text = m.IsAllDay ? "—" : startLocal.ToString("HH:mm", culture),
                Margin = new Thickness(10, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                FontFamily = textFont,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = PanelBrush(PanelBodyColor)
            };
            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = m.Summary,
                FontFamily = textFont,
                FontSize = 13,
                Foreground = PanelBrush(Color.FromRgb(0xEC, 0xEC, 0xF0)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labels.Children.Add(new TextBlock
            {
                Text = sub,
                Margin = new Thickness(0, 1, 0, 0),
                FontFamily = textFont,
                FontSize = 11,
                Foreground = PanelBrush(isLive ? PanelPositive : PanelTertiaryColor, isLive ? (byte)0xE0 : (byte)0xFF),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(labels, 2);
            grid.Children.Add(labels);
            row.Content = grid;
            row.Click += (_, _) =>
            {
                if (_isSmokeOrAuditMode) return;
                ClosePanel(ClockCalendarPopup);
                _orch.Coordinator.RequestExpand(ExpandedTabId.Now);
            };
            PopupUpcomingList.Children.Add(row);
        }
    }

    private void RebuildPopupCalendarGrid()
    {
        var culture = GetActiveUiCulture();
        bool isEn = string.Equals(_orch.Config.Language, "en", StringComparison.OrdinalIgnoreCase);

        string rawMonth = _popupDisplayMonth.ToString("MMMM yyyy", culture);
        PopupMonthTitle.Text = string.IsNullOrEmpty(rawMonth)
            ? rawMonth
            : char.ToUpper(rawMonth[0], culture) + rawMonth[1..];

        var today = _popupRenderedToday;
        bool isCurrentMonth = _popupDisplayMonth.Year == today.Year && _popupDisplayMonth.Month == today.Month;
        BtnPopupToday.Content = isEn ? "Today" : "Bugün";
        BtnPopupToday.Visibility = isCurrentMonth ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(BtnPopupPrevMonth, isEn ? "Previous month" : "Önceki ay");
        AutomationProperties.SetName(BtnPopupNextMonth, isEn ? "Next month" : "Sonraki ay");

        var textFont = PanelResource<FontFamily>(ClockCalendarCard, "PanelTextFont");
        if (PopupWeekdayGrid.Children.Count == 0 || !Equals(PopupWeekdayGrid.Tag, _orch.Config.Language))
        {
            PopupWeekdayGrid.Tag = _orch.Config.Language;
            PopupWeekdayGrid.Children.Clear();
            string[] weekdays = isEn
                ? ["MO", "TU", "WE", "TH", "FR", "SA", "SU"]
                : ["PZT", "SAL", "ÇAR", "PER", "CUM", "CMT", "PAZ"];
            var weekdayBrush = PanelBrush(PanelTertiaryColor);
            foreach (string wd in weekdays)
            {
                PopupWeekdayGrid.Children.Add(new TextBlock
                {
                    Text = wd,
                    FontFamily = textFont,
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = weekdayBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 6)
                });
            }
        }

        var eventDays = new HashSet<DateTime>(GetPopupUpcomingEvents().Select(m => m.StartUtc.ToLocalTime().Date));

        PopupDaysGrid.Children.Clear();
        var firstOfMonth = new DateTime(_popupDisplayMonth.Year, _popupDisplayMonth.Month, 1);
        int mondayOffset = ((int)firstOfMonth.DayOfWeek + 6) % 7; // Mon=0 .. Sun=6
        DateTime gridStart = firstOfMonth.AddDays(-mondayOffset);

        Color accent = PanelAccent();
        var todayBg = PanelBrush(accent, 0x38);
        var todayRing = PanelBrush(accent, 0x70);
        var normalFg = PanelBrush(PanelBodyColor);
        var weekendFg = PanelBrush(PanelSecondaryColor);
        var outsideFg = PanelBrush(Color.FromRgb(0x44, 0x44, 0x4C));
        var dotBrush = PanelBrush(accent, 0xE0);
        var hoverBrush = PanelBrush(Colors.White);

        for (int i = 0; i < 42; i++)
        {
            DateTime cellDate = gridStart.AddDays(i);
            bool inMonth = cellDate.Month == _popupDisplayMonth.Month && cellDate.Year == _popupDisplayMonth.Year;
            bool isToday = cellDate.Date == today.Date;
            bool isWeekend = cellDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            var cell = new Grid { Height = 32, Margin = new Thickness(2), Background = Brushes.Transparent };
            var hover = new Border { CornerRadius = new CornerRadius(10), Background = hoverBrush, Opacity = 0 };
            cell.Children.Add(hover);
            if (isToday)
            {
                cell.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = todayBg,
                    BorderBrush = todayRing,
                    BorderThickness = new Thickness(1)
                });
            }
            var number = new TextBlock
            {
                Text = cellDate.Day.ToString(CultureInfo.InvariantCulture),
                FontFamily = textFont,
                FontSize = 12,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isToday ? PanelBrush(Colors.White) : !inMonth ? outsideFg : isWeekend ? weekendFg : normalFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
            cell.Children.Add(number);
            if (inMonth && eventDays.Contains(cellDate.Date))
            {
                cell.Children.Add(new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = isToday ? PanelBrush(Colors.White, 0xE0) : dotBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 3)
                });
            }

            if (!isToday && inMonth)
            {
                cell.MouseEnter += (_, _) => hover.BeginAnimation(OpacityProperty, Motion.To(0.06, 120));
                cell.MouseLeave += (_, _) => hover.BeginAnimation(OpacityProperty, Motion.To(0, 200));
            }

            PopupDaysGrid.Children.Add(cell);
        }
    }

    // ---- Hover tooltip pill and the AI quota card ----------------------------------------------

    private bool IsDockAiShown() =>
        _orch.Config.ShowAiQuotaInIslandAndDock && _orch.AiQuota.GetSummary().TotalCount > 0;

    private void UpdateHoverTooltip(int slotIdx)
    {
        if (slotIdx < 0 || slotIdx >= _slots.Count || !_isHovered)
        {
            _hoveredSlotIndex = -1;
            DockTooltipBorder.Opacity = 0;
            return;
        }

        var slot = _slots[slotIdx];
        if (!DockPillContainer.IsLoaded || !slot.Container.IsDescendantOf(DockRootGrid))
        {
            DockTooltipBorder.Opacity = 0;
            return;
        }

        if (!string.Equals(_lastTooltipTitle, slot.TooltipLabel, StringComparison.Ordinal))
        {
            _lastTooltipTitle = slot.TooltipLabel;
            DockTooltipText.Text = slot.TooltipLabel;
        }

        bool wantAiCard = slot.Kind == SlotKind.IslandSync && IsDockAiShown();
        bool visible = DockTooltipBorder.Opacity > 0.5;
        if (wantAiCard != _aiTooltipMode)
        {
            _aiTooltipMode = wantAiCard;
            DockTooltipPill.Visibility = wantAiCard ? Visibility.Collapsed : Visibility.Visible;
            if (wantAiCard)
            {
                BuildAiTooltipContent(_orch.AiQuota.GetSnapshots(), _orch.AiQuota.GetSummary());
                if (visible) ShowAiTooltipPopup();
            }
            else
            {
                DockAiTooltipPopup.IsOpen = false;
                if (visible) PlayTooltipPillEntrance();
            }
        }

        Point anchorPt = slot.Container.TranslatePoint(new Point(slot.Container.Width * 0.5, 0), DockRootGrid);
        double extraLift = Math.Abs(slot.CurrentLiftY) + (slot.CurrentScale - 1.0) * _orch.Config.DockIconSizeDip * 0.85;
        if (wantAiCard) PositionAiTooltip(anchorPt, slot.Container.ActualHeight, extraLift);
        else PositionTooltipPill(anchorPt, slot.Container.ActualHeight, extraLift);
        _hoveredSlotIndex = slotIdx;

        if (!visible) ScheduleTooltipReveal();
    }

    private void PositionTooltipPill(Point anchorPt, double slotHeight, double extraLift)
    {
        DockTooltipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tipW = DockTooltipBorder.DesiredSize.Width;
        double tipH = DockTooltipBorder.DesiredSize.Height;
        double x = Math.Clamp(anchorPt.X - tipW * 0.5, 8.0, Math.Max(8.0, Width - tipW - 8.0));
        double y = IsDockAtTop
            ? anchorPt.Y + slotHeight + 2.0 + extraLift
            : Math.Max(2.0, anchorPt.Y - tipH - 2.0 - extraLift);
        DockTooltipTransform.X = Math.Round(x);
        DockTooltipTransform.Y = Math.Round(y);
    }

    private void PositionAiTooltip(Point anchorPt, double slotHeight, double extraLift)
    {
        DockAiTooltipShell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = DockAiTooltipShell.DesiredSize;
        Thickness pad = DockAiTooltipShell.Padding;
        double x = Math.Round(anchorPt.X - size.Width * 0.5);
        double y = Math.Round(IsDockAtTop
            ? anchorPt.Y + slotHeight + 8.0 + extraLift - pad.Top
            : anchorPt.Y - extraLift - 10.0 - (size.Height - pad.Bottom));
        if (Math.Abs(DockAiTooltipPopup.HorizontalOffset - x) > 0.5) DockAiTooltipPopup.HorizontalOffset = x;
        if (Math.Abs(DockAiTooltipPopup.VerticalOffset - y) > 0.5) DockAiTooltipPopup.VerticalOffset = y;
    }

    /// <summary>Tooltips wait ~350 ms before the first reveal; moving straight on to a neighbour shows it at once.</summary>
    private void ScheduleTooltipReveal()
    {
        if (_isSmokeOrAuditMode) return;
        if (_tooltipDelayTimer is { IsEnabled: true }) return;
        double sinceHiddenMs = (Stopwatch.GetTimestamp() - _tooltipHiddenTicks) * 1000.0 / Stopwatch.Frequency;
        if (_tooltipHiddenTicks != 0 && sinceHiddenMs < 450)
        {
            RevealTooltip();
            return;
        }
        if (_tooltipDelayTimer is null)
        {
            _tooltipDelayTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(TooltipDelayMs) };
            _tooltipDelayTimer.Tick += (_, _) =>
            {
                _tooltipDelayTimer.Stop();
                RevealTooltip();
            };
        }
        _tooltipDelayTimer.Start();
    }

    private void RevealTooltip()
    {
        if (_isClosed || !_isHovered || _hoveredSlotIndex < 0 || _isIntellihideTucked || _isReorderDragging || _isEdgeDragging) return;
        DockTooltipBorder.Opacity = 1;
        if (_aiTooltipMode) ShowAiTooltipPopup();
        else PlayTooltipPillEntrance();
    }

    private void PlayTooltipPillEntrance()
    {
        if (!PanelMotion)
        {
            DockTooltipPill.BeginAnimation(OpacityProperty, null);
            DockTooltipPill.Opacity = 1;
            return;
        }
        double rise = IsDockAtTop ? -4 : 4;
        DockTooltipRise.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(rise, 0, TooltipFadeMs + 80));
        DockTooltipPill.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, TooltipFadeMs));
    }

    private void ShowAiTooltipPopup()
    {
        if (_hoveredSlotIndex >= 0 && _hoveredSlotIndex < _slots.Count)
        {
            var slot = _slots[_hoveredSlotIndex];
            Point anchorPt = slot.Container.TranslatePoint(new Point(slot.Container.Width * 0.5, 0), DockRootGrid);
            PositionAiTooltip(anchorPt, slot.Container.ActualHeight, Math.Abs(slot.CurrentLiftY));
        }
        DockAiTooltipPopup.IsOpen = true;
        if (!PanelMotion)
        {
            DockAiTooltipCard.BeginAnimation(OpacityProperty, null);
            DockAiTooltipCard.Opacity = 1;
            return;
        }
        double rise = IsDockAtTop ? -6 : 6;
        DockAiTooltipRise.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(rise, 0, 260));
        DockAiTooltipCard.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, TooltipFadeMs + 40));
    }

    /// <summary>Anything that hides the tooltip gate (mouse leave, drag, tuck) also closes the AI card.</summary>
    private void OnDockTooltipGateChanged(object? sender, EventArgs e)
    {
        if (DockTooltipBorder.Opacity > 0.01) return;
        _tooltipDelayTimer?.Stop();
        _tooltipHiddenTicks = Stopwatch.GetTimestamp();
        if (DockAiTooltipPopup.IsOpen) DockAiTooltipPopup.IsOpen = false;
    }

    private static readonly Regex EmailPattern = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>"ealas977@gmail.com" or "ealas977" → "ea•••77", so shared screenshots never show a full account.</summary>
    internal static string MaskIdentity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string v = raw.Trim();
        int at = v.IndexOf('@');
        if (at > 0) v = v[..at];
        if (v.Length <= 2) return v[..1] + "•••";
        if (v.Length <= 5) return v[..1] + "•••" + v[^1..];
        return v[..2] + "•••" + v[^2..];
    }

    /// <summary>Masks e-mails, the slot's account and handle-like label parts ("ChatGPT · ealas977" → "ChatGPT · ea•••77").</summary>
    internal static string MaskQuotaLabel(string label, string? account)
    {
        if (string.IsNullOrWhiteSpace(label)) return string.Empty;
        string result = EmailPattern.Replace(label, m => MaskIdentity(m.Value));
        if (!string.IsNullOrWhiteSpace(account))
        {
            string acc = account.Trim();
            int at = acc.IndexOf('@');
            string handle = at > 0 ? acc[..at] : acc;
            if (handle.Length >= 3) result = result.Replace(handle, MaskIdentity(handle), StringComparison.OrdinalIgnoreCase);
        }
        var parts = result.Split(" · ");
        for (int i = 1; i < parts.Length; i++)
        {
            string p = parts[i].Trim();
            if (p.Length >= 4 && !p.Contains(' ') && !p.Contains('•') && !p.StartsWith('#')) parts[i] = MaskIdentity(p);
        }
        return string.Join(" · ", parts);
    }

    private static string FormatQuotaSpan(TimeSpan span, bool isEn)
    {
        if (span.TotalDays >= 1) return isEn ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{(int)span.TotalDays} g {span.Hours} sa";
        if (span.TotalHours >= 1) return isEn ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalHours} sa {span.Minutes} dk";
        return isEn ? $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m" : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))} dk";
    }

    /// <summary>Header line plus up to three accounts: name, masked account, a hairline bar and the reset time.</summary>
    private void BuildAiTooltipContent(IReadOnlyList<AiQuotaSlotSnapshot> snaps, AiQuotaSummary summary)
    {
        string lang = _orch.Config.Language;
        bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        var host = DockAiTooltipHost;
        host.Children.Clear();
        var font = (FontFamily)FindResource("UiTextFont");

        var header = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(new TextBlock
        {
            Text = isEn ? "AI quota" : "AI kotası",
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = PanelBrush(PanelTitleColor)
        });
        var readyText = new TextBlock
        {
            Text = isEn ? $"{summary.ReadyCount}/{summary.TotalCount} ready" : $"{summary.ReadyCount}/{summary.TotalCount} hazır",
            FontFamily = font,
            FontSize = 11,
            Foreground = PanelBrush(PanelTertiaryColor),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Typography.SetNumeralAlignment(readyText, FontNumeralAlignment.Tabular);
        header.Children.Add(readyText);
        host.Children.Add(header);

        var ordered = new List<AiQuotaSlotSnapshot>(snaps.Count);
        if (summary.AttentionSlot is { } attention)
        {
            var match = snaps.FirstOrDefault(s => s.Id == attention.Id);
            if (match is not null) ordered.Add(match);
        }
        ordered.AddRange(snaps.Where(s => ordered.All(o => o.Id != s.Id)));

        foreach (var s in ordered.Take(3))
        {
            double pct = Math.Clamp(s.HasQuotaData ? s.EffectiveRemainingPercent : 0, 0, 100);
            Color status = s.RequiresReconnect ? PanelTertiaryColor
                : s.IsExhausted ? PanelNegative
                : s.IsLow ? PanelWarning
                : PanelPositive;

            var block = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = PanelBrush(status),
                Margin = new Thickness(0, 1, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            string provider = string.IsNullOrWhiteSpace(s.Provider) ? s.Label : s.Provider;
            string masked = MaskQuotaLabel(s.Label, s.AccountIdentifier);
            string account = !string.IsNullOrWhiteSpace(s.AccountIdentifier)
                ? MaskIdentity(s.AccountIdentifier)
                : masked.StartsWith(provider, StringComparison.OrdinalIgnoreCase)
                    ? masked[provider.Length..].TrimStart(' ', '·').Trim()
                    : masked;
            var name = new TextBlock
            {
                FontFamily = font,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            name.Inlines.Add(new Run(provider) { Foreground = PanelBrush(PanelBodyColor), FontWeight = FontWeights.Medium });
            if (!string.IsNullOrWhiteSpace(account) && !string.Equals(account, provider, StringComparison.OrdinalIgnoreCase))
                name.Inlines.Add(new Run("  " + account) { Foreground = PanelBrush(PanelTertiaryColor), FontSize = 11 });
            Grid.SetColumn(name, 1);
            line.Children.Add(name);

            int pctInt = (int)Math.Round(pct);
            var pctText = new TextBlock
            {
                Text = isEn ? $"{pctInt}%" : $"%{pctInt}",
                FontFamily = font,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = PanelBrush(PanelSecondaryColor),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(pctText, FontNumeralAlignment.Tabular);
            Grid.SetColumn(pctText, 2);
            line.Children.Add(pctText);
            block.Children.Add(line);

            // Hairline bar: track plus a fill proportional to what is left.
            var bar = new Grid { Height = 3, Margin = new Thickness(14, 6, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0.001), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - pct, 0.001), GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(1.5), Background = PanelBrush(Colors.White, 0x14) };
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            if (pct > 0.5)
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = PanelBrush(status, 0xA8) });
            block.Children.Add(bar);

            string resetText = s.RequiresReconnect
                ? (isEn ? "Reconnect needed" : "Yeniden bağlanın")
                : s.Remaining > TimeSpan.Zero && (s.IsExhausted || s.IsLow || !s.IsReady)
                    ? (isEn ? $"Resets in {FormatQuotaSpan(s.Remaining, true)}" : $"{FormatQuotaSpan(s.Remaining, false)} sonra yenilenir")
                    : s.IsExhausted
                        ? (isEn ? "Waiting for reset" : "Yenilenme bekleniyor")
                        : (isEn ? "Ready" : "Hazır");
            block.Children.Add(new TextBlock
            {
                Text = resetText,
                Margin = new Thickness(14, 4, 0, 0),
                FontFamily = font,
                FontSize = 10.5,
                Foreground = PanelBrush(PanelTertiaryColor)
            });
            host.Children.Add(block);
        }

        if (ordered.Count > 3)
        {
            int more = ordered.Count - 3;
            host.Children.Add(new TextBlock
            {
                Text = isEn ? $"+{more} more" : $"+{more} hesap daha",
                Margin = new Thickness(14, 10, 0, 0),
                FontFamily = font,
                FontSize = 10.5,
                Foreground = PanelBrush(PanelTertiaryColor)
            });
        }
    }

    // ---- AI quota pill on the dock ---------------------------------------------------------------

    private void AddIslandSyncSlot(string lang, int iconSize, double tileW, double tileH, bool showAiSlot)
    {
        var scale = new ScaleTransform(1.0, 1.0, tileW * 0.5, tileH);
        var translate = new TranslateTransform(0.0, 0.0);
        var tg = new TransformGroup();
        tg.Children.Add(scale);
        tg.Children.Add(translate);

        var container = new Grid
        {
            Width = tileW,
            Height = tileH + 9.0,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            RenderTransform = tg
        };

        // Mini island pill synced with the top notch; with AI quota on it becomes a calm "● AI 5/7" chip.
        double pillH = Math.Max(20.0, Math.Round(iconSize * 0.68));
        var plate = new Border
        {
            Height = pillH,
            MinWidth = Math.Max(36.0, iconSize + 10.0),
            CornerRadius = new CornerRadius(pillH * 0.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Math.Round((iconSize - pillH) * 0.5), 0, 0)
        };
        _islandPlate = plate;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Mini camera lens ring + live dot (island mode)
        var lensOuter = new Grid
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        lensOuter.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromRgb(10, 14, 24)),
            Stroke = DockTint(0x30),
            StrokeThickness = 0.6
        });
        lensOuter.Children.Add(new Ellipse
        {
            Width = 3.5,
            Height = 3.5,
            Fill = new SolidColorBrush(Color.FromRgb(22, 64, 130))
        });
        _islandLens = lensOuter;

        var liveDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _islandSyncDot = liveDot;

        // AI chip: status dot + "AI 5/7"
        _islandAiDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(PanelPositive),
            Margin = new Thickness(0, 1, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _islandAiBadgeText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiTextFont"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = DockTextBrush(primary: true),
            VerticalAlignment = VerticalAlignment.Center
        };
        Typography.SetNumeralAlignment(_islandAiBadgeText, FontNumeralAlignment.Tabular);
        var aiRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        aiRow.Children.Add(_islandAiDot);
        aiRow.Children.Add(_islandAiBadgeText);
        _islandAiBadge = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = aiRow
        };

        row.Children.Add(lensOuter);
        row.Children.Add(liveDot);
        row.Children.Add(_islandAiBadge);

        var inner = new Grid();
        inner.Children.Add(row);

        _islandSyncBadgeText = new TextBlock
        {
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        };
        _islandSyncBadge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(255, 69, 58)),
            Padding = new Thickness(3.5, 0, 3.5, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -3, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _islandSyncBadgeText
        };
        inner.Children.Add(_islandSyncBadge);
        plate.Child = inner;
        container.Children.Add(plate);

        var slotVisual = new DockSlotVisual
        {
            Container = container,
            Scale = scale,
            Translate = translate,
            IconPlate = plate,
            RunningDot = null,
            RunningPill = null,
            TooltipLabel = Strings.DockIslandSyncLabel(lang),
            Kind = SlotKind.IslandSync
        };
        _islandSyncSlot = slotVisual;

        container.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            if (_orch.Coordinator.Current.State == IslandPresentationState.S4Expanded)
            {
                _orch.Coordinator.RequestCollapse(forceUnpin: true);
            }
            else
            {
                _orch.Coordinator.RequestExpand(_orch.Coordinator.Current.PrimaryActivity?.TargetTab);
            }
        };
        container.MouseRightButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            HandleSlotContextMenu(slotVisual);
        };

        _slots.Add(slotVisual);
        DockItemsHost.Children.Add(container);
        ApplyIslandPillMode(showAiSlot);
        UpdateDockAiBadgeAndTooltip();
    }

    private void ApplyIslandPillMode(bool ai)
    {
        if (_islandPlate is null) return;
        _islandPlate.Background = ai ? DockTint(0x12) : new SolidColorBrush(Color.FromRgb(0, 0, 0));
        _islandPlate.BorderBrush = ai ? DockTint(0x0C) : DockTint(0x36);
        _islandPlate.BorderThickness = new Thickness(ai ? 0.6 : 0.8);
        _islandPlate.Padding = ai ? new Thickness(9, 0, 10, 1) : new Thickness(0);
        if (_islandLens is not null) _islandLens.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        if (_islandSyncDot is not null) _islandSyncDot.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        if (_islandAiBadge is not null) _islandAiBadge.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDockAiBadgeAndTooltip()
    {
        var summary = _orch.AiQuota.GetSummary();
        bool showAi = _orch.Config.ShowAiQuotaInIslandAndDock && summary.TotalCount > 0;

        ApplyIslandPillMode(showAi);
        if (showAi && _islandAiBadgeText is not null)
        {
            Color accent = ParseHexColor(summary.DockBadgeHex, PanelPositive);
            _islandAiBadgeText.Text = $"AI {summary.DockBadgeText}";
            _islandAiBadgeText.Foreground = DockTextBrush(primary: true);
            if (_islandAiDot is not null) _islandAiDot.Fill = new SolidColorBrush(accent);
        }

        if (_islandSyncSlot is not null)
        {
            // The rich card carries the per-account detail; the plain label never includes an account.
            string baseLabel = Strings.DockIslandSyncLabel(_orch.Config.Language);
            _islandSyncSlot.TooltipLabel = showAi ? $"{baseLabel} · AI {summary.DockBadgeText}" : baseLabel;
        }

        if (DockAiTooltipPopup.IsOpen)
        {
            if (showAi) BuildAiTooltipContent(_orch.AiQuota.GetSnapshots(), summary);
            else DockAiTooltipPopup.IsOpen = false;
        }

        UpdateStartAiStatusPill();
    }

    // ---- Render audit --------------------------------------------------------------------------

    /// <summary>Renders a detached shell (card plus its shadow room) centred on the audit backdrop.</summary>
    private static RenderTargetBitmap RenderShellForAudit(Border shell, double cardW, int width, int height)
    {
        shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = shell.DesiredSize;
        shell.Arrange(new Rect(0, 0, size.Width, size.Height));
        shell.UpdateLayout();
        var shellBmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
        shellBmp.Render(shell);
        shellBmp.Freeze();

        Thickness pad = shell.Padding;
        double cardH = size.Height - pad.Top - pad.Bottom;
        int finalW = Math.Max(width, (int)Math.Ceiling(cardW) + 24);
        int finalH = Math.Max(height, (int)Math.Ceiling(cardH) + 40);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 16)), null, new Rect(0, 0, finalW, finalH));
            double x = Math.Round((finalW - cardW) * 0.5) - pad.Left;
            double y = Math.Round((finalH - cardH) * 0.5) - pad.Top;
            dc.DrawImage(shellBmp, new Rect(x, y, size.Width, size.Height));
        }

        var bmp = new RenderTargetBitmap(finalW, finalH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
    }

    private static IReadOnlyList<MeetingOccurrence> AuditCalendarEvents()
    {
        static DateTimeOffset Local(int d, int h, int m) => new(new DateTime(2026, 11, d, h, m, 0, DateTimeKind.Local));
        return
        [
            new MeetingOccurrence("Tasarım incelemesi", Local(16, 10, 30), Local(16, 11, 15), null),
            new MeetingOccurrence("Haftalık senkron", Local(16, 14, 0), Local(16, 14, 30), null),
            new MeetingOccurrence("Ürün demosu", Local(18, 11, 0), Local(18, 12, 0), null)
        ];
    }

    public RenderTargetBitmap RenderCalendarPopupForAudit(int width = 300, int height = 340)
    {
        SetSmokeOrAuditMode(true);
        ClockCalendarPopup.Child = null;
        try
        {
            _popupAuditNow = new DateTime(2026, 11, 16, 9, 41, 27);
            _popupAuditEvents = AuditCalendarEvents();
            PrepareClockPanel();
            SyncCoordinatorState(_orch.Coordinator.Current);
            return RenderShellForAudit(ClockCalendarShell, ClockCalendarCard.Width, width, height);
        }
        finally
        {
            _popupAuditNow = null;
            _popupAuditEvents = null;
            ClockCalendarPopup.Child = ClockCalendarShell;
        }
    }

    public RenderTargetBitmap RenderTrayPopupForAudit(int width = 412, int height = 460, bool empty = false)
    {
        SetSmokeOrAuditMode(true);
        // Keep the closed PopupRoot out of the standalone capture's layout pass.
        TrayOverflowPopup.Child = null;
        try
        {
            var demoItems = new BackgroundTrayAppItem[]
            {
                new("audit:discord", "Discord", "Discord.exe", null, 1, 0, 0, null),
                new("audit:whatsapp", "WhatsApp", "WhatsApp.Root.exe", null, 2, 0, 0, null),
                new("audit:steam", "Steam", "steam.exe", null, 3, 0, 0, null),
                new("audit:ytmusic", "YouTube Music", "YouTube Music.exe", null, 4, 0, 0, null),
                new("audit:chatgpt", "ChatGPT", "ChatGPT.exe", null, 5, 0, 0, null),
                new("audit:wpe", "Wallpaper Engine", "wallpaper32.exe", null, 6, 0, 0, null)
            };
            TrayOverflowCard.Width = Math.Clamp(width - 24, 280, 340);
            RebuildTrayOverflowGrid(empty ? [] : demoItems);
            // Show the second row as hovered so the capture proves the fill and the revealed actions.
            if (!empty && TrayOverflowGrid.Children.Count > 1 && TrayOverflowGrid.Children[1] is Button hovered)
            {
                hovered.ApplyTemplate();
                if (hovered.Template.FindName("Fill", hovered) is UIElement fill) fill.Opacity = 0.06;
                if (hovered.Content is Grid g && g.Children.Count > 2 && g.Children[2] is Grid right && right.Children.Count > 1)
                {
                    right.Children[0].Opacity = 0;
                    right.Children[1].Opacity = 1;
                }
            }
            SyncCoordinatorState(_orch.Coordinator.Current);
            return RenderShellForAudit(TrayOverflowShell, TrayOverflowCard.Width, width, height);
        }
        finally
        {
            TrayOverflowPopup.Child = TrayOverflowShell;
        }
    }

    /// <summary>The dock hover pill and the AI quota card (with masked accounts) side by side.</summary>
    public RenderTargetBitmap RenderAiTooltipForAudit(int width = 600, int height = 260)
    {
        SetSmokeOrAuditMode(true);
        var seeded = _orch.AiQuota.GetSnapshots();
        var demo = new List<AiQuotaSlotSnapshot>();
        if (seeded.Count > 0)
        {
            var b = seeded[0];
            demo.Add(b with
            {
                Provider = "ChatGPT", Label = "ChatGPT · nova.lane", AccountIdentifier = "nova.lane@example.com",
                RemainingPercent = 100, WeeklyRemainingPercent = null, HasPrimaryQuota = true,
                IsReady = true, IsLow = false, IsExhausted = false, RequiresReconnect = false, Remaining = TimeSpan.Zero
            });
            demo.Add(b with
            {
                Id = "audit_gpt2", Provider = "ChatGPT", Label = "ChatGPT · studio.dev", AccountIdentifier = "studio.dev@example.com",
                RemainingPercent = 34, WeeklyRemainingPercent = null, HasPrimaryQuota = true,
                IsReady = true, IsLow = true, IsExhausted = false, RequiresReconnect = false, Remaining = new TimeSpan(2, 14, 0)
            });
            demo.Add(b with
            {
                Id = "audit_claude", Provider = "Claude", Label = "Claude · k.morgan", AccountIdentifier = "k.morgan@example.com",
                RemainingPercent = 0, WeeklyRemainingPercent = null, HasPrimaryQuota = true,
                IsReady = false, IsLow = false, IsExhausted = true, RequiresReconnect = false, Remaining = new TimeSpan(1, 12, 0)
            });
            for (int i = 0; i < 4; i++) demo.Add(b with { Id = $"audit_more{i}" });
        }
        var summary = new AiQuotaSummary(5, 1, 1, demo.Count, 0, 0, null, null, $"5/{demo.Count}", "#FF9F0A");
        BuildAiTooltipContent(demo, summary);
        DockAiTooltipPopup.Child = null;
        DockTooltipBorder.Child = null;
        try
        {
            DockTooltipText.Text = "Visual Studio Code";
            DockTooltipPill.Opacity = 1;
            DockAiTooltipCard.Opacity = 1;
            var pillWrap = new Border { Padding = new Thickness(14, 10, 14, 14), Child = DockTooltipPill };
            pillWrap.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size pillSize = pillWrap.DesiredSize;
            pillWrap.Arrange(new Rect(pillSize));
            pillWrap.UpdateLayout();
            var pillBmp = new RenderTargetBitmap((int)Math.Ceiling(pillSize.Width), (int)Math.Ceiling(pillSize.Height), 96, 96, PixelFormats.Pbgra32);
            pillBmp.Render(pillWrap);
            pillBmp.Freeze();
            pillWrap.Child = null;

            DockAiTooltipShell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size cardSize = DockAiTooltipShell.DesiredSize;
            DockAiTooltipShell.Arrange(new Rect(cardSize));
            DockAiTooltipShell.UpdateLayout();
            var cardBmp = new RenderTargetBitmap((int)Math.Ceiling(cardSize.Width), (int)Math.Ceiling(cardSize.Height), 96, 96, PixelFormats.Pbgra32);
            cardBmp.Render(DockAiTooltipShell);
            cardBmp.Freeze();

            int finalH = Math.Max(height, (int)Math.Ceiling(cardSize.Height) + 20);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 16)), null, new Rect(0, 0, width, finalH));
                dc.DrawImage(pillBmp, new Rect(48, Math.Round(finalH * 0.5 - pillBmp.Height * 0.5), pillBmp.Width, pillBmp.Height));
                dc.DrawImage(cardBmp, new Rect(width - cardBmp.Width - 36, Math.Round((finalH - cardBmp.Height) * 0.5), cardBmp.Width, cardBmp.Height));
            }
            var bmp = new RenderTargetBitmap(width, finalH, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        finally
        {
            DockTooltipBorder.Child = DockTooltipPill;
            DockAiTooltipPopup.Child = DockAiTooltipShell;
        }
    }
}
