using System.Globalization;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;

namespace DinamikAda.Core.Notifications;

/// <summary>
/// One toast that rises from the dock. UI-agnostic: the dock's toast host turns it into a frosted card.
/// </summary>
/// <param name="Key">Stable identity; the same key never toasts twice while it is remembered (see <see cref="ToastDeduper"/>).</param>
/// <param name="Title">Bold first line.</param>
/// <param name="Subtitle">Optional grey second line.</param>
/// <param name="AccentHex">Optional colour of the square icon tile on the left (no tile when null).</param>
/// <param name="IconGlyph">Optional Segoe Fluent Icons glyph drawn on the icon tile.</param>
/// <param name="ActionLabel">Optional pill button text on the right (e.g. "Takvim" / "Calendar").</param>
/// <param name="Action">Runs on the UI thread when the pill button is clicked; the toast then dismisses.</param>
/// <param name="IsImportant">Important toasts (meeting reminders) still show while Quiet Island is on.</param>
/// <param name="Lifetime">Auto-dismiss delay; defaults to <see cref="DockToastPlanner.DefaultLifetime"/>.</param>
public sealed record DockToastRequest(
    string Key,
    string Title,
    string? Subtitle = null,
    string? AccentHex = null,
    string? IconGlyph = null,
    string? ActionLabel = null,
    Action? Action = null,
    bool IsImportant = false,
    TimeSpan? Lifetime = null);

/// <summary>A meeting reminder computed by <see cref="DockToastPlanner.PlanMeetingReminder"/>.</summary>
public sealed record MeetingReminderPlan(string Key, string Title, string Subtitle, int MinutesUntil);

/// <summary>Pure scheduling rules for dock toasts: when to remind, how to stack, when to stay quiet.</summary>
public static class DockToastPlanner
{
    /// <summary>Meeting reminders fire this long before the start.</summary>
    public static readonly TimeSpan DefaultMeetingLead = TimeSpan.FromMinutes(5);

    /// <summary>Toasts dismiss themselves after this long unless hovered.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(6);

    /// <summary>At most this many toasts are visible at once; the oldest makes room for a new one.</summary>
    public const int MaxVisible = 3;

    public static string MeetingKey(string? summary, DateTimeOffset startUtc) =>
        "meeting:" + startUtc.UtcTicks.ToString(CultureInfo.InvariantCulture) + ":" + (summary ?? string.Empty).Trim();

    /// <summary>
    /// Returns a reminder when the meeting starts within <paramref name="lead"/> (exclusive of "already started"),
    /// or null. All-day events never remind. Minutes round up, so 4m59s reads "5 min".
    /// </summary>
    public static MeetingReminderPlan? PlanMeetingReminder(
        string? summary,
        DateTimeOffset startUtc,
        bool isAllDay,
        DateTimeOffset nowUtc,
        string lang,
        TimeZoneInfo? zone = null,
        TimeSpan? lead = null)
    {
        if (isAllDay) return null;
        var window = lead ?? DefaultMeetingLead;
        var until = startUtc - nowUtc;
        if (until <= TimeSpan.Zero || until > window) return null;

        int minutes = Math.Max(1, (int)Math.Ceiling(until.TotalMinutes - 1e-9));
        string name = string.IsNullOrWhiteSpace(summary) ? Strings.Pick(lang, "Toplantı", "Meeting") : summary.Trim();
        var local = TimeZoneInfo.ConvertTime(startUtc, zone ?? TimeZoneInfo.Local);
        string at = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        string title = Strings.Pick(lang, $"{name} {minutes} dk sonra", $"{name} in {minutes} min");
        string subtitle = Strings.Pick(lang, $"Saat {at}", $"at {at}");
        return new MeetingReminderPlan(MeetingKey(summary, startUtc), title, subtitle, minutes);
    }

    /// <summary>
    /// Whether a toast may show now. Fullscreen apps (games, video, presentations) always silence toasts;
    /// Quiet Island silences everything but important toasts.
    /// </summary>
    public static bool ShouldShow(bool enabled, bool isFullscreen, bool quietMode, bool isImportant) =>
        enabled && !isFullscreen && (!quietMode || isImportant);

    /// <summary>How many of the currently visible toasts (oldest first) must leave so a new one fits.</summary>
    public static int EvictCountForNew(int visibleCount, int maxVisible = MaxVisible) =>
        Math.Max(0, visibleCount - Math.Max(1, maxVisible) + 1);

    /// <summary>
    /// Distance from the dock edge to the near edge of toast <paramref name="index"/>, where index 0 is the newest
    /// (nearest the dock) and heights are listed newest first.
    /// </summary>
    public static double OffsetFromDock(IReadOnlyList<double> heightsNewestFirst, int index, double gap)
    {
        double y = 0;
        for (int i = 0; i < index && i < heightsNewestFirst.Count; i++) y += heightsNewestFirst[i] + gap;
        return y;
    }

    /// <summary>
    /// Maps the island HUD events that deserve a dock toast (timer / Pomodoro finished, AI quota reset) to a request.
    /// Returns null for every other HUD.
    /// </summary>
    public static DockToastRequest? FromHud(TransientHudEvent hud, string lang, Action? openTimers = null, Action? openAiQuota = null)
    {
        if (hud.CoalesceKey.StartsWith("timer:", StringComparison.Ordinal) && hud.Kind == HudKind.TimerAlert)
        {
            return new DockToastRequest(
                hud.CoalesceKey,
                string.IsNullOrWhiteSpace(hud.Title) ? Strings.Pick(lang, "Zamanlayıcı", "Timer") : hud.Title,
                hud.Subtitle ?? Strings.Pick(lang, "Süre doldu!", "Time's up!"),
                AccentHex: "#FF9F0A",
                IconGlyph: "",
                ActionLabel: openTimers is null ? null : Strings.Pick(lang, "Aç", "Open"),
                Action: openTimers);
        }

        if (hud.CoalesceKey.StartsWith("ai-quota-reset:", StringComparison.Ordinal))
        {
            return new DockToastRequest(
                hud.CoalesceKey,
                hud.Title,
                hud.Subtitle,
                AccentHex: string.IsNullOrWhiteSpace(hud.AccentHex) || hud.AccentHex == "#FFFFFF" ? "#30D158" : hud.AccentHex,
                IconGlyph: "",
                ActionLabel: openAiQuota is null ? null : Strings.Pick(lang, "Yönet", "Manage"),
                Action: openAiQuota);
        }

        return null;
    }
}

/// <summary>Remembers toast keys so the same event never toasts twice (bounded, with expiry).</summary>
public sealed class ToastDeduper
{
    private readonly Dictionary<string, DateTimeOffset> _expiresUtc = new(StringComparer.Ordinal);
    private readonly TimeSpan _defaultMemory;
    private readonly int _capacity;

    public ToastDeduper(TimeSpan? defaultMemory = null, int capacity = 256)
    {
        _defaultMemory = defaultMemory ?? TimeSpan.FromHours(24);
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _expiresUtc.Count;

    public bool HasSeen(string key, DateTimeOffset nowUtc) =>
        _expiresUtc.TryGetValue(key, out var exp) && exp > nowUtc;

    /// <summary>Returns true (and remembers the key) the first time; false while the key is remembered.</summary>
    public bool TryMark(string key, DateTimeOffset nowUtc, TimeSpan? memory = null)
    {
        if (string.IsNullOrEmpty(key)) return true;
        Prune(nowUtc);
        if (HasSeen(key, nowUtc)) return false;
        _expiresUtc[key] = nowUtc + (memory ?? _defaultMemory);
        while (_expiresUtc.Count > _capacity)
        {
            string oldest = _expiresUtc.MinBy(kv => kv.Value).Key;
            _expiresUtc.Remove(oldest);
        }
        return true;
    }

    private void Prune(DateTimeOffset nowUtc)
    {
        if (_expiresUtc.Count == 0) return;
        List<string>? dead = null;
        foreach (var kv in _expiresUtc)
        {
            if (kv.Value <= nowUtc) (dead ??= new()).Add(kv.Key);
        }
        if (dead is null) return;
        foreach (var k in dead) _expiresUtc.Remove(k);
    }
}

/// <summary>Auto-dismiss countdown that pauses while the pointer is over the toast.</summary>
public sealed class ToastLifetime
{
    private TimeSpan _remaining;
    private DateTimeOffset _runningSince;

    public ToastLifetime(TimeSpan lifetime, DateTimeOffset nowUtc)
    {
        _remaining = lifetime < TimeSpan.Zero ? TimeSpan.Zero : lifetime;
        _runningSince = nowUtc;
        IsRunning = true;
    }

    public bool IsRunning { get; private set; }

    public TimeSpan RemainingAt(DateTimeOffset nowUtc)
    {
        var r = IsRunning ? _remaining - (nowUtc - _runningSince) : _remaining;
        return r < TimeSpan.Zero ? TimeSpan.Zero : r;
    }

    public bool IsExpired(DateTimeOffset nowUtc) => RemainingAt(nowUtc) <= TimeSpan.Zero;

    public void Pause(DateTimeOffset nowUtc)
    {
        if (!IsRunning) return;
        _remaining = RemainingAt(nowUtc);
        IsRunning = false;
    }

    /// <summary>Resumes; a hover that left very little time grants a short grace so the toast doesn't vanish instantly.</summary>
    public void Resume(DateTimeOffset nowUtc, TimeSpan? minimumAfterResume = null)
    {
        if (IsRunning) return;
        var floor = minimumAfterResume ?? TimeSpan.FromSeconds(1.5);
        if (_remaining < floor) _remaining = floor;
        _runningSince = nowUtc;
        IsRunning = true;
    }
}
