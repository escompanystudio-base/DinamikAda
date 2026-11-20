using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DinamikAda.Core.Voice;

/// <summary>What the command prompt tells Gemini about the user's situation.</summary>
public sealed record VoicePromptContext(
    DateTime NowLocal,
    string TimeZoneName,
    string SpeechLanguage,
    IReadOnlyList<string> AppHints,
    string MascotName,
    int Personality,
    IReadOnlyList<string> RoutineNames);

/// <summary>A parsed voice command: what to do now, what to schedule, and what to say.</summary>
public sealed class VoiceCommandPlan
{
    public string Transcript { get; set; } = string.Empty;
    public string Reply { get; set; } = string.Empty;
    /// <summary>Actions to run right away.</summary>
    public List<VoiceAction> Actions { get; } = new();
    /// <summary>One-off tasks (schedules and timed reminders) to store.</summary>
    public List<ScheduledVoiceTask> Tasks { get; } = new();
    /// <summary>New routines to store.</summary>
    public List<VoiceRoutine> Routines { get; } = new();
    /// <summary>Names of routines the user wants deleted.</summary>
    public List<string> DeleteRoutines { get; } = new();
    public bool ListRoutines { get; set; }
    /// <summary>The user asked a general question; the answer is <see cref="Reply"/>.</summary>
    public bool IsAnswer { get; set; }
    /// <summary>Schedules dropped because their time had already passed.</summary>
    public int SkippedPast { get; set; }

    public bool HasWork => Actions.Count > 0 || Tasks.Count > 0 || Routines.Count > 0 || DeleteRoutines.Count > 0 || ListRoutines;
}

/// <summary>Prompt, response schema and parsing for Alt+Win voice commands (pure, unit tested).</summary>
public static class VoiceIntents
{
    private const int MaxActions = 10;

    /// <summary>Gemini responseSchema (OpenAPI subset) for the command reply.</summary>
    public const string ResponseSchemaJson = """
        {
          "type": "OBJECT",
          "properties": {
            "transcript": { "type": "STRING" },
            "reply": { "type": "STRING" },
            "intents": {
              "type": "ARRAY",
              "items": {
                "type": "OBJECT",
                "properties": {
                  "type": { "type": "STRING", "enum": ["open_app","open_url","web_search","add_todo","start_timer","media","reminder","schedule","create_routine","list_routines","delete_routine","answer"] },
                  "app": { "type": "STRING" },
                  "url": { "type": "STRING" },
                  "site": { "type": "STRING" },
                  "query": { "type": "STRING" },
                  "minutes": { "type": "NUMBER" },
                  "text": { "type": "STRING" },
                  "run_at": { "type": "STRING" },
                  "routine_name": { "type": "STRING" },
                  "time": { "type": "STRING" },
                  "days": { "type": "ARRAY", "items": { "type": "STRING" } },
                  "actions": {
                    "type": "ARRAY",
                    "items": {
                      "type": "OBJECT",
                      "properties": {
                        "type": { "type": "STRING", "enum": ["open_app","open_url","web_search","add_todo","start_timer","media","reminder"] },
                        "app": { "type": "STRING" },
                        "url": { "type": "STRING" },
                        "site": { "type": "STRING" },
                        "query": { "type": "STRING" },
                        "minutes": { "type": "NUMBER" },
                        "text": { "type": "STRING" }
                      },
                      "required": ["type"]
                    }
                  }
                },
                "required": ["type"]
              }
            }
          },
          "required": ["transcript", "reply", "intents"]
        }
        """;

    /// <summary>System instruction for the command request.</summary>
    public static string BuildSystemPrompt(VoicePromptContext ctx)
    {
        var inv = CultureInfo.InvariantCulture;
        string tone = ctx.Personality switch
        {
            1 => "concise: as few words as possible, no small talk",
            2 => "playful: light, witty and cheerful, but still short",
            _ => "friendly: warm and natural, short"
        };
        string name = string.IsNullOrWhiteSpace(ctx.MascotName) ? "Ada" : ctx.MascotName.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"You are {name}, the voice assistant of a Windows desktop app. The user holds a key, speaks a command, and releases it. The audio is attached.");
        sb.AppendLine($"Current local date and time: {ctx.NowLocal.ToString("yyyy-MM-dd HH:mm", inv)} ({ctx.NowLocal.DayOfWeek.ToString()}), time zone: {ctx.TimeZoneName}.");
        sb.AppendLine($"Speech language: {ctx.SpeechLanguage}. Write \"transcript\" exactly as spoken and write \"reply\" in that language.");
        sb.AppendLine($"Tone of the reply: {tone}.");
        if (ctx.AppHints.Count > 0)
            sb.AppendLine("Apps pinned on the user's dock (use these exact names for open_app when they match): " + string.Join(", ", ctx.AppHints.Take(40)) + ".");
        if (ctx.RoutineNames.Count > 0)
            sb.AppendLine("Existing routines: " + string.Join(", ", ctx.RoutineNames.Take(40)) + ".");
        sb.AppendLine("""
            Turn the request into "intents". One sentence may hold several intents. Types:
            - open_app: start an installed program; "app" = the program's plain name without suffixes ("Spotify", not "Spotify'ı").
            - open_url: open a website; "url" = full https URL (e.g. "Amazon'u aç" -> https://www.amazon.com.tr for Turkish, https://www.amazon.com otherwise). Prefer open_url for websites unless the name is an installed app above.
            - web_search: "site" = youtube|google|amazon|amazon_tr|wikipedia|github|maps|spotify|bing|duckduckgo|reddit|x|stackoverflow|trendyol|hepsiburada, "query" = the search text.
            - add_todo: "text" = the to-do item, short (e.g. "yapılacaklara süt almayı ekle" -> "Süt al").
            - start_timer: "minutes" = duration in minutes, optional "text" = label.
            - media: "query" = play_pause|next|previous.
            - reminder: "text" = what to remind, "run_at" = when (or "minutes" from now).
            - schedule: run "actions" once at "run_at" (e.g. "10 Kasım saat 11'de Amazon'u aç").
            - create_routine: repeat "actions" at "time" (HH:mm, 24h) on "days" (mon,tue,wed,thu,fri,sat,sun, or "every day", "weekdays", "weekend"); optional "routine_name" (short, in the speech language).
            - list_routines: show the user's routines.
            - delete_routine: "routine_name" = which routine to delete.
            - answer: a general question or chat; put the answer in "reply".
            Rules:
            - "run_at" is local time in ISO format yyyy-MM-ddTHH:mm:ss without a time zone offset. Resolve relative dates ("yarın", "in 10 minutes", "next Friday") from the current time above. If a date without a year has already passed this year, use next year. "sabah 9" = 09:00, "akşam 7" = 19:00.
            - Nested "actions" may only use: open_app, open_url, web_search, add_todo, start_timer, media, reminder.
            - "reply" confirms what will happen in one short sentence (for answer: at most about 80 words, plain text, no markdown).
            - Never invent actions the user did not ask for. If the request is unclear, return no intents and ask briefly in "reply".
            """);
        return sb.ToString();
    }

    /// <summary>
    /// Parses Gemini's JSON into a plan. Never throws: garbage gives an empty plan. Property names and type values are
    /// matched case-insensitively ("ADD-TODO", "Open App" work); missing fields are tolerated.
    /// </summary>
    public static VoiceCommandPlan Parse(string? json, DateTime nowLocal)
    {
        var plan = new VoiceCommandPlan();
        if (string.IsNullOrWhiteSpace(json)) return plan;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(GeminiJson.StripFences(json));
        }
        catch (JsonException)
        {
            return plan;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return plan;
            plan.Transcript = Str(root, "transcript");
            plan.Reply = Str(root, "reply");
            if (Prop(root, "intents") is not { ValueKind: JsonValueKind.Array } intents) return plan;
            foreach (var intent in intents.EnumerateArray())
            {
                if (intent.ValueKind != JsonValueKind.Object) continue;
                ParseTopIntent(plan, intent, nowLocal);
            }
        }
        return plan;
    }

    private static void ParseTopIntent(VoiceCommandPlan plan, JsonElement e, DateTime now)
    {
        string type = NormType(Str(e, "type"));
        switch (type)
        {
            case "schedule":
            {
                var actions = ParseNested(e, now);
                if (actions.Count == 0) return;
                if (ParseLocalDateTime(Str(e, "run_at")) is not { } at)
                {
                    if (Num(e, "minutes") is { } m && m > 0) at = now.AddMinutes(m);
                    else return;
                }
                if (at < now.AddMinutes(-2))
                {
                    plan.SkippedPast++;
                    return;
                }
                plan.Tasks.Add(new ScheduledVoiceTask
                {
                    Title = FirstNonEmpty(Str(e, "text"), plan.Transcript, string.Join(", ", actions.Select(a => DescribeShort(a)))),
                    RunAtLocal = at,
                    Actions = actions
                });
                return;
            }
            case "reminder":
            {
                string text = FirstNonEmpty(Str(e, "text"), Str(e, "query"), plan.Transcript);
                if (text.Length == 0) return;
                DateTime? at = ParseLocalDateTime(Str(e, "run_at"));
                if (at is null && Num(e, "minutes") is { } m && m > 0) at = now.AddMinutes(m);
                if (at is null)
                {
                    plan.Actions.Add(new VoiceAction { Kind = VoiceActionKind.AddTodo, Query = Trunc(text, 200) });
                    return;
                }
                if (at < now.AddMinutes(-2))
                {
                    plan.SkippedPast++;
                    return;
                }
                plan.Tasks.Add(new ScheduledVoiceTask
                {
                    Title = Trunc(text, 200),
                    RunAtLocal = at.Value,
                    Actions = { new VoiceAction { Kind = VoiceActionKind.Notify, Query = Trunc(text, 200) } }
                });
                return;
            }
            case "create_routine":
            {
                var actions = ParseNested(e, now);
                string? time = ScheduleMath.NormalizeTimeOfDay(Str(e, "time"));
                if (actions.Count == 0 || time is null) return;
                string name = FirstNonEmpty(Str(e, "routine_name"), Str(e, "text"), string.Join(", ", actions.Select(a => DescribeShort(a))));
                plan.Routines.Add(new VoiceRoutine
                {
                    Name = Trunc(name, 60),
                    TimeOfDay = time,
                    DaysMask = ScheduleMath.ParseDaysMask(Days(e)),
                    Actions = actions,
                    Enabled = true
                });
                return;
            }
            case "list_routines":
                plan.ListRoutines = true;
                return;
            case "delete_routine":
            {
                string name = FirstNonEmpty(Str(e, "routine_name"), Str(e, "text"), Str(e, "query"));
                if (name.Length > 0) plan.DeleteRoutines.Add(name);
                return;
            }
            case "answer":
                plan.IsAnswer = true;
                return;
            default:
                if (ParseAction(type, e) is { } a && plan.Actions.Count < MaxActions) plan.Actions.Add(a);
                return;
        }
    }

    private static List<VoiceAction> ParseNested(JsonElement e, DateTime now)
    {
        var list = new List<VoiceAction>();
        if (Prop(e, "actions") is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var x in arr.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object || list.Count >= MaxActions) continue;
            string type = NormType(Str(x, "type"));
            if (type == "reminder")
            {
                // Inside a schedule or routine a reminder fires at that time.
                string text = FirstNonEmpty(Str(x, "text"), Str(x, "query"));
                if (text.Length > 0) list.Add(new VoiceAction { Kind = VoiceActionKind.Notify, Query = Trunc(text, 200) });
                continue;
            }
            if (ParseAction(type, x) is { } a) list.Add(a);
        }
        return list;
    }

    private static VoiceAction? ParseAction(string type, JsonElement e)
    {
        switch (type)
        {
            case "open_app":
            {
                string app = CleanName(FirstNonEmpty(Str(e, "app"), Str(e, "text"), Str(e, "query")));
                return app.Length == 0 ? null : new VoiceAction { Kind = VoiceActionKind.OpenApp, Target = Trunc(app, 120) };
            }
            case "open_url":
            {
                string? url = NormalizeUrl(FirstNonEmpty(Str(e, "url"), Str(e, "text"), Str(e, "query")));
                return url is null ? null : new VoiceAction { Kind = VoiceActionKind.OpenUrl, Target = url };
            }
            case "web_search":
            {
                string q = FirstNonEmpty(Str(e, "query"), Str(e, "text"));
                if (q.Length == 0) return null;
                string site = Str(e, "site").Trim().ToLowerInvariant();
                return new VoiceAction { Kind = VoiceActionKind.WebSearch, Target = site, Query = Trunc(q, 300) };
            }
            case "add_todo":
            {
                string text = FirstNonEmpty(Str(e, "text"), Str(e, "query"));
                return text.Length == 0 ? null : new VoiceAction { Kind = VoiceActionKind.AddTodo, Query = Trunc(text, 200) };
            }
            case "start_timer":
            {
                double? m = Num(e, "minutes");
                if (m is null || m <= 0 || !double.IsFinite(m.Value)) return null;
                int minutes = (int)Math.Clamp(Math.Round(m.Value, MidpointRounding.AwayFromZero), 1, 600);
                return new VoiceAction
                {
                    Kind = VoiceActionKind.StartTimer,
                    Target = Trunc(Str(e, "text"), 60),
                    Query = minutes.ToString(CultureInfo.InvariantCulture)
                };
            }
            case "media":
            {
                string cmd = NormType(FirstNonEmpty(Str(e, "query"), Str(e, "text")));
                var kind = cmd switch
                {
                    "next" or "skip" or "next_track" or "sonraki" => VoiceActionKind.MediaNext,
                    "previous" or "prev" or "back" or "previous_track" or "onceki" or "önceki" => VoiceActionKind.MediaPrevious,
                    _ => VoiceActionKind.MediaPlayPause
                };
                return new VoiceAction { Kind = kind };
            }
            default:
                return null;
        }
    }

    /// <summary>Local DateTime from ISO-ish text ("2026-11-10T11:00:00", "2026-11-10 11:00"); offsets convert to local.</summary>
    public static DateTime? ParseLocalDateTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        bool hasOffset = t.EndsWith('Z') || t.EndsWith('z') ||
                         (t.Length > 19 && (t.LastIndexOf('+') > 10 || t.LastIndexOf('-') > 10));
        if (hasOffset && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return DateTime.SpecifyKind(dto.LocalDateTime, DateTimeKind.Unspecified);
        string[] formats =
        {
            "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd"
        };
        if (DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        return null;
    }

    /// <summary>An http(s) URL, adding https:// to bare domains; null for other schemes or non-URLs.</summary>
    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string u = url.Trim();
        int colon = u.IndexOf(':');
        bool hasScheme = colon > 0 && u.IndexOf("://", StringComparison.Ordinal) == colon;
        if (!hasScheme)
        {
            // "javascript:..." / "mailto:..." have a scheme without "//": reject.
            if (colon > 0 && !u[..colon].Contains('.')) return null;
            if (!u.Contains('.') || u.Contains(' ') && u.IndexOf(' ') < u.IndexOf('.')) return null;
            u = "https://" + u;
        }
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Contains('.')) return null;
        string abs = uri.AbsoluteUri;
        return uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? abs.TrimEnd('/') : abs;
    }

    /// <summary>Search URL for a site ("youtube", "amazon_tr"…); unknown sites fall back to Google.</summary>
    public static string BuildSearchUrl(string? site, string query, string lang = "tr")
    {
        string q = Uri.EscapeDataString((query ?? string.Empty).Trim());
        string s = (site ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        string wikiLang = string.IsNullOrWhiteSpace(lang) ? "tr" : lang.Trim().Split('-', '_')[0].ToLowerInvariant();
        if (wikiLang.Length is < 2 or > 3 || !wikiLang.All(char.IsAsciiLetterLower)) wikiLang = "en";
        return s switch
        {
            "youtube" or "yt" => "https://www.youtube.com/results?search_query=" + q,
            "amazon" or "amazon_com" => "https://www.amazon.com/s?k=" + q,
            "amazon_tr" or "amazon_com_tr" or "amazon_türkiye" or "amazon_turkiye" => "https://www.amazon.com.tr/s?k=" + q,
            "wikipedia" or "wiki" or "vikipedi" => $"https://{wikiLang}.wikipedia.org/w/index.php?search=" + q,
            "github" => "https://github.com/search?q=" + q,
            "maps" or "google_maps" or "harita" or "haritalar" => "https://www.google.com/maps/search/?api=1&query=" + q,
            "spotify" => "https://open.spotify.com/search/" + q,
            "bing" => "https://www.bing.com/search?q=" + q,
            "duckduckgo" or "ddg" => "https://duckduckgo.com/?q=" + q,
            "reddit" => "https://www.reddit.com/search/?q=" + q,
            "x" or "twitter" => "https://x.com/search?q=" + q,
            "stackoverflow" or "stack_overflow" => "https://stackoverflow.com/search?q=" + q,
            "trendyol" => "https://www.trendyol.com/sr?q=" + q,
            "hepsiburada" => "https://www.hepsiburada.com/ara?q=" + q,
            "images" or "google_images" => "https://www.google.com/search?tbm=isch&q=" + q,
            _ when s.Contains('.') && !s.Contains('/') && Uri.CheckHostName(s) == UriHostNameType.Dns =>
                "https://www.google.com/search?q=" + Uri.EscapeDataString("site:" + s + " " + (query ?? string.Empty).Trim()),
            _ => "https://www.google.com/search?q=" + q
        };
    }

    /// <summary>Trims quotes and a Turkish suffix after an apostrophe ("Spotify'ı" → "Spotify").</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        string s = name.Trim().Trim('"', '“', '”', '«', '»').Trim();
        int ap = s.IndexOfAny(new[] { '\'', '’', '`' });
        if (ap > 0) s = s[..ap];
        return s.Trim();
    }

    /// <summary>
    /// Index of the candidate that best matches a spoken name, or -1. Order: exact, prefix, contains, query contains
    /// candidate, then a small typo distance. Case, Turkish letters, spaces and punctuation are ignored.
    /// </summary>
    public static int FindBestNameMatch(string? query, IReadOnlyList<string> candidates)
    {
        string q = Key(CleanName(query));
        if (q.Length == 0) return -1;
        int best = -1, bestScore = int.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            string c = Key(candidates[i]);
            if (c.Length == 0) continue;
            int score;
            if (c == q) score = 0;
            else if (c.StartsWith(q, StringComparison.Ordinal)) score = 100 + c.Length - q.Length;
            else if (c.Contains(q, StringComparison.Ordinal)) score = 200 + c.Length - q.Length;
            else if (q.Length >= 4 && c.Length >= 3 && q.Contains(c, StringComparison.Ordinal)) score = 300 + q.Length - c.Length;
            else if (q.Length >= 4 && Levenshtein(q, c) is var d && d <= (q.Length >= 8 ? 2 : 1)) score = 400 + d;
            else continue;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The routine whose name best matches what the user said, or null.</summary>
    public static VoiceRoutine? FindRoutine(IReadOnlyList<VoiceRoutine> routines, string? name)
    {
        int i = FindBestNameMatch(name, routines.Select(r => r.Name).ToList());
        if (i >= 0) return routines[i];
        // "akşam rutini" for a routine named "Akşam": try the first word.
        string first = CleanName(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (first.Length >= 3 && first != CleanName(name))
        {
            i = FindBestNameMatch(first, routines.Select(r => r.Name).ToList());
            if (i >= 0) return routines[i];
        }
        return null;
    }

    private static string Key(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s.ToLower(CultureInfo.GetCultureInfo("tr-TR")))
        {
            char c = ch switch
            {
                'ı' => 'i', 'ş' => 's', 'ç' => 'c', 'ğ' => 'g', 'ö' => 'o', 'ü' => 'u', 'â' => 'a', 'î' => 'i', 'û' => 'u',
                _ => ch
            };
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3) return 99;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                // Adjacent transposition counts as one edit ("Dicsord").
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) cur[j] = Math.Min(cur[j], prev[j - 1]);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Past-tense description of one action ("Spotify açıldı" / "Opened Spotify").</summary>
    public static string DescribeAction(VoiceAction a, string lang)
    {
        bool en = lang == "en";
        return a.Kind switch
        {
            VoiceActionKind.OpenApp => en ? $"Opened {a.Target}" : $"{a.Target} açıldı",
            VoiceActionKind.OpenUrl => en ? $"Opened {HostOf(a.Target)}" : $"{HostOf(a.Target)} açıldı",
            VoiceActionKind.WebSearch => en ? $"Searched \"{a.Query}\"{SiteSuffix(a.Target, true)}" : $"\"{a.Query}\" arandı{SiteSuffix(a.Target, false)}",
            VoiceActionKind.AddTodo => en ? $"Added to-do: {a.Query}" : $"Yapılacaklara eklendi: {a.Query}",
            VoiceActionKind.StartTimer => en ? $"{a.Query}-minute timer started" : $"{a.Query} dakikalık zamanlayıcı başladı",
            VoiceActionKind.MediaPlayPause => en ? "Play/pause" : "Oynat/duraklat",
            VoiceActionKind.MediaNext => en ? "Next track" : "Sonraki parça",
            VoiceActionKind.MediaPrevious => en ? "Previous track" : "Önceki parça",
            VoiceActionKind.Notify => en ? $"Reminder: {a.Query}" : $"Hatırlatma: {a.Query}",
            _ => a.Kind.ToString()
        };
    }

    private static string DescribeShort(VoiceAction a) => a.Kind switch
    {
        VoiceActionKind.OpenApp => a.Target,
        VoiceActionKind.OpenUrl => HostOf(a.Target),
        VoiceActionKind.WebSearch or VoiceActionKind.AddTodo or VoiceActionKind.Notify => a.Query,
        _ => a.Kind.ToString()
    };

    private static string SiteSuffix(string site, bool en)
    {
        if (string.IsNullOrWhiteSpace(site)) return string.Empty;
        string name = site switch
        {
            "youtube" => "YouTube", "google" => "Google", "amazon" or "amazon_tr" => "Amazon", "wikipedia" => "Wikipedia",
            "github" => "GitHub", "maps" => en ? "Maps" : "Haritalar", "spotify" => "Spotify",
            _ => site
        };
        return en ? $" on {name}" : $" ({name})";
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host) : url;

    /// <summary>A short summary of the plan, used when Gemini sent no reply.</summary>
    public static string Summarize(VoiceCommandPlan plan, string lang)
    {
        bool en = lang == "en";
        var parts = plan.Actions.Select(a => DescribeAction(a, lang)).ToList();
        foreach (var t in plan.Tasks)
            parts.Add(en ? $"Scheduled for {t.RunAtLocal.ToString("d MMM HH:mm", CultureInfo.GetCultureInfo("en-US"))}: {t.Title}"
                         : $"{t.RunAtLocal.ToString("d MMM HH:mm", CultureInfo.GetCultureInfo("tr-TR"))} için planlandı: {t.Title}");
        foreach (var r in plan.Routines)
            parts.Add(en ? $"Routine \"{r.Name}\": {ScheduleMath.DescribeDays(r.DaysMask, lang)} {r.TimeOfDay}"
                         : $"\"{r.Name}\" rutini: {ScheduleMath.DescribeDays(r.DaysMask, lang)} {r.TimeOfDay}");
        if (plan.SkippedPast > 0)
            parts.Add(en ? "That time has already passed." : "O zaman geçmiş.");
        return string.Join(" · ", parts);
    }

    /// <summary>One line per routine ("Sabah — Hafta içi 09:00"), or a "none yet" line.</summary>
    public static string FormatRoutineList(IEnumerable<VoiceRoutine> routines, string lang)
    {
        bool en = lang == "en";
        var lines = routines.Select(r =>
            $"{r.Name} — {ScheduleMath.DescribeDays(r.DaysMask, lang)} {r.TimeOfDay}" +
            (r.Enabled ? string.Empty : en ? " (off)" : " (kapalı)")).ToList();
        if (lines.Count == 0) return en ? "No routines yet." : "Henüz rutin yok.";
        return string.Join("\n", lines);
    }

    /// <summary>How long to show a result: 2.5–5 s by length, up to 12 s for long answers.</summary>
    public static TimeSpan ResultDisplayTime(string? text, bool isAnswer)
    {
        int len = text?.Length ?? 0;
        double secs = 2.5 + Math.Max(0, len - 40) / 40.0;
        return TimeSpan.FromSeconds(Math.Clamp(secs, 2.5, isAnswer ? 12 : 5));
    }

    // ---- JSON helpers ----------------------------------------------------------------------

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty(name, out var v)) return v;
        string plain = name.Replace("_", string.Empty);
        foreach (var p in e.EnumerateObject())
        {
            string n = p.Name.Replace("_", string.Empty).Replace("-", string.Empty);
            if (string.Equals(n, plain, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }
        return null;
    }

    private static string Str(JsonElement e, string name) => Prop(e, name) switch
    {
        { ValueKind: JsonValueKind.String } v => (v.GetString() ?? string.Empty).Trim(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => string.Empty
    };

    private static double? Num(JsonElement e, string name) => Prop(e, name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetDouble(out double d) => d,
        { ValueKind: JsonValueKind.String } v when double.TryParse((v.GetString() ?? string.Empty).Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
        _ => null
    };

    private static IEnumerable<string?> Days(JsonElement e) => Prop(e, "days") switch
    {
        { ValueKind: JsonValueKind.Array } arr => arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList(),
        { ValueKind: JsonValueKind.String } s => (s.GetString() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => Array.Empty<string?>()
    };

    private static string NormType(string s) =>
        s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string Trunc(string s, int max) => s.Length > max ? s[..max].TrimEnd() : s;
}
