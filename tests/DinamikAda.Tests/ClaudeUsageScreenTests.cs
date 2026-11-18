using DinamikAda.Modules.Productivity;
using Xunit;

namespace DinamikAda.Tests;

public sealed class ClaudeUsageScreenTests
{
    [Fact]
    public void ReadsSubscriptionWindowsFromTerminalOutput()
    {
        string output = "\u001b[22;3HCurrent\u001b[1Csession\u001b[23;54H0%\u001b[1Cused" +
            "\u001b[24;3HResets\u001b[1C3:20pm\u001b[1C(Europe/Istanbul)\r\n" +
            "  Current week (all models)\r\n  ███████ 27% used\r\n  Resets Nov 21, 5am (Europe/Istanbul)\r\n" +
            "  Current week (Sonnet only)\r\n  █████ 19% used\r\n  Resets Nov 24, 5am (Europe/Istanbul)\r\n";
        var now = new DateTimeOffset(2026, 11, 18, 7, 24, 0, TimeSpan.Zero);

        var report = ClaudeUsageScreen.Parse(ClaudeUsageScreen.RenderScreen(output, 120, 44), now);

        Assert.NotNull(report);
        Assert.Equal(new ClaudeUsageWindow(0, new DateTimeOffset(2026, 11, 18, 12, 20, 0, TimeSpan.Zero)), report.Session);
        Assert.Equal(new ClaudeUsageWindow(27, new DateTimeOffset(2026, 11, 21, 2, 0, 0, TimeSpan.Zero)), report.Weekly);
    }

    [Fact]
    public void AppliesTerminalUpdatesToTheExistingUsageRow()
    {
        string output = "\u001b[3;1HCurrent week (all models)\u001b[4;1H27% used\u001b[5;1HResets Nov 21, 5am (UTC)" +
            "\u001b[4;1H31\u001b[5;16H4:59am (UTC)\u001b[K";
        var now = new DateTimeOffset(2026, 11, 18, 7, 24, 0, TimeSpan.Zero);

        var report = ClaudeUsageScreen.Parse(ClaudeUsageScreen.RenderScreen(output, 120, 44), now);

        Assert.NotNull(report);
        Assert.Equal(new ClaudeUsageWindow(31, new DateTimeOffset(2026, 11, 21, 4, 59, 0, TimeSpan.Zero)), report.Weekly);
    }

    [Fact]
    public void KeepsMissingSessionUnknownAndHandlesWeeklyResetAcrossTheYear()
    {
        const string output = """
            Current session
            No active session
            Current week (all models)
            66.5% used
            Resets Jan 2, 1:30am (UTC)
            """;
        var now = new DateTimeOffset(2026, 12, 30, 13, 0, 0, TimeSpan.Zero);

        var report = ClaudeUsageScreen.Parse(output, now);

        Assert.NotNull(report);
        Assert.Null(report.Session);
        Assert.Equal(new ClaudeUsageWindow(66.5, new DateTimeOffset(2027, 1, 2, 1, 30, 0, TimeSpan.Zero)), report.Weekly);
    }
}
