using System.Text.Json;
using System.Text.Json.Serialization;
using DinamikAda.Core.Abstractions;

namespace DinamikAda.Core.Config;

public enum NotchScalePreset
{
    Compact = 0,
    Standard = 1,
    Wide = 2
}

public sealed partial class AppConfig
{
    public string Language { get; set; } = "tr";
    public DockPlacementMode DockPlacement { get; set; } = DockPlacementMode.NotchFlush;
    public NotchScalePreset NotchScalePreset { get; set; } = NotchScalePreset.Standard;
    public bool DormantCameraLensEnabled { get; set; } = true;
    public bool DynamicAlbumGlowEnabled { get; set; } = true;
    public MonitorPlacementMode MonitorMode { get; set; } = MonitorPlacementMode.PrimaryOnly;
    public string? PinnedMonitorDeviceName { get; set; }
    public HorizontalAnchorDetent HorizontalDetent { get; set; } = HorizontalAnchorDetent.Center;
    public double CustomHorizontalRatio { get; set; } = 0.5;
    public double CustomFreeTopDip { get; set; } = 8.0;

    public static double GetScaleFactor(NotchScalePreset preset) => preset switch
    {
        NotchScalePreset.Compact => 0.92,
        NotchScalePreset.Wide => 1.06,
        _ => 1.00
    };

    public ExpandTriggerMode ExpandTrigger { get; set; } = ExpandTriggerMode.HoverDwellOrClick;
    public int HoverDwellMs { get; set; } = 160;
    public int CollapseHysteresisMs { get; set; } = 280;
    public bool AutoDodgeEnabled { get; set; } = true;
    public bool FadeWhenIdle { get; set; } = false;

    public FullscreenPolicy GameFullscreenPolicy { get; set; } = FullscreenPolicy.HideCompletely;
    public FullscreenPolicy VideoFullscreenPolicy { get; set; } = FullscreenPolicy.EdgePeekHairline;
    public FullscreenPolicy ProductivityFullscreenPolicy { get; set; } = FullscreenPolicy.KeepCompact;
    public List<string> FullscreenWhitelistExes { get; set; } = new();
    public List<string> FullscreenHideExes { get; set; } = new();

    public bool QuietIslandEnabled { get; set; }
    public bool ShowTelemetryInPill { get; set; }
    public bool ShowMicPrivacyAlert { get; set; } = false;
    public bool MirrorOsNotifications { get; set; } = true;
    public bool EnableClipboardVault { get; set; } = true;
    // LAN QR share runs over plain HTTP on the local network; sharing the latest clipboard text
    // (often a password or token) must be an explicit opt-in.
    public bool LanShareIncludeClipboard { get; set; } = false;
    public int MaxClipboardItems { get; set; } = 25;
    public string CommandHotkey { get; set; } = "Ctrl+Shift+Space";
    public bool UseHardwareRendering { get; set; } = true;
    /// <summary>
    /// On AMD Radeon GPUs (VEN_1002), D3D9Ex WPF AllowsTransparency layered windows can trigger
    /// Windows 11 Multi-Plane Overlay (MPO) black scanout artifacts over Chromium/DXGI apps.
    /// Defaults to true so ProcessRenderMode uses SoftwareOnly cleanly when an AMD adapter is active.
    /// </summary>
    public bool AmdSafeSoftwareCompositing { get; set; } = true;
    public int TargetFpsCap { get; set; } = 0;
    public bool LaunchAtStartup { get; set; } = false;
    public bool EnableSpectrumVisualizer { get; set; } = true;

    public bool BluetoothBatteryAlertsEnabled { get; set; } = true;
    public int BluetoothLowBatteryThreshold { get; set; } = 15;
    public bool CalendarEnabled { get; set; } = true;
    public string CalendarIcsSources { get; set; } = "";
    public bool DownloadsWatchEnabled { get; set; } = true;
    public string DownloadsWatchPath { get; set; } = "";
    public bool LyricsEnabled { get; set; } = true;
    public string LyricsFolders { get; set; } = "";
    public bool NotchDesktopSwipeEnabled { get; set; } = true;

    public string MicMuteHotkey { get; set; } = "Ctrl+Shift+M";
    public string ColorPickerHotkey { get; set; } = "Ctrl+Shift+C";
    public Dictionary<string, string> AppAudioRoutes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EnableGameModeAutomation { get; set; } = false;
    public bool GameModeHideIsland { get; set; } = false;
    public bool GameModeShowPingPill { get; set; } = true;
    public string PingHost { get; set; } = "1.1.1.1";
    public bool UsbDriveHudEnabled { get; set; } = true;
    public bool EnableDeveloperBridge { get; set; } = true;
    public int DeveloperBridgePort { get; set; } = 47821;

    // Synchronized macOS-style Bottom Dock & Taskbar Replacement
    public bool EnableBottomDock { get; set; } = true;
    public bool UseExplorerTaskbar { get; set; }
    [JsonIgnore]
    public bool UseCustomBottomDock => EnableBottomDock && !UseExplorerTaskbar;
    public bool HideWindowsTaskbar { get; set; } = true;
    public bool DockMagnificationEnabled { get; set; } = true;
    public bool DockIntellihideEnabled { get; set; } = true;
    public bool DockFollowFullscreenHide { get; set; } = true;
    /// <summary>Meeting, timer and AI-quota toasts that rise from the dock.</summary>
    public bool DockToastsEnabled { get; set; } = true;
    public bool DockReserveWorkArea { get; set; } = false;
    public int DockIconSizeDip { get; set; } = 32;
    public List<string> PinnedDockApps { get; set; } = new();
    // True once the user has edited dock pins; an empty PinnedDockApps then means "no pins",
    // not "never configured" (which would re-import the Windows taskbar pins on next start).
    public bool DockPinsCustomized { get; set; } = false;

    // Dock widgets (live tiles between the apps and the island slot) and dock appearance.
    public List<string> DockWidgets { get; set; } = new();
    // False until the default widget set was seeded once; an empty list afterwards means "no widgets".
    public bool DockWidgetsSeeded { get; set; } = false;
    public DockVisualStyle DockStyle { get; set; } = DockVisualStyle.Midnight;
    public DockScreenEdge DockEdge { get; set; } = DockScreenEdge.Bottom;
    public bool DockClock24h { get; set; } = true;
    public string WeatherCity { get; set; } = "";
    public double? WeatherLatitude { get; set; }
    public double? WeatherLongitude { get; set; }
    public string WeatherResolvedName { get; set; } = "";
    public bool WeatherFahrenheit { get; set; } = false;
    public List<string> WorldClockZones { get; set; } = new() { "Europe/London", "America/New_York", "Asia/Tokyo" };
    public string WorldTimeZone { get; set; } = "America/Los_Angeles";
    public string CurrencyFrom { get; set; } = "USD";
    public string CurrencyTo { get; set; } = "TRY";
    public string CryptoSymbol { get; set; } = "BTCUSDT";
    public List<string> StockSymbols { get; set; } = new() { "AAPL", "MSFT", "NVDA" };
    // Finnhub key, DPAPI-protected (CurrentUser) and base64 encoded; never stored in plain text.
    public string StockApiKeyProtected { get; set; } = "";
    public int DockTimerMinutes { get; set; } = 5;
    public int PomodoroFocusMinutes { get; set; } = 25;
    public int PomodoroBreakMinutes { get; set; } = 5;
    public List<DockTodoItem> DockTodos { get; set; } = new();
    public bool ScreenTimeEnabled { get; set; } = true;
    /// <summary>
    /// Connected accounts for the account widgets. Secret field values are DPAPI-protected by the app layer
    /// before they are stored here; the config itself never holds a plain key.
    /// </summary>
    public List<DockAccount> DockAccounts { get; set; } = new();

    // ---- Voice assistant ----------------------------------------------------------------------
    public bool VoiceAssistantEnabled { get; set; } = true;
    public Voice.VoiceEngine VoiceEngine { get; set; } = Voice.VoiceEngine.Auto;
    /// <summary>DPAPI-protected Gemini API key (the user's own free key from Google AI Studio).</summary>
    public string GeminiApiKeyProtected { get; set; } = string.Empty;
    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    /// <summary>Modifier-only hold chords.</summary>
    public string DictationChord { get; set; } = "Ctrl+Win";
    public string AskChord { get; set; } = "Ctrl+Shift";
    public string CommandChord { get; set; } = "Alt+Win";
    /// <summary>Let Gemini clean up dictation (fillers, self-corrections, punctuation).</summary>
    public bool DictationCleanup { get; set; } = true;
    /// <summary>Speech language as a BCP-47 tag; empty = follow the app language.</summary>
    public string VoiceLanguage { get; set; } = string.Empty;
    public List<Voice.ScheduledVoiceTask> ScheduledVoiceTasks { get; set; } = new();
    public List<Voice.VoiceRoutine> VoiceRoutines { get; set; } = new();
    public Voice.MascotSettings Mascot { get; set; } = new();
    public Voice.VoiceStats VoiceStats { get; set; } = new();

    // Local Multi-Account AI Subscription Quota & Reset Tracker (no browser cookies or credential bypass)
    public bool AiQuotaSeeded { get; set; } = false;
    public bool AiQuotaTabMigrated { get; set; } = true;
    public bool RealAccountsMigratedV3 { get; set; } = false;
    public bool UserExplicitAccountsOnlyV4 { get; set; } = false;
    public bool ShowAiQuotaInIslandAndDock { get; set; } = true;
    public List<AiQuotaSlotConfig> AiQuotaSlots { get; set; } = new();

    [JsonIgnore]
    public bool ShowSpectrum
    {
        get => EnableSpectrumVisualizer;
        set => EnableSpectrumVisualizer = value;
    }

    public List<ExpandedTabId> PinnedTabs { get; set; } = CreateDefaultTabs();

    internal AppConfig CopyForUpdate() => (AppConfig)MemberwiseClone();

    public void Sanitize()
    {
        Language = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";

        if (!Enum.IsDefined(DockPlacement)) DockPlacement = DockPlacementMode.NotchFlush;
        if (!Enum.IsDefined(NotchScalePreset)) NotchScalePreset = NotchScalePreset.Standard;
        if (!Enum.IsDefined(MonitorMode)) MonitorMode = MonitorPlacementMode.PrimaryOnly;
        if (!Enum.IsDefined(HorizontalDetent)) HorizontalDetent = HorizontalAnchorDetent.Center;
        if (!Enum.IsDefined(ExpandTrigger)) ExpandTrigger = ExpandTriggerMode.HoverDwellOrClick;
        if (!Enum.IsDefined(GameFullscreenPolicy)) GameFullscreenPolicy = FullscreenPolicy.HideCompletely;
        if (!Enum.IsDefined(VideoFullscreenPolicy)) VideoFullscreenPolicy = FullscreenPolicy.EdgePeekHairline;
        if (!Enum.IsDefined(ProductivityFullscreenPolicy)) ProductivityFullscreenPolicy = FullscreenPolicy.KeepCompact;

        if (string.IsNullOrWhiteSpace(PinnedMonitorDeviceName)) PinnedMonitorDeviceName = null;

        CustomHorizontalRatio = double.IsFinite(CustomHorizontalRatio)
            ? Math.Clamp(CustomHorizontalRatio, 0.08, 0.92)
            : 0.5;

        CustomFreeTopDip = double.IsFinite(CustomFreeTopDip)
            ? Math.Clamp(CustomFreeTopDip, 0.0, 2000.0)
            : 8.0;

        HoverDwellMs = Math.Clamp(HoverDwellMs, 40, 2000);
        CollapseHysteresisMs = Math.Clamp(CollapseHysteresisMs, 80, 5000);
        MaxClipboardItems = Math.Clamp(MaxClipboardItems, 5, 100);
        TargetFpsCap = TargetFpsCap <= 0 ? 0 : Math.Clamp(TargetFpsCap, 24, 360);
        BluetoothLowBatteryThreshold = Math.Clamp(BluetoothLowBatteryThreshold, 5, 40);
        CalendarIcsSources = CalendarIcsSources?.Trim() ?? string.Empty;
        DownloadsWatchPath = DownloadsWatchPath?.Trim() ?? string.Empty;
        LyricsFolders = LyricsFolders?.Trim() ?? string.Empty;

        CommandHotkey = string.IsNullOrWhiteSpace(CommandHotkey) ? "Ctrl+Shift+Space" : CommandHotkey.Trim();
        MicMuteHotkey = string.IsNullOrWhiteSpace(MicMuteHotkey) ? "Ctrl+Shift+M" : MicMuteHotkey.Trim();
        ColorPickerHotkey = string.IsNullOrWhiteSpace(ColorPickerHotkey) ? "Ctrl+Shift+C" : ColorPickerHotkey.Trim();
        PingHost = string.IsNullOrWhiteSpace(PingHost) ? "1.1.1.1" : PingHost.Trim();
        DeveloperBridgePort = DeveloperBridgePort is < 1024 or > 65535 ? 47821 : DeveloperBridgePort;

        var cleanRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AppAudioRoutes is not null)
        {
            foreach (var kv in AppAudioRoutes)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    cleanRoutes[kv.Key.Trim()] = kv.Value.Trim();
                }
            }
        }
        AppAudioRoutes = cleanRoutes;

        FullscreenWhitelistExes = SanitizeExeList(FullscreenWhitelistExes);
        FullscreenHideExes = SanitizeExeList(FullscreenHideExes);
        DockIconSizeDip = Math.Clamp(DockIconSizeDip, 26, 48);
        PinnedDockApps = SanitizeExeList(PinnedDockApps);
        SanitizeDockWidgetSettings();
        AiQuotaSlots = SanitizeAiQuotaSlots(AiQuotaSlots);

        PinnedTabs = PinnedTabs is null
            ? CreateDefaultTabs()
            : PinnedTabs.Where(Enum.IsDefined).Distinct().ToList();

        if (PinnedTabs.Count == 0)
        {
            PinnedTabs = CreateDefaultTabs();
        }
    }

    private void SanitizeDockWidgetSettings()
    {
        if (!DockWidgetsSeeded)
        {
            DockWidgets = new List<string>(Widgets.DockWidgetCatalog.DefaultWidgets);
            DockWidgetsSeeded = true;
        }
        DockWidgets = Widgets.DockWidgetCatalog.Sanitize(DockWidgets);
        if (!Enum.IsDefined(DockStyle)) DockStyle = DockVisualStyle.Midnight;
        if (!Enum.IsDefined(DockEdge)) DockEdge = DockScreenEdge.Bottom;
        WeatherCity = WeatherCity?.Trim() ?? string.Empty;
        WeatherResolvedName = WeatherResolvedName?.Trim() ?? string.Empty;
        if (WeatherLatitude is not { } lat || !double.IsFinite(lat) || lat is < -90 or > 90 ||
            WeatherLongitude is not { } lon || !double.IsFinite(lon) || lon is < -180 or > 180)
        {
            WeatherLatitude = null;
            WeatherLongitude = null;
        }
        WorldClockZones = (WorldClockZones ?? new())
            .Where(z => !string.IsNullOrWhiteSpace(z))
            .Select(z => z.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        WorldTimeZone = string.IsNullOrWhiteSpace(WorldTimeZone) ? "America/Los_Angeles" : WorldTimeZone.Trim();
        CurrencyFrom = NormalizeCode(CurrencyFrom, "USD", 3);
        CurrencyTo = NormalizeCode(CurrencyTo, "TRY", 3);
        CryptoSymbol = NormalizeCode(CryptoSymbol, "BTCUSDT", 16);
        StockSymbols = (StockSymbols ?? new())
            .Select(s => NormalizeCode(s, string.Empty, 12))
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
        StockApiKeyProtected = StockApiKeyProtected?.Trim() ?? string.Empty;
        DockTimerMinutes = Math.Clamp(DockTimerMinutes, 1, 180);
        PomodoroFocusMinutes = Math.Clamp(PomodoroFocusMinutes, 5, 120);
        PomodoroBreakMinutes = Math.Clamp(PomodoroBreakMinutes, 1, 60);
        DockTodos = (DockTodos ?? new())
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Text))
            .Take(50)
            .Select(t => new DockTodoItem
            {
                Id = string.IsNullOrWhiteSpace(t.Id) ? Guid.NewGuid().ToString("N") : t.Id,
                Text = t.Text.Trim().Length > 200 ? t.Text.Trim()[..200] : t.Text.Trim(),
                Done = t.Done
            })
            .ToList();
        SanitizeVoiceSettings();
        DockAccounts = (DockAccounts ?? new())
            .Where(a => a is not null && Accounts.AccountCatalog.Find(a.Service) is not null)
            .GroupBy(a => a.Service.Trim().ToLowerInvariant())
            .Select(g => new DockAccount
            {
                Service = g.Key,
                Values = (g.Last().Values ?? new())
                    .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
                    .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value.Trim(), StringComparer.Ordinal)
            })
            .ToList();
    }

    private static string NormalizeCode(string? value, string fallback, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var chars = value.Trim().ToUpperInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray();
        string s = new(chars);
        if (s.Length == 0) return fallback;
        return s.Length > maxLength ? s[..maxLength] : s;
    }

    private static List<string> SanitizeExeList(List<string>? items) =>
        items is null
            ? new List<string>()
            : items
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static List<AiQuotaSlotConfig> SanitizeAiQuotaSlots(List<AiQuotaSlotConfig>? slots)
    {
        if (slots is null || slots.Count == 0)
        {
            return new List<AiQuotaSlotConfig>();
        }

        var clean = new List<AiQuotaSlotConfig>(Math.Min(slots.Count, AiQuotaSlotConfig.MaxSlotCount));
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in slots)
        {
            if (s is null) continue;
            if (clean.Count >= AiQuotaSlotConfig.MaxSlotCount) break;

            string provider = string.IsNullOrWhiteSpace(s.Provider) ? "ChatGPT" : s.Provider.Trim();
            if (provider.Length > 24) provider = provider[..24];

            string label = string.IsNullOrWhiteSpace(s.Label) ? provider : s.Label.Trim();
            if (label.Length > 36) label = label[..36];

            string id = string.IsNullOrWhiteSpace(s.Id) ? Guid.NewGuid().ToString("N")[..8] : s.Id.Trim();
            if (id.Length > 24) id = id[..24];
            if (!seenIds.Add(id))
            {
                id = Guid.NewGuid().ToString("N")[..8];
                seenIds.Add(id);
            }

            double totalQuota = !double.IsFinite(s.TotalQuota) || s.TotalQuota < 0
                ? AiQuotaSlotConfig.GetDefaultTotalQuota(provider)
                : Math.Clamp(s.TotalQuota, 0.0, 1_000_000.0);
            double usedQuota = !double.IsFinite(s.UsedQuota) || s.UsedQuota < 0
                ? 0.0
                : (totalQuota > 0 ? Math.Clamp(s.UsedQuota, 0.0, totalQuota) : 0.0);

            string accountId = s.AccountIdentifier?.Trim() ?? string.Empty;
            if (accountId.Length > 64) accountId = accountId[..64];

            string planTier = s.PlanTier?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(planTier)) planTier = AiQuotaSlotConfig.GetDefaultPlanTier(provider);
            if (planTier.Length > 20) planTier = planTier[..20];

            string priceLabel = s.PriceLabel?.Trim() ?? string.Empty;
            if (priceLabel.Length > 20) priceLabel = priceLabel[..20];

            string quotaUnit = string.IsNullOrWhiteSpace(s.QuotaUnit) ? "use" : s.QuotaUnit.Trim().ToLowerInvariant();
            if (quotaUnit is not ("use" or "pct" or "msg" or "credit")) quotaUnit = "use";

            string localSource = string.IsNullOrWhiteSpace(s.LocalSourceMode) ? "manual" : s.LocalSourceMode.Trim().ToLowerInvariant();
            if (localSource is not ("manual" or "auto" or "codex" or "claude" or "gemini" or "antigravity" or "api")) localSource = "manual";

            string profileDir = s.ProfileDirectory?.Trim() ?? string.Empty;
            if (profileDir.Length > 260) profileDir = profileDir[..260];

            string encKey = s.EncryptedApiKey?.Trim() ?? string.Empty;
            if (encKey.Length > 2048) encKey = string.Empty;

            string notes = s.Notes?.Trim() ?? string.Empty;
            if (notes.Length > 120) notes = notes[..120];

            var cleanBuckets = new List<AiQuotaModelBucketConfig>();
            if (s.ModelBuckets is not null)
            {
                foreach (var b in s.ModelBuckets)
                {
                    if (b is null || cleanBuckets.Count >= 8) break;
                    string bKey = b.BucketKey?.Trim() ?? string.Empty;
                    string bName = b.DisplayName?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(bKey) || string.IsNullOrWhiteSpace(bName)) continue;
                    if (bKey.Length > 40) bKey = bKey[..40];
                    if (bName.Length > 48) bName = bName[..48];
                    string bModels = b.ModelsSummary?.Trim() ?? string.Empty;
                    if (bModels.Length > 160) bModels = bModels[..160];
                    string bAccent = b.AccentHex?.Trim() ?? "#38BDF8";
                    if (bAccent.Length > 16) bAccent = "#38BDF8";
                    cleanBuckets.Add(new AiQuotaModelBucketConfig
                    {
                        BucketKey = bKey,
                        DisplayName = bName,
                        ModelsSummary = bModels,
                        WeeklyRemainingPercent = double.IsFinite(b.WeeklyRemainingPercent)
                            ? Math.Clamp(b.WeeklyRemainingPercent, 0.0, 100.0)
                            : 100.0,
                        WeeklyResetUtcTicks = Math.Max(0L, b.WeeklyResetUtcTicks),
                        FiveHourRemainingPercent = b.FiveHourRemainingPercent.HasValue && double.IsFinite(b.FiveHourRemainingPercent.Value)
                            ? Math.Clamp(b.FiveHourRemainingPercent.Value, 0.0, 100.0)
                            : null,
                        FiveHourResetUtcTicks = Math.Max(0L, b.FiveHourResetUtcTicks),
                        AccentHex = bAccent
                    });
                }
            }

            // Normalize in place: AiQuotaTrackerService (timer thread) holds references to these slot
            // objects, and replacing them on every Save would orphan its in-flight updates.
            s.Id = id;
            s.Provider = provider;
            s.Label = label;
            s.AccountIdentifier = accountId;
            s.PlanTier = planTier;
            s.BillingDayOfMonth = Math.Clamp(s.BillingDayOfMonth, 0, 31);
            s.PriceLabel = priceLabel;
            s.QuotaUnit = quotaUnit;
            s.TotalQuota = totalQuota;
            s.UsedQuota = usedQuota;
            s.CriticalThresholdPercent = Math.Clamp(s.CriticalThresholdPercent <= 0 ? 20 : s.CriticalThresholdPercent, 5, 50);
            s.LocalSourceMode = localSource;
            s.ProfileDirectory = profileDir;
            s.EncryptedApiKey = encKey;
            s.Notes = notes;
            s.ResetWindowMinutes = Math.Clamp(s.ResetWindowMinutes, AiQuotaSlotConfig.MinWindowMinutes, AiQuotaSlotConfig.MaxWindowMinutes);
            s.NextResetUtcTicks = Math.Max(0L, s.NextResetUtcTicks);
            s.WeeklyUsedPercent = s.WeeklyUsedPercent.HasValue && double.IsFinite(s.WeeklyUsedPercent.Value)
                ? Math.Clamp(s.WeeklyUsedPercent.Value, 0.0, 100.0)
                : null;
            s.WeeklyResetUtcTicks = Math.Max(0L, s.WeeklyResetUtcTicks);
            s.ModelBuckets = cleanBuckets;
            clean.Add(s);
        }

        return clean;
    }

    private static List<ExpandedTabId> CreateDefaultTabs() =>
    [
        ExpandedTabId.Now,
        ExpandedTabId.Audio,
        ExpandedTabId.Control,
        ExpandedTabId.Shelf,
        ExpandedTabId.System,
        ExpandedTabId.Widgets,
        ExpandedTabId.AiQuota
    ];
}

public sealed class AiQuotaSlotConfig
{
    public const int MaxSlotCount = 24;
    public const int MinWindowMinutes = 5;
    public const int MaxWindowMinutes = 44640;

    public string Id { get; set; } = string.Empty;
    public string Provider { get; set; } = "ChatGPT";
    public string Label { get; set; } = "ChatGPT #1";
    public string AccountIdentifier { get; set; } = string.Empty;
    public string PlanTier { get; set; } = "Plus";
    public int BillingDayOfMonth { get; set; } = 0;
    public string PriceLabel { get; set; } = string.Empty;
    public string QuotaUnit { get; set; } = "use";
    public double TotalQuota { get; set; } = -1;
    public double UsedQuota { get; set; } = 0;
    public int CriticalThresholdPercent { get; set; } = 20;
    public string LocalSourceMode { get; set; } = "manual";
    public string ProfileDirectory { get; set; } = string.Empty;
    public string EncryptedApiKey { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int ResetWindowMinutes { get; set; } = 180;
    public long NextResetUtcTicks { get; set; }
    public bool? HasReportedPrimaryQuota { get; set; }
    public double? WeeklyUsedPercent { get; set; }
    public long WeeklyResetUtcTicks { get; set; }
    public bool NotifyOnReset { get; set; } = true;
    public List<AiQuotaModelBucketConfig> ModelBuckets { get; set; } = new();

    public static double GetDefaultTotalQuota(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "chatgpt" => 80,
        "claude" => 45,
        "gemini" => 250,
        "antigravity" => 100,
        "cursor" => 500,
        "copilot" => 300,
        "openrouter" => 100,
        _ => 100
    };

    public static string GetDefaultPlanTier(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "chatgpt" => "Plus",
        "claude" => "Pro",
        "gemini" => "Pro",
        "antigravity" => "Pro",
        "cursor" => "Pro",
        "copilot" => "Pro",
        _ => "Plus"
    };
}

public sealed class AiQuotaModelBucketConfig
{
    public string BucketKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ModelsSummary { get; set; } = string.Empty;
    public double WeeklyRemainingPercent { get; set; } = 100.0;
    public long WeeklyResetUtcTicks { get; set; }
    public double? FiveHourRemainingPercent { get; set; }
    public long FiveHourResetUtcTicks { get; set; }
    public string AccentHex { get; set; } = "#38BDF8";
}

public sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configPath;
    private readonly object _sync = new();

    public AppConfigStore(string? configPath = null)
    {
        string baseDir = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DinamikAda",
            "config.json");
        _configPath = baseDir;
    }

    public string ConfigPath => _configPath;

    public AppConfig Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    var defaults = new AppConfig();
                    defaults.Sanitize();
                    return defaults;
                }

                string json = File.ReadAllText(_configPath);
                bool hasAiTabMigratedField = json.Contains("\"AiQuotaTabMigrated\"", StringComparison.OrdinalIgnoreCase);
                try
                {
                    var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                    if (!hasAiTabMigratedField && loaded.PinnedTabs is not null && !loaded.PinnedTabs.Contains(ExpandedTabId.AiQuota))
                    {
                        loaded.PinnedTabs.Add(ExpandedTabId.AiQuota);
                    }
                    loaded.AiQuotaTabMigrated = true;
                    loaded.Sanitize();
                    return loaded;
                }
                catch
                {
                    // Keep a copy of the unreadable file: the next Save would otherwise destroy any
                    // settings the per-property recovery below cannot salvage.
                    BackupCorruptConfig();
                    var partial = LoadResilientPerProperty(json);
                    if (!hasAiTabMigratedField && !partial.PinnedTabs.Contains(ExpandedTabId.AiQuota))
                    {
                        partial.PinnedTabs.Add(ExpandedTabId.AiQuota);
                    }
                    partial.AiQuotaTabMigrated = true;
                    partial.Sanitize();
                    return partial;
                }
            }
            catch
            {
                var fallback = new AppConfig();
                fallback.Sanitize();
                return fallback;
            }
        }
    }

    private void BackupCorruptConfig()
    {
        try
        {
            string backup = $"{_configPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(_configPath, backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static AppConfig LoadResilientPerProperty(string json)
    {
        var cfg = new AppConfig();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return cfg;

            var props = typeof(AppConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var map = new Dictionary<string, System.Reflection.PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in props)
            {
                if (p.CanWrite && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute)))
                {
                    map[p.Name] = p;
                }
            }

            foreach (var jp in doc.RootElement.EnumerateObject())
            {
                if (!map.TryGetValue(jp.Name, out var prop)) continue;
                try
                {
                    object? val = jp.Value.Deserialize(prop.PropertyType, JsonOptions);
                    if (val is not null || Nullable.GetUnderlyingType(prop.PropertyType) is not null || prop.PropertyType == typeof(string))
                    {
                        prop.SetValue(cfg, val);
                    }
                }
                catch
                {
                    // Skip invalid/obsolete property value and preserve remaining settings.
                }
            }
        }
        catch
        {
            // Return defaults if JSON syntax itself is unrecoverable.
        }
        return cfg;
    }

    private static string SerializeWithRetry(AppConfig config)
    {
        // Config lists are mutated by background services (dock pins, audio routes, quota slots)
        // without this store's lock; retry if a collection changes mid-serialization.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return JsonSerializer.Serialize(config, JsonOptions);
            }
            catch (InvalidOperationException) when (attempt < 4)
            {
                Thread.Sleep(5);
            }
        }
    }

    // The edit assigns settings properties; it must not mutate shared collections in place.
    public void Update(AppConfig config, Action<AppConfig> edit)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(edit);
        lock (_sync)
        {
            var candidate = config.CopyForUpdate();
            edit(candidate);
            Save(candidate);
            edit(config);
        }
    }

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_sync)
        {
            config.Sanitize();
            string? dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = SerializeWithRetry(config);
            string tempPath = _configPath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                // Force the bytes to disk before the rename so a power loss can't leave an empty config.
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _configPath, overwrite: true);
        }
    }
}

public enum DockVisualStyle
{
    Midnight = 0,
    Black = 1,
    White = 2,
    LiquidGlass = 3,
    IosGlass = 4
}

public enum DockScreenEdge
{
    Bottom = 0,
    Top = 1
}

public sealed partial class AppConfig
{
    private void SanitizeVoiceSettings()
    {
        if (!Enum.IsDefined(VoiceEngine)) VoiceEngine = Voice.VoiceEngine.Auto;
        GeminiApiKeyProtected = GeminiApiKeyProtected?.Trim() ?? string.Empty;
        GeminiModel = string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-2.5-flash" : GeminiModel.Trim();
        DictationChord = Voice.HoldChordText.Normalize(DictationChord, "Ctrl+Win");
        AskChord = Voice.HoldChordText.Normalize(AskChord, "Ctrl+Shift");
        CommandChord = Voice.HoldChordText.Normalize(CommandChord, "Alt+Win");
        VoiceLanguage = VoiceLanguage?.Trim() ?? string.Empty;
        ScheduledVoiceTasks = (ScheduledVoiceTasks ?? new())
            .Where(t => t is not null && (t.Actions?.Count ?? 0) > 0)
            .Where(t => !t.Done || t.RunAtLocal > DateTime.Now.AddDays(-7))
            .Take(100)
            .Select(t => new Voice.ScheduledVoiceTask
            {
                Id = string.IsNullOrWhiteSpace(t.Id) ? Guid.NewGuid().ToString("N") : t.Id,
                Title = (t.Title ?? string.Empty).Trim(),
                RunAtLocal = t.RunAtLocal,
                Actions = t.Actions!.Where(a => a is not null).Select(a => a.Clone()).Take(10).ToList(),
                Done = t.Done
            })
            .ToList();
        VoiceRoutines = (VoiceRoutines ?? new())
            .Where(r => r is not null)
            .Take(50)
            .Select(r => new Voice.VoiceRoutine
            {
                Id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N") : r.Id,
                Name = (r.Name ?? string.Empty).Trim(),
                TimeOfDay = TimeSpan.TryParse(r.TimeOfDay, System.Globalization.CultureInfo.InvariantCulture, out var tod) && tod >= TimeSpan.Zero && tod < TimeSpan.FromDays(1)
                    ? tod.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture)
                    : "09:00",
                DaysMask = r.DaysMask is <= 0 or > 127 ? 127 : r.DaysMask,
                Actions = (r.Actions ?? new()).Where(a => a is not null).Select(a => a.Clone()).Take(10).ToList(),
                Enabled = r.Enabled,
                LastRunDate = r.LastRunDate ?? string.Empty
            })
            .ToList();
        Mascot = Mascot?.Clone() ?? new Voice.MascotSettings();
        Mascot.Name = string.IsNullOrWhiteSpace(Mascot.Name) ? "Ada" : Mascot.Name.Trim()[..Math.Min(Mascot.Name.Trim().Length, 24)];
        if (!System.Text.RegularExpressions.Regex.IsMatch(Mascot.ColorHex ?? string.Empty, "^#[0-9A-Fa-f]{6}$")) Mascot.ColorHex = "#7C5CFF";
        Mascot.EyeStyle = Math.Clamp(Mascot.EyeStyle, 0, 3);
        Mascot.Accessory = Math.Clamp(Mascot.Accessory, 0, 4);
        Mascot.Personality = Math.Clamp(Mascot.Personality, 0, 2);
        VoiceStats ??= new Voice.VoiceStats();
        if (VoiceStats.WordsDictated < 0 || !double.IsFinite(VoiceStats.SecondsSpoken) || VoiceStats.SecondsSpoken < 0)
            VoiceStats = new Voice.VoiceStats();
    }
}

public sealed class DockAccount
{
    public string Service { get; set; } = string.Empty;
    /// <summary>Field key → value; values of secret fields are stored protected.</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DockTodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
}
