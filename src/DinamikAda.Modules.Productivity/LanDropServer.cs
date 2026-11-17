using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace DinamikAda.Modules.Productivity;

public sealed record LanDropShareInfo(
    string ShareUrl,
    string LoopbackUrl,
    bool HasLanAddress,
    int FileCount,
    bool HasSharedText,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Ephemeral zero-privilege LAN HTTP/1.1 AirDrop-style file & clipboard server.
/// Uses TcpListener on an ephemeral port so non-admin Windows accounts never hit HttpListener URLACL errors.
/// </summary>
public sealed class LanDropServer : IDisposable
{
    private const long MaxUploadBytes = 128L * 1024 * 1024;
    private readonly string _inboxDir;
    private readonly object _sync = new();
    private readonly object _lifecycleSync = new();
    private readonly SemaphoreSlim _clientSlots = new(4, 4);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Timer? _expiryTimer;
    private string _token = string.Empty;
    private LanDropShareInfo? _currentShare;
    private bool _disposed;

    public event EventHandler<IReadOnlyList<string>>? FilesReceived;
    public event EventHandler<LanDropShareInfo?>? ShareStateChanged;

    public LanDropShareInfo? CurrentShare
    {
        get { lock (_sync) return _currentShare; }
    }

    public LanDropServer(string? inboxDir = null)
    {
        _inboxDir = inboxDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DinamikAda",
            "LanDrop");
    }

    public LanDropShareInfo StartShare(
        IReadOnlyList<string> filePaths,
        string? sharedText = null,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        TimeSpan lifetime = ttl ?? TimeSpan.FromMinutes(10);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(ttl), "Share lifetime must be between zero and one day.");
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopShare();

            var existingFiles = filePaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? cleanText = string.IsNullOrWhiteSpace(sharedText) ? null : sharedText;
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            string? lanIp = SelectBestLanIpv4();
            bool hasLan = !string.IsNullOrWhiteSpace(lanIp);
            string hostIp = lanIp ?? "127.0.0.1";

            string shareUrl = $"http://{hostIp}:{port}/s/{token}";
            string loopbackUrl = $"http://127.0.0.1:{port}/s/{token}";
            var expiresAt = DateTimeOffset.UtcNow + lifetime;

            var info = new LanDropShareInfo(
                ShareUrl: shareUrl,
                LoopbackUrl: loopbackUrl,
                HasLanAddress: hasLan,
                FileCount: existingFiles.Count,
                HasSharedText: cleanText is not null,
                ExpiresAtUtc: expiresAt);

            var cts = new CancellationTokenSource();
            lock (_sync)
            {
                _listener = listener;
                _cts = cts;
                _token = token;
                _currentShare = info;
                _expiryTimer = new Timer(_ => StopShare(token), null, lifetime, Timeout.InfiniteTimeSpan);
            }

            CancellationToken shareToken = cts.Token;
            _ = AcceptLoopAsync(listener, "/s/" + token, existingFiles, cleanText, shareToken);
            ShareStateChanged?.Invoke(this, info);
            return info;
        }
    }

    public void StopShare() => StopShare(null);

    private void StopShare(string? expectedToken)
    {
        lock (_lifecycleSync)
        {
            TcpListener? listener;
            CancellationTokenSource? cts;
            Timer? timer;
            bool hadShare;

            lock (_sync)
            {
                if (expectedToken is not null && _token != expectedToken) return;
                hadShare = _currentShare is not null;
                listener = _listener;
                cts = _cts;
                timer = _expiryTimer;
                _listener = null;
                _cts = null;
                _expiryTimer = null;
                _currentShare = null;
                _token = string.Empty;
            }

            timer?.Dispose();
            try { cts?.Cancel(); } catch { }
            try { listener?.Stop(); } catch { }
            cts?.Dispose();

            if (hadShare)
            {
                ShareStateChanged?.Invoke(this, null);
            }
        }
    }

    public static string? SelectBestLanIpv4()
    {
        try
        {
            var candidates = new List<(int Score, string Ip)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                string desc = $"{ni.Name} {ni.Description}".ToLowerInvariant();
                if (desc.Contains("vethernet") || desc.Contains("wsl") || desc.Contains("hyper-v") ||
                    desc.Contains("virtualbox") || desc.Contains("vmware") || desc.Contains("zerotier") ||
                    desc.Contains("tailscale") || desc.Contains("loopback"))
                {
                    continue;
                }

                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = ua.Address.GetAddressBytes();
                    if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;

                    int score = hasGateway ? 100 : 10;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 20;
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 15;
                    if (b[0] == 192 && b[1] == 168) score += 8;
                    else if (b[0] == 10) score += 6;
                    else if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) score += 4;
                    candidates.Add((score, ua.Address.ToString()));
                }
            }

            return candidates.OrderByDescending(c => c.Score).Select(c => c.Ip).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, string expectedPrefix, List<string> files, string? sharedText, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (!_clientSlots.Wait(0)) { client.Dispose(); continue; }
            _ = HandleAcceptedClientAsync(client, expectedPrefix, files, sharedText, ct);
        }
    }

    private async Task HandleAcceptedClientAsync(TcpClient client, string expectedPrefix, List<string> files, string? sharedText, CancellationToken ct)
    {
        try { await HandleClientAsync(client, expectedPrefix, files, sharedText, ct).ConfigureAwait(false); }
        finally { _clientSlots.Release(); }
    }

    private async Task HandleClientAsync(TcpClient client, string expectedPrefix, List<string> files, string? sharedText, CancellationToken shareCt)
    {
        using (client)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shareCt);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            CancellationToken ct = deadline.Token;
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 30000;
            using var stream = client.GetStream();
            try { await HandleRequestAsync(stream, expectedPrefix, files, sharedText, ct).ConfigureAwait(false); }
            catch (Exception) { /* Ignore client aborts and unavailable files. */ }
        }
    }

    internal async Task HandleRequestAsync(Stream stream, string expectedPrefix, List<string> files, string? sharedText, CancellationToken ct)
    {
        try
        {
            var req = await ReadHttpRequestAsync(stream, ct).ConfigureAwait(false);
            if (req is null) return;

            string path = req.Path;
            if (path != expectedPrefix && !path.StartsWith(expectedPrefix + "/", StringComparison.Ordinal))
            {
                await SendStatusAsync(stream, "404 Not Found", "Invalid or expired DinamikAda link.", ct).ConfigureAwait(false);
                return;
            }

            string subPath = path[expectedPrefix.Length..];
            if (req.Method == "GET" && (subPath.Length == 0 || subPath == "/"))
            {
                string html = BuildLandingHtml(expectedPrefix, files, sharedText, uploadedCount: 0);
                await SendResponseAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null, ct).ConfigureAwait(false);
                return;
            }

            if (req.Method == "GET" && subPath == "/bundle.zip" && files.Count > 0)
            {
                await SendZipBundleAsync(stream, files, ct).ConfigureAwait(false);
                return;
            }

            if (req.Method == "GET" && subPath.StartsWith("/dl/", StringComparison.Ordinal))
            {
                if (int.TryParse(subPath[4..], out int idx) && idx >= 0 && idx < files.Count && File.Exists(files[idx]))
                {
                    await SendFileAsync(stream, files[idx], ct).ConfigureAwait(false);
                }
                else
                {
                    await SendStatusAsync(stream, "404 Not Found", "File not found.", ct).ConfigureAwait(false);
                }
                return;
            }

            if (req.Method == "POST" && subPath == "/upload")
            {
                if (req.ExpectContinue)
                    await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
                // Spool the body to a temp file (bounded by MaxUploadBytes) and parse it through a
                // memory map: uploads never sit in the managed heap, so 4 concurrent 128 MB uploads
                // no longer spike memory to ~1 GB.
                string spoolPath = Path.Combine(Path.GetTempPath(), $"dinamikada-landrop-{Guid.NewGuid():N}.part");
                List<string> saved;
                try
                {
                    await ReadHttpBodyToFileAsync(stream, req, spoolPath, ct).ConfigureAwait(false);
                    saved = await Task.Run(() => SaveUploadedMultipartFromFile(req.ContentType, spoolPath, ct), ct).ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(spoolPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (saved.Count == 0)
                {
                    await SendStatusAsync(stream, "400 Bad Request", "No valid files in upload.", ct).ConfigureAwait(false);
                    return;
                }
                if (saved.Count > 0)
                {
                    FilesReceived?.Invoke(this, saved);
                }
                string html = BuildLandingHtml(expectedPrefix, files, sharedText, uploadedCount: saved.Count);
                await SendResponseAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null, ct).ConfigureAwait(false);
                return;
            }

            await SendStatusAsync(stream, "404 Not Found", "Not found.", ct).ConfigureAwait(false);
        }
        catch (HttpProtocolException ex)
        {
            try { await SendStatusAsync(stream, ex.Status, ex.Message, ct).ConfigureAwait(false); }
            catch (Exception) { /* The client may have disconnected. */ }
        }
    }

    private sealed record HttpRequestData(string Method, string Path, string ContentType, long ContentLength, bool IsChunked, bool ExpectContinue);
    private sealed class HttpProtocolException(string status, string message) : IOException(message)
    {
        public string Status { get; } = status;
    }

    private static async Task<HttpRequestData?> ReadHttpRequestAsync(Stream stream, CancellationToken ct)
    {
        var headerBuf = new List<byte>(4096);
        var single = new byte[1];
        int matchState = 0;
        while (headerBuf.Count < 32768)
        {
            int n = await stream.ReadAsync(single.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0) return null;
            byte b = single[0];
            headerBuf.Add(b);
            matchState = matchState switch
            {
                0 when b == '\r' => 1,
                1 when b == '\n' => 2,
                2 when b == '\r' => 3,
                3 when b == '\n' => 4,
                _ when b == '\r' => 1,
                _ => 0
            };
            if (matchState == 4) break;
        }

        if (matchState != 4) throw new HttpProtocolException("431 Request Header Fields Too Large", "Request headers are too large.");
        string headerText = Encoding.ASCII.GetString(headerBuf.ToArray());
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        string[] reqLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (reqLine.Length != 3 || !reqLine[1].StartsWith('/') || !reqLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            throw new HttpProtocolException("400 Bad Request", "Invalid request line.");

        string method = reqLine[0].ToUpperInvariant();
        string rawTarget = reqLine[1];
        int qIdx = rawTarget.IndexOf('?');
        string cleanPath = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;

        long contentLength = 0;
        bool isChunked = false;
        bool hasContentLength = false;
        bool expectContinue = false;
        string contentType = string.Empty;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new HttpProtocolException("400 Bad Request", "Invalid header.");
            string name = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(val, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long cl) ||
                    (hasContentLength && contentLength != cl))
                    throw new HttpProtocolException("400 Bad Request", "Invalid Content-Length.");
                contentLength = cl;
                hasContentLength = true;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                     val.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            {
                isChunked = true;
            }
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = val;
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                throw new HttpProtocolException("400 Bad Request", "Unsupported transfer encoding.");
            else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
                expectContinue = val.Equals("100-continue", StringComparison.OrdinalIgnoreCase);
        }

        if (contentLength > MaxUploadBytes) throw new HttpProtocolException("413 Payload Too Large", "Upload exceeds 128 MB.");
        if (isChunked && hasContentLength) throw new HttpProtocolException("400 Bad Request", "Ambiguous body length.");
        return new HttpRequestData(method, cleanPath, contentType, contentLength, isChunked, expectContinue);
    }

    private static async Task ReadHttpBodyToFileAsync(Stream stream, HttpRequestData req, string destPath, CancellationToken ct)
    {
        await using var output = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        if (req.IsChunked)
        {
            await ReadChunkedBodyAsync(stream, output, ct).ConfigureAwait(false);
            return;
        }

        byte[] buffer = new byte[81920];
        long remaining = req.ContentLength;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int n = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (n <= 0) throw new HttpProtocolException("400 Bad Request", "Incomplete request body.");
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            remaining -= n;
        }
    }

    private static async Task ReadChunkedBodyAsync(Stream stream, Stream ms, CancellationToken ct)
    {
        byte[] chunk = new byte[81920];
        while (true)
        {
            string? sizeLine = await ReadAsciiLineAsync(stream, ct).ConfigureAwait(false);
            if (sizeLine is null) throw new HttpProtocolException("400 Bad Request", "Incomplete chunk header.");
            int semi = sizeLine.IndexOf(';');
            string hexPart = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int chunkSize) || chunkSize < 0)
            {
                throw new HttpProtocolException("400 Bad Request", "Invalid chunk size.");
            }
            if (chunkSize == 0)
            {
                int trailerBytes = 0;
                while (true)
                {
                    string? trailer = await ReadAsciiLineAsync(stream, ct).ConfigureAwait(false);
                    if (trailer is null) throw new HttpProtocolException("400 Bad Request", "Incomplete chunk trailer.");
                    if (trailer.Length == 0) return;
                    trailerBytes += trailer.Length;
                    if (trailerBytes > 32768) throw new HttpProtocolException("431 Request Header Fields Too Large", "Chunk trailers are too large.");
                }
            }
            if (ms.Length + chunkSize > MaxUploadBytes) throw new HttpProtocolException("413 Payload Too Large", "Upload exceeds 128 MB.");

            int read = 0;
            while (read < chunkSize)
            {
                int n = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, chunkSize - read)), ct).ConfigureAwait(false);
                if (n <= 0) throw new HttpProtocolException("400 Bad Request", "Incomplete chunk body.");
                await ms.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
            }
            if (await ReadAsciiLineAsync(stream, ct).ConfigureAwait(false) != string.Empty)
                throw new HttpProtocolException("400 Bad Request", "Invalid chunk terminator.");
        }
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken ct)
    {
        var buf = new List<byte>(128);
        var single = new byte[1];
        while (buf.Count < 8192)
        {
            int n = await stream.ReadAsync(single.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0) return buf.Count == 0 ? null : Encoding.ASCII.GetString(buf.ToArray());
            if (single[0] == '\n') break;
            if (single[0] != '\r') buf.Add(single[0]);
        }
        if (buf.Count >= 8192) throw new HttpProtocolException("400 Bad Request", "HTTP line is too long.");
        return Encoding.ASCII.GetString(buf.ToArray());
    }

    private unsafe List<string> SaveUploadedMultipartFromFile(string contentType, string spoolPath, CancellationToken ct)
    {
        var saved = new List<string>();
        long length = new FileInfo(spoolPath).Length;
        if (length == 0 || length > MaxUploadBytes || !contentType.StartsWith("multipart/form-data;", StringComparison.OrdinalIgnoreCase)) return saved;

        string? boundary = null;
        foreach (string part in contentType.Split(';'))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                boundary = trimmed["boundary=".Length..].Trim('"');
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 200 || boundary.Any(c => c < 32 || c > 126)) return saved;

        Directory.CreateDirectory(_inboxDir);
        using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
            spoolPath, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, length, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            var body = new ReadOnlySpan<byte>(basePtr + view.PointerOffset, (int)length);
            var parts = ParseMultipartParts(body, boundary);
            foreach (var (fileName, offset, count) in parts)
            {
                ct.ThrowIfCancellationRequested();
                var content = new ReadOnlySpan<byte>(basePtr + view.PointerOffset + offset, count);
                saved.Add(WritePartToInbox(fileName, content));
            }
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        return saved;
    }

    private string WritePartToInbox(string fileName, ReadOnlySpan<byte> content)
    {
        string safeName = SanitizeFileName(Path.GetFileName(fileName));
        string destPath;
        FileStream output;
        while (true)
        {
            destPath = GetUniquePath(_inboxDir, safeName);
            try
            {
                output = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, false);
                break;
            }
            catch (IOException) when (File.Exists(destPath)) { }
        }
        try
        {
            using (output) { output.Write(content); }
        }
        catch
        {
            try { File.Delete(destPath); } catch { }
            throw;
        }
        ApplyMarkOfTheWeb(destPath);
        return destPath;
    }

    /// <summary>
    /// Tags a received file with a Zone.Identifier (Internet zone) alternate data stream so that
    /// SmartScreen, Office Protected View and "unblock" prompts treat it like any other download.
    /// </summary>
    internal static void ApplyMarkOfTheWeb(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Non-NTFS volumes (FAT32/exFAT) have no alternate data streams.
        }
    }

    private static List<(string FileName, long Offset, int Count)> ParseMultipartParts(ReadOnlySpan<byte> body, string boundary)
    {
        var results = new List<(string, long, int)>();
        byte[] boundaryBytes = Encoding.ASCII.GetBytes("--" + boundary);
        byte[] delimiterBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        byte[] headerEndMarker = "\r\n\r\n"u8.ToArray();

        ReadOnlySpan<byte> span = body;
        if (!span.StartsWith(boundaryBytes)) throw new HttpProtocolException("400 Bad Request", "Invalid multipart boundary.");
        int searchPos = 0;
        while (searchPos < span.Length)
        {
            int start = span[searchPos..].IndexOf(boundaryBytes);
            if (start < 0) break;
            start += searchPos + boundaryBytes.Length;
            if (start + 2 <= span.Length && span[start] == '-' && span[start + 1] == '-') return results;
            if (start + 2 > span.Length || span[start] != '\r' || span[start + 1] != '\n')
                throw new HttpProtocolException("400 Bad Request", "Invalid multipart delimiter.");
            start += 2;

            int hdrEndRel = span[start..].IndexOf(headerEndMarker);
            if (hdrEndRel < 0 || hdrEndRel > 32768) throw new HttpProtocolException("400 Bad Request", "Invalid multipart headers.");
            string partHeaders = Encoding.UTF8.GetString(span.Slice(start, hdrEndRel));
            int dataStart = start + hdrEndRel + 4;

            int delimiterPos = dataStart;
            while (true)
            {
                int relative = span[delimiterPos..].IndexOf(delimiterBytes);
                if (relative < 0) throw new HttpProtocolException("400 Bad Request", "Incomplete multipart body.");
                delimiterPos += relative;
                int after = delimiterPos + delimiterBytes.Length;
                if (after + 2 <= span.Length &&
                    ((span[after] == '-' && span[after + 1] == '-') || (span[after] == '\r' && span[after + 1] == '\n'))) break;
                delimiterPos += delimiterBytes.Length;
            }
            int dataLen = delimiterPos - dataStart;

            string? fileName = ExtractFilenameField(partHeaders);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                results.Add((fileName, dataStart, dataLen));
            }

            searchPos = delimiterPos + 2;
        }
        throw new HttpProtocolException("400 Bad Request", "Missing final multipart boundary.");
    }

    private static string? ExtractFilenameField(string headers)
    {
        int quotedIdx = headers.IndexOf("filename=\"", StringComparison.OrdinalIgnoreCase);
        if (quotedIdx >= 0)
        {
            int valStart = quotedIdx + "filename=\"".Length;
            int valEnd = headers.IndexOf('"', valStart);
            return valEnd > valStart ? headers[valStart..valEnd] : null;
        }

        int rawIdx = headers.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
        if (rawIdx >= 0)
        {
            int valStart = rawIdx + "filename=".Length;
            int endIdx = headers.IndexOfAny([';', '\r', '\n'], valStart);
            string raw = (endIdx >= 0 ? headers[valStart..endIdx] : headers[valStart..]).Trim().Trim('"');
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        string clean = sb.ToString().Trim('.', ' ');
        if (clean.Length > 180) clean = clean[..180];
        string stem = clean.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsAsciiDigit(stem[3])))
            clean = "_" + clean;
        return string.IsNullOrWhiteSpace(clean) ? $"drop_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.bin" : clean;
    }

    private static string GetUniquePath(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Guid.NewGuid():N}{ext}");
    }

    private static string BuildLandingHtml(string baseUrl, List<string> files, string? sharedText, int uploadedCount)
    {
        var sb = new StringBuilder();
        sb.Append("""
        <!doctype html>
        <html lang="tr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>DinamikAda LAN Drop</title>
        <style>
          body{margin:0;background:#0a0b0e;color:#f5f5f7;font-family:-apple-system,Segoe UI,Roboto,sans-serif;padding:20px}
          .card{max-width:480px;margin:0 auto;background:#16181f;border:1px solid #2b2f3c;border-radius:20px;padding:20px;box-shadow:0 14px 36px rgba(0,0,0,.5)}
          h1{margin:0 0 6px;font-size:18px;font-weight:700}
          .sub{color:#8e95a5;font-size:12.5px;margin-bottom:16px}
          .item{display:flex;justify-content:space-between;align-items:center;padding:11px 13px;background:#1e222c;border-radius:12px;margin-bottom:8px;gap:10px}
          .name{font-size:13.5px;word-break:break-all}
          a.btn,button.btn{background:#0a84ff;color:#fff;border:none;border-radius:10px;padding:8px 13px;font-size:12.5px;font-weight:600;text-decoration:none;cursor:pointer;white-space:nowrap}
          pre{background:#111319;border:1px solid #282c38;border-radius:12px;padding:12px;white-space:pre-wrap;word-break:break-word;font-size:13px;margin:8px 0 12px}
          .upbox{margin-top:16px;padding-top:14px;border-top:1px solid #262a35}
          input[type=file]{width:100%;margin-bottom:10px;color:#b8becb}
          .ok{background:#123421;border:1px solid #30d158;color:#7df29b;border-radius:10px;padding:9px 12px;margin-bottom:12px;font-size:13px}
        </style></head><body><div class="card">
        <h1>DinamikAda LAN Drop</h1>
        <div class="sub">Yerel ağ üzerinden hızlı &amp; güvenli dosya ve metin aktarımı</div>
        """);

        if (uploadedCount > 0)
        {
            sb.Append($"<div class=\"ok\">✓ {uploadedCount} dosya bilgisayar rafına gönderildi.</div>");
        }

        if (files.Count > 0)
        {
            for (int i = 0; i < files.Count; i++)
            {
                string f = files[i];
                string fname = HttpUtility.HtmlEncode(Path.GetFileName(f));
                long len = 0;
                try { len = new FileInfo(f).Length; } catch { }
                string sizeStr = len >= 1048576 ? $"{len / 1048576.0:F1} MB" : $"{Math.Max(1, len / 1024.0):F0} KB";
                sb.Append($"<div class=\"item\"><span class=\"name\">{fname} <span class=\"sub\">({sizeStr})</span></span>");
                sb.Append($"<a class=\"btn\" href=\"{baseUrl}/dl/{i}\">İndir</a></div>");
            }
            if (files.Count > 1)
            {
                sb.Append($"<div style=\"margin:8px 0 14px\"><a class=\"btn\" href=\"{baseUrl}/bundle.zip\">Tümünü ZIP Olarak İndir ({files.Count})</a></div>");
            }
        }

        if (!string.IsNullOrWhiteSpace(sharedText))
        {
            string encoded = HttpUtility.HtmlEncode(sharedText);
            sb.Append($"<pre id=\"txt\">{encoded}</pre>");
            sb.Append("<button type=\"button\" class=\"btn\" onclick=\"copyText(this)\">Metni Kopyala</button>");
            sb.Append("<script>async function copyText(b){const t=document.getElementById('txt').textContent;try{if(navigator.clipboard&&window.isSecureContext){await navigator.clipboard.writeText(t)}else{const a=document.createElement('textarea');a.value=t;document.body.appendChild(a);a.select();const ok=document.execCommand('copy');a.remove();if(!ok)throw Error()}b.textContent='Kopyalandı'}catch{b.textContent='Metni seçip kopyalayın'}}</script>");
        }

        sb.Append($$"""
        <div class="upbox">
          <div class="sub" style="margin-bottom:8px">Telefondan bilgisayara dosya gönder (DinamikAda Rafına eklenir):</div>
          <form method="post" action="{{baseUrl}}/upload" enctype="multipart/form-data">
            <input type="file" name="files" multiple>
            <button type="submit" class="btn">Bilgisayara Gönder</button>
          </form>
        </div></div></body></html>
        """);
        return sb.ToString();
    }

    private static async Task SendFileAsync(Stream stream, string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await SendDownloadAsync(stream, fs, Path.GetFileName(filePath), "application/octet-stream", ct).ConfigureAwait(false);
    }

    private static async Task SendDownloadAsync(Stream stream, Stream source, string fileName, string contentType, CancellationToken ct)
    {
        string safeName = Uri.EscapeDataString(fileName);
        string header =
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {source.Length}\r\n" +
            $"Content-Disposition: attachment; filename*=UTF-8''{safeName}\r\n" +
            $"Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);

        source.Position = 0;
        await source.CopyToAsync(stream, 65536, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task SendZipBundleAsync(Stream stream, List<string> files, CancellationToken ct)
    {
        string temporaryZip = Path.Combine(Path.GetTempPath(), $"DinamikAda-drop-{Guid.NewGuid():N}.zip");
        await using var zipFile = new FileStream(temporaryZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(f)) continue;
                string entryName = Path.GetFileName(f);
                int idx = 1;
                string candidate = entryName;
                while (!usedNames.Add(candidate))
                {
                    candidate = $"{Path.GetFileNameWithoutExtension(entryName)}_{idx++}{Path.GetExtension(entryName)}";
                }
                var entry = zip.CreateEntry(candidate, CompressionLevel.Fastest);
                await using var input = File.OpenRead(f);
                await using var output = entry.Open();
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }
        await SendDownloadAsync(stream, zipFile, "DinamikAda_Drop.zip", "application/zip", ct).ConfigureAwait(false);
    }

    private static Task SendStatusAsync(Stream stream, string status, string message, CancellationToken ct) =>
        SendResponseAsync(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message), null, ct);

    private static async Task SendResponseAsync(
        Stream stream,
        string status,
        string contentType,
        byte[] body,
        string? contentDisposition,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        if (!string.IsNullOrEmpty(contentDisposition))
        {
            sb.Append($"Content-Disposition: {contentDisposition}\r\n");
        }
        sb.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            _disposed = true;
            StopShare();
        }
    }
}
