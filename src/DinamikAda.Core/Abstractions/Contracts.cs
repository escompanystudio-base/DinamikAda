using System.Collections.Immutable;

namespace DinamikAda.Core.Abstractions;

/// <summary>
/// Canonical presentation states of the Dynamic Island shell.
/// </summary>
public enum IslandPresentationState
{
    S0Hidden = 0,
    S1Dormant = 1,
    S2Compact = 2,
    S2Split = 3,
    S3TransientHud = 4,
    S4Expanded = 5,
    S5Command = 6,
    S6DragShelf = 7
}

public enum ExpandedTabId
{
    Now = 0,
    Audio = 1,
    Control = 2,
    Shelf = 3,
    System = 4,
    Widgets = 5,
    AiQuota = 6
}

public enum DockPlacementMode
{
    FloatingTop = 0,
    NotchFlush = 1,
    FreeFloat = 2
}

public enum MonitorPlacementMode
{
    PrimaryOnly = 0,
    PinnedMonitor = 1,
    FollowCursor = 2
}

public enum HorizontalAnchorDetent
{
    LeftSafe = 0,
    Center = 1,
    RightSafe = 2,
    Custom = 3
}

public enum ExpandTriggerMode
{
    HoverDwellOrClick = 0,
    ClickOnly = 1
}

public enum FullscreenPolicy
{
    HideCompletely = 0,
    EdgePeekHairline = 1,
    KeepCompact = 2
}

public enum FullscreenAppKind
{
    None = 0,
    Game = 1,
    Video = 2,
    Productivity = 3
}

public enum ActivityKind
{
    None = 0,
    PrivacySensor = 100,
    MicMuted = 95,
    VoiceNote = 90,
    Meeting = 85,
    Timer = 80,
    ScheduledAction = 78,
    Transfer = 72,
    DevBuild = 70,
    Media = 60,
    LowBattery = 55,
    FileShelf = 45,
    Telemetry = 30,
    CustomWidget = 25
}

public enum WindowSnapPreset
{
    LeftHalf = 0,
    RightHalf = 1,
    CenterFocus70 = 2,
    MoveToNextMonitor = 3
}

public enum ScheduledPowerActionKind
{
    None = 0,
    Sleep = 1,
    Shutdown = 2,
    PauseMedia = 3,
    Lock = 4
}

public enum HudKind
{
    Volume = 0,
    MicMute = 1,
    Brightness = 2,
    CapsLock = 3,
    NumLock = 4,
    Bluetooth = 5,
    Battery = 6,
    Focus = 7,
    MediaTrack = 8,
    Clipboard = 9,
    FileStashed = 10,
    TimerAlert = 11,
    NotificationPulse = 12,
    WidgetAlert = 13,
    BluetoothConnect = 14,
    BluetoothLowBattery = 15,
    QrShare = 16,
    OcrCapture = 17,
    TransferComplete = 18,
    WindowPin = 19,
    VoiceNote = 20,
    ColorPicked = 21,
    UsbDrive = 22,
    DevBridge = 23,
    ScheduledPower = 24,
    GameMode = 25
}

public enum FocusQuietProfile
{
    Off = 0,
    PriorityOnly = 1,
    AlarmsOnly = 2,
    Unknown = 255
}

public sealed record LiveActivityState(
    string SourceId,
    ActivityKind Kind,
    int Priority,
    string PrimaryText,
    string? SecondaryText = null,
    string AccentHex = "#0A84FF",
    double? Progress01 = null,
    bool IsCritical = false,
    bool HasMicActive = false,
    bool HasCamActive = false,
    byte[]? ThumbnailBytes = null,
    string? IconGlyph = null,
    ExpandedTabId TargetTab = ExpandedTabId.Now);

public sealed record TransientHudEvent(
    string CoalesceKey,
    HudKind Kind,
    string Title,
    string? Subtitle = null,
    double? Value01 = null,
    string AccentHex = "#FFFFFF",
    int DurationMs = 1900,
    bool InteractiveOnHover = true,
    bool IsSticky = false,
    string? IconGlyph = null,
    string? PrimaryActionId = null,
    string? PrimaryActionLabel = null,
    string? SecondaryActionId = null,
    string? SecondaryActionLabel = null,
    string? ActionPayload = null);

public sealed record CommandEntry(
    string Id,
    string Title,
    string Subtitle,
    string Category,
    string? ShortcutHint,
    int BaseScore,
    Func<CancellationToken, Task<CommandExecutionResult>> ExecuteAsync,
    string? IconGlyph = null,
    string? CopyText = null);

public sealed record CommandExecutionResult(
    bool Succeeded,
    bool RestorePreviousFocus = true,
    string? FeedbackTitle = null,
    bool CollapseAfter = true);

public sealed record IslandModuleDescriptor(
    string Id,
    string DisplayName,
    bool RequiresPackageIdentity = false);

public interface IIslandModule : IAsyncDisposable
{
    IslandModuleDescriptor Descriptor { get; }
    Task InitializeAsync(IIslandHostContext host, CancellationToken ct);
}

public interface ILiveActivitySource
{
    string SourceId { get; }
    event EventHandler<LiveActivityState?>? ActivityChanged;
    LiveActivityState? CurrentActivity { get; }
}

public interface ITransientHudBus
{
    event EventHandler<TransientHudEvent>? HudRequested;
    void Publish(TransientHudEvent hudEvent);
}

public interface ICommandProvider
{
    string ProviderId { get; }
    int Order { get; }
    ValueTask<IReadOnlyList<CommandEntry>> QueryAsync(string query, CancellationToken ct);
}

public interface IIslandHostContext
{
    ITransientHudBus HudBus { get; }
    TimeProvider TimeProvider { get; }
    void PublishLiveActivity(LiveActivityState? state, string sourceId);
    void RequestNavigateTab(ExpandedTabId tab);
    void RequestExpand(ExpandedTabId? tab = null);
    void RequestCollapse();
}

public sealed record IslandLayoutTarget(
    IslandPresentationState State,
    double WidthDip,
    double HeightDip,
    double CornerRadiusDip,
    double SatelliteWidthDip,
    double SatelliteGapDip,
    bool IsClickThrough,
    bool RequiresKeyboardFocus,
    double EarRadius = 0,
    double TopCornerRadius = 0);

public interface IWindowStyleHost
{
    bool IsClickThrough { get; }
    bool IsActivatable { get; }
    bool IsDragCatcherArmed { get; }
    void SetClickThrough(bool clickThrough);
    void SetActivatable(bool allowFocus, nint? restoreFocusHwnd = null);
    void SetDragCatcherArmed(bool armed);
    void SetWindowTierBounds(double widthDip, double heightDip, double leftScreenDip, double topScreenDip);
    void SetVisible(bool visible);
}

public interface IFullscreenDetector
{
    event EventHandler<FullscreenSnapshot>? FullscreenChanged;
    FullscreenSnapshot Current { get; }
    void RefreshNow();
}

public sealed record FullscreenSnapshot(
    bool IsFullscreen,
    FullscreenAppKind Kind,
    string? ProcessName,
    nint MonitorHandle,
    string Reason);

public interface IMonitorLayoutService
{
    IReadOnlyList<MonitorDescriptor> GetMonitors();
    MonitorDescriptor GetTargetMonitor(MonitorPlacementMode mode, string? pinnedDeviceName, int cursorXPx, int cursorYPx);
}

public sealed record MonitorDescriptor(
    nint Handle,
    string DeviceName,
    string FriendlyName,
    bool IsPrimary,
    int LeftPx,
    int TopPx,
    int WidthPx,
    int HeightPx,
    double DpiScale,
    int RefreshRateHz = 60,
    int WorkLeftPx = 0,
    int WorkTopPx = 0,
    int WorkWidthPx = 0,
    int WorkHeightPx = 0)
{
    public int EffectiveWorkLeftPx => WorkWidthPx > 0 ? WorkLeftPx : LeftPx;
    public int EffectiveWorkTopPx => WorkHeightPx > 0 ? WorkTopPx : TopPx;
    public int EffectiveWorkWidthPx => WorkWidthPx > 0 ? WorkWidthPx : WidthPx;
    public int EffectiveWorkHeightPx => WorkHeightPx > 0 ? WorkHeightPx : HeightPx;
}

public sealed record MediaPlaybackSnapshot(
    bool HasSession,
    string SessionId,
    string AppName,
    string Title,
    string Artist,
    bool IsPlaying,
    bool CanPlayPause,
    bool CanNext,
    bool CanPrev,
    bool CanSeek,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset LastUpdatedUtc,
    double PlaybackRate,
    string AccentHex,
    byte[]? ThumbnailBytes,
    ImmutableArray<MediaSessionSummary> AllSessions,
    string? SecondaryAccentHex = null,
    string? HighlightAccentHex = null);

public sealed record MediaSessionSummary(string SessionId, string AppName, string Title, bool IsPlaying);

public sealed record AudioAppSessionSnapshot(
    string GroupKey,
    int PrimaryProcessId,
    string ProcessName,
    string DisplayName,
    float Volume01,
    bool IsMuted,
    float PeakLevel01,
    string? IconPath,
    string? RoutedOutputDeviceId = null);

public sealed record AudioEndpointSnapshot(
    string Id,
    string FriendlyName,
    bool IsDefault,
    bool IsInput,
    bool IsBluetooth);

public sealed record AudioFormatOption(
    int SampleRateHz,
    ushort BitsPerSample,
    bool IsFloat,
    ushort Channels,
    string Label,
    bool IsActive = false);

public sealed record AudioMixerSnapshot(
    float MasterVolume01,
    bool IsMasterMuted,
    string DefaultOutputId,
    string DefaultOutputName,
    float MicVolume01,
    bool IsMicMuted,
    bool HasMicDevice,
    bool IsLoopbackActive,
    bool IsBluetoothHfpFallback,
    ImmutableArray<double> SpectrumBands,
    ImmutableArray<AudioAppSessionSnapshot> AppSessions,
    ImmutableArray<AudioEndpointSnapshot> OutputDevices,
    ImmutableArray<AudioEndpointSnapshot> InputDevices,
    string? OutputFormatLabel = null,
    ImmutableArray<AudioFormatOption> SupportedOutputFormats = default);

public sealed record DisplayBrightnessInfo(
    string DeviceId,
    string FriendlyName,
    nint MonitorHandle,
    bool IsInternal,
    bool SupportsDdcCi,
    bool IsHdrActive,
    int HardwareBrightness01To100,
    int SoftwareDimmerPercent);

public sealed record PrivacySensorSnapshot(
    bool IsMicActive,
    bool IsCamActive,
    ImmutableArray<string> MicConsumers,
    ImmutableArray<string> CamConsumers);

public sealed record ShelfItem(
    string Id,
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset AddedUtc);

public sealed record ClipboardEntry(
    string Id,
    string Kind,
    string PreviewText,
    string? FullText,
    string? ImagePngPath,
    ImmutableArray<string> FilePaths,
    bool IsPinned,
    DateTimeOffset CapturedUtc);

public sealed record TimerSnapshot(
    string Id,
    string Label,
    bool IsPomodoro,
    bool IsStopwatch,
    bool IsRunning,
    bool IsCompleted,
    TimeSpan Total,
    TimeSpan RemainingOrElapsed);

public sealed record DiskVolumeSnapshot(
    string DriveLetter,
    string Label,
    double UsedGb,
    double TotalGb,
    double UsedPercent,
    bool IsRemovable = false);

public sealed record SystemTelemetrySnapshot(
    double CpuPercent,
    double MemoryPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double GpuPercent,
    double NetUpMbps,
    double NetDownMbps,
    bool HasBattery,
    int BatteryPercent,
    bool IsCharging,
    double? BatteryWatts,
    TimeSpan? BatteryRemaining,
    ImmutableArray<ProcessSummary> TopOrHungProcesses,
    string? GpuName = null,
    string? GpuEngine = null,
    double GpuVramUsedGb = 0.0,
    double GpuVramTotalGb = 0.0,
    double? GpuTemperatureC = null,
    int? GpuFanRpm = null,
    int? GpuFanPercent = null,
    double DiskReadMbps = 0.0,
    double DiskWriteMbps = 0.0,
    double? PingMs = null,
    string? PingHost = null,
    ImmutableArray<DiskVolumeSnapshot> DiskVolumes = default);

public sealed record ProcessSummary(
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    double MemoryMb,
    bool IsHung,
    bool IsProtected,
    double CpuPercent = 0.0,
    int ProcessCount = 1);

public sealed record NotificationEntry(
    uint Id,
    string SourceApp,
    string Title,
    string Body,
    DateTimeOffset ReceivedUtc,
    bool CanDismissFromOs,
    byte[]? AppIconBytes = null,
    string? AppUserModelId = null,
    string? ExecutablePath = null);

public sealed record BluetoothDeviceSnapshot(
    string Id,
    string Name,
    bool IsConnected,
    int? BatteryPercent,
    string? CoreAudioEndpointId);

public sealed record ConnectivitySnapshot(
    bool IsPackagedIdentity,
    bool IsWifiAvailable,
    bool IsWifiOn,
    string? WifiSsid,
    int? WifiSignalPercent,
    bool IsBluetoothAvailable,
    bool IsBluetoothOn,
    ImmutableArray<BluetoothDeviceSnapshot> PairedBluetoothDevices,
    FocusQuietProfile WindowsFocusProfile,
    bool IsDarkTheme,
    bool IsCaffeineActive,
    int PowerModeIndex,
    bool IsEnergySaverOn,
    bool IsEthernetConnected = false,
    string? EthernetName = null,
    string? EthernetSpeedText = null,
    bool HasBattery = true,
    string? ActivePowerPlanName = null);

public sealed record WidgetRenderSnapshot(
    string WidgetId,
    string Name,
    string Title,
    string Subtitle,
    string Status,
    string AccentHex,
    double? Progress0To100,
    bool ShowPillBadge,
    string? PillLabel,
    bool IsFaulted,
    string? FaultReason,
    ImmutableArray<WidgetActionDescriptor> Actions);

public sealed record WidgetActionDescriptor(
    string Id,
    string Label,
    string? IconGlyph = null);

public interface IMediaControlBackend : IAsyncDisposable
{
    event EventHandler<MediaPlaybackSnapshot>? SnapshotChanged;
    MediaPlaybackSnapshot Current { get; }
    Task TogglePlayPauseAsync(CancellationToken ct = default);
    Task NextAsync(CancellationToken ct = default);
    Task PreviousAsync(CancellationToken ct = default);
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);
    Task SelectSessionAsync(string sessionId, CancellationToken ct = default);
}

public interface IAudioBackend : IAsyncDisposable
{
    event EventHandler<AudioMixerSnapshot>? SnapshotChanged;
    AudioMixerSnapshot Current { get; }
    void SetSpectrumRequested(bool requested);
    void SetMasterVolume(float volume01);
    void SetMasterMute(bool muted);
    void SetMicVolume(float volume01);
    void SetMicMute(bool muted);
    void SetAppVolume(string groupKey, float volume01);
    void SetAppMute(string groupKey, bool muted);
    void SetDefaultOutputDevice(string deviceId);
    void SetDefaultInputDevice(string deviceId);
    void SetAppOutputDevice(string processName, string? deviceId);
    void SyncAppRoutingRules(IReadOnlyDictionary<string, string> routes);
    Task<bool> SetDefaultOutputFormatAsync(int sampleRateHz, ushort bitsPerSample, bool isFloat, CancellationToken ct = default);
}

public interface IBrightnessBackend : IDisposable
{
    event EventHandler<IReadOnlyList<DisplayBrightnessInfo>>? DisplaysChanged;
    IReadOnlyList<DisplayBrightnessInfo> GetDisplays();
    void RefreshDisplays();
    void SetHardwareBrightness(string deviceId, int percent);
    void SetSoftwareDimmer(string deviceId, int dimPercent);
}

public interface IConnectivityBackend : IDisposable
{
    event EventHandler<ConnectivitySnapshot>? StateChanged;
    ConnectivitySnapshot Current { get; }
    Task RefreshAsync(CancellationToken ct = default);
    Task<bool> ToggleWifiAsync(CancellationToken ct = default);
    Task<bool> ToggleBluetoothAsync(CancellationToken ct = default);
    Task<bool> ToggleBluetoothAudioDeviceAsync(string deviceId, CancellationToken ct = default);
    void ToggleDarkTheme();
    void SetPowerMode(int modeIndex);
    void ToggleCaffeine();
    void LockWorkstation();
    void SleepWorkstation();
    void TurnOffDisplay();
    void LaunchScreenSnip();
    void OpenWindowsSettings(string uri);
}

public interface INotificationBackend : IDisposable
{
    event EventHandler<IReadOnlyList<NotificationEntry>>? NotificationsChanged;
    event EventHandler<NotificationEntry>? NotificationArrived;
    bool SupportsNativeMirror { get; }
    IReadOnlyList<NotificationEntry> Current { get; }
    Task InitializeAsync(CancellationToken ct);
    Task DismissAsync(uint id);
    Task ClearAllAsync();
    void AddInternalAlert(string sourceApp, string title, string body);
}
