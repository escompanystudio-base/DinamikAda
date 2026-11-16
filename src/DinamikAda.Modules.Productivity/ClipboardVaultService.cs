using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Modules.Productivity;

public enum SmartClipboardKind
{
    UnixTimestamp = 0,
    JsonFormat = 1,
    Base64Text = 2,
    CleanUrl = 3
}

public sealed record SmartClipboardSuggestion(
    SmartClipboardKind Kind,
    string ActionLabel,
    string Preview,
    string TransformedText);

public static class ClipboardPrivacyFilter
{
    public static bool ShouldExclude(
        bool hasExcludeFromMonitorFormat,
        bool hasCanIncludeInHistoryFormat,
        int? canIncludeValue,
        bool hasCanUploadCloudFormat,
        int? canUploadValue)
    {
        if (hasExcludeFromMonitorFormat) return true;
        if (hasCanIncludeInHistoryFormat && canIncludeValue == 0) return true;
        if (hasCanUploadCloudFormat && canUploadValue == 0) return true;
        return false;
    }
}

internal static partial class ClipboardNative
{
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;
    public const uint GMEM_MOVEABLE = 0x0002;
    public const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct DATA_BLOB
    {
        public uint cbData;
        public nint pbData;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    public static partial nint GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll")]
    public static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalFree(nint hMem);

    [LibraryImport("kernel32.dll")]
    public static partial nint LocalFree(nint hMem);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CryptProtectData(
        DATA_BLOB* pDataIn,
        nint szDataDescr,
        nint pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        uint dwFlags,
        DATA_BLOB* pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CryptUnprotectData(
        DATA_BLOB* pDataIn,
        nint ppszDataDescr,
        nint pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        uint dwFlags,
        DATA_BLOB* pDataOut);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial uint DragQueryFileW(nint hDrop, uint iFile, char* lpszFile, uint cch);
}

public sealed class ClipboardVaultService : IDisposable
{
    private static readonly uint FmtExclude = NativeMethods.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtCanInclude = NativeMethods.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint FmtCanUpload = NativeMethods.RegisterClipboardFormatW("CanUploadToCloudClipboard");
    private static readonly JsonSerializerOptions SmartJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly HashSet<string> TrackingQueryParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "mc_cid", "mc_eid",
        "igshid", "si", "yclid", "_hsenc", "_hsmi", "ref_src"
    };

    private readonly string _vaultPath;
    private int _maxItems;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _diskLock = new(1, 1);
    private readonly List<ClipboardEntry> _entries = new();
    private nint _attachedHwnd;
    private long _lastCaptureTickMs;
    private string? _lastSuppressedText;
    private long _lastSuppressedTicksMs;
    private CancellationTokenSource? _saveDebounceCts;
    private List<ClipboardEntry>? _pendingSaveSnapshot;
    private volatile bool _disposed;

    public string Language { get; set; } = "tr";

    public event EventHandler<IReadOnlyList<ClipboardEntry>>? EntriesChanged;
    public event EventHandler<ClipboardEntry>? EntryCaptured;

    public ClipboardVaultService(string? vaultPath = null, int maxItems = 25)
    {
        _vaultPath = vaultPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinamikAda",
            "clipboard.json");
        _maxItems = Math.Clamp(maxItems, 5, 100);
        Load();
    }

    public IReadOnlyList<ClipboardEntry> GetEntries()
    {
        lock (_sync) return _entries.ToList();
    }

    public void SetMaxItems(int maxItems)
    {
        int clamped = Math.Clamp(maxItems, 5, 100);
        List<ClipboardEntry>? trimmed = null;
        lock (_sync)
        {
            if (_disposed || _maxItems == clamped) return;
            _maxItems = clamped;
            int before = _entries.Count;
            SortAndTrimLocked();
            if (_entries.Count != before)
            {
                ScheduleSaveLocked();
                trimmed = _entries.ToList();
            }
        }

        if (trimmed is not null)
        {
            EntriesChanged?.Invoke(this, trimmed);
        }
    }

    public void AttachToWindow(nint hwnd)
    {
        if (_attachedHwnd != 0)
        {
            NativeMethods.RemoveClipboardFormatListener(_attachedHwnd);
        }
        _attachedHwnd = hwnd;
        if (hwnd != 0)
        {
            NativeMethods.AddClipboardFormatListener(hwnd);
        }
    }

    public void OnWmClipboardUpdate()
    {
        long now = Environment.TickCount64;
        if (now - _lastCaptureTickMs < 75) return;
        _lastCaptureTickMs = now;

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (TryCaptureOnce()) return;
                await Task.Delay(12);
            }
        });
    }

    public void PinToggle(string id)
    {
        lock (_sync)
        {
            int idx = _entries.FindIndex(e => e.Id == id);
            if (idx < 0) return;
            _entries[idx] = _entries[idx] with { IsPinned = !_entries[idx].IsPinned };
            SortAndTrimLocked();
            ScheduleSaveLocked();
        }
        EntriesChanged?.Invoke(this, GetEntries());
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            if (_entries.RemoveAll(e => e.Id == id) == 0) return;
            ScheduleSaveLocked();
        }
        EntriesChanged?.Invoke(this, GetEntries());
    }

    public void ClearUnpinned()
    {
        lock (_sync)
        {
            _entries.RemoveAll(e => !e.IsPinned);
            ScheduleSaveLocked();
        }
        EntriesChanged?.Invoke(this, GetEntries());
    }

    public unsafe bool CopyTextToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        lock (_sync)
        {
            _lastSuppressedText = text;
            _lastSuppressedTicksMs = Environment.TickCount64;
        }

        if (!OpenClipboardWithRetry(_attachedHwnd)) return false;
        try
        {
            if (!ClipboardNative.EmptyClipboard()) return false;
            int bytes = (text.Length + 1) * 2;
            nint hMem = ClipboardNative.GlobalAlloc(ClipboardNative.GMEM_MOVEABLE, (nuint)bytes);
            if (hMem == 0) return false;

            bool ownershipTransferred = false;
            try
            {
                nint ptr = ClipboardNative.GlobalLock(hMem);
                if (ptr == 0) return false;
                try
                {
                    fixed (char* src = text)
                    {
                        Buffer.MemoryCopy(src, (void*)ptr, bytes, text.Length * 2L);
                        ((char*)ptr)[text.Length] = '\0';
                    }
                }
                finally
                {
                    ClipboardNative.GlobalUnlock(hMem);
                }

                if (ClipboardNative.SetClipboardData(ClipboardNative.CF_UNICODETEXT, hMem) == 0)
                    return false;

                ownershipTransferred = true;
                return true;
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    ClipboardNative.GlobalFree(hMem);
                }
            }
        }
        finally
        {
            ClipboardNative.CloseClipboard();
        }
    }

    private static bool OpenClipboardWithRetry(nint hwnd)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (ClipboardNative.OpenClipboard(hwnd))
                return true;
            if (attempt < 2)
                Thread.Sleep(10);
        }
        return false;
    }

    private bool TryCaptureOnce()
    {
        if (!ClipboardNative.OpenClipboard(_attachedHwnd)) return false;
        try
        {
            bool hasExcl = FmtExclude != 0 && NativeMethods.IsClipboardFormatAvailable(FmtExclude);
            bool hasInc = FmtCanInclude != 0 && NativeMethods.IsClipboardFormatAvailable(FmtCanInclude);
            bool hasUp = FmtCanUpload != 0 && NativeMethods.IsClipboardFormatAvailable(FmtCanUpload);

            int? incVal = hasInc ? ReadDwordFormat(FmtCanInclude) : null;
            int? upVal = hasUp ? ReadDwordFormat(FmtCanUpload) : null;

            if (ClipboardPrivacyFilter.ShouldExclude(hasExcl, hasInc, incVal, hasUp, upVal))
            {
                return true;
            }

            ClipboardEntry? captured = null;
            if (NativeMethods.IsClipboardFormatAvailable(ClipboardNative.CF_HDROP))
            {
                var files = ReadDropFiles();
                if (files.Length > 0)
                {
                    string preview = files.Length == 1
                        ? Path.GetFileName(files[0])
                        : $"{Path.GetFileName(files[0])} (+{files.Length - 1})";
                    captured = new ClipboardEntry(
                        Guid.NewGuid().ToString("N")[..10],
                        "Files",
                        preview,
                        string.Join(Environment.NewLine, files),
                        null,
                        files,
                        false,
                        DateTimeOffset.UtcNow);
                }
            }
            else if (NativeMethods.IsClipboardFormatAvailable(ClipboardNative.CF_UNICODETEXT))
            {
                string? text = ReadUnicodeText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    bool suppressed;
                    long now = Environment.TickCount64;
                    lock (_sync)
                    {
                        bool fresh = _lastSuppressedText is not null && now - _lastSuppressedTicksMs <= 2000;
                        suppressed = fresh && string.Equals(text, _lastSuppressedText, StringComparison.Ordinal);
                        if (suppressed || !fresh)
                        {
                            _lastSuppressedText = null;
                        }
                    }
                    if (suppressed) return true;

                    string trimmed = text.Trim().Replace("\r\n", " ").Replace('\n', ' ');
                    string preview = trimmed.Length > 72 ? trimmed[..72] + "…" : trimmed;
                    captured = new ClipboardEntry(
                        Guid.NewGuid().ToString("N")[..10],
                        "Text",
                        preview,
                        text,
                        null,
                        ImmutableArray<string>.Empty,
                        false,
                        DateTimeOffset.UtcNow);
                }
            }

            if (captured is null) return true;

            RecordEntry(captured);
            return true;
        }
        finally
        {
            ClipboardNative.CloseClipboard();
        }
    }

    public ClipboardEntry? AddTextEntry(string text, string kindLabel = "Text", bool copyToSystemClipboard = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string trimmed = text.Trim().Replace("\r\n", " ").Replace('\n', ' ');
        string preview = trimmed.Length > 72 ? trimmed[..72] + "…" : trimmed;
        var entry = new ClipboardEntry(
            Guid.NewGuid().ToString("N")[..10],
            string.IsNullOrWhiteSpace(kindLabel) ? "Text" : kindLabel,
            preview,
            text,
            null,
            ImmutableArray<string>.Empty,
            false,
            DateTimeOffset.UtcNow);

        RecordEntry(entry);
        if (copyToSystemClipboard)
        {
            CopyTextToClipboard(text);
        }
        return entry;
    }

    public SmartClipboardSuggestion? GetSmartSuggestion(ClipboardEntry? entry) =>
        entry is null ? null : TryDetectSmartAction(entry.FullText ?? entry.PreviewText, Language);

    public static SmartClipboardSuggestion? TryDetectSmartAction(string? text) =>
        TryDetectSmartAction(text, "tr");

    public static SmartClipboardSuggestion? TryDetectSmartAction(string? text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string lang = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";

        if (TryConvertUnixTimestamp(text, out string tsFormatted))
        {
            return new SmartClipboardSuggestion(
                SmartClipboardKind.UnixTimestamp,
                Strings.SmartTransformUnix(lang),
                MakeSmartPreview(tsFormatted),
                tsFormatted);
        }

        if (TryCleanTrackingUrl(text, out string cleanUrl))
        {
            return new SmartClipboardSuggestion(
                SmartClipboardKind.CleanUrl,
                Strings.SmartTransformCleanUrl(lang),
                MakeSmartPreview(cleanUrl),
                cleanUrl);
        }

        if (TryFormatJson(text, out string prettyJson))
        {
            return new SmartClipboardSuggestion(
                SmartClipboardKind.JsonFormat,
                Strings.SmartTransformJson(lang),
                MakeSmartPreview(prettyJson),
                prettyJson);
        }

        if (TryDecodeBase64(text, out string decoded))
        {
            return new SmartClipboardSuggestion(
                SmartClipboardKind.Base64Text,
                Strings.SmartTransformBase64(lang),
                MakeSmartPreview(decoded),
                decoded);
        }

        return null;
    }

    public static string? ApplySmartAction(string? rawText, SmartClipboardKind kind)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        return kind switch
        {
            SmartClipboardKind.UnixTimestamp => TryConvertUnixTimestamp(rawText, out string ts) ? ts : null,
            SmartClipboardKind.JsonFormat => TryFormatJson(rawText, out string json) ? json : null,
            SmartClipboardKind.Base64Text => TryDecodeBase64(rawText, out string decoded) ? decoded : null,
            SmartClipboardKind.CleanUrl => TryCleanTrackingUrl(rawText, out string url) ? url : null,
            _ => null
        };
    }

    private static bool TryConvertUnixTimestamp(string text, out string formatted)
    {
        formatted = string.Empty;
        string trimmed = text.Trim();
        if (trimmed.Length is not (10 or 13)) return false;
        foreach (char c in trimmed)
        {
            if (c is < '0' or > '9') return false;
        }

        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            return false;

        DateTimeOffset dto;
        if (trimmed.Length == 10)
        {
            if (value is < 1_000_000_000L or > 9_999_999_999L) return false;
            dto = DateTimeOffset.FromUnixTimeSeconds(value);
        }
        else
        {
            if (value is < 1_000_000_000_000L or > 9_999_999_999_999L) return false;
            dto = DateTimeOffset.FromUnixTimeMilliseconds(value);
        }

        formatted = dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss (zzz)", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryFormatJson(string text, out string formatted)
    {
        formatted = string.Empty;
        string trimmed = text.Trim();
        if (trimmed.Length is < 2 or > 1_000_000) return false;
        bool isObj = trimmed[0] == '{' && trimmed[^1] == '}';
        bool isArr = trimmed[0] == '[' && trimmed[^1] == ']';
        if (!isObj && !isArr) return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            string pretty = JsonSerializer.Serialize(doc.RootElement, SmartJsonOptions);
            if (string.Equals(pretty.Replace("\r\n", "\n"), trimmed.Replace("\r\n", "\n"), StringComparison.Ordinal))
            {
                return false;
            }
            formatted = pretty;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64(string text, out string decoded)
    {
        decoded = string.Empty;
        string trimmed = text.Trim();
        if (trimmed.Length < 8 || trimmed.Length > 512_000 || trimmed.Length % 4 != 0) return false;

        foreach (char c in trimmed)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '='))
            {
                return false;
            }
        }

        byte[] buffer = new byte[(trimmed.Length / 4) * 3];
        if (!Convert.TryFromBase64String(trimmed, buffer, out int written) || written == 0)
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = StrictUtf8.GetString(buffer, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('\uFFFD'))
        {
            return false;
        }

        bool hasNonLetterCue = trimmed.IndexOfAny(['=', '+', '/']) >= 0;
        foreach (char c in candidate)
        {
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            {
                return false;
            }
            if (!hasNonLetterCue && (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)))
            {
                hasNonLetterCue = true;
            }
        }

        if (!hasNonLetterCue || string.Equals(candidate, trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        decoded = candidate;
        return true;
    }

    private static bool TryCleanTrackingUrl(string text, out string cleaned)
    {
        cleaned = string.Empty;
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 16_384 || trimmed.Any(char.IsWhiteSpace)) return false;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string query = uri.Query;
        if (string.IsNullOrEmpty(query) || query.Length <= 1) return false;

        var kept = new List<string>();
        int removed = 0;
        foreach (string segment in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = segment.IndexOf('=');
            string rawKey = eq >= 0 ? segment[..eq] : segment;
            string decodedKey;
            try
            {
                decodedKey = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            }
            catch
            {
                decodedKey = rawKey;
            }

            if (IsTrackingQueryParam(decodedKey))
            {
                removed++;
            }
            else
            {
                kept.Add(segment);
            }
        }

        if (removed == 0) return false;

        var sb = new StringBuilder(uri.GetLeftPart(UriPartial.Path));
        if (kept.Count > 0)
        {
            sb.Append('?').Append(string.Join('&', kept));
        }
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            sb.Append(uri.Fragment);
        }

        cleaned = sb.ToString();
        return !string.Equals(cleaned, trimmed, StringComparison.Ordinal);
    }

    private static bool IsTrackingQueryParam(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingQueryParams.Contains(key);

    private static string MakeSmartPreview(string text)
    {
        string singleLine = text.Trim().Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length > 64 ? singleLine[..64] + "…" : singleLine;
    }

    private void RecordEntry(ClipboardEntry captured)
    {
        lock (_sync)
        {
            _entries.RemoveAll(e => string.Equals(e.FullText, captured.FullText, StringComparison.Ordinal) && !e.IsPinned);
            if (!_entries.Any(e => e.IsPinned && string.Equals(e.FullText, captured.FullText, StringComparison.Ordinal)))
            {
                _entries.Insert(0, captured);
            }
            SortAndTrimLocked();
            ScheduleSaveLocked();
        }

        EntryCaptured?.Invoke(this, captured);
        EntriesChanged?.Invoke(this, GetEntries());
    }

    private static unsafe int? ReadDwordFormat(uint fmt)
    {
        nint h = ClipboardNative.GetClipboardData(fmt);
        if (h == 0 || ClipboardNative.GlobalSize(h) < 4) return null;
        nint p = ClipboardNative.GlobalLock(h);
        if (p == 0) return null;
        try { return *(int*)p; }
        finally { ClipboardNative.GlobalUnlock(h); }
    }

    private static unsafe string? ReadUnicodeText()
    {
        nint h = ClipboardNative.GetClipboardData(ClipboardNative.CF_UNICODETEXT);
        if (h == 0) return null;
        nuint byteSize = ClipboardNative.GlobalSize(h);
        if (byteSize < 2) return null;
        nint p = ClipboardNative.GlobalLock(h);
        if (p == 0) return null;
        try
        {
            int maxChars = (int)Math.Min(byteSize / 2, 1_000_000);
            var span = new ReadOnlySpan<char>((void*)p, maxChars);
            int nullIdx = span.IndexOf('\0');
            if (nullIdx >= 0) span = span[..nullIdx];
            return new string(span);
        }
        finally
        {
            ClipboardNative.GlobalUnlock(h);
        }
    }

    private static unsafe ImmutableArray<string> ReadDropFiles()
    {
        nint hDrop = ClipboardNative.GetClipboardData(ClipboardNative.CF_HDROP);
        if (hDrop == 0) return ImmutableArray<string>.Empty;
        uint count = ClipboardNative.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        if (count == 0) return ImmutableArray<string>.Empty;

        uint capped = Math.Min(count, 64u);
        var builder = ImmutableArray.CreateBuilder<string>((int)capped);
        char* buf = stackalloc char[512];
        for (uint i = 0; i < capped; i++)
        {
            uint len = ClipboardNative.DragQueryFileW(hDrop, i, buf, 512);
            if (len > 0) builder.Add(new string(buf, 0, (int)len));
        }
        return builder.ToImmutable();
    }

    private void SortAndTrimLocked()
    {
        _entries.Sort((a, b) =>
        {
            int pin = b.IsPinned.CompareTo(a.IsPinned);
            return pin != 0 ? pin : b.CapturedUtc.CompareTo(a.CapturedUtc);
        });
        while (_entries.Count > _maxItems)
        {
            int lastUnpinned = _entries.FindLastIndex(e => !e.IsPinned);
            if (lastUnpinned >= 0) _entries.RemoveAt(lastUnpinned);
            else _entries.RemoveAt(_entries.Count - 1);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_vaultPath)) return;
            byte[] raw = File.ReadAllBytes(_vaultPath);
            if (raw.Length == 0) return;

            byte[]? plainBytes = UnprotectBytes(raw);
            bool migratedFromPlaintext = false;
            if (plainBytes is null && LooksLikePlaintextJson(raw))
            {
                plainBytes = raw;
                migratedFromPlaintext = true;
            }

            if (plainBytes is null) return;

            var list = JsonSerializer.Deserialize<List<ClipboardEntry>>(plainBytes);
            if (list is not null)
            {
                lock (_sync)
                {
                    _entries.AddRange(list);
                    int before = _entries.Count;
                    SortAndTrimLocked();
                    if (migratedFromPlaintext || _entries.Count != before)
                    {
                        ScheduleSaveLocked();
                    }
                }
            }
        }
        catch { }
    }

    private void ScheduleSaveLocked()
    {
        if (_disposed) return;
        _pendingSaveSnapshot = _entries.ToList();
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _saveDebounceCts = new CancellationTokenSource();
        CancellationToken ct = _saveDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    FlushPendingSave();
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private void FlushPendingSave()
    {
        bool acquired = false;
        try
        {
            _diskLock.Wait();
            acquired = true;
            List<ClipboardEntry>? snapshot;
            lock (_sync)
            {
                snapshot = _pendingSaveSnapshot;
                _pendingSaveSnapshot = null;
            }
            if (snapshot is null) return;
            WriteEncryptedSnapshot(snapshot);
        }
        catch (ObjectDisposedException)
        {
        }
        catch { }
        finally
        {
            if (acquired)
            {
                try { _diskLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private void WriteEncryptedSnapshot(List<ClipboardEntry> snapshot)
    {
        string? dir = Path.GetDirectoryName(_vaultPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        byte[] cipherBytes = ProtectBytes(plainBytes);
        string tmp = _vaultPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, cipherBytes);
            File.Move(tmp, _vaultPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static bool LooksLikePlaintextJson(byte[] bytes)
    {
        int i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            i = 3;
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;
        return i < bytes.Length && (bytes[i] == (byte)'[' || bytes[i] == (byte)'{');
    }

    private static unsafe byte[] ProtectBytes(byte[] plain)
    {
        if (plain.Length == 0) return Array.Empty<byte>();
        ClipboardNative.DATA_BLOB outBlob = default;
        try
        {
            fixed (byte* pIn = plain)
            {
                var inBlob = new ClipboardNative.DATA_BLOB { cbData = (uint)plain.Length, pbData = (nint)pIn };
                if (!ClipboardNative.CryptProtectData(&inBlob, 0, 0, 0, 0, ClipboardNative.CRYPTPROTECT_UI_FORBIDDEN, &outBlob)
                    || outBlob.pbData == 0 || outBlob.cbData == 0)
                {
                    throw new IOException("DPAPI CryptProtectData failed.");
                }
                byte[] result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, (int)outBlob.cbData);
                return result;
            }
        }
        finally
        {
            if (outBlob.pbData != 0) ClipboardNative.LocalFree(outBlob.pbData);
        }
    }

    private static unsafe byte[]? UnprotectBytes(byte[] cipher)
    {
        if (cipher.Length == 0) return null;
        ClipboardNative.DATA_BLOB outBlob = default;
        try
        {
            fixed (byte* pIn = cipher)
            {
                var inBlob = new ClipboardNative.DATA_BLOB { cbData = (uint)cipher.Length, pbData = (nint)pIn };
                if (!ClipboardNative.CryptUnprotectData(&inBlob, 0, 0, 0, 0, ClipboardNative.CRYPTPROTECT_UI_FORBIDDEN, &outBlob)
                    || outBlob.pbData == 0)
                {
                    return null;
                }
                byte[] result = new byte[outBlob.cbData];
                if (outBlob.cbData > 0) Marshal.Copy(outBlob.pbData, result, 0, (int)outBlob.cbData);
                return result;
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (outBlob.pbData != 0) ClipboardNative.LocalFree(outBlob.pbData);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _saveDebounceCts;
            _saveDebounceCts = null;
        }

        try { cts?.Cancel(); } catch { }
        cts?.Dispose();

        FlushPendingSave();

        if (_attachedHwnd != 0)
        {
            NativeMethods.RemoveClipboardFormatListener(_attachedHwnd);
            _attachedHwnd = 0;
        }
        _diskLock.Dispose();
    }
}
