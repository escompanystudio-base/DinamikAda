using System.Text.Json;
using DinamikAda.Core.Config;
using DinamikAda.Core.Voice;
using DinamikAda.Modules.Media;
using Xunit;

namespace DinamikAda.Tests;

public sealed class VoiceCoreTests
{
    [Theory]
    [InlineData("Ctrl+Win", "Ctrl+Win")]
    [InlineData("win + ctrl", "Ctrl+Win")]
    [InlineData("Control+Shift", "Ctrl+Shift")]
    [InlineData("Alt+Windows", "Alt+Win")]
    public void HoldChord_ParsesAndNormalizes(string input, string expected) =>
        Assert.Equal(expected, HoldChordText.Normalize(input, "x"));

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("")]
    [InlineData("Ctrl+K")]
    [InlineData(null)]
    public void HoldChord_RejectsSingleModifiersAndKeys(string? input) => Assert.Null(HoldChordText.Parse(input));

    [Fact]
    public void Gemini_BuildRequest_HasPartsSystemAndSchema()
    {
        string body = GeminiJson.BuildRequest("be brief",
            [GeminiPart.FromText("hello"), GeminiPart.FromBytes("audio/wav", [1, 2, 3])],
            """{"type":"OBJECT","properties":{"a":{"type":"STRING"}}}""");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var parts = root.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("hello", parts[0].GetProperty("text").GetString());
        Assert.Equal("audio/wav", parts[1].GetProperty("inline_data").GetProperty("mime_type").GetString());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), parts[1].GetProperty("inline_data").GetProperty("data").GetString());
        Assert.Equal("be brief", root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("application/json", root.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
    }

    [Fact]
    public void Gemini_ParseText_JoinsPartsAndSkipsThoughts()
    {
        string json = """{"candidates":[{"content":{"parts":[{"text":"plan","thought":true},{"text":"Merhaba "},{"text":"dünya"}]},"finishReason":"STOP"}]}""";
        Assert.Equal("Merhaba dünya", GeminiJson.ParseText(json));
    }

    [Fact]
    public void Gemini_ParseText_ThrowsFriendlyWhenBlockedOrEmpty()
    {
        Assert.Throws<GeminiException>(() => GeminiJson.ParseText("""{"promptFeedback":{"blockReason":"SAFETY"}}"""));
        Assert.Throws<GeminiException>(() => GeminiJson.ParseText("""{"candidates":[{"finishReason":"SAFETY"}]}"""));
        Assert.Throws<GeminiException>(() => GeminiJson.ParseText("""{"candidates":[]}"""));
    }

    [Fact]
    public void Gemini_ErrorAndFences()
    {
        Assert.Equal("API key not valid.", GeminiJson.ParseError("""{"error":{"code":400,"message":"API key not valid.","status":"INVALID_ARGUMENT"}}"""));
        Assert.Null(GeminiJson.ParseError("not json"));
        Assert.Equal("""{"a":1}""", GeminiJson.StripFences("```json\n{\"a\":1}\n```"));
        Assert.Equal(3, GeminiJson.CountWords(" bir  iki üç "));
    }

    [Fact]
    public void Config_SanitizesVoiceSettings()
    {
        var cfg = new AppConfig
        {
            DictationChord = "ctrl+win",
            AskChord = "Ctrl",
            Mascot = new MascotSettings { ColorHex = "blue", EyeStyle = 9, Name = "  " },
            VoiceRoutines = [new VoiceRoutine { TimeOfDay = "9:5", DaysMask = 0, Actions = [new VoiceAction { Kind = VoiceActionKind.OpenApp, Target = "Spotify" }] }]
        };
        cfg.Sanitize();
        Assert.Equal("Ctrl+Win", cfg.DictationChord);
        Assert.Equal("Ctrl+Shift", cfg.AskChord);
        Assert.Equal("#7C5CFF", cfg.Mascot.ColorHex);
        Assert.Equal(3, cfg.Mascot.EyeStyle);
        Assert.Equal("Ada", cfg.Mascot.Name);
        Assert.Equal("09:05", cfg.VoiceRoutines[0].TimeOfDay);
        Assert.Equal(127, cfg.VoiceRoutines[0].DaysMask);
    }

    [Fact]
    public void Resample_48kTo16k_KeepsDurationAndLevel()
    {
        var src = new byte[48000 * 2];
        for (int i = 0; i < 48000; i++)
        {
            short v = (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 10000);
            src[i * 2] = (byte)(v & 0xFF);
            src[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        byte[] outp = MicCaptureService.ResampleTo16k(src, 48000);
        Assert.Equal(16000 * 2, outp.Length);
        double rms = VoiceNoteWavHelper.ComputeNormalizedRms(outp);
        Assert.InRange(rms, 0.3, 1.0);
    }
}
