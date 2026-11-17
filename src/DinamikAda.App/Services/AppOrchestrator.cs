using System.Diagnostics;
using System.IO;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Arbitration;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;
using ActivityKind = DinamikAda.Core.Abstractions.ActivityKind;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Modules.Media;
using DinamikAda.Modules.Notifications;
using DinamikAda.Modules.Productivity;
using DinamikAda.Modules.SystemControl;
using DinamikAda.Modules.Telemetry;
using DinamikAda.Modules.Widgets;
using DinamikAda.Platform.Win32.Identity;
using DinamikAda.Platform.Win32.Interop;
using Timer = System.Threading.Timer;

namespace DinamikAda.App.Services;

public sealed class AppOrchestrator : IAsyncDisposable
{
    public AppConfig Config { get; }
    public AppConfigStore ConfigStore { get; }
    public IslandCoordinator Coordinator { get; }
    public IMonitorLayoutService MonitorLayout { get; }
    public IFullscreenDetector FullscreenDetector { get; }
    public PackageIdentityStatus PackageIdentity { get; }

    public GsmtcMediaService Media { get; }
    public CoreAudioHost Audio { get; }
    public BrightnessService Brightness { get; }
    public ConnectivityAndSystemService Connectivity { get; }
    public PrivacySensorService Privacy { get; }
    public NotificationHubService Notifications { get; }
    public FileShelfService Shelf { get; }
    public ClipboardVaultService Clipboard { get; }
    public FocusTimerService Timers { get; }
    public SystemTelemetryService Telemetry { get; }
    public HungProcessService HungProcesses { get; }
    public SystemMaintenanceService Maintenance { get; }
    public WidgetHostService Widgets { get; }
    public LauncherService Launcher { get; }
    public LanDropServer LanDrop { get; }
    public ScreenOcrService ScreenOcr { get; }
    public MeetingCalendarService Calendar { get; }
    public TransferMonitorService Transfers { get; }
    public LyricsService Lyrics { get; }
    public VoiceNoteService VoiceNotes { get; }
    public WindowDesktopControlService WindowControl { get; }
    public UsbDriveMonitorService UsbDrives { get; }
    public DeveloperBridgeService DeveloperBridge { get; }
    public AiQuotaTrackerService AiQuota { get; }

    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? QrShareRequested;
    public event Func<Task>? RegionOcrRequested;
    public event Func<Task>? RegionColorPickRequested;

    public void RequestOpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    private CancellationTokenSource? _bgCts;
    private Timer? _maintenanceTimer;
    private int _lastReportedVolumePct = -1;
    private bool _lastReportedMute;
    private bool _lastReportedMicMute;
    private bool _hasMicDeviceBaseline;
    private bool _lastCapsState;
    private bool _lastNumState;
    private bool _keyboardSyncPrimed;
    private FocusQuietProfile _lastFocusProfile = FocusQuietProfile.Unknown;
    private readonly HashSet<string> _alertedLowBatteryBtKeys = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastCompletedTransferPath;
    private bool _isGameModeActive;
    // ReconcileGameMode runs on the fullscreen hook thread, the maintenance thread pool and the UI
    // thread; without this the check-then-set could switch power plans twice and lose the user's plan.
    private readonly object _gameModeSync = new();
    private readonly object _devBuildSync = new();
    private string? _lastReportedBridgeError;
    private string? _savedPreGamePowerSchemeGuid;
    private bool _isSystemTabVisible;
    private bool _isDormantTelemetryPillVisible;
    private CancellationTokenSource? _devBuildClearCts;
    private int _maintenanceTickCounter;
    private int _bgPollInFlight;
    private int _hooksStarted;
    private volatile bool _disposed;
    private readonly object _lifecycleSync = new();
    private Task? _startTask;
    private Task? _disposeTask;

    public AppOrchestrator(
        AppConfig config,
        AppConfigStore configStore,
        IslandCoordinator coordinator,
        IMonitorLayoutService monitorLayout,
        IFullscreenDetector fullscreenDetector,
        GsmtcMediaService media,
        CoreAudioHost audio,
        BrightnessService brightness,
        ConnectivityAndSystemService connectivity,
        PrivacySensorService privacy,
        NotificationHubService notifications,
        FileShelfService shelf,
        ClipboardVaultService clipboard,
        FocusTimerService timers,
        SystemTelemetryService telemetry,
        HungProcessService hungProcesses,
        SystemMaintenanceService maintenance,
        WidgetHostService widgets,
        LauncherService launcher,
        LanDropServer lanDrop,
        ScreenOcrService screenOcr,
        MeetingCalendarService calendar,
        TransferMonitorService transfers,
        LyricsService lyrics,
        VoiceNoteService voiceNotes,
        WindowDesktopControlService windowControl,
        UsbDriveMonitorService usbDrives,
        DeveloperBridgeService developerBridge,
        AiQuotaTrackerService aiQuota)
    {
        Config = config;
        ConfigStore = configStore;
        Coordinator = coordinator;
        MonitorLayout = monitorLayout;
        FullscreenDetector = fullscreenDetector;
        PackageIdentity = PackageIdentityDetector.Detect();

        Media = media;
        Audio = audio;
        Brightness = brightness;
        Connectivity = connectivity;
        Privacy = privacy;
        Notifications = notifications;
        Shelf = shelf;
        Clipboard = clipboard;
        Timers = timers;
        Telemetry = telemetry;
        HungProcesses = hungProcesses;
        Maintenance = maintenance;
        Widgets = widgets;
        Launcher = launcher;
        LanDrop = lanDrop;
        ScreenOcr = screenOcr;
        Calendar = calendar;
        Transfers = transfers;
        Lyrics = lyrics;
        VoiceNotes = voiceNotes;
        WindowControl = windowControl;
        UsbDrives = usbDrives;
        DeveloperBridge = developerBridge;
        AiQuota = aiQuota;

        BindModuleSignals();
    }

    /// <summary>
    /// Installs WinEvent message-loop hooks. Must run on an STA thread with an active Win32 message pump
    /// (the WPF UI Dispatcher thread). Safe to call multiple times or omit when StartAsync runs under WPF.
    /// </summary>
    public void StartMessageLoopHooks()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _hooksStarted, 1) != 0) return;

        if (FullscreenDetector is Platform.Win32.Windowing.FullscreenDetector fd)
        {
            fd.Start();
        }

        Notifications.StartToastHook();
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_lifecycleSync)
        {
            if (_disposed) return Task.CompletedTask;
            return _startTask ??= StartCoreAsync(ct);
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (_disposed) return;
        _bgCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _bgCts.Token;

        ApplyRuntimeConfig();
        Privacy.Start();
        Widgets.Start();
        AiQuota.Start();
        Widgets.SetPausedWhenCollapsed(Coordinator.Current.State != IslandPresentationState.S4Expanded);
        UpdateWidgetPillActivity(Widgets.GetSnapshots());

        await Task.WhenAll(
            Media.InitializeAsync(token),
            Notifications.InitializeAsync(token),
            Connectivity.RefreshAsync(token),
            Brightness.RefreshDisplaysAsync(token)).ConfigureAwait(false);

        if (_disposed || token.IsCancellationRequested) return;

        // Warm launcher app index off-thread without stalling startup or failing on restricted environments.
        _ = Task.Run(async () =>
        {
            try
            {
                await Launcher.RebuildAppIndexAsync(includeStoreApps: true, token).ConfigureAwait(false);
            }
            catch
            {
                // Non-critical warmup failure; QueryAsync retries cleanly on demand.
            }
        }, token);

        if (Volatile.Read(ref _hooksStarted) == 0)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                if (dispatcher.CheckAccess())
                {
                    StartMessageLoopHooks();
                }
                else
                {
                    _ = dispatcher.BeginInvoke(new Action(StartMessageLoopHooks));
                }
            }
            else if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                StartMessageLoopHooks();
            }
            else if (FullscreenDetector is Platform.Win32.Windowing.FullscreenDetector fd)
            {
                fd.Start();
            }
        }

        Coordinator.UpdateFullscreenState(FullscreenDetector.Current);
        ReconcileGameMode(FullscreenDetector.Current);
        UpdateMicMutedActivity(Audio.Current);

        _lastFocusProfile = Connectivity.Current.WindowsFocusProfile;
        _lastCapsState = (NativeMethods.GetKeyState(0x14) & 1) != 0;
        _lastNumState = (NativeMethods.GetKeyState(0x90) & 1) != 0;
        _keyboardSyncPrimed = true;

        lock (_lifecycleSync)
        {
            if (!_disposed) _maintenanceTimer = new Timer(_ => MaintenanceTick(), null, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400));
        }
    }

    public void ApplyRuntimeConfig()
    {
        if (_disposed) return;
        SyncRuntimePreferences();
        Clipboard.SetMaxItems(Config.MaxClipboardItems);
        Coordinator.Recompute();
        if (!Config.ShowTelemetryInPill)
        {
            Coordinator.PublishLiveActivity(null, "telemetry.pill");
        }
    }

    private void SyncRuntimePreferences()
    {
        Notifications.Language = Config.Language;
        Notifications.MirrorOsNotificationsEnabled = Config.MirrorOsNotifications;
        Connectivity.Language = Config.Language;
        Timers.Language = Config.Language;
        Launcher.Language = Config.Language;
        Calendar.Language = Config.Language;
        Transfers.Language = Config.Language;
        VoiceNotes.Language = Config.Language;
        AiQuota.Language = Config.Language;

        Calendar.Configure(Config.CalendarEnabled, Config.CalendarIcsSources);
        Transfers.Configure(Config.DownloadsWatchEnabled, Config.DownloadsWatchPath);
        Lyrics.Configure(Config.LyricsEnabled, Config.LyricsFolders);
        Audio.SyncAppRoutingRules(Config.AppAudioRoutes);
        Telemetry.SetPingHost(Config.PingHost);
        DeveloperBridge.UpdateSettings(Config.EnableDeveloperBridge, Config.DeveloperBridgePort);
        string? bridgeError = Config.EnableDeveloperBridge ? DeveloperBridge.LastBindError : null;
        if (bridgeError is not null && !string.Equals(bridgeError, _lastReportedBridgeError, StringComparison.Ordinal))
        {
            Notifications.AddInternalAlert(
                "DinamikAda",
                Strings.Pick(Config.Language, "Geliştirici köprüsü portu kullanılamıyor", "Developer bridge port unavailable"),
                Strings.Pick(
                    Config.Language,
                    $"{bridgeError}. Named pipe çalışmaya devam ediyor; Ayarlar'dan farklı bir port seçin.",
                    $"{bridgeError}. The named pipe still works; pick another port in Settings."));
        }
        _lastReportedBridgeError = bridgeError;
        UpdateMicMutedActivity(Audio.Current);
        ReconcileGameMode(FullscreenDetector.Current);
        UpdateTelemetrySamplingGates();
        OnPrivacyChanged(this, Privacy.Current);
        if (!Config.BluetoothBatteryAlertsEnabled)
        {
            Coordinator.PublishLiveActivity(null, "bt.lowbattery");
        }
        else
        {
            UpdateBluetoothBatteryStatus(Connectivity.Current, fireHudAlert: false);
        }
    }

    private void BindModuleSignals()
    {
        Coordinator.SnapshotChanged += OnCoordinatorSnapshotChanged;
        FullscreenDetector.FullscreenChanged += OnFullscreenChanged;
        Media.SnapshotChanged += OnMediaSnapshotChanged;
        Timers.ActivityChanged += OnTimerActivityChanged;
        Timers.ScheduledPowerWarningReached += OnScheduledPowerWarningReached;
        Timers.ScheduledPowerDue += OnScheduledPowerDue;
        Privacy.PrivacyChanged += OnPrivacyChanged;
        Audio.SnapshotChanged += OnAudioSnapshotChanged;
        Audio.AudioActionFailed += OnAudioActionFailed;
        Brightness.BrightnessAdjustedByUser += OnBrightnessAdjustedByUser;
        Connectivity.StateChanged += OnConnectivityStateChanged;
        Connectivity.BluetoothDeviceConnected += OnBluetoothDeviceConnected;
        Connectivity.OperationFailed += OnConnectivityOperationFailed;
        Clipboard.EntryCaptured += OnClipboardEntryCaptured;
        Widgets.WidgetsChanged += OnWidgetsChanged;
        Telemetry.TelemetryUpdated += OnTelemetryUpdated;
        LanDrop.FilesReceived += OnLanDropFilesReceived;
        Calendar.ActivityChanged += OnCalendarActivityChanged;
        Transfers.ActivityChanged += OnTransferActivityChanged;
        Transfers.TransferCompleted += OnTransferCompleted;
        VoiceNotes.ActivityChanged += OnVoiceNoteActivityChanged;
        VoiceNotes.NoteCompleted += OnVoiceNoteCompleted;
        VoiceNotes.RecordingFailed += OnVoiceNoteRecordingFailed;
        UsbDrives.DriveArrived += OnUsbDriveArrived;
        UsbDrives.DriveRemoved += OnUsbDriveRemoved;
        DeveloperBridge.NotificationReceived += OnDeveloperBridgeNotification;
    }

    private void OnConnectivityStateChanged(object? sender, ConnectivitySnapshot snap)
    {
        UpdateBluetoothBatteryStatus(snap, fireHudAlert: true);
    }

    private void UpdateBluetoothBatteryStatus(ConnectivitySnapshot snap, bool fireHudAlert)
    {
        if (!Config.BluetoothBatteryAlertsEnabled)
        {
            Coordinator.PublishLiveActivity(null, "bt.lowbattery");
            return;
        }

        int threshold = Config.BluetoothLowBatteryThreshold;
        BluetoothDeviceSnapshot? lowest = snap.PairedBluetoothDevices
            .Where(d => d.IsConnected && d.BatteryPercent.HasValue)
            .OrderBy(d => d.BatteryPercent!.Value)
            .FirstOrDefault();

        // Clear alert state for devices recharged above threshold + 5%
        foreach (var d in snap.PairedBluetoothDevices)
        {
            if (d.BatteryPercent is int pct && pct > threshold + 5)
            {
                lock (_alertedLowBatteryBtKeys) _alertedLowBatteryBtKeys.Remove(d.Name);
            }
        }

        if (lowest is null || lowest.BatteryPercent!.Value > threshold)
        {
            Coordinator.PublishLiveActivity(null, "bt.lowbattery");
            return;
        }

        int bat = lowest.BatteryPercent.Value;
        string glyph = BluetoothBatteryResolver.ClassifyDeviceIconGlyph(lowest.Name);
        bool critical = bat <= Math.Min(8, threshold / 2 + 2);
        Coordinator.PublishLiveActivity(new LiveActivityState(
            SourceId: "bt.lowbattery",
            Kind: ActivityKind.LowBattery,
            Priority: (int)ActivityKind.LowBattery,
            PrimaryText: $"{bat}%",
            SecondaryText: lowest.Name,
            AccentHex: critical ? "#FF453A" : "#FF9F0A",
            Progress01: bat / 100.0,
            IsCritical: critical,
            IconGlyph: glyph,
            TargetTab: ExpandedTabId.Control), "bt.lowbattery");

        bool firstAlert = false;
        if (fireHudAlert)
        {
            lock (_alertedLowBatteryBtKeys) firstAlert = _alertedLowBatteryBtKeys.Add(lowest.Name);
        }
        if (firstAlert)
        {
            Coordinator.Publish(new TransientHudEvent(
                CoalesceKey: "hud:bt-lowbat",
                Kind: HudKind.BluetoothLowBattery,
                Title: Strings.BtLowBatteryTitle(Config.Language),
                Subtitle: $"{lowest.Name} • {bat}%",
                Value01: bat / 100.0,
                AccentHex: critical ? "#FF453A" : "#FF9F0A",
                DurationMs: 2600,
                InteractiveOnHover: true,
                IsSticky: false,
                IconGlyph: glyph));
        }
    }

    private void OnBluetoothDeviceConnected(object? sender, BluetoothDeviceSnapshot dev)
    {
        string glyph = BluetoothBatteryResolver.ClassifyDeviceIconGlyph(dev.Name);
        string sub = dev.BatteryPercent.HasValue
            ? $"{dev.BatteryPercent.Value}% • {Strings.BatteryLabel(Config.Language)}"
            : Strings.BtConnectedTitle(Config.Language);
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: $"hud:bt-conn:{dev.Name}",
            Kind: HudKind.BluetoothConnect,
            Title: dev.Name,
            Subtitle: sub,
            Value01: dev.BatteryPercent.HasValue ? dev.BatteryPercent.Value / 100.0 : null,
            AccentHex: "#30D158",
            DurationMs: 2500,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: glyph));
    }

    private void OnLanDropFilesReceived(object? sender, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        Shelf.AddPaths(files);
        string label = files.Count == 1
            ? Path.GetFileName(files[0])
            : $"{Path.GetFileName(files[0])} (+{files.Count - 1})";
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:qr-drop",
            Kind: HudKind.QrShare,
            Title: Strings.QrUploadReceived(Config.Language),
            Subtitle: label,
            Value01: 1.0,
            AccentHex: "#30D158",
            DurationMs: 2400,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uED14"));
    }

    private void OnCalendarActivityChanged(object? sender, LiveActivityState? state) =>
        Coordinator.PublishLiveActivity(state, Calendar.SourceId);

    private void OnTransferActivityChanged(object? sender, LiveActivityState? state) =>
        Coordinator.PublishLiveActivity(state, Transfers.SourceId);

    private void OnTransferCompleted(object? sender, CompletedTransferEventArgs e)
    {
        _lastCompletedTransferPath = e.FinalPath;
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:transfer-done",
            Kind: HudKind.TransferComplete,
            Title: Strings.TransferCompleteTitle(Config.Language),
            Subtitle: $"{e.FileName} • {Strings.TransferStashAction(Config.Language)}",
            Value01: 1.0,
            AccentHex: "#30D158",
            DurationMs: 3200,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uE896"));
    }

    private void OnVoiceNoteActivityChanged(object? sender, LiveActivityState? state) =>
        Coordinator.PublishLiveActivity(state, VoiceNotes.SourceId);

    private void OnVoiceNoteCompleted(object? sender, VoiceNoteCompletedResult res)
    {
        Shelf.AddPaths([res.WavPath]);
        bool hasText = !string.IsNullOrWhiteSpace(res.Transcript);
        if (hasText)
        {
            Clipboard.AddTextEntry(res.Transcript!, "Voice", copyToSystemClipboard: true);
        }

        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:voicenote-done",
            Kind: HudKind.VoiceNote,
            Title: Strings.VoiceNoteSavedTitle(Config.Language),
            Subtitle: hasText ? Strings.VoiceNoteTranscriptSub(Config.Language) : Strings.VoiceNoteAudioOnlySub(Config.Language),
            Value01: 1.0,
            AccentHex: "#30D158",
            DurationMs: 2600,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uE720"));
    }

    private void OnVoiceNoteRecordingFailed(object? sender, string message)
    {
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:voicenote-err",
            Kind: HudKind.VoiceNote,
            Title: Strings.VoiceNoteRecording(Config.Language),
            Subtitle: message,
            Value01: null,
            AccentHex: "#FF453A",
            DurationMs: 2400,
            InteractiveOnHover: false,
            IsSticky: false,
            IconGlyph: "\uE720"));
    }

    public string? GetCurrentLyricLine(MediaPlaybackSnapshot media)
    {
        if (!Config.LyricsEnabled || !media.HasSession) return null;
        TimeSpan pos = media.Position;
        if (media.IsPlaying)
        {
            double dt = Math.Clamp((DateTimeOffset.UtcNow - media.LastUpdatedUtc).TotalSeconds, 0, 120);
            pos += TimeSpan.FromSeconds(dt * Math.Max(0.25, media.PlaybackRate));
        }
        return Lyrics.ResolveLine(media.Title, media.Artist, pos);
    }

    public LanDropShareInfo ToggleOrStartLanQrShare(bool forceStart = true)
    {
        if (!forceStart && LanDrop.CurrentShare is not null)
        {
            LanDrop.StopShare();
            return new LanDropShareInfo(string.Empty, string.Empty, false, 0, false, DateTimeOffset.UtcNow);
        }

        var files = Shelf.GetItems()
            .Select(i => i.FullPath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .ToList();
        string? topClipText = Config.LanShareIncludeClipboard
            ? Clipboard.GetEntries().FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.FullText))?.FullText
            : null;
        return LanDrop.StartShare(files, topClipText);
    }

    public void JoinUpcomingMeeting(bool toggleMicOnly = false)
    {
        if (toggleMicOnly)
        {
            Audio.SetMicMute(!Audio.Current.IsMicMuted);
            return;
        }

        var meeting = Calendar.ActiveOrNextMeeting;
        if (meeting is not null && !string.IsNullOrWhiteSpace(meeting.JoinUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(meeting.JoinUrl) { UseShellExecute = true });
            }
            catch { }
        }
        else
        {
            Coordinator.RequestNavigateTab(ExpandedTabId.Now);
        }

        // Remind/surface current mic mute status cleanly in S3 HUD when entering meeting
        var snap = Audio.Current;
        if (snap.HasMicDevice)
        {
            Coordinator.Publish(new TransientHudEvent(
                CoalesceKey: "hud:mic-mute",
                Kind: HudKind.MicMute,
                Title: snap.IsMicMuted
                    ? Strings.Pick(Config.Language, "Mikrofon Susturuldu", "Mic Muted")
                    : Strings.Pick(Config.Language, "Mikrofon Açık", "Mic Active"),
                Subtitle: meeting?.Summary ?? Strings.MeetingJoinTip(Config.Language),
                Value01: snap.IsMicMuted ? 0.0 : 1.0,
                AccentHex: snap.IsMicMuted ? "#FF9F0A" : "#30D158",
                DurationMs: 2200,
                InteractiveOnHover: true,
                IsSticky: false,
                IconGlyph: snap.IsMicMuted ? "\uEC54" : "\uE720"));
        }
    }

    public bool StashLastCompletedTransfer()
    {
        string? p = _lastCompletedTransferPath;
        if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) return false;
        Shelf.AddPaths([p!]);
        _lastCompletedTransferPath = null;
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:shelf-stashed",
            Kind: HudKind.FileStashed,
            Title: Strings.TransferStashAction(Config.Language),
            Subtitle: Path.GetFileName(p),
            Value01: 1.0,
            AccentHex: "#0A84FF",
            DurationMs: 1700,
            InteractiveOnHover: false,
            IsSticky: false,
            IconGlyph: "\uE8F1"));
        return true;
    }

    public void ToggleActiveWindowPin()
    {
        var (ok, isTopMost, title) = WindowControl.ToggleTopMost();
        if (!ok) return;
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:win-pin",
            Kind: HudKind.WindowPin,
            Title: isTopMost ? Strings.WindowPinnedHud(Config.Language) : Strings.WindowUnpinnedHud(Config.Language),
            Subtitle: title,
            Value01: isTopMost ? 1.0 : 0.0,
            AccentHex: isTopMost ? "#0A84FF" : "#8E8E93",
            DurationMs: 1600,
            InteractiveOnHover: false,
            IsSticky: false,
            IconGlyph: "\uE718"));
    }

    public void SwitchVirtualDesktopWithHud(bool next)
    {
        if (!WindowControl.SwitchVirtualDesktop(next)) return;
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:desktop-switch",
            Kind: HudKind.Focus,
            Title: next ? Strings.VirtualDesktopNext(Config.Language) : Strings.VirtualDesktopPrev(Config.Language),
            Subtitle: Strings.Pick(Config.Language, "Sanal Masaüstü", "Virtual Desktop"),
            Value01: next ? 1.0 : 0.0,
            AccentHex: "#5E5CE6",
            DurationMs: 900,
            InteractiveOnHover: false,
            IsSticky: false,
            IconGlyph: next ? "\uE76C" : "\uE76B"));
    }

    private void OnCoordinatorSnapshotChanged(object? sender, IslandCoordinatorSnapshot snap)
    {
        Widgets.SetPausedWhenCollapsed(snap.State != IslandPresentationState.S4Expanded);
    }

    private void OnFullscreenChanged(object? sender, FullscreenSnapshot snap)
    {
        Coordinator.UpdateFullscreenState(snap);
        ReconcileGameMode(snap);
    }

    private void OnMediaSnapshotChanged(object? sender, MediaPlaybackSnapshot snap)
    {
        if (snap.HasSession)
        {
            string subtitle = string.IsNullOrWhiteSpace(snap.Artist) ? snap.AppName : snap.Artist;
            double? progress = snap.Duration > TimeSpan.Zero
                ? Math.Clamp(snap.Position.TotalSeconds / snap.Duration.TotalSeconds, 0, 1)
                : null;

            Coordinator.PublishLiveActivity(new LiveActivityState(
                SourceId: "media.gsmtc",
                Kind: ActivityKind.Media,
                Priority: snap.IsPlaying ? 65 : 30,
                PrimaryText: snap.Title,
                SecondaryText: subtitle,
                AccentHex: snap.AccentHex,
                Progress01: progress,
                IsCritical: false,
                ThumbnailBytes: snap.ThumbnailBytes,
                IconGlyph: snap.IsPlaying ? "\uE768" : "\uE769",
                TargetTab: ExpandedTabId.Now), "media.gsmtc");
        }
        else
        {
            Coordinator.PublishLiveActivity(null, "media.gsmtc");
        }
    }

    private void OnTimerActivityChanged(object? sender, LiveActivityState? state) =>
        Coordinator.PublishLiveActivity(state, Timers.SourceId);

    private void OnPrivacyChanged(object? sender, PrivacySensorSnapshot snap)
    {
        bool showMic = Config.ShowMicPrivacyAlert && snap.IsMicActive;
        bool showCam = snap.IsCamActive;

        if (showMic || showCam)
        {
            string? who = (showCam ? snap.CamConsumers.FirstOrDefault() : null)
                          ?? (showMic ? snap.MicConsumers.FirstOrDefault() : null)
                          ?? Strings.Pick(Config.Language, "Uygulama", "App");
            string what = showCam && showMic
                ? Strings.Pick(Config.Language, "Kamera + Mikrofon", "Camera + Mic")
                : (showCam
                    ? Strings.Pick(Config.Language, "Kamera", "Camera")
                    : Strings.Pick(Config.Language, "Mikrofon", "Microphone"));

            Coordinator.PublishLiveActivity(new LiveActivityState(
                SourceId: "privacy.sensor",
                Kind: ActivityKind.PrivacySensor,
                Priority: 95,
                PrimaryText: Strings.Pick(Config.Language, $"{what} Aktif", $"{what} Active"),
                SecondaryText: who,
                AccentHex: showCam ? "#30D158" : "#FF9F0A",
                IsCritical: true,
                HasMicActive: showMic,
                HasCamActive: showCam,
                IconGlyph: showCam ? "\uE714" : "\uE720",
                TargetTab: ExpandedTabId.Audio), "privacy.sensor");
        }
        else
        {
            Coordinator.PublishLiveActivity(null, "privacy.sensor");
        }
    }

    private void OnAudioSnapshotChanged(object? sender, AudioMixerSnapshot snap)
    {
        UpdateMicMutedActivity(snap);

        int pct = (int)Math.Round(snap.MasterVolume01 * 100);
        if (_lastReportedVolumePct < 0)
        {
            _lastReportedVolumePct = pct;
            _lastReportedMute = snap.IsMasterMuted;
            _lastReportedMicMute = snap.IsMicMuted;
            _hasMicDeviceBaseline = snap.HasMicDevice;
            return;
        }

        if (pct != _lastReportedVolumePct || snap.IsMasterMuted != _lastReportedMute)
        {
            _lastReportedVolumePct = pct;
            _lastReportedMute = snap.IsMasterMuted;
            Coordinator.Publish(new TransientHudEvent(
                CoalesceKey: "hud:volume",
                Kind: HudKind.Volume,
                Title: snap.IsMasterMuted ? Strings.Pick(Config.Language, "Ses Kısıldı", "Muted") : Strings.Pick(Config.Language, "Ses", "Volume"),
                Subtitle: $"{pct}% • {snap.DefaultOutputName}",
                Value01: snap.IsMasterMuted ? 0 : snap.MasterVolume01,
                AccentHex: snap.IsMasterMuted ? "#FF453A" : "#0A84FF",
                DurationMs: 1600,
                InteractiveOnHover: true,
                IsSticky: false,
                IconGlyph: snap.IsMasterMuted ? "\uE74F" : "\uE767"));
        }

        if (snap.HasMicDevice)
        {
            if (_hasMicDeviceBaseline && snap.IsMicMuted != _lastReportedMicMute)
            {
                Coordinator.Publish(new TransientHudEvent(
                    CoalesceKey: "hud:mic-mute",
                    Kind: HudKind.MicMute,
                    Title: snap.IsMicMuted
                        ? Strings.Pick(Config.Language, "Mikrofon Susturuldu", "Mic Muted")
                        : Strings.Pick(Config.Language, "Mikrofon Açık", "Mic Active"),
                    Subtitle: snap.IsMicMuted
                        ? Strings.Pick(Config.Language, "Giriş sesi kapatıldı", "Input muted")
                        : Strings.Pick(Config.Language, "Giriş sesi etkin", "Input unmuted"),
                    Value01: snap.IsMicMuted ? 0.0 : 1.0,
                    AccentHex: snap.IsMicMuted ? "#FF453A" : "#30D158",
                    DurationMs: 1500,
                    InteractiveOnHover: false,
                    IsSticky: false,
                    IconGlyph: snap.IsMicMuted ? "\uEC54" : "\uE720"));
            }

            _lastReportedMicMute = snap.IsMicMuted;
            _hasMicDeviceBaseline = true;
        }
        else
        {
            _hasMicDeviceBaseline = false;
        }
    }

    private void OnBrightnessAdjustedByUser(object? sender, (string DeviceId, string FriendlyName, int Percent, bool IsSoftwareDimmer) info)
    {
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:brightness",
            Kind: HudKind.Brightness,
            Title: info.IsSoftwareDimmer
                ? Strings.Pick(Config.Language, "Yazılım Karartma", "Software Dimmer")
                : Strings.Pick(Config.Language, "Parlaklık", "Brightness"),
            Subtitle: $"{info.Percent}% • {info.FriendlyName}",
            Value01: info.Percent / 100.0,
            AccentHex: info.IsSoftwareDimmer ? "#FF9F0A" : "#FFD60A",
            DurationMs: 1500,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uE706"));
    }

    private void OnConnectivityOperationFailed(object? sender, string message)
    {
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:connectivity-error",
            Kind: HudKind.NotificationPulse,
            Title: Strings.Pick(Config.Language, "Sistem Denetimi Uyarısı", "System Control Warning"),
            Subtitle: message,
            Value01: null,
            AccentHex: "#FF453A",
            DurationMs: 2600,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uE783"));
    }

    private void OnAudioActionFailed(object? sender, string message)
    {
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:audio-error",
            Kind: HudKind.NotificationPulse,
            Title: Strings.Pick(Config.Language, "Ses Aygıtı Uyarısı", "Audio Device Warning"),
            Subtitle: message,
            Value01: null,
            AccentHex: "#FF453A",
            DurationMs: 2600,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uE767"));
    }

    private void OnClipboardEntryCaptured(object? sender, ClipboardEntry entry)
    {
        if (!Config.EnableClipboardVault) return;
        string label = string.IsNullOrWhiteSpace(entry.PreviewText) ? entry.Kind : entry.PreviewText;
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:clipboard",
            Kind: HudKind.Clipboard,
            Title: Strings.Pick(Config.Language, "Panoya Eklendi", "Copied to Vault"),
            Subtitle: label,
            Value01: null,
            AccentHex: "#64D2FF",
            DurationMs: 1300,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uE77F"));
    }

    private void OnWidgetsChanged(object? sender, IReadOnlyList<WidgetRenderSnapshot> widgets) =>
        UpdateWidgetPillActivity(widgets);

    private void UpdateWidgetPillActivity(IReadOnlyList<WidgetRenderSnapshot> widgets)
    {
        var badged = widgets.FirstOrDefault(w => w.ShowPillBadge && !w.IsFaulted);
        if (badged is null)
        {
            Coordinator.PublishLiveActivity(null, "widgets.pill");
            return;
        }

        double? progress01 = badged.Progress0To100.HasValue
            ? Math.Clamp(badged.Progress0To100.Value / 100.0, 0.0, 1.0)
            : null;
        string primary = !string.IsNullOrWhiteSpace(badged.PillLabel) ? badged.PillLabel! : badged.Title;
        string secondary = string.IsNullOrWhiteSpace(badged.Subtitle) ? badged.Name : badged.Subtitle;
        string accent = string.IsNullOrWhiteSpace(badged.AccentHex) ? "#5E5CE6" : badged.AccentHex;

        Coordinator.PublishLiveActivity(new LiveActivityState(
            SourceId: "widgets.pill",
            Kind: ActivityKind.CustomWidget,
            Priority: (int)ActivityKind.CustomWidget,
            PrimaryText: primary,
            SecondaryText: secondary,
            AccentHex: accent,
            Progress01: progress01,
            IconGlyph: "\uE8A9",
            TargetTab: ExpandedTabId.Widgets), "widgets.pill");
    }

    private void OnTelemetryUpdated(object? sender, SystemTelemetrySnapshot snap)
    {
        if (Config.ShowTelemetryInPill)
        {
            Coordinator.PublishLiveActivity(new LiveActivityState(
                SourceId: "telemetry.pill",
                Kind: ActivityKind.Telemetry,
                Priority: 15,
                PrimaryText: $"CPU {snap.CpuPercent:F0}% • RAM {snap.MemoryPercent:F0}%",
                SecondaryText: $"↓{snap.NetDownMbps:F1} Mbps",
                AccentHex: "#30D158",
                Progress01: snap.CpuPercent / 100.0,
                TargetTab: ExpandedTabId.System), "telemetry.pill");
        }
        else
        {
            Coordinator.PublishLiveActivity(null, "telemetry.pill");
        }

        if (_isGameModeActive && Config.EnableGameModeAutomation && Config.GameModeShowPingPill)
        {
            PublishGamePingPill(snap, FullscreenDetector.Current.ProcessName);
        }
        else
        {
            Coordinator.PublishLiveActivity(null, "game.ping");
        }
    }

    public void UpdateTelemetryUiState(bool isSystemTabVisible, bool isDormantTelemetryPillVisible)
    {
        if (_disposed) return;
        _isSystemTabVisible = isSystemTabVisible;
        _isDormantTelemetryPillVisible = isDormantTelemetryPillVisible;
        UpdateTelemetrySamplingGates();
    }

    private void UpdateTelemetrySamplingGates()
    {
        bool gamePingActive = _isGameModeActive && Config.EnableGameModeAutomation && Config.GameModeShowPingPill;
        Telemetry.SetSamplingActive(_isSystemTabVisible || _isDormantTelemetryPillVisible || gamePingActive);
        Telemetry.SetExtendedMetricsActive(_isSystemTabVisible);
        Telemetry.SetPingMonitorActive(_isSystemTabVisible || gamePingActive);
    }

    public void SetAndPersistAppOutputDevice(string processName, string? deviceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(processName)) return;
        string cleanName = processName.Trim();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            Config.AppAudioRoutes.Remove(cleanName);
        }
        else
        {
            Config.AppAudioRoutes[cleanName] = deviceId.Trim();
        }

        Config.Sanitize();
        ConfigStore.Save(Config);
        Audio.SyncAppRoutingRules(Config.AppAudioRoutes);
        Audio.SetAppOutputDevice(cleanName, string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim());
    }

    public void ToggleMicMuteWithHud()
    {
        if (_disposed) return;
        var snap = Audio.Current;
        if (!snap.HasMicDevice) return;
        Audio.SetMicMute(!snap.IsMicMuted);
    }

    public void HandlePickedColor(string hex, string rgbText, string hslText)
    {
        if (_disposed || string.IsNullOrWhiteSpace(hex)) return;
        Clipboard.CopyTextToClipboard(hex);
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:color-picked",
            Kind: HudKind.ColorPicked,
            Title: $"{Strings.ColorPickerTitle(Config.Language)} • {hex}",
            Subtitle: $"{rgbText} • {hslText}",
            Value01: null,
            AccentHex: hex,
            DurationMs: 5500,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uEF3C",
            PrimaryActionId: "color.copy-rgb",
            PrimaryActionLabel: "RGB",
            SecondaryActionId: "color.copy-hsl",
            SecondaryActionLabel: "HSL",
            ActionPayload: $"{rgbText}|{hslText}"));
    }

    public void OpenUsbDriveFolder(string? driveRoot)
    {
        if (_disposed || string.IsNullOrWhiteSpace(driveRoot)) return;
        _ = UsbDrives.OpenDriveFolder(driveRoot);
    }

    public async Task SafeEjectUsbDriveAsync(string? driveRoot)
    {
        if (_disposed || string.IsNullOrWhiteSpace(driveRoot)) return;
        var (ok, err) = await UsbDrives.SafeEjectAsync(driveRoot).ConfigureAwait(false);
        if (_disposed) return;

        string driveLabel = UsbDriveMonitorService.TryNormalizeDriveRoot(driveRoot, out _, out string letter)
            ? letter
            : driveRoot.Trim();

        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: $"hud:usb-eject:{driveLabel}",
            Kind: HudKind.UsbDrive,
            Title: ok ? Strings.UsbEjectedTitle(Config.Language) : Strings.UsbEjectFailedTitle(Config.Language),
            Subtitle: ok ? driveLabel : (string.IsNullOrWhiteSpace(err) ? driveLabel : err),
            Value01: ok ? 1.0 : 0.0,
            AccentHex: ok ? "#30D158" : "#FF453A",
            DurationMs: ok ? 2400 : 3800,
            InteractiveOnHover: false,
            IsSticky: false,
            IconGlyph: "\uE88E"));
    }

    public bool SnapForegroundWindow(WindowSnapPreset preset)
    {
        if (_disposed) return false;
        return WindowControl.TrySnapForegroundWindow(preset, MonitorLayout.GetMonitors(), IntPtr.Zero);
    }

    private void UpdateMicMutedActivity(AudioMixerSnapshot snap)
    {
        if (snap.HasMicDevice && snap.IsMicMuted)
        {
            Coordinator.PublishLiveActivity(new LiveActivityState(
                SourceId: "audio.micmuted",
                Kind: ActivityKind.MicMuted,
                Priority: (int)ActivityKind.MicMuted,
                PrimaryText: Strings.MicMutedBadge(Config.Language),
                SecondaryText: Strings.Pick(Config.Language, "Mikrofon susturuldu", "Mic input muted"),
                AccentHex: "#FF453A",
                IconGlyph: "\uEC54",
                IsCritical: true,
                TargetTab: ExpandedTabId.Audio), "audio.micmuted");
        }
        else
        {
            Coordinator.PublishLiveActivity(null, "audio.micmuted");
        }
    }

    private void ReconcileGameMode(FullscreenSnapshot snap)
    {
        if (_disposed) return;
        bool inGame = Config.EnableGameModeAutomation && snap.IsFullscreen && snap.Kind == FullscreenAppKind.Game;

        bool entering = false;
        bool exiting = false;
        string? restoreGuid = null;
        lock (_gameModeSync)
        {
            if (inGame && !_isGameModeActive)
            {
                _isGameModeActive = true;
                entering = true;
                if (Connectivity.TrySwitchToHighPerformance(out string? prev))
                {
                    _savedPreGamePowerSchemeGuid = prev;
                }
            }
            else if (!inGame && _isGameModeActive)
            {
                _isGameModeActive = false;
                exiting = true;
                restoreGuid = _savedPreGamePowerSchemeGuid;
                _savedPreGamePowerSchemeGuid = null;
            }
        }

        if (entering)
        {
            UpdateTelemetrySamplingGates();
            if (!Config.GameModeHideIsland && Config.GameModeShowPingPill)
            {
                PublishGamePingPill(Telemetry.Current, snap.ProcessName);
            }

            if (!Config.GameModeHideIsland)
            {
                Coordinator.Publish(new TransientHudEvent(
                    CoalesceKey: "hud:gamemode",
                    Kind: HudKind.GameMode,
                    Title: Strings.GameModeEnteredTitle(Config.Language),
                    Subtitle: string.IsNullOrWhiteSpace(snap.ProcessName) ? Strings.Pick(Config.Language, "Yüksek Performans", "High Performance") : snap.ProcessName,
                    Value01: null,
                    AccentHex: "#30D158",
                    DurationMs: 2600,
                    InteractiveOnHover: false,
                    IsSticky: false,
                    IconGlyph: "\uE7FC"));
            }
        }
        else if (exiting)
        {
            Coordinator.PublishLiveActivity(null, "game.ping");
            UpdateTelemetrySamplingGates();

            if (!string.IsNullOrWhiteSpace(restoreGuid))
            {
                _ = Connectivity.TryRestorePowerScheme(restoreGuid);
            }

            Coordinator.Publish(new TransientHudEvent(
                CoalesceKey: "hud:gamemode",
                Kind: HudKind.GameMode,
                Title: Strings.GameModeExitedTitle(Config.Language),
                Subtitle: Strings.Pick(Config.Language, "Oyun modu kapatıldı", "Game mode ended"),
                Value01: null,
                AccentHex: "#0A84FF",
                DurationMs: 2200,
                InteractiveOnHover: false,
                IsSticky: false,
                IconGlyph: "\uE7FC"));
        }
        else if (inGame && Config.GameModeShowPingPill && !Config.GameModeHideIsland)
        {
            PublishGamePingPill(Telemetry.Current, snap.ProcessName);
        }
        else if (inGame && (Config.GameModeHideIsland || !Config.GameModeShowPingPill))
        {
            Coordinator.PublishLiveActivity(null, "game.ping");
        }
    }

    private void PublishGamePingPill(SystemTelemetrySnapshot snap, string? gameExe)
    {
        if (Config.GameModeHideIsland || !Config.GameModeShowPingPill)
        {
            Coordinator.PublishLiveActivity(null, "game.ping");
            return;
        }

        string pingText = snap.PingMs.HasValue
            ? $"{Math.Round(snap.PingMs.Value):F0} ms"
            : "... ms";
        string accent = snap.PingMs switch
        {
            <= 45 => "#30D158",
            <= 90 => "#FF9F0A",
            > 90 => "#FF453A",
            _ => "#64D2FF"
        };

        Coordinator.PublishLiveActivity(new LiveActivityState(
            SourceId: "game.ping",
            Kind: ActivityKind.Telemetry,
            Priority: 48,
            PrimaryText: pingText,
            SecondaryText: string.IsNullOrWhiteSpace(gameExe) ? Config.PingHost : gameExe,
            AccentHex: accent,
            IconGlyph: "\uE968",
            TargetTab: ExpandedTabId.System), "game.ping");
    }

    private void OnUsbDriveArrived(object? sender, UsbDriveArrivalInfo info)
    {
        if (_disposed || !Config.UsbDriveHudEnabled) return;
        string freeText = Strings.Pick(
            Config.Language,
            $"{info.FreeGb:F1} GB boş / {info.TotalGb:F1} GB",
            $"{info.FreeGb:F1} GB free / {info.TotalGb:F1} GB");

        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: $"hud:usb:{info.DriveLetter}",
            Kind: HudKind.UsbDrive,
            Title: $"{Strings.UsbConnectedTitle(Config.Language)} • {info.DisplayName}",
            Subtitle: freeText,
            Value01: info.TotalGb > 0 ? Math.Clamp(info.UsedGb / info.TotalGb, 0.0, 1.0) : null,
            AccentHex: "#30D158",
            DurationMs: 6500,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uE88E",
            PrimaryActionId: "usb.open",
            PrimaryActionLabel: Strings.UsbOpenFolder(Config.Language),
            SecondaryActionId: info.IsRemovable ? "usb.eject" : null,
            SecondaryActionLabel: info.IsRemovable ? Strings.UsbSafeEject(Config.Language) : null,
            ActionPayload: info.DriveRoot));
    }

    private void OnUsbDriveRemoved(object? sender, string driveRoot)
    {
        if (_disposed) return;
        if (UsbDriveMonitorService.TryNormalizeDriveRoot(driveRoot, out _, out string letter) &&
            string.Equals(Coordinator.Current.ActiveHud?.CoalesceKey, $"hud:usb:{letter}", StringComparison.OrdinalIgnoreCase))
        {
            Coordinator.DismissTransientHud();
        }
    }

    private void OnDeveloperBridgeNotification(DevBridgeNotification msg)
    {
        if (_disposed || !Config.EnableDeveloperBridge) return;
        string status = msg.Status?.Trim().ToLowerInvariant() ?? "info";
        (string accent, string glyph) = status switch
        {
            "success" or "ok" => ("#30D158", "\uE73E"),
            "error" => ("#FF453A", "\uEA39"),
            "warning" => ("#FF9F0A", "\uE7BA"),
            "progress" => ("#0A84FF", "\uE896"),
            _ => ("#64D2FF", "\uE756")
        };

        if (msg.Sound)
        {
            try
            {
                if (status == "error")
                    System.Media.SystemSounds.Hand.Play();
                else
                    System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        if (status == "progress")
        {
            // Bridge clients are served concurrently; swap the token source atomically so one thread
            // never disposes a CTS that another is cancelling.
            var cts = new CancellationTokenSource();
            CancellationTokenSource? previous;
            lock (_devBuildSync)
            {
                previous = _devBuildClearCts;
                _devBuildClearCts = cts;
            }
            previous?.Cancel();
            previous?.Dispose();

            Coordinator.PublishLiveActivity(new LiveActivityState(
                SourceId: "dev.bridge.build",
                Kind: ActivityKind.DevBuild,
                Priority: (int)ActivityKind.DevBuild,
                PrimaryText: msg.Title,
                SecondaryText: msg.Subtitle,
                AccentHex: accent,
                Progress01: msg.Progress01,
                IconGlyph: glyph,
                TargetTab: ExpandedTabId.Now), "dev.bridge.build");

            _ = Task.Delay(TimeSpan.FromSeconds(30), cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled && !_disposed)
                {
                    Coordinator.PublishLiveActivity(null, "dev.bridge.build");
                }
            }, TaskScheduler.Default);
            return;
        }

        CancellationTokenSource? pendingClear;
        lock (_devBuildSync)
        {
            pendingClear = _devBuildClearCts;
            _devBuildClearCts = null;
        }
        pendingClear?.Cancel();
        pendingClear?.Dispose();
        Coordinator.PublishLiveActivity(null, "dev.bridge.build");

        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:dev-bridge",
            Kind: HudKind.DevBridge,
            Title: msg.Title,
            Subtitle: msg.Subtitle,
            Value01: msg.Progress01,
            AccentHex: accent,
            DurationMs: status == "error" ? 5000 : 3800,
            InteractiveOnHover: false,
            IsSticky: false,
            IconGlyph: glyph));
    }

    private void OnScheduledPowerWarningReached(object? sender, ScheduledPowerSnapshot snap)
    {
        if (_disposed || !snap.IsActive) return;
        string actionName = FormatScheduledPowerKind(snap.Kind, Config.Language);
        int secs = Math.Max(1, (int)Math.Ceiling(snap.Remaining.TotalSeconds));
        Coordinator.Publish(new TransientHudEvent(
            CoalesceKey: "hud:power-sched-warn",
            Kind: HudKind.ScheduledPower,
            Title: $"{Strings.ScheduledWarningTitle(Config.Language)} • {actionName}",
            Subtitle: Strings.Pick(Config.Language, $"{secs} sn kaldı", $"{secs}s remaining"),
            Value01: snap.Total.TotalSeconds > 0 ? Math.Clamp(snap.Remaining.TotalSeconds / snap.Total.TotalSeconds, 0.0, 1.0) : 0.1,
            AccentHex: "#FF9F0A",
            DurationMs: 10000,
            InteractiveOnHover: true,
            IsSticky: true,
            IconGlyph: "\uE7E8",
            PrimaryActionId: "power.snooze10",
            PrimaryActionLabel: Strings.Snooze10mBtn(Config.Language),
            SecondaryActionId: "power.cancel",
            SecondaryActionLabel: Strings.Close(Config.Language)));
    }

    private void OnScheduledPowerDue(object? sender, ScheduledPowerActionKind kind)
    {
        if (_disposed) return;
        if (string.Equals(Coordinator.Current.ActiveHud?.CoalesceKey, "hud:power-sched-warn", StringComparison.Ordinal))
        {
            Coordinator.DismissTransientHud();
        }

        switch (kind)
        {
            case ScheduledPowerActionKind.Sleep:
                Connectivity.SleepWorkstation();
                break;
            case ScheduledPowerActionKind.Shutdown:
                if (!Connectivity.TryShutdownWorkstation(out string? err) && !string.IsNullOrWhiteSpace(err))
                {
                    OnConnectivityOperationFailed(this, err);
                }
                break;
            case ScheduledPowerActionKind.Lock:
                Connectivity.LockWorkstation();
                break;
            case ScheduledPowerActionKind.PauseMedia:
                if (Media.Current.IsPlaying)
                {
                    _ = Media.TogglePlayPauseAsync();
                }
                Coordinator.Publish(new TransientHudEvent(
                    CoalesceKey: "hud:power-sched-done",
                    Kind: HudKind.ScheduledPower,
                    Title: Strings.ScheduledPauseMedia(Config.Language),
                    Subtitle: Strings.Pick(Config.Language, "Oynatma duraklatıldı", "Playback paused"),
                    Value01: 0.0,
                    AccentHex: "#5E5CE6",
                    DurationMs: 2400,
                    InteractiveOnHover: false,
                    IsSticky: false,
                    IconGlyph: "\uE769"));
                break;
        }
    }

    private static string FormatScheduledPowerKind(ScheduledPowerActionKind kind, string lang) => kind switch
    {
        ScheduledPowerActionKind.Sleep => Strings.ScheduledSleep(lang),
        ScheduledPowerActionKind.Shutdown => Strings.ScheduledShutdown(lang),
        ScheduledPowerActionKind.Lock => Strings.ScheduledLock(lang),
        ScheduledPowerActionKind.PauseMedia => Strings.ScheduledPauseMedia(lang),
        _ => Strings.ScheduledPowerHeader(lang)
    };

    public Task RunBuiltinActionAsync(string actionKey)
    {
        switch (actionKey)
        {
            case "timer:pomo":
                Timers.StartCountdown("Pomodoro", TimeSpan.FromMinutes(25), isPomodoro: true);
                Coordinator.RequestNavigateTab(ExpandedTabId.Now);
                break;
            case "timer:5m":
                Timers.StartCountdown(Strings.Pick(Config.Language, "5 Dakika", "5 Minutes"), TimeSpan.FromMinutes(5), isPomodoro: false);
                Coordinator.RequestNavigateTab(ExpandedTabId.Now);
                break;
            case "toggle:quiet":
                bool nextQuiet = !Config.QuietIslandEnabled;
                Coordinator.SetQuietIsland(nextQuiet);
                ConfigStore.Save(Config);
                break;
            case "toggle:caffeine":
                Connectivity.ToggleCaffeine();
                break;
            case "toggle:theme":
                return Connectivity.ToggleDarkThemeAsync();
            case "toggle:wifi":
                return Connectivity.ToggleWifiAsync();
            case "toggle:bt":
                return Connectivity.ToggleBluetoothAsync();
            case "system:snip":
                Connectivity.LaunchScreenSnip();
                break;
            case "system:ocr":
                if (RegionOcrRequested is not null)
                {
                    return RegionOcrRequested.Invoke();
                }
                break;
            case "system:eyedropper":
                if (RegionColorPickRequested is not null)
                {
                    return RegionColorPickRequested.Invoke();
                }
                break;
            case "audio:mic-mute":
                ToggleMicMuteWithHud();
                break;
            case "shelf:qr":
                if (QrShareRequested is not null)
                    QrShareRequested.Invoke(this, EventArgs.Empty);
                else
                {
                    ToggleOrStartLanQrShare(forceStart: true);
                    Coordinator.RequestNavigateTab(ExpandedTabId.Shelf);
                }
                break;
            case "voice:toggle":
                VoiceNotes.ToggleRecording();
                break;
            case "win:topmost":
                ToggleActiveWindowPin();
                break;
            case "win:snap-left":
                SnapForegroundWindow(WindowSnapPreset.LeftHalf);
                break;
            case "win:snap-right":
                SnapForegroundWindow(WindowSnapPreset.RightHalf);
                break;
            case "win:snap-center":
                SnapForegroundWindow(WindowSnapPreset.CenterFocus70);
                break;
            case "win:next-monitor":
                SnapForegroundWindow(WindowSnapPreset.MoveToNextMonitor);
                break;
            case "desktop:prev":
                SwitchVirtualDesktopWithHud(next: false);
                break;
            case "desktop:next":
                SwitchVirtualDesktopWithHud(next: true);
                break;
            case "power:sched-sleep-30m":
                Timers.StartScheduledPowerAction(ScheduledPowerActionKind.Sleep, TimeSpan.FromMinutes(30));
                Coordinator.RequestNavigateTab(ExpandedTabId.Control);
                break;
            case "power:sched-pause-30m":
                Timers.StartScheduledPowerAction(ScheduledPowerActionKind.PauseMedia, TimeSpan.FromMinutes(30));
                Coordinator.RequestNavigateTab(ExpandedTabId.Control);
                break;
            case "power:sched-cancel":
                Timers.CancelScheduledPowerAction();
                break;
            case "meeting:join":
                JoinUpcomingMeeting();
                break;
            case "transfer:stash-last":
                StashLastCompletedTransfer();
                break;
            case "system:lock":
                Connectivity.LockWorkstation();
                break;
            case "open:settings":
                OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
        return Task.CompletedTask;
    }

    public void SaveConfig()
    {
        Config.Sanitize();
        ConfigStore.Save(Config);
        ApplyRuntimeConfig();
        _ = Telemetry.SampleOnceAsync();
        _ = Notifications.RefreshOsMirrorAsync();
    }

    private void MaintenanceTick()
    {
        if (_disposed) return;

        Coordinator.TickExpirations();
        WindowControl.CaptureForegroundIfExternal();

        if (_keyboardSyncPrimed)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                if (dispatcher.CheckAccess())
                {
                    PollLockKeyStates();
                }
                else
                {
                    _ = dispatcher.BeginInvoke(new Action(PollLockKeyStates));
                }
            }
            else
            {
                PollLockKeyStates();
            }
        }

        void PollLockKeyStates()
        {
            if (_disposed) return;

            bool caps = (NativeMethods.GetKeyState(0x14) & 1) != 0;
            if (caps != _lastCapsState)
            {
                _lastCapsState = caps;
                Coordinator.Publish(new TransientHudEvent(
                    CoalesceKey: "hud:caps",
                    Kind: HudKind.CapsLock,
                    Title: "Caps Lock",
                    Subtitle: caps ? Strings.Pick(Config.Language, "Açık", "On") : Strings.Pick(Config.Language, "Kapalı", "Off"),
                    Value01: caps ? 1.0 : 0.0,
                    AccentHex: caps ? "#FF9F0A" : "#8E8E93",
                    DurationMs: 1200,
                    InteractiveOnHover: false,
                    IsSticky: false,
                    IconGlyph: "\uE751"));
            }

            bool num = (NativeMethods.GetKeyState(0x90) & 1) != 0;
            if (num != _lastNumState)
            {
                _lastNumState = num;
                Coordinator.Publish(new TransientHudEvent(
                    CoalesceKey: "hud:num",
                    Kind: HudKind.NumLock,
                    Title: "Num Lock",
                    Subtitle: num ? Strings.Pick(Config.Language, "Açık", "On") : Strings.Pick(Config.Language, "Kapalı", "Off"),
                    Value01: num ? 1.0 : 0.0,
                    AccentHex: num ? "#0A84FF" : "#8E8E93",
                    DurationMs: 1200,
                    InteractiveOnHover: false,
                    IsSticky: false,
                    IconGlyph: "\uE8EF"));
            }
        }

        // Every ~1.6s (4th 400ms tick), run lightweight Win32 fullscreen/Focus Assist reconciliation off the timer callback.
        if (Interlocked.Increment(ref _maintenanceTickCounter) % 4 == 0 &&
            Interlocked.CompareExchange(ref _bgPollInFlight, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (_disposed) return;
                    FullscreenDetector.RefreshNow();

                    var focus = WnfFocusParser.QueryWindowsFocusProfile();
                    if (focus != FocusQuietProfile.Unknown)
                    {
                        if (_lastFocusProfile != FocusQuietProfile.Unknown && focus != _lastFocusProfile)
                        {
                            string focusSub = focus switch
                            {
                                FocusQuietProfile.PriorityOnly => Strings.Pick(Config.Language, "Yalnızca Öncelikli", "Priority Only"),
                                FocusQuietProfile.AlarmsOnly => Strings.Pick(Config.Language, "Yalnızca Alarmlar", "Alarms Only"),
                                _ => Strings.Pick(Config.Language, "Kapalı", "Off")
                            };

                            Coordinator.Publish(new TransientHudEvent(
                                CoalesceKey: "hud:focus",
                                Kind: HudKind.Focus,
                                Title: Strings.Pick(Config.Language, "Odak Yardımı", "Focus Assist"),
                                Subtitle: focusSub,
                                Value01: focus == FocusQuietProfile.Off ? 0.0 : 1.0,
                                AccentHex: focus == FocusQuietProfile.Off ? "#8E8E93" : "#5E5CE6",
                                DurationMs: 1700,
                                InteractiveOnHover: false,
                                IsSticky: false,
                                IconGlyph: "\uE708"));
                        }

                        _lastFocusProfile = focus;
                    }
                }
                catch
                {
                    // Background safety guard.
                }
                finally
                {
                    Volatile.Write(ref _bgPollInFlight, 0);
                }
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _maintenanceTimer?.Dispose();
        _maintenanceTimer = null;
        _bgCts?.Cancel();
        _bgCts?.Dispose();
        _bgCts = null;

        _devBuildClearCts?.Cancel();
        _devBuildClearCts?.Dispose();
        _devBuildClearCts = null;

        if (_isGameModeActive && !string.IsNullOrWhiteSpace(_savedPreGamePowerSchemeGuid))
        {
            Connectivity.TryRestorePowerScheme(_savedPreGamePowerSchemeGuid);
            _savedPreGamePowerSchemeGuid = null;
            _isGameModeActive = false;
        }

        Coordinator.SnapshotChanged -= OnCoordinatorSnapshotChanged;
        FullscreenDetector.FullscreenChanged -= OnFullscreenChanged;
        Media.SnapshotChanged -= OnMediaSnapshotChanged;
        Timers.ActivityChanged -= OnTimerActivityChanged;
        Timers.ScheduledPowerWarningReached -= OnScheduledPowerWarningReached;
        Timers.ScheduledPowerDue -= OnScheduledPowerDue;
        Privacy.PrivacyChanged -= OnPrivacyChanged;
        Audio.SnapshotChanged -= OnAudioSnapshotChanged;
        Audio.AudioActionFailed -= OnAudioActionFailed;
        Brightness.BrightnessAdjustedByUser -= OnBrightnessAdjustedByUser;
        Connectivity.StateChanged -= OnConnectivityStateChanged;
        Connectivity.BluetoothDeviceConnected -= OnBluetoothDeviceConnected;
        Connectivity.OperationFailed -= OnConnectivityOperationFailed;
        Clipboard.EntryCaptured -= OnClipboardEntryCaptured;
        Widgets.WidgetsChanged -= OnWidgetsChanged;
        Telemetry.TelemetryUpdated -= OnTelemetryUpdated;
        LanDrop.FilesReceived -= OnLanDropFilesReceived;
        Calendar.ActivityChanged -= OnCalendarActivityChanged;
        Transfers.ActivityChanged -= OnTransferActivityChanged;
        Transfers.TransferCompleted -= OnTransferCompleted;
        VoiceNotes.ActivityChanged -= OnVoiceNoteActivityChanged;
        VoiceNotes.NoteCompleted -= OnVoiceNoteCompleted;
        VoiceNotes.RecordingFailed -= OnVoiceNoteRecordingFailed;
        UsbDrives.DriveArrived -= OnUsbDriveArrived;
        UsbDrives.DriveRemoved -= OnUsbDriveRemoved;
        DeveloperBridge.NotificationReceived -= OnDeveloperBridgeNotification;

        Shelf.ClearShareWindow();

        DeveloperBridge.Dispose();
        AiQuota.Dispose();
        LanDrop.Dispose();
        Calendar.Dispose();
        Transfers.Dispose();
        VoiceNotes.Dispose();
        Widgets.Dispose();
        Telemetry.Dispose();
        Timers.Dispose();
        Clipboard.Dispose();
        Notifications.Dispose();
        Privacy.Dispose();
        Connectivity.Dispose();
        if (FullscreenDetector is IDisposable fdd) fdd.Dispose();

        await Brightness.DisposeAsync().ConfigureAwait(false);
        await Media.DisposeAsync().ConfigureAwait(false);
        await Audio.DisposeAsync().ConfigureAwait(false);
    }
}
