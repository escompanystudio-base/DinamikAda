using System.Text.Json;
using DinamikAda.Core.Voice;
using Xunit;

namespace DinamikAda.Tests;

public sealed class AskGeometryTests
{
    private static AskPoint P(double x, double y) => new(x, y);

    private static List<AskPoint> Circle(double cx, double cy, double r, int n = 48)
    {
        var list = new List<AskPoint>();
        for (int i = 0; i <= n; i++)
        {
            double a = i * 2 * Math.PI / n;
            list.Add(P(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        return list;
    }

    // ---------- Bounds / region ----------

    [Fact]
    public void Bounds_OfPath()
    {
        var b = AskGeometry.Bounds([P(10, 20), P(50, 5), P(30, 80)]);
        Assert.Equal(new AskRect(10, 5, 40, 75), b);
    }

    [Fact]
    public void Bounds_Empty_IsEmpty() => Assert.True(AskGeometry.Bounds([]).IsEmpty);

    [Fact]
    public void Region_PadsBoundingBoxByTwelvePercent()
    {
        // 400x200 box at (500,400); pad 48 px horizontally, 24 px vertically.
        var r = AskGeometry.ComputeRegion([P(500, 400), P(900, 400), P(900, 600), P(500, 600)], 1920, 1080);
        Assert.Equal(452, r.X, 6);
        Assert.Equal(376, r.Y, 6);
        Assert.Equal(496, r.Width, 6);
        Assert.Equal(248, r.Height, 6);
    }

    [Fact]
    public void Region_EnforcesMinimumSizeAroundCentre()
    {
        // 60x40 box (bigger than the tiny threshold, smaller than the minimum).
        var r = AskGeometry.ComputeRegion([P(1000, 500), P(1060, 540)], 1920, 1080, minSize: 200);
        Assert.Equal(200, r.Width, 6);
        Assert.Equal(200, r.Height, 6);
        Assert.Equal(1030, r.X + r.Width / 2, 6);
        Assert.Equal(520, r.Y + r.Height / 2, 6);
    }

    [Fact]
    public void Region_ClampedToScreen_ShiftsInsteadOfShrinking()
    {
        var r = AskGeometry.ComputeRegion([P(0, 0), P(100, 100)], 1920, 1080, minSize: 300);
        Assert.Equal(0, r.X, 6);
        Assert.Equal(0, r.Y, 6);
        Assert.Equal(300, r.Width, 6);
        Assert.Equal(300, r.Height, 6);

        var r2 = AskGeometry.ComputeRegion([P(1800, 1000), P(1919, 1079)], 1920, 1080, minSize: 300);
        Assert.Equal(1920, r2.Right, 6);
        Assert.Equal(1080, r2.Bottom, 6);
        Assert.Equal(300, r2.Width, 6);
    }

    [Fact]
    public void Region_LargerThanScreen_IsWholeScreen()
    {
        var r = AskGeometry.ComputeRegion([P(-50, -50), P(2000, 1200)], 1920, 1080);
        Assert.Equal(new AskRect(0, 0, 1920, 1080), r);
    }

    [Fact]
    public void Region_EmptyOrTinyPath_IsWholeScreen()
    {
        Assert.Equal(new AskRect(0, 0, 1920, 1080), AskGeometry.ComputeRegion([], 1920, 1080));
        Assert.Equal(new AskRect(0, 0, 1920, 1080), AskGeometry.ComputeRegion([P(500, 500)], 1920, 1080));
        Assert.Equal(new AskRect(0, 0, 1920, 1080), AskGeometry.ComputeRegion([P(500, 500), P(505, 510), P(498, 503)], 1920, 1080));
    }

    [Fact]
    public void Region_IgnoresNonFinitePoints()
    {
        var r = AskGeometry.ComputeRegion([P(double.NaN, 3), P(500, 400), P(900, 600), P(double.PositiveInfinity, 0)], 1920, 1080);
        Assert.Equal(452, r.X, 6);
    }

    [Fact]
    public void Region_MinSizeNeverExceedsScreen()
    {
        var r = AskGeometry.ComputeRegion([P(10, 10), P(60, 60)], 100, 80, minSize: 500);
        Assert.Equal(new AskRect(0, 0, 100, 80), r);
    }

    // ---------- Smoothing ----------

    [Fact]
    public void Decimate_DropsPointsCloserThanDistance_KeepsEndpoints()
    {
        var d = AskGeometry.Decimate([P(0, 0), P(1, 0), P(2, 0), P(5, 0), P(5.5, 0), P(6, 0)], 3);
        Assert.Equal(P(0, 0), d[0]);
        Assert.Equal(P(6, 0), d[^1]);
        Assert.Contains(P(5, 0), d);
        Assert.DoesNotContain(P(1, 0), d);
        Assert.Equal(3, d.Count);
    }

    [Fact]
    public void Chaikin_KeepsEndpoints_AndAddsPoints()
    {
        var input = new List<AskPoint> { P(0, 0), P(10, 0), P(10, 10) };
        var s = AskGeometry.SmoothChaikin(input, 1);
        Assert.Equal(P(0, 0), s[0]);
        Assert.Equal(P(10, 10), s[^1]);
        // 2 interior points per segment + 2 endpoints.
        Assert.Equal(6, s.Count);
        Assert.Equal(P(2.5, 0), s[1]);
        Assert.Equal(P(7.5, 0), s[2]);
    }

    [Fact]
    public void Chaikin_CutsTheCorner()
    {
        var s = AskGeometry.SmoothChaikin([P(0, 0), P(10, 0), P(10, 10)], 3);
        Assert.DoesNotContain(P(10, 0), s);
        Assert.All(s, p => Assert.True(p.X >= 0 && p.X <= 10 && p.Y >= 0 && p.Y <= 10));
    }

    [Fact]
    public void Chaikin_ShortPaths_Unchanged()
    {
        Assert.Empty(AskGeometry.SmoothChaikin([], 2));
        Assert.Single(AskGeometry.SmoothChaikin([P(1, 1)], 2));
        Assert.Equal(2, AskGeometry.SmoothChaikin([P(1, 1), P(3, 3)], 2).Count);
    }

    [Fact]
    public void Chaikin_CircleStaysNearRadius()
    {
        var s = AskGeometry.SmoothChaikin(AskGeometry.Decimate(Circle(100, 100, 50), 2), 2);
        Assert.All(s, p =>
        {
            double r = Math.Sqrt((p.X - 100) * (p.X - 100) + (p.Y - 100) * (p.Y - 100));
            Assert.InRange(r, 47, 50.5);
        });
    }

    // ---------- Card placement ----------

    [Fact]
    public void Card_GoesRight_WhenMostRoomOnRight()
    {
        var p = AskGeometry.PlaceCard(new AskRect(100, 300, 300, 200), 380, 240, 1920, 1080);
        Assert.Equal(AskCardSide.Right, p.Side);
        Assert.Equal(100 + 300 + 16, p.X, 6);
        Assert.Equal(300, p.Y, 6);
    }

    [Fact]
    public void Card_GoesLeft_WhenRegionAtRightEdge()
    {
        var p = AskGeometry.PlaceCard(new AskRect(1500, 300, 400, 200), 380, 240, 1920, 1080);
        Assert.Equal(AskCardSide.Left, p.Side);
        Assert.Equal(1500 - 16 - 380, p.X, 6);
    }

    [Fact]
    public void Card_GoesBelowOrAbove_WhenRegionIsWide()
    {
        var below = AskGeometry.PlaceCard(new AskRect(40, 60, 1840, 300), 380, 240, 1920, 1080);
        Assert.Equal(AskCardSide.Below, below.Side);
        Assert.Equal(60 + 300 + 16, below.Y, 6);
        Assert.Equal(40 + (1840 - 380) / 2.0, below.X, 6);

        var above = AskGeometry.PlaceCard(new AskRect(40, 700, 1840, 340), 380, 240, 1920, 1080);
        Assert.Equal(AskCardSide.Above, above.Side);
        Assert.Equal(700 - 16 - 240, above.Y, 6);
    }

    [Fact]
    public void Card_SideAlignment_ClampedOnScreen()
    {
        var p = AskGeometry.PlaceCard(new AskRect(100, 950, 300, 120), 380, 400, 1920, 1080);
        Assert.Equal(AskCardSide.Right, p.Side);
        Assert.True(p.Y + 400 <= 1080 - 12 + 1e-9);
        Assert.True(p.Y >= 12);
    }

    [Fact]
    public void Card_WholeScreenRegion_FallsInside_OnScreen()
    {
        var p = AskGeometry.PlaceCard(new AskRect(0, 0, 1920, 1080), 380, 240, 1920, 1080);
        Assert.Equal(AskCardSide.Inside, p.Side);
        Assert.InRange(p.X, 12, 1920 - 380 - 12);
        Assert.InRange(p.Y, 12, 1080 - 240 - 12);
    }

    [Fact]
    public void Card_TallerThanScreen_StaysAtMargin()
    {
        var p = AskGeometry.PlaceCard(new AskRect(100, 100, 200, 200), 380, 5000, 1920, 1080);
        Assert.Equal(12, p.Y, 6);
    }

    // ---------- Prompt ----------

    [Fact]
    public void Prompt_MentionsCircledRegion_LanguageAndMascot()
    {
        string s = AskGeometry.BuildSystemPrompt("tr-TR", "Ada", 0);
        Assert.Contains("circled", s);
        Assert.Contains("Turkish", s);
        Assert.Contains("tr-TR", s);
        Assert.Contains("Ada", s);
        Assert.Contains("2-6 sentences", s);
        Assert.Contains("unclear", s);
        Assert.Contains("warm", s);
    }

    [Theory]
    [InlineData(1, "concise")]
    [InlineData(2, "playful")]
    [InlineData(9, "warm")]
    public void Prompt_PersonalityTone(int personality, string word) =>
        Assert.Contains(word, AskGeometry.BuildSystemPrompt("en-US", "Bit", personality));

    [Fact]
    public void Prompt_BlankName_FallsBackToAda() =>
        Assert.Contains("Ada", AskGeometry.BuildSystemPrompt("en-US", "  ", 0));

    [Fact]
    public void Prompt_UnknownLanguage_UsesTag()
    {
        string s = AskGeometry.BuildSystemPrompt("xx-YY", "Ada", 0);
        Assert.Contains("xx-YY", s);
        Assert.Contains("English", AskGeometry.BuildSystemPrompt("en-GB", "Ada", 0));
        Assert.Contains("German", AskGeometry.BuildSystemPrompt("de", "Ada", 0));
    }

    [Fact]
    public void UserPrompt_RefersToImageAndAudio()
    {
        string u = AskGeometry.BuildUserPrompt();
        Assert.Contains("image", u);
        Assert.Contains("audio", u);
    }

    [Fact]
    public void Schema_IsValidJson_WithQuestionAndAnswer()
    {
        using var doc = JsonDocument.Parse(AskGeometry.ResponseSchemaJson);
        var props = doc.RootElement.GetProperty("properties");
        Assert.True(props.TryGetProperty("question", out _));
        Assert.True(props.TryGetProperty("answer", out _));
        Assert.Equal("answer", doc.RootElement.GetProperty("required")[0].GetString());
    }

    // ---------- Answer parsing ----------

    [Fact]
    public void Parse_Json()
    {
        var a = AskGeometry.ParseAnswer("{\"question\":\"Bu ne?\",\"answer\":\"Bir grafik.\"}");
        Assert.Equal("Bu ne?", a.Question);
        Assert.Equal("Bir grafik.", a.Answer);
    }

    [Fact]
    public void Parse_FencedJson_BlankQuestion_IsNull()
    {
        var a = AskGeometry.ParseAnswer("```json\n{\"question\":\"  \",\"answer\":\" ok \"}\n```");
        Assert.Null(a.Question);
        Assert.Equal("ok", a.Answer);
    }

    [Fact]
    public void Parse_PlainText_FallsBack()
    {
        var a = AskGeometry.ParseAnswer("  Just text.  ");
        Assert.Null(a.Question);
        Assert.Equal("Just text.", a.Answer);
    }

    [Fact]
    public void Parse_JsonWithoutAnswer_UsesRaw()
    {
        var a = AskGeometry.ParseAnswer("{\"foo\":1}");
        Assert.Equal("{\"foo\":1}", a.Answer);
        Assert.Equal(string.Empty, AskGeometry.ParseAnswer("   ").Answer);
    }

    // ---------- Markdown cleanup ----------

    [Fact]
    public void CleanMarkdown_StripsEmphasisHeadersAndBullets()
    {
        string s = AskGeometry.CleanMarkdown("## Başlık\n**Kalın** ve __alt__ ve `kod`\n* bir\n- iki\n  + üç\n1. sıra");
        Assert.Equal("Başlık\nKalın ve alt ve kod\n• bir\n• iki\n  • üç\n1. sıra", s);
    }

    [Fact]
    public void CleanMarkdown_CollapsesBlankLines_AndNormalizesNewlines()
    {
        Assert.Equal("a\n\nb", AskGeometry.CleanMarkdown("a\r\n\r\n\r\n\r\nb\r\n"));
        Assert.Equal(string.Empty, AskGeometry.CleanMarkdown(null));
    }

    [Fact]
    public void CleanMarkdown_KeepsMultiplicationAsterisks()
    {
        Assert.Equal("2 * 3 = 6", AskGeometry.CleanMarkdown("2 * 3 = 6"));
    }
}
