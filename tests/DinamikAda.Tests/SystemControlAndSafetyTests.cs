using System.Net.NetworkInformation;
using DinamikAda.Core.Abstractions;
using DinamikAda.Modules.Productivity;
using DinamikAda.Modules.SystemControl;
using DinamikAda.Modules.Telemetry;
using DinamikAda.Modules.Widgets;
using DinamikAda.Platform.Win32.Hotkeys;
using DinamikAda.Platform.Win32.Identity;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;
using Moq;
using Xunit;

namespace DinamikAda.Tests;

public sealed class SystemControlAndSafetyTests
{
    [Fact]
    public async Task CoalescedBrightnessWriter_HandlesConcurrentFloodWithoutDeadlockAndKeepsLatestValue()
    {
        int lastWritten = -1;
        int writeCount = 0;
        var driver = new Mock<IBrightnessHardwareDriver>();
        driver.Setup(d => d.WriteInternalBrightnessAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((pct, _) =>
            {
                Interlocked.Exchange(ref lastWritten, pct);
                Interlocked.Increment(ref writeCount);
                return Task.FromResult(true);
            });

        await using var writer = new CoalescedBrightnessWriter(driver.Object, _ => 0, minSpacing: TimeSpan.FromMilliseconds(5));

        var tasks = Enumerable.Range(0, 8)
            .Select(worker => Task.Run(() =>
            {
                for (int i = 0; i < 60; i++)
                {
                    writer.Enqueue(new BrightnessWriteRequest("DISPLAY1", i, IsInternal: true));
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);
        writer.Enqueue(new BrightnessWriteRequest("DISPLAY1", 83, IsInternal: true));
        await Task.Delay(80);

        Assert.True(writeCount < 480); // Bounded DropOldest dropped stale requests
        Assert.Equal(83, Volatile.Read(ref lastWritten));
    }

    [Fact]
    public void PrivacySensorEvaluator_IgnoresCrashedProcessWithZeroStopTimestampWhenProcessNotRunning()
    {
        var records = new[]
        {
            new ConsentStoreRecord(@"C:#Program Files#CrashedApp#crashed.exe", IsPackaged: false, LastUsedTimeStart: 133000000000000000, LastUsedTimeStop: 0),
            new ConsentStoreRecord(@"C:#Program Files#Discord#discord.exe", IsPackaged: false, LastUsedTimeStart: 133000000000000000, LastUsedTimeStop: 0),
            new ConsentStoreRecord(@"C:#Program Files#ClosedApp#zoom.exe", IsPackaged: false, LastUsedTimeStart: 100, LastUsedTimeStop: 200)
        };

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "discord", "zoom" };
        var active = PrivacySensorEvaluator.FilterActiveConsumers(records, running);

        Assert.Single(active);
        Assert.Equal("Discord", active[0], ignoreCase: true);
    }

    [Fact]
    public void ClipboardPrivacyFilter_HonorsPasswordManagerSuppressionFormats()
    {
        Assert.True(ClipboardPrivacyFilter.ShouldExclude(true, false, null, false, null));
        Assert.True(ClipboardPrivacyFilter.ShouldExclude(false, true, 0, false, null));
        Assert.True(ClipboardPrivacyFilter.ShouldExclude(false, false, null, true, 0));
        Assert.False(ClipboardPrivacyFilter.ShouldExclude(false, true, 1, true, 1));
    }

    [Fact]
    public void WnfFocusParser_HandlesValidAndTruncatedPayloadsSafely()
    {
        Assert.Equal(FocusQuietProfile.Unknown, WnfFocusParser.ParseQuietHoursBuffer(-1, ReadOnlySpan<byte>.Empty));
        Assert.Equal(FocusQuietProfile.Unknown, WnfFocusParser.ParseQuietHoursBuffer(0, new byte[] { 1, 2 }));

        Assert.Equal(FocusQuietProfile.Off, WnfFocusParser.ParseQuietHoursBuffer(0, BitConverter.GetBytes(0)));
        Assert.Equal(FocusQuietProfile.PriorityOnly, WnfFocusParser.ParseQuietHoursBuffer(0, BitConverter.GetBytes(1)));
        Assert.Equal(FocusQuietProfile.AlarmsOnly, WnfFocusParser.ParseQuietHoursBuffer(0, BitConverter.GetBytes(2)));
    }

    [Fact]
    public void FullscreenDetector_EvaluatePureClassifiesVideoAndSkipsDesktopShell()
    {
        var monRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        var progman = FullscreenDetector.EvaluatePure(1, "explorer", "Progman", 0, 0, monRect, monRect, 1);
        Assert.False(progman.IsFullscreen);

        var chromeVideo = FullscreenDetector.EvaluatePure(2 /* QUNS_BUSY */, "chrome", "Chrome_WidgetWin_1", 0, 0, monRect, monRect, 1);
        Assert.True(chromeVideo.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Video, chromeVideo.Kind);

        var d3dGame = FullscreenDetector.EvaluatePure(3 /* QUNS_RUNNING_D3D_FULL_SCREEN */, "eldenring", "GameWnd", 0, 0, monRect, monRect, 1);
        Assert.True(d3dGame.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Game, d3dGame.Kind);
    }

    [Fact]
    public void WidgetCircuitBreaker_TripsAfterThresholdAndResetsAfterCooldown()
    {
        var fakeTime = new FakeTimeProvider();
        var breaker = new WidgetCircuitBreaker(threshold: 3, cooldown: TimeSpan.FromSeconds(30), fakeTime);

        Assert.True(breaker.CanExecute());
        breaker.RecordFailure("err1");
        breaker.RecordFailure("err2");
        Assert.False(breaker.IsOpen);

        breaker.RecordFailure("err3");
        Assert.True(breaker.IsOpen);
        Assert.False(breaker.CanExecute());

        fakeTime.Advance(TimeSpan.FromSeconds(31));
        Assert.False(breaker.IsOpen);
        Assert.True(breaker.CanExecute());
    }

    [Fact]
    public void HungProcessService_ProtectsCriticalWindowsProcesses()
    {
        Assert.True(HungProcessService.IsProtectedProcessName("lsass"));
        Assert.True(HungProcessService.IsProtectedProcessName("DWM"));
        Assert.True(HungProcessService.IsProtectedProcessName("csrss"));
        Assert.False(HungProcessService.IsProtectedProcessName("notepad"));
    }

    [Fact]
    public void FullscreenDetector_RejectsDegenerateRectAndClassifiesModernBrowsersAndTerminals()
    {
        var monRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var emptyRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 0, Bottom = 0 };

        var degenerate = FullscreenDetector.EvaluatePure(3, "eldenring", "GameWnd", 0, 0, emptyRect, monRect, 1);
        Assert.False(degenerate.IsFullscreen);

        var zenVideo = FullscreenDetector.EvaluatePure(2, "zen", "MozillaWindowClass", 0x170B0000, 0, monRect, monRect, 1);
        Assert.True(zenVideo.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Video, zenVideo.Kind);

        var wtProd = FullscreenDetector.EvaluatePure(2, "windowsterminal", "CASCADIA_HOSTING_WINDOW_CLASS", 0, 0, monRect, monRect, 1);
        Assert.True(wtProd.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Productivity, wtProd.Kind);
    }

    [Fact]
    public void GlobalHotkeyService_ValidatesChordsSafelyWithoutLosingPreviousRegistrationState()
    {
        Assert.True(GlobalHotkeyService.TryParseChord("Ctrl+Shift+Space", out uint mods, out uint vk));
        Assert.True(mods != 0 && vk == 0x20);

        Assert.True(GlobalHotkeyService.TryParseChord("Ctrl++", out uint plusMods, out uint plusVk));
        Assert.Equal(NativeMethods.MOD_CONTROL, plusMods);
        Assert.Equal(0xBBu, plusVk);

        Assert.False(GlobalHotkeyService.TryParseChord("OnlyOneKey", out _, out _));

        using var svc = new GlobalHotkeyService();
        Assert.False(svc.TryRegister(0, "Alt+X"));
        Assert.Equal("Ctrl+Shift+Space", svc.ActiveChord);
    }

    [Fact]
    public void FullscreenDetector_DistinguishesPresentationProductivityAndAutoHideMaximizedWindows()
    {
        var monRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var autoHideWorkRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1078 };
        var normalWorkRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1032 };

        var presentation = FullscreenDetector.EvaluatePure(4 /* QUNS_PRESENTATION_MODE */, "slidedeck", "SlideWnd", 0, 0, monRect, monRect, 1);
        Assert.True(presentation.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Productivity, presentation.Kind);

        var unknownBorderless = FullscreenDetector.EvaluatePure(1, "customviewer", "ViewerWnd", 0, 0, monRect, monRect, 1);
        Assert.True(unknownBorderless.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Productivity, unknownBorderless.Kind);

        var browserF11Docs = FullscreenDetector.EvaluatePure(5, "chrome", "Chrome_WidgetWin_1", 0, 0, monRect, monRect, 1, isZoomed: false, workRect: monRect, hasBrowserVideoCue: false);
        Assert.True(browserF11Docs.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Productivity, browserF11Docs.Kind);

        var browserVideoCue = FullscreenDetector.EvaluatePure(5, "chrome", "Chrome_WidgetWin_1", 0, 0, monRect, monRect, 1, isZoomed: false, workRect: monRect, hasBrowserVideoCue: true);
        Assert.True(browserVideoCue.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Video, browserVideoCue.Kind);

        var maximizedAutoHide = FullscreenDetector.EvaluatePure(5, "code", "Chrome_WidgetWin_1", 0x17CF0000, 0, monRect, monRect, 1, isZoomed: true, workRect: autoHideWorkRect);
        Assert.False(maximizedAutoHide.IsFullscreen);
        Assert.Equal("MaximizedWorkArea", maximizedAutoHide.Reason);

        // Even if Shell reports QUNS_BUSY (2) while taskbar is auto-hidden and work area covers the full monitor,
        // a normal maximized captioned browser/editor window must NOT be misclassified as fullscreen.
        var maximizedWithBusyQuns = FullscreenDetector.EvaluatePure(2 /* QUNS_BUSY */, "chrome", "Chrome_WidgetWin_1", 0x17CF0000, 0x200100, monRect, monRect, 1, isZoomed: true, workRect: monRect);
        Assert.False(maximizedWithBusyQuns.IsFullscreen);
        Assert.Equal("MaximizedWorkArea", maximizedWithBusyQuns.Reason);

        var coveredVisibleTaskbar = FullscreenDetector.EvaluatePure(5, "code", "Chrome_WidgetWin_1", 0x17000000, 0, monRect, monRect, 1, isZoomed: true, workRect: normalWorkRect);
        Assert.True(coveredVisibleTaskbar.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Productivity, coveredVisibleTaskbar.Kind);

        var borderlessSecondaryMonitor = FullscreenDetector.EvaluatePure(5, "vlc", "Qt5QWindowIcon", 0x16000000, 0, monRect, monRect, 2, isZoomed: true, workRect: monRect);
        Assert.True(borderlessSecondaryMonitor.IsFullscreen);
        Assert.Equal(FullscreenAppKind.Video, borderlessSecondaryMonitor.Kind);
    }

    [Fact]
    public async Task WidgetHostService_StarterUptimeWidget_ReturnsCleanUtf8TurkishTimeAndMigratesLegacyArgs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dinamikada_wid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string uptimePath = Path.Combine(tempDir, "uptime.widget.json");
        try
        {
            // Write legacy broken TickCount64 widget JSON first to verify auto-migration.
            File.WriteAllText(uptimePath, """
            {
              "id": "starter.uptime",
              "name": "Sistem Çalışma Süresi",
              "kind": "cli",
              "command": "powershell.exe",
              "args": "-NoProfile -Command \"$u=[TimeSpan]::FromMilliseconds([Environment]::TickCount64); @{ title='Açık Kalma'; subtitle=('{0}g {1}s {2}dk' -f $u.Days,$u.Hours,$u.Minutes); status='Aktif'; accentHex='#30D158' } | ConvertTo-Json -Compress\"",
              "refreshSeconds": 20
            }
            """);

            var readyTcs = new TaskCompletionSource<WidgetRenderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var host = new WidgetHostService(tempDir);
            host.WidgetsChanged += (_, list) =>
            {
                var found = list.FirstOrDefault(s => s.WidgetId == "starter.uptime" && s.Status == "Aktif");
                if (found is not null) readyTcs.TrySetResult(found);
            };
            host.Start();
            await host.RefreshDueWidgetsAsync();

            var migratedText = File.ReadAllText(uptimePath);
            Assert.Contains("OutputEncoding", migratedText, StringComparison.Ordinal);

            var completed = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            var snap = completed == readyTcs.Task
                ? await readyTcs.Task
                : host.GetSnapshots().Single(s => s.WidgetId == "starter.uptime");
            Assert.False(snap.IsFaulted, snap.FaultReason);
            Assert.Equal("Açık Kalma", snap.Title);
            Assert.DoesNotContain('?', snap.Title);
            Assert.Matches(@"^\d+g \d+s \d+dk$", snap.Subtitle);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ConnectivityAndSystemService_ClassifiesDesktopPowerSchemesAndFormatsEthernetSpeed()
    {
        var saver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        var balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        var high = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        var ultimateClone = new Guid("b30a0cc6-19fb-49d4-a782-faeb3ecdbac6");

        Assert.Equal(0, ConnectivityAndSystemService.ClassifyPowerScheme(saver, "Güç tasarrufu"));
        Assert.Equal(1, ConnectivityAndSystemService.ClassifyPowerScheme(balanced, "Dengeli"));
        Assert.Equal(2, ConnectivityAndSystemService.ClassifyPowerScheme(high, "Yüksek performans"));
        Assert.Equal(2, ConnectivityAndSystemService.ClassifyPowerScheme(ultimateClone, "Nihai Performans"));

        var schemes = new List<(Guid, string)>
        {
            (balanced, "Dengeli"),
            (high, "Yüksek performans"),
            (saver, "Güç tasarrufu"),
            (ultimateClone, "Nihai Performans")
        };

        Assert.Equal(saver, ConnectivityAndSystemService.PickSchemeForMode(schemes, 0, high));
        Assert.Equal(balanced, ConnectivityAndSystemService.PickSchemeForMode(schemes, 1, high));
        Assert.Equal(ultimateClone, ConnectivityAndSystemService.PickSchemeForMode(schemes, 2, ultimateClone));
        Assert.Equal(high, ConnectivityAndSystemService.PickSchemeForMode(schemes, 2, balanced));

        Assert.Equal("1 Gbps", ConnectivityAndSystemService.FormatLinkSpeed(1_000_000_000));
        Assert.Equal("2.5 Gbps", ConnectivityAndSystemService.FormatLinkSpeed(2_500_000_000));
        Assert.Equal("100 Mbps", ConnectivityAndSystemService.FormatLinkSpeed(100_000_000));
        Assert.Null(ConnectivityAndSystemService.FormatLinkSpeed(0));
    }

    [Fact]
    public void SystemTelemetryService_FiltersVirtualAdaptersAndFormatsGpuLabelsCleanly()
    {
        Assert.False(SystemTelemetryService.ShouldIncludeNetworkAdapter(
            "vEthernet (Default Switch)",
            "Hyper-V Virtual Ethernet Adapter",
            NetworkInterfaceType.Ethernet,
            OperationalStatus.Up,
            hasPhysicalUpAdapter: true));

        Assert.False(SystemTelemetryService.ShouldIncludeNetworkAdapter(
            "Ethernet 2",
            "VirtualBox Host-Only Ethernet Adapter",
            NetworkInterfaceType.Ethernet,
            OperationalStatus.Up,
            hasPhysicalUpAdapter: true));

        Assert.True(SystemTelemetryService.ShouldIncludeNetworkAdapter(
            "Ethernet",
            "Realtek PCIe GbE Family Controller",
            NetworkInterfaceType.Ethernet,
            OperationalStatus.Up,
            hasPhysicalUpAdapter: true));

        Assert.True(SystemTelemetryService.ShouldIncludeNetworkAdapter(
            "Wi-Fi",
            "Intel(R) Wi-Fi 6 AX200",
            NetworkInterfaceType.Wireless80211,
            OperationalStatus.Up,
            hasPhysicalUpAdapter: true));

        Assert.Equal("RX 6750 XT", SystemTelemetryService.ShortenGpuName("AMD Radeon RX 6750 XT"));
        Assert.Equal("RTX 4070 SUPER", SystemTelemetryService.ShortenGpuName("NVIDIA GeForce RTX 4070 SUPER"));
        Assert.Equal("3D", SystemTelemetryService.FormatGpuEngineLabel("luid_0x0_phys_0_eng_0_engtype_3D"));
        Assert.Equal("Video Codec", SystemTelemetryService.FormatGpuEngineLabel("engtype_Video Codec"));
    }

    [Fact]
    public async Task SystemTelemetryService_RapidRefreshesKeepRateWindowStableAndShowRealMemory()
    {
        var hung = new HungProcessService();
        using var telemetry = new SystemTelemetryService(hung);

        var initial = telemetry.Current;
        Assert.True(initial.MemoryTotalGb > 1.0);
        Assert.True(initial.MemoryUsedGb > 0.1);

        await telemetry.RefreshNowAsync();
        var s1 = telemetry.Current;
        await Task.Delay(50);
        await telemetry.RefreshNowAsync();
        var s2 = telemetry.Current;
        await Task.Delay(50);
        await telemetry.RefreshNowAsync();
        var s3 = telemetry.Current;

        Assert.Equal(s1.CpuPercent, s2.CpuPercent, precision: 3);
        Assert.Equal(s2.CpuPercent, s3.CpuPercent, precision: 3);
        Assert.Equal(s1.GpuPercent, s2.GpuPercent, precision: 3);
        Assert.Equal(s1.NetDownMbps, s2.NetDownMbps, precision: 3);
        Assert.InRange(s3.MemoryPercent, 1.0, 100.0);
        Assert.NotEmpty(s3.TopOrHungProcesses);
        Assert.DoesNotContain(s3.TopOrHungProcesses, p => p.ProcessId == 0 || p.ProcessName == "Idle");
    }

    [Fact]
    public void HungProcessService_NormalizesNamesAndComputesBoundedProcessCpuPercent()
    {
        Assert.Equal("RobloxPlayerBeta", HungProcessService.NormalizeImageName("RobloxPlayerBeta.exe"));
        Assert.Equal("Registry", HungProcessService.NormalizeImageName("Registry"));
        Assert.True(HungProcessService.IsProtectedProcessName("dwm"));
        Assert.True(HungProcessService.IsProtectedProcessName("DinamikAda.App"));

        // 1 logical core completely busy for 1.0s out of 12 logical cores => ~8.33%
        double cpu = HungProcessService.ComputeProcessCpuPercent(10_000_000L, 1.0, 12);
        Assert.InRange(cpu, 8.2, 8.5);
        Assert.Equal(0.0, HungProcessService.ComputeProcessCpuPercent(10_000_000L, 0.001, 12));

        var hung = new HungProcessService();
        hung.PrimeCpuBaseline();
        var procs = hung.ScanHeavyAndHungProcesses();
        Assert.NotEmpty(procs);
        Assert.All(procs, p =>
        {
            Assert.True(p.MemoryMb >= 0.0);
            Assert.True(p.ProcessCount >= 1);
        });

        var nonHungNames = procs.Where(p => !p.IsHung).Select(p => p.ProcessName).ToList();
        Assert.Equal(nonHungNames.Count, nonHungNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SystemTelemetryService_FiltersSoftwareGpuLuidInstancesCleanly()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "luid_0x00000000_0x12BBF43B",
            "luid_0x00000000_0x00021865"
        };

        Assert.Equal(
            "luid_0x00000000_0x12BBF43B",
            SystemTelemetryService.ExtractGpuLuidToken("pid_2020_luid_0x00000000_0x12BBF43B_phys_0_eng_0_engtype_3D"));

        Assert.True(SystemTelemetryService.ShouldIncludeGpuInstance(
            "pid_2020_luid_0x00000000_0x12BBF43B_phys_0_eng_0_engtype_3D",
            allowed));

        Assert.False(SystemTelemetryService.ShouldIncludeGpuInstance(
            "pid_21188_luid_0x00000000_0x00019033_phys_0_eng_1_engtype_3D",
            allowed));

        Assert.True(SystemTelemetryService.ShouldIncludeGpuInstance(
            "pid_21188_luid_0x00000000_0x00019033_phys_0_eng_1_engtype_3D",
            null));
    }

    [Fact]
    public async Task SystemTelemetryService_PauseAndResumePreservesLiveMeasurements()
    {
        var hung = new HungProcessService();
        using var telemetry = new SystemTelemetryService(hung);

        telemetry.SetSamplingActive(true);
        await Task.Delay(750);
        var activeSnap = telemetry.Current;

        telemetry.SetSamplingActive(false);
        await Task.Delay(80);
        telemetry.SetSamplingActive(true);
        await Task.Delay(120);
        var resumedSnap = telemetry.Current;

        Assert.True(activeSnap.MemoryTotalGb > 1.0);
        Assert.True(resumedSnap.MemoryTotalGb > 1.0);
        Assert.NotEmpty(resumedSnap.TopOrHungProcesses);
        Assert.True(resumedSnap.CpuPercent >= 0.0 && resumedSnap.CpuPercent <= 100.0);
    }

    [Fact]
    public void FullscreenDetector_FramelessMaximizedThickFrameApp_IsNotClassifiedFullscreen()
    {
        var monRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };

        // Maximized custom-chrome desktop app (e.g., ChatGPT / Discord / Electron) has WS_THICKFRAME | WS_MAXIMIZE without WS_CAPTION.
        var chatGptMax = FullscreenDetector.EvaluatePure(
            2,
            "chatgpt",
            "Chrome_WidgetWin_1",
            0x15070000,
            0x00000100,
            monRect,
            monRect,
            1,
            isZoomed: true,
            workRect: monRect);

        Assert.False(chatGptMax.IsFullscreen);
        Assert.Equal("MaximizedWorkArea", chatGptMax.Reason);

        // True exclusive D3D fullscreen MUST still hide overlays.
        var d3dMax = FullscreenDetector.EvaluatePure(
            3,
            "game",
            "GameWindow",
            0x15070000,
            0,
            monRect,
            monRect,
            1,
            isZoomed: true,
            workRect: monRect);

        Assert.True(d3dMax.IsFullscreen);
    }
}
