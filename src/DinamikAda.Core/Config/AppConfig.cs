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
    public int MaxClipboardItems { get; set; } = 25;
    public string CommandHotkey { get; set; } = "Ctrl+Shift+Space";
    public bool UseHardwareRendering { get; set; } = true;
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

    [JsonIgnore]
    public bool ShowSpectrum
    {
        get => EnableSpectrumVisualizer;
        set => EnableSpectrumVisualizer = value;
    }

    public List<ExpandedTabId> PinnedTabs { get; set; } =
    [
        ExpandedTabId.Now,
        ExpandedTabId.Audio,
        ExpandedTabId.Control,
        ExpandedTabId.Shelf,
        ExpandedTabId.System,
        ExpandedTabId.Widgets
    ];

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

    private static List<ExpandedTabId> CreateDefaultTabs() =>
    [
        ExpandedTabId.Now,
        ExpandedTabId.Audio,
        ExpandedTabId.Control,
        ExpandedTabId.Shelf,
        ExpandedTabId.System,
        ExpandedTabId.Widgets
    ];
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
                try
                {
                    var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                    loaded.Sanitize();
                    return loaded;
                }
                catch
                {
                    var partial = LoadResilientPerProperty(json);
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

            string json = JsonSerializer.Serialize(config, JsonOptions);
            string tempPath = _configPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _configPath, overwrite: true);
        }
    }
}
