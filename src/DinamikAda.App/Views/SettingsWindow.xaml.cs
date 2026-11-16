using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DinamikAda.App.Services;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Config;
using DinamikAda.Core.Localization;
using DinamikAda.Platform.Win32.Hotkeys;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;
using Microsoft.Win32;

namespace DinamikAda.App.Views;

public partial class SettingsWindow : Window
{
    private sealed record ChoiceItem<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly AppOrchestrator _orch;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly Action _onAppliedCallback;
    private readonly nint _islandHwnd;

    private bool _suppressUiEvents;
    private string _currentLang = "tr";

    public SettingsWindow(
        AppOrchestrator orchestrator,
        GlobalHotkeyService hotkeyService,
        Action onApplied,
        nint islandHwnd = 0)
    {
        _orch = orchestrator;
        _hotkeyService = hotkeyService;
        _onAppliedCallback = onApplied;
        _islandHwnd = islandHwnd;

        InitializeComponent();
        LoadFromConfig(_orch.Config);
        SourceInitialized += (_, _) => CenterOnIslandMonitorWorkArea();
        CenterOnIslandMonitorWorkArea();
    }

    private unsafe void CenterOnIslandMonitorWorkArea()
    {
        var monitors = _orch.MonitorLayout.GetMonitors();
        MonitorDescriptor? targetMon = null;
        double workLeftDip = 0;
        double workTopDip = 0;
        double workWDip = 0;
        double workHDip = 0;
        int workLeftPx = 0;
        int workTopPx = 0;
        int workWPx = 0;
        int workHPx = 0;
        bool hasWorkPx = false;

        nint probeMon = _islandHwnd != 0
            ? NativeMethods.MonitorFromWindow(_islandHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST)
            : 0;

        if (probeMon == 0)
        {
            targetMon = ResolveTargetMonitorDescriptor(monitors, _orch.Config, _orch.MonitorLayout);
            if (targetMon is not null)
            {
                probeMon = targetMon.Handle;
            }
        }

        if (probeMon != 0)
        {
            var info = new NativeMethods.MONITORINFOEXW { cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW) };
            if (NativeMethods.GetMonitorInfoW(probeMon, ref info))
            {
                uint dpiX = 96;
                _ = NativeMethods.GetDpiForMonitor(probeMon, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out _);
                double scale = Math.Max(1.0, dpiX / 96.0);
                workLeftPx = info.rcWork.Left;
                workTopPx = info.rcWork.Top;
                workWPx = Math.Max(1, info.rcWork.Width);
                workHPx = Math.Max(1, info.rcWork.Height);
                workLeftDip = workLeftPx / scale;
                workTopDip = workTopPx / scale;
                workWDip = workWPx / scale;
                workHDip = workHPx / scale;
                hasWorkPx = true;
            }
        }

        if (!hasWorkPx && targetMon is not null)
        {
            double scale = Math.Max(1.0, targetMon.DpiScale);
            workLeftPx = targetMon.LeftPx;
            workTopPx = targetMon.TopPx;
            workWPx = Math.Max(1, targetMon.WidthPx);
            workHPx = Math.Max(1, targetMon.HeightPx);
            workLeftDip = workLeftPx / scale;
            workTopDip = workTopPx / scale;
            workWDip = workWPx / scale;
            workHDip = workHPx / scale;
            hasWorkPx = true;
        }

        if (!hasWorkPx)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        double maxH = Math.Max(460.0, workHDip - 24.0);
        MaxHeight = maxH;
        if (Height > maxH) Height = maxH;
        double maxW = Math.Max(580.0, workWDip - 24.0);
        if (Width > maxW) Width = maxW;

        nint hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double localScale = Math.Max(1.0, dpi.DpiScaleX);
            int targetW = (int)Math.Round(Math.Min(Width, workWDip - 16.0) * localScale);
            int targetH = (int)Math.Round(Math.Min(Height, maxH) * localScale);
            int x = workLeftPx + Math.Max(0, (workWPx - targetW) / 2);
            int y = workTopPx + Math.Max(0, (workHPx - targetH) / 2);
            _ = NativeMethods.SetWindowPos(
                hwnd,
                0,
                x, y, targetW, targetH,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            return;
        }

        Left = workLeftDip + Math.Max(0.0, (workWDip - Width) * 0.5);
        Top = workTopDip + Math.Max(0.0, (workHDip - Height) * 0.5);
    }

    private void LoadFromConfig(AppConfig cfg)
    {
        _suppressUiEvents = true;
        try
        {
            _currentLang = string.Equals(cfg.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";

            PopulateStaticChoices(_currentLang, cfg);
            SelectConfigChoices(cfg);

            SliderCustomRatio.Value = Math.Clamp(Math.Round(cfg.CustomHorizontalRatio * 100.0), 8, 92);
            SliderCustomFreeTop.Value = Math.Clamp(Math.Round(cfg.CustomFreeTopDip), 0, 400);
            SliderHoverDwell.Value = Math.Clamp(cfg.HoverDwellMs, 40, 2000);
            SliderCollapseDelay.Value = Math.Clamp(cfg.CollapseHysteresisMs, 80, 5000);
            SliderMaxClipboard.Value = Math.Clamp(cfg.MaxClipboardItems, 5, 100);

            TxtHotkey.Text = _hotkeyService.TryGetRegisteredChord(GlobalHotkeyService.HotkeyId, out var activeCmd) ? activeCmd : cfg.CommandHotkey;
            TxtMicMuteHotkey.Text = _hotkeyService.TryGetRegisteredChord(GlobalHotkeyService.MicMuteHotkeyId, out var activeMic) ? activeMic : cfg.MicMuteHotkey;
            TxtColorPickerHotkey.Text = _hotkeyService.TryGetRegisteredChord(GlobalHotkeyService.ColorPickerHotkeyId, out var activePicker) ? activePicker : cfg.ColorPickerHotkey;

            ChkDormantCameraLens.IsChecked = cfg.DormantCameraLensEnabled;
            ChkDynamicAlbumGlow.IsChecked = cfg.DynamicAlbumGlowEnabled;
            ChkEnableBottomDock.IsChecked = cfg.EnableBottomDock;
            ChkHideWindowsTaskbar.IsChecked = cfg.HideWindowsTaskbar;
            ChkDockMagnification.IsChecked = cfg.DockMagnificationEnabled;
            ChkDockIntellihide.IsChecked = cfg.DockIntellihideEnabled;
            SliderDockIconSize.Value = Math.Clamp(cfg.DockIconSizeDip, 26, 48);
            ChkHardwareRendering.IsChecked = cfg.UseHardwareRendering;
            ChkSpectrumVisualizer.IsChecked = cfg.EnableSpectrumVisualizer;
            ChkAutoDodge.IsChecked = cfg.AutoDodgeEnabled;
            ChkFadeWhenIdle.IsChecked = cfg.FadeWhenIdle;
            ChkQuietIsland.IsChecked = cfg.QuietIslandEnabled;
            ChkGameModeAuto.IsChecked = cfg.EnableGameModeAutomation;
            ChkGameModeHide.IsChecked = cfg.GameModeHideIsland;
            ChkGameModePingPill.IsChecked = cfg.GameModeShowPingPill;
            ChkShowTelemetryPill.IsChecked = cfg.ShowTelemetryInPill;
            ChkMicPrivacyAlert.IsChecked = cfg.ShowMicPrivacyAlert;
            ChkMirrorOsNotifications.IsChecked = cfg.MirrorOsNotifications;
            ChkClipboardVault.IsChecked = cfg.EnableClipboardVault;
            ChkBtBatteryAlerts.IsChecked = cfg.BluetoothBatteryAlertsEnabled;
            ChkNotchDesktopSwipe.IsChecked = cfg.NotchDesktopSwipeEnabled;
            ChkCalendarEnabled.IsChecked = cfg.CalendarEnabled;
            ChkDownloadsWatch.IsChecked = cfg.DownloadsWatchEnabled;
            ChkLyricsEnabled.IsChecked = cfg.LyricsEnabled;
            ChkUsbDriveHud.IsChecked = cfg.UsbDriveHudEnabled;
            ChkDevBridgeEnabled.IsChecked = cfg.EnableDeveloperBridge;
            TxtCalendarSources.Text = cfg.CalendarIcsSources;
            TxtDownloadsPath.Text = cfg.DownloadsWatchPath;
            TxtLyricsFolders.Text = cfg.LyricsFolders;
            TxtPingHost.Text = cfg.PingHost;
            TxtDevBridgePort.Text = cfg.DeveloperBridgePort.ToString();
            ChkLaunchAtStartup.IsChecked = cfg.LaunchAtStartup;

            TxtWhitelistExes.Text = string.Join(", ", cfg.FullscreenWhitelistExes);
            TxtHideExes.Text = string.Join(", ", cfg.FullscreenHideExes);

            var pinned = cfg.PinnedTabs?.ToHashSet() ?? [];
            ChkTabNow.IsChecked = pinned.Contains(ExpandedTabId.Now);
            ChkTabAudio.IsChecked = pinned.Contains(ExpandedTabId.Audio);
            ChkTabControl.IsChecked = pinned.Contains(ExpandedTabId.Control);
            ChkTabShelf.IsChecked = pinned.Contains(ExpandedTabId.Shelf);
            ChkTabSystem.IsChecked = pinned.Contains(ExpandedTabId.System);
            ChkTabWidgets.IsChecked = pinned.Contains(ExpandedTabId.Widgets);

            ApplyLocalization(_currentLang);
            UpdateSliderReadouts();
            UpdateConditionalEnablement();
            UpdateBezelPreview();
            HideInlineError();
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void PopulateStaticChoices(string lang, AppConfig cfg)
    {
        ComboLanguage.ItemsSource = new[]
        {
            new ChoiceItem<string>("tr", "Türkçe (TR)"),
            new ChoiceItem<string>("en", "English (EN)")
        };

        ComboDockMode.ItemsSource = new[]
        {
            new ChoiceItem<DockPlacementMode>(DockPlacementMode.NotchFlush, Strings.DockPlacementNotchFlush(lang)),
            new ChoiceItem<DockPlacementMode>(DockPlacementMode.FloatingTop, Strings.DockPlacementFloating(lang)),
            new ChoiceItem<DockPlacementMode>(DockPlacementMode.FreeFloat, Strings.DockPlacementFree(lang))
        };

        ComboNotchScalePreset.ItemsSource = new[]
        {
            new ChoiceItem<NotchScalePreset>(NotchScalePreset.Compact, Strings.NotchScaleCompact(lang)),
            new ChoiceItem<NotchScalePreset>(NotchScalePreset.Standard, Strings.NotchScaleStandard(lang)),
            new ChoiceItem<NotchScalePreset>(NotchScalePreset.Wide, Strings.NotchScaleWide(lang))
        };

        ComboMonitorMode.ItemsSource = new[]
        {
            new ChoiceItem<MonitorPlacementMode>(MonitorPlacementMode.PrimaryOnly, Strings.PrimaryMonitor(lang)),
            new ChoiceItem<MonitorPlacementMode>(MonitorPlacementMode.FollowCursor, Strings.Pick(lang, "İmleci Takip Et (Aktif Monitör)", "Follow Cursor (Active Monitor)")),
            new ChoiceItem<MonitorPlacementMode>(MonitorPlacementMode.PinnedMonitor, Strings.Pick(lang, "Belirli Monitöre Sabitle", "Pin to Specific Monitor"))
        };

        var monitors = _orch.MonitorLayout.GetMonitors();
        var monitorChoices = monitors
            .Select(m => new ChoiceItem<string?>(
                m.DeviceName,
                $"{(m.IsPrimary ? Strings.Pick(lang, "[Birincil] ", "[Primary] ") : "")}{m.FriendlyName} — {m.WidthPx}x{m.HeightPx} @ {m.RefreshRateHz}Hz"))
            .ToList();

        if (!string.IsNullOrWhiteSpace(cfg.PinnedMonitorDeviceName) &&
            monitorChoices.All(c => !string.Equals(c.Value, cfg.PinnedMonitorDeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            monitorChoices.Add(new ChoiceItem<string?>(
                cfg.PinnedMonitorDeviceName,
                $"{cfg.PinnedMonitorDeviceName} ({Strings.Pick(lang, "bağlı değil", "disconnected")})"));
        }

        ComboPinnedMonitor.ItemsSource = monitorChoices;

        ComboDetent.ItemsSource = new[]
        {
            new ChoiceItem<HorizontalAnchorDetent>(HorizontalAnchorDetent.Center, Strings.Pick(lang, "Orta", "Center")),
            new ChoiceItem<HorizontalAnchorDetent>(HorizontalAnchorDetent.LeftSafe, Strings.Pick(lang, "Sol Güvenli Bölge", "Left Safe Zone")),
            new ChoiceItem<HorizontalAnchorDetent>(HorizontalAnchorDetent.RightSafe, Strings.Pick(lang, "Sağ Güvenli Bölge", "Right Safe Zone")),
            new ChoiceItem<HorizontalAnchorDetent>(HorizontalAnchorDetent.Custom, Strings.Pick(lang, "Özel Yatay Konum (%)", "Custom Horizontal Ratio (%)"))
        };

        int activeRefreshHz = ResolveActiveRefreshRateHz(monitors, cfg);
        var fpsPresets = new List<int> { 0, 60, 90, 120, 144, 165, 240, 360 };
        if (cfg.TargetFpsCap > 0 && !fpsPresets.Contains(cfg.TargetFpsCap))
        {
            fpsPresets.Add(cfg.TargetFpsCap);
            fpsPresets.Sort();
        }

        ComboTargetFpsCap.ItemsSource = fpsPresets.Select(fps => new ChoiceItem<int>(
            fps,
            fps <= 0
                ? Strings.Pick(lang, $"Otomatik — Monitör ({activeRefreshHz} Hz)", $"Auto — Monitor ({activeRefreshHz} Hz)")
                : $"{fps} FPS")).ToList();

        ComboExpandTrigger.ItemsSource = new[]
        {
            new ChoiceItem<ExpandTriggerMode>(ExpandTriggerMode.HoverDwellOrClick, Strings.Pick(lang, "Üzerine Gelme + Tıklama", "Hover Dwell + Click")),
            new ChoiceItem<ExpandTriggerMode>(ExpandTriggerMode.ClickOnly, Strings.Pick(lang, "Yalnızca Tıklama", "Click Only"))
        };

        var policyChoices = new[]
        {
            new ChoiceItem<FullscreenPolicy>(FullscreenPolicy.HideCompletely, Strings.PolicyHideCompletely(lang)),
            new ChoiceItem<FullscreenPolicy>(FullscreenPolicy.EdgePeekHairline, Strings.PolicyEdgePeekHairline(lang)),
            new ChoiceItem<FullscreenPolicy>(FullscreenPolicy.KeepCompact, Strings.PolicyKeepCompact(lang))
        };

        ComboGamePolicy.ItemsSource = policyChoices;
        ComboVideoPolicy.ItemsSource = policyChoices.ToArray();
        ComboProductivityPolicy.ItemsSource = policyChoices.ToArray();
    }

    private void SelectConfigChoices(AppConfig cfg)
    {
        SelectChoice(ComboLanguage, cfg.Language, StringComparer.OrdinalIgnoreCase);
        SelectChoice(ComboDockMode, cfg.DockPlacement);
        SelectChoice(ComboNotchScalePreset, cfg.NotchScalePreset);
        SelectChoice(ComboMonitorMode, cfg.MonitorMode);
        SelectChoice(ComboDetent, cfg.HorizontalDetent);
        SelectChoice(ComboTargetFpsCap, cfg.TargetFpsCap);
        SelectChoice(ComboExpandTrigger, cfg.ExpandTrigger);
        SelectChoice(ComboGamePolicy, cfg.GameFullscreenPolicy);
        SelectChoice(ComboVideoPolicy, cfg.VideoFullscreenPolicy);
        SelectChoice(ComboProductivityPolicy, cfg.ProductivityFullscreenPolicy);

        string? pinnedTarget = cfg.PinnedMonitorDeviceName;
        if (!string.IsNullOrWhiteSpace(pinnedTarget))
        {
            SelectChoice(ComboPinnedMonitor, pinnedTarget, StringComparer.OrdinalIgnoreCase);
        }
        else if (ComboPinnedMonitor.Items.Count > 0)
        {
            ComboPinnedMonitor.SelectedIndex = 0;
        }
    }

    private static void SelectChoice<T>(System.Windows.Controls.ComboBox combo, T target, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        if (combo.ItemsSource is not IEnumerable<ChoiceItem<T>> items) return;

        foreach (var item in items)
        {
            if (comparer.Equals(item.Value, target))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static T GetSelectedValue<T>(System.Windows.Controls.ComboBox combo, T fallback)
        => combo.SelectedItem is ChoiceItem<T> choice ? choice.Value : fallback;

    private static MonitorDescriptor? ResolveTargetMonitorDescriptor(IReadOnlyList<MonitorDescriptor> monitors, AppConfig cfg, IMonitorLayoutService layoutService)
    {
        if (cfg.MonitorMode == MonitorPlacementMode.FollowCursor)
        {
            var (cx, cy) = IslandWindowController.GetCursorScreenPos();
            return layoutService.GetTargetMonitor(cfg.MonitorMode, cfg.PinnedMonitorDeviceName, cx, cy);
        }

        MonitorDescriptor? target = cfg.MonitorMode == MonitorPlacementMode.PinnedMonitor && !string.IsNullOrWhiteSpace(cfg.PinnedMonitorDeviceName)
            ? monitors.FirstOrDefault(m => string.Equals(m.DeviceName, cfg.PinnedMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            : null;
        return target ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    private int ResolveActiveRefreshRateHz(IReadOnlyList<MonitorDescriptor> monitors, AppConfig cfg)
    {
        var target = ResolveTargetMonitorDescriptor(monitors, cfg, _orch.MonitorLayout);
        return Math.Clamp(target?.RefreshRateHz ?? 60, 24, 360);
    }

    private void ApplyLocalization(string lang)
    {
        Title = $"{Strings.AppTitle(lang)} — {Strings.Settings(lang)}";
        TxtHeaderTitle.Text = Strings.SettingsTitle(lang);
        TxtHeaderSubtitle.Text = Strings.Pick(
            lang,
            "MacBook çentik görünümü, çoklu monitör yerleşimi, yenileme hızı ve modül izinleri",
            "MacBook notch styling, multi-monitor placement, native refresh rate & module controls");

        PreviewMenuWindow.Text = Strings.Pick(lang, "Pencere", "Window");
        PreviewMenuAudio.Text = Strings.TabAudio(lang);
        HdrPreviewSection.Text = Strings.BezelPreviewLabel(lang);
        HdrPlacementSection.Text = Strings.Pick(lang, "Görünüm & Çoklu Monitör Yerleşimi", "Appearance & Multi-Monitor Layout");
        LblLanguage.Text = Strings.LanguageLabel(lang);
        LblDockMode.Text = Strings.DockPlacementLabel(lang);
        LblNotchScalePreset.Text = Strings.NotchScalePresetLabel(lang);
        ChkDormantCameraLens.Content = Strings.DormantCameraLensLabel(lang);
        ChkDynamicAlbumGlow.Content = Strings.DynamicAlbumGlowLabel(lang);
        LblMonitorMode.Text = Strings.Pick(lang, "Monitör Modu", "Monitor Mode");
        LblPinnedMonitor.Text = Strings.TargetMonitorLabel(lang);
        LblDetent.Text = Strings.Pick(lang, "Yatay Hizalama", "Horizontal Alignment");
        LblCustomRatio.Text = Strings.Pick(lang, "Yatay Konum (%)", "Custom Horizontal Ratio (%)");
        LblCustomFreeTop.Text = Strings.Pick(lang, "Serbest Üst Mesafe (dp)", "Free Float Top Offset (dp)");

        HdrBottomDockSection.Text = Strings.DockSectionHeader(lang);
        ChkEnableBottomDock.Content = Strings.EnableBottomDockLabel(lang);
        ChkHideWindowsTaskbar.Content = Strings.HideWindowsTaskbarLabel(lang);
        ChkDockMagnification.Content = Strings.DockMagnificationLabel(lang);
        ChkDockIntellihide.Content = Strings.DockIntellihideLabel(lang);
        LblDockIconSize.Text = Strings.DockIconSizeLabel(lang);

        HdrMotionSection.Text = Strings.Pick(lang, "Performans, Yenileme Hızı & Etkileşim", "Performance, Refresh Rate & Interaction");
        LblTargetFpsCap.Text = Strings.TargetFpsCapLabel(lang);
        LblExpandTrigger.Text = Strings.Pick(lang, "Genişletme Tetikleyici", "Expand Trigger");
        LblHoverDwell.Text = Strings.Pick(lang, "Üzerinde Bekleme (ms)", "Hover Dwell (ms)");
        LblCollapseDelay.Text = Strings.Pick(lang, "Daralma Gecikmesi (ms)", "Collapse Delay (ms)");
        LblHotkey.Text = Strings.HotkeyLabel(lang);
        TxtHotkeyHint.Text = Strings.Pick(
            lang,
            "Örn: Ctrl+Shift+Space, Alt+Space, Win+Shift+D",
            "e.g. Ctrl+Shift+Space, Alt+Space, Win+Shift+D");
        LblMicMuteHotkey.Text = Strings.Pick(lang, "Mikrofon Susturma Kısayolu", "Mic Mute Hotkey");
        LblColorPickerHotkey.Text = Strings.Pick(lang, "Renk Seçici Kısayolu", "Color Picker Hotkey");

        bool amdMpoGuardActive = NativeMethods.HasAmdDisplayAdapter() && _orch.Config.AmdSafeSoftwareCompositing;
        ChkHardwareRendering.Content = amdMpoGuardActive
            ? Strings.Pick(
                lang,
                "Donanım GPU İşleme (AMD Radeon MPO koruması: Güvenli SoftwareOnly)",
                "Hardware GPU Rendering (AMD Radeon MPO guard: Safe SoftwareOnly)")
            : Strings.Pick(
                lang,
                "Donanım GPU İşleme (yeniden başlatma gerekir)",
                "Hardware GPU Rendering (restart required)");
        ChkSpectrumVisualizer.Content = Strings.SpectrumVisualizerLabel(lang);
        ChkAutoDodge.Content = Strings.Pick(
            lang,
            "Auto-Dodge (Shift ile geçici tıklama geçirgenliği)",
            "Auto-Dodge (Shift key for temporary click-through)");
        ChkFadeWhenIdle.Content = Strings.Pick(
            lang,
            "Boştayken Adanın Opaklığını Azalt",
            "Fade Island Opacity When Idle");
        ChkQuietIsland.Content = Strings.Pick(
            lang,
            "Sessiz Ada (Sadece Kritik Uyarılar)",
            "Quiet Island (Critical Alerts Only)");

        HdrFullscreenSection.Text = Strings.FullscreenPolicyLabel(lang);
        LblGamePolicy.Text = Strings.GameFullscreenPolicyLabel(lang);
        LblVideoPolicy.Text = Strings.VideoFullscreenPolicyLabel(lang);
        LblProductivityPolicy.Text = Strings.ProductivityFullscreenPolicyLabel(lang);
        LblWhitelistExes.Text = Strings.Pick(lang, "Her Zaman Kompakt (.exe)", "Always Keep Compact (.exe)");
        TxtWhitelistExes.ToolTip = Strings.Pick(lang, "Virgülle ayrılmış: vlc.exe, potplayermini64.exe", "Comma-separated: vlc.exe, potplayermini64.exe");
        LblHideExes.Text = Strings.Pick(lang, "Her Zaman Gizle (.exe)", "Always Hide (.exe)");
        TxtHideExes.ToolTip = Strings.Pick(lang, "Virgülle ayrılmış: cs2.exe, valorant.exe", "Comma-separated: cs2.exe, valorant.exe");
        ChkGameModeAuto.Content = Strings.Pick(
            lang,
            "Oyun Modu Otomasyonu (Yüksek Performans Güç Planı)",
            "Game Mode Automation (High Performance Power Plan)");
        ChkGameModeHide.Content = Strings.Pick(
            lang,
            "Oyun Modunda Adayı Tamamen Gizle",
            "Hide Island Completely in Game Mode");
        ChkGameModePingPill.Content = Strings.Pick(
            lang,
            "Oyun Modunda S2 Split Canlı Ping Hapını Göster",
            "Show S2 Split Live Ping Pill in Game Mode");

        HdrModulesSection.Text = Strings.Pick(lang, "Sekmeler, Modüller & Pano Kasası", "Tabs, Modules & Clipboard Vault");
        LblPinnedTabs.Text = Strings.Pick(lang, "Görünür Sekmeler (en az 1)", "Pinned Tabs (at least 1)");
        ChkTabNow.Content = Strings.TabNow(lang);
        ChkTabAudio.Content = Strings.TabAudio(lang);
        ChkTabControl.Content = Strings.TabControl(lang);
        ChkTabShelf.Content = Strings.TabShelf(lang);
        ChkTabSystem.Content = Strings.TabSystem(lang);
        ChkTabWidgets.Content = Strings.TabWidgets(lang);
        LblMaxClipboard.Text = Strings.Pick(lang, "Maks. Pano Öğesi", "Max Clipboard Items");
        ChkShowTelemetryPill.Content = Strings.Pick(
            lang,
            "Kompakt Kapsülde CPU/RAM Özeti Göster",
            "Show CPU/RAM Summary in Compact Capsule");
        ChkMicPrivacyAlert.Content = Strings.Pick(
            lang,
            "Mikrofon Aktif Uyarısını Adada Göster",
            "Show Microphone Active Alert in Notch");
        ChkMirrorOsNotifications.Content = Strings.Pick(
            lang,
            "Windows Bildirimlerini Adada Aynala",
            "Mirror Windows Notifications in Island");
        ChkClipboardVault.Content = Strings.Pick(
            lang,
            "Şifreli Pano Kasası (DPAPI) Etkin",
            "Encrypted Clipboard Vault (DPAPI)");
        ChkBtBatteryAlerts.Content = Strings.Pick(
            lang,
            "Bluetooth Bağlantı & Düşük Pil Uyarıları",
            "Bluetooth Connection & Low Battery Alerts");
        ChkNotchDesktopSwipe.Content = Strings.Pick(
            lang,
            "Çentik Üstünde Sanal Masaüstü Geçişi (Yatay / Shift+Tekerlek)",
            "Virtual Desktop Switch on Notch (Horizontal / Shift+Wheel)");
        ChkCalendarEnabled.Content = Strings.Pick(
            lang,
            "Toplantı Geri Sayımı (.ics)",
            "Meeting Countdown (.ics)");
        ChkDownloadsWatch.Content = Strings.DownloadsWatchLabel(lang);
        ChkLyricsEnabled.Content = Strings.LyricsEnabledLabel(lang);
        LblCalendarSources.Text = Strings.CalendarSourcesLabel(lang);
        LblDownloadsPath.Text = Strings.Pick(lang, "İzlenen İndirme Klasörü", "Watched Downloads Folder");
        LblLyricsFolders.Text = Strings.LyricsFoldersLabel(lang);
        ChkUsbDriveHud.Content = Strings.Pick(
            lang,
            "USB / Harici Disk Tak-Çıkar HUD & Güvenle Çıkar",
            "USB / External Drive Connect HUD & Safe Eject");
        ChkDevBridgeEnabled.Content = Strings.Pick(
            lang,
            "Geliştirici Bildirim Köprüsü (HTTP + Named Pipe)",
            "Developer Notification Bridge (HTTP + Named Pipe)");
        LblPingHost.Text = Strings.Pick(lang, "Ping Hedefi (IP / Alan Adı)", "Ping Target (IP / Hostname)");
        LblDevBridgePort.Text = Strings.Pick(lang, "Geliştirici Köprüsü Portu (127.0.0.1)", "Developer Bridge Port (127.0.0.1)");
        ChkLaunchAtStartup.Content = Strings.LaunchAtStartupLabel(lang);

        HdrIdentitySection.Text = Strings.Pick(lang, "Sparse MSIX Paket Kimliği & Eklentiler", "Sparse MSIX Identity & Plugins");
        BtnCopyMsix.Content = Strings.Pick(lang, "Sparse MSIX Komutunu Kopyala", "Copy Sparse MSIX Command");
        BtnOpenPlugins.Content = Strings.Pick(lang, "Widget Eklenti Klasörü", "Widget Plugins Folder");

        var id = _orch.PackageIdentity;
        TxtIdentityStatus.Text = id.HasIdentity
            ? Strings.Pick(
                lang,
                $"MSIX Kimliği Aktif: {id.PackageFullName} (Bildirim Aynalama ve WNF Odak Algılama tam yetkili).",
                $"MSIX Identity Active: {id.PackageFullName} (Notification mirroring & WNF Focus detection enabled).")
            : Strings.Pick(
                lang,
                "Kimlik: Paketsiz (Taşınabilir mod — Wi-Fi/BT/Ses/Parlaklık tam çalışır; tam Windows Bildirim Geçmişi için aşağıdaki scripti çalıştırabilirsiniz).",
                "Identity: Unpackaged (Portable mode — Wi-Fi/BT/Audio/Brightness fully active; run the script below for full Windows Notification Listener access).");

        BtnResetDefaults.Content = Strings.ResetDefaults(lang);
        BtnCancel.Content = Strings.Cancel(lang);
        BtnSave.Content = Strings.Save(lang);

        UpdateRefreshRateHint(lang);
    }

    private void UpdateRefreshRateHint(string lang)
    {
        var monitors = _orch.MonitorLayout.GetMonitors();
        var monMode = GetSelectedValue(ComboMonitorMode, _orch.Config.MonitorMode);
        var pinnedMon = GetSelectedValue<string?>(ComboPinnedMonitor, _orch.Config.PinnedMonitorDeviceName);
        int selectedFps = GetSelectedValue(ComboTargetFpsCap, _orch.Config.TargetFpsCap);

        var tempCfg = new AppConfig
        {
            MonitorMode = monMode,
            PinnedMonitorDeviceName = pinnedMon
        };
        int activeHz = ResolveActiveRefreshRateHz(monitors, tempCfg);

        TxtRefreshRateHint.Text = selectedFps <= 0
            ? Strings.Pick(
                lang,
                $"Otomatik mod: Hedef monitör ({activeHz} Hz) VBlank/DWM temposuyla tam senkronize.",
                $"Auto mode: Synchronized with target monitor VBlank/DWM ({activeHz} Hz).")
            : Strings.Pick(
                lang,
                $"Sabit sınır: {selectedFps} FPS (aktif monitör {activeHz} Hz).",
                $"Manual cap: {selectedFps} FPS (active monitor {activeHz} Hz).");
    }

    private void UpdateBezelPreview()
    {
        if (!IsInitialized) return;

        var dock = GetSelectedValue(ComboDockMode, _orch.Config.DockPlacement);
        var preset = GetSelectedValue(ComboNotchScalePreset, _orch.Config.NotchScalePreset);
        var detent = GetSelectedValue(ComboDetent, _orch.Config.HorizontalDetent);
        double scaleFactor = AppConfig.GetScaleFactor(preset);

        bool isFlush = dock == DockPlacementMode.NotchFlush;
        double previewW = Math.Round(212.0 * scaleFactor, 1);
        double previewH = Math.Round((isFlush ? 36.0 : 34.0) * scaleFactor, 1);
        double earR = isFlush ? Math.Round(11.0 * scaleFactor, 1) : 0.0;
        double topCr = isFlush ? 0.0 : Math.Round(17.0 * scaleFactor, 1);
        double bottomCr = isFlush ? Math.Round(13.0 * scaleFactor, 1) : Math.Round(17.0 * scaleFactor, 1);
        double topY = dock switch
        {
            DockPlacementMode.NotchFlush => 0.0,
            DockPlacementMode.FloatingTop => 8.0,
            DockPlacementMode.FreeFloat => Math.Clamp(10.0 + (SliderCustomFreeTop.Value / 400.0) * 28.0, 8.0, 38.0),
            _ => 8.0
        };

        double canvasW = BezelPreviewCanvas.Width > 10 ? BezelPreviewCanvas.Width : 560.0;
        double ratio = MonitorLayoutCalculator.ResolveAnchorRatio(detent, Math.Round(SliderCustomRatio.Value) / 100.0);
        double usableMargin = previewW * 0.5 + earR + 18.0;
        double centerX = Math.Clamp(canvasW * ratio, usableMargin, Math.Max(usableMargin, canvasW - usableMargin));

        var shellGeom = NotchGeometryBuilder.CreateSnapshotGeometry(centerX, topY, previewW, previewH, earR, topCr, bottomCr, isRim: false);
        var rimGeom = NotchGeometryBuilder.CreateSnapshotGeometry(centerX, topY, previewW, previewH, earR, topCr, bottomCr, isRim: true);

        PreviewShellPath.Data = shellGeom;
        PreviewShellPath.Fill = (Brush)FindResource(isFlush ? "NotchOledShellBrush" : "FloatingOledShellBrush");
        PreviewRimPath.Data = rimGeom;
        PreviewRimPath.Stroke = (Brush)FindResource(isFlush ? "NotchRimGradientBrush" : "FloatingCapsuleRimBrush");

        bool showLens = ChkDormantCameraLens.IsChecked == true;
        PreviewCameraLens.Visibility = showLens ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(PreviewCameraLens, centerX - 6.0);
        Canvas.SetTop(PreviewCameraLens, topY + previewH * 0.5 - 6.0);

        Canvas.SetLeft(PreviewSensorDot, centerX + 12.0);
        Canvas.SetTop(PreviewSensorDot, topY + previewH * 0.5 - 2.25);

        PreviewAlbumGlow.Visibility = ChkDynamicAlbumGlow.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        TxtPreviewDimsBadge.Text = isFlush
            ? $"{previewW:0} × {previewH:0} dp • Ear {earR:0}"
            : $"{previewW:0} × {previewH:0} dp • Y {topY:0} dp";
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;

        string newLang = GetSelectedValue(ComboLanguage, "tr");
        if (string.Equals(newLang, _currentLang, StringComparison.OrdinalIgnoreCase)) return;

        var dock = GetSelectedValue(ComboDockMode, _orch.Config.DockPlacement);
        var scalePreset = GetSelectedValue(ComboNotchScalePreset, _orch.Config.NotchScalePreset);
        var monMode = GetSelectedValue(ComboMonitorMode, _orch.Config.MonitorMode);
        var pinnedMon = GetSelectedValue<string?>(ComboPinnedMonitor, _orch.Config.PinnedMonitorDeviceName);
        var detent = GetSelectedValue(ComboDetent, _orch.Config.HorizontalDetent);
        var fpsCap = GetSelectedValue(ComboTargetFpsCap, _orch.Config.TargetFpsCap);
        var trigger = GetSelectedValue(ComboExpandTrigger, _orch.Config.ExpandTrigger);
        var gamePolicy = GetSelectedValue(ComboGamePolicy, _orch.Config.GameFullscreenPolicy);
        var videoPolicy = GetSelectedValue(ComboVideoPolicy, _orch.Config.VideoFullscreenPolicy);
        var prodPolicy = GetSelectedValue(ComboProductivityPolicy, _orch.Config.ProductivityFullscreenPolicy);

        _suppressUiEvents = true;
        try
        {
            _currentLang = newLang;
            var snapshot = new AppConfig
            {
                Language = newLang,
                DockPlacement = dock,
                NotchScalePreset = scalePreset,
                MonitorMode = monMode,
                PinnedMonitorDeviceName = pinnedMon,
                TargetFpsCap = fpsCap
            };

            PopulateStaticChoices(newLang, snapshot);
            SelectChoice(ComboLanguage, newLang, StringComparer.OrdinalIgnoreCase);
            SelectChoice(ComboDockMode, dock);
            SelectChoice(ComboNotchScalePreset, scalePreset);
            SelectChoice(ComboMonitorMode, monMode);
            if (!string.IsNullOrWhiteSpace(pinnedMon))
            {
                SelectChoice(ComboPinnedMonitor, pinnedMon, StringComparer.OrdinalIgnoreCase);
            }
            else if (ComboPinnedMonitor.Items.Count > 0)
            {
                ComboPinnedMonitor.SelectedIndex = 0;
            }
            SelectChoice(ComboDetent, detent);
            SelectChoice(ComboTargetFpsCap, fpsCap);
            SelectChoice(ComboExpandTrigger, trigger);
            SelectChoice(ComboGamePolicy, gamePolicy);
            SelectChoice(ComboVideoPolicy, videoPolicy);
            SelectChoice(ComboProductivityPolicy, prodPolicy);

            ApplyLocalization(newLang);
            UpdateConditionalEnablement();
            UpdateBezelPreview();
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private void OnPlacementChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateConditionalEnablement();
        UpdateRefreshRateHint(_currentLang);
        UpdateBezelPreview();
    }

    private void OnPreviewToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateBezelPreview();
    }

    private void OnBottomDockToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;
        UpdateConditionalEnablement();
    }

    private void UpdateConditionalEnablement()
    {
        bool dockEnabled = ChkEnableBottomDock.IsChecked == true;
        ChkHideWindowsTaskbar.IsEnabled = dockEnabled;
        ChkDockMagnification.IsEnabled = dockEnabled;
        ChkDockIntellihide.IsEnabled = dockEnabled;
        SliderDockIconSize.IsEnabled = dockEnabled;
        LblDockIconSize.Opacity = dockEnabled ? 1.0 : 0.45;
        ValDockIconSize.Opacity = dockEnabled ? 1.0 : 0.45;
        var monMode = GetSelectedValue(ComboMonitorMode, _orch.Config.MonitorMode);
        bool pinMonitor = monMode == MonitorPlacementMode.PinnedMonitor;
        ComboPinnedMonitor.IsEnabled = pinMonitor;
        LblPinnedMonitor.Opacity = pinMonitor ? 1.0 : 0.45;

        var detent = GetSelectedValue(ComboDetent, _orch.Config.HorizontalDetent);
        bool customX = detent == HorizontalAnchorDetent.Custom;
        SliderCustomRatio.IsEnabled = customX;
        LblCustomRatio.Opacity = customX ? 1.0 : 0.45;
        ValCustomRatio.Opacity = customX ? 1.0 : 0.45;

        var dock = GetSelectedValue(ComboDockMode, _orch.Config.DockPlacement);
        bool freeFloat = dock == DockPlacementMode.FreeFloat;
        SliderCustomFreeTop.IsEnabled = freeFloat;
        LblCustomFreeTop.Opacity = freeFloat ? 1.0 : 0.45;
        ValCustomFreeTop.Opacity = freeFloat ? 1.0 : 0.45;
    }

    private void OnSliderValueUpdated(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        UpdateSliderReadouts();
        if (!_suppressUiEvents)
        {
            UpdateBezelPreview();
        }
    }

    private void UpdateSliderReadouts()
    {
        ValCustomRatio.Text = $"{Math.Round(SliderCustomRatio.Value):0}%";
        ValCustomFreeTop.Text = $"{Math.Round(SliderCustomFreeTop.Value):0} dp";
        ValDockIconSize.Text = $"{Math.Round(SliderDockIconSize.Value):0} dp";
        ValHoverDwell.Text = $"{Math.Round(SliderHoverDwell.Value):0} ms";
        ValCollapseDelay.Text = $"{Math.Round(SliderCollapseDelay.Value):0} ms";
        ValMaxClipboard.Text = $"{Math.Round(SliderMaxClipboard.Value):0}";
    }

    private void OnPinnedTabChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents) return;

        if (CollectCheckedTabs().Count == 0 && sender is System.Windows.Controls.CheckBox cb)
        {
            _suppressUiEvents = true;
            cb.IsChecked = true;
            _suppressUiEvents = false;

            ShowInlineError(Strings.Pick(
                _currentLang,
                "En az bir sekme görünür kalmalıdır.",
                "At least one expanded tab must remain visible."));
        }
        else
        {
            HideInlineError();
        }
    }

    private List<ExpandedTabId> CollectCheckedTabs()
    {
        var list = new List<ExpandedTabId>(6);
        if (ChkTabNow.IsChecked == true) list.Add(ExpandedTabId.Now);
        if (ChkTabAudio.IsChecked == true) list.Add(ExpandedTabId.Audio);
        if (ChkTabControl.IsChecked == true) list.Add(ExpandedTabId.Control);
        if (ChkTabShelf.IsChecked == true) list.Add(ExpandedTabId.Shelf);
        if (ChkTabSystem.IsChecked == true) list.Add(ExpandedTabId.System);
        if (ChkTabWidgets.IsChecked == true) list.Add(ExpandedTabId.Widgets);
        return list;
    }

    private static List<string> ParseExeList(string raw) =>
        raw.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Where(s => !string.IsNullOrWhiteSpace(s))
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .ToList();

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        var defaults = new AppConfig { Language = _currentLang };
        defaults.Sanitize();
        LoadFromConfig(defaults);
        TxtIdentityActionStatus.Text = Strings.Pick(
            _currentLang,
            "Varsayılan ayarlar yüklendi — uygulamak için Kaydet'e basın.",
            "Default values loaded — click Save to apply.");
        TxtIdentityActionStatus.Visibility = Visibility.Visible;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        HideInlineError();

        string newHotkey = string.IsNullOrWhiteSpace(TxtHotkey.Text)
            ? "Ctrl+Shift+Space"
            : TxtHotkey.Text.Trim();
        string newMicHotkey = string.IsNullOrWhiteSpace(TxtMicMuteHotkey.Text)
            ? "Ctrl+Shift+M"
            : TxtMicMuteHotkey.Text.Trim();
        string newPickerHotkey = string.IsNullOrWhiteSpace(TxtColorPickerHotkey.Text)
            ? "Ctrl+Shift+C"
            : TxtColorPickerHotkey.Text.Trim();

        if (!GlobalHotkeyService.TryParseChord(newHotkey, out _, out _))
        {
            ShowInlineError($"{Strings.HotkeyInvalid(_currentLang)} ({newHotkey})");
            TxtHotkey.Focus();
            return;
        }
        if (!GlobalHotkeyService.TryParseChord(newMicHotkey, out _, out _))
        {
            ShowInlineError($"{Strings.HotkeyInvalid(_currentLang)} ({newMicHotkey})");
            TxtMicMuteHotkey.Focus();
            return;
        }
        if (!GlobalHotkeyService.TryParseChord(newPickerHotkey, out _, out _))
        {
            ShowInlineError($"{Strings.HotkeyInvalid(_currentLang)} ({newPickerHotkey})");
            TxtColorPickerHotkey.Focus();
            return;
        }

        if (_hotkeyService.Conflicts(newHotkey, newMicHotkey) ||
            _hotkeyService.Conflicts(newHotkey, newPickerHotkey) ||
            _hotkeyService.Conflicts(newMicHotkey, newPickerHotkey))
        {
            ShowInlineError(Strings.Pick(
                _currentLang,
                "Kısayol tuşları birbiriyle çakışamaz.",
                "Global hotkeys cannot share the same key chord."));
            return;
        }

        int newBridgePort = 47821;
        string rawPort = TxtDevBridgePort.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(rawPort) &&
            (!int.TryParse(rawPort, out newBridgePort) || newBridgePort is < 1024 or > 65535))
        {
            ShowInlineError(Strings.Pick(
                _currentLang,
                "Geliştirici köprüsü portu 1024 ile 65535 arasında olmalıdır.",
                "Developer bridge port must be between 1024 and 65535."));
            TxtDevBridgePort.Focus();
            return;
        }

        string newPingHost = string.IsNullOrWhiteSpace(TxtPingHost.Text)
            ? "1.1.1.1"
            : TxtPingHost.Text.Trim();

        string previousHotkey = _orch.Config.CommandHotkey;
        string previousMicHotkey = _orch.Config.MicMuteHotkey;
        string previousPickerHotkey = _orch.Config.ColorPickerHotkey;

        bool hotkeyChanged = !string.Equals(newHotkey, previousHotkey, StringComparison.OrdinalIgnoreCase);
        bool micHotkeyChanged = !string.Equals(newMicHotkey, previousMicHotkey, StringComparison.OrdinalIgnoreCase);
        bool pickerHotkeyChanged = !string.Equals(newPickerHotkey, previousPickerHotkey, StringComparison.OrdinalIgnoreCase);

        void RollbackHotkeys()
        {
            if (_islandHwnd == 0) return;
            if (hotkeyChanged) _hotkeyService.Unregister(GlobalHotkeyService.HotkeyId);
            if (micHotkeyChanged) _hotkeyService.Unregister(GlobalHotkeyService.MicMuteHotkeyId);
            if (pickerHotkeyChanged) _hotkeyService.Unregister(GlobalHotkeyService.ColorPickerHotkeyId);

            if (hotkeyChanged || !_hotkeyService.IsRegistered)
                _ = _hotkeyService.TryRegister(GlobalHotkeyService.HotkeyId, _islandHwnd, previousHotkey);
            if (micHotkeyChanged)
                _ = _hotkeyService.TryRegister(GlobalHotkeyService.MicMuteHotkeyId, _islandHwnd, previousMicHotkey);
            if (pickerHotkeyChanged)
                _ = _hotkeyService.TryRegister(GlobalHotkeyService.ColorPickerHotkeyId, _islandHwnd, previousPickerHotkey);
        }

        if (_islandHwnd != 0)
        {
            if (hotkeyChanged) _hotkeyService.Unregister(GlobalHotkeyService.HotkeyId);
            if (micHotkeyChanged) _hotkeyService.Unregister(GlobalHotkeyService.MicMuteHotkeyId);
            if (pickerHotkeyChanged) _hotkeyService.Unregister(GlobalHotkeyService.ColorPickerHotkeyId);

            if ((hotkeyChanged || !_hotkeyService.IsRegistered) &&
                !_hotkeyService.TryRegister(GlobalHotkeyService.HotkeyId, _islandHwnd, newHotkey))
            {
                string detail = string.IsNullOrWhiteSpace(_hotkeyService.LastError)
                    ? Strings.HotkeyInvalid(_currentLang)
                    : $"{Strings.HotkeyInvalid(_currentLang)} ({_hotkeyService.LastError})";
                RollbackHotkeys();
                ShowInlineError(detail);
                TxtHotkey.Focus();
                return;
            }

            if (micHotkeyChanged &&
                !_hotkeyService.TryRegister(GlobalHotkeyService.MicMuteHotkeyId, _islandHwnd, newMicHotkey))
            {
                string detail = string.IsNullOrWhiteSpace(_hotkeyService.LastError)
                    ? Strings.HotkeyInvalid(_currentLang)
                    : $"{Strings.HotkeyInvalid(_currentLang)} ({_hotkeyService.LastError})";
                RollbackHotkeys();
                ShowInlineError(detail);
                TxtMicMuteHotkey.Focus();
                return;
            }

            if (pickerHotkeyChanged &&
                !_hotkeyService.TryRegister(GlobalHotkeyService.ColorPickerHotkeyId, _islandHwnd, newPickerHotkey))
            {
                string detail = string.IsNullOrWhiteSpace(_hotkeyService.LastError)
                    ? Strings.HotkeyInvalid(_currentLang)
                    : $"{Strings.HotkeyInvalid(_currentLang)} ({_hotkeyService.LastError})";
                RollbackHotkeys();
                ShowInlineError(detail);
                TxtColorPickerHotkey.Focus();
                return;
            }
        }

        bool wantStartup = ChkLaunchAtStartup.IsChecked == true;
        if (wantStartup != _orch.Config.LaunchAtStartup && !TryApplyWindowsStartup(wantStartup, out string? startupError))
        {
            RollbackHotkeys();

            ShowInlineError(Strings.Pick(
                _currentLang,
                $"Windows başlangıç kaydı güncellenemedi: {startupError}",
                $"Failed to update Windows startup entry: {startupError}"));
            return;
        }

        var cfg = _orch.Config;
        cfg.Language = GetSelectedValue(ComboLanguage, cfg.Language);
        cfg.DockPlacement = GetSelectedValue(ComboDockMode, cfg.DockPlacement);
        cfg.NotchScalePreset = GetSelectedValue(ComboNotchScalePreset, cfg.NotchScalePreset);
        cfg.DormantCameraLensEnabled = ChkDormantCameraLens.IsChecked == true;
        cfg.DynamicAlbumGlowEnabled = ChkDynamicAlbumGlow.IsChecked == true;
        cfg.EnableBottomDock = ChkEnableBottomDock.IsChecked == true;
        cfg.HideWindowsTaskbar = ChkHideWindowsTaskbar.IsChecked == true;
        cfg.DockMagnificationEnabled = ChkDockMagnification.IsChecked == true;
        cfg.DockIntellihideEnabled = ChkDockIntellihide.IsChecked == true;
        cfg.DockIconSizeDip = (int)Math.Round(SliderDockIconSize.Value);
        cfg.MonitorMode = GetSelectedValue(ComboMonitorMode, cfg.MonitorMode);
        if (cfg.MonitorMode == MonitorPlacementMode.PinnedMonitor)
        {
            cfg.PinnedMonitorDeviceName = GetSelectedValue<string?>(ComboPinnedMonitor, cfg.PinnedMonitorDeviceName);
        }
        cfg.HorizontalDetent = GetSelectedValue(ComboDetent, cfg.HorizontalDetent);
        cfg.CustomHorizontalRatio = Math.Round(SliderCustomRatio.Value) / 100.0;
        cfg.CustomFreeTopDip = Math.Round(SliderCustomFreeTop.Value);

        cfg.TargetFpsCap = GetSelectedValue(ComboTargetFpsCap, cfg.TargetFpsCap);
        cfg.ExpandTrigger = GetSelectedValue(ComboExpandTrigger, cfg.ExpandTrigger);
        cfg.HoverDwellMs = (int)Math.Round(SliderHoverDwell.Value);
        cfg.CollapseHysteresisMs = (int)Math.Round(SliderCollapseDelay.Value);
        cfg.CommandHotkey = newHotkey;
        cfg.MicMuteHotkey = newMicHotkey;
        cfg.ColorPickerHotkey = newPickerHotkey;

        cfg.UseHardwareRendering = ChkHardwareRendering.IsChecked == true;
        cfg.EnableSpectrumVisualizer = ChkSpectrumVisualizer.IsChecked == true;
        cfg.AutoDodgeEnabled = ChkAutoDodge.IsChecked == true;
        cfg.FadeWhenIdle = ChkFadeWhenIdle.IsChecked == true;
        cfg.QuietIslandEnabled = ChkQuietIsland.IsChecked == true;

        cfg.GameFullscreenPolicy = GetSelectedValue(ComboGamePolicy, cfg.GameFullscreenPolicy);
        cfg.VideoFullscreenPolicy = GetSelectedValue(ComboVideoPolicy, cfg.VideoFullscreenPolicy);
        cfg.ProductivityFullscreenPolicy = GetSelectedValue(ComboProductivityPolicy, cfg.ProductivityFullscreenPolicy);
        cfg.FullscreenWhitelistExes = ParseExeList(TxtWhitelistExes.Text);
        cfg.FullscreenHideExes = ParseExeList(TxtHideExes.Text);
        cfg.EnableGameModeAutomation = ChkGameModeAuto.IsChecked == true;
        cfg.GameModeHideIsland = ChkGameModeHide.IsChecked == true;
        cfg.GameModeShowPingPill = ChkGameModePingPill.IsChecked == true;

        var tabs = CollectCheckedTabs();
        if (tabs.Count > 0)
        {
            cfg.PinnedTabs = tabs;
        }

        cfg.MaxClipboardItems = (int)Math.Round(SliderMaxClipboard.Value);
        cfg.ShowTelemetryInPill = ChkShowTelemetryPill.IsChecked == true;
        cfg.ShowMicPrivacyAlert = ChkMicPrivacyAlert.IsChecked == true;
        cfg.MirrorOsNotifications = ChkMirrorOsNotifications.IsChecked == true;
        cfg.EnableClipboardVault = ChkClipboardVault.IsChecked == true;
        cfg.BluetoothBatteryAlertsEnabled = ChkBtBatteryAlerts.IsChecked == true;
        cfg.NotchDesktopSwipeEnabled = ChkNotchDesktopSwipe.IsChecked == true;
        cfg.CalendarEnabled = ChkCalendarEnabled.IsChecked == true;
        cfg.DownloadsWatchEnabled = ChkDownloadsWatch.IsChecked == true;
        cfg.LyricsEnabled = ChkLyricsEnabled.IsChecked == true;
        cfg.UsbDriveHudEnabled = ChkUsbDriveHud.IsChecked == true;
        cfg.EnableDeveloperBridge = ChkDevBridgeEnabled.IsChecked == true;
        cfg.CalendarIcsSources = TxtCalendarSources.Text?.Trim() ?? string.Empty;
        cfg.DownloadsWatchPath = TxtDownloadsPath.Text?.Trim() ?? string.Empty;
        cfg.LyricsFolders = TxtLyricsFolders.Text?.Trim() ?? string.Empty;
        cfg.PingHost = newPingHost;
        cfg.DeveloperBridgePort = newBridgePort;
        cfg.LaunchAtStartup = wantStartup;

        try
        {
            _orch.SaveConfig();
            _orch.Coordinator.EnsureValidPinnedTabSelection();
            _onAppliedCallback();
            Close();
        }
        catch (Exception ex)
        {
            ShowInlineError(Strings.Pick(_currentLang, $"Ayarlar kaydedilemedi: {ex.Message}", $"Could not save settings: {ex.Message}"));
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnCopyMsixCommandClick(object sender, RoutedEventArgs e)
    {
        string scriptPath = ResolveSparseMsixScriptPath();
        string cmd = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -BinDir \"{AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}\"";
        _orch.Clipboard.CopyTextToClipboard(cmd);

        TxtIdentityActionStatus.Text = Strings.Pick(
            _currentLang,
            $"Panoya kopyalandı: {cmd}",
            $"Copied to clipboard: {cmd}");
        TxtIdentityActionStatus.Visibility = Visibility.Visible;
    }

    private void OnOpenPluginsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_orch.Widgets.PluginsDirectory);
            Process.Start(new ProcessStartInfo(_orch.Widgets.PluginsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowInlineError(ex.Message);
        }
    }

    private static string ResolveSparseMsixScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "scripts", "register-sparse.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return Path.Combine("scripts", "register-sparse.ps1");
    }

    private static bool TryApplyWindowsStartup(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            if (key is null)
            {
                error = "HKCU Run registry key unavailable.";
                return false;
            }

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    error = "Executable path could not be resolved.";
                    return false;
                }

                key.SetValue("DinamikAda", $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue("DinamikAda", throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void ShowInlineError(string message)
    {
        TxtInlineError.Text = message;
        BannerInlineError.Visibility = Visibility.Visible;
    }

    private void HideInlineError()
    {
        TxtInlineError.Text = string.Empty;
        BannerInlineError.Visibility = Visibility.Collapsed;
    }

    public RenderTargetBitmap RenderToBitmap()
    {
        double w = Width;
        double h = Height;
        SettingsRootGrid.Measure(new System.Windows.Size(w, h));
        SettingsRootGrid.Arrange(new System.Windows.Rect(0, 0, w, h));
        SettingsRootGrid.UpdateLayout();
        UpdateBezelPreview();
        SettingsRootGrid.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(SettingsRootGrid);
        rtb.Freeze();
        return rtb;
    }
}
