using System.Collections.Immutable;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Arbitration;
using DinamikAda.Core.Config;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Modules.Productivity;
using DinamikAda.Modules.SystemControl;
using DinamikAda.Modules.Telemetry;
using DinamikAda.Platform.Win32.Hotkeys;
using DinamikAda.Platform.Win32.Interop;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace DinamikAda.Tests;

public sealed class FeatureSuite10Tests
{
    [Fact]
    public void AppConfig_SanitizeAndHotkeys_ValidateSuite10SettingsAndChords()
    {
        var cfg = new AppConfig
        {
            DeveloperBridgePort = 80, // invalid privileged port -> resets to 47821
            PingHost = "   ",
            ColorPickerHotkey = "",
            MicMuteHotkey = "  ",
            AppAudioRoutes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["  spotify.exe  "] = "  {0.0.0.00000000}.{guid-1}  ",
                ["bad_empty_target"] = "   ",
                [""] = "ignored"
            }
        };

        cfg.Sanitize();

        Assert.Equal(47821, cfg.DeveloperBridgePort);
        Assert.Equal("1.1.1.1", cfg.PingHost);
        Assert.Equal("Ctrl+Shift+C", cfg.ColorPickerHotkey);
        Assert.Equal("Ctrl+Shift+M", cfg.MicMuteHotkey);
        Assert.Single(cfg.AppAudioRoutes);
        Assert.Equal("{0.0.0.00000000}.{guid-1}", cfg.AppAudioRoutes["SPOTIFY.EXE"]);

        Assert.Equal(0x4442, GlobalHotkeyService.MicMuteHotkeyId);
        Assert.Equal(0x4443, GlobalHotkeyService.ColorPickerHotkeyId);

        Assert.True(GlobalHotkeyService.TryParseChord("Ctrl+Shift+C", out uint colorMods, out uint colorVk));
        Assert.Equal((uint)(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT), colorMods);
        Assert.Equal((uint)'C', colorVk);

        Assert.True(GlobalHotkeyService.TryParseChord("Ctrl+Shift+M", out uint micMods, out uint micVk));
        Assert.Equal((uint)(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT), micMods);
        Assert.Equal((uint)'M', micVk);

        var hotkeys = new GlobalHotkeyService();
        Assert.True(hotkeys.Conflicts("ctrl+shift+c", "Ctrl+Shift+C"));
        Assert.False(hotkeys.Conflicts("Ctrl+Shift+C", "Ctrl+Shift+M"));
    }

    [Fact]
    public void ClipboardVaultService_TryDetectSmartAction_HandlesAll4TransformsAndRejectsPlainText()
    {
        string expectedLocalSec = DateTimeOffset.FromUnixTimeSeconds(1700000000)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var unixSec = ClipboardVaultService.TryDetectSmartAction("1700000000", "en");
        Assert.NotNull(unixSec);
        Assert.Equal(SmartClipboardKind.UnixTimestamp, unixSec!.Kind);
        Assert.Contains(expectedLocalSec, unixSec.TransformedText);

        var unixMs = ClipboardVaultService.TryDetectSmartAction("1700000000123", "tr");
        Assert.NotNull(unixMs);
        Assert.Equal(SmartClipboardKind.UnixTimestamp, unixMs!.Kind);
        Assert.Contains(expectedLocalSec, unixMs.TransformedText);

        var json = ClipboardVaultService.TryDetectSmartAction("""{"name":"DinamikAda","count":10}""", "en");
        Assert.NotNull(json);
        Assert.Equal(SmartClipboardKind.JsonFormat, json!.Kind);
        Assert.Contains('\n', json.TransformedText);
        Assert.Contains("\"name\": \"DinamikAda\"", json.TransformedText);

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Merhaba DinamikAda Test!"));
        var decoded = ClipboardVaultService.TryDetectSmartAction(b64, "tr");
        Assert.NotNull(decoded);
        Assert.Equal(SmartClipboardKind.Base64Text, decoded!.Kind);
        Assert.Equal("Merhaba DinamikAda Test!", decoded.TransformedText);

        var dirtyUrl = ClipboardVaultService.TryDetectSmartAction(
            "https://example.com/shop?utm_source=newsletter&fbclid=xyz123&item=42",
            "en");
        Assert.NotNull(dirtyUrl);
        Assert.Equal(SmartClipboardKind.CleanUrl, dirtyUrl!.Kind);
        Assert.Equal("https://example.com/shop?item=42", dirtyUrl.TransformedText);

        Assert.Null(ClipboardVaultService.TryDetectSmartAction("sadece duz metin icerigi", "tr"));
    }

    [Fact]
    public void IslandCoordinator_GameModeFullscreenPolicies_HideOrKeepPingPillAccurately()
    {
        var fakeTime = new FakeTimeProvider();
        var hideCfg = new AppConfig
        {
            GameFullscreenPolicy = FullscreenPolicy.HideCompletely
        };
        var hideCoordinator = new IslandCoordinator(hideCfg, fakeTime);
        hideCoordinator.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Game, "eldenring", 1, "Game"));
        Assert.Equal(IslandPresentationState.S0Hidden, hideCoordinator.Current.State);

        var keepCfg = new AppConfig
        {
            GameFullscreenPolicy = FullscreenPolicy.KeepCompact
        };
        var keepCoordinator = new IslandCoordinator(keepCfg, fakeTime);
        keepCoordinator.UpdateFullscreenState(new FullscreenSnapshot(true, FullscreenAppKind.Game, "cs2", 1, "Game"));
        keepCoordinator.PublishLiveActivity(
            new LiveActivityState(
                SourceId: "game.ping",
                Kind: ActivityKind.Telemetry,
                Priority: 48,
                PrimaryText: "14 ms",
                SecondaryText: "cs2",
                AccentHex: "#30D158",
                IconGlyph: "\uE968",
                TargetTab: ExpandedTabId.System),
            "game.ping");

        var snap = keepCoordinator.Current;
        Assert.Equal(IslandPresentationState.S2Compact, snap.State);
        Assert.Equal(ActivityKind.Telemetry, snap.PrimaryActivity?.Kind);
        Assert.Equal("14 ms", snap.PrimaryActivity?.PrimaryText);
    }

    [Fact]
    public void SystemTelemetryService_SensorsDisksAndPing_NeverFabricateMissingValues()
    {
        int[] unsupportedSensors = new int[512];
        var emptyParsed = SystemTelemetryService.ParseAdlPmLogSensors(unsupportedSensors);
        Assert.Null(emptyParsed.TempC);
        Assert.Null(emptyParsed.FanRpm);
        Assert.Null(emptyParsed.FanPercent);

        int[] populatedSensors = new int[512];
        populatedSensors[8 * 2] = 1;
        populatedSensors[8 * 2 + 1] = 63; // Edge Temp C
        populatedSensors[14 * 2] = 1;
        populatedSensors[14 * 2 + 1] = 1480; // Fan RPM
        populatedSensors[15 * 2] = 1;
        populatedSensors[15 * 2 + 1] = 44; // Fan %

        var parsed = SystemTelemetryService.ParseAdlPmLogSensors(populatedSensors);
        Assert.Equal(63.0, parsed.TempC);
        Assert.Equal(1480, parsed.FanRpm);
        Assert.Equal(44, parsed.FanPercent);

        var cDrive = SystemTelemetryService.BuildDiskVolumeSnapshot(
            @"C:\",
            DriveType.Fixed,
            isReady: true,
            totalSizeBytes: 100L * 1024 * 1024 * 1024,
            availableFreeSpaceBytes: 25L * 1024 * 1024 * 1024,
            volumeLabel: "System");
        Assert.NotNull(cDrive);
        Assert.Equal("C:", cDrive!.DriveLetter);
        Assert.Equal(100.0, cDrive.TotalGb);
        Assert.Equal(75.0, cDrive.UsedGb);
        Assert.Equal(75.0, cDrive.UsedPercent);

        Assert.Null(SystemTelemetryService.BuildDiskVolumeSnapshot(@"Z:\", DriveType.Network, true, 1000, 500, "Net"));
        Assert.Null(SystemTelemetryService.BuildDiskVolumeSnapshot(@"D:\", DriveType.Fixed, false, 1000, 500, "Offline"));

        Assert.Equal("1.1.1.1", SystemTelemetryService.NormalizePingHost("   "));
        Assert.Equal("8.8.8.8", SystemTelemetryService.NormalizePingHost(" 8.8.8.8 "));

        var snap = new SystemTelemetrySnapshot(
            CpuPercent: 21.5,
            MemoryPercent: 52.0,
            MemoryUsedGb: 16.6,
            MemoryTotalGb: 32.0,
            GpuPercent: 9.0,
            NetUpMbps: 0.4,
            NetDownMbps: 1.8,
            HasBattery: false,
            BatteryPercent: 0,
            IsCharging: false,
            BatteryWatts: null,
            BatteryRemaining: null,
            TopOrHungProcesses: ImmutableArray<ProcessSummary>.Empty,
            GpuName: "RX 6750 XT",
            GpuEngine: "3D",
            GpuVramUsedGb: 1.8,
            GpuVramTotalGb: 12.0,
            GpuTemperatureC: null,
            GpuFanRpm: null,
            GpuFanPercent: null,
            DiskReadMbps: 4.2,
            DiskWriteMbps: 1.5,
            PingMs: null,
            PingHost: "1.1.1.1",
            DiskVolumes: [cDrive]);

        Assert.Null(snap.GpuTemperatureC);
        Assert.Null(snap.GpuFanRpm);
        Assert.Null(snap.PingMs);
        Assert.Single(snap.DiskVolumes);
    }

    [Fact]
    public void UsbDriveMonitorService_ParsesDriveMasksAndBuildsArrivalMetrics()
    {
        // Bits 3 and 4 => D:\ and E:\
        var letters = UsbDriveMonitorService.DriveLettersFromUnitMask(0b11000u);
        Assert.Equal([@"D:\", @"E:\"], letters);

        var arrival = UsbDriveMonitorService.BuildArrivalInfo(
            @"E:\",
            DriveType.Removable,
            isReady: true,
            volumeLabel: "BACKUP",
            totalBytes: 32L * 1024 * 1024 * 1024,
            availableFreeBytes: 12L * 1024 * 1024 * 1024);

        Assert.NotNull(arrival);
        Assert.Equal("E:\\", arrival!.DriveRoot);
        Assert.Equal("E:", arrival.DriveLetter);
        Assert.Equal("BACKUP (E:)", arrival.DisplayName);
        Assert.Equal(32.0, arrival.TotalGb);
        Assert.Equal(12.0, arrival.FreeGb);
        Assert.Equal(20.0, arrival.UsedGb);
        Assert.True(arrival.IsRemovable);

        Assert.Null(UsbDriveMonitorService.BuildArrivalInfo(@"F:\", DriveType.Removable, isReady: false, "Empty", 1024, 512));
        Assert.Null(UsbDriveMonitorService.BuildArrivalInfo(@"Z:\", DriveType.Network, isReady: true, "Net", 1024, 512));
    }

    [Fact]
    public async Task FileShelfService_CompressAndStripExifCopyAsync_CreatesCleanJpegAndPreservesOriginal()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"DinamikAda_ExifTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string sourceJpg = Path.Combine(tempDir, "sample_exif.jpg");
            await CreateTestJpegWithCameraModelAsync(sourceJpg, "DinamikTestCam X1");

            // Sanity-check source EXIF metadata exists before cleaning.
            await using (var verifySrcFs = new FileStream(sourceJpg, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var verifySrcRas = verifySrcFs.AsRandomAccessStream())
            {
                var srcDecoder = await BitmapDecoder.CreateAsync(verifySrcRas);
                var srcProps = await srcDecoder.BitmapProperties.GetPropertiesAsync(["/app1/ifd/{ushort=272}"]);
                Assert.True(srcProps.ContainsKey("/app1/ifd/{ushort=272}"));
            }

            string shelfDir = Path.Combine(tempDir, "shelf");
            var shelf = new FileShelfService(shelfDir);

            var unsupported = await shelf.CompressAndStripExifCopyAsync(Path.Combine(tempDir, "notes.txt"));
            Assert.False(unsupported.Succeeded);

            var cleanResult = await shelf.CompressAndStripExifCopyAsync(sourceJpg);
            Assert.True(cleanResult.Succeeded, cleanResult.ErrorMessage);
            Assert.NotNull(cleanResult.CleanFilePath);
            Assert.True(File.Exists(sourceJpg));
            Assert.True(File.Exists(cleanResult.CleanFilePath));
            Assert.Contains("_clean", Path.GetFileName(cleanResult.CleanFilePath!));

            await using (var verifyCleanFs = new FileStream(cleanResult.CleanFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var verifyCleanRas = verifyCleanFs.AsRandomAccessStream())
            {
                var cleanDecoder = await BitmapDecoder.CreateAsync(verifyCleanRas);
                Assert.Equal(32u, cleanDecoder.PixelWidth);
                Assert.Equal(32u, cleanDecoder.PixelHeight);

                var cleanProps = await cleanDecoder.BitmapProperties.GetPropertiesAsync(["/app1/ifd/{ushort=272}"]);
                Assert.False(cleanProps.ContainsKey("/app1/ifd/{ushort=272}"));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeveloperBridgeService_HttpAndNamedPipeEndpoints_DeliverEventsAndEnforceBodyLimit()
    {
        string pipeName = $"DinamikAdaTestPipe_{Guid.NewGuid():N}";
        using var bridge = new DeveloperBridgeService(pipeName);

        var received = new List<DevBridgeNotification>();
        var firstHttpTcs = new TaskCompletionSource<DevBridgeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeTcs = new TaskCompletionSource<DevBridgeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);

        bridge.NotificationReceived += note =>
        {
            lock (received)
            {
                received.Add(note);
            }

            if (note.Title == "HTTP Build")
            {
                firstHttpTcs.TrySetResult(note);
            }
            else if (note.Title == "Pipe Build")
            {
                pipeTcs.TrySetResult(note);
            }
        };

        bridge.Start(enabled: true, port: 0);
        Assert.True(bridge.IsListening);
        Assert.InRange(bridge.ListeningPort, 1, 65535);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bridge.ListeningPort}") };

        using (var healthResp = await http.GetAsync("/health"))
        {
            Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);
            string body = await healthResp.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\":true", body);
        }

        const string notifyJson = """{"title":"HTTP Build","subtitle":"Release x64 OK","status":"ok","progress01":1.0,"sound":false}""";
        using (var notifyResp = await http.PostAsync("/notify", new StringContent(notifyJson, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, notifyResp.StatusCode);
        }

        var httpNote = await firstHttpTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Release x64 OK", httpNote.Subtitle);
        Assert.Equal("ok", httpNote.Status);
        Assert.Equal(1.0, httpNote.Progress01);

        string oversizedJson = $$"""{"title":"TooBig","subtitle":"{{new string('A', DeveloperBridgeService.MaxPayloadBytes + 512)}}"}""";
        using (var oversizedResp = await http.PostAsync("/notify", new StringContent(oversizedJson, Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResp.StatusCode);
        }

        await using (var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await pipeClient.ConnectAsync(3000);
            byte[] lineBytes = Encoding.UTF8.GetBytes("""{"title":"Pipe Build","subtitle":"0 warnings","status":"ok"}""" + "\n");
            await pipeClient.WriteAsync(lineBytes);
            await pipeClient.FlushAsync();
        }

        var pipeNote = await pipeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("0 warnings", pipeNote.Subtitle);
        Assert.Equal("ok", pipeNote.Status);
    }

    [Fact]
    public void WindowControlMath_ComputesSnapPresetsAndCrossMonitorClamping()
    {
        var leftMonitor = new MonitorDescriptor(
            Handle: 1,
            DeviceName: @"\\.\DISPLAY1",
            FriendlyName: "Primary 1080p",
            IsPrimary: true,
            LeftPx: 0,
            TopPx: 0,
            WidthPx: 1920,
            HeightPx: 1080,
            DpiScale: 1.0,
            RefreshRateHz: 165,
            WorkLeftPx: 0,
            WorkTopPx: 0,
            WorkWidthPx: 1920,
            WorkHeightPx: 1032);

        var rightMonitor = new MonitorDescriptor(
            Handle: 2,
            DeviceName: @"\\.\DISPLAY2",
            FriendlyName: "Secondary Compact",
            IsPrimary: false,
            LeftPx: 1920,
            TopPx: 0,
            WidthPx: 1280,
            HeightPx: 720,
            DpiScale: 1.0,
            RefreshRateHz: 60,
            WorkLeftPx: 1920,
            WorkTopPx: 40,
            WorkWidthPx: 1280,
            WorkHeightPx: 680);

        var leftHalf = WindowControlMath.ComputeSnapBounds(leftMonitor, WindowSnapPreset.LeftHalf);
        Assert.Equal(new WindowSnapBounds(0, 0, 960, 1032), leftHalf);

        var rightHalf = WindowControlMath.ComputeSnapBounds(leftMonitor, WindowSnapPreset.RightHalf);
        Assert.Equal(new WindowSnapBounds(960, 0, 960, 1032), rightHalf);

        var center70 = WindowControlMath.ComputeSnapBounds(leftMonitor, WindowSnapPreset.CenterFocus70);
        Assert.Equal(1344, center70.WidthPx);
        Assert.Equal((1920 - 1344) / 2, center70.LeftPx);

        var nextMon = WindowControlMath.SelectTargetMonitor([leftMonitor, rightMonitor], 100, 100, 800, 600, WindowSnapPreset.MoveToNextMonitor);
        Assert.Equal(rightMonitor.Handle, nextMon.Handle);

        var movedClamped = WindowControlMath.ComputeSnapBounds(nextMon, WindowSnapPreset.MoveToNextMonitor, 1600, 900);
        Assert.Equal(1280, movedClamped.WidthPx);
        Assert.Equal(680, movedClamped.HeightPx);
        Assert.Equal(1920, movedClamped.LeftPx);
        Assert.Equal(40, movedClamped.TopPx);
    }

    [Fact]
    public void FocusTimerService_ScheduledPowerAction_WarnsSnoozesExecutesAndCancels()
    {
        var fakeTime = new FakeTimeProvider();
        var bus = new TestHudBus();
        using var timers = new FocusTimerService(bus, fakeTime, "tr");

        int warnings = 0;
        var dueKinds = new List<ScheduledPowerActionKind>();
        timers.ScheduledPowerWarningReached += (_, _) => warnings++;
        timers.ScheduledPowerDue += (_, kind) => dueKinds.Add(kind);

        timers.StartScheduledPowerAction(ScheduledPowerActionKind.Sleep, TimeSpan.FromMinutes(2));
        Assert.True(timers.ScheduledPowerState.IsActive);
        Assert.False(timers.ScheduledPowerState.WarningTriggered);

        fakeTime.Advance(TimeSpan.FromSeconds(30));
        timers.Tick();
        Assert.Equal(0, warnings);
        Assert.Equal(TimeSpan.FromSeconds(90), timers.ScheduledPowerState.Remaining);

        fakeTime.Advance(TimeSpan.FromSeconds(35)); // Remaining = 55s (<= 60s)
        timers.Tick();
        Assert.Equal(1, warnings);
        Assert.True(timers.ScheduledPowerState.WarningTriggered);

        timers.SnoozeScheduledPowerAction(TimeSpan.FromMinutes(10));
        Assert.False(timers.ScheduledPowerState.WarningTriggered);
        Assert.Equal(TimeSpan.FromSeconds(655), timers.ScheduledPowerState.Remaining);

        fakeTime.Advance(TimeSpan.FromSeconds(660));
        timers.Tick();
        Assert.Single(dueKinds, ScheduledPowerActionKind.Sleep);
        Assert.False(timers.ScheduledPowerState.IsActive);

        timers.StartScheduledPowerAction(ScheduledPowerActionKind.Lock, TimeSpan.FromMinutes(5));
        timers.CancelScheduledPowerAction();
        fakeTime.Advance(TimeSpan.FromMinutes(10));
        timers.Tick();
        Assert.Single(dueKinds);
    }

    private static async Task CreateTestJpegWithCameraModelAsync(string path, string model)
    {
        byte[] bgra = new byte[32 * 32 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 240;     // B
            bgra[i + 1] = 140; // G
            bgra[i + 2] = 40;  // R
            bgra[i + 3] = 255; // A
        }

        using var ras = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            32,
            32,
            96,
            96,
            bgra);

        await encoder.BitmapProperties.SetPropertiesAsync(new Dictionary<string, BitmapTypedValue>
        {
            ["/app1/ifd/{ushort=272}"] = new BitmapTypedValue(model, PropertyType.String)
        });

        await encoder.FlushAsync();
        ras.Seek(0);

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var src = ras.AsStreamForRead();
        await src.CopyToAsync(fs);
    }

    private sealed class TestHudBus : ITransientHudBus
    {
        public event EventHandler<TransientHudEvent>? HudRequested;

        public void Publish(TransientHudEvent hudEvent)
        {
            HudRequested?.Invoke(this, hudEvent);
        }
    }
}
