using System.Diagnostics;
using System.Globalization;
using System.Windows;
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
using DinamikAda.Modules.CommandHub;
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
    private TextBlock? _clockTimeText;
    private TextBlock? _clockDateText;
    private Border? _clockChip;
    private Border? _islandSyncBadge;
    private TextBlock? _islandSyncBadgeText;
    private FrameworkElement? _islandSyncDot;
    private TextBlock? _trashIconText;
    private Ellipse? _trashDot;
    private Border? _trayChevronBtn;
    private Ellipse? _trayActiveDot;
    private int _lastTrayAppCount;
    private long _lastTrayPopupClosedTicks;
    private ContextMenu? _activeContextMenu;
    private DateTime _popupDisplayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _popupRenderedToday = DateTime.Today;
    private long _lastPopupClosedTicks;

    private sealed class DockSlotVisual
    {
        public required Grid Container { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TranslateTransform Translate { get; init; }
        public required Border IconPlate { get; init; }
        public required Ellipse? RunningDot { get; init; }
        public required Border? RunningPill { get; init; }
        public required string TooltipLabel { get; init; }
        public DockAppItem? AppItem { get; init; }
        public SlotKind Kind { get; init; }

        public double CurrentScale { get; set; } = 1.0;
        public double TargetScale { get; set; } = 1.0;
        public double CurrentShiftX { get; set; }
        public double TargetShiftX { get; set; }
        public double CurrentLiftY { get; set; }
        public double TargetLiftY { get; set; }
        public long BounceStartTicks { get; set; }
        public double CurrentBounceY { get; set; }
    }

    private enum SlotKind
    {
        StartMenu = 0,
        App = 1,
        IslandSync = 2,
        Trash = 3,
        TrayOverflow = 4
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

        bool enableDock = _orch.Config.EnableBottomDock;
        bool followFs = _orch.Config.DockFollowFullscreenHide;
        var fs = _orch.FullscreenDetector.Current;
        _isFullscreenHidden = followFs && fs.IsFullscreen && fs.Kind is FullscreenAppKind.Game or FullscreenAppKind.Video;

        _activeMonitor = ResolveDockMonitor();
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
                reserveFullWidthWorkArea: _orch.Config.DockReserveWorkArea);
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
            SyncCoordinatorState(_orch.Coordinator.Current);
            EvaluateDockOverlapAndState();
        }
        else
        {
            StopRenderLoop();
            _intellihideDebounceTimer.Stop();
            _intellihideEdgeTimer.Stop();
            SetClickThrough(false);
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

        Color primary = ParseAccentColor(snap.PrimaryActivity?.AccentHex, Color.FromRgb(10, 132, 255));
        Color secondary = ParseAccentColor(_orch.Media.Current.SecondaryAccentHex, Color.FromRgb(94, 92, 230));
        Color highlight = ParseAccentColor(_orch.Media.Current.HighlightAccentHex, Color.FromRgb(100, 210, 255));

        DockAuraStop0.Color = Color.FromArgb(0x1E, primary.R, primary.G, primary.B);
        DockAuraStop1.Color = Color.FromArgb(0x14, secondary.R, secondary.G, secondary.B);
        DockAuraStop2.Color = Color.FromArgb(0x1A, highlight.R, highlight.G, highlight.B);
        PopupAuraStop0.Color = Color.FromArgb(0x1C, primary.R, primary.G, primary.B);
        PopupAuraStop1.Color = Color.FromArgb(0x10, secondary.R, secondary.G, secondary.B);
        TrayPopupAuraStop0.Color = Color.FromArgb(0x1C, primary.R, primary.G, primary.B);
        TrayPopupAuraStop1.Color = Color.FromArgb(0x10, secondary.R, secondary.G, secondary.B);

        // Update fullscreen auto-hide smoothly
        bool wantFsHide = _orch.Config.DockFollowFullscreenHide
            && snap.Fullscreen.IsFullscreen
            && snap.Fullscreen.Kind is FullscreenAppKind.Game or FullscreenAppKind.Video;
        if (wantFsHide != _isFullscreenHidden)
        {
            _isFullscreenHidden = wantFsHide;
            if (_orch.Config.EnableBottomDock && !_isSmokeOrAuditMode)
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

    public RenderTargetBitmap RenderTrayPopupForAudit(int width = 300, int height = 240)
    {
        SetSmokeOrAuditMode(true);
        var demoItems = new BackgroundTrayAppItem[]
        {
            new("audit:discord", "Discord", "Discord.exe", null, 1, 0, "Electron_NotifyIconHostWindow", null),
            new("audit:whatsapp", "WhatsApp", "WhatsApp.Root.exe", null, 2, 0, "H.NotifyIcon", null),
            new("audit:steam", "Steam", "steam.exe", null, 3, 0, "Chrome_StatusTrayWindow", null),
            new("audit:ytmusic", "YouTube Music", "YouTube Music.exe", null, 4, 0, "Electron_NotifyIconHostWindow", null),
            new("audit:chatgpt", "ChatGPT", "ChatGPT.exe", null, 5, 0, "OwlElectron_NotifyIconHostWindow", null),
            new("audit:wpe", "Wallpaper Engine", "wallpaper32.exe", null, 6, 0, "WPETrayWindow", null)
        };
        RebuildTrayOverflowGrid(demoItems);
        SyncCoordinatorState(_orch.Coordinator.Current);

        TrayOverflowCard.Measure(new Size(298, double.PositiveInfinity));
        double cardH = Math.Ceiling(TrayOverflowCard.DesiredSize.Height);
        TrayOverflowCard.Arrange(new Rect(0, 0, 298, cardH));
        TrayOverflowCard.UpdateLayout();

        int finalW = Math.Max(width, 322);
        int finalH = Math.Max(height, (int)cardH + 24);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 16)), null, new Rect(0, 0, finalW, finalH));
            var vb = new VisualBrush(TrayOverflowCard) { Stretch = Stretch.None };
            double x = Math.Round((finalW - 298) * 0.5);
            double y = Math.Round((finalH - cardH) * 0.5);
            dc.DrawRectangle(vb, null, new Rect(x, y, 298, cardH));
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
        ApplyConfigAndSync();
        _lastTrayAppCount = _appsService.QueryBackgroundTrayApps().Count;
        if (_trayActiveDot is not null)
        {
            _trayActiveDot.Opacity = _lastTrayAppCount > 0 ? 1.0 : 0.0;
        }
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
        _orch.Coordinator.SnapshotChanged -= OnCoordinatorSnapshotChanged;
        UninstallWindowEventHooks();
        _hwndSource?.RemoveHook(OnDockWndProc);
        _hwndSource = null;

        if (!_isSmokeOrAuditMode)
        {
            _taskbarManager.Restore();
        }
    }

    private void OnCoordinatorSnapshotChanged(object? sender, IslandCoordinatorSnapshot snap)
    {
        SyncCoordinatorState(snap);
    }

    private void OnReconcileTick(object? sender, EventArgs e)
    {
        if (_isClosed || _isSmokeOrAuditMode) return;

        if (_orch.Config.EnableBottomDock && _orch.Config.HideWindowsTaskbar)
        {
            _taskbarManager.EnsureHiddenTick();
        }

        UpdateClockText();

        if (++_tickCounter % 4 == 0)
        {
            var prevRb = _lastRecycleBin;
            _lastRecycleBin = DockAppsService.QueryRecycleBin();
            if (prevRb.HasItems != _lastRecycleBin.HasItems && _trashDot is not null)
            {
                _trashDot.Opacity = _lastRecycleBin.HasItems ? 1.0 : 0.0;
            }

            int trayCount = _appsService.QueryBackgroundTrayApps().Count;
            _lastTrayAppCount = trayCount;
            if (_trayActiveDot is not null)
            {
                _trayActiveDot.Opacity = trayCount > 0 ? 1.0 : 0.0;
            }
        }

        if (_orch.Config.EnableBottomDock && !_isFullscreenHidden)
        {
            RefreshDockItemsNow();
            EvaluateDockOverlapAndState();
        }
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
        var bounds = DockLayoutCalculator.ComputeHostWindowBounds(_activeMonitor, HostHeightDip);
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

        if (eventType != NativeMethods.EVENT_OBJECT_LOCATIONCHANGE && !_refreshDebounceTimer.IsEnabled)
        {
            _refreshDebounceTimer.Start();
        }

        if (_orch.Config.EnableBottomDock && _orch.Config.DockIntellihideEnabled && !_isFullscreenHidden)
        {
            ScheduleIntellihideEval();
        }
    }

    private void ScheduleIntellihideEval()
    {
        if (_isClosed || _isSmokeOrAuditMode) return;
        _intellihideDebounceTimer.Stop();
        _intellihideDebounceTimer.Start();
    }

    private NativeMethods.RECT ComputeCurrentDockHitRectPx()
    {
        double capW = DockPillContainer.ActualWidth > 10.0
            ? DockPillContainer.ActualWidth
            : Math.Max(300.0, DockPillContainer.DesiredSize.Width);
        double capH = DockPillContainer.ActualHeight > 10.0
            ? DockPillContainer.ActualHeight
            : Math.Max(_orch.Config.DockIconSizeDip + 19.0, DockPillContainer.DesiredSize.Height);
        return DockLayoutCalculator.ComputeDockHitRectPx(_activeMonitor, capW, capH, bottomMarginDip: 6.0, topBufferPx: 4);
    }

    private bool IsAnyDockOverlayOpen()
    {
        return ClockCalendarPopup.IsOpen
            || TrayOverflowPopup.IsOpen
            || (_activeContextMenu is { IsOpen: true });
    }

    private void EvaluateDockOverlapAndState()
    {
        if (_isClosed || _isSmokeOrAuditMode || !_orch.Config.EnableBottomDock || _isFullscreenHidden)
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
        if (_isClosed || _isSmokeOrAuditMode || !_orch.Config.EnableBottomDock || _isFullscreenHidden || !_orch.Config.DockIntellihideEnabled)
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
        bool inRevealEdge = DockLayoutCalculator.IsPointInBottomRevealZone(pt.X, pt.Y, _activeMonitor, edgePx);
        int hoverPadH = (int)Math.Round(24.0 * _activeMonitor.DpiScale);
        int hoverPadTop = (int)Math.Round(30.0 * _activeMonitor.DpiScale);
        bool inKeepOpenZone = DockLayoutCalculator.IsPointInDockKeepOpenZone(pt.X, pt.Y, _cachedDockHitRectPx, hoverPadH, hoverPadTop);
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
        keyBuilder.Append(iconSize).Append('|').Append(lang).Append('|');
        foreach (var item in _currentAppItems)
        {
            keyBuilder.Append(item.Key)
                .Append(':').Append(item.IsPinned ? 'P' : 'U')
                .Append(':').Append(item.IsRunning ? item.Windows.Count : 0)
                .Append(':').Append(item.IsActiveForeground ? '1' : '0')
                .Append(';');
        }

        string nextKey = keyBuilder.ToString();
        if (!force && string.Equals(nextKey, _lastRenderedStructureKey, StringComparison.Ordinal))
        {
            return;
        }
        _lastRenderedStructureKey = nextKey;

        DockItemsHost.Children.Clear();
        _slots.Clear();

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

        // 3. Synchronized DinamikAda Live Island Slot
        AddIslandSyncSlot(lang, iconSize, tileWidth + 10.0, tileHeight);

        // 4. Windows Background / Tray Overflow ("^") Chevron Slot (stationary)
        AddTrayChevronSlot(lang, iconSize, Math.Max(26.0, iconSize - 6.0), tileHeight);

        // 5. macOS Trash / Recycle Bin Slot (stationary)
        AddTrashSlot(lang, iconSize, tileWidth, tileHeight);

        // 5. Compact Clock & Date Chip
        AddClockChip(iconSize);

        DockPillContainer.ClearValue(WidthProperty);
        DockPillContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        RecomputeSlotCenters();

        if (_isHovered && _hoverLocalX.HasValue)
        {
            UpdateMagnificationTargets(_hoverLocalX);
        }

        if (!_isSmokeOrAuditMode && _orch.Config.EnableBottomDock && !_isFullscreenHidden)
        {
            _cachedDockHitRectPx = ComputeCurrentDockHitRectPx();
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
            Height = tileH + 7.0,
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
            Background = useGlassPlate ? new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
            BorderBrush = useGlassPlate ? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
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
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(glyphBrushHex)),
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
        if (isForeground)
        {
            pill = new Border
            {
                Width = 10,
                Height = 3.0,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(ParseAccentColor(_orch.Coordinator.Current.PrimaryActivity?.AccentHex, Color.FromRgb(10, 132, 255))),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 0.5),
                IsHitTestVisible = false
            };
            container.Children.Add(pill);
        }
        else if (isRunning)
        {
            dot = new Ellipse
            {
                Width = 3.5,
                Height = 3.5,
                Fill = new SolidColorBrush(Color.FromArgb(0xC8, 0xF5, 0xF5, 0xF7)),
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
            container.MouseEnter += (_, _) =>
                plate.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            container.MouseLeave += (_, _) =>
                plate.Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        }

        container.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
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

    private void AddIslandSyncSlot(string lang, int iconSize, double tileW, double tileH)
    {
        var scale = new ScaleTransform(1.0, 1.0, tileW * 0.5, tileH);
        var translate = new TranslateTransform(0.0, 0.0);
        var tg = new TransformGroup();
        tg.Children.Add(scale);
        tg.Children.Add(translate);

        var container = new Grid
        {
            Width = tileW,
            Height = tileH + 7.0,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            RenderTransform = tg
        };

        // Sleek miniature Dynamic Island pill synced with top notch
        double pillH = Math.Max(20.0, Math.Round(iconSize * 0.68));
        double pillW = Math.Max(36.0, iconSize + 10.0);
        var plate = new Border
        {
            Width = pillW,
            Height = pillH,
            CornerRadius = new CornerRadius(pillH * 0.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Math.Round((iconSize - pillH) * 0.5), 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF)),
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
            Margin = new Thickness(0, 0, 5, 0)
        };
        lensOuter.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromRgb(10, 14, 24)),
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
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

        row.Children.Add(lensOuter);
        row.Children.Add(liveDot);

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
            Height = tileH + 7.0,
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
            Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.6)
        };

        plate.Child = new TextBlock
        {
            Text = "\uE70E",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = Math.Max(9.5, Math.Round(iconSize * 0.34)),
            Foreground = new SolidColorBrush(Color.FromRgb(228, 228, 231)),
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
            plate.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            plate.BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        };
        container.MouseLeave += (_, _) =>
        {
            plate.Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
            plate.BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
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
        if (!TrayOverflowPopup.IsOpen && !ClockCalendarPopup.IsOpen)
        {
            _popupLightDismissTimer?.Stop();
            UninstallPopupMouseHook();
        }
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
        if (_isClosed || (!TrayOverflowPopup.IsOpen && !ClockCalendarPopup.IsOpen))
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
            && curFg != _hwnd;
        if (fgChanged)
        {
            _popupOpenForegroundHwnd = curFg;
        }

        if (!isEscapeDown && !fgChanged)
        {
            return;
        }

        if (TrayOverflowPopup.IsOpen)
        {
            TrayOverflowPopup.IsOpen = false;
        }

        if (ClockCalendarPopup.IsOpen)
        {
            ClockCalendarPopup.IsOpen = false;
        }
    }

    private void ToggleTrayPopup()
    {
        if (_trayChevronBtn is null) return;
        ClockCalendarPopup.IsOpen = false;
        if (TrayOverflowPopup.IsOpen)
        {
            TrayOverflowPopup.IsOpen = false;
            return;
        }

        double sinceClose = (Stopwatch.GetTimestamp() - _lastTrayPopupClosedTicks) / (double)Stopwatch.Frequency;
        if (_lastTrayPopupClosedTicks != 0 && sinceClose < 0.22)
        {
            return;
        }

        TrayOverflowPopup.PlacementTarget = _trayChevronBtn;
        TrayOverflowPopup.IsOpen = true;
    }

    private void OnTrayPopupOpened(object? sender, EventArgs e)
    {
        if (_isClosed) return;
        var items = _isSmokeOrAuditMode ? [] : _appsService.QueryBackgroundTrayApps();
        RebuildTrayOverflowGrid(items);
        SyncCoordinatorState(_orch.Coordinator.Current);
        if (!_isSmokeOrAuditMode)
        {
            StartPopupLightDismissWatcher();
        }
    }

    private void OnTrayPopupClosed(object? sender, EventArgs e)
    {
        _lastTrayPopupClosedTicks = Stopwatch.GetTimestamp();
        StopPopupLightDismissWatcherIfIdle();
        ScheduleIntellihideEval();
    }

    private void OnTrayPopupRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_isSmokeOrAuditMode) return;
        RebuildTrayOverflowGrid(_appsService.QueryBackgroundTrayApps());
    }

    private void RebuildTrayOverflowGrid(IReadOnlyList<BackgroundTrayAppItem> items)
    {
        string lang = _orch.Config.Language;
        _lastTrayAppCount = items.Count;
        if (_trayActiveDot is not null)
        {
            _trayActiveDot.Opacity = items.Count > 0 ? 1.0 : 0.0;
        }

        TrayPopupTitleText.Text = Strings.TrayOverflowHeader(lang);
        TrayPopupEmptyText.Text = Strings.TrayOverflowEmpty(lang);
        TrayPopupCountText.Text = items.Count.ToString(CultureInfo.InvariantCulture);

        TrayOverflowGrid.Children.Clear();
        TrayPopupEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var tileNormalBg = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        var tileHoverBg = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        var tileBorder = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        var tileHoverBorder = new SolidColorBrush(Color.FromArgb(0x36, 0x64, 0xD2, 0xFF));
        var textPrimary = (Brush)FindResource("TextPrimaryBrush");
        var iconFont = (FontFamily)FindResource("IconFont");
        var uiTextFont = (FontFamily)FindResource("UiTextFont");

        foreach (var item in items)
        {
            var captured = item;
            var tile = new Border
            {
                Width = 51,
                Height = 54,
                Margin = new Thickness(2),
                Padding = new Thickness(2, 5, 2, 4),
                CornerRadius = new CornerRadius(10),
                Background = tileNormalBg,
                BorderBrush = tileBorder,
                BorderThickness = new Thickness(0.6),
                Cursor = Cursors.Hand,
                ToolTip = string.IsNullOrWhiteSpace(captured.ExePath)
                    ? captured.DisplayName
                    : $"{captured.DisplayName}\n{System.IO.Path.GetFileName(captured.ExePath)}"
            };

            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (captured.Icon is not null)
            {
                content.Children.Add(new Image
                {
                    Source = captured.Icon,
                    Width = 24,
                    Height = 24,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
            else
            {
                content.Children.Add(new TextBlock
                {
                    Text = "\uE737",
                    FontFamily = iconFont,
                    FontSize = 18,
                    Foreground = textPrimary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 4)
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = captured.DisplayName,
                FontFamily = uiTextFont,
                FontSize = 9.0,
                Foreground = textPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 48
            });

            tile.Child = content;
            tile.MouseEnter += (_, _) =>
            {
                tile.Background = tileHoverBg;
                tile.BorderBrush = tileHoverBorder;
            };
            tile.MouseLeave += (_, _) =>
            {
                tile.Background = tileNormalBg;
                tile.BorderBrush = tileBorder;
            };
            tile.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                DismissPopupsAndActivateTrayApp(captured);
            };
            tile.MouseRightButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                ShowTrayItemContextMenu(tile, captured, lang);
            };

            TrayOverflowGrid.Children.Add(tile);
        }
    }

    private void DismissPopupsAndActivateTrayApp(BackgroundTrayAppItem item)
    {
        if (_activeContextMenu is not null)
        {
            _activeContextMenu.IsOpen = false;
            _activeContextMenu = null;
        }
        ClockCalendarPopup.IsOpen = false;
        TrayOverflowPopup.IsOpen = false;
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }

        if (_isSmokeOrAuditMode) return;

        // Defer Win32 ShowWindow/ForceForeground until WPF finishes closing PopupRoot and releasing mouse capture.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (Mouse.Captured is not null)
            {
                Mouse.Capture(null);
            }
            DockAppsService.ActivateBackgroundTrayApp(item);
        });
    }

    private void ShowTrayItemContextMenu(FrameworkElement anchor, BackgroundTrayAppItem item, string lang)
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
        openItem.Click += (_, _) => DismissPopupsAndActivateTrayApp(item);
        menu.Items.Add(openItem);

        if (!string.IsNullOrWhiteSpace(item.ExePath))
        {
            var locItem = new MenuItem { Header = Strings.TrayOpenLocation(lang) };
            locItem.Click += (_, _) =>
            {
                TrayOverflowPopup.IsOpen = false;
                DockAppsService.OpenAppFileLocation(item.ExePath);
            };
            menu.Items.Add(locItem);
        }

        menu.Items.Add(new Separator());
        var quitItem = new MenuItem { Header = Strings.TrayQuitApp(lang) };
        quitItem.Click += (_, _) =>
        {
            DockAppsService.QuitBackgroundTrayApp(item);
            if (TrayOverflowPopup.IsOpen)
            {
                RebuildTrayOverflowGrid(_appsService.QueryBackgroundTrayApps());
            }
        };
        menu.Items.Add(quitItem);

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Top;
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
            Height = tileH + 7.0,
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
            Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)),
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
            plate.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
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
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
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
            Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.6),
            VerticalAlignment = VerticalAlignment.Top
        };

        _clockTimeText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 11.2,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _clockDateText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiTextFont"),
            FontSize = 8.8,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_clockTimeText);
        stack.Children.Add(_clockDateText);
        clockBtn.Child = stack;

        clockBtn.MouseEnter += (_, _) =>
        {
            clockBtn.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            clockBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        };
        clockBtn.MouseLeave += (_, _) =>
        {
            clockBtn.Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
            clockBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
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
        string hhmm = now.ToString("HH:mm");
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
                WindowsTaskbarManager.TriggerStartMenu();
                break;

            case SlotKind.App when slot.AppItem is { } app:
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

    private void HandleSlotContextMenu(DockSlotVisual slot)
    {
        string lang = _orch.Config.Language;
        var menu = new ContextMenu();
        _activeContextMenu = menu;

        if (slot.Kind == SlotKind.StartMenu)
        {
            var startItem = new MenuItem { Header = Strings.DockStartLabel(lang) };
            startItem.Click += (_, _) => WindowsTaskbarManager.TriggerStartMenu();
            var cmdItem = new MenuItem { Header = Strings.CommandPalette(lang) };
            cmdItem.Click += (_, _) => _orch.Coordinator.OpenCommandPalette();
            menu.Items.Add(startItem);
            menu.Items.Add(cmdItem);
        }
        else if (slot.Kind == SlotKind.App && slot.AppItem is { } app)
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
        else if (slot.Kind == SlotKind.IslandSync)
        {
            var toggleHub = new MenuItem { Header = Strings.OpenHub(lang) };
            toggleHub.Click += (_, _) => _orch.Coordinator.RequestExpand();
            var cmdPal = new MenuItem { Header = Strings.CommandPalette(lang) };
            cmdPal.Click += (_, _) => _orch.Coordinator.OpenCommandPalette();
            menu.Items.Add(toggleHub);
            menu.Items.Add(cmdPal);
        }
        else if (slot.Kind == SlotKind.TrayOverflow)
        {
            var openTrayItem = new MenuItem { Header = Strings.DockTrayAppsLabel(lang) };
            openTrayItem.Click += (_, _) => ToggleTrayPopup();
            menu.Items.Add(openTrayItem);
        }
        else if (slot.Kind == SlotKind.Trash)
        {
            var openRb = new MenuItem { Header = Strings.DockOpenTrash(lang) };
            openRb.Click += (_, _) => DockAppsService.OpenRecycleBin();
            var emptyRb = new MenuItem { Header = Strings.DockEmptyTrash(lang) };
            emptyRb.Click += (_, _) => DockAppsService.EmptyRecycleBin(_hwnd);
            menu.Items.Add(openRb);
            menu.Items.Add(emptyRb);
        }

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
        menu.PlacementTarget = slot.Container;
        menu.Placement = PlacementMode.Top;
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
        _isHovered = true;
        _hoverLocalX = e.GetPosition(DockItemsHost).X;
        UpdateMagnificationTargets(_hoverLocalX);
        StartRenderLoop();
    }

    private void OnDockMouseLeave(object sender, MouseEventArgs e)
    {
        _isHovered = false;
        _hoverLocalX = null;
        _hoveredSlotIndex = -1;
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
        double y = Math.Max(4.0, anchorPt.Y - 30.0 - extraLift);

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

    private void StartRenderLoop()
    {
        if (_isClosed || _isSmokeOrAuditMode || _isRenderingLoopActive) return;
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
        double dt = Math.Clamp((nowTicks - _lastRenderTicks) / (double)Stopwatch.Frequency, 0.001, 0.05);
        _lastRenderTicks = nowTicks;

        double alpha = 1.0 - Math.Exp(-MotionSmoothSpeed * dt);
        bool anyMoving = false;

        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];

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
            s.Translate.X = s.CurrentShiftX;
            s.Translate.Y = s.CurrentLiftY + s.CurrentBounceY;
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
        DockCapsuleOffsetTransform.Y = _currentHideProgress * hideDistance;
        DockPillContainer.Opacity = Math.Clamp(1.0 - Math.Pow(_currentHideProgress, 1.35), 0.0, 1.0);

        if (_isHovered && !_isIntellihideTucked && _hoveredSlotIndex >= 0)
        {
            UpdateHoverTooltip(_hoveredSlotIndex);
        }

        if (!anyMoving && (!_isHovered || _isIntellihideTucked))
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
