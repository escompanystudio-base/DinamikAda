using System.Globalization;

namespace DinamikAda.Core.Voice;

/// <summary>What the scheduler should do with a one-off task right now.</summary>
public enum TaskDue
{
    NotDue = 0,
    /// <summary>Its time has come: run it once and mark it done.</summary>
    Run = 1,
    /// <summary>Missed by more than <see cref="ScheduleMath.StaleTaskAge"/>: mark it done without running.</summary>
    Stale = 2
}

/// <summary>When scheduled voice tasks and routines are due (pure, unit tested).</summary>
public static class ScheduleMath
{
    /// <summary>A routine still runs if the PC wakes up this long after its time.</summary>
    public static readonly TimeSpan RoutineCatchUp = TimeSpan.FromMinutes(10);

    /// <summary>One-off tasks missed by more than this are only marked done.</summary>
    public static readonly TimeSpan StaleTaskAge = TimeSpan.FromHours(12);

    public const int EveryDay = 127;
    public const int Weekdays = 31;
    public const int Weekend = 96;

    private static readonly string[] ShortTr = { "Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz" };
    private static readonly string[] ShortEn = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    /// <summary>The DaysMask bit for a day: Monday = 1 … Sunday = 64.</summary>
    public static int DayBit(DayOfWeek day) => 1 << (((int)day + 6) % 7);

    /// <summary>True when <paramref name="mask"/> includes <paramref name="day"/>; 0 means every day.</summary>
    public static bool RunsOn(int mask, DayOfWeek day) => (mask & EveryDay) == 0 || (mask & DayBit(day)) != 0;

    /// <summary>yyyy-MM-dd, the format of <see cref="VoiceRoutine.LastRunDate"/>.</summary>
    public static string DateKey(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Parses "9", "09:00", "9:5", "21.30"; null when invalid.</summary>
    public static TimeSpan? ParseTimeOfDay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':', '.');
        if (parts.Length is < 1 or > 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h)) return null;
        int m = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out m)) return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return new TimeSpan(h, m, 0);
    }

    /// <summary>"HH:mm" or null when invalid.</summary>
    public static string? NormalizeTimeOfDay(string? text) =>
        ParseTimeOfDay(text) is { } t ? t.ToString(@"hh\:mm", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Day names (mon..sun, English or Turkish, full or short) or groups ("every day", "weekdays", "weekend",
    /// "her gün", "hafta içi") into a DaysMask. Nothing recognised means every day.
    /// </summary>
    public static int ParseDaysMask(IEnumerable<string?>? days)
    {
        int mask = 0;
        if (days is not null)
        {
            foreach (var raw in days)
                mask |= DayTokenMask(raw);
        }
        mask &= EveryDay;
        return mask == 0 ? EveryDay : mask;
    }

    private static int DayTokenMask(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        string s = raw.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"))
            .Replace('_', ' ').Replace('-', ' ');
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        switch (s)
        {
            case "every day" or "everyday" or "daily" or "all" or "her gün" or "hergün" or "her gun" or "günlük" or "her sabah" or "her akşam":
                return EveryDay;
            case "weekdays" or "weekday" or "work days" or "workdays" or "hafta içi" or "hafta ici" or "iş günleri":
                return Weekdays;
            case "weekend" or "weekends" or "hafta sonu" or "haftasonu":
                return Weekend;
        }
        // Prefix match against day names (both languages, Turkish dotless/dotted normalised).
        string f = Fold(s);
        string[][] names =
        {
            new[] { "mon", "monday", "pzt", "pazartesi" },
            new[] { "tue", "tues", "tuesday", "sal", "sali" },
            new[] { "wed", "wednesday", "car", "carsamba" },
            new[] { "thu", "thur", "thurs", "thursday", "per", "persembe" },
            new[] { "fri", "friday", "cum", "cuma" },
            new[] { "sat", "saturday", "cmt", "cumartesi" },
            new[] { "sun", "sunday", "paz", "pazar" },
        };
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].Contains(f)) return 1 << i;
        }
        return 0;
    }

    private static string Fold(string s) => s
        .Replace('ı', 'i').Replace('ş', 's').Replace('ç', 'c').Replace('ğ', 'g').Replace('ö', 'o').Replace('ü', 'u')
        .Replace("i̇", "i");

    /// <summary>"Her gün", "Hafta içi", "Hafta sonu" or "Pzt, Çar, Cum" (English when <paramref name="lang"/> is "en").</summary>
    public static string DescribeDays(int mask, string lang)
    {
        bool en = lang == "en";
        mask &= EveryDay;
        if (mask is 0 or EveryDay) return en ? "Every day" : "Her gün";
        if (mask == Weekdays) return en ? "Weekdays" : "Hafta içi";
        if (mask == Weekend) return en ? "Weekends" : "Hafta sonu";
        var names = en ? ShortEn : ShortTr;
        return string.Join(", ", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => names[i]));
    }

    /// <summary>Whether a one-off task should run now, be dropped as stale, or wait.</summary>
    public static TaskDue CheckTask(ScheduledVoiceTask task, DateTime nowLocal)
    {
        if (task.Done || task.RunAtLocal > nowLocal) return TaskDue.NotDue;
        return nowLocal - task.RunAtLocal > StaleTaskAge ? TaskDue.Stale : TaskDue.Run;
    }

    /// <summary>True when the routine should run now (see <see cref="DueOccurrence"/>).</summary>
    public static bool IsRoutineDue(VoiceRoutine routine, DateTime nowLocal, TimeZoneInfo? zone = null) =>
        DueOccurrence(routine, nowLocal, zone) is not null;

    /// <summary>
    /// The local time of the routine occurrence that is due now, or null. An occurrence is due from its time until
    /// <see cref="RoutineCatchUp"/> later (measured in real time, so DST shifts do not skew it), on a day in the mask,
    /// and only if <see cref="VoiceRoutine.LastRunDate"/> is before that day. Yesterday's occurrence counts too, so a
    /// 23:55 routine still runs when the PC wakes at 00:03. A time that falls in a DST gap runs when the clock jumps.
    /// After running, set <see cref="VoiceRoutine.LastRunDate"/> to <see cref="DateKey"/> of the returned value.
    /// </summary>
    public static DateTime? DueOccurrence(VoiceRoutine routine, DateTime nowLocal, TimeZoneInfo? zone = null)
    {
        if (!routine.Enabled || routine.Actions is not { Count: > 0 }) return null;
        if (ParseTimeOfDay(routine.TimeOfDay) is not { } tod) return null;
        var tz = zone ?? TimeZoneInfo.Local;
        DateTime nowUtc = ToUtc(nowLocal, tz);
        for (int back = 1; back >= 0; back--)
        {
            DateTime day = nowLocal.Date.AddDays(-back);
            if (!RunsOn(routine.DaysMask, day.DayOfWeek)) continue;
            if (string.CompareOrdinal(routine.LastRunDate ?? string.Empty, DateKey(day)) >= 0) continue;
            DateTime at = Effective(day + tod, tz);
            TimeSpan elapsed = nowUtc - ToUtc(at, tz);
            if (elapsed >= TimeSpan.Zero && elapsed <= RoutineCatchUp) return at;
        }
        return null;
    }

    /// <summary>
    /// The next local time the routine will run, for display: today's time while it has not run and is not yet
    /// missed (including the catch-up window), otherwise the next matching day. Null when disabled or invalid.
    /// </summary>
    public static DateTime? NextRunLocal(VoiceRoutine routine, DateTime now) => NextRunLocal(routine, now, null);

    /// <inheritdoc cref="NextRunLocal(VoiceRoutine, DateTime)"/>
    public static DateTime? NextRunLocal(VoiceRoutine routine, DateTime now, TimeZoneInfo? zone)
    {
        if (!routine.Enabled) return null;
        if (ParseTimeOfDay(routine.TimeOfDay) is not { } tod) return null;
        var tz = zone ?? TimeZoneInfo.Local;
        DateTime nowUtc = ToUtc(now, tz);
        for (int off = 0; off <= 7; off++)
        {
            DateTime day = now.Date.AddDays(off);
            if (!RunsOn(routine.DaysMask, day.DayOfWeek)) continue;
            if (string.CompareOrdinal(routine.LastRunDate ?? string.Empty, DateKey(day)) >= 0) continue;
            DateTime at = Effective(day + tod, tz);
            if (nowUtc - ToUtc(at, tz) <= RoutineCatchUp) return at;
        }
        return null;
    }

    /// <summary>A local time that does not exist (DST gap) moves to the end of the gap.</summary>
    private static DateTime Effective(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (!tz.IsInvalidTime(local)) return local;
        DateTime t = local;
        for (int i = 0; i < 240 && tz.IsInvalidTime(t); i++) t = t.AddMinutes(1);
        return t;
    }

    /// <summary>Local wall time to UTC; ambiguous times use the first (daylight) occurrence, gap times the gap end.</summary>
    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = Effective(local, tz);
        if (tz.IsAmbiguousTime(local))
        {
            var offsets = tz.GetAmbiguousTimeOffsets(local);
            return DateTime.SpecifyKind(local - offsets.Max(), DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}
