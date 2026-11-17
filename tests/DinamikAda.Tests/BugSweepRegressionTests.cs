using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using DinamikAda.Core.Config;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Modules.Productivity;
using DinamikAda.Platform.Win32.Audio;
using DinamikAda.Platform.Win32.Interop;
using Xunit;

namespace DinamikAda.Tests;

/// <summary>
/// Regression coverage for the 2026-11 bug sweep (struct layouts, bridge hardening, ICS recurrence,
/// config durability).
/// </summary>
public sealed class BugSweepRegressionTests
{
    [Fact]
    public void WaveFormatStructs_MatchNativeMmregPacking()
    {
        Assert.Equal(18, Marshal.SizeOf<WaveFormatEx>());
        Assert.Equal(40, Marshal.SizeOf<WaveFormatExtensible>());
        Assert.Equal(18, Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.Samples)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.dwChannelMask)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<WaveFormatExtensible>(nameof(WaveFormatExtensible.SubFormat)).ToInt32());
    }

    [Fact]
    public void TokenPrivileges_MatchNativePshpack4Layout()
    {
        Assert.Equal(16, Marshal.SizeOf<NativeMethods.TOKEN_PRIVILEGES_SINGLE>());
        Assert.Equal(4, Marshal.OffsetOf<NativeMethods.TOKEN_PRIVILEGES_SINGLE>(nameof(NativeMethods.TOKEN_PRIVILEGES_SINGLE.Luid)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeMethods.TOKEN_PRIVILEGES_SINGLE>(nameof(NativeMethods.TOKEN_PRIVILEGES_SINGLE.Attributes)).ToInt32());
        Assert.Equal(12, Marshal.SizeOf<NativeMethods.LUID_AND_ATTRIBUTES>());
    }

    [Theory]
    [InlineData("127.0.0.1:47821", true)]
    [InlineData("localhost:47821", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("[::1]:47821", true)]
    [InlineData("evil.example.com:47821", false)]
    [InlineData("127.0.0.1.nip.io:47821", false)]
    public void DeveloperBridge_HostHeaderMustBeLoopback(string host, bool expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = host };
        Assert.Equal(expected, DeveloperBridgeService.IsLoopbackHostHeader(headers));
        Assert.True(DeveloperBridgeService.IsLoopbackHostHeader(new Dictionary<string, string>()));
    }

    [Fact]
    public async Task DeveloperBridge_RejectsBrowserOriginRequests_AndAcceptsSuccessWarning()
    {
        using var bridge = new DeveloperBridgeService($"DinamikAdaSweepPipe_{Guid.NewGuid():N}");
        var received = new List<DevBridgeNotification>();
        bridge.NotificationReceived += n => { lock (received) received.Add(n); };
        bridge.Start(enabled: true, port: 0);
        Assert.True(bridge.IsListening);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bridge.ListeningPort}") };

        using (var req = new HttpRequestMessage(HttpMethod.Post, "/notify"))
        {
            req.Content = new StringContent("""{"title":"phish"}""", Encoding.UTF8, "text/plain");
            req.Headers.TryAddWithoutValidation("Origin", "https://evil.example.com");
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        foreach (string status in new[] { "success", "warning" })
        {
            using var resp = await http.PostAsync("/notify", new StringContent($$"""{"title":"t","status":"{{status}}"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        lock (received)
        {
            Assert.DoesNotContain(received, n => n.Title == "phish");
        }
    }

    private static readonly DateTimeOffset SweepNow = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Ics(params string[] eventBlocks) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + string.Concat(eventBlocks) + "END:VCALENDAR\r\n";

    [Fact]
    public void Ics_AllDayEventsAreFlagged()
    {
        string ics = Ics("BEGIN:VEVENT\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20261102\r\nEND:VEVENT\r\n",
                         "BEGIN:VEVENT\r\nSUMMARY:Standup\r\nDTSTART:20261102T090000Z\r\nDTEND:20261102T091500Z\r\nEND:VEVENT\r\n");
        var items = IcsCalendarParser.ParseUpcomingFromIcs(ics, SweepNow, TimeSpan.FromDays(3));
        Assert.True(items.Single(i => i.Summary == "Holiday").IsAllDay);
        Assert.False(items.Single(i => i.Summary == "Standup").IsAllDay);
    }

    [Fact]
    public void Ics_MonthlyOrdinalByDay_FirstMondayAndLastFriday()
    {
        string firstMonday = Ics("BEGIN:VEVENT\r\nSUMMARY:M\r\nDTSTART:20261102T100000Z\r\nDTEND:20261102T110000Z\r\nRRULE:FREQ=MONTHLY;BYDAY=1MO\r\nEND:VEVENT\r\n");
        var m = IcsCalendarParser.ParseUpcomingFromIcs(firstMonday, SweepNow, TimeSpan.FromDays(70));
        Assert.Equal(new[] { 2, 7, 4 }, m.Select(o => o.StartUtc.Day).ToArray());

        string lastFriday = Ics("BEGIN:VEVENT\r\nSUMMARY:F\r\nDTSTART:20261127T090000Z\r\nDTEND:20261127T100000Z\r\nRRULE:FREQ=MONTHLY;BYDAY=-1FR\r\nEND:VEVENT\r\n");
        var f = IcsCalendarParser.ParseUpcomingFromIcs(lastFriday, SweepNow, TimeSpan.FromDays(70));
        Assert.Equal(new[] { 27, 25 }, f.Select(o => o.StartUtc.Day).ToArray());
    }

    [Fact]
    public void Ics_DateOnlyUntilIncludesLastDay()
    {
        string ics = Ics("BEGIN:VEVENT\r\nSUMMARY:D\r\nDTSTART:20261116T100000Z\r\nDTEND:20261116T103000Z\r\nRRULE:FREQ=DAILY;UNTIL=20261118\r\nEND:VEVENT\r\n");
        var items = IcsCalendarParser.ParseUpcomingFromIcs(ics, new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(10));
        Assert.Equal(new[] { 16, 17, 18 }, items.Select(o => o.StartUtc.Day).ToArray());
    }

    [Fact]
    public void Ics_RecurrenceIdOverridesReplaceAndCancelMasterInstances()
    {
        string ics = Ics(
            "BEGIN:VEVENT\r\nUID:abc\r\nSUMMARY:Sync\r\nDTSTART:20261116T100000Z\r\nDTEND:20261116T103000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\n",
            "BEGIN:VEVENT\r\nUID:abc\r\nRECURRENCE-ID:20261117T100000Z\r\nSUMMARY:Sync (moved)\r\nDTSTART:20261117T150000Z\r\nDTEND:20261117T153000Z\r\nEND:VEVENT\r\n",
            "BEGIN:VEVENT\r\nUID:abc\r\nRECURRENCE-ID:20261118T100000Z\r\nSTATUS:CANCELLED\r\nSUMMARY:Sync\r\nDTSTART:20261118T100000Z\r\nEND:VEVENT\r\n");
        var items = IcsCalendarParser.ParseUpcomingFromIcs(ics, new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromDays(5));
        Assert.Equal(
            new[] { new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 17, 15, 0, 0, TimeSpan.Zero) },
            items.Select(o => o.StartUtc).ToArray());
    }

    [Fact]
    public void AppConfigSanitize_KeepsAiQuotaSlotInstances()
    {
        var slot = new AiQuotaSlotConfig { Id = "a", Provider = "Claude", Label = "  Claude #1  ", ResetWindowMinutes = 300 };
        var cfg = new AppConfig { AiQuotaSlots = [slot] };
        cfg.Sanitize();
        Assert.Same(slot, Assert.Single(cfg.AiQuotaSlots));
        Assert.Equal("Claude #1", slot.Label);
    }

    [Fact]
    public void AppConfigStore_BacksUpCorruptConfigBeforeFallingBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dinamikada-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{ \"Language\": \"en\", this is not json");
            var store = new AppConfigStore(path);
            _ = store.Load();
            Assert.Single(Directory.GetFiles(dir, "config.json.corrupt-*.bak"));

            store.Save(new AppConfig { Language = "en" });
            Assert.Equal("en", new AppConfigStore(path).Load().Language);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
