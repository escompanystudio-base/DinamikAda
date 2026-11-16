using System.Collections.Concurrent;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Config;

namespace DinamikAda.Core.Arbitration;

public sealed record IslandCoordinatorSnapshot(
    IslandPresentationState State,
    IslandLayoutTarget Layout,
    ExpandedTabId ActiveTab,
    LiveActivityState? PrimaryActivity,
    LiveActivityState? SatelliteActivity,
    TransientHudEvent? ActiveHud,
    bool IsPinnedOpen,
    bool IsDragCatcherArmed,
    bool IsQuietIsland,
    FullscreenSnapshot Fullscreen);

public sealed class IslandCoordinator : ITransientHudBus, IIslandHostContext
{
    private readonly ConcurrentDictionary<string, LiveActivityState> _activities = new();
    private readonly object _sync = new();
    private readonly AppConfig _config;

    private ExpandedTabId _activeTab = ExpandedTabId.Now;
    private bool _isExpanded;
    private bool _isPinnedOpen;
    private bool _isCommandOpen;
    private bool _isDragActive;
    private bool _isDragCatcherArmed;
    private bool _isHudPromotedInteractive;
    private bool _isHudHovered;
    private long _islandMonitorHandle;
    private TransientHudEvent? _activeHud;
    private DateTimeOffset _hudExpiresUtc;
    private FullscreenSnapshot _fullscreen = new(false, FullscreenAppKind.None, null, 0, "Init");
    private IslandCoordinatorSnapshot _current;

    public TimeProvider TimeProvider { get; }
    public ITransientHudBus HudBus => this;

    public event EventHandler<TransientHudEvent>? HudRequested;
    public event EventHandler<IslandCoordinatorSnapshot>? SnapshotChanged;
    public event EventHandler<ExpandedTabId>? TabRequested;

    public IslandCoordinator(AppConfig config, TimeProvider? timeProvider = null)
    {
        _config = config;
        TimeProvider = timeProvider ?? TimeProvider.System;
        if (_config.PinnedTabs is { Count: > 0 } && !_config.PinnedTabs.Contains(_activeTab))
        {
            _activeTab = _config.PinnedTabs[0];
        }
        _current = ComputeSnapshotLocked();
    }

    public IslandCoordinatorSnapshot Current
    {
        get { lock (_sync) return _current; }
    }

    public ExpandedTabId ActiveTab
    {
        get { lock (_sync) return _activeTab; }
    }

    public void PublishLiveActivity(LiveActivityState? state, string sourceId)
    {
        if (state is null)
        {
            _activities.TryRemove(sourceId, out _);
        }
        else
        {
            _activities[sourceId] = state;
        }

        Recompute();
    }

    public void Publish(TransientHudEvent hudEvent)
    {
        HudRequested?.Invoke(this, hudEvent);

        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (_config.QuietIslandEnabled && IsQuietSuppressed(hudEvent.Kind))
            {
                return;
            }

            if (EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                return;
            }

            if (_isCommandOpen || _isDragActive)
            {
                return;
            }

            // While Expanded Hub is already open, live bindings already show volume/mic/brightness; do not queue a stale overlay HUD.
            if (_isExpanded && hudEvent.Kind is HudKind.Volume or HudKind.MicMute or HudKind.Brightness)
            {
                return;
            }

            bool sameKey = _activeHud is not null && string.Equals(_activeHud.CoalesceKey, hudEvent.CoalesceKey, StringComparison.Ordinal);
            _activeHud = hudEvent;
            _isHudPromotedInteractive = sameKey && _isHudPromotedInteractive;
            _hudExpiresUtc = hudEvent.IsSticky
                ? DateTimeOffset.MaxValue
                : TimeProvider.GetUtcNow().AddMilliseconds(Math.Clamp(hudEvent.DurationMs, 600, 10000));

            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void PromoteTransientHudInteractive()
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (_activeHud is { InteractiveOnHover: true })
            {
                _isHudPromotedInteractive = true;
                _hudExpiresUtc = TimeProvider.GetUtcNow().AddMilliseconds(2600);
                emitted = CommitSnapshotLocked();
            }
        }

        if (emitted is not null)
        {
            SnapshotChanged?.Invoke(this, emitted);
        }
    }

    public void ExtendPromotedHud(int durationMs = 2600)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (_activeHud is not null && !_activeHud.IsSticky)
            {
                DateTimeOffset candidate = TimeProvider.GetUtcNow().AddMilliseconds(Math.Clamp(durationMs, 600, 10000));
                if (candidate > _hudExpiresUtc)
                {
                    _hudExpiresUtc = candidate;
                }
                if (!_isHudPromotedInteractive && _activeHud.InteractiveOnHover)
                {
                    _isHudPromotedInteractive = true;
                    emitted = CommitSnapshotLocked();
                }
            }
        }

        if (emitted is not null)
        {
            SnapshotChanged?.Invoke(this, emitted);
        }
    }

    public void SetHudHovered(bool hovered)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            _isHudHovered = hovered;
            if (hovered)
            {
                if (_activeHud is { InteractiveOnHover: true })
                {
                    bool wasPromoted = _isHudPromotedInteractive;
                    _isHudPromotedInteractive = true;
                    if (!_activeHud.IsSticky)
                    {
                        DateTimeOffset candidate = TimeProvider.GetUtcNow().AddMilliseconds(2600);
                        if (candidate > _hudExpiresUtc)
                        {
                            _hudExpiresUtc = candidate;
                        }
                    }
                    if (!wasPromoted)
                    {
                        emitted = CommitSnapshotLocked();
                    }
                }
            }
            else if (_activeHud is not null && !_activeHud.IsSticky)
            {
                DateTimeOffset exitGrace = TimeProvider.GetUtcNow().AddMilliseconds(1200);
                if (exitGrace > _hudExpiresUtc)
                {
                    _hudExpiresUtc = exitGrace;
                }
            }
        }

        if (emitted is not null)
        {
            SnapshotChanged?.Invoke(this, emitted);
        }
    }

    public void DismissTransientHud()
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (_activeHud is null) return;
            _activeHud = null;
            _isHudPromotedInteractive = false;
            _isHudHovered = false;
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void TickExpirations()
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (_activeHud is null || _activeHud.IsSticky)
            {
                return;
            }

            if (_isHudHovered && (_isHudPromotedInteractive || _activeHud.InteractiveOnHover))
            {
                return;
            }

            if (TimeProvider.GetUtcNow() >= _hudExpiresUtc)
            {
                _activeHud = null;
                _isHudPromotedInteractive = false;
                _isHudHovered = false;
                emitted = CommitSnapshotLocked();
            }
        }

        if (emitted is not null)
        {
            SnapshotChanged?.Invoke(this, emitted);
        }
    }

    public void RequestExpand(ExpandedTabId? tab = null)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                return;
            }

            if (tab.HasValue) _activeTab = tab.Value;
            _isExpanded = true;
            _isCommandOpen = false;
            _isDragActive = false;
            _isDragCatcherArmed = false;
            _activeHud = null;
            _isHudPromotedInteractive = false;
            _isHudHovered = false;
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
        if (tab.HasValue) TabRequested?.Invoke(this, tab.Value);
    }

    public void RequestNavigateTab(ExpandedTabId tab)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                return;
            }

            _activeTab = tab;
            if (_isCommandOpen || _isDragActive || !_isExpanded)
            {
                _isCommandOpen = false;
                _isDragActive = false;
                _isDragCatcherArmed = false;
                _isExpanded = true;
                _activeHud = null;
                _isHudPromotedInteractive = false;
                _isHudHovered = false;
            }
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
        TabRequested?.Invoke(this, tab);
    }

    public void EnsureValidPinnedTabSelection()
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            var pinned = _config.PinnedTabs;
            if (pinned is { Count: > 0 } && !pinned.Contains(_activeTab))
            {
                _activeTab = pinned[0];
                emitted = CommitSnapshotLocked();
            }
        }

        if (emitted is not null)
        {
            SnapshotChanged?.Invoke(this, emitted);
        }
    }

    public void RequestCollapse(bool forceUnpin = false)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (_isPinnedOpen && !forceUnpin) return;
            _isExpanded = false;
            _isPinnedOpen = false;
            _isCommandOpen = false;
            _isDragActive = false;
            _isDragCatcherArmed = false;
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    void IIslandHostContext.RequestCollapse() => RequestCollapse(forceUnpin: false);

    public void TogglePinned()
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                _isPinnedOpen = false;
                _isExpanded = false;
                return;
            }

            _isPinnedOpen = !_isPinnedOpen;
            _isCommandOpen = false;
            _isDragActive = false;
            _isDragCatcherArmed = false;
            if (_isPinnedOpen)
            {
                _isExpanded = true;
                _activeHud = null;
                _isHudPromotedInteractive = false;
                _isHudHovered = false;
            }
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void OpenCommandPalette()
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                return;
            }

            _isCommandOpen = true;
            _isExpanded = false;
            _isPinnedOpen = false;
            _isDragActive = false;
            _isDragCatcherArmed = false;
            _activeHud = null;
            _isHudPromotedInteractive = false;
            _isHudHovered = false;
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void CloseCommandPalette()
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (!_isCommandOpen) return;
            _isCommandOpen = false;
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void SetDragCatcherArmed(bool armed)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (_fullscreen.IsFullscreen && EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                armed = false;
            }
            if (_isDragCatcherArmed == armed) return;
            _isDragCatcherArmed = armed;
            if (!armed && _isDragActive)
            {
                _isDragActive = false;
            }
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void SetDragShelfActive(bool active)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (active && EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                return;
            }

            _isDragActive = active;
            if (active)
            {
                _isCommandOpen = false;
                _isExpanded = false;
                _isPinnedOpen = false;
                _activeHud = null;
                _isHudPromotedInteractive = false;
                _isHudHovered = false;
            }
            else
            {
                _isDragCatcherArmed = false;
            }
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void SetIslandMonitorHandle(long monitorHandle)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            if (_islandMonitorHandle == monitorHandle) return;
            _islandMonitorHandle = monitorHandle;
            if (_fullscreen.IsFullscreen && EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                ClearInteractiveOverlaysLocked();
            }
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void UpdateFullscreenState(FullscreenSnapshot snapshot)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            _fullscreen = snapshot;
            if (snapshot.IsFullscreen && EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                ClearInteractiveOverlaysLocked();
            }
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void UpdateFullscreenState(FullscreenSnapshot snapshot, long islandMonitorHandle)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            _islandMonitorHandle = islandMonitorHandle;
            _fullscreen = snapshot;
            if (snapshot.IsFullscreen && EffectivePolicyLocked() == FullscreenPolicy.HideCompletely)
            {
                ClearInteractiveOverlaysLocked();
            }
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void SetQuietIsland(bool enabled)
    {
        IslandCoordinatorSnapshot? emitted = null;
        lock (_sync)
        {
            _config.QuietIslandEnabled = enabled;
            if (enabled && _activeHud is not null && IsQuietSuppressed(_activeHud.Kind))
            {
                _activeHud = null;
                _isHudPromotedInteractive = false;
                _isHudHovered = false;
            }
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    public void Recompute()
    {
        IslandCoordinatorSnapshot? emitted;
        lock (_sync)
        {
            emitted = CommitSnapshotLocked();
        }

        SnapshotChanged?.Invoke(this, emitted);
    }

    private void ClearInteractiveOverlaysLocked()
    {
        _isExpanded = false;
        _isPinnedOpen = false;
        _isCommandOpen = false;
        _isDragActive = false;
        _isDragCatcherArmed = false;
        _activeHud = null;
        _isHudPromotedInteractive = false;
        _isHudHovered = false;
    }

    private static bool IsQuietSuppressed(HudKind kind) =>
        kind is HudKind.NotificationPulse or HudKind.MediaTrack or HudKind.WidgetAlert or HudKind.Clipboard or HudKind.GameMode;

    private IslandCoordinatorSnapshot CommitSnapshotLocked()
    {
        var next = ComputeSnapshotLocked();
        _current = next;
        return next;
    }

    private FullscreenPolicy EffectivePolicyLocked()
    {
        if (!_fullscreen.IsFullscreen) return FullscreenPolicy.KeepCompact;

        if (_islandMonitorHandle != 0 && _fullscreen.MonitorHandle != 0 && _islandMonitorHandle != _fullscreen.MonitorHandle)
        {
            return FullscreenPolicy.KeepCompact;
        }

        if (!string.IsNullOrWhiteSpace(_fullscreen.ProcessName))
        {
            if (_config.FullscreenWhitelistExes.Any(x => string.Equals(x, _fullscreen.ProcessName, StringComparison.OrdinalIgnoreCase)))
                return FullscreenPolicy.KeepCompact;
            if (_config.FullscreenHideExes.Any(x => string.Equals(x, _fullscreen.ProcessName, StringComparison.OrdinalIgnoreCase)))
                return FullscreenPolicy.HideCompletely;
        }

        return _fullscreen.Kind switch
        {
            FullscreenAppKind.Game when _config.EnableGameModeAutomation => _config.GameModeHideIsland
                ? FullscreenPolicy.HideCompletely
                : (_config.GameModeShowPingPill ? FullscreenPolicy.KeepCompact : _config.GameFullscreenPolicy),
            FullscreenAppKind.Game => _config.GameFullscreenPolicy,
            FullscreenAppKind.Video => _config.VideoFullscreenPolicy,
            _ => _config.ProductivityFullscreenPolicy
        };
    }

    private IslandCoordinatorSnapshot ComputeSnapshotLocked()
    {
        var ordered = _activities.Values
            .Where(a => a.Kind != ActivityKind.None)
            .OrderByDescending(a => a.Priority)
            .ThenByDescending(a => a.IsCritical)
            .ThenBy(a => a.SourceId, StringComparer.Ordinal)
            .ToList();

        LiveActivityState? primary = ordered.Count > 0 ? ordered[0] : null;
        LiveActivityState? satellite = ordered.Count > 1 ? ordered[1] : null;
        FullscreenPolicy policy = EffectivePolicyLocked();

        if (_fullscreen.IsFullscreen && policy == FullscreenPolicy.HideCompletely)
        {
            return new IslandCoordinatorSnapshot(
                IslandPresentationState.S0Hidden,
                new IslandLayoutTarget(IslandPresentationState.S0Hidden, 0, 0, 0, 0, 0, IsClickThrough: true, RequiresKeyboardFocus: false, EarRadius: 0, TopCornerRadius: 0),
                _activeTab,
                primary,
                satellite,
                null,
                false,
                false,
                _config.QuietIslandEnabled,
                _fullscreen);
        }

        IslandPresentationState state;
        if (_isCommandOpen)
        {
            state = IslandPresentationState.S5Command;
        }
        else if (_isDragActive)
        {
            state = IslandPresentationState.S6DragShelf;
        }
        else if (_isExpanded)
        {
            state = IslandPresentationState.S4Expanded;
        }
        else if (_activeHud is not null)
        {
            state = IslandPresentationState.S3TransientHud;
        }
        else if (_fullscreen.IsFullscreen && policy == FullscreenPolicy.EdgePeekHairline)
        {
            return new IslandCoordinatorSnapshot(
                IslandPresentationState.S1Dormant,
                new IslandLayoutTarget(IslandPresentationState.S1Dormant, 68, 4, 2, 0, 0, IsClickThrough: false, RequiresKeyboardFocus: false, EarRadius: 0, TopCornerRadius: 0),
                _activeTab,
                primary,
                satellite,
                _activeHud,
                _isPinnedOpen,
                _isDragCatcherArmed,
                _config.QuietIslandEnabled,
                _fullscreen);
        }
        else if (primary is not null && satellite is not null)
        {
            state = IslandPresentationState.S2Split;
        }
        else if (primary is not null)
        {
            state = IslandPresentationState.S2Compact;
        }
        else
        {
            state = IslandPresentationState.S1Dormant;
        }

        double baseSatelliteWidthDip = !string.IsNullOrWhiteSpace(satellite?.PrimaryText) ? 96.0 : 44.0;
        IslandLayoutTarget layout = GetTargetLayout(state, baseSatelliteWidthDip);

        return new IslandCoordinatorSnapshot(
            state,
            layout,
            _activeTab,
            primary,
            satellite,
            _activeHud,
            _isPinnedOpen,
            _isDragCatcherArmed,
            _config.QuietIslandEnabled,
            _fullscreen);
    }

    private IslandLayoutTarget GetTargetLayout(IslandPresentationState state, double baseSatelliteWidthDip)
    {
        bool flush = _config.DockPlacement == DockPlacementMode.NotchFlush;
        double scale = AppConfig.GetScaleFactor(_config.NotchScalePreset);

        return state switch
        {
            IslandPresentationState.S0Hidden => new(
                state, 0, 0, 0, 0, 0,
                IsClickThrough: true,
                RequiresKeyboardFocus: false,
                EarRadius: 0,
                TopCornerRadius: 0),

            IslandPresentationState.S1Dormant => flush
                ? CreateScaledLayout(
                    state,
                    width: _config.ShowTelemetryInPill ? 212 : 176,
                    height: 28,
                    cornerRadius: 13,
                    earRadius: 8,
                    topCornerRadius: 0,
                    scale: scale)
                : CreateScaledLayout(
                    state,
                    width: 140,
                    height: 30,
                    cornerRadius: 15,
                    earRadius: 0,
                    topCornerRadius: 15,
                    scale: scale),

            IslandPresentationState.S2Compact => CreateScaledLayout(
                state,
                width: 276,
                height: 36,
                cornerRadius: flush ? 16 : 18,
                earRadius: flush ? 9 : 0,
                topCornerRadius: flush ? 0 : 18,
                scale: scale),

            IslandPresentationState.S2Split => CreateScaledLayout(
                state,
                width: 252,
                height: 36,
                cornerRadius: flush ? 16 : 18,
                earRadius: flush ? 9 : 0,
                topCornerRadius: flush ? 0 : 18,
                scale: scale,
                satelliteWidth: baseSatelliteWidthDip,
                satelliteGap: 10),

            IslandPresentationState.S3TransientHud => CreateScaledLayout(
                state,
                width: ResolveTransientHudWidthDip(_activeHud),
                height: 44,
                cornerRadius: flush ? 18 : 22,
                earRadius: flush ? 9 : 0,
                topCornerRadius: flush ? 0 : 22,
                scale: scale,
                isClickThrough: !_isHudPromotedInteractive &&
                                !(_activeHud?.IsSticky ?? false) &&
                                string.IsNullOrWhiteSpace(_activeHud?.PrimaryActionLabel)),

            IslandPresentationState.S4Expanded => GetExpandedLayout(_activeTab, flush, scale),

            IslandPresentationState.S5Command => CreateScaledLayout(
                state,
                width: 564,
                height: 368,
                cornerRadius: 24,
                earRadius: flush ? 12 : 0,
                topCornerRadius: flush ? 0 : 24,
                scale: scale,
                requiresKeyboardFocus: true),

            IslandPresentationState.S6DragShelf => CreateScaledLayout(
                state,
                width: 416,
                height: 164,
                cornerRadius: 24,
                earRadius: flush ? 12 : 0,
                topCornerRadius: flush ? 0 : 24,
                scale: scale),

            _ => CreateScaledLayout(
                IslandPresentationState.S1Dormant,
                width: flush ? 176 : 140,
                height: flush ? 28 : 30,
                cornerRadius: flush ? 13 : 15,
                earRadius: flush ? 8 : 0,
                topCornerRadius: flush ? 0 : 15,
                scale: scale)
        };
    }

    private static IslandLayoutTarget GetExpandedLayout(ExpandedTabId tab, bool flush, double scale)
    {
        double height = tab switch
        {
            ExpandedTabId.Now => 280,
            ExpandedTabId.Control => 296,
            ExpandedTabId.Audio => 332,
            ExpandedTabId.Shelf => 336,
            ExpandedTabId.System => 336,
            ExpandedTabId.Widgets => 316,
            _ => 280
        };

        return CreateScaledLayout(
            IslandPresentationState.S4Expanded,
            width: 616,
            height: height,
            cornerRadius: 24,
            earRadius: flush ? 14 : 0,
            topCornerRadius: flush ? 0 : 24,
            scale: scale);
    }

    private static double ResolveTransientHudWidthDip(TransientHudEvent? hud)
    {
        if (hud is null) return 308;
        if (!string.IsNullOrWhiteSpace(hud.PrimaryActionLabel) &&
            !string.IsNullOrWhiteSpace(hud.SecondaryActionLabel))
        {
            return 416;
        }
        if (!string.IsNullOrWhiteSpace(hud.PrimaryActionLabel) ||
            (hud.InteractiveOnHover && hud.Kind is HudKind.FileStashed or HudKind.Clipboard or HudKind.NotificationPulse or HudKind.TimerAlert or HudKind.TransferComplete or HudKind.QrShare or HudKind.OcrCapture or HudKind.VoiceNote or HudKind.ColorPicked))
        {
            return 352;
        }
        return 308;
    }

    private static IslandLayoutTarget CreateScaledLayout(
        IslandPresentationState state,
        double width,
        double height,
        double cornerRadius,
        double earRadius,
        double topCornerRadius,
        double scale,
        double satelliteWidth = 0,
        double satelliteGap = 0,
        bool isClickThrough = false,
        bool requiresKeyboardFocus = false) =>
        new(
            state,
            width * scale,
            height * scale,
            cornerRadius * scale,
            satelliteWidth * scale,
            satelliteGap,
            isClickThrough,
            requiresKeyboardFocus,
            earRadius * scale,
            topCornerRadius * scale);
}
