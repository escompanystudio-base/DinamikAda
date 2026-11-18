using System.Globalization;

namespace DinamikAda.Core.Widgets;

/// <summary>Pure, UI-free helpers shared by the Dock widget tiles (unit tested).</summary>
public static class DockWidgetMath
{
    public static double DayProgress(DateTime now) =>
        Math.Clamp(now.TimeOfDay.TotalSeconds / 86400.0, 0.0, 1.0);

    public static double MonthProgress(DateTime now)
    {
        int days = DateTime.DaysInMonth(now.Year, now.Month);
        double elapsed = (now.Day - 1) + now.TimeOfDay.TotalDays;
        return Math.Clamp(elapsed / days, 0.0, 1.0);
    }

    public static double YearProgress(DateTime now)
    {
        int days = DateTime.IsLeapYear(now.Year) ? 366 : 365;
        double elapsed = (now.DayOfYear - 1) + now.TimeOfDay.TotalDays;
        return Math.Clamp(elapsed / days, 0.0, 1.0);
    }

    /// <summary>Number of lit cells (0..cells) for a charge percentage; any charge above 0 lights one.</summary>
    public static int LitCells(int percent, int cells)
    {
        if (cells <= 0) return 0;
        percent = Math.Clamp(percent, 0, 100);
        if (percent == 0) return 0;
        return Math.Clamp((int)Math.Ceiling(percent / 100.0 * cells), 1, cells);
    }

    public static string FormatClock(DateTime time, bool use24h, bool withSeconds = false)
    {
        string fmt = use24h
            ? (withSeconds ? "HH:mm:ss" : "HH:mm")
            : (withSeconds ? "h:mm:ss" : "h:mm");
        return time.ToString(fmt, CultureInfo.InvariantCulture);
    }

    public static string AmPm(DateTime time) => time.Hour < 12 ? "AM" : "PM";

    /// <summary>"1h 27m", "12m", "45s" (TR: "1sa 27dk").</summary>
    public static string FormatDuration(TimeSpan span, string lang)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        bool en = lang == "en";
        if (span.TotalHours >= 1)
        {
            int h = (int)span.TotalHours;
            return en ? $"{h}h {span.Minutes}m" : $"{h}sa {span.Minutes}dk";
        }
        if (span.TotalMinutes >= 1)
            return en ? $"{(int)span.TotalMinutes}m" : $"{(int)span.TotalMinutes}dk";
        return en ? $"{span.Seconds}s" : $"{span.Seconds}sn";
    }

    /// <summary>mm:ss for under an hour, h:mm:ss otherwise.</summary>
    public static string FormatTimer(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }

    /// <summary>Position of <paramref name="now"/> between sunrise and sunset, 0..1, or null at night.</summary>
    public static double? SunProgress(DateTime now, DateTime sunrise, DateTime sunset)
    {
        if (sunset <= sunrise || now < sunrise || now > sunset) return null;
        return (now - sunrise).TotalSeconds / (sunset - sunrise).TotalSeconds;
    }

    /// <summary>Normalizes a series into 0..1 (flat series map to 0.5).</summary>
    public static double[] Normalize(IReadOnlyList<double> values)
    {
        var result = new double[values.Count];
        if (values.Count == 0) return result;
        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in values)
        {
            if (!double.IsFinite(v)) continue;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }
        if (min == double.MaxValue) return result;
        double range = max - min;
        for (int i = 0; i < values.Count; i++)
        {
            double v = double.IsFinite(values[i]) ? values[i] : min;
            result[i] = range < 1e-9 ? 0.5 : (v - min) / range;
        }
        return result;
    }

    public static double PercentChange(double from, double to) =>
        Math.Abs(from) < 1e-12 ? 0.0 : (to - from) / Math.Abs(from) * 100.0;

    /// <summary>"12.4 MB/s", "820 KB/s".</summary>
    public static string FormatRate(double megabytesPerSec)
    {
        if (!double.IsFinite(megabytesPerSec) || megabytesPerSec < 0) megabytesPerSec = 0;
        if (megabytesPerSec >= 100) return $"{megabytesPerSec:0} MB/s";
        if (megabytesPerSec >= 1) return $"{megabytesPerSec:0.0} MB/s";
        return $"{megabytesPerSec * 1024:0} KB/s";
    }

    /// <summary>Compact price: 64,210 / 1.234 / 0.0042.</summary>
    public static string FormatPrice(double value)
    {
        double a = Math.Abs(value);
        string fmt = a >= 1000 ? "#,0" : a >= 1 ? "0.00" : a >= 0.01 ? "0.0000" : "0.000000";
        return value.ToString(fmt, CultureInfo.InvariantCulture);
    }

    /// <summary>WMO weather interpretation code (Open-Meteo) to a short text.</summary>
    public static string WeatherText(int code, string lang)
    {
        bool en = lang == "en";
        return code switch
        {
            0 => en ? "Clear" : "Açık",
            1 => en ? "Mostly clear" : "Az bulutlu",
            2 => en ? "Partly cloudy" : "Parçalı bulutlu",
            3 => en ? "Overcast" : "Kapalı",
            45 or 48 => en ? "Fog" : "Sis",
            51 or 53 or 55 or 56 or 57 => en ? "Drizzle" : "Çisenti",
            61 or 63 or 65 or 66 or 67 => en ? "Rain" : "Yağmur",
            71 or 73 or 75 or 77 => en ? "Snow" : "Kar",
            80 or 81 or 82 => en ? "Showers" : "Sağanak",
            85 or 86 => en ? "Snow showers" : "Kar sağanağı",
            95 or 96 or 99 => en ? "Thunderstorm" : "Fırtına",
            _ => en ? "Unknown" : "Bilinmiyor"
        };
    }

    /// <summary>
    /// Weather symbol for a WMO code. Uses Unicode weather symbols (rendered through font fallback),
    /// because Segoe Fluent Icons has no complete weather set.
    /// </summary>
    public static string WeatherGlyph(int code, bool isDay = true) => code switch
    {
        0 => isDay ? "☀" : "☾",
        1 or 2 => isDay ? "⛅" : "☁",
        3 => "☁",
        45 or 48 => "▒",
        51 or 53 or 55 or 56 or 57 or 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "☔",
        71 or 73 or 75 or 77 or 85 or 86 => "❄",
        95 or 96 or 99 => "⚡",
        _ => "☁"
    };

    /// <summary>Clock-hand angles in degrees (0 = 12 o'clock) for hour, minute and second.</summary>
    public static (double Hour, double Minute, double Second) HandAngles(DateTime t)
    {
        double sec = t.Second + t.Millisecond / 1000.0;
        double min = t.Minute + sec / 60.0;
        double hour = (t.Hour % 12) + min / 60.0;
        return (hour * 30.0, min * 6.0, sec * 6.0);
    }

    /// <summary>"+3", "-5", "+5:30" offset label relative to local time.</summary>
    public static string FormatOffset(TimeSpan offsetFromLocal)
    {
        string sign = offsetFromLocal < TimeSpan.Zero ? "-" : "+";
        var a = offsetFromLocal.Duration();
        return a.Minutes == 0 ? $"{sign}{(int)a.TotalHours}" : $"{sign}{(int)a.TotalHours}:{a.Minutes:00}";
    }

    /// <summary>Resolves an IANA or Windows time-zone id; null when unknown.</summary>
    public static TimeZoneInfo? FindTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id.Trim(), out var win))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
        }
        return null;
    }

    /// <summary>"Europe/Istanbul" → "Istanbul", "America/New_York" → "New York".</summary>
    public static string CityFromZoneId(string zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId)) return string.Empty;
        string last = zoneId.Trim().Split('/').Last();
        return last.Replace('_', ' ');
    }
}
