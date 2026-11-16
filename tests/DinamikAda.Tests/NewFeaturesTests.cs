using System.Net.Http;
using System.Text;
using DinamikAda.Core.Abstractions;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Modules.Media;
using DinamikAda.Modules.Productivity;
using DinamikAda.Modules.SystemControl;
using Xunit;

namespace DinamikAda.Tests;

public sealed class NewFeaturesTests
{
    // -------------------------------------------------------------------------
    // Feature 1: Bluetooth Battery & Device Classification
    // -------------------------------------------------------------------------

    [Fact]
    public void BluetoothBatteryResolver_ExtractsNumericTypesAndClamps()
    {
        Assert.Equal(84, BluetoothBatteryResolver.TryExtractBatteryPercent((byte)84));
        Assert.Equal(100, BluetoothBatteryResolver.TryExtractBatteryPercent((ushort)140));
        Assert.Equal(12, BluetoothBatteryResolver.TryExtractBatteryPercent(12));
        Assert.Equal(55, BluetoothBatteryResolver.TryExtractBatteryPercent(55u));
        Assert.Null(BluetoothBatteryResolver.TryExtractBatteryPercent(-3));
        Assert.Null(BluetoothBatteryResolver.TryExtractBatteryPercent("invalid"));

        Assert.Equal(67, BluetoothBatteryResolver.ParseGattBatteryLevel(new byte[] { 67 }));
        Assert.Null(BluetoothBatteryResolver.ParseGattBatteryLevel(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void BluetoothBatteryResolver_ClassifiesIconsAndMergesConnectedWithBattery()
    {
        Assert.Equal("\uE7F6", BluetoothBatteryResolver.ClassifyDeviceIconGlyph("AirPods Pro"));
        Assert.Equal("\uE962", BluetoothBatteryResolver.ClassifyDeviceIconGlyph("Logitech MX Master 3S"));
        Assert.Equal("\uE765", BluetoothBatteryResolver.ClassifyDeviceIconGlyph("Keychron K8 Keyboard"));
        Assert.Equal("\uE7FC", BluetoothBatteryResolver.ClassifyDeviceIconGlyph("Xbox Wireless Controller"));
        Assert.Equal("\uE702", BluetoothBatteryResolver.ClassifyDeviceIconGlyph("Generic Device"));

        var merged = BluetoothBatteryResolver.MergeDeviceSnapshots(
        [
            new BluetoothDeviceSnapshot("bt-a", "AirPods Pro", true, null, "out-1"),
            new BluetoothDeviceSnapshot("pnp-a", "AirPods Pro", false, 14, null)
        ]);

        Assert.Single(merged);
        Assert.True(merged[0].IsConnected);
        Assert.Equal(14, merged[0].BatteryPercent);
        Assert.Equal("out-1", merged[0].CoreAudioEndpointId);
    }

    // -------------------------------------------------------------------------
    // Feature 2: QR Encoder + Ephemeral LAN AirDrop Server
    // -------------------------------------------------------------------------

    [Fact]
    public void QrCodeEncoder_GeneratesValidMatrixWithAllThreeFinderPatterns()
    {
        bool[,] matrix = QrCodeEncoder.Encode("http://192.168.1.42:51842/?t=abc123", quietZoneModules: 2);
        int size = matrix.GetLength(0);
        Assert.Equal(size, matrix.GetLength(1));
        Assert.True(size >= 25); // 21 + 4 quiet-zone minimum

        // Outer quiet zone corners must remain dark=false
        Assert.False(matrix[0, 0]);
        Assert.False(matrix[size - 1, 0]);
        Assert.False(matrix[0, size - 1]);

        // Inside top-left finder pattern (offset q=2): outer ring [2..8, 2..8] & 3x3 center [4..6, 4..6] dark
        Assert.True(matrix[2, 2]);
        Assert.True(matrix[5, 5]);
        Assert.False(matrix[3, 3]);
    }

    [Fact]
    public async Task LanDropServer_ServesIndexDownloadsAndAcceptsMultipartUpload()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"dinamikada-landrop-{Guid.NewGuid():N}");
        string inboxDir = Path.Combine(tempRoot, "inbox");
        string sampleFile = Path.Combine(tempRoot, "notes.txt");
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllTextAsync(sampleFile, "Merhaba DinamikAda AirDrop");

        try
        {
            using var server = new LanDropServer(inboxDir);
            var info = server.StartShare([sampleFile], "Clipboard sample secret");
            Assert.True(info.HasSharedText);
            Assert.Equal(1, info.FileCount);

            var receivedBatchTcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.FilesReceived += (_, files) => receivedBatchTcs.TrySetResult(files);

            using var http = new HttpClient();
            string html = await http.GetStringAsync(info.LoopbackUrl);
            Assert.Contains("notes.txt", html);
            Assert.Contains("Clipboard sample secret", html);

            string dlUrl = $"{info.LoopbackUrl}/dl/0";
            string downloaded = await http.GetStringAsync(dlUrl);
            Assert.Equal("Merhaba DinamikAda AirDrop", downloaded);

            using var multipart = new MultipartFormDataContent();
            var uploadContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Phone -> PC payload"));
            multipart.Add(uploadContent, "file", "from-phone.txt");
            string uploadUrl = $"{info.LoopbackUrl}/upload";
            var resp = await http.PostAsync(uploadUrl, multipart);
            Assert.True(resp.IsSuccessStatusCode);

            IReadOnlyList<string> saved = await receivedBatchTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(saved);
            Assert.Equal("Phone -> PC payload", await File.ReadAllTextAsync(saved[0]));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Feature 3: Region OCR Formatting & Clipboard Text Recording
    // -------------------------------------------------------------------------

    [Fact]
    public void ScreenOcrAndClipboardVault_FormatsLinesAndRecordsText()
    {
        string formatted = ScreenOcrService.FormatOcrLines(["  First line  ", "", "   ", "Second line"]);
        Assert.Equal($"First line{Environment.NewLine}Second line", formatted);

        string tempVault = Path.Combine(Path.GetTempPath(), $"dinamikada-clip-{Guid.NewGuid():N}", "vault.json");
        Directory.CreateDirectory(Path.GetDirectoryName(tempVault)!);
        try
        {
            using var vault = new ClipboardVaultService(vaultPath: tempVault, maxItems: 10);
            vault.AddTextEntry("Extracted OCR Receipt #402");
            var entries = vault.GetEntries();
            Assert.Single(entries);
            Assert.Contains("Extracted OCR Receipt #402", entries[0].FullText);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(tempVault)!, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Feature 4: ICS Calendar Parser & Meeting Countdown
    // -------------------------------------------------------------------------

    [Fact]
    public void IcsCalendarParser_ParsesUtcZAndJoinUrlFromFoldedDescription()
    {
        DateTimeOffset now = new(2026, 11, 15, 14, 00, 00, TimeSpan.Zero);
        string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            SUMMARY:Architecture Sync
            DTSTART:20261115T140415Z
            DTEND:20261115T143415Z
            DESCRIPTION:Join here: https://meet.google.com/abc-defg-
             hij please test audio
            END:VEVENT
            END:VCALENDAR
            """;

        var list = IcsCalendarParser.ParseUpcomingFromIcs(ics, now, TimeSpan.FromHours(2));
        Assert.Single(list);
        Assert.Equal("Architecture Sync", list[0].Summary);
        Assert.Equal("https://meet.google.com/abc-defg-hij", list[0].JoinUrl);
        Assert.Equal(now.AddMinutes(4).AddSeconds(15), list[0].StartUtc);
    }

    [Fact]
    public async Task MeetingCalendarService_EmitsCountdownSatelliteInside10MinWindow()
    {
        string icsPath = Path.Combine(Path.GetTempPath(), $"dinamikada-cal-{Guid.NewGuid():N}.ics");
        DateTimeOffset startUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        string ics = $"""
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            SUMMARY:Sprint Review
            DTSTART:{startUtc:yyyyMMdd'T'HHmmss'Z'}
            DTEND:{startUtc.AddMinutes(30):yyyyMMdd'T'HHmmss'Z'}
            LOCATION:https://zoom.us/j/123456789
            END:VEVENT
            END:VCALENDAR
            """;
        await File.WriteAllTextAsync(icsPath, ics);

        try
        {
            using var cal = new MeetingCalendarService();
            cal.Language = "en";
            cal.Configure(enabled: true, sources: icsPath);
            await cal.RefreshAndEvaluateAsync();

            Assert.NotNull(cal.ActiveOrNextMeeting);
            Assert.NotNull(cal.CurrentActivity);
            Assert.Equal(ActivityKind.Meeting, cal.CurrentActivity!.Kind);
            Assert.StartsWith("Meeting •", cal.CurrentActivity.PrimaryText);
            Assert.Equal("https://zoom.us/j/123456789", cal.ActiveOrNextMeeting!.JoinUrl);
        }
        finally
        {
            try { File.Delete(icsPath); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Feature 5: Downloads / Transfer Watcher Helpers
    // -------------------------------------------------------------------------

    [Fact]
    public void TransferMonitorService_DetectsPartialExtensionsAndFormatsRate()
    {
        Assert.True(TransferMonitorService.IsPartialTransferFile(@"C:\Downloads\setup.exe.crdownload"));
        Assert.True(TransferMonitorService.IsPartialTransferFile(@"C:\Downloads\ubuntu.iso.part"));
        Assert.True(TransferMonitorService.IsPartialTransferFile(@"C:\Downloads\archive.tmp"));
        Assert.False(TransferMonitorService.IsPartialTransferFile(@"C:\Downloads\final.zip"));

        Assert.Equal(@"C:\Downloads\setup.exe", TransferMonitorService.InferCompletedPath(@"C:\Downloads\setup.exe.crdownload"));
        Assert.Equal("12.5 MB/s", TransferMonitorService.FormatTransferRate(12.5 * 1024 * 1024));
        Assert.Equal("512 KB/s", TransferMonitorService.FormatTransferRate(512 * 1024));
    }

    // -------------------------------------------------------------------------
    // Feature 6: Synced LRC Lyrics & Audio Mix Format Badge
    // -------------------------------------------------------------------------

    [Fact]
    public void LrcParserAndLyricsService_ResolvesSyncedLinesAndMixFormatBadge()
    {
        string lrcDir = Path.Combine(Path.GetTempPath(), $"dinamikada-lrc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(lrcDir);
        string lrcPath = Path.Combine(lrcDir, "M83 - Midnight City.lrc");
        File.WriteAllText(lrcPath, """
            [00:12.50]Waiting in a car
            [00:16.00]Waiting for a ride in the dark
            [00:20.25]The night city grows
            """);

        try
        {
            var svc = new LyricsService();
            svc.Configure(enabled: true, customFolders: lrcDir);

            Assert.Null(svc.ResolveLine("Midnight City", "M83", TimeSpan.FromSeconds(5)));
            Assert.Equal("Waiting in a car", svc.ResolveLine("Midnight City (Remastered)", "M83", TimeSpan.FromSeconds(14)));
            Assert.Equal("Waiting for a ride in the dark", svc.ResolveLine("Midnight City", "M83", TimeSpan.FromSeconds(18)));
            Assert.Equal("The night city grows", svc.ResolveLine("Midnight City", "M83", TimeSpan.FromSeconds(25)));
        }
        finally
        {
            try { Directory.Delete(lrcDir, recursive: true); } catch { }
        }

        Assert.Equal("48 kHz • 24-bit", AudioFormatFormatter.FormatMixFormat(48000, 32, 24, isFloat: true));
        Assert.Equal("44.1 kHz • 16-bit", AudioFormatFormatter.FormatMixFormat(44100, 16, 16, isFloat: false));
    }

    // -------------------------------------------------------------------------
    // Feature 7: External Window Pin & Opacity Math
    // -------------------------------------------------------------------------

    [Fact]
    public void WindowControlMath_ConvertsOpacitySafelyAndFiltersSelfWindow()
    {
        Assert.Equal((byte)255, WindowControlMath.OpacityPercentToByte(100));
        Assert.Equal((byte)77, WindowControlMath.OpacityPercentToByte(30)); // clamped min 30%
        Assert.Equal((byte)77, WindowControlMath.OpacityPercentToByte(5));  // still clamped to 30% -> 77
        Assert.Equal(100, WindowControlMath.ByteToOpacityPercent(255));
        Assert.Equal(50, WindowControlMath.ByteToOpacityPercent(128));

        Assert.False(WindowControlMath.IsExternalTargetWindow(0, 1000, 2000));
        Assert.False(WindowControlMath.IsExternalTargetWindow(2000, 1000, 2000)); // matches island HWND
        Assert.False(WindowControlMath.IsExternalHandleAndPid(2000, 5000, 1000, 2000)); // matches island HWND
        Assert.False(WindowControlMath.IsExternalHandleAndPid(3000, 1000, 1000, 2000)); // matches own PID
        Assert.True(WindowControlMath.IsExternalHandleAndPid(3000, 5000, 1000, 2000));
    }

    // -------------------------------------------------------------------------
    // Feature 8: Voice Note WAV Helper & RMS
    // -------------------------------------------------------------------------

    [Fact]
    public void VoiceNoteWavHelper_WritesValidRiffHeaderAndConvertsFloatStereoToPcm16()
    {
        byte[] header = VoiceNoteWavHelper.BuildRiffHeader(sampleRateHz: 16000, channels: 1, bitsPerSample: 16, dataBytes: 320);
        Assert.Equal(44, header.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(header, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(header, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(header, 36, 4));
        Assert.Equal(356, BitConverter.ToInt32(header, 4)); // 36 + 320

        // 2 frames stereo IEEE float32: L=0.5, R=-0.5 -> mono avg 0.0; second frame L=0.5, R=0.5 -> 0.5 -> ~16383
        byte[] floatInput = new byte[16];
        BitConverter.GetBytes(0.5f).CopyTo(floatInput, 0);
        BitConverter.GetBytes(-0.5f).CopyTo(floatInput, 4);
        BitConverter.GetBytes(0.5f).CopyTo(floatInput, 8);
        BitConverter.GetBytes(0.5f).CopyTo(floatInput, 12);

        byte[] pcm16 = VoiceNoteWavHelper.ConvertSamplesToPcm16(
            floatInput,
            channels: 2,
            bitsPerSample: 32,
            isFloat: true);

        Assert.Equal(4, pcm16.Length);
        short s0 = BitConverter.ToInt16(pcm16, 0);
        short s1 = BitConverter.ToInt16(pcm16, 2);
        Assert.Equal(0, s0);
        Assert.InRange(s1, 16300, 16450);

        double loudRms = VoiceNoteWavHelper.ComputeNormalizedRms(pcm16);
        Assert.InRange(loudRms, 0.90, 1.0);

        byte[] quietPcm16 = new byte[4];
        BitConverter.GetBytes((short)3277).CopyTo(quietPcm16, 2); // ~0.10 amplitude -> ~0.0707 RMS * 3.2 ~= 0.226
        double quietRms = VoiceNoteWavHelper.ComputeNormalizedRms(quietPcm16);
        Assert.InRange(quietRms, 0.15, 0.35);
    }
}
