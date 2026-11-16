using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Platform.Win32.Identity;
using DinamikAda.Platform.Win32.Interop;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DinamikAda.Modules.Notifications;

public sealed class NotificationHubService : INotificationBackend
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private readonly ITransientHudBus _hudBus;
    private readonly Func<bool>? _isMirroringEnabled;
    private readonly SemaphoreSlim _osSyncGate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<NotificationEntry> _internalAlerts = new();
    private readonly List<NotificationEntry> _osNotifications = new();
    private readonly HashSet<uint> _seenOsIds = new();
    private readonly ConcurrentDictionary<string, byte[]?> _logoCache = new(StringComparer.OrdinalIgnoreCase);

    private UserNotificationListener? _listener;
    private Timer? _pollTimer;
    private NativeMethods.WinEventDelegate? _toastHookProc;
    private SynchronizationContext? _hookSyncContext;
    private nint _toastHookHandle;
    private long _lastToastPulseMs;
    private uint _nextInternalId = 900_000;
    private bool _baselineSynced;
    private volatile bool _disposed;

    public event EventHandler<IReadOnlyList<NotificationEntry>>? NotificationsChanged;
    public event EventHandler<NotificationEntry>? NotificationArrived;

    public bool SupportsNativeMirror { get; private set; }
    public bool MirrorOsNotificationsEnabled { get; set; } = true;
    public string Language { get; set; } = "tr";

    public IReadOnlyList<NotificationEntry> Current
    {
        get
        {
            lock (_sync)
            {
                return MergeLocked();
            }
        }
    }

    public NotificationHubService(ITransientHudBus hudBus, Func<bool>? isMirroringEnabled = null, string language = "tr")
    {
        _hudBus = hudBus;
        _isMirroringEnabled = isMirroringEnabled;
        Language = string.IsNullOrWhiteSpace(language) ? "tr" : language;
    }

    private bool IsMirroringActive() => MirrorOsNotificationsEnabled && (_isMirroringEnabled?.Invoke() ?? true);

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_disposed) return;
        if (PackageIdentityDetector.Detect().HasIdentity)
        {
            try
            {
                _listener = UserNotificationListener.Current;
                var access = await _listener.RequestAccessAsync().AsTask(ct);
                if (access == UserNotificationListenerAccessStatus.Allowed && !_disposed)
                {
                    SupportsNativeMirror = true;
                    _listener.NotificationChanged += OnOsNotificationChanged;
                    await SyncOsNotificationsAsync();
                    if (!_disposed)
                    {
                        _pollTimer = new Timer(_ => _ = SyncOsNotificationsAsync(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8));
                    }
                    return;
                }
            }
            catch
            {
                SupportsNativeMirror = false;
            }
        }
    }

    /// <summary>
    /// Must be invoked on the UI STA thread that runs a Win32 message loop so SetWinEventHook receives events.
    /// </summary>
    public void StartToastHook()
    {
        if (_disposed || SupportsNativeMirror) return;
        InstallToastPulseHook();
    }

    public Task RefreshOsMirrorAsync() => SyncOsNotificationsAsync();

    public void AddInternalAlert(string sourceApp, string title, string body)
    {
        NotificationEntry entry;
        lock (_sync)
        {
            entry = new NotificationEntry(
                Id: ++_nextInternalId,
                SourceApp: string.IsNullOrWhiteSpace(sourceApp) ? "DinamikAda" : sourceApp,
                Title: title,
                Body: body,
                ReceivedUtc: DateTimeOffset.UtcNow,
                CanDismissFromOs: false);

            _internalAlerts.Insert(0, entry);
            if (_internalAlerts.Count > 24)
            {
                _internalAlerts.RemoveRange(24, _internalAlerts.Count - 24);
            }
        }

        NotificationArrived?.Invoke(this, entry);
        NotificationsChanged?.Invoke(this, Current);
    }

    public Task DismissAsync(uint id)
    {
        bool removedInternal;
        lock (_sync)
        {
            removedInternal = _internalAlerts.RemoveAll(n => n.Id == id) > 0;
            _osNotifications.RemoveAll(n => n.Id == id);
        }

        if (!removedInternal && SupportsNativeMirror && _listener is not null)
        {
            try
            {
                _listener.RemoveNotification(id);
            }
            catch
            {
                // OS notification may already have been dismissed.
            }
        }

        NotificationsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task ClearAllAsync()
    {
        lock (_sync)
        {
            _internalAlerts.Clear();
            _osNotifications.Clear();
        }

        if (SupportsNativeMirror && _listener is not null)
        {
            try
            {
                _listener.ClearNotifications();
            }
            catch
            {
                // Ignore transient COM error during OS clear.
            }
        }

        NotificationsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    private void OnOsNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        _ = SyncOsNotificationsAsync();
    }

    private async Task SyncOsNotificationsAsync()
    {
        if (_disposed || _listener is null || !SupportsNativeMirror) return;

        bool acquired = false;
        try
        {
            await _osSyncGate.WaitAsync().ConfigureAwait(false);
            acquired = true;
            if (_disposed || _listener is null) return;

            if (!IsMirroringActive())
            {
                bool hadOs;
                lock (_sync)
                {
                    hadOs = _osNotifications.Count > 0;
                    _osNotifications.Clear();
                    _seenOsIds.Clear();
                    _baselineSynced = false;
                }
                if (hadOs)
                {
                    NotificationsChanged?.Invoke(this, Current);
                }
                return;
            }

            var items = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (_disposed) return;

            var parsed = new List<NotificationEntry>(items.Count);
            var newlyArrived = new List<NotificationEntry>();
            var liveIds = new HashSet<uint>(items.Count);

            foreach (var item in items)
            {
                string appName = "Windows";
                string? aumid = null;
                try
                {
                    appName = item.AppInfo?.DisplayInfo?.DisplayName ?? "Windows";
                    aumid = item.AppInfo?.AppUserModelId;
                }
                catch
                {
                    // Some system notifications have null AppInfo in WinRT.
                }

                string title = string.Empty;
                string body = string.Empty;
                try
                {
                    var binding = item.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    if (binding is not null)
                    {
                        var textElements = binding.GetTextElements();
                        if (textElements.Count > 0) title = textElements[0].Text ?? string.Empty;
                        if (textElements.Count > 1) body = string.Join("\n", textElements.Skip(1).Select(t => t.Text).Where(s => !string.IsNullOrWhiteSpace(s)));
                    }
                }
                catch
                {
                    // Guard malformed notification payloads.
                }

                if (string.IsNullOrWhiteSpace(title)) title = appName;

                byte[]? iconBytes = await TryGetCachedAppLogoBytesAsync(item, aumid, appName).ConfigureAwait(false);
                var entry = new NotificationEntry(
                    item.Id,
                    appName,
                    title,
                    body,
                    item.CreationTime,
                    CanDismissFromOs: true,
                    AppIconBytes: iconBytes,
                    AppUserModelId: aumid);

                parsed.Add(entry);
                liveIds.Add(item.Id);
            }

            bool emitArrivals;
            lock (_sync)
            {
                emitArrivals = _baselineSynced;
                _seenOsIds.IntersectWith(liveIds);

                foreach (var entry in parsed)
                {
                    if (_seenOsIds.Add(entry.Id) && emitArrivals)
                    {
                        newlyArrived.Add(entry);
                    }
                }

                _baselineSynced = true;
                _osNotifications.Clear();
                _osNotifications.AddRange(parsed.OrderByDescending(n => n.ReceivedUtc).Take(30));
            }

            foreach (var arrival in newlyArrived)
            {
                NotificationArrived?.Invoke(this, arrival);
                _hudBus.Publish(new TransientHudEvent(
                    CoalesceKey: $"notif:{arrival.Id}",
                    Kind: HudKind.NotificationPulse,
                    Title: arrival.SourceApp,
                    Subtitle: arrival.Title,
                    Value01: null,
                    AccentHex: "#0A84FF",
                    DurationMs: 2600,
                    InteractiveOnHover: true,
                    IsSticky: false,
                    IconGlyph: "\uEA8F"));
            }

            NotificationsChanged?.Invoke(this, Current);
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            // Listener access may revoke if Sparse MSIX permission toggled off.
        }
        finally
        {
            if (acquired)
            {
                try { _osSyncGate.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private void InstallToastPulseHook()
    {
        if (_disposed || _toastHookHandle != 0) return;
        _hookSyncContext = SynchronizationContext.Current;
        _toastHookProc = OnWinEventToastShow;
        _toastHookHandle = NativeMethods.SetWinEventHook(
            EVENT_OBJECT_SHOW,
            EVENT_OBJECT_SHOW,
            0,
            _toastHookProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    private unsafe void OnWinEventToastShow(nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_disposed || SupportsNativeMirror || hwnd == 0 || idObject != 0 || !IsMirroringActive()) return;

        long now = Environment.TickCount64;
        if (now - _lastToastPulseMs < 1200) return;

        char* clsBuf = stackalloc char[128];
        int clsLen = NativeMethods.GetClassNameW(hwnd, clsBuf, 128);
        if (clsLen <= 0) return;
        string className = new string(clsBuf, 0, clsLen);

        if (!string.Equals(className, "Windows.UI.Core.CoreWindow", StringComparison.Ordinal)) return;

        if (!NativeMethods.IsWindowVisible(hwnd)) return;
        uint style = unchecked((uint)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE).ToInt64());
        if ((style & NativeMethods.WS_VISIBLE) == 0 || (style & NativeMethods.WS_MINIMIZE) != 0) return;

        // Locale-independent ShellExperienceHost toast banner geometry check (compact banner, not Action Center flyout)
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        if (rect.Width is < 160 or > 720 || rect.Height is < 48 or > 420) return;

        nint hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon != 0)
        {
            var mi = new NativeMethods.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEXW>() };
            if (NativeMethods.GetMonitorInfoW(hMon, ref mi))
            {
                int monHeight = mi.rcWork.Height > 0 ? mi.rcWork.Height : mi.rcMonitor.Height;
                if (monHeight > 0 && rect.Height > monHeight * 0.45) return;
            }
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (!IsShellToastProcess(pid)) return;

        _lastToastPulseMs = now;
        _hudBus.Publish(new TransientHudEvent(
            CoalesceKey: "notif:toast-pulse",
            Kind: HudKind.NotificationPulse,
            Title: Strings.Pick(Language, "Windows Bildirimi", "Windows Notification"),
            Subtitle: Strings.Pick(Language, "Tam bildirim içeriği için Sparse MSIX kaydedin", "Register Sparse MSIX for full notification content"),
            Value01: null,
            AccentHex: "#0A84FF",
            DurationMs: 2200,
            InteractiveOnHover: true,
            IsSticky: false,
            IconGlyph: "\uEA8F"));
    }

    private static bool IsShellToastProcess(uint pid)
    {
        if (pid == 0) return false;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(proc.ProcessName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<byte[]?> TryGetCachedAppLogoBytesAsync(UserNotification item, string? aumid, string appName)
    {
        string cacheKey = !string.IsNullOrWhiteSpace(aumid) ? aumid : appName;
        if (string.IsNullOrWhiteSpace(cacheKey)) return null;
        if (_logoCache.TryGetValue(cacheKey, out byte[]? cached)) return cached;

        byte[]? bytes = null;
        try
        {
            var logoRef = item.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(48, 48));
            if (logoRef is not null)
            {
                using IRandomAccessStreamWithContentType stream = await logoRef.OpenReadAsync();
                uint size = (uint)Math.Min(stream.Size, 128 * 1024);
                if (size > 0)
                {
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    uint loaded = await reader.LoadAsync(size);
                    if (loaded > 0)
                    {
                        bytes = new byte[loaded];
                        reader.ReadBytes(bytes);
                    }
                }
            }
        }
        catch
        {
            bytes = null;
        }

        if (_logoCache.Count >= 64)
        {
            _logoCache.Clear();
        }
        _logoCache[cacheKey] = bytes;
        return bytes;
    }

    private List<NotificationEntry> MergeLocked()
    {
        var merged = new List<NotificationEntry>(_osNotifications.Count + _internalAlerts.Count);
        merged.AddRange(_osNotifications);
        merged.AddRange(_internalAlerts);
        merged.Sort((a, b) => b.ReceivedUtc.CompareTo(a.ReceivedUtc));
        return merged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;

        if (_listener is not null)
        {
            try { _listener.NotificationChanged -= OnOsNotificationChanged; } catch { }
            _listener = null;
        }

        nint hook = Interlocked.Exchange(ref _toastHookHandle, 0);
        if (hook != 0)
        {
            var syncCtx = _hookSyncContext;
            if (syncCtx is not null && SynchronizationContext.Current != syncCtx)
            {
                try
                {
                    syncCtx.Post(_ =>
                    {
                        try { NativeMethods.UnhookWinEvent(hook); } catch { }
                        _toastHookProc = null;
                    }, null);
                }
                catch
                {
                    try { NativeMethods.UnhookWinEvent(hook); } catch { }
                    _toastHookProc = null;
                }
            }
            else
            {
                try { NativeMethods.UnhookWinEvent(hook); } catch { }
                _toastHookProc = null;
            }
        }
        else
        {
            _toastHookProc = null;
        }

        if (_osSyncGate.Wait(250))
        {
            try { _osSyncGate.Release(); } catch { }
        }
        _osSyncGate.Dispose();
    }
}
