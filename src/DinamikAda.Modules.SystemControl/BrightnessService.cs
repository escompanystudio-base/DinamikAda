using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Modules.SystemControl;

public sealed record BrightnessWriteRequest(string DeviceId, int Percent, bool IsInternal);

public interface IBrightnessHardwareDriver
{
    Task<bool> WriteInternalBrightnessAsync(int percent, CancellationToken ct);
    Task<bool> WriteDdcBrightnessAsync(nint monitorHandle, int percent, CancellationToken ct);
}

public sealed class Win32BrightnessHardwareDriver : IBrightnessHardwareDriver
{
    public Task<bool> WriteInternalBrightnessAsync(int percent, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\WMI"), new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
                foreach (ManagementObject m in searcher.Get())
                {
                    using (m)
                    {
                        m.InvokeMethod("WmiSetBrightness", [1u, (byte)Math.Clamp(percent, 0, 100)]);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }, ct);
    }

    public unsafe Task<bool> WriteDdcBrightnessAsync(nint monitorHandle, int percent, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (monitorHandle == 0) return false;
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(monitorHandle, out uint count) || count == 0)
                return false;

            var mons = new NativeMethods.PHYSICAL_MONITOR[count];
            fixed (NativeMethods.PHYSICAL_MONITOR* pMons = mons)
            {
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(monitorHandle, count, pMons))
                    return false;
                try
                {
                    int clamped = Math.Clamp(percent, 0, 100);
                    bool anySuccess = false;
                    for (uint i = 0; i < count; i++)
                    {
                        nint hPhys = mons[i].hPhysicalMonitor;
                        if (hPhys == 0) continue;
                        if (NativeMethods.GetMonitorBrightness(hPhys, out uint min, out _, out uint max) && max > min)
                        {
                            uint scaled = min + (uint)Math.Round((max - min) * (clamped / 100.0));
                            if (NativeMethods.SetMonitorBrightness(hPhys, scaled))
                                anySuccess = true;
                        }
                        else if (NativeMethods.SetMonitorBrightness(hPhys, (uint)clamped))
                        {
                            anySuccess = true;
                        }
                    }
                    return anySuccess;
                }
                finally
                {
                    NativeMethods.DestroyPhysicalMonitors(count, pMons);
                }
            }
        }, ct);
    }
}

/// <summary>
/// Bounded DropOldest coalescing brightness pipeline (prevents I2C DDC/CI slider stalls).
/// </summary>
public sealed class CoalescedBrightnessWriter : IAsyncDisposable
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentDictionary<string, BrightnessWriteRequest> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly IBrightnessHardwareDriver _driver;
    private readonly Func<string, nint> _handleResolver;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly TimeSpan _minSpacing;
    private int _disposed;

    public CoalescedBrightnessWriter(
        IBrightnessHardwareDriver driver,
        Func<string, nint> handleResolver,
        TimeSpan? minSpacing = null)
    {
        _driver = driver;
        _handleResolver = handleResolver;
        _minSpacing = minSpacing ?? TimeSpan.FromMilliseconds(65);
        _worker = Task.Run(PumpAsync);
    }

    public void Enqueue(BrightnessWriteRequest request)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _pending[request.DeviceId] = request;
        _channel.Writer.TryWrite(true);
    }

    public void BeginDispose()
    {
        _channel.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var _ in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                foreach (string deviceId in _pending.Keys)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    if (!_pending.TryRemove(deviceId, out var req)) continue;
                    try
                    {
                        if (req.IsInternal)
                            await _driver.WriteInternalBrightnessAsync(req.Percent, _cts.Token);
                        else
                            await _driver.WriteDdcBrightnessAsync(_handleResolver(req.DeviceId), req.Percent, _cts.Token);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        // A disconnected or failing monitor must not terminate the write pipeline.
                    }

                    if (_minSpacing > TimeSpan.Zero)
                        await Task.Delay(_minSpacing, _cts.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker; } catch { }
        _pending.Clear();
        _cts.Dispose();
    }
}

public sealed class BrightnessService : IBrightnessBackend, IAsyncDisposable
{
    private readonly IMonitorLayoutService _monitors;
    private readonly CoalescedBrightnessWriter _writer;
    private readonly ConcurrentDictionary<string, int> _softwareDimmers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly object _refreshSync = new();
    private List<DisplayBrightnessInfo> _displays = new();
    private int _disposed;
    private int _asyncDisposed;

    public event EventHandler<IReadOnlyList<DisplayBrightnessInfo>>? DisplaysChanged;
    public event EventHandler<IReadOnlyList<DisplayBrightnessInfo>>? ValuesUpdated;
    public event EventHandler<(string DeviceId, nint MonitorHandle, int DimPercent)>? SoftwareDimmerChanged;
    public event EventHandler<(string DeviceId, string FriendlyName, int Percent, bool IsSoftwareDimmer)>? BrightnessAdjustedByUser;

    public BrightnessService(IMonitorLayoutService monitors, IBrightnessHardwareDriver? driver = null)
    {
        _monitors = monitors;
        _writer = new CoalescedBrightnessWriter(
            driver ?? new Win32BrightnessHardwareDriver(),
            id =>
            {
                lock (_sync)
                {
                    return _displays.FirstOrDefault(d => string.Equals(d.DeviceId, id, StringComparison.OrdinalIgnoreCase))?.MonitorHandle ?? 0;
                }
            });
    }

    public IReadOnlyList<DisplayBrightnessInfo> GetDisplays()
    {
        lock (_sync) return _displays.ToList();
    }

    public Task RefreshDisplaysAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0) return;
            RefreshDisplays();
        }, ct);
    }

    public void RefreshDisplays()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        List<DisplayBrightnessInfo> updated;
        lock (_refreshSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var topology = QueryDisplayTopologyMetadata();
            int? wmiBrightness = TryReadInternalWmiBrightness();
            var monList = _monitors.GetMonitors();
            updated = new List<DisplayBrightnessInfo>(monList.Count);

            foreach (var mon in monList)
            {
                topology.TryGetValue(mon.DeviceName, out var meta);
                bool isInternal = meta.IsInternal || (wmiBrightness.HasValue && mon.IsPrimary && topology.Count == 0);
                string friendly = !string.IsNullOrWhiteSpace(meta.FriendlyName) ? meta.FriendlyName : mon.FriendlyName;
                bool supportsDdc = false;
                int brightness = isInternal && wmiBrightness.HasValue ? wmiBrightness.Value : 80;

                if (!isInternal && mon.Handle != 0)
                {
                    ProbeDdcMonitor(mon.Handle, out supportsDdc, out int ddcLevel);
                    if (supportsDdc) brightness = ddcLevel;
                }

                int dim = _softwareDimmers.GetValueOrDefault(mon.DeviceName, 0);
                updated.Add(new DisplayBrightnessInfo(
                    mon.DeviceName,
                    friendly,
                    mon.Handle,
                    isInternal,
                    supportsDdc,
                    meta.IsHdr,
                    brightness,
                    dim));
            }

            lock (_sync) _displays = updated;
        }

        if (Volatile.Read(ref _disposed) == 0)
        {
            DisplaysChanged?.Invoke(this, updated);
        }
    }

    public void SetHardwareBrightness(string deviceId, int percent)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        int clamped = Math.Clamp(percent, 0, 100);
        bool isInternal = false;
        string friendlyName = deviceId;
        lock (_sync)
        {
            for (int i = 0; i < _displays.Count; i++)
            {
                if (string.Equals(_displays[i].DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    isInternal = _displays[i].IsInternal;
                    friendlyName = _displays[i].FriendlyName;
                    _displays[i] = _displays[i] with { HardwareBrightness01To100 = clamped };
                }
            }
        }

        var snapshot = GetDisplays();
        _writer.Enqueue(new BrightnessWriteRequest(deviceId, clamped, isInternal));
        BrightnessAdjustedByUser?.Invoke(this, (deviceId, friendlyName, clamped, false));
        ValuesUpdated?.Invoke(this, snapshot);
        DisplaysChanged?.Invoke(this, snapshot);
    }

    public void SetSoftwareDimmer(string deviceId, int dimPercent)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        int clamped = Math.Clamp(dimPercent, 0, 75);
        _softwareDimmers[deviceId] = clamped;
        nint handle = 0;
        string friendlyName = deviceId;
        lock (_sync)
        {
            for (int i = 0; i < _displays.Count; i++)
            {
                if (string.Equals(_displays[i].DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    handle = _displays[i].MonitorHandle;
                    friendlyName = _displays[i].FriendlyName;
                    _displays[i] = _displays[i] with { SoftwareDimmerPercent = clamped };
                }
            }
        }
        var snapshot = GetDisplays();
        SoftwareDimmerChanged?.Invoke(this, (deviceId, handle, clamped));
        BrightnessAdjustedByUser?.Invoke(this, (deviceId, friendlyName, clamped, true));
        ValuesUpdated?.Invoke(this, snapshot);
        DisplaysChanged?.Invoke(this, snapshot);
    }

    private static int? TryReadInternalWmiBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\WMI"), new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    return Convert.ToInt32(obj["CurrentBrightness"]);
                }
            }
        }
        catch { }
        return null;
    }

    private static unsafe void ProbeDdcMonitor(nint hMonitor, out bool supportsDdc, out int currentPercent)
    {
        supportsDdc = false;
        currentPercent = 80;
        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
            return;

        var mons = new NativeMethods.PHYSICAL_MONITOR[count];
        fixed (NativeMethods.PHYSICAL_MONITOR* pMons = mons)
        {
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, pMons))
                return;
            try
            {
                for (uint i = 0; i < count; i++)
                {
                    nint hPhys = mons[i].hPhysicalMonitor;
                    if (hPhys == 0) continue;
                    if (NativeMethods.GetMonitorCapabilities(hPhys, out uint caps, out _) && (caps & 0x00000002 /* MC_CAPS_BRIGHTNESS */) != 0)
                    {
                        if (NativeMethods.GetMonitorBrightness(hPhys, out uint min, out uint cur, out uint max) && max > min)
                        {
                            supportsDdc = true;
                            currentPercent = (int)Math.Clamp(Math.Round((cur - min) * 100.0 / (max - min)), 0, 100);
                            break;
                        }
                    }
                }
            }
            finally
            {
                NativeMethods.DestroyPhysicalMonitors(count, pMons);
            }
        }
    }

    private static unsafe Dictionary<string, (string FriendlyName, bool IsInternal, bool IsHdr)> QueryDisplayTopologyMetadata()
    {
        var map = new Dictionary<string, (string, bool, bool)>(StringComparer.OrdinalIgnoreCase);
        if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
            return map;

        var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[modeCount];
        fixed (NativeMethods.DISPLAYCONFIG_PATH_INFO* pPaths = paths)
        fixed (NativeMethods.DISPLAYCONFIG_MODE_INFO* pModes = modes)
        {
            if (NativeMethods.QueryDisplayConfig(NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref pathCount, pPaths, ref modeCount, pModes, 0) != 0)
                return map;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var src = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = 1 /* DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME */,
                    size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id
                }
            };
            if (NativeMethods.DisplayConfigGetDeviceInfo(&src) != 0) continue;
            string gdiName = new string(src.viewGdiDeviceName).TrimEnd('\0');

            var tgt = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id
                }
            };
            string friendly = string.Empty;
            if (NativeMethods.DisplayConfigGetDeviceInfo(&tgt) == 0)
            {
                friendly = new string(tgt.monitorFriendlyDeviceName).TrimEnd('\0');
            }

            var hdr = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
            {
                header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                    size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id
                }
            };
            bool isHdr = NativeMethods.DisplayConfigGetDeviceInfo(&hdr) == 0 && (hdr.value & 0x2) != 0;

            uint tech = paths[i].targetInfo.outputTechnology;
            bool isInternal = tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
                           || tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
                           || tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS;

            if (!string.IsNullOrWhiteSpace(gdiName))
            {
                map[gdiName] = (friendly, isInternal, isHdr);
            }
        }

        return map;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writer.BeginDispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _asyncDisposed, 1) != 0) return;
        Interlocked.Exchange(ref _disposed, 1);
        await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
