using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DinamikAda.App.Services;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Arbitration;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;
using DinamikAda.Core.Physics;
using DinamikAda.Modules.Media;
using DinamikAda.Modules.Productivity;
using DinamikAda.Modules.SystemControl;
using DinamikAda.Modules.Telemetry;
using DinamikAda.Platform.Win32.Hotkeys;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;
using ActivityKind = DinamikAda.Core.Abstractions.ActivityKind;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace DinamikAda.App.Views;

public partial class IslandWindow : Window
{
    private const double CompactTierW = IslandWindowController.CompactTierWidthDip;
    private const double CompactTierH = IslandWindowController.CompactTierHeightDip;
    private const double ExpandedTierW = IslandWindowController.ExpandedTierWidthDip;
    private const double ExpandedTierH = IslandWindowController.ExpandedTierHeightDip;

    private static readonly Brush DefaultShellBrush = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
    private static readonly Brush FloatingShellBrush = new SolidColorBrush(Color.FromArgb(244, 5, 6, 8));
    private static readonly Brush ActiveTileBgBrush = new SolidColorBrush(Color.FromArgb(56, 10, 132, 255));
    private static readonly Brush ActiveTileBorderBrush = new SolidColorBrush(Color.FromArgb(145, 10, 132, 255));
    private static readonly Brush DefaultTileBgBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
    private static readonly Brush DefaultTileBorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
    private static readonly Brush CardSurfaceBrush = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
    private static readonly Brush CardBorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
    private static readonly Brush ActiveTabFgBrush = new SolidColorBrush(Color.FromArgb(255, 245, 245, 247));
    private static readonly Brush MutedTabFgBrush = new SolidColorBrush(Color.FromArgb(255, 161, 161, 170));
    private static readonly Brush ActiveAccentDotBrush = new SolidColorBrush(Color.FromArgb(255, 48, 209, 88));
    private static readonly Brush MicLiveBrush = new SolidColorBrush(Color.FromArgb(255, 255, 159, 10));
    private static readonly Brush HungBadgeBgBrush = new SolidColorBrush(Color.FromArgb(46, 255, 69, 58));
    private static readonly Brush HungBadgeFgBrush = new SolidColorBrush(Color.FromArgb(255, 255, 118, 110));
    private static readonly Brush TileAccentBlueBrush = new SolidColorBrush(Color.FromArgb(255, 10, 132, 255));
    private static readonly Brush DefaultHudWellBgBrush = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255));
    private static readonly Brush DefaultHudWellBorderBrush = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255));
    private static readonly FontFamily UiTextFontFamily = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily UiIconFontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    static IslandWindow()
    {
        DefaultShellBrush.Freeze();
        FloatingShellBrush.Freeze();
        ActiveTileBgBrush.Freeze();
        ActiveTileBorderBrush.Freeze();
        DefaultTileBgBrush.Freeze();
        DefaultTileBorderBrush.Freeze();
        CardSurfaceBrush.Freeze();
        CardBorderBrush.Freeze();
        ActiveTabFgBrush.Freeze();
        MutedTabFgBrush.Freeze();
        ActiveAccentDotBrush.Freeze();
        MicLiveBrush.Freeze();
        HungBadgeBgBrush.Freeze();
        HungBadgeFgBrush.Freeze();
        TileAccentBlueBrush.Freeze();
        DefaultHudWellBgBrush.Freeze();
        DefaultHudWellBorderBrush.Freeze();
    }

    private readonly AppOrchestrator _orch;
    private readonly IslandWindowController _controller;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly DimmerOverlayManager _dimmerManager;
    private readonly DockWindow? _dockWindow;

    private readonly IslandMotionState _motion = new();
    private readonly NotchGeometryBuilder _mainNotchBuilder = new();
    private readonly NotchGeometryBuilder _mainHitNotchBuilder = new();
    private readonly NotchGeometryBuilder _satNotchBuilder = new();
    private readonly NotchGeometryBuilder _satHitNotchBuilder = new();
    private double _glowAnimPhase;
    private Color _currentSecondaryAuraColor = Color.FromRgb(94, 92, 230);
    private Color _currentHighlightAuraColor = Color.FromRgb(100, 210, 255);
    private NotificationEntry? _activeDetailNotification;
    private readonly Dictionary<string, ImageSource> _notifIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _pidExePathCache = new();
    private long _lastMasterSliderEditTick;
    private long _lastMicSliderEditTick;
    private List<IdleBackgroundAppCandidate> _pendingIdleApps = [];

    private readonly ScaleTransform[] _compactSpecScales;
    private readonly ScaleTransform[] _audioSpecScales;
    private readonly ScaleTransform[] _voiceWaveScales;

    private readonly DispatcherTimer _hoverDwellTimer;
    private readonly DispatcherTimer _collapseDebounceTimer;
    private readonly DispatcherTimer _dragLeaveDebounceTimer;
    private readonly DispatcherTimer _mediaPositionTimer;
    private readonly DispatcherTimer _dragCursorMonitorTimer;
    private readonly DispatcherTimer _shiftDodgeTimer;
    private readonly Win32TrayIcon _trayIcon = new();

    private nint _hwnd;
    private HwndSource? _hwndSource;
    private nint _lastReportedIslandMonitor;
    private bool _shiftAutoDodgeActive;
    private IslandCoordinatorSnapshot _lastSnapshot;
    private MonitorDescriptor _activeMonitor;
    private nint _lockedExpandedMonitorHandle;

    private double _tierHostWidth = ExpandedTierW;
    private double _tierHostHeight = ExpandedTierH;
    private bool _isExpandedTierActive = true;
    private bool _pendingCompactTierCollapse;
    private bool _hitContourDirty = true;

    private bool _isRenderingLoopActive;
    private bool _isClosed;
    private bool _isSmokeMode;
    private bool _isRenderAuditMode;
    private bool _resetNextFrameDt;
    private bool _snapTabIndicatorOnMeasure;
    private long _lastRenderTicks;
    private long _lastFrameCommittedTicks;
    private bool _burstMeasuring;
    private int _burstFrameCount;

    private bool _isMainHovered;
    private bool _isSatelliteHovered;
    private bool _isPointerPressed;
    private bool _isDraggingNotch;
    private bool _isInternalDragOut;
    private Point _dragStartScreenDip;
    private double _dragStartCenterDip;
    private double _dragStartTopDip;
    private bool _didDragMove;

    private bool _isSeekingMedia;
    private bool _suppressAudioUiEvents;
    private bool _suppressBrightnessUiEvents;
    private bool _s5CommandActivatedOnce;
    private long _s5CommandOpenedMs;
    private bool _isCapturingOcr;
    private bool _isCapturingColor;
    private int _openAppRouteCombos;
    private bool _qrOverlayVisible;
    private string? _renderedQrUrl;
    private string _brightnessTopologyKey = string.Empty;
    private readonly Dictionary<string, (Slider? HwSlider, TextBlock? HwLabel, Slider? DimSlider, TextBlock? DimLabel)> _brightnessRowRefs = new(StringComparer.OrdinalIgnoreCase);

    private sealed record AudioRouteChoice(string DeviceId, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SchedChoice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class AppMixerRowRef
    {
        public required TextBlock PctText { get; init; }
        public required Button MuteBtn { get; init; }
        public required Slider VolSlider { get; init; }
        public required ComboBox RouteCombo { get; init; }
        public bool IsMuted { get; set; }
        public long LastUserEditTick { get; set; }
    }

    private string _appMixerTopologyKey = string.Empty;
    private readonly Dictionary<string, AppMixerRowRef> _appMixerRowRefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool[] _tabDirty = new bool[6];
    private static readonly ConcurrentDictionary<uint, Brush> HexBrushCache = new();

    private byte[]? _lastThumbBytes;
    private BitmapImage? _cachedAlbumBitmap;
    private Color _currentAuraColor = Color.FromRgb(10, 132, 255);
    private string _lastMediaTrackKey = string.Empty;
    private bool _wasMediaPlaying;

    private CancellationTokenSource? _commandSearchCts;
    private bool _isExecutingCommand;
    private SettingsWindow? _settingsWindow;
    private ContextMenu? _islandContextMenu;
    private UIElement? _lastPrimarySurface;

    private MediaPlaybackSnapshot? _auditMediaOverride;
    private AudioMixerSnapshot? _auditAudioOverride;
    private string? _auditLyricOverride;
    private MeetingOccurrence? _auditMeetingOverride;
    private LanDropShareInfo? _auditQrShareOverride;
    private IReadOnlyList<DisplayBrightnessInfo>? _auditDisplaysOverride;
    private ConnectivitySnapshot? _auditConnOverride;
    private PrivacySensorSnapshot? _auditPrivacyOverride;
    private IReadOnlyList<TimerSnapshot>? _auditTimersOverride;
    private IReadOnlyList<NotificationEntry>? _auditNotifsOverride;
    private IReadOnlyList<ShelfItem>? _auditShelfOverride;
    private IReadOnlyList<ClipboardEntry>? _auditClipOverride;
    private SystemTelemetrySnapshot? _auditTelemetryOverride;
    private IReadOnlyList<WidgetRenderSnapshot>? _auditWidgetsOverride;
    private ScheduledPowerSnapshot? _auditScheduledPowerOverride;

    public IslandWindowController WindowController => _controller;
    public bool IsRenderingLoopActive => _isRenderingLoopActive;

    public IslandWindow(
        AppOrchestrator orchestrator,
        IslandWindowController controller,
        GlobalHotkeyService hotkeyService,
        DimmerOverlayManager dimmerManager,
        DockWindow? dockWindow = null)
    {
        _orch = orchestrator;
        _controller = controller;
        _hotkeyService = hotkeyService;
        _dimmerManager = dimmerManager;
        _dockWindow = dockWindow;

        InitializeComponent();

        _compactSpecScales =
        [
            SpecScale0, SpecScale1, SpecScale2, SpecScale3,
            SpecScale4, SpecScale5, SpecScale6, SpecScale7
        ];
        _audioSpecScales =
        [
            AudioSpecScale0, AudioSpecScale1, AudioSpecScale2, AudioSpecScale3,
            AudioSpecScale4, AudioSpecScale5, AudioSpecScale6, AudioSpecScale7
        ];
        _voiceWaveScales =
        [
            VoiceWaveScale0, VoiceWaveScale1, VoiceWaveScale2, VoiceWaveScale3,
            VoiceWaveScale4, VoiceWaveScale5, VoiceWaveScale6, VoiceWaveScale7
        ];

        NotchShellPath.Data = _mainNotchBuilder.ShellGeometry;
        NotchContentClipHost.Clip = _mainNotchBuilder.ShellGeometry;
        NotchRimPath.Data = _mainNotchBuilder.RimGeometry;
        NotchHitPath.Data = _mainHitNotchBuilder.ShellGeometry;

        SatelliteShellPath.Data = _satNotchBuilder.ShellGeometry;
        SatelliteContentClipHost.Clip = _satNotchBuilder.ShellGeometry;
        SatelliteRimPath.Data = _satNotchBuilder.RimGeometry;
        SatelliteHitPath.Data = _satHitNotchBuilder.ShellGeometry;

        Width = ExpandedTierW;
        Height = ExpandedTierH;

        _lastSnapshot = _orch.Coordinator.Current;
        _activeMonitor = _orch.MonitorLayout.GetTargetMonitor(
            _orch.Config.MonitorMode,
            _orch.Config.PinnedMonitorDeviceName,
            0,
            0);

        _hoverDwellTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(240) };
        _hoverDwellTimer.Tick += OnHoverDwellTick;

        _collapseDebounceTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(320) };
        _collapseDebounceTimer.Tick += OnCollapseDebounceTick;

        _dragLeaveDebounceTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(180) };
        _dragLeaveDebounceTimer.Tick += OnDragLeaveDebounceTick;

        _mediaPositionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _mediaPositionTimer.Tick += OnMediaPositionTimerTick;

        _dragCursorMonitorTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        _dragCursorMonitorTimer.Tick += OnDragCursorMonitorTick;

        _shiftDodgeTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(85) };
        _shiftDodgeTimer.Tick += OnShiftDodgeTick;

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    public void SetSmokeMode(bool enabled) => _isSmokeMode = enabled;

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (_isRenderAuditMode) return;

        var helper = new WindowInteropHelper(this);
        nint hwnd = helper.Handle;
        _hwnd = hwnd;

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(OnWindowMessageHook);

        _controller.Attach(hwnd);
        _controller.ApplyBaseExtendedStyles();
        _controller.DisplayOrDpiChanged += OnControllerDisplayOrDpiChanged;
        _controller.NamedHotkeyPressed += OnControllerNamedHotkeyPressed;
        _controller.VolumeDeviceChanged += OnControllerVolumeDeviceChanged;
        _controller.CaptureStateChanged += OnControllerCaptureStateChanged;
        _controller.ActivationChanged += OnControllerActivationChanged;
        _controller.ClipboardUpdated += OnControllerClipboardUpdated;

        _orch.Clipboard.AttachToWindow(hwnd);
        _orch.WindowControl.SetIslandHandle(hwnd);
        RegisterHotkeyWithAlert(GlobalHotkeyService.HotkeyId, _orch.Config.CommandHotkey);
        RegisterHotkeyWithAlert(GlobalHotkeyService.MicMuteHotkeyId, _orch.Config.MicMuteHotkey);
        RegisterHotkeyWithAlert(GlobalHotkeyService.ColorPickerHotkeyId, _orch.Config.ColorPickerHotkey);

        if (!_isSmokeMode)
        {
            _trayIcon.LeftClicked += OnTrayLeftClicked;
            _trayIcon.DoubleClicked += OnTrayLeftClicked;
            _trayIcon.RightClicked += OnTrayRightClicked;
            _trayIcon.Attach(hwnd, Strings.AppTitle(_orch.Config.Language));
        }

        BindOrchestratorEvents();
        ApplyLocalization();
        ApplyShellVisualTheme();

        _activeMonitor = ResolveTargetMonitor(_lastSnapshot);
        ApplyTierAndMonitorPlacement(_lastSnapshot, snapImmediately: true);
        RenderCoordinatorSnapshot(_lastSnapshot, isInitial: true);
        if (!_isSmokeMode)
        {
            _shiftDodgeTimer.Start();
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;

        SyncMediaPositionTimerState();
        _ = RefreshInitialContentAsync();
        if (!_isSmokeMode && _dockWindow is not null && !_dockWindow.IsVisible)
        {
            _dockWindow.Show();
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosed = true;
        StopRenderLoop();

        _hoverDwellTimer.Stop();
        _collapseDebounceTimer.Stop();
        _dragLeaveDebounceTimer.Stop();
        _mediaPositionTimer.Stop();
        _dragCursorMonitorTimer.Stop();
        _shiftDodgeTimer.Stop();

        _commandSearchCts?.Cancel();
        _commandSearchCts?.Dispose();
        _commandSearchCts = null;

        UnbindOrchestratorEvents();

        if (!_isRenderAuditMode)
        {
            _dockWindow?.RestoreTaskbarImmediately();
            try { _dockWindow?.Close(); } catch { }
            _orch.Clipboard.AttachToWindow(0);
            _trayIcon.LeftClicked -= OnTrayLeftClicked;
            _trayIcon.DoubleClicked -= OnTrayLeftClicked;
            _trayIcon.RightClicked -= OnTrayRightClicked;
            _trayIcon.Dispose();
            _hwndSource?.RemoveHook(OnWindowMessageHook);
            _hwndSource = null;

            _hotkeyService.UnregisterAll();
            _controller.DisplayOrDpiChanged -= OnControllerDisplayOrDpiChanged;
            _controller.NamedHotkeyPressed -= OnControllerNamedHotkeyPressed;
            _controller.VolumeDeviceChanged -= OnControllerVolumeDeviceChanged;
            _controller.CaptureStateChanged -= OnControllerCaptureStateChanged;
            _controller.ActivationChanged -= OnControllerActivationChanged;
            _controller.ClipboardUpdated -= OnControllerClipboardUpdated;
            _controller.Dispose();
        }
    }

    private void BindOrchestratorEvents()
    {
        _orch.Coordinator.SnapshotChanged += OnCoordinatorSnapshotChanged;
        _orch.Media.SnapshotChanged += OnMediaSnapshotChanged;
        _orch.Audio.SnapshotChanged += OnAudioSnapshotChanged;
        _orch.Audio.SpectrumUpdated += OnAudioSpectrumUpdated;
        _orch.Brightness.DisplaysChanged += OnBrightnessDisplaysChanged;
        _orch.Brightness.ValuesUpdated += OnBrightnessValuesUpdated;
        _orch.Brightness.SoftwareDimmerChanged += OnBrightnessSoftwareDimmerChanged;
        _orch.Connectivity.StateChanged += OnConnectivityStateChanged;
        _orch.Privacy.PrivacyChanged += OnPrivacyChanged;
        _orch.Notifications.NotificationsChanged += OnNotificationsChanged;
        _orch.Timers.TimersChanged += OnTimersChanged;
        _orch.Shelf.ItemsChanged += OnShelfItemsChanged;
        _orch.Clipboard.EntriesChanged += OnClipboardEntriesChanged;
        _orch.Telemetry.TelemetryUpdated += OnTelemetryUpdated;
        _orch.Widgets.WidgetsChanged += OnWidgetsChanged;
        _orch.OpenSettingsRequested += OnOrchestratorOpenSettingsRequested;
        _orch.QrShareRequested += OnOrchestratorQrShareRequested;
        _orch.RegionOcrRequested += OnOrchestratorRegionOcrRequested;
        _orch.RegionColorPickRequested += OnOrchestratorRegionColorPickRequested;
        _orch.LanDrop.ShareStateChanged += OnLanDropShareStateChanged;
        _orch.VoiceNotes.WaveformUpdated += OnVoiceNoteWaveformUpdated;
        _orch.VoiceNotes.NoteCompleted += OnVoiceNoteCompleted;
        _orch.Calendar.ActivityChanged += OnCalendarActivityChanged;
        _orch.Timers.ScheduledPowerChanged += OnScheduledPowerChanged;
    }

    private void UnbindOrchestratorEvents()
    {
        _orch.Coordinator.SnapshotChanged -= OnCoordinatorSnapshotChanged;
        _orch.Media.SnapshotChanged -= OnMediaSnapshotChanged;
        _orch.Audio.SnapshotChanged -= OnAudioSnapshotChanged;
        _orch.Audio.SpectrumUpdated -= OnAudioSpectrumUpdated;
        _orch.Brightness.DisplaysChanged -= OnBrightnessDisplaysChanged;
        _orch.Brightness.ValuesUpdated -= OnBrightnessValuesUpdated;
        _orch.Brightness.SoftwareDimmerChanged -= OnBrightnessSoftwareDimmerChanged;
        _orch.Connectivity.StateChanged -= OnConnectivityStateChanged;
        _orch.Privacy.PrivacyChanged -= OnPrivacyChanged;
        _orch.Notifications.NotificationsChanged -= OnNotificationsChanged;
        _orch.Timers.TimersChanged -= OnTimersChanged;
        _orch.Shelf.ItemsChanged -= OnShelfItemsChanged;
        _orch.Clipboard.EntriesChanged -= OnClipboardEntriesChanged;
        _orch.Telemetry.TelemetryUpdated -= OnTelemetryUpdated;
        _orch.Widgets.WidgetsChanged -= OnWidgetsChanged;
        _orch.OpenSettingsRequested -= OnOrchestratorOpenSettingsRequested;
        _orch.QrShareRequested -= OnOrchestratorQrShareRequested;
        _orch.RegionOcrRequested -= OnOrchestratorRegionOcrRequested;
        _orch.RegionColorPickRequested -= OnOrchestratorRegionColorPickRequested;
        _orch.LanDrop.ShareStateChanged -= OnLanDropShareStateChanged;
        _orch.VoiceNotes.WaveformUpdated -= OnVoiceNoteWaveformUpdated;
        _orch.VoiceNotes.NoteCompleted -= OnVoiceNoteCompleted;
        _orch.Calendar.ActivityChanged -= OnCalendarActivityChanged;
        _orch.Timers.ScheduledPowerChanged -= OnScheduledPowerChanged;
    }

    // -------------------------------------------------------------------------
    // 240Hz Zero-Layout Physics & Contour Render Engine
    // -------------------------------------------------------------------------

    private void StartRenderLoop()
    {
        if (_isClosed || _isRenderAuditMode || _isRenderingLoopActive) return;
        _isRenderingLoopActive = true;
        _lastRenderTicks = Stopwatch.GetTimestamp();
        _lastFrameCommittedTicks = _lastRenderTicks;
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopRenderLoop()
    {
        if (!_isRenderingLoopActive) return;
        _isRenderingLoopActive = false;
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (_isClosed) return;

        long now = Stopwatch.GetTimestamp();
        if (_burstMeasuring)
        {
            _burstFrameCount++;
        }

        int fpsCap = _orch.Config.TargetFpsCap;
        if (fpsCap > 0)
        {
            double minTicks = Stopwatch.Frequency / (double)fpsCap * 0.90;
            if (now - _lastFrameCommittedTicks < minTicks)
            {
                return;
            }
        }

        bool preSpectrum = ShouldAnimateSpectrum(_lastSnapshot);
        bool preGlow = ShouldAnimateAmbientGlow(_lastSnapshot);
        bool settledAmbientOnly = !_burstMeasuring
            && _motion.IsAllSettled
            && !preSpectrum
            && !_isDraggingNotch
            && preGlow
            && !HasAnyInactiveVisibleSurface(_lastSnapshot);

        if (settledAmbientOnly)
        {
            // Slow silk aura drifts smoothly (~0.6 rad/s); 24 Hz keeps DWM layered compositing under ~0.2% CPU
            double ambientMinTicks = Stopwatch.Frequency / 24.0;
            if (now - _lastFrameCommittedTicks < ambientMinTicks)
            {
                return;
            }
        }

        double rawDt;
        if (_resetNextFrameDt)
        {
            _resetNextFrameDt = false;
            rawDt = 1.0 / 240.0;
        }
        else
        {
            rawDt = Math.Clamp((now - _lastRenderTicks) / (double)Stopwatch.Frequency, 0.0005, 0.06);
        }
        double springDt = Math.Min(rawDt, 0.025);
        _lastRenderTicks = now;
        _lastFrameCommittedTicks = now;

        if (settledAmbientOnly)
        {
            double w = Math.Max(0.0, _motion.Width.Value);
            double h = Math.Max(0.0, _motion.Height.Value);
            bool flush = _orch.Config.DockPlacement == DockPlacementMode.NotchFlush;
            double earR = flush ? Math.Max(0.0, _motion.EarRadius.Value) : 0.0;
            double topY = ResolveShellTopY(_lastSnapshot.State);
            double centerX = ResolveNotchCenterInHost(_tierHostWidth, w, earR);
            ApplyAmbientGlowFrame(_lastSnapshot, centerX, topY, w, h, rawDt);
        }
        else
        {
            _motion.StepAll(springDt);
            ApplyMotionFrameToVisuals(_lastSnapshot, forceHitUpdate: false, springDt);
        }

        bool spectrumRunning = ShouldAnimateSpectrum(_lastSnapshot);
        bool glowRunning = ShouldAnimateAmbientGlow(_lastSnapshot);
        if (!spectrumRunning && !AreSpectrumBarsAtRest())
        {
            _motion.ResetSpectrum(0.0);
        }

        if (_motion.IsAllSettled && !spectrumRunning && !glowRunning && !_isDraggingNotch && !HasAnyInactiveVisibleSurface(_lastSnapshot))
        {
            OnRenderSettled();
            StopRenderLoop();
        }
    }

    private bool HasAnyInactiveVisibleSurface(IslandCoordinatorSnapshot snap)
    {
        bool isHairline = snap.State == IslandPresentationState.S1Dormant &&
                          snap.Layout.HeightDip <= 8.0 &&
                          snap.Layout.WidthDip <= 80.0;
        if ((snap.State != IslandPresentationState.S1Dormant || isHairline) &&
            DormantNotchSurface.Visibility == Visibility.Visible) return true;
        if (snap.State is not (IslandPresentationState.S2Compact or IslandPresentationState.S2Split) &&
            CompactSurface.Visibility == Visibility.Visible) return true;
        if (snap.State != IslandPresentationState.S3TransientHud && TransientHudSurface.Visibility == Visibility.Visible) return true;
        if (snap.State != IslandPresentationState.S4Expanded && ExpandedSurface.Visibility == Visibility.Visible) return true;
        if (snap.State != IslandPresentationState.S5Command && CommandSurface.Visibility == Visibility.Visible) return true;
        if (snap.State != IslandPresentationState.S6DragShelf && DragShelfSurface.Visibility == Visibility.Visible) return true;
        return false;
    }

    private void OnRenderSettled()
    {
        if (_pendingCompactTierCollapse && !RequiresExpandedTier(_lastSnapshot.State))
        {
            SwitchToCompactTier();
        }
        else
        {
            ApplyMotionFrameToVisuals(_lastSnapshot, forceHitUpdate: true);
        }

        if (_lastSnapshot.State is not (IslandPresentationState.S4Expanded or IslandPresentationState.S5Command or IslandPresentationState.S6DragShelf))
        {
            _lockedExpandedMonitorHandle = 0;
        }
    }

    private bool ShouldAnimateSpectrum(IslandCoordinatorSnapshot snap)
    {
        if (!_orch.Config.EnableSpectrumVisualizer) return false;
        bool playing = (_auditMediaOverride ?? _orch.Media.Current).IsPlaying;
        if (!playing) return false;

        if (snap.State is IslandPresentationState.S2Compact or IslandPresentationState.S2Split)
        {
            return snap.PrimaryActivity?.Kind == ActivityKind.Media;
        }
        return snap.State == IslandPresentationState.S4Expanded && snap.ActiveTab == ExpandedTabId.Audio;
    }

    private bool ShouldAnimateAmbientGlow(IslandCoordinatorSnapshot snap)
    {
        if (!_orch.Config.DynamicAlbumGlowEnabled) return false;
        var media = _auditMediaOverride ?? _orch.Media.Current;
        if (!media.HasSession || !media.IsPlaying) return false;

        if (snap.State is IslandPresentationState.S2Compact or IslandPresentationState.S2Split)
        {
            return snap.PrimaryActivity?.Kind == ActivityKind.Media;
        }
        return snap.State == IslandPresentationState.S4Expanded && snap.ActiveTab == ExpandedTabId.Now;
    }

    private bool AreSpectrumBarsAtRest()
    {
        for (int i = 0; i < IslandMotionState.SpectrumBarCount; i++)
        {
            if (_motion.SpectrumBars[i] > 0.03) return false;
        }
        return true;
    }

    private double ResolveShellTopY(IslandPresentationState state)
    {
        if (_orch.Config.DockPlacement == DockPlacementMode.NotchFlush)
        {
            return 0.0;
        }
        // Leave 6 DIP headroom inside host canvas so 1.032x floating hover scale and 1px top rim never clip
        return state == IslandPresentationState.S0Hidden ? 0.0 : 6.0;
    }

    private void ApplyMotionFrameToVisuals(IslandCoordinatorSnapshot snap, bool forceHitUpdate, double dt = 0.0)
    {
        bool flush = _orch.Config.DockPlacement == DockPlacementMode.NotchFlush;
        double hostW = _tierHostWidth;
        double hostH = _tierHostHeight;
        double w = Math.Max(0.0, _motion.Width.Value);
        double h = Math.Max(0.0, _motion.Height.Value);
        double cr = Math.Max(0.0, _motion.EffectiveCornerRadius);
        double earR = flush ? Math.Max(0.0, _motion.EarRadius.Value) : 0.0;
        double topCr = flush ? 0.0 : Math.Max(0.0, _motion.TopCornerRadius.Value);
        double topY = ResolveShellTopY(snap.State);
        double centerX = ResolveNotchCenterInHost(hostW, w, earR);

        _mainNotchBuilder.Update(centerX, topY, w, h, earR, topCr, cr);

        MainOffsetTransform.X = _motion.OffsetX.Value;
        MainOffsetTransform.Y = _motion.OffsetY.Value;

        MainHoverScaleTransform.CenterX = centerX;
        MainHoverScaleTransform.CenterY = flush ? topY : topY + h * 0.5;
        MainHoverScaleTransform.ScaleX = _motion.HoverScale.Value;
        MainHoverScaleTransform.ScaleY = _motion.HoverScale.Value;

        NotchVisualRoot.Opacity = Math.Clamp(_motion.Opacity.Value, 0.0, 1.0);

        if (forceHitUpdate || _hitContourDirty)
        {
            _hitContourDirty = false;
            var t = snap.Layout;
            double hitEar = flush ? t.EarRadius : 0.0;
            double hitTopCr = flush ? 0.0 : t.TopCornerRadius;
            double hitCenterX = ResolveNotchCenterInHost(hostW, t.WidthDip, hitEar);
            double hitTopY = ResolveShellTopY(t.State);
            _mainHitNotchBuilder.Update(hitCenterX, hitTopY, t.WidthDip, t.HeightDip, hitEar, hitTopCr, t.CornerRadiusDip);
        }

        // Dynamic multi-layer macOS ambient album glow aura inside shell clip
        ApplyAmbientGlowFrame(snap, centerX, topY, w, h, dt);

        // Surface positioning & zero-overlap sequential crossfades (pure TranslateTransform + Opacity, zero layout!)
        bool isHairline = snap.State == IslandPresentationState.S1Dormant && snap.Layout.HeightDip <= 8.0 && snap.Layout.WidthDip <= 80.0;
        bool isOverlayTarget = snap.State is IslandPresentationState.S3TransientHud
            or IslandPresentationState.S4Expanded
            or IslandPresentationState.S5Command
            or IslandPresentationState.S6DragShelf;
        double compactOpacity = _motion.CompactContentOpacity;
        double compactWindowMul = isOverlayTarget
            ? _motion.OverlayCompactFadeOut
            : _motion.CollapseCompactGate;
        double compactOutgoing = isOverlayTarget
            ? _motion.OverlayCompactFadeOut
            : Math.Max(0.0, 1.0 - compactOpacity * 1.8);
        double overlayEnter = _motion.ContentRevealTier2;
        double overlayExit = _motion.OverlayCollapseFadeOut;

        if (HairlineSurface.Visibility == Visibility.Visible)
        {
            HairlineSurfaceTransform.X = centerX - HairlineSurface.Width * 0.5;
            HairlineSurfaceTransform.Y = topY + Math.Max(0.0, (h - HairlineSurface.Height) * 0.5);
            HairlineSurface.Opacity = snap.State == IslandPresentationState.S0Hidden ? 0.0 : 1.0;
        }

        if (DormantNotchSurface.Visibility == Visibility.Visible)
        {
            DormantSurfaceTransform.X = centerX - DormantNotchSurface.Width * 0.5;
            DormantSurfaceTransform.Y = topY + Math.Max(0.0, (h - DormantNotchSurface.Height) * 0.5);
            bool activeDormant = !isHairline && snap.State == IslandPresentationState.S1Dormant;
            DormantNotchSurface.Opacity = activeDormant
                ? compactOpacity * compactWindowMul
                : (_isRenderAuditMode ? 0.0 : Math.Min(DormantNotchSurface.Opacity, _motion.IsAllSettled ? 0.0 : compactOutgoing));
            if (!activeDormant && DormantNotchSurface.Opacity <= 0.015)
            {
                DormantNotchSurface.Opacity = 0.0;
                DormantNotchSurface.Visibility = Visibility.Collapsed;
            }
        }

        if (CompactSurface.Visibility == Visibility.Visible)
        {
            CompactSurfaceTransform.X = centerX - CompactSurface.Width * 0.5;
            CompactSurfaceTransform.Y = topY + Math.Max(0.0, (h - CompactSurface.Height) * 0.5);
            bool activeCompact = snap.State is IslandPresentationState.S2Compact or IslandPresentationState.S2Split;
            CompactSurface.Opacity = activeCompact
                ? compactOpacity * compactWindowMul
                : (_isRenderAuditMode ? 0.0 : Math.Min(CompactSurface.Opacity, _motion.IsAllSettled ? 0.0 : compactOutgoing));
            if (!activeCompact && CompactSurface.Opacity <= 0.015)
            {
                CompactSurface.Opacity = 0.0;
                CompactSurface.Visibility = Visibility.Collapsed;
            }
        }

        bool activeHud = snap.State == IslandPresentationState.S3TransientHud;
        bool activeDrag = snap.State == IslandPresentationState.S6DragShelf;
        bool activeCmd = snap.State == IslandPresentationState.S5Command;
        bool activeExp = snap.State == IslandPresentationState.S4Expanded;

        double maxOutgoingOverlay = Math.Max(
            Math.Max(
                StepOutgoingOverlaySurface(TransientHudSurface, activeHud, dt, overlayExit, isOverlayTarget),
                StepOutgoingOverlaySurface(DragShelfSurface, activeDrag, dt, overlayExit, isOverlayTarget)),
            Math.Max(
                StepOutgoingOverlaySurface(CommandSurface, activeCmd, dt, overlayExit, isOverlayTarget),
                StepOutgoingOverlaySurface(ExpandedSurface, activeExp, dt, overlayExit, isOverlayTarget)));

        double siblingOverlayGate = isOverlayTarget
            ? Math.Clamp(1.0 - maxOutgoingOverlay * 1.4, 0.0, 1.0)
            : 1.0;

        if (TransientHudSurface.Visibility == Visibility.Visible && activeHud)
        {
            TransientHudTransform.X = centerX - TransientHudSurface.Width * 0.5;
            TransientHudTransform.Y = topY + Math.Max(0.0, (h - TransientHudSurface.Height) * 0.5);
            TransientHudSurface.Opacity = _isRenderAuditMode
                ? 1.0
                : compactOpacity * _motion.ContentRevealTier1 * siblingOverlayGate;
        }
        else if (TransientHudSurface.Visibility == Visibility.Visible)
        {
            TransientHudTransform.X = centerX - TransientHudSurface.Width * 0.5;
            TransientHudTransform.Y = topY + Math.Max(0.0, (h - TransientHudSurface.Height) * 0.5);
        }

        if (DragShelfSurface.Visibility == Visibility.Visible)
        {
            DragShelfTransform.X = centerX - DragShelfSurface.Width * 0.5;
            DragShelfTransform.Y = topY + (flush ? 12.0 : 10.0);
            if (activeDrag)
            {
                DragShelfSurface.Opacity = _isRenderAuditMode ? 1.0 : overlayEnter * siblingOverlayGate;
            }
        }

        if (CommandSurface.Visibility == Visibility.Visible)
        {
            CommandSurfaceTransform.X = centerX - CommandSurface.Width * 0.5;
            CommandSurfaceTransform.Y = topY + (flush ? 12.0 : 11.0);
            if (activeCmd)
            {
                CommandSurface.Opacity = _isRenderAuditMode ? 1.0 : overlayEnter * siblingOverlayGate;
            }
        }

        if (ExpandedSurface.Visibility == Visibility.Visible)
        {
            ExpandedSurfaceTransform.X = centerX - ExpandedSurface.Width * 0.5;
            ExpandedSurfaceTransform.Y = topY + (flush ? 10.0 : 11.0);
            double expAlpha = activeExp
                ? (_isRenderAuditMode ? 1.0 : overlayEnter * siblingOverlayGate)
                : ExpandedSurface.Opacity;
            if (activeExp)
            {
                ExpandedSurface.Opacity = expAlpha;
            }
            double sc = activeExp
                ? 0.965 + 0.035 * expAlpha
                : 0.985 + 0.015 * expAlpha;
            ExpandedSurfaceScaleTransform.CenterX = ExpandedSurface.Width * 0.5;
            ExpandedSurfaceScaleTransform.ScaleX = sc;
            ExpandedSurfaceScaleTransform.ScaleY = sc;

            TabContentHost.Opacity = _motion.TabContentOpacity;
            TabContentSlideTransform.Y = _motion.TabContentTranslateY;
            TabContentScaleTransform.ScaleX = _motion.TabContentScale;
            TabContentScaleTransform.ScaleY = _motion.TabContentScale;

            TabIndicatorPill.Width = Math.Max(36.0, _motion.TabIndicatorWidth.Value);
            TabIndicatorTransform.X = _motion.TabIndicatorX.Value;
        }

        // Spectrum 8-bar kinetic scale updates (pure ScaleTransform)
        if (CompactSpectrumCanvas.Visibility == Visibility.Visible || AudioSpectrumCanvas.IsVisible)
        {
            for (int i = 0; i < IslandMotionState.SpectrumBarCount; i++)
            {
                double val = Math.Clamp(_motion.SpectrumBars[i], 0.12, 1.0);
                _compactSpecScales[i].ScaleY = val;
                _audioSpecScales[i].ScaleY = val;
            }
        }

        // Split Satellite Wing (S2Split)
        double satW = Math.Max(0.0, _motion.SatelliteWidth.Value);
        double satScale = _motion.SatelliteEffectiveScale;
        double satAlpha = _motion.SatelliteOpacity;
        if (satW > 1.0 && satAlpha > 0.02)
        {
            if (SatelliteVisualRoot.Visibility != Visibility.Visible)
            {
                SatelliteVisualRoot.Visibility = Visibility.Visible;
            }

            double satH = Math.Max(20.0, _motion.SatelliteHeight.Value);
            double satGap = Math.Max(4.0, _motion.SatelliteGap.Value);
            double satEar = flush ? Math.Min(earR, 9.0) : 0.0;
            double satTopCr = flush ? 0.0 : satH * 0.5;
            double satCr = flush ? Math.Min(16.0, satH * 0.45) : satH * 0.5;

            double satBodyLeftX = centerX + w * 0.5 + earR + satGap + satEar;
            double satCanvasX = satBodyLeftX - satEar - 2.0;
            double satLocalCenterX = 2.0 + satEar + satW * 0.5;

            SatelliteTranslateTransform.X = satCanvasX + _motion.OffsetX.Value;
            SatelliteTranslateTransform.Y = _motion.OffsetY.Value;

            _satNotchBuilder.Update(satLocalCenterX, topY, satW, satH, satEar, satTopCr, satCr);
            if (forceHitUpdate || _hitContourDirty)
            {
                double targetSatW = snap.Layout.SatelliteWidthDip;
                double targetSatGap = Math.Max(4.0, snap.Layout.SatelliteGapDip);
                double targetSatEar = flush ? Math.Min(snap.Layout.EarRadius, 9.0) : 0.0;
                double targetSatLocalCenterX = 2.0 + targetSatEar + targetSatW * 0.5;
                if (targetSatW > 1.0)
                {
                    _satHitNotchBuilder.Update(targetSatLocalCenterX, topY, targetSatW, satH, targetSatEar, satTopCr, satCr);
                }
            }

            SatelliteScaleTransform.CenterX = satLocalCenterX;
            SatelliteScaleTransform.CenterY = flush ? topY : topY + satH * 0.5;
            SatelliteScaleTransform.ScaleX = satScale;
            SatelliteScaleTransform.ScaleY = satScale;

            SatelliteHoverScaleTransform.CenterX = satLocalCenterX;
            SatelliteHoverScaleTransform.CenterY = flush ? topY : topY + satH * 0.5;
            SatelliteHoverScaleTransform.ScaleX = _isSatelliteHovered && _orch.Config.ExpandTrigger != ExpandTriggerMode.ClickOnly ? 1.04 : 1.0;
            SatelliteHoverScaleTransform.ScaleY = SatelliteHoverScaleTransform.ScaleX;

            SatelliteVisualRoot.Opacity = satAlpha;
            SatelliteContentTransform.X = 2.0 + satEar + Math.Max(0.0, (satW - SatelliteInnerGrid.Width) * 0.5);
            SatelliteContentTransform.Y = topY + Math.Max(0.0, (satH - SatelliteInnerGrid.Height) * 0.5);
            if (_orch.Config.DynamicAlbumGlowEnabled && snap.PrimaryActivity?.Kind == ActivityKind.Media)
            {
                bool satPlaying = (_auditMediaOverride ?? _orch.Media.Current).IsPlaying;
                SatelliteGlowRect.Opacity = satAlpha * (satPlaying ? 0.62 : 0.28);
                SatelliteGlowTransform.X = 2.0 + satEar + satW * 0.5 - 70.0 + Math.Cos(_glowAnimPhase * 0.72) * 10.0;
                SatelliteGlowTransform.Y = topY + satH * 0.5 - 30.0 + Math.Sin(_glowAnimPhase * 0.88) * 3.5;
            }
            else
            {
                SatelliteGlowRect.Opacity = 0.0;
            }
        }
        else if (SatelliteVisualRoot.Visibility == Visibility.Visible)
        {
            SatelliteVisualRoot.Visibility = Visibility.Collapsed;
            SatelliteGlowRect.Opacity = 0.0;
        }

        _hitContourDirty = false;
    }

    private void ApplyAmbientGlowFrame(IslandCoordinatorSnapshot snap, double centerX, double topY, double w, double h, double dt)
    {
        var media = _auditMediaOverride ?? _orch.Media.Current;
        bool eligibleSurface = snap.State switch
        {
            IslandPresentationState.S2Compact or IslandPresentationState.S2Split when snap.PrimaryActivity?.Kind == ActivityKind.Media => true,
            IslandPresentationState.S4Expanded when snap.ActiveTab == ExpandedTabId.Now && media.HasSession => true,
            _ => false
        };

        if (!_orch.Config.DynamicAlbumGlowEnabled || !eligibleSurface || w <= 60 || h <= 16)
        {
            AccentGlowHost.Opacity = 0.0;
            return;
        }

        if (_isRenderAuditMode)
        {
            _glowAnimPhase = 0.95;
        }
        else if (media.IsPlaying && dt > 0.0)
        {
            _glowAnimPhase += dt * 0.62;
            if (_glowAnimPhase > Math.PI * 200.0)
            {
                _glowAnimPhase -= Math.PI * 200.0;
            }
        }

        bool isCompact = snap.State is IslandPresentationState.S2Compact or IslandPresentationState.S2Split;
        double baseGlow = isCompact
            ? (media.IsPlaying ? 0.58 : 0.24)
            : (media.IsPlaying ? 0.64 : 0.26);
        AccentGlowHost.Opacity = Math.Clamp(baseGlow + _motion.AlbumAuraPulse.Value * 0.24, 0.0, 0.88);

        // Full-shell diffused silk undertone wash so there are no dark gaps isolating blobs into dots
        AccentGlowWashScale.ScaleX = Math.Max(0.1, w / 640.0);
        AccentGlowWashScale.ScaleY = Math.Max(0.05, h / 300.0);
        AccentGlowWashTransform.X = centerX - w * 0.5;
        AccentGlowWashTransform.Y = topY;

        double p1 = _glowAnimPhase * 0.82;
        double p2 = _glowAnimPhase * 1.07 + 2.094;
        double p3 = _glowAnimPhase * 1.29 + 4.188;

        double orbitRx = Math.Clamp(w * 0.24, 34.0, 170.0);
        double orbitRy = Math.Clamp(h * 0.16, 4.0, 44.0);
        double scaleXBase = Math.Clamp((w * 1.15) / 520.0, 0.58, 1.38);
        double scaleYBase = isCompact
            ? Math.Clamp((h * 2.8) / 340.0, 0.34, 0.92)
            : Math.Clamp((h * 1.15) / 340.0, 0.72, 1.22);

        AccentGlowPrimaryTransform.X = centerX - 260.0 + Math.Cos(p1) * orbitRx * 0.78;
        AccentGlowPrimaryTransform.Y = topY + h * 0.48 - 170.0 + Math.Sin(p1 * 1.2) * orbitRy * 0.70;
        AccentGlowPrimaryScale.ScaleX = scaleXBase * (1.0 + 0.10 * Math.Sin(p1 * 0.8));
        AccentGlowPrimaryScale.ScaleY = scaleYBase * (1.0 + 0.10 * Math.Cos(p1 * 0.95));

        AccentGlowSecondaryTransform.X = centerX - 240.0 - Math.Cos(p2 * 0.92) * orbitRx * 0.88;
        AccentGlowSecondaryTransform.Y = topY + h * 0.54 - 160.0 + Math.Sin(p2) * orbitRy * 0.75;
        AccentGlowSecondaryScale.ScaleX = scaleXBase * (0.96 + 0.12 * Math.Cos(p2 * 1.05));
        AccentGlowSecondaryScale.ScaleY = scaleYBase * (0.96 + 0.12 * Math.Sin(p2 * 0.85));

        AccentGlowHighlightTransform.X = centerX - 220.0 + Math.Sin(p3) * orbitRx * 0.92;
        AccentGlowHighlightTransform.Y = topY + h * 0.42 - 150.0 - Math.Cos(p3 * 1.15) * orbitRy * 0.68;
        AccentGlowHighlightScale.ScaleX = scaleXBase * (0.92 + 0.12 * Math.Sin(p3 * 1.15));
        AccentGlowHighlightScale.ScaleY = scaleYBase * (0.92 + 0.12 * Math.Cos(p3 * 0.9));
    }

    private double ResolveNotchCenterInHost(double hostW, double notchW, double earR)
    {
        if (_isRenderAuditMode)
        {
            return hostW * 0.5;
        }

        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        double monLeftDip = _activeMonitor.LeftPx / scale;
        double monWidthDip = _activeMonitor.WidthPx / scale;
        double ratio = MonitorLayoutCalculator.ResolveAnchorRatio(_orch.Config.HorizontalDetent, _orch.Config.CustomHorizontalRatio);
        double targetCenterDip = monLeftDip + monWidthDip * ratio;

        var placement = MonitorLayoutCalculator.ComputeHostWindowBounds(
            _activeMonitor,
            hostW,
            _tierHostHeight,
            _orch.Config.HorizontalDetent,
            _orch.Config.CustomHorizontalRatio,
            _orch.Config.DockPlacement,
            _orch.Config.CustomFreeTopDip);

        double localCenter = targetCenterDip - placement.LeftDip;
        double margin = notchW * 0.5 + earR + 2.0;
        double minX = Math.Min(hostW * 0.5, margin);
        double maxX = Math.Max(minX, hostW - margin);
        return Math.Clamp(localCenter, minX, maxX);
    }

    // -------------------------------------------------------------------------
    // Coordinator Snapshot Application & Tier Lifecycle
    // -------------------------------------------------------------------------

    private void OnCoordinatorSnapshotChanged(object? sender, IslandCoordinatorSnapshot snap)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            RenderCoordinatorSnapshot(_orch.Coordinator.Current, isInitial: false);
        });
    }

    private void RenderCoordinatorSnapshot(IslandCoordinatorSnapshot snap, bool isInitial)
    {
        var prevSnap = _lastSnapshot;
        _lastSnapshot = snap;

        bool stateChanged = isInitial || prevSnap.State != snap.State;
        bool tabChanged = isInitial || prevSnap.ActiveTab != snap.ActiveTab;
        bool geometryChanged = isInitial || !IslandMotionState.HasSameLayoutGeometry(prevSnap.Layout, snap.Layout);
        bool expanding = IsStateHigher(snap.State, prevSnap.State) ||
                         (snap.State == prevSnap.State && snap.Layout.HeightDip > prevSnap.Layout.HeightDip + 0.5);

        if (geometryChanged)
        {
            _hitContourDirty = true;
        }

        // Update FollowCursor monitor lock BEFORE computing placement
        if (!_isRenderAuditMode)
        {
            if (RequiresExpandedTier(snap.State))
            {
                if (_lockedExpandedMonitorHandle == 0 && _activeMonitor.Handle != 0)
                {
                    _lockedExpandedMonitorHandle = _activeMonitor.Handle;
                }
            }

            _activeMonitor = ResolveTargetMonitor(snap);
            ApplyTierAndMonitorPlacement(snap, snapImmediately: isInitial || _isRenderAuditMode);

            bool shouldClickThrough = (snap.Layout.IsClickThrough || _shiftAutoDodgeActive) && !snap.IsDragCatcherArmed;
            _controller.SetClickThrough(shouldClickThrough);
            _controller.SetDragCatcherArmed(snap.IsDragCatcherArmed);
            _controller.SetActivatable(snap.Layout.RequiresKeyboardFocus, restoreFocusHwnd: null);
            _controller.SetVisible(snap.State != IslandPresentationState.S0Hidden);
        }

        // Strictly scope DragCatcherBand: NEVER cover 720x424 empty space when S6DragShelf or collapsed idle
        DragCatcherBand.Visibility = (snap.IsDragCatcherArmed &&
                                      !_isInternalDragOut &&
                                      snap.State != IslandPresentationState.S6DragShelf &&
                                      !RequiresExpandedTier(snap.State))
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Motion targets
        _motion.ApplyTarget(snap.Layout, expanding);
        if (isInitial || _isRenderAuditMode)
        {
            SnapMotionToTarget(snap.Layout);
        }

        if (tabChanged && snap.State == IslandPresentationState.S4Expanded)
        {
            _motion.TriggerTabTransition();
        }

        // Adaptive service sampling & widget pause policy
        bool widgetsTabVisible = snap.State == IslandPresentationState.S4Expanded && snap.ActiveTab == ExpandedTabId.Widgets;
        bool systemVisible = snap.State == IslandPresentationState.S4Expanded && snap.ActiveTab == ExpandedTabId.System;
        bool dormantPill = _orch.Config.ShowTelemetryInPill && snap.State == IslandPresentationState.S1Dormant;
        bool spectrumNeeded = _orch.Config.EnableSpectrumVisualizer &&
                              ((snap.State is IslandPresentationState.S2Compact or IslandPresentationState.S2Split &&
                                snap.PrimaryActivity?.Kind == ActivityKind.Media) ||
                               (snap.State == IslandPresentationState.S4Expanded && snap.ActiveTab == ExpandedTabId.Audio));

        if (!_isRenderAuditMode)
        {
            _orch.Widgets.SetPausedWhenCollapsed(!widgetsTabVisible);
            _orch.UpdateTelemetryUiState(systemVisible, dormantPill);
            _orch.Audio.SetSpectrumRequested(spectrumNeeded);
        }

        // Discrete surface sizing & content hydration
        ApplySurfaceSizing(snap);
        ActivateSurfaceForState(snap);

        if (stateChanged || tabChanged || isInitial)
        {
            ApplyLocalization();
        }
        else
        {
            ApplyHeaderButtonStates(snap);
        }

        SyncMediaPositionTimerState();

        UpdateCompactAndSatelliteContent(snap);
        UpdateTransientHudContent(snap);

        if (snap.State == IslandPresentationState.S4Expanded)
        {
            bool enterExpanded = isInitial || !wasAlreadyExpanded(prevSnap);
            _snapTabIndicatorOnMeasure = enterExpanded;
            SelectExpandedTabVisual(snap.ActiveTab, snapIndicator: enterExpanded);
            if (stateChanged || tabChanged || IsTabDirty(snap.ActiveTab))
            {
                RefreshExpandedTabPanel(snap.ActiveTab);
            }
        }
        else
        {
            CloseExpandedOverlays();
            if (snap.State == IslandPresentationState.S5Command && stateChanged)
            {
                _s5CommandActivatedOnce = false;
                _s5CommandOpenedMs = Environment.TickCount64;
                FocusCommandPalette();
            }
            else if (snap.State != IslandPresentationState.S5Command)
            {
                _s5CommandActivatedOnce = false;
            }
        }

        ApplyMotionFrameToVisuals(snap, forceHitUpdate: isInitial || _isRenderAuditMode);

        if (!_isRenderAuditMode)
        {
            if (stateChanged)
            {
                _resetNextFrameDt = true;
            }
            _lastRenderTicks = Stopwatch.GetTimestamp();
            if (!_motion.IsAllSettled ||
                ShouldAnimateSpectrum(snap) ||
                ShouldAnimateAmbientGlow(snap) ||
                _isDraggingNotch ||
                HasAnyInactiveVisibleSurface(snap))
            {
                StartRenderLoop();
            }
        }
    }

    private static bool wasAlreadyExpanded(IslandCoordinatorSnapshot prevSnap) =>
        prevSnap.State == IslandPresentationState.S4Expanded;

    private static bool RequiresExpandedTier(IslandPresentationState state) =>
        state is IslandPresentationState.S4Expanded or IslandPresentationState.S5Command or IslandPresentationState.S6DragShelf;

    private static bool IsStateHigher(IslandPresentationState next, IslandPresentationState prev)
    {
        int Rank(IslandPresentationState s) => s switch
        {
            IslandPresentationState.S0Hidden => 0,
            IslandPresentationState.S1Dormant => 1,
            IslandPresentationState.S2Compact => 2,
            IslandPresentationState.S2Split => 2,
            IslandPresentationState.S3TransientHud => 3,
            IslandPresentationState.S6DragShelf => 4,
            IslandPresentationState.S4Expanded => 5,
            IslandPresentationState.S5Command => 6,
            _ => 1
        };
        return Rank(next) > Rank(prev);
    }

    private MonitorDescriptor ResolveTargetMonitor(IslandCoordinatorSnapshot snap)
    {
        if (_orch.Config.MonitorMode == MonitorPlacementMode.FollowCursor &&
            RequiresExpandedTier(snap.State) &&
            _lockedExpandedMonitorHandle != 0)
        {
            var locked = _orch.MonitorLayout.GetMonitors().FirstOrDefault(m => m.Handle == _lockedExpandedMonitorHandle);
            if (locked is not null)
            {
                return locked;
            }
        }

        var (cx, cy) = IslandWindowController.GetCursorScreenPos();
        return _orch.MonitorLayout.GetTargetMonitor(_orch.Config.MonitorMode, _orch.Config.PinnedMonitorDeviceName, cx, cy);
    }

    private void ApplyTierAndMonitorPlacement(IslandCoordinatorSnapshot snap, bool snapImmediately)
    {
        if (_activeMonitor.Handle != 0 && _activeMonitor.Handle != _lastReportedIslandMonitor)
        {
            _lastReportedIslandMonitor = _activeMonitor.Handle;
            _orch.Coordinator.SetIslandMonitorHandle(_activeMonitor.Handle);
        }

        bool needExpanded = RequiresExpandedTier(snap.State);
        var placement = MonitorLayoutCalculator.ComputeHostWindowBounds(
            _activeMonitor,
            needExpanded ? ExpandedTierW : CompactTierW,
            needExpanded ? ExpandedTierH : CompactTierH,
            _orch.Config.HorizontalDetent,
            _orch.Config.CustomHorizontalRatio,
            _orch.Config.DockPlacement,
            _orch.Config.CustomFreeTopDip);

        if (needExpanded)
        {
            _pendingCompactTierCollapse = false;
            if (!_isExpandedTierActive || _tierHostWidth != ExpandedTierW || _tierHostHeight != ExpandedTierH)
            {
                _isExpandedTierActive = true;
                SetTierCanvasSize(ExpandedTierW, ExpandedTierH);
                ApplyMotionFrameToVisuals(snap, forceHitUpdate: true);
                RootSurface.UpdateLayout();
            }

            if (_isRenderAuditMode)
            {
                _controller.EnsureExpandedHwndTier(ExpandedTierW, ExpandedTierH, placement.LeftDip, placement.TopDip);
            }
            else
            {
                _controller.EnsureExpandedHwnd(placement.LeftPx, placement.TopPx, placement.WidthPx, placement.HeightPx);
            }
        }
        else if (_isExpandedTierActive && !snapImmediately)
        {
            // Keep expanded HWND & canvas alive while shrinking smoothly down to compact bounds
            _pendingCompactTierCollapse = true;
        }
        else
        {
            _pendingCompactTierCollapse = false;
            if (_isExpandedTierActive || _tierHostWidth != CompactTierW || _tierHostHeight != CompactTierH)
            {
                _isExpandedTierActive = false;
                SetTierCanvasSize(CompactTierW, CompactTierH);
                ApplyMotionFrameToVisuals(snap, forceHitUpdate: true);
                RootSurface.UpdateLayout();
            }
            _controller.SetWindowTierBounds(CompactTierW, CompactTierH, placement.LeftDip, placement.TopDip);
        }
    }

    private void SwitchToCompactTier()
    {
        _pendingCompactTierCollapse = false;
        _isExpandedTierActive = false;
        SetTierCanvasSize(CompactTierW, CompactTierH);

        var placement = MonitorLayoutCalculator.ComputeHostWindowBounds(
            _activeMonitor,
            CompactTierW,
            CompactTierH,
            _orch.Config.HorizontalDetent,
            _orch.Config.CustomHorizontalRatio,
            _orch.Config.DockPlacement,
            _orch.Config.CustomFreeTopDip);

        ApplyMotionFrameToVisuals(_lastSnapshot, forceHitUpdate: true);
        RootSurface.UpdateLayout();
        _controller.CollapseHwndTierNow(CompactTierW, CompactTierH, placement.LeftDip, placement.TopDip);
    }

    private void SetTierCanvasSize(double w, double h)
    {
        _tierHostWidth = w;
        _tierHostHeight = h;
        TierHostCanvas.Width = w;
        TierHostCanvas.Height = h;
        MainNotchAssembly.Width = w;
        MainNotchAssembly.Height = h;
        NotchContentCanvas.Width = w;
        NotchContentCanvas.Height = h;
        DragCatcherBand.Width = w;
        Width = w;
        Height = h;
    }

    private void SnapMotionToTarget(IslandLayoutTarget target)
    {
        _motion.Width.SnapTo(target.WidthDip);
        _motion.Height.SnapTo(target.HeightDip);
        _motion.CornerRadius.SnapTo(target.CornerRadiusDip);
        _motion.EarRadius.SnapTo(target.EarRadius);
        _motion.TopCornerRadius.SnapTo(target.TopCornerRadius);
        _motion.SatelliteWidth.SnapTo(target.SatelliteWidthDip);
        _motion.SatelliteHeight.SnapTo(target.SatelliteWidthDip > 0.5 ? Math.Min(36.0, target.HeightDip) : 26.0);
        _motion.SatelliteGap.SnapTo(target.SatelliteGapDip);
        _motion.SatelliteScale.SnapTo(target.SatelliteWidthDip > 0.5 ? 1.0 : 0.0);
        _motion.HoverScale.SnapTo(1.0);
        _motion.OffsetX.SnapTo(0.0);
        _motion.OffsetY.SnapTo(0.0);
        _motion.Opacity.SnapTo(target.State == IslandPresentationState.S0Hidden ? 0.0 : 1.0);
        bool isOverlay = target.State is IslandPresentationState.S3TransientHud
            or IslandPresentationState.S4Expanded
            or IslandPresentationState.S5Command
            or IslandPresentationState.S6DragShelf;
        _motion.CompactContentReveal.SnapTo(target.State == IslandPresentationState.S0Hidden ? 0.0 : 1.0);
        _motion.ContentReveal.SnapTo(isOverlay ? 1.0 : 0.0);
        _motion.TabContentSlide.SnapTo(1.0);
        _motion.AlbumScale.SnapTo(1.0);
        _motion.AlbumAuraPulse.SnapTo(0.0);
    }

    private void ApplySurfaceSizing(IslandCoordinatorSnapshot snap)
    {
        double scale = AppConfig.GetScaleFactor(_orch.Config.NotchScalePreset);
        var layout = snap.Layout;

        switch (snap.State)
        {
            case IslandPresentationState.S1Dormant:
                DormantNotchSurface.Width = Math.Max(36.0, layout.WidthDip - 14.0 * scale);
                DormantNotchSurface.Height = Math.Max(18.0, layout.HeightDip - 4.0 * scale);
                break;

            case IslandPresentationState.S2Compact:
            case IslandPresentationState.S2Split:
                CompactSurface.Width = Math.Max(60.0, layout.WidthDip - 20.0 * scale);
                CompactSurface.Height = Math.Max(22.0, layout.HeightDip - 6.0 * scale);
                break;

            case IslandPresentationState.S3TransientHud:
                TransientHudSurface.Width = Math.Max(120.0, layout.WidthDip - 24.0 * scale);
                TransientHudSurface.Height = Math.Max(24.0, layout.HeightDip - 8.0 * scale);
                break;

            case IslandPresentationState.S6DragShelf:
                DragShelfSurface.Width = Math.Max(240.0, layout.WidthDip - 28.0 * scale);
                DragShelfSurface.Height = Math.Max(90.0, layout.HeightDip - 24.0 * scale);
                break;

            case IslandPresentationState.S5Command:
                CommandSurface.Width = Math.Max(360.0, layout.WidthDip - 28.0 * scale);
                CommandSurface.Height = Math.Max(240.0, layout.HeightDip - 26.0 * scale);
                break;

            case IslandPresentationState.S4Expanded:
                ExpandedSurface.Width = Math.Max(420.0, layout.WidthDip - 28.0 * scale);
                ExpandedSurface.Height = Math.Max(190.0, layout.HeightDip - 22.0 * scale);
                ExpandedSurfaceScaleTransform.CenterX = ExpandedSurface.Width * 0.5;
                TabContentScaleTransform.CenterX = ExpandedSurface.Width * 0.5;
                break;
        }
    }

    private void ActivateSurfaceForState(IslandCoordinatorSnapshot snap)
    {
        bool isHairline = snap.State == IslandPresentationState.S1Dormant &&
                          snap.Layout.HeightDip <= 8.0 &&
                          snap.Layout.WidthDip <= 80.0;

        HairlineSurface.Visibility = isHairline ? Visibility.Visible : Visibility.Collapsed;

        UIElement? nextPrimary = snap.State switch
        {
            IslandPresentationState.S0Hidden => null,
            IslandPresentationState.S1Dormant => isHairline ? null : DormantNotchSurface,
            IslandPresentationState.S2Compact or IslandPresentationState.S2Split => CompactSurface,
            IslandPresentationState.S3TransientHud => TransientHudSurface,
            IslandPresentationState.S4Expanded => ExpandedSurface,
            IslandPresentationState.S5Command => CommandSurface,
            IslandPresentationState.S6DragShelf => DragShelfSurface,
            _ => null
        };

        if (nextPrimary is not null)
        {
            nextPrimary.Visibility = Visibility.Visible;
        }

        DormantNotchSurface.IsHitTestVisible = nextPrimary == DormantNotchSurface;
        CompactSurface.IsHitTestVisible = nextPrimary == CompactSurface;
        TransientHudSurface.IsHitTestVisible = nextPrimary == TransientHudSurface;
        ExpandedSurface.IsHitTestVisible = nextPrimary == ExpandedSurface;
        CommandSurface.IsHitTestVisible = nextPrimary == CommandSurface;
        DragShelfSurface.IsHitTestVisible = nextPrimary == DragShelfSurface;

        if (_isRenderAuditMode && _lastPrimarySurface is not null && _lastPrimarySurface != nextPrimary)
        {
            _lastPrimarySurface.Opacity = 0.0;
            _lastPrimarySurface.Visibility = Visibility.Collapsed;
        }
        _lastPrimarySurface = nextPrimary;
    }

    private double StepOutgoingOverlaySurface(UIElement surface, bool isActive, double dt, double overlayExit, bool isOverlayTarget)
    {
        surface.IsHitTestVisible = isActive;
        if (isActive || surface.Visibility != Visibility.Visible)
        {
            return 0.0;
        }

        if (_isRenderAuditMode)
        {
            surface.Opacity = 0.0;
            surface.Visibility = Visibility.Collapsed;
            return 0.0;
        }

        double next = isOverlayTarget
            ? Math.Max(0.0, surface.Opacity - Math.Max(dt, 1.0 / 240.0) * 16.0)
            : Math.Min(surface.Opacity, _motion.IsAllSettled ? 0.0 : overlayExit);

        surface.Opacity = next;
        if (next <= 0.015)
        {
            surface.Opacity = 0.0;
            surface.Visibility = Visibility.Collapsed;
            return 0.0;
        }

        return next;
    }

    // -------------------------------------------------------------------------
    // Compact, Dormant, Satellite, HUD Content Sync
    // -------------------------------------------------------------------------

    private void UpdateCompactAndSatelliteContent(IslandCoordinatorSnapshot snap)
    {
        // Dormant notch camera lens + telemetry
        DormantCameraLensHost.Visibility = _orch.Config.DormantCameraLensEnabled ? Visibility.Visible : Visibility.Collapsed;

        var privacy = _auditPrivacyOverride ?? _orch.Privacy.Current;
        bool effectiveMic = (_auditPrivacyOverride is not null || _orch.Config.ShowMicPrivacyAlert) && privacy.IsMicActive;
        bool sensorActive = effectiveMic || privacy.IsCamActive;
        DormantStatusDot.Opacity = sensorActive ? 1.0 : 0.0;
        DormantStatusDot.Fill = privacy.IsCamActive ? ActiveAccentDotBrush : MicLiveBrush;

        if (_orch.Config.ShowTelemetryInPill && snap.State == IslandPresentationState.S1Dormant)
        {
            var telem = _auditTelemetryOverride ?? _orch.Telemetry.Current;
            DormantTelemetryText.Text = $"CPU {telem.CpuPercent:F0}%";
            DormantTelemetryText.Visibility = Visibility.Visible;
        }
        else
        {
            DormantTelemetryText.Visibility = Visibility.Collapsed;
        }

        // Primary compact activity
        var prim = snap.PrimaryActivity;
        var media = _auditMediaOverride ?? _orch.Media.Current;
        if (prim is not null)
        {
            CompactPrimaryText.Text = prim.PrimaryText;
            CompactSecondaryText.Text = prim.SecondaryText ?? string.Empty;
            CompactSecondaryText.Visibility = string.IsNullOrWhiteSpace(prim.SecondaryText) ? Visibility.Collapsed : Visibility.Visible;

            bool isMediaPrimary = prim.Kind == ActivityKind.Media;
            CompactSensorDot.Visibility = (prim.HasMicActive || prim.HasCamActive || sensorActive)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (isMediaPrimary)
            {
                string? lyric = _auditLyricOverride ?? (!_isRenderAuditMode ? _orch.GetCurrentLyricLine(media) : null);
                if (!string.IsNullOrWhiteSpace(lyric))
                {
                    CompactSecondaryText.Text = $"♪ {lyric}";
                    CompactSecondaryText.Visibility = Visibility.Visible;
                }

                string mediaAccent = string.IsNullOrWhiteSpace(prim.AccentHex) ? media.AccentHex : prim.AccentHex;
                UpdateMediaArtworkIfChanged(prim.ThumbnailBytes ?? media.ThumbnailBytes, mediaAccent, media.SecondaryAccentHex, media.HighlightAccentHex);
                CompactLeadingGlyph.Text = "\uE8D6";
                CompactLeadingGlyph.Visibility = CompactAlbumImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
                bool showSpec = _orch.Config.EnableSpectrumVisualizer && media.IsPlaying;
                CompactSpectrumCanvas.Visibility = showSpec ? Visibility.Visible : Visibility.Collapsed;
                CompactBadgeText.Visibility = showSpec ? Visibility.Collapsed : Visibility.Visible;
                CompactBadgeText.Text = media.IsPlaying ? "LIVE" : "II";
                CompactBadgeText.Foreground = ParseHexBrush(prim.AccentHex, "#0A84FF");
            }
            else
            {
                ClearCompactAlbumThumbnailOnly();
                CompactLeadingGlyph.Text = string.IsNullOrWhiteSpace(prim.IconGlyph) ? ResolveActivityGlyph(prim.Kind) : prim.IconGlyph;
                CompactLeadingGlyph.Visibility = Visibility.Visible;
                CompactSpectrumCanvas.Visibility = Visibility.Collapsed;

                if (!string.IsNullOrWhiteSpace(prim.SecondaryText) && prim.Kind == ActivityKind.Timer)
                {
                    CompactBadgeText.Text = prim.PrimaryText;
                    CompactPrimaryText.Text = prim.SecondaryText;
                    CompactSecondaryText.Text = Strings.FocusTimerTitle(_orch.Config.Language);
                    CompactSecondaryText.Visibility = Visibility.Visible;
                    CompactBadgeText.Visibility = Visibility.Visible;
                    CompactBadgeText.Foreground = ParseHexBrush(prim.AccentHex, "#FF9F0A");
                }
                else if (prim.Progress01.HasValue)
                {
                    CompactBadgeText.Text = $"{Math.Clamp(prim.Progress01.Value * 100.0, 0, 100):F0}%";
                    CompactBadgeText.Visibility = Visibility.Visible;
                    CompactBadgeText.Foreground = ParseHexBrush(prim.AccentHex, "#64D2FF");
                }
                else
                {
                    CompactBadgeText.Visibility = Visibility.Collapsed;
                }
            }
        }

        // Split Satellite Pill
        var sat = snap.SatelliteActivity;
        if (sat is not null)
        {
            SatelliteIconText.Text = string.IsNullOrWhiteSpace(sat.IconGlyph) ? ResolveActivityGlyph(sat.Kind) : sat.IconGlyph;
            SatelliteAccentDot.Fill = ParseHexBrush(sat.AccentHex, "#FF9F0A");
            if (!string.IsNullOrWhiteSpace(sat.PrimaryText))
            {
                SatellitePrimaryText.Text = sat.PrimaryText;
                SatellitePrimaryText.Visibility = Visibility.Visible;
                SatelliteInnerGrid.Width = 84.0;
            }
            else
            {
                SatellitePrimaryText.Visibility = Visibility.Collapsed;
                SatelliteInnerGrid.Width = 32.0;
            }
        }
    }

    private void ClearCompactAlbumThumbnailOnly()
    {
        if (CompactAlbumImage.Source is not null)
        {
            CompactAlbumImage.Source = null;
        }
    }

    private void UpdateTransientHudContent(IslandCoordinatorSnapshot snap)
    {
        var hud = snap.ActiveHud;
        if (hud is null) return;

        HudTitleText.Text = hud.Title;
        string? subtitle = hud.Subtitle;
        HudIconGlyph.Text = string.IsNullOrWhiteSpace(hud.IconGlyph) ? ResolveHudGlyph(hud.Kind) : hud.IconGlyph;

        var accent = ParseHexBrush(hud.AccentHex, "#0A84FF");
        if (hud.Kind == HudKind.ColorPicked)
        {
            var c = accent is SolidColorBrush scb ? scb.Color : Colors.White;
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            HudIconWell.Background = accent;
            HudIconWell.BorderBrush = CardBorderBrush;
            HudIconGlyph.Foreground = lum >= 0.58 ? DefaultShellBrush : ActiveTabFgBrush;
        }
        else
        {
            HudIconWell.Background = DefaultHudWellBgBrush;
            HudIconWell.BorderBrush = DefaultHudWellBorderBrush;
            HudIconGlyph.Foreground = accent;
        }
        HudProgressBar.Foreground = accent;

        if (hud.Value01.HasValue)
        {
            double v = Math.Clamp(hud.Value01.Value, 0.0, 1.0);
            string pctText = $"{Math.Round(v * 100):F0}%";
            if (string.Equals(subtitle?.Trim(), pctText, StringComparison.Ordinal))
            {
                subtitle = null;
            }
            HudProgressBar.Value = v;
            HudProgressBar.Visibility = Visibility.Visible;
            HudPercentText.Text = pctText;
            HudPercentText.Visibility = Visibility.Visible;
        }
        else
        {
            HudProgressBar.Visibility = Visibility.Collapsed;
            HudPercentText.Visibility = Visibility.Collapsed;
        }

        HudSubtitleText.Text = subtitle ?? string.Empty;
        HudSubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(hud.PrimaryActionLabel))
        {
            HudActionButton.Content = hud.PrimaryActionLabel;
            HudActionButton.Visibility = Visibility.Visible;
        }
        else if (hud.InteractiveOnHover && hud.Kind is HudKind.FileStashed or HudKind.Clipboard or HudKind.NotificationPulse or HudKind.TimerAlert or HudKind.TransferComplete or HudKind.QrShare or HudKind.OcrCapture or HudKind.VoiceNote)
        {
            HudActionButton.Content = hud.Kind switch
            {
                HudKind.FileStashed => Strings.TabShelf(_orch.Config.Language),
                HudKind.Clipboard => Strings.TabShelf(_orch.Config.Language),
                HudKind.QrShare => Strings.TabShelf(_orch.Config.Language),
                HudKind.OcrCapture => Strings.TabShelf(_orch.Config.Language),
                HudKind.VoiceNote => Strings.TabShelf(_orch.Config.Language),
                HudKind.TransferComplete => Strings.TransferStashAction(_orch.Config.Language),
                HudKind.TimerAlert => Strings.TabNow(_orch.Config.Language),
                _ => Strings.OpenHub(_orch.Config.Language)
            };
            HudActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            HudActionButton.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(hud.SecondaryActionLabel))
        {
            HudSecondaryActionButton.Content = hud.SecondaryActionLabel;
            HudSecondaryActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            HudSecondaryActionButton.Visibility = Visibility.Collapsed;
        }
    }

    private static string ResolveActivityGlyph(ActivityKind kind) => kind switch
    {
        ActivityKind.Media => "\uE8D6",
        ActivityKind.Timer => "\uE916",
        ActivityKind.ScheduledAction => "\uE823",
        ActivityKind.PrivacySensor => "\uE720",
        ActivityKind.MicMuted => "\uEC54",
        ActivityKind.VoiceNote => "\uE720",
        ActivityKind.Meeting => "\uE787",
        ActivityKind.Transfer => "\uE896",
        ActivityKind.DevBuild => "\uE756",
        ActivityKind.LowBattery => "\uEBA0",
        ActivityKind.FileShelf => "\uE8F1",
        ActivityKind.Telemetry => "\uE9D9",
        ActivityKind.CustomWidget => "\uE74C",
        _ => "\uE946"
    };

    private static string ResolveHudGlyph(HudKind kind) => kind switch
    {
        HudKind.Volume => "\uE767",
        HudKind.MicMute => "\uEC54",
        HudKind.Brightness => "\uE706",
        HudKind.Bluetooth => "\uE702",
        HudKind.BluetoothConnect => "\uE7F6",
        HudKind.BluetoothLowBattery => "\uEBA0",
        HudKind.Battery => "\uE83F",
        HudKind.Focus => "\uE708",
        HudKind.MediaTrack => "\uE8D6",
        HudKind.Clipboard => "\uE77F",
        HudKind.FileStashed => "\uE8F1",
        HudKind.QrShare => "\uED14",
        HudKind.OcrCapture => "\uE722",
        HudKind.ColorPicked => "\uEF3C",
        HudKind.UsbDrive => "\uECF0",
        HudKind.DevBridge => "\uE756",
        HudKind.ScheduledPower => "\uE7E8",
        HudKind.GameMode => "\uE7FC",
        HudKind.TransferComplete => "\uE896",
        HudKind.WindowPin => "\uE718",
        HudKind.VoiceNote => "\uE720",
        HudKind.TimerAlert => "\uE916",
        HudKind.NotificationPulse => "\uEA8F",
        _ => "\uE946"
    };

    // -------------------------------------------------------------------------
    // Media & Album Artwork Cache + 8-Bar Spectrum
    // -------------------------------------------------------------------------

    private void OnMediaSnapshotChanged(object? sender, MediaPlaybackSnapshot snap)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            UpdateMediaUi(snap);
        });
    }

    private void UpdateMediaUi(MediaPlaybackSnapshot media)
    {
        UpdateMediaArtworkIfChanged(media.ThumbnailBytes, media.AccentHex, media.SecondaryAccentHex, media.HighlightAccentHex);

        string trackKey = $"{media.SessionId}|{media.Title}|{media.Artist}";
        bool trackChanged = !string.Equals(_lastMediaTrackKey, trackKey, StringComparison.Ordinal);
        _lastMediaTrackKey = trackKey;
        if (trackChanged && media.HasSession)
        {
            _motion.TriggerAlbumChange(triggerAuraPulse: _orch.Config.DynamicAlbumGlowEnabled);
            StartRenderLoop();
        }

        if (_wasMediaPlaying != media.IsPlaying)
        {
            _wasMediaPlaying = media.IsPlaying;
            if (!media.IsPlaying)
            {
                _motion.ResetSpectrum(0.0);
            }
            if (ShouldAnimateSpectrum(_lastSnapshot) || ShouldAnimateAmbientGlow(_lastSnapshot))
            {
                StartRenderLoop();
            }
        }
        SyncMediaPositionTimerState();

        string lang = _orch.Config.Language;
        var audioSnap = _auditAudioOverride ?? _orch.Audio.Current;
        UpdateMediaQualityBadge(audioSnap);

        if (!media.HasSession)
        {
            NowMediaAppText.Text = Strings.AppTitle(lang);
            NowMediaTitle.Text = Strings.NoMedia(lang);
            NowMediaArtist.Text = string.Empty;
            NowMediaLyricLine.Text = string.Empty;
            NowMediaLyricLine.Visibility = Visibility.Collapsed;
            BtnPlayPause.Content = "\uE768";
            BtnPlayPause.IsEnabled = false;
            BtnPrevTrack.IsEnabled = false;
            BtnNextTrack.IsEnabled = false;
            MediaSeekSlider.IsEnabled = false;
            MediaSeekSlider.Value = 0;
            NowElapsedText.Text = "0:00";
            NowDurationText.Text = "0:00";
            SessionSwitcherPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            NowMediaAppText.Text = string.IsNullOrWhiteSpace(media.AppName) ? "Media" : media.AppName;
            NowMediaTitle.Text = string.IsNullOrWhiteSpace(media.Title) ? Strings.NoMedia(lang) : media.Title;
            NowMediaArtist.Text = media.Artist;
            BtnPlayPause.Content = media.IsPlaying ? "\uE769" : "\uE768";
            BtnPlayPause.IsEnabled = media.CanPlayPause;
            BtnPrevTrack.IsEnabled = media.CanPrev;
            BtnNextTrack.IsEnabled = media.CanNext;

            if (media.AllSessions.Length > 1)
            {
                int idx = 0;
                for (int i = 0; i < media.AllSessions.Length; i++)
                {
                    if (string.Equals(media.AllSessions[i].SessionId, media.SessionId, StringComparison.Ordinal))
                    {
                        idx = i;
                        break;
                    }
                }
                NowSessionIndexText.Text = $"{idx + 1}/{media.AllSessions.Length}";
                SessionSwitcherPanel.Visibility = Visibility.Visible;
            }
            else
            {
                SessionSwitcherPanel.Visibility = Visibility.Collapsed;
            }

            UpdateMediaTimelineVisuals(media);
        }

        if (_lastSnapshot.PrimaryActivity?.Kind == ActivityKind.Media)
        {
            UpdateCompactAndSatelliteContent(_lastSnapshot);
        }
    }

    private void UpdateMediaTimelineVisuals(MediaPlaybackSnapshot media)
    {
        if (!media.HasSession || _isSeekingMedia) return;

        TimeSpan effectivePos = media.Position;
        if (media.IsPlaying && media.Duration > TimeSpan.Zero)
        {
            double elapsedSec = Math.Max(0.0, (DateTimeOffset.UtcNow - media.LastUpdatedUtc).TotalSeconds) * Math.Max(0.5, media.PlaybackRate);
            effectivePos = media.Position + TimeSpan.FromSeconds(elapsedSec);
            if (effectivePos > media.Duration) effectivePos = media.Duration;
        }

        MediaSeekSlider.IsEnabled = media.CanSeek && media.Duration > TimeSpan.FromSeconds(1);
        double pct = media.Duration.TotalSeconds > 0.5
            ? Math.Clamp(effectivePos.TotalSeconds / media.Duration.TotalSeconds * 100.0, 0.0, 100.0)
            : 0.0;
        MediaSeekSlider.Value = pct;
        NowElapsedText.Text = FormatDuration(effectivePos);
        NowDurationText.Text = FormatDuration(media.Duration);

        string? lyric = _auditLyricOverride ?? (!_isRenderAuditMode ? _orch.GetCurrentLyricLine(media) : null);
        if (!string.IsNullOrWhiteSpace(lyric))
        {
            NowMediaLyricLine.Text = $"♪ {lyric}";
            NowMediaLyricLine.Visibility = Visibility.Visible;
        }
        else
        {
            NowMediaLyricLine.Text = string.Empty;
            NowMediaLyricLine.Visibility = Visibility.Collapsed;
        }
    }

    private void OnMediaPositionTimerTick(object? sender, EventArgs e)
    {
        if (_lastSnapshot.State == IslandPresentationState.S4Expanded &&
            _lastSnapshot.ActiveTab == ExpandedTabId.Now &&
            !_isRenderAuditMode)
        {
            var media = _orch.Media.Current;
            if (media.HasSession && media.IsPlaying)
            {
                UpdateMediaTimelineVisuals(media);
            }
        }
    }

    private void UpdateMediaQualityBadge(AudioMixerSnapshot audioSnap)
    {
        if (!string.IsNullOrWhiteSpace(audioSnap.OutputFormatLabel))
        {
            NowMediaQualityText.Text = audioSnap.OutputFormatLabel;
            NowMediaQualityBadge.Visibility = Visibility.Visible;
        }
        else
        {
            NowMediaQualityBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMediaArtworkIfChanged(byte[]? bytes, string? accentHex, string? secondaryHex = null, string? highlightHex = null)
    {
        UpdateAccentGlowPalette(accentHex, secondaryHex, highlightHex);

        if (bytes is not { Length: > 0 })
        {
            _lastThumbBytes = null;
            _cachedAlbumBitmap = null;
            ExpandedAlbumArt.Source = null;
            ExpandedArtFallback.Visibility = Visibility.Visible;
            CompactAlbumImage.Source = null;
            if (_lastSnapshot.PrimaryActivity?.Kind == ActivityKind.Media)
            {
                CompactLeadingGlyph.Visibility = Visibility.Visible;
            }
            return;
        }

        if (!BytesEqual(_lastThumbBytes, bytes))
        {
            _lastThumbBytes = bytes;
            _cachedAlbumBitmap = DecodeFrozenBitmap(bytes, decodeWidth: 152);
        }

        ExpandedAlbumArt.Source = _cachedAlbumBitmap;
        ExpandedArtFallback.Visibility = _cachedAlbumBitmap is null ? Visibility.Visible : Visibility.Collapsed;

        if (_lastSnapshot.PrimaryActivity?.Kind == ActivityKind.Media)
        {
            CompactAlbumImage.Source = _cachedAlbumBitmap;
            CompactLeadingGlyph.Visibility = _cachedAlbumBitmap is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateAccentGlowPalette(string? accentHex, string? secondaryHex = null, string? highlightHex = null)
    {
        if (!_orch.Config.DynamicAlbumGlowEnabled)
        {
            AuraWashStop0.Color = Color.FromArgb(0, 10, 132, 255);
            AuraWashStop1.Color = Color.FromArgb(0, 94, 92, 230);
            AuraWashStop2.Color = Color.FromArgb(0, 100, 210, 255);
            AuraStop0.Color = Color.FromArgb(0, 10, 132, 255);
            AuraStop1.Color = Color.FromArgb(0, 10, 132, 255);
            AuraStop2.Color = Color.FromArgb(0, 10, 132, 255);
            AuraStop3.Color = Color.FromArgb(0, 10, 132, 255);
            AuraSecStop0.Color = Color.FromArgb(0, 94, 92, 230);
            AuraSecStop1.Color = Color.FromArgb(0, 94, 92, 230);
            AuraSecStop2.Color = Color.FromArgb(0, 94, 92, 230);
            AuraSecStop3.Color = Color.FromArgb(0, 94, 92, 230);
            AuraHiStop0.Color = Color.FromArgb(0, 100, 210, 255);
            AuraHiStop1.Color = Color.FromArgb(0, 100, 210, 255);
            AuraHiStop2.Color = Color.FromArgb(0, 100, 210, 255);
            AuraHiStop3.Color = Color.FromArgb(0, 100, 210, 255);
            AuraSatStop0.Color = Color.FromArgb(0, 94, 92, 230);
            AuraSatStop1.Color = Color.FromArgb(0, 94, 92, 230);
            AuraSatStop2.Color = Color.FromArgb(0, 10, 132, 255);
            return;
        }

        Color primary = TryParseColor(accentHex, out Color c) ? c : Color.FromRgb(10, 132, 255);
        Color rawSecondary = TryParseColor(secondaryHex, out Color sc) ? sc : ShiftColorHue(primary, 36.0);
        Color rawHighlight = TryParseColor(highlightHex, out Color hc) ? hc : ShiftColorHue(primary, -30.0);

        // Harmonize secondary & highlight gently toward primary so transitions glide cleanly as silk gradients
        Color secondary = BlendColors(rawSecondary, primary, 0.18);
        Color highlight = BlendColors(rawHighlight, primary, 0.15);

        _currentAuraColor = primary;
        _currentSecondaryAuraColor = secondary;
        _currentHighlightAuraColor = highlight;

        AuraWashStop0.Color = Color.FromArgb(46, primary.R, primary.G, primary.B);
        AuraWashStop1.Color = Color.FromArgb(36, secondary.R, secondary.G, secondary.B);
        AuraWashStop2.Color = Color.FromArgb(30, highlight.R, highlight.G, highlight.B);

        AuraStop0.Color = Color.FromArgb(90, primary.R, primary.G, primary.B);
        AuraStop1.Color = Color.FromArgb(78, primary.R, primary.G, primary.B);
        AuraStop2.Color = Color.FromArgb(46, primary.R, primary.G, primary.B);
        AuraStop3.Color = Color.FromArgb(16, primary.R, primary.G, primary.B);

        AuraSecStop0.Color = Color.FromArgb(80, secondary.R, secondary.G, secondary.B);
        AuraSecStop1.Color = Color.FromArgb(68, secondary.R, secondary.G, secondary.B);
        AuraSecStop2.Color = Color.FromArgb(40, secondary.R, secondary.G, secondary.B);
        AuraSecStop3.Color = Color.FromArgb(14, secondary.R, secondary.G, secondary.B);

        AuraHiStop0.Color = Color.FromArgb(68, highlight.R, highlight.G, highlight.B);
        AuraHiStop1.Color = Color.FromArgb(56, highlight.R, highlight.G, highlight.B);
        AuraHiStop2.Color = Color.FromArgb(34, highlight.R, highlight.G, highlight.B);
        AuraHiStop3.Color = Color.FromArgb(12, highlight.R, highlight.G, highlight.B);

        AuraSatStop0.Color = Color.FromArgb(88, secondary.R, secondary.G, secondary.B);
        AuraSatStop1.Color = Color.FromArgb(54, primary.R, primary.G, primary.B);
        AuraSatStop2.Color = Color.FromArgb(22, highlight.R, highlight.G, highlight.B);
    }

    private static Color BlendColors(Color a, Color b, double weightB) => Color.FromRgb(
        (byte)Math.Clamp(Math.Round(a.R * (1.0 - weightB) + b.R * weightB), 0, 255),
        (byte)Math.Clamp(Math.Round(a.G * (1.0 - weightB) + b.G * weightB), 0, 255),
        (byte)Math.Clamp(Math.Round(a.B * (1.0 - weightB) + b.B * weightB), 0, 255));

    private static Color ShiftColorHue(Color src, double hueShiftDeg)
    {
        double r = src.R / 255.0;
        double g = src.G / 255.0;
        double b = src.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0.0;
        if (d > 1e-4)
        {
            if (max == r) h = ((g - b) / d) % 6.0;
            else if (max == g) h = (b - r) / d + 2.0;
            else h = (r - g) / d + 4.0;
            h *= 60.0;
            if (h < 0) h += 360.0;
        }
        double s = max < 1e-4 ? 0.55 : Math.Max(0.45, d / max);
        double v = Math.Max(0.70, max);
        h = (h + hueShiftDeg + 360.0) % 360.0;

        double c = v * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = v - c;
        (double nr, double ng, double nb) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round((nr + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((ng + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((nb + m) * 255.0), 0, 255));
    }

    private void OnAudioSpectrumUpdated(object? sender, double[] bands)
    {
        if (_isClosed || !_orch.Config.EnableSpectrumVisualizer) return;
        _motion.SetSpectrumTargets(bands);
    }

    // -------------------------------------------------------------------------
    // Tab Switching & Indicator Spring
    // -------------------------------------------------------------------------

    private void SelectExpandedTabVisual(ExpandedTabId activeTab, bool snapIndicator)
    {
        Button[] all = [TabBtnNow, TabBtnAudio, TabBtnControl, TabBtnShelf, TabBtnSystem, TabBtnWidgets];
        Button targetBtn = TabBtnNow;

        foreach (var btn in all)
        {
            bool isMatch = Enum.TryParse<ExpandedTabId>(btn.Tag?.ToString(), out var id) && id == activeTab;
            btn.Foreground = isMatch ? ActiveTabFgBrush : MutedTabFgBrush;
            btn.FontWeight = isMatch ? FontWeights.SemiBold : FontWeights.Medium;
            if (isMatch) targetBtn = btn;
        }

        TabNowPanel.Visibility = activeTab == ExpandedTabId.Now ? Visibility.Visible : Visibility.Collapsed;
        TabAudioPanel.Visibility = activeTab == ExpandedTabId.Audio ? Visibility.Visible : Visibility.Collapsed;
        TabControlPanel.Visibility = activeTab == ExpandedTabId.Control ? Visibility.Visible : Visibility.Collapsed;
        TabShelfPanel.Visibility = activeTab == ExpandedTabId.Shelf ? Visibility.Visible : Visibility.Collapsed;
        TabSystemPanel.Visibility = activeTab == ExpandedTabId.System ? Visibility.Visible : Visibility.Collapsed;
        TabWidgetsPanel.Visibility = activeTab == ExpandedTabId.Widgets ? Visibility.Visible : Visibility.Collapsed;

        CloseExpandedOverlays();
        UpdateTabIndicatorTarget(targetBtn, snapIndicator);
        if (!_isRenderAuditMode && activeTab == ExpandedTabId.Now && ShouldAnimateAmbientGlow(_lastSnapshot))
        {
            StartRenderLoop();
        }
    }

    private void CloseExpandedOverlays()
    {
        NotifDetailOverlay.Visibility = Visibility.Collapsed;
        AudioQualityOverlay.Visibility = Visibility.Collapsed;
        SysIdleAppsConfirmPanel.Visibility = Visibility.Collapsed;
        SystemProcessScrollViewer.Visibility = Visibility.Visible;
        _activeDetailNotification = null;
    }

    private void UpdateTabIndicatorTarget(Button targetBtn, bool snap)
    {
        if (targetBtn.ActualWidth > 1.0 && TabButtonsPanel.ActualWidth > 1.0)
        {
            Point offset = targetBtn.TranslatePoint(new Point(0, 0), TabButtonsPanel);
            _motion.RetargetTabIndicator(offset.X, targetBtn.ActualWidth, snap);
            if (snap)
            {
                TabIndicatorPill.Width = targetBtn.ActualWidth;
                TabIndicatorTransform.X = offset.X;
            }
        }
    }

    private void OnTabButtonSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Button btn &&
            Enum.TryParse<ExpandedTabId>(btn.Tag?.ToString(), out var id) &&
            id == _lastSnapshot.ActiveTab)
        {
            bool shouldSnap = _snapTabIndicatorOnMeasure || !_isRenderingLoopActive;
            if (btn.ActualWidth > 1.0 && TabButtonsPanel.ActualWidth > 1.0)
            {
                _snapTabIndicatorOnMeasure = false;
            }
            UpdateTabIndicatorTarget(btn, snap: shouldSnap);
        }
    }

    private bool IsExpandedTabVisible(ExpandedTabId tab) =>
        !_isClosed && !_isRenderAuditMode && _lastSnapshot.State == IslandPresentationState.S4Expanded && _lastSnapshot.ActiveTab == tab;

    private bool IsTabDirty(ExpandedTabId tab) => _tabDirty[(int)tab];

    private void MarkTabDirty(ExpandedTabId tab) => _tabDirty[(int)tab] = true;

    private void ClearTabDirty(ExpandedTabId tab) => _tabDirty[(int)tab] = false;

    private void SyncMediaPositionTimerState()
    {
        bool shouldTick = !_isClosed &&
                          !_isRenderAuditMode &&
                          _lastSnapshot.State == IslandPresentationState.S4Expanded &&
                          _lastSnapshot.ActiveTab == ExpandedTabId.Now &&
                          _orch.Media.Current.HasSession &&
                          _orch.Media.Current.IsPlaying;

        if (shouldTick)
        {
            if (!_mediaPositionTimer.IsEnabled)
            {
                _mediaPositionTimer.Start();
            }
        }
        else if (_mediaPositionTimer.IsEnabled)
        {
            _mediaPositionTimer.Stop();
        }
    }

    private void RefreshExpandedTabPanel(ExpandedTabId tab)
    {
        ClearTabDirty(tab);
        switch (tab)
        {
            case ExpandedTabId.Now:
                UpdateMediaUi(_auditMediaOverride ?? _orch.Media.Current);
                RenderNowMeetingBanner();
                RenderNowTimers(_auditTimersOverride ?? _orch.Timers.GetTimers());
                RenderNowNotifications(_auditNotifsOverride ?? _orch.Notifications.Current);
                break;

            case ExpandedTabId.Audio:
                RenderAudioTab(_auditAudioOverride ?? _orch.Audio.Current);
                break;

            case ExpandedTabId.Control:
                if (!_isRenderAuditMode) _orch.WindowControl.CaptureForegroundIfExternal();
                RenderControlConnectivity(_auditConnOverride ?? _orch.Connectivity.Current);
                RenderControlBrightness(_auditDisplaysOverride ?? _orch.Brightness.GetDisplays());
                RenderControlScheduledPower(_auditScheduledPowerOverride ?? _orch.Timers.ScheduledPowerState);
                if (!_isRenderAuditMode) _ = _orch.Connectivity.RefreshAsync();
                break;

            case ExpandedTabId.Shelf:
                RenderShelfItems(_auditShelfOverride ?? _orch.Shelf.GetItems());
                RenderClipboardItems(_auditClipOverride ?? _orch.Clipboard.GetEntries());
                RenderShelfQrOverlay();
                break;

            case ExpandedTabId.System:
                RenderSystemTelemetry(_auditTelemetryOverride ?? _orch.Telemetry.Current);
                break;

            case ExpandedTabId.Widgets:
                RenderWidgetsTab(_auditWidgetsOverride ?? _orch.Widgets.GetSnapshots());
                break;
        }
    }

    private async Task RefreshInitialContentAsync()
    {
        if (_isClosed || _isRenderAuditMode) return;

        for (int i = 0; i < _tabDirty.Length; i++)
        {
            _tabDirty[i] = true;
        }

        UpdateMediaUi(_orch.Media.Current);
        if (_lastSnapshot.State == IslandPresentationState.S4Expanded)
        {
            RefreshExpandedTabPanel(_lastSnapshot.ActiveTab);
        }

        try
        {
            await _orch.Connectivity.RefreshAsync();
        }
        catch
        {
            // Non-fatal background refresh guard.
        }
    }

    // -------------------------------------------------------------------------
    // Tab 1: NOW (Timers incl. Completed Reset + Notifications)
    // -------------------------------------------------------------------------

    private void OnCalendarActivityChanged(object? sender, LiveActivityState? activity)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (IsExpandedTabVisible(ExpandedTabId.Now))
            {
                RenderNowMeetingBanner();
            }
            else
            {
                MarkTabDirty(ExpandedTabId.Now);
            }
        });
    }

    private void RenderNowMeetingBanner()
    {
        var meeting = _auditMeetingOverride ?? (!_isRenderAuditMode ? _orch.Calendar.ActiveOrNextMeeting : null);
        var lang = _orch.Config.Language;
        if (meeting is null)
        {
            NowMeetingBanner.Visibility = Visibility.Collapsed;
            return;
        }

        TimeSpan remaining = meeting.StartUtc - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.FromMinutes(15) || remaining < -TimeSpan.FromHours(2))
        {
            if (_auditMeetingOverride is null)
            {
                NowMeetingBanner.Visibility = Visibility.Collapsed;
                return;
            }
        }

        string badge = remaining <= TimeSpan.Zero
            ? Strings.MeetingLive(lang)
            : $"{(int)Math.Floor(remaining.TotalMinutes)}:{remaining.Seconds:00}";
        NowMeetingCountdownText.Text = $"{Strings.MeetingPrefix(lang)} • {badge}";
        NowMeetingTitleText.Text = meeting.Summary;
        NowMeetingBanner.ToolTip = Strings.MeetingJoinTip(lang);
        NowMeetingBanner.Visibility = Visibility.Visible;

        var audioSnap = _auditAudioOverride ?? _orch.Audio.Current;
        BtnMeetingMicToggle.Content = audioSnap.IsMicMuted ? "\uEC54" : "\uE720";
        BtnMeetingMicToggle.Foreground = audioSnap.IsMicMuted ? HungBadgeFgBrush : ActiveAccentDotBrush;
        BtnMeetingJoin.Content = Strings.Pick(lang, "Katıl", "Join");
    }

    private void OnTimersChanged(object? sender, IReadOnlyList<TimerSnapshot> timers)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!IsExpandedTabVisible(ExpandedTabId.Now))
            {
                MarkTabDirty(ExpandedTabId.Now);
                return;
            }
            RenderNowTimers(timers);
        });
    }

    private void RenderNowTimers(IReadOnlyList<TimerSnapshot> timers)
    {
        string lang = _orch.Config.Language;
        NowTimersStack.Children.Clear();

        if (timers.Count == 0)
        {
            NowTimersStack.Children.Add(new TextBlock
            {
                Text = Strings.Pick(lang, "Aktif zamanlayıcı yok", "No active timers"),
                FontFamily = UiTextFontFamily,
                FontSize = 10.5,
                Foreground = MutedTabFgBrush,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        foreach (var t in timers)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = CardSurfaceBrush,
                BorderBrush = t.IsCompleted ? ActiveTileBorderBrush : CardBorderBrush,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleTb = new TextBlock
            {
                Text = t.Label,
                FontFamily = UiTextFontFamily,
                FontSize = 10.2,
                FontWeight = FontWeights.SemiBold,
                Foreground = ActiveTabFgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleTb, 0);
            row.Children.Add(titleTb);

            var timeTb = new TextBlock
            {
                Text = FormatDuration(t.RemainingOrElapsed),
                FontFamily = UiTextFontFamily,
                FontSize = 11.2,
                FontWeight = FontWeights.Bold,
                Foreground = t.IsCompleted ? HungBadgeFgBrush : ParseHexBrush("#FF9F0A", "#FF9F0A"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 4, 0)
            };
            Grid.SetColumn(timeTb, 1);
            row.Children.Add(timeTb);

            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string tid = t.Id;

            if (t.IsCompleted)
            {
                var resetBtn = new Button
                {
                    Style = (Style)FindResource("PillTabButtonStyle"),
                    Content = Strings.TimerReset(lang),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 9.2,
                    Margin = new Thickness(2, 0, 3, 0)
                };
                resetBtn.Click += (_, _) => _orch.Timers.ResetTimer(tid, autoStart: true);
                actions.Children.Add(resetBtn);
            }
            else
            {
                var toggleBtn = new Button
                {
                    Style = (Style)FindResource("IconCircleButtonStyle"),
                    Width = 22,
                    Height = 22,
                    FontFamily = UiIconFontFamily,
                    FontSize = 9.5,
                    Content = t.IsRunning ? "\uE769" : "\uE768",
                    ToolTip = t.IsRunning ? Strings.PauseTimer(lang) : Strings.StartTimer(lang),
                    Margin = new Thickness(2, 0, 3, 0)
                };
                toggleBtn.Click += (_, _) => _orch.Timers.TogglePause(tid);
                actions.Children.Add(toggleBtn);
            }

            var delBtn = new Button
            {
                Style = (Style)FindResource("IconCircleButtonStyle"),
                Width = 22,
                Height = 22,
                FontFamily = UiIconFontFamily,
                FontSize = 9,
                Content = "\uE711"
            };
            delBtn.Click += (_, _) => _orch.Timers.Remove(tid);
            actions.Children.Add(delBtn);

            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            card.Child = row;
            NowTimersStack.Children.Add(card);
        }
    }

    private void OnNotificationsChanged(object? sender, IReadOnlyList<NotificationEntry> items)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!IsExpandedTabVisible(ExpandedTabId.Now))
            {
                MarkTabDirty(ExpandedTabId.Now);
                return;
            }
            RenderNowNotifications(items);
        });
    }

    private void RenderNowNotifications(IReadOnlyList<NotificationEntry> items)
    {
        NowNotificationsStack.Children.Clear();
        NowNoNotificationsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var n in items)
        {
            var item = n;
            ImageSource? appIcon = ResolveNotificationIcon(item);

            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = CardSurfaceBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(7, 5, 7, 5),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = "Interactive"
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Column 0: App Icon or Category Glyph Well
            var iconWell = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(7),
                Background = ActiveTileBgBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(0.6),
                Margin = new Thickness(0, 1, 7, 0),
                VerticalAlignment = VerticalAlignment.Top,
                ClipToBounds = true
            };
            if (appIcon is not null)
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = appIcon,
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                iconWell.Child = img;
            }
            else
            {
                iconWell.Child = new TextBlock
                {
                    Text = ResolveFallbackNotifGlyph(item),
                    FontFamily = UiIconFontFamily,
                    FontSize = 11.5,
                    Foreground = TileAccentBlueBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            Grid.SetColumn(iconWell, 0);
            row.Children.Add(iconWell);

            // Column 1: App header + Time + Title + Multi-line readable preview
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            string timeTag = item.ReceivedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            string appPrefix = string.IsNullOrWhiteSpace(item.SourceApp) ? "Windows" : item.SourceApp;
            string headerLine = string.IsNullOrWhiteSpace(item.Title) || string.Equals(item.Title, item.SourceApp, StringComparison.OrdinalIgnoreCase)
                ? $"{appPrefix} • {timeTag}"
                : $"{appPrefix} • {item.Title}";

            stack.Children.Add(new TextBlock
            {
                Text = headerLine,
                FontFamily = UiTextFontFamily,
                FontSize = 10.2,
                FontWeight = FontWeights.SemiBold,
                Foreground = ActiveTabFgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            string previewText = string.IsNullOrWhiteSpace(item.Body)
                ? item.Title
                : item.Body.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            if (!string.IsNullOrWhiteSpace(previewText) && !string.Equals(previewText, headerLine, StringComparison.Ordinal))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = previewText,
                    FontFamily = UiTextFontFamily,
                    FontSize = 9.2,
                    Foreground = MutedTabFgBrush,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 28,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }
            Grid.SetColumn(stack, 1);
            row.Children.Add(stack);

            uint nid = item.Id;
            var dismissBtn = new Button
            {
                Style = (Style)FindResource("IconCircleButtonStyle"),
                Width = 22,
                Height = 22,
                FontFamily = UiIconFontFamily,
                FontSize = 9,
                Content = "\uE711",
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            dismissBtn.Click += async (_, args) =>
            {
                args.Handled = true;
                await _orch.Notifications.DismissAsync(nid);
            };
            Grid.SetColumn(dismissBtn, 2);
            row.Children.Add(dismissBtn);

            card.MouseLeftButtonUp += (_, args) =>
            {
                if (args.OriginalSource is DependencyObject dep && HasAncestor<Button>(dep)) return;
                args.Handled = true;
                OpenNotificationDetail(item);
            };

            card.Child = row;
            NowNotificationsStack.Children.Add(card);
        }
    }

    private ImageSource? ResolveNotificationIcon(NotificationEntry n)
    {
        string key = !string.IsNullOrWhiteSpace(n.AppUserModelId)
            ? n.AppUserModelId!
            : (!string.IsNullOrWhiteSpace(n.ExecutablePath) ? n.ExecutablePath! : n.SourceApp);

        if (!string.IsNullOrWhiteSpace(key) && _notifIconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? resolved = null;
        if (n.AppIconBytes is { Length: > 0 })
        {
            resolved = DecodeFrozenBitmap(n.AppIconBytes, decodeWidth: 36);
        }

        if (resolved is null && !string.IsNullOrWhiteSpace(n.ExecutablePath) && File.Exists(n.ExecutablePath))
        {
            resolved = ExtractExeIconFrozen(n.ExecutablePath);
        }

        if (resolved is null && !string.IsNullOrWhiteSpace(n.SourceApp))
        {
            string? runningExe = TryFindRunningProcessExe(n.SourceApp);
            if (!string.IsNullOrWhiteSpace(runningExe))
            {
                resolved = ExtractExeIconFrozen(runningExe);
            }
        }

        if (resolved is not null && !string.IsNullOrWhiteSpace(key))
        {
            _notifIconCache[key] = resolved;
        }
        return resolved;
    }

    private string? TryFindRunningProcessExe(string sourceApp)
    {
        try
        {
            string clean = sourceApp.Trim();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (!p.ProcessName.Contains(clean, StringComparison.OrdinalIgnoreCase) &&
                        !clean.Contains(p.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (_pidExePathCache.TryGetValue(p.Id, out string? cachedExe) && File.Exists(cachedExe))
                    {
                        return cachedExe;
                    }
                    try
                    {
                        string? exe = p.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                        {
                            _pidExePathCache[p.Id] = exe;
                            return exe;
                        }
                    }
                    catch
                    {
                        // Protected process or session mismatch; skip silently.
                    }
                }
            }
        }
        catch
        {
            // Ignore process enumeration race.
        }
        return null;
    }

    private unsafe BitmapSource? ExtractExeIconFrozen(string exePath)
    {
        nint hLarge = 0;
        nint hSmall = 0;
        try
        {
            uint extracted = NativeMethods.ExtractIconExW(exePath, 0, &hLarge, &hSmall, 1);
            nint hIcon = hLarge != 0 ? hLarge : hSmall;
            if (extracted == 0 || hIcon == 0) return null;

            var src = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));
            if (src.CanFreeze) src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hLarge != 0) NativeMethods.DestroyIcon(hLarge);
            if (hSmall != 0 && hSmall != hLarge) NativeMethods.DestroyIcon(hSmall);
        }
    }

    private static string ResolveFallbackNotifGlyph(NotificationEntry n)
    {
        string src = $"{n.SourceApp} {n.AppUserModelId}".ToLowerInvariant();
        if (src.Contains("discord") || src.Contains("teams") || src.Contains("whatsapp") || src.Contains("telegram") || src.Contains("slack"))
            return "\uE8BD";
        if (src.Contains("mail") || src.Contains("outlook"))
            return "\uE715";
        if (src.Contains("security") || src.Contains("defender"))
            return "\uE72E";
        if (src.Contains("dinamik") || src.Contains("system") || src.Contains("disk") || src.Contains("usb"))
            return "\uE946";
        return "\uEA8F";
    }

    internal void OpenNotificationDetailForAudit(NotificationEntry n)
    {
        OpenNotificationDetail(n);
        RootSurface.UpdateLayout();
    }

    private void OpenNotificationDetail(NotificationEntry n)
    {
        _activeDetailNotification = n;
        AudioQualityOverlay.Visibility = Visibility.Collapsed;

        ImageSource? icon = ResolveNotificationIcon(n);
        if (icon is not null)
        {
            NotifDetailIconImg.Source = icon;
            NotifDetailIconImg.Visibility = Visibility.Visible;
            NotifDetailIconGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            NotifDetailIconImg.Source = null;
            NotifDetailIconImg.Visibility = Visibility.Collapsed;
            NotifDetailIconGlyph.Text = ResolveFallbackNotifGlyph(n);
            NotifDetailIconGlyph.Visibility = Visibility.Visible;
        }

        NotifDetailAppText.Text = string.IsNullOrWhiteSpace(n.SourceApp) ? "Windows" : n.SourceApp;
        NotifDetailTimeText.Text = n.ReceivedUtc.ToLocalTime().ToString("HH:mm • dd MMM yyyy", CultureInfo.CurrentUICulture);
        NotifDetailTitleText.Text = string.IsNullOrWhiteSpace(n.Title) ? n.SourceApp : n.Title;
        NotifDetailBodyText.Text = string.IsNullOrWhiteSpace(n.Body)
            ? n.Title
            : n.Body;
        NotifDetailOverlay.Visibility = Visibility.Visible;
    }

    private void OnNotifDetailCloseClick(object sender, RoutedEventArgs e)
    {
        NotifDetailOverlay.Visibility = Visibility.Collapsed;
        _activeDetailNotification = null;
    }

    private void OnNotifDetailCopyClick(object sender, RoutedEventArgs e)
    {
        if (_activeDetailNotification is null || _isRenderAuditMode) return;
        string text = string.IsNullOrWhiteSpace(_activeDetailNotification.Body)
            ? $"{_activeDetailNotification.SourceApp}: {_activeDetailNotification.Title}"
            : $"{_activeDetailNotification.Title}\n{_activeDetailNotification.Body}";
        _orch.Clipboard.CopyTextToClipboard(text);
        NotifDetailOverlay.Visibility = Visibility.Collapsed;
        _activeDetailNotification = null;
    }

    private async void OnNotifDetailDismissClick(object sender, RoutedEventArgs e)
    {
        if (_activeDetailNotification is null) return;
        uint nid = _activeDetailNotification.Id;
        NotifDetailOverlay.Visibility = Visibility.Collapsed;
        _activeDetailNotification = null;
        if (!_isRenderAuditMode)
        {
            await _orch.Notifications.DismissAsync(nid);
        }
    }

    private void OnOverlayBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        NotifDetailOverlay.Visibility = Visibility.Collapsed;
        AudioQualityOverlay.Visibility = Visibility.Collapsed;
        _activeDetailNotification = null;
    }

    private void OnOverlayCardSwallowMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    // -------------------------------------------------------------------------
    // Tab 2: AUDIO (Endpoints + Master/Mic + Per-App Mixer)
    // -------------------------------------------------------------------------

    private void OnAudioSnapshotChanged(object? sender, AudioMixerSnapshot snap)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (IsExpandedTabVisible(ExpandedTabId.Control))
            {
                RenderControlConnectivity(_auditConnOverride ?? _orch.Connectivity.Current);
            }
            else
            {
                MarkTabDirty(ExpandedTabId.Control);
            }
            if (IsExpandedTabVisible(ExpandedTabId.Now))
            {
                RenderNowMeetingBanner();
                UpdateMediaQualityBadge(snap);
            }
            if (AudioQualityOverlay.Visibility == Visibility.Visible)
            {
                PopulateAudioQualityOverlay(snap);
            }
            if (!IsExpandedTabVisible(ExpandedTabId.Audio))
            {
                MarkTabDirty(ExpandedTabId.Audio);
                return;
            }
            RenderAudioTab(snap);
        });
    }

    private void OnVoiceNoteCompleted(object? sender, VoiceNoteCompletedResult result)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            RenderVoiceNoteBar();
        });
    }

    private void OnVoiceNoteWaveformUpdated(object? sender, double[] bands)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            for (int i = 0; i < _voiceWaveScales.Length; i++)
            {
                double val = i < bands.Length ? Math.Clamp(bands[i], 0.16, 1.0) : 0.16;
                _voiceWaveScales[i].ScaleY = val;
            }
            RenderVoiceNoteBar();
        });
    }

    private void RenderVoiceNoteBar()
    {
        string lang = _orch.Config.Language;
        bool recording = _orch.VoiceNotes.IsRecording;
        VoiceNoteGlyphText.Text = recording ? "\uE71A" : "\uE720";
        VoiceNoteLabelText.Text = recording
            ? Strings.VoiceNoteStopBtn(lang)
            : Strings.VoiceNoteStartBtn(lang);
        BtnVoiceNoteToggle.Foreground = recording ? HungBadgeFgBrush : ActiveTabFgBrush;
        BtnVoiceNoteToggle.Background = recording ? HungBadgeBgBrush : DefaultTileBgBrush;

        string? lastWav = _orch.VoiceNotes.LastSavedWavPath;
        if (!string.IsNullOrWhiteSpace(lastWav))
        {
            VoiceNotePathText.Text = $"{Strings.VoiceNotesFolderLabel(lang)} {Path.GetFileName(lastWav)}";
            VoiceNotePathText.ToolTip = lastWav;
        }
        else
        {
            string dir = _orch.VoiceNotes.NotesDirectory;
            VoiceNotePathText.Text = $"{Strings.VoiceNotesFolderLabel(lang)} {ShortenFolderForDisplay(dir)}";
            VoiceNotePathText.ToolTip = dir;
        }

        if (!recording)
        {
            for (int i = 0; i < _voiceWaveScales.Length; i++)
            {
                _voiceWaveScales[i].ScaleY = 0.18;
            }
        }
    }

    private static string ShortenFolderForDisplay(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parent = Path.GetFileName(Path.GetDirectoryName(trimmed));
        string leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(parent) ? leaf : Path.Combine(parent, leaf);
    }

    private void RenderAudioTab(AudioMixerSnapshot snap)
    {
        _suppressAudioUiEvents = true;
        try
        {
            if (!OutputDeviceCombo.IsDropDownOpen)
            {
                OutputDeviceCombo.ItemsSource = snap.OutputDevices;
                OutputDeviceCombo.SelectedValue = snap.DefaultOutputId;
            }
            if (!InputDeviceCombo.IsDropDownOpen)
            {
                InputDeviceCombo.ItemsSource = snap.InputDevices;
                var defIn = snap.InputDevices.FirstOrDefault(d => d.IsDefault);
                InputDeviceCombo.SelectedValue = defIn?.Id;
            }

            long nowTick = Environment.TickCount64;
            if (!MasterVolumeSlider.IsMouseCaptureWithin && nowTick - _lastMasterSliderEditTick > 220)
            {
                double pct = Math.Clamp(Math.Round(snap.MasterVolume01 * 100.0), 0, 100);
                MasterVolumeSlider.Value = pct;
                MasterVolPctText.Text = $"{pct:F0}%";
            }
            MasterMuteBtn.Content = snap.IsMasterMuted ? "\uE74F" : "\uE767";

            if (!MicVolumeSlider.IsMouseCaptureWithin && nowTick - _lastMicSliderEditTick > 220)
            {
                double micPct = Math.Clamp(Math.Round(snap.MicVolume01 * 100.0), 0, 100);
                MicVolumeSlider.Value = micPct;
                MicVolPctText.Text = snap.HasMicDevice ? $"{micPct:F0}%" : "--";
            }
            MicVolumeSlider.IsEnabled = snap.HasMicDevice;
            MicMuteBtn.IsEnabled = snap.HasMicDevice;
            MicMuteBtn.Content = snap.IsMicMuted ? "\uEC54" : "\uE720";
            MicMuteBtn.Background = snap.IsMicMuted ? HungBadgeBgBrush : DefaultTileBgBrush;
            MicMuteBtn.Foreground = snap.IsMicMuted ? HungBadgeFgBrush : ActiveTabFgBrush;

            bool hasFormat = !string.IsNullOrWhiteSpace(snap.OutputFormatLabel);
            AudioFormatBadgeBorder.Visibility = hasFormat ? Visibility.Visible : Visibility.Collapsed;
            AudioFormatBadgeText.Text = hasFormat ? snap.OutputFormatLabel! : string.Empty;
            RenderVoiceNoteBar();
        }
        finally
        {
            _suppressAudioUiEvents = false;
        }

        string outsKey = string.Join(",", snap.OutputDevices.Select(d => d.Id));
        string nextMixerKey = snap.AppSessions.Length == 0
            ? string.Empty
            : $"{_orch.Config.Language}|{outsKey}|{string.Join(";", snap.AppSessions.Select(a => a.GroupKey))}";

        if (!string.Equals(_appMixerTopologyKey, nextMixerKey, StringComparison.Ordinal) ||
            _appMixerRowRefs.Count != snap.AppSessions.Length)
        {
            if (AudioAppMixerStack.IsMouseCaptureWithin || _openAppRouteCombos > 0)
            {
                return;
            }

            _appMixerTopologyKey = nextMixerKey;
            _appMixerRowRefs.Clear();
            AudioAppMixerStack.Children.Clear();

            if (snap.AppSessions.Length == 0)
            {
                AudioAppMixerStack.Children.Add(new TextBlock
                {
                    Text = Strings.Pick(_orch.Config.Language, "Aktif uygulama sesi yok", "No active audio streams"),
                    FontFamily = UiTextFontFamily,
                    FontSize = 10.8,
                    Foreground = MutedTabFgBrush,
                    Margin = new Thickness(0, 6, 0, 0)
                });
                return;
            }

            var routeChoices = new List<AudioRouteChoice>(snap.OutputDevices.Length + 1)
            {
                new AudioRouteChoice(string.Empty, Strings.AppDefaultOutput(_orch.Config.Language))
            };
            foreach (var dev in snap.OutputDevices)
            {
                routeChoices.Add(new AudioRouteChoice(dev.Id, dev.FriendlyName));
            }

            foreach (var app in snap.AppSessions)
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = CardSurfaceBrush,
                    BorderBrush = CardBorderBrush,
                    BorderThickness = new Thickness(0.8),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 0, 0, 5)
                };

                var stack = new StackPanel();
                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var appNameTb = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(app.DisplayName) ? app.ProcessName : app.DisplayName,
                    FontFamily = UiTextFontFamily,
                    FontSize = 10.8,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ActiveTabFgBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetColumn(appNameTb, 0);
                top.Children.Add(appNameTb);

                var pctText = new TextBlock
                {
                    Text = app.IsMuted ? Strings.Pick(_orch.Config.Language, "SESSİZ", "MUTED") : $"{Math.Round(app.Volume01 * 100):F0}%",
                    FontFamily = UiTextFontFamily,
                    FontSize = 10,
                    Foreground = app.IsMuted ? HungBadgeFgBrush : MutedTabFgBrush,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(pctText, 1);
                top.Children.Add(pctText);
                stack.Children.Add(top);

                var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                string gKey = app.GroupKey;
                string procName = app.ProcessName;
                var muteBtn = new Button
                {
                    Style = (Style)FindResource("IconCircleButtonStyle"),
                    Width = 22,
                    Height = 22,
                    FontFamily = UiIconFontFamily,
                    FontSize = 10,
                    Content = app.IsMuted ? "\uE74F" : "\uE767",
                    Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(muteBtn, 0);
                row.Children.Add(muteBtn);

                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = Math.Clamp(Math.Round(app.Volume01 * 100.0), 0, 100),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(slider, 1);
                row.Children.Add(slider);

                string selectedRouteId = !string.IsNullOrWhiteSpace(app.RoutedOutputDeviceId) &&
                                         snap.OutputDevices.Any(d => string.Equals(d.Id, app.RoutedOutputDeviceId, StringComparison.OrdinalIgnoreCase))
                    ? app.RoutedOutputDeviceId!
                    : string.Empty;

                var routeCombo = new ComboBox
                {
                    Width = 102,
                    Height = 22,
                    FontSize = 9.0,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ItemsSource = routeChoices,
                    DisplayMemberPath = nameof(AudioRouteChoice.Label),
                    SelectedValuePath = nameof(AudioRouteChoice.DeviceId)
                };
                _suppressAudioUiEvents = true;
                routeCombo.SelectedValue = selectedRouteId;
                _suppressAudioUiEvents = false;
                Grid.SetColumn(routeCombo, 2);
                row.Children.Add(routeCombo);

                var rowRef = new AppMixerRowRef
                {
                    PctText = pctText,
                    MuteBtn = muteBtn,
                    VolSlider = slider,
                    RouteCombo = routeCombo,
                    IsMuted = app.IsMuted
                };

                muteBtn.Click += (_, _) =>
                {
                    if (!_isRenderAuditMode) _orch.Audio.SetAppMute(gKey, !rowRef.IsMuted);
                };
                slider.ValueChanged += (_, args) =>
                {
                    if (_suppressAudioUiEvents || _isRenderAuditMode) return;
                    rowRef.LastUserEditTick = Environment.TickCount64;
                    if (!rowRef.IsMuted)
                    {
                        rowRef.PctText.Text = $"{Math.Round(args.NewValue):F0}%";
                    }
                    _orch.Audio.SetAppVolume(gKey, (float)(args.NewValue / 100.0));
                };
                routeCombo.DropDownOpened += (_, _) =>
                {
                    _openAppRouteCombos++;
                    _collapseDebounceTimer.Stop();
                };
                routeCombo.DropDownClosed += (_, _) =>
                {
                    _openAppRouteCombos = Math.Max(0, _openAppRouteCombos - 1);
                    ReEvaluateCollapseAfterPopupOrCapture();
                };
                routeCombo.SelectionChanged += (_, _) =>
                {
                    if (_suppressAudioUiEvents || _isRenderAuditMode) return;
                    string? chosen = routeCombo.SelectedValue as string;
                    _orch.SetAndPersistAppOutputDevice(procName, string.IsNullOrWhiteSpace(chosen) ? null : chosen);
                };

                _appMixerRowRefs[gKey] = rowRef;
                stack.Children.Add(row);
                card.Child = stack;
                AudioAppMixerStack.Children.Add(card);
            }
            return;
        }

        _suppressAudioUiEvents = true;
        try
        {
            long nowTick = Environment.TickCount64;
            foreach (var app in snap.AppSessions)
            {
                if (!_appMixerRowRefs.TryGetValue(app.GroupKey, out var rowRef)) continue;
                bool recentlyEdited = rowRef.VolSlider.IsMouseCaptureWithin || nowTick - rowRef.LastUserEditTick <= 220;
                rowRef.IsMuted = app.IsMuted;
                rowRef.MuteBtn.Content = app.IsMuted ? "\uE74F" : "\uE767";
                if (app.IsMuted || !recentlyEdited)
                {
                    rowRef.PctText.Text = app.IsMuted ? Strings.Pick(_orch.Config.Language, "SESSİZ", "MUTED") : $"{Math.Round(app.Volume01 * 100):F0}%";
                }
                rowRef.PctText.Foreground = app.IsMuted ? HungBadgeFgBrush : MutedTabFgBrush;
                if (!recentlyEdited)
                {
                    rowRef.VolSlider.Value = Math.Clamp(Math.Round(app.Volume01 * 100.0), 0, 100);
                }
                if (!rowRef.RouteCombo.IsDropDownOpen)
                {
                    string selectedRouteId = !string.IsNullOrWhiteSpace(app.RoutedOutputDeviceId) &&
                                             snap.OutputDevices.Any(d => string.Equals(d.Id, app.RoutedOutputDeviceId, StringComparison.OrdinalIgnoreCase))
                        ? app.RoutedOutputDeviceId!
                        : string.Empty;
                    rowRef.RouteCombo.SelectedValue = selectedRouteId;
                }
            }
        }
        finally
        {
            _suppressAudioUiEvents = false;
        }
    }

    private async void OnOutputDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAudioUiEvents || _isRenderAuditMode) return;
        if (OutputDeviceCombo.SelectedValue is not string id || string.IsNullOrWhiteSpace(id)) return;
        bool ok = await _orch.Audio.SetDefaultOutputDeviceAsync(id);
        if (!ok && !_isClosed)
        {
            _suppressAudioUiEvents = true;
            OutputDeviceCombo.SelectedValue = _orch.Audio.Current.DefaultOutputId;
            _suppressAudioUiEvents = false;
        }
    }

    private async void OnInputDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAudioUiEvents || _isRenderAuditMode) return;
        if (InputDeviceCombo.SelectedValue is not string id || string.IsNullOrWhiteSpace(id)) return;
        bool ok = await _orch.Audio.SetDefaultInputDeviceAsync(id);
        if (!ok && !_isClosed)
        {
            _suppressAudioUiEvents = true;
            InputDeviceCombo.SelectedValue = _orch.Audio.Current.InputDevices.FirstOrDefault(d => d.IsDefault)?.Id;
            _suppressAudioUiEvents = false;
        }
    }

    private void OnAudioComboDropDownOpened(object? sender, EventArgs e)
    {
        _collapseDebounceTimer.Stop();
    }

    private void OnAudioComboDropDownClosed(object? sender, EventArgs e)
    {
        ReEvaluateCollapseAfterPopupOrCapture();
    }

    private void OnMasterMuteClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        _orch.Audio.SetMasterMute(!_orch.Audio.Current.IsMasterMuted);
    }

    private void OnMasterVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressAudioUiEvents || _isRenderAuditMode) return;
        _lastMasterSliderEditTick = Environment.TickCount64;
        _orch.Audio.SetMasterVolume((float)(e.NewValue / 100.0));
        MasterVolPctText.Text = $"{e.NewValue:F0}%";
    }

    private void OnMicMuteClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        _orch.ToggleMicMuteWithHud();
    }

    private void OnMicVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressAudioUiEvents || _isRenderAuditMode) return;
        _lastMicSliderEditTick = Environment.TickCount64;
        _orch.Audio.SetMicVolume((float)(e.NewValue / 100.0));
        MicVolPctText.Text = $"{e.NewValue:F0}%";
    }

    // -------------------------------------------------------------------------
    // Tab 3: CONTROL CENTER (In-Place Brightness Fix + Connectivity + Power)
    // -------------------------------------------------------------------------

    private void OnConnectivityStateChanged(object? sender, ConnectivitySnapshot snap)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!IsExpandedTabVisible(ExpandedTabId.Control))
            {
                MarkTabDirty(ExpandedTabId.Control);
                return;
            }
            RenderControlConnectivity(snap);
        });
    }

    private void RenderControlConnectivity(ConnectivitySnapshot snap)
    {
        string lang = _orch.Config.Language;
        var audio = _auditAudioOverride ?? _orch.Audio.Current;

        if (snap.IsWifiAvailable)
        {
            TileWifiIcon.Text = "\uE701";
            TileWifiIcon.Foreground = TileAccentBlueBrush;
            TileWifiLabel.Text = "Wi-Fi";
            TileWifi.Background = snap.IsWifiOn ? ActiveTileBgBrush : DefaultTileBgBrush;
            TileWifi.BorderBrush = snap.IsWifiOn ? ActiveTileBorderBrush : DefaultTileBorderBrush;
            TileWifiSub.Text = snap.IsWifiOn
                ? (string.IsNullOrWhiteSpace(snap.WifiSsid) ? Strings.Pick(lang, "Açık", "On") : snap.WifiSsid)
                : Strings.Pick(lang, "Kapalı", "Off");
        }
        else
        {
            TileWifiIcon.Text = "\uE839";
            TileWifiIcon.Foreground = snap.IsEthernetConnected ? ActiveAccentDotBrush : MutedTabFgBrush;
            TileWifiLabel.Text = "Ethernet";
            TileWifi.Background = snap.IsEthernetConnected ? ActiveTileBgBrush : DefaultTileBgBrush;
            TileWifi.BorderBrush = snap.IsEthernetConnected ? ActiveTileBorderBrush : DefaultTileBorderBrush;
            TileWifiSub.Text = snap.IsEthernetConnected
                ? (!string.IsNullOrWhiteSpace(snap.EthernetSpeedText) ? snap.EthernetSpeedText : Strings.Pick(lang, "Bağlı", "Connected"))
                : Strings.Pick(lang, "Bağlantı Yok", "Unplugged");
        }

        if (snap.IsBluetoothAvailable)
        {
            TileBtIcon.Text = "\uE702";
            TileBtIcon.Foreground = TileAccentBlueBrush;
            TileBtLabel.Text = "Bluetooth";
            TileBluetooth.Background = snap.IsBluetoothOn ? ActiveTileBgBrush : DefaultTileBgBrush;
            TileBluetooth.BorderBrush = snap.IsBluetoothOn ? ActiveTileBorderBrush : DefaultTileBorderBrush;
            var connectedBt = snap.PairedBluetoothDevices.FirstOrDefault(d => d.IsConnected);
            TileBtSub.Text = snap.IsBluetoothOn
                ? (connectedBt is not null
                    ? (connectedBt.BatteryPercent.HasValue ? $"{connectedBt.Name} {connectedBt.BatteryPercent}%" : connectedBt.Name)
                    : Strings.Pick(lang, "Açık", "On"))
                : Strings.Pick(lang, "Kapalı", "Off");
        }
        else
        {
            bool audible = !audio.IsMasterMuted;
            int volPct = (int)Math.Clamp(Math.Round(audio.MasterVolume01 * 100.0), 0, 100);
            TileBtIcon.Text = audible ? "\uE767" : "\uE74F";
            TileBtIcon.Foreground = audible ? TileAccentBlueBrush : HungBadgeFgBrush;
            TileBtLabel.Text = Strings.Pick(lang, "Ana Ses", "Sound");
            TileBluetooth.Background = audible ? ActiveTileBgBrush : DefaultTileBgBrush;
            TileBluetooth.BorderBrush = audible ? ActiveTileBorderBrush : DefaultTileBorderBrush;
            TileBtSub.Text = audible ? $"{volPct}%" : Strings.Pick(lang, "Sessiz", "Muted");
        }

        TileTheme.Background = snap.IsDarkTheme ? ActiveTileBgBrush : DefaultTileBgBrush;
        TileTheme.BorderBrush = snap.IsDarkTheme ? ActiveTileBorderBrush : DefaultTileBorderBrush;
        TileThemeSub.Text = snap.IsDarkTheme ? Strings.Pick(lang, "Koyu", "Dark") : Strings.Pick(lang, "Açık", "Light");

        TileCaffeine.Background = snap.IsCaffeineActive ? ActiveTileBgBrush : DefaultTileBgBrush;
        TileCaffeine.BorderBrush = snap.IsCaffeineActive ? ActiveTileBorderBrush : DefaultTileBorderBrush;
        TileCaffeineSub.Text = snap.IsCaffeineActive ? Strings.Pick(lang, "Uyanık", "Awake") : Strings.Pick(lang, "Kapalı", "Off");

        string defaultPlanLabel = snap.PowerModeIndex switch
        {
            0 => Strings.Pick(lang, "Güç Tasarrufu", "Power Saver"),
            2 => Strings.Pick(lang, "Yüksek Performans", "High Performance"),
            _ => Strings.Pick(lang, "Dengeli", "Balanced")
        };
        ControlPowerPlanText.Text = !string.IsNullOrWhiteSpace(snap.ActivePowerPlanName)
            ? snap.ActivePowerPlanName
            : defaultPlanLabel;

        ApplyPowerModeButtonVisual(PowerEffBtn, snap.PowerModeIndex == 0);
        ApplyPowerModeButtonVisual(PowerBalBtn, snap.PowerModeIndex == 1);
        ApplyPowerModeButtonVisual(PowerPerfBtn, snap.PowerModeIndex == 2);
    }

    private void ApplyPowerModeButtonVisual(Button button, bool isActive)
    {
        button.Background = isActive ? ActiveTileBgBrush : Brushes.Transparent;
        button.Foreground = isActive ? ActiveTabFgBrush : MutedTabFgBrush;
        button.FontWeight = isActive ? FontWeights.Bold : FontWeights.Medium;
    }

    private void OnPrivacyChanged(object? sender, PrivacySensorSnapshot snap)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            UpdateCompactAndSatelliteContent(_lastSnapshot);
        });
    }

    private void OnBrightnessDisplaysChanged(object? sender, IReadOnlyList<DisplayBrightnessInfo> displays)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!IsExpandedTabVisible(ExpandedTabId.Control))
            {
                MarkTabDirty(ExpandedTabId.Control);
                return;
            }
            RenderControlBrightness(displays);
        });
    }

    private void OnBrightnessValuesUpdated(object? sender, IReadOnlyList<DisplayBrightnessInfo> displays)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!IsExpandedTabVisible(ExpandedTabId.Control))
            {
                MarkTabDirty(ExpandedTabId.Control);
                return;
            }
            UpdateBrightnessValuesInPlace(displays);
        });
    }

    private void OnBrightnessSoftwareDimmerChanged(object? sender, (string DeviceId, nint MonitorHandle, int DimPercent) info)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            _dimmerManager.UpdateDimmer(info.DeviceId, info.MonitorHandle, info.DimPercent);
        });
    }

    private static string BuildBrightnessTopologyKey(IReadOnlyList<DisplayBrightnessInfo> displays) =>
        string.Join(";", displays.Select(d => $"{d.DeviceId}|{d.IsInternal}|{d.SupportsDdcCi}|{d.IsHdrActive}"));

    private void RenderControlBrightness(IReadOnlyList<DisplayBrightnessInfo> displays)
    {
        string nextKey = BuildBrightnessTopologyKey(displays);
        if (string.Equals(_brightnessTopologyKey, nextKey, StringComparison.Ordinal) && _brightnessRowRefs.Count > 0)
        {
            UpdateBrightnessValuesInPlace(displays);
            return;
        }

        _brightnessTopologyKey = nextKey;
        _brightnessRowRefs.Clear();
        BrightnessDisplaysStack.Children.Clear();

        if (displays.Count == 0)
        {
            BrightnessDisplaysStack.Children.Add(new TextBlock
            {
                Text = Strings.Pick(_orch.Config.Language, "Ekran bulunamadı", "No displays detected"),
                FontFamily = UiTextFontFamily,
                FontSize = 10.5,
                Foreground = MutedTabFgBrush
            });
            return;
        }

        foreach (var disp in displays)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = CardSurfaceBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var stack = new StackPanel();
            string tag = disp.IsHdrActive ? " • HDR" : disp.SupportsDdcCi ? " • DDC/CI" : disp.IsInternal ? " • eDP" : " • Dim";
            stack.Children.Add(new TextBlock
            {
                Text = $"{disp.FriendlyName}{tag}",
                FontFamily = UiTextFontFamily,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = ActiveTabFgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            string did = disp.DeviceId;
            Slider? hwSlider = null;
            TextBlock? hwLabel = null;
            bool hasHw = disp.IsInternal || disp.SupportsDdcCi;

            var controlsGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            if (hasHw)
            {
                controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var hwRow = new Grid();
                hwRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                hwRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hwRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

                var ico = new TextBlock
                {
                    Text = "\uE706",
                    FontFamily = UiIconFontFamily,
                    FontSize = 10.5,
                    Foreground = MutedTabFgBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(ico, 0);
                hwRow.Children.Add(ico);

                hwSlider = new Slider { Minimum = 0, Maximum = 100, Value = Math.Clamp(disp.HardwareBrightness01To100, 0, 100) };
                hwLabel = new TextBlock
                {
                    Text = $"{disp.HardwareBrightness01To100}%",
                    FontFamily = UiTextFontFamily,
                    FontSize = 9.5,
                    Foreground = ActiveTabFgBrush,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var labelRef = hwLabel;
                hwSlider.ValueChanged += (_, args) =>
                {
                    if (_suppressBrightnessUiEvents) return;
                    int val = (int)Math.Round(args.NewValue);
                    labelRef.Text = $"{val}%";
                    if (!_isRenderAuditMode) _orch.Brightness.SetHardwareBrightness(did, val);
                };
                Grid.SetColumn(hwSlider, 1);
                Grid.SetColumn(hwLabel, 2);
                hwRow.Children.Add(hwSlider);
                hwRow.Children.Add(hwLabel);

                Grid.SetColumn(hwRow, 0);
                controlsGrid.Children.Add(hwRow);
            }
            else
            {
                controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            var dimRow = new Grid();
            dimRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            dimRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dimRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var dimIco = new TextBlock
            {
                Text = "\uE708",
                FontFamily = UiIconFontFamily,
                FontSize = 10,
                Foreground = MutedTabFgBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dimIco, 0);
            dimRow.Children.Add(dimIco);

            var dimSlider = new Slider { Minimum = 0, Maximum = 75, Value = Math.Clamp(disp.SoftwareDimmerPercent, 0, 75) };
            var dimLabel = new TextBlock
            {
                Text = $"-{disp.SoftwareDimmerPercent}%",
                FontFamily = UiTextFontFamily,
                FontSize = 9.5,
                Foreground = MutedTabFgBrush,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            dimSlider.ValueChanged += (_, args) =>
            {
                if (_suppressBrightnessUiEvents) return;
                int val = (int)Math.Round(args.NewValue);
                dimLabel.Text = $"-{val}%";
                if (!_isRenderAuditMode) _orch.Brightness.SetSoftwareDimmer(did, val);
            };
            Grid.SetColumn(dimSlider, 1);
            Grid.SetColumn(dimLabel, 2);
            dimRow.Children.Add(dimSlider);
            dimRow.Children.Add(dimLabel);

            Grid.SetColumn(dimRow, hasHw ? 2 : 0);
            controlsGrid.Children.Add(dimRow);
            stack.Children.Add(controlsGrid);

            _brightnessRowRefs[did] = (hwSlider, hwLabel, dimSlider, dimLabel);
            card.Child = stack;
            BrightnessDisplaysStack.Children.Add(card);
        }
    }

    private void UpdateBrightnessValuesInPlace(IReadOnlyList<DisplayBrightnessInfo> displays)
    {
        _suppressBrightnessUiEvents = true;
        try
        {
            foreach (var disp in displays)
            {
                if (!_brightnessRowRefs.TryGetValue(disp.DeviceId, out var refs)) continue;
                if (refs.HwSlider is not null && !refs.HwSlider.IsMouseCaptureWithin)
                {
                    refs.HwSlider.Value = Math.Clamp(disp.HardwareBrightness01To100, 0, 100);
                }
                if (refs.HwLabel is not null)
                {
                    refs.HwLabel.Text = $"{disp.HardwareBrightness01To100}%";
                }
                if (refs.DimSlider is not null && !refs.DimSlider.IsMouseCaptureWithin)
                {
                    refs.DimSlider.Value = Math.Clamp(disp.SoftwareDimmerPercent, 0, 75);
                }
                if (refs.DimLabel is not null)
                {
                    refs.DimLabel.Text = $"-{disp.SoftwareDimmerPercent}%";
                }
            }
        }
        finally
        {
            _suppressBrightnessUiEvents = false;
        }
    }

    // -------------------------------------------------------------------------
    // Tab 4: SHELF & FULL SCROLLABLE CLIPBOARD VAULT
    // -------------------------------------------------------------------------

    private void OnShelfItemsChanged(object? sender, IReadOnlyList<ShelfItem> items)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!IsExpandedTabVisible(ExpandedTabId.Shelf))
            {
                MarkTabDirty(ExpandedTabId.Shelf);
                return;
            }
            RenderShelfItems(items);
        });
    }

    private void OnLanDropShareStateChanged(object? sender, LanDropShareInfo? share)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (share is null)
            {
                _qrOverlayVisible = false;
            }
            if (IsExpandedTabVisible(ExpandedTabId.Shelf))
            {
                RenderShelfQrOverlay();
            }
            else
            {
                MarkTabDirty(ExpandedTabId.Shelf);
            }
        });
    }

    private void RenderShelfQrOverlay()
    {
        string lang = _orch.Config.Language;
        var share = _auditQrShareOverride ?? (!_isRenderAuditMode ? _orch.LanDrop.CurrentShare : null);
        bool show = share is not null && (_auditQrShareOverride is not null || _qrOverlayVisible);
        if (!show || share is null)
        {
            ShelfQrOverlayBorder.Visibility = Visibility.Collapsed;
            ShelfItemsScroll.Visibility = Visibility.Visible;
            return;
        }

        string url = string.IsNullOrWhiteSpace(share.ShareUrl) ? share.LoopbackUrl : share.ShareUrl;
        if (!string.Equals(_renderedQrUrl, url, StringComparison.Ordinal))
        {
            _renderedQrUrl = url;
            double dpiScale = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
            BitmapSource qrBitmap = BuildQrBitmap(url, dpiScale);
            ShelfQrImage.Source = qrBitmap;
            // Size image 1:1 to physical pixels so NearestNeighbor never drops modules
            ShelfQrImage.Width = qrBitmap.PixelWidth / dpiScale;
            ShelfQrImage.Height = qrBitmap.PixelHeight / dpiScale;
        }

        ShelfQrTitleText.Text = !share.HasLanAddress
            ? Strings.QrNoLocalNetwork(lang)
            : share.FileCount > 0
                ? Strings.Pick(lang, $"LAN Paylaşım ({share.FileCount} dosya)", $"LAN Drop ({share.FileCount} files)")
                : share.HasSharedText
                    ? Strings.Pick(lang, "LAN Paylaşım (Pano Metni)", "LAN Drop (Clipboard Text)")
                    : Strings.Pick(lang, "LAN Paylaşım (Dosya Al)", "LAN Drop (Receive Files)");
        ShelfQrHintText.Text = Strings.QrScanHint(lang);
        ShelfQrUrlText.Text = url;
        ShelfQrUrlText.ToolTip = Strings.Pick(lang, "Tarayıcıda açmak için tıklayın", "Click to open in browser");
        BtnShelfQrCopyUrl.Content = Strings.Pick(lang, "Kopyala", "Copy Link");
        BtnShelfQrStop.Content = Strings.Pick(lang, "Durdur", "Stop");
        ShelfItemsScroll.Visibility = Visibility.Collapsed;
        ShelfQrOverlayBorder.Visibility = Visibility.Visible;
    }

    private static BitmapSource BuildQrBitmap(string url, double dpiScale = 1.0)
    {
        bool[,] matrix = QrCodeEncoder.Encode(url, quietZoneModules: 4);
        int size = matrix.GetLength(0);
        int targetPx = (int)Math.Round(102.0 * Math.Max(1.0, dpiScale));
        int scale = Math.Max(2, targetPx / size);
        int pxSize = size * scale;
        byte[] pixels = new byte[pxSize * pxSize * 4];
        int stride = pxSize * 4;

        for (int my = 0; my < size; my++)
        {
            for (int mx = 0; mx < size; mx++)
            {
                byte c = matrix[my, mx] ? (byte)15 : (byte)255;
                int baseY = my * scale;
                int baseX = mx * scale;
                for (int dy = 0; dy < scale; dy++)
                {
                    int rowOffset = (baseY + dy) * stride;
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int idx = rowOffset + (baseX + dx) * 4;
                        pixels[idx + 0] = c;
                        pixels[idx + 1] = c;
                        pixels[idx + 2] = c;
                        pixels[idx + 3] = 255;
                    }
                }
            }
        }

        var wb = BitmapSource.Create(pxSize, pxSize, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        wb.Freeze();
        return wb;
    }

    private void RenderShelfItems(IReadOnlyList<ShelfItem> items)
    {
        ShelfItemsStack.Children.Clear();
        ShelfEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in items)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = CardSurfaceBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            string path = item.FullPath;
            string itemId = item.Id;
            string ext = Path.GetExtension(path);
            bool isImage = !item.IsDirectory &&
                (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(ext, ".jfif", StringComparison.OrdinalIgnoreCase));

            card.ToolTip = Strings.Pick(
                _orch.Config.Language,
                "Tıkla: Dosyayı aç • Sürükle: Başka pencereye bırak",
                "Click: Open file • Drag: Drop into another window");

            Point? dragStart = null;
            bool dragStarted = false;

            card.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (IsVisualOriginButton(args.OriginalSource as DependencyObject))
                {
                    dragStart = null;
                    dragStarted = false;
                    return;
                }
                if (args.ClickCount == 1)
                {
                    dragStart = args.GetPosition(card);
                    dragStarted = false;
                }
            };

            card.PreviewMouseMove += (_, args) =>
            {
                if (!dragStart.HasValue || dragStarted || args.LeftButton != MouseButtonState.Pressed) return;
                if (!File.Exists(path) && !Directory.Exists(path)) return;

                Vector delta = args.GetPosition(card) - dragStart.Value;
                if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }

                dragStarted = true;
                dragStart = null;
                _isInternalDragOut = true;
                try
                {
                    var data = new System.Windows.DataObject(DataFormats.FileDrop, new[] { path });
                    DragDrop.DoDragDrop(card, data, DragDropEffects.Copy | DragDropEffects.Move);
                }
                finally
                {
                    _isInternalDragOut = false;
                }
            };

            card.MouseLeftButtonUp += (_, args) =>
            {
                if (!dragStart.HasValue || dragStarted || IsVisualOriginButton(args.OriginalSource as DependencyObject))
                {
                    dragStart = null;
                    dragStarted = false;
                    return;
                }

                dragStart = null;
                if (_isRenderAuditMode) return;
                if (File.Exists(path) || Directory.Exists(path))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        _orch.Notifications.AddInternalAlert("DinamikAda", item.Name, ex.Message);
                    }
                }
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = item.IsDirectory ? "\uE8B7" : "\uE8A5",
                FontFamily = UiIconFontFamily,
                FontSize = 13,
                Foreground = ParseHexBrush("#64D2FF", "#64D2FF"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontFamily = UiTextFontFamily,
                FontSize = 10.8,
                FontWeight = FontWeights.SemiBold,
                Foreground = ActiveTabFgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            meta.Children.Add(new TextBlock
            {
                Text = item.IsDirectory ? Strings.Pick(_orch.Config.Language, "Klasör", "Folder") : FormatBytes(item.SizeBytes),
                FontFamily = UiTextFontFamily,
                FontSize = 9.5,
                Foreground = MutedTabFgBrush
            });
            Grid.SetColumn(meta, 1);
            row.Children.Add(meta);

            var rowBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (isImage)
            {
                var exifBtn = new Button
                {
                    Style = (Style)FindResource("PillTabButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 8.8,
                    Content = Strings.ExifCleanBtn(_orch.Config.Language),
                    ToolTip = Strings.Pick(_orch.Config.Language, "EXIF temizle ve sıkıştırılmış kopya üret", "Strip EXIF & compress clean copy"),
                    Margin = new Thickness(4, 0, 4, 0),
                    Foreground = ActiveAccentDotBrush
                };
                exifBtn.Click += async (_, args) =>
                {
                    args.Handled = true;
                    if (_isRenderAuditMode) return;
                    try
                    {
                        var res = await _orch.Shelf.CompressAndStripExifCopyAsync(path);
                        if (res.Succeeded && !string.IsNullOrWhiteSpace(res.CleanFilePath))
                        {
                            string cleanName = Path.GetFileName(res.CleanFilePath);
                            _orch.Coordinator.Publish(new TransientHudEvent(
                                CoalesceKey: "hud:exif-clean",
                                Kind: HudKind.FileStashed,
                                Title: Strings.ExifCleanDoneTitle(_orch.Config.Language),
                                Subtitle: $"{cleanName} ({FormatBytes(res.OriginalBytes)} → {FormatBytes(res.CleanBytes)})",
                                AccentHex: "#30D158",
                                DurationMs: 2400,
                                InteractiveOnHover: true,
                                IconGlyph: "\uE91B"));
                        }
                        else if (!string.IsNullOrWhiteSpace(res.ErrorMessage))
                        {
                            _orch.Notifications.AddInternalAlert("DinamikAda", Strings.ExifCleanBtn(_orch.Config.Language), res.ErrorMessage);
                        }
                    }
                    catch (Exception ex)
                    {
                        _orch.Notifications.AddInternalAlert("DinamikAda", Strings.ExifCleanBtn(_orch.Config.Language), ex.Message);
                    }
                };
                rowBtns.Children.Add(exifBtn);
            }

            var copyBtn = new Button
            {
                Style = (Style)FindResource("IconCircleButtonStyle"),
                Width = 22,
                Height = 22,
                FontFamily = UiIconFontFamily,
                FontSize = 9.5,
                Margin = new Thickness(2, 0, 4, 0),
                Content = "\uE8C8",
                ToolTip = Strings.Pick(_orch.Config.Language, "Panoya kopyala", "Copy to clipboard")
            };
            copyBtn.Click += (_, args) =>
            {
                args.Handled = true;
                if (_isRenderAuditMode) return;
                CopyShelfItemToClipboard(path, isImage, item.Name);
            };
            rowBtns.Children.Add(copyBtn);

            var remBtn = new Button
            {
                Style = (Style)FindResource("IconCircleButtonStyle"),
                Width = 22,
                Height = 22,
                FontFamily = UiIconFontFamily,
                FontSize = 9,
                Content = "\uE711",
                ToolTip = Strings.Pick(_orch.Config.Language, "Raftan kaldır", "Remove from shelf")
            };
            remBtn.Click += (_, args) =>
            {
                args.Handled = true;
                _orch.Shelf.Remove(itemId);
            };
            rowBtns.Children.Add(remBtn);
            Grid.SetColumn(rowBtns, 2);
            row.Children.Add(rowBtns);

            card.Child = row;
        ShelfItemsStack.Children.Add(card);
        }
    }

    private static bool IsVisualOriginButton(DependencyObject? source)
    {
        DependencyObject? cur = source;
        while (cur is not null)
        {
            if (cur is Button) return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private void CopyShelfItemToClipboard(string path, bool isImage, string displayName)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;

            if (!isImage && File.Exists(path) && string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                if (info.Length <= 256 * 1024)
                {
                    string txt = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(txt) && _orch.Clipboard.CopyTextToClipboard(txt))
                    {
                        _orch.Coordinator.Publish(new TransientHudEvent(
                            CoalesceKey: "hud:clipboard",
                            Kind: HudKind.Clipboard,
                            Title: Strings.Pick(_orch.Config.Language, "Panoya Kopyalandı", "Copied to Clipboard"),
                            Subtitle: displayName,
                            AccentHex: "#64D2FF",
                            DurationMs: 1600,
                            IconGlyph: "\uE8C8"));
                        return;
                    }
                }
            }

            var dataObj = new System.Windows.DataObject();
            if (isImage && File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                dataObj.SetImage(bmp);
            }

            var dropList = new System.Collections.Specialized.StringCollection { path };
            dataObj.SetFileDropList(dropList);
            System.Windows.Clipboard.SetDataObject(dataObj, true);

            _orch.Coordinator.Publish(new TransientHudEvent(
                CoalesceKey: "hud:clipboard",
                Kind: HudKind.Clipboard,
                Title: Strings.Pick(_orch.Config.Language, "Panoya Kopyalandı", "Copied to Clipboard"),
                Subtitle: displayName,
                AccentHex: "#64D2FF",
                DurationMs: 1600,
                IconGlyph: "\uE8C8"));
        }
        catch (Exception ex)
        {
            _orch.Notifications.AddInternalAlert("DinamikAda", displayName, ex.Message);
        }
    }

    private void OnClipboardEntriesChanged(object? sender, IReadOnlyList<ClipboardEntry> entries)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!IsExpandedTabVisible(ExpandedTabId.Shelf))
            {
                MarkTabDirty(ExpandedTabId.Shelf);
                return;
            }
            RenderClipboardItems(entries);
        });
    }

    private void RenderClipboardItems(IReadOnlyList<ClipboardEntry> entries)
    {
        ClipboardItemsStack.Children.Clear();
        ClipboardEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Render full configured vault history without arbitrary .Take(10) cutoff
        foreach (var entry in entries)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = CardSurfaceBrush,
                BorderBrush = entry.IsPinned ? ActiveTileBorderBrush : CardBorderBrush,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            string eid = entry.Id;
            string copyText = entry.FullText ?? entry.PreviewText;
            card.MouseLeftButtonUp += (_, _) =>
            {
                if (!string.IsNullOrEmpty(copyText))
                {
                    _orch.Clipboard.CopyTextToClipboard(copyText);
                }
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(new TextBlock
            {
                Text = entry.PreviewText,
                FontFamily = UiTextFontFamily,
                FontSize = 10.6,
                FontWeight = entry.IsPinned ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ActiveTabFgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textCol.Children.Add(new TextBlock
            {
                Text = entry.IsPinned ? Strings.Pick(_orch.Config.Language, $"{entry.Kind} • Sabit", $"{entry.Kind} • Pinned") : entry.Kind,
                FontFamily = UiTextFontFamily,
                FontSize = 9.2,
                Foreground = MutedTabFgBrush
            });
            Grid.SetColumn(textCol, 0);
            row.Children.Add(textCol);

            var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var suggestion = ClipboardVaultService.TryDetectSmartAction(copyText, _orch.Config.Language);
            if (suggestion is not null)
            {
                var smartBtn = new Button
                {
                    Style = (Style)FindResource("PillTabButtonStyle"),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 8.8,
                    Content = suggestion.ActionLabel,
                    ToolTip = suggestion.Preview,
                    Margin = new Thickness(4, 0, 0, 0),
                    Foreground = TileAccentBlueBrush
                };
                string transformed = suggestion.TransformedText;
                string actionLbl = suggestion.ActionLabel;
                string previewTxt = suggestion.Preview;
                smartBtn.Click += (_, args) =>
                {
                    args.Handled = true;
                    if (_isRenderAuditMode) return;
                    _orch.Clipboard.CopyTextToClipboard(transformed);
                    _orch.Coordinator.Publish(new TransientHudEvent(
                        CoalesceKey: "hud:clip-smart",
                        Kind: HudKind.Clipboard,
                        Title: actionLbl,
                        Subtitle: previewTxt,
                        AccentHex: "#0A84FF",
                        DurationMs: 1800,
                        InteractiveOnHover: true,
                        IconGlyph: "\uE77F"));
                };
                btns.Children.Add(smartBtn);
            }
            var pinBtn = new Button
            {
                Style = (Style)FindResource("IconCircleButtonStyle"),
                Width = 22,
                Height = 22,
                FontFamily = UiIconFontFamily,
                FontSize = 9.5,
                Content = "\uE718",
                Foreground = entry.IsPinned ? ActiveAccentDotBrush : MutedTabFgBrush,
                Margin = new Thickness(4, 0, 4, 0)
            };
            pinBtn.Click += (_, args) =>
            {
                args.Handled = true;
                _orch.Clipboard.PinToggle(eid);
            };
            var delBtn = new Button
            {
                Style = (Style)FindResource("IconCircleButtonStyle"),
                Width = 22,
                Height = 22,
                FontFamily = UiIconFontFamily,
                FontSize = 9,
                Content = "\uE711"
            };
            delBtn.Click += (_, args) =>
            {
                args.Handled = true;
                _orch.Clipboard.Delete(eid);
            };
            btns.Children.Add(pinBtn);
            btns.Children.Add(delBtn);

            Grid.SetColumn(btns, 1);
            row.Children.Add(btns);
            card.Child = row;
            ClipboardItemsStack.Children.Add(card);
        }
    }

    // -------------------------------------------------------------------------
    // Tab 5: SYSTEM TELEMETRY & HUNG PROCESS WATCHDOG
    // -------------------------------------------------------------------------

    private void OnTelemetryUpdated(object? sender, SystemTelemetrySnapshot snap)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (_lastSnapshot.State == IslandPresentationState.S4Expanded && _lastSnapshot.ActiveTab == ExpandedTabId.System)
            {
                RenderSystemTelemetry(snap);
            }
            if (_orch.Config.ShowTelemetryInPill && _lastSnapshot.State == IslandPresentationState.S1Dormant)
            {
                DormantTelemetryText.Text = $"CPU {snap.CpuPercent:F0}%";
            }
        });
    }

    private void RenderSystemTelemetry(SystemTelemetrySnapshot snap)
    {
        var lang = _orch.Config.Language;
        SysCpuVal.Text = $"{snap.CpuPercent:F0}%";
        SysCpuBar.Value = Math.Clamp(snap.CpuPercent, 0, 100);

        if (!snap.HasBattery)
        {
            var topProc = snap.TopOrHungProcesses
                .OrderByDescending(p => p.CpuPercent)
                .FirstOrDefault(p => p.CpuPercent >= 0.5);
            SysGpuSub.Text = topProc is not null
                ? $"{topProc.ProcessName} • %{topProc.CpuPercent:F1}"
                : Strings.Pick(lang, $"{Environment.ProcessorCount} Çekirdek", $"{Environment.ProcessorCount} Threads");
        }
        else
        {
            SysGpuSub.Text = $"GPU {snap.GpuPercent:F0}%";
        }

        SysMemVal.Text = $"{snap.MemoryPercent:F0}%";
        SysMemBar.Value = Math.Clamp(snap.MemoryPercent, 0, 100);
        SysMemSub.Text = $"{snap.MemoryUsedGb:F1} / {snap.MemoryTotalGb:F1} GB";

        SysNetDownVal.Text = $"{snap.NetDownMbps:F1} Mb/s";
        SysNetUpSub.Text = Strings.Pick(lang, $"Yük. {snap.NetUpMbps:F1} Mb/s", $"Up {snap.NetUpMbps:F1} Mb/s");

        if (!snap.HasBattery)
        {
            SysBatLabel.Text = "GPU";
            SysBatVal.Text = $"{snap.GpuPercent:F0}%";
            SysBatBar.Value = Math.Clamp(snap.GpuPercent, 0, 100);
            string shortGpu = SystemTelemetryService.ShortenGpuName(snap.GpuName);
            SysBatSub.Text = !string.IsNullOrWhiteSpace(snap.GpuEngine) && snap.GpuPercent >= 1.0
                ? $"{shortGpu} • {snap.GpuEngine}"
                : shortGpu;
        }
        else
        {
            SysBatLabel.Text = Strings.BatteryLabel(lang);
            SysBatVal.Text = $"{snap.BatteryPercent}%";
            SysBatBar.Value = Math.Clamp(snap.BatteryPercent, 0, 100);
            SysBatSub.Text = snap.IsCharging
                ? Strings.Pick(lang, "Şarj Oluyor", "Charging")
                : snap.BatteryRemaining.HasValue
                    ? $"{(int)snap.BatteryRemaining.Value.TotalHours}h {snap.BatteryRemaining.Value.Minutes:00}m"
                    : Strings.Pick(lang, "Pil", "On Battery");
        }

        var gpuParts = new List<string>(3);
        if (snap.GpuVramTotalGb > 0.05)
        {
            gpuParts.Add($"{snap.GpuVramUsedGb:F1}/{snap.GpuVramTotalGb:F1} GB");
        }
        if (snap.GpuTemperatureC.HasValue)
        {
            gpuParts.Add($"{Math.Round(snap.GpuTemperatureC.Value):F0}°C");
        }
        if (snap.GpuFanPercent.HasValue)
        {
            gpuParts.Add($"{snap.GpuFanPercent.Value}% Fan");
        }
        else if (snap.GpuFanRpm.HasValue)
        {
            gpuParts.Add($"{snap.GpuFanRpm.Value} RPM");
        }
        SysGpuDetailText.Text = gpuParts.Count > 0 ? string.Join(" • ", gpuParts) : "--";

        string volPart = !snap.DiskVolumes.IsDefaultOrEmpty
            ? string.Join("  ", snap.DiskVolumes.Take(2).Select(d => $"{d.DriveLetter} {d.UsedPercent:F0}%"))
            : "--";
        string ioPart = $"R {snap.DiskReadMbps:F1} / W {snap.DiskWriteMbps:F1} MB/s";
        SysDiskDetailText.Text = $"{volPart} • {ioPart}";

        string pingHost = string.IsNullOrWhiteSpace(snap.PingHost) ? _orch.Config.PingHost : snap.PingHost;
        SysPingDetailLabel.Text = $"{Strings.PingLabel(lang)} ({pingHost})";
        if (snap.PingMs.HasValue)
        {
            double ms = Math.Round(snap.PingMs.Value);
            SysPingDetailText.Text = $"{ms:F0} ms";
            SysPingDetailText.Foreground = ms <= 45.0
                ? ActiveAccentDotBrush
                : (ms <= 90.0 ? MicLiveBrush : HungBadgeFgBrush);
        }
        else
        {
            SysPingDetailText.Text = "--";
            SysPingDetailText.Foreground = MutedTabFgBrush;
        }

        SystemProcessStack.Children.Clear();
        foreach (var proc in snap.TopOrHungProcesses)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = proc.IsHung ? HungBadgeBgBrush : CardSurfaceBrush,
                BorderBrush = proc.IsHung ? HungBadgeFgBrush : CardBorderBrush,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            string procLabel = proc.ProcessCount > 1
                ? $"{proc.ProcessName} ({proc.ProcessCount})"
                : proc.ProcessName;
            left.Children.Add(new TextBlock
            {
                Text = proc.IsHung
                    ? Strings.Pick(lang, $"{proc.ProcessName} (Yanıt Vermiyor)", $"{proc.ProcessName} (Not Responding)")
                    : procLabel,
                FontFamily = UiTextFontFamily,
                FontSize = 10.6,
                FontWeight = FontWeights.SemiBold,
                Foreground = proc.IsHung ? HungBadgeFgBrush : ActiveTabFgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            string cpuTag = $"%{proc.CpuPercent:F1} CPU • ";
            string primaryTag = !string.IsNullOrWhiteSpace(proc.WindowTitle)
                ? proc.WindowTitle
                : (proc.ProcessCount > 1
                    ? Strings.Pick(lang, $"{proc.ProcessCount} süreç", $"{proc.ProcessCount} processes")
                    : $"PID {proc.ProcessId}");
            left.Children.Add(new TextBlock
            {
                Text = $"{cpuTag}{proc.MemoryMb:F0} MB • {primaryTag}",
                FontFamily = UiTextFontFamily,
                FontSize = 9.5,
                Foreground = MutedTabFgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            if (!proc.IsProtected)
            {
                int pid = proc.ProcessId;
                string procName = proc.ProcessName;
                int procCount = proc.ProcessCount;
                var killBtn = new Button
                {
                    Style = (Style)FindResource("DarkActionButtonStyle"),
                    Content = Strings.Pick(_orch.Config.Language, "Sonlandır", "End"),
                    Padding = new Thickness(9, 3, 9, 3),
                    FontSize = 10,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                killBtn.Click += async (_, _) =>
                {
                    if (_orch.HungProcesses.TryKillProcessGroup(pid, procName, procCount, out string? err))
                    {
                        await _orch.Telemetry.SampleOnceAsync();
                    }
                    else if (!string.IsNullOrWhiteSpace(err))
                    {
                        _orch.Notifications.AddInternalAlert("DinamikAda", proc.ProcessName, err);
                    }
                };
                Grid.SetColumn(killBtn, 1);
                row.Children.Add(killBtn);
            }

            card.Child = row;
            SystemProcessStack.Children.Add(card);
        }
    }

    // -------------------------------------------------------------------------
    // Tab 6: WIDGETS
    // -------------------------------------------------------------------------

    private void OnWidgetsChanged(object? sender, IReadOnlyList<WidgetRenderSnapshot> widgets)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (!IsExpandedTabVisible(ExpandedTabId.Widgets))
            {
                MarkTabDirty(ExpandedTabId.Widgets);
                return;
            }
            RenderWidgetsTab(widgets);
        });
    }

    private void RenderWidgetsTab(IReadOnlyList<WidgetRenderSnapshot> widgets)
    {
        WidgetsWrapPanel.Children.Clear();
        WidgetsEmptyText.Visibility = widgets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var w in widgets)
        {
            var card = new Border
            {
                Width = 178,
                CornerRadius = new CornerRadius(14),
                Background = CardSurfaceBrush,
                BorderBrush = w.IsFaulted ? HungBadgeFgBrush : CardBorderBrush,
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 8, 8)
            };

            var stack = new StackPanel();
            var head = new Grid();
            head.Children.Add(new TextBlock
            {
                Text = w.Name,
                FontFamily = UiTextFontFamily,
                FontSize = 10,
                Foreground = ParseHexBrush(w.AccentHex, "#0A84FF"),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            head.Children.Add(new TextBlock
            {
                Text = w.Status,
                FontFamily = UiTextFontFamily,
                FontSize = 9.5,
                Foreground = w.IsFaulted ? HungBadgeFgBrush : MutedTabFgBrush,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            });
            stack.Children.Add(head);

            stack.Children.Add(new TextBlock
            {
                Text = w.Title,
                FontFamily = UiTextFontFamily,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ActiveTabFgBrush,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            stack.Children.Add(new TextBlock
            {
                Text = w.IsFaulted ? (w.FaultReason ?? Strings.Pick(_orch.Config.Language, "Arızalı", "Faulted")) : w.Subtitle,
                FontFamily = UiTextFontFamily,
                FontSize = 10,
                Foreground = MutedTabFgBrush,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (w.Progress0To100.HasValue)
            {
                var pb = new System.Windows.Controls.ProgressBar
                {
                    Style = (Style)FindResource("ObsidianProgressBarStyle"),
                    Minimum = 0,
                    Maximum = 100,
                    Value = Math.Clamp(w.Progress0To100.Value, 0, 100),
                    Foreground = ParseHexBrush(w.AccentHex, "#0A84FF"),
                    Height = 4,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                stack.Children.Add(pb);
            }

            if (w.IsFaulted || w.Actions.Length > 0)
            {
                var actionBar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                string widgetId = w.WidgetId;
                if (w.IsFaulted)
                {
                    var resetBtn = new Button
                    {
                        Style = (Style)FindResource("PillTabButtonStyle"),
                        Content = Strings.ResetTimer(_orch.Config.Language),
                        Padding = new Thickness(7, 2, 7, 2),
                        FontSize = 9.5
                    };
                    resetBtn.Click += (_, _) => _orch.Widgets.ResetBreaker(widgetId);
                    actionBar.Children.Add(resetBtn);
                }
                foreach (var act in w.Actions)
                {
                    string actionId = act.Id;
                    var btn = new Button
                    {
                        Style = (Style)FindResource("PillTabButtonStyle"),
                        Content = act.Label,
                        Padding = new Thickness(7, 2, 7, 2),
                        FontSize = 9.5,
                        Margin = new Thickness(0, 0, 4, 2)
                    };
                    btn.Click += async (_, _) => await _orch.Widgets.ExecuteActionAsync(widgetId, actionId);
                    actionBar.Children.Add(btn);
                }
                stack.Children.Add(actionBar);
            }

            card.Child = stack;
            WidgetsWrapPanel.Children.Add(card);
        }
    }

    // -------------------------------------------------------------------------
    // Command Palette (S5Command — Single Click + ScrollBar Safe Guard)
    // -------------------------------------------------------------------------

    private void FocusCommandPalette()
    {
        CommandSearchBox.Text = string.Empty;
        CommandPlaceholderText.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            CommandSearchBox.Focus();
            Keyboard.Focus(CommandSearchBox);
        });
        _ = QueryCommandPaletteAsync(string.Empty);
    }

    private void OnCommandQueryChanged(object sender, TextChangedEventArgs e)
    {
        string q = CommandSearchBox.Text;
        CommandPlaceholderText.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;
        _ = QueryCommandPaletteAsync(q);
    }

    private async Task QueryCommandPaletteAsync(string query)
    {
        if (_isClosed) return;
        ShowCommandStatus(null);
        _commandSearchCts?.Cancel();
        _commandSearchCts?.Dispose();
        _commandSearchCts = new CancellationTokenSource();
        var ct = _commandSearchCts.Token;

        try
        {
            if (query.Length > 0)
            {
                await Task.Delay(35, ct);
            }
            var items = await _orch.Launcher.QueryAsync(query, ct);
            if (ct.IsCancellationRequested || _isClosed) return;

            CommandResultsList.ItemsSource = items;
            if (items.Count > 0)
            {
                CommandResultsList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer keystroke.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && !_isClosed) ShowCommandStatus(ex.Message);
        }
    }

    private async void OnCommandSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (CommandResultsList.Items.Count > 0)
                {
                    CommandResultsList.SelectedIndex = Math.Min(CommandResultsList.Items.Count - 1, Math.Max(0, CommandResultsList.SelectedIndex + 1));
                    CommandResultsList.ScrollIntoView(CommandResultsList.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Up:
                if (CommandResultsList.Items.Count > 0)
                {
                    CommandResultsList.SelectedIndex = Math.Max(0, CommandResultsList.SelectedIndex - 1);
                    CommandResultsList.ScrollIntoView(CommandResultsList.SelectedItem);
                }
                e.Handled = true;
                break;

            case Key.Enter:
                e.Handled = true;
                await ExecuteSelectedCommandAsync();
                break;

            case Key.Escape:
                e.Handled = true;
                _orch.Coordinator.CloseCommandPalette();
                break;
        }
    }

    private async void OnCommandResultPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject dep) return;
        if (HasAncestor<System.Windows.Controls.Primitives.ScrollBar>(dep)) return;

        var item = FindAncestor<ListBoxItem>(dep);
        if (item is null) return;

        CommandResultsList.SelectedItem = item.DataContext;
        e.Handled = true;
        await ExecuteSelectedCommandAsync();
    }

    private async Task ExecuteSelectedCommandAsync()
    {
        if (_isClosed || _isExecutingCommand || CommandResultsList.SelectedItem is not CommandEntry entry) return;
        _isExecutingCommand = true;
        try
        {
            var res = await entry.ExecuteAsync(CancellationToken.None);
            if (_isClosed) return;
            if (!res.Succeeded)
                ShowCommandStatus(res.FeedbackTitle ?? Strings.Pick(_orch.Config.Language, "İşlem tamamlanamadı.", "The operation could not be completed."));
            if (res.CollapseAfter)
            {
                _controller.SetActivatable(false, restoreFocusHwnd: null, restorePreviousFocus: res.RestorePreviousFocus);
                _orch.Coordinator.CloseCommandPalette();
            }
        }
        catch (Exception ex) { if (!_isClosed) ShowCommandStatus(ex.Message); }
        finally { _isExecutingCommand = false; }
    }

    private void ShowCommandStatus(string? message)
    {
        CommandStatusText.Text = message ?? string.Empty;
        CommandStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    // -------------------------------------------------------------------------
    // Pointer, Hover, Click, Wheel, Reposition Drag & Drop
    // -------------------------------------------------------------------------

    private bool IsAnyPointerOverIsland() => _isMainHovered || _isSatelliteHovered;

    private bool IsPopupOrDropdownOpen() =>
        OutputDeviceCombo.IsDropDownOpen ||
        InputDeviceCombo.IsDropDownOpen ||
        ComboSchedAction.IsDropDownOpen ||
        ComboSchedMinutes.IsDropDownOpen ||
        _openAppRouteCombos > 0 ||
        (_islandContextMenu?.IsOpen ?? false) ||
        Mouse.Captured is not null ||
        ExpandedSurface.IsMouseCaptureWithin ||
        MainNotchAssembly.IsMouseCaptureWithin;

    private void ReEvaluateCollapseAfterPopupOrCapture()
    {
        if (_lastSnapshot.State == IslandPresentationState.S4Expanded &&
            !_lastSnapshot.IsPinnedOpen &&
            !IsAnyPointerOverIsland() &&
            !IsPopupOrDropdownOpen())
        {
            _collapseDebounceTimer.Stop();
            _collapseDebounceTimer.Start();
        }
    }

    private void OnExpandedSurfaceLostMouseCapture(object sender, MouseEventArgs e)
    {
        ReEvaluateCollapseAfterPopupOrCapture();
    }

    private void OnMainAssemblyMouseEnter(object sender, MouseEventArgs e)
    {
        if (_isSmokeMode || _isRenderAuditMode) return;
        _orch.WindowControl.CaptureForegroundIfExternal();
        _isMainHovered = true;
        _collapseDebounceTimer.Stop();

        if (_lastSnapshot.State is IslandPresentationState.S1Dormant or IslandPresentationState.S2Compact or IslandPresentationState.S2Split)
        {
            if (_orch.Config.ExpandTrigger != ExpandTriggerMode.ClickOnly)
            {
                _motion.SetHoverScale(1.018);
                StartRenderLoop();
                _hoverDwellTimer.Stop();
                _hoverDwellTimer.Start();
            }
        }
        else if (_lastSnapshot.State == IslandPresentationState.S3TransientHud)
        {
            _orch.Coordinator.SetHudHovered(true);
            _motion.SetHoverScale(1.012);
            StartRenderLoop();
        }
    }

    private void OnMainAssemblyMouseLeave(object sender, MouseEventArgs e)
    {
        _isMainHovered = false;
        _hoverDwellTimer.Stop();

        if (!_isSatelliteHovered)
        {
            _motion.SetHoverScale(1.0);
            StartRenderLoop();
        }

        if (_lastSnapshot.State == IslandPresentationState.S3TransientHud)
        {
            _orch.Coordinator.SetHudHovered(false);
        }
        else if (_lastSnapshot.State == IslandPresentationState.S4Expanded &&
                 !_lastSnapshot.IsPinnedOpen &&
                 !_isSatelliteHovered &&
                 !IsPopupOrDropdownOpen() &&
                 !_isDraggingNotch)
        {
            _collapseDebounceTimer.Stop();
            _collapseDebounceTimer.Start();
        }
    }

    private void OnSatelliteMouseEnter(object sender, MouseEventArgs e)
    {
        if (_isSmokeMode || _isRenderAuditMode) return;
        _isSatelliteHovered = true;
        _collapseDebounceTimer.Stop();
        StartRenderLoop();
    }

    private void OnSatelliteMouseLeave(object sender, MouseEventArgs e)
    {
        if (_isSmokeMode || _isRenderAuditMode) return;
        _isSatelliteHovered = false;
        StartRenderLoop();
        ReEvaluateCollapseAfterPopupOrCapture();
    }

    private void OnHoverDwellTick(object? sender, EventArgs e)
    {
        _hoverDwellTimer.Stop();
        if (_isSmokeMode || _isRenderAuditMode) return;
        if (_orch.Config.ExpandTrigger == ExpandTriggerMode.ClickOnly) return;
        if (_isMainHovered &&
            _lastSnapshot.State is IslandPresentationState.S1Dormant or IslandPresentationState.S2Compact or IslandPresentationState.S2Split &&
            !_isDraggingNotch)
        {
            _orch.Coordinator.RequestExpand();
        }
    }

    private void OnCollapseDebounceTick(object? sender, EventArgs e)
    {
        _collapseDebounceTimer.Stop();
        if (_lastSnapshot.State == IslandPresentationState.S4Expanded &&
            !_lastSnapshot.IsPinnedOpen &&
            !IsAnyPointerOverIsland() &&
            !IsPopupOrDropdownOpen() &&
            !_isDraggingNotch)
        {
            _orch.Coordinator.RequestCollapse();
        }
    }

    private void OnMainAssemblyMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSmokeMode || _isRenderAuditMode) return;
        if (e.OriginalSource is DependencyObject dep && IsInteractiveChild(dep))
        {
            return;
        }

        _isPointerPressed = true;
        _didDragMove = false;
        var (cx, cy) = IslandWindowController.GetCursorScreenPos();
        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        _dragStartScreenDip = new Point(cx / scale, cy / scale);

        double monLeftDip = _activeMonitor.LeftPx / scale;
        double monWidthDip = _activeMonitor.WidthPx / scale;
        _dragStartCenterDip = monLeftDip + monWidthDip * MonitorLayoutCalculator.ResolveAnchorRatio(_orch.Config.HorizontalDetent, _orch.Config.CustomHorizontalRatio);
        _dragStartTopDip = _orch.Config.CustomFreeTopDip;
    }

    private void OnMainAssemblyMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerPressed || e.LeftButton != MouseButtonState.Pressed) return;
        if (_lastSnapshot.State is IslandPresentationState.S4Expanded or IslandPresentationState.S5Command or IslandPresentationState.S6DragShelf)
        {
            // Only drag from top chrome bar if expanded; never hijack card/slider drags
            if (e.OriginalSource is DependencyObject dep && IsInteractiveChild(dep)) return;
            if (e.GetPosition(MainNotchAssembly).Y > 44.0) return;
        }

        var (cx, cy) = IslandWindowController.GetCursorScreenPos();
        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        double curXDip = cx / scale;
        double curYDip = cy / scale;

        double dx = curXDip - _dragStartScreenDip.X;
        double dy = curYDip - _dragStartScreenDip.Y;
        if (!_isDraggingNotch && Math.Sqrt(dx * dx + dy * dy) < 6.0) return;

        if (!_isDraggingNotch)
        {
            _isDraggingNotch = true;
            _didDragMove = true;
            _hoverDwellTimer.Stop();
            _dragCursorMonitorTimer.Start();
            MainNotchAssembly.CaptureMouse();
        }

        double monLeftDip = _activeMonitor.LeftPx / scale;
        double monWidthDip = _activeMonitor.WidthPx / scale;
        double rawCenterDip = _dragStartCenterDip + dx;
        var (detent, ratio) = MonitorLayoutCalculator.SnapHorizontalDetent(rawCenterDip - monLeftDip, monWidthDip);
        _orch.Config.HorizontalDetent = detent;
        _orch.Config.CustomHorizontalRatio = ratio;

        if (_orch.Config.DockPlacement == DockPlacementMode.FreeFloat)
        {
            _orch.Config.CustomFreeTopDip = Math.Clamp(Math.Round(_dragStartTopDip + dy, 1), 0.0, 2000.0);
        }

        ApplyTierAndMonitorPlacement(_lastSnapshot, snapImmediately: true);
        ApplyMotionFrameToVisuals(_lastSnapshot, forceHitUpdate: false);
    }

    private void OnMainAssemblyMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool wasDragging = _isDraggingNotch;
        _isPointerPressed = false;
        _isDraggingNotch = false;
        _dragCursorMonitorTimer.Stop();
        if (MainNotchAssembly.IsMouseCaptured)
        {
            MainNotchAssembly.ReleaseMouseCapture();
        }

        if (wasDragging && _didDragMove)
        {
            _orch.SaveConfig();
            _hitContourDirty = true;
            ApplyMotionFrameToVisuals(_lastSnapshot, forceHitUpdate: true);
            return;
        }

        if (e.OriginalSource is DependencyObject dep && IsInteractiveChild(dep))
        {
            return;
        }

        if (_lastSnapshot.State is IslandPresentationState.S1Dormant or IslandPresentationState.S2Compact or IslandPresentationState.S2Split)
        {
            _orch.Coordinator.RequestExpand(_lastSnapshot.PrimaryActivity?.TargetTab);
        }
        else if (_lastSnapshot.State == IslandPresentationState.S3TransientHud)
        {
            _orch.Coordinator.PromoteTransientHudInteractive();
        }
    }

    private void OnDragCursorMonitorTick(object? sender, EventArgs e)
    {
        if (!_isDraggingNotch)
        {
            _dragCursorMonitorTimer.Stop();
            return;
        }
        if (!IslandWindowController.IsLeftMouseButtonDown())
        {
            _isPointerPressed = false;
            _isDraggingNotch = false;
            _dragCursorMonitorTimer.Stop();
            if (MainNotchAssembly.IsMouseCaptured) MainNotchAssembly.ReleaseMouseCapture();
            _orch.SaveConfig();
            _hitContourDirty = true;
            ApplyMotionFrameToVisuals(_lastSnapshot, forceHitUpdate: true);
        }
    }

    private void OnSatelliteMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isSmokeMode || _isRenderAuditMode) return;
        e.Handled = true;
        if (_lastSnapshot.SatelliteActivity?.Kind == ActivityKind.Meeting)
        {
            _orch.JoinUpcomingMeeting();
            return;
        }
        var targetTab = _lastSnapshot.SatelliteActivity?.TargetTab ?? ExpandedTabId.Now;
        _orch.Coordinator.RequestExpand(targetTab);
    }

    private void OnMainAssemblyMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenContextMenuAtIsland();
    }

    private void OnMainAssemblyMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_lastSnapshot.State is not (IslandPresentationState.S1Dormant or IslandPresentationState.S2Compact or IslandPresentationState.S2Split or IslandPresentationState.S3TransientHud))
        {
            return;
        }
        if (_isRenderAuditMode) return;

        if (_orch.Config.NotchDesktopSwipeEnabled &&
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            TrySwitchVirtualDesktopDebounced(next: e.Delta < 0);
            e.Handled = true;
            return;
        }

        float cur = _orch.Audio.Current.MasterVolume01;
        float delta = e.Delta > 0 ? 0.04f : -0.04f;
        _orch.Audio.SetMasterVolume(Math.Clamp(cur + delta, 0f, 1f));
        e.Handled = true;
    }

    private long _lastDesktopSwipeTickMs;

    private void TrySwitchVirtualDesktopDebounced(bool next)
    {
        long now = Environment.TickCount64;
        if (now - _lastDesktopSwipeTickMs < 260) return;
        _lastDesktopSwipeTickMs = now;
        _orch.SwitchVirtualDesktopWithHud(next);
    }

    // -------------------------------------------------------------------------
    // Drag & Drop File Shelf Ingestion
    // -------------------------------------------------------------------------

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        if (_isInternalDragOut)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _dragLeaveDebounceTimer.Stop();
        if (CanAcceptDrop(e.Data))
        {
            e.Effects = DragDropEffects.Copy;
            _orch.Coordinator.SetDragShelfActive(true);
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (_isInternalDragOut)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        _dragLeaveDebounceTimer.Stop();
        e.Effects = CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _dragLeaveDebounceTimer.Stop();
        _dragLeaveDebounceTimer.Start();
    }

    private void OnDragLeaveDebounceTick(object? sender, EventArgs e)
    {
        _dragLeaveDebounceTimer.Stop();
        if (!IslandWindowController.IsLeftMouseButtonDown() || !IsCursorInsideNotchBounds())
        {
            _orch.Coordinator.SetDragShelfActive(false);
        }
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        _dragLeaveDebounceTimer.Stop();
        if (_isInternalDragOut)
        {
            e.Handled = true;
            return;
        }

        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                int added = _orch.Shelf.AddPaths(files);
                if (added > 0)
                {
                    _orch.Coordinator.Publish(new TransientHudEvent(
                        "hud:shelf",
                        HudKind.FileStashed,
                        Strings.Pick(_orch.Config.Language, "Rafa Eklendi", "Stashed to Shelf"),
                        $"{added}",
                        AccentHex: "#64D2FF",
                        IconGlyph: "\uE8F1"));
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText) && e.Data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
            {
                _orch.Shelf.StashTextAsNoteFile(text, "DropNote");
                _orch.Coordinator.Publish(new TransientHudEvent(
                    "hud:shelf",
                    HudKind.FileStashed,
                    Strings.Pick(_orch.Config.Language, "Not Rafa Kaydedildi", "Note Saved to Shelf"),
                    AccentHex: "#64D2FF",
                    IconGlyph: "\uE8F1"));
            }
        }
        finally
        {
            _orch.Coordinator.SetDragShelfActive(false);
            e.Handled = true;
        }
    }

    private static bool CanAcceptDrop(System.Windows.IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.UnicodeText);

    private bool IsCursorInsideNotchBounds()
    {
        var (cx, cy) = IslandWindowController.GetCursorScreenPos();
        double scale = Math.Max(1.0, _activeMonitor.DpiScale);
        double curX = cx / scale;
        double curY = cy / scale;

        var placement = MonitorLayoutCalculator.ComputeHostWindowBounds(
            _activeMonitor,
            _tierHostWidth,
            _tierHostHeight,
            _orch.Config.HorizontalDetent,
            _orch.Config.CustomHorizontalRatio,
            _orch.Config.DockPlacement,
            _orch.Config.CustomFreeTopDip);

        double localX = curX - placement.LeftDip;
        double localY = curY - placement.TopDip;

        double w = Math.Max(120.0, _motion.Width.Value);
        double h = Math.Max(40.0, _motion.Height.Value);
        double centerX = ResolveNotchCenterInHost(_tierHostWidth, w, _motion.EarRadius.Value);
        double topY = ResolveShellTopY(_lastSnapshot.State);

        return localX >= centerX - w * 0.5 - 18.0 &&
               localX <= centerX + w * 0.5 + 18.0 &&
               localY >= topY - 10.0 &&
               localY <= topY + h + 18.0;
    }

    // -------------------------------------------------------------------------
    // Window / Controller / Keyboard Handlers
    // -------------------------------------------------------------------------

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_lastSnapshot.State == IslandPresentationState.S5Command)
            {
                _orch.Coordinator.CloseCommandPalette();
                e.Handled = true;
            }
            else if (_lastSnapshot.State == IslandPresentationState.S4Expanded)
            {
                _orch.Coordinator.RequestCollapse(forceUnpin: true);
                e.Handled = true;
            }
        }
    }

    private void OnControllerDisplayOrDpiChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            (_orch.MonitorLayout as Win32MonitorLayoutService)?.InvalidateCache();
            _activeMonitor = ResolveTargetMonitor(_lastSnapshot);
            ApplyTierAndMonitorPlacement(_lastSnapshot, snapImmediately: true);
            _hitContourDirty = true;
            ApplyMotionFrameToVisuals(_lastSnapshot, forceHitUpdate: true);
        });
    }

    private void RegisterHotkeyWithAlert(int hotkeyId, string chord)
    {
        if (string.IsNullOrWhiteSpace(chord) || _hwnd == nint.Zero) return;
        if (!_hotkeyService.TryRegisterWithFallback(hotkeyId, _hwnd, chord, out _, out _))
        {
            Debug.WriteLine($"[DinamikAda] Startup hotkey unavailable ({hotkeyId}: {chord}): {_hotkeyService.LastError}");
        }
    }

    private void OnControllerNamedHotkeyPressed(object? sender, (int HotkeyId, int Modifiers, int Key) hotkey)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            switch (hotkey.HotkeyId)
            {
                case GlobalHotkeyService.MicMuteHotkeyId:
                    _orch.ToggleMicMuteWithHud();
                    break;
                case GlobalHotkeyService.ColorPickerHotkeyId:
                    _ = ExecuteColorPickerAsync();
                    break;
                default:
                    if (_lastSnapshot.State == IslandPresentationState.S5Command)
                    {
                        _orch.Coordinator.CloseCommandPalette();
                    }
                    else
                    {
                        _orch.Coordinator.OpenCommandPalette();
                    }
                    break;
            }
        });
    }

    private void OnControllerVolumeDeviceChanged(object? sender, (bool IsArrival, uint UnitMask) args)
    {
        if (_isClosed || _isRenderAuditMode) return;
        _orch.UsbDrives.HandleVolumeDeviceChange(args.IsArrival, args.UnitMask);
    }

    private void OnControllerClipboardUpdated(object? sender, EventArgs e)
    {
        if (_isClosed || _isRenderAuditMode || !_orch.Config.EnableClipboardVault) return;
        _orch.Clipboard.OnWmClipboardUpdate();
    }

    private void OnControllerCaptureStateChanged(object? sender, bool isActive)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            _orch.Coordinator.SetDragCatcherArmed(isActive);
        });
    }

    private void OnControllerActivationChanged(object? sender, bool activated)
    {
        if (activated)
        {
            _s5CommandActivatedOnce = true;
            return;
        }

        if (_isSmokeMode || !_s5CommandActivatedOnce || (Environment.TickCount64 - _s5CommandOpenedMs) < 180)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed || _isSmokeMode) return;
            if (_lastSnapshot.State == IslandPresentationState.S5Command)
            {
                _orch.Coordinator.CloseCommandPalette();
            }
        });
    }

    private void OnOrchestratorOpenSettingsRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(OpenSettingsDialog);
    }

    private void OpenSettingsDialog()
    {
        if (_isClosed || _isSmokeMode || _isRenderAuditMode) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _orch,
            _hotkeyService,
            onApplied: () =>
            {
                (_orch.MonitorLayout as Win32MonitorLayoutService)?.InvalidateCache();
                ApplyLocalization();
                ApplyShellVisualTheme();
                _activeMonitor = ResolveTargetMonitor(_lastSnapshot);
                ApplyTierAndMonitorPlacement(_lastSnapshot, snapImmediately: false);
                RenderCoordinatorSnapshot(_lastSnapshot, isInitial: false);
                _dockWindow?.ApplyConfigAndSync();
            },
            islandHwnd: _hwnd);

        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private const int WmMouseHWheel = 0x020E;

    private nint OnWindowMessageHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_trayIcon.ProcessMessage(msg, lParam))
        {
            handled = true;
            return nint.Zero;
        }

        nint ctrlResult = _controller.ProcessWndProc(hwnd, msg, wParam, lParam, ref handled);
        if (handled)
        {
            return ctrlResult;
        }

        if (msg == WmMouseHWheel &&
            !_isRenderAuditMode &&
            _orch.Config.NotchDesktopSwipeEnabled &&
            _lastSnapshot.State is IslandPresentationState.S1Dormant or IslandPresentationState.S2Compact or IslandPresentationState.S2Split or IslandPresentationState.S3TransientHud)
        {
            short tiltDelta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            if (tiltDelta != 0)
            {
                TrySwitchVirtualDesktopDebounced(next: tiltDelta > 0);
                handled = true;
                return nint.Zero;
            }
        }
        return ctrlResult;
    }

    private void OnTrayLeftClicked(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (_lastSnapshot.State == IslandPresentationState.S4Expanded)
            {
                _orch.Coordinator.RequestCollapse(forceUnpin: true);
            }
            else
            {
                _orch.Coordinator.RequestExpand(_lastSnapshot.PrimaryActivity?.TargetTab);
            }
        });
    }

    private void OnTrayRightClicked(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => OpenContextMenu(atCursor: true));
    }

    private void OnShiftDodgeTick(object? sender, EventArgs e)
    {
        if (_isClosed || _isRenderAuditMode || _isSmokeMode) return;

        bool wantDodge = _orch.Config.AutoDodgeEnabled &&
                         _lastSnapshot.State != IslandPresentationState.S5Command &&
                         !_lastSnapshot.IsDragCatcherArmed &&
                         !_isDraggingNotch &&
                         IslandWindowController.IsShiftKeyDown() &&
                         IsCursorInsideNotchBounds();

        if (wantDodge != _shiftAutoDodgeActive)
        {
            _shiftAutoDodgeActive = wantDodge;
            _controller.SetClickThrough((_lastSnapshot.Layout.IsClickThrough || wantDodge) && !_lastSnapshot.IsDragCatcherArmed);
        }
    }

    private void OpenContextMenuAtIsland() => OpenContextMenu(atCursor: false);

    private void OpenContextMenu(bool atCursor)
    {
        if (_isClosed || _isSmokeMode || _isRenderAuditMode) return;
        string lang = _orch.Config.Language;

        _islandContextMenu = new ContextMenu();
        var openHub = new MenuItem { Header = Strings.OpenHub(lang) };
        openHub.Click += (_, _) => _orch.Coordinator.RequestExpand();

        var cmdItem = new MenuItem { Header = Strings.CommandPalette(lang) };
        cmdItem.Click += (_, _) => _orch.Coordinator.OpenCommandPalette();

        var pinItem = new MenuItem { Header = Strings.PinOpen(lang), IsCheckable = true, IsChecked = _lastSnapshot.IsPinnedOpen };
        pinItem.Click += (_, _) => _orch.Coordinator.TogglePinned();

        var taskbarToggleItem = new MenuItem
        {
            Header = _orch.Config.HideWindowsTaskbar
                ? Strings.RestoreWindowsTaskbar(lang)
                : Strings.HideWindowsTaskbarMenu(lang)
        };
        taskbarToggleItem.Click += (_, _) =>
        {
            _orch.Config.HideWindowsTaskbar = !_orch.Config.HideWindowsTaskbar;
            _orch.SaveConfig();
            _dockWindow?.ApplyConfigAndSync();
        };

        var settingsItem = new MenuItem { Header = Strings.Settings(lang) };
        settingsItem.Click += (_, _) => OpenSettingsDialog();

        var exitItem = new MenuItem { Header = Strings.ExitApp(lang) };
        exitItem.Click += (_, _) => System.Windows.Application.Current?.Shutdown();

        _islandContextMenu.Items.Add(openHub);
        _islandContextMenu.Items.Add(cmdItem);
        _islandContextMenu.Items.Add(pinItem);
        _islandContextMenu.Items.Add(new Separator());
        _islandContextMenu.Items.Add(taskbarToggleItem);
        _islandContextMenu.Items.Add(settingsItem);
        _islandContextMenu.Items.Add(exitItem);

        _islandContextMenu.Closed += (_, _) => ReEvaluateCollapseAfterPopupOrCapture();

        _controller.TryPrepareForegroundForPopup();
        _islandContextMenu.PlacementTarget = atCursor ? null : MainNotchAssembly;
        _islandContextMenu.Placement = atCursor ? PlacementMode.MousePoint : PlacementMode.Bottom;
        _islandContextMenu.IsOpen = true;
    }

    // -------------------------------------------------------------------------
    // UI Button & Slider Event Handlers
    // -------------------------------------------------------------------------

    private void OnTabButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && Enum.TryParse<ExpandedTabId>(btn.Tag?.ToString(), out var tab))
        {
            _orch.Coordinator.RequestNavigateTab(tab);
        }
    }

    private void OnQuietIslandClick(object sender, RoutedEventArgs e)
    {
        bool next = !_orch.Config.QuietIslandEnabled;
        _orch.Coordinator.SetQuietIsland(next);
        _orch.SaveConfig();
        ApplyLocalization();
    }

    private void OnOpenCommandClick(object sender, RoutedEventArgs e) => _orch.Coordinator.OpenCommandPalette();

    private void OnPinToggleClick(object sender, RoutedEventArgs e)
    {
        _orch.Coordinator.TogglePinned();
        PinToggleBtn.Foreground = _orch.Coordinator.Current.IsPinnedOpen ? ActiveAccentDotBrush : MutedTabFgBrush;
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsDialog();

    private void OnCollapseHeaderClick(object sender, RoutedEventArgs e) => _orch.Coordinator.RequestCollapse(forceUnpin: true);

    private void OnHudActionClick(object sender, RoutedEventArgs e)
    {
        var hud = _lastSnapshot.ActiveHud;
        if (hud is null) return;
        if (!string.IsNullOrWhiteSpace(hud.PrimaryActionId))
        {
            HandleHudButtonAction(hud.PrimaryActionId, hud.ActionPayload);
            return;
        }
        if (hud.Kind == HudKind.TransferComplete)
        {
            _orch.StashLastCompletedTransfer();
            return;
        }
        _orch.Coordinator.PromoteTransientHudInteractive();
    }

    private void OnHudSecondaryActionClick(object sender, RoutedEventArgs e)
    {
        var hud = _lastSnapshot.ActiveHud;
        if (hud is null || string.IsNullOrWhiteSpace(hud.SecondaryActionId)) return;
        HandleHudButtonAction(hud.SecondaryActionId, hud.ActionPayload);
    }

    private void HandleHudButtonAction(string? actionId, string? payload)
    {
        if (string.IsNullOrWhiteSpace(actionId) || _isRenderAuditMode) return;
        switch (actionId)
        {
            case "color.copy-rgb":
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    string rgb = payload.Split('|', 2)[0];
                    _orch.Clipboard.CopyTextToClipboard(rgb);
                    _orch.Coordinator.Publish(new TransientHudEvent(
                        CoalesceKey: "hud:color-copied",
                        Kind: HudKind.Clipboard,
                        Title: rgb,
                        Subtitle: Strings.ColorCopiedSub(_orch.Config.Language),
                        AccentHex: "#0A84FF",
                        DurationMs: 1400,
                        InteractiveOnHover: false,
                        IconGlyph: "\uEF3C"));
                }
                break;

            case "color.copy-hsl":
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var parts = payload.Split('|', 2);
                    string hsl = parts.Length > 1 ? parts[1] : parts[0];
                    _orch.Clipboard.CopyTextToClipboard(hsl);
                    _orch.Coordinator.Publish(new TransientHudEvent(
                        CoalesceKey: "hud:color-copied",
                        Kind: HudKind.Clipboard,
                        Title: hsl,
                        Subtitle: Strings.ColorCopiedSub(_orch.Config.Language),
                        AccentHex: "#0A84FF",
                        DurationMs: 1400,
                        InteractiveOnHover: false,
                        IconGlyph: "\uEF3C"));
                }
                break;

            case "usb.open":
                _orch.OpenUsbDriveFolder(payload);
                _orch.Coordinator.DismissTransientHud();
                break;

            case "usb.eject":
                _ = _orch.SafeEjectUsbDriveAsync(payload);
                break;

            case "power.snooze10":
                _orch.Timers.SnoozeScheduledPowerAction(TimeSpan.FromMinutes(10));
                _orch.Coordinator.DismissTransientHud();
                break;

            case "power.cancel":
                _orch.Timers.CancelScheduledPowerAction();
                _orch.Coordinator.DismissTransientHud();
                break;
        }
    }

    private void OnDragOpenShelfClick(object sender, RoutedEventArgs e)
    {
        _orch.Coordinator.SetDragShelfActive(false);
        _orch.Coordinator.RequestExpand(ExpandedTabId.Shelf);
    }

    private void OnDragCancelClick(object sender, RoutedEventArgs e) => _orch.Coordinator.SetDragShelfActive(false);

    private async void OnPrevTrackClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) await _orch.Media.PreviousAsync();
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) await _orch.Media.TogglePlayPauseAsync();
    }

    private async void OnNextTrackClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) await _orch.Media.NextAsync();
    }

    private async void OnPrevMediaSessionClick(object sender, RoutedEventArgs e) => await SwitchMediaSessionAsync(-1);
    private async void OnNextMediaSessionClick(object sender, RoutedEventArgs e) => await SwitchMediaSessionAsync(+1);

    private async Task SwitchMediaSessionAsync(int delta)
    {
        var cur = _orch.Media.Current;
        if (cur.AllSessions.Length <= 1) return;
        int idx = 0;
        for (int i = 0; i < cur.AllSessions.Length; i++)
        {
            if (string.Equals(cur.AllSessions[i].SessionId, cur.SessionId, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }
        int nextIdx = (idx + delta + cur.AllSessions.Length) % cur.AllSessions.Length;
        await _orch.Media.SelectSessionAsync(cur.AllSessions[nextIdx].SessionId);
    }

    private void OnMediaSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        _isSeekingMedia = true;
    }

    private async void OnMediaSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isSeekingMedia = false;
        await CommitMediaSeekAsync();
    }

    private async void OnMediaSeekPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeekingMedia)
        {
            await CommitMediaSeekAsync();
        }
    }

    private async void OnMediaSeekPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            await CommitMediaSeekAsync();
        }
    }

    private async Task CommitMediaSeekAsync()
    {
        if (_isRenderAuditMode) return;
        var cur = _orch.Media.Current;
        if (!cur.HasSession || !cur.CanSeek || cur.Duration <= TimeSpan.Zero) return;
        double ratio = Math.Clamp(MediaSeekSlider.Value / 100.0, 0.0, 1.0);
        var pos = TimeSpan.FromSeconds(cur.Duration.TotalSeconds * ratio);
        NowElapsedText.Text = FormatDuration(pos);
        await _orch.Media.SeekAsync(pos);
    }

    private void OnAdd5MinTimerClick(object sender, RoutedEventArgs e)
    {
        _orch.Timers.StartCountdown(Strings.Pick(_orch.Config.Language, "5 Dakika", "5 Minutes"), TimeSpan.FromMinutes(5), isPomodoro: false);
    }

    private void OnAddPomodoroTimerClick(object sender, RoutedEventArgs e)
    {
        _orch.Timers.StartCountdown("Pomodoro", TimeSpan.FromMinutes(25), isPomodoro: true);
    }

    private void OnAddStopwatchClick(object sender, RoutedEventArgs e)
    {
        _orch.Timers.StartStopwatch();
    }

    private async void OnClearNotificationsClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) await _orch.Notifications.ClearAllAsync();
    }

    private async void OnTileWifiClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        if (!_orch.Connectivity.Current.IsWifiAvailable)
        {
            _orch.Connectivity.OpenWindowsSettings("ms-settings:network-ethernet");
            return;
        }
        await _orch.Connectivity.ToggleWifiAsync();
    }

    private async void OnTileBluetoothClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        if (!_orch.Connectivity.Current.IsBluetoothAvailable)
        {
            _orch.Audio.SetMasterMute(!_orch.Audio.Current.IsMasterMuted);
            RenderControlConnectivity(_orch.Connectivity.Current);
            return;
        }
        await _orch.Connectivity.ToggleBluetoothAsync();
    }

    private async void OnTileThemeClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) await _orch.Connectivity.ToggleDarkThemeAsync();
    }

    private void OnTileCaffeineClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.ToggleCaffeine();
    }

    private void OnTileSnipClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.LaunchScreenSnip();
    }

    private void OnPowerEffClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.SetPowerMode(0);
    }

    private void OnPowerBalClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.SetPowerMode(1);
    }

    private void OnPowerPerfClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.SetPowerMode(2);
    }

    private void OnLockPcClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.LockWorkstation();
    }

    private void OnSleepPcClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.SleepWorkstation();
    }

    private void OnDisplayOffPcClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.TurnOffDisplay();
    }

    private void OnPowerSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.OpenWindowsSettings("ms-settings:powersleep");
    }

    private void OnNightLightSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.OpenWindowsSettings("ms-settings:nightlight");
    }

    private void OnDisplaySettingsClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Connectivity.OpenWindowsSettings("ms-settings:display");
    }

    private void OnScheduledPowerChanged(object? sender, ScheduledPowerSnapshot snap)
    {
        if (_isClosed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosed) return;
            if (IsExpandedTabVisible(ExpandedTabId.Control))
            {
                RenderControlScheduledPower(snap);
            }
            else
            {
                MarkTabDirty(ExpandedTabId.Control);
            }
        });
    }

    private void RenderControlScheduledPower(ScheduledPowerSnapshot snap)
    {
        if (snap.IsActive)
        {
            SchedSetupGrid.Visibility = Visibility.Collapsed;
            SchedActiveGrid.Visibility = Visibility.Visible;
            string lang = _orch.Config.Language;
            string actionLabel = snap.Kind switch
            {
                ScheduledPowerActionKind.Sleep => Strings.ScheduledSleep(lang),
                ScheduledPowerActionKind.Shutdown => Strings.ScheduledShutdown(lang),
                ScheduledPowerActionKind.Lock => Strings.ScheduledLock(lang),
                ScheduledPowerActionKind.PauseMedia => Strings.ScheduledPauseMedia(lang),
                _ => Strings.ScheduledPowerHeader(lang)
            };
            int remSec = Math.Max(0, (int)Math.Ceiling(snap.Remaining.TotalSeconds));
            string remText = remSec >= 3600
                ? $"{remSec / 3600}:{(remSec % 3600) / 60:00}:{remSec % 60:00}"
                : $"{remSec / 60}:{remSec % 60:00}";
            TxtSchedCountdown.Text = $"{actionLabel} • {remText}";
        }
        else
        {
            SchedSetupGrid.Visibility = Visibility.Visible;
            SchedActiveGrid.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSchedPowerStartClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        var kind = ComboSchedAction.SelectedValue is ScheduledPowerActionKind k
            ? k
            : ScheduledPowerActionKind.Sleep;
        int mins = ComboSchedMinutes.SelectedValue is int m ? m : 30;
        _orch.Timers.StartScheduledPowerAction(kind, TimeSpan.FromMinutes(Math.Max(1, mins)));
        RenderControlScheduledPower(_orch.Timers.ScheduledPowerState);
    }

    private void OnSchedPowerSnoozeClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        _orch.Timers.SnoozeScheduledPowerAction(TimeSpan.FromMinutes(10));
        RenderControlScheduledPower(_orch.Timers.ScheduledPowerState);
    }

    private void OnSchedPowerCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        _orch.Timers.CancelScheduledPowerAction();
        RenderControlScheduledPower(_orch.Timers.ScheduledPowerState);
    }

    private void OnShelfShareClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        string lang = _orch.Config.Language;
        if (_orch.Shelf.TryShowShareUi(_hwnd))
        {
            if (!FileShelfService.HasPackageIdentity())
            {
                _orch.Coordinator.Publish(new TransientHudEvent(
                    "hud:share",
                    HudKind.FileStashed,
                    Strings.Pick(lang, "Gezginde Seçildi", "Selected in Explorer"),
                    Strings.Pick(lang, "Paylaşım menüsü için Dosya Gezgini açıldı", "Opened File Explorer for Share menu"),
                    AccentHex: "#64D2FF",
                    DurationMs: 2200,
                    IconGlyph: "\uE72D"));
            }
        }
        else
        {
            _orch.Coordinator.Publish(new TransientHudEvent(
                "hud:share",
                HudKind.FileStashed,
                Strings.Pick(lang, "Paylaşılacak Dosya Yok", "Nothing to Share"),
                Strings.Pick(lang, "Önce Dosya Rafına dosya sürükleyin", "Drop a file into File Shelf first"),
                AccentHex: "#FF9F0A",
                DurationMs: 2200,
                IconGlyph: "\uE72D"));
        }
    }

    private async void OnShelfZipClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        string lang = _orch.Config.Language;
        try
        {
            string? zip = await _orch.Shelf.CreateZipArchiveAsync();
            if (!string.IsNullOrWhiteSpace(zip))
            {
                _orch.Shelf.AddPaths([zip]);
                _orch.Coordinator.Publish(new TransientHudEvent(
                    "hud:zip",
                    HudKind.FileStashed,
                    Strings.Pick(lang, "ZIP Rafa Eklendi", "ZIP Added to Shelf"),
                    Path.GetFileName(zip),
                    AccentHex: "#30D158",
                    IconGlyph: "\uE8F1"));
            }
            else
            {
                _orch.Coordinator.Publish(new TransientHudEvent(
                    "hud:zip",
                    HudKind.FileStashed,
                    Strings.Pick(lang, "ZIP İçin Dosya Yok", "Shelf Is Empty"),
                    Strings.Pick(lang, "Önce Dosya Rafına dosya ekleyin", "Add files to the shelf first"),
                    AccentHex: "#FF9F0A",
                    DurationMs: 2000,
                    IconGlyph: "\uE8F1"));
            }
        }
        catch (Exception ex)
        {
            _orch.Notifications.AddInternalAlert("DinamikAda", Strings.Pick(lang, "ZIP oluşturulamadı", "Could not create ZIP"), ex.Message);
        }
    }

    private void OnShelfClearClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        _qrOverlayVisible = false;
        _renderedQrUrl = null;
        _orch.LanDrop.StopShare();
        _orch.Shelf.Clear();
        RenderShelfQrOverlay();
    }

    private Task OnOrchestratorRegionColorPickRequested()
    {
        if (_isClosed || Dispatcher.HasShutdownStarted) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await ExecuteColorPickerAsync();
            }
            finally
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    private void OnOrchestratorQrShareRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => StartQrShareFlow());
    }

    private Task OnOrchestratorRegionOcrRequested()
    {
        if (_isClosed || Dispatcher.HasShutdownStarted) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await ExecuteRegionOcrCaptureAsync();
            }
            finally
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    private void OnShelfQrClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        if (_qrOverlayVisible)
        {
            _qrOverlayVisible = false;
            RenderShelfQrOverlay();
            return;
        }
        StartQrShareFlow();
    }

    private void StartQrShareFlow()
    {
        if (_isClosed) return;
        try
        {
            _renderedQrUrl = null;
            _orch.ToggleOrStartLanQrShare(forceStart: true);
            _qrOverlayVisible = true;
            _orch.Coordinator.SetDragShelfActive(false);
            _orch.Coordinator.RequestExpand(ExpandedTabId.Shelf);
            RenderShelfQrOverlay();
        }
        catch (Exception ex)
        {
            _orch.Notifications.AddInternalAlert("DinamikAda", Strings.Pick(_orch.Config.Language, "Paylaşım başlatılamadı", "Could not start sharing"), ex.Message);
        }
    }

    private void OnShelfQrUrlClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var share = _orch.LanDrop.CurrentShare;
        if (share is null || _isRenderAuditMode) return;
        string url = string.IsNullOrWhiteSpace(share.ShareUrl) ? share.LoopbackUrl : share.ShareUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void OnShelfQrCopyClick(object sender, RoutedEventArgs e)
    {
        var share = _orch.LanDrop.CurrentShare;
        if (share is null || _isRenderAuditMode) return;
        string url = string.IsNullOrWhiteSpace(share.ShareUrl) ? share.LoopbackUrl : share.ShareUrl;
        bool ok = _orch.Clipboard.CopyTextToClipboard(url);
        if (!ok)
        {
            try
            {
                System.Windows.Clipboard.SetText(url);
                ok = true;
            }
            catch { }
        }
        if (ok)
        {
            BtnShelfQrCopyUrl.Content = Strings.Pick(_orch.Config.Language, "Kopyalandı", "Copied");
        }
    }

    private void OnShelfQrStopClick(object sender, RoutedEventArgs e)
    {
        _qrOverlayVisible = false;
        _renderedQrUrl = null;
        _orch.LanDrop.StopShare();
        RenderShelfQrOverlay();
    }

    private async Task ExecuteColorPickerAsync()
    {
        if (_isRenderAuditMode || _isClosed || _isCapturingColor) return;
        _isCapturingColor = true;
        double prevOpacity = Opacity;
        try
        {
            Opacity = 0.0;
            await Task.Delay(55);
            var picked = RegionCaptureWindow.PickColorInteractive(_orch.Config.Language);
            if (!_isClosed) Opacity = prevOpacity;
            if (picked is not null)
            {
                _orch.HandlePickedColor(picked.Hex, picked.RgbText, picked.HslText);
            }
        }
        catch (Exception ex)
        {
            _orch.Notifications.AddInternalAlert("DinamikAda", Strings.ColorPickerTitle(_orch.Config.Language), ex.Message);
        }
        finally
        {
            if (!_isClosed) Opacity = prevOpacity;
            _isCapturingColor = false;
        }
    }

    private async void OnRegionOcrClick(object sender, RoutedEventArgs e)
    {
        await ExecuteRegionOcrCaptureAsync();
    }

    private async Task ExecuteRegionOcrCaptureAsync()
    {
        if (_isRenderAuditMode || _isClosed || _isCapturingOcr) return;
        _isCapturingOcr = true;
        CapturedRegionPixels? captured = null;
        double prevOpacity = Opacity;
        try
        {
            Opacity = 0.0;
            await Task.Delay(55);
            captured = RegionCaptureWindow.CaptureInteractiveRegion(_orch.Config.Language);
            if (!_isClosed) Opacity = prevOpacity;
            if (captured is not null)
            {
                var ocrRes = await _orch.ScreenOcr.ProcessCapturedBgraAsync(captured.BgraPixels, captured.Width, captured.Height);
                string lang = _orch.Config.Language;
                if (ocrRes.HasText)
                {
                    _orch.Coordinator.Publish(new TransientHudEvent(
                        CoalesceKey: "hud:clipboard",
                        Kind: HudKind.Clipboard,
                        Title: Strings.Pick(lang, "OCR Metni Kopyalandı", "OCR Text Copied"),
                        Subtitle: ocrRes.ExtractedText.Replace("\r\n", " ").Replace('\n', ' '),
                        AccentHex: "#30D158",
                        DurationMs: 2400,
                        InteractiveOnHover: true,
                        IconGlyph: "\uE8C8"));
                }
                else
                {
                    _orch.Coordinator.Publish(new TransientHudEvent(
                        CoalesceKey: "hud:ocr",
                        Kind: HudKind.FileStashed,
                        Title: Strings.Pick(lang, "Metin Algılanamadı", "No Text Detected"),
                        Subtitle: Strings.Pick(lang, "Görsel Dosya Rafına eklendi", "Screenshot saved to File Shelf"),
                        AccentHex: "#FF9F0A",
                        DurationMs: 2400,
                        InteractiveOnHover: true,
                        IconGlyph: "\uE722"));
                }
            }
        }
        catch (Exception ex)
        {
            _orch.Notifications.AddInternalAlert("DinamikAda", Strings.Pick(_orch.Config.Language, "Ekran metni alınamadı", "Could not capture screen text"), ex.Message);
        }
        finally
        {
            if (!_isClosed) Opacity = prevOpacity;
            _isCapturingOcr = false;
        }
    }

    private void OnMeetingMicToggleClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.JoinUpcomingMeeting(toggleMicOnly: true);
    }

    private void OnMeetingJoinClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.JoinUpcomingMeeting(toggleMicOnly: false);
    }

    private void OnVoiceNoteToggleClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        _orch.VoiceNotes.ToggleRecording();
        RenderVoiceNoteBar();
    }

    private void OnVoiceNoteOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        _orch.VoiceNotes.TryOpenNotesFolder();
    }

    private void OnMediaQualityBadgeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var audioSnap = _auditAudioOverride ?? _orch.Audio.Current;
        PopulateAudioQualityOverlay(audioSnap);
        NotifDetailOverlay.Visibility = Visibility.Collapsed;
        AudioQualityOverlay.Visibility = Visibility.Visible;
    }

    private void PopulateAudioQualityOverlay(AudioMixerSnapshot snap)
    {
        string lang = _orch.Config.Language;
        AudioQualityDeviceText.Text = string.IsNullOrWhiteSpace(snap.DefaultOutputName)
            ? Strings.Pick(lang, "Varsayılan Çıkış Aygıtı", "Default Output Device")
            : snap.DefaultOutputName;
        AudioQualityStatusText.Text = string.Empty;
        AudioQualityListPanel.Children.Clear();

        var formats = snap.SupportedOutputFormats;
        if (formats.IsDefaultOrEmpty)
        {
            if (!string.IsNullOrWhiteSpace(snap.OutputFormatLabel))
            {
                AudioQualityListPanel.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ActiveTileBgBrush,
                    BorderBrush = TileAccentBlueBrush,
                    BorderThickness = new Thickness(0.8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Child = new TextBlock
                    {
                        Text = $"{snap.OutputFormatLabel}  •  " + Strings.Pick(lang, "Etkin", "Active"),
                        FontFamily = UiTextFontFamily,
                        FontSize = 10.2,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ActiveTabFgBrush
                    }
                });
            }
            BtnAudioQualityUseMax.IsEnabled = false;
            return;
        }

        BtnAudioQualityUseMax.IsEnabled = true;
        foreach (var opt in formats)
        {
            var captured = opt;
            string tier = opt.SampleRateHz >= 176400
                ? "Hi-Res"
                : (opt.BitsPerSample >= 24 ? "Studio" : "Standard");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = $"{opt.Label}  •  {opt.Channels}ch",
                FontFamily = UiTextFontFamily,
                FontSize = 10.2,
                FontWeight = opt.IsActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = opt.IsActive ? TileAccentBlueBrush : ActiveTabFgBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            var tagText = new TextBlock
            {
                Text = opt.IsActive ? $"✓ {tier}" : tier,
                FontFamily = UiTextFontFamily,
                FontSize = 9.2,
                FontWeight = FontWeights.SemiBold,
                Foreground = opt.IsActive ? ActiveAccentDotBrush : MutedTabFgBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tagText, 1);
            row.Children.Add(tagText);

            var btn = new Button
            {
                Style = (Style)FindResource("PillTabButtonStyle"),
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 0, 4),
                Background = opt.IsActive ? ActiveTileBgBrush : CardSurfaceBrush,
                BorderBrush = opt.IsActive ? TileAccentBlueBrush : CardBorderBrush,
                BorderThickness = new Thickness(opt.IsActive ? 1.0 : 0.7)
            };
            btn.Click += async (_, _) => await ApplyAudioFormatOptionAsync(captured);
            AudioQualityListPanel.Children.Add(btn);
        }
    }

    private async Task ApplyAudioFormatOptionAsync(AudioFormatOption opt)
    {
        if (_isRenderAuditMode) return;
        string lang = _orch.Config.Language;
        AudioQualityStatusText.Foreground = MutedTabFgBrush;
        AudioQualityStatusText.Text = Strings.Pick(lang, $"Uygulanıyor: {opt.Label}...", $"Applying: {opt.Label}...");

        bool ok = await _orch.Audio.SetDefaultOutputFormatAsync(opt.SampleRateHz, opt.BitsPerSample, opt.IsFloat);
        if (_isClosed) return;

        if (ok)
        {
            AudioQualityStatusText.Foreground = ActiveAccentDotBrush;
            AudioQualityStatusText.Text = Strings.Pick(lang, $"Etkin: {opt.Label}", $"Active: {opt.Label}");
            PopulateAudioQualityOverlay(_orch.Audio.Current);
        }
        else
        {
            AudioQualityStatusText.Foreground = MicLiveBrush;
            AudioQualityStatusText.Text = Strings.Pick(
                lang,
                "Aygıt bu formatı kabul etmedi (Ses Ayarları'ndan kontrol edin)",
                "Device rejected this format (check Sound Settings)");
        }
    }

    private async void OnAudioQualityUseMaxClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        var best = CoreAudioHost.SelectHighestQualityFormat(_orch.Audio.Current.SupportedOutputFormats);
        if (best is not null)
        {
            await ApplyAudioFormatOptionAsync(best);
        }
    }

    private void OnAudioQualityOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        _orch.Connectivity.OpenWindowsSettings("ms-settings:sound");
    }

    private void OnAudioQualityCloseClick(object sender, RoutedEventArgs e)
    {
        AudioQualityOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnSysCleanDiskClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        string lang = _orch.Config.Language;
        BtnSysCleanDisk.IsEnabled = false;
        SysMaintStatusText.Foreground = MutedTabFgBrush;
        SysMaintStatusText.Text = Strings.Pick(lang, "Geçici dosyalar taranıyor...", "Cleaning temp files...");
        try
        {
            var res = await _orch.Maintenance.ExecuteTempCleanupAsync();
            if (_isClosed) return;
            string sizeStr = SystemMaintenanceService.FormatBytes(res.FreedBytes, lang);
            SysMaintStatusText.Foreground = res.FreedBytes > 0 ? ActiveAccentDotBrush : ActiveTabFgBrush;
            SysMaintStatusText.Text = Strings.Pick(
                lang,
                $"{sizeStr} temizlendi • {res.DeletedFileCount} dosya silindi",
                $"Freed {sizeStr} • {res.DeletedFileCount} files removed");
            await _orch.Telemetry.SampleOnceAsync();
        }
        finally
        {
            if (!_isClosed) BtnSysCleanDisk.IsEnabled = true;
        }
    }

    private async void OnSysTrimRamClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        string lang = _orch.Config.Language;
        BtnSysTrimRam.IsEnabled = false;
        SysMaintStatusText.Foreground = MutedTabFgBrush;
        SysMaintStatusText.Text = Strings.Pick(lang, "Çalışma kümeleri sıkıştırılıyor...", "Trimming working sets...");
        try
        {
            var res = await _orch.Maintenance.TrimWorkingSetsAsync();
            if (_isClosed) return;
            long delta = Math.Max(0L, res.AvailableMemoryDeltaBytes);
            string reclaimed = SystemMaintenanceService.FormatBytes(delta, lang);
            SysMaintStatusText.Foreground = ActiveAccentDotBrush;
            SysMaintStatusText.Text = Strings.Pick(
                lang,
                $"{res.TrimmedProcessCount} süreç kırpıldı • +{reclaimed} kullanılabilir RAM",
                $"Trimmed {res.TrimmedProcessCount} processes • +{reclaimed} available RAM");
            await _orch.Telemetry.SampleOnceAsync();
        }
        finally
        {
            if (!_isClosed) BtnSysTrimRam.IsEnabled = true;
        }
    }

    private async void OnSysScanIdleAppsClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        if (SysIdleAppsConfirmPanel.Visibility == Visibility.Visible)
        {
            SysIdleAppsConfirmPanel.Visibility = Visibility.Collapsed;
            SystemProcessScrollViewer.Visibility = Visibility.Visible;
            return;
        }

        string lang = _orch.Config.Language;
        BtnSysCloseIdleApps.IsEnabled = false;
        SysMaintStatusText.Foreground = MutedTabFgBrush;
        SysMaintStatusText.Text = Strings.Pick(lang, "Boşta arka plan uygulamaları taranıyor...", "Scanning idle background apps...");
        try
        {
            var audioPids = _orch.Audio.Current.AppSessions
                .Where(s => !s.IsMuted || s.PeakLevel01 > 0.005f)
                .Select(s => s.PrimaryProcessId)
                .ToHashSet();

            var candidates = await _orch.Maintenance.ScanIdleBackgroundAppsAsync(audioPids);
            if (_isClosed) return;

            if (candidates.IsDefaultOrEmpty)
            {
                _pendingIdleApps.Clear();
                SysIdleAppsConfirmPanel.Visibility = Visibility.Collapsed;
                SystemProcessScrollViewer.Visibility = Visibility.Visible;
                SysMaintStatusText.Foreground = MutedTabFgBrush;
                SysMaintStatusText.Text = Strings.SysNoIdleAppsFound(lang);
                return;
            }

            _pendingIdleApps = [.. candidates];
            SysIdleAppsCheckStack.Children.Clear();
            foreach (var cand in _pendingIdleApps)
            {
                string titlePart = !string.IsNullOrWhiteSpace(cand.WindowTitle)
                    ? $" — {cand.WindowTitle}"
                    : string.Empty;
                string countPart = cand.InstanceCount > 1 ? $" ×{cand.InstanceCount}" : string.Empty;
                string hungPart = cand.IsHung ? Strings.Pick(lang, " • Yanıt Vermiyor", " • Hung") : string.Empty;
                var cb = new System.Windows.Controls.CheckBox
                {
                    IsChecked = true,
                    Tag = cand,
                    Foreground = ActiveTabFgBrush,
                    FontFamily = UiTextFontFamily,
                    FontSize = 9.8,
                    Margin = new Thickness(0, 2, 0, 2),
                    Content = $"{cand.ProcessName}{countPart} ({cand.MemoryMb:F0} MB{hungPart}){titlePart}"
                };
                SysIdleAppsCheckStack.Children.Add(cb);
            }

            SystemProcessScrollViewer.Visibility = Visibility.Collapsed;
            SysIdleAppsConfirmPanel.Visibility = Visibility.Visible;
            SysMaintStatusText.Foreground = MicLiveBrush;
            SysMaintStatusText.Text = Strings.Pick(
                lang,
                $"{_pendingIdleApps.Count} aday bulundu — onaylayın",
                $"{_pendingIdleApps.Count} candidates found — confirm below");
        }
        finally
        {
            if (!_isClosed) BtnSysCloseIdleApps.IsEnabled = true;
        }
    }

    private void OnSysCancelIdleAppsClick(object sender, RoutedEventArgs e)
    {
        _pendingIdleApps.Clear();
        SysIdleAppsConfirmPanel.Visibility = Visibility.Collapsed;
        SystemProcessScrollViewer.Visibility = Visibility.Visible;
        SysMaintStatusText.Text = string.Empty;
    }

    private async void OnSysConfirmCloseIdleAppsClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        string lang = _orch.Config.Language;
        var selected = SysIdleAppsCheckStack.Children
            .OfType<System.Windows.Controls.CheckBox>()
            .Where(cb => cb.IsChecked == true && cb.Tag is IdleBackgroundAppCandidate)
            .Select(cb => (IdleBackgroundAppCandidate)cb.Tag)
            .ToList();

        SysIdleAppsConfirmPanel.Visibility = Visibility.Collapsed;
        SystemProcessScrollViewer.Visibility = Visibility.Visible;
        _pendingIdleApps.Clear();

        if (selected.Count == 0)
        {
            SysMaintStatusText.Text = string.Empty;
            return;
        }

        BtnSysConfirmCloseIdle.IsEnabled = false;
        try
        {
            var res = await _orch.Maintenance.CloseBackgroundAppsAsync(selected);
            if (_isClosed) return;
            SysMaintStatusText.Foreground = res.ClosedProcessCount > 0 ? ActiveAccentDotBrush : MicLiveBrush;
            SysMaintStatusText.Text = Strings.Pick(
                lang,
                $"{res.ClosedProcessCount} uygulama kapatıldı (~{res.EstimatedMemoryMb:F0} MB)",
                $"Closed {res.ClosedProcessCount} apps (~{res.EstimatedMemoryMb:F0} MB)");
            await _orch.Telemetry.SampleOnceAsync();
        }
        finally
        {
            if (!_isClosed) BtnSysConfirmCloseIdle.IsEnabled = true;
        }
    }

    private void OnClipboardClearUnpinnedClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Clipboard.ClearUnpinned();
    }

    private void OnOpenWidgetsFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_orch.Widgets.PluginsDirectory);
            Process.Start(new ProcessStartInfo(_orch.Widgets.PluginsDirectory) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch error.
        }
    }

    private void OnReloadWidgetsClick(object sender, RoutedEventArgs e)
    {
        if (!_isRenderAuditMode) _orch.Widgets.ReloadAllWidgets();
    }

    private async void OnToolColorPickerClick(object sender, RoutedEventArgs e)
    {
        if (_isRenderAuditMode) return;
        await ExecuteColorPickerAsync();
    }

    // -------------------------------------------------------------------------
    // Localization, Theme, Smoke & Render Audit Helpers
    // -------------------------------------------------------------------------

    private void PopulateScheduledPowerChoices(string lang)
    {
        var selectedKind = ComboSchedAction.SelectedValue is ScheduledPowerActionKind k
            ? k
            : ScheduledPowerActionKind.Sleep;
        int selectedMins = ComboSchedMinutes.SelectedValue is int m ? m : 30;

        ComboSchedAction.DisplayMemberPath = nameof(SchedChoice<ScheduledPowerActionKind>.Label);
        ComboSchedAction.SelectedValuePath = nameof(SchedChoice<ScheduledPowerActionKind>.Value);
        ComboSchedAction.ItemsSource = new[]
        {
            new SchedChoice<ScheduledPowerActionKind>(ScheduledPowerActionKind.Sleep, Strings.ScheduledSleep(lang)),
            new SchedChoice<ScheduledPowerActionKind>(ScheduledPowerActionKind.Shutdown, Strings.ScheduledShutdown(lang)),
            new SchedChoice<ScheduledPowerActionKind>(ScheduledPowerActionKind.Lock, Strings.ScheduledLock(lang)),
            new SchedChoice<ScheduledPowerActionKind>(ScheduledPowerActionKind.PauseMedia, Strings.ScheduledPauseMedia(lang))
        };
        ComboSchedAction.SelectedValue = selectedKind;

        ComboSchedMinutes.DisplayMemberPath = nameof(SchedChoice<int>.Label);
        ComboSchedMinutes.SelectedValuePath = nameof(SchedChoice<int>.Value);
        ComboSchedMinutes.ItemsSource = new[]
        {
            new SchedChoice<int>(5, Strings.Pick(lang, "5dk", "5m")),
            new SchedChoice<int>(10, Strings.Pick(lang, "10dk", "10m")),
            new SchedChoice<int>(15, Strings.Pick(lang, "15dk", "15m")),
            new SchedChoice<int>(30, Strings.Pick(lang, "30dk", "30m")),
            new SchedChoice<int>(45, Strings.Pick(lang, "45dk", "45m")),
            new SchedChoice<int>(60, Strings.Pick(lang, "60dk", "60m")),
            new SchedChoice<int>(90, Strings.Pick(lang, "90dk", "90m")),
            new SchedChoice<int>(120, Strings.Pick(lang, "120dk", "120m"))
        };
        ComboSchedMinutes.SelectedValue = selectedMins;
    }

    private void ApplyShellVisualTheme()
    {
        bool flush = _orch.Config.DockPlacement == DockPlacementMode.NotchFlush;
        NotchShellPath.Fill = flush ? DefaultShellBrush : FloatingShellBrush;
        NotchRimPath.Stroke = (Brush)FindResource(flush ? "NotchRimGradientBrush" : "FloatingCapsuleRimBrush");
        SatelliteShellPath.Fill = flush ? DefaultShellBrush : FloatingShellBrush;
        SatelliteRimPath.Stroke = (Brush)FindResource(flush ? "NotchRimGradientBrush" : "FloatingCapsuleRimBrush");
    }

    private void ApplyLocalization()
    {
        _hoverDwellTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_orch.Config.HoverDwellMs, 40, 2000));
        _collapseDebounceTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_orch.Config.CollapseHysteresisMs, 80, 5000));

        string lang = _orch.Config.Language;
        TabBtnNow.Content = Strings.TabNow(lang);
        TabBtnAudio.Content = Strings.TabAudio(lang);
        TabBtnControl.Content = Strings.TabControl(lang);
        TabBtnShelf.Content = Strings.TabShelf(lang);
        TabBtnSystem.Content = Strings.TabSystem(lang);
        TabBtnWidgets.Content = Strings.TabWidgets(lang);

        var pinned = _orch.Config.PinnedTabs;
        bool hasAnyPinned = pinned is { Count: > 0 };
        TabBtnNow.Visibility = !hasAnyPinned || pinned!.Contains(ExpandedTabId.Now) ? Visibility.Visible : Visibility.Collapsed;
        TabBtnAudio.Visibility = !hasAnyPinned || pinned!.Contains(ExpandedTabId.Audio) ? Visibility.Visible : Visibility.Collapsed;
        TabBtnControl.Visibility = !hasAnyPinned || pinned!.Contains(ExpandedTabId.Control) ? Visibility.Visible : Visibility.Collapsed;
        TabBtnShelf.Visibility = !hasAnyPinned || pinned!.Contains(ExpandedTabId.Shelf) ? Visibility.Visible : Visibility.Collapsed;
        TabBtnSystem.Visibility = !hasAnyPinned || pinned!.Contains(ExpandedTabId.System) ? Visibility.Visible : Visibility.Collapsed;
        TabBtnWidgets.Visibility = !hasAnyPinned || pinned!.Contains(ExpandedTabId.Widgets) ? Visibility.Visible : Visibility.Collapsed;

        CommandPlaceholderText.Text = Strings.SearchHint(lang);
        CmdSyntaxHintsText.Text = Strings.Pick(lang, "@ Pencere   # Pano   = Hesap   > Kabuk", "@ Windows   # Clipboard   = Calculator   > Shell");
        CmdKeyHintsText.Text = Strings.Pick(lang, "↑↓ Gez   ↵ Çalıştır   Esc Kapat", "↑↓ Navigate   ↵ Run   Esc Close");

        DragHintTitle.Text = Strings.DropHint(lang);
        DragHintSub.Text = Strings.Pick(lang, "AirDrop Raf Cebi • Anında sakla ve paylaş", "AirDrop Shelf Pocket • Instant stash & share");
        DragQrShareButton.Content = Strings.QrShareBtn(lang);
        DragOpenShelfButton.Content = Strings.Pick(lang, "Rafı Aç", "Open Shelf");
        DragCancelButton.Content = Strings.Close(lang);

        NowTimersHeaderText.Text = Strings.FocusTimerTitle(lang);
        NowNotificationsHeaderText.Text = Strings.Pick(lang, "Bildirimler", "Notifications");
        BtnClearNotifications.Content = Strings.ClearNotifications(lang);
        NowNoNotificationsText.Text = Strings.NoNotifications(lang);

        AudioDevicesHeaderText.Text = Strings.Pick(lang, "Ses ve Aygıtlar", "Audio & Devices");
        AudioAppsHeaderText.Text = Strings.Pick(lang, "Uygulama Karıştırıcı", "App Mixer");
        BtnVoiceNoteOpenFolder.Content = Strings.UsbOpenFolder(lang);
        BtnNotifDetailCopy.Content = Strings.NotifCopyBtn(lang);
        BtnNotifDetailDismiss.Content = Strings.NotifDismissBtn(lang);
        AudioQualityTitleText.Text = Strings.AudioQualityTitle(lang);
        BtnAudioQualityUseMax.Content = Strings.AudioQualityUseMaxBtn(lang);
        BtnAudioQualitySettings.Content = Strings.AudioQualitySettingsBtn(lang);
        RenderVoiceNoteBar();

        TileThemeLabel.Text = Strings.Pick(lang, "Tema", "Theme");
        TileCaffeineLabel.Text = Strings.Pick(lang, "Kafein", "Caffeine");
        TileSnipLabel.Text = Strings.Pick(lang, "Ekran Alıntısı", "Snip");
        ControlBrightnessHeader.Text = Strings.Pick(lang, "Ekran Parlaklığı ve Kısıcı", "Display Brightness & Dimmer");
        BtnRegionOcr.Content = Strings.Pick(lang, "Bölge OCR", "Region OCR");
        BtnNightLightSettings.Content = Strings.Pick(lang, "Gece Işığı", "Night Light");
        BtnDisplaySettings.Content = Strings.Pick(lang, "Görüntü", "Display");
        ControlPowerHeader.Text = Strings.Pick(lang, "Güç ve Oturum", "Power & Session");
        PowerEffBtn.Content = Strings.Pick(lang, "Eko", "Eco");
        PowerBalBtn.Content = Strings.Pick(lang, "Dengeli", "Bal");
        PowerPerfBtn.Content = Strings.Pick(lang, "Perf", "Perf");
        BtnLockPc.Content = Strings.Pick(lang, "Kilitle", "Lock");
        BtnSleepPc.Content = Strings.Pick(lang, "Uyut", "Sleep");
        BtnDisplayOffPc.Content = Strings.Pick(lang, "Ekran", "Display");
        BtnPowerSettings.Content = Strings.Pick(lang, "Güç Planı", "Power Plan");
        if (IsExpandedTabVisible(ExpandedTabId.Control))
        {
            RenderControlConnectivity(_auditConnOverride ?? _orch.Connectivity.Current);
        }

        ShelfHeaderTitle.Text = Strings.FileShelfTitle(lang);
        BtnShelfOcr.Content = "OCR";
        BtnShelfQr.Content = "QR";
        BtnShelfShare.Content = Strings.Pick(lang, "Paylaş", "Share");
        BtnShelfZip.Content = "ZIP";
        BtnShelfClear.Content = Strings.Pick(lang, "Temizle", "Clear");
        ShelfEmptyText.Text = Strings.EmptyShelf(lang);
        ClipboardHeaderTitle.Text = Strings.ClipboardVault(lang);
        BtnClipClearUnpinned.Content = Strings.Pick(lang, "Sabit Olmayanları Sil", "Clear Unpinned");
        ClipboardEmptyText.Text = Strings.Pick(lang, "Pano geçmişi boş", "No clipboard history");

        SysCpuLabel.Text = Strings.CpuLabel(lang);
        SysMemLabel.Text = Strings.MemoryLabel(lang);
        SysNetLabel.Text = Strings.NetworkLabel(lang);
        SysBatLabel.Text = Strings.BatteryLabel(lang);
        SysProcHeaderText.Text = Strings.Pick(lang, "Süreç İzleyici ve Yanıt Vermeyenler", "Process Watchdog & Hung Detector");
        SysLiveBadgeText.Text = Strings.Pick(lang, "● Canlı • 1s", "● Live • 1s");
        BtnSysCleanDisk.Content = Strings.SysCleanDiskBtn(lang);
        BtnSysTrimRam.Content = Strings.SysTrimRamBtn(lang);
        BtnSysCloseIdleApps.Content = Strings.SysCloseIdleAppsBtn(lang);
        BtnSysCancelIdleApps.Content = Strings.Cancel(lang);
        BtnSysConfirmCloseIdle.Content = Strings.SysCloseSelectedBtn(lang);
        SysIdleConfirmTitleText.Text = Strings.Pick(lang, "Kapatılacak Boşta Uygulamalar", "Idle Background Apps to Close");
        if (IsExpandedTabVisible(ExpandedTabId.System))
        {
            RenderSystemTelemetry(_auditTelemetryOverride ?? _orch.Telemetry.Current);
        }

        WidgetsHeaderTitle.Text = Strings.Pick(lang, "Araç Kartları", "Widget Cards");
        BtnOpenWidgetsFolder.Content = Strings.Pick(lang, "Eklenti Klasörü", "Plugins Folder");
        BtnReloadWidgets.Content = Strings.Pick(lang, "Yenile", "Reload");
        WidgetsEmptyText.Text = Strings.Pick(lang, "Etkin araç eklentisi yok.", "No active widget plugins.");
        BtnToolColorPicker.Content = Strings.ColorPickerTitle(lang);

        SysGpuDetailLabel.Text = Strings.Pick(lang, "GPU VRAM & Sıcaklık", "GPU VRAM & Temp");
        SysDiskDetailLabel.Text = Strings.Pick(lang, $"{Strings.DiskUsageLabel(lang)} & I/O", $"{Strings.DiskUsageLabel(lang)} & I/O");

        SchedPowerHeaderText.Text = Strings.ScheduledPowerHeader(lang);
        BtnSchedStart.Content = Strings.Pick(lang, "Başlat", "Start");
        BtnSchedSnooze.Content = Strings.Pick(lang, "+10 dk", "+10m");
        BtnSchedCancel.Content = Strings.Close(lang);
        PopulateScheduledPowerChoices(lang);
        RenderControlScheduledPower(_auditScheduledPowerOverride ?? _orch.Timers.ScheduledPowerState);

        BtnPrevTrack.ToolTip = Strings.PrevTrack(lang);
        BtnPlayPause.ToolTip = Strings.PlayPauseTrack(lang);
        BtnNextTrack.ToolTip = Strings.NextTrack(lang);
        MediaSeekSlider.ToolTip = Strings.MediaSeek(lang);

        QuietIslandBtn.ToolTip = Strings.QuietIsland(lang);
        OpenCommandBtn.ToolTip = Strings.CommandPalette(lang);
        PinToggleBtn.ToolTip = Strings.PinOpen(lang);
        SettingsHeaderBtn.ToolTip = Strings.Settings(lang);
        CollapseHeaderBtn.ToolTip = Strings.Close(lang);
        _trayIcon.UpdateTooltip(Strings.AppTitle(lang));
        ApplyHeaderButtonStates(_lastSnapshot);
    }

    private void ApplyHeaderButtonStates(IslandCoordinatorSnapshot snap)
    {
        QuietIslandBtn.Content = (snap.IsQuietIsland || _orch.Config.QuietIslandEnabled) ? "\uE7ED" : "\uE708";
        PinToggleBtn.Foreground = snap.IsPinnedOpen ? ActiveAccentDotBrush : MutedTabFgBrush;
    }

    public async Task<(double MeasuredFps, int DisplayHz)> MeasureAnimationBurstAsync(int durationMs = 420)
    {
        int hz = (_orch.MonitorLayout as Win32MonitorLayoutService)?.GetMonitorRefreshRateHz(_hwnd) ?? 60;
        _burstFrameCount = 0;
        long startTicks = Stopwatch.GetTimestamp();
        _burstMeasuring = true;

        _motion.SetHoverScale(1.032);
        StartRenderLoop();
        await Task.Delay(Math.Max(120, durationMs / 2));
        _motion.SetHoverScale(1.0);
        StartRenderLoop();
        await Task.Delay(Math.Max(120, durationMs / 2));

        _burstMeasuring = false;
        double elapsedSec = Math.Max(0.1, (Stopwatch.GetTimestamp() - startTicks) / (double)Stopwatch.Frequency);
        double fps = _burstFrameCount / elapsedSec;
        return (fps, hz);
    }

    public void ApplyRenderAuditState(
        IslandCoordinatorSnapshot snap,
        double[]? spectrumBands = null,
        MediaPlaybackSnapshot? demoMedia = null,
        IReadOnlyList<DisplayBrightnessInfo>? demoDisplays = null,
        ConnectivitySnapshot? demoConn = null,
        PrivacySensorSnapshot? demoPrivacy = null,
        IReadOnlyList<TimerSnapshot>? demoTimers = null,
        IReadOnlyList<NotificationEntry>? demoNotifs = null,
        IReadOnlyList<ShelfItem>? demoShelf = null,
        IReadOnlyList<ClipboardEntry>? demoClip = null,
        SystemTelemetrySnapshot? demoTelemetry = null,
        IReadOnlyList<WidgetRenderSnapshot>? demoWidgets = null,
        IReadOnlyList<CommandEntry>? demoCommands = null,
        AudioMixerSnapshot? demoAudio = null,
        string? demoLyric = null,
        MeetingOccurrence? demoMeeting = null,
        LanDropShareInfo? demoQrShare = null,
        ScheduledPowerSnapshot? demoScheduledPower = null)
    {
        _isRenderAuditMode = true;
        _auditMediaOverride = demoMedia;
        _auditAudioOverride = demoAudio;
        _auditLyricOverride = demoLyric;
        _auditMeetingOverride = demoMeeting;
        _auditQrShareOverride = demoQrShare;
        _auditDisplaysOverride = demoDisplays;
        _auditConnOverride = demoConn;
        _auditPrivacyOverride = demoPrivacy;
        _auditTimersOverride = demoTimers;
        _auditNotifsOverride = demoNotifs;
        _auditShelfOverride = demoShelf;
        _auditClipOverride = demoClip;
        _auditTelemetryOverride = demoTelemetry;
        _auditWidgetsOverride = demoWidgets;
        _auditScheduledPowerOverride = demoScheduledPower;

        bool needExpanded = RequiresExpandedTier(snap.State);
        _isExpandedTierActive = needExpanded;
        SetTierCanvasSize(needExpanded ? ExpandedTierW : CompactTierW, needExpanded ? ExpandedTierH : CompactTierH);

        ApplyLocalization();
        ApplyShellVisualTheme();

        _lastSnapshot = snap;
        _motion.ApplyTarget(snap.Layout, true);
        SnapMotionToTarget(snap.Layout);

        if (spectrumBands is { Length: >= 8 })
        {
            _motion.SetSpectrumTargets(spectrumBands);
            for (int i = 0; i < IslandMotionState.SpectrumBarCount; i++)
            {
                double v = Math.Clamp(spectrumBands[i], 0.14, 1.0);
                _compactSpecScales[i].ScaleY = v;
                _audioSpecScales[i].ScaleY = v;
            }
        }

        ApplySurfaceSizing(snap);
        ActivateSurfaceForState(snap);

        UpdateCompactAndSatelliteContent(snap);
        UpdateTransientHudContent(snap);

        if (snap.State == IslandPresentationState.S4Expanded)
        {
            if (demoAudio is not null && snap.ActiveTab == ExpandedTabId.Audio)
            {
                RenderAudioTab(demoAudio);
            }
            else
            {
                RefreshExpandedTabPanel(snap.ActiveTab);
            }
            SelectExpandedTabVisual(snap.ActiveTab, snapIndicator: true);
        }
        else if (snap.State == IslandPresentationState.S5Command && demoCommands is not null)
        {
            CommandSearchBox.Text = "sp";
            CommandPlaceholderText.Visibility = Visibility.Collapsed;
            CommandResultsList.ItemsSource = demoCommands;
            CommandResultsList.SelectedIndex = demoCommands.Count > 0 ? 0 : -1;
        }

        RootSurface.Measure(new System.Windows.Size(_tierHostWidth, _tierHostHeight));
        RootSurface.Arrange(new System.Windows.Rect(0, 0, _tierHostWidth, _tierHostHeight));
        RootSurface.UpdateLayout();

        if (snap.State == IslandPresentationState.S4Expanded)
        {
            SelectExpandedTabVisual(snap.ActiveTab, snapIndicator: true);
        }

        ApplyMotionFrameToVisuals(snap, forceHitUpdate: true);
        RootSurface.UpdateLayout();
    }

    public RenderTargetBitmap RenderAuditBackdropBitmap(int backdropWidth = 800, int backdropHeight = 460)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bgBrush = new LinearGradientBrush(
                Color.FromRgb(11, 16, 26),
                Color.FromRgb(23, 33, 52),
                new Point(0, 0),
                new Point(1, 1));
            bgBrush.Freeze();
            dc.DrawRectangle(bgBrush, null, new System.Windows.Rect(0, 0, backdropWidth, backdropHeight));

            // Subtle macOS wallpaper ambient bloom
            var bloomBrush = new RadialGradientBrush(
                Color.FromArgb(42, 56, 189, 248),
                Color.FromArgb(0, 0, 0, 0));
            bloomBrush.Freeze();
            dc.DrawEllipse(bloomBrush, null, new Point(backdropWidth * 0.5, backdropHeight * 0.35), 340, 220);

            // Crisp MacBook display bezel bar / top edge
            if (_orch.Config.DockPlacement == DockPlacementMode.NotchFlush)
            {
                var bezelPen = new System.Windows.Media.Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1.0);
                bezelPen.Freeze();
                dc.DrawLine(bezelPen, new Point(0, 0.5), new Point(backdropWidth, 0.5));
            }
        }

        var rtb = new RenderTargetBitmap(backdropWidth, backdropHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        double offsetX = Math.Round((backdropWidth - _tierHostWidth) * 0.5);
        double offsetY = _orch.Config.DockPlacement == DockPlacementMode.NotchFlush ? 0.0 : 12.0;

        var islandVisual = new DrawingVisual();
        using (var dc = islandVisual.RenderOpen())
        {
            var vb = new VisualBrush(RootSurface)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Viewbox = new System.Windows.Rect(0, 0, _tierHostWidth, _tierHostHeight),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            dc.DrawRectangle(vb, null, new System.Windows.Rect(offsetX, offsetY, _tierHostWidth, _tierHostHeight));
        }
        rtb.Render(islandVisual);
        rtb.Freeze();
        return rtb;
    }

    // -------------------------------------------------------------------------
    // Utility Helpers
    // -------------------------------------------------------------------------

    private static bool IsInteractiveChild(DependencyObject? source) =>
        HasAncestor<Button>(source) ||
        HasAncestor<Slider>(source) ||
        HasAncestor<System.Windows.Controls.ComboBox>(source) ||
        HasAncestor<System.Windows.Controls.TextBox>(source) ||
        HasAncestor<ListBoxItem>(source) ||
        HasAncestor<System.Windows.Controls.CheckBox>(source) ||
        HasAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) ||
        HasInteractiveAncestor(source);

    private static bool HasInteractiveAncestor(DependencyObject? source)
    {
        var cur = source;
        while (cur is not null)
        {
            if (cur is FrameworkElement fe &&
                (Equals(fe.Tag, "Interactive") ||
                 ReferenceEquals(fe.Cursor, System.Windows.Input.Cursors.Hand) ||
                 fe.Name is "NotifDetailOverlay" or "AudioQualityOverlay"))
            {
                return true;
            }
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private static bool HasAncestor<T>(DependencyObject? current) where T : DependencyObject =>
        FindAncestor<T>(current) is not null;

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static BitmapImage? DecodeFrozenBitmap(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        return a.AsSpan().SequenceEqual(b.AsSpan());
    }

    private static Brush ParseHexBrush(string? hex, string fallback)
    {
        Color c = TryParseColor(hex, out Color parsed)
            ? parsed
            : (TryParseColor(fallback, out Color fb) ? fb : Color.FromRgb(10, 132, 255));
        uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        return HexBrushCache.GetOrAdd(key, static k =>
        {
            var color = Color.FromArgb((byte)(k >> 24), (byte)(k >> 16), (byte)(k >> 8), (byte)k);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        });
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string clean = hex.Trim().TrimStart('#');
        if (clean.Length == 6 && uint.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            color = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
            return true;
        }
        if (clean.Length == 8 && uint.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            color = Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
            return true;
        }
        return false;
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
