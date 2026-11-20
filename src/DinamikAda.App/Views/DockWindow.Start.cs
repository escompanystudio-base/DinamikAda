using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DinamikAda.App.Views.DockWidgets;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Arbitration;
using DinamikAda.Core.Localization;
using DinamikAda.Core.MathAndSearch;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Platform.Win32.Windowing;

namespace DinamikAda.App.Views;

/// <summary>
/// The dock Start menu: a calm dark panel with a pinned row, a borderless all-apps grid that pages in as you
/// scroll, quiet quick-action pills and footer icon buttons. Opening, closing, filtering and launching all use
/// the shared soft <see cref="Motion"/> language.
/// </summary>
public partial class DockWindow
{
    /// <summary>Transparent room around the card inside the popup so the soft shadow is never clipped.</summary>
    private const double StartShellMarginDip = 36.0;
    private const int StartFirstPageSize = 60;
    private const int StartNextPageSize = 40;
    private const double StartTileHeight = 84.0;
    private const double StartTileRowHeight = StartTileHeight + 4.0;

    private readonly List<AppLaunchItem> _displayedStartApps = new(64);
    private (AppLaunchItem App, bool Pinned)[] _startRanked = [];
    private int _startRenderedCount;
    private Button? _startBestTile;
    private bool _startClosing;
    private double _startTileLabelWidth = 80;
    private double _startScrollBaseHeight = 230;
    private readonly ScaleTransform _startShellScale = new(1, 1);
    private readonly TranslateTransform _startShellShift = new();

    private bool StartMotionEnabled => !_isSmokeOrAuditMode && SystemParameters.ClientAreaAnimation;

    // ---- Open / close ------------------------------------------------------------------------------

    private void ToggleStartPopup()
    {
        TrayOverflowPopup.IsOpen = false;
        ClockCalendarPopup.IsOpen = false;
        if (StartLaunchPopup.IsOpen)
        {
            CloseStartPopupSoft();
            return;
        }

        double sinceClose = (Stopwatch.GetTimestamp() - _lastStartPopupClosedTicks) / (double)Stopwatch.Frequency;
        if (_lastStartPopupClosedTicks != 0 && sinceClose < 0.22)
        {
            return;
        }

        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        SizeStartMenu(_activeMonitor.EffectiveWorkWidthPx / scale, _activeMonitor.EffectiveWorkHeightPx / scale);
        PlaceStartPopup();
        StartLaunchPopup.PlacementTarget = DockPillContainer;
        // Start invisible so the first frame never flashes the finished panel before the entrance plays.
        if (StartMotionEnabled) StartLaunchShell.Opacity = 0;
        StartLaunchPopup.IsOpen = true;
    }

    /// <summary>Keeps the visible card exactly where the old card sat: 24 DIP from the dock, opening away from its edge.</summary>
    private void PlaceStartPopup()
    {
        bool top = IsDockAtTop;
        StartLaunchPopup.Placement = top ? PlacementMode.Bottom : PlacementMode.Top;
        StartLaunchPopup.VerticalOffset = top ? 24 - StartShellMarginDip : -24 + StartShellMarginDip;
        StartLaunchPopup.HorizontalOffset = -StartShellMarginDip;
        StartLaunchShell.RenderTransformOrigin = new Point(0.5, top ? 0.0 : 1.0);
    }

    /// <summary>Fades, shrinks to 0.98 and sinks 6px toward the dock, then really closes the popup.</summary>
    private void CloseStartPopupSoft()
    {
        if (!StartLaunchPopup.IsOpen || _startClosing) return;
        if (!StartMotionEnabled)
        {
            StartLaunchPopup.IsOpen = false;
            return;
        }

        _startClosing = true;
        StartLaunchShell.IsHitTestVisible = false;
        EnsureStartShellTransform();
        double towardDock = IsDockAtTop ? -1 : 1;
        var fade = Motion.To(0, 220, Motion.Exit);
        fade.Completed += (_, _) =>
        {
            if (_startClosing) StartLaunchPopup.IsOpen = false;
        };
        StartLaunchShell.BeginAnimation(OpacityProperty, fade);
        _startShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(0.98, 220, Motion.Exit));
        _startShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(0.98, 220, Motion.Exit));
        _startShellShift.BeginAnimation(TranslateTransform.YProperty, Motion.To(6 * towardDock, 220, Motion.Exit));
    }

    private void EnsureStartShellTransform()
    {
        if (StartLaunchShell.RenderTransform is not TransformGroup)
        {
            StartLaunchShell.RenderTransform = new TransformGroup { Children = { _startShellScale, _startShellShift } };
        }
    }

    private void ResetStartShellVisual()
    {
        _startClosing = false;
        EnsureStartShellTransform();
        StartLaunchShell.BeginAnimation(OpacityProperty, null);
        _startShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _startShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _startShellShift.BeginAnimation(TranslateTransform.YProperty, null);
        StartLaunchShell.Opacity = 1;
        _startShellScale.ScaleX = _startShellScale.ScaleY = 1;
        _startShellShift.Y = 0;
        StartLaunchShell.IsHitTestVisible = true;
    }

    /// <summary>Panel fades in, grows from 0.97 at the dock edge and glides 10px away from the dock; items stagger in.</summary>
    private void PlayStartOpenAnimation()
    {
        ResetStartShellVisual();
        if (!StartMotionEnabled) return;

        double towardDock = IsDockAtTop ? -1 : 1;
        StartLaunchShell.BeginAnimation(OpacityProperty, Motion.FromTo(0, 1, 300));
        _startShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.FromTo(0.97, 1, Motion.EnterMs));
        _startShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.FromTo(0.97, 1, Motion.EnterMs));
        _startShellShift.BeginAnimation(TranslateTransform.YProperty, Motion.FromTo(10 * towardDock, 0, Motion.EnterMs));

        var items = new List<UIElement> { StartHeaderRow, StartSearchHost };
        foreach (UIElement chip in StartShortcutGrid.Children) items.Add(chip);
        if (StartPinnedSection.Visibility == Visibility.Visible)
        {
            items.Add(StartPinnedTitle);
            foreach (UIElement tile in StartPinnedGrid.Children) items.Add(tile);
        }
        items.Add(StartAppsHeader);
        items.Add(StartAppsScrollViewer);
        items.Add(StartFooterRow);

        for (int i = 0; i < items.Count; i++)
        {
            // The first 12 items arrive 18 ms apart; everything after that arrives together.
            double delay = 40 + Math.Min(i, 12) * 18;
            var el = items[i];
            if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
            {
                tt = new TranslateTransform();
                el.RenderTransform = tt;
            }
            el.BeginAnimation(OpacityProperty, Delayed(0, 1, delay, 320));
            tt.BeginAnimation(TranslateTransform.YProperty, Delayed(6 * towardDock, 0, delay, Motion.EnterMs));
        }
    }

    /// <summary>Holds <paramref name="from"/> through the delay so the element never flashes at its final value first.</summary>
    private static DoubleAnimationUsingKeyFrames Delayed(double from, double to, double delayMs, double ms)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + ms)), Motion.Soft));
        return a;
    }

    private void SizeStartMenu(double availableWidth, double availableHeight)
    {
        double cardW = Math.Clamp(availableWidth - 32, 300, 560);
        StartLaunchCard.Width = cardW;
        int columns = cardW >= 520 ? 5 : cardW >= 400 ? 4 : 3;
        StartAppsGrid.Columns = columns;
        StartPinnedGrid.Columns = columns;
        // card border 2 + side padding 44 - grid bleed 4, then per tile: margin 4 + padding 12.
        double columnW = (cardW - 2 - 44 + 4) / columns;
        _startTileLabelWidth = Math.Max(40, columnW - 16);
        _startScrollBaseHeight = Math.Clamp(availableHeight - 440, 170, 300);
        StartAppsScrollViewer.Height = _startScrollBaseHeight;
    }

    private void OnStartPopupOpened(object? sender, EventArgs e)
    {
        if (_isClosed) return;

        ApplyStartPopupLocalization();
        UpdateStartAiStatusPill();

        if (!string.IsNullOrEmpty(StartSearchBox.Text))
        {
            StartSearchBox.Text = string.Empty;
        }
        StartSearchPlaceholder.Visibility = Visibility.Visible;
        BtnStartSearchClear.Visibility = Visibility.Collapsed;
        StartLaunchError.Visibility = Visibility.Collapsed;

        var indexed = _orch.Launcher.GetIndexedAppsSnapshot();
        if (indexed.Count > 0)
        {
            _cachedStartApps = indexed;
        }

        RebuildStartAppsGrid(string.Empty, animate: false);
        StartAppsScrollViewer.ScrollToTop();
        SyncCoordinatorState(_orch.Coordinator.Current);
        PlayStartOpenAnimation();

        if (!_isSmokeOrAuditMode)
        {
            StartPopupLightDismissWatcher();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (StartLaunchPopup.IsOpen && !StartLaunchCard.IsKeyboardFocusWithin) StartSearchBox.Focus();
            });
            if (!_startAppsLoading)
            {
                _ = EnsureStartAppsIndexedAndRefreshAsync();
            }
        }
    }

    private void OnStartPopupClosed(object? sender, EventArgs e)
    {
        CancelStartIconLoad();
        ResetStartShellVisual();
        _lastStartPopupClosedTicks = Stopwatch.GetTimestamp();
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }
        StopPopupLightDismissWatcherIfIdle();
        ScheduleIntellihideEval();
    }

    private async Task EnsureStartAppsIndexedAndRefreshAsync()
    {
        _startAppsLoading = true;
        try
        {
            await _orch.Launcher.RebuildAppIndexAsync(includeStoreApps: true).ConfigureAwait(false);
            var fresh = _orch.Launcher.GetIndexedAppsSnapshot();
            if (fresh.Count == 0 || _isClosed) return;

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_isClosed) return;
                _cachedStartApps = fresh;
                if (StartLaunchPopup.IsOpen && !_startClosing)
                {
                    RebuildStartAppsGrid(StartSearchBox.Text ?? string.Empty);
                }
            });
        }
        catch
        {
            // Keep existing dock/app fallback items cleanly if index refresh fails.
        }
        finally
        {
            _startAppsLoading = false;
        }
    }

    // ---- Text and tooltips -------------------------------------------------------------------------

    /// <summary>Small-caps section label: uppercase with a hair space between letters for gentle tracking.</summary>
    private static string SmallCaps(string text, string lang)
    {
        var culture = lang.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("tr-TR")
            : CultureInfo.InvariantCulture;
        string upper = text.ToUpper(culture);
        var parts = new List<string>(upper.Length);
        var e = StringInfo.GetTextElementEnumerator(upper);
        while (e.MoveNext()) parts.Add(e.GetTextElement());
        return string.Join(" ", parts);
    }

    private static void SetSectionLabel(TextBlock label, string text, string lang)
    {
        label.Text = SmallCaps(text, lang);
        AutomationProperties.SetName(label, text);
    }

    private void SetStartTip(FrameworkElement el, string text)
    {
        var tip = new ToolTip
        {
            Content = text,
            Style = (Style)FindResource("StartGlassToolTipStyle"),
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                double dpi = VisualTreeHelper.GetDpi(el).DpiScaleY;
                return [new CustomPopupPlacement(
                    new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height + 10 * dpi),
                    PopupPrimaryAxis.Horizontal)];
            }
        };
        el.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(el, 350);
        ToolTipService.SetBetweenShowDelay(el, 120);
    }

    private void ApplyStartPopupLocalization()
    {
        string lang = _orch.Config.Language;
        StartBadgeText.Text = Strings.StartMenuBrandBadge(lang);
        StartModIslandText.Text = Strings.StartMenuModuleIsland(lang);
        StartModAiText.Text = Strings.StartMenuModuleAi(lang);
        StartModSystemText.Text = Strings.StartMenuModuleSystem(lang);
        StartModCommandText.Text = Strings.StartMenuModuleCommand(lang);
        StartSearchPlaceholder.Text = Strings.StartMenuSearchPlaceholder(lang);
        SetSectionLabel(StartPinnedTitle, Strings.Pick(lang, "Sabitlenenler", "Pinned"), lang);
        StartAppsEmptyText.Text = Strings.StartMenuNoMatches(lang);

        string user = _isSmokeOrAuditMode || string.IsNullOrWhiteSpace(Environment.UserName) ? "Windows" : Environment.UserName;
        StartUserNameText.Text = user;
        StartUserInitialText.Text = StringInfo.GetNextTextElement(user).ToUpper(CultureInfo.CurrentCulture);

        var tips = new (Button Button, string Text)[]
        {
            (BtnStartClassicWin, Strings.StartMenuClassicWin(lang)),
            (BtnStartSettings, Strings.Settings(lang)),
            (BtnStartExplorer, Strings.StartMenuExplorer(lang)),
            (BtnStartTerminal, "Terminal"),
            (BtnStartTaskMgr, Strings.Pick(lang, "Görev Yöneticisi", "Task Manager")),
            (BtnStartLock, Strings.StartMenuLock(lang)),
            (BtnStartPower, Strings.StartMenuPowerOptions(lang)),
            (BtnStartSearchClear, Strings.Pick(lang, "Aramayı temizle", "Clear search")),
        };
        foreach (var (button, text) in tips)
        {
            SetStartTip(button, text);
            AutomationProperties.SetName(button, text);
        }
    }

    // ---- App grid ----------------------------------------------------------------------------------

    private void RebuildStartAppsGrid(string rawQuery, bool animate = true)
    {
        string lang = _orch.Config.Language;
        string q = rawQuery.Trim();
        string? focusedApp = Keyboard.FocusedElement is Button focused
                             && (StartAppsGrid.Children.Contains(focused) || StartPinnedGrid.Children.Contains(focused))
            ? AutomationProperties.GetName(focused)
            : null;
        CancelStartIconLoad();

        var pool = new Dictionary<string, AppLaunchItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _cachedStartApps)
            pool.TryAdd(app.DisplayName, app);
        foreach (var dockItem in _currentAppItems)
        {
            string target = !string.IsNullOrWhiteSpace(dockItem.LaunchTarget)
                ? dockItem.LaunchTarget
                : (dockItem.ExePath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(target))
                pool.TryAdd(dockItem.DisplayName, new AppLaunchItem(dockItem.DisplayName, target, IsStoreApp: false));
        }

        var all = pool.Values
            .Select(app => (App: app,
                Score: q.Length == 0 ? 1 : FuzzyMatcher.Score(q, app.DisplayName),
                Pinned: _isSmokeOrAuditMode
                    ? _currentAppItems.Any(item => item.IsPinned && item.DisplayName == app.DisplayName)
                    : _appsService.IsStartAppPinned(app)))
            .ToArray();

        // Searching: best match first, pinned apps win ties. Browsing: plain A to Z (pinned apps have their own row).
        var matches = all.Where(result => result.Score > 0);
        var ordered = q.Length > 0
            ? matches.OrderByDescending(result => result.Score)
                .ThenByDescending(result => result.Pinned)
                .ThenBy(result => result.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            : matches.OrderBy(result => result.App.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        _startRanked = ordered.Select(result => (result.App, result.Pinned)).ToArray();

        // Pinned row follows the dock's own order.
        int maxPinned = StartPinnedGrid.Columns * 2;
        var pinned = all
            .Where(result => result.Pinned)
            .OrderBy(result =>
            {
                for (int i = 0; i < _currentAppItems.Count; i++)
                {
                    var item = _currentAppItems[i];
                    if (item.IsPinned && string.Equals(item.DisplayName, result.App.DisplayName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
                return int.MaxValue;
            })
            .ThenBy(result => result.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(maxPinned)
            .ToArray();

        _startBestTile = null;
        _displayedStartApps.Clear();
        StartAppsGrid.Children.Clear();
        StartPinnedGrid.Children.Clear();
        _startRenderedCount = 0;

        var tilesToLoad = new List<(AppLaunchItem App, Image Icon, Border Fallback)>();
        bool searching = q.Length > 0;
        bool showPinned = !searching && pinned.Length > 0;
        StartPinnedSection.Visibility = showPinned ? Visibility.Visible : Visibility.Collapsed;
        if (showPinned)
        {
            foreach (var result in pinned)
            {
                var (tile, icon, fallback) = CreateStartAppTile(result.App, isPinned: true, lang);
                StartPinnedGrid.Children.Add(tile);
                tilesToLoad.Add((result.App, icon, fallback));
                if (result.App.DisplayName == focusedApp) tile.Focus();
            }
        }

        // While searching the pinned row folds away; the list takes its room so the panel keeps its height.
        double pinnedRoom = pinned.Length == 0
            ? 0
            : 30 + Math.Ceiling(pinned.Length / (double)StartPinnedGrid.Columns) * StartTileRowHeight;
        StartAppsScrollViewer.Height = _startScrollBaseHeight + (searching ? pinnedRoom : 0);

        SetSectionLabel(StartAppsSectionTitle,
            searching ? Strings.Pick(lang, "Sonuçlar", "Results") : Strings.Pick(lang, "Tüm uygulamalar", "All apps"),
            lang);
        StartAppsCountText.Text = _startRanked.Length.ToString(CultureInfo.InvariantCulture);
        AutomationProperties.SetName(StartAppsCountText, Strings.StartMenuAppsCount(lang, _startRanked.Length));
        StartAppsEmptyText.Visibility = _startRanked.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartEnterHint.Text = Strings.Pick(lang, "Enter ile aç  ↵", "Enter to open  ↵");
        StartEnterHint.Visibility = searching && _startRanked.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        AppendStartTiles(StartFirstPageSize, tilesToLoad, focusedApp);

        if (searching && StartAppsGrid.Children.Count > 0 && StartAppsGrid.Children[0] is Button best)
        {
            // Enter launches this one; it stays highlighted until the keyboard moves elsewhere.
            best.Tag = "best";
            _startBestTile = best;
        }

        if (animate && StartMotionEnabled && StartLaunchPopup.IsOpen)
        {
            // Soft crossfade instead of a hard swap when results change.
            StartAppsGrid.BeginAnimation(OpacityProperty, Motion.FromTo(0.3, 1, 220));
            if (showPinned) StartPinnedSection.BeginAnimation(OpacityProperty, Motion.FromTo(0.3, 1, 220));
        }
    }

    /// <summary>Adds the next page of tiles. Long lists page in as the user scrolls, so 200+ apps stay light.</summary>
    private void AppendStartTiles(
        int count,
        List<(AppLaunchItem App, Image Icon, Border Fallback)>? tilesToLoad = null,
        string? focusedApp = null)
    {
        string lang = _orch.Config.Language;
        tilesToLoad ??= [];
        int end = Math.Min(_startRanked.Length, _startRenderedCount + count);
        for (int i = _startRenderedCount; i < end; i++)
        {
            var (app, isPinned) = _startRanked[i];
            _displayedStartApps.Add(app);
            var (tile, icon, fallback) = CreateStartAppTile(app, isPinned, lang);
            StartAppsGrid.Children.Add(tile);
            tilesToLoad.Add((app, icon, fallback));
            if (app.DisplayName == focusedApp) tile.Focus();
        }
        _startRenderedCount = end;

        if (!_isSmokeOrAuditMode && tilesToLoad.Count > 0)
        {
            _startIconLoad ??= new CancellationTokenSource();
            _ = LoadStartIconsAsync(tilesToLoad, _startIconLoad.Token);
        }
    }

    private void OnStartAppsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_startRenderedCount >= _startRanked.Length) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - StartTileRowHeight * 2)
        {
            AppendStartTiles(StartNextPageSize);
        }
    }

    private static readonly (Color Top, Color Bottom)[] StartFallbackHues =
    [
        (Color.FromRgb(0x5C, 0x78, 0xD6), Color.FromRgb(0x3E, 0x55, 0xA8)),
        (Color.FromRgb(0x86, 0x6C, 0xD4), Color.FromRgb(0x5E, 0x4A, 0xA6)),
        (Color.FromRgb(0xC9, 0x6A, 0x94), Color.FromRgb(0x9A, 0x4A, 0x6E)),
        (Color.FromRgb(0xD0, 0x86, 0x5A), Color.FromRgb(0xA2, 0x60, 0x3A)),
        (Color.FromRgb(0xC4, 0xA2, 0x52), Color.FromRgb(0x96, 0x78, 0x32)),
        (Color.FromRgb(0x56, 0xA8, 0x84), Color.FromRgb(0x38, 0x80, 0x62)),
        (Color.FromRgb(0x4E, 0x9C, 0xB4), Color.FromRgb(0x33, 0x74, 0x88)),
        (Color.FromRgb(0x84, 0x8A, 0x9A), Color.FromRgb(0x5C, 0x62, 0x72)),
    ];

    /// <summary>Deterministic (not per-process randomised) hue so fallbacks look the same every run.</summary>
    private static Brush StartFallbackBrush(string name)
    {
        int sum = 0;
        foreach (char c in name) sum = unchecked(sum * 31 + c);
        var (top, bottom) = StartFallbackHues[(int)((uint)sum % (uint)StartFallbackHues.Length)];
        var brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    private (Button Tile, Image Icon, Border Fallback) CreateStartAppTile(AppLaunchItem app, bool isPinned, string lang)
    {
        var tile = new Button
        {
            Style = (Style)FindResource("StartTileButtonStyle"),
            Height = StartTileHeight
        };
        AutomationProperties.SetName(tile, app.DisplayName);
        if (isPinned)
        {
            // Pinned state is conveyed by the pinned row (and to screen readers here); no glyph on the tile.
            AutomationProperties.SetHelpText(tile, Strings.Pick(lang, "Dock’a sabitlendi", "Pinned to Dock"));
        }

        var icon = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        string initial = string.IsNullOrEmpty(app.DisplayName)
            ? "?"
            : StringInfo.GetNextTextElement(app.DisplayName).ToUpper(CultureInfo.CurrentCulture);
        var fallback = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(11),
            Background = StartFallbackBrush(app.DisplayName),
            Child = new TextBlock
            {
                Text = initial,
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var iconHost = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 7) };
        iconHost.Children.Add(fallback);
        iconHost.Children.Add(icon);

        var label = new TextBlock
        {
            Text = app.DisplayName,
            FontFamily = (FontFamily)FindResource("UiTextFont"),
            FontSize = 12,
            Foreground = (Brush)FindResource("StartTextBodyBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = _startTileLabelWidth
        };

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(iconHost);
        content.Children.Add(label);
        tile.Content = content;

        // Full name in a glass tooltip only when the single-line label actually had to trim.
        var measured = new FormattedText(app.DisplayName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize,
            Brushes.White, 1.0);
        if (measured.WidthIncludingTrailingWhitespace > _startTileLabelWidth)
        {
            SetStartTip(tile, app.DisplayName);
        }

        tile.Click += (_, _) => DismissStartAndLaunchApp(app, tile);
        tile.GotKeyboardFocus += (_, _) =>
        {
            if (_startBestTile is not null && !ReferenceEquals(_startBestTile, tile))
            {
                _startBestTile.Tag = null;
                _startBestTile = null;
            }
        };
        tile.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Up && IsStartTileInTopRow(tile))
            {
                ev.Handled = true;
                StartSearchBox.Focus();
                return;
            }
            if (ev.Key != Key.Apps && !(ev.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)) return;
            ev.Handled = true;
            ShowStartAppContextMenu(tile, app, lang);
        };
        tile.MouseRightButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            ShowStartAppContextMenu(tile, app, lang);
        };
        return (tile, icon, fallback);
    }

    private bool IsStartTileInTopRow(Button tile)
    {
        var topGrid = StartPinnedSection.Visibility == Visibility.Visible && StartPinnedGrid.Children.Count > 0
            ? StartPinnedGrid
            : StartAppsGrid;
        int index = topGrid.Children.IndexOf(tile);
        return index >= 0 && index < Math.Max(1, topGrid.Columns);
    }

    private void CancelStartIconLoad()
    {
        _startIconLoad?.Cancel();
        _startIconLoad?.Dispose();
        _startIconLoad = null;
    }

    private async Task LoadStartIconsAsync(
        IReadOnlyList<(AppLaunchItem App, Image Icon, Border Fallback)> tiles,
        CancellationToken token)
    {
        try
        {
            await Task.Run(async () =>
            {
                foreach (var tile in tiles)
                {
                    token.ThrowIfCancellationRequested();
                    ImageSource? source = _appsService.GetOrLoadStartAppIcon(tile.App);
                    if (source is null) continue;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || !StartLaunchPopup.IsOpen) return;
                        tile.Icon.Source = source;
                        tile.Icon.Visibility = Visibility.Visible;
                        tile.Fallback.Visibility = Visibility.Collapsed;
                    }, DispatcherPriority.Background, token);
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            // Results changed or the popup closed while icon requests were pending.
        }
    }

    /// <summary>A quick soft press on the tile, then launch; the panel closes softly once the app is on its way.</summary>
    private async void DismissStartAndLaunchApp(AppLaunchItem app, FrameworkElement? pressed = null)
    {
        if (_startClosing) return;
        if (pressed is not null && StartMotionEnabled) W.Pulse(pressed, 0.95);
        if (_isSmokeOrAuditMode) return;

        long started = Stopwatch.GetTimestamp();
        var result = await _orch.Launcher.LaunchInstalledAppAsync(app);
        if (result.Succeeded)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (pressed is not null && elapsedMs < 130)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(130 - elapsedMs));
            }
            CloseStartPopupSoft();
        }
        else if (StartLaunchPopup.IsOpen)
        {
            string lang = _orch.Config.Language;
            StartLaunchError.Text = Strings.Pick(lang, $"{app.DisplayName} açılamadı.", $"Could not open {app.DisplayName}.");
            StartLaunchError.ToolTip = result.FeedbackTitle;
            StartLaunchError.Visibility = Visibility.Visible;
        }
    }

    private void ShowStartAppContextMenu(FrameworkElement anchor, AppLaunchItem app, string lang)
    {
        var menu = new ContextMenu();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
            {
                _activeContextMenu = null;
            }
            if (Mouse.Captured is not null)
            {
                Mouse.Capture(null);
            }
        };

        var openItem = new MenuItem { Header = Strings.TrayOpenApp(lang) };
        openItem.Click += (_, _) => DismissStartAndLaunchApp(app);
        menu.Items.Add(openItem);

        bool pinned = _appsService.IsStartAppPinned(app);
        if (pinned)
        {
            var dockItem = _currentAppItems.FirstOrDefault(item => item.IsPinned
                && string.Equals(item.DisplayName, app.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (dockItem is not null)
            {
                var unpinItem = new MenuItem { Header = Strings.DockUnpinApp(lang) };
                unpinItem.Click += (_, _) =>
                {
                    if (_appsService.TogglePin(dockItem)) RefreshStartAfterPinChange();
                };
                menu.Items.Add(unpinItem);
            }
        }
        else if (!app.IsStoreApp && !string.IsNullOrWhiteSpace(app.LaunchTarget) && System.IO.File.Exists(app.LaunchTarget))
        {
            var pinItem = new MenuItem { Header = Strings.DockPinApp(lang) };
            pinItem.Click += (_, _) =>
            {
                if (_appsService.TryPinStartApp(app)) RefreshStartAfterPinChange();
            };
            menu.Items.Add(pinItem);
        }

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void RefreshStartAfterPinChange()
    {
        RefreshDockItemsNow();
        if (StartLaunchPopup.IsOpen)
        {
            RebuildStartAppsGrid(StartSearchBox.Text ?? string.Empty);
        }
    }

    // ---- Search and keyboard -----------------------------------------------------------------------

    private void OnStartSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string text = StartSearchBox.Text ?? string.Empty;
        bool hasText = !string.IsNullOrEmpty(text);
        StartLaunchError.Visibility = Visibility.Collapsed;
        StartSearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        BtnStartSearchClear.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        RebuildStartAppsGrid(text);
        StartAppsScrollViewer.ScrollToTop();
    }

    private void OnStartSearchFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        double target = StartSearchBox.IsKeyboardFocusWithin ? 1 : 0;
        if (StartMotionEnabled)
            StartSearchGlow.BeginAnimation(OpacityProperty, Motion.To(target, target > 0 ? 160 : 220));
        else
        {
            StartSearchGlow.BeginAnimation(OpacityProperty, null);
            StartSearchGlow.Opacity = target;
        }
    }

    private void OnStartCardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseStartPopupSoft();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            StartSearchBox.Focus();
            StartSearchBox.SelectAll();
        }
    }

    private void OnStartSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            var first = StartPinnedSection.Visibility == Visibility.Visible && StartPinnedGrid.Children.Count > 0
                ? StartPinnedGrid.Children[0]
                : StartAppsGrid.Children.Count > 0 ? StartAppsGrid.Children[0] : null;
            if (first is null) return;
            e.Handled = true;
            first.Focus();
        }
        else if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(StartSearchBox.Text) && _displayedStartApps.Count > 0)
        {
            e.Handled = true;
            DismissStartAndLaunchApp(_displayedStartApps[0], StartAppsGrid.Children.Count > 0 ? StartAppsGrid.Children[0] as FrameworkElement : null);
        }
    }

    private void OnStartSearchClearClick(object sender, RoutedEventArgs e)
    {
        StartSearchBox.Text = string.Empty;
        StartSearchBox.Focus();
    }

    // ---- Header, quick actions and footer ----------------------------------------------------------

    /// <summary>Soft press on the control, then the panel closes softly and the action runs.</summary>
    private void PressAndCloseStart(object sender)
    {
        if (sender is FrameworkElement el && StartMotionEnabled) W.Pulse(el, 0.95);
        CloseStartPopupSoft();
    }

    private void OnStartAiStatusClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (!_isSmokeOrAuditMode) _orch.Coordinator.RequestExpand(ExpandedTabId.AiQuota);
    }

    private void OnStartClassicWindowsClick(object sender, RoutedEventArgs e)
    {
        // Closes at once so the system Start menu is not covered by our fading panel.
        StartLaunchPopup.IsOpen = false;
        if (_isSmokeOrAuditMode) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            WindowsTaskbarManager.TriggerStartMenu();
        });
    }

    private void OnStartSettingsClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (!_isSmokeOrAuditMode) _orch.RequestOpenSettings();
    }

    private void OnStartModuleIslandClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (!_isSmokeOrAuditMode) _orch.Coordinator.RequestExpand(ExpandedTabId.Now);
    }

    private void OnStartModuleAiClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (!_isSmokeOrAuditMode) _orch.Coordinator.RequestExpand(ExpandedTabId.AiQuota);
    }

    private void OnStartModuleSystemClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (!_isSmokeOrAuditMode) _orch.Coordinator.RequestExpand(ExpandedTabId.System);
    }

    private void OnStartModuleCommandClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (!_isSmokeOrAuditMode) _orch.Coordinator.OpenCommandPalette();
    }

    private void OnStartExplorerClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (_isSmokeOrAuditMode) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
    }

    private void OnStartTerminalClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (_isSmokeOrAuditMode) return;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true }); } catch { }
        }
    }

    private void OnStartTaskMgrClick(object sender, RoutedEventArgs e)
    {
        PressAndCloseStart(sender);
        if (_isSmokeOrAuditMode) return;
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
    }

    private void OnStartLockClick(object sender, RoutedEventArgs e)
    {
        // Locking hides the desktop at once; close immediately so the panel is not caught mid-fade.
        StartLaunchPopup.IsOpen = false;
        if (!_isSmokeOrAuditMode) _orch.Connectivity.LockWorkstation();
    }

    /// <summary>Power options open as a menu, which doubles as the confirmation step before sleep, restart or shut down.</summary>
    private void OnStartPowerClick(object sender, RoutedEventArgs e)
    {
        string lang = _orch.Config.Language;
        var menu = new ContextMenu();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
            {
                _activeContextMenu = null;
            }
        };

        var sleepItem = new MenuItem { Header = Strings.StartMenuSleep(lang) };
        sleepItem.Click += (_, _) =>
        {
            StartLaunchPopup.IsOpen = false;
            if (!_isSmokeOrAuditMode) _orch.Connectivity.SleepWorkstation();
        };

        var restartItem = new MenuItem { Header = Strings.StartMenuRestart(lang) };
        restartItem.Click += (_, _) =>
        {
            StartLaunchPopup.IsOpen = false;
            if (!_isSmokeOrAuditMode)
            {
                try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
            }
        };

        var shutdownItem = new MenuItem { Header = Strings.StartMenuShutdown(lang) };
        shutdownItem.Click += (_, _) =>
        {
            StartLaunchPopup.IsOpen = false;
            if (!_isSmokeOrAuditMode)
            {
                try { Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
            }
        };

        menu.Items.Add(sleepItem);
        menu.Items.Add(restartItem);
        menu.Items.Add(shutdownItem);
        menu.PlacementTarget = BtnStartPower;
        menu.Placement = IsDockAtTop ? PlacementMode.Bottom : PlacementMode.Top;
        menu.IsOpen = true;
    }
}
