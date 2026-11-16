using DinamikAda.Modules.Media;
using Xunit;

namespace DinamikAda.Tests;

public sealed class MediaAndAudioTests
{
    [Fact]
    public void AudioSessionGrouper_MergesMultiProcessSameAppKeysCleanly()
    {
        var entries = new[]
        {
            new RawAudioSessionEntry("spotify", 101, "Spotify", "Spotify Free", 0.40f, true, 0.0f, null),
            new RawAudioSessionEntry("spotify", 102, "Spotify", "Spotify", 0.85f, false, 0.45f, @"C:\app\spotify.exe"),
            new RawAudioSessionEntry("chrome", 205, "chrome", "YouTube - Chrome", 0.65f, false, 0.25f, null)
        };

        var grouped = AudioSessionGrouper.GroupSessions(entries);
        Assert.Equal(2, grouped.Length);

        var spotify = grouped.First(g => g.GroupKey == "spotify");
        Assert.Equal(0.85f, spotify.Volume01, 2);
        Assert.False(spotify.IsMuted);
        Assert.Equal(@"C:\app\spotify.exe", spotify.IconPath);
    }

    [Fact]
    public void ColorQuantizer_PrefersVibrantAccentOverDarkShadowPixels()
    {
        // BGRA pixels: 3 nearly-black pixels + 1 vibrant orange pixel (#FF9F0A)
        byte[] bgra =
        {
            5, 5, 5, 255,
            10, 10, 12, 255,
            8, 8, 9, 255,
            10, 159, 255, 255 // B=10, G=159, R=255 -> #FF9F0A
        };

        string hex = ColorQuantizer.PickVibrantHexFromBgraPixels(bgra);
        Assert.StartsWith("#", hex);
        Assert.Equal(7, hex.Length);
        // Orange hue clamped to island readability bounds (#F09C19): high red > blue
        byte r = Convert.ToByte(hex[1..3], 16);
        byte g = Convert.ToByte(hex[3..5], 16);
        byte b = Convert.ToByte(hex[5..7], 16);
        Assert.True(r > 200 && g > 120 && b < 60, $"Expected orange tint, got {hex}");
    }

    [Fact]
    public void ColorQuantizer_FallsBackToDefaultAccentOnPureGrayscale()
    {
        byte[] gray = { 128, 128, 128, 255, 40, 40, 40, 255, 230, 230, 230, 255 };
        string hex = ColorQuantizer.PickVibrantHexFromBgraPixels(gray);
        Assert.Equal("#0A84FF", hex);
    }

    [Fact]
    public void Radix2Fft_DetectsStrongSineWaveAboveSilenceFloor()
    {
        float[] sine = new float[Radix2Fft.FftSize];
        for (int i = 0; i < sine.Length; i++)
        {
            sine[i] = 0.75f * MathF.Sin(2f * MathF.PI * 16f * i / sine.Length);
        }

        double[] bands = new double[Radix2Fft.BandCount];
        double[] smoothed = new double[Radix2Fft.BandCount];
        Radix2Fft.ComputeLogBands(sine, bands, smoothed);

        double peak = bands.Max();
        Assert.InRange(peak, 0.35, 1.0);
        Assert.All(bands, b => Assert.InRange(b, 0.0, 1.0));
    }
}
