namespace DinamikAda.Core.Voice;

/// <summary>Which speech engine turns the recording into text.</summary>
public enum VoiceEngine
{
    /// <summary>Gemini when a key is set, otherwise Windows speech recognition.</summary>
    Auto = 0,
    Gemini = 1,
    /// <summary>Offline Windows speech recognition (dictation only; no cleanup, no ask/commands).</summary>
    Windows = 2
}

/// <summary>What the user holds the keys for.</summary>
public enum VoiceMode
{
    /// <summary>Speak, release, and the cleaned-up text is typed where the cursor is.</summary>
    Dictation = 0,
    /// <summary>Circle something on screen while speaking a question; the answer appears next to it.</summary>
    Ask = 1,
    /// <summary>Speak a command: open apps, search, schedule, routines, todos, timers, media.</summary>
    Command = 2
}

public enum VoiceActionKind
{
    OpenApp = 0,
    OpenUrl = 1,
    /// <summary>Search <see cref="VoiceAction.Target"/> site ("youtube", "google", "amazon"…) for <see cref="VoiceAction.Query"/>.</summary>
    WebSearch = 2,
    AddTodo = 3,
    /// <summary>Countdown; <see cref="VoiceAction.Query"/> holds minutes.</summary>
    StartTimer = 4,
    MediaPlayPause = 5,
    MediaNext = 6,
    MediaPrevious = 7,
    /// <summary>Shows a reminder notification with <see cref="VoiceAction.Query"/> as text.</summary>
    Notify = 8
}

/// <summary>One executable step of a command, scheduled task or routine.</summary>
public sealed class VoiceAction
{
    public VoiceActionKind Kind { get; set; }
    /// <summary>App name/path, URL, or search site.</summary>
    public string Target { get; set; } = string.Empty;
    /// <summary>Search text, todo text, timer minutes or reminder text.</summary>
    public string Query { get; set; } = string.Empty;

    public VoiceAction Clone() => new() { Kind = Kind, Target = Target, Query = Query };
}

/// <summary>A one-off action at a set local time ("open Amazon on 10 Nov at 11:00").</summary>
public sealed class ScheduledVoiceTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>What the user said, shown in lists.</summary>
    public string Title { get; set; } = string.Empty;
    public DateTime RunAtLocal { get; set; }
    public List<VoiceAction> Actions { get; set; } = new();
    public bool Done { get; set; }
}

/// <summary>A repeating set of actions ("every morning at 9 open Unity and Spotify").</summary>
public sealed class VoiceRoutine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    /// <summary>"HH:mm" local time.</summary>
    public string TimeOfDay { get; set; } = "09:00";
    /// <summary>Bit 0 = Monday … bit 6 = Sunday; 0 or 127 = every day.</summary>
    public int DaysMask { get; set; } = 127;
    public List<VoiceAction> Actions { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Local date (yyyy-MM-dd) of the last run, so a routine runs once per day.</summary>
    public string LastRunDate { get; set; } = string.Empty;
}

/// <summary>The assistant character's personalization (five slots).</summary>
public sealed class MascotSettings
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Ada";
    /// <summary>Body colour as #RRGGBB.</summary>
    public string ColorHex { get; set; } = "#7C5CFF";
    /// <summary>0 round, 1 sleepy, 2 happy arcs, 3 sparkle.</summary>
    public int EyeStyle { get; set; }
    /// <summary>0 none, 1 headphones, 2 bow, 3 antenna, 4 glasses.</summary>
    public int Accessory { get; set; }
    /// <summary>0 friendly, 1 concise, 2 playful: the tone of spoken answers.</summary>
    public int Personality { get; set; }

    public MascotSettings Clone() => new()
    {
        Enabled = Enabled, Name = Name, ColorHex = ColorHex, EyeStyle = EyeStyle, Accessory = Accessory, Personality = Personality
    };
}

/// <summary>Running totals for the dictation speed comparison.</summary>
public sealed class VoiceStats
{
    public long WordsDictated { get; set; }
    public double SecondsSpoken { get; set; }
    public int Sessions { get; set; }

    /// <summary>Words per minute while speaking (0 until there is data).</summary>
    public double SpeakingWpm => SecondsSpoken < 1 ? 0 : WordsDictated / (SecondsSpoken / 60.0);
}

/// <summary>Helpers for modifier-only "hold" chords like "Ctrl+Win".</summary>
public static class HoldChordText
{
    [Flags]
    public enum Mods { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

    /// <summary>Parses "Ctrl+Win" / "ctrl + shift" / "Win+Alt"; null for empty, unknown parts or a single modifier.</summary>
    public static Mods? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        Mods m = Mods.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Mods part = raw.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "ctl" => Mods.Ctrl,
                "shift" => Mods.Shift,
                "alt" or "menu" => Mods.Alt,
                "win" or "windows" or "meta" or "super" => Mods.Win,
                _ => (Mods)(-1)
            };
            if ((int)part < 0) return null;
            m |= part;
        }
        // At least two modifiers: a single one would fire on every ordinary shortcut.
        int count = ((m & Mods.Ctrl) != 0 ? 1 : 0) + ((m & Mods.Shift) != 0 ? 1 : 0) + ((m & Mods.Alt) != 0 ? 1 : 0) + ((m & Mods.Win) != 0 ? 1 : 0);
        return count >= 2 ? m : null;
    }

    public static string Format(Mods m)
    {
        var parts = new List<string>();
        if ((m & Mods.Ctrl) != 0) parts.Add("Ctrl");
        if ((m & Mods.Shift) != 0) parts.Add("Shift");
        if ((m & Mods.Alt) != 0) parts.Add("Alt");
        if ((m & Mods.Win) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    /// <summary>Normalizes user text to canonical form, or returns <paramref name="fallback"/> when invalid.</summary>
    public static string Normalize(string? text, string fallback) => Parse(text) is { } m ? Format(m) : fallback;
}
