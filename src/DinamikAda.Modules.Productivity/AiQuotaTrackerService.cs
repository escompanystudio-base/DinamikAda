using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;

namespace DinamikAda.Modules.Productivity;

public sealed record AiQuotaSlotSnapshot(
    string Id,
    string Provider,
    string Label,
    string AccentHex,
    int ResetWindowMinutes,
    DateTimeOffset? NextResetUtc,
    TimeSpan Remaining,
    bool IsReady,
    double? CooldownProgress01,
    bool NotifyOnReset);

public sealed record AiQuotaSummary(
    int ReadyCount,
    int TotalCount,
    AiQuotaSlotSnapshot? SoonestCooldown);

/// <summary>
/// Ultra-lightweight, zero-credential-bypass multi-account AI subscription reset &amp; quota countdown tracker.
/// Persists user-managed subscription windows cleanly in AppConfig and runs zero background polling when all slots are ready.
/// </summary>
public sealed class AiQuotaTrackerService : IDisposable
{
    private readonly AppConfig _config;
    private readonly Action _saveConfig;
    private readonly ITransientHudBus _hudBus;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private Timer? _wakeTimer;
    private bool _disposed;

    public string Language { get; set; } = "tr";
    public event EventHandler<IReadOnlyList<AiQuotaSlotSnapshot>>? SlotsChanged;

    public bool HasActiveCooldowns
    {
        get
        {
            long nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            lock (_sync)
            {
                return _config.AiQuotaSlots.Any(s => s.NextResetUtcTicks > nowTicks);
            }
        }
    }

    public AiQuotaTrackerService(
        AppConfig config,
        Action saveConfig,
        ITransientHudBus hudBus,
        TimeProvider? timeProvider = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
        _hudBus = hudBus ?? throw new ArgumentNullException(nameof(hudBus));
        _timeProvider = timeProvider ?? TimeProvider.System;
        Language = string.IsNullOrWhiteSpace(config.Language) ? "tr" : config.Language;
    }

    public void Start()
    {
        SeedStarterSlotsIfNeeded();
        ReevaluateDueResets();
    }

    public void SeedStarterSlotsIfNeeded()
    {
        bool seeded = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (!_config.AiQuotaSeeded && _config.AiQuotaSlots.Count == 0)
            {
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_gpt1",
                    Provider = "ChatGPT",
                    Label = "ChatGPT #1",
                    ResetWindowMinutes = 180,
                    NotifyOnReset = true
                });
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_gpt2",
                    Provider = "ChatGPT",
                    Label = "ChatGPT #2",
                    ResetWindowMinutes = 180,
                    NotifyOnReset = true
                });
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_cld1",
                    Provider = "Claude",
                    Label = "Claude #1",
                    ResetWindowMinutes = 300,
                    NotifyOnReset = true
                });
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_gem1",
                    Provider = "Gemini",
                    Label = "Gemini #1",
                    ResetWindowMinutes = 180,
                    NotifyOnReset = true
                });
                _config.AiQuotaSeeded = true;
                seeded = true;
            }
            else if (!_config.AiQuotaSeeded)
            {
                _config.AiQuotaSeeded = true;
                seeded = true;
            }
        }

        if (seeded)
        {
            SafeSaveConfig();
            NotifySlotsChanged();
        }
    }

    public IReadOnlyList<AiQuotaSlotSnapshot> GetSnapshots()
    {
        var nowUtc = _timeProvider.GetUtcNow();
        List<AiQuotaSlotSnapshot> ready = [];
        List<AiQuotaSlotSnapshot> cooling = [];

        lock (_sync)
        {
            foreach (var s in _config.AiQuotaSlots)
            {
                var snap = ToSnapshot(s, nowUtc);
                if (snap.IsReady)
                {
                    ready.Add(snap);
                }
                else
                {
                    cooling.Add(snap);
                }
            }
        }

        cooling.Sort(static (a, b) => a.Remaining.CompareTo(b.Remaining));
        ready.AddRange(cooling);
        return ready;
    }

    public AiQuotaSummary GetSummary()
    {
        var snaps = GetSnapshots();
        int readyCount = 0;
        AiQuotaSlotSnapshot? soonest = null;
        foreach (var s in snaps)
        {
            if (s.IsReady)
            {
                readyCount++;
            }
            else if (soonest is null || s.Remaining < soonest.Remaining)
            {
                soonest = s;
            }
        }
        return new AiQuotaSummary(readyCount, snaps.Count, soonest);
    }

    public AiQuotaSlotSnapshot? AddSlot(
        string provider,
        string? label = null,
        int? resetWindowMinutes = null,
        bool notifyOnReset = true)
    {
        string cleanProvider = NormalizeProvider(provider);
        int windowMin = Math.Clamp(
            resetWindowMinutes ?? GetDefaultWindowMinutes(cleanProvider),
            AiQuotaSlotConfig.MinWindowMinutes,
            AiQuotaSlotConfig.MaxWindowMinutes);

        AiQuotaSlotConfig created;
        lock (_sync)
        {
            if (_disposed || _config.AiQuotaSlots.Count >= AiQuotaSlotConfig.MaxSlotCount)
            {
                return null;
            }

            string cleanLabel = string.IsNullOrWhiteSpace(label)
                ? GenerateNextLabelLocked(cleanProvider)
                : label.Trim();
            if (cleanLabel.Length > 36) cleanLabel = cleanLabel[..36];

            created = new AiQuotaSlotConfig
            {
                Id = "ai_" + Guid.NewGuid().ToString("N")[..6],
                Provider = cleanProvider,
                Label = cleanLabel,
                ResetWindowMinutes = windowMin,
                NextResetUtcTicks = 0,
                NotifyOnReset = notifyOnReset
            };
            _config.AiQuotaSlots.Add(created);
            _config.AiQuotaSeeded = true;
        }

        SafeSaveConfig();
        NotifySlotsChanged();
        return ToSnapshot(created, _timeProvider.GetUtcNow());
    }

    public bool UpdateSlot(
        string id,
        string provider,
        string label,
        int resetWindowMinutes,
        bool notifyOnReset)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        string cleanProvider = NormalizeProvider(provider);
        int cleanWindow = Math.Clamp(resetWindowMinutes, AiQuotaSlotConfig.MinWindowMinutes, AiQuotaSlotConfig.MaxWindowMinutes);
        string cleanLabel = string.IsNullOrWhiteSpace(label) ? cleanProvider : label.Trim();
        if (cleanLabel.Length > 36) cleanLabel = cleanLabel[..36];

        lock (_sync)
        {
            if (_disposed) return false;
            var slot = _config.AiQuotaSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return false;

            slot.Provider = cleanProvider;
            slot.Label = cleanLabel;
            slot.ResetWindowMinutes = cleanWindow;
            slot.NotifyOnReset = notifyOnReset;
        }

        SafeSaveConfig();
        NotifySlotsChanged();
        return true;
    }

    public bool RemoveSlot(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        bool removed;
        lock (_sync)
        {
            if (_disposed) return false;
            int idx = _config.AiQuotaSlots.FindIndex(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            _config.AiQuotaSlots.RemoveAt(idx);
            _config.AiQuotaSeeded = true;
            removed = true;
            ArmWakeTimerLocked(_timeProvider.GetUtcNow().UtcTicks);
        }

        if (removed)
        {
            SafeSaveConfig();
            NotifySlotsChanged();
        }
        return removed;
    }

    public bool MarkExhausted(string id, int? overrideMinutes = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var nowUtc = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (_disposed) return false;
            var slot = _config.AiQuotaSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return false;

            int mins = Math.Clamp(
                overrideMinutes ?? slot.ResetWindowMinutes,
                AiQuotaSlotConfig.MinWindowMinutes,
                AiQuotaSlotConfig.MaxWindowMinutes);
            slot.NextResetUtcTicks = nowUtc.AddMinutes(mins).UtcTicks;
            ArmWakeTimerLocked(nowUtc.UtcTicks);
        }

        SafeSaveConfig();
        NotifySlotsChanged();
        return true;
    }

    public bool AdjustRemaining(string id, int deltaMinutes)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var nowUtc = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (_disposed) return false;
            var slot = _config.AiQuotaSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return false;

            var baseReset = slot.NextResetUtcTicks > nowUtc.UtcTicks
                ? new DateTimeOffset(slot.NextResetUtcTicks, TimeSpan.Zero)
                : nowUtc;

            var next = baseReset.AddMinutes(deltaMinutes);
            if (next <= nowUtc)
            {
                slot.NextResetUtcTicks = 0;
            }
            else
            {
                var maxFuture = nowUtc.AddMinutes(AiQuotaSlotConfig.MaxWindowMinutes);
                slot.NextResetUtcTicks = (next > maxFuture ? maxFuture : next).UtcTicks;
            }

            ArmWakeTimerLocked(nowUtc.UtcTicks);
        }

        SafeSaveConfig();
        NotifySlotsChanged();
        return true;
    }

    public bool MarkReady(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        bool changed;
        lock (_sync)
        {
            if (_disposed) return false;
            var slot = _config.AiQuotaSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return false;
            changed = slot.NextResetUtcTicks != 0;
            slot.NextResetUtcTicks = 0;
            ArmWakeTimerLocked(_timeProvider.GetUtcNow().UtcTicks);
        }

        if (changed)
        {
            SafeSaveConfig();
            NotifySlotsChanged();
        }
        return true;
    }

    public void ReevaluateDueResets()
    {
        bool anyExpired = false;
        List<AiQuotaSlotConfig>? unlocked = null;
        var nowUtc = _timeProvider.GetUtcNow();
        long nowTicks = nowUtc.UtcTicks;

        lock (_sync)
        {
            if (_disposed) return;
            foreach (var s in _config.AiQuotaSlots)
            {
                if (s.NextResetUtcTicks > 0 && s.NextResetUtcTicks <= nowTicks)
                {
                    s.NextResetUtcTicks = 0;
                    anyExpired = true;
                    if (s.NotifyOnReset)
                    {
                        unlocked ??= new List<AiQuotaSlotConfig>(2);
                        unlocked.Add(new AiQuotaSlotConfig
                        {
                            Id = s.Id,
                            Provider = s.Provider,
                            Label = s.Label,
                            ResetWindowMinutes = s.ResetWindowMinutes,
                            NotifyOnReset = s.NotifyOnReset
                        });
                    }
                }
            }
            ArmWakeTimerLocked(nowTicks);
        }

        if (!anyExpired) return;

        SafeSaveConfig();
        if (unlocked is { Count: > 0 })
        {
            string lang = Language;
            foreach (var item in unlocked)
            {
                _hudBus.Publish(new TransientHudEvent(
                    CoalesceKey: $"ai-quota-reset:{item.Id}",
                    Kind: HudKind.WidgetAlert,
                    Title: Strings.AiQuotaUnlockedTitle(lang, item.Label),
                    Subtitle: Strings.AiQuotaUnlockedSub(lang, item.Provider),
                    AccentHex: GetProviderAccentHex(item.Provider),
                    DurationMs: 4200,
                    InteractiveOnHover: true,
                    IsSticky: false,
                    IconGlyph: "\uE916",
                    PrimaryActionId: "ai.open-tab",
                    PrimaryActionLabel: Strings.TabWidgets(lang)));
            }
        }
        NotifySlotsChanged();
    }

    private void ArmWakeTimerLocked(long nowUtcTicks)
    {
        if (_disposed) return;

        long earliestFutureTicks = long.MaxValue;
        foreach (var s in _config.AiQuotaSlots)
        {
            if (s.NextResetUtcTicks > nowUtcTicks && s.NextResetUtcTicks < earliestFutureTicks)
            {
                earliestFutureTicks = s.NextResetUtcTicks;
            }
        }

        if (earliestFutureTicks == long.MaxValue)
        {
            _wakeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        double msRemaining = Math.Max(250.0, (earliestFutureTicks - nowUtcTicks) / (double)TimeSpan.TicksPerMillisecond + 80.0);
        int dueMs = (int)Math.Min(msRemaining, 60_000.0);
        if (_wakeTimer is null)
        {
            _wakeTimer = new Timer(_ => ReevaluateDueResets(), null, dueMs, Timeout.Infinite);
        }
        else
        {
            _wakeTimer.Change(dueMs, Timeout.Infinite);
        }
    }

    private string GenerateNextLabelLocked(string provider)
    {
        int n = 1;
        while (_config.AiQuotaSlots.Any(s => string.Equals(s.Label, $"{provider} #{n}", StringComparison.OrdinalIgnoreCase)))
        {
            n++;
        }
        return $"{provider} #{n}";
    }

    private static AiQuotaSlotSnapshot ToSnapshot(AiQuotaSlotConfig s, DateTimeOffset nowUtc)
    {
        if (s.NextResetUtcTicks <= nowUtc.UtcTicks)
        {
            return new AiQuotaSlotSnapshot(
                Id: s.Id,
                Provider: s.Provider,
                Label: s.Label,
                AccentHex: GetProviderAccentHex(s.Provider),
                ResetWindowMinutes: s.ResetWindowMinutes,
                NextResetUtc: null,
                Remaining: TimeSpan.Zero,
                IsReady: true,
                CooldownProgress01: null,
                NotifyOnReset: s.NotifyOnReset);
        }

        var resetUtc = new DateTimeOffset(s.NextResetUtcTicks, TimeSpan.Zero);
        var remaining = resetUtc - nowUtc;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        double totalSec = Math.Max(60.0, s.ResetWindowMinutes * 60.0);
        double elapsed01 = Math.Clamp(1.0 - (remaining.TotalSeconds / totalSec), 0.0, 1.0);

        return new AiQuotaSlotSnapshot(
            Id: s.Id,
            Provider: s.Provider,
            Label: s.Label,
            AccentHex: GetProviderAccentHex(s.Provider),
            ResetWindowMinutes: s.ResetWindowMinutes,
            NextResetUtc: resetUtc,
            Remaining: remaining,
            IsReady: false,
            CooldownProgress01: elapsed01,
            NotifyOnReset: s.NotifyOnReset);
    }

    public static int GetDefaultWindowMinutes(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "claude" => 300,
        "gemini" => 180,
        "chatgpt" => 180,
        _ => 180
    };

    public static string GetProviderAccentHex(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "chatgpt" => "#10A37F",
        "claude" => "#D97757",
        "gemini" => "#4E8DF5",
        _ => "#AF52DE"
    };

    public static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return "ChatGPT";
        string trimmed = provider.Trim();
        if (trimmed.Equals("chatgpt", StringComparison.OrdinalIgnoreCase)) return "ChatGPT";
        if (trimmed.Equals("claude", StringComparison.OrdinalIgnoreCase)) return "Claude";
        if (trimmed.Equals("gemini", StringComparison.OrdinalIgnoreCase)) return "Gemini";
        return trimmed.Length > 24 ? trimmed[..24] : trimmed;
    }

    public static string FormatDurationCompact(TimeSpan remaining, string lang)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return Strings.AiQuotaReadyBadge(lang);
        }

        bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        int totalHours = (int)Math.Floor(remaining.TotalHours);
        if (totalHours >= 1)
        {
            return isEn
                ? $"{totalHours:D2}h {remaining.Minutes:D2}m"
                : $"{totalHours:D2}s {remaining.Minutes:D2}d";
        }
        if (remaining.Minutes >= 1)
        {
            return isEn
                ? $"{remaining.Minutes:D2}m {remaining.Seconds:D2}s"
                : $"{remaining.Minutes:D2}d {remaining.Seconds:D2}sn";
        }
        return isEn ? $"{remaining.Seconds}s" : $"{remaining.Seconds}sn";
    }

    private void SafeSaveConfig()
    {
        try
        {
            _saveConfig();
        }
        catch
        {
            // Persistence failures should never crash UI interaction.
        }
    }

    private void NotifySlotsChanged() => SlotsChanged?.Invoke(this, GetSnapshots());

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _wakeTimer?.Dispose();
            _wakeTimer = null;
        }
    }
}
