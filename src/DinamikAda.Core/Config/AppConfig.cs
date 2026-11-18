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

public sealed class AppConfig
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
    public bool DockReserveWorkArea { get; set; } = false;
    public int DockIconSizeDip { get; set; } = 32;
    public List<string> PinnedDockApps { get; set; } = new();
    // True once the user has edited dock pins; an empty PinnedDockApps then means "no pins",
    // not "never configured" (which would re-import the Windows taskbar pins on next start).
    public bool DockPinsCustomized { get; set; } = false;

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
        AiQuotaSlots = SanitizeAiQuotaSlots(AiQuotaSlots);

        PinnedTabs = PinnedTabs is null
            ? CreateDefaultTabs()
            : PinnedTabs.Where(Enum.IsDefined).Distinct().ToList();

        if (PinnedTabs.Count == 0)
        {
            PinnedTabs = CreateDefaultTabs();
        }
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
