using System.Text.Json;
using DinamikAda.Core.Voice;
using Xunit;

namespace DinamikAda.Tests;

public sealed class VoiceIntentTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 10, 0, 0); // Thursday

    [Fact]
    public void Schedule_OpenAmazonOnDate_Tr()
    {
        const string json = """
        {"transcript":"10 Kasım saat 11'de Amazon'u aç","reply":"Tamam, 10 Kasım 11:00'de Amazon'u açacağım.",
         "intents":[{"type":"schedule","run_at":"2026-11-10T11:00:00","actions":[{"type":"open_url","url":"https://www.amazon.com.tr"}]}]}
        """;
        var plan = VoiceIntents.Parse(json, Now);
        Assert.Equal("10 Kasım saat 11'de Amazon'u aç", plan.Transcript);
        Assert.StartsWith("Tamam", plan.Reply);
        var task = Assert.Single(plan.Tasks);
        Assert.Equal(new DateTime(2026, 11, 10, 11, 0, 0), task.RunAtLocal);
        Assert.Equal("10 Kasım saat 11'de Amazon'u aç", task.Title);
        var action = Assert.Single(task.Actions);
        Assert.Equal(VoiceActionKind.OpenUrl, action.Kind);
        Assert.Equal("https://www.amazon.com.tr", action.Target);
        Assert.Empty(plan.Actions);
        Assert.True(plan.HasWork);
    }

    [Fact]
    public void CreateRoutine_EveryMorning_Tr()
    {
        const string json = """
        {"transcript":"her sabah 9'da Unity ve Spotify'ı aç","reply":"Her sabah 9'da Unity ve Spotify açılacak.",
         "intents":[{"type":"create_routine","routine_name":"Sabah rutini","time":"9:00","days":["every day"],
           "actions":[{"type":"open_app","app":"Unity"},{"type":"open_app","app":"Spotify'ı"}]}]}
        """;
        var plan = VoiceIntents.Parse(json, Now);
        var r = Assert.Single(plan.Routines);
        Assert.Equal("Sabah rutini", r.Name);
        Assert.Equal("09:00", r.TimeOfDay);
        Assert.Equal(127, r.DaysMask);
        Assert.True(r.Enabled);
        Assert.Equal(new[] { "Unity", "Spotify" }, r.Actions.Select(a => a.Target));
        Assert.All(r.Actions, a => Assert.Equal(VoiceActionKind.OpenApp, a.Kind));
    }

    [Fact]
    public void CreateRoutine_Weekdays_En_DaysAsString_NameDerived()
    {
        const string json = """
        {"transcript":"Every weekday at 8:30 open Outlook","reply":"Done.",
         "intents":[{"type":"create_routine","time":"08:30","days":"weekdays","actions":[{"type":"open_app","app":"Outlook"}]}]}
        """;
        var r = Assert.Single(VoiceIntents.Parse(json, Now).Routines);
        Assert.Equal(31, r.DaysMask);
        Assert.Equal("08:30", r.TimeOfDay);
        Assert.Equal("Outlook", r.Name);
    }

    [Fact]
    public void CreateRoutine_WithoutValidTimeOrActions_IsIgnored()
    {
        const string json = """
        {"intents":[{"type":"create_routine","time":"morning","actions":[{"type":"open_app","app":"X"}]},
                    {"type":"create_routine","time":"9:00","actions":[]}]}
        """;
        Assert.Empty(VoiceIntents.Parse(json, Now).Routines);
    }

    [Fact]
    public void WebSearch_YouTube_Tr()
    {
        const string json = """{"transcript":"YouTube'da lo-fi müzik ara","reply":"","intents":[{"type":"web_search","site":"YouTube","query":"lo-fi müzik"}]}""";
        var a = Assert.Single(VoiceIntents.Parse(json, Now).Actions);
        Assert.Equal(VoiceActionKind.WebSearch, a.Kind);
        Assert.Equal("youtube", a.Target);
        Assert.Equal("lo-fi müzik", a.Query);
    }

    [Fact]
    public void AddTodo_OddCasing()
    {
        const string json = """{"Transcript":"yapılacaklara süt almayı ekle","Reply":"Ekledim.","Intents":[{"Type":"ADD-TODO","Text":"Süt al"}]}""";
        var plan = VoiceIntents.Parse(json, Now);
        var a = Assert.Single(plan.Actions);
        Assert.Equal(VoiceActionKind.AddTodo, a.Kind);
        Assert.Equal("Süt al", a.Query);
        Assert.Equal("Ekledim.", plan.Reply);
    }

    [Fact]
    public void AddTodo_FallsBackToQuery()
    {
        const string json = """{"intents":[{"type":"add_todo","query":"ekmek"}]}""";
        Assert.Equal("ekmek", Assert.Single(VoiceIntents.Parse(json, Now).Actions).Query);
    }

    [Theory]
    [InlineData("\"25\"", "25")]
    [InlineData("25", "25")]
    [InlineData("1.5", "2")]
    [InlineData("9999", "600")]
    public void StartTimer_MinutesFromStringOrNumber(string minutes, string expected)
    {
        string json = $$"""{"transcript":"25 dakikalık zamanlayıcı kur","intents":[{"type":"start_timer","minutes":{{minutes}},"text":"Odak"}]}""";
        var a = Assert.Single(VoiceIntents.Parse(json, Now).Actions);
        Assert.Equal(VoiceActionKind.StartTimer, a.Kind);
        Assert.Equal(expected, a.Query);
        Assert.Equal("Odak", a.Target);
    }

    [Fact]
    public void StartTimer_WithoutMinutes_IsIgnored()
    {
        Assert.Empty(VoiceIntents.Parse("""{"intents":[{"type":"start_timer"}]}""", Now).Actions);
    }

    [Theory]
    [InlineData("next", VoiceActionKind.MediaNext)]
    [InlineData("Previous", VoiceActionKind.MediaPrevious)]
    [InlineData("prev", VoiceActionKind.MediaPrevious)]
    [InlineData("play_pause", VoiceActionKind.MediaPlayPause)]
    [InlineData("pause", VoiceActionKind.MediaPlayPause)]
    [InlineData("", VoiceActionKind.MediaPlayPause)]
    public void Media_Commands(string cmd, VoiceActionKind kind)
    {
        string json = $$"""{"intents":[{"type":"media","query":"{{cmd}}"}]}""";
        Assert.Equal(kind, Assert.Single(VoiceIntents.Parse(json, Now).Actions).Kind);
    }

    [Fact]
    public void Reminder_WithRunAt_BecomesNotifyTask()
    {
        const string json = """
        {"transcript":"remind me in 10 minutes to call mom","reply":"Sure.",
         "intents":[{"type":"reminder","text":"Call mom","run_at":"2026-11-05 10:10"}]}
        """;
        var plan = VoiceIntents.Parse(json, Now);
        var t = Assert.Single(plan.Tasks);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 10, 0), t.RunAtLocal);
        Assert.Equal("Call mom", t.Title);
        var a = Assert.Single(t.Actions);
        Assert.Equal(VoiceActionKind.Notify, a.Kind);
        Assert.Equal("Call mom", a.Query);
    }

    [Fact]
    public void Reminder_WithMinutesOnly_UsesNow()
    {
        const string json = """{"intents":[{"type":"reminder","text":"Çay","minutes":5}]}""";
        Assert.Equal(Now.AddMinutes(5), Assert.Single(VoiceIntents.Parse(json, Now).Tasks).RunAtLocal);
    }

    [Fact]
    public void Reminder_WithoutTime_BecomesTodo()
    {
        const string json = """{"intents":[{"type":"reminder","text":"Faturayı öde"}]}""";
        var a = Assert.Single(VoiceIntents.Parse(json, Now).Actions);
        Assert.Equal(VoiceActionKind.AddTodo, a.Kind);
        Assert.Equal("Faturayı öde", a.Query);
    }

    [Fact]
    public void Schedule_InThePast_IsSkippedAndCounted()
    {
        const string json = """{"intents":[{"type":"schedule","run_at":"2026-11-01T09:00:00","actions":[{"type":"open_app","app":"Unity"}]}]}""";
        var plan = VoiceIntents.Parse(json, Now);
        Assert.Empty(plan.Tasks);
        Assert.Equal(1, plan.SkippedPast);
    }

    [Fact]
    public void Schedule_NestedScheduleAndRoutineAreDropped_ReminderBecomesNotify()
    {
        const string json = """
        {"intents":[{"type":"schedule","run_at":"2026-11-06T08:00","actions":[
            {"type":"schedule","run_at":"2026-11-07T08:00"},{"type":"create_routine","time":"9:00"},
            {"type":"reminder","text":"Toplantı"},{"type":"media","query":"play_pause"}]}]}
        """;
        var t = Assert.Single(VoiceIntents.Parse(json, Now).Tasks);
        Assert.Equal(new[] { VoiceActionKind.Notify, VoiceActionKind.MediaPlayPause }, t.Actions.Select(a => a.Kind));
        Assert.Equal("Toplantı", t.Actions[0].Query);
    }

    [Fact]
    public void Schedule_WithoutActions_IsIgnored()
    {
        Assert.Empty(VoiceIntents.Parse("""{"intents":[{"type":"schedule","run_at":"2026-11-06T08:00"}]}""", Now).Tasks);
    }

    [Fact]
    public void ListAndDeleteRoutines()
    {
        const string json = """{"intents":[{"type":"list_routines"},{"type":"delete_routine","routine_name":"Sabah"}]}""";
        var plan = VoiceIntents.Parse(json, Now);
        Assert.True(plan.ListRoutines);
        Assert.Equal(new[] { "Sabah" }, plan.DeleteRoutines);
        Assert.True(plan.HasWork);
    }

    [Fact]
    public void Answer_IsNotWork()
    {
        const string json = """{"transcript":"Fransa'nın başkenti ne?","reply":"Paris.","intents":[{"type":"answer"}]}""";
        var plan = VoiceIntents.Parse(json, Now);
        Assert.True(plan.IsAnswer);
        Assert.False(plan.HasWork);
        Assert.Equal("Paris.", plan.Reply);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"intents\":null}")]
    [InlineData("{\"intents\":[null, 5, \"x\", {\"type\":null}, {\"type\":\"unknown_kind\"}]}")]
    [InlineData("{\"intents\":{\"type\":\"open_app\"}}")]
    public void Parse_IsRobustToGarbage(string json)
    {
        var plan = VoiceIntents.Parse(json, Now);
        Assert.False(plan.HasWork);
        Assert.NotNull(plan.Reply);
        Assert.NotNull(plan.Transcript);
    }

    [Fact]
    public void Parse_StripsFences_AndOpenAppFallsBackToText()
    {
        const string json = "```json\n{\"intents\":[{\"type\":\"Open App\",\"text\":\"Discord\"}]}\n```";
        var a = Assert.Single(VoiceIntents.Parse(json, Now).Actions);
        Assert.Equal(VoiceActionKind.OpenApp, a.Kind);
        Assert.Equal("Discord", a.Target);
    }

    [Fact]
    public void OpenUrl_NormalizesAndRejectsUnsafeSchemes()
    {
        const string json = """
        {"intents":[{"type":"open_url","url":"amazon.com.tr"},{"type":"open_url","url":"javascript:alert(1)"},
                    {"type":"open_url","url":"file:///C:/Windows"},{"type":"open_url","url":"HTTP://Example.com/a b"}]}
        """;
        var actions = VoiceIntents.Parse(json, Now).Actions;
        Assert.Equal(2, actions.Count);
        Assert.Equal("https://amazon.com.tr", actions[0].Target);
        Assert.StartsWith("http://example.com/a", actions[1].Target, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("amazon.com.tr", "https://amazon.com.tr")]
    [InlineData("https://github.com/x", "https://github.com/x")]
    [InlineData("www.youtube.com", "https://www.youtube.com")]
    [InlineData("amazon", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ftp://x.com", null)]
    [InlineData("", null)]
    public void NormalizeUrl(string input, string? expected) => Assert.Equal(expected, VoiceIntents.NormalizeUrl(input));

    [Theory]
    [InlineData("youtube", "lo-fi müzik", "https://www.youtube.com/results?search_query=lo-fi%20m%C3%BCzik")]
    [InlineData("YouTube", "a&b=c", "https://www.youtube.com/results?search_query=a%26b%3Dc")]
    [InlineData("google", "hava durumu", "https://www.google.com/search?q=hava%20durumu")]
    [InlineData("amazon", "usb c", "https://www.amazon.com/s?k=usb%20c")]
    [InlineData("amazon_tr", "kulaklık", "https://www.amazon.com.tr/s?k=kulakl%C4%B1k")]
    [InlineData("github", "dotnet wpf", "https://github.com/search?q=dotnet%20wpf")]
    [InlineData("maps", "Kadıköy", "https://www.google.com/maps/search/?api=1&query=Kad%C4%B1k%C3%B6y")]
    [InlineData("spotify", "lo fi", "https://open.spotify.com/search/lo%20fi")]
    [InlineData("", "test", "https://www.google.com/search?q=test")]
    [InlineData(null, "test", "https://www.google.com/search?q=test")]
    [InlineData("unknownsite", "test", "https://www.google.com/search?q=test")]
    [InlineData("example.org", "test", "https://www.google.com/search?q=site%3Aexample.org%20test")]
    public void BuildSearchUrl(string? site, string query, string expected) =>
        Assert.Equal(expected, VoiceIntents.BuildSearchUrl(site, query));

    [Fact]
    public void BuildSearchUrl_WikipediaUsesLanguage()
    {
        Assert.Equal("https://tr.wikipedia.org/w/index.php?search=Atat%C3%BCrk", VoiceIntents.BuildSearchUrl("wikipedia", "Atatürk", "tr"));
        Assert.Equal("https://en.wikipedia.org/w/index.php?search=Moon", VoiceIntents.BuildSearchUrl("Wikipedia", "Moon", "en-US"));
    }

    [Theory]
    [InlineData("Spotify'ı", "Spotify")]
    [InlineData("Amazon’u", "Amazon")]
    [InlineData("  Visual Studio Code ", "Visual Studio Code")]
    [InlineData("\"Unity\"", "Unity")]
    public void CleanName(string input, string expected) => Assert.Equal(expected, VoiceIntents.CleanName(input));

    [Fact]
    public void FindBestNameMatch_PrefersExactThenPrefixThenContainsThenFuzzy()
    {
        var names = new[] { "Unity Hub", "Unity 2022.3", "Spotify", "Microsoft Outlook", "Visual Studio Code", "Discord" };
        Assert.Equal(2, VoiceIntents.FindBestNameMatch("spotify", names));
        Assert.Equal(0, VoiceIntents.FindBestNameMatch("Unity Hub", names));
        Assert.Equal(3, VoiceIntents.FindBestNameMatch("outlook", names));
        Assert.Equal(4, VoiceIntents.FindBestNameMatch("visual studio", names));
        Assert.Equal(5, VoiceIntents.FindBestNameMatch("Dicsord", names)); // typo
        Assert.Equal(-1, VoiceIntents.FindBestNameMatch("Photoshop", names));
        Assert.Equal(-1, VoiceIntents.FindBestNameMatch("", names));
        Assert.Equal(0, VoiceIntents.FindBestNameMatch("ÜNİTY", new[] { "unity" }));
    }

    [Fact]
    public void FindRoutine_ByNameFuzzy()
    {
        var routines = new List<VoiceRoutine>
        {
            new() { Name = "Sabah rutini" }, new() { Name = "Akşam" }
        };
        Assert.Same(routines[0], VoiceIntents.FindRoutine(routines, "sabah"));
        Assert.Same(routines[1], VoiceIntents.FindRoutine(routines, "akşam rutini"));
        Assert.Null(VoiceIntents.FindRoutine(routines, "öğle"));
    }

    [Fact]
    public void SystemPrompt_IncludesContext()
    {
        var ctx = new VoicePromptContext(new DateTime(2026, 11, 19, 14, 30, 0), "Türkiye Saati (UTC+03:00)", "tr-TR",
            new[] { "Spotify", "Unity Hub" }, "Ada", 2, new[] { "Sabah rutini" });
        string p = VoiceIntents.BuildSystemPrompt(ctx);
        Assert.Contains("2026-11-19", p);
        Assert.Contains("14:30", p);
        Assert.Contains("Thursday", p);
        Assert.Contains("UTC+03:00", p);
        Assert.Contains("tr-TR", p);
        Assert.Contains("Spotify", p);
        Assert.Contains("Unity Hub", p);
        Assert.Contains("Ada", p);
        Assert.Contains("playful", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sabah rutini", p);
        Assert.Contains("run_at", p);
    }

    [Theory]
    [InlineData(0, "friendly")]
    [InlineData(1, "concise")]
    [InlineData(2, "playful")]
    public void SystemPrompt_RespectsPersonality(int personality, string word)
    {
        var ctx = new VoicePromptContext(Now, "UTC", "en-US", Array.Empty<string>(), "Ada", personality, Array.Empty<string>());
        Assert.Contains(word, VoiceIntents.BuildSystemPrompt(ctx), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResponseSchema_IsValidGeminiSubset()
    {
        using var doc = JsonDocument.Parse(VoiceIntents.ResponseSchemaJson);
        var root = doc.RootElement;
        Assert.Equal("OBJECT", root.GetProperty("type").GetString());
        var intent = root.GetProperty("properties").GetProperty("intents").GetProperty("items");
        var types = intent.GetProperty("properties").GetProperty("type").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        foreach (var t in new[] { "open_app", "open_url", "web_search", "add_todo", "start_timer", "media", "reminder", "schedule", "create_routine", "list_routines", "delete_routine", "answer" })
            Assert.Contains(t, types);
        Assert.Equal("ARRAY", intent.GetProperty("properties").GetProperty("actions").GetProperty("type").GetString());
        // Only the Gemini OpenAPI subset of type names is used.
        var allowed = new HashSet<string> { "OBJECT", "ARRAY", "STRING", "INTEGER", "NUMBER", "BOOLEAN" };
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "type" && p.Value.ValueKind == JsonValueKind.String) Assert.Contains(p.Value.GetString()!, allowed);
                    Assert.DoesNotContain("$ref", p.Name);
                    Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray()) Walk(x);
            }
        }
        // "type" inside properties is a property name, not a schema type: skip that level by walking schema nodes only.
        Walk(root.GetProperty("properties").GetProperty("intents"));
    }

    [Fact]
    public void Summarize_DescribesWhatWasPlanned()
    {
        const string json = """
        {"intents":[{"type":"open_app","app":"Spotify"},{"type":"start_timer","minutes":25},
                    {"type":"web_search","site":"youtube","query":"lofi"}]}
        """;
        var plan = VoiceIntents.Parse(json, Now);
        string tr = VoiceIntents.Summarize(plan, "tr");
        Assert.Contains("Spotify", tr);
        Assert.Contains("25", tr);
        Assert.Contains("lofi", tr);
        string en = VoiceIntents.Summarize(plan, "en");
        Assert.Contains("Opened Spotify", en);
    }

    [Fact]
    public void FormatRoutineList()
    {
        var routines = new[]
        {
            new VoiceRoutine { Name = "Sabah", TimeOfDay = "09:00", DaysMask = 31 },
            new VoiceRoutine { Name = "Hafta sonu", TimeOfDay = "10:30", DaysMask = 96, Enabled = false }
        };
        string tr = VoiceIntents.FormatRoutineList(routines, "tr");
        Assert.Contains("Sabah", tr);
        Assert.Contains("09:00", tr);
        Assert.Contains("Hafta içi", tr);
        Assert.Contains("kapalı", tr);
        Assert.Equal("Henüz rutin yok.", VoiceIntents.FormatRoutineList(Array.Empty<VoiceRoutine>(), "tr"));
        Assert.Equal("No routines yet.", VoiceIntents.FormatRoutineList(Array.Empty<VoiceRoutine>(), "en"));
    }

    [Fact]
    public void ResultDisplayTime_ScalesWithLength()
    {
        Assert.Equal(TimeSpan.FromSeconds(2.5), VoiceIntents.ResultDisplayTime("Tamam.", false));
        Assert.Equal(TimeSpan.FromSeconds(5), VoiceIntents.ResultDisplayTime(new string('a', 400), false));
        var longAnswer = VoiceIntents.ResultDisplayTime(new string('a', 600), true);
        Assert.True(longAnswer > TimeSpan.FromSeconds(5));
        Assert.True(longAnswer <= TimeSpan.FromSeconds(12));
    }
}
