using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Modules.Productivity;

public sealed record ScheduledPowerSnapshot(
    ScheduledPowerActionKind Kind,
    TimeSpan Total,
    TimeSpan Remaining,
    bool WarningTriggered,
    bool IsActive);

public sealed class FocusTimerService : ILiveActivitySource, IDisposable
{
    private sealed class MutableTimer
    {
        public required string Id;
        public required string Label;
        public required bool IsPomodoro;
        public required bool IsStopwatch;
        public required TimeSpan Total;
        public required TimeSpan Accumulated;
        public long StartedTimestamp;
        public bool IsRunning;
        public bool IsCompleted;
    }

    private readonly TimeProvider _timeProvider;
    private readonly ITransientHudBus _hudBus;
    private readonly object _sync = new();
    private readonly List<MutableTimer> _timers = new();
    private Timer? _tickTimer;
    private bool _disposed;

    private ScheduledPowerActionKind _scheduledKind = ScheduledPowerActionKind.None;
    private TimeSpan _scheduledTotal;
    private TimeSpan _scheduledRemainingBase;
    private long _scheduledStartedTimestamp;
    private bool _scheduledWarningTriggered;
    private bool _scheduledActive;

    public string SourceId => "productivity.timer";
    public string Language { get; set; } = "tr";
    public event EventHandler<LiveActivityState?>? ActivityChanged;
    public event EventHandler<IReadOnlyList<TimerSnapshot>>? TimersChanged;
    public event EventHandler<ScheduledPowerSnapshot>? ScheduledPowerChanged;
    public event EventHandler<ScheduledPowerSnapshot>? ScheduledPowerWarningReached;
    public event EventHandler<ScheduledPowerActionKind>? ScheduledPowerDue;
    public LiveActivityState? CurrentActivity { get; private set; }

    public ScheduledPowerSnapshot ScheduledPowerState
    {
        get { lock (_sync) return BuildScheduledSnapshotLocked(); }
    }

    public LiveActivityState? CurrentScheduledPowerActivity
    {
        get { lock (_sync) return BuildScheduledPowerActivityLocked(); }
    }

    public FocusTimerService(ITransientHudBus hudBus, TimeProvider? timeProvider = null, string language = "tr")
    {
        _hudBus = hudBus;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Language = string.IsNullOrWhiteSpace(language) ? "tr" : language;
    }

    public IReadOnlyList<TimerSnapshot> GetTimers()
    {
        lock (_sync) return _timers.Select(ToSnapshot).ToList();
    }

    public string StartCountdown(string label, TimeSpan duration, bool isPomodoro = false)
    {
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromMinutes(5);
        string id = Guid.NewGuid().ToString("N")[..8];
        lock (_sync)
        {
            if (_disposed) return string.Empty;
            _timers.Add(new MutableTimer
            {
                Id = id,
                Label = label,
                IsPomodoro = isPomodoro,
                IsStopwatch = false,
                Total = duration,
                Accumulated = TimeSpan.Zero,
                StartedTimestamp = _timeProvider.GetTimestamp(),
                IsRunning = true,
                IsCompleted = false
            });
            EnsureTickerLocked();
        }
        EmitUpdates();
        return id;
    }

    public string StartStopwatch(string? label = null)
    {
        string effectiveLabel = string.IsNullOrWhiteSpace(label)
            ? Strings.Pick(Language, "Kronometre", "Stopwatch")
            : label;
        string id = Guid.NewGuid().ToString("N")[..8];
        lock (_sync)
        {
            if (_disposed) return string.Empty;
            _timers.Add(new MutableTimer
            {
                Id = id,
                Label = effectiveLabel,
                IsPomodoro = false,
                IsStopwatch = true,
                Total = TimeSpan.Zero,
                Accumulated = TimeSpan.Zero,
                StartedTimestamp = _timeProvider.GetTimestamp(),
                IsRunning = true,
                IsCompleted = false
            });
            EnsureTickerLocked();
        }
        EmitUpdates();
        return id;
    }

    public void TogglePause(string id)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var t = _timers.FirstOrDefault(x => x.Id == id);
            if (t is null || t.IsCompleted) return;
            if (t.IsRunning)
            {
                t.Accumulated += _timeProvider.GetElapsedTime(t.StartedTimestamp);
                t.IsRunning = false;
            }
            else
            {
                t.StartedTimestamp = _timeProvider.GetTimestamp();
                t.IsRunning = true;
            }
            EnsureTickerLocked();
        }
        EmitUpdates();
    }

    public void ResetTimer(string id, bool autoStart = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var t = _timers.FirstOrDefault(x => x.Id == id);
            if (t is null) return;
            t.Accumulated = TimeSpan.Zero;
            t.IsCompleted = false;
            t.IsRunning = autoStart;
            t.StartedTimestamp = _timeProvider.GetTimestamp();
            EnsureTickerLocked();
        }
        EmitUpdates();
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _timers.RemoveAll(x => x.Id == id);
            EnsureTickerLocked();
        }
        EmitUpdates();
    }

    public ScheduledPowerSnapshot GetScheduledPowerState() => ScheduledPowerState;

    public void StartScheduledPowerAction(ScheduledPowerActionKind kind, TimeSpan duration)
    {
        if (kind == ScheduledPowerActionKind.None)
        {
            CancelScheduledPowerAction();
            return;
        }

        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.FromMinutes(15);
        }

        ScheduledPowerSnapshot snapshot;
        lock (_sync)
        {
            if (_disposed) return;
            _scheduledKind = kind;
            _scheduledTotal = duration;
            _scheduledRemainingBase = duration;
            _scheduledStartedTimestamp = _timeProvider.GetTimestamp();
            _scheduledWarningTriggered = false;
            _scheduledActive = true;
            EnsureTickerLocked();
            snapshot = BuildScheduledSnapshotLocked(duration);
        }

        ScheduledPowerChanged?.Invoke(this, snapshot);
        EmitUpdates();
    }

    public void SnoozeScheduledPowerAction(TimeSpan extra)
    {
        if (extra <= TimeSpan.Zero) return;

        ScheduledPowerSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_disposed || !_scheduledActive) return;
            TimeSpan remaining = GetScheduledRemainingLocked();
            TimeSpan nextRemaining = remaining + extra;
            _scheduledRemainingBase = nextRemaining;
            _scheduledStartedTimestamp = _timeProvider.GetTimestamp();
            if (nextRemaining > _scheduledTotal)
            {
                _scheduledTotal = nextRemaining;
            }
            if (nextRemaining > TimeSpan.FromSeconds(60))
            {
                _scheduledWarningTriggered = false;
            }
            EnsureTickerLocked();
            snapshot = BuildScheduledSnapshotLocked(nextRemaining);
        }

        if (snapshot is not null && !_disposed)
        {
            ScheduledPowerChanged?.Invoke(this, snapshot);
            EmitUpdates();
        }
    }

    public void CancelScheduledPowerAction()
    {
        ScheduledPowerSnapshot snapshot;
        bool wasActive;
        lock (_sync)
        {
            if (_disposed) return;
            wasActive = _scheduledActive || _scheduledKind != ScheduledPowerActionKind.None;
            _scheduledActive = false;
            _scheduledKind = ScheduledPowerActionKind.None;
            _scheduledTotal = TimeSpan.Zero;
            _scheduledRemainingBase = TimeSpan.Zero;
            _scheduledWarningTriggered = false;
            EnsureTickerLocked();
            snapshot = BuildScheduledSnapshotLocked(TimeSpan.Zero);
        }

        if (wasActive && !_disposed)
        {
            ScheduledPowerChanged?.Invoke(this, snapshot);
            EmitUpdates();
        }
    }

    public void Tick()
    {
        List<MutableTimer> completedNow = new();
        ScheduledPowerSnapshot? scheduledSnapshotToPublish = null;
        ScheduledPowerSnapshot? warningSnapshot = null;
        ScheduledPowerActionKind dueKind = ScheduledPowerActionKind.None;

        lock (_sync)
        {
            if (_disposed) return;
            foreach (var t in _timers)
            {
                if (!t.IsRunning || t.IsStopwatch || t.IsCompleted) continue;
                TimeSpan elapsed = t.Accumulated + _timeProvider.GetElapsedTime(t.StartedTimestamp);
                if (elapsed >= t.Total)
                {
                    t.Accumulated = t.Total;
                    t.IsRunning = false;
                    t.IsCompleted = true;
                    completedNow.Add(t);
                }
            }

            if (_scheduledActive)
            {
                TimeSpan remaining = GetScheduledRemainingLocked();
                if (remaining <= TimeSpan.Zero)
                {
                    dueKind = _scheduledKind;
                    _scheduledActive = false;
                    _scheduledKind = ScheduledPowerActionKind.None;
                    _scheduledTotal = TimeSpan.Zero;
                    _scheduledRemainingBase = TimeSpan.Zero;
                    _scheduledWarningTriggered = false;
                    scheduledSnapshotToPublish = BuildScheduledSnapshotLocked(TimeSpan.Zero);
                }
                else
                {
                    if (remaining <= TimeSpan.FromSeconds(60) && !_scheduledWarningTriggered)
                    {
                        _scheduledWarningTriggered = true;
                        warningSnapshot = BuildScheduledSnapshotLocked(remaining);
                    }
                    scheduledSnapshotToPublish = BuildScheduledSnapshotLocked(remaining);
                }
            }

            EnsureTickerLocked();
        }

        foreach (var done in completedNow)
        {
            if (_disposed) return;
            try { NativeMethods.MessageBeep(0x00000040); } catch { }
            _hudBus.Publish(new TransientHudEvent(
                CoalesceKey: $"timer:{done.Id}",
                Kind: HudKind.TimerAlert,
                Title: done.Label,
                Subtitle: Strings.Pick(Language, "Süre doldu!", "Time's up!"),
                Value01: 1.0,
                AccentHex: "#FF9F0A",
                DurationMs: 4500,
                InteractiveOnHover: true,
                IsSticky: false));
        }

        if (!_disposed && scheduledSnapshotToPublish is not null)
        {
            ScheduledPowerChanged?.Invoke(this, scheduledSnapshotToPublish);
        }

        if (!_disposed && warningSnapshot is not null)
        {
            ScheduledPowerWarningReached?.Invoke(this, warningSnapshot);
        }

        if (!_disposed && dueKind != ScheduledPowerActionKind.None)
        {
            ScheduledPowerDue?.Invoke(this, dueKind);
        }

        if (!_disposed)
        {
            EmitUpdates();
        }
    }

    public void NotifyResume() => Tick();

    private void EnsureTickerLocked()
    {
        if (_disposed) return;
        bool anyRunning = _timers.Any(x => x.IsRunning) || _scheduledActive;
        if (anyRunning && _tickTimer is null)
        {
            _tickTimer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        }
        else if (!anyRunning && _tickTimer is not null)
        {
            _tickTimer.Dispose();
            _tickTimer = null;
        }
    }

    private void EmitUpdates()
    {
        var snapshots = GetTimers();
        TimersChanged?.Invoke(this, snapshots);

        var active = snapshots
            .Where(s => s.IsRunning && !s.IsCompleted)
            .OrderBy(s => s.IsStopwatch)
            .ThenBy(s => s.RemainingOrElapsed)
            .FirstOrDefault();

        if (active is null)
        {
            LiveActivityState? scheduledActivity;
            lock (_sync)
            {
                scheduledActivity = BuildScheduledPowerActivityLocked();
            }
            CurrentActivity = scheduledActivity;
            ActivityChanged?.Invoke(this, scheduledActivity);
            return;
        }

        string formatted = FormatDuration(active.RemainingOrElapsed);

        double? progress = active.IsStopwatch || active.Total <= TimeSpan.Zero
            ? null
            : Math.Clamp(1.0 - (active.RemainingOrElapsed.TotalSeconds / active.Total.TotalSeconds), 0.0, 1.0);

        CurrentActivity = new LiveActivityState(
            SourceId: SourceId,
            Kind: ActivityKind.Timer,
            Priority: 80,
            PrimaryText: formatted,
            SecondaryText: active.Label,
            AccentHex: active.RemainingOrElapsed <= TimeSpan.FromSeconds(15) && !active.IsStopwatch ? "#FF453A" : "#FF9F0A",
            Progress01: progress,
            IsCritical: false,
            TargetTab: ExpandedTabId.Now);

        ActivityChanged?.Invoke(this, CurrentActivity);
    }

    private TimeSpan GetScheduledRemainingLocked()
    {
        if (!_scheduledActive) return TimeSpan.Zero;
        TimeSpan elapsed = _timeProvider.GetElapsedTime(_scheduledStartedTimestamp);
        TimeSpan remaining = _scheduledRemainingBase - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private ScheduledPowerSnapshot BuildScheduledSnapshotLocked() =>
        BuildScheduledSnapshotLocked(GetScheduledRemainingLocked());

    private ScheduledPowerSnapshot BuildScheduledSnapshotLocked(TimeSpan remaining) =>
        new(_scheduledKind, _scheduledTotal, remaining, _scheduledWarningTriggered, _scheduledActive);

    private LiveActivityState? BuildScheduledPowerActivityLocked()
    {
        if (!_scheduledActive || _scheduledKind == ScheduledPowerActionKind.None)
        {
            return null;
        }

        TimeSpan remaining = GetScheduledRemainingLocked();
        string formatted = FormatDuration(remaining);
        double? progress = _scheduledTotal > TimeSpan.Zero
            ? Math.Clamp(1.0 - (remaining.TotalSeconds / _scheduledTotal.TotalSeconds), 0.0, 1.0)
            : null;
        bool isFinalMinute = remaining <= TimeSpan.FromSeconds(60);

        return new LiveActivityState(
            SourceId: SourceId,
            Kind: ActivityKind.ScheduledAction,
            Priority: (int)ActivityKind.ScheduledAction,
            PrimaryText: formatted,
            SecondaryText: FormatScheduledActionLabel(_scheduledKind, Language),
            AccentHex: isFinalMinute ? "#FF453A" : "#FF9F0A",
            Progress01: progress,
            IsCritical: isFinalMinute,
            IconGlyph: "\uE7E8",
            TargetTab: ExpandedTabId.Control);
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"mm\:ss");

    private static string FormatScheduledActionLabel(ScheduledPowerActionKind kind, string lang) => kind switch
    {
        ScheduledPowerActionKind.Sleep => Strings.ScheduledSleep(lang),
        ScheduledPowerActionKind.Shutdown => Strings.ScheduledShutdown(lang),
        ScheduledPowerActionKind.Lock => Strings.ScheduledLock(lang),
        ScheduledPowerActionKind.PauseMedia => Strings.ScheduledPauseMedia(lang),
        _ => Strings.ScheduledPowerHeader(lang)
    };

    private TimerSnapshot ToSnapshot(MutableTimer t)
    {
        TimeSpan elapsed = t.IsRunning ? t.Accumulated + _timeProvider.GetElapsedTime(t.StartedTimestamp) : t.Accumulated;
        TimeSpan display = t.IsStopwatch
            ? elapsed
            : (elapsed >= t.Total ? TimeSpan.Zero : t.Total - elapsed);

        return new TimerSnapshot(t.Id, t.Label, t.IsPomodoro, t.IsStopwatch, t.IsRunning, t.IsCompleted, t.Total, display);
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _tickTimer;
            _tickTimer = null;
        }
        timer?.Dispose();
    }
}
