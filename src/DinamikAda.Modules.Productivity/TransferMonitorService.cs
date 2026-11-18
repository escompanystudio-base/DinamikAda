using System.Globalization;
using DinamikAda.Core.Abstractions;

namespace DinamikAda.Modules.Productivity;

public sealed record CompletedTransferEventArgs(string FileName, string FinalPath, long TotalBytes);

/// <summary>
/// Event-driven Downloads/Transfer progress watcher for DinamikAda.
/// Idle CPU = 0.000% (timer remains Stopped whenever no active .crdownload/.part/.opdownload/.tmp transfer exists).
/// </summary>
public sealed class TransferMonitorService : ILiveActivitySource, IDisposable
{
    private static readonly string[] PartialExtensions =
        [".crdownload", ".part", ".opdownload", ".download", ".tmp", ".aria2"];

    private sealed class ActiveFileTrack
    {
        public string Path { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long LastBytes { get; set; }
        public long PeakBytes { get; set; }
        public long LastSampleTickMs { get; set; }
        public double SmoothedBytesPerSec { get; set; }
        public long? ExpectedTotalBytes { get; set; }
        public long LastSeenTickMs { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, ActiveFileTrack> _active = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _rateTimer;
    private bool _rateTimerRunning;
    private string _watchFolder = string.Empty;
    private bool _enabled;
    private bool _disposed;
    private LiveActivityState? _currentActivity;

    public string SourceId => "transfer:downloads";
    public string Language { get; set; } = "tr";
    public LiveActivityState? CurrentActivity { get { lock (_sync) return _currentActivity; } }

    public event EventHandler<LiveActivityState?>? ActivityChanged;
    public event EventHandler<CompletedTransferEventArgs>? TransferCompleted;

    public static bool IsPartialTransferFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string ext = Path.GetExtension(filePath);
        if (!PartialExtensions.Any(p => ext.Equals(p, StringComparison.OrdinalIgnoreCase))) return false;
        if (ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) && IsApplicationScratchTempName(Path.GetFileNameWithoutExtension(filePath)))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Office and other editors write scratch files into the folder being saved to ("~WRL0001.tmp",
    /// "~$Report.tmp", Excel's "A1B2C3D4.tmp"). Saving a document into Downloads must not look like a
    /// finished download.
    /// </summary>
    private static bool IsApplicationScratchTempName(string stem)
    {
        if (stem.StartsWith('~')) return true;
        return stem.Length == 8 && stem.All(Uri.IsHexDigit);
    }

    public static string InferCompletedPath(string partialPath)
    {
        string ext = Path.GetExtension(partialPath);
        return PartialExtensions.Any(p => ext.Equals(p, StringComparison.OrdinalIgnoreCase))
            ? partialPath[..^ext.Length]
            : partialPath;
    }

    public static string FormatTransferRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1048576.0) return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec / 1048576.0:F1} MB/s");
        if (bytesPerSec >= 1024.0) return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec / 1024.0:F0} KB/s");
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytesPerSec):F0} B/s");
    }

    public void Configure(bool enabled, string? customFolder = null)
    {
        string targetFolder = !string.IsNullOrWhiteSpace(customFolder) && Directory.Exists(customFolder)
            ? customFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        targetFolder = Path.GetFullPath(targetFolder);

        lock (_sync)
        {
            if (_disposed) return;
            _enabled = enabled;
            if (!enabled || !Directory.Exists(targetFolder))
            {
                StopWatcherLocked();
                _active.Clear();
                PublishActivityLocked(null);
                return;
            }

            if (string.Equals(_watchFolder, targetFolder, StringComparison.OrdinalIgnoreCase) && _watcher is not null)
            {
                return;
            }

            StopWatcherLocked();
            _active.Clear();
            PublishActivityLocked(null);
            _watchFolder = targetFolder;
            _rateTimer ??= new Timer(_ => PollActiveTransfers(), null, Timeout.Infinite, Timeout.Infinite);

            try
            {
                _watcher = new FileSystemWatcher(targetFolder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                _watcher.Created += OnFsCreatedOrChanged;
                _watcher.Changed += OnFsCreatedOrChanged;
                _watcher.Renamed += OnFsRenamed;
                _watcher.Deleted += OnFsDeleted;
                _watcher.EnableRaisingEvents = true;
                foreach (string file in Directory.EnumerateFiles(targetFolder))
                {
                    if (IsPartialTransferFile(file)) TrackPartialPath(file);
                }
            }
            catch
            {
                StopWatcherLocked();
            }
        }
    }

    private void OnFsCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _watcher) || !IsPartialTransferFile(e.FullPath)) return;
        TrackPartialPath(e.FullPath);
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _watcher)) return;

        bool wasPartial = IsPartialTransferFile(e.OldFullPath);
        bool isNowPartial = IsPartialTransferFile(e.FullPath);

        if (wasPartial && !isNowPartial && File.Exists(e.FullPath))
        {
            long finalSize = 0;
            try { finalSize = new FileInfo(e.FullPath).Length; } catch { }

            lock (_sync)
            {
                _active.Remove(e.OldFullPath);
                UpdateTimerAndPublishLocked();
            }
            if (finalSize > 0)
            {
                TransferCompleted?.Invoke(this, new CompletedTransferEventArgs(Path.GetFileName(e.FullPath), e.FullPath, finalSize));
            }
            return;
        }

        if (isNowPartial)
        {
            lock (_sync)
            {
                _active.Remove(e.OldFullPath);
            }
            TrackPartialPath(e.FullPath);
        }
    }

    private void OnFsDeleted(object sender, FileSystemEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _watcher) || !IsPartialTransferFile(e.FullPath)) return;
        string possibleFinal = InferCompletedPath(e.FullPath);
        CompletedTransferEventArgs? completed = null;

        lock (_sync)
        {
            if (_active.Remove(e.FullPath) && !string.Equals(possibleFinal, e.FullPath, StringComparison.OrdinalIgnoreCase) && File.Exists(possibleFinal))
            {
                try
                {
                    var fi = new FileInfo(possibleFinal);
                    if (fi.Length > 0 && (DateTime.UtcNow - fi.LastWriteTimeUtc) <= TimeSpan.FromSeconds(15))
                    {
                        completed = new CompletedTransferEventArgs(fi.Name, possibleFinal, fi.Length);
                    }
                }
                catch { }
            }
            UpdateTimerAndPublishLocked();
        }

        if (completed is not null)
        {
            TransferCompleted?.Invoke(this, completed);
        }
    }

    private void TrackPartialPath(string fullPath)
    {
        long nowMs = Environment.TickCount64;
        long bytes = 0;
        try { bytes = new FileInfo(fullPath).Length; } catch { }

        lock (_sync)
        {
            if (!_enabled || _disposed || !string.Equals(Path.GetDirectoryName(fullPath), _watchFolder, StringComparison.OrdinalIgnoreCase)) return;
            if (!_active.TryGetValue(fullPath, out var track))
            {
                string inferred = InferCompletedPath(fullPath);
                track = new ActiveFileTrack
                {
                    Path = fullPath,
                    DisplayName = Path.GetFileName(inferred),
                    LastBytes = bytes,
                    PeakBytes = bytes,
                    LastSampleTickMs = nowMs,
                    LastSeenTickMs = nowMs
                };
                _active[fullPath] = track;
            }
            else
            {
                track.LastSeenTickMs = nowMs;
            }

            UpdateTimerAndPublishLocked();
        }
    }

    private void PollActiveTransfers()
    {
        if (_disposed) return;
        long nowMs = Environment.TickCount64;
        var completedEvents = new List<CompletedTransferEventArgs>();

        lock (_sync)
        {
            if (_disposed) return;
            if (!_enabled || _active.Count == 0)
            {
                _rateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _rateTimerRunning = false;
                PublishActivityLocked(null);
                return;
            }

            var toRemove = new List<string>();
            foreach (var kv in _active)
            {
                var track = kv.Value;
                if (!File.Exists(track.Path))
                {
                    toRemove.Add(kv.Key);
                    string possibleFinal = InferCompletedPath(track.Path);
                    if (File.Exists(possibleFinal))
                    {
                        try
                        {
                            var fi = new FileInfo(possibleFinal);
                            if (fi.Length > 0)
                            {
                                completedEvents.Add(new CompletedTransferEventArgs(fi.Name, possibleFinal, fi.Length));
                            }
                        }
                        catch { }
                    }
                    continue;
                }

                long len = 0;
                try { len = new FileInfo(track.Path).Length; } catch { }
                double dtSec = Math.Max(0.2, (nowMs - track.LastSampleTickMs) / 1000.0);
                long delta = Math.Max(0, len - track.LastBytes);
                double instRate = delta / dtSec;
                track.SmoothedBytesPerSec = track.SmoothedBytesPerSec <= 0
                    ? instRate
                    : track.SmoothedBytesPerSec * 0.6 + instRate * 0.4;
                track.LastBytes = len;
                track.PeakBytes = Math.Max(track.PeakBytes, len);
                track.LastSampleTickMs = nowMs;
                if (delta > 0) track.LastSeenTickMs = nowMs;
                else if (nowMs - track.LastSeenTickMs > 20000) toRemove.Add(kv.Key);
            }

            foreach (string key in toRemove) _active.Remove(key);
            UpdateTimerAndPublishLocked();
        }

        foreach (var ev in completedEvents)
        {
            TransferCompleted?.Invoke(this, ev);
        }
    }

    private void UpdateTimerAndPublishLocked()
    {
        if (_active.Count == 0)
        {
            _rateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _rateTimerRunning = false;
            PublishActivityLocked(null);
            return;
        }

        if (!_rateTimerRunning && _rateTimer is not null)
        {
            _rateTimer.Change(500, 500);
            _rateTimerRunning = true;
        }
        var lead = _active.Values.OrderByDescending(v => v.SmoothedBytesPerSec).First();
        double rate = _active.Values.Sum(v => v.SmoothedBytesPerSec);
        string rateLabel = FormatTransferRate(rate);
        double? progress = lead.ExpectedTotalBytes is > 0
            ? Math.Clamp((double)lead.LastBytes / lead.ExpectedTotalBytes.Value, 0.02, 0.99)
            // Unknown total size: report no progress instead of a clock-driven fake percentage
            // (the compact pill would otherwise show e.g. "47%" for an unknown download).
            : null;

        PublishActivityLocked(new LiveActivityState(
            SourceId: SourceId,
            Kind: ActivityKind.Transfer,
            Priority: (int)ActivityKind.Transfer,
            PrimaryText: rateLabel,
            SecondaryText: lead.DisplayName,
            AccentHex: "#0A84FF",
            Progress01: progress,
            IconGlyph: "\uE896",
            TargetTab: ExpandedTabId.Shelf));
    }

    private void PublishActivityLocked(LiveActivityState? state)
    {
        if (_currentActivity == state) return;
        _currentActivity = state;
        ActivityChanged?.Invoke(this, state);
    }

    private void StopWatcherLocked()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _rateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _rateTimerRunning = false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            StopWatcherLocked();
            _rateTimer?.Dispose();
            _rateTimer = null;
        }
    }
}
