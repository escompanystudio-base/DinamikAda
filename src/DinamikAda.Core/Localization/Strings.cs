namespace DinamikAda.Core.Localization;

public static class Strings
{
    public static string Pick(string lang, string tr, string en) =>
        string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? en : tr;

    public static string TabNow(string lang) => Pick(lang, "Akış", "Feed");
    public static string TabAudio(string lang) => Pick(lang, "Ses", "Audio");
    public static string TabControl(string lang) => Pick(lang, "Denetim", "Control");
    public static string TabShelf(string lang) => Pick(lang, "Raf", "Shelf");
    public static string TabSystem(string lang) => Pick(lang, "Sistem", "System");
    public static string TabWidgets(string lang) => Pick(lang, "Araçlar", "Widgets");
    public static string SearchHint(string lang) => Pick(lang, "Uygulama, pencere, komut (= hesap, > kabuk, @ pencere)...", "Search apps, windows, commands (= math, > shell, @ win)...");
    public static string QuietIsland(string lang) => Pick(lang, "Sessiz Ada", "Quiet Island");
    public static string DropHint(string lang) => Pick(lang, "Dosyayı veya metni adaya bırak", "Drop files or text onto the island");
    public static string NoMedia(string lang) => Pick(lang, "Aktif medya yok", "No active media");

    // Common & Tray
    public static string AppTitle(string lang) => Pick(lang, "DinamikAda", "DinamikAda");
    public static string OpenHub(string lang) => Pick(lang, "Adayı Aç", "Open Island");
    public static string CommandPalette(string lang) => Pick(lang, "Komut Paleti", "Command Palette");
    public static string PinOpen(string lang) => Pick(lang, "Sabit Tut", "Pin Open");
    public static string Settings(string lang) => Pick(lang, "Ayarlar", "Settings");
    public static string ExitApp(string lang) => Pick(lang, "Çıkış", "Exit");
    public static string Save(string lang) => Pick(lang, "Kaydet", "Save");
    public static string Cancel(string lang) => Pick(lang, "İptal", "Cancel");
    public static string ResetDefaults(string lang) => Pick(lang, "Varsayılanlara Sıfırla", "Reset to Defaults");
    public static string Close(string lang) => Pick(lang, "Kapat", "Close");

    // Settings Labels & Notch Controls
    public static string SettingsTitle(string lang) => Pick(lang, "DinamikAda Ayarları", "DinamikAda Settings");
    public static string LanguageLabel(string lang) => Pick(lang, "Dil", "Language");
    public static string DockPlacementLabel(string lang) => Pick(lang, "Yerleşim Konumu", "Dock Placement");
    public static string DockPlacementFloating(string lang) => Pick(lang, "Yüzer Kapsül", "Floating Capsule");
    public static string DockPlacementNotchFlush(string lang) => Pick(lang, "Üst Kenara Yapışık", "Notch Flush");
    public static string DockPlacementFree(string lang) => Pick(lang, "Serbest Konum", "Free Float");
    public static string NotchScalePresetLabel(string lang) => Pick(lang, "Çentik Boyutu", "Notch Size");
    public static string NotchScaleCompact(string lang) => Pick(lang, "Kompakt (%92)", "Compact (92%)");
    public static string NotchScaleStandard(string lang) => Pick(lang, "Standart (%100)", "Standard (100%)");
    public static string NotchScaleWide(string lang) => Pick(lang, "Geniş (%106)", "Wide (106%)");
    public static string DormantCameraLensLabel(string lang) => Pick(lang, "Beklemede Kamera Lens Detayı", "Dormant Camera Lens Detail");
    public static string DynamicAlbumGlowLabel(string lang) => Pick(lang, "Dinamik Albüm Işıltısı", "Dynamic Album Glow");
    public static string BezelPreviewLabel(string lang) => Pick(lang, "Canlı Ekran Çentik Önizlemesi", "Live Display Bezel Preview");
    public static string TargetMonitorLabel(string lang) => Pick(lang, "Hedef Monitör", "Target Monitor");
    public static string PrimaryMonitor(string lang) => Pick(lang, "Birincil Monitör", "Primary Monitor");
    public static string VerticalOffsetLabel(string lang) => Pick(lang, "Dikey Boşluk (px)", "Vertical Offset (px)");
    public static string HorizontalOffsetLabel(string lang) => Pick(lang, "Yatay Kaydırma (px)", "Horizontal Offset (px)");
    public static string GlassIntensityLabel(string lang) => Pick(lang, "Cam Yoğunluğu", "Glass Intensity");
    public static string TargetFpsCapLabel(string lang) => Pick(lang, "Kare Hızı Sınırı", "Target FPS Cap");
    public static string TargetFpsAutoMonitor(string lang) => Pick(lang, "Monitör Yenileme Hızı ile Eşleş (Otomatik)", "Match Monitor Refresh Rate (Auto)");
    public static string HardwareRenderingLabel(string lang) => Pick(lang, "Donanım GPU İşleme", "Hardware GPU Rendering");
    public static string SpectrumVisualizerLabel(string lang) => Pick(lang, "Ses Spektrum Görselleştirici", "Audio Spectrum Visualizer");
    public static string LaunchAtStartupLabel(string lang) => Pick(lang, "Windows ile Başlat", "Launch at Windows Startup");
    public static string HotkeyLabel(string lang) => Pick(lang, "Genel Kısayol Tuşu", "Global Hotkey");
    public static string HotkeyInvalid(string lang) => Pick(lang, "Geçersiz veya çakışan kısayol tuşu. Önceki kısayol korundu.", "Invalid or conflicting hotkey. Previous hotkey kept.");

    // Fullscreen Policies
    public static string FullscreenPolicyLabel(string lang) => Pick(lang, "Tam Ekran Davranışı", "Fullscreen Behavior");
    public static string GameFullscreenPolicyLabel(string lang) => Pick(lang, "Oyunlar", "Games");
    public static string VideoFullscreenPolicyLabel(string lang) => Pick(lang, "Tam Ekran Video", "Fullscreen Video");
    public static string ProductivityFullscreenPolicyLabel(string lang) => Pick(lang, "Diğer Tam Ekran Uygulamalar", "Other Fullscreen Apps");
    public static string PolicyHideCompletely(string lang) => Pick(lang, "Tamamen Gizle", "Hide Completely");
    public static string PolicyEdgePeekHairline(string lang) => Pick(lang, "İnce Kenar Çizgisi", "Edge Peek Hairline");
    public static string PolicyKeepCompact(string lang) => Pick(lang, "Kompakt Kalsın", "Keep Compact");

    // Control, Media, Audio, Shelf, System, Widgets
    public static string VolumeLabel(string lang) => Pick(lang, "Ses Seviyesi", "Volume");
    public static string BrightnessLabel(string lang) => Pick(lang, "Parlaklık", "Brightness");
    public static string MuteMic(string lang) => Pick(lang, "Mikrofonu Sustur", "Mute Mic");
    public static string UnmuteMic(string lang) => Pick(lang, "Mikrofon Açık", "Mic Unmuted");
    public static string AudioDeviceSwitchFailed(string lang) => Pick(lang, "Ses aygıtı değiştirilemedi", "Failed to switch audio device");
    public static string PrevTrack(string lang) => Pick(lang, "Önceki Parça", "Previous Track");
    public static string PlayPauseTrack(string lang) => Pick(lang, "Oynat / Duraklat", "Play / Pause");
    public static string NextTrack(string lang) => Pick(lang, "Sonraki Parça", "Next Track");
    public static string MediaSeek(string lang) => Pick(lang, "Parça Konumu", "Media Seek");
    public static string ClearNotifications(string lang) => Pick(lang, "Bildirimleri Temizle", "Clear Notifications");
    public static string ClipboardVault(string lang) => Pick(lang, "Pano Kasası", "Clipboard Vault");
    public static string FileShelfTitle(string lang) => Pick(lang, "Dosya Rafı", "File Shelf");
    public static string ClearShelf(string lang) => Pick(lang, "Rafı Temizle", "Clear Shelf");
    public static string FocusTimerTitle(string lang) => Pick(lang, "Odak Zamanlayıcı", "Focus Timer");
    public static string StartTimer(string lang) => Pick(lang, "Başlat", "Start");
    public static string PauseTimer(string lang) => Pick(lang, "Duraklat", "Pause");
    public static string ResetTimer(string lang) => Pick(lang, "Sıfırla", "Reset");
    public static string TimerReset(string lang) => Pick(lang, "Yeniden Başlat", "Restart");
    public static string CpuLabel(string lang) => Pick(lang, "CPU", "CPU");
    public static string MemoryLabel(string lang) => Pick(lang, "Bellek", "Memory");
    public static string NetworkLabel(string lang) => Pick(lang, "Ağ", "Network");
    public static string BatteryLabel(string lang) => Pick(lang, "Pil", "Battery");
    public static string NoNotifications(string lang) => Pick(lang, "Okunmamış bildirim yok", "No unread notifications");
    public static string EmptyShelf(string lang) => Pick(lang, "Raf boş — dosya veya metin sürükleyip bırakın", "Shelf is empty — drag & drop files or text");

    // 1. Bluetooth Battery HUD
    public static string BtConnectedTitle(string lang) => Pick(lang, "Bluetooth Bağlandı", "Bluetooth Connected");
    public static string BtLowBatteryTitle(string lang) => Pick(lang, "Düşük Pil Uyarısı", "Low Battery Alert");

    // 2. QR / LAN AirDrop Share
    public static string QrShareBtn(string lang) => Pick(lang, "QR Paylaş", "QR Share");
    public static string QrDropStopBtn(string lang) => Pick(lang, "Sunucuyu Durdur", "Stop Share");
    public static string QrScanHint(string lang) => Pick(lang, "Telefon kamerasıyla okut · İndir veya Telefondan Yükle", "Scan with phone camera · Download or Upload from Phone");
    public static string QrNoLocalNetwork(string lang) => Pick(lang, "Yerel ağ IP adresi bulunamadı", "No local network IPv4 address found");
    public static string QrUploadReceived(string lang) => Pick(lang, "Telefondan dosya alındı", "File received from phone");

    // 3. Region OCR Capture
    public static string OcrCaptureTile(string lang) => Pick(lang, "Bölge OCR", "Region OCR");
    public static string OcrDoneTitle(string lang) => Pick(lang, "OCR & Rafa Eklendi", "OCR & Stashed");
    public static string OcrImageOnlyTitle(string lang) => Pick(lang, "Bölge Rafa Eklendi", "Region Stashed");
    public static string OcrNoTextSub(string lang) => Pick(lang, "Görsel rafa eklendi (metin algılanamadı)", "Image stashed to shelf (no text detected)");

    // 4. Meeting / Calendar
    public static string MeetingPrefix(string lang) => Pick(lang, "Toplantı", "Meeting");
    public static string MeetingLive(string lang) => Pick(lang, "Canlı", "Live");
    public static string MeetingJoinTip(string lang) => Pick(lang, "Katılmak için tıkla · Sağ tık: Mikrofonu değiştir", "Click to join · Right-click: Toggle mic");
    public static string CalendarSourcesLabel(string lang) => Pick(lang, "Takvim .ics Kaynakları", "Calendar .ics Sources");

    // 5. Downloads / Transfers
    public static string TransferCompleteTitle(string lang) => Pick(lang, "İndirme Tamamlandı", "Download Complete");
    public static string TransferStashAction(string lang) => Pick(lang, "Rafa Al", "Stash to Shelf");
    public static string DownloadsWatchLabel(string lang) => Pick(lang, "İndirmeleri Çentikte İzle", "Track Downloads in Notch");

    // 6. Lyrics & Audio Format
    public static string LyricsEnabledLabel(string lang) => Pick(lang, "Senkron Şarkı Sözü (.lrc)", "Synced Lyrics (.lrc)");
    public static string LyricsFoldersLabel(string lang) => Pick(lang, "Şarkı Sözü Klasörleri", "Lyrics Folders");

    // 7. Window Pin & Virtual Desktop
    public static string PinWindowBtn(string lang) => Pick(lang, "Üstte Tut", "Always On Top");
    public static string WindowPinnedHud(string lang) => Pick(lang, "Üstte Sabitlendi", "Pinned Always-on-Top");
    public static string WindowUnpinnedHud(string lang) => Pick(lang, "Sabitleme Kaldırıldı", "Unpinned Topmost");
    public static string WindowOpacityLabel(string lang) => Pick(lang, "Pencere Opaklığı", "Window Opacity");
    public static string VirtualDesktopPrev(string lang) => Pick(lang, "Önceki Masaüstü", "Previous Desktop");
    public static string VirtualDesktopNext(string lang) => Pick(lang, "Sonraki Masaüstü", "Next Desktop");

    // 8. Voice Note
    public static string VoiceNoteStartBtn(string lang) => Pick(lang, "Sesli Not", "Voice Note");
    public static string VoiceNoteStopBtn(string lang) => Pick(lang, "Kaydı Bitir", "Stop Rec");
    public static string VoiceNoteRecording(string lang) => Pick(lang, "Sesli Not", "Voice Note");
    public static string VoiceNoteSavedTitle(string lang) => Pick(lang, "Sesli Not Kaydedildi", "Voice Note Saved");
    public static string VoiceNoteTranscriptSub(string lang) => Pick(lang, "Ses rafa, metin panoya eklendi", "Audio stashed, transcript copied");
    public static string VoiceNoteAudioOnlySub(string lang) => Pick(lang, "WAV dosyası rafa eklendi", "WAV audio stashed to shelf");

    // 9. New 10-Feature Suite Strings
    public static string ColorPickerTitle(string lang) => Pick(lang, "Renk Seçici", "Color Picker");
    public static string ColorCopiedSub(string lang) => Pick(lang, "Panoya kopyalandı", "Copied to clipboard");
    public static string SmartTransformUnix(string lang) => Pick(lang, "Tarih", "Date");
    public static string SmartTransformJson(string lang) => Pick(lang, "Biçimle", "Pretty");
    public static string SmartTransformBase64(string lang) => Pick(lang, "Çöz", "Decode");
    public static string SmartTransformCleanUrl(string lang) => Pick(lang, "Temizle", "Clean");
    public static string MicMutedBadge(string lang) => Pick(lang, "Mic Kapalı", "Mic Muted");
    public static string AppDefaultOutput(string lang) => Pick(lang, "Varsayılan", "Default");
    public static string GameModeEnteredTitle(string lang) => Pick(lang, "Oyun Modu Etkin", "Game Mode Active");
    public static string GameModeExitedTitle(string lang) => Pick(lang, "Güç Planı Geri Yüklendi", "Power Plan Restored");
    public static string UsbConnectedTitle(string lang) => Pick(lang, "USB Sürücü Takıldı", "USB Drive Connected");
    public static string UsbOpenFolder(string lang) => Pick(lang, "Klasörü Aç", "Open Folder");
    public static string UsbSafeEject(string lang) => Pick(lang, "Güvenle Çıkar", "Safe Eject");
    public static string UsbEjectedTitle(string lang) => Pick(lang, "Güvenle Çıkarıldı", "Safely Ejected");
    public static string UsbEjectFailedTitle(string lang) => Pick(lang, "Çıkarma Engellendi", "Eject Blocked");
    public static string ExifCleanBtn(string lang) => Pick(lang, "Sıkıştır", "Clean");
    public static string ExifCleanDoneTitle(string lang) => Pick(lang, "EXIF Temizlendi", "EXIF Stripped");
    public static string SnapLeftBtn(string lang) => Pick(lang, "Sol %50", "Left 50%");
    public static string SnapRightBtn(string lang) => Pick(lang, "Sağ %50", "Right 50%");
    public static string SnapCenterBtn(string lang) => Pick(lang, "Odak %70", "Center 70%");
    public static string MoveNextMonitorBtn(string lang) => Pick(lang, "Diğer Ekran", "Next Monitor");
    public static string ScheduledPowerHeader(string lang) => Pick(lang, "Zamanlı Eylem", "Scheduled Action");
    public static string ScheduledSleep(string lang) => Pick(lang, "Uyku", "Sleep");
    public static string ScheduledShutdown(string lang) => Pick(lang, "Kapat", "Shutdown");
    public static string ScheduledLock(string lang) => Pick(lang, "Kilitle", "Lock");
    public static string ScheduledPauseMedia(string lang) => Pick(lang, "Medya Durdur", "Pause Media");
    public static string ScheduledWarningTitle(string lang) => Pick(lang, "Son 1 Dakika Uyarısı", "Final Minute Warning");
    public static string Snooze10mBtn(string lang) => Pick(lang, "+10 dk Ertele", "Snooze +10m");
    public static string DiskUsageLabel(string lang) => Pick(lang, "Disk", "Disk");
    public static string PingLabel(string lang) => Pick(lang, "Gecikme", "Ping");
    public static string VoiceNotesFolderLabel(string lang) => Pick(lang, "Kayıt Klasörü:", "Saved Folder:");
    public static string AudioQualityTitle(string lang) => Pick(lang, "Ses Çıkış Kalitesi", "Audio Output Quality");
    public static string AudioQualityUseMaxBtn(string lang) => Pick(lang, "En Yüksek Kaliteyi Kullan", "Use Highest Quality");
    public static string AudioQualitySettingsBtn(string lang) => Pick(lang, "Ses Ayarları", "Sound Settings");
    public static string NotifCopyBtn(string lang) => Pick(lang, "Kopyala", "Copy");
    public static string NotifDismissBtn(string lang) => Pick(lang, "Kapat", "Dismiss");
    public static string SysCleanDiskBtn(string lang) => Pick(lang, "Disk Temizle", "Clean Temp");
    public static string SysTrimRamBtn(string lang) => Pick(lang, "RAM Boşalt", "Trim RAM");
    public static string SysCloseIdleAppsBtn(string lang) => Pick(lang, "Boşta Uygulamalar", "Idle Bg Apps");
    public static string SysCloseSelectedBtn(string lang) => Pick(lang, "Seçilenleri Kapat", "Close Selected");
    public static string SysNoIdleAppsFound(string lang) => Pick(lang, "Kapatılabilir boşta arka plan uygulaması yok", "No closeable idle background apps found");

    // Bottom Dock & Taskbar Replacement
    public static string DockSectionHeader(string lang) => Pick(lang, "Alt Dock & Görev Çubuğu", "Bottom Dock & Taskbar");
    public static string EnableBottomDockLabel(string lang) => Pick(lang, "macOS Tarzı Senkron Alt Dock", "macOS-Style Synced Bottom Dock");
    public static string HideWindowsTaskbarLabel(string lang) => Pick(lang, "Windows Görev Çubuğunu Gizle (Alt Dock Açıkken)", "Hide Windows Taskbar (While Dock Active)");
    public static string DockMagnificationLabel(string lang) => Pick(lang, "Üzerine Gelince Simge Büyütme Animasyonu", "Hover Icon Magnification Animation");
    public static string DockIconSizeLabel(string lang) => Pick(lang, "Alt Dock Simge Boyutu (dp)", "Bottom Dock Icon Size (dp)");
    public static string RestoreWindowsTaskbar(string lang) => Pick(lang, "Windows Görev Çubuğunu Geri Getir", "Restore Windows Taskbar");
    public static string HideWindowsTaskbarMenu(string lang) => Pick(lang, "Windows Görev Çubuğunu Gizle", "Hide Windows Taskbar");
    public static string DockStartLabel(string lang) => Pick(lang, "Başlat Menüsü", "Start Menu");
    public static string DockIslandSyncLabel(string lang) => Pick(lang, "Üst Adayı Aç", "Toggle Top Island");
    public static string DockTrashLabel(string lang) => Pick(lang, "Çöp Kutusu", "Recycle Bin");
    public static string DockEmptyTrash(string lang) => Pick(lang, "Çöp Kutusunu Boşalt", "Empty Recycle Bin");
    public static string DockOpenTrash(string lang) => Pick(lang, "Çöp Kutusunu Aç", "Open Recycle Bin");
    public static string DockPinApp(string lang) => Pick(lang, "Dock'a Sabitle", "Pin to Dock");
    public static string DockUnpinApp(string lang) => Pick(lang, "Dock'tan Kaldır", "Unpin from Dock");
    public static string DockNewWindow(string lang) => Pick(lang, "Yeni Pencere Aç", "Open New Window");
    public static string DockCloseWindow(string lang) => Pick(lang, "Pencereyi Kapat", "Close Window");
    public static string DockTrayAppsLabel(string lang) => Pick(lang, "Arka Plan Uygulamaları", "Background & Tray Apps");
    public static string TrayOverflowHeader(string lang) => Pick(lang, "Arka Plan Uygulamaları", "Background Apps");
    public static string TrayOverflowEmpty(string lang) => Pick(lang, "Arka planda çalışan gizli uygulama yok", "No background tray apps detected");
    public static string TrayOpenApp(string lang) => Pick(lang, "Aç / Öne Getir", "Open / Bring to Front");
    public static string TrayOpenLocation(string lang) => Pick(lang, "Dosya Konumunu Aç", "Open File Location");
    public static string TrayQuitApp(string lang) => Pick(lang, "Uygulamayı Kapat", "Quit Background App");
    public static string DockFloatOverAppsMenu(string lang) => Pick(lang, "Yüzer Kavisli Mod (Uygulamalar Alta İnsin)", "Floating Curved Mode (Full-Height Apps)");
    public static string DockReserveStripMenu(string lang) => Pick(lang, "Alt Şerit Boşluğunu Koru", "Reserve Full Bottom Strip");

    // Multi-Account AI Subscription & Quota Reset Tracker
    public static string AiQuotaSectionTitle(string lang) => Pick(lang, "AI Abonelik & Kota Takibi", "AI Subscriptions & Quota");
    public static string AiQuotaReadyBadge(string lang) => Pick(lang, "HAZIR", "READY");
    public static string AiQuotaCooldownBadge(string lang) => Pick(lang, "BEKLEMEDE", "COOLDOWN");
    public static string AiQuotaMarkExhaustedBtn(string lang) => Pick(lang, "Limit Doldu", "Hit Limit");
    public static string AiQuotaMarkReadyBtn(string lang) => Pick(lang, "Hazır Yap", "Mark Ready");
    public static string AiQuotaAddCustomBtn(string lang) => Pick(lang, "+ Özel", "+ Custom");
    public static string AiQuotaEditTitle(string lang) => Pick(lang, "AI Hesabı Ekle / Düzenle", "Add / Edit AI Account");
    public static string AiQuotaAccountNameLabel(string lang) => Pick(lang, "Hesap Etiketi", "Account Label");
    public static string AiQuotaProviderLabel(string lang) => Pick(lang, "Platform", "Provider");
    public static string AiQuotaWindowMinutesLabel(string lang) => Pick(lang, "Yenilenme Süresi (dk)", "Reset Window (min)");
    public static string AiQuotaNotifyResetLabel(string lang) => Pick(lang, "Kota açılınca adada bildir", "Notify on Island when quota resets");
    public static string AiQuotaUnlockedTitle(string lang, string accountLabel) =>
        Pick(lang, $"Kota Yenilendi · {accountLabel}", $"Quota Reset · {accountLabel}");
    public static string AiQuotaUnlockedSub(string lang, string provider) =>
        Pick(lang, $"{provider} hesabı yeniden kullanıma hazır", $"{provider} account is ready to use again");
    public static string AiQuotaEmpty(string lang) =>
        Pick(lang, "Henüz takip edilen AI hesabı yok. Yukarıdan hızlıca ekleyin.", "No AI accounts added yet. Use quick buttons above.");
    public static string AiQuotaAllReadySummary(string lang, int total) =>
        Pick(lang, $"{total}/{total} Hazır", $"{total}/{total} Ready");
    public static string AiQuotaMixedSummary(string lang, int ready, int total, string nextClock) =>
        Pick(lang, $"{ready}/{total} Hazır · Sonraki {nextClock}", $"{ready}/{total} Ready · Next {nextClock}");

    public static string Get(string lang, string key) => key switch
    {
        nameof(TabNow) => TabNow(lang),
        nameof(TabAudio) => TabAudio(lang),
        nameof(TabControl) => TabControl(lang),
        nameof(TabShelf) => TabShelf(lang),
        nameof(TabSystem) => TabSystem(lang),
        nameof(TabWidgets) => TabWidgets(lang),
        nameof(SearchHint) => SearchHint(lang),
        nameof(QuietIsland) => QuietIsland(lang),
        nameof(DropHint) => DropHint(lang),
        nameof(NoMedia) => NoMedia(lang),
        nameof(AppTitle) => AppTitle(lang),
        nameof(OpenHub) => OpenHub(lang),
        nameof(CommandPalette) => CommandPalette(lang),
        nameof(PinOpen) => PinOpen(lang),
        nameof(Settings) => Settings(lang),
        nameof(ExitApp) => ExitApp(lang),
        nameof(Save) => Save(lang),
        nameof(Cancel) => Cancel(lang),
        nameof(ResetDefaults) => ResetDefaults(lang),
        nameof(Close) => Close(lang),
        nameof(SettingsTitle) => SettingsTitle(lang),
        nameof(LanguageLabel) => LanguageLabel(lang),
        nameof(DockPlacementLabel) => DockPlacementLabel(lang),
        nameof(DockPlacementFloating) => DockPlacementFloating(lang),
        nameof(DockPlacementNotchFlush) => DockPlacementNotchFlush(lang),
        nameof(DockPlacementFree) => DockPlacementFree(lang),
        nameof(NotchScalePresetLabel) => NotchScalePresetLabel(lang),
        nameof(NotchScaleCompact) => NotchScaleCompact(lang),
        nameof(NotchScaleStandard) => NotchScaleStandard(lang),
        nameof(NotchScaleWide) => NotchScaleWide(lang),
        nameof(DormantCameraLensLabel) => DormantCameraLensLabel(lang),
        nameof(DynamicAlbumGlowLabel) => DynamicAlbumGlowLabel(lang),
        nameof(BezelPreviewLabel) => BezelPreviewLabel(lang),
        nameof(TargetMonitorLabel) => TargetMonitorLabel(lang),
        nameof(PrimaryMonitor) => PrimaryMonitor(lang),
        nameof(VerticalOffsetLabel) => VerticalOffsetLabel(lang),
        nameof(HorizontalOffsetLabel) => HorizontalOffsetLabel(lang),
        nameof(GlassIntensityLabel) => GlassIntensityLabel(lang),
        nameof(TargetFpsCapLabel) => TargetFpsCapLabel(lang),
        nameof(TargetFpsAutoMonitor) => TargetFpsAutoMonitor(lang),
        nameof(HardwareRenderingLabel) => HardwareRenderingLabel(lang),
        nameof(SpectrumVisualizerLabel) => SpectrumVisualizerLabel(lang),
        nameof(LaunchAtStartupLabel) => LaunchAtStartupLabel(lang),
        nameof(HotkeyLabel) => HotkeyLabel(lang),
        nameof(HotkeyInvalid) => HotkeyInvalid(lang),
        nameof(FullscreenPolicyLabel) => FullscreenPolicyLabel(lang),
        nameof(GameFullscreenPolicyLabel) => GameFullscreenPolicyLabel(lang),
        nameof(VideoFullscreenPolicyLabel) => VideoFullscreenPolicyLabel(lang),
        nameof(ProductivityFullscreenPolicyLabel) => ProductivityFullscreenPolicyLabel(lang),
        nameof(PolicyHideCompletely) => PolicyHideCompletely(lang),
        nameof(PolicyEdgePeekHairline) => PolicyEdgePeekHairline(lang),
        nameof(PolicyKeepCompact) => PolicyKeepCompact(lang),
        nameof(VolumeLabel) => VolumeLabel(lang),
        nameof(BrightnessLabel) => BrightnessLabel(lang),
        nameof(MuteMic) => MuteMic(lang),
        nameof(UnmuteMic) => UnmuteMic(lang),
        nameof(AudioDeviceSwitchFailed) => AudioDeviceSwitchFailed(lang),
        nameof(PrevTrack) => PrevTrack(lang),
        nameof(PlayPauseTrack) => PlayPauseTrack(lang),
        nameof(NextTrack) => NextTrack(lang),
        nameof(MediaSeek) => MediaSeek(lang),
        nameof(ClearNotifications) => ClearNotifications(lang),
        nameof(ClipboardVault) => ClipboardVault(lang),
        nameof(FileShelfTitle) => FileShelfTitle(lang),
        nameof(ClearShelf) => ClearShelf(lang),
        nameof(FocusTimerTitle) => FocusTimerTitle(lang),
        nameof(StartTimer) => StartTimer(lang),
        nameof(PauseTimer) => PauseTimer(lang),
        nameof(ResetTimer) => ResetTimer(lang),
        nameof(TimerReset) => TimerReset(lang),
        nameof(CpuLabel) => CpuLabel(lang),
        nameof(MemoryLabel) => MemoryLabel(lang),
        nameof(NetworkLabel) => NetworkLabel(lang),
        nameof(BatteryLabel) => BatteryLabel(lang),
        nameof(NoNotifications) => NoNotifications(lang),
        nameof(EmptyShelf) => EmptyShelf(lang),
        _ => key
    };
}
