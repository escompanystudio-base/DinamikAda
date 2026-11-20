using DinamikAda.Core.Voice;
using Xunit;

namespace DinamikAda.Tests;

public sealed class DictationTextTests
{
    // ---- Prompt --------------------------------------------------------------------------------

    [Fact]
    public void CleanupPrompt_CoversAllRules()
    {
        string p = DictationText.BuildSystemPrompt("tr-TR", cleanup: true);
        Assert.Contains("Turkish", p);
        Assert.Contains("tr-TR", p);
        Assert.Contains("ııı", p);
        Assert.Contains("şey", p);
        Assert.Contains("um", p);
        Assert.Contains("yok, onda", p);              // self-correction example
        Assert.Contains("Saat onda buluşalım.", p);
        Assert.Contains("punctuation", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yeni satır", p);
        Assert.Contains("new line", p);
        Assert.Contains("yeni paragraf", p);
        Assert.Contains("ONLY", p);
        Assert.Contains(DictationText.NothingHeardToken, p);
        Assert.DoesNotContain("verbatim", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerbatimPrompt_KeepsFormattingWordsButNoCleanup()
    {
        string p = DictationText.BuildSystemPrompt("en-US", cleanup: false);
        Assert.Contains("English", p);
        Assert.Contains("verbatim", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new line", p);
        Assert.Contains("yeni paragraf", p);
        Assert.Contains(DictationText.NothingHeardToken, p);
        Assert.DoesNotContain("Saat onda buluşalım.", p);
    }

    [Fact]
    public void Prompt_UnknownLanguage_FallsBackToTag()
    {
        string p = DictationText.BuildSystemPrompt("xx-YY", cleanup: true);
        Assert.Contains("xx-YY", p);
    }

    [Theory]
    [InlineData("tr-TR", "Turkish")]
    [InlineData("en-GB", "English")]
    [InlineData("de-DE", "German")]
    [InlineData("", "Turkish")]
    public void LanguageName_MapsTags(string tag, string expected) =>
        Assert.Equal(expected, DictationText.LanguageName(tag));

    // ---- Post-processing -----------------------------------------------------------------------

    [Theory]
    [InlineData("  Merhaba dünya.  ", "Merhaba dünya.")]
    [InlineData("\"Merhaba dünya.\"", "Merhaba dünya.")]
    [InlineData("“Merhaba dünya.”", "Merhaba dünya.")]
    [InlineData("«Merhaba»", "Merhaba")]
    [InlineData("```\nMerhaba dünya.\n```", "Merhaba dünya.")]
    [InlineData("```text\nMerhaba dünya.\n```", "Merhaba dünya.")]
    [InlineData("Merhaba    güzel   dünya.", "Merhaba güzel dünya.")]
    [InlineData("Transcript: Hello there.", "Hello there.")]
    [InlineData("Metin: Merhaba.", "Merhaba.")]
    public void Clean_StripsWrappersAndCollapsesSpaces(string raw, string expected) =>
        Assert.Equal(expected, DictationText.Clean(raw));

    [Fact]
    public void Clean_KeepsInnerQuotes()
    {
        Assert.Equal("\"A\" ve \"B\" dedi.", DictationText.Clean("\"A\" ve \"B\" dedi."));
    }

    [Fact]
    public void Clean_PreservesNewlinesButTrimsAroundThem()
    {
        Assert.Equal("Birinci satır.\nİkinci satır.\n\nYeni paragraf.",
            DictationText.Clean("Birinci satır.  \n   İkinci satır.\r\n\r\n\r\n\r\nYeni paragraf."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<<EMPTY>>")]
    [InlineData("\"<<EMPTY>>\"")]
    [InlineData("[silence]")]
    [InlineData("(sessizlik)")]
    [InlineData("[inaudible]")]
    [InlineData("...")]
    [InlineData("I'm sorry, but the audio does not contain any speech.")]
    [InlineData("There is no speech in the recording.")]
    [InlineData("Ses kaydında konuşma bulunmuyor.")]
    [InlineData("Üzgünüm, sesi anlayamadım.")]
    [InlineData("Kayıtta anlaşılır bir konuşma yok.")]
    public void Clean_NothingHeard_ReturnsEmpty(string? raw) =>
        Assert.Equal(string.Empty, DictationText.Clean(raw));

    [Theory]
    [InlineData("I'm sorry I can't make it tonight.")]
    [InlineData("Üzgünüm, yarın gelemiyorum.")]
    [InlineData("Toplantıda ses sistemi yoktu, o yüzden erken çıktık ve konuşmayı ertesi güne bıraktık.")]
    public void Clean_RealSentencesThatLookApologetic_AreKept(string raw) =>
        Assert.Equal(raw, DictationText.Clean(raw));

    [Theory]
    [InlineData("merhaba yeni satır nasılsın", "merhaba\nnasılsın")]
    [InlineData("hello new line world", "hello\nworld")]
    [InlineData("bir yeni paragraf iki", "bir\n\niki")]
    [InlineData("one new paragraph two", "one\n\ntwo")]
    [InlineData("Yeni satır başı", "Yeni satır başı")] // only when isolated words with content around? kept literal at sentence start
    public void ApplySpokenFormatting_ConvertsKeywords(string raw, string expected) =>
        Assert.Equal(expected, DictationText.ApplySpokenFormatting(raw));

    [Fact]
    public void Preview_TruncatesWithEllipsisAndFlattensNewlines()
    {
        string text = new string('a', 50) + "\n" + new string('b', 60);
        string p = DictationText.Preview(text, 80);
        Assert.True(p.Length <= 80);
        Assert.EndsWith("…", p);
        Assert.DoesNotContain("\n", p);
        Assert.Equal("Kısa metin.", DictationText.Preview("Kısa metin.", 80));
    }

    // ---- Speed test ----------------------------------------------------------------------------

    [Fact]
    public void Samples_ExistInBothLanguages_AndPickIsDeterministicWithSeed()
    {
        Assert.True(DictationText.SampleSentences("tr").Count >= 3);
        Assert.True(DictationText.SampleSentences("en").Count >= 3);
        Assert.Contains(DictationText.PickSample("tr", new Random(1)), DictationText.SampleSentences("tr"));
        Assert.Contains(DictationText.PickSample("en", new Random(1)), DictationText.SampleSentences("en"));
        Assert.NotEqual(DictationText.SampleSentences("tr")[0], DictationText.SampleSentences("en")[0]);
    }

    [Fact]
    public void PickSample_AvoidsPrevious()
    {
        var list = DictationText.SampleSentences("en");
        for (int i = 0; i < 20; i++)
            Assert.NotEqual(list[0], DictationText.PickSample("en", new Random(i), avoid: list[0]));
    }

    [Theory]
    [InlineData(10, 60, 10)]
    [InlineData(10, 30, 20)]
    [InlineData(45, 45, 60)]
    [InlineData(5, 0, 0)]
    public void Wpm_IsWordsPerMinute(int words, double seconds, double expected) =>
        Assert.Equal(expected, DictationText.Wpm(words, TimeSpan.FromSeconds(seconds)), 3);

    [Theory]
    [InlineData("merhaba dünya", "merhaba dünya", 100)]
    [InlineData("abcd", "abcf", 75)]
    [InlineData("abcd", "", 0)]
    [InlineData("abcd", "abcdefgh", 50)]
    public void Accuracy_IsCharacterLevel(string target, string typed, double expected) =>
        Assert.Equal(expected, DictationText.Accuracy(target, typed), 3);

    [Fact]
    public void Accuracy_IgnoresSurroundingWhitespaceAndRepeatedSpaces() =>
        Assert.Equal(100, DictationText.Accuracy("a b c", "  a  b c "), 3);

    [Theory]
    [InlineData("Merhaba dünya.", "merhaba dünya.", true)]
    [InlineData("Merhaba dünya.", "Merhaba dünya. ", true)]
    [InlineData("Merhaba dünya.", "Merhaba dünya", false)]
    public void IsComplete_IgnoresCaseAndTrailingSpace(string target, string typed, bool expected) =>
        Assert.Equal(expected, DictationText.IsComplete(target, typed));

    [Theory]
    [InlineData(40, 130, 3.25)]
    [InlineData(0, 130, 0)]
    [InlineData(130, 0, 0)]
    public void SpeedRatio_SpeakingOverTyping(double typing, double speaking, double expected) =>
        Assert.Equal(expected, DictationText.SpeedRatio(typing, speaking), 3);
}
