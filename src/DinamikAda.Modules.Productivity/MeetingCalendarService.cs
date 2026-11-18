using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;

namespace DinamikAda.Modules.Productivity;

public sealed record MeetingOccurrence(
    string Summary,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? JoinUrl,
    bool IsAllDay = false);

public static partial class IcsCalendarParser
{
    [GeneratedRegex(@"https?://(?:meet\.google\.com/[^\s""'<>\\]+|teams\.microsoft\.com/l/meetup-join/[^\s""'<>\\]+|teams\.live\.com/meet/[^\s""'<>\\]+|(?:[a-z0-9-]+\.)?zoom\.us/(?:j|w|my)/[^\s""'<>\\]+|[a-z0-9.-]+\.webex\.com/[^\s""'<>\\]+|meet\.jit\.si/[^\s""'<>\\]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeetingUrlRegex();

    public static IReadOnlyList<MeetingOccurrence> ParseUpcomingFromIcs(
        string icsContent,
        DateTimeOffset nowUtc,
        TimeSpan lookaheadWindow)
    {
        if (string.IsNullOrWhiteSpace(icsContent)) return [];

        var unfolded = UnfoldLines(icsContent);
        var results = new List<MeetingOccurrence>();
        bool inEvent = false;
        int nestedDepth = 0;

        string? summary = null;
        string? rawDtStart = null;
        string? rawDtStartParams = null;
        string? rawDtEnd = null;
        string? rawDtEndParams = null;
        string? rrule = null;
        string? location = null;
        string? description = null;
        string? url = null;
        string? xConf = null;
        string? status = null;
        string? rawDuration = null;
        string? uid = null;
        string? rawRecurrenceId = null;
        string? rawRecurrenceIdParams = null;
        var excluded = new HashSet<DateTimeOffset>();
        // RFC 5545 RECURRENCE-ID overrides: (UID, original start) pairs that replace or cancel a master occurrence.
        var overriddenInstances = new HashSet<(string Uid, DateTimeOffset StartUtc)>();
        var masterRanges = new List<(string Uid, int Start, int Count)>();

        foreach (string line in unfolded)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = true;
                nestedDepth = 0;
                excluded.Clear();
                summary = rawDtStart = rawDtStartParams = rawDtEnd = rawDtEndParams = rrule = location = description = url = xConf = status = rawDuration = null;
                uid = rawRecurrenceId = rawRecurrenceIdParams = null;
                continue;
            }
            if (inEvent && line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { nestedDepth++; continue; }
            if (inEvent && nestedDepth > 0)
            {
                if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) nestedDepth--;
                continue;
            }

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                bool isOverride = false;
                if (inEvent && uid is not null && rawRecurrenceId is not null &&
                    TryParseIcsDateTime(rawRecurrenceId, rawRecurrenceIdParams, out var recurrenceIdUtc))
                {
                    overriddenInstances.Add((uid, recurrenceIdUtc));
                    isOverride = true;
                }

                if (inEvent && !string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase) && rawDtStart is not null &&
                    TryParseIcsDateTime(rawDtStart, rawDtStartParams, out var startUtc))
                {
                    DateTimeOffset endUtc;
                    if (rawDtEnd is not null && TryParseIcsDateTime(rawDtEnd, rawDtEndParams, out var parsedEnd) && parsedEnd > startUtc)
                    {
                        endUtc = parsedEnd;
                    }
                    else
                    {
                        TimeSpan fallbackDuration = rawDtStart.Length == 8 ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(30);
                        if (rawDuration is not null)
                        {
                            try
                            {
                                TimeSpan parsedDuration = System.Xml.XmlConvert.ToTimeSpan(rawDuration);
                                if (parsedDuration > TimeSpan.Zero && parsedDuration <= TimeSpan.FromDays(366)) fallbackDuration = parsedDuration;
                            }
                            catch (FormatException) { }
                            catch (OverflowException) { }
                        }
                        endUtc = startUtc <= DateTimeOffset.MaxValue - fallbackDuration ? startUtc + fallbackDuration : DateTimeOffset.MaxValue;
                    }

                    string? joinUrl = ExtractBestMeetingUrl(xConf, url, location, description);
                    string cleanSummary = UnescapeIcsText(summary ?? "Toplantı");
                    TimeSpan duration = endUtc - startUtc;
                    bool isAllDay = rawDtStart.Length == 8 ||
                        (rawDtStartParams?.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) == true &&
                         rawDtStartParams.IndexOf("VALUE=DATE-TIME", StringComparison.OrdinalIgnoreCase) < 0);

                    int before = results.Count;
                    ExpandOccurrences(
                        cleanSummary,
                        startUtc,
                        duration,
                        rrule,
                        joinUrl,
                        nowUtc,
                        nowUtc + lookaheadWindow,
                        results,
                        excluded,
                        rawDtStart.EndsWith('Z') ? null : ResolveTimeZone(rawDtStartParams) ?? TimeZoneInfo.Local,
                        isAllDay);
                    if (!isOverride && uid is not null && rrule is not null && results.Count > before)
                    {
                        masterRanges.Add((uid, before, results.Count - before));
                    }
                }
                inEvent = false;
                continue;
            }

            if (!inEvent) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string left = line[..colon];
            string value = line[(colon + 1)..];
            int semi = left.IndexOf(';');
            string propName = (semi >= 0 ? left[..semi] : left).Trim().ToUpperInvariant();
            string? propParams = semi >= 0 ? left[(semi + 1)..] : null;

            switch (propName)
            {
                case "SUMMARY": summary = value; break;
                case "DTSTART": rawDtStart = value; rawDtStartParams = propParams; break;
                case "DTEND": rawDtEnd = value; rawDtEndParams = propParams; break;
                case "RRULE": rrule = value; break;
                case "LOCATION": location = value; break;
                case "DESCRIPTION": description = value; break;
                case "URL": url = value; break;
                case "X-GOOGLE-CONFERENCE": xConf = value; break;
                case "STATUS": status = value; break;
                case "DURATION": rawDuration = value; break;
                case "UID": uid = value.Trim(); break;
                case "RECURRENCE-ID": rawRecurrenceId = value; rawRecurrenceIdParams = propParams; break;
                case "EXDATE":
                    foreach (string date in value.Split(','))
                        if (TryParseIcsDateTime(date, propParams, out var excludedDate)) excluded.Add(excludedDate);
                    break;
            }
        }

        if (overriddenInstances.Count > 0 && masterRanges.Count > 0)
        {
            var drop = new HashSet<int>();
            foreach (var (masterUid, start, count) in masterRanges)
            {
                for (int i = start; i < start + count; i++)
                {
                    if (overriddenInstances.Contains((masterUid, results[i].StartUtc))) drop.Add(i);
                }
            }
            if (drop.Count > 0)
            {
                results = results.Where((_, i) => !drop.Contains(i)).ToList();
            }
        }

        return results.OrderBy(o => o.StartUtc).ToList();
    }

    public static string? ExtractBestMeetingUrl(params string?[] fields)
    {
        foreach (string? field in fields)
        {
            if (string.IsNullOrWhiteSpace(field)) continue;
            string clean = UnescapeIcsText(field);
            var m = MeetingUrlRegex().Match(clean);
            if (m.Success)
            {
                return m.Value.TrimEnd('.', ',', ';', ')', '>');
            }
        }
        return null;
    }

    private static List<string> UnfoldLines(string raw)
    {
        var lines = new List<string>();
        foreach (string line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && lines.Count > 0)
            {
                lines[^1] += line[1..];
            }
            else if (!string.IsNullOrEmpty(line))
            {
                lines.Add(line);
            }
        }
        return lines;
    }

    private static bool TryParseIcsDateTime(string raw, string? propParams, out DateTimeOffset dto)
    {
        raw = raw.Trim();
        if (DateTime.TryParseExact(raw, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utcDt))
        {
            dto = new DateTimeOffset(utcDt, TimeSpan.Zero);
            return true;
        }
        if (DateTime.TryParseExact(raw, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDt))
        {
            var tz = ResolveTimeZone(propParams) ?? TimeZoneInfo.Local;
            dto = new DateTimeOffset(localDt, tz.GetUtcOffset(localDt)).ToUniversalTime();
            return true;
        }
        if (DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            dto = new DateTimeOffset(dateOnly, TimeZoneInfo.Local.GetUtcOffset(dateOnly)).ToUniversalTime();
            return true;
        }
        dto = default;
        return false;
    }

    private static TimeZoneInfo? ResolveTimeZone(string? propParams)
    {
        if (string.IsNullOrWhiteSpace(propParams)) return null;
        foreach (string p in propParams.Split(';'))
        {
            if (!p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase)) continue;
            string id = p["TZID=".Length..].Trim('"');
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        }
        return null;
    }

    private static void ExpandOccurrences(
        string summary,
        DateTimeOffset startUtc,
        TimeSpan duration,
        string? rrule,
        string? joinUrl,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        List<MeetingOccurrence> output,
        HashSet<DateTimeOffset> excluded,
        TimeZoneInfo? timeZone,
        bool isAllDay = false)
    {
        DateTimeOffset cutoffPast = windowStart - TimeSpan.FromMinutes(15);

        if (string.IsNullOrWhiteSpace(rrule))
        {
            if (!excluded.Contains(startUtc) && startUtc <= windowEnd && startUtc + duration >= cutoffPast)
            {
                output.Add(new MeetingOccurrence(summary, startUtc, startUtc + duration, joinUrl, isAllDay));
            }
            return;
        }

        string? freq = null;
        int interval = 1;
        int count = int.MaxValue;
        DateTimeOffset untilUtc = windowEnd;
        HashSet<DayOfWeek>? byDays = null;
        List<(int Ordinal, DayOfWeek Day)>? byDayOrdinals = null;
        HashSet<int>? byMonthDays = null;
        DayOfWeek weekStart = DayOfWeek.Monday;

        foreach (string part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string k = part[..eq].Trim().ToUpperInvariant();
            string v = part[(eq + 1)..].Trim();
            switch (k)
            {
                case "FREQ": freq = v.ToUpperInvariant(); break;
                case "INTERVAL" when int.TryParse(v, out int iv) && iv > 0: interval = iv; break;
                case "COUNT" when int.TryParse(v, out int c) && c > 0: count = c; break;
                case "BYDAY":
                    byDays = new HashSet<DayOfWeek>();
                    byDayOrdinals = new List<(int, DayOfWeek)>();
                    foreach (string token in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = token.Trim();
                        if (t.Length < 2) continue;
                        DayOfWeek? wd = ParseWeekDay(t[^2..]);
                        if (wd is null) continue;
                        string ordPart = t[..^2];
                        if (ordPart.Length == 0)
                        {
                            byDays.Add(wd.Value);
                        }
                        else if (int.TryParse(ordPart, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int ord) && ord != 0)
                        {
                            byDayOrdinals.Add((ord, wd.Value));
                        }
                    }
                    break;
                case "BYMONTHDAY":
                    byMonthDays = new HashSet<int>();
                    foreach (string token in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(token.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int md) && md != 0 && md is >= -31 and <= 31)
                            byMonthDays.Add(md);
                    }
                    break;
                case "WKST": weekStart = ParseWeekDay(v) ?? DayOfWeek.Monday; break;
                case "UNTIL" when TryParseIcsDateTime(v, null, out var u):
                    // A date-only UNTIL is inclusive of that whole day (RFC 5545 3.3.10).
                    if (v.Length == 8) u = u.AddDays(1).AddTicks(-1);
                    if (u < untilUtc) untilUtc = u;
                    break;
            }
        }

        if (freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
        {
            if (!excluded.Contains(startUtc) && startUtc <= windowEnd && startUtc + duration >= cutoffPast)
            {
                output.Add(new MeetingOccurrence(summary, startUtc, startUtc + duration, joinUrl, isAllDay));
            }
            return;
        }

        DateTime localStart = timeZone is null ? startUtc.UtcDateTime : TimeZoneInfo.ConvertTime(startUtc, timeZone).DateTime;
        DateTime localEnd = timeZone is null ? windowEnd.UtcDateTime : TimeZoneInfo.ConvertTime(windowEnd, timeZone).DateTime;
        DateTime anchorWeek = localStart.Date.AddDays(-((7 + (int)localStart.DayOfWeek - (int)weekStart) % 7));
        long daysInWindow = Math.Max(0, (localEnd.Date - localStart.Date).Days);
        int emitted = 0;
        for (long day = 0; day <= daysInWindow && emitted < count; day++)
        {
            DateTime local = localStart.AddDays(day);
            bool matches = freq switch
            {
                "DAILY" => day % interval == 0 && (byDays is not { Count: > 0 } || byDays.Contains(local.DayOfWeek)),
                "WEEKLY" => ((local.Date - anchorWeek).Days / 7) % interval == 0 &&
                    (byDays is { Count: > 0 } ? byDays.Contains(local.DayOfWeek) : local.DayOfWeek == localStart.DayOfWeek),
                "MONTHLY" => ((local.Year - localStart.Year) * 12L + local.Month - localStart.Month) % interval == 0 &&
                    MatchesMonthlyRule(local, localStart, byDays, byDayOrdinals, byMonthDays),
                "YEARLY" => (local.Year - localStart.Year) % interval == 0 && local.Month == localStart.Month && local.Day == localStart.Day,
                _ => false
            };
            if (!matches) continue;
            emitted++;
            if (timeZone?.IsInvalidTime(local) == true) continue;
            DateTimeOffset cursor = timeZone is null
                ? new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc))
                : new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone.GetUtcOffset(local)).ToUniversalTime();
            if (cursor > untilUtc || cursor > windowEnd) break;
            if (cursor > DateTimeOffset.MaxValue - duration) break;
            if (!excluded.Contains(cursor) && cursor + duration >= cutoffPast)
            {
                output.Add(new MeetingOccurrence(summary, cursor, cursor + duration, joinUrl, isAllDay));
            }
        }
    }

    private static bool MatchesMonthlyRule(
        DateTime local,
        DateTime localStart,
        HashSet<DayOfWeek>? byDays,
        List<(int Ordinal, DayOfWeek Day)>? byDayOrdinals,
        HashSet<int>? byMonthDays)
    {
        int daysInMonth = DateTime.DaysInMonth(local.Year, local.Month);
        bool anyRule = false;

        if (byMonthDays is { Count: > 0 })
        {
            anyRule = true;
            int fromEnd = local.Day - daysInMonth - 1; // last day of month => -1
            if (!byMonthDays.Contains(local.Day) && !byMonthDays.Contains(fromEnd)) return false;
        }

        bool hasPlainDays = byDays is { Count: > 0 };
        bool hasOrdinalDays = byDayOrdinals is { Count: > 0 };
        if (hasPlainDays || hasOrdinalDays)
        {
            anyRule = true;
            bool dayMatch = hasPlainDays && byDays!.Contains(local.DayOfWeek);
            if (!dayMatch && hasOrdinalDays)
            {
                int nth = (local.Day - 1) / 7 + 1;                      // 1 = first such weekday of month
                int nthFromEnd = -((daysInMonth - local.Day) / 7 + 1);  // -1 = last such weekday
                foreach (var (ord, wd) in byDayOrdinals!)
                {
                    if (wd == local.DayOfWeek && (ord == nth || ord == nthFromEnd))
                    {
                        dayMatch = true;
                        break;
                    }
                }
            }
            if (!dayMatch) return false;
        }

        return anyRule || local.Day == localStart.Day;
    }

    private static DayOfWeek? ParseWeekDay(string value) => value.Trim().ToUpperInvariant() switch
    {
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        "SU" => DayOfWeek.Sunday,
        _ => null
    };

    private static string UnescapeIcsText(string value) =>
        value.Replace(@"\,", ",").Replace(@"\;", ";").Replace(@"\n", " ").Replace(@"\N", " ").Replace(@"\\", @"\").Trim();
}

public sealed class MeetingCalendarService : ILiveActivitySource, IDisposable
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly object _sync = new();
    private readonly List<MeetingOccurrence> _upcomingCache = new();
    private Timer? _pollTimer;
    private string _icsSourcesConfig = string.Empty;
    private bool _enabled = true;
    private DateTimeOffset _lastFeedFetchUtc = DateTimeOffset.MinValue;
    // Last successful parse per remote feed (only touched under _refreshGate). A transient network
    // failure falls back to this instead of wiping an imminent meeting's countdown.
    private readonly Dictionary<string, List<MeetingOccurrence>> _lastGoodFeedResults = new(StringComparer.OrdinalIgnoreCase);
    private LiveActivityState? _currentActivity;
    private MeetingOccurrence? _activeOrNextMeeting;
    private bool _disposed;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private int _configVersion;
    private int _pollInFlight;
    private readonly HttpClient _http;

    public MeetingCalendarService(HttpClient? http = null) => _http = http ?? SharedHttp;

    public string SourceId => "meeting:calendar";
    public string Language { get; set; } = "tr";
    public LiveActivityState? CurrentActivity { get { lock (_sync) return _currentActivity; } }
    public MeetingOccurrence? ActiveOrNextMeeting { get { lock (_sync) return _activeOrNextMeeting; } }

    public event EventHandler<LiveActivityState?>? ActivityChanged;

    public void Configure(bool enabled, string? sources)
    {
        bool cleared = false;
        lock (_sync)
        {
            if (_disposed) return;
            string cleanSources = sources?.Trim() ?? string.Empty;
            if (_enabled == enabled && _icsSourcesConfig == cleanSources && (_pollTimer is not null || !enabled)) return;
            _enabled = enabled;
            _icsSourcesConfig = cleanSources;
            _configVersion++;
            _lastFeedFetchUtc = DateTimeOffset.MinValue;
            if (!enabled)
            {
                _upcomingCache.Clear();
                cleared = UpdateStateLocked(null, null);
                _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            else
            {
                _pollTimer ??= new Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
                _pollTimer.Change(100, 1000);
            }
        }
        if (cleared) ActivityChanged?.Invoke(this, null);
    }

    private async Task PollAsync()
    {
        if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0) return;
        try { await RefreshAndEvaluateAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Retry unavailable feeds on the next poll. */ }
        finally { Volatile.Write(ref _pollInFlight, 0); }
    }

    public async Task RefreshAndEvaluateAsync(CancellationToken ct = default)
    {
        CancellationToken lifetimeToken;
        lock (_sync)
        {
            if (_disposed) return;
            lifetimeToken = _lifetimeCts.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        await _refreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {

            bool enabled;
            string rawSources;
            bool needReloadFeeds;
            int version;
            var nowUtc = DateTimeOffset.UtcNow;

            lock (_sync)
            {
                enabled = _enabled;
                version = _configVersion;
                rawSources = _icsSourcesConfig;
                needReloadFeeds = (nowUtc - _lastFeedFetchUtc) >= TimeSpan.FromMinutes(2);
            }

            if (!enabled) return;

            if (needReloadFeeds)
            {
                var fresh = await LoadAllOccurrencesAsync(rawSources, nowUtc, linked.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_disposed || !_enabled || version != _configVersion) return;
                    _upcomingCache.Clear();
                    _upcomingCache.AddRange(fresh);
                    _lastFeedFetchUtc = nowUtc;
                }
            }

            EvaluateCountdown(DateTimeOffset.UtcNow);
        }
        finally { _refreshGate.Release(); }
    }

    public void EvaluateCountdown(DateTimeOffset nowUtc)
    {
        LiveActivityState? nextState = null;
        MeetingOccurrence? chosen = null;
        bool changed;

        lock (_sync)
        {
            if (_disposed) return;
            if (_enabled && _upcomingCache.Count > 0)
            {
                foreach (var occ in _upcomingCache)
                {
                    if (occ.EndUtc < nowUtc) continue;
                    // All-day entries (holidays, OOO, birthdays) are not meetings to count down to.
                    if (occ.IsAllDay) continue;
                    TimeSpan until = occ.StartUtc - nowUtc;
                    if (until <= TimeSpan.FromMinutes(10))
                    {
                        chosen = occ;
                        break;
                    }
                }
            }

            if (chosen is not null)
            {
                TimeSpan rem = chosen.StartUtc - nowUtc;
                string badge = rem > TimeSpan.Zero
                    ? $"{Strings.MeetingPrefix(Language)} • {(int)rem.TotalMinutes}:{(int)rem.Seconds:D2}"
                    : $"{Strings.MeetingPrefix(Language)} • {Strings.MeetingLive(Language)}";

                nextState = new LiveActivityState(
                    SourceId: SourceId,
                    Kind: ActivityKind.Meeting,
                    Priority: (int)ActivityKind.Meeting,
                    PrimaryText: badge,
                    SecondaryText: chosen.Summary,
                    AccentHex: rem <= TimeSpan.FromMinutes(2) ? "#FF9F0A" : "#30D158",
                    Progress01: rem > TimeSpan.Zero ? Math.Clamp(1.0 - rem.TotalSeconds / 600.0, 0.0, 1.0) : 1.0,
                    IsCritical: rem <= TimeSpan.FromMinutes(1),
                    IconGlyph: "\uE787",
                    TargetTab: ExpandedTabId.Now);
            }

            changed = UpdateStateLocked(nextState, chosen);
        }
        if (changed) ActivityChanged?.Invoke(this, nextState);
    }

    private bool UpdateStateLocked(LiveActivityState? nextState, MeetingOccurrence? chosen)
    {
        bool changed = _currentActivity != nextState || _activeOrNextMeeting != chosen;
        _currentActivity = nextState;
        _activeOrNextMeeting = chosen;
        return changed;
    }

    private async Task<List<MeetingOccurrence>> LoadAllOccurrencesAsync(
        string rawSources,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var all = new List<MeetingOccurrence>();
        var files = new List<string>();
        var urls = new List<string>();

        string localCalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinamikAda",
            "Calendar");
        if (Directory.Exists(localCalDir))
        {
            try { files.AddRange(Directory.EnumerateFiles(localCalDir, "*.ics", SearchOption.TopDirectoryOnly)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (string item in rawSources.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string s = item.Trim();
            if (s.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add("https://" + s["webcal://".Length..]);
            }
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(s);
            }
            else if (Directory.Exists(s))
            {
                try { files.AddRange(Directory.EnumerateFiles(s, "*.ics", SearchOption.TopDirectoryOnly)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else if (File.Exists(s))
            {
                files.Add(s);
            }
        }

        foreach (string f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string content = await File.ReadAllTextAsync(f, ct).ConfigureAwait(false);
                all.AddRange(IcsCalendarParser.ParseUpcomingFromIcs(content, nowUtc, TimeSpan.FromHours(12)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }

        foreach (string u in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string content = await _http.GetStringAsync(u, ct).ConfigureAwait(false);
                var parsed = IcsCalendarParser.ParseUpcomingFromIcs(content, nowUtc, TimeSpan.FromHours(12)).ToList();
                _lastGoodFeedResults[u] = parsed;
                all.AddRange(parsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                if (_lastGoodFeedResults.TryGetValue(u, out var cached))
                {
                    all.AddRange(cached.Where(o => o.EndUtc >= nowUtc));
                }
            }
        }

        return all.OrderBy(o => o.StartUtc).ToList();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}
