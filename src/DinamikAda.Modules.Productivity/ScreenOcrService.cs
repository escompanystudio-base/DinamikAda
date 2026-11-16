using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DinamikAda.Modules.Productivity;

public sealed record ScreenOcrCaptureResult(
    string ImagePath,
    string ExtractedText,
    bool HasText);

/// <summary>
/// Saves captured screen regions as crisp PNGs directly into FileShelfService
/// and extracts text offline via Windows.Media.Ocr into ClipboardVaultService.
/// </summary>
public sealed class ScreenOcrService
{
    private readonly string _shotsDir;
    private readonly FileShelfService _shelf;
    private readonly ClipboardVaultService _clipboard;

    public ScreenOcrService(
        FileShelfService shelf,
        ClipboardVaultService clipboard,
        string? shotsDir = null)
    {
        _shelf = shelf;
        _clipboard = clipboard;
        _shotsDir = shotsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinamikAda",
            "CapturedShots");
    }

    public static string FormatOcrLines(IEnumerable<string> lines)
    {
        var nonEmpty = lines
            .Select(l => l?.Trim() ?? string.Empty)
            .Where(l => !string.IsNullOrEmpty(l));
        return string.Join(Environment.NewLine, nonEmpty);
    }

    public async Task<ScreenOcrCaptureResult> ProcessCapturedBgraAsync(
        byte[] bgraPixels,
        int width,
        int height,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Capture dimensions must be positive.");
        }
        if ((long)width * height * 4 != bgraPixels.Length)
            throw new ArgumentException("Pixel buffer length does not match capture dimensions.", nameof(bgraPixels));
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_shotsDir);
        string pngPath = Path.Combine(_shotsDir, $"ocr_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");

        using var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        sb.CopyFromBuffer(bgraPixels.AsBuffer());

        using (var ras = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras).AsTask(ct).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(sb);
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

            ras.Seek(0);
            await using var fs = new FileStream(pngPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            using var netStream = ras.AsStreamForRead();
            await netStream.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        _shelf.AddPaths([pngPath]);

        string extracted = await ExtractTextMultiPassAsync(bgraPixels, width, height, sb, pngPath, ct).ConfigureAwait(false);
        bool hasText = !string.IsNullOrWhiteSpace(extracted);
        if (hasText)
        {
            _clipboard.AddTextEntry(extracted, "OCR", copyToSystemClipboard: true);
        }

        return new ScreenOcrCaptureResult(pngPath, extracted, hasText);
    }

    /// <summary>
    /// Upscales compact regions with bilinear filtering, adds an edge-matched quiet margin
    /// (since Windows.Media.Ocr often returns empty on crops below ~40px tall or tight margins),
    /// and optionally inverts RGB channels as a contrast fallback.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) PrepareBgraForOcr(
        byte[] bgraPixels,
        int width,
        int height,
        int scaleFactor = 3,
        int paddingPx = 36,
        bool invertRgb = false)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Capture dimensions must be positive.");
        if ((long)width * height * 4 != bgraPixels.Length)
            throw new ArgumentException("Pixel buffer length does not match capture dimensions.", nameof(bgraPixels));

        int scale = Math.Clamp(scaleFactor, 1, 6);
        int pad = Math.Clamp(paddingPx, 0, 128);
        int innerW = width * scale;
        int innerH = height * scale;
        int dstW = innerW + pad * 2;
        int dstH = innerH + pad * 2;

        // Average the four corners for a border fill matching light or dark captures cleanly.
        ReadCornerAverageBgra(bgraPixels, width, height, invertRgb, out byte borderB, out byte borderG, out byte borderR);

        byte[] dst = new byte[dstW * dstH * 4];
        for (int i = 0; i < dst.Length; i += 4)
        {
            dst[i] = borderB;
            dst[i + 1] = borderG;
            dst[i + 2] = borderR;
            dst[i + 3] = 255;
        }

        for (int y = 0; y < innerH; y++)
        {
            double srcY = scale == 1 ? y : (y + 0.5) / scale - 0.5;
            double clampedY = Math.Clamp(srcY, 0.0, height - 1);
            int y0 = (int)Math.Floor(clampedY);
            int y1 = Math.Min(y0 + 1, height - 1);
            double fy = clampedY - y0;

            int dstRowStart = (y + pad) * dstW * 4;
            int row0 = y0 * width * 4;
            int row1 = y1 * width * 4;

            for (int x = 0; x < innerW; x++)
            {
                double srcX = scale == 1 ? x : (x + 0.5) / scale - 0.5;
                double clampedX = Math.Clamp(srcX, 0.0, width - 1);
                int x0 = (int)Math.Floor(clampedX);
                int x1 = Math.Min(x0 + 1, width - 1);
                double fx = clampedX - x0;

                int i00 = row0 + x0 * 4;
                int i10 = row0 + x1 * 4;
                int i01 = row1 + x0 * 4;
                int i11 = row1 + x1 * 4;

                double w00 = (1.0 - fx) * (1.0 - fy);
                double w10 = fx * (1.0 - fy);
                double w01 = (1.0 - fx) * fy;
                double w11 = fx * fy;

                byte b = (byte)Math.Clamp((int)Math.Round(bgraPixels[i00] * w00 + bgraPixels[i10] * w10 + bgraPixels[i01] * w01 + bgraPixels[i11] * w11), 0, 255);
                byte g = (byte)Math.Clamp((int)Math.Round(bgraPixels[i00 + 1] * w00 + bgraPixels[i10 + 1] * w10 + bgraPixels[i01 + 1] * w01 + bgraPixels[i11 + 1] * w11), 0, 255);
                byte r = (byte)Math.Clamp((int)Math.Round(bgraPixels[i00 + 2] * w00 + bgraPixels[i10 + 2] * w10 + bgraPixels[i01 + 2] * w01 + bgraPixels[i11 + 2] * w11), 0, 255);

                if (invertRgb)
                {
                    b = (byte)(255 - b);
                    g = (byte)(255 - g);
                    r = (byte)(255 - r);
                }

                int di = dstRowStart + (x + pad) * 4;
                dst[di] = b;
                dst[di + 1] = g;
                dst[di + 2] = r;
                dst[di + 3] = 255;
            }
        }

        return (dst, dstW, dstH);
    }

    private static void ReadCornerAverageBgra(byte[] bgra, int width, int height, bool invertRgb, out byte b, out byte g, out byte r)
    {
        int[] offsets =
        [
            0,
            (width - 1) * 4,
            (height - 1) * width * 4,
            ((height - 1) * width + (width - 1)) * 4
        ];
        int sumB = 0, sumG = 0, sumR = 0;
        foreach (int idx in offsets)
        {
            sumB += bgra[idx];
            sumG += bgra[idx + 1];
            sumR += bgra[idx + 2];
        }
        b = (byte)(sumB / 4);
        g = (byte)(sumG / 4);
        r = (byte)(sumR / 4);
        if (invertRgb)
        {
            b = (byte)(255 - b);
            g = (byte)(255 - g);
            r = (byte)(255 - r);
        }
    }

    private static async Task<string> ExtractTextMultiPassAsync(
        byte[] bgraPixels,
        int width,
        int height,
        SoftwareBitmap rawBitmap,
        string pngPath,
        CancellationToken ct)
    {
        uint maxDimension = Math.Max(64u, OcrEngine.MaxImageDimension);
        if (Math.Max(width, height) > maxDimension)
        {
            await using var input = File.OpenRead(pngPath);
            using var randomAccess = input.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccess).AsTask(ct).ConfigureAwait(false);
            double scale = maxDimension / (double)Math.Max(width, height);
            using var resized = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = (uint)Math.Max(1, Math.Floor(width * scale)),
                    ScaledHeight = (uint)Math.Max(1, Math.Floor(height * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant
                }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
            return await RecognizeTextAsync(resized, ct).ConfigureAwait(false);
        }

        // Standard desktop crops large enough for WinRT OCR try direct recognition first.
        if (width >= 220 && height >= 64)
        {
            string direct = await RecognizeTextAsync(rawBitmap, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
        }

        const int pad = 36;
        int desiredScale = (height < 64 || width < 220) ? 3 : 2;
        int maxScaleAllowed = (int)Math.Max(1, Math.Min((maxDimension - pad * 2) / (double)width, (maxDimension - pad * 2) / (double)height));
        int scaleFactor = Math.Clamp(desiredScale, 1, maxScaleAllowed);

        var (upPixels, upW, upH) = PrepareBgraForOcr(bgraPixels, width, height, scaleFactor, pad, invertRgb: false);
        string upscaledText = await RecognizeBgraBufferAsync(upPixels, upW, upH, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(upscaledText)) return upscaledText;

        var (invPixels, invW, invH) = PrepareBgraForOcr(bgraPixels, width, height, scaleFactor, pad, invertRgb: true);
        return await RecognizeBgraBufferAsync(invPixels, invW, invH, ct).ConfigureAwait(false);
    }

    private static async Task<string> RecognizeBgraBufferAsync(byte[] bgraPixels, int width, int height, CancellationToken ct)
    {
        using var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bmp.CopyFromBuffer(bgraPixels.AsBuffer());
        return await RecognizeTextAsync(bmp, ct).ConfigureAwait(false);
    }

    private static async Task<string> RecognizeTextAsync(SoftwareBitmap bitmap, CancellationToken ct)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                var langs = OcrEngine.AvailableRecognizerLanguages;
                if (langs.Count > 0)
                {
                    engine = OcrEngine.TryCreateFromLanguage(langs[0]);
                }
                else
                {
                    engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
                }
            }
            if (engine is null) return string.Empty;

            // OcrEngine rejects images larger than MaxImageDimension (~4096) or smaller than 2px cleanly.
            var res = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(res.Text) && res.Lines.Count == 0)
            {
                return res.Text.Trim();
            }
            return FormatOcrLines(res.Lines.Select(l => l.Text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return string.Empty;
        }
    }
}
