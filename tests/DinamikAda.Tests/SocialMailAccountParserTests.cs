using System.Text;
using System.Text.Json;
using DinamikAda.Core.Accounts;
using Xunit;

namespace DinamikAda.Tests;

public class SocialMailAccountParserTests
{
    // ---- DeepSeek ---------------------------------------------------------------------------------

    [Fact]
    public void DeepSeek_balance_parses_string_amounts_and_prefers_usd()
    {
        const string json = """
        {"is_available":true,"balance_infos":[
          {"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"},
          {"currency":"USD","total_balance":"18.42","granted_balance":"2.00","topped_up_balance":"16.42"}]}
        """;
        var b = SocialMailParsers.ParseDeepSeekBalance(json);
        Assert.True(b.IsAvailable);
        Assert.Equal(2, b.Items.Count);
        Assert.Equal("USD", b.Primary!.Currency);
        Assert.Equal(18.42, b.Primary.Total, 3);
        Assert.Equal(16.42, b.Primary.ToppedUp, 3);
    }

    [Fact]
    public void DeepSeek_balance_tolerates_missing_fields()
    {
        var b = SocialMailParsers.ParseDeepSeekBalance("{}");
        Assert.False(b.IsAvailable);
        Assert.Empty(b.Items);
        Assert.Null(b.Primary);
    }

    // ---- YouTube ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("@kanalim", "forHandle=%40kanalim")]
    [InlineData("kanalim", "forHandle=%40kanalim")]
    [InlineData("UC_x5XG1OV2P6uZZ5FSM9Ttw", "id=UC_x5XG1OV2P6uZZ5FSM9Ttw")]
    [InlineData("https://www.youtube.com/@GoogleDevelopers", "forHandle=%40GoogleDevelopers")]
    [InlineData("https://www.youtube.com/channel/UC_x5XG1OV2P6uZZ5FSM9Ttw", "id=UC_x5XG1OV2P6uZZ5FSM9Ttw")]
    [InlineData(" @çay kanalı ", "forHandle=%40%C3%A7ay%20kanal%C4%B1")]
    public void YouTube_channel_selector_builds_handle_or_id_query(string input, string expected)
    {
        Assert.Equal(expected, SocialMailParsers.YouTubeChannelSelector(input));
    }

    [Fact]
    public void YouTube_channel_parses_string_statistics()
    {
        const string json = """
        {
          "kind": "youtube#channelListResponse",
          "etag": "abc",
          "pageInfo": {"totalResults": 1, "resultsPerPage": 5},
          "items": [{
            "kind": "youtube#channel",
            "id": "UC_x5XG1OV2P6uZZ5FSM9Ttw",
            "snippet": {
              "title": "Google for Developers",
              "customUrl": "@googledevelopers",
              "thumbnails": {
                "default": {"url": "https://yt3.ggpht.com/a/default.jpg", "width": 88, "height": 88},
                "medium": {"url": "https://yt3.ggpht.com/a/medium.jpg", "width": 240, "height": 240}
              }
            },
            "statistics": {
              "viewCount": "246589722",
              "subscriberCount": "2410000",
              "hiddenSubscriberCount": false,
              "videoCount": "6342"
            }
          }]
        }
        """;
        var c = SocialMailParsers.ParseYouTubeChannel(json);
        Assert.NotNull(c);
        Assert.Equal("UC_x5XG1OV2P6uZZ5FSM9Ttw", c!.Id);
        Assert.Equal("Google for Developers", c.Title);
        Assert.Equal("@googledevelopers", c.Handle);
        Assert.Equal(2_410_000, c.Subscribers);
        Assert.Equal(246_589_722, c.Views);
        Assert.Equal(6342, c.Videos);
        Assert.False(c.HiddenSubscribers);
        Assert.Equal("https://yt3.ggpht.com/a/medium.jpg", c.ThumbnailUrl);
    }

    [Fact]
    public void YouTube_channel_returns_null_when_no_items()
    {
        Assert.Null(SocialMailParsers.ParseYouTubeChannel("""{"kind":"youtube#channelListResponse","pageInfo":{"totalResults":0}}"""));
        Assert.Null(SocialMailParsers.ParseYouTubeChannel("""{"items":[]}"""));
    }

    [Fact]
    public void YouTube_hidden_subscribers_are_flagged()
    {
        var c = SocialMailParsers.ParseYouTubeChannel("""
            {"items":[{"id":"UCx","snippet":{"title":"T"},"statistics":{"viewCount":"10","hiddenSubscriberCount":true,"videoCount":"1"}}]}
            """);
        Assert.True(c!.HiddenSubscribers);
        Assert.Equal(0, c.Subscribers);
    }

    [Fact]
    public void Google_api_error_message_is_extracted()
    {
        const string json = """
        {"error":{"code":400,"message":"API key not valid. Please pass a valid API key.",
         "errors":[{"message":"API key not valid. Please pass a valid API key.","domain":"global","reason":"badRequest"}],
         "status":"INVALID_ARGUMENT"}}
        """;
        Assert.Equal("API key not valid. Please pass a valid API key.", SocialMailParsers.ParseGoogleApiError(json));
        Assert.Null(SocialMailParsers.ParseGoogleApiError("not json"));
        Assert.Null(SocialMailParsers.ParseGoogleApiError("{}"));
    }

    // ---- LeetCode ---------------------------------------------------------------------------------

    [Fact]
    public void LeetCode_request_body_carries_query_and_variables()
    {
        string body = SocialMailParsers.LeetCodeRequestBody("neal_wu");
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("matchedUser(username:$u)", doc.RootElement.GetProperty("query").GetString());
        Assert.Contains("allQuestionsCount", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal("neal_wu", doc.RootElement.GetProperty("variables").GetProperty("u").GetString());
    }

    [Fact]
    public void LeetCode_stats_parse_solved_and_totals()
    {
        const string json = """
        {"data":{
          "allQuestionsCount":[{"difficulty":"All","count":3342},{"difficulty":"Easy","count":837},{"difficulty":"Medium","count":1748},{"difficulty":"Hard","count":757}],
          "matchedUser":{"username":"emre_dev","profile":{"ranking":184233},
            "submitStatsGlobal":{"acSubmissionNum":[{"difficulty":"All","count":412},{"difficulty":"Easy","count":186},{"difficulty":"Medium","count":192},{"difficulty":"Hard","count":34}]}}}}
        """;
        var s = SocialMailParsers.ParseLeetCode(json);
        Assert.NotNull(s);
        Assert.Equal("emre_dev", s!.Username);
        Assert.Equal(184233, s.Ranking);
        Assert.Equal(new LeetCodeDifficulty(186, 837), s.Easy);
        Assert.Equal(new LeetCodeDifficulty(192, 1748), s.Medium);
        Assert.Equal(new LeetCodeDifficulty(34, 757), s.Hard);
        Assert.Equal(412, s.TotalSolved);
        Assert.Equal(3342, s.TotalQuestions);
    }

    [Fact]
    public void LeetCode_unknown_user_returns_null()
    {
        const string json = """
        {"errors":[{"message":"That user does not exist.","locations":[{"line":1,"column":62}],"path":["matchedUser"],"extensions":{"handled":true}}],
         "data":{"allQuestionsCount":[{"difficulty":"All","count":3342}],"matchedUser":null}}
        """;
        Assert.Null(SocialMailParsers.ParseLeetCode(json));
    }

    [Fact]
    public void LeetCode_missing_ranking_is_zero()
    {
        var s = SocialMailParsers.ParseLeetCode("""{"data":{"matchedUser":{"username":"x","profile":{"ranking":null}}}}""");
        Assert.NotNull(s);
        Assert.Equal(0, s!.Ranking);
        Assert.Equal(0, s.TotalSolved);
    }

    // ---- IMAP (Gmail) -----------------------------------------------------------------------------

    [Theory]
    [InlineData("ad@gmail.com", "\"ad@gmail.com\"")]
    [InlineData("pa\"ss\\word", "\"pa\\\"ss\\\\word\"")]
    [InlineData("abcd efgh\r\nijkl", "\"abcd efghijkl\"")]
    public void Imap_quote_escapes_quotes_and_backslashes(string input, string expected)
    {
        Assert.Equal(expected, ImapParsers.Quote(input));
    }

    [Fact]
    public void Imap_tagged_end_skips_literals()
    {
        // The literal contains a line that looks like the tagged reply; it must not end the response.
        string literal = "Subject: hi\r\na5 OK fake\r\n\r\n";
        string resp = "* 3 FETCH (BODY[HEADER.FIELDS (SUBJECT)] {" + Encoding.ASCII.GetByteCount(literal) + "}\r\n" + literal + ")\r\na5 OK Success\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(resp);
        int end = ImapParsers.FindTaggedEnd(bytes, "a5");
        Assert.Equal(bytes.Length, end);
        Assert.Equal(-1, ImapParsers.FindTaggedEnd(bytes.AsSpan(0, bytes.Length - 3), "a5"));
    }

    [Fact]
    public void Imap_tagged_end_waits_for_incomplete_literal()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("* 3 FETCH (BODY[] {50}\r\nshort\r\na5 OK done\r\n");
        Assert.Equal(-1, ImapParsers.FindTaggedEnd(bytes, "a5"));
    }

    [Fact]
    public void Imap_tagged_result_reads_status()
    {
        var r = ImapParsers.TaggedResult("* CAPABILITY IMAP4rev1\r\na1 NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)\r\n", "a1");
        Assert.Equal("NO", r.Status);
        Assert.Contains("AUTHENTICATIONFAILED", r.Text);
        Assert.Equal("OK", ImapParsers.TaggedResult("a2 OK Success\r\n", "a2").Status);
        Assert.Equal("", ImapParsers.TaggedResult("* nothing\r\n", "a9").Status);
    }

    [Fact]
    public void Imap_status_line_parses_counts_in_any_order()
    {
        var s = ImapParsers.ParseStatus("* STATUS \"INBOX\" (UNSEEN 12 MESSAGES 4821)\r\na2 OK Success\r\n");
        Assert.Equal(12, s.Unseen);
        Assert.Equal(4821, s.Messages);
        var s2 = ImapParsers.ParseStatus("* STATUS INBOX (MESSAGES 3 UNSEEN 0)\r\n");
        Assert.Equal(0, s2.Unseen);
        Assert.Equal(3, s2.Messages);
    }

    [Fact]
    public void Imap_search_parses_ids_across_lines()
    {
        var ids = ImapParsers.ParseSearch("* SEARCH 4 17 2039 2040\r\n* SEARCH 2041\r\na4 OK SEARCH completed (Success)\r\n");
        Assert.Equal(new[] { 4, 17, 2039, 2040, 2041 }, ids);
        Assert.Empty(ImapParsers.ParseSearch("* SEARCH\r\na4 OK done\r\n"));
    }

    [Fact]
    public void Imap_newest_ids_takes_last_five_descending()
    {
        Assert.Equal(new[] { 9, 8, 7, 6, 5 }, ImapParsers.Newest(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 5));
        Assert.Equal("9,8,7", string.Join(",", ImapParsers.Newest(new[] { 7, 9, 8 }, 5)));
    }

    [Fact]
    public void Imap_fetch_headers_with_literals_and_utf8_byte_counts()
    {
        string h1 = "From: =?UTF-8?B?QXnFn2UgWcSxbG1heg==?= <ayse@example.com>\r\nSubject: =?UTF-8?Q?Toplant=C4=B1_notlar=C4=B1?=\r\nDate: Thu, 19 Nov 2026 09:14:03 +0300\r\n\r\n";
        // RFC 6532 raw UTF-8 header; the literal length is in bytes, not characters.
        string h2 = "From: \"GitHub\" <noreply@github.com>\r\nSubject: Çağrı: [repo] PR #42 merged\r\nDate: Wed, 18 Nov 2026 22:01:00 +0000\r\n\r\n";
        var sb = new StringBuilder();
        var bytes = new List<byte>();
        void Add(string s) => bytes.AddRange(Encoding.UTF8.GetBytes(s));
        Add("* 2040 FETCH (BODY[HEADER.FIELDS (FROM SUBJECT DATE)] {" + Encoding.UTF8.GetByteCount(h1) + "}\r\n");
        Add(h1);
        Add(")\r\n");
        Add("* 2039 FETCH (BODY[HEADER.FIELDS (FROM SUBJECT DATE)] {" + Encoding.UTF8.GetByteCount(h2) + "}\r\n");
        Add(h2);
        Add(")\r\na5 OK Success\r\n");

        var msgs = ImapParsers.ParseFetchHeaders(bytes.ToArray());
        Assert.Equal(2, msgs.Count);
        Assert.Equal(2040, msgs[0].Sequence);
        Assert.Equal("Ayşe Yılmaz", msgs[0].FromName);
        Assert.Equal("ayse@example.com", msgs[0].FromAddress);
        Assert.Equal("Toplantı notları", msgs[0].Subject);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 9, 14, 3, TimeSpan.FromHours(3)), msgs[0].Date);
        Assert.Equal("GitHub", msgs[1].FromName);
        Assert.Equal("Çağrı: [repo] PR #42 merged", msgs[1].Subject);
    }

    [Fact]
    public void Header_block_unfolds_continuation_lines()
    {
        var h = ImapParsers.ParseHeaderBlock("Subject: A very long\r\n  subject line\r\nFrom: Bob <bob@x.io>\r\n\r\n");
        Assert.Equal("A very long subject line", h["subject"]);
        Assert.Equal("Bob <bob@x.io>", h["from"]);
    }

    [Theory]
    [InlineData("=?UTF-8?B?w4dvayDDtm5lbWxp?=", "Çok önemli")]
    [InlineData("=?utf-8?q?Fatura_hazır?=", "Fatura hazır")]
    [InlineData("=?UTF-8?Q?=C3=96deme?= =?UTF-8?Q?_al=C4=B1nd=C4=B1?=", "Ödeme alındı")]
    [InlineData("Re: =?ISO-8859-1?Q?caf=E9?= time", "Re: café time")]
    [InlineData("Plain subject", "Plain subject")]
    [InlineData("=?UTF-8?B?bad base64!?=", "=?UTF-8?B?bad base64!?=")]
    public void Encoded_words_are_decoded(string input, string expected)
    {
        Assert.Equal(expected, ImapParsers.DecodeEncodedWords(input));
    }

    [Theory]
    [InlineData("\"Doe, John\" <john@doe.com>", "Doe, John", "john@doe.com")]
    [InlineData("<only@addr.com>", "only", "only@addr.com")]
    [InlineData("bare@addr.com", "bare", "bare@addr.com")]
    [InlineData("Medium Daily Digest <noreply@medium.com>", "Medium Daily Digest", "noreply@medium.com")]
    public void From_header_yields_display_name(string from, string name, string address)
    {
        var (n, a) = ImapParsers.ParseFrom(from);
        Assert.Equal(name, n);
        Assert.Equal(address, a);
    }

    [Theory]
    [InlineData("Thu, 19 Nov 2026 09:14:03 +0300")]
    [InlineData("19 Nov 2026 09:14:03 +0300")]
    [InlineData("Thu, 19 Nov 2026 09:14:03 +0300 (+03)")]
    public void Rfc2822_dates_parse(string s)
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 9, 14, 3, TimeSpan.FromHours(3)), ImapParsers.ParseDate(s));
    }

    [Fact]
    public void Rfc2822_gmt_and_garbage()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 22, 1, 0, TimeSpan.Zero), ImapParsers.ParseDate("Wed, 18 Nov 2026 22:01:00 GMT"));
        Assert.Null(ImapParsers.ParseDate("yesterday"));
    }

    // ---- iCal (Google Calendar) -------------------------------------------------------------------

    private static readonly TimeZoneInfo Local3 =
        TimeZoneInfo.CreateCustomTimeZone("Test+3", TimeSpan.FromHours(3), "Test+3", "Test+3");

    private static string Cal(params string[] events) =>
        "BEGIN:VCALENDAR\r\nPRODID:-//Google Inc//Google Calendar 70.9054//EN\r\nVERSION:2.0\r\nX-WR-TIMEZONE:Europe/Istanbul\r\n" +
        string.Concat(events.Select(e => "BEGIN:VEVENT\r\n" + e.Replace("\n", "\r\n") + "\r\nEND:VEVENT\r\n")) +
        "END:VCALENDAR\r\n";

    private static IReadOnlyList<CalendarEvent> Parse(string ics, DateTime now) =>
        ICalParser.Parse(ics, now.AddDays(-1), now.AddDays(14), Local3);

    [Fact]
    public void ICal_utc_event_converts_to_local_and_unescapes()
    {
        string ics = Cal("DTSTART:20261120T070000Z\nDTEND:20261120T080000Z\nSUMMARY:Sprint planı\\, ekip\nLOCATION:Ofis\\; Kat 3\\nOda 2\nUID:a1@google.com");
        var e = Assert.Single(Parse(ics, new DateTime(2026, 11, 19, 12, 0, 0)));
        Assert.Equal("Sprint planı, ekip", e.Title);
        Assert.Equal("Ofis; Kat 3\nOda 2", e.Location);
        Assert.Equal(new DateTime(2026, 11, 20, 10, 0, 0), e.Start);
        Assert.Equal(new DateTime(2026, 11, 20, 11, 0, 0), e.End);
        Assert.False(e.AllDay);
    }

    [Fact]
    public void ICal_unfolds_lines()
    {
        string ics = Cal("DTSTART:20261120T070000Z\nDTEND:20261120T080000Z\nSUMMARY:Uzun bir başlık\n  devamı burada\nUID:a2");
        Assert.Equal("Uzun bir başlık devamı burada", Assert.Single(Parse(ics, new DateTime(2026, 11, 19))).Title);
    }

    [Fact]
    public void ICal_tzid_is_resolved_with_iana_ids()
    {
        // Istanbul is UTC+3 all year; London is UTC+0 in November.
        string ics = Cal(
            "DTSTART;TZID=Europe/Istanbul:20261120T090000\nDTEND;TZID=Europe/Istanbul:20261120T100000\nSUMMARY:Ist\nUID:t1",
            "DTSTART;TZID=\"Europe/London\":20261120T090000\nDTEND;TZID=Europe/London:20261120T093000\nSUMMARY:Lon\nUID:t2");
        var evs = Parse(ics, new DateTime(2026, 11, 19));
        Assert.Equal(new DateTime(2026, 11, 20, 9, 0, 0), evs.Single(e => e.Title == "Ist").Start);
        Assert.Equal(new DateTime(2026, 11, 20, 12, 0, 0), evs.Single(e => e.Title == "Lon").Start);
        Assert.Equal(new DateTime(2026, 11, 20, 12, 30, 0), evs.Single(e => e.Title == "Lon").End);
    }

    [Fact]
    public void ICal_floating_and_all_day_events()
    {
        string ics = Cal(
            "DTSTART:20261121T140000\nDTEND:20261121T153000\nSUMMARY:Floating\nUID:f1",
            "DTSTART;VALUE=DATE:20261122\nDTEND;VALUE=DATE:20261123\nSUMMARY:Doğum günü\nUID:d1");
        var evs = Parse(ics, new DateTime(2026, 11, 19));
        var f = evs.Single(e => e.Title == "Floating");
        Assert.Equal(new DateTime(2026, 11, 21, 14, 0, 0), f.Start);
        var d = evs.Single(e => e.AllDay);
        Assert.Equal(new DateTime(2026, 11, 22), d.Start);
        Assert.Equal(new DateTime(2026, 11, 23), d.End);
    }

    [Fact]
    public void ICal_skips_cancelled_and_out_of_window()
    {
        string ics = Cal(
            "DTSTART:20261120T070000Z\nDTEND:20261120T080000Z\nSUMMARY:İptal\nSTATUS:CANCELLED\nUID:c1",
            "DTSTART:20270120T070000Z\nDTEND:20270120T080000Z\nSUMMARY:Far\nUID:c2",
            "DTSTART:20261001T070000Z\nDTEND:20261001T080000Z\nSUMMARY:Past\nUID:c3");
        Assert.Empty(Parse(ics, new DateTime(2026, 11, 19)));
    }

    [Fact]
    public void ICal_daily_rrule_with_count_and_exdate()
    {
        string ics = Cal("DTSTART;TZID=Europe/Istanbul:20261116T093000\nDTEND;TZID=Europe/Istanbul:20261116T094500\nRRULE:FREQ=DAILY;COUNT=6\nEXDATE;TZID=Europe/Istanbul:20261119T093000\nSUMMARY:Standup\nUID:r1");
        var evs = Parse(ics, new DateTime(2026, 11, 19, 12, 0, 0));
        // Window starts 18 Nov 12:00: occurrences 18 (ended), 19 excluded, 20, 21 → 20, 21 (18 ends 09:45 before window start).
        Assert.Equal(new[] { 20, 21 }, evs.Select(e => e.Start.Day).ToArray());
        Assert.All(evs, e => Assert.Equal(new TimeSpan(9, 30, 0), e.Start.TimeOfDay));
    }

    [Fact]
    public void ICal_weekly_byday_with_interval_and_until()
    {
        // Every other week on Monday and Thursday from Mon 2 Nov 2026 until 31 Dec.
        string ics = Cal("DTSTART:20261102T060000Z\nDTEND:20261102T070000Z\nRRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,TH;UNTIL=20261231T235959Z\nSUMMARY:Ders\nUID:w1");
        var evs = Parse(ics, new DateTime(2026, 11, 19, 12, 0, 0));
        // Weeks of 2 Nov, 16 Nov, 30 Nov … window 18 Nov → 3 Dec: Thu 19 Nov, Mon 30 Nov, Thu 3 Dec.
        Assert.Equal(new[] { new DateTime(2026, 11, 19, 9, 0, 0), new DateTime(2026, 11, 30, 9, 0, 0), new DateTime(2026, 12, 3, 9, 0, 0) },
            evs.Select(e => e.Start).ToArray());
    }

    [Fact]
    public void ICal_weekly_until_stops_expansion()
    {
        string ics = Cal("DTSTART:20261105T060000Z\nDTEND:20261105T070000Z\nRRULE:FREQ=WEEKLY;UNTIL=20261126T060000Z\nSUMMARY:Kurs\nUID:w2");
        var evs = Parse(ics, new DateTime(2026, 11, 19, 12, 0, 0));
        Assert.Equal(new[] { 19, 26 }, evs.Select(e => e.Start.Day).ToArray());
    }

    [Fact]
    public void ICal_monthly_and_yearly_rules()
    {
        string ics = Cal(
            "DTSTART;VALUE=DATE:20250125\nDTEND;VALUE=DATE:20250126\nRRULE:FREQ=MONTHLY\nSUMMARY:Kira\nUID:m1",
            "DTSTART;VALUE=DATE:19901128\nDTEND;VALUE=DATE:19901129\nRRULE:FREQ=YEARLY\nSUMMARY:Annemin doğum günü\nUID:y1",
            "DTSTART:20260831T090000Z\nDTEND:20260831T100000Z\nRRULE:FREQ=MONTHLY;INTERVAL=1\nSUMMARY:Ay sonu\nUID:m2");
        var evs = Parse(ics, new DateTime(2026, 11, 19, 12, 0, 0));
        Assert.Equal(new DateTime(2026, 11, 25), evs.Single(e => e.Title == "Kira").Start);
        Assert.Equal(new DateTime(2026, 11, 28), evs.Single(e => e.Title == "Annemin doğum günü").Start);
        // 31st only exists in some months: November has none, so no occurrence in the window.
        Assert.DoesNotContain(evs, e => e.Title == "Ay sonu");
    }

    [Fact]
    public void ICal_recurrence_id_override_replaces_instance()
    {
        string ics = Cal(
            "DTSTART:20261116T070000Z\nDTEND:20261116T080000Z\nRRULE:FREQ=DAILY;COUNT=10\nSUMMARY:Daily\nUID:o1",
            "RECURRENCE-ID:20261120T070000Z\nDTSTART:20261120T110000Z\nDTEND:20261120T120000Z\nSUMMARY:Daily (moved)\nUID:o1");
        var evs = Parse(ics, new DateTime(2026, 11, 19, 12, 0, 0)).Where(e => e.Start.Day == 20).ToList();
        var moved = Assert.Single(evs);
        Assert.Equal("Daily (moved)", moved.Title);
        Assert.Equal(14, moved.Start.Hour);
    }

    [Fact]
    public void ICal_duration_is_used_without_dtend()
    {
        string ics = Cal("DTSTART:20261120T070000Z\nDURATION:PT1H30M\nSUMMARY:Dur\nUID:du");
        var e = Assert.Single(Parse(ics, new DateTime(2026, 11, 19)));
        Assert.Equal(TimeSpan.FromMinutes(90), e.End - e.Start);
    }

    [Fact]
    public void ICal_upcoming_includes_in_progress_and_limits_to_eight()
    {
        var now = new DateTime(2026, 11, 19, 10, 15, 0);
        var list = new List<CalendarEvent>
        {
            new("Ended", null, now.AddHours(-2), now.AddHours(-1), false),
            new("Now", null, now.AddMinutes(-15), now.AddMinutes(30), false),
        };
        for (int i = 1; i <= 10; i++) list.Add(new CalendarEvent("E" + i, null, now.AddHours(i), now.AddHours(i).AddMinutes(30), false));
        var up = ICalParser.Upcoming(list, now, 8);
        Assert.Equal(8, up.Count);
        Assert.Equal("Now", up[0].Title);
        Assert.Equal("E7", up[^1].Title);
    }

    [Fact]
    public void ICal_garbage_returns_empty()
    {
        Assert.Empty(ICalParser.Parse("<html>not a calendar</html>", DateTime.Now.AddDays(-1), DateTime.Now.AddDays(14), Local3));
    }

    // ---- Unsplash ---------------------------------------------------------------------------------

    [Fact]
    public void Unsplash_random_photo_parses()
    {
        const string json = """
        {
          "id": "Dwu85P9SOIk",
          "created_at": "2016-05-03T11:00:28-04:00",
          "width": 2448, "height": 3264,
          "color": "#6E633A",
          "description": "A man drinking a coffee.",
          "alt_description": "man drinking coffee",
          "urls": {
            "raw": "https://images.unsplash.com/photo-1417325384643-aac51acc9e5d",
            "full": "https://images.unsplash.com/photo-1417325384643-aac51acc9e5d?q=75&fm=jpg",
            "regular": "https://images.unsplash.com/photo-1417325384643-aac51acc9e5d?q=75&fm=jpg&w=1080&fit=max",
            "small": "https://images.unsplash.com/photo-1417325384643-aac51acc9e5d?q=75&fm=jpg&w=400&fit=max",
            "thumb": "https://images.unsplash.com/photo-1417325384643-aac51acc9e5d?q=75&fm=jpg&w=200&fit=max"
          },
          "links": {
            "self": "https://api.unsplash.com/photos/Dwu85P9SOIk",
            "html": "https://unsplash.com/photos/Dwu85P9SOIk",
            "download": "https://unsplash.com/photos/Dwu85P9SOIk/download",
            "download_location": "https://api.unsplash.com/photos/Dwu85P9SOIk/download?ixid=abc"
          },
          "user": {
            "id": "QPxL2MGqfrw", "username": "exampleuser", "name": "Joe Example",
            "links": {"self": "https://api.unsplash.com/users/exampleuser", "html": "https://unsplash.com/@exampleuser"}
          }
        }
        """;
        var ph = SocialMailParsers.ParseUnsplashPhoto(json);
        Assert.NotNull(ph);
        Assert.Equal("Dwu85P9SOIk", ph!.Id);
        Assert.EndsWith("w=400&fit=max", ph.SmallUrl);
        Assert.EndsWith("w=1080&fit=max", ph.RegularUrl);
        Assert.EndsWith("q=75&fm=jpg", ph.FullUrl);
        Assert.Equal("Joe Example", ph.UserName);
        Assert.Equal("https://unsplash.com/@exampleuser", ph.UserLink);
        Assert.Equal("https://unsplash.com/photos/Dwu85P9SOIk", ph.PhotoLink);
        Assert.Equal("https://api.unsplash.com/photos/Dwu85P9SOIk/download?ixid=abc", ph.DownloadLocation);
        Assert.Equal("#6E633A", ph.Color);
        Assert.Equal("A man drinking a coffee.", ph.Description);
    }

    [Fact]
    public void Unsplash_photo_without_urls_is_null()
    {
        Assert.Null(SocialMailParsers.ParseUnsplashPhoto("""{"id":"x","user":{"name":"n"}}"""));
        Assert.Null(SocialMailParsers.ParseUnsplashPhoto("[]"));
    }

    [Theory]
    [InlineData("https://unsplash.com/@joe", "https://unsplash.com/@joe?utm_source=dinamikada&utm_medium=referral")]
    [InlineData("https://unsplash.com/photos/x?a=1", "https://unsplash.com/photos/x?a=1&utm_source=dinamikada&utm_medium=referral")]
    public void Unsplash_referral_is_appended(string url, string expected)
    {
        Assert.Equal(expected, SocialMailParsers.WithUnsplashReferral(url));
    }

    [Theory]
    [InlineData("Dwu85P9SOIk", "unsplash-Dwu85P9SOIk.jpg")]
    [InlineData("../evil\\id", "unsplash-evilid.jpg")]
    public void Unsplash_wallpaper_file_name_is_safe(string id, string expected)
    {
        Assert.Equal(expected, SocialMailParsers.UnsplashWallpaperFileName(id));
    }
}
