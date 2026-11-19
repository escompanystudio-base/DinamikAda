using DinamikAda.Core.Accounts;
using Xunit;

namespace DinamikAda.Tests;

public class DeveloperAccountParserTests
{
    private static readonly DateOnly Today = new(2026, 11, 19);

    // ---- GitHub contributions (HTML) ------------------------------------------------------------

    private static string Day(string date, int level, int r, int c, bool reorder = false) => reorder
        ? $"<td data-level=\"{level}\" id=\"contribution-day-component-{r}-{c}\" role=\"gridcell\" class=\"ContributionCalendar-day\" data-date=\"{date}\" tabindex=\"-1\"></td>"
        : $"<td tabindex=\"0\" data-ix=\"{c}\" aria-selected=\"false\" aria-describedby=\"contribution-graph-legend-level-{level}\" style=\"width: 10px\" data-date=\"{date}\" id=\"contribution-day-component-{r}-{c}\" data-level=\"{level}\" role=\"gridcell\" data-view-component=\"true\" class=\"ContributionCalendar-day\"></td>";

    private static string Tip(int r, int c, string text) =>
        $"<tool-tip id=\"tooltip-{r}{c}\" for=\"contribution-day-component-{r}-{c}\" popover=\"manual\" data-direction=\"n\" data-type=\"label\" data-view-component=\"true\" class=\"sr-only position-absolute\">{text}</tool-tip>";

    [Fact]
    public void GitHub_html_parses_days_counts_levels_and_streaks()
    {
        // Nov 14..19 2026 (Sat..Thu). 14: 0, 15: 3, 16: 1,234, 17: 0, 18: 2, 19: 5 → current streak 2, longest 2.
        string html = """
            <div class="js-yearly-contributions"><h2 class="f4 text-normal mb-2">1,244 contributions in the last year</h2>
            <table class="ContributionCalendar-grid js-calendar-graph-table" role="grid">
            <tbody><tr style="height: 10px">
            <td class="ContributionCalendar-label" style="position: relative"><span class="sr-only">Sunday</span></td>
            """
            + Day("2026-11-15", 2, 0, 52) + Tip(0, 52, "3 contributions on November 15th.")
            + Day("2026-11-16", 4, 1, 52, reorder: true) + Tip(1, 52, "1,234 contributions on November 16th.")
            + Day("2026-11-17", 0, 2, 52) + Tip(2, 52, "No contributions on November 17th.")
            + Day("2026-11-18", 1, 3, 52) + Tip(3, 52, "2 contributions on November 18th.")
            + Day("2026-11-19", 3, 4, 52) + Tip(4, 52, "5 contributions on November 19th.")
            + Day("2026-11-14", 0, 6, 51) + Tip(6, 51, "No contributions on November 14th.")
            + """
            </tr></tbody></table>
            <div class="legend"><td class="ContributionCalendar-day" data-level="4" id="contribution-graph-legend-level-4"></td></div></div>
            """;
        var g = DeveloperParsers.ParseGitHubContributionsHtml(html, Today);

        Assert.Equal(6, g.Days.Count);
        Assert.Equal(new DateOnly(2026, 11, 14), g.Days[0].Date); // sorted by date
        var d16 = g.Days.Single(d => d.Date == new DateOnly(2026, 11, 16));
        Assert.Equal(1234, d16.Count);
        Assert.Equal(4, d16.Level);
        Assert.Equal(0, g.Days.Single(d => d.Date == new DateOnly(2026, 11, 17)).Count);
        Assert.Equal(1244, g.Total);
        Assert.Equal(2, g.CurrentStreak);
        Assert.Equal(2, g.LongestStreak);
    }

    [Fact]
    public void GitHub_html_tolerates_tooltips_before_cells_and_single_quotes()
    {
        string html = Tip(0, 0, "1 contribution on November 18th.") + Tip(1, 0, "7 contributions on November 19th.")
            + "<td data-date='2026-11-18' data-level='1' id='contribution-day-component-0-0'></td>"
            + Day("2026-11-19", 2, 1, 0);
        var g = DeveloperParsers.ParseGitHubContributionsHtml(html, Today);
        Assert.Equal(2, g.Days.Count);
        Assert.Equal(1, g.Days[0].Count);
        Assert.Equal(7, g.Days[1].Count);
        Assert.Equal(8, g.Total);
    }

    [Fact]
    public void GitHub_html_without_cells_is_malformed()
    {
        Assert.Throws<FormatException>(() => DeveloperParsers.ParseGitHubContributionsHtml("<html><body>Not Found</body></html>", Today));
    }

    [Fact]
    public void Streaks_count_from_yesterday_and_ignore_future_days()
    {
        var days = new List<GitHubContributionDay>
        {
            new(new DateOnly(2026, 11, 10), 1, 1), new(new DateOnly(2026, 11, 11), 2, 1), new(new DateOnly(2026, 11, 12), 4, 2),
            new(new DateOnly(2026, 11, 13), 1, 1), new(new DateOnly(2026, 11, 14), 0, 0),
            new(new DateOnly(2026, 11, 17), 3, 2), new(new DateOnly(2026, 11, 18), 1, 1),
            new(new DateOnly(2026, 11, 19), 0, 0), new(new DateOnly(2026, 11, 20), 9, 4)
        };
        var (current, longest) = DeveloperParsers.ComputeStreaks(days, Today);
        Assert.Equal(2, current);   // 17, 18 (today has none yet)
        Assert.Equal(4, longest);   // 10..13

        // Two days later the 20th is "yesterday" and has contributions, so the streak is 1.
        var later = DeveloperParsers.ComputeStreaks(days, new DateOnly(2026, 11, 21));
        Assert.Equal(1, later.Current);
    }

    [Fact]
    public void Streaks_stale_when_gap_before_yesterday()
    {
        var days = new List<GitHubContributionDay> { new(new DateOnly(2026, 11, 16), 3, 2), new(new DateOnly(2026, 11, 17), 1, 1) };
        Assert.Equal((0, 2), DeveloperParsers.ComputeStreaks(days, Today));
    }

    [Fact]
    public void GitHub_graphql_parses_calendar()
    {
        string json = """
            {"data":{"user":{"contributionsCollection":{"contributionCalendar":{"totalContributions":412,"weeks":[
              {"contributionDays":[{"date":"2026-11-15","contributionCount":0,"contributionLevel":"NONE"},
                                   {"date":"2026-11-16","contributionCount":4,"contributionLevel":"SECOND_QUARTILE"}]},
              {"contributionDays":[{"date":"2026-11-17","contributionCount":"11","contributionLevel":"FOURTH_QUARTILE"},
                                   {"date":"2026-11-18","contributionCount":1,"contributionLevel":"FIRST_QUARTILE"},
                                   {"date":"2026-11-19","contributionCount":2,"contributionLevel":"THIRD_QUARTILE"}]}]}}}}}
            """;
        var g = DeveloperParsers.ParseGitHubContributionsGraphQl(json, Today);
        Assert.Equal(5, g.Days.Count);
        Assert.Equal(412, g.Total);
        Assert.Equal(new[] { 0, 2, 4, 1, 3 }, g.Days.Select(d => d.Level));
        Assert.Equal(11, g.Days[2].Count);
        Assert.Equal(4, g.CurrentStreak);
        Assert.Equal(4, g.LongestStreak);
    }

    [Fact]
    public void GitHub_graphql_errors_throw_api_exception()
    {
        string json = """{"data":{"user":null},"errors":[{"type":"NOT_FOUND","path":["user"],"message":"Could not resolve to a User with the login of 'nobody-xyz'."}]}""";
        var ex = Assert.Throws<DeveloperApiException>(() => DeveloperParsers.ParseGitHubContributionsGraphQl(json, Today));
        Assert.Contains("nobody-xyz", ex.Message);
    }

    // ---- GitHub notifications -------------------------------------------------------------------

    [Fact]
    public void GitHub_notifications_parse_and_map_urls()
    {
        string json = """
            [
              {"id":"1","repository":{"id":1296269,"name":"Hello-World","full_name":"octocat/Hello-World","html_url":"https://github.com/octocat/Hello-World"},
               "subject":{"title":"Greetings","url":"https://api.github.com/repos/octocat/Hello-World/issues/123","latest_comment_url":"https://api.github.com/repos/octocat/Hello-World/issues/comments/123","type":"Issue"},
               "reason":"subscribed","unread":true,"updated_at":"2026-11-19T08:01:45Z","last_read_at":null,"url":"https://api.github.com/notifications/threads/1"},
              {"id":"2","repository":{"full_name":"dotnet/runtime"},
               "subject":{"title":"Fix span bounds check","url":"https://api.github.com/repos/dotnet/runtime/pulls/98765","type":"PullRequest"},
               "reason":"review_requested","unread":true,"updated_at":"2026-11-18T22:30:00Z"},
              {"id":"3","repository":{"full_name":"acme/app"},
               "subject":{"title":"v2.4.0","url":"https://api.github.com/repos/acme/app/releases/12345","type":"Release"},
               "reason":"subscribed","unread":false,"updated_at":"2026-11-17T10:00:00Z"},
              {"id":"4","repository":{"full_name":"acme/app"},
               "subject":{"title":"CI failed","url":null,"type":"CheckSuite"},
               "reason":"ci_activity","unread":true}
            ]
            """;
        var n = DeveloperParsers.ParseGitHubNotifications(json);
        Assert.Equal(4, n.Items.Count);
        Assert.Equal(3, n.UnreadCount);
        var first = n.Items[0];
        Assert.Equal("Greetings", first.Title);
        Assert.Equal("Issue", first.Type);
        Assert.Equal("octocat/Hello-World", first.Repo);
        Assert.Equal("subscribed", first.Reason);
        Assert.True(first.Unread);
        Assert.Equal(new DateTime(2026, 11, 19, 8, 1, 45, DateTimeKind.Utc), first.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, first.UpdatedUtc.Kind);
        Assert.Equal("https://github.com/octocat/Hello-World/issues/123", first.HtmlUrl);
        Assert.Equal("https://github.com/dotnet/runtime/pull/98765", n.Items[1].HtmlUrl);
        Assert.Equal("https://github.com/acme/app", n.Items[2].HtmlUrl);
        Assert.Equal("https://github.com/acme/app", n.Items[3].HtmlUrl);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/pulls/7", "o/r", "https://github.com/o/r/pull/7")]
    [InlineData("https://api.github.com/repos/o/r/issues/42", "o/r", "https://github.com/o/r/issues/42")]
    [InlineData("https://api.github.com/repos/o/r/commits/abc123", "o/r", "https://github.com/o/r")]
    [InlineData(null, "o/r", "https://github.com/o/r")]
    [InlineData("https://evil.example.com/repos/o/r/pulls/1", "o/r", "https://github.com/o/r")]
    [InlineData(null, "", "https://github.com/notifications")]
    public void GitHub_api_url_maps_to_html(string? api, string repo, string expected)
    {
        Assert.Equal(expected, DeveloperParsers.GitHubApiToHtmlUrl(api, repo));
    }

    [Fact]
    public void GitHub_notifications_reject_non_array()
    {
        Assert.Throws<FormatException>(() => DeveloperParsers.ParseGitHubNotifications("""{"message":"Bad credentials"}"""));
    }

    // ---- Linear ---------------------------------------------------------------------------------

    [Fact]
    public void Linear_parses_and_sorts_by_priority_then_due_date()
    {
        string json = """
            {"data":{"viewer":{"name":"Ada Lovelace","assignedIssues":{"nodes":[
              {"identifier":"ENG-12","title":"No priority task","priority":0,"priorityLabel":"No priority","url":"https://linear.app/acme/issue/ENG-12","dueDate":null,"state":{"name":"Todo","color":"#e2e2e2","type":"unstarted"}},
              {"identifier":"ENG-40","title":"Low thing","priority":4,"priorityLabel":"Low","url":"https://linear.app/acme/issue/ENG-40","dueDate":"2026-11-20","state":{"name":"Backlog","color":"#bec2c8","type":"backlog"}},
              {"identifier":"ENG-31","title":"High later","priority":2,"priorityLabel":"High","url":"https://linear.app/acme/issue/ENG-31","dueDate":"2026-12-01","state":{"name":"In Progress","color":"#f2c94c","type":"started"}},
              {"identifier":"ENG-30","title":"High sooner","priority":2.0,"priorityLabel":"High","url":"https://linear.app/acme/issue/ENG-30","dueDate":"2026-11-22","state":{"name":"In Review","color":"#0f783c","type":"started"}},
              {"identifier":"ENG-29","title":"High no date","priority":"2","priorityLabel":"High","url":"https://linear.app/acme/issue/ENG-29","state":{"name":"Todo","color":"#e2e2e2","type":"unstarted"}},
              {"identifier":"ENG-7","title":"Prod is down","priority":1,"priorityLabel":"Urgent","url":"https://linear.app/acme/issue/ENG-7","dueDate":"2026-11-19","state":{"name":"In Progress","color":"#f2c94c","type":"started"}}
            ]}}}}
            """;
        var l = DeveloperParsers.ParseLinearIssues(json);
        Assert.Equal("Ada Lovelace", l.ViewerName);
        Assert.Equal(new[] { "ENG-7", "ENG-30", "ENG-31", "ENG-29", "ENG-40", "ENG-12" }, l.Issues.Select(i => i.Identifier));
        var top = l.Top!;
        Assert.Equal("ENG-7", top.Identifier);
        Assert.Equal("Urgent", top.PriorityLabel);
        Assert.Equal(new DateOnly(2026, 11, 19), top.DueDate);
        Assert.Equal("#f2c94c", top.StateColor);
        Assert.Equal("started", top.StateType);
        Assert.Null(l.Issues.Single(i => i.Identifier == "ENG-12").DueDate);
    }

    [Fact]
    public void Linear_errors_throw_api_exception()
    {
        string json = """{"errors":[{"message":"Authentication required, not authenticated","extensions":{"type":"authentication error","code":"AUTHENTICATION_ERROR"}}]}""";
        var ex = Assert.Throws<DeveloperApiException>(() => DeveloperParsers.ParseLinearIssues(json));
        Assert.Contains("Authentication required", ex.Message);
    }

    [Fact]
    public void Linear_empty_list_is_fine()
    {
        var l = DeveloperParsers.ParseLinearIssues("""{"data":{"viewer":{"name":"A","assignedIssues":{"nodes":[]}}}}""");
        Assert.Empty(l.Issues);
        Assert.Null(l.Top);
    }

    [Theory]
    [InlineData("#f2c94c", true, 0xF2, 0xC9, 0x4C)]
    [InlineData("0f783c", true, 0x0F, 0x78, 0x3C)]
    [InlineData("#abc", true, 0xAA, 0xBB, 0xCC)]
    [InlineData("#zzzzzz", false, 0, 0, 0)]
    [InlineData("", false, 0, 0, 0)]
    public void Hex_colors_parse(string hex, bool ok, byte r, byte g, byte b)
    {
        Assert.Equal(ok, DeveloperParsers.TryParseHexColor(hex, out var cr, out var cg, out var cb));
        if (ok) Assert.Equal((r, g, b), (cr, cg, cb));
    }

    // ---- Resend ---------------------------------------------------------------------------------

    [Fact]
    public void Resend_parses_emails_counts_and_delivery_rate()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        string json = """
            {"object":"list","has_more":false,"data":[
              {"id":"4ef9a417-02e9-4d39-ad75-9611e0fcc33c","to":["delivered@resend.dev"],"from":"Acme <onboarding@resend.dev>","created_at":"2026-11-19 10:13:42.674981+00","subject":"Hello World","bcc":null,"cc":null,"reply_to":null,"last_event":"delivered","scheduled_at":null},
              {"id":"b2","to":["jane.doe@example.com","x@y.z"],"created_at":"2026-11-19T09:00:00.000Z","subject":"Your receipt","last_event":"opened"},
              {"id":"b3","to":["bob@example.com"],"created_at":"2026-11-18 08:00:00+00:00","subject":"Welcome","last_event":"clicked"},
              {"id":"b4","to":["bad@nowhere.invalid"],"created_at":"2026-11-17 08:00:00+00","subject":"Reset password","last_event":"bounced"},
              {"id":"b5","to":["q@example.com"],"created_at":"2026-11-19 11:59:00+00","subject":"Queued one","last_event":"queued"},
              {"id":"b6","to":["old@example.com"],"created_at":"2026-11-01 08:00:00+00","subject":"Old newsletter","last_event":"Delivered"}
            ]}
            """;
        var r = DeveloperParsers.ParseResendEmails(json, now);
        Assert.Equal(6, r.Latest.Count);
        Assert.Equal("b5", r.Latest[0].Id); // newest first
        Assert.Equal(new DateTime(2026, 11, 19, 10, 13, 42, 674, DateTimeKind.Utc).AddTicks(9810), r.Latest[1].CreatedUtc);
        Assert.Equal(5, r.Sent7Days);
        Assert.Equal(1, r.Last7Days["delivered"]);
        Assert.Equal(1, r.Last7Days["opened"]);
        Assert.Equal(1, r.Last7Days["bounced"]);
        Assert.Equal(1, r.Last7Days["queued"]);
        Assert.False(r.Last7Days.ContainsKey("failed"));
        Assert.Equal("delivered", r.Latest.Single(e => e.Id == "b6").LastEvent); // normalised
        Assert.NotNull(r.DeliveryRate);
        Assert.Equal(0.75, r.DeliveryRate!.Value, 3); // 3 delivered-ish of 4 settled
        Assert.Equal(2, r.Latest.Single(e => e.Id == "b2").To.Count);
    }

    [Fact]
    public void Resend_keeps_latest_twenty_and_handles_empty()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var items = string.Join(",", Enumerable.Range(0, 30).Select(i =>
            $"{{\"id\":\"e{i}\",\"to\":[\"a{i}@x.io\"],\"created_at\":\"2026-11-19T{(i % 24):00}:00:00Z\",\"subject\":\"S{i}\",\"last_event\":\"sent\"}}"));
        var r = DeveloperParsers.ParseResendEmails("{\"data\":[" + items + "]}", now);
        Assert.Equal(20, r.Latest.Count);
        Assert.Null(r.DeliveryRate); // nothing settled yet

        var empty = DeveloperParsers.ParseResendEmails("""{"object":"list","data":[]}""", now);
        Assert.Empty(empty.Latest);
        Assert.Equal(0, empty.Sent7Days);
        Assert.Throws<FormatException>(() => DeveloperParsers.ParseResendEmails("""{"name":"validation_error"}""", now));
    }

    [Theory]
    [InlineData("jane.doe@example.com", "j•••@example.com")]
    [InlineData("a@b.co", "a•••@b.co")]
    [InlineData("not-an-email", "n•••")]
    [InlineData("", "")]
    public void Emails_are_masked(string input, string expected)
    {
        Assert.Equal(expected, DeveloperParsers.MaskEmail(input));
    }

    [Theory]
    [InlineData("2026-11-19 10:13:42.674981+00")]
    [InlineData("2026-11-19T10:13:42.674981+00:00")]
    [InlineData("2026-11-19T10:13:42.674981Z")]
    public void Timestamps_parse_as_utc(string s)
    {
        var d = DeveloperParsers.ParseUtc(s);
        Assert.NotNull(d);
        Assert.Equal(DateTimeKind.Utc, d!.Value.Kind);
        Assert.Equal(10, d.Value.Hour);
    }

    // ---- Plausible ------------------------------------------------------------------------------

    [Fact]
    public void Plausible_fills_series_and_computes_change()
    {
        // 14 days ending today; 2026-11-08 is missing (no visitors) and must be filled with zero.
        var rows = new List<string>();
        for (int i = 13; i >= 0; i--)
        {
            var d = Today.AddDays(-i);
            if (d == new DateOnly(2026, 11, 8)) continue;
            int visitors = i < 7 ? 20 : 10;
            rows.Add($"{{\"dimensions\":[\"{d:yyyy-MM-dd}\"],\"metrics\":[{visitors},{visitors * 3}]}}");
        }
        string json = "{\"results\":[" + string.Join(",", rows) + "],\"meta\":{},\"query\":{\"site_id\":\"example.com\",\"metrics\":[\"visitors\",\"pageviews\"]}}";
        var s = DeveloperParsers.ParsePlausibleStats(json, realtime: 7, Today);

        Assert.Equal(14, s.Days.Count);
        Assert.Equal(Today.AddDays(-13), s.Days[0].Date);
        Assert.Equal(Today, s.Days[^1].Date);
        Assert.Equal(0, s.Days.Single(d => d.Date == new DateOnly(2026, 11, 8)).Visitors);
        Assert.Equal(20, s.Today);
        Assert.Equal(140, s.Last7);
        Assert.Equal(60, s.Prev7);     // 7 days of 10 minus the missing day
        Assert.Equal(420, s.Pageviews7);
        Assert.Equal(7, s.Realtime);
        Assert.NotNull(s.ChangePct);
        Assert.Equal(133.33, s.ChangePct!.Value, 1);
    }

    [Fact]
    public void Plausible_change_is_null_without_previous_week_and_bad_payload_throws()
    {
        string json = "{\"results\":[{\"dimensions\":[\"2026-11-19\"],\"metrics\":[5,9]}]}";
        var s = DeveloperParsers.ParsePlausibleStats(json, 0, Today);
        Assert.Null(s.ChangePct);
        Assert.Equal(5, s.Last7);
        Assert.Throws<FormatException>(() => DeveloperParsers.ParsePlausibleStats("""{"error":"Invalid site"}""", 0, Today));
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData(" 3\n", 3)]
    [InlineData("\"4\"", 4)]
    [InlineData("oops", 0)]
    public void Plausible_realtime_parses_bare_number(string body, int expected)
    {
        Assert.Equal(expected, DeveloperParsers.ParsePlausibleRealtime(body));
    }

    [Fact]
    public void Plausible_query_body_uses_a_14_day_custom_range()
    {
        string body = DeveloperParsers.PlausibleQueryBody("example.com", Today);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("example.com", root.GetProperty("site_id").GetString());
        Assert.Equal("2026-11-06", root.GetProperty("date_range")[0].GetString());
        Assert.Equal("2026-11-19", root.GetProperty("date_range")[1].GetString());
        Assert.Equal("time:day", root.GetProperty("dimensions")[0].GetString());
        Assert.Equal(2, root.GetProperty("metrics").GetArrayLength());
    }

    [Fact]
    public void Api_error_message_is_extracted()
    {
        Assert.Equal("Invalid site", DeveloperParsers.TryReadErrorMessage("""{"error":"Invalid site"}"""));
        Assert.Equal("Bad credentials", DeveloperParsers.TryReadErrorMessage("""{"message":"Bad credentials","documentation_url":"x"}"""));
        Assert.Equal("Nope", DeveloperParsers.TryReadErrorMessage("""{"errors":[{"message":"Nope"}]}"""));
        Assert.Null(DeveloperParsers.TryReadErrorMessage("<html>"));
        Assert.Null(DeveloperParsers.TryReadErrorMessage("[]"));
    }
}
