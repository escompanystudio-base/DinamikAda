using System.Globalization;
using System.Text.RegularExpressions;

namespace DinamikAda.Modules.Media;

public sealed record LrcLine(TimeSpan Timestamp, string Text);

public static partial class LrcLyricsParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[\.:](\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampTagRegex();

    public static IReadOnlyList<LrcLine> Parse(string lrcContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent)) return [];

        var list = new List<LrcLine>();
        foreach (string rawLine in lrcContent.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            var matches = TimestampTagRegex().Matches(line);
            if (matches.Count == 0) continue;

            int textStart = matches[^1].Index + matches[^1].Length;
            string lyricText = line[textStart..].Trim();

            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins)) continue;
                if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs)) continue;
                if (secs >= 60) continue;
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    string frac = m.Groups[3].Value;
                    if (int.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawFrac))
                    {
                        ms = frac.Length switch
                        {
                            1 => rawFrac * 100,
                            2 => rawFrac * 10,
                            _ => rawFrac
                        };
                    }
                }

                var ts = TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs) + TimeSpan.FromMilliseconds(ms);
                list.Add(new LrcLine(ts, lyricText));
            }
        }

        return list.OrderBy(line => line.Timestamp).ToList();
    }

    public static string? FindActiveLine(IReadOnlyList<LrcLine> lines, TimeSpan position)
    {
        if (lines.Count == 0 || position < lines[0].Timestamp) return null;

        int lo = 0;
        int hi = lines.Count - 1;
        int best = 0;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (lines[mid].Timestamp <= position)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return string.IsNullOrWhiteSpace(lines[best].Text) ? null : lines[best].Text;
    }
}

public static class AudioFormatFormatter
{
    public static string? FormatMixFormat(uint sampleRateHz, ushort bitsPerSample, ushort validBitsPerSample, bool isFloat)
    {
        if (sampleRateHz == 0 || bitsPerSample == 0) return null;
        string khz = sampleRateHz % 1000 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{sampleRateHz / 1000} kHz")
            : string.Create(CultureInfo.InvariantCulture, $"{sampleRateHz / 1000.0:F1} kHz");

        int displayBits = validBitsPerSample > 0 && validBitsPerSample <= bitsPerSample
            ? validBitsPerSample
            : bitsPerSample;

        string suffix = isFloat && displayBits == 32 ? "32-bit float" : $"{displayBits}-bit";
        return $"{khz} • {suffix}";
    }
}

/// <summary>
/// Matches playing track Artist/Title against local .lrc files in Music / Lyrics folders
/// and serves binary-searched synced lyric lines with zero hot-path allocation.
/// </summary>
public sealed class LyricsService
{
    private readonly object _sync = new();
    private bool _enabled = true;
    private List<string> _searchFolders = [];
    private string _cachedTrackKey = string.Empty;
    private IReadOnlyList<LrcLine> _cachedLines = [];
    private string? _loadingTrackKey;
    private int _configGeneration;

    public LyricsService()
    {
        Configure(true, string.Empty);
    }

    public void Configure(bool enabled, string? customFolders)
    {
        lock (_sync)
        {
            _enabled = enabled;
            var dirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(customFolders))
            {
                foreach (string d in customFolders.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string clean = d.Trim();
                    if (Directory.Exists(clean)) dirs.Add(clean);
                }
            }

            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (!string.IsNullOrWhiteSpace(music))
            {
                string musicLyrics = Path.Combine(music, "Lyrics");
                if (Directory.Exists(musicLyrics)) dirs.Add(musicLyrics);
                if (Directory.Exists(music)) dirs.Add(music);
            }

            string appLyrics = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DinamikAda", "Lyrics");
            if (Directory.Exists(appLyrics)) dirs.Add(appLyrics);

            _searchFolders = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _cachedTrackKey = string.Empty;
            _cachedLines = [];
            _loadingTrackKey = null;
            _configGeneration++;
        }
    }

    /// <summary>
    /// UI-safe variant of <see cref="ResolveLine"/>: never touches the disk on the caller's thread.
    /// On a track change it returns null and loads the matching .lrc on the thread pool (folder scans
    /// can stall for seconds on OneDrive/network Music folders); the next call returns the line.
    /// </summary>
    public string? ResolveLineNonBlocking(string? title, string? artist, TimeSpan position)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string cleanTitle = title.Trim();
        string cleanArtist = artist?.Trim() ?? string.Empty;
        string trackKey = $"{cleanArtist}|{cleanTitle}";

        IReadOnlyList<LrcLine> lines;
        lock (_sync)
        {
            if (!_enabled) return null;
            if (!string.Equals(trackKey, _cachedTrackKey, StringComparison.Ordinal))
            {
                if (!string.Equals(trackKey, _loadingTrackKey, StringComparison.Ordinal))
                {
                    _loadingTrackKey = trackKey;
                    int generation = _configGeneration;
                    List<string> folders = _searchFolders;
                    _ = Task.Run(() =>
                    {
                        IReadOnlyList<LrcLine> loaded;
                        try { loaded = LoadMatchingLrc(folders, cleanTitle, cleanArtist); }
                        catch { loaded = []; }
                        lock (_sync)
                        {
                            if (generation != _configGeneration || !string.Equals(_loadingTrackKey, trackKey, StringComparison.Ordinal)) return;
                            _cachedTrackKey = trackKey;
                            _cachedLines = loaded;
                            _loadingTrackKey = null;
                        }
                    });
                }
                return null;
            }
            lines = _cachedLines;
        }

        return LrcLyricsParser.FindActiveLine(lines, position);
    }

    public string? ResolveLine(string? title, string? artist, TimeSpan position)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string trackKey = $"{artist?.Trim()}|{title.Trim()}";

        IReadOnlyList<LrcLine> lines;
        lock (_sync)
        {
            if (!_enabled) return null;
            if (!string.Equals(trackKey, _cachedTrackKey, StringComparison.Ordinal))
            {
                _cachedTrackKey = trackKey;
                _cachedLines = LoadMatchingLrc(_searchFolders, title.Trim(), artist?.Trim() ?? string.Empty);
            }
            lines = _cachedLines;
        }

        return LrcLyricsParser.FindActiveLine(lines, position);
    }

    private static IReadOnlyList<LrcLine> LoadMatchingLrc(List<string> searchFolders, string title, string artist)
    {
        string safeTitle = SanitizeMatchToken(title);
        string safeArtist = SanitizeMatchToken(artist);
        if (string.IsNullOrEmpty(safeTitle)) return [];

        string cleanTitle = StripTitleSuffixes(safeTitle);
        string[] titleVariants = !string.IsNullOrEmpty(cleanTitle) && !cleanTitle.Equals(safeTitle, StringComparison.OrdinalIgnoreCase)
            ? [safeTitle, cleanTitle]
            : [safeTitle];

        foreach (string folder in searchFolders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (string t in titleVariants)
            {
                string[] exactCandidates = string.IsNullOrEmpty(safeArtist)
                    ? [$"{t}.lrc"]
                    : [$"{safeArtist} - {t}.lrc", $"{t} - {safeArtist}.lrc", $"{t}.lrc"];

                foreach (string candidate in exactCandidates)
                {
                    string p = Path.Combine(folder, candidate);
                    if (File.Exists(p))
                    {
                        try { return LrcLyricsParser.Parse(File.ReadAllText(p)); } catch { }
                    }
                }
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.lrc", SearchOption.TopDirectoryOnly))
                {
                    string stem = SanitizeMatchToken(Path.GetFileNameWithoutExtension(file));
                    bool artistMatch = string.IsNullOrEmpty(safeArtist) || stem.Contains(safeArtist, StringComparison.OrdinalIgnoreCase);
                    if (artistMatch && titleVariants.Any(t => stem.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    {
                        return LrcLyricsParser.Parse(File.ReadAllText(file));
                    }
                }
            }
            catch { }
        }
        return [];
    }

    private static string StripTitleSuffixes(string value)
    {
        string stripped = Regex.Replace(value, @"\s*[\(\[].*?[\)\]]\s*", " ", RegexOptions.CultureInvariant).Trim();
        int dashIdx = stripped.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 2) stripped = stripped[..dashIdx].Trim();
        return stripped;
    }

    private static string SanitizeMatchToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Where(c => Array.IndexOf(invalid, c) < 0).ToArray()).Trim();
    }
}
