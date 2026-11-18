using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinamikAda.Core.Widgets;

/// <summary>One day of foreground usage: seconds per app and per hour of the day.</summary>
public sealed class ScreenTimeDay
{
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd (local)
    public Dictionary<string, double> AppSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double[] HourSeconds { get; set; } = new double[24];
    /// <summary>Kind of app (see <see cref="AppKindClassifier"/>) → seconds per hour of the day.</summary>
    public Dictionary<string, double[]> KindHourSeconds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public double TotalSeconds => HourSeconds.Sum();
}

/// <summary>
/// Local-only screen time ledger. Samples foreground usage into per-day buckets and keeps the
/// last <see cref="KeepDays"/> days. Pure model; persistence goes through <see cref="Serialize"/>.
/// </summary>
public sealed class ScreenTimeLedger
{
    public const int KeepDays = 8;
    /// <summary>A gap longer than this between samples is not counted (sleep, lock, crash).</summary>
    public static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly List<ScreenTimeDay> _days = new();

    public static string DateKey(DateTime local) => local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Adds <paramref name="seconds"/> of foreground time for <paramref name="app"/>, split across hour boundaries.</summary>
    public void AddSample(string app, DateTime endLocal, double seconds, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(app) || !double.IsFinite(seconds) || seconds <= 0) return;
        seconds = Math.Min(seconds, MaxSampleGap.TotalSeconds);
        lock (_sync)
        {
            DateTime cursor = endLocal.AddSeconds(-seconds);
            while (cursor < endLocal)
            {
                DateTime hourEnd = new DateTime(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0).AddHours(1);
                DateTime segEnd = hourEnd < endLocal ? hourEnd : endLocal;
                double seg = (segEnd - cursor).TotalSeconds;
                var day = GetOrCreateDay(cursor);
                day.HourSeconds[cursor.Hour] += seg;
                day.AppSeconds[app] = day.AppSeconds.TryGetValue(app, out var prev) ? prev + seg : seg;
                if (!string.IsNullOrWhiteSpace(kind))
                {
                    if (!day.KindHourSeconds.TryGetValue(kind, out var hours))
                    {
                        hours = new double[24];
                        day.KindHourSeconds[kind] = hours;
                    }
                    hours[cursor.Hour] += seg;
                }
                cursor = segEnd;
            }
            Prune(endLocal);
        }
    }

    public ScreenTimeDay? GetDay(DateTime local)
    {
        lock (_sync)
        {
            string key = DateKey(local);
            var d = _days.FirstOrDefault(x => x.Date == key);
            return d is null ? null : Clone(d);
        }
    }

    /// <summary>Total seconds for each of the 7 days ending today (oldest first).</summary>
    public double[] WeekTotals(DateTime todayLocal)
    {
        var result = new double[7];
        lock (_sync)
        {
            for (int i = 0; i < 7; i++)
            {
                string key = DateKey(todayLocal.Date.AddDays(i - 6));
                result[i] = _days.FirstOrDefault(x => x.Date == key)?.TotalSeconds ?? 0.0;
            }
        }
        return result;
    }

    /// <summary>Top apps by seconds, today or across the last 7 days.</summary>
    public IReadOnlyList<(string App, double Seconds)> TopApps(DateTime todayLocal, int count, bool week)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            for (int i = week ? 6 : 0; i >= 0; i--)
            {
                string key = DateKey(todayLocal.Date.AddDays(-i));
                var day = _days.FirstOrDefault(x => x.Date == key);
                if (day is null) continue;
                foreach (var kv in day.AppSeconds)
                    totals[kv.Key] = totals.TryGetValue(kv.Key, out var p) ? p + kv.Value : kv.Value;
            }
        }
        return totals.OrderByDescending(kv => kv.Value).Take(count).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public string Serialize()
    {
        lock (_sync)
        {
            return JsonSerializer.Serialize(_days);
        }
    }

    public static ScreenTimeLedger Deserialize(string? json, DateTime nowLocal)
    {
        var ledger = new ScreenTimeLedger();
        if (string.IsNullOrWhiteSpace(json)) return ledger;
        try
        {
            var days = JsonSerializer.Deserialize<List<ScreenTimeDay>>(json);
            if (days is null) return ledger;
            foreach (var d in days)
            {
                if (string.IsNullOrWhiteSpace(d.Date)) continue;
                if (d.HourSeconds is null || d.HourSeconds.Length != 24) d.HourSeconds = new double[24];
                d.AppSeconds = new Dictionary<string, double>(d.AppSeconds ?? new(), StringComparer.OrdinalIgnoreCase);
                d.KindHourSeconds = new Dictionary<string, double[]>(
                    (d.KindHourSeconds ?? new()).Where(kv => kv.Value is { Length: 24 }),
                    StringComparer.OrdinalIgnoreCase);
                ledger._days.Add(d);
            }
            ledger.Prune(nowLocal);
        }
        catch (JsonException)
        {
        }
        return ledger;
    }

    private ScreenTimeDay GetOrCreateDay(DateTime local)
    {
        string key = DateKey(local);
        var day = _days.FirstOrDefault(x => x.Date == key);
        if (day is null)
        {
            day = new ScreenTimeDay { Date = key };
            _days.Add(day);
        }
        return day;
    }

    private void Prune(DateTime nowLocal)
    {
        string cutoff = DateKey(nowLocal.Date.AddDays(-(KeepDays - 1)));
        _days.RemoveAll(d => string.CompareOrdinal(d.Date, cutoff) < 0);
    }

    private static ScreenTimeDay Clone(ScreenTimeDay d) => new()
    {
        Date = d.Date,
        AppSeconds = new Dictionary<string, double>(d.AppSeconds, StringComparer.OrdinalIgnoreCase),
        HourSeconds = (double[])d.HourSeconds.Clone(),
        KindHourSeconds = d.KindHourSeconds.ToDictionary(kv => kv.Key, kv => (double[])kv.Value.Clone(), StringComparer.OrdinalIgnoreCase)
    };
}

/// <summary>Buckets a process name into a coarse kind for the day timeline.</summary>
public static class AppKindClassifier
{
    public const string Work = "work";
    public const string Browser = "browser";
    public const string Chat = "chat";
    public const string Media = "media";
    public const string Other = "other";

    private static readonly string[] WorkApps =
    [
        "code", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "clion64", "goland64", "studio64",
        "windowsterminal", "wt", "powershell", "pwsh", "cmd", "notepad", "notepad++", "sublime_text", "winword",
        "excel", "powerpnt", "onenote", "obsidian", "notion", "figma", "photoshop", "illustrator", "blender",
        "unity", "unity hub", "godot", "cursor", "windsurf", "antigravity", "acrobat", "explorer"
    ];
    private static readonly string[] BrowserApps = ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen"];
    private static readonly string[] ChatApps =
        ["discord", "teams", "ms-teams", "slack", "whatsapp", "whatsapp.root", "telegram", "zoom", "skype", "signal", "outlook", "thunderbird"];
    private static readonly string[] MediaApps =
        ["spotify", "vlc", "mpc-hc64", "potplayermini64", "steam", "steamwebhelper", "epicgameslauncher", "netflix", "youtube music", "applemusic"];

    public static string Classify(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return Other;
        string p = processName.Trim();
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = p[..^4];
        if (Matches(WorkApps, p)) return Work;
        if (Matches(BrowserApps, p)) return Browser;
        if (Matches(ChatApps, p)) return Chat;
        if (Matches(MediaApps, p)) return Media;
        return Other;
    }

    private static bool Matches(string[] list, string p)
    {
        foreach (var x in list)
        {
            if (string.Equals(x, p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
