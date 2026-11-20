using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DinamikAda.Core.Accounts;

/// <summary>DeepSeek balance (one entry per currency).</summary>
public sealed record DeepSeekBalance(bool IsAvailable, IReadOnlyList<DeepSeekBalanceItem> Items)
{
    public DeepSeekBalanceItem? Primary => Items.FirstOrDefault(i => i.Currency == "USD") ?? Items.FirstOrDefault();
}

public sealed record DeepSeekBalanceItem(string Currency, double Total, double Granted, double ToppedUp);

/// <summary>A YouTube channel's public statistics.</summary>
public sealed record YouTubeChannel(string Id, string Title, string? Handle, long Subscribers, long Views, long Videos,
    bool HiddenSubscribers, string? ThumbnailUrl);

/// <summary>Solved / total problems of one LeetCode difficulty.</summary>
public sealed record LeetCodeDifficulty(int Solved, int Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Solved / Total, 0, 1);
}

/// <summary>A LeetCode user's solved problems per difficulty and global ranking.</summary>
public sealed record LeetCodeStats(string Username, int Ranking, LeetCodeDifficulty Easy, LeetCodeDifficulty Medium,
    LeetCodeDifficulty Hard)
{
    public int TotalSolved => Easy.Solved + Medium.Solved + Hard.Solved;
    public int TotalQuestions => Easy.Total + Medium.Total + Hard.Total;
}

/// <summary>A random Unsplash photo (GET /photos/random).</summary>
public sealed record UnsplashPhoto(string Id, string SmallUrl, string RegularUrl, string FullUrl, string UserName,
    string UserLink, string PhotoLink, string DownloadLocation, string? Color, string? Description);

/// <summary>Pure JSON/text parsers for the social, mail, calendar, AI and app account widgets (unit tested).</summary>
public static class SocialMailParsers
{
    /// <summary>Parses GET https://api.deepseek.com/user/balance.</summary>
    public static DeepSeekBalance ParseDeepSeekBalance(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        bool available = root.TryGetProperty("is_available", out var a) && a.ValueKind == JsonValueKind.True;
        var items = new List<DeepSeekBalanceItem>();
        if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in infos.EnumerateArray())
            {
                items.Add(new DeepSeekBalanceItem(
                    i.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD",
                    Num(i, "total_balance"),
                    Num(i, "granted_balance"),
                    Num(i, "topped_up_balance")));
            }
        }
        return new DeepSeekBalance(available, items);
    }

    /// <summary>Reads a number that may be sent as a JSON number or a numeric string.</summary>
    public static double Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0
        };
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonElement Obj(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : default;

    // ---- YouTube ----------------------------------------------------------------------------------

    /// <summary>
    /// The channel selector query part for /youtube/v3/channels: <c>id=UC…</c> for channel IDs, otherwise
    /// <c>forHandle=@handle</c> (an @ is prepended when missing). Accepts youtube.com/@x and /channel/UC… links.
    /// </summary>
    public static string YouTubeChannelSelector(string channel)
    {
        string c = channel.Trim();
        int at = c.IndexOf("youtube.com/", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            c = c[(at + "youtube.com/".Length)..];
            if (c.StartsWith("channel/", StringComparison.OrdinalIgnoreCase)) c = c["channel/".Length..];
            int cut = c.IndexOfAny(['/', '?', '#']);
            if (cut >= 0) c = c[..cut];
        }
        if (c.StartsWith("UC", StringComparison.Ordinal) && c.Length == 24 && !c.Contains(' '))
            return "id=" + Uri.EscapeDataString(c);
        if (!c.StartsWith('@')) c = "@" + c;
        return "forHandle=" + Uri.EscapeDataString(c);
    }

    /// <summary>Parses /youtube/v3/channels?part=snippet,statistics; null when no channel matched.</summary>
    public static YouTubeChannel? ParseYouTubeChannel(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return null;
        var ch = items[0];
        var snippet = Obj(ch, "snippet");
        var stats = Obj(ch, "statistics");
        string? thumb = null;
        var thumbs = Obj(snippet, "thumbnails");
        foreach (var size in new[] { "medium", "high", "default" })
        {
            thumb = Str(Obj(thumbs, size), "url");
            if (thumb is not null) break;
        }
        bool hidden = stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty("hiddenSubscriberCount", out var h) &&
                      h.ValueKind == JsonValueKind.True;
        return new YouTubeChannel(
            Str(ch, "id") ?? string.Empty,
            Str(snippet, "title") ?? "YouTube",
            Str(snippet, "customUrl"),
            stats.ValueKind == JsonValueKind.Object ? (long)Num(stats, "subscriberCount") : 0,
            stats.ValueKind == JsonValueKind.Object ? (long)Num(stats, "viewCount") : 0,
            stats.ValueKind == JsonValueKind.Object ? (long)Num(stats, "videoCount") : 0,
            hidden,
            thumb);
    }

    /// <summary>The <c>error.message</c> of a Google API error body, or null.</summary>
    public static string? ParseGoogleApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Str(Obj(doc.RootElement, "error"), "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- LeetCode ---------------------------------------------------------------------------------

    public const string LeetCodeQuery =
        "query($u:String!){ allQuestionsCount{difficulty count} matchedUser(username:$u){ username profile{ranking} submitStatsGlobal{acSubmissionNum{difficulty count}} } }";

    /// <summary>The JSON body for POST https://leetcode.com/graphql.</summary>
    public static string LeetCodeRequestBody(string user) =>
        JsonSerializer.Serialize(new { query = LeetCodeQuery, variables = new { u = user.Trim() } });

    /// <summary>Parses the LeetCode GraphQL reply; null when the user does not exist.</summary>
    public static LeetCodeStats? ParseLeetCode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = Obj(doc.RootElement, "data");
        var user = Obj(data, "matchedUser");
        if (user.ValueKind != JsonValueKind.Object) return null;

        static Dictionary<string, int> Counts(JsonElement arr)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (arr.ValueKind != JsonValueKind.Array) return d;
            foreach (var e in arr.EnumerateArray())
            {
                string? k = Str(e, "difficulty");
                if (k is not null) d[k] = (int)Num(e, "count");
            }
            return d;
        }

        var totals = Counts(data.TryGetProperty("allQuestionsCount", out var aq) ? aq : default);
        var solvedArr = Obj(user, "submitStatsGlobal");
        var solved = Counts(solvedArr.ValueKind == JsonValueKind.Object && solvedArr.TryGetProperty("acSubmissionNum", out var ac) ? ac : default);
        LeetCodeDifficulty D(string k) => new(solved.GetValueOrDefault(k), totals.GetValueOrDefault(k));
        return new LeetCodeStats(
            Str(user, "username") ?? string.Empty,
            (int)Num(Obj(user, "profile"), "ranking"),
            D("Easy"), D("Medium"), D("Hard"));
    }

    // ---- Unsplash ---------------------------------------------------------------------------------

    /// <summary>Parses GET /photos/random (a single photo object); null when the essential URLs are missing.</summary>
    public static UnsplashPhoto? ParseUnsplashPhoto(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.ValueKind != JsonValueKind.Object) return null;
        var urls = Obj(r, "urls");
        var links = Obj(r, "links");
        var user = Obj(r, "user");
        string? id = Str(r, "id");
        string? small = Str(urls, "small");
        string? regular = Str(urls, "regular") ?? small;
        string? full = Str(urls, "full") ?? regular;
        if (id is null || small is null || regular is null || full is null) return null;
        return new UnsplashPhoto(id, small, regular, full,
            Str(user, "name") ?? Str(user, "username") ?? "Unsplash",
            Str(Obj(user, "links"), "html") ?? "https://unsplash.com",
            Str(links, "html") ?? "https://unsplash.com",
            Str(links, "download_location") ?? string.Empty,
            Str(r, "color"),
            Str(r, "description") ?? Str(r, "alt_description"));
    }

    /// <summary>Adds the referral parameters Unsplash's guidelines require on attribution links.</summary>
    public static string WithUnsplashReferral(string url) =>
        url + (url.Contains('?') ? "&" : "?") + "utm_source=dinamikada&utm_medium=referral";

    /// <summary>A file name for a downloaded wallpaper that cannot escape its folder.</summary>
    public static string UnsplashWallpaperFileName(string id)
    {
        var sb = new StringBuilder();
        foreach (char ch in id)
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_') sb.Append(ch);
        return "unsplash-" + (sb.Length == 0 ? "photo" : sb.ToString()) + ".jpg";
    }
}

// ==== IMAP ===============================================================================================

/// <summary>Counts from <c>STATUS INBOX (UNSEEN MESSAGES)</c>.</summary>
public sealed record ImapMailboxStatus(int Messages, int Unseen);

/// <summary>One message header block from <c>FETCH … BODY.PEEK[HEADER.FIELDS (FROM SUBJECT DATE)]</c>.</summary>
public sealed record ImapMessageHeader(int Sequence, string FromName, string FromAddress, string Subject, DateTimeOffset? Date);

/// <summary>Pure IMAP response parsing for the Gmail widget (the socket I/O lives in the app).</summary>
public static class ImapParsers
{
    static ImapParsers()
    {
        // windows-1254, ISO-8859-9, KOI8-R… in encoded-words.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>An IMAP quoted string: backslash and double quote escaped, CR/LF removed.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char ch in s)
        {
            if (ch is '\r' or '\n') continue;
            if (ch is '\\' or '"') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Index just past the CRLF of the line tagged <paramref name="tag"/>, or -1 when the response is not complete yet.
    /// Literals (<c>{n}</c> at a line end) are skipped by byte count so their content never ends the response.
    /// </summary>
    public static int FindTaggedEnd(ReadOnlySpan<byte> data, string tag)
    {
        byte[] prefix = Encoding.ASCII.GetBytes(tag + " ");
        int pos = 0;
        while (pos < data.Length)
        {
            int lineStart = pos;
            // A logical line can span several physical lines when it contains literals.
            while (true)
            {
                int crlf = IndexOfCrlf(data, pos);
                if (crlf < 0) return -1;
                int literal = LiteralLength(data[pos..crlf]);
                pos = crlf + 2;
                if (literal < 0) break;
                if (pos + literal > data.Length) return -1;
                pos += literal;
            }
            if (data.Length - lineStart >= prefix.Length && data.Slice(lineStart, prefix.Length).SequenceEqual(prefix)) return pos;
        }
        return -1;
    }

    private static int IndexOfCrlf(ReadOnlySpan<byte> data, int from)
    {
        for (int i = from; i + 1 < data.Length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n') return i;
        return -1;
    }

    /// <summary>n when the line ends with {n} or {n+}, else -1.</summary>
    private static int LiteralLength(ReadOnlySpan<byte> line)
    {
        if (line.Length < 3 || line[^1] != '}') return -1;
        int open = line.LastIndexOf((byte)'{');
        if (open < 0) return -1;
        var inner = line[(open + 1)..^1];
        if (inner.Length > 0 && inner[^1] == '+') inner = inner[..^1];
        if (inner.Length is 0 or > 9) return -1;
        int n = 0;
        foreach (byte b in inner)
        {
            if (b is < (byte)'0' or > (byte)'9') return -1;
            n = n * 10 + (b - '0');
        }
        return n;
    }

    /// <summary>The status word (OK/NO/BAD) and text of the tagged line, or ("", "") when absent.</summary>
    public static (string Status, string Text) TaggedResult(string response, string tag)
    {
        foreach (var line in Lines(response))
        {
            if (!line.StartsWith(tag + " ", StringComparison.Ordinal)) continue;
            string rest = line[(tag.Length + 1)..];
            int sp = rest.IndexOf(' ');
            return sp < 0 ? (rest.ToUpperInvariant(), string.Empty) : (rest[..sp].ToUpperInvariant(), rest[(sp + 1)..]);
        }
        return (string.Empty, string.Empty);
    }

    private static IEnumerable<string> Lines(string s) => s.Split('\n').Select(l => l.TrimEnd('\r'));

    private static readonly Regex StatusRx = new(@"^\* STATUS .*?\((?<items>[^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Parses the untagged <c>* STATUS INBOX (MESSAGES n UNSEEN m)</c> line.</summary>
    public static ImapMailboxStatus ParseStatus(string response)
    {
        int messages = 0, unseen = 0;
        foreach (var line in Lines(response))
        {
            var m = StatusRx.Match(line);
            if (!m.Success) continue;
            var parts = m.Groups["items"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                if (!int.TryParse(parts[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int v)) continue;
                if (parts[i].Equals("MESSAGES", StringComparison.OrdinalIgnoreCase)) messages = v;
                else if (parts[i].Equals("UNSEEN", StringComparison.OrdinalIgnoreCase)) unseen = v;
            }
        }
        return new ImapMailboxStatus(messages, unseen);
    }

    /// <summary>Message sequence numbers from every <c>* SEARCH …</c> line.</summary>
    public static IReadOnlyList<int> ParseSearch(string response)
    {
        var ids = new List<int>();
        foreach (var line in Lines(response))
        {
            if (!line.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var t in line[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>The <paramref name="count"/> highest ids, newest first.</summary>
    public static IReadOnlyList<int> Newest(IEnumerable<int> ids, int count) =>
        ids.Distinct().OrderByDescending(i => i).Take(count).ToList();

    private static readonly Regex FetchRx = new(@"^\* (?<seq>\d+) FETCH ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Parses a FETCH response whose header blocks arrive as literals; newest (highest sequence) first.</summary>
    public static IReadOnlyList<ImapMessageHeader> ParseFetchHeaders(ReadOnlySpan<byte> data)
    {
        var result = new List<ImapMessageHeader>();
        int pos = 0;
        int currentSeq = -1;
        while (pos < data.Length)
        {
            int crlf = IndexOfCrlf(data, pos);
            if (crlf < 0) break;
            var line = data[pos..crlf];
            string text = Encoding.Latin1.GetString(line);
            var m = FetchRx.Match(text);
            if (m.Success) currentSeq = int.Parse(m.Groups["seq"].Value, CultureInfo.InvariantCulture);
            int literal = LiteralLength(line);
            pos = crlf + 2;
            if (literal < 0) continue;
            int len = Math.Min(literal, data.Length - pos);
            string block = Encoding.UTF8.GetString(data.Slice(pos, len));
            pos += len;
            if (currentSeq < 0) continue;
            var h = ParseHeaderBlock(block);
            var (name, addr) = ParseFrom(DecodeEncodedWords(h.GetValueOrDefault("from") ?? string.Empty));
            result.Add(new ImapMessageHeader(currentSeq, name, addr,
                DecodeEncodedWords(h.GetValueOrDefault("subject") ?? string.Empty).Trim(),
                ParseDate(h.GetValueOrDefault("date") ?? string.Empty)));
        }
        return result.OrderByDescending(r => r.Sequence).ToList();
    }

    /// <summary>Header fields (lower-case names) with folded continuation lines joined.</summary>
    public static Dictionary<string, string> ParseHeaderBlock(string block)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        var value = new StringBuilder();
        void Flush()
        {
            if (name is not null && !d.ContainsKey(name)) d[name] = value.ToString().Trim();
            name = null;
            value.Clear();
        }
        foreach (var line in Lines(block))
        {
            if (line.Length == 0) { Flush(); continue; }
            if (line[0] is ' ' or '\t')
            {
                if (name is not null) value.Append(' ').Append(line.Trim());
                continue;
            }
            Flush();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            name = line[..colon].Trim().ToLowerInvariant();
            value.Append(line[(colon + 1)..].Trim());
        }
        Flush();
        return d;
    }

    private static readonly Regex EncodedWordRx = new(@"=\?(?<cs>[^?\s]+)\?(?<enc>[BbQq])\?(?<text>[^?\s]*)\?=",
        RegexOptions.CultureInvariant);
    private static readonly Regex BetweenWordsRx = new(@"(\?=)\s+(=\?)", RegexOptions.CultureInvariant);

    /// <summary>Decodes RFC 2047 encoded-words (B and Q, any registered charset); bad words are left as-is.</summary>
    public static string DecodeEncodedWords(string s)
    {
        if (!s.Contains("=?", StringComparison.Ordinal)) return s;
        // Whitespace between adjacent encoded-words is not displayed.
        s = BetweenWordsRx.Replace(s, "$1$2");
        return EncodedWordRx.Replace(s, m =>
        {
            try
            {
                string cs = m.Groups["cs"].Value;
                int star = cs.IndexOf('*'); // RFC 2231 language suffix
                if (star >= 0) cs = cs[..star];
                var enc = Encoding.GetEncoding(cs);
                string text = m.Groups["text"].Value;
                byte[] bytes = m.Groups["enc"].Value is "B" or "b" ? Convert.FromBase64String(text) : DecodeQ(text);
                return enc.GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                return m.Value;
            }
        });
    }

    private static byte[] DecodeQ(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '_') bytes.Add((byte)' ');
            else if (ch == '=' && i + 2 < text.Length &&
                     byte.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            {
                bytes.Add(b);
                i += 2;
            }
            else if (ch > 0x7F) bytes.AddRange(Encoding.UTF8.GetBytes(ch.ToString())); // invalid but seen in the wild
            else bytes.Add((byte)ch);
        }
        return bytes.ToArray();
    }

    /// <summary>Display name and address of a From header; the name falls back to the address's local part.</summary>
    public static (string Name, string Address) ParseFrom(string from)
    {
        string f = from.Trim();
        string name, addr;
        int lt = f.LastIndexOf('<');
        int gt = f.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
        {
            addr = f[(lt + 1)..gt].Trim();
            name = f[..lt].Trim().Trim('"').Replace("\\\"", "\"").Trim();
        }
        else
        {
            addr = f;
            name = string.Empty;
        }
        if (name.Length == 0)
        {
            int at = addr.IndexOf('@');
            name = at > 0 ? addr[..at] : addr;
        }
        return (name, addr);
    }

    private static readonly string[] DateFormats =
    [
        "ddd, d MMM yyyy H:mm:ss zzz", "d MMM yyyy H:mm:ss zzz", "ddd, d MMM yyyy H:mm zzz", "d MMM yyyy H:mm zzz",
    ];

    /// <summary>Parses an RFC 5322 date ("Thu, 19 Nov 2026 09:14:03 +0300", optional comment, GMT/UT).</summary>
    public static DateTimeOffset? ParseDate(string s)
    {
        string t = s.Trim();
        int paren = t.IndexOf('(');
        if (paren >= 0) t = t[..paren].Trim();
        t = Regex.Replace(t, @"\s+", " ");
        // "+0300" → "+03:00"; GMT/UT/Z → +00:00.
        t = Regex.Replace(t, @" ([+-])(\d{2})(\d{2})$", " $1$2:$3");
        t = Regex.Replace(t, @" (GMT|UT|UTC|Z)$", " +00:00", RegexOptions.IgnoreCase);
        return DateTimeOffset.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)
            ? d
            : null;
    }
}

// ==== iCalendar =========================================================================================

/// <summary>One calendar event occurrence in local time. All-day events run from midnight to midnight.</summary>
public sealed record CalendarEvent(string Title, string? Location, DateTime Start, DateTime End, bool AllDay);

/// <summary>A minimal iCalendar (RFC 5545) reader: VEVENTs with time zones, basic RRULEs, EXDATE and overrides.</summary>
public static class ICalParser
{
    private const int MaxIterations = 50_000;

    private sealed record Prop(string Name, Dictionary<string, string> Params, string Value);

    /// <summary>A date-time as written: wall time plus its zone (null = floating, Utc for "Z").</summary>
    private readonly record struct ICalTime(DateTime Wall, TimeZoneInfo? Zone, bool DateOnly);

    private sealed class VEvent
    {
        public string? Uid, Summary, Location, Status, RRule;
        public ICalTime? Start, End, RecurrenceId;
        public TimeSpan? Duration;
        public readonly List<ICalTime> ExDates = new();
    }

    /// <summary>Occurrences overlapping [<paramref name="from"/>, <paramref name="to"/>) in <paramref name="local"/> time, sorted.</summary>
    public static IReadOnlyList<CalendarEvent> Parse(string ics, DateTime from, DateTime to, TimeZoneInfo local)
    {
        var events = ReadEvents(ics);
        var result = new List<CalendarEvent>();
        // Instances moved or cancelled by a RECURRENCE-ID override are dropped from their master's expansion.
        var overrides = events.Where(e => e.RecurrenceId is not null && e.Uid is not null)
            .GroupBy(e => e.Uid!)
            .ToDictionary(g => g.Key, g => g.Select(e => ToLocal(e.RecurrenceId!.Value, local)).ToList(), StringComparer.Ordinal);

        foreach (var e in events)
        {
            if (e.Start is not { } start) continue;
            if (string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            TimeSpan length = e.End is { } end ? end.Wall - start.Wall
                : e.Duration ?? (start.DateOnly ? TimeSpan.FromDays(1) : TimeSpan.Zero);
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            string title = string.IsNullOrWhiteSpace(e.Summary) ? string.Empty : e.Summary!.Trim();
            string? location = string.IsNullOrWhiteSpace(e.Location) ? null : e.Location;

            var excluded = e.ExDates.Select(x => ToLocal(x, local)).ToList();
            if (e.RecurrenceId is null && e.Uid is not null && overrides.TryGetValue(e.Uid, out var moved)) excluded.AddRange(moved);

            IEnumerable<DateTime> walls = e.RRule is not null && e.RecurrenceId is null
                ? Expand(start, e.RRule, to, local)
                : [start.Wall];
            foreach (var wall in walls)
            {
                var occStart = ToLocal(start with { Wall = wall }, local);
                var occEnd = ToLocal(start with { Wall = wall + length }, local);
                if (occEnd <= from && !(length == TimeSpan.Zero && occStart >= from)) continue;
                if (occStart >= to) continue;
                if (excluded.Any(x => start.DateOnly ? x.Date == occStart.Date : Math.Abs((x - occStart).TotalSeconds) < 60)) continue;
                result.Add(new CalendarEvent(title, location, occStart, occEnd, start.DateOnly));
            }
        }
        return result.OrderBy(r => r.Start).ThenBy(r => r.Title, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Events not yet ended at <paramref name="now"/> (in-progress ones included), soonest first.</summary>
    public static IReadOnlyList<CalendarEvent> Upcoming(IEnumerable<CalendarEvent> events, DateTime now, int max) =>
        events.Where(e => e.End > now || (e.End == e.Start && e.Start >= now)).OrderBy(e => e.Start).Take(max).ToList();

    // ---- Reading ------------------------------------------------------------------------------------

    private static List<VEvent> ReadEvents(string ics)
    {
        var list = new List<VEvent>();
        VEvent? cur = null;
        int depth = 0; // nested components inside a VEVENT (VALARM)
        foreach (var raw in Unfold(ics))
        {
            var p = ParseLine(raw);
            if (p is null) continue;
            if (p.Name == "BEGIN")
            {
                if (cur is not null) depth++;
                else if (p.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) cur = new VEvent();
                continue;
            }
            if (p.Name == "END")
            {
                if (cur is null) continue;
                if (depth > 0) { depth--; continue; }
                list.Add(cur);
                cur = null;
                continue;
            }
            if (cur is null || depth > 0) continue;
            switch (p.Name)
            {
                case "UID": cur.Uid = p.Value; break;
                case "SUMMARY": cur.Summary = Unescape(p.Value); break;
                case "LOCATION": cur.Location = Unescape(p.Value); break;
                case "STATUS": cur.Status = p.Value.Trim(); break;
                case "RRULE": cur.RRule = p.Value; break;
                case "DTSTART": cur.Start = ParseTime(p); break;
                case "DTEND": cur.End = ParseTime(p); break;
                case "RECURRENCE-ID": cur.RecurrenceId = ParseTime(p); break;
                case "DURATION": cur.Duration = ParseDuration(p.Value); break;
                case "EXDATE":
                    foreach (var v in p.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (ParseTime(p with { Value = v }) is { } x) cur.ExDates.Add(x);
                    break;
            }
        }
        return list;
    }

    private static IEnumerable<string> Unfold(string ics)
    {
        var sb = new StringBuilder();
        foreach (var line in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                sb.Append(line, 1, line.Length - 1);
                continue;
            }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(line);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static Prop? ParseLine(string line)
    {
        bool quoted = false;
        int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;
        var head = line[..colon].Split(';');
        var pars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < head.Length; i++)
        {
            int eq = head[i].IndexOf('=');
            if (eq > 0) pars[head[i][..eq].Trim()] = head[i][(eq + 1)..].Trim().Trim('"');
        }
        return new Prop(head[0].Trim().ToUpperInvariant(), pars, line[(colon + 1)..]);
    }

    private static string Unescape(string v)
    {
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                char n = v[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else sb.Append(v[i]);
        }
        return sb.ToString().Trim();
    }

    private static ICalTime? ParseTime(Prop p)
    {
        string v = p.Value.Trim();
        bool dateOnly = (p.Params.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || v.Length == 8;
        if (dateOnly)
        {
            return DateTime.TryParseExact(v.Length >= 8 ? v[..8] : v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? new ICalTime(d, null, true)
                : null;
        }
        bool utc = v.EndsWith('Z');
        if (utc) v = v[..^1];
        if (!DateTime.TryParseExact(v, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        t = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
        TimeZoneInfo? zone = utc ? TimeZoneInfo.Utc
            : p.Params.TryGetValue("TZID", out var tzid) ? FindZone(tzid) : null;
        return new ICalTime(t, zone, false);
    }

    /// <summary>Resolves a TZID (IANA or Windows id); null (= floating local time) when unknown.</summary>
    public static TimeZoneInfo? FindZone(string tzid)
    {
        string id = tzid.Trim().Trim('"');
        if (id.Length == 0) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(win!); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return null;
    }

    private static DateTime ToLocal(ICalTime t, TimeZoneInfo local)
    {
        if (t.DateOnly || t.Zone is null) return DateTime.SpecifyKind(t.Wall, DateTimeKind.Unspecified);
        try
        {
            var src = DateTime.SpecifyKind(t.Wall, DateTimeKind.Unspecified);
            if (t.Zone.IsInvalidTime(src)) src = src.AddHours(1); // skipped by a DST jump
            var utc = TimeZoneInfo.ConvertTimeToUtc(src, t.Zone);
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, local), DateTimeKind.Unspecified);
        }
        catch (ArgumentException)
        {
            return t.Wall;
        }
    }

    private static readonly Regex DurationRx = new(@"^(?<neg>[+-])?P(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static TimeSpan? ParseDuration(string v)
    {
        var m = DurationRx.Match(v.Trim());
        if (!m.Success) return null;
        int G(string n) => m.Groups[n].Success ? int.Parse(m.Groups[n].Value, CultureInfo.InvariantCulture) : 0;
        var ts = new TimeSpan(G("w") * 7 + G("d"), G("h"), G("m"), G("s"));
        return m.Groups["neg"].Value == "-" ? -ts : ts;
    }

    // ---- Recurrence ---------------------------------------------------------------------------------

    /// <summary>Wall-clock starts (in the event's own zone) of a recurring event, up to <paramref name="toLocal"/>.</summary>
    private static IEnumerable<DateTime> Expand(ICalTime start, string rrule, DateTime toLocal, TimeZoneInfo local)
    {
        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .GroupBy(x => x[0].Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.Ordinal);
        string freq = parts.GetValueOrDefault("FREQ", string.Empty).ToUpperInvariant();
        int interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), NumberStyles.None, CultureInfo.InvariantCulture, out int iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), NumberStyles.None, CultureInfo.InvariantCulture, out int c) ? c : null;
        DateTime? untilLocal = null;
        if (parts.TryGetValue("UNTIL", out var u) && ParseTime(new Prop("UNTIL", new(StringComparer.OrdinalIgnoreCase), u)) is { } ut)
        {
            // A date-only UNTIL includes that whole day.
            untilLocal = ut.DateOnly ? ut.Wall.Date.AddDays(1).AddTicks(-1) : ToLocal(ut, local);
        }

        var byDay = new List<DayOfWeek>();
        if (freq == "WEEKLY" && parts.TryGetValue("BYDAY", out var bd))
        {
            foreach (var d in bd.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string code = new string(d.Where(char.IsLetter).ToArray()).ToUpperInvariant();
                DayOfWeek? dow = code switch
                {
                    "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
                    "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null
                };
                if (dow is { } x && !byDay.Contains(x)) byDay.Add(x);
            }
            byDay.Sort((a, b) => ((int)a + 6) % 7 - ((int)b + 6) % 7); // Monday first (WKST=MO)
        }

        int produced = 0;
        int iterations = 0;
        bool Accept(DateTime wall, out bool stop)
        {
            stop = false;
            var loc = ToLocal(start with { Wall = wall }, local);
            if ((untilLocal is { } ul && loc > ul) || loc >= toLocal) { stop = true; return false; }
            if (count is { } n && produced >= n) { stop = true; return false; }
            produced++;
            return true;
        }

        var s = start.Wall;
        switch (freq)
        {
            case "DAILY":
                for (int k = 0; iterations++ < MaxIterations; k++)
                {
                    var w = s.AddDays((double)k * interval);
                    if (!Accept(w, out bool stop)) { if (stop) yield break; continue; }
                    yield return w;
                }
                break;
            case "WEEKLY":
                if (byDay.Count == 0) byDay.Add(s.DayOfWeek);
                var weekStart = s.Date.AddDays(-(((int)s.DayOfWeek + 6) % 7));
                for (int k = 0; iterations++ < MaxIterations; k++)
                {
                    var ws = weekStart.AddDays(7.0 * k * interval);
                    foreach (var dow in byDay)
                    {
                        var w = ws.AddDays(((int)dow + 6) % 7) + s.TimeOfDay;
                        if (w < s) continue;
                        if (!Accept(w, out bool stop)) { if (stop) yield break; continue; }
                        yield return w;
                    }
                }
                break;
            case "MONTHLY":
                for (int k = 0; iterations++ < MaxIterations; k++)
                {
                    var month = new DateTime(s.Year, s.Month, 1).AddMonths(k * interval);
                    if (s.Day > DateTime.DaysInMonth(month.Year, month.Month))
                    {
                        // Months without that day are skipped, but still stop once past the window.
                        if (ToLocal(start with { Wall = month }, local) >= toLocal) yield break;
                        continue;
                    }
                    var w = month.AddDays(s.Day - 1) + s.TimeOfDay;
                    if (!Accept(w, out bool stop)) { if (stop) yield break; continue; }
                    yield return w;
                }
                break;
            case "YEARLY":
                for (int k = 0; iterations++ < MaxIterations; k++)
                {
                    int year = s.Year + k * interval;
                    if (year > 9998) yield break;
                    if (s.Month == 2 && s.Day == 29 && !DateTime.IsLeapYear(year)) continue;
                    var w = new DateTime(year, s.Month, s.Day) + s.TimeOfDay;
                    if (!Accept(w, out bool stop)) { if (stop) yield break; continue; }
                    yield return w;
                }
                break;
            default:
                yield return s;
                break;
        }
    }
}
