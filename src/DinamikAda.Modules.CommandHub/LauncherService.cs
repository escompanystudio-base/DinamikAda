using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Core.MathAndSearch;
using DinamikAda.Modules.Productivity;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Modules.CommandHub;

[ComImport]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);

    [PreserveSig]
    int MoveWindowToDesktop(nint topLevelWindow, ref Guid desktopId);
}

public sealed record AppLaunchItem(string DisplayName, string LaunchTarget, bool IsStoreApp, string? StoreAumid = null);

public sealed record OpenWindowItem(nint Hwnd, string Title, string ProcessName, bool IsOnCurrentDesktop);

public sealed class LauncherService
{
    private const int SW_RESTORE = 9;
    private static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    private readonly ClipboardVaultService _clipboard;
    private readonly IReadOnlyList<ICommandProvider> _customProviders;
    private readonly Action<ExpandedTabId> _navigateTabAction;
    private readonly Func<string, Task> _builtInActionRunner;
    private readonly List<AppLaunchItem> _indexedApps = new();
    private readonly object _indexSync = new();
    private readonly object _rebuildSync = new();
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private Task? _activeRebuildTask;
    private bool _activeRebuildIncludesStoreApps;
    private volatile bool _indexReady;
    private volatile bool _storeAppsIndexed;

    public event EventHandler<string>? CommandFailed;
    public string Language { get; set; } = "tr";

    public LauncherService(
        ClipboardVaultService clipboard,
        IEnumerable<ICommandProvider> customProviders,
        Action<ExpandedTabId> navigateTabAction,
        Func<string, Task> builtInActionRunner,
        string language = "tr")
    {
        _clipboard = clipboard;
        _customProviders = customProviders.OrderBy(p => p.Order).ToList();
        _navigateTabAction = navigateTabAction;
        _builtInActionRunner = builtInActionRunner;
        Language = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
    }

    public Task RebuildAppIndexAsync(CancellationToken ct = default) => RebuildAppIndexAsync(includeStoreApps: true, ct);

    public async Task RebuildAppIndexAsync(bool includeStoreApps, CancellationToken ct = default)
    {
        if (_indexReady && (!includeStoreApps || _storeAppsIndexed))
        {
            return;
        }

        Task rebuildTask;
        lock (_rebuildSync)
        {
            if (_indexReady && (!includeStoreApps || _storeAppsIndexed))
            {
                return;
            }

            if (_activeRebuildTask is not null && !_activeRebuildTask.IsCompleted &&
                (!includeStoreApps || _activeRebuildIncludesStoreApps))
            {
                rebuildTask = _activeRebuildTask;
            }
            else
            {
                _activeRebuildIncludesStoreApps = includeStoreApps;
                rebuildTask = RunIndexBuildLoopAsync(includeStoreApps);
                _activeRebuildTask = rebuildTask;
            }
        }

        await rebuildTask.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunIndexBuildLoopAsync(bool includeStoreApps)
    {
        // Never pass caller typing CancellationTokens into indexing, otherwise cancelling a search
        // would leave a truncated app index marked as ready.
        await _rebuildLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_indexReady && (!includeStoreApps || _storeAppsIndexed))
            {
                return;
            }

            await Task.Run(async () =>
            {
                var found = new Dictionary<string, AppLaunchItem>(StringComparer.OrdinalIgnoreCase);

                string[] startMenuRoots =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                };

                foreach (string root in startMenuRoots)
                {
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                    try
                    {
                        foreach (string lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                        {
                            string name = Path.GetFileNameWithoutExtension(lnk);
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Kaldır", StringComparison.OrdinalIgnoreCase))
                                continue;

                            found.TryAdd(name, new AppLaunchItem(name, lnk, IsStoreApp: false));
                        }

                        foreach (string urlFile in Directory.EnumerateFiles(root, "*.url", SearchOption.AllDirectories))
                        {
                            string name = Path.GetFileNameWithoutExtension(urlFile);
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Kaldır", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string? targetUrl = TryExtractUrlShortcutTarget(urlFile);
                            found.TryAdd(name, new AppLaunchItem(name, targetUrl ?? urlFile, IsStoreApp: false));
                        }
                    }
                    catch
                    {
                        // Skip protected Start Menu subfolders cleanly.
                    }
                }

                if (includeStoreApps)
                {
                    try
                    {
                        var pkgManager = new Windows.Management.Deployment.PackageManager();
                        foreach (var pkg in pkgManager.FindPackagesForUser(string.Empty))
                        {
                            if (pkg.IsFramework || pkg.IsResourcePackage) continue;

                            IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry>? entries = null;
                            try
                            {
                                entries = await pkg.GetAppListEntriesAsync().AsTask().ConfigureAwait(false);
                            }
                            catch
                            {
                                continue;
                            }

                            foreach (var entry in entries)
                            {
                                string display = entry.DisplayInfo?.DisplayName ?? string.Empty;
                                string aumid = entry.AppUserModelId ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(aumid)) continue;
                                found.TryAdd(display, new AppLaunchItem(display, aumid, IsStoreApp: true, StoreAumid: aumid));
                            }
                        }
                    }
                    catch
                    {
                        // PackageManager enumeration optional on unpackaged restricted environments.
                    }
                }

                lock (_indexSync)
                {
                    _indexedApps.Clear();
                    _indexedApps.AddRange(found.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase));
                    _indexReady = true;
                    if (includeStoreApps) _storeAppsIndexed = true;
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    public Task<IReadOnlyList<OpenWindowItem>> EnumerateOpenWindowsAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<OpenWindowItem>>(EnumerateOpenWindows, ct);

    public async Task<IReadOnlyList<CommandEntry>> QueryAsync(string rawQuery, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string lang = Language;
        string q = rawQuery?.Trim() ?? string.Empty;
        if (q.Length > 0 && q[0] is not ('=' or '@' or '#' or '>' or '/'))
        {
            if (!_indexReady)
            {
                await RebuildAppIndexAsync(includeStoreApps: false, ct).ConfigureAwait(false);
            }

            if (!_storeAppsIndexed)
            {
                // Warm Store AUMIDs in background so UWP PackageManager enumeration never stalls typing.
                _ = RebuildAppIndexAsync(includeStoreApps: true);
            }
        }

        ct.ThrowIfCancellationRequested();
        var results = new List<CommandEntry>();

        // 1) Inline Math & Hex/Binary/Unit Calculator (require '=' or explicit math/unit syntax so bare 'e'/'pi'/'1' never hijack apps)
        if (ShouldEvaluateMath(q) && QuickExpressionEvaluator.TryEvaluate(q, out string formatted, out string copyVal))
        {
            results.Add(new CommandEntry(
                Id: "calc:result",
                Title: formatted,
                Subtitle: Strings.Pick(lang, "Kopyalamak için Enter (Hesaplayıcı)", "Press Enter to copy (Calculator)"),
                Category: Strings.Pick(lang, "Hesap", "Math"),
                ShortcutHint: Strings.Pick(lang, "Enter -> Kopyala", "Enter -> Copy"),
                BaseScore: 100_000,
                ExecuteAsync: _ =>
                {
                    _clipboard.CopyTextToClipboard(copyVal);
                    return Task.FromResult(new CommandExecutionResult(
                        true,
                        RestorePreviousFocus: true,
                        FeedbackTitle: Strings.Pick(lang, $"Kopyalandı: {copyVal}", $"Copied: {copyVal}"),
                        CollapseAfter: true));
                },
                IconGlyph: "\uE8EF",
                CopyText: copyVal));
        }

        // 2) Prefix `@` -> Open Window & Virtual Desktop Switcher (off UI thread because COM IVirtualDesktopManager)
        if (q.StartsWith('@') || q.Length == 0)
        {
            string winFilter = q.StartsWith('@') ? q[1..].Trim() : string.Empty;
            var windows = await EnumerateOpenWindowsAsync(ct).ConfigureAwait(false);
            foreach (var win in windows)
            {
                int score = string.IsNullOrEmpty(winFilter)
                    ? (win.IsOnCurrentDesktop ? 350 : 250)
                    : Math.Max(FuzzyMatcher.Score(winFilter, win.Title), FuzzyMatcher.Score(winFilter, win.ProcessName));
                if (score <= 0) continue;

                results.Add(new CommandEntry(
                    Id: $"win:{win.Hwnd}",
                    Title: win.Title,
                    Subtitle: win.IsOnCurrentDesktop
                        ? Strings.Pick(lang, $"{win.ProcessName} • Aktif Masaüstü", $"{win.ProcessName} • Current Desktop")
                        : Strings.Pick(lang, $"{win.ProcessName} • Diğer Sanal Masaüstü", $"{win.ProcessName} • Other Virtual Desktop"),
                    Category: Strings.Pick(lang, "Pencere", "Window"),
                    ShortcutHint: Strings.Pick(lang, "Odakla", "Focus"),
                    BaseScore: (q.StartsWith('@') ? 40_000 : 800) + score,
                    ExecuteAsync: _ =>
                    {
                        if (NativeMethods.IsIconic(win.Hwnd))
                        {
                            NativeMethods.ShowWindow(win.Hwnd, SW_RESTORE);
                        }
                        bool focused = NativeMethods.SetForegroundWindow(win.Hwnd);
                        if (!focused)
                        {
                            string msg = Strings.Pick(lang, "Pencere odaklanamadı.", "Could not focus window.");
                            CommandFailed?.Invoke(this, msg);
                            return Task.FromResult(new CommandExecutionResult(false, RestorePreviousFocus: false, FeedbackTitle: msg, CollapseAfter: false));
                        }
                        return Task.FromResult(new CommandExecutionResult(true, RestorePreviousFocus: false, CollapseAfter: true));
                    },
                    IconGlyph: "\uE737"));
            }
            if (q.StartsWith('@')) return SortTop(results);
        }

        // 3) Prefix `#` -> Clipboard Vault Search (matches PreviewText and FullText)
        if (q.StartsWith('#'))
        {
            string clipFilter = q[1..].Trim();
            foreach (var item in _clipboard.GetEntries())
            {
                int score = string.IsNullOrEmpty(clipFilter)
                    ? (item.IsPinned ? 500 : 300)
                    : Math.Max(
                        FuzzyMatcher.Score(clipFilter, item.PreviewText ?? string.Empty),
                        item.FullText is not null ? FuzzyMatcher.Score(clipFilter, item.FullText) : 0);
                if (score <= 0) continue;

                results.Add(new CommandEntry(
                    Id: $"clip:{item.Id}",
                    Title: item.PreviewText ?? string.Empty,
                    Subtitle: item.IsPinned ? Strings.Pick(lang, $"Sabit • {item.Kind}", $"Pinned • {item.Kind}") : item.Kind,
                    Category: Strings.Pick(lang, "Pano", "Clipboard"),
                    ShortcutHint: Strings.Pick(lang, "Kopyala", "Copy"),
                    BaseScore: 50_000 + score,
                    ExecuteAsync: _ =>
                    {
                        _clipboard.CopyTextToClipboard(item.FullText ?? item.PreviewText ?? string.Empty);
                        return Task.FromResult(new CommandExecutionResult(
                            true,
                            RestorePreviousFocus: true,
                            FeedbackTitle: Strings.Pick(lang, "Panoya alındı", "Copied to clipboard"),
                            CollapseAfter: true));
                    },
                    IconGlyph: "\uE77F"));
            }
            return SortTop(results);
        }

        // 4) Prefix `>` -> Shell Command Execution (user-initiated)
        if (q.StartsWith('>') && q.Length > 1)
        {
            string cmd = q[1..].Trim();
            results.Add(new CommandEntry(
                Id: "shell:run",
                Title: Strings.Pick(lang, $"PowerShell Çalıştır: {cmd}", $"Run PowerShell: {cmd}"),
                Subtitle: Strings.Pick(lang, "Yeni konsol penceresinde çalıştır", "Run in a new console window"),
                Category: Strings.Pick(lang, "Komut", "Command"),
                ShortcutHint: "Enter",
                BaseScore: 95_000,
                ExecuteAsync: _ =>
                {
                    try
                    {
                        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd));
                        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -EncodedCommand {encoded}")
                        {
                            UseShellExecute = true
                        });
                        return Task.FromResult(new CommandExecutionResult(true, RestorePreviousFocus: false, CollapseAfter: true));
                    }
                    catch (Exception ex)
                    {
                        CommandFailed?.Invoke(this, ex.Message);
                        return Task.FromResult(new CommandExecutionResult(false, RestorePreviousFocus: true, FeedbackTitle: ex.Message, CollapseAfter: false));
                    }
                },
                IconGlyph: "\uE756"));

            results.Add(new CommandEntry(
                Id: "shell:cmd",
                Title: Strings.Pick(lang, $"CMD Çalıştır: {cmd}", $"Run CMD: {cmd}"),
                Subtitle: Strings.Pick(lang, "cmd.exe /K ile çalıştır", "Run with cmd.exe /K"),
                Category: Strings.Pick(lang, "Komut", "Command"),
                ShortcutHint: "CMD",
                BaseScore: 80_000,
                ExecuteAsync: _ =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("cmd.exe", $"/K {cmd}") { UseShellExecute = true });
                        return Task.FromResult(new CommandExecutionResult(true, RestorePreviousFocus: false, CollapseAfter: true));
                    }
                    catch (Exception ex)
                    {
                        CommandFailed?.Invoke(this, ex.Message);
                        return Task.FromResult(new CommandExecutionResult(false, RestorePreviousFocus: true, FeedbackTitle: ex.Message, CollapseAfter: false));
                    }
                },
                IconGlyph: "\uE756"));
            return results;
        }

        // 5) Built-in Island Controls & Tabs (`/` or plain text)
        string actionQuery = q.StartsWith('/') ? q[1..].Trim() : q;
        AddBuiltInCommands(actionQuery, lang, results);

        // 6) Installed Apps (Start Menu .lnk + Microsoft Store AUMID)
        if (!q.StartsWith('/'))
        {
            List<AppLaunchItem> appsCopy;
            lock (_indexSync) appsCopy = _indexedApps.ToList();

            var appMatches = await Task.Run(() =>
            {
                var matched = new List<CommandEntry>();
                foreach (var app in appsCopy)
                {
                    ct.ThrowIfCancellationRequested();
                    int score = string.IsNullOrEmpty(q) ? 120 : FuzzyMatcher.Score(q, app.DisplayName);
                    if (score <= 0) continue;

                    matched.Add(new CommandEntry(
                        Id: $"app:{app.LaunchTarget}",
                        Title: app.DisplayName,
                        Subtitle: app.IsStoreApp
                            ? Strings.Pick(lang, "Microsoft Store Uygulaması", "Microsoft Store App")
                            : Strings.Pick(lang, "Başlat Menüsü Uygulaması", "Start Menu App"),
                        Category: Strings.Pick(lang, "Uygulama", "App"),
                        ShortcutHint: Strings.Pick(lang, "Aç", "Open"),
                        BaseScore: 1000 + score,
                        ExecuteAsync: _ => LaunchAppAsync(app),
                        IconGlyph: "\uE71D"));
                }
                return matched;
            }, ct).ConfigureAwait(false);

            results.AddRange(appMatches);
        }

        // 7) External plugin / extension ICommandProviders
        foreach (var provider in _customProviders)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var extra = await provider.QueryAsync(q, ct).ConfigureAwait(false);
                results.AddRange(extra);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CommandFailed?.Invoke(this, ex.Message);
            }
        }

        return SortTop(results);
    }

    private static bool ShouldEvaluateMath(string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return false;
        if (q[0] == '=') return true;
        if (q[0] is '@' or '#' or '>' or '/') return false;

        return q.IndexOfAny(['+', '*', '/', '%', '^', '!', '(']) >= 0
            || (q.Length > 1 && q.AsSpan(1).IndexOf('-') >= 0)
            || q.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || q.StartsWith("0b", StringComparison.OrdinalIgnoreCase)
            || q.Contains(" to ", StringComparison.OrdinalIgnoreCase)
            || q.Contains(" -> ", StringComparison.Ordinal)
            || q.Contains(" in ", StringComparison.OrdinalIgnoreCase);
    }

    private void AddBuiltInCommands(string q, string lang, List<CommandEntry> results)
    {
        (string id, string title, string sub, string glyph, Func<Task> action)[] builtins =
        {
            ("island:tab-now", Strings.Pick(lang, "Akış & Medya Sekmesi", "Feed & Media Tab"), Strings.Pick(lang, "Dinamik Ada medya, timer ve bildirim merkezi", "Dynamic Island media, timers, and notification hub"), "\uE8D6", () => { _navigateTabAction(ExpandedTabId.Now); return Task.CompletedTask; }),
            ("island:tab-audio", Strings.Pick(lang, "Ses & Mikser Sekmesi", "Audio & Mixer Tab"), Strings.Pick(lang, "Uygulama bazlı ses mikseri ve Bluetooth aygıtları", "Per-app audio mixer and Bluetooth devices"), "\uE767", () => { _navigateTabAction(ExpandedTabId.Audio); return Task.CompletedTask; }),
            ("island:tab-control", Strings.Pick(lang, "Sistem Denetimi Sekmesi", "System Control Tab"), Strings.Pick(lang, "Parlaklık, Wi-Fi, BT, Tema ve Güç modları", "Brightness, Wi-Fi, Bluetooth, Theme, and Power modes"), "\uE713", () => { _navigateTabAction(ExpandedTabId.Control); return Task.CompletedTask; }),
            ("island:tab-shelf", Strings.Pick(lang, "Dosya & Pano Kasası", "File Shelf & Clipboard"), Strings.Pick(lang, "Sürükle-bırak rafı ve pano geçmişi", "Drag-and-drop shelf and clipboard history"), "\uE8F1", () => { _navigateTabAction(ExpandedTabId.Shelf); return Task.CompletedTask; }),
            ("island:tab-system", Strings.Pick(lang, "Sistem Telemetri & Kapatıcı", "System Telemetry & Tasks"), Strings.Pick(lang, "CPU, GPU, RAM, Ağ ve yanıt vermeyen uygulamalar", "CPU, GPU, RAM, Network, and hung app terminator"), "\uE9D9", () => { _navigateTabAction(ExpandedTabId.System); return Task.CompletedTask; }),
            ("island:tab-widgets", Strings.Pick(lang, "Widget Merkezi", "Widget Hub"), Strings.Pick(lang, "Modüler widget kartları ve canlı kaynaklar", "Modular widget cards and live feeds"), "\uE8A9", () => { _navigateTabAction(ExpandedTabId.Widgets); return Task.CompletedTask; }),
            ("act:pomo", Strings.Pick(lang, "Pomodoro Başlat (25 dk)", "Start Pomodoro (25 min)"), Strings.Pick(lang, "Odak zamanlayıcısı başlat", "Start a focus countdown timer"), "\uE916", () => _builtInActionRunner("timer:pomo")),
            ("act:timer5", Strings.Pick(lang, "5 Dakika Zamanlayıcı", "5-Minute Timer"), Strings.Pick(lang, "Hızlı geri sayım başlat", "Start a quick countdown"), "\uE916", () => _builtInActionRunner("timer:5m")),
            ("act:quiet", Strings.Pick(lang, "Sessiz Ada Aç/Kapat", "Toggle Quiet Island"), Strings.Pick(lang, "Bildirim ve HUD bannerlarını sustur", "Mute notifications and transient HUD banners"), "\uE7ED", () => _builtInActionRunner("toggle:quiet")),
            ("act:caffeine", Strings.Pick(lang, "Uyanık Tut (Caffeine) Aç/Kapat", "Toggle Keep Awake (Caffeine)"), Strings.Pick(lang, "Ekran ve uyku zamanlayıcısını geçici kilitle", "Temporarily prevent display sleep and idle lock"), "\uEC32", () => _builtInActionRunner("toggle:caffeine")),
            ("act:theme", Strings.Pick(lang, "Açık / Koyu Tema Değiştir", "Toggle Light / Dark Theme"), Strings.Pick(lang, "Windows uygulama temasını anında çevir", "Switch Windows app theme immediately"), "\uE793", () => _builtInActionRunner("toggle:theme")),
            ("act:wifi", Strings.Pick(lang, "Wi-Fi Aç/Kapat", "Toggle Wi-Fi"), Strings.Pick(lang, "Kablosuz ağ adaptörünü değiştir", "Enable or disable wireless radio"), "\uE701", () => _builtInActionRunner("toggle:wifi")),
            ("act:bt", Strings.Pick(lang, "Bluetooth Aç/Kapat", "Toggle Bluetooth"), Strings.Pick(lang, "Bluetooth radyosunu değiştir", "Enable or disable Bluetooth radio"), "\uE702", () => _builtInActionRunner("toggle:bt")),
            ("act:snip", Strings.Pick(lang, "Ekran Alıntısı (Snip)", "Screen Snip"), Strings.Pick(lang, "Windows ekran yakalama aracını başlat", "Launch Windows screen capture tool"), "\uF407", () => _builtInActionRunner("system:snip")),
            ("act:ocr", Strings.Pick(lang, "Bölge OCR & Rafa Al", "Region OCR & Stash"), Strings.Pick(lang, "Ekran alanı seçip metni panoya ve rafa aktar", "Capture screen area, run OCR to clipboard & shelf"), "\uE7C5", () => _builtInActionRunner("system:ocr")),
            ("act:eyedropper", Strings.Pick(lang, "Ekran Renk Seçici (#HEX)", "Screen Color Picker (#HEX)"), Strings.Pick(lang, "8x büyüteçle piksel rengini HEX/RGB/HSL kopyala", "Pick screen pixel with 8x loupe as HEX/RGB/HSL"), "\uEF3C", () => _builtInActionRunner("system:eyedropper")),
            ("act:micmute", Strings.Pick(lang, "Mikrofonu Sustur / Aç", "Toggle Microphone Mute"), Strings.Pick(lang, "Varsayılan mikrofonu anında sustur veya aç", "Instantly mute or unmute default microphone"), "\uEC54", () => _builtInActionRunner("audio:mic-mute")),
            ("act:qr", Strings.Pick(lang, "QR Yerel Ağ Paylaşımı", "QR LAN AirDrop Share"), Strings.Pick(lang, "Raf/Panoyu telefonla anında paylaş & dosya al", "Share shelf/clipboard with phone via QR & receive files"), "\uED14", () => _builtInActionRunner("shelf:qr")),
            ("act:voicenote", Strings.Pick(lang, "Sesli Not Başlat/Durdur", "Start / Stop Voice Note"), Strings.Pick(lang, "Mikrofondan canlı dalgalı sesli not al", "Record live waveform voice note to shelf"), "\uE720", () => _builtInActionRunner("voice:toggle")),
            ("act:pinwin", Strings.Pick(lang, "Aktif Pencereyi Üstte Sabitle", "Pin Window Always-on-Top"), Strings.Pick(lang, "Ön plandaki pencereyi üstte tut / bırak", "Toggle HWND_TOPMOST on active external window"), "\uE718", () => _builtInActionRunner("win:topmost")),
            ("act:snapleft", Strings.Pick(lang, "Pencereyi Sola Sabitle (%50)", "Snap Window Left (50%)"), Strings.Pick(lang, "Aktif pencereyi sol yarıya yasla", "Snap active window to left half of screen"), "\uE8A0", () => _builtInActionRunner("win:snap-left")),
            ("act:snapright", Strings.Pick(lang, "Pencereyi Sağa Sabitle (%50)", "Snap Window Right (50%)"), Strings.Pick(lang, "Aktif pencereyi sağ yarıya yasla", "Snap active window to right half of screen"), "\uE89F", () => _builtInActionRunner("win:snap-right")),
            ("act:snapcenter", Strings.Pick(lang, "Pencereyi Ortala (Odak %70)", "Center Focus Window (70%)"), Strings.Pick(lang, "Aktif pencereyi odak boyutunda ortala", "Center active window at 70% focus bounds"), "\uE73F", () => _builtInActionRunner("win:snap-center")),
            ("act:nextmon", Strings.Pick(lang, "Pencereyi Diğer Monitöre Taşı", "Move Window to Next Monitor"), Strings.Pick(lang, "Aktif pencereyi sonraki ekrana aktar", "Move active window to next monitor"), "\uE7F4", () => _builtInActionRunner("win:next-monitor")),
            ("act:deskprev", Strings.Pick(lang, "Önceki Sanal Masaüstü", "Previous Virtual Desktop"), Strings.Pick(lang, "Sol sanal masaüstüne geç", "Switch to left Windows virtual desktop"), "\uE76B", () => _builtInActionRunner("desktop:prev")),
            ("act:desknext", Strings.Pick(lang, "Sonraki Sanal Masaüstü", "Next Virtual Desktop"), Strings.Pick(lang, "Sağ sanal masaüstüne geç", "Switch to right Windows virtual desktop"), "\uE76C", () => _builtInActionRunner("desktop:next")),
            ("act:schedsleep30", Strings.Pick(lang, "30 dk Sonra Uykuya Al", "Sleep PC in 30 Min"), Strings.Pick(lang, "Zamanlanmış uyku geri sayımını başlat", "Start 30-minute scheduled sleep countdown"), "\uE708", () => _builtInActionRunner("power:sched-sleep-30m")),
            ("act:schedpause30", Strings.Pick(lang, "30 dk Medya Uyku Zamanlayıcısı", "Pause Media in 30 Min"), Strings.Pick(lang, "30 dk sonra çalan medyayı duraklat", "Auto-pause media playback in 30 minutes"), "\uE769", () => _builtInActionRunner("power:sched-pause-30m")),
            ("act:schedcancel", Strings.Pick(lang, "Zamanlanmış Gücü İptal Et", "Cancel Scheduled Power"), Strings.Pick(lang, "Bekleyen uyku/kapatma geri sayımını iptal et", "Cancel pending power countdown"), "\uE711", () => _builtInActionRunner("power:sched-cancel")),
            ("act:meeting", Strings.Pick(lang, "Yaklaşan Toplantıya Katıl", "Join Upcoming Meeting"), Strings.Pick(lang, "Takvim toplantı linkini aç ve mikrofonu denetle", "Open calendar meeting URL and check mic mute"), "\uE787", () => _builtInActionRunner("meeting:join")),
            ("act:lock", Strings.Pick(lang, "Ekranı Kilitle", "Lock Screen"), Strings.Pick(lang, "Windows oturumunu kilitle", "Lock Windows workstation session"), "\uE72E", () => _builtInActionRunner("system:lock")),
            ("act:settings", Strings.Pick(lang, "DinamikAda Ayarları", "DinamikAda Settings"), Strings.Pick(lang, "Yerleşim, kısayollar ve Sparse MSIX ayarları", "Layout, hotkeys, and Sparse MSIX settings"), "\uE713", () => _builtInActionRunner("open:settings")),
        };

        foreach (var item in builtins)
        {
            int score = string.IsNullOrEmpty(q) ? 500 : FuzzyMatcher.Score(q, item.title + " " + item.sub);
            if (score <= 0) continue;

            results.Add(new CommandEntry(
                Id: item.id,
                Title: item.title,
                Subtitle: item.sub,
                Category: Strings.Pick(lang, "Ada Eylemi", "Island Action"),
                ShortcutHint: null,
                BaseScore: 2000 + score,
                ExecuteAsync: async _ =>
                {
                    try
                    {
                        await item.action().ConfigureAwait(false);
                        bool collapse = !item.id.StartsWith("island:tab-", StringComparison.Ordinal);
                        return new CommandExecutionResult(true, RestorePreviousFocus: collapse, CollapseAfter: collapse);
                    }
                    catch (Exception ex)
                    {
                        CommandFailed?.Invoke(this, ex.Message);
                        return new CommandExecutionResult(false, RestorePreviousFocus: true, FeedbackTitle: ex.Message, CollapseAfter: false);
                    }
                },
                IconGlyph: item.glyph));
        }
    }

    private Task<CommandExecutionResult> LaunchAppAsync(AppLaunchItem app)
    {
        try
        {
            if (app.IsStoreApp && !string.IsNullOrWhiteSpace(app.StoreAumid))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.StoreAumid}") { UseShellExecute = true });
                return Task.FromResult(new CommandExecutionResult(true, RestorePreviousFocus: false, CollapseAfter: true));
            }

            Process.Start(new ProcessStartInfo(app.LaunchTarget) { UseShellExecute = true });
            return Task.FromResult(new CommandExecutionResult(true, RestorePreviousFocus: false, CollapseAfter: true));
        }
        catch (Exception ex)
        {
            CommandFailed?.Invoke(this, ex.Message);
            return Task.FromResult(new CommandExecutionResult(false, RestorePreviousFocus: true, FeedbackTitle: ex.Message, CollapseAfter: false));
        }
    }

    private static unsafe List<OpenWindowItem> EnumerateOpenWindows()
    {
        var list = new List<OpenWindowItem>();
        IVirtualDesktopManager? vdm = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidVirtualDesktopManager);
            if (type is not null)
            {
                vdm = Activator.CreateInstance(type) as IVirtualDesktopManager;
            }
        }
        catch
        {
            vdm = null;
        }

        var pidNameCache = new Dictionary<uint, string>();
        try
        {
            NativeMethods.EnumWindows((hwnd, _lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0) return true;

                bool onCurrent = true;
                if (vdm is not null)
                {
                    try { _ = vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out onCurrent); } catch { onCurrent = true; }
                }

                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0)
                {
                    // DWM_CLOAKED_SHELL (2) marks windows living on inactive virtual desktops. Keep them when !onCurrent.
                    if (cloaked != 2 || onCurrent)
                        return true;
                }

                int len = NativeMethods.GetWindowTextLengthW(hwnd);
                if (len <= 0 || len > 256) return true;
                char* buf = stackalloc char[len + 1];
                int read = NativeMethods.GetWindowTextW(hwnd, buf, len + 1);
                if (read <= 0) return true;
                string title = new string(buf, 0, read);

                _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == Environment.ProcessId) return true;

                if (!pidNameCache.TryGetValue(pid, out string? procName))
                {
                    procName = ResolveProcessNameFast(pid);
                    pidNameCache[pid] = procName;
                }

                list.Add(new OpenWindowItem(hwnd, title, procName, onCurrent));
                return true;
            }, 0);
        }
        finally
        {
            if (vdm is not null && Marshal.IsComObject(vdm))
            {
                Marshal.ReleaseComObject(vdm);
            }
        }

        return list;
    }

    private static unsafe string ResolveProcessNameFast(uint pid)
    {
        nint hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc != 0)
        {
            try
            {
                char* buf = stackalloc char[512];
                uint len = 512;
                if (NativeMethods.QueryFullProcessImageNameW(hProc, 0, buf, ref len) && len > 0)
                {
                    string fullPath = new string(buf, 0, (int)len);
                    string file = Path.GetFileNameWithoutExtension(fullPath);
                    if (!string.IsNullOrWhiteSpace(file))
                    {
                        return file;
                    }
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return "app";
        }
    }

    private static List<CommandEntry> SortTop(List<CommandEntry> list) =>
        list.OrderByDescending(c => c.BaseScore)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .ToList();

    private static string? TryExtractUrlShortcutTarget(string urlFilePath)
    {
        try
        {
            int linesRead = 0;
            foreach (string line in File.ReadLines(urlFilePath))
            {
                if (++linesRead > 40) break;
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    string target = line[4..].Trim();
                    return string.IsNullOrWhiteSpace(target) ? null : target;
                }
            }
        }
        catch
        {
            // Ignore malformed or locked .url shortcuts cleanly.
        }
        return null;
    }
}
