using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using DinamikAda.Core.Config;
using DinamikAda.Core.MathAndSearch;
using DinamikAda.Modules.Media;
using DinamikAda.Modules.Productivity;
using DinamikAda.Modules.SystemControl;
using Moq;
using Xunit;

namespace DinamikAda.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dinamikada-audit-{Guid.NewGuid():N}");

    public AuditRegressionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Calculator_RejectsOverflowAndExcessiveNesting()
    {
        Assert.False(QuickExpressionEvaluator.TryEvaluate("1e999 km to m", out _, out _));
        Assert.False(QuickExpressionEvaluator.TryEvaluate("1e308 km to mm", out _, out _));
        Assert.False(QuickExpressionEvaluator.TryEvaluate(new string('(', 1000) + "1" + new string(')', 1000), out _, out _));
        Assert.False(QuickExpressionEvaluator.TryEvaluate(new string('-', 1000) + "1", out _, out _));
    }

    [Fact]
    public void Calculator_UsesMathematicalPowerPrecedenceAndAllowsSmallNonzeroDivisors()
    {
        Assert.True(QuickExpressionEvaluator.TryEvaluate("-2^2", out _, out var negativePower));
        Assert.Equal("-4", negativePower);
        Assert.True(QuickExpressionEvaluator.TryEvaluate("2^-2", out _, out var negativeExponent));
        Assert.Equal("0.25", negativeExponent);
        Assert.True(QuickExpressionEvaluator.TryEvaluate("2^3^2", out _, out var rightAssociative));
        Assert.Equal("512", rightAssociative);
        Assert.True(QuickExpressionEvaluator.TryEvaluate("1e-16 / 1e-16", out _, out var smallDivisor));
        Assert.Equal("1", smallDivisor);
    }

    [Fact]
    public void ConfigStore_RecognizesCaseInsensitivePropertyNames()
    {
        string path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{"language":"en","hoverdwellms":350,"commandhotkey":"Alt+Space"}""");
        var config = new AppConfigStore(path).Load();
        Assert.Equal("en", config.Language);
        Assert.Equal(350, config.HoverDwellMs);
        Assert.Equal("Alt+Space", config.CommandHotkey);
    }

    [Fact]
    public void QrCode_PreservesEveryFinderBorderAndNeverTruncatesInput()
    {
        foreach (string text in new[] { "HELLO", new string('x', 60), new string('x', 250) })
        {
            bool[,] matrix = QrCodeEncoder.Encode(text, quietZoneModules: 0);
            int size = matrix.GetLength(0);
            foreach (var (left, top) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })
            {
                for (int y = 0; y < 7; y++)
                    for (int x = 0; x < 7; x++)
                    {
                        bool expected = x == 0 || x == 6 || y == 0 || y == 6 || (x is >= 2 and <= 4 && y is >= 2 and <= 4);
                        Assert.Equal(expected, matrix[top + y, left + x]);
                    }
            }
        }
        Assert.Throws<ArgumentException>(() => QrCodeEncoder.Encode(new string('x', 1000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => QrCodeEncoder.Encode("x", int.MaxValue));
    }

    [Fact]
    public async Task BrightnessWriter_PreservesLatestRequestForEveryMonitor()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new System.Collections.Concurrent.ConcurrentDictionary<nint, int>();
        var driver = new Mock<IBrightnessHardwareDriver>();
        driver.Setup(d => d.WriteDdcBrightnessAsync(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<nint, int, CancellationToken>(async (handle, percent, ct) =>
            {
                if (percent == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
                written[handle] = percent;
                if (written.GetValueOrDefault(1) == 70 && written.GetValueOrDefault(2) == 80) complete.TrySetResult();
                return true;
            });
        await using var writer = new CoalescedBrightnessWriter(driver.Object, id => id == "a" ? 1 : 2, TimeSpan.Zero);
        writer.Enqueue(new BrightnessWriteRequest("a", 1, false));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        writer.Enqueue(new BrightnessWriteRequest("a", 70, false));
        writer.Enqueue(new BrightnessWriteRequest("b", 80, false));
        release.TrySetResult();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(70, written[1]);
        Assert.Equal(80, written[2]);
    }

    [Fact]
    public async Task BrightnessWriter_RecoversFromDriverFailureAndDisposesTwice()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = new Mock<IBrightnessHardwareDriver>();
        driver.Setup(d => d.WriteInternalBrightnessAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((value, _) =>
            {
                if (value == 1) { failed.TrySetResult(); throw new IOException("Monitor disconnected"); }
                recovered.TrySetResult();
                return Task.FromResult(true);
            });
        var writer = new CoalescedBrightnessWriter(driver.Object, _ => 0, TimeSpan.Zero);
        try
        {
            writer.Enqueue(new BrightnessWriteRequest("a", 1, true));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            writer.Enqueue(new BrightnessWriteRequest("a", 75, true));
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await writer.DisposeAsync(); await writer.DisposeAsync(); }
    }

    [Fact]
    public async Task LanDrop_PreservesBinaryBoundaryLikeContentAndEmptyFiles()
    {
        using var server = new LanDropServer(_root);
        var share = server.StartShare([]);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var multipart = new MultipartFormDataContent("test-boundary");
        byte[] payload = Encoding.UTF8.GetBytes("binary\0prefix--test-boundary\r\n--test-boundaryX\r\nend");
        multipart.Add(new ByteArrayContent(payload), "file", "payload.bin");
        multipart.Add(new ByteArrayContent([]), "file", "empty.txt");
        using var response = await http.PostAsync(share.LoopbackUrl + "/upload", multipart);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(_root, "payload.bin")));
        Assert.Equal(0, new FileInfo(Path.Combine(_root, "empty.txt")).Length);
    }

    [Fact]
    public async Task LanDrop_ConcurrentUploadsNeverOverwriteTheSameFilename()
    {
        using var server = new LanDropServer(_root);
        var share = server.StartShare([]);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            using var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent("payload-" + i), "file", "same.txt");
            using var response = await http.PostAsync(share.LoopbackUrl + "/upload", multipart);
            Assert.True(response.IsSuccessStatusCode);
        }));
        var files = Directory.GetFiles(_root, "*.txt");
        Assert.Equal(3, files.Length);
        Assert.Equal(new[] { "payload-0", "payload-1", "payload-2" }, files.Select(File.ReadAllText).Order().ToArray());
    }

    [Fact]
    public async Task LanDrop_RejectsUnauthorizedRequestsBeforeReadingTheirBody()
    {
        using var server = new LanDropServer(_root);
        var uri = new Uri(server.StartShare([]).LoopbackUrl);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /s/wrong/upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1024\r\n\r\n"));
        using var response = new MemoryStream();
        await stream.CopyToAsync(response).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("404 Not Found", Encoding.ASCII.GetString(response.ToArray()));
    }

    [Fact]
    public async Task LanDrop_RejectsInvalidFramingAndIncompleteMultipartWithoutSavingFiles()
    {
        using var server = new LanDropServer(_root);
        var uri = new Uri(server.StartShare([]).LoopbackUrl);
        Assert.Contains("400 Bad Request", await SendRawAsync(uri, "Content-Length: -1\r\n", ""));
        Assert.Contains("413 Payload Too Large", await SendRawAsync(uri, "Content-Length: 134217729\r\n", ""));
        Assert.Contains("400 Bad Request", await SendRawAsync(uri, "Transfer-Encoding: chunked\r\n", "xyz\r\n"));
        Assert.Contains("400 Bad Request", await SendRawAsync(uri, "Content-Length: 100\r\n", "short"));
        string incomplete = "--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"broken.txt\"\r\n\r\npartial";
        Assert.Contains("400 Bad Request", await SendRawAsync(uri, $"Content-Type: multipart/form-data; boundary=b\r\nContent-Length: {Encoding.UTF8.GetByteCount(incomplete)}\r\n", incomplete));
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task LanDrop_AcceptsChunkedUploadsWithTrailers()
    {
        using var server = new LanDropServer(_root);
        var uri = new Uri(server.StartShare([]).LoopbackUrl);
        string body = "--b\r\nContent-Disposition: form-data; name=\"file\"; filename=\"chunk.txt\"\r\n\r\nhello\r\n--b--\r\n";
        string chunked = $"{Encoding.UTF8.GetByteCount(body):X}\r\n{body}\r\n0\r\nX-Test: done\r\n\r\n";
        Assert.Contains("200 OK", await SendRawAsync(uri, "Content-Type: multipart/form-data; boundary=b\r\nTransfer-Encoding: chunked\r\n", chunked));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_root, "chunk.txt")));
    }

    [Fact]
    public void LanDrop_ValidatesLifetimeAndCannotRestartAfterDisposal()
    {
        var server = new LanDropServer(_root);
        Assert.Throws<ArgumentOutOfRangeException>(() => server.StartShare([], ttl: TimeSpan.Zero));
        server.Dispose();
        Assert.Throws<ObjectDisposedException>(() => server.StartShare([]));
    }

    [Fact]
    public async Task Shelf_ZipsItsOwnFolderWithoutIncludingTheArchiveItIsWriting()
    {
        string shelfDir = Path.Combine(_root, "shelf");
        Directory.CreateDirectory(shelfDir);
        await File.WriteAllTextAsync(Path.Combine(shelfDir, "notes.txt"), "hello");
        var shelf = new FileShelfService(shelfDir);
        shelf.AddPaths([shelfDir]);
        string? zip = await shelf.CreateZipArchiveAsync();
        Assert.NotNull(zip);
        using var archive = ZipFile.OpenRead(zip!);
        Assert.Contains(archive.Entries, entry => entry.FullName == "shelf/notes.txt");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Shelf_KeepsDistinctFoldersAndEmptyDirectoriesInArchives()
    {
        string first = Path.Combine(_root, "a", "same");
        string second = Path.Combine(_root, "b", "same");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(first, "data.txt"), "first");
        await File.WriteAllTextAsync(Path.Combine(second, "data.txt"), "second");
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var shelf = new FileShelfService(Path.Combine(_root, "vault"));
        shelf.AddPaths([first, second, empty]);
        string? zip = await shelf.CreateZipArchiveAsync();
        using var archive = ZipFile.OpenRead(zip!);
        Assert.Contains(archive.Entries, e => e.FullName == "empty/");
        Assert.Equal(2, archive.Entries.Count(e => e.FullName.EndsWith("/data.txt", StringComparison.Ordinal)));
        Assert.DoesNotContain(archive.Entries, e => e.FullName == "data_2.txt");
        Assert.Contains(archive.Entries, e => e.FullName.StartsWith("same_2/", StringComparison.Ordinal));
    }

    [Fact]
    public void Shelf_SkipsInvalidPathsAndStillAddsValidFiles()
    {
        string path = Path.Combine(_root, "valid.txt");
        File.WriteAllText(path, "ok");
        var shelf = new FileShelfService(Path.Combine(_root, "shelf"));
        Assert.Equal(1, shelf.AddPaths(["bad\0path", path]));
        Assert.Single(shelf.GetItems());
    }

    [Fact]
    public void Calendar_IgnoresCancelledEventsAndNestedAlarmProperties()
    {
        string content = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            SUMMARY:Cancelled
            DTSTART:20261115T140000Z
            STATUS:CANCELLED
            END:VEVENT
            BEGIN:VEVENT
            SUMMARY:Actual meeting
            DTSTART:20261115T140500Z
            DURATION:PT1H
            BEGIN:VALARM
            SUMMARY:Alarm summary
            DESCRIPTION:Alarm text
            END:VALARM
            END:VEVENT
            END:VCALENDAR
            """;
        DateTimeOffset now = new(2026, 11, 15, 14, 0, 0, TimeSpan.Zero);
        var meetings = IcsCalendarParser.ParseUpcomingFromIcs(content, now, TimeSpan.FromHours(2));
        Assert.Single(meetings);
        Assert.Equal("Actual meeting", meetings[0].Summary);
        Assert.Equal(TimeSpan.FromHours(1), meetings[0].EndUtc - meetings[0].StartUtc);
    }

    [Fact]
    public void Calendar_RespectsWeeklyWeekdaysCountsAndExceptions()
    {
        string content = """
            BEGIN:VEVENT
            SUMMARY:Recurring
            DTSTART:20261109T090000Z
            RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=5
            EXDATE:20261111T090000Z
            END:VEVENT
            """;
        DateTimeOffset now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var meetings = IcsCalendarParser.ParseUpcomingFromIcs(content, now, TimeSpan.FromDays(14));
        Assert.Equal(new[] { 9, 13, 16, 18 }, meetings.Select(m => m.StartUtc.Day).ToArray());
    }

    [Fact]
    public void Calendar_MonthlyRecurrenceSkipsInvalidMonthDays()
    {
        string content = """
            BEGIN:VEVENT
            SUMMARY:Month end
            DTSTART:20260131T090000Z
            RRULE:FREQ=MONTHLY;COUNT=3
            END:VEVENT
            """;
        var meetings = IcsCalendarParser.ParseUpcomingFromIcs(content, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(180));
        Assert.Equal(new[] { 1, 3, 5 }, meetings.Select(m => m.StartUtc.Month).ToArray());
    }

    [Fact]
    public void Calendar_RecurringMeetingsRetainLocalTimeAcrossDaylightSaving()
    {
        string content = """
            BEGIN:VEVENT
            SUMMARY:Local time
            DTSTART;TZID=Eastern Standard Time:20261031T090000
            RRULE:FREQ=DAILY;COUNT=3
            END:VEVENT
            """;
        var meetings = IcsCalendarParser.ParseUpcomingFromIcs(content, new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(4));
        Assert.Equal(new[] { 13, 14, 14 }, meetings.Select(m => m.StartUtc.Hour).ToArray());
    }

    [Fact]
    public async Task Calendar_ChangedConfigurationCannotBeOverwrittenByAnOlderFetch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var http = new HttpClient(new DelayedCalendarHandler(async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            string content = $"BEGIN:VEVENT\r\nSUMMARY:Old feed\r\nDTSTART:{DateTimeOffset.UtcNow.AddMinutes(3):yyyyMMdd'T'HHmmss'Z'}\r\nEND:VEVENT\r\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        }));
        try
        {
            using var calendar = new MeetingCalendarService(http);
            calendar.Configure(true, "https://calendar.example.test/feed");
            Task refresh = calendar.RefreshAndEvaluateAsync(timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            calendar.Configure(false, string.Empty);
            release.TrySetResult();
            await refresh;
            Assert.Null(calendar.CurrentActivity);
            Assert.Null(calendar.ActiveOrNextMeeting);
        }
        finally { release.TrySetResult(); }
    }

    private sealed class DelayedCalendarHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(cancellationToken);
    }

    [Fact]
    public void Transfers_DetectsDownloadsAlreadyInProgressAndClearsOnFolderChange()
    {
        string first = Path.Combine(_root, "first");
        string second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "active.zip.part"), "partial");
        using var monitor = new TransferMonitorService();
        monitor.Configure(true, first);
        Assert.NotNull(monitor.CurrentActivity);
        monitor.Configure(true, second);
        Assert.Null(monitor.CurrentActivity);
    }

    [Fact]
    public async Task Transfers_FrequentFileUpdatesDoNotPostponeSpeedSampling()
    {
        using var monitor = new TransferMonitorService();
        var measured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.ActivityChanged += (_, activity) =>
        {
            if (activity is not null && activity.PrimaryText != "0 B/s") measured.TrySetResult();
        };
        monitor.Configure(true, _root);
        string path = Path.Combine(_root, "busy.zip.part");
        for (int i = 0; i < 16; i++)
        {
            await File.AppendAllTextAsync(path, new string('x', 8192));
            await Task.Delay(75);
        }
        await measured.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Lyrics_BlankTimestampClearsThePreviousLyricAndInvalidSecondsAreIgnored()
    {
        var lines = LrcLyricsParser.Parse("[00:01]First\n[00:03]\n[00:75]Invalid\n[00:05]Next");
        Assert.Equal("First", LrcLyricsParser.FindActiveLine(lines, TimeSpan.FromSeconds(2)));
        Assert.Null(LrcLyricsParser.FindActiveLine(lines, TimeSpan.FromSeconds(4)));
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void VoiceSamples_SupportUnsigned8BitAndHandleNonfiniteFloats()
    {
        var pcm = VoiceNoteWavHelper.ConvertSamplesToPcm16(new byte[] { 0, 128, 255 }, 1, 8, false);
        Assert.True(BitConverter.ToInt16(pcm, 0) < -32000);
        Assert.Equal(0, BitConverter.ToInt16(pcm, 2));
        Assert.True(BitConverter.ToInt16(pcm, 4) > 32000);
        var invalid = VoiceNoteWavHelper.ConvertSamplesToPcm16(BitConverter.GetBytes(float.NaN), 1, 32, true);
        Assert.Equal(0, BitConverter.ToInt16(invalid));
    }

    [Fact]
    public async Task VoiceNotes_CaptureFailureAlwaysResetsRecordingState()
    {
        string inaccessibleDirectory = Path.Combine(_root, "file-not-directory");
        File.WriteAllText(inaccessibleDirectory, "block directory creation");
        using var service = new VoiceNoteService(inaccessibleDirectory);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RecordingFailed += (_, _) => failure.TrySetResult();
        Assert.True(service.StartRecording());
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await service.StopRecordingAsync();
        Assert.False(service.IsRecording);
        Assert.Null(service.CurrentActivity);
    }

    private static async Task<string> SendRawAsync(Uri uri, string headers, string body)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
        using var stream = client.GetStream();
        string request = $"POST {uri.AbsolutePath}/upload HTTP/1.1\r\nHost: localhost\r\n{headers}\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
        client.Client.Shutdown(SocketShutdown.Send);
        using var response = new MemoryStream();
        await stream.CopyToAsync(response).WaitAsync(TimeSpan.FromSeconds(3));
        return Encoding.UTF8.GetString(response.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
