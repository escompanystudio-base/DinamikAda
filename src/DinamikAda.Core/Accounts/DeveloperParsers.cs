using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DinamikAda.Core.Accounts;

// ---- Records ------------------------------------------------------------------------------------

/// <summary>One day of the GitHub contribution calendar (level 0..4).</summary>
public sealed record GitHubContributionDay(DateOnly Date, int Count, int Level);

/// <summary>GitHub contribution calendar for the last year with streaks.</summary>
public sealed record GitHubContributions(IReadOnlyList<GitHubContributionDay> Days, int Total, int CurrentStreak, int LongestStreak);

public sealed record GitHubNotification(string Title, string Type, string Repo, string Reason, DateTime UpdatedUtc, bool Unread, string HtmlUrl);

public sealed record GitHubNotifications(IReadOnlyList<GitHubNotification> Items)
{
    public int UnreadCount => Items.Count(i => i.Unread);
}

public sealed record LinearIssue(string Identifier, string Title, int Priority, string PriorityLabel, string Url,
    DateOnly? DueDate, string StateName, string StateColor, string StateType);

/// <summary>Open Linear issues assigned to the viewer, sorted by priority (urgent first, none last) then due date.</summary>
public sealed record LinearIssues(string ViewerName, IReadOnlyList<LinearIssue> Issues)
{
    public LinearIssue? Top => Issues.Count > 0 ? Issues[0] : null;
}

public sealed record ResendEmail(string Id, IReadOnlyList<string> To, string Subject, DateTime CreatedUtc, string LastEvent);

/// <summary>Latest Resend emails (newest first, max 20) plus status counts for the last 7 days.</summary>
public sealed record ResendSummary(IReadOnlyList<ResendEmail> Latest, IReadOnlyDictionary<string, int> Last7Days, int Sent7Days, double? DeliveryRate);

public sealed record PlausibleDay(DateOnly Date, int Visitors, int Pageviews);

/// <summary>14-day Plausible series ending today plus realtime visitors.</summary>
public sealed record PlausibleStats(IReadOnlyList<PlausibleDay> Days, int Realtime, int Today, int Last7, int Prev7, int Pageviews7, double? ChangePct);

/// <summary>An error reported by the service itself (e.g. GraphQL <c>errors</c>); the message is the service's text.</summary>
public sealed class DeveloperApiException(string message) : Exception(message);

/// <summary>Pure JSON/HTML parsers for the developer and analytics account widgets (unit tested).</summary>
public static partial class DeveloperParsers
{
    // ---- GitHub contributions -------------------------------------------------------------------

    [GeneratedRegex(@"<td\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TdTag();

    [GeneratedRegex(@"<tool-tip\b([^>]*)>([^<]*)</tool-tip>", RegexOptions.IgnoreCase)]
    private static partial Regex ToolTip();

    [GeneratedRegex(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')")]
    private static partial Regex Attr();

    [GeneratedRegex(@"^\s*([\d,.]+)")]
    private static partial Regex LeadingNumber();

    private static Dictionary<string, string> Attributes(string tag)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Attr().Matches(tag))
            map[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        return map;
    }

    /// <summary>Parses the HTML fragment from GET https://github.com/users/{user}/contributions.</summary>
    /// <exception cref="FormatException">No day cells were found.</exception>
    public static GitHubContributions ParseGitHubContributionsHtml(string html, DateOnly today)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in ToolTip().Matches(html))
        {
            var a = Attributes(m.Groups[1].Value);
            if (!a.TryGetValue("for", out var id) || id.Length == 0) continue;
            string text = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            var n = LeadingNumber().Match(text);
            counts[id] = n.Success && int.TryParse(n.Groups[1].Value.Replace(",", "").Replace(".", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
        }

        var days = new Dictionary<DateOnly, GitHubContributionDay>();
        foreach (Match m in TdTag().Matches(html))
        {
            var a = Attributes(m.Value);
            if (!a.TryGetValue("data-date", out var ds)
                || !DateOnly.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            int level = a.TryGetValue("data-level", out var ls) && int.TryParse(ls, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 0, 4) : 0;
            int count;
            if (a.TryGetValue("id", out var id) && counts.TryGetValue(id, out var c)) count = c;
            else if (a.TryGetValue("data-count", out var dc) && int.TryParse(dc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c2)) count = c2;
            else count = level > 0 ? 1 : 0; // no tooltip: keep streaks right at least
            days[date] = new GitHubContributionDay(date, count, level);
        }
        if (days.Count == 0) throw new FormatException("No contribution cells found.");
        return BuildContributions(days.Values, null, today);
    }

    /// <summary>Parses the GraphQL contributionCalendar response (POST https://api.github.com/graphql).</summary>
    public static GitHubContributions ParseGitHubContributionsGraphQl(string json, DateOnly today)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        ThrowGraphQlErrors(root);
        if (!TryPath(root, out var cal, "data", "user", "contributionsCollection", "contributionCalendar"))
            throw new FormatException("contributionCalendar missing.");
        var days = new List<GitHubContributionDay>();
        if (cal.TryGetProperty("weeks", out var weeks) && weeks.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in weeks.EnumerateArray())
            {
                if (!w.TryGetProperty("contributionDays", out var cds) || cds.ValueKind != JsonValueKind.Array) continue;
                foreach (var d in cds.EnumerateArray())
                {
                    if (!DateOnly.TryParseExact(Str(d, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                    int level = Str(d, "contributionLevel") switch
                    {
                        "FIRST_QUARTILE" => 1,
                        "SECOND_QUARTILE" => 2,
                        "THIRD_QUARTILE" => 3,
                        "FOURTH_QUARTILE" => 4,
                        _ => 0
                    };
                    days.Add(new GitHubContributionDay(date, (int)SocialMailParsers.Num(d, "contributionCount"), level));
                }
            }
        }
        int? total = cal.TryGetProperty("totalContributions", out _) ? (int)SocialMailParsers.Num(cal, "totalContributions") : null;
        return BuildContributions(days, total, today);
    }

    private static GitHubContributions BuildContributions(IEnumerable<GitHubContributionDay> source, int? total, DateOnly today)
    {
        var days = source.Where(d => d.Date <= today).OrderBy(d => d.Date).ToList();
        var (current, longest) = ComputeStreaks(days, today);
        return new GitHubContributions(days, total ?? days.Sum(d => d.Count), current, longest);
    }

    /// <summary>
    /// Current streak: consecutive days with contributions ending today, or yesterday when today has none yet.
    /// Longest: the longest such run up to today. Missing days count as zero; future days are ignored.
    /// </summary>
    public static (int Current, int Longest) ComputeStreaks(IEnumerable<GitHubContributionDay> days, DateOnly today)
    {
        var active = new HashSet<DateOnly>(days.Where(d => d.Count > 0 && d.Date <= today).Select(d => d.Date));
        int longest = 0, run = 0;
        DateOnly? prev = null;
        foreach (var d in active.OrderBy(x => x))
        {
            run = prev is { } p && p.AddDays(1) == d ? run + 1 : 1;
            longest = Math.Max(longest, run);
            prev = d;
        }
        var start = active.Contains(today) ? today : today.AddDays(-1);
        int current = 0;
        while (active.Contains(start.AddDays(-current))) current++;
        return (current, longest);
    }

    // ---- GitHub notifications -------------------------------------------------------------------

    /// <summary>Parses GET https://api.github.com/notifications.</summary>
    public static GitHubNotifications ParseGitHubNotifications(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) throw new FormatException("Expected an array of notifications.");
        var items = new List<GitHubNotification>();
        foreach (var n in root.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Object) continue;
            string repo = n.TryGetProperty("repository", out var r) && r.ValueKind == JsonValueKind.Object ? Str(r, "full_name") : string.Empty;
            string title = string.Empty, type = string.Empty;
            string? url = null;
            if (n.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                title = Str(s, "title");
                type = Str(s, "type");
                url = s.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            }
            items.Add(new GitHubNotification(title, type, repo, Str(n, "reason"),
                ParseUtc(Str(n, "updated_at")) ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
                n.TryGetProperty("unread", out var un) && un.ValueKind == JsonValueKind.True,
                GitHubApiToHtmlUrl(url, repo)));
        }
        return new GitHubNotifications(items);
    }

    [GeneratedRegex(@"^https://api\.github\.com/repos/([^/]+/[^/]+)/(pulls|issues)/(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubApiItem();

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+$")]
    private static partial Regex RepoName();

    /// <summary>api.github.com/repos/o/r/pulls/N → github.com/o/r/pull/N; issues/N → issues/N; else the repo page.</summary>
    public static string GitHubApiToHtmlUrl(string? apiUrl, string repoFullName)
    {
        if (apiUrl is not null)
        {
            var m = GitHubApiItem().Match(apiUrl.Trim());
            if (m.Success)
            {
                string kind = m.Groups[2].Value.Equals("pulls", StringComparison.OrdinalIgnoreCase) ? "pull" : "issues";
                return $"https://github.com/{m.Groups[1].Value}/{kind}/{m.Groups[3].Value}";
            }
        }
        return RepoName().IsMatch(repoFullName ?? string.Empty) ? "https://github.com/" + repoFullName : "https://github.com/notifications";
    }

    // ---- Linear ---------------------------------------------------------------------------------

    /// <summary>The GraphQL query sent to https://api.linear.app/graphql.</summary>
    public const string LinearQuery =
        "{ viewer { name assignedIssues(first: 25, orderBy: updatedAt, filter: { state: { type: { nin: [\"completed\", \"canceled\"] } } }) " +
        "{ nodes { identifier title priority priorityLabel url dueDate state { name color type } } } } }";

    /// <summary>Parses the Linear assigned-issues GraphQL response; GraphQL errors throw <see cref="DeveloperApiException"/>.</summary>
    public static LinearIssues ParseLinearIssues(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        ThrowGraphQlErrors(root);
        if (!TryPath(root, out var viewer, "data", "viewer")) throw new FormatException("viewer missing.");
        var issues = new List<LinearIssue>();
        if (TryPath(viewer, out var nodes, "assignedIssues", "nodes") && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in nodes.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object) continue;
                DateOnly? due = DateOnly.TryParseExact(Str(n, "dueDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd) ? dd : null;
                string sn = string.Empty, sc = string.Empty, st = string.Empty;
                if (n.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object)
                {
                    sn = Str(s, "name");
                    sc = Str(s, "color");
                    st = Str(s, "type");
                }
                int pr = Math.Clamp((int)Math.Round(SocialMailParsers.Num(n, "priority")), 0, 4);
                issues.Add(new LinearIssue(Str(n, "identifier"), Str(n, "title"), pr, Str(n, "priorityLabel"), Str(n, "url"), due, sn, sc, st));
            }
        }
        return new LinearIssues(Str(viewer, "name"), SortLinear(issues));
    }

    /// <summary>Urgent (1) first … low (4), then no priority (0); within a priority the earliest due date first, undated last.</summary>
    public static IReadOnlyList<LinearIssue> SortLinear(IEnumerable<LinearIssue> issues) =>
        issues.OrderBy(i => i.Priority == 0 ? 5 : i.Priority)
            .ThenBy(i => i.DueDate ?? DateOnly.MaxValue)
            .ToList();

    /// <summary>Parses "#rrggbb", "rrggbb" or "#rgb".</summary>
    public static bool TryParseHexColor(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    // ---- Resend ---------------------------------------------------------------------------------

    private static readonly HashSet<string> Delivered = new(StringComparer.Ordinal) { "delivered", "opened", "clicked" };
    private static readonly HashSet<string> Settled = new(StringComparer.Ordinal) { "delivered", "opened", "clicked", "bounced", "complained", "failed" };

    /// <summary>Parses GET https://api.resend.com/emails.</summary>
    public static ResendSummary ParseResendEmails(string json, DateTime nowUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new FormatException("data[] missing.");
        var all = new List<ResendEmail>();
        foreach (var e in data.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var to = new List<string>();
            if (e.TryGetProperty("to", out var t))
            {
                if (t.ValueKind == JsonValueKind.Array)
                    to.AddRange(t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                else if (t.ValueKind == JsonValueKind.String) to.Add(t.GetString()!);
            }
            all.Add(new ResendEmail(Str(e, "id"), to, Str(e, "subject"),
                ParseUtc(Str(e, "created_at")) ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
                Str(e, "last_event").Trim().ToLowerInvariant()));
        }
        all.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        var cutoff = nowUtc.ToUniversalTime().AddDays(-7);
        var week = all.Where(e => e.CreatedUtc >= cutoff).ToList();
        var counts = week.GroupBy(e => e.LastEvent.Length == 0 ? "sent" : e.LastEvent, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int settled = week.Count(e => Settled.Contains(e.LastEvent));
        double? rate = settled == 0 ? null : week.Count(e => Delivered.Contains(e.LastEvent)) / (double)settled;
        return new ResendSummary(all.Take(20).ToList(), counts, week.Count, rate);
    }

    /// <summary>"jane@example.com" → "j•••@example.com" (recipients are never shown in full on the dock).</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return string.Empty;
        int at = email.IndexOf('@');
        if (at <= 0) return email[0] + "•••";
        return email[0] + "•••" + email[at..];
    }

    // ---- Plausible ------------------------------------------------------------------------------

    /// <summary>Body for POST {host}/api/v2/query: visitors and pageviews per day for the 14 days ending today.</summary>
    public static string PlausibleQueryBody(string siteId, DateOnly today) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["site_id"] = siteId,
        ["metrics"] = new[] { "visitors", "pageviews" },
        ["date_range"] = new[] { today.AddDays(-13).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        ["dimensions"] = new[] { "time:day" }
    });

    /// <summary>Parses the v2 query response into a zero-filled 14-day series ending <paramref name="today"/>.</summary>
    public static PlausibleStats ParsePlausibleStats(string json, int realtime, DateOnly today)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new FormatException("results[] missing.");
        var byDate = new Dictionary<DateOnly, (int V, int P)>();
        foreach (var r in results.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object
                || !r.TryGetProperty("dimensions", out var dims) || dims.ValueKind != JsonValueKind.Array || dims.GetArrayLength() == 0
                || dims[0].ValueKind != JsonValueKind.String) continue;
            string ds = dims[0].GetString()!;
            if (ds.Length > 10) ds = ds[..10];
            if (!DateOnly.TryParseExact(ds, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            int v = 0, p = 0;
            if (r.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                if (m.GetArrayLength() > 0) v = (int)NumValue(m[0]);
                if (m.GetArrayLength() > 1) p = (int)NumValue(m[1]);
            }
            byDate[date] = (v, p);
        }
        var days = new List<PlausibleDay>(14);
        for (int i = 13; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            var (v, p) = byDate.TryGetValue(d, out var x) ? x : (0, 0);
            days.Add(new PlausibleDay(d, v, p));
        }
        int last7 = days.Skip(7).Sum(d => d.Visitors);
        int prev7 = days.Take(7).Sum(d => d.Visitors);
        double? change = prev7 > 0 ? (last7 - prev7) * 100.0 / prev7 : null;
        return new PlausibleStats(days, Math.Max(0, realtime), days[^1].Visitors, last7, prev7, days.Skip(7).Sum(d => d.Pageviews), change);
    }

    /// <summary>GET {host}/api/v1/stats/realtime/visitors returns a bare number.</summary>
    public static int ParsePlausibleRealtime(string body)
    {
        string s = (body ?? string.Empty).Trim().Trim('"');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? (int)v : 0;
    }

    // ---- Shared helpers -------------------------------------------------------------------------

    /// <summary>Reads a service error message ("error", "message" or GraphQL "errors[0].message"); null if none.</summary>
    public static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (GraphQlError(root) is { } g) return g;
            foreach (var name in new[] { "error", "message" })
            {
                if (!root.TryGetProperty(name, out var v)) continue;
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s;
                if (v.ValueKind == JsonValueKind.Object && Str(v, "message") is { Length: > 0 } s2) return s2;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses ISO-8601 or Postgres-style ("2026-11-19 10:13:42.67+00") timestamps as UTC.</summary>
    public static DateTime? ParseUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string t = s.Trim();
        if (t.Length >= 3 && (t[^3] == '+' || t[^3] == '-') && char.IsDigit(t[^1]) && char.IsDigit(t[^2])) t += ":00";
        return DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
    }

    private static string? GraphQlError(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errs) || errs.ValueKind != JsonValueKind.Array || errs.GetArrayLength() == 0) return null;
        var first = errs[0];
        string msg = first.ValueKind == JsonValueKind.Object ? Str(first, "message") : string.Empty;
        return msg.Length > 0 ? msg : "GraphQL error";
    }

    private static void ThrowGraphQlErrors(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && GraphQlError(root) is { } msg) throw new DeveloperApiException(msg);
    }

    private static bool TryPath(JsonElement el, out JsonElement result, params string[] path)
    {
        result = el;
        foreach (var p in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(p, out result) || result.ValueKind == JsonValueKind.Null) return false;
        }
        return true;
    }

    private static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static double NumValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => 0
    };
}
