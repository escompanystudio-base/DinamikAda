using DinamikAda.Core.Voice;
using Xunit;

namespace DinamikAda.Tests;

public sealed class ScheduleMathTests
{
    // 2026-11-16 is a Monday.
    private static readonly DateTime Monday = new(2026, 11, 16);

    private static VoiceRoutine Routine(string time, int mask = 127, string lastRun = "", bool enabled = true) => new()
    {
        Name = "r",
        TimeOfDay = time,
        DaysMask = mask,
        LastRunDate = lastRun,
        Enabled = enabled,
        Actions = new() { new VoiceAction { Kind = VoiceActionKind.OpenApp, Target = "Spotify" } }
    };

    private static TimeZoneInfo Berlin => TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    [Theory]
    [InlineData(DayOfWeek.Monday, 1)]
    [InlineData(DayOfWeek.Wednesday, 4)]
    [InlineData(DayOfWeek.Saturday, 32)]
    [InlineData(DayOfWeek.Sunday, 64)]
    public void DayBit_MondayIsBitZero(DayOfWeek day, int bit) => Assert.Equal(bit, ScheduleMath.DayBit(day));

    [Theory]
    [InlineData(0, DayOfWeek.Sunday, true)]
    [InlineData(127, DayOfWeek.Thursday, true)]
    [InlineData(31, DayOfWeek.Friday, true)]
    [InlineData(31, DayOfWeek.Saturday, false)]
    [InlineData(96, DayOfWeek.Sunday, true)]
    [InlineData(96, DayOfWeek.Monday, false)]
    public void RunsOn_RespectsMask(int mask, DayOfWeek day, bool expected) => Assert.Equal(expected, ScheduleMath.RunsOn(mask, day));

    [Theory]
    [InlineData("9", "09:00")]
    [InlineData("09:00", "09:00")]
    [InlineData("9:5", "09:05")]
    [InlineData("21.30", "21:30")]
    [InlineData(" 7:45 ", "07:45")]
    [InlineData("00:00", "00:00")]
    [InlineData("23:59", "23:59")]
    [InlineData("24:00", null)]
    [InlineData("9:60", null)]
    [InlineData("sabah", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeTimeOfDay(string? input, string? expected) => Assert.Equal(expected, ScheduleMath.NormalizeTimeOfDay(input));

    [Fact]
    public void ParseDaysMask_HandlesNamesAndGroups()
    {
        Assert.Equal(127, ScheduleMath.ParseDaysMask(null));
        Assert.Equal(127, ScheduleMath.ParseDaysMask(Array.Empty<string>()));
        Assert.Equal(127, ScheduleMath.ParseDaysMask(new[] { "every day" }));
        Assert.Equal(127, ScheduleMath.ParseDaysMask(new[] { "Her gün" }));
        Assert.Equal(31, ScheduleMath.ParseDaysMask(new[] { "mon", "TUE", "wed", "thu", "fri" }));
        Assert.Equal(31, ScheduleMath.ParseDaysMask(new[] { "weekdays" }));
        Assert.Equal(96, ScheduleMath.ParseDaysMask(new[] { "weekend" }));
        Assert.Equal(1 | 4 | 16, ScheduleMath.ParseDaysMask(new[] { "Monday", "çarşamba", "Cum" }));
        Assert.Equal(64, ScheduleMath.ParseDaysMask(new[] { "sun", "nonsense" }));
        Assert.Equal(127, ScheduleMath.ParseDaysMask(new[] { "nonsense" }));
    }

    [Theory]
    [InlineData(127, "tr", "Her gün")]
    [InlineData(0, "en", "Every day")]
    [InlineData(31, "tr", "Hafta içi")]
    [InlineData(31, "en", "Weekdays")]
    [InlineData(96, "tr", "Hafta sonu")]
    [InlineData(96, "en", "Weekends")]
    [InlineData(1 | 4 | 16, "tr", "Pzt, Çar, Cum")]
    [InlineData(1 | 4 | 16, "en", "Mon, Wed, Fri")]
    [InlineData(64 | 2, "en", "Tue, Sun")]
    public void DescribeDays(int mask, string lang, string expected) => Assert.Equal(expected, ScheduleMath.DescribeDays(mask, lang));

    [Fact]
    public void Task_DueStaleAndDone()
    {
        var now = Monday.AddHours(11).AddMinutes(1);
        var task = new ScheduledVoiceTask { RunAtLocal = Monday.AddHours(11), Actions = { new VoiceAction() } };
        Assert.Equal(TaskDue.Run, ScheduleMath.CheckTask(task, now));
        Assert.Equal(TaskDue.NotDue, ScheduleMath.CheckTask(task, Monday.AddHours(10).AddMinutes(59)));
        Assert.Equal(TaskDue.Run, ScheduleMath.CheckTask(task, Monday.AddHours(22)));
        Assert.Equal(TaskDue.Stale, ScheduleMath.CheckTask(task, Monday.AddHours(23).AddMinutes(1)));
        task.Done = true;
        Assert.Equal(TaskDue.NotDue, ScheduleMath.CheckTask(task, now));
    }

    [Fact]
    public void Routine_RunsAtTimeWithinCatchUpTolerance()
    {
        var r = Routine("09:00");
        Assert.Null(ScheduleMath.DueOccurrence(r, Monday.AddHours(8).AddMinutes(59)));
        Assert.Equal(Monday.AddHours(9), ScheduleMath.DueOccurrence(r, Monday.AddHours(9)));
        Assert.True(ScheduleMath.IsRoutineDue(r, Monday.AddHours(9).AddMinutes(5)));   // woke at 9:05
        Assert.True(ScheduleMath.IsRoutineDue(r, Monday.AddHours(9).AddMinutes(10)));
        Assert.False(ScheduleMath.IsRoutineDue(r, Monday.AddHours(9).AddMinutes(30))); // woke at 9:30
    }

    [Fact]
    public void Routine_RunsOncePerDay()
    {
        var r = Routine("09:00", lastRun: "2026-11-16");
        Assert.False(ScheduleMath.IsRoutineDue(r, Monday.AddHours(9).AddMinutes(1)));
        Assert.True(ScheduleMath.IsRoutineDue(r, Monday.AddDays(1).AddHours(9).AddMinutes(1)));
    }

    [Fact]
    public void Routine_RespectsDaysMaskAndEnabled()
    {
        var weekend = Routine("09:00", mask: 96);
        Assert.False(ScheduleMath.IsRoutineDue(weekend, Monday.AddHours(9)));
        Assert.True(ScheduleMath.IsRoutineDue(weekend, Monday.AddDays(5).AddHours(9))); // Saturday
        Assert.False(ScheduleMath.IsRoutineDue(Routine("09:00", enabled: false), Monday.AddHours(9)));
        var empty = Routine("09:00");
        empty.Actions.Clear();
        Assert.False(ScheduleMath.IsRoutineDue(empty, Monday.AddHours(9)));
        Assert.False(ScheduleMath.IsRoutineDue(Routine("bad"), Monday.AddHours(9)));
    }

    [Fact]
    public void Routine_CatchesUpAcrossMidnight()
    {
        // Monday-only routine at 23:55; the PC wakes Tuesday 00:03.
        var r = Routine("23:55", mask: 1);
        var due = ScheduleMath.DueOccurrence(r, Monday.AddDays(1).AddMinutes(3));
        Assert.Equal(Monday.AddHours(23).AddMinutes(55), due);
        Assert.Equal("2026-11-16", ScheduleMath.DateKey(due!.Value));
        // Already run for Monday: nothing to do.
        r.LastRunDate = "2026-11-16";
        Assert.Null(ScheduleMath.DueOccurrence(r, Monday.AddDays(1).AddMinutes(3)));
        // Midnight itself.
        var midnight = Routine("00:00");
        Assert.Equal(Monday, ScheduleMath.DueOccurrence(midnight, Monday.AddMinutes(4)));
    }

    [Fact]
    public void Routine_DstSpringForward_GapTimeRunsWhenClockJumps()
    {
        // Europe: 2026-03-29 02:00 -> 03:00. 02:30 does not exist that day.
        var day = new DateTime(2026, 3, 29);
        var r = Routine("02:30");
        Assert.Equal(day.AddHours(3), ScheduleMath.DueOccurrence(r, day.AddHours(3).AddMinutes(4), Berlin));
        Assert.False(ScheduleMath.IsRoutineDue(r, day.AddHours(3).AddMinutes(15), Berlin));
        Assert.Equal(day.AddHours(3), ScheduleMath.NextRunLocal(r, day.AddHours(1), Berlin));
        // A normal time on the DST day behaves normally.
        Assert.True(ScheduleMath.IsRoutineDue(Routine("09:00"), day.AddHours(9).AddMinutes(5), Berlin));
    }

    [Fact]
    public void Routine_DstFallBack_RunsOnceInRepeatedHour()
    {
        // Europe: 2026-10-25 03:00 -> 02:00. 02:30 happens twice.
        var day = new DateTime(2026, 10, 25);
        var r = Routine("02:30");
        var due = ScheduleMath.DueOccurrence(r, day.AddHours(2).AddMinutes(35), Berlin);
        Assert.NotNull(due);
        r.LastRunDate = ScheduleMath.DateKey(due!.Value);
        Assert.False(ScheduleMath.IsRoutineDue(r, day.AddHours(2).AddMinutes(36), Berlin));
        // Elapsed time across the 01:59 -> 02:xx boundary is measured in real time (10 min, still due).
        Assert.True(ScheduleMath.IsRoutineDue(Routine("01:55"), day.AddHours(2).AddMinutes(5), Berlin));
    }

    [Fact]
    public void NextRunLocal_ForDisplay()
    {
        var r = Routine("09:00");
        Assert.Equal(Monday.AddHours(9), ScheduleMath.NextRunLocal(r, Monday.AddHours(8)));
        // Within the catch-up window and not run yet: it is about to run now.
        Assert.Equal(Monday.AddHours(9), ScheduleMath.NextRunLocal(r, Monday.AddHours(9).AddMinutes(5)));
        // Missed today: tomorrow.
        Assert.Equal(Monday.AddDays(1).AddHours(9), ScheduleMath.NextRunLocal(r, Monday.AddHours(10)));
        // Already ran today: tomorrow.
        r.LastRunDate = "2026-11-16";
        Assert.Equal(Monday.AddDays(1).AddHours(9), ScheduleMath.NextRunLocal(r, Monday.AddHours(8)));
        // Weekend-only from a Monday: Saturday.
        Assert.Equal(Monday.AddDays(5).AddHours(9), ScheduleMath.NextRunLocal(Routine("09:00", mask: 96), Monday.AddHours(8)));
        // Monday-only, already past today: next Monday.
        Assert.Equal(Monday.AddDays(7).AddHours(9), ScheduleMath.NextRunLocal(Routine("09:00", mask: 1), Monday.AddHours(12)));
        Assert.Null(ScheduleMath.NextRunLocal(Routine("09:00", enabled: false), Monday));
        Assert.Null(ScheduleMath.NextRunLocal(Routine("x"), Monday));
    }
}
