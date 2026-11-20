using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Notifications;
using Xunit;

namespace DinamikAda.Tests;

public class DockToastPlanningTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 13, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(301, false)]   // 5m01s before: too early
    [InlineData(300, true)]    // exactly 5 min before
    [InlineData(299, true)]
    [InlineData(30, true)]
    [InlineData(0, false)]     // already started
    [InlineData(-60, false)]
    public void Meeting_reminder_fires_only_inside_the_five_minute_window(int secondsBefore, bool expected)
    {
        var plan = DockToastPlanner.PlanMeetingReminder("Design review", Start, false, Start.AddSeconds(-secondsBefore), "en", TimeZoneInfo.Utc);
        Assert.Equal(expected, plan is not null);
    }

    [Fact]
    public void Meeting_reminder_rounds_minutes_up_and_formats_both_languages()
    {
        var en = DockToastPlanner.PlanMeetingReminder("Design review", Start, false, Start.AddSeconds(-299), "en", TimeZoneInfo.Utc)!;
        Assert.Equal("Design review in 5 min", en.Title);
        Assert.Equal("at 13:00", en.Subtitle);
        Assert.Equal(5, en.MinutesUntil);

        var tr = DockToastPlanner.PlanMeetingReminder("  ", Start, false, Start.AddSeconds(-20), "tr", TimeZoneInfo.Utc)!;
        Assert.Equal("Toplantı 1 dk sonra", tr.Title);
        Assert.Equal("Saat 13:00", tr.Subtitle);
    }

    [Fact]
    public void All_day_events_never_remind()
    {
        Assert.Null(DockToastPlanner.PlanMeetingReminder("Holiday", Start, true, Start.AddMinutes(-2), "en", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Meeting_key_is_stable_across_polls_and_differs_per_occurrence()
    {
        var a = DockToastPlanner.PlanMeetingReminder("Standup", Start, false, Start.AddMinutes(-4.5), "en", TimeZoneInfo.Utc)!;
        var b = DockToastPlanner.PlanMeetingReminder("Standup", Start, false, Start.AddMinutes(-1), "en", TimeZoneInfo.Utc)!;
        var nextDay = DockToastPlanner.PlanMeetingReminder("Standup", Start.AddDays(1), false, Start.AddDays(1).AddMinutes(-1), "en", TimeZoneInfo.Utc)!;
        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, nextDay.Key);
    }

    [Fact]
    public void Deduper_toasts_each_key_once_until_it_expires()
    {
        var d = new ToastDeduper(TimeSpan.FromHours(1));
        var now = Start;
        Assert.True(d.TryMark("meeting:1", now));
        Assert.False(d.TryMark("meeting:1", now.AddMinutes(4)));
        Assert.True(d.TryMark("meeting:2", now.AddMinutes(4)));
        Assert.True(d.HasSeen("meeting:1", now.AddMinutes(59)));
        Assert.True(d.TryMark("meeting:1", now.AddMinutes(61)));
    }

    [Fact]
    public void Deduper_honours_per_key_memory_and_capacity()
    {
        var d = new ToastDeduper(TimeSpan.FromHours(24), capacity: 2);
        Assert.True(d.TryMark("timer:a@hud", Start, TimeSpan.FromSeconds(90)));
        Assert.False(d.TryMark("timer:a@hud", Start.AddSeconds(30)));
        Assert.True(d.TryMark("timer:a@hud", Start.AddSeconds(91)));
        d.TryMark("b", Start.AddSeconds(92));
        d.TryMark("c", Start.AddSeconds(93));
        Assert.Equal(2, d.Count);
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, false, true, false)]  // disabled in settings
    [InlineData(true, true, false, true, false)]    // fullscreen silences even important toasts
    [InlineData(true, false, true, false, false)]   // quiet island silences ordinary toasts
    [InlineData(true, false, true, true, true)]     // ...but not meeting reminders
    public void Gate_respects_settings_fullscreen_and_quiet(bool enabled, bool fullscreen, bool quiet, bool important, bool expected)
    {
        Assert.Equal(expected, DockToastPlanner.ShouldShow(enabled, fullscreen, quiet, important));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 3)]
    public void Stack_keeps_at_most_three(int visible, int evict)
    {
        Assert.Equal(evict, DockToastPlanner.EvictCountForNew(visible));
    }

    [Fact]
    public void Newest_sits_nearest_the_dock_and_older_ones_stack_away()
    {
        var heights = new[] { 60.0, 70.0, 64.0 };
        Assert.Equal(0, DockToastPlanner.OffsetFromDock(heights, 0, 10));
        Assert.Equal(70, DockToastPlanner.OffsetFromDock(heights, 1, 10));
        Assert.Equal(150, DockToastPlanner.OffsetFromDock(heights, 2, 10));
    }

    [Fact]
    public void Lifetime_pauses_while_hovered_and_grants_grace_on_resume()
    {
        var t = new ToastLifetime(TimeSpan.FromSeconds(6), Start);
        Assert.Equal(TimeSpan.FromSeconds(2), t.RemainingAt(Start.AddSeconds(4)));
        t.Pause(Start.AddSeconds(5));
        Assert.False(t.IsExpired(Start.AddSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(1), t.RemainingAt(Start.AddSeconds(60)));
        t.Resume(Start.AddSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(1.5), t.RemainingAt(Start.AddSeconds(60)));
        Assert.True(t.IsExpired(Start.AddSeconds(61.5)));
    }

    [Fact]
    public void Hud_mapping_picks_timers_and_quota_resets_only()
    {
        var timer = new TransientHudEvent("timer:abc", HudKind.TimerAlert, "Pomodoro", "Time's up!");
        var quota = new TransientHudEvent("ai-quota-reset:slot1", HudKind.WidgetAlert, "Claude ready", "Limit reset", AccentHex: "#D97757");
        var volume = new TransientHudEvent("hud:volume", HudKind.Volume, "Volume");

        var t = DockToastPlanner.FromHud(timer, "en", openTimers: () => { });
        Assert.NotNull(t);
        Assert.Equal("Open", t!.ActionLabel);
        var q = DockToastPlanner.FromHud(quota, "tr", openAiQuota: () => { });
        Assert.Equal("Yönet", q!.ActionLabel);
        Assert.Equal("#D97757", q.AccentHex);
        Assert.Null(DockToastPlanner.FromHud(volume, "en"));
    }
}
