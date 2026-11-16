using System.Buffers;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DinamikAda.Modules.CommandHub;

/// <summary>
/// Normalized notification payload accepted by <see cref="DeveloperBridgeService"/>.
/// </summary>
public sealed record DevBridgeNotification(
    string Title,
    string? Subtitle,
    string Status,
    double? Progress01,
    bool Sound);

/// <summary>
/// Localhost-only developer CLI/CI notification bridge supporting HTTP/1.1 over loopback TCP
/// (GET /health, POST /notify) and a Windows Named Pipe (DinamikAdaBridge).
/// </summary>
public sealed class DeveloperBridgeService : IDisposable
{
    public const int DefaultPort = 47821;
    public const int MaxPayloadBytes = 65536;
    public const string DefaultPipeName = "DinamikAdaBridge";

    private const int MaxHeaderBytes = 16384;
    private const int TcpIoTimeoutMs = 5000;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly object _sync = new();
    private readonly string _pipeName;

    private CancellationTokenSource? _cts;
    private TcpListener? _tcpListener;
    private Task? _tcpAcceptTask;
    private Task? _pipeAcceptTask;
    private bool _enabled;
    private int _requestedPort = DefaultPort;
    private int _listeningPort;
    private bool _disposed;

    public DeveloperBridgeService() : this(DefaultPipeName)
    {
    }

    public DeveloperBridgeService(string pipeName)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName.Trim();
    }

    public event Action<DevBridgeNotification>? NotificationReceived;

    public string PipeName => _pipeName;

    public bool IsEnabled
    {
        get { lock (_sync) return _enabled; }
    }

    public bool IsListening
    {
        get { lock (_sync) return _tcpListener is not null && !_disposed; }
    }

    public int ListeningPort
    {
        get { lock (_sync) return _listeningPort; }
    }

    /// <summary>
    /// Starts or stops the localhost TCP and named-pipe listeners according to <paramref name="enabled"/> and <paramref name="port"/>.
    /// </summary>
    public void Start(bool enabled, int port = DefaultPort)
    {
        UpdateSettings(enabled, port);
    }

    /// <summary>
    /// Updates listener activation or port binding, restarting listeners only when settings change.
    /// </summary>
    public void UpdateSettings(bool enabled, int port = DefaultPort)
    {
        int normalizedPort = NormalizePort(port);

        CancellationTokenSource? oldCts;
        TcpListener? oldTcp;
        Task? oldTcpTask;
        Task? oldPipeTask;

        lock (_sync)
        {
            if (_disposed) return;

            bool alreadyRunningSame =
                _enabled == enabled &&
                (!enabled || (_requestedPort == normalizedPort && _tcpListener is not null));

            if (alreadyRunningSame)
            {
                return;
            }

            _enabled = enabled;
            _requestedPort = normalizedPort;

            oldCts = _cts;
            oldTcp = _tcpListener;
            oldTcpTask = _tcpAcceptTask;
            oldPipeTask = _pipeAcceptTask;

            _cts = null;
            _tcpListener = null;
            _tcpAcceptTask = null;
            _pipeAcceptTask = null;
            _listeningPort = 0;
        }

        ShutdownBackground(oldCts, oldTcp, oldTcpTask, oldPipeTask);

        if (!enabled)
        {
            return;
        }

        var newCts = new CancellationTokenSource();
        TcpListener? newTcp = null;
        int boundPort = 0;

        try
        {
            newTcp = new TcpListener(IPAddress.Loopback, normalizedPort);
            newTcp.Start(32);
            if (newTcp.LocalEndpoint is IPEndPoint ep)
            {
                boundPort = ep.Port;
            }
        }
        catch (SocketException)
        {
            try { newTcp?.Stop(); } catch { /* best-effort cleanup */ }
            newCts.Dispose();
            return;
        }
        catch (ObjectDisposedException)
        {
            newCts.Dispose();
            return;
        }

        lock (_sync)
        {
            if (_disposed || !_enabled || _requestedPort != normalizedPort)
            {
                try { newTcp.Stop(); } catch { }
                newCts.Dispose();
                return;
            }

            _cts = newCts;
            _tcpListener = newTcp;
            _listeningPort = boundPort;
            _tcpAcceptTask = Task.Run(() => TcpAcceptLoopAsync(newTcp, newCts.Token));
            _pipeAcceptTask = Task.Run(() => PipeAcceptLoopAsync(_pipeName, newCts.Token));
        }
    }

    /// <summary>
    /// Validates and normalizes a developer bridge JSON payload into a <see cref="DevBridgeNotification"/>.
    /// </summary>
    public static bool TryParsePayload(string json, out DevBridgeNotification? msg, out string? error)
    {
        msg = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Payload must not be empty.";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes)
        {
            error = $"Payload exceeds {MaxPayloadBytes} bytes.";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Payload must be a JSON object.";
                return false;
            }

            if (!TryGetPropertyCaseInsensitive(root, "title", out var titleEl) ||
                titleEl.ValueKind != JsonValueKind.String)
            {
                error = "Field 'title' is required and must be a non-empty string.";
                return false;
            }

            string? rawTitle = titleEl.GetString();
            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                error = "Field 'title' must not be empty.";
                return false;
            }

            string title = rawTitle.Trim();

            string? subtitle = null;
            if (TryGetPropertyCaseInsensitive(root, "subtitle", out var subEl) &&
                subEl.ValueKind != JsonValueKind.Null)
            {
                if (subEl.ValueKind != JsonValueKind.String)
                {
                    error = "Field 'subtitle' must be a string.";
                    return false;
                }

                string? rawSub = subEl.GetString();
                if (!string.IsNullOrWhiteSpace(rawSub))
                {
                    subtitle = rawSub.Trim();
                }
            }

            string status = "info";
            if (TryGetPropertyCaseInsensitive(root, "status", out var statusEl) &&
                statusEl.ValueKind != JsonValueKind.Null)
            {
                if (statusEl.ValueKind != JsonValueKind.String)
                {
                    error = "Field 'status' must be a string.";
                    return false;
                }

                string normalized = (statusEl.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                if (normalized.Length > 0)
                {
                    if (normalized is not ("ok" or "error" or "progress" or "info"))
                    {
                        error = "Field 'status' must be one of: ok, error, progress, info.";
                        return false;
                    }

                    status = normalized;
                }
            }

            double? progress01 = null;
            if (TryGetPropertyCaseInsensitive(root, "progress01", out var progressEl) &&
                progressEl.ValueKind != JsonValueKind.Null)
            {
                if (progressEl.ValueKind != JsonValueKind.Number ||
                    !progressEl.TryGetDouble(out double rawProgress) ||
                    !double.IsFinite(rawProgress))
                {
                    error = "Field 'progress01' must be a finite number between 0 and 1.";
                    return false;
                }

                progress01 = Math.Clamp(rawProgress, 0.0, 1.0);
            }

            bool sound = false;
            if (TryGetPropertyCaseInsensitive(root, "sound", out var soundEl) &&
                soundEl.ValueKind != JsonValueKind.Null)
            {
                if (soundEl.ValueKind == JsonValueKind.True)
                {
                    sound = true;
                }
                else if (soundEl.ValueKind == JsonValueKind.False)
                {
                    sound = false;
                }
                else
                {
                    error = "Field 'sound' must be a boolean.";
                    return false;
                }
            }

            msg = new DevBridgeNotification(title, subtitle, status, progress01, sound);
            return true;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? oldCts;
        TcpListener? oldTcp;
        Task? oldTcpTask;
        Task? oldPipeTask;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _listeningPort = 0;

            oldCts = _cts;
            oldTcp = _tcpListener;
            oldTcpTask = _tcpAcceptTask;
            oldPipeTask = _pipeAcceptTask;

            _cts = null;
            _tcpListener = null;
            _tcpAcceptTask = null;
            _pipeAcceptTask = null;
        }

        ShutdownBackground(oldCts, oldTcp, oldTcpTask, oldPipeTask);
    }

    private static int NormalizePort(int port)
    {
        if (port == 0) return 0; // Ephemeral port for automated tests
        return port is >= 1024 and <= 65535 ? port : DefaultPort;
    }

    private static void ShutdownBackground(
        CancellationTokenSource? cts,
        TcpListener? tcp,
        Task? tcpTask,
        Task? pipeTask)
    {
        try { cts?.Cancel(); } catch { }
        try { tcp?.Stop(); } catch { }

        var tasks = new List<Task>(2);
        if (tcpTask is not null) tasks.Add(tcpTask);
        if (pipeTask is not null) tasks.Add(pipeTask);

        if (tasks.Count > 0)
        {
            try
            {
                Task.WhenAll(tasks).Wait(1500);
            }
            catch
            {
                // Ignore cancellation / shutdown faults.
            }
        }

        try { cts?.Dispose(); } catch { }
    }

    private async Task TcpAcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleHttpClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch (SocketException)
            {
                client?.Dispose();
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { break; }
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleHttpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = TcpIoTimeoutMs;
                client.SendTimeout = TcpIoTimeoutMs;

                using var stream = client.GetStream();
                using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                ioCts.CancelAfter(TcpIoTimeoutMs);
                var ioCt = ioCts.Token;

                var headerRead = await ReadHttpHeaderBlockAsync(stream, ioCt).ConfigureAwait(false);
                if (headerRead is null)
                {
                    await WriteJsonResponseAndCloseAsync(client, stream, 400, "Bad Request", new { ok = false, error = "Invalid or incomplete HTTP headers." }, drainUnread: true, ioCt).ConfigureAwait(false);
                    return;
                }

                var (headerBytes, trailingBytes) = headerRead.Value;
                string headerText = Encoding.ASCII.GetString(headerBytes);
                string[] lines = headerText.Split(["\r\n"], StringSplitOptions.None);
                if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
                {
                    await WriteJsonResponseAndCloseAsync(client, stream, 400, "Bad Request", new { ok = false, error = "Missing HTTP request line." }, drainUnread: true, ioCt).ConfigureAwait(false);
                    return;
                }

                string[] reqParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (reqParts.Length < 2)
                {
                    await WriteJsonResponseAndCloseAsync(client, stream, 400, "Bad Request", new { ok = false, error = "Malformed HTTP request line." }, drainUnread: true, ioCt).ConfigureAwait(false);
                    return;
                }

                string method = reqParts[0].ToUpperInvariant();
                string rawTarget = reqParts[1];
                int qIdx = rawTarget.IndexOf('?');
                string path = (qIdx >= 0 ? rawTarget[..qIdx] : rawTarget).TrimEnd('/');
                if (path.Length == 0) path = "/";

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0) break;
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string key = line[..colon].Trim();
                    string val = line[(colon + 1)..].Trim();
                    headers[key] = val;
                }

                if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
                {
                    if (method != "GET")
                    {
                        await WriteJsonResponseAndCloseAsync(client, stream, 405, "Method Not Allowed", new { ok = false, error = "Use GET /health." }, drainUnread: true, ioCt).ConfigureAwait(false);
                        return;
                    }

                    await WriteJsonResponseAndCloseAsync(client, stream, 200, "OK", new { ok = true }, drainUnread: false, ioCt).ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(path, "/notify", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonResponseAndCloseAsync(client, stream, 404, "Not Found", new { ok = false, error = "Endpoint not found." }, drainUnread: true, ioCt).ConfigureAwait(false);
                    return;
                }

                if (method != "POST")
                {
                    await WriteJsonResponseAndCloseAsync(client, stream, 405, "Method Not Allowed", new { ok = false, error = "Use POST /notify." }, drainUnread: true, ioCt).ConfigureAwait(false);
                    return;
                }

                if (!headers.TryGetValue("Content-Length", out string? clStr) ||
                    !long.TryParse(clStr, out long contentLength) ||
                    contentLength < 0)
                {
                    await WriteJsonResponseAndCloseAsync(client, stream, 400, "Bad Request", new { ok = false, error = "Valid Content-Length header is required." }, drainUnread: true, ioCt).ConfigureAwait(false);
                    return;
                }

                if (contentLength > MaxPayloadBytes)
                {
                    await WriteJsonResponseAndCloseAsync(client, stream, 413, "Payload Too Large", new { ok = false, error = $"Content-Length exceeds {MaxPayloadBytes} bytes." }, drainUnread: true, ioCt).ConfigureAwait(false);
                    return;
                }

                if (headers.TryGetValue("Expect", out string? expectVal) &&
                    expectVal.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    await stream.WriteAsync(cont, ioCt).ConfigureAwait(false);
                }

                int expectedLen = (int)contentLength;
                byte[] bodyBytes = new byte[expectedLen];
                int copied = Math.Min(trailingBytes.Length, expectedLen);
                if (copied > 0)
                {
                    Buffer.BlockCopy(trailingBytes, 0, bodyBytes, 0, copied);
                }

                while (copied < expectedLen)
                {
                    int read = await stream.ReadAsync(bodyBytes.AsMemory(copied, expectedLen - copied), ioCt).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        await WriteJsonResponseAndCloseAsync(client, stream, 400, "Bad Request", new { ok = false, error = "Unexpected EOF while reading request body." }, drainUnread: false, ioCt).ConfigureAwait(false);
                        return;
                    }

                    copied += read;
                }

                string json = Encoding.UTF8.GetString(bodyBytes);
                if (!TryParsePayload(json, out var msg, out var parseError) || msg is null)
                {
                    await WriteJsonResponseAndCloseAsync(client, stream, 400, "Bad Request", new { ok = false, error = parseError ?? "Invalid payload." }, drainUnread: false, ioCt).ConfigureAwait(false);
                    return;
                }

                RaiseNotificationReceived(msg);
                await WriteJsonResponseAndCloseAsync(client, stream, 200, "OK", new { ok = true }, drainUnread: false, ioCt).ConfigureAwait(false);
            }
            catch
            {
                // Ignore client socket drops / malformed connections.
            }
        }
    }

    private static async Task<(byte[] HeaderBytes, byte[] TrailingBytes)?> ReadHttpHeaderBlockAsync(
        NetworkStream stream,
        CancellationToken ct)
    {
        byte[] buffer = new byte[MaxHeaderBytes + 4];
        int total = 0;

        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            int searchStart = Math.Max(0, total - 3);
            total += read;

            int endIdx = IndexOfCrlfCrlf(buffer, searchStart, total);
            if (endIdx >= 0)
            {
                byte[] headerBytes = new byte[endIdx];
                Buffer.BlockCopy(buffer, 0, headerBytes, 0, endIdx);

                int bodyStart = endIdx + 4;
                int trailingLen = total - bodyStart;
                byte[] trailingBytes = trailingLen > 0 ? new byte[trailingLen] : [];
                if (trailingLen > 0)
                {
                    Buffer.BlockCopy(buffer, bodyStart, trailingBytes, 0, trailingLen);
                }

                return (headerBytes, trailingBytes);
            }

            if (total > MaxHeaderBytes)
            {
                return null;
            }
        }

        return null;
    }

    private static int IndexOfCrlfCrlf(byte[] buf, int start, int length)
    {
        for (int i = start; i <= length - 4; i++)
        {
            if (buf[i] == (byte)'\r' &&
                buf[i + 1] == (byte)'\n' &&
                buf[i + 2] == (byte)'\r' &&
                buf[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteJsonResponseAndCloseAsync(
        TcpClient client,
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        object bodyObj,
        bool drainUnread,
        CancellationToken ct)
    {
        string bodyJson = JsonSerializer.Serialize(bodyObj, s_jsonOptions);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        string header =
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            return;
        }

        if (!drainUnread) return;

        // Briefly drain unread incoming body bytes after sending an early error response
        // so Windows TCP closes cleanly with FIN instead of resetting the connection mid-flight.
        byte[] sink = ArrayPool<byte>.Shared.Rent(4096);
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(250);
        int drained = 0;
        try
        {
            while (drained < 512 * 1024 && !drainCts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(sink, drainCts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                drained += n;
            }
        }
        catch
        {
            // Ignore timeout or peer close during drain.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sink);
        }
    }

    private async Task PipeAcceptLoopAsync(string pipeName, CancellationToken ct)
    {
        int busyRetries = 0;

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                busyRetries = 0;

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ReadPipeMessagesAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException) when (!ct.IsCancellationRequested && busyRetries < 5)
            {
                busyRetries++;
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { break; }
            }
            catch (IOException)
            {
                // Another instance holds the single pipe slot or pipe closed; stop cleanly.
                break;
            }
            catch (UnauthorizedAccessException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { break; }
            }
            finally
            {
                if (pipe is not null)
                {
                    try { pipe.Dispose(); } catch { }
                }
            }
        }
    }

    private async Task ReadPipeMessagesAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        byte[] chunk = ArrayPool<byte>.Shared.Rent(4096);
        using var messageBuffer = new MemoryStream();
        bool oversized = false;

        try
        {
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                int read = await pipe.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    if (!oversized && messageBuffer.Length > 0)
                    {
                        DispatchBufferedPipePayload(messageBuffer);
                    }
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    byte b = chunk[i];
                    if (b == (byte)'\n')
                    {
                        if (oversized)
                        {
                            messageBuffer.SetLength(0);
                            oversized = false;
                            continue;
                        }

                        if (messageBuffer.Length == 0)
                        {
                            continue;
                        }

                        string candidate = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length).Trim();
                        if (candidate.Length == 0)
                        {
                            messageBuffer.SetLength(0);
                            continue;
                        }

                        if (!candidate.StartsWith('{') || IsJsonObjectComplete(candidate))
                        {
                            if (TryParsePayload(candidate, out var msg, out _) && msg is not null)
                            {
                                RaiseNotificationReceived(msg);
                            }

                            messageBuffer.SetLength(0);
                        }
                        else
                        {
                            if (messageBuffer.Length < MaxPayloadBytes)
                            {
                                messageBuffer.WriteByte(b);
                            }
                            else
                            {
                                oversized = true;
                            }
                        }
                    }
                    else if (!oversized)
                    {
                        if (messageBuffer.Length < MaxPayloadBytes)
                        {
                            messageBuffer.WriteByte(b);
                        }
                        else
                        {
                            oversized = true;
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private void DispatchBufferedPipePayload(MemoryStream buffer)
    {
        string json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).Trim();
        buffer.SetLength(0);
        if (json.Length == 0) return;

        if (TryParsePayload(json, out var msg, out _) && msg is not null)
        {
            RaiseNotificationReceived(msg);
        }
    }

    private static bool IsJsonObjectComplete(string text)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        foreach (char c in text)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return true;
                if (depth < 0) return true;
            }
        }

        return false;
    }

    private void RaiseNotificationReceived(DevBridgeNotification msg)
    {
        try
        {
            NotificationReceived?.Invoke(msg);
        }
        catch
        {
            // Keep bridge listener alive even if an external subscriber throws.
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string targetName, out JsonElement value)
    {
        if (obj.TryGetProperty(targetName, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, targetName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
