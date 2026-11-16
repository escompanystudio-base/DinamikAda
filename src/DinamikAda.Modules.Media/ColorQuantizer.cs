using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DinamikAda.Modules.Media;

public static class ColorQuantizer
{
    public static async Task<string> ExtractVibrantAccentHexAsync(byte[]? imageBytes, CancellationToken ct = default)
    {
        var (primary, _, _) = await ExtractPaletteHexesAsync(imageBytes, ct).ConfigureAwait(false);
        return primary;
    }

    public static async Task<(string PrimaryHex, string SecondaryHex, string HighlightHex)> ExtractPaletteHexesAsync(
        byte[]? imageBytes,
        CancellationToken ct = default)
    {
        if (imageBytes is null || imageBytes.Length < 16)
        {
            return ("#0A84FF", "#5E5CE6", "#64D2FF");
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(imageBytes);
                await writer.StoreAsync().AsTask(ct);
            }
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
            var transform = new BitmapTransform
            {
                ScaledWidth = 16,
                ScaledHeight = 16,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            var pixelProvider = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(ct);

            byte[] pixels = pixelProvider.DetachPixelData();
            return PickPaletteFromBgraPixels(pixels);
        }
        catch
        {
            return ("#0A84FF", "#5E5CE6", "#64D2FF");
        }
    }

    public static string PickVibrantHexFromBgraPixels(ReadOnlySpan<byte> bgraPixels) =>
        PickPaletteFromBgraPixels(bgraPixels).PrimaryHex;

    public static (string PrimaryHex, string SecondaryHex, string HighlightHex) PickPaletteFromBgraPixels(ReadOnlySpan<byte> bgraPixels)
    {
        if (bgraPixels.Length < 4)
        {
            return ("#0A84FF", "#5E5CE6", "#64D2FF");
        }

        const int BucketCount = 12;
        Span<double> bucketScoreSum = stackalloc double[BucketCount];
        Span<double> bucketBestScore = stackalloc double[BucketCount];
        Span<double> bucketBestR = stackalloc double[BucketCount];
        Span<double> bucketBestG = stackalloc double[BucketCount];
        Span<double> bucketBestB = stackalloc double[BucketCount];
        bucketBestScore.Fill(-1);

        double bestScore = -1.0;
        double bestR = 10, bestG = 132, bestB = 255;

        for (int i = 0; i + 3 < bgraPixels.Length; i += 4)
        {
            double b = bgraPixels[i] / 255.0;
            double g = bgraPixels[i + 1] / 255.0;
            double r = bgraPixels[i + 2] / 255.0;

            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) * 0.5;
            double delta = max - min;
            if (delta < 0.05) continue;

            double s = delta / (1.0 - Math.Abs(2.0 * l - 1.0));
            if (l < 0.18 || l > 0.85) continue;

            double score = s * (1.0 - Math.Abs(l - 0.52));
            if (score > bestScore)
            {
                bestScore = score;
                bestR = r * 255.0;
                bestG = g * 255.0;
                bestB = b * 255.0;
            }

            RgbToHsl(r, g, b, out double pxHue, out _, out _);
            int bucket = Math.Clamp((int)Math.Floor(pxHue * BucketCount) % BucketCount, 0, BucketCount - 1);
            bucketScoreSum[bucket] += score;
            if (score > bucketBestScore[bucket])
            {
                bucketBestScore[bucket] = score;
                bucketBestR[bucket] = r;
                bucketBestG[bucket] = g;
                bucketBestB[bucket] = b;
            }
        }

        if (bestScore < 0)
        {
            return ("#0A84FF", "#5E5CE6", "#64D2FF");
        }

        // Clamp luminance to [0.42, 0.72] so island tint stays readable & refined
        RgbToHsl(bestR / 255.0, bestG / 255.0, bestB / 255.0, out double h, out double sat, out double lum);
        sat = Math.Clamp(sat, 0.48, 0.88);
        lum = Math.Clamp(lum, 0.42, 0.72);
        HslToRgb(h, sat, lum, out byte cr, out byte cg, out byte cb);
        string primaryHex = $"#{cr:X2}{cg:X2}{cb:X2}";

        int primaryBucket = Math.Clamp((int)Math.Floor(h * BucketCount) % BucketCount, 0, BucketCount - 1);
        int secondBucket = -1;
        double secondBestSum = 0;
        for (int bIdx = 0; bIdx < BucketCount; bIdx++)
        {
            int dist = Math.Abs(bIdx - primaryBucket);
            dist = Math.Min(dist, BucketCount - dist);
            if (dist < 2 || bucketScoreSum[bIdx] <= 0.12) continue;
            if (bucketScoreSum[bIdx] > secondBestSum)
            {
                secondBestSum = bucketScoreSum[bIdx];
                secondBucket = bIdx;
            }
        }

        string secondaryHex;
        if (secondBucket >= 0)
        {
            RgbToHsl(bucketBestR[secondBucket], bucketBestG[secondBucket], bucketBestB[secondBucket], out double h2, out double s2, out double l2);
            HslToRgb(h2, Math.Clamp(s2, 0.46, 0.88), Math.Clamp(l2, 0.36, 0.68), out byte sr, out byte sg, out byte sb);
            secondaryHex = $"#{sr:X2}{sg:X2}{sb:X2}";
        }
        else
        {
            double h2 = (h + 0.095) % 1.0;
            HslToRgb(h2, Math.Clamp(sat * 0.90, 0.42, 0.85), Math.Clamp(lum - 0.06, 0.34, 0.66), out byte sr, out byte sg, out byte sb);
            secondaryHex = $"#{sr:X2}{sg:X2}{sb:X2}";
        }

        int thirdBucket = -1;
        double thirdBestSum = 0;
        for (int bIdx = 0; bIdx < BucketCount; bIdx++)
        {
            int distP = Math.Abs(bIdx - primaryBucket);
            distP = Math.Min(distP, BucketCount - distP);
            int distS = secondBucket >= 0 ? Math.Min(Math.Abs(bIdx - secondBucket), BucketCount - Math.Abs(bIdx - secondBucket)) : 2;
            if (distP < 2 || distS < 2 || bucketScoreSum[bIdx] <= 0.08) continue;
            if (bucketScoreSum[bIdx] > thirdBestSum)
            {
                thirdBestSum = bucketScoreSum[bIdx];
                thirdBucket = bIdx;
            }
        }

        string highlightHex;
        if (thirdBucket >= 0)
        {
            RgbToHsl(bucketBestR[thirdBucket], bucketBestG[thirdBucket], bucketBestB[thirdBucket], out double h3, out double s3, out double l3);
            HslToRgb(h3, Math.Clamp(s3, 0.50, 0.92), Math.Clamp(l3 + 0.08, 0.52, 0.78), out byte hr, out byte hg, out byte hb);
            highlightHex = $"#{hr:X2}{hg:X2}{hb:X2}";
        }
        else
        {
            double h3 = (h + 0.16) % 1.0;
            HslToRgb(h3, Math.Clamp(sat * 0.94, 0.45, 0.90), Math.Clamp(lum + 0.12, 0.54, 0.78), out byte hr, out byte hg, out byte hb);
            highlightHex = $"#{hr:X2}{hg:X2}{hb:X2}";
        }

        return (primaryHex, secondaryHex, highlightHex);
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        l = (max + min) * 0.5;
        double d = max - min;
        if (d < 1e-6)
        {
            h = 0;
            s = 0;
            return;
        }
        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6.0;
    }

    private static void HslToRgb(double h, double s, double l, out byte r, out byte g, out byte b)
    {
        if (s < 1e-6)
        {
            byte v = (byte)Math.Clamp((int)Math.Round(l * 255.0), 0, 255);
            r = g = b = v;
            return;
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
        double p = 2.0 * l - q;
        r = (byte)Math.Clamp((int)Math.Round(Hue2Rgb(p, q, h + 1.0 / 3.0) * 255.0), 0, 255);
        g = (byte)Math.Clamp((int)Math.Round(Hue2Rgb(p, q, h) * 255.0), 0, 255);
        b = (byte)Math.Clamp((int)Math.Round(Hue2Rgb(p, q, h - 1.0 / 3.0) * 255.0), 0, 255);
    }

    private static double Hue2Rgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    }
}
