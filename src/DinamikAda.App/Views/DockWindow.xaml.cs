using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DinamikAda.App.Services;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Arbitration;
using DinamikAda.Core.Localization;
using DinamikAda.Core.MathAndSearch;
using DinamikAda.Core.Widgets;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Modules.Productivity;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;

namespace DinamikAda.App.Views;

public partial class DockWindow : Window
{
    private const double HostHeightDip = DockLayoutCalculator.DefaultHostHeightDip;
    private const double MagnifyMaxScale = 1.34;
    private const double MagnifyRadiusDip = 104.0;
    private const double MotionSmoothSpeed = 24.0;
    private const double BounceDurationSec = 0.55;

    private readonly AppOrchestrator _orch;
    private readonly DockAppsService _appsService;
    private readonly WindowsTaskbarManager _taskbarManager;
    private readonly ExplorerTaskbarService _explorerTaskbar = new();

    private readonly DispatcherTimer _reconcileTimer;
    private readonly DispatcherTimer _refreshDebounceTimer;
    private readonly DispatcherTimer _popupClockTimer;
    private readonly DispatcherTimer _intellihideDebounceTimer;
    private readonly DispatcherTimer _intellihideEdgeTimer;

    private nint _hwnd;
    private HwndSource? _hwndSource;
    private nint _winEventSysHook;
    private nint _winEventObjHook;
    private nint _winEventLocHook;
    private nint _winEventCloakHook;
    private NativeMethods.WinEventDelegate? _winEventDelegate;

    private MonitorDescriptor _activeMonitor;
    private nint _lastForegroundHwnd;
    private IReadOnlyList<DockAppItem> _currentAppItems = [];
    private RecycleBinSummary _lastRecycleBin;
    private string _lastRenderedStructureKey = string.Empty;
    private string _lastClockMinute = string.Empty;
    private string _lastTooltipTitle = string.Empty;
    private int _hoveredSlotIndex = -1;
    private int _tickCounter;
    private bool _isClosed;
    private bool _isSmokeOrAuditMode;
    private bool _isFullscreenHidden;
    private bool _hasOverlappingWindow;
    private bool _isIntellihidePeeking;
    private bool _isIntellihideTucked;
    private bool _isClickThroughApplied;
    private double _currentHideProgress;
    private double _targetHideProgress;
    private long _edgeEnterTickMs;
    private long _hideGraceStartTickMs;
    private NativeMethods.RECT _cachedDockHitRectPx;

    // 240Hz macOS hover magnification & bounce motion state (strictly middle app icons; Start/Trash stay static)
    private readonly List<DockSlotVisual> _slots = new(32);
    private double[] _slotCentersX = [];
    private double[] _appSlotCentersX = [];
    private int[] _appSlotIndices = [];
    private double _appsMinX;
    private double _appsMaxX;
    private double? _hoverLocalX;
    private bool _isHovered;
    private bool _isRenderingLoopActive;
    private long _lastRenderTicks;
    private long _lastAmbientFrameTicks;
    private int _lastTooltipSlotIndex = -1;
    private bool _slotsAtRest = true;
    private TextBlock? _clockTimeText;
    private TextBlock? _clockDateText;
    private Border? _clockChip;
    private Border? _islandSyncBadge;
    private TextBlock? _islandSyncBadgeText;
    private Border? _islandAiBadge;
    private TextBlock? _islandAiBadgeText;
    private DockSlotVisual? _islandSyncSlot;
    private FrameworkElement? _islandSyncDot;
    private TextBlock? _trashIconText;
    private Ellipse? _trashDot;
    private Border? _trayChevronBtn;
    private Ellipse? _trayActiveDot;

    // Pinned-slot drag-to-reorder and Explorer drop-to-pin state.
    private const double ReorderDragThresholdDip = 6.0;
    private DockSlotVisual? _pressedPinnedSlot;
    private Point _pressStartPoint;
    private bool _isReorderDragging;
    private int _reorderTargetIndex = -1;
    private bool _isExternalDropPreview;
    private int _lastTrayAppCount;
    private IReadOnlyList<BackgroundTrayAppItem> _cachedTrayItems = [];
    private IReadOnlyList<BackgroundTrayAppItem> _renderedTrayItems = [];
    private Task? _trayRefreshTask;
    private bool _trayActionBusy;
    private ContextMenu? _activeContextMenu;
    private DateTime _popupDisplayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _popupRenderedToday = DateTime.Today;
    private long _lastPopupClosedTicks;
    private Border? _startMenuBtn;
    private long _lastStartPopupClosedTicks;
    private bool _suppressNextStartOpenOnUp;
    private IReadOnlyList<AppLaunchItem> _cachedStartApps = [];
    private bool _startAppsLoading;
    private CancellationTokenSource? _startIconLoad;

    private sealed class DockSlotVisual
    {
        public required Grid Container { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TranslateTransform Translate { get; init; }
        public required Border IconPlate { get; init; }
        public required Ellipse? RunningDot { get; init; }
        public required Border? RunningPill { get; init; }
        public required string TooltipLabel { get; set; }
        public DockAppItem? AppItem { get; set; }
        public SlotKind Kind { get; init; }

        public double CurrentScale { get; set; } = 1.0;
        public double TargetScale { get; set; } = 1.0;
        public double CurrentShiftX { get; set; }
        public double TargetShiftX { get; set; }
        public double CurrentLiftY { get; set; }
        public double TargetLiftY { get; set; }
        public long BounceStartTicks { get; set; }
        public double ReorderShiftX { get; set; }
        public double CurrentReorderShiftX { get; set; }
        public double CurrentBounceY { get; set; }
        public string? WidgetId { get; init; }
    }

    private enum SlotKind
    {
        StartMenu = 0,
        App = 1,
        IslandSync = 2,
        Trash = 3,
        TrayOverflow = 4,
        Widget = 5
    }

    public DockWindow(AppOrchestrator orchestrator, WindowsTaskbarManager taskbarManager)
    {
        _orch = orchestrator;
        _taskbarManager = taskbarManager;
        _appsService = new DockAppsService(_orch.Config, _orch.SaveConfig);
        _activeMonitor = ResolveDockMonitor();

        InitializeComponent();

        Height = HostHeightDip;
        _reconcileTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _reconcileTimer.Tick += OnReconcileTick;

        _refreshDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(110)
        };
        _refreshDebounceTimer.Tick += (_, _) =>
        {
            _refreshDebounceTimer.Stop();
            RefreshDockItemsNow();
        };

        _popupClockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _popupClockTimer.Tick += (_, _) => UpdatePopupLiveClockAndFooter();

        _intellihideDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _intellihideDebounceTimer.Tick += (_, _) =>
        {
            _intellihideDebounceTimer.Stop();
            EvaluateDockOverlapAndState();
        };

        _intellihideEdgeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _intellihideEdgeTimer.Tick += (_, _) => UpdateEdgePeekAndIntellihideTick();

        SourceInitialized += OnDockSourceInitialized;
        Loaded += OnDockLoaded;
        Closing += OnDockClosing;
    }

    public void SetSmokeOrAuditMode(bool enabled) => _isSmokeOrAuditMode = enabled;

    public bool IsTaskbarHiddenByManager => _taskbarManager.IsTaskbarHidden;

    public void ApplyConfigAndSync()
    {
        if (_isClosed) return;

        // Settings may have rewritten the pinned app list; pick it up without a restart.
        if (_appsService.ReloadIfConfigChanged())
        {
            RefreshDockItemsNow();
        }

        bool enableDock = _orch.Config.UseCustomBottomDock;
        if (!_isSmokeOrAuditMode)
            ApplyExplorerTaskbar();
        bool followFs = _orch.Config.DockFollowFullscreenHide;
        var fs = _orch.FullscreenDetector.Current;
        _isFullscreenHidden = followFs && fs.IsFullscreen && fs.Kind is FullscreenAppKind.Game or FullscreenAppKind.Video;

        _activeMonitor = ResolveDockMonitor();
        ApplyDockEdgeLayout();
        ApplyDockStyleVisuals();
        UpdateHostPlacement();

        bool shouldBeVisible = enableDock && !_isFullscreenHidden;
        bool hideTaskbar = !_isSmokeOrAuditMode && enableDock && _orch.Config.HideWindowsTaskbar;
        int reservedPx = DockLayoutCalculator.ComputeReservedBottomHeightPx(_activeMonitor.DpiScale, _orch.Config.DockIconSizeDip);

        if (!_isSmokeOrAuditMode)
        {
            _taskbarManager.ApplyState(
                hideTaskbar,
                _activeMonitor,
                reservedPx,
                reserveFullWidthWorkArea: _orch.Config.DockReserveWorkArea,
                reserveAtTop: IsDockAtTop);
        }

        if (shouldBeVisible)
        {
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }
            if (_hwnd != 0 && !NativeMethods.IsWindowVisible(_hwnd))
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            }
            _lastRenderedStructureKey = string.Empty; // Force icon size/layout rebuild cleanly
            RefreshDockItemsNow();
            UpdateWidgetHubDemand();
            SyncCoordinatorState(_orch.Coordinator.Current);
            EvaluateDockOverlapAndState();
        }
        else
        {
            StopRenderLoop();
            _intellihideDebounceTimer.Stop();
            _intellihideEdgeTimer.Stop();
            SetClickThrough(false);
            CloseWidgetCard();
            _widgetHub?.SetActiveWidgets([], dockVisible: false);
            if (_hwnd != 0 && NativeMethods.IsWindowVisible(_hwnd))
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            }
            if (!enableDock)
            {
                Visibility = Visibility.Collapsed;
            }
        }
    }

    public void RestoreTaskbarImmediately()
    {
        _taskbarManager.Restore();
    }

    public void SyncCoordinatorState(IslandCoordinatorSnapshot snap)
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SyncCoordinatorState(snap));
            return;
        }

        if (_orch.Config.UseExplorerTaskbar && !_isSmokeOrAuditMode)
        {
            ApplyExplorerTaskbar();
            return;
        }

        Color primary = ParseAccentColor(snap.PrimaryActivity?.AccentHex, Color.FromRgb(10, 132, 255));
        Color secondary = ParseAccentColor(_orch.Media.Current.SecondaryAccentHex, Color.FromRgb(94, 92, 230));
        Color highlight = ParseAccentColor(_orch.Media.Current.HighlightAccentHex, Color.FromRgb(100, 210, 255));

        UpdateDockAmbientPaletteAndState(primary, secondary, highlight);
        PopupAuraStop0.Color = Color.FromArgb(0x1C, primary.R, primary.G, primary.B);
        PopupAuraStop1.Color = Color.FromArgb(0x10, secondary.R, secondary.G, secondary.B);
        TrayPopupAuraStop0.Color = Color.FromArgb(0x1C, primary.R, primary.G, primary.B);
        TrayPopupAuraStop1.Color = Color.FromArgb(0x10, secondary.R, secondary.G, secondary.B);
        StartPopupAuraStop0.Color = Color.FromArgb(0x1E, primary.R, primary.G, primary.B);
        StartPopupAuraStop1.Color = Color.FromArgb(0x12, secondary.R, secondary.G, secondary.B);

        // Update fullscreen auto-hide smoothly
        bool wantFsHide = _orch.Config.DockFollowFullscreenHide
            && snap.Fullscreen.IsFullscreen
            && snap.Fullscreen.Kind is FullscreenAppKind.Game or FullscreenAppKind.Video;
        if (wantFsHide != _isFullscreenHidden)
        {
            _isFullscreenHidden = wantFsHide;
            if (_orch.Config.UseCustomBottomDock && !_isSmokeOrAuditMode)
            {
                if (_hwnd != 0)
                {
                    NativeMethods.ShowWindow(_hwnd, wantFsHide ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOWNOACTIVATE);
                }
                if (wantFsHide)
                {
                    _intellihideDebounceTimer.Stop();
                    _intellihideEdgeTimer.Stop();
                    SetClickThrough(false);
                    StopRenderLoop();
                }
                else
                {
                    EvaluateDockOverlapAndState();
                }
            }
        }

        if (_islandSyncDot is Ellipse dot)
        {
            dot.Fill = new SolidColorBrush(snap.PrimaryActivity is not null ? primary : Color.FromRgb(100, 210, 255));
        }

        UpdateDockAiBadgeAndTooltip();

        int alertCount = _orch.Notifications.Current.Count + _orch.Shelf.GetItems().Count;
        if (_islandSyncBadge is not null && _islandSyncBadgeText is not null)
        {
            if (alertCount > 0)
            {
                _islandSyncBadgeText.Text = alertCount > 99 ? "99+" : alertCount.ToString();
                _islandSyncBadge.Visibility = Visibility.Visible;
            }
            else
            {
                _islandSyncBadge.Visibility = Visibility.Collapsed;
            }
        }

        foreach (var slot in _slots)
        {
            if (slot.AppItem?.IsActiveForeground == true && slot.RunningPill is not null)
            {
                slot.RunningPill.Background = new SolidColorBrush(primary);
            }
        }
    }

    private void UpdateDockAiBadgeAndTooltip()
    {
        var summary = _orch.AiQuota.GetSummary();
        bool showAi = _orch.Config.ShowAiQuotaInIslandAndDock && summary.TotalCount > 0;

        if (_islandAiBadge is not null && _islandAiBadgeText is not null)
        {
            if (showAi)
            {
                Color accent = ParseHexColor(summary.DockBadgeHex, Color.FromRgb(48, 209, 88));
                _islandAiBadgeText.Text = summary.DockBadgeText;
                _islandAiBadgeText.Foreground = new SolidColorBrush(accent);
                _islandAiBadge.Background = new SolidColorBrush(Color.FromArgb(0x28, accent.R, accent.G, accent.B));
                _islandAiBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
                _islandAiBadge.Visibility = Visibility.Visible;
            }
            else
            {
                _islandAiBadge.Visibility = Visibility.Collapsed;
            }
        }

        if (_islandSyncSlot is not null)
        {
            string baseLabel = Strings.DockIslandSyncLabel(_orch.Config.Language);
            if (showAi)
            {
                var focus = summary.AttentionSlot ?? _orch.AiQuota.GetSnapshots().FirstOrDefault();
                string detail = focus is not null && focus.TotalQuota > 0
                    ? $" · {focus.Label}: %{(int)Math.Round(focus.RemainingPercent)}"
                    : string.Empty;
                _islandSyncSlot.TooltipLabel = $"{baseLabel} · AI {summary.DockBadgeText}{detail}";
            }
            else
            {
                _islandSyncSlot.TooltipLabel = baseLabel;
            }
        }

        UpdateStartAiStatusPill();
    }

    private void UpdateStartAiStatusPill()
    {
        if (StartAiStatusText is null || StartAiStatusDot is null) return;
        string lang = _orch.Config.Language;
        var summary = _orch.AiQuota.GetSummary();
        if (summary.TotalCount == 0)
        {
            StartAiStatusDot.Fill = (Brush)FindResource("AccentCyanBrush");
            StartAiStatusText.Text = Strings.StartMenuModuleAi(lang);
            return;
        }

        Color accent = ParseHexColor(summary.DockBadgeHex, Color.FromRgb(48, 209, 88));
        StartAiStatusDot.Fill = new SolidColorBrush(accent);
        StartAiStatusText.Text = $"AI {summary.DockBadgeText}";
    }

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return fallback;
        }
    }

    public RenderTargetBitmap RenderForAudit(int width = 960, int height = 96)
    {
        SetSmokeOrAuditMode(true);
        var auditApps = new[]
        {
            new DockAppItem("audit:explorer", "Dosya Gezgini", "explorer.exe", "explorer.exe", true, false, null, []),
            new DockAppItem("audit:chrome", "Google Chrome", "chrome.exe", "chrome.exe", true, true, null,
                [new DockWindowRef(1, 100, "DinamikAda", "chrome.exe", false)]),
            new DockAppItem("audit:vscode", "Visual Studio Code", "Code.exe", "Code.exe", true, false, null,
                [new DockWindowRef(2, 101, "DinamikAda.sln", "Code.exe", false)]),
            new DockAppItem("audit:terminal", "Terminal", "wt.exe", "wt.exe", false, false, null,
                [new DockWindowRef(3, 102, "PowerShell", "wt.exe", false)])
        };
        _currentAppItems = auditApps;
        _lastRecycleBin = new RecycleBinSummary(3, 4096);
        _isIntellihideTucked = false;
        _isIntellihidePeeking = false;
        _currentHideProgress = 0.0;
        _targetHideProgress = 0.0;
        DockCapsuleOffsetTransform.Y = 0.0;
        DockPillContainer.Opacity = 1.0;
        RebuildVisualSlots(force: true);
        SyncCoordinatorState(_orch.Coordinator.Current);

        DockRootGrid.Measure(new Size(width, height));
        DockRootGrid.Arrange(new Rect(0, 0, width, height));
        DockRootGrid.UpdateLayout();

        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(DockRootGrid);
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
    }

    public RenderTargetBitmap RenderCalendarPopupForAudit(int width = 300, int height = 340)
    {
        SetSmokeOrAuditMode(true);
        _popupDisplayMonth = new DateTime(2026, 11, 1);
        _popupRenderedToday = new DateTime(2026, 11, 16);
        UpdatePopupLiveClockAndFooter();
        RebuildPopupCalendarGrid();
        SyncCoordinatorState(_orch.Coordinator.Current);

        ClockCalendarCard.Measure(new Size(264, double.PositiveInfinity));
        double cardH = Math.Ceiling(ClockCalendarCard.DesiredSize.Height);
        ClockCalendarCard.Arrange(new Rect(0, 0, 264, cardH));
        ClockCalendarCard.UpdateLayout();

        int finalW = Math.Max(width, 288);
        int finalH = Math.Max(height, (int)cardH + 24);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 16)), null, new Rect(0, 0, finalW, finalH));
            var vb = new VisualBrush(ClockCalendarCard) { Stretch = Stretch.None };
            double x = Math.Round((finalW - 264) * 0.5);
            double y = Math.Round((finalH - cardH) * 0.5);
            dc.DrawRectangle(vb, null, new Rect(x, y, 264, cardH));
        }

        var bmp = new RenderTargetBitmap(finalW, finalH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
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
            TrayOverflowCard.Width = Math.Clamp(width - 24, 300, 388);
            RebuildTrayOverflowGrid(empty ? [] : demoItems);
            SyncCoordinatorState(_orch.Coordinator.Current);

            double cardW = TrayOverflowCard.Width;
            TrayOverflowCard.InvalidateMeasure();
            TrayOverflowCard.Measure(new Size(cardW, double.PositiveInfinity));
            double cardH = Math.Ceiling(TrayOverflowCard.DesiredSize.Height);
            TrayOverflowCard.Arrange(new Rect(0, 0, cardW, cardH));
            TrayOverflowCard.UpdateLayout();
            var cardBitmap = new RenderTargetBitmap((int)Math.Ceiling(cardW), (int)cardH, 96, 96, PixelFormats.Pbgra32);
            cardBitmap.Render(TrayOverflowCard);
            cardBitmap.Freeze();

            int finalW = Math.Max(width, (int)cardW + 24);
            int finalH = Math.Max(height, (int)cardH + 24);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 16)), null, new Rect(0, 0, finalW, finalH));
                double x = Math.Round((finalW - cardW) * 0.5);
                double y = Math.Round((finalH - cardH) * 0.5);
                dc.DrawImage(cardBitmap, new Rect(x, y, cardW, cardH));
            }

            var bmp = new RenderTargetBitmap(finalW, finalH, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        finally
        {
            TrayOverflowPopup.Child = TrayOverflowCard;
        }
    }

    public RenderTargetBitmap RenderStartPopupForAudit(int width = 680, int height = 650, string query = "")
    {
        SetSmokeOrAuditMode(true);
        _currentAppItems =
        [
            new DockAppItem("audit:explorer", "Dosya Gezgini", "explorer.exe", "explorer.exe", true, false, null, []),
            new DockAppItem("audit:chrome", "Google Chrome", "chrome.exe", "chrome.exe", true, true, null,
                [new DockWindowRef(1, 100, "DinamikAda", "chrome.exe", false)]),
            new DockAppItem("audit:vscode", "Visual Studio Code", "Code.exe", "Code.exe", true, false, null,
                [new DockWindowRef(2, 101, "DinamikAda.sln", "Code.exe", false)]),
            new DockAppItem("audit:terminal", "Terminal", "wt.exe", "wt.exe", false, false, null,
                [new DockWindowRef(3, 102, "PowerShell", "wt.exe", false)])
        ];
        RebuildVisualSlots(force: true);

        var demoApps = new AppLaunchItem[]
        {
            new("Antigravity", "Antigravity.exe", false),
            new("Google Chrome", "chrome.exe", false),
            new("Visual Studio", "devenv.exe", false),
            new("Adobe Photoshop", "Photoshop.exe", false),
            new("Discord", "Discord.exe", false),
            new("Spotify", "Spotify.exe", false),
            new("ChatGPT", "ChatGPT.exe", true, "ChatGPT"),
            new("Outlook", "Outlook.exe", true, "Outlook"),
            new("Microsoft Store", "Store.exe", true, "Store"),
            new("Not Defteri", "notepad.exe", false),
            new("Hesap Makinesi", "calc.exe", false),
            new("Ekran Alıntısı", "SnippingTool.exe", false),
            new("FileZilla", "filezilla.exe", false),
            new("7-Zip", "7zFM.exe", false),
            new("Ayarlar", "ms-settings:", true, "Settings")
        };

        ApplyStartPopupLocalization();
        UpdateStartAiStatusPill();
        _cachedStartApps = demoApps;
        SizeStartMenu(width, height);
        StartSearchBox.Text = query;
        StartSearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        RebuildStartAppsGrid(query);
        SyncCoordinatorState(_orch.Coordinator.Current);

        double cardW = StartLaunchCard.Width;
        StartLaunchCard.Measure(new Size(cardW, double.PositiveInfinity));
        double cardH = Math.Ceiling(StartLaunchCard.DesiredSize.Height);
        StartLaunchCard.Arrange(new Rect(0, 0, cardW, cardH));
        StartLaunchCard.UpdateLayout();

        DockRootGrid.Measure(new Size(width, 96));
        DockRootGrid.Arrange(new Rect(0, 0, width, 96));
        DockRootGrid.UpdateLayout();
        double dockPillW = Math.Max(320, Math.Ceiling(DockPillContainer.ActualWidth));
        double dockPillH = Math.Max(52, Math.Ceiling(DockPillContainer.ActualHeight));

        int finalW = Math.Max(width, (int)Math.Max(cardW, dockPillW) + 24);
        int finalH = Math.Max(height, (int)cardH + 24 + (int)dockPillH + 28);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 16)), null, new Rect(0, 0, finalW, finalH));
            double dockX = Math.Round((finalW - dockPillW) * 0.5);
            double dockY = finalH - dockPillH - 8.0;
            var dockBrush = new VisualBrush(DockPillContainer) { Stretch = Stretch.None };
            dc.DrawRectangle(dockBrush, null, new Rect(dockX, dockY, dockPillW, dockPillH));

            // Render Start Menu floating cleanly 24 DIP above the Dock capsule
            double startX = Math.Round((finalW - cardW) * 0.5);
            double startY = Math.Max(12.0, dockY - 24.0 - cardH);
            var cardBrush = new VisualBrush(StartLaunchCard) { Stretch = Stretch.None };
            dc.DrawRectangle(cardBrush, null, new Rect(startX, startY, cardW, cardH));
        }

        var bmp = new RenderTargetBitmap(finalW, finalH, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        if (bmp.CanFreeze) bmp.Freeze();
        return bmp;
    }

    private void OnDockSourceInitialized(object? sender, EventArgs e)
    {
        if (_isSmokeOrAuditMode) return;

        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(OnDockWndProc);

        ApplyToolWindowNoActivateStyles();
        UpdateHostPlacement();
        InstallWindowEventHooks();
    }

    private void OnDockLoaded(object sender, RoutedEventArgs e)
    {
        if (_isSmokeOrAuditMode) return;

        _orch.Coordinator.SnapshotChanged += OnCoordinatorSnapshotChanged;
        _orch.AiQuota.SlotsChanged += OnDockAiQuotaChanged;
        _orch.OpenWidgetPickerRequested += OnOpenWidgetPickerRequested;
        ApplyConfigAndSync();
        _reconcileTimer.Start();
    }

    private void OnDockClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosed) return;
        _isClosed = true;
        StopRenderLoop();
        _reconcileTimer.Stop();
        _refreshDebounceTimer.Stop();
        _popupClockTimer.Stop();
        _intellihideDebounceTimer.Stop();
        _intellihideEdgeTimer.Stop();
        ClockCalendarPopup.IsOpen = false;
        TrayOverflowPopup.IsOpen = false;
        StartLaunchPopup.IsOpen = false;
        WidgetCardPopup.IsOpen = false;
        _widgetPicker?.Close();
        DetachWidgetTiles();
        _widgetHub?.Dispose();
        _orch.Coordinator.SnapshotChanged -= OnCoordinatorSnapshotChanged;
        _orch.AiQuota.SlotsChanged -= OnDockAiQuotaChanged;
        _orch.OpenWidgetPickerRequested -= OnOpenWidgetPickerRequested;
        UninstallWindowEventHooks();
        _hwndSource?.RemoveHook(OnDockWndProc);
        _hwndSource = null;

        if (!_isSmokeOrAuditMode)
        {
            _explorerTaskbar.Dispose();
            _taskbarManager.Restore();
        }
    }

    private void OnCoordinatorSnapshotChanged(object? sender, IslandCoordinatorSnapshot snap)
    {
        SyncCoordinatorState(snap);
    }

    private void OnDockAiQuotaChanged(object? sender, IReadOnlyList<AiQuotaSlotSnapshot> _)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isClosed) UpdateDockAiBadgeAndTooltip();
        });
    }

    private void OnReconcileTick(object? sender, EventArgs e)
    {
        if (_isClosed || _isSmokeOrAuditMode) return;
        if (_orch.Config.UseExplorerTaskbar)
        {
            ApplyExplorerTaskbar();
            return;
        }

        if (_orch.Config.UseCustomBottomDock && _orch.Config.HideWindowsTaskbar)
        {
            _taskbarManager.EnsureHiddenTick();
        }

        // With the dock disabled nothing shows the clock, trash or tray dots; skip the per-process
        // tray scan (Process.GetProcesses + OpenProcess + EnumWindows) and recycle-bin query entirely.
        if (!_orch.Config.EnableBottomDock) return;

        UpdateClockText();

        if (++_tickCounter % 4 == 0)
        {
            var prevRb = _lastRecycleBin;
            _lastRecycleBin = DockAppsService.QueryRecycleBin();
            if (prevRb.HasItems != _lastRecycleBin.HasItems && _trashDot is not null)
            {
                _trashDot.Opacity = _lastRecycleBin.HasItems ? 1.0 : 0.0;
            }

            if (TrayOverflowPopup.IsOpen) _ = RefreshTrayAppsAsync();
        }

        if (_orch.Config.UseCustomBottomDock && !_isFullscreenHidden)
        {
            RefreshDockItemsNow();
            EvaluateDockOverlapAndState();
        }
    }

    private void ApplyExplorerTaskbar()
    {
        var media = _orch.Media.Current;
        _explorerTaskbar.Apply(_orch.Config.UseExplorerTaskbar, media.AccentHex,
            media.IsPlaying, media.SecondaryAccentHex, media.HighlightAccentHex,
            _orch.Config.DockMagnificationEnabled, _orch.Config.DynamicAlbumGlowEnabled, _orch.Config.DockIntellihideEnabled, media.HasSession);
    }

    private void ApplyToolWindowNoActivateStyles()
    {
        if (_hwnd == 0) return;
        nint exStyle = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE);
        long next = exStyle.ToInt64()
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TOPMOST
            | NativeMethods.WS_EX_LAYERED;
        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(next));
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private MonitorDescriptor ResolveDockMonitor()
    {
        var all = _orch.MonitorLayout.GetMonitors();
        if (all.Count == 0)
        {
            return new MonitorDescriptor(0, "\\\\.\\DISPLAY1", "Primary", true, 0, 0, 1920, 1080, 1.0, 60);
        }

        if (_orch.Config.MonitorMode == MonitorPlacementMode.PinnedMonitor &&
            !string.IsNullOrWhiteSpace(_orch.Config.PinnedMonitorDeviceName))
        {
            var match = all.FirstOrDefault(m => string.Equals(m.DeviceName, _orch.Config.PinnedMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
    }

    private void UpdateHostPlacement()
    {
        var bounds = DockLayoutCalculator.ComputeHostWindowBounds(_activeMonitor, HostHeightDip, IsDockAtTop, DockTopInsetPx());
        double scale = Math.Max(1.0, bounds.DpiScale);
        Width = bounds.WidthPx / scale;
        Height = bounds.HeightPx / scale;

        if (_hwnd != 0)
        {
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOPMOST,
                bounds.LeftPx,
                bounds.TopPx,
                bounds.WidthPx,
                bounds.HeightPx,
                NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void InstallWindowEventHooks()
    {
        UninstallWindowEventHooks();
        _winEventDelegate = OnDockTrackedWinEvent;
        const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

        _winEventSysHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            0, _winEventDelegate, 0, 0, flags);

        _winEventObjHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CREATE,
            NativeMethods.EVENT_OBJECT_HIDE,
            0, _winEventDelegate, 0, 0, flags);

        _winEventLocHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            0, _winEventDelegate, 0, 0, flags);

        _winEventCloakHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CLOAKED,
            NativeMethods.EVENT_OBJECT_UNCLOAKED,
            0, _winEventDelegate, 0, 0, flags);
    }

    private void UninstallWindowEventHooks()
    {
        UninstallPopupMouseHook();
        if (_winEventSysHook != 0) { NativeMethods.UnhookWinEvent(_winEventSysHook); _winEventSysHook = 0; }
        if (_winEventObjHook != 0) { NativeMethods.UnhookWinEvent(_winEventObjHook); _winEventObjHook = 0; }
        if (_winEventLocHook != 0) { NativeMethods.UnhookWinEvent(_winEventLocHook); _winEventLocHook = 0; }
        if (_winEventCloakHook != 0) { NativeMethods.UnhookWinEvent(_winEventCloakHook); _winEventCloakHook = 0; }
        _winEventDelegate = null;
    }

    private void OnDockTrackedWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_isClosed || _isSmokeOrAuditMode || hwnd == 0 || idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF)
        {
            return;
        }

        if (hwnd == _hwnd)
        {
            return;
        }

        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE && !NativeMethods.IsWindowVisible(hwnd))
        {
            return;
        }

        if (eventType != NativeMethods.EVENT_OBJECT_LOCATIONCHANGE && !_refreshDebounceTimer.IsEnabled)
        {
            _refreshDebounceTimer.Start();
        }

        if (_orch.Config.UseCustomBottomDock && _orch.Config.DockIntellihideEnabled && !_isFullscreenHidden)
        {
            ScheduleIntellihideEval();
        }
    }

    private void ScheduleIntellihideEval()
    {
        if (_isClosed || _isSmokeOrAuditMode) return;
        if (!_intellihideDebounceTimer.IsEnabled)
        {
            _intellihideDebounceTimer.Start();
        }
    }

    private NativeMethods.RECT ComputeCurrentDockHitRectPx()
    {
        double capW = DockPillContainer.ActualWidth > 10.0
            ? DockPillContainer.ActualWidth
            : Math.Max(300.0, DockPillContainer.DesiredSize.Width);
        double capH = DockPillContainer.ActualHeight > 10.0
            ? DockPillContainer.ActualHeight
            : Math.Max(_orch.Config.DockIconSizeDip + 19.0, DockPillContainer.DesiredSize.Height);
        return DockLayoutCalculator.ComputeDockHitRectPx(_activeMonitor, capW, capH, bottomMarginDip: 6.0, topBufferPx: 4,
            atTop: IsDockAtTop, topInsetPx: DockTopInsetPx());
    }

    private bool IsAnyDockOverlayOpen()
    {
        return ClockCalendarPopup.IsOpen
            || TrayOverflowPopup.IsOpen
            || StartLaunchPopup.IsOpen
            || WidgetCardPopup.IsOpen
            || _widgetPicker is not null
            || (_activeContextMenu is { IsOpen: true });
    }

    private void EvaluateDockOverlapAndState()
    {
        if (_isClosed || _isSmokeOrAuditMode || !_orch.Config.UseCustomBottomDock || _isFullscreenHidden)
        {
            _hasOverlappingWindow = false;
            _isIntellihidePeeking = false;
            _edgeEnterTickMs = 0;
            _hideGraceStartTickMs = 0;
            _intellihideEdgeTimer.Stop();
            SetClickThrough(false);
            return;
        }

        if (!_orch.Config.DockIntellihideEnabled)
        {
            _hasOverlappingWindow = false;
            _isIntellihidePeeking = false;
            _edgeEnterTickMs = 0;
            _hideGraceStartTickMs = 0;
            _intellihideEdgeTimer.Stop();
            SetIntellihideTucked(false);
            return;
        }

        _cachedDockHitRectPx = ComputeCurrentDockHitRectPx();
        int minOverlapPx = _orch.Config.DockReserveWorkArea
            ? Math.Max(12, (int)Math.Round(12.0 * _activeMonitor.DpiScale))
            : 1;

        bool overlaps = HasAnyOverlappingExternalWindow(_cachedDockHitRectPx, minOverlapPx);
        _hasOverlappingWindow = overlaps;

        if (!overlaps)
        {
            _isIntellihidePeeking = false;
            _edgeEnterTickMs = 0;
            _hideGraceStartTickMs = 0;
            _intellihideEdgeTimer.Stop();
            SetIntellihideTucked(false);
            return;
        }

        if (!_intellihideEdgeTimer.IsEnabled)
        {
            _intellihideEdgeTimer.Start();
        }
        UpdateEdgePeekAndIntellihideTick();
    }

    private void UpdateEdgePeekAndIntellihideTick()
    {
        if (_isClosed || _isSmokeOrAuditMode || !_orch.Config.UseCustomBottomDock || _isFullscreenHidden || !_orch.Config.DockIntellihideEnabled)
        {
            _intellihideEdgeTimer.Stop();
            SetClickThrough(false);
            return;
        }

        if (!_hasOverlappingWindow)
        {
            _intellihideEdgeTimer.Stop();
            SetIntellihideTucked(false);
            return;
        }

        _cachedDockHitRectPx = ComputeCurrentDockHitRectPx();
        if (!NativeMethods.GetCursorPos(out var pt))
        {
            SetIntellihideTucked(!IsAnyDockOverlayOpen());
            return;
        }

        bool leftButtonDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        long nowMs = Environment.TickCount64;
        int edgePx = Math.Clamp((int)Math.Round(4.0 * _activeMonitor.DpiScale), 4, 8);
        bool inRevealEdge = IsDockAtTop
            ? DockLayoutCalculator.IsPointInTopRevealZone(pt.X, pt.Y, _activeMonitor, edgePx + DockTopInsetPx())
            : DockLayoutCalculator.IsPointInBottomRevealZone(pt.X, pt.Y, _activeMonitor, edgePx);
        int hoverPadH = (int)Math.Round(24.0 * _activeMonitor.DpiScale);
        int hoverPadTop = (int)Math.Round(30.0 * _activeMonitor.DpiScale);
        bool inKeepOpenZone = DockLayoutCalculator.IsPointInDockKeepOpenZone(pt.X, pt.Y, _cachedDockHitRectPx, hoverPadH, hoverPadTop, IsDockAtTop);
        bool overlayOpen = IsAnyDockOverlayOpen();

        if (!_isIntellihidePeeking)
        {
            if (overlayOpen)
            {
                _isIntellihidePeeking = true;
                _hideGraceStartTickMs = 0;
                SetIntellihideTucked(false);
                return;
            }

            if (inRevealEdge && !leftButtonDown)
            {
                if (_edgeEnterTickMs == 0)
                {
                    _edgeEnterTickMs = nowMs;
                }
                else if (nowMs - _edgeEnterTickMs >= 35)
                {
                    _isIntellihidePeeking = true;
                    _hideGraceStartTickMs = 0;
                    SetIntellihideTucked(false);
                    return;
                }
            }
            else
            {
                _edgeEnterTickMs = 0;
            }

            SetIntellihideTucked(true);
            return;
        }

        if (overlayOpen || _isHovered || inKeepOpenZone || inRevealEdge)
        {
            _hideGraceStartTickMs = 0;
            SetIntellihideTucked(false);
            return;
        }

        if (_hideGraceStartTickMs == 0)
        {
            _hideGraceStartTickMs = nowMs;
            return;
        }

        if (nowMs - _hideGraceStartTickMs >= 260)
        {
            _isIntellihidePeeking = false;
            _edgeEnterTickMs = 0;
            _hideGraceStartTickMs = 0;
            SetIntellihideTucked(true);
        }
    }

    private void SetIntellihideTucked(bool tucked)
    {
        if (_isIntellihideTucked == tucked && (_targetHideProgress == (tucked ? 1.0 : 0.0)))
        {
            return;
        }

        _isIntellihideTucked = tucked;
        _targetHideProgress = tucked ? 1.0 : 0.0;
        SetClickThrough(tucked);

        if (tucked)
        {
            _isHovered = false;
            _hoverLocalX = null;
            _hoveredSlotIndex = -1;
            DockTooltipBorder.Opacity = 0;
            UpdateMagnificationTargets(null);
        }

        StartRenderLoop();
    }

    private void SetClickThrough(bool clickThrough)
    {
        if (_hwnd == 0 || _isSmokeOrAuditMode || _isClickThroughApplied == clickThrough) return;
        _isClickThroughApplied = clickThrough;
        nint exStyle = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE);
        long val = exStyle.ToInt64();
        long next = clickThrough
            ? (val | NativeMethods.WS_EX_TRANSPARENT)
            : (val & ~NativeMethods.WS_EX_TRANSPARENT);
        if (next != val)
        {
            NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(next));
            NativeMethods.SetWindowPos(
                _hwnd,
                0,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE |
                NativeMethods.SWP_FRAMECHANGED);
        }
    }

    private bool HasAnyOverlappingExternalWindow(NativeMethods.RECT dockZone, int minOverlapPx)
    {
        int ownPid = Environment.ProcessId;
        bool foundOverlap = false;

        NativeMethods.EnumWindows((hwnd, _lParam) =>
        {
            if (hwnd == _hwnd || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
                return true;

            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0)
                return true;

            long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == ownPid)
                return true;

            if (!TryReadExtendedWindowRect(hwnd, out var rect))
                return true;

            if (!DockLayoutCalculator.DoesWindowOverlapDockZone(rect, dockZone, minOverlapPx))
                return true;

            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0)
                return true;

            string cls = ReadWindowClassSafe(hwnd);
            string title = ReadWindowTitleSafe(hwnd);
            if (DockLayoutCalculator.IsNonBlockingShellOrWallpaperClass(cls)
                || !WindowControlMath.IsEligibleExternalWindowMetadata(cls, title))
            {
                return true;
            }

            foundOverlap = true;
            return false;
        }, 0);

        return foundOverlap;
    }

    private static unsafe bool TryReadExtendedWindowRect(nint hwnd, out NativeMethods.RECT rect)
    {
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, sizeof(NativeMethods.RECT)) == 0)
        {
            return true;
        }
        return NativeMethods.GetWindowRect(hwnd, out rect);
    }

    private static unsafe string ReadWindowClassSafe(nint hwnd)
    {
        char* clsBuf = stackalloc char[128];
        int clsLen = NativeMethods.GetClassNameW(hwnd, clsBuf, 128);
        return clsLen > 0 ? new string(clsBuf, 0, clsLen).Trim() : string.Empty;
    }

    private static unsafe string ReadWindowTitleSafe(nint hwnd)
    {
        int len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        int cap = Math.Min(len + 1, 256);
        char* buf = stackalloc char[cap];
        int read = NativeMethods.GetWindowTextW(hwnd, buf, cap);
        return read > 0 ? new string(buf, 0, read).Trim() : string.Empty;
    }

    private nint OnDockWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new nint(NativeMethods.MA_NOACTIVATE);
        }

        if (msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED)
        {
            (_orch.MonitorLayout as Win32MonitorLayoutService)?.InvalidateCache();
            ApplyConfigAndSync();
        }
        return nint.Zero;
    }

    private void RefreshDockItemsNow()
    {
        if (_isClosed) return;

        var items = _appsService.BuildSnapshot(out nint fgHwnd);
        _lastForegroundHwnd = fgHwnd;
        _currentAppItems = items;
        if (_tickCounter == 0)
        {
            _lastRecycleBin = DockAppsService.QueryRecycleBin();
        }

        RebuildVisualSlots(force: false);
    }

    private void RebuildVisualSlots(bool force)
    {
        int iconSize = Math.Clamp(_orch.Config.DockIconSizeDip, 26, 48);
        string lang = _orch.Config.Language;
        var keyBuilder = new System.Text.StringBuilder(256);
        keyBuilder.Append(iconSize).Append('|').Append(lang).Append('|').Append(WidgetStructureKey()).Append('|');
        foreach (var item in _currentAppItems)
        {
            keyBuilder.Append(item.Key)
                .Append(':').Append(item.IsPinned ? 'P' : 'U')
                .Append(':').Append(item.IsRunning ? item.Windows.Count : 0)
                .Append(';');
        }

        string nextKey = keyBuilder.ToString();
        if (!force && string.Equals(nextKey, _lastRenderedStructureKey, StringComparison.Ordinal))
        {
            UpdateDockSlotStatesInPlace();
            return;
        }

        // Never tear down the slot being dragged; the next refresh after release rebuilds.
        if (_isReorderDragging)
        {
            if (!force) return;
            EndReorderDrag(commit: false);
        }
        _pressedPinnedSlot = null;
        _lastRenderedStructureKey = nextKey;

        Dictionary<string, (double Scale, double ShiftX, double LiftY)>? preservedTransforms = null;
        if (_slots.Count > 0)
        {
            preservedTransforms = new Dictionary<string, (double Scale, double ShiftX, double LiftY)>(StringComparer.OrdinalIgnoreCase);
            foreach (var oldSlot in _slots)
            {
                if (oldSlot.Kind == SlotKind.App && oldSlot.AppItem is not null &&
                    (Math.Abs(oldSlot.CurrentScale - 1.0) > 0.002 ||
                     Math.Abs(oldSlot.CurrentShiftX) > 0.05 ||
                     Math.Abs(oldSlot.CurrentLiftY) > 0.05))
                {
                    preservedTransforms[oldSlot.AppItem.Key] = (oldSlot.CurrentScale, oldSlot.CurrentShiftX, oldSlot.CurrentLiftY);
                }
            }
        }

        DockItemsHost.Children.Clear();
        _slots.Clear();
        _lastTooltipSlotIndex = -1;

        double tileWidth = iconSize + 6.0;
        double tileHeight = iconSize;

        // 1. Start Menu Slot
        AddIconSlot(
            kind: SlotKind.StartMenu,
            item: null,
            tooltip: Strings.DockStartLabel(lang),
            iconImage: null,
            fallbackGlyph: "\uE8A9",
            glyphBrushHex: "#FFE4E4E7",
            iconSize: iconSize,
            tileW: tileWidth,
            tileH: tileHeight,
            isRunning: false,
            isForeground: false,
            windowCount: 0);

        AddVerticalDivider(iconSize, leftMargin: 4, rightMargin: 10);

        // 2. Pinned & Unpinned App Slots (separated cleanly when both exist)
        bool sawPinned = false;
        bool insertedUnpinnedDivider = false;
        foreach (var item in _currentAppItems)
        {
            if (item.IsPinned)
            {
                sawPinned = true;
            }
            else if (sawPinned && !insertedUnpinnedDivider)
            {
                AddVerticalDivider(iconSize);
                insertedUnpinnedDivider = true;
            }

            string tip = item.Windows.Count > 1
                ? $"{item.DisplayName} ({item.Windows.Count})"
                : item.DisplayName;

            AddIconSlot(
                kind: SlotKind.App,
                item: item,
                tooltip: tip,
                iconImage: item.Icon,
                fallbackGlyph: "\uE737",
                glyphBrushHex: "#FFE4E4E7",
                iconSize: iconSize,
                tileW: tileWidth,
                tileH: tileHeight,
                isRunning: item.IsRunning,
                isForeground: item.IsActiveForeground,
                windowCount: item.Windows.Count);
        }

        AddVerticalDivider(iconSize, leftMargin: 10, rightMargin: 4);

        // 2b. Live widgets (Lumabar-style tiles), in the user's order
        if (AddWidgetSlots(iconSize) > 0)
        {
            AddVerticalDivider(iconSize, leftMargin: 7, rightMargin: 4);
        }

        // 3. Synchronized DinamikAda Live Island Slot
        bool aiInDock = _orch.Config.ShowAiQuotaInIslandAndDock && _orch.AiQuota.GetSummary().TotalCount > 0;
        AddIslandSyncSlot(lang, iconSize, tileWidth + (aiInDock ? 24.0 : 10.0), tileHeight, aiInDock);

        // 4. Windows Background / Tray Overflow ("^") Chevron Slot (stationary)
        AddTrayChevronSlot(lang, iconSize, Math.Max(26.0, iconSize - 6.0), tileHeight);

        // 5. macOS Trash / Recycle Bin Slot (stationary)
        AddTrashSlot(lang, iconSize, tileWidth, tileHeight);

        // 5. Compact Clock & Date Chip
        AddClockChip(iconSize);

        DockPillContainer.ClearValue(WidthProperty);
        DockPillContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        RecomputeSlotCenters();

        if (preservedTransforms is not null)
        {
            foreach (var s in _slots)
            {
                if (s.Kind == SlotKind.App && s.AppItem is not null &&
                    preservedTransforms.TryGetValue(s.AppItem.Key, out var prev))
                {
                    s.CurrentScale = prev.Scale;
                    s.CurrentShiftX = prev.ShiftX;
                    s.CurrentLiftY = prev.LiftY;
                    s.Scale.ScaleX = prev.Scale;
                    s.Scale.ScaleY = prev.Scale;
                    s.Translate.X = prev.ShiftX;
                    s.Translate.Y = prev.LiftY;
                }
            }
        }

        if (_isHovered && _hoverLocalX.HasValue)
        {
            UpdateMagnificationTargets(_hoverLocalX);
            StartRenderLoop();
        }

        if (!_isSmokeOrAuditMode && _orch.Config.UseCustomBottomDock && !_isFullscreenHidden)
        {
            _cachedDockHitRectPx = ComputeCurrentDockHitRectPx();
        }
    }

    private void UpdateDockSlotStatesInPlace()
    {
        var fgAccent = ParseAccentColor(_orch.Coordinator.Current.PrimaryActivity?.AccentHex, Color.FromRgb(96, 165, 250));
        int itemIdx = 0;
        for (int i = 0; i < _slots.Count && itemIdx < _currentAppItems.Count; i++)
        {
            var slot = _slots[i];
            if (slot.Kind != SlotKind.App) continue;

            var item = _currentAppItems[itemIdx++];
            slot.AppItem = item;
            string tip = item.Windows.Count > 1
                ? $"{item.DisplayName} ({item.Windows.Count})"
                : item.DisplayName;
            slot.TooltipLabel = tip;
            if (_isHovered && _hoveredSlotIndex == i && !string.Equals(_lastTooltipTitle, tip, StringComparison.Ordinal))
            {
                _lastTooltipTitle = tip;
                DockTooltipText.Text = tip;
            }

            if (slot.RunningPill is not null)
            {
                slot.RunningPill.Visibility = item.IsActiveForeground ? Visibility.Visible : Visibility.Collapsed;
                if (item.IsActiveForeground)
                {
                    if (slot.RunningPill.Background is SolidColorBrush b && !b.IsFrozen)
                    {
                        b.Color = fgAccent;
                    }
                    else
                    {
                        slot.RunningPill.Background = new SolidColorBrush(fgAccent);
                    }
                }
            }
            if (slot.RunningDot is not null)
            {
                slot.RunningDot.Visibility = item.IsRunning && !item.IsActiveForeground
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private void AddIconSlot(
        SlotKind kind,
        DockAppItem? item,
        string tooltip,
        ImageSource? iconImage,
        string fallbackGlyph,
        string glyphBrushHex,
        int iconSize,
        double tileW,
        double tileH,
        bool isRunning,
        bool isForeground,
        int windowCount)
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

        // macOS aesthetic: direct crisp 1:1 app icons without heavy gray box frames;
        // subtle glass squircle only for Start button or glyph-only fallbacks.
        bool useGlassPlate = kind == SlotKind.StartMenu || iconImage is null;
        var plate = new Border
        {
            Width = iconSize,
            Height = iconSize,
            CornerRadius = new CornerRadius(Math.Round(iconSize * 0.26)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = useGlassPlate ? DockTint(0x12) : Brushes.Transparent,
            BorderBrush = useGlassPlate ? DockTint(0x20) : Brushes.Transparent,
            BorderThickness = new Thickness(useGlassPlate ? 0.7 : 0.0)
        };

        var plateGrid = new Grid();
        if (kind == SlotKind.StartMenu)
        {
            double tile = Math.Max(5.0, Math.Round(iconSize * 0.18));
            double gap = Math.Max(2.0, Math.Round(tile * 0.25));
            var startGrid = new System.Windows.Controls.Primitives.UniformGrid
            {
                Rows = 2,
                Columns = 2,
                Width = tile * 2 + gap,
                Height = tile * 2 + gap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Color[] quadColors =
            [
                Color.FromArgb(0xE8, 0xF5, 0xF5, 0xF7),
                Color.FromArgb(0xD8, 0x93, 0xC5, 0xFD),
                Color.FromArgb(0xD8, 0x93, 0xC5, 0xFD),
                Color.FromArgb(0xE8, 0x64, 0xD2, 0xFF)
            ];
            for (int q = 0; q < 4; q++)
            {
                startGrid.Children.Add(new Border
                {
                    Width = tile,
                    Height = tile,
                    CornerRadius = new CornerRadius(Math.Max(1.2, tile * 0.22)),
                    Background = new SolidColorBrush(quadColors[q]),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            plateGrid.Children.Add(startGrid);
        }
        else if (iconImage is not null)
        {
            plateGrid.Children.Add(new Image
            {
                Source = iconImage,
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            plateGrid.Children.Add(new TextBlock
            {
                Text = fallbackGlyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = Math.Round(iconSize * 0.48),
                Foreground = DockGlyph((Color)ColorConverter.ConvertFromString(glyphBrushHex)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        if (windowCount > 1)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(10, 132, 255)),
                Padding = new Thickness(3.5, 0, 3.5, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -2, 0),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = windowCount.ToString(),
                    FontSize = 8.8,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White
                }
            };
            plateGrid.Children.Add(badge);
        }

        plate.Child = plateGrid;
        container.Children.Add(plate);

        Ellipse? dot = null;
        Border? pill = null;
        if (kind == SlotKind.App)
        {
            var fgAccent = ParseAccentColor(_orch.Coordinator.Current.PrimaryActivity?.AccentHex, Color.FromRgb(96, 165, 250));
            pill = new Border
            {
                Width = 14.0,
                Height = 3.5,
                CornerRadius = new CornerRadius(1.75),
                Background = new SolidColorBrush(fgAccent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 0.5),
                IsHitTestVisible = false,
                Visibility = isForeground ? Visibility.Visible : Visibility.Collapsed
            };
            container.Children.Add(pill);

            dot = new Ellipse
            {
                Width = 5.0,
                Height = 5.0,
                Fill = new SolidColorBrush(Color.FromArgb(0xF2, 0xF5, 0xF5, 0xF7)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 0.5),
                IsHitTestVisible = false,
                Visibility = (isRunning && !isForeground) ? Visibility.Visible : Visibility.Collapsed
            };
            container.Children.Add(dot);
        }
        else if (isRunning)
        {
            dot = new Ellipse
            {
                Width = 5.0,
                Height = 5.0,
                Fill = new SolidColorBrush(Color.FromArgb(0xF2, 0xF5, 0xF5, 0xF7)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 0.5),
                IsHitTestVisible = false
            };
            container.Children.Add(dot);
        }

        var slotVisual = new DockSlotVisual
        {
            Container = container,
            Scale = scale,
            Translate = translate,
            IconPlate = plate,
            RunningDot = dot,
            RunningPill = pill,
            TooltipLabel = tooltip,
            AppItem = item,
            Kind = kind
        };

        if (kind == SlotKind.StartMenu)
        {
            _startMenuBtn = plate;
            container.MouseEnter += (_, _) =>
                plate.Background = DockTint(0x24);
            container.MouseLeave += (_, _) =>
                plate.Background = DockTint(0x12);
            container.PreviewMouseLeftButtonDown += (_, ev) =>
            {
                if (StartLaunchPopup.IsOpen)
                {
                    StartLaunchPopup.IsOpen = false;
                    _suppressNextStartOpenOnUp = true;
                    ev.Handled = true;
                }
                else
                {
                    _suppressNextStartOpenOnUp = false;
                }
            };
        }

        if (kind == SlotKind.App && item is { IsPinned: true })
        {
            container.MouseLeftButtonDown += (_, ev) =>
            {
                _pressedPinnedSlot = slotVisual;
                _pressStartPoint = ev.GetPosition(DockItemsHost);
            };
            container.LostMouseCapture += (_, _) =>
            {
                if (_isReorderDragging && ReferenceEquals(_pressedPinnedSlot, slotVisual))
                {
                    EndReorderDrag(commit: false);
                }
            };
        }

        container.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            if (_isReorderDragging)
            {
                EndReorderDrag(commit: true);
                return;
            }
            _pressedPinnedSlot = null;
            if (kind == SlotKind.StartMenu && _suppressNextStartOpenOnUp)
            {
                _suppressNextStartOpenOnUp = false;
                return;
            }
            HandleSlotPrimaryClick(slotVisual);
        };
        container.MouseRightButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            HandleSlotContextMenu(slotVisual);
        };

        _slots.Add(slotVisual);
        DockItemsHost.Children.Add(container);
    }

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

        // Sleek miniature Dynamic Island pill synced with top notch (+ live AI Quota ready pill)
        double pillH = Math.Max(20.0, Math.Round(iconSize * 0.68));
        double pillW = showAiSlot
            ? Math.Max(54.0, iconSize + 20.0)
            : Math.Max(36.0, iconSize + 10.0);
        var plate = new Border
        {
            Width = pillW,
            Height = pillH,
            CornerRadius = new CornerRadius(pillH * 0.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Math.Round((iconSize - pillH) * 0.5), 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
            BorderBrush = DockTint(0x36),
            BorderThickness = new Thickness(0.8)
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Mini camera lens ring
        var lensOuter = new Grid
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 4, 0)
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

        var liveDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _islandSyncDot = liveDot;

        _islandAiBadgeText = new TextBlock
        {
            FontSize = 8.4,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(48, 209, 88)),
            VerticalAlignment = VerticalAlignment.Center
        };
        _islandAiBadge = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0x30, 0xD1, 0x58)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x30, 0xD1, 0x58)),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(3.5, 0.2, 3.5, 0.2),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _islandAiBadgeText
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
        UpdateDockAiBadgeAndTooltip();
    }

    private void AddTrayChevronSlot(string lang, int iconSize, double tileW, double tileH)
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

        var plate = new Border
        {
            Width = Math.Max(24.0, tileW - 2.0),
            Height = iconSize,
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = DockTint(0x0E),
            BorderBrush = DockTint(0x16),
            BorderThickness = new Thickness(0.6)
        };

        plate.Child = new TextBlock
        {
            Text = "\uE70E",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = Math.Max(9.5, Math.Round(iconSize * 0.34)),
            Foreground = DockGlyph(Color.FromRgb(228, 228, 231)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        container.Children.Add(plate);

        _trayActiveDot = new Ellipse
        {
            Width = 3.5,
            Height = 3.5,
            Fill = new SolidColorBrush(Color.FromRgb(100, 210, 255)),
            Opacity = _lastTrayAppCount > 0 ? 1.0 : 0.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0.5),
            IsHitTestVisible = false
        };
        container.Children.Add(_trayActiveDot);

        var slotVisual = new DockSlotVisual
        {
            Container = container,
            Scale = scale,
            Translate = translate,
            IconPlate = plate,
            RunningDot = _trayActiveDot,
            RunningPill = null,
            TooltipLabel = Strings.DockTrayAppsLabel(lang),
            Kind = SlotKind.TrayOverflow
        };

        container.MouseEnter += (_, _) =>
        {
            plate.Background = DockTint(0x1C);
            plate.BorderBrush = DockTint(0x2A);
        };
        container.MouseLeave += (_, _) =>
        {
            plate.Background = DockTint(0x0E);
            plate.BorderBrush = DockTint(0x16);
        };
        container.PreviewMouseLeftButtonDown += (_, ev) =>
        {
            if (TrayOverflowPopup.IsOpen)
            {
                TrayOverflowPopup.IsOpen = false;
                _suppressNextTrayOpenOnUp = true;
                ev.Handled = true;
            }
            else
            {
                _suppressNextTrayOpenOnUp = false;
            }
        };
        container.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            if (_suppressNextTrayOpenOnUp)
            {
                _suppressNextTrayOpenOnUp = false;
                return;
            }
            ToggleTrayPopup();
        };
        container.MouseRightButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            HandleSlotContextMenu(slotVisual);
        };

        _trayChevronBtn = plate;
        _slots.Add(slotVisual);
        DockItemsHost.Children.Add(container);
    }

    private System.Windows.Threading.DispatcherTimer? _popupLightDismissTimer;
    private NativeMethods.LowLevelMouseProc? _popupMouseHookProc;
    private nint _popupMouseHook;
    private nint _popupOpenForegroundHwnd;
    private long _popupOpenedTickMs;
    private bool _suppressNextTrayOpenOnUp;
    private bool _suppressNextClockOpenOnUp;

    private static bool IsScreenPointInsideElement(FrameworkElement? el, NativeMethods.POINT pt, double padPx)
    {
        if (el is null || !el.IsVisible || !el.IsLoaded) return false;
        double w = el.ActualWidth;
        double h = el.ActualHeight;
        if (w <= 1.0 || h <= 1.0) return false;
        try
        {
            var tl = el.PointToScreen(new System.Windows.Point(0, 0));
            var br = el.PointToScreen(new System.Windows.Point(w, h));
            return pt.X >= tl.X - padPx && pt.X <= br.X + padPx && pt.Y >= tl.Y - padPx && pt.Y <= br.Y + padPx;
        }
        catch
        {
            return false;
        }
    }

    private void StartPopupLightDismissWatcher()
    {
        _popupOpenForegroundHwnd = NativeMethods.GetForegroundWindow();
        _popupOpenedTickMs = Environment.TickCount64;

        if (_popupMouseHook == 0)
        {
            _popupMouseHookProc ??= OnLowLevelPopupMouseEvent;
            _popupMouseHook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_MOUSE_LL,
                _popupMouseHookProc,
                NativeMethods.GetModuleHandleW(null),
                0);
        }

        if (_popupLightDismissTimer is null)
        {
            _popupLightDismissTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(50),
                System.Windows.Threading.DispatcherPriority.Input,
                (_, _) => OnPopupLightDismissTick(),
                Dispatcher);
        }

        if (!_popupLightDismissTimer.IsEnabled)
        {
            _popupLightDismissTimer.Start();
        }
    }

    private void UninstallPopupMouseHook()
    {
        if (_popupMouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_popupMouseHook);
            _popupMouseHook = 0;
        }
    }

    private void StopPopupLightDismissWatcherIfIdle()
    {
        if (!TrayOverflowPopup.IsOpen && !ClockCalendarPopup.IsOpen && !StartLaunchPopup.IsOpen && !WidgetCardPopup.IsOpen)
        {
            _popupLightDismissTimer?.Stop();
            UninstallPopupMouseHook();
        }
    }

    private nint GetElementPopupHwnd(FrameworkElement? el)
    {
        if (el is null) return 0;
        return PresentationSource.FromVisual(el) is HwndSource hs ? hs.Handle : 0;
    }

    private bool IsOwnOrChildPopupHwnd(nint candidateHwnd)
    {
        if (candidateHwnd == 0) return false;
        if (candidateHwnd == _hwnd) return true;
        return candidateHwnd == GetElementPopupHwnd(StartLaunchCard)
            || candidateHwnd == GetElementPopupHwnd(TrayOverflowCard)
            || candidateHwnd == GetElementPopupHwnd(ClockCalendarCard)
            || candidateHwnd == GetElementPopupHwnd(WidgetCardBorder);
    }

    private unsafe nint OnLowLevelPopupMouseEvent(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != 0)
        {
            int wm = unchecked((int)wParam);
            if (wm is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
            {
                var pt = ((NativeMethods.MSLLHOOKSTRUCT*)lParam)->pt;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Send,
                    () => HandleGlobalPopupMouseDown(pt, isLeftButton: wm == NativeMethods.WM_LBUTTONDOWN));
            }
        }
        return NativeMethods.CallNextHookEx(_popupMouseHook, nCode, wParam, lParam);
    }

    private void HandleGlobalPopupMouseDown(NativeMethods.POINT pt, bool isLeftButton)
    {
        if (_isClosed || _activeContextMenu is { IsOpen: true }) return;

        if (StartLaunchPopup.IsOpen && WindowRestoreGeometry.ShouldLightDismissPopup(
                isOpen: true,
                waitingInitialRelease: false,
                prevAnyButtonDown: false,
                curAnyButtonDown: true,
                isEscapeDown: false,
                insideCard: IsScreenPointInsideElement(StartLaunchCard, pt, 4.0),
                insideToggleButton: isLeftButton && IsScreenPointInsideElement(_startMenuBtn, pt, 8.0),
                foregroundChanged: false,
                contextMenuOpen: false))
        {
            StartLaunchPopup.IsOpen = false;
        }

        if (TrayOverflowPopup.IsOpen && WindowRestoreGeometry.ShouldLightDismissPopup(
                isOpen: true,
                waitingInitialRelease: false,
                prevAnyButtonDown: false,
                curAnyButtonDown: true,
                isEscapeDown: false,
                insideCard: IsScreenPointInsideElement(TrayOverflowCard, pt, 4.0),
                insideToggleButton: isLeftButton && IsScreenPointInsideElement(_trayChevronBtn, pt, 8.0),
                foregroundChanged: false,
                contextMenuOpen: false))
        {
            TrayOverflowPopup.IsOpen = false;
        }

        if (WidgetCardPopup.IsOpen && WindowRestoreGeometry.ShouldLightDismissPopup(
                isOpen: true,
                waitingInitialRelease: false,
                prevAnyButtonDown: false,
                curAnyButtonDown: true,
                isEscapeDown: false,
                insideCard: IsScreenPointInsideElement(WidgetCardBorder, pt, 4.0),
                insideToggleButton: isLeftButton && IsScreenPointInsideElement(_widgetCardAnchor, pt, 6.0),
                foregroundChanged: false,
                contextMenuOpen: false))
        {
            WidgetCardPopup.IsOpen = false;
        }

        if (ClockCalendarPopup.IsOpen && WindowRestoreGeometry.ShouldLightDismissPopup(
                isOpen: true,
                waitingInitialRelease: false,
                prevAnyButtonDown: false,
                curAnyButtonDown: true,
                isEscapeDown: false,
                insideCard: IsScreenPointInsideElement(ClockCalendarCard, pt, 4.0),
                insideToggleButton: isLeftButton && IsScreenPointInsideElement(_clockChip, pt, 8.0),
                foregroundChanged: false,
                contextMenuOpen: false))
        {
            ClockCalendarPopup.IsOpen = false;
        }
    }

    private void OnPopupLightDismissTick()
    {
        if (_isClosed || (!TrayOverflowPopup.IsOpen && !ClockCalendarPopup.IsOpen && !StartLaunchPopup.IsOpen && !WidgetCardPopup.IsOpen))
        {
            StopPopupLightDismissWatcherIfIdle();
            return;
        }

        if (_activeContextMenu is { IsOpen: true })
        {
            return;
        }

        bool isEscapeDown = (NativeMethods.GetAsyncKeyState(0x1B /* VK_ESCAPE */) & 0x8000) != 0;
        nint curFg = NativeMethods.GetForegroundWindow();
        bool fgChanged = (Environment.TickCount64 - _popupOpenedTickMs) >= 120
            && _popupOpenForegroundHwnd != 0
            && curFg != 0
            && curFg != _popupOpenForegroundHwnd
            && !IsOwnOrChildPopupHwnd(curFg);
        if (fgChanged)
        {
            _popupOpenForegroundHwnd = curFg;
        }

        if (!isEscapeDown && !fgChanged)
        {
            return;
        }

        if (StartLaunchPopup.IsOpen)
        {
            StartLaunchPopup.IsOpen = false;
        }

        if (TrayOverflowPopup.IsOpen)
        {
            TrayOverflowPopup.IsOpen = false;
        }

        if (ClockCalendarPopup.IsOpen)
        {
            ClockCalendarPopup.IsOpen = false;
        }

        if (WidgetCardPopup.IsOpen)
        {
            WidgetCardPopup.IsOpen = false;
        }
    }

    private void ToggleTrayPopup()
    {
        if (_trayChevronBtn is null) return;
        StartLaunchPopup.IsOpen = false;
        ClockCalendarPopup.IsOpen = false;
        if (TrayOverflowPopup.IsOpen)
        {
            TrayOverflowPopup.IsOpen = false;
            return;
        }

        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        TrayOverflowCard.Width = Math.Clamp(_activeMonitor.EffectiveWorkWidthPx / scale - 32, 300, 388);
        TrayAppsScroller.MaxHeight = Math.Clamp(_activeMonitor.EffectiveWorkHeightPx / scale - 180, 90, 380);
        TrayOverflowPopup.PlacementTarget = _trayChevronBtn;
        TrayOverflowPopup.IsOpen = true;
    }

    private void OnTrayPopupOpened(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        TrayPopupStatusText.Visibility = Visibility.Collapsed;
        RebuildTrayOverflowGrid(_cachedTrayItems);
        TrayAppsScroller.ScrollToTop();
        SyncCoordinatorState(_orch.Coordinator.Current);
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
        StopPopupLightDismissWatcherIfIdle();
        ScheduleIntellihideEval();
    }

    private readonly List<AppLaunchItem> _displayedStartApps = new(48);

    private void ToggleStartPopup()
    {
        TrayOverflowPopup.IsOpen = false;
        ClockCalendarPopup.IsOpen = false;
        if (StartLaunchPopup.IsOpen)
        {
            StartLaunchPopup.IsOpen = false;
            return;
        }

        double sinceClose = (Stopwatch.GetTimestamp() - _lastStartPopupClosedTicks) / (double)Stopwatch.Frequency;
        if (_lastStartPopupClosedTicks != 0 && sinceClose < 0.22)
        {
            return;
        }

        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        SizeStartMenu(_activeMonitor.EffectiveWorkWidthPx / scale, _activeMonitor.EffectiveWorkHeightPx / scale);
        StartLaunchPopup.PlacementTarget = DockPillContainer;
        StartLaunchPopup.IsOpen = true;
    }

    private void SizeStartMenu(double availableWidth, double availableHeight)
    {
        StartLaunchCard.Width = Math.Clamp(availableWidth - 32, 300, 560);
        StartShortcutGrid.Columns = StartLaunchCard.Width >= 420 ? 4 : 2;
        StartAppsGrid.Columns = StartLaunchCard.Width >= 510 ? 5 : StartLaunchCard.Width >= 410 ? 4 : 3;
        StartAppsScrollViewer.Height = Math.Clamp(availableHeight - (StartShortcutGrid.Columns == 4 ? 280 : 328), 90, 340);
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

        RebuildStartAppsGrid(string.Empty);
        StartAppsScrollViewer.ScrollToTop();
        SyncCoordinatorState(_orch.Coordinator.Current);

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
                if (StartLaunchPopup.IsOpen)
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

    private void ApplyStartPopupLocalization()
    {
        string lang = _orch.Config.Language;
        StartBadgeText.Text = Strings.StartMenuBrandBadge(lang);
        StartModIslandText.Text = Strings.StartMenuModuleIsland(lang);
        StartModAiText.Text = Strings.StartMenuModuleAi(lang);
        StartModSystemText.Text = Strings.StartMenuModuleSystem(lang);
        StartModCommandText.Text = Strings.StartMenuModuleCommand(lang);
        StartSearchPlaceholder.Text = Strings.StartMenuSearchPlaceholder(lang);
        StartAppsSectionTitle.Text = Strings.StartMenuAppsSection(lang);
        StartAppsEmptyText.Text = Strings.StartMenuNoMatches(lang);
        StartUserNameText.Text = _isSmokeOrAuditMode || string.IsNullOrWhiteSpace(Environment.UserName) ? "Windows" : Environment.UserName;
        StartExplorerText.Text = Strings.StartMenuExplorer(lang);
        StartTaskMgrText.Text = Strings.StartMenuTaskMgr(lang);
        BtnStartClassicWin.ToolTip = Strings.StartMenuClassicWin(lang);
        BtnStartSettings.ToolTip = Strings.Settings(lang);
        BtnStartLock.ToolTip = Strings.StartMenuLock(lang);
        BtnStartPower.ToolTip = Strings.StartMenuPowerOptions(lang);
        AutomationProperties.SetName(BtnStartSearchClear, Strings.Pick(lang, "Aramayı temizle", "Clear search"));
        foreach (var button in new[] { BtnStartClassicWin, BtnStartSettings, BtnStartLock, BtnStartPower })
            AutomationProperties.SetName(button, button.ToolTip as string ?? string.Empty);
    }

    private void RebuildStartAppsGrid(string rawQuery)
    {
        string lang = _orch.Config.Language;
        string q = rawQuery.Trim();
        string? focusedApp = Keyboard.FocusedElement is Button focused && StartAppsGrid.Children.Contains(focused)
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

        var ranked = pool.Values
            .Select(app => (App: app,
                Score: q.Length == 0 ? 1 : FuzzyMatcher.Score(q, app.DisplayName),
                Pinned: _isSmokeOrAuditMode
                    ? _currentAppItems.Any(item => item.IsPinned && item.DisplayName == app.DisplayName)
                    : _appsService.IsStartAppPinned(app)))
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Pinned)
            .ThenBy(result => result.App.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _displayedStartApps.Clear();
        StartAppsGrid.Children.Clear();
        StartAppsSectionTitle.Text = q.Length == 0
            ? Strings.StartMenuAppsSection(lang)
            : Strings.Pick(lang, "Arama sonuçları", "Search results");
        StartAppsEmptyText.Visibility = ranked.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var tilesToLoad = new List<(AppLaunchItem App, Image Icon, Border Fallback)>();
        foreach (var result in ranked.Take(60))
        {
            var app = result.App;
            _displayedStartApps.Add(app);
            var (tile, icon, fallback) = CreateStartAppTile(app, result.Pinned, lang);
            StartAppsGrid.Children.Add(tile);
            tilesToLoad.Add((app, icon, fallback));
            if (app.DisplayName == focusedApp) tile.Focus();
        }

        StartAppsCountText.Text = ranked.Length > _displayedStartApps.Count
            ? $"{_displayedStartApps.Count} / {ranked.Length}"
            : Strings.StartMenuAppsCount(lang, ranked.Length);

        if (!_isSmokeOrAuditMode && tilesToLoad.Count > 0)
        {
            _startIconLoad = new CancellationTokenSource();
            _ = LoadStartIconsAsync(tilesToLoad, _startIconLoad.Token);
        }
    }

    private (Button Tile, Image Icon, Border Fallback) CreateStartAppTile(AppLaunchItem app, bool isPinned, string lang)
    {
        var tile = new Button
        {
            Style = (Style)FindResource("StartTileButtonStyle"),
            Height = 79,
            Cursor = Cursors.Hand,
            ToolTip = app.DisplayName
        };
        AutomationProperties.SetName(tile, app.DisplayName);

        var accent = (Brush)FindResource("AccentCyanBrush");
        var icon = new Image
        {
            Width = 32,
            Height = 32,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed
        };
        var fallback = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromRgb(31, 54, 83)),
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(app.DisplayName) ? "?" : StringInfo.GetNextTextElement(app.DisplayName).ToUpperInvariant(),
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var iconHost = new Grid { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5) };
        iconHost.Children.Add(fallback);
        iconHost.Children.Add(icon);

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(iconHost);
        content.Children.Add(new TextBlock
        {
            Text = app.DisplayName,
            FontFamily = (FontFamily)FindResource("UiTextFont"),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 14,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Height = 28
        });

        var root = new Grid();
        root.Children.Add(content);
        if (isPinned)
        {
            root.Children.Add(new TextBlock
            {
                Text = "\uE718",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 9,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            });
            AutomationProperties.SetHelpText(tile, Strings.Pick(lang, "Dock’a sabitlendi", "Pinned to Dock"));
        }

        tile.Content = root;
        tile.Click += (_, _) => DismissStartAndLaunchApp(app);
        tile.PreviewKeyDown += (_, ev) =>
        {
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

    private async void DismissStartAndLaunchApp(AppLaunchItem app)
    {
        if (_isSmokeOrAuditMode) return;
        var result = await _orch.Launcher.LaunchInstalledAppAsync(app);
        if (result.Succeeded)
        {
            StartLaunchPopup.IsOpen = false;
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

        if (!app.IsStoreApp && !string.IsNullOrWhiteSpace(app.LaunchTarget) && System.IO.File.Exists(app.LaunchTarget))
        {
            bool pinned = _appsService.IsStartAppPinned(app);
            if (!pinned)
            {
                var pinItem = new MenuItem { Header = Strings.DockPinApp(lang) };
                pinItem.Click += (_, _) =>
                {
                    if (_appsService.TryPinStartApp(app))
                    {
                        RefreshDockItemsNow();
                        if (StartLaunchPopup.IsOpen)
                        {
                            RebuildStartAppsGrid(StartSearchBox.Text ?? string.Empty);
                        }
                    }
                };
                menu.Items.Add(pinItem);
            }
        }

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

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

    private void OnStartCardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            StartLaunchPopup.IsOpen = false;
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
        if (e.Key == Key.Down && StartAppsGrid.Children.Count > 0)
        {
            e.Handled = true;
            StartAppsGrid.Children[0].Focus();
        }
        else if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(StartSearchBox.Text) && _displayedStartApps.Count > 0)
        {
            e.Handled = true;
            DismissStartAndLaunchApp(_displayedStartApps[0]);
        }
    }

    private void OnStartSearchClearClick(object sender, RoutedEventArgs e)
    {
        StartSearchBox.Text = string.Empty;
        StartSearchBox.Focus();
    }

    private void OnStartAiStatusClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (!_isSmokeOrAuditMode) _orch.Coordinator.RequestExpand(ExpandedTabId.AiQuota);
    }

    private void OnStartClassicWindowsClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (_isSmokeOrAuditMode) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            WindowsTaskbarManager.TriggerStartMenu();
        });
    }

    private void OnStartSettingsClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (!_isSmokeOrAuditMode) _orch.RequestOpenSettings();
    }

    private void OnStartModuleIslandClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (!_isSmokeOrAuditMode) _orch.Coordinator.RequestExpand(ExpandedTabId.Now);
    }

    private void OnStartModuleAiClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (!_isSmokeOrAuditMode) _orch.Coordinator.RequestExpand(ExpandedTabId.AiQuota);
    }

    private void OnStartModuleSystemClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (!_isSmokeOrAuditMode) _orch.Coordinator.RequestExpand(ExpandedTabId.System);
    }

    private void OnStartModuleCommandClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (!_isSmokeOrAuditMode) _orch.Coordinator.OpenCommandPalette();
    }

    private void OnStartExplorerClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (_isSmokeOrAuditMode) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
    }

    private void OnStartTerminalClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
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
        StartLaunchPopup.IsOpen = false;
        if (_isSmokeOrAuditMode) return;
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
    }

    private void OnStartLockClick(object sender, RoutedEventArgs e)
    {
        StartLaunchPopup.IsOpen = false;
        if (!_isSmokeOrAuditMode) _orch.Connectivity.LockWorkstation();
    }

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
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private Task RefreshTrayAppsAsync(bool showLoading = false)
    {
        if (_isClosed || _isSmokeOrAuditMode || _trayActionBusy) return Task.CompletedTask;
        if (_trayRefreshTask is { IsCompleted: false })
        {
            if (showLoading)
            {
                SetTrayStatus(Strings.Pick(_orch.Config.Language, "Uygulamalar kontrol ediliyor…", "Checking apps…"));
                TrayPopupEmptyText.Visibility = Visibility.Collapsed;
            }
            return _trayRefreshTask;
        }
        return _trayRefreshTask = RefreshTrayAppsCoreAsync(showLoading);
    }

    private async Task RefreshTrayAppsCoreAsync(bool showLoading)
    {
        BtnTrayPopupRefresh.IsEnabled = false;
        if (showLoading)
        {
            SetTrayStatus(Strings.Pick(_orch.Config.Language, "Uygulamalar kontrol ediliyor…", "Checking apps…"));
            TrayPopupEmptyText.Visibility = Visibility.Collapsed;
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
                if (!_renderedTrayItems.SequenceEqual(items)) RebuildTrayOverflowGrid(items);
                TrayPopupEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            if (!_isClosed) BtnTrayPopupRefresh.IsEnabled = true;
        }
    }

    private void SetTrayStatus(string message, bool isError = false)
    {
        TrayPopupStatusText.Text = message;
        TrayPopupStatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(255, 180, 171))
            : (Brush)FindResource("AccentCyanBrush");
        TrayPopupStatusText.Visibility = Visibility.Visible;
    }

    private async void OnTrayPopupRefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshTrayAppsAsync(showLoading: true);

    private void OnTrayCardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            TrayOverflowPopup.IsOpen = false;
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
    }

    private void RebuildTrayOverflowGrid(IReadOnlyList<BackgroundTrayAppItem> items)
    {
        string lang = _orch.Config.Language;
        string? focusedKey = (Keyboard.FocusedElement as Button)?.Tag as string;
        double scrollOffset = TrayAppsScroller.VerticalOffset;
        _renderedTrayItems = items;
        _lastTrayAppCount = items.Count;
        if (_trayActiveDot is not null) _trayActiveDot.Opacity = items.Count > 0 ? 1.0 : 0.0;

        TrayPopupTitleText.Text = Strings.TrayOverflowHeader(lang);
        TrayPopupEmptyText.Text = Strings.TrayOverflowEmpty(lang);
        TrayPopupCountText.Text = items.Count.ToString(CultureInfo.InvariantCulture);
        BtnTrayPopupRefresh.ToolTip = Strings.Pick(lang, "Yenile (F5)", "Refresh (F5)");
        AutomationProperties.SetName(BtnTrayPopupRefresh, Strings.Pick(lang, "Uygulama listesini yenile", "Refresh the app list"));
        TrayOverflowGrid.Children.Clear();
        TrayPopupEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in items)
        {
            var row = new Button
            {
                Tag = item.Key,
                Style = (Style)FindResource("DarkActionButtonStyle"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Background = new SolidColorBrush(Color.FromRgb(24, 32, 47)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(43, 56, 75)),
                ToolTip = string.IsNullOrWhiteSpace(item.ExePath) ? item.DisplayName : item.ExePath
            };
            AutomationProperties.SetName(row, item.DisplayName);
            AutomationProperties.SetHelpText(row, Strings.TrayOpenApp(lang));

            var content = new Grid { Width = Math.Max(180, TrayOverflowCard.Width - 58) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            FrameworkElement icon = item.Icon is not null
                ? new Image { Source = item.Icon, Width = 28, Height = 28, Stretch = Stretch.Uniform }
                : new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(Color.FromRgb(31, 54, 83)),
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(item.DisplayName) ? "?" : StringInfo.GetNextTextElement(item.DisplayName).ToUpperInvariant(),
                        Foreground = (Brush)FindResource("AccentCyanBrush"), FontSize = 15, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
            content.Children.Add(icon);

            var labels = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = item.DisplayName, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
            });
            labels.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileName(item.ExePath) ?? string.Empty, FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0), Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);
            var arrow = new TextBlock
            {
                Text = "\uE8A7", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12,
                Foreground = (Brush)FindResource("AccentCyanBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(arrow, 2);
            content.Children.Add(arrow);
            row.Content = content;
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
            TrayOverflowGrid.Children.Add(row);
            if (focusedKey == item.Key) row.Focus();
        }
        TrayAppsScroller.ScrollToVerticalOffset(scrollOffset);
    }

    private async void DismissPopupsAndActivateTrayApp(BackgroundTrayAppItem item)
    {
        if (_isSmokeOrAuditMode || _trayActionBusy) return;
        _trayActionBusy = true;
        if (_activeContextMenu is not null) _activeContextMenu.IsOpen = false;
        ClockCalendarPopup.IsOpen = false;
        TrayOverflowPopup.IsOpen = false;
        StartLaunchPopup.IsOpen = false;
        if (Mouse.Captured is not null) Mouse.Capture(null);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            var result = await DockAppsService.ActivateBackgroundTrayAppAsync(item);
            if (!_isClosed && result != TrayAppActionResult.Succeeded)
                ReportTrayActionFailure(item, result);
        }
        finally
        {
            _trayActionBusy = false;
            await RefreshTrayAppsAsync();
        }
    }

    private void ReportTrayActionFailure(BackgroundTrayAppItem item, TrayAppActionResult result)
    {
        string lang = _orch.Config.Language;
        string detail = result == TrayAppActionResult.Stale
            ? Strings.Pick(lang, "Uygulama kapanmış veya yeniden başlamış. Listeyi yenileyip tekrar deneyin.",
                "The app closed or restarted. Refresh the list and try again.")
            : Strings.Pick(lang, "İşlem tamamlanamadı. Uygulamanın yanıt verdiğini kontrol edin.",
                "The action could not be completed. Check that the app is responding.");
        if (TrayOverflowPopup.IsOpen) SetTrayStatus(detail, isError: true);
        else _orch.Notifications.AddInternalAlert("DinamikAda", item.DisplayName, detail);
    }

    private void ShowTrayItemContextMenu(FrameworkElement anchor, BackgroundTrayAppItem item, string lang)
    {
        if (_trayActionBusy) return;
        var menu = new ContextMenu();
        _activeContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu)) _activeContextMenu = null;
            if (Mouse.Captured is not null) Mouse.Capture(null);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (!_isClosed && TrayOverflowPopup.IsOpen && !_trayActionBusy &&
                    !_renderedTrayItems.SequenceEqual(_cachedTrayItems))
                    RebuildTrayOverflowGrid(_cachedTrayItems);
            });
        };

        var openItem = new MenuItem { Header = Strings.TrayOpenApp(lang) };
        openItem.Click += (_, _) => DismissPopupsAndActivateTrayApp(item);
        menu.Items.Add(openItem);

        if (!string.IsNullOrWhiteSpace(item.ExePath))
        {
            var locationItem = new MenuItem { Header = Strings.TrayOpenLocation(lang) };
            locationItem.Click += (_, _) =>
            {
                if (DockAppsService.OpenAppFileLocation(item.ExePath)) TrayOverflowPopup.IsOpen = false;
                else ReportTrayActionFailure(item, TrayAppActionResult.Failed);
            };
            menu.Items.Add(locationItem);
        }

        menu.Items.Add(new Separator());
        var closeItem = new MenuItem
        {
            Header = Strings.Pick(lang, "Pencereyi Kapat", "Close Window"),
            IsEnabled = item.RestoreWindowHwnd != 0
        };
        closeItem.Click += async (_, _) =>
        {
            if (_trayActionBusy || _isSmokeOrAuditMode) return;
            _trayActionBusy = true;
            TrayAppActionResult result;
            try
            {
                result = await Task.Run(() => DockAppsService.RequestCloseTrayWindow(item));
            }
            finally
            {
                _trayActionBusy = false;
                await RefreshTrayAppsAsync();
            }
            if (_isClosed) return;
            if (result == TrayAppActionResult.Succeeded)
                SetTrayStatus(Strings.Pick(lang, "Kapatma isteği uygulamaya iletildi.", "Close request sent to the app."));
            else ReportTrayActionFailure(item, result);
        };
        menu.Items.Add(closeItem);
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Right;
        menu.IsOpen = true;
    }

    private void AddTrashSlot(string lang, int iconSize, double tileW, double tileH)
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

        var plate = new Border
        {
            Width = iconSize,
            Height = iconSize,
            CornerRadius = new CornerRadius(Math.Round(iconSize * 0.26)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent
        };

        _trashIconText = new TextBlock
        {
            Text = "\uE74D",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = Math.Round(iconSize * 0.54),
            Foreground = DockGlyph(Color.FromRgb(212, 212, 216)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        plate.Child = _trashIconText;
        container.Children.Add(plate);

        _trashDot = new Ellipse
        {
            Width = 3.5,
            Height = 3.5,
            Fill = new SolidColorBrush(Color.FromRgb(255, 159, 10)),
            Opacity = _lastRecycleBin.HasItems ? 1.0 : 0.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0.5),
            IsHitTestVisible = false
        };
        container.Children.Add(_trashDot);

        var slotVisual = new DockSlotVisual
        {
            Container = container,
            Scale = scale,
            Translate = translate,
            IconPlate = plate,
            RunningDot = _trashDot,
            RunningPill = null,
            TooltipLabel = Strings.DockTrashLabel(lang),
            Kind = SlotKind.Trash
        };

        // Trash remains completely stationary (no magnification, shift, or click bounce)
        container.MouseEnter += (_, _) =>
            plate.Background = DockTint(0x16);
        container.MouseLeave += (_, _) =>
            plate.Background = Brushes.Transparent;
        container.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            DockAppsService.OpenRecycleBin();
        };
        container.MouseRightButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            HandleSlotContextMenu(slotVisual);
        };

        _slots.Add(slotVisual);
        DockItemsHost.Children.Add(container);
    }

    private void AddVerticalDivider(int iconSize, double leftMargin = 4.0, double rightMargin = 4.0)
    {
        int divH = Math.Max(16, iconSize - 12);
        DockItemsHost.Children.Add(new Border
        {
            Width = 1,
            Height = divH,
            Margin = new Thickness(leftMargin, Math.Round((iconSize - divH) * 0.5), rightMargin, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = DockTint(0x22),
            IsHitTestVisible = false
        });
    }

    private void AddClockChip(int iconSize)
    {
        var clockBtn = new Border
        {
            Height = iconSize,
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(9),
            Background = DockTint(0x0E),
            BorderBrush = DockTint(0x16),
            BorderThickness = new Thickness(0.6),
            VerticalAlignment = VerticalAlignment.Top
        };

        _clockTimeText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 11.2,
            FontWeight = FontWeights.SemiBold,
            Foreground = DockTextBrush(primary: true),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _clockDateText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiTextFont"),
            FontSize = 8.8,
            Foreground = DockTextBrush(primary: false),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_clockTimeText);
        stack.Children.Add(_clockDateText);
        clockBtn.Child = stack;

        clockBtn.MouseEnter += (_, _) =>
        {
            clockBtn.Background = DockTint(0x18);
            clockBtn.BorderBrush = DockTint(0x28);
        };
        clockBtn.MouseLeave += (_, _) =>
        {
            clockBtn.Background = DockTint(0x0E);
            clockBtn.BorderBrush = DockTint(0x16);
        };
        clockBtn.PreviewMouseLeftButtonDown += (_, ev) =>
        {
            if (ClockCalendarPopup.IsOpen)
            {
                ClockCalendarPopup.IsOpen = false;
                _suppressNextClockOpenOnUp = true;
                ev.Handled = true;
            }
            else
            {
                _suppressNextClockOpenOnUp = false;
            }
        };
        clockBtn.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            if (_suppressNextClockOpenOnUp)
            {
                _suppressNextClockOpenOnUp = false;
                return;
            }
            ToggleClockPopup();
        };

        _clockChip = clockBtn;
        _lastClockMinute = string.Empty;
        UpdateClockText();
        DockItemsHost.Children.Add(clockBtn);
    }

    private void ToggleClockPopup()
    {
        if (_clockChip is null) return;
        StartLaunchPopup.IsOpen = false;
        TrayOverflowPopup.IsOpen = false;
        if (ClockCalendarPopup.IsOpen)
        {
            ClockCalendarPopup.IsOpen = false;
            return;
        }

        double sinceClose = (Stopwatch.GetTimestamp() - _lastPopupClosedTicks) / (double)Stopwatch.Frequency;
        if (_lastPopupClosedTicks != 0 && sinceClose < 0.22)
        {
            return;
        }

        ClockCalendarPopup.PlacementTarget = _clockChip;
        ClockCalendarPopup.IsOpen = true;
    }

    private void OnClockPopupOpened(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        _popupRenderedToday = DateTime.Today;
        _popupDisplayMonth = new DateTime(_popupRenderedToday.Year, _popupRenderedToday.Month, 1);
        UpdatePopupLiveClockAndFooter();
        RebuildPopupCalendarGrid();
        SyncCoordinatorState(_orch.Coordinator.Current);
        if (!_isSmokeOrAuditMode)
        {
            _popupClockTimer.Start();
            StartPopupLightDismissWatcher();
        }
    }

    private void OnClockPopupClosed(object? sender, EventArgs e)
    {
        _popupClockTimer.Stop();
        _lastPopupClosedTicks = Stopwatch.GetTimestamp();
        StopPopupLightDismissWatcherIfIdle();
        ScheduleIntellihideEval();
    }

    private void OnPopupPrevMonthClick(object sender, RoutedEventArgs e)
    {
        _popupDisplayMonth = _popupDisplayMonth.AddMonths(-1);
        RebuildPopupCalendarGrid();
    }

    private void OnPopupNextMonthClick(object sender, RoutedEventArgs e)
    {
        _popupDisplayMonth = _popupDisplayMonth.AddMonths(1);
        RebuildPopupCalendarGrid();
    }

    private void OnPopupTodayClick(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        _popupRenderedToday = today;
        _popupDisplayMonth = new DateTime(today.Year, today.Month, 1);
        RebuildPopupCalendarGrid();
    }

    private void OnPopupFooterClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ClockCalendarPopup.IsOpen = false;
        _orch.Coordinator.RequestExpand(ExpandedTabId.Now);
    }

    private void UpdatePopupLiveClockAndFooter()
    {
        var now = DateTime.Now;
        var culture = GetActiveUiCulture();

        PopupClockHm.Text = now.ToString("HH:mm", culture);
        PopupClockSec.Text = now.ToString(":ss", culture);

        string weekday = culture.DateTimeFormat.GetDayName(now.DayOfWeek);
        PopupDateWeekday.Text = string.IsNullOrEmpty(weekday)
            ? weekday
            : char.ToUpper(weekday[0], culture) + weekday[1..];
        PopupDateFull.Text = now.ToString("dd MMMM yyyy", culture);

        if (!_isSmokeOrAuditMode && now.Date != _popupRenderedToday)
        {
            _popupRenderedToday = now.Date;
            RebuildPopupCalendarGrid();
        }

        bool isEn = string.Equals(_orch.Config.Language, "en", StringComparison.OrdinalIgnoreCase);
        var meeting = _orch.Calendar.ActiveOrNextMeeting;
        if (meeting is not null)
        {
            var startLocal = meeting.StartUtc.ToLocalTime();
            bool isLive = meeting.StartUtc <= DateTimeOffset.UtcNow && meeting.EndUtc > DateTimeOffset.UtcNow;
            PopupMeetingDot.Fill = new SolidColorBrush(isLive
                ? Color.FromRgb(48, 209, 88)
                : Color.FromRgb(10, 132, 255));
            PopupFooterText.Text = isLive
                ? $"{(isEn ? "Now" : "Şimdi")} · {meeting.Summary}"
                : $"{startLocal:HH:mm} · {meeting.Summary}";
        }
        else
        {
            PopupMeetingDot.Fill = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
            PopupFooterText.Text = isEn ? "No events scheduled today" : "Bugün planlı etkinlik yok";
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

        if (PopupWeekdayGrid.Children.Count == 0 || !Equals(PopupWeekdayGrid.Tag, _orch.Config.Language))
        {
            PopupWeekdayGrid.Tag = _orch.Config.Language;
            PopupWeekdayGrid.Children.Clear();
            string[] weekdays = isEn
                ? ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"]
                : ["Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz"];
            var weekdayBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xF5, 0xF5, 0xF7));
            foreach (string wd in weekdays)
            {
                PopupWeekdayGrid.Children.Add(new TextBlock
                {
                    Text = wd,
                    FontFamily = (FontFamily)FindResource("UiFont"),
                    FontSize = 9.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = weekdayBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
        }

        PopupDaysGrid.Children.Clear();
        var firstOfMonth = new DateTime(_popupDisplayMonth.Year, _popupDisplayMonth.Month, 1);
        int mondayOffset = ((int)firstOfMonth.DayOfWeek + 6) % 7; // Mon=0 .. Sun=6
        DateTime gridStart = firstOfMonth.AddDays(-mondayOffset);

        var todayBg = new SolidColorBrush(Color.FromRgb(10, 132, 255));
        var hoverBg = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        var normalFg = new SolidColorBrush(Color.FromRgb(245, 245, 247));
        var weekendFg = new SolidColorBrush(Color.FromArgb(0xB8, 0xF5, 0xF5, 0xF7));
        var outsideFg = new SolidColorBrush(Color.FromArgb(0x3E, 0xF5, 0xF5, 0xF7));

        for (int i = 0; i < 42; i++)
        {
            DateTime cellDate = gridStart.AddDays(i);
            bool inMonth = cellDate.Month == _popupDisplayMonth.Month && cellDate.Year == _popupDisplayMonth.Year;
            bool isToday = cellDate.Date == today.Date;
            bool isWeekend = cellDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            var cell = new Border
            {
                Height = 25,
                Margin = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Background = isToday ? todayBg : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = cellDate.Day.ToString(CultureInfo.InvariantCulture),
                    FontFamily = (FontFamily)FindResource("UiFont"),
                    FontSize = 10.5,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Medium,
                    Foreground = isToday
                        ? Brushes.White
                        : !inMonth
                            ? outsideFg
                            : isWeekend
                                ? weekendFg
                                : normalFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            if (!isToday && inMonth)
            {
                cell.MouseEnter += (_, _) => cell.Background = hoverBg;
                cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
            }

            PopupDaysGrid.Children.Add(cell);
        }
    }

    private CultureInfo GetActiveUiCulture() =>
        CultureInfo.GetCultureInfo(string.Equals(_orch.Config.Language, "en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : "tr-TR");

    private void UpdateClockText()
    {
        if (_clockTimeText is null || _clockDateText is null) return;
        var now = DateTime.Now;
        string hhmm = _orch.Config.DockClock24h
            ? now.ToString("HH:mm", CultureInfo.InvariantCulture)
            : now.ToString("h:mm", CultureInfo.InvariantCulture) + " " + DockWidgetMath.AmPm(now);
        if (string.Equals(hhmm, _lastClockMinute, StringComparison.Ordinal)) return;
        _lastClockMinute = hhmm;
        _clockTimeText.Text = hhmm;
        _clockDateText.Text = now.ToString("dd MMM");
    }

    private void HandleSlotPrimaryClick(DockSlotVisual slot)
    {
        switch (slot.Kind)
        {
            case SlotKind.StartMenu:
                ToggleStartPopup();
                break;

            case SlotKind.App when slot.AppItem is { } app:
                StartLaunchPopup.IsOpen = false;
                TrayOverflowPopup.IsOpen = false;
                ClockCalendarPopup.IsOpen = false;
                if (!app.IsRunning)
                {
                    TriggerBounce(slot);
                    DockAppsService.LaunchItem(app);
                    break;
                }

                if (app.Windows.Count == 1)
                {
                    DockAppsService.FocusOrToggleWindow(app.Windows[0], _lastForegroundHwnd);
                    break;
                }

                // Multi-window app: cycle cleanly if closed or open selector menu
                var nextWin = app.Windows.FirstOrDefault(w => w.Hwnd != _lastForegroundHwnd) ?? app.Windows[0];
                DockAppsService.FocusOrToggleWindow(nextWin, _lastForegroundHwnd);
                break;
        }
    }

    private void HandleSlotContextMenu(DockSlotVisual? slot)
    {
        string lang = _orch.Config.Language;
        var menu = new ContextMenu();
        _activeContextMenu = menu;

        if (slot?.Kind == SlotKind.StartMenu)
        {
            var startItem = new MenuItem { Header = Strings.DockStartLabel(lang) };
            startItem.Click += (_, _) => ToggleStartPopup();
            var cmdItem = new MenuItem { Header = Strings.CommandPalette(lang) };
            cmdItem.Click += (_, _) => _orch.Coordinator.OpenCommandPalette();
            var winStartItem = new MenuItem { Header = Strings.StartMenuClassicWin(lang) };
            winStartItem.Click += (_, _) => WindowsTaskbarManager.TriggerStartMenu();
            menu.Items.Add(startItem);
            menu.Items.Add(cmdItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(winStartItem);
        }
        else if (slot?.Kind == SlotKind.App && slot.AppItem is { } app)
        {
            foreach (var win in app.Windows.Take(6))
            {
                var wItem = new MenuItem { Header = string.IsNullOrWhiteSpace(win.Title) ? app.DisplayName : win.Title };
                var targetHwnd = win.Hwnd;
                wItem.Click += (_, _) => DockAppsService.FocusWindow(targetHwnd);
                menu.Items.Add(wItem);
            }

            if (app.Windows.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            var newInst = new MenuItem { Header = Strings.DockNewWindow(lang) };
            newInst.Click += (_, _) => DockAppsService.LaunchItem(app);
            menu.Items.Add(newInst);

            if (!string.IsNullOrWhiteSpace(app.LaunchTarget))
            {
                var pinItem = new MenuItem { Header = app.IsPinned ? Strings.DockUnpinApp(lang) : Strings.DockPinApp(lang) };
                pinItem.Click += (_, _) =>
                {
                    _appsService.TogglePin(app);
                    RefreshDockItemsNow();
                };
                menu.Items.Add(pinItem);
            }

            if (app.Windows.Count > 0)
            {
                var closeAll = new MenuItem { Header = Strings.DockCloseWindow(lang) };
                closeAll.Click += (_, _) =>
                {
                    foreach (var w in app.Windows)
                    {
                        DockAppsService.CloseWindow(w.Hwnd);
                    }
                };
                menu.Items.Add(closeAll);
            }
        }
        else if (slot?.Kind == SlotKind.IslandSync)
        {
            var toggleHub = new MenuItem { Header = Strings.OpenHub(lang) };
            toggleHub.Click += (_, _) => _orch.Coordinator.RequestExpand();
            var aiQuotaItem = new MenuItem { Header = Strings.Pick(lang, "AI Kota & Aboneliklerim", "AI Quota & Subscriptions") };
            aiQuotaItem.Click += (_, _) => _orch.Coordinator.RequestExpand(ExpandedTabId.AiQuota);
            var cmdPal = new MenuItem { Header = Strings.CommandPalette(lang) };
            cmdPal.Click += (_, _) => _orch.Coordinator.OpenCommandPalette();
            menu.Items.Add(toggleHub);
            menu.Items.Add(aiQuotaItem);
            menu.Items.Add(cmdPal);
        }
        else if (slot?.Kind == SlotKind.TrayOverflow)
        {
            var openTrayItem = new MenuItem { Header = Strings.DockTrayAppsLabel(lang) };
            openTrayItem.Click += (_, _) => ToggleTrayPopup();
            menu.Items.Add(openTrayItem);
        }
        else if (slot?.Kind == SlotKind.Trash)
        {
            var openRb = new MenuItem { Header = Strings.DockOpenTrash(lang) };
            openRb.Click += (_, _) => DockAppsService.OpenRecycleBin();
            var emptyRb = new MenuItem { Header = Strings.DockEmptyTrash(lang) };
            emptyRb.Click += (_, _) => DockAppsService.EmptyRecycleBin(_hwnd);
            menu.Items.Add(openRb);
            menu.Items.Add(emptyRb);
        }

        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        AppendWidgetAndStyleMenuItems(menu, slot);

        menu.Items.Add(new Separator());
        var toggleIntellihide = new MenuItem
        {
            Header = _orch.Config.DockIntellihideEnabled
                ? Strings.DockIntellihideDisableMenu(lang)
                : Strings.DockIntellihideEnableMenu(lang)
        };
        toggleIntellihide.Click += (_, _) =>
        {
            _orch.Config.DockIntellihideEnabled = !_orch.Config.DockIntellihideEnabled;
            _orch.SaveConfig();
            ApplyConfigAndSync();
        };
        menu.Items.Add(toggleIntellihide);

        var toggleWorkArea = new MenuItem
        {
            Header = _orch.Config.DockReserveWorkArea
                ? Strings.DockFloatOverAppsMenu(lang)
                : Strings.DockReserveStripMenu(lang)
        };
        toggleWorkArea.Click += (_, _) =>
        {
            _orch.Config.DockReserveWorkArea = !_orch.Config.DockReserveWorkArea;
            _orch.SaveConfig();
            ApplyConfigAndSync();
        };
        menu.Items.Add(toggleWorkArea);

        var toggleTaskbar = new MenuItem
        {
            Header = _orch.Config.HideWindowsTaskbar
                ? Strings.RestoreWindowsTaskbar(lang)
                : Strings.HideWindowsTaskbarMenu(lang)
        };
        toggleTaskbar.Click += (_, _) =>
        {
            _orch.Config.HideWindowsTaskbar = !_orch.Config.HideWindowsTaskbar;
            _orch.SaveConfig();
            ApplyConfigAndSync();
        };
        menu.Items.Add(toggleTaskbar);

        var settingsItem = new MenuItem { Header = Strings.Settings(lang) };
        settingsItem.Click += (_, _) => _orch.RequestOpenSettings();
        menu.Items.Add(settingsItem);

        menu.Closed += (_, _) => ScheduleIntellihideEval();
        menu.PlacementTarget = slot?.Container ?? (UIElement)DockPillContainer;
        menu.Placement = IsDockAtTop ? PlacementMode.Bottom : PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void RecomputeSlotCenters()
    {
        if (_slots.Count == 0)
        {
            _slotCentersX = [];
            _appSlotCentersX = [];
            _appSlotIndices = [];
            _appsMinX = 0.0;
            _appsMaxX = 0.0;
            return;
        }

        DockItemsHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double accumX = 0.0;
        var centers = new double[_slots.Count];
        var appCenters = new List<double>(_slots.Count);
        var appIndices = new List<int>(_slots.Count);
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        int slotIdx = 0;

        foreach (UIElement child in DockItemsHost.Children)
        {
            double w = child.DesiredSize.Width;
            if (child is Grid g && slotIdx < _slots.Count && ReferenceEquals(g, _slots[slotIdx].Container))
            {
                double cx = accumX + w * 0.5;
                centers[slotIdx] = cx;
                if (_slots[slotIdx].Kind == SlotKind.App)
                {
                    appCenters.Add(cx);
                    appIndices.Add(slotIdx);
                    if (accumX < minX) minX = accumX;
                    if (accumX + w > maxX) maxX = accumX + w;
                }
                slotIdx++;
            }
            accumX += w;
        }

        _slotCentersX = centers;
        _appSlotCentersX = [.. appCenters];
        _appSlotIndices = [.. appIndices];
        _appsMinX = appIndices.Count > 0 ? minX : 0.0;
        _appsMaxX = appIndices.Count > 0 ? maxX : 0.0;
    }

    private void OnDockMouseEnter(object sender, MouseEventArgs e)
    {
        _isHovered = true;
        _hoverLocalX = e.GetPosition(DockItemsHost).X;
        UpdateMagnificationTargets(_hoverLocalX);
        StartRenderLoop();
    }

    private void OnDockMouseMove(object sender, MouseEventArgs e)
    {
        if (_isEdgeDragPending)
        {
            OnDockPillEdgeDragMove(sender, e);
            return;
        }

        if (_pressedPinnedSlot is not null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                if (_isReorderDragging) EndReorderDrag(commit: false);
                else _pressedPinnedSlot = null;
            }
            else
            {
                double x = e.GetPosition(DockItemsHost).X;
                if (!_isReorderDragging && Math.Abs(x - _pressStartPoint.X) >= ReorderDragThresholdDip)
                {
                    BeginReorderDrag();
                }

                if (_isReorderDragging)
                {
                    UpdateReorderDrag(x);
                    e.Handled = true;
                    return;
                }
            }
        }

        _isHovered = true;
        _hoverLocalX = e.GetPosition(DockItemsHost).X;
        UpdateMagnificationTargets(_hoverLocalX);
        StartRenderLoop();
    }

    // ── Pinned slot drag-to-reorder ─────────────────────────────────────────

    private List<DockSlotVisual> GetPinnedSlots()
        => _slots.Where(sl => sl.Kind == SlotKind.App && sl.AppItem is { IsPinned: true }).ToList();

    /// <summary>Slots a drag can reorder among: other widgets for a widget, other pins for a pinned app.</summary>
    private List<DockSlotVisual> GetReorderGroup(DockSlotVisual dragged)
        => dragged.Kind == SlotKind.Widget
            ? _slots.Where(sl => sl.Kind == SlotKind.Widget).ToList()
            : GetPinnedSlots();

    private double ReorderPitch(DockSlotVisual dragged, double[] centers)
        => dragged.Kind == SlotKind.Widget
            ? dragged.Container.Width + dragged.Container.Margin.Left + dragged.Container.Margin.Right
            : PinnedPitch(centers);

    private double[] GetPinnedSlotCenters(List<DockSlotVisual> pinned)
    {
        var centers = new double[pinned.Count];
        for (int i = 0; i < pinned.Count; i++)
        {
            int idx = _slots.IndexOf(pinned[i]);
            centers[i] = idx >= 0 && idx < _slotCentersX.Length ? _slotCentersX[idx] : 0.0;
        }
        return centers;
    }

    private double PinnedPitch(double[] centers)
        => centers.Length > 1
            ? Math.Max(1.0, centers[1] - centers[0])
            : Math.Clamp(_orch.Config.DockIconSizeDip, 26, 48) + 6.0;

    private void BeginReorderDrag()
    {
        var slot = _pressedPinnedSlot;
        if (slot is null) return;

        _isReorderDragging = true;
        _reorderTargetIndex = -1;
        DockTooltipBorder.Opacity = 0;
        _hoveredSlotIndex = -1;
        UpdateMagnificationTargets(null);
        slot.Container.Opacity = 0.88;
        Panel.SetZIndex(slot.Container, 100);
        slot.Container.CaptureMouse();
        StartRenderLoop();
    }

    private void UpdateReorderDrag(double cursorX)
    {
        var dragged = _pressedPinnedSlot;
        if (dragged is null) return;

        var pinned = GetReorderGroup(dragged);
        int from = pinned.IndexOf(dragged);
        if (from < 0) return;

        double[] centers = GetPinnedSlotCenters(pinned);
        double pitch = ReorderPitch(dragged, centers);
        int target = DockLayoutCalculator.ComputeReorderTargetIndex(centers, cursorX, from);
        _reorderTargetIndex = target;

        for (int i = 0; i < pinned.Count; i++)
        {
            if (i == from) continue;
            pinned[i].ReorderShiftX = DockLayoutCalculator.ComputeReorderShift(i, from, target, pitch);
        }

        double minOffset = centers[0] - centers[from] - pitch * 0.5;
        double maxOffset = centers[^1] - centers[from] + pitch * 0.5;
        double offset = Math.Clamp(cursorX - _pressStartPoint.X, minOffset, maxOffset);
        dragged.ReorderShiftX = offset;
        dragged.CurrentReorderShiftX = offset;
        StartRenderLoop();
    }

    private void EndReorderDrag(bool commit)
    {
        if (!_isReorderDragging) return;
        _isReorderDragging = false;

        var slot = _pressedPinnedSlot;
        _pressedPinnedSlot = null;
        int target = _reorderTargetIndex;
        _reorderTargetIndex = -1;

        int from = -1;
        if (slot is not null)
        {
            from = GetReorderGroup(slot).IndexOf(slot);
            slot.Container.Opacity = 1.0;
            Panel.SetZIndex(slot.Container, 0);
            if (slot.Container.IsMouseCaptured) slot.Container.ReleaseMouseCapture();
        }

        bool validMove = commit && from >= 0 && target >= 0 && target != from;
        bool movedWidget = validMove && slot is { Kind: SlotKind.Widget, WidgetId: { } wid } && MoveDockWidget(wid, target);
        bool moved = movedWidget || (validMove && slot?.AppItem is { } app && slot.Kind == SlotKind.App
            && _appsService.MovePinned(app.Key, target));

        if (moved)
        {
            // Slots are rebuilt in the new order, so clear offsets without a snap-back animation.
            foreach (var sl in _slots) { sl.ReorderShiftX = 0.0; sl.CurrentReorderShiftX = 0.0; }
            if (movedWidget) RebuildVisualSlots(force: true);
            else RefreshDockItemsNow();
        }
        else
        {
            foreach (var sl in _slots) sl.ReorderShiftX = 0.0;
        }

        if (_isHovered && _hoverLocalX.HasValue) UpdateMagnificationTargets(_hoverLocalX);
        StartRenderLoop();
    }

    // ── Drop files / shortcuts / folders from Explorer to pin ───────────────

    private static List<string> ExtractPinnableDropPaths(IDataObject data)
    {
        var result = new List<string>();
        if (!data.GetDataPresent(DataFormats.FileDrop)) return result;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths) return result;
        foreach (string path in paths.Take(16))
        {
            if (DockAppsService.IsPinnableTarget(path)) result.Add(path);
        }
        return result;
    }

    private int ComputeExternalDropIndex(double cursorX, bool applyPreview)
    {
        var pinned = GetPinnedSlots();
        if (pinned.Count == 0) return 0;

        double[] centers = GetPinnedSlotCenters(pinned);
        int target = DockLayoutCalculator.ComputeReorderTargetIndex(centers, cursorX, -1);
        if (applyPreview)
        {
            double pitch = PinnedPitch(centers);
            for (int i = 0; i < pinned.Count; i++)
            {
                pinned[i].ReorderShiftX = DockLayoutCalculator.ComputeReorderShift(i, -1, target, pitch);
            }
            _isExternalDropPreview = true;
            StartRenderLoop();
        }
        return target;
    }

    private void ClearExternalDropPreview()
    {
        if (!_isExternalDropPreview) return;
        _isExternalDropPreview = false;
        foreach (var sl in _slots) sl.ReorderShiftX = 0.0;
        StartRenderLoop();
    }

    private void OnDockDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_isReorderDragging || ExtractPinnableDropPaths(e.Data).Count == 0)
        {
            e.Effects = DragDropEffects.None;
            ClearExternalDropPreview();
            return;
        }

        e.Effects = DragDropEffects.Link;
        DockTooltipBorder.Opacity = 0;
        ComputeExternalDropIndex(e.GetPosition(DockItemsHost).X, applyPreview: true);
    }

    private void OnDockDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when crossing child elements; only clear when truly outside.
        var pos = e.GetPosition(DockPillContainer);
        var size = DockPillContainer.RenderSize;
        if (pos.X < 0 || pos.Y < 0 || pos.X >= size.Width || pos.Y >= size.Height)
        {
            ClearExternalDropPreview();
        }
    }

    private void OnDockDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = ExtractPinnableDropPaths(e.Data);
        int insertAt = ComputeExternalDropIndex(e.GetPosition(DockItemsHost).X, applyPreview: false);
        ClearExternalDropPreview();
        if (paths.Count == 0) return;

        foreach (var sl in _slots) { sl.ReorderShiftX = 0.0; sl.CurrentReorderShiftX = 0.0; }
        foreach (string path in paths)
        {
            if (_appsService.PinPath(path, insertAt)) insertAt++;
        }
        RefreshDockItemsNow();
    }

    private void OnDockMouseLeave(object sender, MouseEventArgs e)
    {
        _isHovered = false;
        _hoverLocalX = null;
        _hoveredSlotIndex = -1;
        _lastTooltipSlotIndex = -1;
        DockTooltipBorder.Opacity = 0;
        UpdateMagnificationTargets(null);
        StartRenderLoop();
    }

    private void UpdateMagnificationTargets(double? cursorLocalX)
    {
        int iconSize = Math.Clamp(_orch.Config.DockIconSizeDip, 26, 48);
        double pitch = iconSize + 6.0;
        bool enableMag = _orch.Config.DockMagnificationEnabled;

        // Lock outer dock elements (Start Menu, Island Sync, Trash) strictly stationary
        foreach (var slot in _slots)
        {
            slot.TargetScale = 1.0;
            slot.TargetShiftX = 0.0;
            slot.TargetLiftY = 0.0;
        }

        double? appCursorX = enableMag
            && cursorLocalX.HasValue
            && _appSlotIndices.Length > 0
            && cursorLocalX.Value >= _appsMinX - 8.0
            && cursorLocalX.Value <= _appsMaxX + 8.0
                ? cursorLocalX
                : null;

        if (_appSlotCentersX.Length > 0)
        {
            var appTransforms = DockLayoutCalculator.ComputeMagnification(
                appCursorX,
                _appSlotCentersX,
                pitch,
                maxScale: enableMag ? MagnifyMaxScale : 1.0,
                influenceRadiusDip: MagnifyRadiusDip,
                liftFactor: 0.34,
                spreadFactor: 0.30);

            for (int k = 0; k < _appSlotIndices.Length && k < appTransforms.Length; k++)
            {
                int slotIndex = _appSlotIndices[k];
                _slots[slotIndex].TargetScale = appTransforms[k].Scale;
                _slots[slotIndex].TargetShiftX = appTransforms[k].ShiftXDip;
                _slots[slotIndex].TargetLiftY = appTransforms[k].LiftYDip;
            }
        }

        int closestIdx = -1;
        double closestDist = double.MaxValue;
        if (cursorLocalX.HasValue)
        {
            for (int i = 0; i < _slots.Count && i < _slotCentersX.Length; i++)
            {
                double hitHalfWidth = (_slots[i].Container.Width * 0.5) + 3.0;
                double dist = Math.Abs(cursorLocalX.Value - _slotCentersX[i]);
                if (dist <= hitHalfWidth && dist < closestDist)
                {
                    closestDist = dist;
                    closestIdx = i;
                }
            }
        }

        UpdateHoverTooltip(closestIdx);
    }

    private void UpdateHoverTooltip(int slotIdx)
    {
        if (slotIdx < 0 || slotIdx >= _slots.Count || !_isHovered)
        {
            _hoveredSlotIndex = -1;
            DockTooltipBorder.Opacity = 0;
            return;
        }

        var slot = _slots[slotIdx];
        if (!string.Equals(_lastTooltipTitle, slot.TooltipLabel, StringComparison.Ordinal))
        {
            _lastTooltipTitle = slot.TooltipLabel;
            DockTooltipText.Text = slot.TooltipLabel;
        }

        if (!DockPillContainer.IsLoaded || !slot.Container.IsDescendantOf(DockRootGrid))
        {
            DockTooltipBorder.Opacity = 0;
            return;
        }

        DockTooltipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tipW = DockTooltipBorder.DesiredSize.Width;

        Point anchorPt = slot.Container.TranslatePoint(new Point(slot.Container.Width * 0.5, 0), DockRootGrid);
        double x = Math.Clamp(anchorPt.X - tipW * 0.5, 8.0, Math.Max(8.0, Width - tipW - 8.0));
        double extraLift = Math.Abs(slot.CurrentLiftY) + (slot.CurrentScale - 1.0) * _orch.Config.DockIconSizeDip * 0.85;
        double y = IsDockAtTop
            ? anchorPt.Y + slot.Container.ActualHeight + 6.0 + extraLift
            : Math.Max(4.0, anchorPt.Y - 30.0 - extraLift);

        DockTooltipTransform.X = x;
        DockTooltipTransform.Y = y;
        DockTooltipBorder.Opacity = 1.0;
        _hoveredSlotIndex = slotIdx;
    }

    private void TriggerBounce(DockSlotVisual slot)
    {
        slot.BounceStartTicks = Stopwatch.GetTimestamp();
        StartRenderLoop();
    }

    private double _dockGlowPhase;

    private bool ShouldAnimateDockAmbientGlow()
    {
        if (_isClosed || _isSmokeOrAuditMode || !_orch.Config.UseCustomBottomDock || _isFullscreenHidden || _isIntellihideTucked)
        {
            return false;
        }

        if (!_orch.Config.DynamicAlbumGlowEnabled)
        {
            return false;
        }

        var media = _orch.Media.Current;
        return media.HasSession && media.IsPlaying;
    }

    private void UpdateDockAmbientPaletteAndState(Color primary, Color secondary, Color highlight)
    {
        var media = _orch.Media.Current;
        bool glowAllowed = _orch.Config.DynamicAlbumGlowEnabled;
        bool hasMediaSession = media.HasSession;

        if (!glowAllowed)
        {
            DockAuraStop0.Color = Color.FromArgb(0x0E, 10, 132, 255);
            DockAuraStop1.Color = Color.FromArgb(0x08, 94, 92, 230);
            DockAuraStop2.Color = Color.FromArgb(0x0C, 100, 210, 255);
            DockAuraOrbPrimaryBorder.Opacity = 0.0;
            DockAuraOrbSecondaryBorder.Opacity = 0.0;
            DockAuraOrbHighlightBorder.Opacity = 0.0;
            DockBorderStop0.Color = DockNeutralBorderColor();
            DockBorderStop1.Color = DockNeutralBorderColor();
            DockBorderStop2.Color = DockNeutralBorderColor();
            return;
        }

        byte washA = hasMediaSession ? (media.IsPlaying ? (byte)0x32 : (byte)0x22) : (byte)0x18;
        byte washB = hasMediaSession ? (media.IsPlaying ? (byte)0x28 : (byte)0x18) : (byte)0x12;
        byte washC = hasMediaSession ? (media.IsPlaying ? (byte)0x2E : (byte)0x1E) : (byte)0x16;
        DockAuraStop0.Color = Color.FromArgb(washA, primary.R, primary.G, primary.B);
        DockAuraStop1.Color = Color.FromArgb(washB, secondary.R, secondary.G, secondary.B);
        DockAuraStop2.Color = Color.FromArgb(washC, highlight.R, highlight.G, highlight.B);

        DockOrbPrim0.Color = Color.FromArgb(0x70, primary.R, primary.G, primary.B);
        DockOrbPrim1.Color = Color.FromArgb(0x38, primary.R, primary.G, primary.B);
        DockOrbSec0.Color = Color.FromArgb(0x64, secondary.R, secondary.G, secondary.B);
        DockOrbSec1.Color = Color.FromArgb(0x30, secondary.R, secondary.G, secondary.B);
        DockOrbHi0.Color = Color.FromArgb(0x58, highlight.R, highlight.G, highlight.B);
        DockOrbHi1.Color = Color.FromArgb(0x2A, highlight.R, highlight.G, highlight.B);

        if (hasMediaSession)
        {
            byte edgeA = media.IsPlaying ? (byte)0x6C : (byte)0x42;
            byte edgeB = media.IsPlaying ? (byte)0x5C : (byte)0x38;
            DockBorderStop0.Color = Color.FromArgb(edgeA, primary.R, primary.G, primary.B);
            DockBorderStop1.Color = Color.FromArgb(edgeB, highlight.R, highlight.G, highlight.B);
            DockBorderStop2.Color = Color.FromArgb(edgeA, secondary.R, secondary.G, secondary.B);
        }
        else
        {
            DockBorderStop0.Color = DockNeutralBorderColor();
            DockBorderStop1.Color = DockNeutralBorderColor();
            DockBorderStop2.Color = DockNeutralBorderColor();
        }

        if (_isSmokeOrAuditMode)
        {
            _dockGlowPhase = 0.95;
            ApplyDockAmbientGlowFrame(dt: 0.0, activeMedia: true);
        }
        else if (ShouldAnimateDockAmbientGlow())
        {
            ApplyDockAmbientGlowFrame(dt: 0.0, activeMedia: true);
            StartRenderLoop();
        }
        else
        {
            ApplyDockAmbientGlowFrame(dt: 0.0, activeMedia: hasMediaSession);
        }
    }

    private void ApplyDockAmbientGlowFrame(double dt, bool activeMedia)
    {
        if (!_orch.Config.DynamicAlbumGlowEnabled || !activeMedia)
        {
            DockAuraOrbPrimaryBorder.Opacity = 0.0;
            DockAuraOrbSecondaryBorder.Opacity = 0.0;
            DockAuraOrbHighlightBorder.Opacity = 0.0;
            return;
        }

        if (dt > 0.0 && _orch.Media.Current.IsPlaying)
        {
            _dockGlowPhase += dt * 0.62;
            if (_dockGlowPhase > Math.PI * 200.0)
            {
                _dockGlowPhase -= Math.PI * 200.0;
            }
        }

        double orbOpacity = _isSmokeOrAuditMode || _orch.Media.Current.IsPlaying ? 0.86 : 0.34;
        DockAuraOrbPrimaryBorder.Opacity = orbOpacity;
        DockAuraOrbSecondaryBorder.Opacity = orbOpacity * 0.92;
        DockAuraOrbHighlightBorder.Opacity = orbOpacity * 0.84;

        var (p1x, p1y) = DockLayoutCalculator.ComputeDockAuraOrbitPoint(_dockGlowPhase, 0.82, 1.05, 0.0, 0.36, 0.24);
        var p1 = new Point(p1x, p1y);
        DockAuraPrimaryBrush.Center = p1;
        DockAuraPrimaryBrush.GradientOrigin = p1;

        var (p2x, p2y) = DockLayoutCalculator.ComputeDockAuraOrbitPoint(_dockGlowPhase, 1.07, 0.88, 2.094, 0.38, 0.24);
        var p2 = new Point(p2x, p2y);
        DockAuraSecondaryBrush.Center = p2;
        DockAuraSecondaryBrush.GradientOrigin = p2;

        var (p3x, p3y) = DockLayoutCalculator.ComputeDockAuraOrbitPoint(_dockGlowPhase, 1.29, 1.15, 4.188, 0.35, 0.26);
        var p3 = new Point(p3x, p3y);
        DockAuraHighlightBrush.Center = p3;
        DockAuraHighlightBrush.GradientOrigin = p3;

        double sweep = Math.Sin(_dockGlowPhase * 0.75) * 0.25;
        DockAuraWashBrush.StartPoint = new Point(Math.Clamp(0.0 + sweep, 0.0, 0.45), 0.15);
        DockAuraWashBrush.EndPoint = new Point(Math.Clamp(1.0 + sweep, 0.55, 1.0), 0.85);
        DockBorderGlowBrush.StartPoint = new Point(Math.Clamp(0.0 + sweep * 1.4, 0.0, 0.5), 0.0);
        DockBorderGlowBrush.EndPoint = new Point(Math.Clamp(1.0 - sweep * 1.4, 0.5, 1.0), 1.0);
    }

    private void StartRenderLoop()
    {
        if (_isClosed || _isSmokeOrAuditMode) return;
        _slotsAtRest = false;
        if (_isRenderingLoopActive) return;
        _lastRenderTicks = Stopwatch.GetTimestamp();
        _isRenderingLoopActive = true;
        CompositionTarget.Rendering += OnCompositionFrame;
    }

    private void StopRenderLoop()
    {
        if (!_isRenderingLoopActive) return;
        _isRenderingLoopActive = false;
        CompositionTarget.Rendering -= OnCompositionFrame;
    }

    private void OnCompositionFrame(object? sender, EventArgs e)
    {
        if (_isClosed)
        {
            StopRenderLoop();
            return;
        }

        long nowTicks = Stopwatch.GetTimestamp();
        bool glowRunning = ShouldAnimateDockAmbientGlow();
        if (_slotsAtRest && Math.Abs(_targetHideProgress - _currentHideProgress) <= 0.002 && glowRunning)
        {
            double ambientMinTicks = Stopwatch.Frequency / 24.0;
            if (nowTicks - _lastAmbientFrameTicks < ambientMinTicks)
            {
                return;
            }
        }

        double dt = Math.Clamp((nowTicks - _lastRenderTicks) / (double)Stopwatch.Frequency, 0.001, 0.05);
        _lastRenderTicks = nowTicks;

        double alpha = 1.0 - Math.Exp(-MotionSmoothSpeed * dt);
        bool anyMoving = false;

        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];

            if (s.Kind == SlotKind.Widget)
            {
                s.CurrentReorderShiftX += (s.ReorderShiftX - s.CurrentReorderShiftX) * alpha;
                if (Math.Abs(s.ReorderShiftX - s.CurrentReorderShiftX) > 0.05) anyMoving = true;
                else s.CurrentReorderShiftX = s.ReorderShiftX;
                s.Scale.ScaleX = 1.0;
                s.Scale.ScaleY = 1.0;
                s.Translate.X = s.CurrentReorderShiftX;
                s.Translate.Y = 0.0;
                continue;
            }

            if (s.Kind != SlotKind.App)
            {
                s.CurrentScale = 1.0;
                s.CurrentShiftX = 0.0;
                s.CurrentLiftY = 0.0;
                s.BounceStartTicks = 0;
                s.CurrentBounceY = 0.0;
                s.Scale.ScaleX = 1.0;
                s.Scale.ScaleY = 1.0;
                s.Translate.X = 0.0;
                s.Translate.Y = 0.0;
                continue;
            }

            s.CurrentScale += (s.TargetScale - s.CurrentScale) * alpha;
            s.CurrentShiftX += (s.TargetShiftX - s.CurrentShiftX) * alpha;
            s.CurrentLiftY += (s.TargetLiftY - s.CurrentLiftY) * alpha;
            s.CurrentReorderShiftX += (s.ReorderShiftX - s.CurrentReorderShiftX) * alpha;
            if (Math.Abs(s.ReorderShiftX - s.CurrentReorderShiftX) > 0.05)
            {
                anyMoving = true;
            }
            else
            {
                s.CurrentReorderShiftX = s.ReorderShiftX;
            }

            if (Math.Abs(s.TargetScale - s.CurrentScale) > 0.0008
                || Math.Abs(s.TargetShiftX - s.CurrentShiftX) > 0.05
                || Math.Abs(s.TargetLiftY - s.CurrentLiftY) > 0.05)
            {
                anyMoving = true;
            }
            else
            {
                s.CurrentScale = s.TargetScale;
                s.CurrentShiftX = s.TargetShiftX;
                s.CurrentLiftY = s.TargetLiftY;
            }

            if (s.BounceStartTicks != 0)
            {
                double elapsedSec = (nowTicks - s.BounceStartTicks) / (double)Stopwatch.Frequency;
                if (elapsedSec >= BounceDurationSec)
                {
                    s.BounceStartTicks = 0;
                    s.CurrentBounceY = 0.0;
                }
                else
                {
                    double decay = 1.0 - (elapsedSec / BounceDurationSec);
                    s.CurrentBounceY = -Math.Abs(Math.Sin(elapsedSec * Math.PI * 3.0)) * 14.0 * decay;
                    anyMoving = true;
                }
            }

            s.Scale.ScaleX = s.CurrentScale;
            s.Scale.ScaleY = s.CurrentScale;
            s.Translate.X = s.CurrentShiftX + s.CurrentReorderShiftX;
            s.Translate.Y = (s.CurrentLiftY + s.CurrentBounceY) * (IsDockAtTop ? -1.0 : 1.0);
        }

        double hideDiff = _targetHideProgress - _currentHideProgress;
        if (Math.Abs(hideDiff) > 0.002)
        {
            double hideSpeed = _targetHideProgress > _currentHideProgress ? 18.0 : 28.0;
            double hideAlpha = 1.0 - Math.Exp(-hideSpeed * dt);
            _currentHideProgress = Math.Clamp(_currentHideProgress + hideDiff * hideAlpha, 0.0, 1.0);
            anyMoving = true;
        }
        else
        {
            _currentHideProgress = _targetHideProgress;
        }

        double capH = DockPillContainer.ActualHeight > 10.0
            ? DockPillContainer.ActualHeight
            : Math.Max(_orch.Config.DockIconSizeDip + 19.0, 52.0);
        double hideDistance = capH + 14.0;
        DockCapsuleOffsetTransform.Y = _currentHideProgress * hideDistance * (IsDockAtTop ? -1.0 : 1.0);
        DockPillContainer.Opacity = Math.Clamp(1.0 - Math.Pow(_currentHideProgress, 1.35), 0.0, 1.0);

        if (_isHovered && !_isIntellihideTucked && _hoveredSlotIndex >= 0 && (anyMoving || _hoveredSlotIndex != _lastTooltipSlotIndex))
        {
            UpdateHoverTooltip(_hoveredSlotIndex);
            _lastTooltipSlotIndex = _hoveredSlotIndex;
        }

        _slotsAtRest = !anyMoving && Math.Abs(_targetHideProgress - _currentHideProgress) <= 0.002;

        if (glowRunning)
        {
            double ambientMinTicks = Stopwatch.Frequency / 24.0;
            if (_lastAmbientFrameTicks == 0 || (nowTicks - _lastAmbientFrameTicks) >= ambientMinTicks)
            {
                double glowDt = _lastAmbientFrameTicks == 0
                    ? dt
                    : Math.Clamp((nowTicks - _lastAmbientFrameTicks) / (double)Stopwatch.Frequency, 0.001, 0.12);
                _lastAmbientFrameTicks = nowTicks;
                ApplyDockAmbientGlowFrame(glowDt, activeMedia: true);
            }
        }

        if (!anyMoving && !glowRunning)
        {
            StopRenderLoop();
        }
    }

    private static Color ParseAccentColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var obj = ColorConverter.ConvertFromString(hex.Trim());
            return obj is Color c ? c : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
