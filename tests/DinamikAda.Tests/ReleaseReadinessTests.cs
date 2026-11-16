using System.Collections.Immutable;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Modules.Media;
using DinamikAda.Modules.SystemControl;
using DinamikAda.Modules.Telemetry;
using DinamikAda.Platform.Win32.Hotkeys;
using Moq;
using Xunit;

namespace DinamikAda.Tests;

public sealed class ReleaseReadinessTests
{
    private sealed class NoopBrightnessDriver : IBrightnessHardwareDriver
    {
        public Task<bool> WriteInternalBrightnessAsync(int percent, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> WriteDdcBrightnessAsync(nint monitorHandle, int percent, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public void Strings_TabNow_IsRenamedToAkisAndFeed()
    {
        Assert.Equal("Akış", Strings.TabNow("tr"));
        Assert.Equal("Feed", Strings.TabNow("en"));
        Assert.Equal("Akış", Strings.Get("tr", nameof(Strings.TabNow)));
        Assert.Equal("Feed", Strings.Get("en", nameof(Strings.TabNow)));
    }

    [Fact]
    public async Task BrightnessService_ClampsSoftwareDimmerBetweenZeroAndSeventyFive()
    {
        var monitors = new Mock<IMonitorLayoutService>();
        monitors.Setup(m => m.GetMonitors()).Returns([]);
        using var svc = new BrightnessService(monitors.Object, new NoopBrightnessDriver());
        var reported = new List<int>();
        svc.SoftwareDimmerChanged += (_, e) => reported.Add(e.DimPercent);

        svc.SetSoftwareDimmer("disp-main", -25);
        svc.SetSoftwareDimmer("disp-main", 0);
        svc.SetSoftwareDimmer("disp-main", 42);
        svc.SetSoftwareDimmer("disp-main", 75);
        svc.SetSoftwareDimmer("disp-main", 100);

        Assert.Equal([0, 0, 42, 75, 75], reported);
        await svc.DisposeAsync();
    }

    [Fact]
    public void CoreAudioHost_ShouldIgnoreEventContext_FiltersOnlyMatchingNonEmptySelfGuid()
    {
        Guid self = Guid.NewGuid();
        Guid other = Guid.NewGuid();

        Assert.True(CoreAudioHost.ShouldIgnoreEventContext(self, self));
        Assert.False(CoreAudioHost.ShouldIgnoreEventContext(other, self));
        Assert.False(CoreAudioHost.ShouldIgnoreEventContext(Guid.Empty, self));
        Assert.False(CoreAudioHost.ShouldIgnoreEventContext(Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void CoreAudioHost_SelectHighestQualityFormat_PrioritizesRateDepthAndFloat()
    {
        Assert.Null(CoreAudioHost.SelectHighestQualityFormat(null));
        Assert.Null(CoreAudioHost.SelectHighestQualityFormat([]));

        var options = new[]
        {
            new AudioFormatOption(44100, 16, false, 2, "44.1 kHz · 16-bit", false),
            new AudioFormatOption(48000, 24, false, 2, "48 kHz · 24-bit", true),
            new AudioFormatOption(192000, 24, false, 2, "192 kHz · 24-bit", false),
            new AudioFormatOption(192000, 32, true, 2, "192 kHz · 32-bit Float", false)
        };

        var best = CoreAudioHost.SelectHighestQualityFormat(options);
        Assert.NotNull(best);
        Assert.Equal(192000, best!.SampleRateHz);
        Assert.Equal(32, best.BitsPerSample);
        Assert.True(best.IsFloat);
    }

    [Fact]
    public void CoreAudioHost_BuildWaveFormatExtensible_SetsAccurateExtensibleHeaders()
    {
        var pcm24In32 = CoreAudioHost.BuildWaveFormatExtensible(96000, 32, 24, 2, 0, isFloat: false);
        Assert.Equal((ushort)0xFFFE, pcm24In32.Format.wFormatTag);
        Assert.Equal(96000u, pcm24In32.Format.nSamplesPerSec);
        Assert.Equal((ushort)32, pcm24In32.Format.wBitsPerSample);
        Assert.Equal((ushort)24, pcm24In32.Samples);
        Assert.Equal((ushort)8, pcm24In32.Format.nBlockAlign);
        Assert.Equal(96000u * 8u, pcm24In32.Format.nAvgBytesPerSec);
        Assert.Equal(0x3u, pcm24In32.dwChannelMask);

        var float32 = CoreAudioHost.BuildWaveFormatExtensible(48000, 32, 32, 1, 0, isFloat: true);
        Assert.Equal((ushort)32, float32.Samples);
        Assert.Equal(0x4u, float32.dwChannelMask);
        Assert.NotEqual(pcm24In32.SubFormat, float32.SubFormat);
    }

    [Fact]
    public void ColorQuantizer_PickPaletteFromBgraPixels_ExtractsDistinctRichPalette()
    {
        // Mix vibrant sapphire blue (#0A84FF), purple (#AF52DE), and emerald green (#30D158) blocks
        var pixels = new List<byte>(120 * 4);
        for (int i = 0; i < 40; i++) pixels.AddRange(new byte[] { 255, 132, 10, 255 });  // BGRA -> #0A84FF
        for (int i = 0; i < 35; i++) pixels.AddRange(new byte[] { 222, 82, 175, 255 }); // BGRA -> #AF52DE
        for (int i = 0; i < 25; i++) pixels.AddRange(new byte[] { 88, 209, 48, 255 });  // BGRA -> #30D158

        var (primary, secondary, highlight) = ColorQuantizer.PickPaletteFromBgraPixels(pixels.ToArray());
        Assert.StartsWith("#", primary);
        Assert.StartsWith("#", secondary);
        Assert.StartsWith("#", highlight);
        Assert.Equal(7, primary.Length);
        Assert.Equal(7, secondary.Length);
        Assert.Equal(7, highlight.Length);
        Assert.NotEqual(primary, secondary);
    }

    [Fact]
    public async Task VoiceNoteService_StopRequestFlipsIsRecordingToFalseImmediately()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dinamikada-voicenotes-{Guid.NewGuid():N}");
        using var svc = new VoiceNoteService(tempDir);
        try
        {
            Assert.Equal(tempDir, svc.NotesDirectory);
            Assert.Null(svc.LastSavedWavPath);
            Assert.False(svc.IsRecording);

            bool started = svc.StartRecording();
            Assert.True(started);

            var stopTask = svc.StopRecordingAsync();
            // Even while background WAV finalization completes, IsRecording must be false immediately.
            Assert.False(svc.IsRecording);
            await stopTask;
            Assert.False(svc.IsRecording);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WindowControlMath_FiltersShellAndTrayOverflowAndOwnWindows()
    {
        Assert.False(WindowControlMath.IsEligibleExternalWindowMetadata("TopLevelWindowForOverflowXamlIsland", "Sistem tepsisi taşma penceresi."));
        Assert.False(WindowControlMath.IsEligibleExternalWindowMetadata("NotifyIconOverflowWindow", "Overflow"));
        Assert.False(WindowControlMath.IsEligibleExternalWindowMetadata("Shell_TrayWnd", "Taskbar"));
        Assert.False(WindowControlMath.IsEligibleExternalWindowMetadata("Progman", "Program Manager"));
        Assert.False(WindowControlMath.IsEligibleExternalWindowMetadata("HwndWrapper[DinamikAda.App;;123]", "DinamikAda — Island"));
        Assert.False(WindowControlMath.IsEligibleExternalWindowMetadata("Chrome_WidgetWin_1", "   "));

        Assert.True(WindowControlMath.IsEligibleExternalWindowMetadata("Chrome_WidgetWin_1", "Spotify — Midnight City"));
        Assert.True(WindowControlMath.IsEligibleExternalWindowMetadata("CASCADIA_HOSTING_WINDOW_CLASS", "Windows Terminal"));
    }

    [Fact]
    public void SystemMaintenanceService_ExclusionsFormatBytesAndIsolatedTempCleanupWorkAccurately()
    {
        Assert.Equal("0 B", SystemMaintenanceService.FormatBytes(0));
        Assert.Equal("512 B", SystemMaintenanceService.FormatBytes(512));
        Assert.Contains("KB", SystemMaintenanceService.FormatBytes(4096));
        Assert.Contains("MB", SystemMaintenanceService.FormatBytes(5 * 1024 * 1024));
        Assert.Contains("GB", SystemMaintenanceService.FormatBytes(3L * 1024 * 1024 * 1024));

        // Critical OS, foreground, visible, audio-active, and self PIDs must be excluded.
        Assert.True(SystemMaintenanceService.IsExcludedBackgroundApp(4, "System", 0, false, false, false, false));
        Assert.True(SystemMaintenanceService.IsExcludedBackgroundApp(100, "svchost", 1, false, false, false, false));
        Assert.True(SystemMaintenanceService.IsExcludedBackgroundApp(200, "explorer", 1, false, false, false, false));
        Assert.True(SystemMaintenanceService.IsExcludedBackgroundApp(Environment.ProcessId, "MyApp", 1, false, false, false, false));
        Assert.True(SystemMaintenanceService.IsExcludedBackgroundApp(300, "CustomHelper", 1, hasVisibleWindow: true, false, false, false));
        Assert.True(SystemMaintenanceService.IsExcludedBackgroundApp(301, "CustomHelper", 1, false, isForeground: true, false, false));
        Assert.True(SystemMaintenanceService.IsExcludedBackgroundApp(302, "CustomHelper", 1, false, false, hasActiveAudio: true, false));
        Assert.False(SystemMaintenanceService.IsExcludedBackgroundApp(303, "CustomIdleHelper", 1, false, false, false, false));

        string tempRoot = Path.Combine(Path.GetTempPath(), $"dinamikada-clean-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string oldFile = Path.Combine(tempRoot, "stale.tmp");
            string subDir = Path.Combine(tempRoot, "empty_sub");
            string freshFile = Path.Combine(tempRoot, "active.tmp");
            Directory.CreateDirectory(subDir);

            byte[] payload = new byte[2048];
            File.WriteAllBytes(oldFile, payload);
            File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddMinutes(-10));
            File.SetCreationTimeUtc(oldFile, DateTime.UtcNow.AddMinutes(-10));

            File.WriteAllBytes(freshFile, new byte[512]);
            File.SetLastWriteTimeUtc(freshFile, DateTime.UtcNow);
            File.SetCreationTimeUtc(freshFile, DateTime.UtcNow);

            var svc = new SystemMaintenanceService();
            var result = svc.ExecuteTempCleanup([tempRoot], minFileAge: TimeSpan.FromMinutes(2));

            Assert.Equal(2048L + 512L, result.ScannedBytes);
            Assert.Equal(2048L, result.FreedBytes);
            Assert.Equal(1, result.DeletedFileCount);
            Assert.Equal(1, result.DeletedDirectoryCount);
            Assert.False(File.Exists(oldFile));
            Assert.False(Directory.Exists(subDir));
            Assert.True(File.Exists(freshFile));
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GlobalHotkeyService_GetStartupFallbackChords_ProvidesValidUniqueNonCollidingFallbacks()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int[] ids =
        [
            GlobalHotkeyService.HotkeyId,
            GlobalHotkeyService.MicMuteHotkeyId,
            GlobalHotkeyService.ColorPickerHotkeyId
        ];

        foreach (int id in ids)
        {
            var fallbacks = GlobalHotkeyService.GetStartupFallbackChords(id);
            Assert.NotEmpty(fallbacks);
            foreach (string chord in fallbacks)
            {
                Assert.True(GlobalHotkeyService.TryParseChord(chord, out uint mods, out uint vk));
                Assert.NotEqual(0u, mods);
                Assert.NotEqual(0u, vk);
                Assert.True(seen.Add(chord), $"Fallback chord '{chord}' collided across hotkey IDs.");
            }
        }
    }

    [Fact]
    public void GsmtcMediaService_FormatFallbackAppName_CleansHexHashesAndKnownBrowsers()
    {
        Assert.Equal("Browser", GsmtcMediaService.FormatFallbackAppName("F0DC299D809B9700"));
        Assert.Equal("Browser", GsmtcMediaService.FormatFallbackAppName("F0DC299D809B9700;PrivateBrowsingAUMID"));
        Assert.Equal("Microsoft Edge", GsmtcMediaService.FormatFallbackAppName("MSEdge"));
        Assert.Equal("Google Chrome", GsmtcMediaService.FormatFallbackAppName("Chrome"));
        Assert.Equal("Spotify", GsmtcMediaService.FormatFallbackAppName("Spotify.exe"));
        Assert.Equal("youtube-music", GsmtcMediaService.FormatFallbackAppName("com.github.th-ch.youtube-music"));
    }
}
