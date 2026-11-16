using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using DinamikAda.Core.Abstractions;
using Windows.Media.Control;

namespace DinamikAda.Modules.Media;

public sealed class GsmtcMediaService : IMediaControlBackend
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _sessionsLock = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> _trackedSessions = new(StringComparer.OrdinalIgnoreCase);
    private string? _preferredSessionId;
    private string? _lastTrackSignature;
    private byte[]? _lastThumbHash;
    private long _currentTimelineStartTicks;
    private MediaPlaybackSnapshot _current = EmptySnapshot();
    private volatile bool _disposed;
    private int _refreshRunning;
    private int _refreshDirty;

    public event EventHandler<MediaPlaybackSnapshot>? SnapshotChanged;
    public MediaPlaybackSnapshot Current => _current;

    public static MediaPlaybackSnapshot EmptySnapshot() => new(
        HasSession: false,
        SessionId: string.Empty,
        AppName: string.Empty,
        Title: string.Empty,
        Artist: string.Empty,
        IsPlaying: false,
        CanPlayPause: false,
        CanNext: false,
        CanPrev: false,
        CanSeek: false,
        Position: TimeSpan.Zero,
        Duration: TimeSpan.Zero,
        LastUpdatedUtc: DateTimeOffset.UtcNow,
        PlaybackRate: 1.0,
        AccentHex: "#0A84FF",
        ThumbnailBytes: null,
        AllSessions: ImmutableArray<MediaSessionSummary>.Empty);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
            if (manager is null || _disposed) return;

            _manager = manager;
            manager.SessionsChanged += OnSessionsChanged;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            await RefreshSessionsAsync(ct);
        }
        catch
        {
            // Graceful degradation if Windows Media service is unavailable
        }
    }

    public async Task TogglePlayPauseAsync(CancellationToken ct = default)
    {
        var session = GetSelectedWinRtSession();
        if (session is not null)
        {
            try { await session.TryTogglePlayPauseAsync().AsTask(ct); } catch { }
        }
    }

    public async Task NextAsync(CancellationToken ct = default)
    {
        var session = GetSelectedWinRtSession();
        if (session is not null)
        {
            try { await session.TrySkipNextAsync().AsTask(ct); } catch { }
        }
    }

    public async Task PreviousAsync(CancellationToken ct = default)
    {
        var session = GetSelectedWinRtSession();
        if (session is not null)
        {
            try { await session.TrySkipPreviousAsync().AsTask(ct); } catch { }
        }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        var session = GetSelectedWinRtSession();
        if (session is not null && _current.CanSeek)
        {
            TimeSpan duration = _current.Duration;
            TimeSpan clamped = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : (duration > TimeSpan.Zero && position > duration ? duration : position);
            long startTicks = Volatile.Read(ref _currentTimelineStartTicks);
            long targetTicks = startTicks + clamped.Ticks;
            try { await session.TryChangePlaybackPositionAsync(targetTicks).AsTask(ct); } catch { }
        }
    }

    public async Task SelectSessionAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_sessionsLock)
        {
            _preferredSessionId = sessionId;
        }
        await RefreshSessionsAsync(ct);
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        RequestCoalescedRefresh();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        RequestCoalescedRefresh();
    }

    private void OnSessionPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        RequestCoalescedRefresh();
    }

    private void RequestCoalescedRefresh()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _refreshDirty, 1);
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(RunCoalescedRefreshLoopAsync);
    }

    private async Task RunCoalescedRefreshLoopAsync()
    {
        try
        {
            while (!_disposed && Interlocked.Exchange(ref _refreshDirty, 0) == 1)
            {
                await RefreshSessionsAsync(CancellationToken.None);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);
            if (!_disposed && Volatile.Read(ref _refreshDirty) == 1 &&
                Interlocked.CompareExchange(ref _refreshRunning, 1, 0) == 0)
            {
                _ = Task.Run(RunCoalescedRefreshLoopAsync);
            }
        }
    }

    private GlobalSystemMediaTransportControlsSession? GetSelectedWinRtSession()
    {
        if (_disposed || _manager is null) return null;
        lock (_sessionsLock)
        {
            if (!string.IsNullOrWhiteSpace(_preferredSessionId) && _trackedSessions.TryGetValue(_preferredSessionId, out var pref))
            {
                return pref;
            }
        }
        try
        {
            var current = _manager.GetCurrentSession();
            if (current is not null) return current;
        }
        catch { }
        lock (_sessionsLock)
        {
            return _trackedSessions.Values.FirstOrDefault();
        }
    }

    private async Task RefreshSessionsAsync(CancellationToken ct)
    {
        var manager = _manager;
        if (_disposed || manager is null) return;

        bool acquired = false;
        try
        {
            await _refreshLock.WaitAsync(ct);
            acquired = true;
            if (_disposed) return;

            var rawSessions = manager.GetSessions();
            var liveMap = new Dictionary<string, GlobalSystemMediaTransportControlsSession>(StringComparer.OrdinalIgnoreCase);
            var seenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < rawSessions.Count; i++)
            {
                var s = rawSessions[i];
                string baseId = string.IsNullOrWhiteSpace(s.SourceAppUserModelId)
                    ? $"session:{i}"
                    : s.SourceAppUserModelId.Trim();
                int count = seenCounts.GetValueOrDefault(baseId, 0) + 1;
                seenCounts[baseId] = count;
                string id = count == 1 ? baseId : $"{baseId}#{count}";
                liveMap[id] = s;
            }

            List<KeyValuePair<string, GlobalSystemMediaTransportControlsSession>> snapshotEntries;
            string? preferredId;
            lock (_sessionsLock)
            {
                foreach (var kv in liveMap)
                {
                    if (!_trackedSessions.TryGetValue(kv.Key, out var existing) || !ReferenceEquals(existing, kv.Value))
                    {
                        if (existing is not null)
                        {
                            UnsubscribeSession(existing);
                        }
                        kv.Value.MediaPropertiesChanged += OnSessionPropertiesChanged;
                        kv.Value.PlaybackInfoChanged += OnSessionPropertiesChanged;
                        kv.Value.TimelinePropertiesChanged += OnSessionPropertiesChanged;
                        _trackedSessions[kv.Key] = kv.Value;
                    }
                }

                foreach (string staleId in _trackedSessions.Keys.Where(k => !liveMap.ContainsKey(k)).ToList())
                {
                    if (_trackedSessions.Remove(staleId, out var stale))
                    {
                        UnsubscribeSession(stale);
                    }
                }

                if (!string.IsNullOrWhiteSpace(_preferredSessionId) && !_trackedSessions.ContainsKey(_preferredSessionId))
                {
                    _preferredSessionId = null;
                }

                preferredId = _preferredSessionId;
                snapshotEntries = _trackedSessions.ToList();
            }

            var summaries = ImmutableArray.CreateBuilder<MediaSessionSummary>(snapshotEntries.Count);
            string? chosenId = null;
            GlobalSystemMediaTransportControlsSession? chosen = null;
            string? firstPlayingId = null;
            GlobalSystemMediaTransportControlsSession? firstPlaying = null;
            GlobalSystemMediaTransportControlsSession? currentWinRt = null;
            try { currentWinRt = manager.GetCurrentSession(); } catch { }

            foreach (var kv in snapshotEntries)
            {
                var info = kv.Value.GetPlaybackInfo();
                bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                string title = string.Empty;
                try
                {
                    var props = await kv.Value.TryGetMediaPropertiesAsync().AsTask(ct);
                    title = props?.Title ?? string.Empty;
                }
                catch { }

                summaries.Add(new MediaSessionSummary(kv.Key, FormatAppName(kv.Key), title, playing));

                if (preferredId is not null && string.Equals(kv.Key, preferredId, StringComparison.OrdinalIgnoreCase))
                {
                    chosenId = kv.Key;
                    chosen = kv.Value;
                }
                if (firstPlaying is null && playing)
                {
                    firstPlayingId = kv.Key;
                    firstPlaying = kv.Value;
                }
            }

            if (chosen is null)
            {
                if (firstPlaying is not null)
                {
                    chosen = firstPlaying;
                    chosenId = firstPlayingId;
                }
                else if (currentWinRt is not null)
                {
                    chosen = currentWinRt;
                    chosenId = snapshotEntries.FirstOrDefault(x => ReferenceEquals(x.Value, currentWinRt)).Key
                               ?? currentWinRt.SourceAppUserModelId
                               ?? "current";
                }
                else if (snapshotEntries.Count > 0)
                {
                    chosenId = snapshotEntries[0].Key;
                    chosen = snapshotEntries[0].Value;
                }
            }

            if (chosen is null || chosenId is null)
            {
                _lastTrackSignature = null;
                _lastThumbHash = null;
                Volatile.Write(ref _currentTimelineStartTicks, 0);
                _current = EmptySnapshot();
                if (!_disposed)
                {
                    SnapshotChanged?.Invoke(this, _current);
                }
                return;
            }

            var playback = chosen.GetPlaybackInfo();
            var timeline = chosen.GetTimelineProperties();
            var mediaProps = await chosen.TryGetMediaPropertiesAsync().AsTask(ct);

            string trackTitle = mediaProps?.Title ?? string.Empty;
            string artist = mediaProps?.Artist ?? string.Empty;
            string sig = $"{chosenId}|{trackTitle}|{artist}";

            bool trackChanged = !string.Equals(sig, _lastTrackSignature, StringComparison.Ordinal);
            byte[]? thumbBytes = trackChanged ? null : _current.ThumbnailBytes;
            string accent = trackChanged ? "#0A84FF" : _current.AccentHex;
            string? secondaryAccent = trackChanged ? "#5E5CE6" : _current.SecondaryAccentHex;
            string? highlightAccent = trackChanged ? "#64D2FF" : _current.HighlightAccentHex;

            if (trackChanged || (thumbBytes is null && mediaProps?.Thumbnail is not null))
            {
                _lastTrackSignature = sig;
                byte[]? fetched = await ReadThumbnailBytesAsync(mediaProps?.Thumbnail, ct);
                byte[]? fetchedHash = ComputeThumbnailHash(fetched);

                if (fetched is null)
                {
                    _lastThumbHash = null;
                    thumbBytes = null;
                    accent = "#0A84FF";
                    secondaryAccent = "#5E5CE6";
                    highlightAccent = "#64D2FF";
                }
                else if (fetchedHash is null || _lastThumbHash is null || !fetchedHash.AsSpan().SequenceEqual(_lastThumbHash))
                {
                    _lastThumbHash = fetchedHash;
                    thumbBytes = fetched;
                    (accent, secondaryAccent, highlightAccent) = await ColorQuantizer.ExtractPaletteHexesAsync(thumbBytes, ct);
                }
                else
                {
                    thumbBytes = fetched;
                    if (trackChanged)
                    {
                        (accent, secondaryAccent, highlightAccent) = await ColorQuantizer.ExtractPaletteHexesAsync(thumbBytes, ct);
                    }
                    else
                    {
                        accent = _current.AccentHex;
                        secondaryAccent = _current.SecondaryAccentHex;
                        highlightAccent = _current.HighlightAccentHex;
                    }
                }
            }

            TimeSpan duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            TimeSpan timelineStart = timeline.EndTime > timeline.StartTime
                ? timeline.StartTime
                : TimeSpan.Zero;
            Volatile.Write(ref _currentTimelineStartTicks, timelineStart.Ticks);
            bool canSeek = (playback?.Controls?.IsPlaybackPositionEnabled ?? false) && duration > TimeSpan.Zero;

            var next = new MediaPlaybackSnapshot(
                HasSession: !string.IsNullOrWhiteSpace(trackTitle) || playback is not null,
                SessionId: chosenId,
                AppName: FormatAppName(chosenId),
                Title: string.IsNullOrWhiteSpace(trackTitle) ? FormatAppName(chosenId) : trackTitle,
                Artist: artist,
                IsPlaying: playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanPlayPause: playback?.Controls?.IsPlayPauseToggleEnabled ?? true,
                CanNext: playback?.Controls?.IsNextEnabled ?? true,
                CanPrev: playback?.Controls?.IsPreviousEnabled ?? true,
                CanSeek: canSeek,
                Position: timeline.Position >= timeline.StartTime ? timeline.Position - timeline.StartTime : timeline.Position,
                Duration: duration,
                LastUpdatedUtc: timeline.LastUpdatedTime == default ? DateTimeOffset.UtcNow : timeline.LastUpdatedTime,
                PlaybackRate: playback?.PlaybackRate ?? 1.0,
                AccentHex: accent,
                ThumbnailBytes: thumbBytes,
                AllSessions: summaries.ToImmutable(),
                SecondaryAccentHex: secondaryAccent,
                HighlightAccentHex: highlightAccent);

            _current = next;
            if (!_disposed)
            {
                SnapshotChanged?.Invoke(this, next);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            // Keep prior stable snapshot on transient WinRT COM error
        }
        finally
        {
            if (acquired)
            {
                try { _refreshLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private void UnsubscribeSession(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            session.MediaPropertiesChanged -= OnSessionPropertiesChanged;
            session.PlaybackInfoChanged -= OnSessionPropertiesChanged;
            session.TimelinePropertiesChanged -= OnSessionPropertiesChanged;
        }
        catch { }
    }

    public static byte[]? ComputeThumbnailHash(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;

        // Mix length + 16 evenly sampled stride bytes with full SHA-256 to eliminate header/trailer collisions
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> meta = stackalloc byte[20];
        BitConverter.TryWriteBytes(meta, bytes.Length);
        int step = Math.Max(1, bytes.Length / 16);
        for (int i = 0; i < 16; i++)
        {
            int idx = Math.Min(bytes.Length - 1, i * step);
            meta[4 + i] = bytes[idx];
        }
        sha.AppendData(meta);
        sha.AppendData(bytes);
        return sha.GetHashAndReset();
    }

    private static async Task<byte[]?> ReadThumbnailBytesAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumbRef, CancellationToken ct)
    {
        if (thumbRef is null) return null;
        try
        {
            using var stream = await thumbRef.OpenReadAsync().AsTask(ct);
            using var netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static string FormatAppName(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "Media";
        string cleaned = aumid.Split('!')[0];
        if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            cleaned = Path.GetFileNameWithoutExtension(cleaned);
        int dot = cleaned.LastIndexOf('.');
        if (dot >= 0 && dot < cleaned.Length - 1)
            cleaned = cleaned[(dot + 1)..];
        return string.IsNullOrWhiteSpace(cleaned) ? "Media" : cleaned;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var manager = _manager;
        _manager = null;
        if (manager is not null)
        {
            try
            {
                manager.SessionsChanged -= OnSessionsChanged;
                manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            }
            catch { }
        }

        bool acquired = false;
        try
        {
            acquired = await _refreshLock.WaitAsync(500);
        }
        catch { }

        try
        {
            lock (_sessionsLock)
            {
                foreach (var s in _trackedSessions.Values)
                {
                    UnsubscribeSession(s);
                }
                _trackedSessions.Clear();
            }
        }
        finally
        {
            if (acquired)
            {
                try { _refreshLock.Release(); } catch { }
            }
            _refreshLock.Dispose();
        }
    }
}
