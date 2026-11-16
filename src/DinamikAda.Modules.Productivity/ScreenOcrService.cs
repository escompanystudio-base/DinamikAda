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

        string extracted;
        uint maxDimension = OcrEngine.MaxImageDimension;
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
            extracted = await RecognizeTextAsync(resized, ct).ConfigureAwait(false);
        }
        else extracted = await RecognizeTextAsync(sb, ct).ConfigureAwait(false);
        bool hasText = !string.IsNullOrWhiteSpace(extracted);
        if (hasText)
        {
            _clipboard.AddTextEntry(extracted, "OCR", copyToSystemClipboard: true);
        }

        return new ScreenOcrCaptureResult(pngPath, extracted, hasText);
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
