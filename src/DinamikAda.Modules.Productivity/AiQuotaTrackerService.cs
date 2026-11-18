using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;

namespace DinamikAda.Modules.Productivity;

public sealed record AiQuotaModelBucketSnapshot(
    string BucketKey,
    string DisplayName,
    string? ModelsSummary,
    double WeeklyRemainingPercent,
    DateTimeOffset? WeeklyResetUtc,
    double? FiveHourRemainingPercent,
    DateTimeOffset? FiveHourResetUtc,
    string AccentHex)
{
    public bool HasExpiredWindow(DateTimeOffset nowUtc) => WeeklyResetUtc <= nowUtc || FiveHourResetUtc <= nowUtc;

    public double EffectiveRemainingPercent => FiveHourRemainingPercent.HasValue
        ? Math.Min(WeeklyRemainingPercent, FiveHourRemainingPercent.Value)
        : WeeklyRemainingPercent;
}

public sealed record AiQuotaSlotSnapshot(
    string Id,
    string Provider,
    string Label,
    string AccentHex,
    string AccountIdentifier,
    string PlanTier,
    int BillingDayOfMonth,
    int? DaysUntilBillingRenewal,
    string? BillingDateLabel,
    string PriceLabel,
    string QuotaUnit,
    double TotalQuota,
    double UsedQuota,
    double RemainingQuota,
    double RemainingPercent,
    int CriticalThresholdPercent,
    string LocalSourceMode,
    string Notes,
    bool IsCliSynced,
    string? CliBadgeText,
    string? SecondarySummary,
    string? ActiveModelName,
    int ResetWindowMinutes,
    DateTimeOffset? NextResetUtc,
    TimeSpan Remaining,
    bool IsReady,
    bool IsLow,
    bool IsExhausted,
    double? CooldownProgress01,
    bool NotifyOnReset,
    double? WeeklyRemainingPercent = null,
    DateTimeOffset? WeeklyResetUtc = null,
    ImmutableArray<AiQuotaModelBucketSnapshot> ModelBuckets = default,
    bool HasPrimaryQuota = true,
    bool QuotaAwaitingRefresh = false,
    DateTimeOffset? BlockingResetUtc = null,
    bool RequiresReconnect = false)
{
    public bool HasQuotaData => HasPrimaryQuota || WeeklyRemainingPercent.HasValue || !ModelBuckets.IsDefaultOrEmpty;
    public double EffectiveRemainingPercent => WeeklyRemainingPercent is { } weekly
        ? HasPrimaryQuota ? Math.Min(RemainingPercent, weekly) : weekly
        : RemainingPercent;
}

public sealed record AiQuotaSummary(
    int ReadyCount,
    int LowCount,
    int ExhaustedCount,
    int TotalCount,
    int CliSyncedCount,
    double TotalRemainingUnits,
    AiQuotaSlotSnapshot? SoonestCooldown,
    AiQuotaSlotSnapshot? AttentionSlot,
    string DockBadgeText,
    string DockBadgeHex);

public sealed record AiQuotaPreset(
    string Key,
    string DisplayNameTr,
    string DisplayNameEn,
    string Provider,
    string PlanTier,
    string QuotaUnit,
    double TotalQuota,
    int ResetWindowMinutes,
    string LocalSourceMode);

/// <summary>
/// Multi-account AI subscription usage, live OAuth account sign-in (isolated CODEX_HOME / CLAUDE_CONFIG_DIR profiles),
/// DPAPI API key balance polling, and real-time quota countdown tracker.
/// </summary>
public sealed class AiQuotaTrackerService : IDisposable
{
    private sealed record CliSlotOverlay(
        bool IsSynced,
        string? BadgeText,
        string? SecondarySummary,
        string? ActiveModelName,
        DateTimeOffset SyncedAtUtc,
        ImmutableArray<LocalAntigravityBucket> ModelBuckets = default,
        bool RequiresReconnect = false);

    public static IReadOnlyList<AiQuotaPreset> BuiltinPresets { get; } =
    [
        new("codex_cli", "ChatGPT OAuth (Canlı)", "ChatGPT OAuth (Live)", "ChatGPT", "Plus", "pct", 100, 300, "codex"),
        new("claude_code", "Claude hesabı", "Claude account", "Claude", "Pro", "pct", 100, 300, "claude"),
        new("gemini_cli", "Gemini hesabı", "Gemini account", "Gemini", "Pro", "msg", 1000, 1440, "gemini"),
        new("openrouter_api", "OpenRouter API (Canlı)", "OpenRouter API (Live)", "OpenRouter", "Credits", "credit", 20, 43200, "api"),
        new("deepseek_api", "DeepSeek API (Canlı)", "DeepSeek API (Live)", "DeepSeek", "API", "credit", 20, 43200, "api"),
        new("chatgpt_plus", "ChatGPT Plus (80/3s)", "ChatGPT Plus (80/3h)", "ChatGPT", "Plus", "use", 80, 180, "manual"),
        new("claude_pro", "Claude Pro (45/5s)", "Claude Pro (45/5h)", "Claude", "Pro", "msg", 45, 300, "manual"),
        new("antigravity_pro", "Antigravity (%100/5s)", "Antigravity (100%/5h)", "Antigravity", "Pro", "pct", 100, 300, "manual"),
        new("cursor_pro", "Cursor Pro (500/ay)", "Cursor Pro (500/mo)", "Cursor", "Pro", "credit", 500, 43200, "manual")
    ];

    private readonly AppConfig _config;
    private readonly Action _saveConfig;
    private readonly ITransientHudBus _hudBus;
    private readonly TimeProvider _timeProvider;
    private readonly LocalAiCliPaths? _cliPaths;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _liveRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _codexLoginGate = new(1, 1);
    private string? _codexLoginSlotId;
    private readonly ConcurrentDictionary<string, CliSlotOverlay> _cliOverlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lowAlertedWindowTicks = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _wakeTimer;
    private long? _nextScheduledResetTicks;
    private LocalAiCliUsageSnapshot? _lastCliSnapshot;
    private bool _disposed;

    public string Language { get; set; } = "tr";
    public event EventHandler<IReadOnlyList<AiQuotaSlotSnapshot>>? SlotsChanged;

    public LocalAiCliUsageSnapshot? LastCliSnapshot => _lastCliSnapshot;

    public bool HasActiveCooldowns
    {
        get
        {
            long nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            lock (_sync)
            {
                return _config.AiQuotaSlots.SelectMany(GetResetTicks).Any(t => t > nowTicks);
            }
        }
    }

    public AiQuotaTrackerService(
        AppConfig config,
        Action saveConfig,
        ITransientHudBus hudBus,
        TimeProvider? timeProvider = null,
        LocalAiCliPaths? cliPaths = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
        _hudBus = hudBus ?? throw new ArgumentNullException(nameof(hudBus));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cliPaths = cliPaths;
        Language = string.IsNullOrWhiteSpace(config.Language) ? "tr" : config.Language;
    }

    public void Start()
    {
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
                    PlanTier = "Plus",
                    QuotaUnit = "use",
                    TotalQuota = 80,
                    UsedQuota = 0,
                    ResetWindowMinutes = 180,
                    NotifyOnReset = true
                });
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_gpt2",
                    Provider = "ChatGPT",
                    Label = "ChatGPT #2",
                    PlanTier = "Plus",
                    QuotaUnit = "use",
                    TotalQuota = 80,
                    UsedQuota = 0,
                    ResetWindowMinutes = 180,
                    NotifyOnReset = true
                });
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_cld1",
                    Provider = "Claude",
                    Label = "Claude #1",
                    PlanTier = "Pro",
                    QuotaUnit = "msg",
                    TotalQuota = 45,
                    UsedQuota = 0,
                    ResetWindowMinutes = 300,
                    NotifyOnReset = true
                });
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_gem1",
                    Provider = "Gemini",
                    Label = "Gemini #1",
                    PlanTier = "Pro",
                    QuotaUnit = "use",
                    TotalQuota = 250,
                    UsedQuota = 0,
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

    /// <summary>
    /// Migrates old account settings without discarding saved accounts or usage.
    /// </summary>
    public async Task MigrateAndSyncRealAccountsAsync(bool allowLiveSubprocess = true, CancellationToken ct = default)
    {
        bool migrated = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (!_config.UserExplicitAccountsOnlyV4 || !_config.RealAccountsMigratedV3)
            {
                _config.UserExplicitAccountsOnlyV4 = true;
                _config.RealAccountsMigratedV3 = true;
                _config.AiQuotaSeeded = true;
                migrated = true;
            }
        }

        if (migrated)
        {
            SafeSaveConfig();
            NotifySlotsChanged();
        }

        if (allowLiveSubprocess && !ct.IsCancellationRequested)
        {
            await RefreshAllLiveAccountsAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task RefreshAllLiveAccountsAsync(CancellationToken ct = default)
    {
        await _liveRefreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RefreshLiveAccountsCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _liveRefreshGate.Release();
        }
    }

    private async Task RefreshLiveAccountsCoreAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_disposed) return;
        }

        var nowUtc = _timeProvider.GetUtcNow();
        SyncWithLocalCli();

        List<AiQuotaSlotConfig> liveSlots;
        lock (_sync)
        {
            if (_disposed) return;
            liveSlots = _config.AiQuotaSlots
                .Select(CloneSlot)
                .Where(s => ResolveEffectiveCliSource(s) is "codex" or "claude" or "antigravity" or "gemini" or "api")
                .ToList();
        }

        bool anyUpdated = false;
        string lang = Language;
        bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);

        foreach (var s in liveSlots)
        {
            if (ct.IsCancellationRequested) break;
            string mode = ResolveEffectiveCliSource(s);

            if (mode == "codex")
            {
                var live = await LocalAiCliUsageReader.QueryCodexAccountLiveAsync(
                    string.IsNullOrWhiteSpace(s.ProfileDirectory) ? "default" : s.ProfileDirectory,
                    nowUtc,
                    ct).ConfigureAwait(false);

                if (live.Detected)
                {
                    lock (_sync)
                    {
                        var target = ct.IsCancellationRequested ? null : FindLiveRefreshTarget(s, mode);
                        if (target is not null && target.Id != _codexLoginSlotId)
                        {
                            ApplyCodexReading(target, live, nowUtc);
                            ArmWakeTimerLocked(nowUtc.UtcTicks);
                            anyUpdated = true;
                        }
                    }
                }
            }
            else if (mode == "claude")
            {
                var live = await LocalAiCliUsageReader.QueryClaudeAccountLiveAsync(
                    string.IsNullOrWhiteSpace(s.ProfileDirectory) ? "default" : s.ProfileDirectory,
                    nowUtc,
                    ct).ConfigureAwait(false);

                lock (_sync)
                {
                    var target = ct.IsCancellationRequested ? null : FindLiveRefreshTarget(s, mode);
                    if (target is not null)
                    {
                        if (live.IsAuthenticated && !string.IsNullOrWhiteSpace(live.AccountEmail))
                        {
                            bool emailChanged = !string.Equals(target.AccountIdentifier, live.AccountEmail, StringComparison.OrdinalIgnoreCase);
                            if (emailChanged && !string.IsNullOrWhiteSpace(target.AccountIdentifier))
                            {
                                ClearReportedQuota(target);
                            }
                            target.AccountIdentifier = TrimMax(live.AccountEmail, 64);
                            if (IsGenericAccountLabel(target.Label) ||
                                (emailChanged && target.Label.StartsWith("Claude · ", StringComparison.OrdinalIgnoreCase)))
                            {
                                target.Label = FormatEmailShortLabel("Claude", live.AccountEmail);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(live.PlanTier))
                        {
                            target.PlanTier = TrimMax(live.PlanTier, 20);
                        }

                        if (live.HasReportedQuota)
                        {
                            target.HasReportedPrimaryQuota = live.SessionUsedPercent.HasValue;
                            target.QuotaUnit = "pct";
                            target.TotalQuota = 100;
                            target.UsedQuota = live.SessionUsedPercent ?? 0;
                            target.ResetWindowMinutes = 300;
                            target.NextResetUtcTicks = live.SessionQuotaResetUtc?.UtcTicks ?? 0;
                            target.WeeklyUsedPercent = live.WeeklyUsedPercent;
                            target.WeeklyResetUtcTicks = live.WeeklyQuotaResetUtc?.UtcTicks ?? 0;
                        }

                        bool hasCachedQuota = target.HasReportedPrimaryQuota == true ||
                            (target.HasReportedPrimaryQuota.HasValue && target.WeeklyUsedPercent.HasValue);
                        string badge = live.HasReportedQuota
                            ? Strings.Pick(lang, "Canlı kota", "Live quota")
                            : hasCachedQuota ? Strings.Pick(lang, "Son ölçüm", "Last reading")
                            : live.IsAuthenticated ? Strings.Pick(lang, "Kota okunamadı", "Could not read quota")
                            : Strings.Pick(lang, "Giriş gerekli", "Sign-in required");

                        _cliOverlays[target.Id] = new CliSlotOverlay(
                            IsSynced: live.HasReportedQuota,
                            BadgeText: badge,
                            SecondarySummary: null,
                            ActiveModelName: null,
                            SyncedAtUtc: nowUtc);

                        ArmWakeTimerLocked(nowUtc.UtcTicks);
                        anyUpdated = true;
                    }
                }
            }
            else if (mode is "antigravity" or "gemini")
            {
                var live = await LocalAiCliUsageReader.QueryAntigravityAccountLiveAsync(
                    string.IsNullOrWhiteSpace(s.ProfileDirectory) ? "default" : s.ProfileDirectory,
                    nowUtc,
                    ct,
                    targetEmail: s.AccountIdentifier).ConfigureAwait(false);

                if (live.Detected)
                {
                    lock (_sync)
                    {
                        var target = ct.IsCancellationRequested ? null : FindLiveRefreshTarget(s, mode);
                        if (target is not null)
                        {
                            bool shouldAdoptLiveEmail =
                                !string.IsNullOrWhiteSpace(live.AccountEmail) &&
                                (string.IsNullOrWhiteSpace(target.AccountIdentifier) ||
                                 string.Equals(target.AccountIdentifier, live.AccountEmail, StringComparison.OrdinalIgnoreCase));

                            if (shouldAdoptLiveEmail && !string.IsNullOrWhiteSpace(live.AccountEmail))
                            {
                                bool emailChanged = !string.Equals(target.AccountIdentifier, live.AccountEmail, StringComparison.OrdinalIgnoreCase);
                                target.AccountIdentifier = TrimMax(live.AccountEmail, 64);
                                if (IsGenericAccountLabel(target.Label) ||
                                    (emailChanged && target.Label.StartsWith($"{target.Provider} · ", StringComparison.OrdinalIgnoreCase)))
                                {
                                    target.Label = FormatEmailShortLabel(target.Provider, live.AccountEmail);
                                }
                                if (!string.IsNullOrWhiteSpace(live.PlanTier))
                                {
                                    target.PlanTier = TrimMax(live.PlanTier, 20);
                                }
                            }

                            bool isSlotAuth = live.IsAuthenticated && shouldAdoptLiveEmail;
                            bool hasBuckets = !live.ModelBuckets.IsDefaultOrEmpty;
                            if (isSlotAuth && hasBuckets)
                            {
                                ApplyLiveAntigravityBucketsToConfig(target, live.ModelBuckets, nowUtc);
                            }

                            string secondary = mode == "antigravity"
                                ? (isSlotAuth
                                    ? (hasBuckets
                                        ? (isEn
                                            ? $"Google OAuth · {live.ModelBuckets.Length} model pools"
                                            : $"Google OAuth · {live.ModelBuckets.Length} model havuzu")
                                        : (isEn
                                            ? $"Google OAuth · 5h: {live.TurnsLast5Hours} req · 7d: {live.TurnsLast7Days} req"
                                            : $"Google OAuth · 5s: {live.TurnsLast5Hours} istek · 7g: {live.TurnsLast7Days} istek"))
                                    : Strings.Pick(lang, "Karttan '🔑' ile Google hesabınıza giriş yapın", "Click '🔑' on card to sign in with Google"))
                                : (isSlotAuth
                                    ? (hasBuckets
                                        ? (isEn
                                            ? $"Google OAuth · {live.ModelBuckets.Length} model pools"
                                            : $"Google OAuth · {live.ModelBuckets.Length} model havuzu")
                                        : (isEn ? "Google OAuth · Connected" : "Google OAuth · Bağlı"))
                                    : Strings.Pick(lang, "Karttan '🔑' ile Gemini hesabınıza giriş yapın", "Click '🔑' on card to sign in to Gemini"));

                            _cliOverlays[target.Id] = new CliSlotOverlay(
                                IsSynced: isSlotAuth && live.HasLiveQuota,
                                BadgeText: isSlotAuth && hasBuckets
                                    ? live.HasLiveQuota ? Strings.Pick(lang, "Canlı kota", "Live quota") : Strings.Pick(lang, "Son ölçüm", "Last reading")
                                    : null,
                                SecondarySummary: secondary,
                                ActiveModelName: live.ActiveModel,
                                SyncedAtUtc: nowUtc,
                                ModelBuckets: isSlotAuth ? live.ModelBuckets : default);

                            ArmWakeTimerLocked(nowUtc.UtcTicks);
                            anyUpdated = true;
                        }
                    }
                }
            }
            else if (mode == "api" && !string.IsNullOrWhiteSpace(s.EncryptedApiKey))
            {
                string? plainKey = LocalAiCliUsageReader.UnprotectSecret(s.EncryptedApiKey);
                if (!string.IsNullOrWhiteSpace(plainKey))
                {
                    var apiSnap = await LocalAiCliUsageReader.QueryApiAccountLiveAsync(s.Provider, plainKey, ct).ConfigureAwait(false);
                    if (apiSnap.Success)
                    {
                        lock (_sync)
                        {
                            var target = ct.IsCancellationRequested ? null : FindLiveRefreshTarget(s, mode);
                            if (target is not null)
                            {
                                if (!string.IsNullOrWhiteSpace(apiSnap.PlanTier)) target.PlanTier = apiSnap.PlanTier;
                                target.HasReportedPrimaryQuota = apiSnap.TotalQuota.HasValue && apiSnap.UsedQuota.HasValue;
                                if (apiSnap.TotalQuota.HasValue) target.TotalQuota = apiSnap.TotalQuota.Value;
                                if (apiSnap.UsedQuota.HasValue) target.UsedQuota = Math.Clamp(apiSnap.UsedQuota.Value, 0.0, target.TotalQuota);
                                target.QuotaUnit = apiSnap.QuotaUnit;
                                _cliOverlays[target.Id] = new CliSlotOverlay(
                                    IsSynced: true,
                                    BadgeText: "● API Canlı",
                                    SecondarySummary: apiSnap.SecondarySummary,
                                    ActiveModelName: null,
                                    SyncedAtUtc: nowUtc);
                                anyUpdated = true;
                            }
                        }
                    }
                }
            }
        }

        if (anyUpdated)
        {
            SafeSaveConfig();
            NotifySlotsChanged();
        }
    }

    // Called with _sync held. An in-flight response must not update a removed or reconfigured account.
    private AiQuotaSlotConfig? FindLiveRefreshTarget(AiQuotaSlotConfig source, string mode)
    {
        if (_disposed) return null;
        return _config.AiQuotaSlots.FirstOrDefault(slot =>
            string.Equals(slot.Id, source.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(slot.Provider, source.Provider, StringComparison.OrdinalIgnoreCase) &&
            ResolveEffectiveCliSource(slot) == mode &&
            string.Equals(slot.ProfileDirectory, source.ProfileDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(slot.AccountIdentifier, source.AccountIdentifier, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(slot.EncryptedApiKey, source.EncryptedApiKey, StringComparison.Ordinal));
    }

    private bool IsRepeatedCodexSlot(AiQuotaSlotConfig slot)
    {
        if (string.IsNullOrWhiteSpace(slot.AccountIdentifier)) return false;
        lock (_sync)
        {
            var first = _config.AiQuotaSlots
                .Where(other => ResolveEffectiveCliSource(other) == "codex" &&
                    string.Equals(other.AccountIdentifier, slot.AccountIdentifier, StringComparison.OrdinalIgnoreCase))
                .OrderBy(other => _cliOverlays.GetValueOrDefault(other.Id)?.RequiresReconnect == true)
                .ThenBy(other => string.IsNullOrWhiteSpace(other.ProfileDirectory) ||
                    string.Equals(other.ProfileDirectory, "default", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            return first is not null && first.Id != slot.Id;
        }
    }

    internal void ApplyCodexReading(AiQuotaSlotConfig slot, LocalCodexTelemetry live, DateTimeOffset nowUtc)
    {
        string lang = Language;
        if (!live.AccountReadSucceeded && _cliOverlays.TryGetValue(slot.Id, out var previous) && previous.RequiresReconnect)
        {
            return;
        }

        string email = TrimMax(live.AccountEmail, 64);
        if (live.AccountReadSucceeded)
        {
            bool mismatch = email.Length > 0 && slot.AccountIdentifier.Length > 0 &&
                !string.Equals(slot.AccountIdentifier, email, StringComparison.OrdinalIgnoreCase);
            if (mismatch || email.Length == 0)
            {
                ClearReportedQuota(slot);
                _cliOverlays[slot.Id] = new CliSlotOverlay(false,
                    mismatch ? Strings.Pick(lang, "Oturum farklı bir hesaba ait", "Signed in to a different account")
                        : Strings.Pick(lang, "Giriş gerekli", "Sign-in required"),
                    null, null, nowUtc, RequiresReconnect: true);
                return;
            }

            slot.AccountIdentifier = email;
            if (IsGenericAccountLabel(slot.Label)) slot.Label = FormatEmailShortLabel("ChatGPT", email);
            if (!string.IsNullOrWhiteSpace(live.PlanTier)) slot.PlanTier = TrimMax(live.PlanTier, 20);
        }

        bool hasQuota = live.HasLiveQuota && live.AccountReadSucceeded && email.Length > 0;
        if (hasQuota)
        {
            slot.HasReportedPrimaryQuota = live.PrimaryUsedPercent.HasValue;
            slot.QuotaUnit = "pct";
            slot.TotalQuota = 100;
            slot.UsedQuota = Math.Clamp(live.PrimaryUsedPercent ?? 0, 0, 100);
            slot.ResetWindowMinutes = Math.Clamp(live.PrimaryWindowMinutes ?? 300, AiQuotaSlotConfig.MinWindowMinutes, AiQuotaSlotConfig.MaxWindowMinutes);
            slot.NextResetUtcTicks = live.PrimaryResetsAtUtc?.UtcTicks ?? 0;
            slot.WeeklyUsedPercent = live.SecondaryUsedPercent;
            slot.WeeklyResetUtcTicks = live.SecondaryResetsAtUtc?.UtcTicks ?? 0;
        }

        string badge = hasQuota ? Strings.Pick(lang, "Canlı kota", "Live quota")
            : live.IsAuthenticated ? Strings.Pick(lang, "Son ölçüm", "Last reading")
            : Strings.Pick(lang, "Giriş gerekli", "Sign-in required");
        string? details = live.AvailableResetCredits > 0
            ? Strings.Pick(lang, $"{live.AvailableResetCredits} sıfırlama kredisi", $"{live.AvailableResetCredits} reset credits")
            : live.ActiveModel;
        _cliOverlays[slot.Id] = new CliSlotOverlay(hasQuota, badge, details, live.ActiveModel, nowUtc);
    }

    private async Task<AiQuotaSlotSnapshot?> LoginCodexSlotAsync(AiQuotaSlotConfig source, string? expectedEmail, CancellationToken ct)
    {
        string lang = Language;
        if (!await _codexLoginGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            PublishCodexLoginStatus(source.Id, false, Strings.Pick(lang, "Açık olan ChatGPT girişini tamamlayın.", "Finish the ChatGPT sign-in already in progress."));
            return null;
        }

        string profile = Path.Combine(LocalAiCliPaths.GetIsolatedProfilesRoot(), $"{source.Id}-{Guid.NewGuid():N}");
        bool committed = false;
        string? failure = null;
        CliSlotOverlay? previous = null;
        try
        {
            lock (_sync)
            {
                if (FindLiveRefreshTarget(source, "codex") is null) return null;
                _codexLoginSlotId = source.Id;
                _cliOverlays.TryGetValue(source.Id, out previous);
                _cliOverlays[source.Id] = new CliSlotOverlay(false,
                    Strings.Pick(lang, "Tarayıcıda giriş bekleniyor", "Waiting for browser sign-in"),
                    null, null, _timeProvider.GetUtcNow());
            }
            NotifySlotsChanged();

            var result = await LocalAiCliUsageReader.RunCodexOAuthLoginAsync(profile, ct: ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return null;
            string email = TrimMax(result?.AccountEmail, 64);
            lock (_sync)
            {
                var target = FindLiveRefreshTarget(source, "codex");
                if (target is null) return null;
                if (result is not { IsAuthenticated: true, AccountReadSucceeded: true } || email.Length == 0)
                {
                    failure = Strings.Pick(lang, "ChatGPT girişi tamamlanmadı. Tekrar bağlanabilirsin.", "ChatGPT sign-in did not complete. You can reconnect.");
                }
                else if (!string.IsNullOrWhiteSpace(expectedEmail) &&
                    !string.Equals(expectedEmail.Trim(), email, StringComparison.OrdinalIgnoreCase))
                {
                    failure = Strings.Pick(lang, "Tarayıcıda girilen hesap, kartta yazdığın e-postayla eşleşmiyor.", "The browser account does not match the email entered on the card.");
                }
                else if (_config.AiQuotaSlots.Any(other => other.Id != source.Id && ResolveEffectiveCliSource(other) == "codex" &&
                    string.Equals(other.AccountIdentifier, email, StringComparison.OrdinalIgnoreCase) && !IsRepeatedCodexSlot(other) &&
                    (_cliOverlays.GetValueOrDefault(other.Id)?.RequiresReconnect != true)))
                {
                    failure = Strings.Pick(lang, "Tarayıcı mevcut hesabı seçti. Hesap değiştirip tekrar bağlan.", "The browser selected an existing account. Switch accounts and reconnect.");
                }
                else
                {
                    ClearReportedQuota(target);
                    if (IsGenericAccountLabel(target.Label) || target.Label.StartsWith("ChatGPT · ", StringComparison.OrdinalIgnoreCase))
                        target.Label = FormatEmailShortLabel("ChatGPT", email);
                    target.AccountIdentifier = email;
                    target.ProfileDirectory = profile;
                    ApplyCodexReading(target, result, _timeProvider.GetUtcNow());
                    ArmWakeTimerLocked(_timeProvider.GetUtcNow().UtcTicks);
                    committed = true;
                }
            }
            if (committed)
            {
                SafeSaveConfig();
                PublishCodexLoginStatus(source.Id, true, email);
            }
            else if (failure is not null)
            {
                PublishCodexLoginStatus(source.Id, false, failure);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (_codexLoginSlotId == source.Id)
                {
                    if (!committed && FindLiveRefreshTarget(source, "codex") is not null)
                    {
                        if (previous is not null) _cliOverlays[source.Id] = previous;
                        else if (failure is not null) _cliOverlays[source.Id] = new CliSlotOverlay(false,
                            Strings.Pick(lang, "Yeniden bağla", "Reconnect"), failure, null, _timeProvider.GetUtcNow(),
                            RequiresReconnect: source.HasReportedPrimaryQuota != true && !source.WeeklyUsedPercent.HasValue);
                        else _cliOverlays.TryRemove(source.Id, out _);
                    }
                    _codexLoginSlotId = null;
                }
            }
            _codexLoginGate.Release();
            if (!committed)
            {
                // Only remove the temporary profile created for this particular attempt.
                try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            NotifySlotsChanged();
        }

        return GetSnapshots().FirstOrDefault(s => s.Id == source.Id);
    }

    private void PublishCodexLoginStatus(string id, bool success, string subtitle)
    {
        _hudBus.Publish(new TransientHudEvent(
            CoalesceKey: $"ai-oauth:{id}",
            Kind: HudKind.WidgetAlert,
            Title: success ? Strings.Pick(Language, "ChatGPT hesabı bağlandı", "ChatGPT account connected")
                : Strings.Pick(Language, "ChatGPT hesabı bağlanamadı", "ChatGPT account could not connect"),
            Subtitle: subtitle,
            AccentHex: success ? "#30D158" : "#FF9F0A",
            DurationMs: 4800,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: success ? "\uE930" : "\uE7BA",
            PrimaryActionId: "ai.open-tab",
            PrimaryActionLabel: Strings.TabAiQuota(Language)));
    }

    /// <summary>
    /// Creates a new isolated account slot for the specified provider (ChatGPT or Claude) and immediately opens
    /// the official OAuth browser login flow so the user can sign into multiple accounts one by one.
    /// </summary>
    public async Task<AiQuotaSlotSnapshot?> ConnectNewOAuthAccountAsync(
        string provider,
        string? preferredEmail = null,
        string? customLabel = null,
        CancellationToken ct = default)
    {
        string cleanProv = NormalizeProvider(provider);
        string mode = GetDefaultOAuthModeForProvider(cleanProv);
        if (mode == "manual")
        {
            return null;
        }

        string slotId = "ai_" + Guid.NewGuid().ToString("N")[..6];
        string profileDir;
        lock (_sync)
        {
            if (_disposed || _config.AiQuotaSlots.Count >= AiQuotaSlotConfig.MaxSlotCount) return null;
            profileDir = Path.Combine(LocalAiCliPaths.GetIsolatedProfilesRoot(), slotId);

            (string unit, double total, int winMin) = mode switch
            {
                "codex" => ("pct", 100.0, 300),
                "claude" => ("pct", 100.0, 300),
                "antigravity" => ("pct", 100.0, 300),
                "gemini" => ("msg", 1000.0, 1440),
                _ => ("use", 80.0, 180)
            };

            string resolvedLabel = !string.IsNullOrWhiteSpace(customLabel)
                ? TrimMax(customLabel, 36)
                : (!string.IsNullOrWhiteSpace(preferredEmail) && preferredEmail.Contains('@')
                    ? FormatEmailShortLabel(cleanProv, preferredEmail.Trim())
                    : GenerateNextLabelLocked(cleanProv));

            var created = new AiQuotaSlotConfig
            {
                Id = slotId,
                Provider = cleanProv,
                Label = resolvedLabel,
                AccountIdentifier = TrimMax(preferredEmail, 64),
                PlanTier = AiQuotaSlotConfig.GetDefaultPlanTier(cleanProv),
                QuotaUnit = unit,
                TotalQuota = total,
                UsedQuota = 0,
                ResetWindowMinutes = winMin,
                LocalSourceMode = mode,
                ProfileDirectory = profileDir,
                NotifyOnReset = true
            };
            _config.AiQuotaSlots.Add(created);
            _config.AiQuotaSeeded = true;
            _config.RealAccountsMigratedV3 = true;
            _config.UserExplicitAccountsOnlyV4 = true;
        }

        SafeSaveConfig();
        return await LoginSlotOAuthAsync(slotId, ct, expectedEmail: mode == "codex" ? preferredEmail : null).ConfigureAwait(false);
    }

    public static string GetDefaultOAuthModeForProvider(string provider) => NormalizeProvider(provider) switch
    {
        "ChatGPT" => "codex",
        "Claude" => "claude",
        "Antigravity" => "antigravity",
        "Gemini" => "gemini",
        _ => "manual"
    };

    /// <summary>
    /// Launches interactive OAuth browser sign-in for an existing slot's isolated profile and syncs real account data upon completion.
    /// </summary>
    public async Task<AiQuotaSlotSnapshot?> LoginSlotOAuthAsync(string slotId, CancellationToken ct = default, string? expectedEmail = null)
    {
        AiQuotaSlotConfig? snapshotSlot;
        lock (_sync)
        {
            if (_disposed) return null;
            var s = _config.AiQuotaSlots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));
            if (s is null) return null;
            string expectedMode = GetDefaultOAuthModeForProvider(s.Provider);
            if (s.LocalSourceMode == "manual" || (expectedMode != "manual" && s.LocalSourceMode != expectedMode))
            {
                s.LocalSourceMode = expectedMode;
            }
            if (string.IsNullOrWhiteSpace(s.ProfileDirectory))
            {
                bool otherUsesDefault = _config.AiQuotaSlots.Any(x =>
                    !string.Equals(x.Id, s.Id, StringComparison.OrdinalIgnoreCase) &&
                    ResolveEffectiveCliSource(x) == ResolveEffectiveCliSource(s) &&
                    (string.IsNullOrWhiteSpace(x.ProfileDirectory) || string.Equals(x.ProfileDirectory, "default", StringComparison.OrdinalIgnoreCase)));
                s.ProfileDirectory = otherUsesDefault
                    ? Path.Combine(LocalAiCliPaths.GetIsolatedProfilesRoot(), s.Id)
                    : "default";
            }
            snapshotSlot = CloneSlot(s);
        }

        string mode = ResolveEffectiveCliSource(snapshotSlot);
        if (mode == "manual")
        {
            return GetSnapshots().FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));
        }

        if (mode == "codex")
        {
            return await LoginCodexSlotAsync(snapshotSlot, expectedEmail, ct).ConfigureAwait(false);
        }

        string lang = Language;
        _cliOverlays[slotId] = new CliSlotOverlay(
            IsSynced: false,
            BadgeText: Strings.Pick(lang, "🔑 Giriş Bekleniyor...", "🔑 Waiting Login..."),
            SecondarySummary: Strings.Pick(lang, "Tarayıcıda hesabınıza giriş yapın...", "Complete sign-in in your browser..."),
            ActiveModelName: null,
            SyncedAtUtc: _timeProvider.GetUtcNow());
        NotifySlotsChanged();

        if (mode == "claude")
        {
            _hudBus.Publish(new TransientHudEvent(
                CoalesceKey: $"ai-oauth-start:{slotId}",
                Kind: HudKind.WidgetAlert,
                Title: Strings.Pick(lang, "Claude.ai Girişi Başlatıldı", "Claude.ai Login Started"),
                Subtitle: Strings.Pick(lang, "Tarayıcıda Claude hesabınıza giriş yapın", "Sign in to your Claude account in the browser"),
                AccentHex: "#D97757",
                DurationMs: 4200,
                InteractiveOnHover: false,
                IsSticky: false,
                IconGlyph: "\uE77B"));

            var result = await LocalAiCliUsageReader.RunClaudeOAuthLoginAsync(
                snapshotSlot.ProfileDirectory,
                snapshotSlot.AccountIdentifier,
                ct).ConfigureAwait(false);

            if (result.IsAuthenticated && !string.IsNullOrWhiteSpace(result.AccountEmail))
            {
                lock (_sync)
                {
                    var target = _config.AiQuotaSlots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));
                    if (target is not null)
                    {
                        if (!string.Equals(target.AccountIdentifier, result.AccountEmail, StringComparison.OrdinalIgnoreCase))
                        {
                            ClearReportedQuota(target);
                            _cliOverlays.TryRemove(target.Id, out _);
                        }
                        target.AccountIdentifier = TrimMax(result.AccountEmail, 64);
                        target.Label = FormatEmailShortLabel(target.Provider, result.AccountEmail);
                        if (!string.IsNullOrWhiteSpace(result.PlanTier))
                        {
                            target.PlanTier = TrimMax(result.PlanTier, 20);
                        }
                    }
                }
                SafeSaveConfig();
            }

            await RefreshAllLiveAccountsAsync(ct).ConfigureAwait(false);
            var updated = GetSnapshots().FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));
            if (result.IsAuthenticated && updated is not null)
            {
                _hudBus.Publish(new TransientHudEvent(
                    CoalesceKey: $"ai-oauth-ok:{slotId}",
                    Kind: HudKind.WidgetAlert,
                    Title: Strings.Pick(lang, $"Claude Bağlandı · {updated.Label}", $"Claude Connected · {updated.Label}"),
                    Subtitle: $"{updated.AccountIdentifier} · {updated.PlanTier}",
                    AccentHex: "#D97757",
                    DurationMs: 4200,
                    InteractiveOnHover: true,
                    IsSticky: false,
                    IconGlyph: "\uE930",
                    PrimaryActionId: "ai.open-tab",
                    PrimaryActionLabel: Strings.TabAiQuota(lang)));
            }
            return updated;
        }
        else if (mode is "antigravity" or "gemini")
        {
            string provTitle = mode == "antigravity" ? "Antigravity (Google)" : "Gemini (Google)";
            string accent = mode == "antigravity" ? "#38BDF8" : "#4E8DF5";
            _hudBus.Publish(new TransientHudEvent(
                CoalesceKey: $"ai-oauth-start:{slotId}",
                Kind: HudKind.WidgetAlert,
                Title: Strings.Pick(lang, $"{provTitle} Girişi Başlatıldı", $"{provTitle} Login Started"),
                Subtitle: Strings.Pick(lang, "Tarayıcıda Google hesabınıza giriş yapın ve yetki kodunu kopyalayın", "Sign in to your Google account in the browser and copy the auth code"),
                AccentHex: accent,
                DurationMs: 4200,
                InteractiveOnHover: false,
                IsSticky: false,
                IconGlyph: "\uE77B"));

            var result = await LocalAiCliUsageReader.RunGoogleOAuthLoginAsync(snapshotSlot.ProfileDirectory, ct).ConfigureAwait(false);
            if (result.IsAuthenticated && !string.IsNullOrWhiteSpace(result.AccountEmail))
            {
                lock (_sync)
                {
                    var target = _config.AiQuotaSlots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));
                    if (target is not null)
                    {
                        target.AccountIdentifier = TrimMax(result.AccountEmail, 64);
                        target.Label = FormatEmailShortLabel(target.Provider, result.AccountEmail);
                        if (!string.IsNullOrWhiteSpace(result.PlanTier))
                        {
                            target.PlanTier = TrimMax(result.PlanTier, 20);
                        }
                        if (!result.ModelBuckets.IsDefaultOrEmpty)
                        {
                            ApplyLiveAntigravityBucketsToConfig(target, result.ModelBuckets, _timeProvider.GetUtcNow());
                        }
                    }
                }
                SafeSaveConfig();
            }

            await RefreshAllLiveAccountsAsync(ct).ConfigureAwait(false);
            var updated = GetSnapshots().FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));
            if (result.IsAuthenticated && updated is not null)
            {
                _hudBus.Publish(new TransientHudEvent(
                    CoalesceKey: $"ai-oauth-ok:{slotId}",
                    Kind: HudKind.WidgetAlert,
                    Title: Strings.Pick(lang, $"{snapshotSlot.Provider} Bağlandı · {updated.Label}", $"{snapshotSlot.Provider} Connected · {updated.Label}"),
                    Subtitle: $"{updated.AccountIdentifier} · {updated.PlanTier}",
                    AccentHex: accent,
                    DurationMs: 4200,
                    InteractiveOnHover: true,
                    IsSticky: false,
                    IconGlyph: "\uE930",
                    PrimaryActionId: "ai.open-tab",
                    PrimaryActionLabel: Strings.TabAiQuota(lang)));
            }
            return updated;
        }

        return GetSnapshots().FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));
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
        int lowCount = 0;
        int exhaustedCount = 0;
        int cliCount = 0;
        double totalRemUnits = 0;
        AiQuotaSlotSnapshot? soonest = null;
        AiQuotaSlotSnapshot? attention = null;

        foreach (var s in snaps)
        {
            if (s.IsCliSynced) cliCount++;
            if (s.HasQuotaData && !s.QuotaAwaitingRefresh && s.TotalQuota > 0 && s.QuotaUnit != "pct")
            {
                totalRemUnits += s.RemainingQuota;
            }

            if (s.IsReady)
            {
                readyCount++;
                if (s.IsLow)
                {
                    lowCount++;
                    if (attention is null || (!attention.IsExhausted && s.EffectiveRemainingPercent < attention.EffectiveRemainingPercent))
                    {
                        attention = s;
                    }
                }
            }
            else if (s.IsExhausted)
            {
                exhaustedCount++;
                if (s.BlockingResetUtc.HasValue && (soonest is null || s.BlockingResetUtc < soonest.BlockingResetUtc))
                {
                    soonest = s;
                }
                if (attention is null || !attention.IsExhausted)
                {
                    attention = s;
                }
            }
        }

        string dockBadgeText = snaps.Count > 0 ? $"{readyCount}/{snaps.Count}" : string.Empty;
        string dockBadgeHex = exhaustedCount > 0
            ? "#FF453A"
            : (lowCount > 0 ? "#FF9F0A" : readyCount < snaps.Count ? "#94A3B8" : "#30D158");

        return new AiQuotaSummary(
            ReadyCount: readyCount,
            LowCount: lowCount,
            ExhaustedCount: exhaustedCount,
            TotalCount: snaps.Count,
            CliSyncedCount: cliCount,
            TotalRemainingUnits: totalRemUnits,
            SoonestCooldown: soonest,
            AttentionSlot: attention,
            DockBadgeText: dockBadgeText,
            DockBadgeHex: dockBadgeHex);
    }

    public AiQuotaSlotSnapshot? AddSlot(
        string provider,
        string? label = null,
        int? resetWindowMinutes = null,
        bool notifyOnReset = true,
        string? accountIdentifier = null,
        string? planTier = null,
        int billingDayOfMonth = 0,
        string? priceLabel = null,
        string quotaUnit = "use",
        double? totalQuota = null,
        double? remainingQuota = null,
        int criticalThresholdPercent = 20,
        string localSourceMode = "manual",
        string? notes = null,
        string? plainApiKey = null)
    {
        string cleanProvider = NormalizeProvider(provider);
        int windowMin = Math.Clamp(
            resetWindowMinutes ?? GetDefaultWindowMinutes(cleanProvider),
            AiQuotaSlotConfig.MinWindowMinutes,
            AiQuotaSlotConfig.MaxWindowMinutes);

        double cleanTotal = totalQuota.HasValue
            ? Math.Clamp(totalQuota.Value, 0.0, 1_000_000.0)
            : AiQuotaSlotConfig.GetDefaultTotalQuota(cleanProvider);
        double cleanUsed = remainingQuota.HasValue && cleanTotal > 0
            ? Math.Clamp(cleanTotal - remainingQuota.Value, 0.0, cleanTotal)
            : 0.0;

        string cleanUnit = quotaUnit is "use" or "pct" or "msg" or "credit" ? quotaUnit : "use";
        string cleanPlan = string.IsNullOrWhiteSpace(planTier)
            ? AiQuotaSlotConfig.GetDefaultPlanTier(cleanProvider)
            : TrimMax(planTier, 20);
        string cleanSource = !string.IsNullOrWhiteSpace(plainApiKey)
            ? "api"
            : NormalizeLocalSource(localSourceMode);

        var nowUtc = _timeProvider.GetUtcNow();
        long initialResetTicks = 0;
        if (cleanTotal > 0 && cleanUsed > 0 && windowMin > 0)
        {
            initialResetTicks = nowUtc.AddMinutes(windowMin).UtcTicks;
        }

        AiQuotaSlotConfig created;
        lock (_sync)
        {
            if (_disposed || _config.AiQuotaSlots.Count >= AiQuotaSlotConfig.MaxSlotCount)
            {
                return null;
            }

            string slotId = "ai_" + Guid.NewGuid().ToString("N")[..6];
            string cleanLabel = string.IsNullOrWhiteSpace(label)
                ? GenerateNextLabelLocked(cleanProvider)
                : TrimMax(label, 36);

            string profileDir = string.Empty;
            if (cleanSource is "codex" or "claude" or "antigravity" or "gemini")
            {
                bool hasDefault = _config.AiQuotaSlots.Any(x =>
                    ResolveEffectiveCliSource(x) == cleanSource &&
                    (string.IsNullOrWhiteSpace(x.ProfileDirectory) || string.Equals(x.ProfileDirectory, "default", StringComparison.OrdinalIgnoreCase)));
                profileDir = (cleanSource != "codex" || hasDefault)
                    ? Path.Combine(LocalAiCliPaths.GetIsolatedProfilesRoot(), slotId)
                    : "default";
            }

            created = new AiQuotaSlotConfig
            {
                Id = slotId,
                Provider = cleanProvider,
                Label = cleanLabel,
                AccountIdentifier = TrimMax(accountIdentifier, 64),
                PlanTier = cleanPlan,
                BillingDayOfMonth = Math.Clamp(billingDayOfMonth, 0, 31),
                PriceLabel = TrimMax(priceLabel, 20),
                QuotaUnit = cleanUnit,
                TotalQuota = cleanTotal,
                UsedQuota = cleanUsed,
                CriticalThresholdPercent = Math.Clamp(criticalThresholdPercent, 5, 50),
                LocalSourceMode = cleanSource,
                ProfileDirectory = profileDir,
                EncryptedApiKey = !string.IsNullOrWhiteSpace(plainApiKey) ? LocalAiCliUsageReader.ProtectSecret(plainApiKey) : string.Empty,
                Notes = TrimMax(notes, 120),
                ResetWindowMinutes = windowMin,
                NextResetUtcTicks = initialResetTicks,
                NotifyOnReset = notifyOnReset
            };
            _config.AiQuotaSlots.Add(created);
            _config.AiQuotaSeeded = true;
            _config.RealAccountsMigratedV3 = true;
            _config.UserExplicitAccountsOnlyV4 = true;
            ArmWakeTimerLocked(nowUtc.UtcTicks);
        }

        SafeSaveConfig();
        if (cleanSource != "manual" && _lastCliSnapshot is not null)
        {
            SyncWithLocalCli(_lastCliSnapshot);
        }
        NotifySlotsChanged();
        return ToSnapshot(created, _timeProvider.GetUtcNow());
    }

    public bool UpdateSlot(
        string id,
        string provider,
        string label,
        int resetWindowMinutes,
        bool notifyOnReset,
        string? accountIdentifier = null,
        string? planTier = null,
        int? billingDayOfMonth = null,
        string? priceLabel = null,
        string? quotaUnit = null,
        double? totalQuota = null,
        double? remainingQuota = null,
        int? criticalThresholdPercent = null,
        string? localSourceMode = null,
        string? notes = null,
        string? plainApiKey = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        string cleanProvider = NormalizeProvider(provider);
        int cleanWindow = Math.Clamp(resetWindowMinutes, AiQuotaSlotConfig.MinWindowMinutes, AiQuotaSlotConfig.MaxWindowMinutes);
        string cleanLabel = string.IsNullOrWhiteSpace(label) ? cleanProvider : TrimMax(label, 36);

        var nowUtc = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            if (_disposed) return false;
            var slot = _config.AiQuotaSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return false;

            string previousMode = ResolveEffectiveCliSource(slot);
            string previousProvider = slot.Provider;
            string previousAccount = slot.AccountIdentifier;
            slot.Provider = cleanProvider;
            slot.Label = cleanLabel;
            slot.NotifyOnReset = notifyOnReset;

            if (accountIdentifier is not null) slot.AccountIdentifier = TrimMax(accountIdentifier, 64);
            if (!string.IsNullOrWhiteSpace(planTier)) slot.PlanTier = TrimMax(planTier, 20);
            if (billingDayOfMonth.HasValue) slot.BillingDayOfMonth = Math.Clamp(billingDayOfMonth.Value, 0, 31);
            if (priceLabel is not null) slot.PriceLabel = TrimMax(priceLabel, 20);
            if (criticalThresholdPercent.HasValue) slot.CriticalThresholdPercent = Math.Clamp(criticalThresholdPercent.Value, 5, 50);
            if (localSourceMode is not null) slot.LocalSourceMode = NormalizeLocalSource(localSourceMode);
            if (!string.IsNullOrWhiteSpace(plainApiKey))
            {
                slot.EncryptedApiKey = LocalAiCliUsageReader.ProtectSecret(plainApiKey);
                slot.LocalSourceMode = "api";
            }
            if (notes is not null) slot.Notes = TrimMax(notes, 120);

            string mode = ResolveEffectiveCliSource(slot);
            if (mode != "manual" && (mode != previousMode ||
                !string.Equals(slot.Provider, previousProvider, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(slot.AccountIdentifier, previousAccount, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(plainApiKey)))
            {
                ClearReportedQuota(slot);
                _cliOverlays.TryRemove(slot.Id, out _);
            }
            if (mode == "manual")
            {
                slot.ResetWindowMinutes = cleanWindow;
                if (quotaUnit is "use" or "pct" or "msg" or "credit") slot.QuotaUnit = quotaUnit;
            }

            if (totalQuota.HasValue)
            {
                slot.TotalQuota = Math.Clamp(totalQuota.Value, 0.0, 1_000_000.0);
                slot.UsedQuota = Math.Clamp(slot.UsedQuota, 0.0, slot.TotalQuota);
            }

            if (remainingQuota.HasValue && slot.TotalQuota > 0)
            {
                double cleanRem = Math.Clamp(remainingQuota.Value, 0.0, slot.TotalQuota);
                slot.UsedQuota = slot.TotalQuota - cleanRem;
                if (cleanRem <= 0 && slot.NextResetUtcTicks <= nowUtc.UtcTicks)
                {
                    slot.NextResetUtcTicks = nowUtc.AddMinutes(slot.ResetWindowMinutes).UtcTicks;
                }
                else if (cleanRem >= slot.TotalQuota)
                {
                    slot.NextResetUtcTicks = 0;
                    _lowAlertedWindowTicks.TryRemove(slot.Id, out _);
                }
            }

            if (slot.LocalSourceMode == "manual")
            {
                _cliOverlays.TryRemove(slot.Id, out _);
            }

            ArmWakeTimerLocked(nowUtc.UtcTicks);
        }

        SafeSaveConfig();
        if (_lastCliSnapshot is not null)
        {
            SyncWithLocalCli(_lastCliSnapshot);
        }
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
            _cliOverlays.TryRemove(id, out _);
            _lowAlertedWindowTicks.TryRemove(id, out _);
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

    public bool ConsumeUsage(string id, double amount = 1.0)
    {
        if (string.IsNullOrWhiteSpace(id) || !double.IsFinite(amount)) return false;
        var nowUtc = _timeProvider.GetUtcNow();
        AiQuotaSlotConfig? alertCandidate = null;
        bool crossedLow = false;

        lock (_sync)
        {
            if (_disposed) return false;
            var slot = _config.AiQuotaSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (slot is null) return false;

            if (slot.TotalQuota <= 0)
            {
                return MarkExhausted(id);
            }

            double prevRemaining = Math.Max(0.0, slot.TotalQuota - slot.UsedQuota);
            double prevPct = (prevRemaining / slot.TotalQuota) * 100.0;

            slot.UsedQuota = Math.Clamp(slot.UsedQuota + amount, 0.0, slot.TotalQuota);
            double nextRemaining = Math.Max(0.0, slot.TotalQuota - slot.UsedQuota);
            double nextPct = (nextRemaining / slot.TotalQuota) * 100.0;

            if (nextRemaining >= slot.TotalQuota)
            {
                slot.NextResetUtcTicks = 0;
                _lowAlertedWindowTicks.TryRemove(slot.Id, out _);
            }
            else if (slot.NextResetUtcTicks <= nowUtc.UtcTicks && slot.ResetWindowMinutes > 0)
            {
                slot.NextResetUtcTicks = nowUtc.AddMinutes(slot.ResetWindowMinutes).UtcTicks;
            }

            if (amount > 0 &&
                nextRemaining > 0 &&
                nextPct <= slot.CriticalThresholdPercent &&
                prevPct > slot.CriticalThresholdPercent &&
                slot.NotifyOnReset)
            {
                long winKey = slot.NextResetUtcTicks;
                if (!_lowAlertedWindowTicks.TryGetValue(slot.Id, out long prevAlertKey) || prevAlertKey != winKey)
                {
                    _lowAlertedWindowTicks[slot.Id] = winKey;
                    crossedLow = true;
                    alertCandidate = CloneSlot(slot);
                }
            }

            ArmWakeTimerLocked(nowUtc.UtcTicks);
        }

        SafeSaveConfig();
        if (crossedLow && alertCandidate is not null)
        {
            PublishLowQuotaHud(ToSnapshot(alertCandidate, nowUtc));
        }
        NotifySlotsChanged();
        return true;
    }

    public bool SetRemainingUsage(string id, double remaining)
    {
        if (string.IsNullOrWhiteSpace(id) || !double.IsFinite(remaining)) return false;
        var nowUtc = _timeProvider.GetUtcNow();
        AiQuotaSlotConfig? lowAlertSlot = null;

        lock (_sync)
        {
            if (_disposed) return false;
            var slot = _config.AiQuotaSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (slot is null || slot.TotalQuota <= 0) return false;

            double prevPct = (Math.Max(0.0, slot.TotalQuota - slot.UsedQuota) / slot.TotalQuota) * 100.0;
            double cleanRem = Math.Clamp(remaining, 0.0, slot.TotalQuota);
            slot.UsedQuota = slot.TotalQuota - cleanRem;

            if (cleanRem >= slot.TotalQuota)
            {
                slot.NextResetUtcTicks = 0;
                _lowAlertedWindowTicks.TryRemove(slot.Id, out _);
            }
            else if (slot.NextResetUtcTicks <= nowUtc.UtcTicks && slot.ResetWindowMinutes > 0)
            {
                slot.NextResetUtcTicks = nowUtc.AddMinutes(slot.ResetWindowMinutes).UtcTicks;
            }

            double nextPct = (cleanRem / slot.TotalQuota) * 100.0;
            if (cleanRem > 0 && nextPct <= slot.CriticalThresholdPercent && prevPct > slot.CriticalThresholdPercent && slot.NotifyOnReset)
            {
                _lowAlertedWindowTicks[slot.Id] = slot.NextResetUtcTicks;
                lowAlertSlot = CloneSlot(slot);
            }

            ArmWakeTimerLocked(nowUtc.UtcTicks);
        }

        SafeSaveConfig();
        if (lowAlertSlot is not null)
        {
            PublishLowQuotaHud(ToSnapshot(lowAlertSlot, nowUtc));
        }
        NotifySlotsChanged();
        return true;
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
            if (slot.TotalQuota > 0)
            {
                slot.UsedQuota = slot.TotalQuota;
            }
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
                slot.UsedQuota = 0;
                _lowAlertedWindowTicks.TryRemove(slot.Id, out _);
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
            changed = slot.NextResetUtcTicks != 0 || slot.UsedQuota > 0;
            slot.NextResetUtcTicks = 0;
            slot.UsedQuota = 0;
            _lowAlertedWindowTicks.TryRemove(slot.Id, out _);
            ArmWakeTimerLocked(_timeProvider.GetUtcNow().UtcTicks);
        }

        if (changed)
        {
            SafeSaveConfig();
            NotifySlotsChanged();
        }
        return true;
    }

    private bool SetOverlayIfChanged(string slotId, CliSlotOverlay next)
    {
        if (_cliOverlays.TryGetValue(slotId, out var prev) &&
            prev.IsSynced == next.IsSynced &&
            prev.RequiresReconnect == next.RequiresReconnect &&
            string.Equals(prev.BadgeText, next.BadgeText, StringComparison.Ordinal) &&
            string.Equals(prev.SecondarySummary, next.SecondarySummary, StringComparison.Ordinal) &&
            string.Equals(prev.ActiveModelName, next.ActiveModelName, StringComparison.Ordinal) &&
            AreBucketsEqual(prev.ModelBuckets, next.ModelBuckets))
        {
            return false;
        }
        _cliOverlays[slotId] = next;
        return true;
    }

    private static void ClearReportedQuota(AiQuotaSlotConfig slot)
    {
        slot.HasReportedPrimaryQuota = false;
        slot.UsedQuota = 0;
        slot.NextResetUtcTicks = 0;
        slot.WeeklyUsedPercent = null;
        slot.WeeklyResetUtcTicks = 0;
        slot.ModelBuckets?.Clear();
    }

    private static bool AreBucketsEqual(ImmutableArray<LocalAntigravityBucket> a, ImmutableArray<LocalAntigravityBucket> b)
    {
        int lenA = a.IsDefault ? 0 : a.Length;
        int lenB = b.IsDefault ? 0 : b.Length;
        if (lenA != lenB) return false;
        for (int i = 0; i < lenA; i++)
        {
            var ba = a[i];
            var bb = b[i];
            if (!string.Equals(ba.BucketKey, bb.BucketKey, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(ba.WeeklyRemainingPercent - bb.WeeklyRemainingPercent) > 0.05 ||
                ba.WeeklyResetUtc != bb.WeeklyResetUtc ||
                Math.Abs((ba.FiveHourRemainingPercent ?? -1.0) - (bb.FiveHourRemainingPercent ?? -1.0)) > 0.05 ||
                ba.FiveHourResetUtc != bb.FiveHourResetUtc)
            {
                return false;
            }
        }
        return true;
    }

    public LocalAiCliUsageSnapshot SyncWithLocalCli(LocalAiCliUsageSnapshot? providedSnapshot = null)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var cli = providedSnapshot ?? LocalAiCliUsageReader.ReadAll(_cliPaths, nowUtc);
        _lastCliSnapshot = cli;

        bool changed = false;
        bool overlayChanged = false;
        List<AiQuotaSlotSnapshot>? lowAlerts = null;
        string lang = Language;

        lock (_sync)
        {
            if (_disposed) return cli;

            foreach (var slot in _config.AiQuotaSlots)
            {
                string effectiveMode = ResolveEffectiveCliSource(slot);
                if (effectiveMode == "manual")
                {
                    if (_cliOverlays.TryRemove(slot.Id, out _)) changed = true;
                    continue;
                }
                if (effectiveMode == "api")
                {
                    continue;
                }

                bool isCustomProfile = !string.IsNullOrWhiteSpace(slot.ProfileDirectory) &&
                                       !string.Equals(slot.ProfileDirectory, "default", StringComparison.OrdinalIgnoreCase);

                double prevPct = slot.TotalQuota > 0
                    ? (Math.Max(0.0, slot.TotalQuota - slot.UsedQuota) / slot.TotalQuota) * 100.0
                    : 100.0;

                if (effectiveMode == "codex")
                {
                    if (slot.Id == _codexLoginSlotId) continue;
                    var codexData = isCustomProfile
                        ? LocalAiCliUsageReader.ReadCodex(slot.ProfileDirectory, nowUtc)
                        : cli.Codex;

                    if (codexData.Detected)
                    {
                        if (!string.IsNullOrWhiteSpace(codexData.AccountEmail) && string.IsNullOrWhiteSpace(slot.AccountIdentifier))
                        {
                            slot.AccountIdentifier = TrimMax(codexData.AccountEmail, 64);
                            changed = true;
                        }
                        if (!string.IsNullOrWhiteSpace(codexData.PlanTier))
                        {
                            if (!string.Equals(slot.PlanTier, codexData.PlanTier, StringComparison.Ordinal)) changed = true;
                            slot.PlanTier = codexData.PlanTier;
                        }

                        bool hasFresherLiveOverlay = _cliOverlays.TryGetValue(slot.Id, out var existingOverlay) &&
                                                     existingOverlay.SyncedAtUtc >= (codexData.LastEventUtc ?? DateTimeOffset.MinValue) &&
                                                     (!existingOverlay.IsSynced || slot.HasReportedPrimaryQuota.HasValue);

                        bool canImportLocal = !hasFresherLiveOverlay && slot.HasReportedPrimaryQuota is null &&
                            string.IsNullOrWhiteSpace(slot.AccountIdentifier) && !isCustomProfile;
                        if (codexData.PrimaryUsedPercent.HasValue && canImportLocal)
                        {
                            double newTotal = slot.QuotaUnit == "pct" || slot.TotalQuota <= 0 ? 100.0 : slot.TotalQuota;
                            double newUsed = Math.Round((codexData.PrimaryUsedPercent.Value / 100.0) * newTotal, 1);
                            int newWin = codexData.PrimaryWindowMinutes ?? slot.ResetWindowMinutes;
                            long newReset = codexData.PrimaryResetsAtUtc?.UtcTicks ?? 0L;

                            if (Math.Abs(slot.TotalQuota - newTotal) > 0.01 ||
                                Math.Abs(slot.UsedQuota - newUsed) > 0.01 ||
                                slot.ResetWindowMinutes != newWin ||
                                slot.NextResetUtcTicks != newReset)
                            {
                                slot.TotalQuota = newTotal;
                                slot.UsedQuota = Math.Clamp(newUsed, 0.0, newTotal);
                                slot.ResetWindowMinutes = Math.Clamp(newWin, AiQuotaSlotConfig.MinWindowMinutes, AiQuotaSlotConfig.MaxWindowMinutes);
                                slot.NextResetUtcTicks = newReset;
                                changed = true;
                            }
                        }

                        if (codexData.SecondaryUsedPercent.HasValue && canImportLocal)
                        {
                            double newWkUsed = Math.Clamp(Math.Round(codexData.SecondaryUsedPercent.Value, 1), 0.0, 100.0);
                            long newWkReset = codexData.SecondaryResetsAtUtc?.UtcTicks ?? 0L;
                            if (!slot.WeeklyUsedPercent.HasValue ||
                                Math.Abs(slot.WeeklyUsedPercent.Value - newWkUsed) > 0.01 ||
                                slot.WeeklyResetUtcTicks != newWkReset)
                            {
                                slot.WeeklyUsedPercent = newWkUsed;
                                slot.WeeklyResetUtcTicks = newWkReset;
                                changed = true;
                            }
                        }

                        if (!hasFresherLiveOverlay && existingOverlay is not { RequiresReconnect: true })
                        {
                            if (SetOverlayIfChanged(slot.Id, new CliSlotOverlay(
                                IsSynced: false,
                                BadgeText: codexData.IsAuthenticated ? Strings.Pick(lang, "Son ölçüm", "Last reading") : Strings.Pick(lang, "Giriş gerekli", "Sign-in required"),
                                SecondarySummary: codexData.ActiveModel,
                                ActiveModelName: codexData.ActiveModel,
                                SyncedAtUtc: codexData.LastEventUtc ?? nowUtc)))
                            {
                                overlayChanged = true;
                            }
                        }
                    }
                    else if (_cliOverlays.GetValueOrDefault(slot.Id)?.RequiresReconnect != true)
                    {
                        if (SetOverlayIfChanged(slot.Id, new CliSlotOverlay(
                            IsSynced: false,
                            BadgeText: "⚠ Giriş Gerekli",
                            SecondarySummary: Strings.Pick(lang, "Karttan 'Giriş Yap' ile hesabınızı bağlayın", "Click 'Sign In' on card to connect account"),
                            ActiveModelName: null,
                            SyncedAtUtc: nowUtc)))
                        {
                            overlayChanged = true;
                        }
                    }
                }
                else if (effectiveMode == "claude")
                {
                    var claudeData = isCustomProfile
                        ? LocalAiCliUsageReader.ReadClaude(
                            Path.Combine(slot.ProfileDirectory, ".claude.json"),
                            Path.Combine(slot.ProfileDirectory, "projects"),
                            nowUtc)
                        : cli.Claude;

                    bool hasLiveClaudeOverlay = _cliOverlays.TryGetValue(slot.Id, out var existingClaudeOverlay);

                    if (claudeData.Detected)
                    {
                        if (!string.IsNullOrWhiteSpace(claudeData.AccountEmail) && string.IsNullOrWhiteSpace(slot.AccountIdentifier))
                        {
                            slot.AccountIdentifier = TrimMax(claudeData.AccountEmail, 64);
                            changed = true;
                            if (IsGenericAccountLabel(slot.Label))
                            {
                                slot.Label = FormatEmailShortLabel("Claude", claudeData.AccountEmail);
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(claudeData.PlanTier))
                        {
                            if (!string.Equals(slot.PlanTier, claudeData.PlanTier, StringComparison.Ordinal)) changed = true;
                            slot.PlanTier = claudeData.PlanTier;
                        }

                        if (!hasLiveClaudeOverlay)
                        {
                            if (SetOverlayIfChanged(slot.Id, new CliSlotOverlay(
                                IsSynced: false,
                                BadgeText: null,
                                SecondarySummary: Strings.Pick(lang, "Abonelik kotası Claude Kullanım sayfasında", "Subscription quota is on Claude’s Usage page"),
                                ActiveModelName: null,
                                SyncedAtUtc: nowUtc)))
                            {
                                overlayChanged = true;
                            }
                        }
                    }
                    else if (!hasLiveClaudeOverlay)
                    {
                        if (SetOverlayIfChanged(slot.Id, new CliSlotOverlay(
                            IsSynced: false,
                            BadgeText: "⚠ Giriş Gerekli",
                            SecondarySummary: Strings.Pick(lang, "Karttan '🔑' ile Claude hesabınızı bağlayın", "Click '🔑' on card to connect Claude"),
                            ActiveModelName: null,
                            SyncedAtUtc: nowUtc)))
                        {
                            overlayChanged = true;
                        }
                    }
                }
                else if (effectiveMode == "antigravity")
                {
                    if (!_cliOverlays.TryGetValue(slot.Id, out var curOverlay) || curOverlay.ModelBuckets.IsDefaultOrEmpty)
                    {
                        var toolsSnap = LocalAiCliUsageReader.TryReadAntigravityToolsAccount(slot.AccountIdentifier, nowUtc, out _, out _);
                        if (toolsSnap is not null && !toolsSnap.ModelBuckets.IsDefaultOrEmpty)
                        {
                            if (!string.IsNullOrWhiteSpace(toolsSnap.PlanTier) &&
                                !string.Equals(slot.PlanTier, toolsSnap.PlanTier, StringComparison.Ordinal))
                            {
                                slot.PlanTier = TrimMax(toolsSnap.PlanTier, 20);
                                changed = true;
                            }
                            ApplyLiveAntigravityBucketsToConfig(slot, toolsSnap.ModelBuckets, nowUtc);
                            changed = true;
                            string secondary = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
                                ? $"Google OAuth · {toolsSnap.ModelBuckets.Length} model pools"
                                : $"Google OAuth · {toolsSnap.ModelBuckets.Length} model havuzu";
                            if (SetOverlayIfChanged(slot.Id, new CliSlotOverlay(
                                IsSynced: false,
                                BadgeText: Strings.Pick(lang, "Son ölçüm", "Last reading"),
                                SecondarySummary: secondary,
                                ActiveModelName: null,
                                SyncedAtUtc: nowUtc,
                                ModelBuckets: toolsSnap.ModelBuckets)))
                            {
                                overlayChanged = true;
                            }
                        }
                        else if (curOverlay is null)
                        {
                            bool hasEmail = !string.IsNullOrWhiteSpace(slot.AccountIdentifier);
                            string? secondary = hasEmail
                                ? null
                                : Strings.Pick(lang, "Karttan '🔑' ile Google hesabınıza giriş yapın", "Click '🔑' on card to sign in with Google");

                            if (SetOverlayIfChanged(slot.Id, new CliSlotOverlay(
                                IsSynced: false,
                                BadgeText: hasEmail ? null : Strings.Pick(lang, "Giriş gerekli", "Sign-in required"),
                                SecondarySummary: secondary,
                                ActiveModelName: null,
                                SyncedAtUtc: nowUtc)))
                            {
                                overlayChanged = true;
                            }
                        }
                    }
                }
                else if (effectiveMode == "gemini" && slot.ModelBuckets.Count == 0)
                {
                    var gemData = isCustomProfile
                        ? LocalAiCliUsageReader.ReadGemini(
                            Path.Combine(slot.ProfileDirectory, "google_accounts.json"),
                            Path.Combine(slot.ProfileDirectory, "tmp"),
                            nowUtc)
                        : cli.Gemini;

                    if (gemData.Detected)
                    {
                        if (!string.IsNullOrWhiteSpace(gemData.ActiveAccountEmail) && string.IsNullOrWhiteSpace(slot.AccountIdentifier))
                        {
                            slot.AccountIdentifier = TrimMax(gemData.ActiveAccountEmail, 64);
                            changed = true;
                            if (IsGenericAccountLabel(slot.Label))
                            {
                                slot.Label = FormatEmailShortLabel(slot.Provider, gemData.ActiveAccountEmail);
                            }
                        }

                        if (SetOverlayIfChanged(slot.Id, new CliSlotOverlay(
                            IsSynced: false,
                            BadgeText: null,
                            SecondarySummary: null,
                            ActiveModelName: null,
                            SyncedAtUtc: nowUtc)))
                        {
                            overlayChanged = true;
                        }
                    }
                }

                if (slot.TotalQuota > 0 && slot.NotifyOnReset && prevPct > slot.CriticalThresholdPercent)
                {
                    var snapshot = ToSnapshot(slot, nowUtc);
                    if (snapshot.IsLow)
                    {
                        long key = slot.NextResetUtcTicks;
                        if (!_lowAlertedWindowTicks.TryGetValue(slot.Id, out long prevKey) || prevKey != key)
                        {
                            _lowAlertedWindowTicks[slot.Id] = key;
                            lowAlerts ??= new List<AiQuotaSlotSnapshot>(2);
                            lowAlerts.Add(snapshot);
                        }
                    }
                }
            }

            ArmWakeTimerLocked(nowUtc.UtcTicks);
        }

        if (changed) SafeSaveConfig();
        if (lowAlerts is { Count: > 0 })
        {
            foreach (var alert in lowAlerts) PublishLowQuotaHud(alert);
        }
        if (changed || overlayChanged)
        {
            NotifySlotsChanged();
        }
        return cli;
    }

    public int ImportDetectedLocalCliAccounts(LocalAiCliUsageSnapshot? providedSnapshot = null)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var cli = providedSnapshot ?? LocalAiCliUsageReader.ReadAll(_cliPaths, nowUtc);
        _lastCliSnapshot = cli;
        int imported = 0;

        lock (_sync)
        {
            if (_disposed) return 0;

            if (cli.Codex.Detected &&
                !_config.AiQuotaSlots.Any(s => ResolveEffectiveCliSource(s) == "codex") &&
                _config.AiQuotaSlots.Count < AiQuotaSlotConfig.MaxSlotCount)
            {
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_" + Guid.NewGuid().ToString("N")[..6],
                    Provider = "ChatGPT",
                    Label = !string.IsNullOrWhiteSpace(cli.Codex.AccountEmail)
                        ? FormatEmailShortLabel("ChatGPT", cli.Codex.AccountEmail)
                        : "Codex CLI",
                    AccountIdentifier = TrimMax(cli.Codex.AccountEmail, 64),
                    PlanTier = cli.Codex.PlanTier ?? "Plus",
                    QuotaUnit = "pct",
                    TotalQuota = 100,
                    UsedQuota = 0,
                    ResetWindowMinutes = cli.Codex.PrimaryWindowMinutes ?? 300,
                    LocalSourceMode = "codex",
                    ProfileDirectory = "default",
                    NotifyOnReset = true
                });
                imported++;
            }

            if (cli.Claude.Detected &&
                !_config.AiQuotaSlots.Any(s => ResolveEffectiveCliSource(s) == "claude") &&
                _config.AiQuotaSlots.Count < AiQuotaSlotConfig.MaxSlotCount)
            {
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_" + Guid.NewGuid().ToString("N")[..6],
                    Provider = "Claude",
                    Label = !string.IsNullOrWhiteSpace(cli.Claude.AccountEmail)
                        ? FormatEmailShortLabel("Claude", cli.Claude.AccountEmail)
                        : "Claude Code",
                    AccountIdentifier = TrimMax(cli.Claude.AccountEmail, 64),
                    PlanTier = cli.Claude.PlanTier ?? "Pro",
                    QuotaUnit = "msg",
                    TotalQuota = string.Equals(cli.Claude.PlanTier, "Max", StringComparison.OrdinalIgnoreCase) ? 225 : 45,
                    UsedQuota = 0,
                    ResetWindowMinutes = 300,
                    LocalSourceMode = "claude",
                    ProfileDirectory = "default",
                    NotifyOnReset = true
                });
                imported++;
            }

            if (cli.Gemini.Detected &&
                !_config.AiQuotaSlots.Any(s => ResolveEffectiveCliSource(s) == "gemini") &&
                _config.AiQuotaSlots.Count < AiQuotaSlotConfig.MaxSlotCount)
            {
                _config.AiQuotaSlots.Add(new AiQuotaSlotConfig
                {
                    Id = "ai_" + Guid.NewGuid().ToString("N")[..6],
                    Provider = "Gemini",
                    Label = !string.IsNullOrWhiteSpace(cli.Gemini.ActiveAccountEmail)
                        ? FormatEmailShortLabel("Gemini", cli.Gemini.ActiveAccountEmail)
                        : "Gemini CLI",
                    AccountIdentifier = TrimMax(cli.Gemini.ActiveAccountEmail, 64),
                    PlanTier = "Pro",
                    QuotaUnit = "msg",
                    TotalQuota = 1000,
                    UsedQuota = 0,
                    ResetWindowMinutes = 1440,
                    LocalSourceMode = "gemini",
                    ProfileDirectory = "default",
                    NotifyOnReset = true
                });
                imported++;
            }
        }

        SyncWithLocalCli(cli);
        if (imported > 0)
        {
            SafeSaveConfig();
            NotifySlotsChanged();
        }
        return imported;
    }

    public void ReevaluateDueResets()
    {
        bool anyExpired = false;
        bool providerWindowElapsed;
        List<AiQuotaSlotConfig>? unlocked = null;
        var nowUtc = _timeProvider.GetUtcNow();
        long nowTicks = nowUtc.UtcTicks;

        lock (_sync)
        {
            if (_disposed) return;
            providerWindowElapsed = _nextScheduledResetTicks <= nowTicks;
            foreach (var s in _config.AiQuotaSlots)
            {
                if (ResolveEffectiveCliSource(s) == "manual" && s.NextResetUtcTicks > 0 && s.NextResetUtcTicks <= nowTicks)
                {
                    bool wasExhausted = s.TotalQuota <= 0 || s.UsedQuota >= s.TotalQuota;
                    s.NextResetUtcTicks = 0;
                    s.UsedQuota = 0;
                    _lowAlertedWindowTicks.TryRemove(s.Id, out _);
                    anyExpired = true;
                    if (s.NotifyOnReset && wasExhausted)
                    {
                        unlocked ??= new List<AiQuotaSlotConfig>(2);
                        unlocked.Add(CloneSlot(s));
                    }
                }
            }
            ArmWakeTimerLocked(nowTicks);
        }

        if (!anyExpired && !providerWindowElapsed) return;

        if (anyExpired) SafeSaveConfig();
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
                    PrimaryActionLabel: Strings.TabAiQuota(lang)));
            }
        }
        NotifySlotsChanged();
    }

    private void PublishLowQuotaHud(AiQuotaSlotSnapshot slot)
    {
        string lang = Language;
        string remTxt = FormatQuotaValue(slot.RemainingQuota);
        string totTxt = FormatQuotaValue(slot.TotalQuota);
        string sub = Strings.AiQuotaRemainingCount(lang, remTxt, totTxt, slot.QuotaUnit);
        _hudBus.Publish(new TransientHudEvent(
            CoalesceKey: $"ai-quota-low:{slot.Id}",
            Kind: HudKind.WidgetAlert,
            Title: Strings.AiQuotaLowTitle(lang, slot.Label),
            Subtitle: sub,
            Value01: slot.TotalQuota > 0 ? Math.Clamp(slot.RemainingPercent / 100.0, 0.0, 1.0) : null,
            AccentHex: "#FF9F0A",
            DurationMs: 4200,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uE7BA",
            PrimaryActionId: "ai.open-tab",
            PrimaryActionLabel: Strings.TabAiQuota(lang)));
    }

    private void ArmWakeTimerLocked(long nowUtcTicks)
    {
        if (_disposed) return;

        _nextScheduledResetTicks = _config.AiQuotaSlots.SelectMany(GetResetTicks)
            .Where(t => t > nowUtcTicks)
            .Select(t => (long?)t)
            .Min();

        if (!_nextScheduledResetTicks.HasValue)
        {
            _wakeTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        double msRemaining = Math.Max(250.0, (_nextScheduledResetTicks.Value - nowUtcTicks) / (double)TimeSpan.TicksPerMillisecond + 80.0);
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

    private static IEnumerable<long> GetResetTicks(AiQuotaSlotConfig slot)
    {
        switch (ResolveEffectiveCliSource(slot))
        {
            case "manual":
                yield return slot.NextResetUtcTicks;
                break;
            case "codex":
            case "claude" when slot.HasReportedPrimaryQuota.HasValue:
                yield return slot.NextResetUtcTicks;
                yield return slot.WeeklyResetUtcTicks;
                break;
            case "antigravity":
            case "gemini":
                foreach (var bucket in slot.ModelBuckets)
                {
                    yield return bucket.WeeklyResetUtcTicks;
                    yield return bucket.FiveHourResetUtcTicks;
                }
                break;
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

    private AiQuotaSlotSnapshot ToSnapshot(AiQuotaSlotConfig s, DateTimeOffset nowUtc)
    {
        string mode = ResolveEffectiveCliSource(s);
        _cliOverlays.TryGetValue(s.Id, out var overlay);

        bool duplicate = mode == "codex" && IsRepeatedCodexSlot(s);
        bool reconnect = duplicate || overlay?.RequiresReconnect == true;

        bool hasPrimary = !reconnect && (mode switch
        {
            "manual" => true,
            "codex" => s.HasReportedPrimaryQuota ?? (s.NextResetUtcTicks > 0 || s.UsedQuota > 0),
            "claude" or "api" => s.HasReportedPrimaryQuota == true,
            _ => false
        });
        double total = hasPrimary ? Math.Max(0.0, s.TotalQuota) : 0;
        double used = Math.Clamp(s.UsedQuota, 0.0, total);
        double remQuota = total - used;
        double remPct = total > 0 ? Math.Clamp(remQuota / total * 100.0, 0.0, 100.0) : 0;
        DateTimeOffset? primaryReset = hasPrimary ? ReadResetTime(s.NextResetUtcTicks) : null;
        DateTimeOffset? resetUtc = primaryReset > nowUtc ? primaryReset : null;
        TimeSpan remainingTime = resetUtc.HasValue ? resetUtc.Value - nowUtc : TimeSpan.Zero;
        double? cooldown01 = resetUtc.HasValue
            ? Math.Clamp(1.0 - remainingTime.TotalMinutes / Math.Max(1, s.ResetWindowMinutes), 0.0, 1.0)
            : null;

        bool hasProviderWindows = mode == "codex" || (mode == "claude" && s.HasReportedPrimaryQuota.HasValue);
        double? weeklyRemPct = !reconnect && hasProviderWindows && s.WeeklyUsedPercent.HasValue
            ? Math.Clamp(100.0 - s.WeeklyUsedPercent.Value, 0.0, 100.0)
            : null;
        DateTimeOffset? weeklyResetUtc = weeklyRemPct.HasValue ? ReadResetTime(s.WeeklyResetUtcTicks) : null;

        var modelBuckets = mode is "antigravity" or "gemini" ? BuildBucketSnapshots(s, overlay) : default;
        bool hasBuckets = !modelBuckets.IsDefaultOrEmpty;
        var currentBuckets = hasBuckets ? modelBuckets.Where(b => !b.HasExpiredWindow(nowUtc)).ToArray() : [];
        bool awaitingRefresh = (hasProviderWindows && (primaryReset <= nowUtc || weeklyResetUtc <= nowUtc)) ||
                               (hasBuckets && currentBuckets.Length < modelBuckets.Length &&
                                currentBuckets.All(b => b.EffectiveRemainingPercent <= 0.0001));

        double effectivePercent = hasPrimary && total > 0 ? remPct : 100.0;
        if (weeklyRemPct.HasValue) effectivePercent = Math.Min(effectivePercent, weeklyRemPct.Value);
        if (hasBuckets)
        {
            // One usable model pool is enough to keep the provider available.
            var measuredBuckets = currentBuckets.Length > 0 ? currentBuckets.AsEnumerable() : modelBuckets;
            effectivePercent = measuredBuckets.Max(b => b.EffectiveRemainingPercent);
            total = 100.0;
            remQuota = effectivePercent;
            used = total - remQuota;
            remPct = effectivePercent;
        }

        bool known = !awaitingRefresh && (hasPrimary || weeklyRemPct.HasValue || hasBuckets);
        bool isExhausted = known && (effectivePercent <= 0.0001 || (mode == "manual" && total <= 0 && resetUtc.HasValue));
        bool isReady = known && !isExhausted;
        bool isLow = isReady && effectivePercent <= s.CriticalThresholdPercent;
        DateTimeOffset? blockingReset = null;
        if (isExhausted)
        {
            if (hasBuckets)
            {
                blockingReset = currentBuckets.Select(b => LatestBlockingReset(
                    b.WeeklyRemainingPercent, b.WeeklyResetUtc, b.FiveHourRemainingPercent, b.FiveHourResetUtc)).Min();
                if (currentBuckets.Length != modelBuckets.Length) blockingReset = null;
            }
            else if (mode == "manual")
            {
                blockingReset = resetUtc;
            }
            else
            {
                blockingReset = LatestBlockingReset(hasPrimary ? remPct : null, resetUtc, weeklyRemPct, weeklyResetUtc);
            }
        }

        (int? daysUntilBilling, string? billingLabel) = ComputeBillingRenewal(s.BillingDayOfMonth, nowUtc.ToLocalTime(), Language);
        return new AiQuotaSlotSnapshot(
            Id: s.Id,
            Provider: s.Provider,
            Label: s.Label,
            AccentHex: GetProviderAccentHex(s.Provider),
            AccountIdentifier: s.AccountIdentifier,
            PlanTier: string.IsNullOrWhiteSpace(s.PlanTier) ? AiQuotaSlotConfig.GetDefaultPlanTier(s.Provider) : s.PlanTier,
            BillingDayOfMonth: s.BillingDayOfMonth,
            DaysUntilBillingRenewal: daysUntilBilling,
            BillingDateLabel: billingLabel,
            PriceLabel: s.PriceLabel,
            QuotaUnit: s.QuotaUnit,
            TotalQuota: total,
            UsedQuota: used,
            RemainingQuota: remQuota,
            RemainingPercent: remPct,
            CriticalThresholdPercent: s.CriticalThresholdPercent,
            LocalSourceMode: mode,
            Notes: s.Notes,
            IsCliSynced: known && overlay?.IsSynced == true,
            CliBadgeText: duplicate && overlay?.RequiresReconnect != true
                ? Strings.Pick(Language, "Aynı hesap zaten bağlı", "This account is already connected")
                : overlay?.BadgeText,
            SecondarySummary: overlay?.SecondarySummary,
            ActiveModelName: overlay?.ActiveModelName,
            ResetWindowMinutes: s.ResetWindowMinutes,
            NextResetUtc: resetUtc,
            Remaining: remainingTime,
            IsReady: isReady,
            IsLow: isLow,
            IsExhausted: isExhausted,
            CooldownProgress01: cooldown01,
            NotifyOnReset: s.NotifyOnReset,
            WeeklyRemainingPercent: weeklyRemPct,
            WeeklyResetUtc: weeklyResetUtc,
            ModelBuckets: modelBuckets,
            HasPrimaryQuota: hasPrimary,
            QuotaAwaitingRefresh: awaitingRefresh,
            BlockingResetUtc: blockingReset,
            RequiresReconnect: reconnect);
    }

    private static DateTimeOffset? ReadResetTime(long ticks) =>
        ticks > 0 && ticks <= DateTimeOffset.MaxValue.Ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    private static DateTimeOffset? LatestBlockingReset(
        double? primaryPercent, DateTimeOffset? primaryReset, double? secondaryPercent, DateTimeOffset? secondaryReset)
    {
        bool primaryBlocked = primaryPercent <= 0.0001;
        bool secondaryBlocked = secondaryPercent <= 0.0001;
        if ((primaryBlocked && primaryReset is null) || (secondaryBlocked && secondaryReset is null)) return null;
        if (!primaryBlocked) return secondaryBlocked ? secondaryReset : null;
        return secondaryBlocked && secondaryReset > primaryReset ? secondaryReset : primaryReset;
    }

    private static ImmutableArray<AiQuotaModelBucketSnapshot> BuildBucketSnapshots(
        AiQuotaSlotConfig slot, CliSlotOverlay? overlay)
    {
        if (overlay is not null && !overlay.ModelBuckets.IsDefaultOrEmpty)
        {
            return overlay.ModelBuckets.Select(b => new AiQuotaModelBucketSnapshot(
                b.BucketKey, b.DisplayName, b.ModelsSummary, b.WeeklyRemainingPercent,
                b.WeeklyResetUtc, b.FiveHourRemainingPercent, b.FiveHourResetUtc, b.AccentHex)).ToImmutableArray();
        }
        return slot.ModelBuckets.Select(b => new AiQuotaModelBucketSnapshot(
            b.BucketKey, b.DisplayName, b.ModelsSummary, b.WeeklyRemainingPercent,
            ReadResetTime(b.WeeklyResetUtcTicks), b.FiveHourRemainingPercent, ReadResetTime(b.FiveHourResetUtcTicks),
            b.AccentHex)).ToImmutableArray();
    }

    private static void ApplyLiveAntigravityBucketsToConfig(
        AiQuotaSlotConfig target,
        ImmutableArray<LocalAntigravityBucket> buckets,
        DateTimeOffset nowUtc)
    {
        if (buckets.IsDefaultOrEmpty) return;

        target.ModelBuckets = buckets.Select(b => new AiQuotaModelBucketConfig
        {
            BucketKey = b.BucketKey,
            DisplayName = b.DisplayName,
            ModelsSummary = b.ModelsSummary ?? string.Empty,
            WeeklyRemainingPercent = Math.Clamp(b.WeeklyRemainingPercent, 0.0, 100.0),
            WeeklyResetUtcTicks = b.WeeklyResetUtc?.UtcTicks ?? 0L,
            FiveHourRemainingPercent = b.FiveHourRemainingPercent,
            FiveHourResetUtcTicks = b.FiveHourResetUtc?.UtcTicks ?? 0L,
            AccentHex = b.AccentHex
        }).ToList();

        double minEff = buckets.Min(b => b.FiveHourRemainingPercent.HasValue
            ? Math.Min(b.WeeklyRemainingPercent, b.FiveHourRemainingPercent.Value)
            : b.WeeklyRemainingPercent);
        target.TotalQuota = 100.0;
        target.QuotaUnit = "pct";
        target.UsedQuota = Math.Round(100.0 - Math.Clamp(minEff, 0.0, 100.0), 1);

        var lowestWeekly = buckets.OrderBy(b => b.WeeklyRemainingPercent).First();
        target.WeeklyUsedPercent = Math.Round(100.0 - Math.Clamp(lowestWeekly.WeeklyRemainingPercent, 0.0, 100.0), 1);
        target.WeeklyResetUtcTicks = lowestWeekly.WeeklyResetUtc?.UtcTicks ?? 0L;

        DateTimeOffset? soonestReset = null;
        foreach (var b in buckets)
        {
            if (b.FiveHourResetUtc.HasValue && b.FiveHourResetUtc.Value > nowUtc &&
                (b.FiveHourRemainingPercent ?? 100.0) < 99.9 &&
                (soonestReset is null || b.FiveHourResetUtc.Value < soonestReset.Value))
            {
                soonestReset = b.FiveHourResetUtc.Value;
            }
        }
        target.NextResetUtcTicks = soonestReset?.UtcTicks ?? 0L;
    }

    public static (int? DaysLeft, string? Label) ComputeBillingRenewal(int billingDayOfMonth, DateTimeOffset nowLocal, string lang)
    {
        if (billingDayOfMonth is < 1 or > 31) return (null, null);

        var today = nowLocal.Date;
        int dimCurrent = DateTime.DaysInMonth(today.Year, today.Month);
        var candidate = new DateTime(today.Year, today.Month, Math.Min(billingDayOfMonth, dimCurrent));
        if (candidate < today)
        {
            var nextMonth = today.AddMonths(1);
            int dimNext = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
            candidate = new DateTime(nextMonth.Year, nextMonth.Month, Math.Min(billingDayOfMonth, dimNext));
        }

        int daysLeft = (int)(candidate - today).TotalDays;
        var culture = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("tr-TR");
        string label = candidate.ToString("dd MMM", culture);
        return (daysLeft, label);
    }

    public static string FormatQuotaValue(double val)
    {
        if (Math.Abs(val - Math.Round(val)) < 0.05)
        {
            return Math.Round(val).ToString("0", CultureInfo.InvariantCulture);
        }
        return val.ToString("0.#", CultureInfo.InvariantCulture);
    }

    public static int GetDefaultWindowMinutes(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "claude" => 300,
        "antigravity" => 300,
        "gemini" => 180,
        "chatgpt" => 180,
        "cursor" => 43200,
        "copilot" => 43200,
        "openrouter" => 43200,
        "deepseek" => 43200,
        _ => 180
    };

    public static string GetProviderAccentHex(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "chatgpt" => "#10A37F",
        "claude" => "#D97757",
        "gemini" => "#4E8DF5",
        "antigravity" => "#38BDF8",
        "cursor" => "#A855F7",
        "copilot" => "#6366F1",
        "openrouter" => "#F59E0B",
        "deepseek" => "#3B82F6",
        _ => "#AF52DE"
    };

    public static string NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return "ChatGPT";
        string trimmed = provider.Trim();
        if (trimmed.Equals("chatgpt", StringComparison.OrdinalIgnoreCase)) return "ChatGPT";
        if (trimmed.Equals("claude", StringComparison.OrdinalIgnoreCase)) return "Claude";
        if (trimmed.Equals("gemini", StringComparison.OrdinalIgnoreCase)) return "Gemini";
        if (trimmed.Equals("antigravity", StringComparison.OrdinalIgnoreCase)) return "Antigravity";
        if (trimmed.Equals("cursor", StringComparison.OrdinalIgnoreCase)) return "Cursor";
        if (trimmed.Equals("copilot", StringComparison.OrdinalIgnoreCase)) return "Copilot";
        if (trimmed.Equals("openrouter", StringComparison.OrdinalIgnoreCase)) return "OpenRouter";
        if (trimmed.Equals("deepseek", StringComparison.OrdinalIgnoreCase)) return "DeepSeek";
        return trimmed.Length > 24 ? trimmed[..24] : trimmed;
    }

    private static bool IsGenericAccountLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return true;
        string s = label.Trim();
        return s.Equals("Codex CLI", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("Claude Code", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("Gemini CLI", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("ChatGPT #", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("Claude #", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("Antigravity #", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("Gemini #", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatEmailShortLabel(string provider, string email)
    {
        int at = email.IndexOf('@');
        string handle = at > 0 ? email[..at] : email;
        if (handle.Length > 16) handle = handle[..16];
        return TrimMax($"{provider} · {handle}", 36);
    }

    private static string ResolveEffectiveCliSource(AiQuotaSlotConfig s)
    {
        string raw = NormalizeLocalSource(s.LocalSourceMode);
        if (raw != "auto") return raw;
        return s.Provider.Trim().ToLowerInvariant() switch
        {
            "chatgpt" => "codex",
            "claude" => "claude",
            "antigravity" => "antigravity",
            "gemini" => "gemini",
            _ => "manual"
        };
    }

    private static string NormalizeLocalSource(string? raw)
    {
        string s = (raw ?? "manual").Trim().ToLowerInvariant();
        return s is "manual" or "auto" or "codex" or "claude" or "gemini" or "antigravity" or "api" ? s : "manual";
    }

    private static string TrimMax(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string trimmed = value.Trim();
        return trimmed.Length > maxLen ? trimmed[..maxLen] : trimmed;
    }

    private static AiQuotaSlotConfig CloneSlot(AiQuotaSlotConfig s) => new()
    {
        Id = s.Id,
        Provider = s.Provider,
        Label = s.Label,
        AccountIdentifier = s.AccountIdentifier,
        PlanTier = s.PlanTier,
        BillingDayOfMonth = s.BillingDayOfMonth,
        PriceLabel = s.PriceLabel,
        QuotaUnit = s.QuotaUnit,
        TotalQuota = s.TotalQuota,
        UsedQuota = s.UsedQuota,
        CriticalThresholdPercent = s.CriticalThresholdPercent,
        LocalSourceMode = s.LocalSourceMode,
        ProfileDirectory = s.ProfileDirectory,
        EncryptedApiKey = s.EncryptedApiKey,
        Notes = s.Notes,
        ResetWindowMinutes = s.ResetWindowMinutes,
        NextResetUtcTicks = s.NextResetUtcTicks,
        WeeklyUsedPercent = s.WeeklyUsedPercent,
        HasReportedPrimaryQuota = s.HasReportedPrimaryQuota,
        WeeklyResetUtcTicks = s.WeeklyResetUtcTicks,
        NotifyOnReset = s.NotifyOnReset,
        ModelBuckets = s.ModelBuckets?.Select(b => new AiQuotaModelBucketConfig
        {
            BucketKey = b.BucketKey,
            DisplayName = b.DisplayName,
            ModelsSummary = b.ModelsSummary,
            WeeklyRemainingPercent = b.WeeklyRemainingPercent,
            WeeklyResetUtcTicks = b.WeeklyResetUtcTicks,
            FiveHourRemainingPercent = b.FiveHourRemainingPercent,
            FiveHourResetUtcTicks = b.FiveHourResetUtcTicks,
            AccentHex = b.AccentHex
        }).ToList() ?? new()
    };

    public static string FormatDurationCompact(TimeSpan remaining, string lang)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return Strings.AiQuotaReadyBadge(lang);
        }

        bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
        int totalHours = (int)Math.Floor(remaining.TotalHours);
        if (totalHours >= 24)
        {
            int days = totalHours / 24;
            int hrs = totalHours % 24;
            return isEn ? $"{days}d {hrs}h" : $"{days}g {hrs}s";
        }
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
