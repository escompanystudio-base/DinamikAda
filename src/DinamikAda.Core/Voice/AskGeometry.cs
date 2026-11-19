using System.Text.Json;
using System.Text.RegularExpressions;

namespace DinamikAda.Core.Voice;

/// <summary>A point in screenshot pixels.</summary>
public readonly record struct AskPoint(double X, double Y);

/// <summary>An axis-aligned rectangle in screenshot pixels.</summary>
public readonly record struct AskRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public static AskRect Empty => new(0, 0, 0, 0);
}

/// <summary>Where the answer card sits relative to the circled region.</summary>
public enum AskCardSide { Right, Left, Below, Above, Inside }

public readonly record struct AskCardPlacement(AskCardSide Side, double X, double Y);

/// <summary>The parsed Gemini reply: the transcribed question (if any) and the answer text.</summary>
public sealed record AskAnswer(string? Question, string Answer);

/// <summary>
/// Pure logic for "circle to ask": lasso smoothing, the region to send, where the answer card goes,
/// the Gemini prompt and reply parsing.
/// </summary>
public static partial class AskGeometry
{
    public const double DefaultPadding = 0.12;

    /// <summary>Gemini responseSchema for <c>{question, answer}</c>.</summary>
    public const string ResponseSchemaJson =
        """
        {
          "type": "OBJECT",
          "properties": {
            "question": { "type": "STRING", "description": "The user's spoken question, transcribed verbatim in the spoken language." },
            "answer": { "type": "STRING", "description": "The answer to show next to the circled region." }
          },
          "required": ["answer"],
          "propertyOrdering": ["question", "answer"]
        }
        """;

    /// <summary>Bounding box of the finite points of <paramref name="path"/>; empty when there are none.</summary>
    public static AskRect Bounds(IReadOnlyList<AskPoint> path)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        int n = 0;
        foreach (var p in path)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
            n++;
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return n == 0 ? AskRect.Empty : new AskRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// The region to send: the path's bounding box padded by <paramref name="padding"/> of its size on each side,
    /// grown to at least <paramref name="minSize"/> around its centre and shifted (not cut) to stay on the screen.
    /// An empty or tiny path (smaller than <paramref name="tinyThreshold"/>) means the whole screen.
    /// </summary>
    public static AskRect ComputeRegion(IReadOnlyList<AskPoint> path, double screenWidth, double screenHeight,
        double minSize = 160, double tinyThreshold = 24, double padding = DefaultPadding)
    {
        var screen = new AskRect(0, 0, Math.Max(1, screenWidth), Math.Max(1, screenHeight));
        int finite = path.Count(p => double.IsFinite(p.X) && double.IsFinite(p.Y));
        var b = Bounds(path);
        if (finite < 2 || Math.Max(b.Width, b.Height) < tinyThreshold) return screen;

        double w = b.Width * (1 + 2 * padding);
        double h = b.Height * (1 + 2 * padding);
        double cx = b.X + b.Width / 2;
        double cy = b.Y + b.Height / 2;
        w = Math.Min(Math.Max(w, minSize), screen.Width);
        h = Math.Min(Math.Max(h, minSize), screen.Height);
        double x = Math.Clamp(cx - w / 2, 0, screen.Width - w);
        double y = Math.Clamp(cy - h / 2, 0, screen.Height - h);
        return new AskRect(x, y, w, h);
    }

    /// <summary>Drops points closer than <paramref name="minDistance"/> to the last kept one; always keeps both ends.</summary>
    public static IReadOnlyList<AskPoint> Decimate(IReadOnlyList<AskPoint> path, double minDistance)
    {
        var result = new List<AskPoint>(path.Count);
        if (path.Count == 0) return result;
        double min2 = minDistance * minDistance;
        result.Add(path[0]);
        for (int i = 1; i < path.Count; i++)
        {
            var last = result[^1];
            double dx = path[i].X - last.X, dy = path[i].Y - last.Y;
            if (dx * dx + dy * dy >= min2) result.Add(path[i]);
        }
        if (path.Count > 1 && result[^1] != path[^1]) result.Add(path[^1]);
        return result;
    }

    /// <summary>Chaikin corner cutting on an open path (endpoints kept), <paramref name="iterations"/> times.</summary>
    public static IReadOnlyList<AskPoint> SmoothChaikin(IReadOnlyList<AskPoint> path, int iterations = 2)
    {
        IReadOnlyList<AskPoint> current = path;
        if (path.Count < 3) return path.ToList();
        for (int it = 0; it < iterations; it++)
        {
            var next = new List<AskPoint>(current.Count * 2) { current[0] };
            for (int i = 0; i < current.Count - 1; i++)
            {
                var a = current[i];
                var b = current[i + 1];
                next.Add(new AskPoint(0.75 * a.X + 0.25 * b.X, 0.75 * a.Y + 0.25 * b.Y));
                next.Add(new AskPoint(0.25 * a.X + 0.75 * b.X, 0.25 * a.Y + 0.75 * b.Y));
            }
            next.Add(current[^1]);
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Places a <paramref name="cardWidth"/>×<paramref name="cardHeight"/> card next to <paramref name="region"/> on the side
    /// with the most spare room, never off-screen. When no side fits, the card goes inside the region's bottom-right corner.
    /// </summary>
    public static AskCardPlacement PlaceCard(AskRect region, double cardWidth, double cardHeight,
        double screenWidth, double screenHeight, double gap = 16, double margin = 12)
    {
        double ClampX(double v) => Math.Max(margin, Math.Min(v, screenWidth - cardWidth - margin));
        double ClampY(double v) => Math.Max(margin, Math.Min(v, screenHeight - cardHeight - margin));

        var options = new (AskCardSide Side, double Spare)[]
        {
            (AskCardSide.Right, screenWidth - margin - (region.Right + gap) - cardWidth),
            (AskCardSide.Left, region.X - gap - margin - cardWidth),
            (AskCardSide.Below, screenHeight - margin - (region.Bottom + gap) - cardHeight),
            (AskCardSide.Above, region.Y - gap - margin - cardHeight)
        };
        var best = (Side: AskCardSide.Inside, Spare: double.NegativeInfinity);
        foreach (var o in options)
        {
            if (o.Spare >= 0 && o.Spare > best.Spare) best = o;
        }

        return best.Side switch
        {
            AskCardSide.Right => new(AskCardSide.Right, region.Right + gap, ClampY(region.Y)),
            AskCardSide.Left => new(AskCardSide.Left, region.X - gap - cardWidth, ClampY(region.Y)),
            AskCardSide.Below => new(AskCardSide.Below, ClampX(region.X + (region.Width - cardWidth) / 2), region.Bottom + gap),
            AskCardSide.Above => new(AskCardSide.Above, ClampX(region.X + (region.Width - cardWidth) / 2), region.Y - gap - cardHeight),
            _ => new(AskCardSide.Inside, ClampX(region.Right - cardWidth - 2 * margin), ClampY(region.Bottom - cardHeight - 2 * margin))
        };
    }

    /// <summary>System instruction for the ask request.</summary>
    public static string BuildSystemPrompt(string speechLanguage, string? mascotName, int personality)
    {
        string name = string.IsNullOrWhiteSpace(mascotName) ? "Ada" : mascotName.Trim();
        string lang = LanguageName(speechLanguage);
        string tone = personality switch
        {
            1 => "Personality: concise and to the point; no small talk or filler.",
            2 => "Personality: playful and witty, with a light touch of humour, while staying accurate and helpful.",
            _ => "Personality: friendly and warm, like a helpful friend."
        };
        return $"""
            You are {name}, a desktop assistant. The user circled a part of their screen and asked a question about it out loud.
            You receive the circled region as an image and the user's spoken question as audio.
            - Transcribe the spoken question verbatim into "question".
            - Put your answer to that question, about what is in the image, into "answer".
            - Answer in {lang}, concisely: 2-6 sentences or a short list.
            - Use plain text; light markdown at most (short bullet lists, occasional bold). No headings, tables or code fences.
            - If the question is unclear, inaudible or missing, describe what is in the region and what it might mean.
            - Read any text, numbers, code or UI in the image carefully; do not invent details you cannot see.
            {tone}
            """;
    }

    /// <summary>The text part that accompanies the image and audio.</summary>
    public static string BuildUserPrompt() =>
        "The attached image is the part of my screen I circled. The attached audio is my spoken question about it.";

    /// <summary>Reads <c>{question, answer}</c>; anything that is not such JSON is treated as the answer text itself.</summary>
    public static AskAnswer ParseAnswer(string? raw)
    {
        string text = GeminiJson.StripFences(raw ?? string.Empty);
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(a.GetString()))
                {
                    string? q = root.TryGetProperty("question", out var qe) && qe.ValueKind == JsonValueKind.String
                        ? qe.GetString()?.Trim()
                        : null;
                    return new AskAnswer(string.IsNullOrWhiteSpace(q) ? null : q, a.GetString()!.Trim());
                }
            }
            catch (JsonException)
            {
                // Not JSON after all: show it as text.
            }
        }
        return new AskAnswer(null, text.Trim());
    }

    /// <summary>Turns light markdown into clean plain text: no headings, emphasis markers or code ticks; bullets become "•".</summary>
    public static string CleanMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = s.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = HeadingRegex().Replace(lines[i], string.Empty);
            line = BulletRegex().Replace(line, "$1• ");
            line = BoldRegex().Replace(line, "$2");
            line = ItalicRegex().Replace(line, "$1");
            line = CodeRegex().Replace(line, "$1");
            lines[i] = line.TrimEnd();
        }
        s = string.Join('\n', lines);
        s = BlankLinesRegex().Replace(s, "\n\n");
        return s.Trim();
    }

    private static string LanguageName(string? tag)
    {
        string t = string.IsNullOrWhiteSpace(tag) ? "tr-TR" : tag.Trim();
        string primary = t.Split('-', '_')[0].ToLowerInvariant();
        string? name = primary switch
        {
            "tr" => "Turkish",
            "en" => "English",
            "de" => "German",
            "fr" => "French",
            "es" => "Spanish",
            "it" => "Italian",
            "pt" => "Portuguese",
            "nl" => "Dutch",
            "ru" => "Russian",
            "ar" => "Arabic",
            "ja" => "Japanese",
            "zh" => "Chinese",
            "ko" => "Korean",
            "az" => "Azerbaijani",
            _ => null
        };
        return name is null ? $"the language with tag {t}" : $"{name} ({t})";
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)[*\-+]\s+")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<![\*\w])\*(?=\S)([^*]+?)(?<=\S)\*(?![\*\w])")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
}
