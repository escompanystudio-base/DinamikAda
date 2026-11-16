using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;
using Microsoft.Win32;

namespace DinamikAda.Modules.Telemetry;

public sealed partial class SystemTelemetryService : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const int SystemBatteryStateInfoLevel = 15;
    private const int BatteryUnknownRate = unchecked((int)0x80000000);
    private const double DefaultMinRateWindowSec = 0.60;
    private const double InitialPrimeWindowSec = 0.42;
    private const int InitialPrimeDelayMs = 480;
    private const double StaleBaselineWindowSec = 20.0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DxgiEnumAdapters1Delegate(nint thisPtr, uint adapterIndex, out nint ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DxgiGetDesc1Delegate(nint thisPtr, out DXGI_ADAPTER_DESC1 pDesc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint DxgiReleaseDelegate(nint thisPtr);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out nint ppFactory);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
    {
        public nint szName;
        public uint CStatus;
        private readonly uint _pad;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_BATTERY_STATE
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare0;
        public byte Spare1;
        public byte Spare2;
        public byte Tag;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    [LibraryImport("powrprof.dll")]
    private static unsafe partial int CallNtPowerInformation(
        int InformationLevel,
        nint InputBuffer,
        uint InputBufferLength,
        SYSTEM_BATTERY_STATE* OutputBuffer,
        uint OutputBufferLength);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhRemoveCounter(nint hCounter);

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AdlPmLogDataOutput
    {
        public int Size;
        public fixed int Sensors[512];
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdlAdapterInfo
    {
        public int Size;
        public int AdapterIndex;
        public fixed byte StrUDID[256];
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorID;
        public fixed byte StrAdapterName[256];
        public fixed byte StrDisplayName[256];
        public int Present;
        public int Exist;
        public fixed byte StrDriverPath[256];
        public fixed byte StrDriverPathExt[256];
        public fixed byte StrPNPString[256];
        public int OSDisplayIndex;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint AdlMainMallocCallback(int size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Adl2MainControlCreateDelegate(AdlMainMallocCallback callback, int enumConnectedAdapters, out nint context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Adl2MainControlDestroyDelegate(nint context);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Adl2AdapterNumberOfAdaptersGetDelegate(nint context, out int numAdapters);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Adl2AdapterAdapterInfoGetDelegate(nint context, nint lpInfo, int inputSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Adl2NewQueryPmLogDataGetDelegate(nint context, int adapterIndex, ref AdlPmLogDataOutput lpPmLogDataOutput);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitV2Delegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleByIndexV2Delegate(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetTemperatureDelegate(nint device, uint sensorType, out uint temp);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetFanSpeedDelegate(nint device, out uint speed);

    private readonly HungProcessService _hungService;
    private readonly double _minRateWindowSec;
    private readonly string? _gpuAdapterName;
    private readonly HashSet<string>? _primaryGpuLuidTokens;
    private readonly double _gpuVramTotalGb;
    private readonly AdlMainMallocCallback _adlMallocCallback;
    private readonly List<nint> _adlAllocations = new();
    private readonly object _adlAllocLock = new();
    private readonly object _sync = new();
    private readonly object _gpuHwSync = new();
    private readonly object _pingSync = new();
    private readonly object _snapshotSync = new();
    private Timer? _timer;
    private Timer? _pingTimer;
    private CancellationTokenSource? _primeCts;
    private int _samplingActive;
    private int _extendedMetricsActive;
    private int _pingMonitorActive;
    private volatile bool _disposed;
    private int _samplingInFlight;
    private int _pingInFlight;

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasRateBaseline;
    private bool _hasValidRateSample;
    private long _lastRateSampleTicks;

    private long _prevNetBytesRx;
    private long _prevNetBytesTx;

    private double _lastCpuPercent;
    private double _lastGpuPercent;
    private string? _lastGpuEngine;
    private double _lastGpuVramUsedGb;
    private double _lastDiskReadMbps;
    private double _lastDiskWriteMbps;
    private double _lastNetUpMbps;
    private double _lastNetDownMbps;

    private string _pingHost = "1.1.1.1";
    private double? _lastPingMs;
    private long _lastPingAttemptTicks;

    private nint _pdhQuery;
    private nint _pdhCpuCounter;
    private nint _pdhGpuCounter;
    private nint _pdhGpuVramCounter;
    private nint _pdhDiskReadCounter;
    private nint _pdhDiskWriteCounter;
    private bool _pdhHasFirstSample;
    private bool _pdhExtendedBaselinesReady;
    private long _pdhInitTicks;

    private nint _adlLib;
    private nint _adlContext;
    private int _adlAdapterIndex = -1;
    private bool _adlInitAttempted;
    private Adl2MainControlDestroyDelegate? _adlDestroy;
    private Adl2AdapterNumberOfAdaptersGetDelegate? _adlNumAdapters;
    private Adl2AdapterAdapterInfoGetDelegate? _adlAdapterInfoGet;
    private Adl2NewQueryPmLogDataGetDelegate? _adlQueryPmLog;

    private nint _nvmlLib;
    private nint _nvmlDevice;
    private bool _nvmlInitialized;
    private bool _nvmlInitAttempted;
    private NvmlShutdownDelegate? _nvmlShutdown;
    private NvmlDeviceGetTemperatureDelegate? _nvmlGetTemp;
    private NvmlDeviceGetFanSpeedDelegate? _nvmlGetFanSpeed;

    private SystemTelemetrySnapshot _current;

    public event EventHandler<SystemTelemetrySnapshot>? TelemetryUpdated;
    public SystemTelemetrySnapshot Current => _current;

    public SystemTelemetryService(HungProcessService hungService)
        : this(hungService, DefaultMinRateWindowSec)
    {
    }

    internal SystemTelemetryService(HungProcessService hungService, double minRateWindowSec)
    {
        _hungService = hungService;
        _minRateWindowSec = Math.Max(0.05, minRateWindowSec);
        _adlMallocCallback = AdlMalloc;
        (_gpuAdapterName, _primaryGpuLuidTokens, _gpuVramTotalGb) = ReadPrimaryGpuInfo();
        var mem = SampleMemory();
        var bat = SampleBattery();
        _current = new SystemTelemetrySnapshot(
            0,
            Math.Round(mem.Pct, 1),
            Math.Round(mem.UsedGb, 1),
            Math.Round(mem.TotalGb, 1),
            0,
            0,
            0,
            bat.HasBattery,
            bat.Percent,
            bat.Charging,
            bat.Watts,
            bat.Eta,
            ImmutableArray<ProcessSummary>.Empty,
            _gpuAdapterName,
            null,
            GpuVramUsedGb: 0.0,
            GpuVramTotalGb: Math.Round(_gpuVramTotalGb, 2),
            GpuTemperatureC: null,
            GpuFanRpm: null,
            GpuFanPercent: null,
            DiskReadMbps: 0.0,
            DiskWriteMbps: 0.0,
            PingMs: null,
            PingHost: null,
            DiskVolumes: ImmutableArray<DiskVolumeSnapshot>.Empty);
    }

    public void SetExtendedMetricsActive(bool active)
    {
        if (_disposed) return;
        int next = active ? 1 : 0;
        if (Interlocked.Exchange(ref _extendedMetricsActive, next) == next) return;

        if (active)
        {
            if (Volatile.Read(ref _samplingActive) != 0)
            {
                _ = Task.Run(() =>
                {
                    if (_disposed || Volatile.Read(ref _extendedMetricsActive) == 0) return;
                    lock (_sync)
                    {
                        if (_disposed || Volatile.Read(ref _extendedMetricsActive) == 0) return;
                        if (_pdhQuery == 0)
                        {
                            InitializePdhLocked(includeExtended: true);
                        }
                        else
                        {
                            EnsureExtendedPdhCountersLocked();
                        }
                    }

                    SampleOnceCore(allowShortPrimeWindow: false, forceProcessScan: false, resetTimerPhase: false);
                });
            }
        }
        else
        {
            lock (_sync)
            {
                _lastGpuVramUsedGb = 0;
                _lastDiskReadMbps = 0;
                _lastDiskWriteMbps = 0;
            }

            bool pingMonitorStillActive = Volatile.Read(ref _pingMonitorActive) != 0;
            if (!pingMonitorStillActive)
            {
                lock (_pingSync)
                {
                    _lastPingMs = null;
                    _lastPingAttemptTicks = 0;
                }
            }

            SystemTelemetrySnapshot? cleared = null;
            lock (_snapshotSync)
            {
                if (!_disposed)
                {
                    cleared = _current with
                    {
                        GpuVramUsedGb = 0.0,
                        GpuTemperatureC = null,
                        GpuFanRpm = null,
                        GpuFanPercent = null,
                        DiskReadMbps = 0.0,
                        DiskWriteMbps = 0.0,
                        PingMs = pingMonitorStillActive ? _current.PingMs : null,
                        PingHost = pingMonitorStillActive ? _current.PingHost : null,
                        DiskVolumes = ImmutableArray<DiskVolumeSnapshot>.Empty
                    };
                    _current = cleared;
                }
            }

            if (cleared is not null && !_disposed)
            {
                TelemetryUpdated?.Invoke(this, cleared);
            }

            _ = Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_disposed || Volatile.Read(ref _extendedMetricsActive) == 0)
                    {
                        if (Volatile.Read(ref _samplingActive) == 0)
                        {
                            ClosePdhLocked();
                        }
                        else
                        {
                            RemoveExtendedPdhCountersLocked();
                        }
                    }
                }

                lock (_gpuHwSync)
                {
                    if (_disposed || Volatile.Read(ref _extendedMetricsActive) == 0)
                    {
                        CloseGpuThermalBackendsLocked();
                    }
                }
            });
        }
    }

    public void SetPingMonitorActive(bool active)
    {
        if (_disposed) return;
        int next = active ? 1 : 0;
        if (Interlocked.Exchange(ref _pingMonitorActive, next) == next) return;

        if (active)
        {
            var newTimer = new Timer(_ =>
            {
                if (_disposed || Volatile.Read(ref _pingMonitorActive) == 0) return;
                if (Volatile.Read(ref _samplingActive) == 0)
                {
                    MaybeTriggerPing(publishOnCompletion: true);
                }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));

            var oldTimer = Interlocked.Exchange(ref _pingTimer, newTimer);
            oldTimer?.Dispose();

            if (_disposed || Volatile.Read(ref _pingMonitorActive) == 0)
            {
                Interlocked.CompareExchange(ref _pingTimer, null, newTimer);
                newTimer.Dispose();
                return;
            }

            if (Volatile.Read(ref _samplingActive) != 0)
            {
                MaybeTriggerPing(publishOnCompletion: true);
            }
        }
        else
        {
            Interlocked.Exchange(ref _pingTimer, null)?.Dispose();

            if (Volatile.Read(ref _extendedMetricsActive) == 0)
            {
                lock (_pingSync)
                {
                    _lastPingMs = null;
                    _lastPingAttemptTicks = 0;
                }

                SystemTelemetrySnapshot? cleared = null;
                lock (_snapshotSync)
                {
                    if (!_disposed)
                    {
                        cleared = _current with { PingMs = null, PingHost = null };
                        _current = cleared;
                    }
                }

                if (cleared is not null && !_disposed)
                {
                    TelemetryUpdated?.Invoke(this, cleared);
                }
            }
        }
    }

    public void SetPingHost(string host)
    {
        string normalized = NormalizePingHost(host);
        bool changed;
        lock (_pingSync)
        {
            changed = !string.Equals(_pingHost, normalized, StringComparison.Ordinal);
            if (!changed) return;
            _pingHost = normalized;
            _lastPingMs = null;
            _lastPingAttemptTicks = 0;
        }

        if (_disposed) return;

        bool pingAllowed = Volatile.Read(ref _extendedMetricsActive) != 0 || Volatile.Read(ref _pingMonitorActive) != 0;
        if (pingAllowed)
        {
            SystemTelemetrySnapshot? updated = null;
            lock (_snapshotSync)
            {
                if (!_disposed)
                {
                    updated = _current with { PingMs = null, PingHost = normalized };
                    _current = updated;
                }
            }

            if (updated is not null && !_disposed)
            {
                TelemetryUpdated?.Invoke(this, updated);
            }

            MaybeTriggerPing(publishOnCompletion: true);
        }
    }

    public void SetSamplingActive(bool active)
    {
        if (_disposed) return;
        int next = active ? 1 : 0;
        if (Interlocked.Exchange(ref _samplingActive, next) == next) return;

        if (active)
        {
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var prevCts = Interlocked.Exchange(ref _primeCts, cts);
            if (prevCts is not null)
            {
                try { prevCts.Cancel(); } catch { }
                prevCts.Dispose();
            }

            var newTimer = new Timer(_ => _ = SampleOnceAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var oldTimer = Interlocked.Exchange(ref _timer, newTimer);
            oldTimer?.Dispose();

            if (_disposed || Volatile.Read(ref _samplingActive) == 0)
            {
                Interlocked.CompareExchange(ref _timer, null, newTimer);
                newTimer.Dispose();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_disposed || token.IsCancellationRequested || Volatile.Read(ref _samplingActive) == 0) return;

                    bool needsSynchronizedPrime;
                    bool extendedActive = Volatile.Read(ref _extendedMetricsActive) != 0;
                    lock (_sync)
                    {
                        if (_disposed) return;
                        if (_pdhQuery == 0)
                        {
                            InitializePdhLocked(includeExtended: extendedActive);
                        }
                        else if (extendedActive)
                        {
                            EnsureExtendedPdhCountersLocked();
                        }

                        long nowTicks = Stopwatch.GetTimestamp();
                        double gapSec = _hasRateBaseline && _lastRateSampleTicks > 0
                            ? (nowTicks - _lastRateSampleTicks) / (double)Stopwatch.Frequency
                            : double.MaxValue;

                        needsSynchronizedPrime = !_hasRateBaseline || gapSec > StaleBaselineWindowSec;
                        if (needsSynchronizedPrime)
                        {
                            PrimeBaselinesLocked(nowTicks);
                        }
                    }

                    if (needsSynchronizedPrime)
                    {
                        _hungService.PrimeCpuBaseline();
                        // Emit instant memory/battery/process snapshot without blanking valid cached rates.
                        SampleOnceCore(allowShortPrimeWindow: false, forceProcessScan: true, resetTimerPhase: false);
                    }
                    else
                    {
                        // Fresh recent baseline exists: immediately compute real delta over elapsed wall time.
                        SampleOnceCore(allowShortPrimeWindow: false, forceProcessScan: true, resetTimerPhase: false);
                    }

                    await Task.Delay(InitialPrimeDelayMs, token).ConfigureAwait(false);
                    if (_disposed || token.IsCancellationRequested || Volatile.Read(ref _samplingActive) == 0) return;

                    // Compute synchronized PDH CPU Utility + Hardware GPU + Process CPU window.
                    SampleOnceCore(allowShortPrimeWindow: true, forceProcessScan: true, resetTimerPhase: false);

                    var timer = Volatile.Read(ref _timer);
                    if (!_disposed && !token.IsCancellationRequested && Volatile.Read(ref _samplingActive) != 0 && timer is not null)
                    {
                        try { timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)); } catch { }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }
        else
        {
            var prevCts = Interlocked.Exchange(ref _primeCts, null);
            if (prevCts is not null)
            {
                try { prevCts.Cancel(); } catch { }
                prevCts.Dispose();
            }

            Interlocked.Exchange(ref _timer, null)?.Dispose();
            _ = Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_disposed || (Volatile.Read(ref _samplingActive) == 0 && Volatile.Read(ref _extendedMetricsActive) == 0))
                    {
                        ClosePdhLocked();
                    }
                }
            });
        }
    }

    public Task SampleOnceAsync(CancellationToken ct = default) =>
        SampleOnceCoreAsync(waitForInitialPrime: false, forceProcessScan: false, resetTimerPhase: false, ct);

    public Task RefreshNowAsync(CancellationToken ct = default) =>
        SampleOnceCoreAsync(waitForInitialPrime: true, forceProcessScan: true, resetTimerPhase: true, ct);

    private Task SampleOnceCoreAsync(bool waitForInitialPrime, bool forceProcessScan, bool resetTimerPhase, CancellationToken ct)
    {
        if (_disposed) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            if (ct.IsCancellationRequested || _disposed) return;

            if (waitForInitialPrime && !_hasValidRateSample)
            {
                SampleOnceCore(allowShortPrimeWindow: false, forceProcessScan: forceProcessScan, resetTimerPhase: false);
                await Task.Delay(InitialPrimeDelayMs, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _disposed) return;
                SampleOnceCore(allowShortPrimeWindow: true, forceProcessScan: forceProcessScan, resetTimerPhase: resetTimerPhase);
                return;
            }

            SampleOnceCore(allowShortPrimeWindow: false, forceProcessScan: forceProcessScan, resetTimerPhase: resetTimerPhase);
        }, ct);
    }

    public void SampleOnce() =>
        SampleOnceCore(allowShortPrimeWindow: false, forceProcessScan: false, resetTimerPhase: false);

    private void SampleOnceCore(bool allowShortPrimeWindow, bool forceProcessScan, bool resetTimerPhase)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _samplingInFlight, 1, 0) != 0) return;

        bool sampledNewRates = false;
        try
        {
            if (_disposed) return;

            bool active = Volatile.Read(ref _samplingActive) != 0;
            bool extendedActive = Volatile.Read(ref _extendedMetricsActive) != 0;
            bool pingActive = extendedActive || Volatile.Read(ref _pingMonitorActive) != 0;

            double cpu;
            double gpu;
            string? gpuEngine;
            double gpuVramUsedGb;
            double diskReadMbps;
            double diskWriteMbps;
            double netUp;
            double netDown;

            lock (_sync)
            {
                if (_disposed) return;

                if ((active || extendedActive) && _pdhQuery == 0)
                {
                    InitializePdhLocked(includeExtended: extendedActive);
                }
                else if (extendedActive && _pdhQuery != 0)
                {
                    EnsureExtendedPdhCountersLocked();
                }

                long nowTicks = Stopwatch.GetTimestamp();
                double elapsedSec = _hasRateBaseline && _lastRateSampleTicks > 0
                    ? (nowTicks - _lastRateSampleTicks) / (double)Stopwatch.Frequency
                    : 0.0;

                double minWindow = allowShortPrimeWindow ? Math.Min(_minRateWindowSec, InitialPrimeWindowSec) : _minRateWindowSec;

                if (!_hasRateBaseline)
                {
                    PrimeBaselinesLocked(nowTicks);
                }
                else if (elapsedSec >= minWindow)
                {
                    SampleRateMetricsLocked(nowTicks, elapsedSec, active, extendedActive);
                    sampledNewRates = true;
                }

                cpu = _lastCpuPercent;
                gpu = _lastGpuPercent;
                gpuEngine = _lastGpuEngine;
                gpuVramUsedGb = extendedActive ? _lastGpuVramUsedGb : 0.0;
                diskReadMbps = extendedActive ? _lastDiskReadMbps : 0.0;
                diskWriteMbps = extendedActive ? _lastDiskWriteMbps : 0.0;
                netUp = _lastNetUpMbps;
                netDown = _lastNetDownMbps;
            }

            (double memPct, double memUsedGb, double memTotalGb) = SampleMemory();
            (bool hasBat, int batPct, bool charging, double? watts, TimeSpan? eta) = SampleBattery();
            var procs = active || forceProcessScan
                ? _hungService.ScanHeavyAndHungProcesses()
                : _current.TopOrHungProcesses;

            double? gpuTempC = null;
            int? gpuFanRpm = null;
            int? gpuFanPercent = null;
            ImmutableArray<DiskVolumeSnapshot> diskVolumes = ImmutableArray<DiskVolumeSnapshot>.Empty;

            if (extendedActive)
            {
                (gpuTempC, gpuFanRpm, gpuFanPercent) = SampleGpuThermalsAndFan();
                diskVolumes = SampleFixedDiskVolumes();
            }

            double? pingMs = null;
            string? pingHost = null;
            if (pingActive)
            {
                MaybeTriggerPing(publishOnCompletion: true);
                lock (_pingSync)
                {
                    pingMs = _lastPingMs;
                    pingHost = _pingHost;
                }
            }

            var next = new SystemTelemetrySnapshot(
                Math.Round(cpu, 1),
                Math.Round(memPct, 1),
                Math.Round(memUsedGb, 1),
                Math.Round(memTotalGb, 1),
                Math.Round(gpu, 1),
                Math.Round(netUp, 2),
                Math.Round(netDown, 2),
                hasBat,
                batPct,
                charging,
                watts,
                eta,
                procs,
                _gpuAdapterName,
                gpuEngine,
                GpuVramUsedGb: Math.Round(gpuVramUsedGb, 2),
                GpuVramTotalGb: Math.Round(_gpuVramTotalGb, 2),
                GpuTemperatureC: gpuTempC,
                GpuFanRpm: gpuFanRpm,
                GpuFanPercent: gpuFanPercent,
                DiskReadMbps: Math.Round(diskReadMbps, 2),
                DiskWriteMbps: Math.Round(diskWriteMbps, 2),
                PingMs: pingMs,
                PingHost: pingHost,
                DiskVolumes: diskVolumes);

            lock (_snapshotSync)
            {
                if (_disposed) return;
                _current = next;
            }

            if (!_disposed)
            {
                TelemetryUpdated?.Invoke(this, next);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _samplingInFlight, 0);
        }

        if (sampledNewRates && resetTimerPhase && !_disposed && Volatile.Read(ref _samplingActive) != 0)
        {
            var timer = Volatile.Read(ref _timer);
            if (timer is not null)
            {
                try { timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)); } catch { }
            }
        }
    }

    private void ResetRateStateLocked()
    {
        _hasRateBaseline = false;
        _hasValidRateSample = false;
        _lastRateSampleTicks = 0;
        _prevIdle = 0;
        _prevKernel = 0;
        _prevUser = 0;
        _prevNetBytesRx = 0;
        _prevNetBytesTx = 0;
        _lastCpuPercent = 0;
        _lastGpuPercent = 0;
        _lastGpuEngine = null;
        _lastGpuVramUsedGb = 0;
        _lastDiskReadMbps = 0;
        _lastDiskWriteMbps = 0;
        _lastNetUpMbps = 0;
        _lastNetDownMbps = 0;
    }

    private void PrimeBaselinesLocked(long nowTicks)
    {
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _prevIdle = idle.ToUInt64();
            _prevKernel = kernel.ToUInt64();
            _prevUser = user.ToUInt64();
        }

        var (rx, tx) = ReadTotalNetworkBytes();
        _prevNetBytesRx = rx;
        _prevNetBytesTx = tx;

        if (_pdhQuery != 0 && !_pdhHasFirstSample)
        {
            _ = NativeMethods.PdhCollectQueryData(_pdhQuery);
            _pdhHasFirstSample = true;
            _pdhExtendedBaselinesReady = _pdhGpuVramCounter != 0 || _pdhDiskReadCounter != 0 || _pdhDiskWriteCounter != 0;
        }

        _hasRateBaseline = true;
        _lastRateSampleTicks = nowTicks;
    }

    private void SampleRateMetricsLocked(long nowTicks, double elapsedSec, bool collectGpuPdh, bool collectExtendedPdh)
    {
        double? pdhCpu = null;
        double? pdhGpu = null;
        string? pdhGpuEngine = _lastGpuEngine;
        double? pdhGpuVramGb = null;
        double? pdhDiskReadMbps = null;
        double? pdhDiskWriteMbps = null;

        if (_pdhQuery != 0)
        {
            double pdhElapsedSec = _pdhInitTicks > 0
                ? (nowTicks - _pdhInitTicks) / (double)Stopwatch.Frequency
                : elapsedSec;
            if (pdhElapsedSec >= 0.30)
            {
                try
                {
                    if (NativeMethods.PdhCollectQueryData(_pdhQuery) == 0)
                    {
                        if (!_pdhHasFirstSample)
                        {
                            _pdhHasFirstSample = true;
                            if (collectExtendedPdh)
                            {
                                _pdhExtendedBaselinesReady = true;
                            }
                        }
                        else
                        {
                            pdhCpu = ReadPdhCpuUtilityLocked();
                            if (collectGpuPdh)
                            {
                                (pdhGpu, pdhGpuEngine) = ReadPdhGpuPercentLocked();
                            }

                            if (collectExtendedPdh)
                            {
                                pdhGpuVramGb = ReadPdhGpuVramUsedGbLocked();
                                if (_pdhExtendedBaselinesReady)
                                {
                                    pdhDiskReadMbps = ReadPdhDiskRateMbpsLocked(_pdhDiskReadCounter);
                                    pdhDiskWriteMbps = ReadPdhDiskRateMbpsLocked(_pdhDiskWriteCounter);
                                }
                                _pdhExtendedBaselinesReady = true;
                            }
                        }
                    }
                }
                catch
                {
                    // Keep Win32 GetSystemTimes fallback active.
                }
            }
        }

        double win32Cpu = ReadGetSystemTimesCpuLocked();
        _lastCpuPercent = pdhCpu ?? win32Cpu;

        if (collectGpuPdh)
        {
            if (pdhGpu.HasValue)
            {
                _lastGpuPercent = pdhGpu.Value;
                _lastGpuEngine = pdhGpuEngine;
            }
        }
        else
        {
            _lastGpuPercent = 0;
            _lastGpuEngine = null;
        }

        if (collectExtendedPdh)
        {
            if (pdhGpuVramGb.HasValue)
            {
                _lastGpuVramUsedGb = pdhGpuVramGb.Value;
            }
            if (pdhDiskReadMbps.HasValue)
            {
                _lastDiskReadMbps = pdhDiskReadMbps.Value;
            }
            if (pdhDiskWriteMbps.HasValue)
            {
                _lastDiskWriteMbps = pdhDiskWriteMbps.Value;
            }
        }
        else
        {
            _lastGpuVramUsedGb = 0;
            _lastDiskReadMbps = 0;
            _lastDiskWriteMbps = 0;
        }

        var (rx, tx) = ReadTotalNetworkBytes();
        long deltaRx = rx >= _prevNetBytesRx ? rx - _prevNetBytesRx : 0;
        long deltaTx = tx >= _prevNetBytesTx ? tx - _prevNetBytesTx : 0;
        _prevNetBytesRx = rx;
        _prevNetBytesTx = tx;

        _lastNetDownMbps = deltaRx * 8.0 / (elapsedSec * 1_000_000.0);
        _lastNetUpMbps = deltaTx * 8.0 / (elapsedSec * 1_000_000.0);

        _lastRateSampleTicks = nowTicks;
        _hasValidRateSample = true;
    }

    private double ReadGetSystemTimesCpuLocked()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user)) return _lastCpuPercent;
        ulong id = idle.ToUInt64();
        ulong ke = kernel.ToUInt64();
        ulong us = user.ToUInt64();

        ulong dIdle = id >= _prevIdle ? id - _prevIdle : 0;
        ulong dKernel = ke >= _prevKernel ? ke - _prevKernel : 0;
        ulong dUser = us >= _prevUser ? us - _prevUser : 0;
        ulong dSys = dKernel + dUser;

        _prevIdle = id;
        _prevKernel = ke;
        _prevUser = us;

        if (dSys == 0) return _lastCpuPercent;
        double busy = dSys > dIdle ? (double)(dSys - dIdle) * 100.0 / dSys : 0.0;
        return Math.Clamp(busy, 0.0, 100.0);
    }

    private double? ReadPdhCpuUtilityLocked()
    {
        if (_pdhCpuCounter == 0) return null;
        if (NativeMethods.PdhGetFormattedCounterValue(_pdhCpuCounter, PDH_FMT_DOUBLE, out _, out var val) == 0 &&
            val.CStatus == 0 &&
            double.IsFinite(val.doubleValue))
        {
            return Math.Clamp(val.doubleValue, 0.0, 100.0);
        }
        return null;
    }

    private static (double Pct, double UsedGb, double TotalGb) SampleMemory()
    {
        var st = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref st) || st.ullTotalPhys == 0) return (0, 0, 0);

        double totalGb = st.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
        double availGb = st.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
        double usedGb = Math.Max(0, totalGb - availGb);
        double pct = Math.Clamp((double)(st.ullTotalPhys - st.ullAvailPhys) * 100.0 / st.ullTotalPhys, 0.0, 100.0);
        return (pct, usedGb, totalGb);
    }

    private void InitializePdhLocked(bool includeExtended)
    {
        try
        {
            if (NativeMethods.PdhOpenQueryW(null, 0, out _pdhQuery) == 0 && _pdhQuery != 0)
            {
                uint cpuSt = NativeMethods.PdhAddEnglishCounterW(
                    _pdhQuery,
                    @"\Processor Information(_Total)\% Processor Utility",
                    0,
                    out _pdhCpuCounter);
                if (cpuSt != 0)
                {
                    _pdhCpuCounter = 0;
                }

                uint gpuSt = NativeMethods.PdhAddEnglishCounterW(
                    _pdhQuery,
                    @"\GPU Engine(*)\Utilization Percentage",
                    0,
                    out _pdhGpuCounter);
                if (gpuSt != 0)
                {
                    _pdhGpuCounter = 0;
                }

                if (includeExtended)
                {
                    _ = AddExtendedPdhCountersLocked();
                }

                if (_pdhCpuCounter == 0 &&
                    _pdhGpuCounter == 0 &&
                    _pdhGpuVramCounter == 0 &&
                    _pdhDiskReadCounter == 0 &&
                    _pdhDiskWriteCounter == 0)
                {
                    ClosePdhLocked();
                }
                else
                {
                    _ = NativeMethods.PdhCollectQueryData(_pdhQuery);
                    _pdhHasFirstSample = true;
                    _pdhExtendedBaselinesReady =
                        _pdhGpuVramCounter != 0 || _pdhDiskReadCounter != 0 || _pdhDiskWriteCounter != 0;
                    _pdhInitTicks = Stopwatch.GetTimestamp();
                }
            }
        }
        catch
        {
            ClosePdhLocked();
        }
    }

    private void EnsureExtendedPdhCountersLocked()
    {
        if (_pdhQuery == 0 || _disposed) return;
        if (AddExtendedPdhCountersLocked())
        {
            try
            {
                _ = NativeMethods.PdhCollectQueryData(_pdhQuery);
            }
            catch
            {
            }
            _pdhHasFirstSample = true;
            // Require one elapsed interval after dynamically attaching disk rate counters
            // so the next sample computes a smooth bytes/sec delta rather than a near-zero interval.
            _pdhExtendedBaselinesReady = false;
        }
    }

    private bool AddExtendedPdhCountersLocked()
    {
        if (_pdhQuery == 0) return false;
        bool added = false;

        if (_pdhGpuVramCounter == 0)
        {
            if (NativeMethods.PdhAddEnglishCounterW(
                    _pdhQuery,
                    @"\GPU Adapter Memory(*)\Dedicated Usage",
                    0,
                    out _pdhGpuVramCounter) != 0)
            {
                _pdhGpuVramCounter = 0;
            }
            else
            {
                added = true;
            }
        }

        if (_pdhDiskReadCounter == 0)
        {
            if (NativeMethods.PdhAddEnglishCounterW(
                    _pdhQuery,
                    @"\PhysicalDisk(_Total)\Disk Read Bytes/sec",
                    0,
                    out _pdhDiskReadCounter) != 0)
            {
                _pdhDiskReadCounter = 0;
            }
            else
            {
                added = true;
            }
        }

        if (_pdhDiskWriteCounter == 0)
        {
            if (NativeMethods.PdhAddEnglishCounterW(
                    _pdhQuery,
                    @"\PhysicalDisk(_Total)\Disk Write Bytes/sec",
                    0,
                    out _pdhDiskWriteCounter) != 0)
            {
                _pdhDiskWriteCounter = 0;
            }
            else
            {
                added = true;
            }
        }

        return added;
    }

    private void RemoveExtendedPdhCountersLocked()
    {
        if (_pdhGpuVramCounter != 0)
        {
            try { _ = PdhRemoveCounter(_pdhGpuVramCounter); } catch { }
            _pdhGpuVramCounter = 0;
        }
        if (_pdhDiskReadCounter != 0)
        {
            try { _ = PdhRemoveCounter(_pdhDiskReadCounter); } catch { }
            _pdhDiskReadCounter = 0;
        }
        if (_pdhDiskWriteCounter != 0)
        {
            try { _ = PdhRemoveCounter(_pdhDiskWriteCounter); } catch { }
            _pdhDiskWriteCounter = 0;
        }
        _pdhExtendedBaselinesReady = false;
    }

    private void ClosePdhLocked()
    {
        if (_pdhQuery != 0)
        {
            try { NativeMethods.PdhCloseQuery(_pdhQuery); } catch { }
            _pdhQuery = 0;
            _pdhCpuCounter = 0;
            _pdhGpuCounter = 0;
            _pdhGpuVramCounter = 0;
            _pdhDiskReadCounter = 0;
            _pdhDiskWriteCounter = 0;
            _pdhHasFirstSample = false;
            _pdhExtendedBaselinesReady = false;
            _pdhInitTicks = 0;
        }
    }

    private unsafe double? ReadPdhGpuVramUsedGbLocked()
    {
        if (_pdhQuery == 0 || _pdhGpuVramCounter == 0) return null;
        try
        {
            uint bufSize = 0;
            uint status = NativeMethods.PdhGetFormattedCounterArrayW(_pdhGpuVramCounter, PDH_FMT_DOUBLE, ref bufSize, out uint count, 0);
            if (status != PDH_MORE_DATA || bufSize == 0) return null;

            nint mem = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                if (NativeMethods.PdhGetFormattedCounterArrayW(_pdhGpuVramCounter, PDH_FMT_DOUBLE, ref bufSize, out count, mem) != 0)
                    return null;

                var entries = new List<KeyValuePair<string, double>>((int)Math.Min(count, 64));
                var items = (PDH_FMT_COUNTERVALUE_ITEM_DOUBLE*)mem;
                for (uint i = 0; i < count; i++)
                {
                    if (items[i].CStatus == 0 && double.IsFinite(items[i].doubleValue))
                    {
                        string? name = items[i].szName != 0 ? Marshal.PtrToStringUni(items[i].szName) : null;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            entries.Add(new KeyValuePair<string, double>(name, items[i].doubleValue));
                        }
                    }
                }

                return entries.Count == 0
                    ? null
                    : ComputeGpuVramUsedGb(entries, _primaryGpuLuidTokens, _gpuVramTotalGb);
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        catch
        {
            return null;
        }
    }

    private double? ReadPdhDiskRateMbpsLocked(nint counter)
    {
        if (_pdhQuery == 0 || counter == 0) return null;
        try
        {
            if (NativeMethods.PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var val) == 0 &&
                val.CStatus == 0 &&
                double.IsFinite(val.doubleValue))
            {
                return BytesPerSecToMegabytesPerSec(Math.Max(0.0, val.doubleValue));
            }
        }
        catch
        {
        }
        return null;
    }

    public static double BytesToGigabytes(ulong bytes) =>
        bytes / (1024.0 * 1024.0 * 1024.0);

    public static double BytesPerSecToMegabytesPerSec(double bytesPerSec) =>
        double.IsFinite(bytesPerSec) && bytesPerSec > 0
            ? bytesPerSec / (1024.0 * 1024.0)
            : 0.0;

    public static double ComputeGpuVramUsedGb(
        IEnumerable<KeyValuePair<string, double>> instances,
        IReadOnlySet<string>? allowedLuidTokens,
        double totalVramGb = 0.0)
    {
        double maxUsedBytes = 0.0;
        bool matchedAny = false;

        foreach (var entry in instances)
        {
            if (!ShouldIncludeGpuInstance(entry.Key, allowedLuidTokens)) continue;
            if (!double.IsFinite(entry.Value) || entry.Value < 0) continue;
            matchedAny = true;
            if (entry.Value > maxUsedBytes)
            {
                maxUsedBytes = entry.Value;
            }
        }

        if (!matchedAny) return 0.0;

        double usedGb = maxUsedBytes / (1024.0 * 1024.0 * 1024.0);
        return totalVramGb > 0.0
            ? Math.Clamp(usedGb, 0.0, totalVramGb)
            : Math.Max(0.0, usedGb);
    }

    private unsafe (double? Pct, string? DominantEngine) ReadPdhGpuPercentLocked()
    {
        if (_pdhQuery == 0 || _pdhGpuCounter == 0) return (null, null);
        try
        {
            uint bufSize = 0;
            uint status = NativeMethods.PdhGetFormattedCounterArrayW(_pdhGpuCounter, PDH_FMT_DOUBLE, ref bufSize, out uint count, 0);
            if (status != PDH_MORE_DATA || bufSize == 0) return (0.0, null);

            nint mem = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                if (NativeMethods.PdhGetFormattedCounterArrayW(_pdhGpuCounter, PDH_FMT_DOUBLE, ref bufSize, out count, mem) != 0)
                    return (null, null);

                var engineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var items = (PDH_FMT_COUNTERVALUE_ITEM_DOUBLE*)mem;
                for (uint i = 0; i < count; i++)
                {
                    if (items[i].CStatus == 0 && double.IsFinite(items[i].doubleValue))
                    {
                        string? name = items[i].szName != 0 ? Marshal.PtrToStringUni(items[i].szName) : null;
                        if (!ShouldIncludeGpuInstance(name, _primaryGpuLuidTokens)) continue;
                        string key = ExtractGpuEngineGroupKey(name);
                        double val = Math.Max(0.0, items[i].doubleValue);
                        engineTotals[key] = engineTotals.TryGetValue(key, out double cur) ? cur + val : val;
                    }
                }

                double maxEngineUtil = 0;
                string? maxEngineKey = null;
                foreach (var kv in engineTotals)
                {
                    if (kv.Value > maxEngineUtil)
                    {
                        maxEngineUtil = kv.Value;
                        maxEngineKey = kv.Key;
                    }
                }

                double clamped = Math.Clamp(maxEngineUtil, 0.0, 100.0);
                string? label = clamped >= 0.5 ? FormatGpuEngineLabel(maxEngineKey) : null;
                return (clamped, label);
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
        catch
        {
            return (null, null);
        }
    }

    public static string? ExtractGpuLuidToken(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;
        int luidIdx = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (luidIdx < 0) return null;
        int physIdx = instanceName.IndexOf("_phys_", luidIdx, StringComparison.OrdinalIgnoreCase);
        return physIdx > luidIdx
            ? instanceName[luidIdx..physIdx]
            : instanceName[luidIdx..];
    }

    public static bool ShouldIncludeGpuInstance(string? instanceName, IReadOnlySet<string>? allowedLuidTokens)
    {
        if (allowedLuidTokens is null || allowedLuidTokens.Count == 0) return true;
        string? token = ExtractGpuLuidToken(instanceName);
        return token is not null && allowedLuidTokens.Contains(token);
    }

    private static string ExtractGpuEngineGroupKey(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return "default";
        int luidIdx = instanceName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (luidIdx >= 0) return instanceName[luidIdx..];
        int engTypeIdx = instanceName.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        return engTypeIdx >= 0 ? instanceName[engTypeIdx..] : instanceName;
    }

    public static string? FormatGpuEngineLabel(string? groupKey)
    {
        if (string.IsNullOrWhiteSpace(groupKey)) return null;
        int idx = groupKey.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        string raw = (idx >= 0 ? groupKey[(idx + 8)..] : groupKey).Trim();
        if (raw.Length == 0) return null;

        if (raw.StartsWith("3d", StringComparison.OrdinalIgnoreCase)) return "3D";
        if (raw.Contains("video codec", StringComparison.OrdinalIgnoreCase)) return "Video Codec";
        if (raw.Contains("video decode", StringComparison.OrdinalIgnoreCase)) return "Video Decode";
        if (raw.Contains("video encode", StringComparison.OrdinalIgnoreCase)) return "Video Encode";
        if (raw.Contains("compute", StringComparison.OrdinalIgnoreCase)) return "Compute";
        if (raw.Contains("copy", StringComparison.OrdinalIgnoreCase)) return "Copy";
        return raw.Length > 14 ? raw[..14] : raw;
    }

    public static string ShortenGpuName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "GPU";
        string clean = fullName.Trim()
            .Replace("AMD Radeon ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return clean.Length > 20 ? clean[..20].TrimEnd() : clean;
    }

    private static (string? PrimaryName, HashSet<string>? AllowedLuidTokens, double DedicatedVramGb) ReadPrimaryGpuInfo()
    {
        try
        {
            Guid factoryIid = new("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(in factoryIid, out nint factoryPtr) == 0 && factoryPtr != 0)
            {
                nint vtbl = Marshal.ReadIntPtr(factoryPtr);
                var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<DxgiEnumAdapters1Delegate>(
                    Marshal.ReadIntPtr(vtbl, 12 * IntPtr.Size));
                var releaseFactory = Marshal.GetDelegateForFunctionPointer<DxgiReleaseDelegate>(
                    Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));

                try
                {
                    var hardware = new List<(string Name, ulong DedicatedVram, string LuidToken)>();
                    for (uint idx = 0; enumAdapters1(factoryPtr, idx, out nint adapterPtr) == 0; idx++)
                    {
                        if (adapterPtr == 0) continue;
                        nint adVtbl = Marshal.ReadIntPtr(adapterPtr);
                        var getDesc1 = Marshal.GetDelegateForFunctionPointer<DxgiGetDesc1Delegate>(
                            Marshal.ReadIntPtr(adVtbl, 10 * IntPtr.Size));
                        var releaseAd = Marshal.GetDelegateForFunctionPointer<DxgiReleaseDelegate>(
                            Marshal.ReadIntPtr(adVtbl, 2 * IntPtr.Size));

                        try
                        {
                            if (getDesc1(adapterPtr, out var desc) == 0)
                            {
                                bool isSoftware = (desc.Flags & 2u) != 0; // DXGI_ADAPTER_FLAG_SOFTWARE
                                string name = (desc.Description ?? string.Empty).Trim();
                                if (!isSoftware && !string.IsNullOrWhiteSpace(name) && !IsVirtualDisplayAdapter(name))
                                {
                                    uint low = (uint)(desc.AdapterLuid & 0xFFFFFFFFL);
                                    int high = (int)(desc.AdapterLuid >> 32);
                                    string token = $"luid_0x{high:X8}_0x{low:X8}";
                                    hardware.Add((name, (ulong)desc.DedicatedVideoMemory, token));
                                }
                            }
                        }
                        finally
                        {
                            releaseAd(adapterPtr);
                        }
                    }

                    if (hardware.Count > 0)
                    {
                        var primary = hardware
                            .OrderByDescending(a => a.DedicatedVram)
                            .First();
                        var luids = new HashSet<string>(
                            hardware
                                .Where(a => string.Equals(a.Name, primary.Name, StringComparison.OrdinalIgnoreCase))
                                .Select(a => a.LuidToken),
                            StringComparer.OrdinalIgnoreCase);
                        return (primary.Name, luids, BytesToGigabytes(primary.DedicatedVram));
                    }
                }
                finally
                {
                    releaseFactory(factoryPtr);
                }
            }
        }
        catch
        {
            // Fall back cleanly to Registry display adapter lookup.
        }

        return (ReadPrimaryGpuName(), null, 0.0);
    }

    private (double? TempC, int? FanRpm, int? FanPercent) SampleGpuThermalsAndFan()
    {
        lock (_gpuHwSync)
        {
            if (_disposed || Volatile.Read(ref _extendedMetricsActive) == 0)
            {
                return (null, null, null);
            }

            bool preferNvml = _gpuAdapterName is not null &&
                _gpuAdapterName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

            if (preferNvml)
            {
                if (TryReadNvmlThermalsLocked(out var nvml)) return nvml;
                if (TryReadAdlThermalsLocked(out var adl)) return adl;
            }
            else
            {
                if (TryReadAdlThermalsLocked(out var adl)) return adl;
                if (TryReadNvmlThermalsLocked(out var nvml)) return nvml;
            }

            return (null, null, null);
        }
    }

    private nint AdlMalloc(int size)
    {
        if (size <= 0) return 0;
        nint ptr = Marshal.AllocHGlobal(size);
        lock (_adlAllocLock)
        {
            _adlAllocations.Add(ptr);
        }
        return ptr;
    }

    private void FreeAdlAllocationsLocked()
    {
        lock (_adlAllocLock)
        {
            for (int i = 0; i < _adlAllocations.Count; i++)
            {
                nint ptr = _adlAllocations[i];
                if (ptr != 0)
                {
                    try { Marshal.FreeHGlobal(ptr); } catch { }
                }
            }
            _adlAllocations.Clear();
        }
    }

    private unsafe bool EnsureAdlInitializedLocked()
    {
        if (_adlContext != 0 && _adlAdapterIndex >= 0 && _adlQueryPmLog is not null)
        {
            return true;
        }

        if (_adlInitAttempted) return false;
        _adlInitAttempted = true;

        try
        {
            if (!NativeLibrary.TryLoad("atiadlxx.dll", out _adlLib) || _adlLib == 0)
            {
                return false;
            }

            if (!NativeLibrary.TryGetExport(_adlLib, "ADL2_Main_Control_Create", out nint pCreate) ||
                !NativeLibrary.TryGetExport(_adlLib, "ADL2_Main_Control_Destroy", out nint pDestroy) ||
                !NativeLibrary.TryGetExport(_adlLib, "ADL2_Adapter_NumberOfAdapters_Get", out nint pNumAdapters) ||
                !NativeLibrary.TryGetExport(_adlLib, "ADL2_Adapter_AdapterInfo_Get", out nint pAdapterInfo) ||
                !NativeLibrary.TryGetExport(_adlLib, "ADL2_New_QueryPMLogData_Get", out nint pQueryPmLog))
            {
                CloseAdlLocked(resetAttemptFlag: false);
                return false;
            }

            var adlCreate = Marshal.GetDelegateForFunctionPointer<Adl2MainControlCreateDelegate>(pCreate);
            _adlDestroy = Marshal.GetDelegateForFunctionPointer<Adl2MainControlDestroyDelegate>(pDestroy);
            _adlNumAdapters = Marshal.GetDelegateForFunctionPointer<Adl2AdapterNumberOfAdaptersGetDelegate>(pNumAdapters);
            _adlAdapterInfoGet = Marshal.GetDelegateForFunctionPointer<Adl2AdapterAdapterInfoGetDelegate>(pAdapterInfo);
            _adlQueryPmLog = Marshal.GetDelegateForFunctionPointer<Adl2NewQueryPmLogDataGetDelegate>(pQueryPmLog);

            if (adlCreate(_adlMallocCallback, 1, out _adlContext) != 0 || _adlContext == 0)
            {
                CloseAdlLocked(resetAttemptFlag: false);
                return false;
            }

            if (_adlNumAdapters(_adlContext, out int adapterCount) != 0 || adapterCount <= 0)
            {
                try { _adlDestroy(_adlContext); } catch { }
                _adlContext = 0;
                FreeAdlAllocationsLocked();

                if (adlCreate(_adlMallocCallback, 0, out _adlContext) != 0 || _adlContext == 0 ||
                    _adlNumAdapters(_adlContext, out adapterCount) != 0 || adapterCount <= 0)
                {
                    CloseAdlLocked(resetAttemptFlag: false);
                    return false;
                }
            }

            int structSize = sizeof(AdlAdapterInfo);
            int bufferBytes = checked(structSize * adapterCount);
            nint infoPtr = Marshal.AllocHGlobal(bufferBytes);
            try
            {
                new Span<byte>((void*)infoPtr, bufferBytes).Clear();
                AdlAdapterInfo* infos = (AdlAdapterInfo*)infoPtr;
                for (int i = 0; i < adapterCount; i++)
                {
                    infos[i].Size = structSize;
                }

                if (_adlAdapterInfoGet(_adlContext, infoPtr, bufferBytes) != 0)
                {
                    CloseAdlLocked(resetAttemptFlag: false);
                    return false;
                }

                var candidates = new List<(int AdapterIndex, int Priority)>(adapterCount);
                for (int i = 0; i < adapterCount; i++)
                {
                    string name = DecodeFixedAsciiString(infos[i].StrAdapterName, 256);
                    bool nameMatch = !string.IsNullOrWhiteSpace(_gpuAdapterName) &&
                        !string.IsNullOrWhiteSpace(name) &&
                        (name.Contains(_gpuAdapterName, StringComparison.OrdinalIgnoreCase) ||
                         _gpuAdapterName.Contains(name, StringComparison.OrdinalIgnoreCase));

                    int priority = (nameMatch ? 4 : 0) + (infos[i].Present != 0 ? 2 : 0) + (infos[i].Exist != 0 ? 1 : 0);
                    candidates.Add((infos[i].AdapterIndex, priority));
                }

                candidates.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                int fallbackIndex = -1;
                foreach (var candidate in candidates)
                {
                    AdlPmLogDataOutput probe = default;
                    probe.Size = sizeof(AdlPmLogDataOutput);
                    if (_adlQueryPmLog(_adlContext, candidate.AdapterIndex, ref probe) == 0)
                    {
                        fallbackIndex = fallbackIndex < 0 ? candidate.AdapterIndex : fallbackIndex;
                        var parsed = ParseAdlPmLogOutput(in probe);
                        if (parsed.TempC.HasValue || parsed.FanRpm.HasValue || parsed.FanPercent.HasValue)
                        {
                            _adlAdapterIndex = candidate.AdapterIndex;
                            return true;
                        }
                    }
                }

                if (fallbackIndex >= 0)
                {
                    _adlAdapterIndex = fallbackIndex;
                    return true;
                }

                CloseAdlLocked(resetAttemptFlag: false);
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }
        catch
        {
            CloseAdlLocked(resetAttemptFlag: false);
            return false;
        }
    }

    private unsafe bool TryReadAdlThermalsLocked(out (double? TempC, int? FanRpm, int? FanPercent) reading)
    {
        reading = default;
        if (!EnsureAdlInitializedLocked() || _adlContext == 0 || _adlAdapterIndex < 0 || _adlQueryPmLog is null)
        {
            return false;
        }

        try
        {
            AdlPmLogDataOutput output = default;
            output.Size = sizeof(AdlPmLogDataOutput);
            if (_adlQueryPmLog(_adlContext, _adlAdapterIndex, ref output) != 0)
            {
                return false;
            }

            reading = ParseAdlPmLogOutput(in output);
            return reading.TempC.HasValue || reading.FanRpm.HasValue || reading.FanPercent.HasValue;
        }
        catch
        {
            return false;
        }
    }

    private void CloseAdlLocked(bool resetAttemptFlag)
    {
        if (_adlContext != 0 && _adlDestroy is not null)
        {
            try { _ = _adlDestroy(_adlContext); } catch { }
        }
        _adlContext = 0;
        _adlAdapterIndex = -1;
        _adlDestroy = null;
        _adlNumAdapters = null;
        _adlAdapterInfoGet = null;
        _adlQueryPmLog = null;
        FreeAdlAllocationsLocked();

        if (_adlLib != 0)
        {
            try { NativeLibrary.Free(_adlLib); } catch { }
            _adlLib = 0;
        }

        if (resetAttemptFlag)
        {
            _adlInitAttempted = false;
        }
    }

    private bool EnsureNvmlInitializedLocked()
    {
        if (_nvmlInitialized && _nvmlDevice != 0 && _nvmlGetTemp is not null)
        {
            return true;
        }

        if (_nvmlInitAttempted) return false;
        _nvmlInitAttempted = true;

        try
        {
            if (!NativeLibrary.TryLoad("nvml.dll", out _nvmlLib) || _nvmlLib == 0)
            {
                string fallbackPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation",
                    "NVSMI",
                    "nvml.dll");
                if (!File.Exists(fallbackPath) || !NativeLibrary.TryLoad(fallbackPath, out _nvmlLib) || _nvmlLib == 0)
                {
                    return false;
                }
            }

            if (!NativeLibrary.TryGetExport(_nvmlLib, "nvmlInit_v2", out nint pInit) ||
                !NativeLibrary.TryGetExport(_nvmlLib, "nvmlShutdown", out nint pShutdown) ||
                !NativeLibrary.TryGetExport(_nvmlLib, "nvmlDeviceGetHandleByIndex_v2", out nint pGetHandle) ||
                !NativeLibrary.TryGetExport(_nvmlLib, "nvmlDeviceGetTemperature", out nint pGetTemp))
            {
                CloseNvmlLocked(resetAttemptFlag: false);
                return false;
            }

            var nvmlInit = Marshal.GetDelegateForFunctionPointer<NvmlInitV2Delegate>(pInit);
            _nvmlShutdown = Marshal.GetDelegateForFunctionPointer<NvmlShutdownDelegate>(pShutdown);
            var getHandle = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetHandleByIndexV2Delegate>(pGetHandle);
            _nvmlGetTemp = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetTemperatureDelegate>(pGetTemp);
            _nvmlGetFanSpeed = NativeLibrary.TryGetExport(_nvmlLib, "nvmlDeviceGetFanSpeed", out nint pGetFan)
                ? Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetFanSpeedDelegate>(pGetFan)
                : null;

            if (nvmlInit() != 0)
            {
                CloseNvmlLocked(resetAttemptFlag: false);
                return false;
            }

            _nvmlInitialized = true;
            if (getHandle(0u, out _nvmlDevice) != 0 || _nvmlDevice == 0)
            {
                CloseNvmlLocked(resetAttemptFlag: false);
                return false;
            }

            return true;
        }
        catch
        {
            CloseNvmlLocked(resetAttemptFlag: false);
            return false;
        }
    }

    private bool TryReadNvmlThermalsLocked(out (double? TempC, int? FanRpm, int? FanPercent) reading)
    {
        reading = default;
        if (!EnsureNvmlInitializedLocked() || !_nvmlInitialized || _nvmlDevice == 0)
        {
            return false;
        }

        try
        {
            const uint NvmlTemperatureGpu = 0u;
            double? tempC = null;
            if (_nvmlGetTemp is not null &&
                _nvmlGetTemp(_nvmlDevice, NvmlTemperatureGpu, out uint temp) == 0 &&
                temp is > 0 and < 200)
            {
                tempC = temp;
            }

            int? fanPercent = null;
            if (_nvmlGetFanSpeed is not null &&
                _nvmlGetFanSpeed(_nvmlDevice, out uint fan) == 0 &&
                fan <= 100)
            {
                fanPercent = (int)fan;
            }

            reading = (tempC, null, fanPercent);
            return tempC.HasValue || fanPercent.HasValue;
        }
        catch
        {
            return false;
        }
    }

    private void CloseNvmlLocked(bool resetAttemptFlag)
    {
        if (_nvmlInitialized && _nvmlShutdown is not null)
        {
            try { _ = _nvmlShutdown(); } catch { }
        }
        _nvmlInitialized = false;
        _nvmlDevice = 0;
        _nvmlShutdown = null;
        _nvmlGetTemp = null;
        _nvmlGetFanSpeed = null;

        if (_nvmlLib != 0)
        {
            try { NativeLibrary.Free(_nvmlLib); } catch { }
            _nvmlLib = 0;
        }

        if (resetAttemptFlag)
        {
            _nvmlInitAttempted = false;
        }
    }

    private void CloseGpuThermalBackendsLocked()
    {
        CloseAdlLocked(resetAttemptFlag: true);
        CloseNvmlLocked(resetAttemptFlag: true);
    }

    private static unsafe string DecodeFixedAsciiString(byte* buffer, int maxLength)
    {
        int len = 0;
        while (len < maxLength && buffer[len] != 0)
        {
            len++;
        }
        return len == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(buffer, len).Trim();
    }

    public static unsafe (double? TempC, int? FanRpm, int? FanPercent) ParseAdlPmLogOutput(in AdlPmLogDataOutput output)
    {
        if (output.Size < 0) return (null, null, null);
        fixed (int* pSensors = output.Sensors)
        {
            return ParseAdlPmLogSensors(new ReadOnlySpan<int>(pSensors, 512));
        }
    }

    public static (double? TempC, int? FanRpm, int? FanPercent) ParseAdlPmLogSensors(ReadOnlySpan<int> sensors)
    {
        static int? ReadSupportedValue(ReadOnlySpan<int> span, int sensorIndex)
        {
            int offset = sensorIndex * 2;
            if (offset < 0 || offset + 1 >= span.Length) return null;
            return span[offset] != 0 ? span[offset + 1] : null;
        }

        int? edgeTemp = ReadSupportedValue(sensors, 8);
        int? hotspotTemp = ReadSupportedValue(sensors, 27);
        double? tempC = edgeTemp is > 0 and < 200
            ? edgeTemp.Value
            : hotspotTemp is > 0 and < 200
                ? hotspotTemp.Value
                : null;

        int? rawFanRpm = ReadSupportedValue(sensors, 14);
        int? fanRpm = rawFanRpm is >= 0 and <= 20000 ? rawFanRpm.Value : null;

        int? rawFanPct = ReadSupportedValue(sensors, 15);
        int? fanPercent = rawFanPct is >= 0 and <= 100 ? rawFanPct.Value : null;

        return (tempC, fanRpm, fanPercent);
    }

    public static DiskVolumeSnapshot? BuildDiskVolumeSnapshot(
        string? name,
        DriveType driveType,
        bool isReady,
        long totalSizeBytes,
        long availableFreeSpaceBytes,
        string? volumeLabel)
    {
        if (driveType != DriveType.Fixed || !isReady || totalSizeBytes <= 0 || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim().TrimEnd('\\', '/').ToUpperInvariant();
        if (trimmed.Length == 0) return null;
        string driveLetter = trimmed.Length == 1 ? $"{trimmed}:" : trimmed;

        long clampedFree = Math.Clamp(availableFreeSpaceBytes, 0L, totalSizeBytes);
        long usedBytes = totalSizeBytes - clampedFree;

        double totalGb = Math.Round(totalSizeBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double usedGb = Math.Round(usedBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double usedPercent = Math.Round(Math.Clamp(usedBytes * 100.0 / totalSizeBytes, 0.0, 100.0), 1);
        string label = string.IsNullOrWhiteSpace(volumeLabel) ? string.Empty : volumeLabel.Trim();

        return new DiskVolumeSnapshot(
            DriveLetter: driveLetter,
            Label: label,
            UsedGb: usedGb,
            TotalGb: totalGb,
            UsedPercent: usedPercent,
            IsRemovable: false);
    }

    private static ImmutableArray<DiskVolumeSnapshot> SampleFixedDiskVolumes()
    {
        try
        {
            var drives = DriveInfo.GetDrives();
            var builder = ImmutableArray.CreateBuilder<DiskVolumeSnapshot>(drives.Length);
            foreach (var drive in drives)
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    var snap = BuildDiskVolumeSnapshot(
                        drive.Name,
                        drive.DriveType,
                        drive.IsReady,
                        drive.TotalSize,
                        drive.AvailableFreeSpace,
                        drive.VolumeLabel);
                    if (snap is not null)
                    {
                        builder.Add(snap);
                    }
                }
                catch
                {
                }
            }

            return builder.ToImmutable();
        }
        catch
        {
            return ImmutableArray<DiskVolumeSnapshot>.Empty;
        }
    }

    public static string NormalizePingHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "1.1.1.1";
        string trimmed = host.Trim();
        return trimmed.Length > 128 ? trimmed[..128] : trimmed;
    }

    private void MaybeTriggerPing(bool publishOnCompletion)
    {
        if (_disposed) return;
        bool pingAllowed = Volatile.Read(ref _extendedMetricsActive) != 0 || Volatile.Read(ref _pingMonitorActive) != 0;
        if (!pingAllowed) return;

        const double MinPingIntervalSec = 1.85;
        const int PingTimeoutMs = 900;

        string targetHost;
        long nowTicks = Stopwatch.GetTimestamp();
        lock (_pingSync)
        {
            long prevTicks = _lastPingAttemptTicks;
            if (prevTicks > 0)
            {
                double elapsedSec = (nowTicks - prevTicks) / (double)Stopwatch.Frequency;
                if (elapsedSec < MinPingIntervalSec) return;
            }

            if (Interlocked.CompareExchange(ref _pingInFlight, 1, 0) != 0) return;

            _lastPingAttemptTicks = nowTicks;
            targetHost = _pingHost;
        }

        _ = Task.Run(async () =>
        {
            double? measuredMs = null;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(targetHost, PingTimeoutMs).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success && reply.RoundtripTime >= 0)
                {
                    measuredMs = reply.RoundtripTime;
                }
            }
            catch
            {
                measuredMs = null;
            }
            finally
            {
                Interlocked.Exchange(ref _pingInFlight, 0);
            }

            if (_disposed) return;
            bool stillAllowed = Volatile.Read(ref _extendedMetricsActive) != 0 || Volatile.Read(ref _pingMonitorActive) != 0;
            if (!stillAllowed) return;

            bool hostStillMatches;
            lock (_pingSync)
            {
                hostStillMatches = string.Equals(_pingHost, targetHost, StringComparison.Ordinal);
                if (hostStillMatches)
                {
                    _lastPingMs = measuredMs;
                }
            }

            if (publishOnCompletion && hostStillMatches)
            {
                PublishPingUpdate(measuredMs, targetHost);
            }
        });
    }

    private void PublishPingUpdate(double? pingMs, string pingHost)
    {
        if (_disposed) return;
        bool stillAllowed = Volatile.Read(ref _extendedMetricsActive) != 0 || Volatile.Read(ref _pingMonitorActive) != 0;
        if (!stillAllowed) return;

        SystemTelemetrySnapshot? updated = null;
        lock (_snapshotSync)
        {
            if (_disposed) return;
            updated = _current with
            {
                PingMs = pingMs,
                PingHost = pingHost
            };
            _current = updated;
        }

        if (updated is not null && !_disposed)
        {
            TelemetryUpdated?.Invoke(this, updated);
        }
    }

    private static string? ReadPrimaryGpuName()
    {
        try
        {
            using var clsKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (clsKey is null) return null;

            string? fallback = null;
            foreach (string subName in clsKey.GetSubKeyNames())
            {
                if (!subName.StartsWith("0", StringComparison.Ordinal)) continue;
                using var sub = clsKey.OpenSubKey(subName);
                string? desc = sub?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc)) continue;

                desc = desc.Trim();
                fallback ??= desc;
                if (IsVirtualDisplayAdapter(desc)) continue;
                return desc;
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsVirtualDisplayAdapter(string desc) =>
        desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("Meta", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("RDP", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("Citrix", StringComparison.OrdinalIgnoreCase) ||
        desc.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    public static bool IsVirtualNetworkAdapter(string? name, string? description)
    {
        static bool Match(string? text) =>
            !string.IsNullOrWhiteSpace(text) &&
            (text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Host-Only", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Kaspersky", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Npcap", StringComparison.OrdinalIgnoreCase));

        return Match(name) || Match(description);
    }

    public static bool ShouldIncludeNetworkAdapter(
        string? name,
        string? description,
        NetworkInterfaceType type,
        OperationalStatus status,
        bool hasPhysicalUpAdapter)
    {
        if (status != OperationalStatus.Up) return false;
        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        if (hasPhysicalUpAdapter && IsVirtualNetworkAdapter(name, description)) return false;
        return true;
    }

    private static (long Rx, long Tx) ReadTotalNetworkBytes()
    {
        long totalRx = 0, totalTx = 0;
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            bool hasPhysicalUp = false;
            foreach (var nic in nics)
            {
                if (nic.OperationalStatus == OperationalStatus.Up &&
                    nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                    !IsVirtualNetworkAdapter(nic.Name, nic.Description))
                {
                    hasPhysicalUp = true;
                    break;
                }
            }

            foreach (var nic in nics)
            {
                if (!ShouldIncludeNetworkAdapter(nic.Name, nic.Description, nic.NetworkInterfaceType, nic.OperationalStatus, hasPhysicalUp))
                    continue;
                var stats = nic.GetIPStatistics();
                totalRx += stats.BytesReceived;
                totalTx += stats.BytesSent;
            }
        }
        catch
        {
            return (0, 0);
        }

        return (totalRx, totalTx);
    }

    private static unsafe (bool HasBattery, int Percent, bool Charging, double? Watts, TimeSpan? Eta) SampleBattery()
    {
        try
        {
            if (NativeMethods.GetSystemPowerStatus(out var pwr) && pwr.BatteryFlag != 128 && pwr.BatteryLifePercent <= 100)
            {
                bool charging = pwr.ACLineStatus == 1;
                double? watts = null;
                uint estSeconds = pwr.BatteryLifeTime > 0 ? (uint)pwr.BatteryLifeTime : 0;

                SYSTEM_BATTERY_STATE sbs = default;
                if (CallNtPowerInformation(SystemBatteryStateInfoLevel, 0, 0, &sbs, (uint)sizeof(SYSTEM_BATTERY_STATE)) == 0
                    && sbs.BatteryPresent != 0)
                {
                    if (sbs.Rate != BatteryUnknownRate && sbs.Rate != 0)
                    {
                        watts = Math.Round(Math.Abs(sbs.Rate) / 1000.0, 2);
                    }
                    if (estSeconds == 0 && sbs.EstimatedTime is > 0 and < uint.MaxValue)
                    {
                        estSeconds = sbs.EstimatedTime;
                    }
                }

                TimeSpan? eta = !charging && estSeconds > 0 ? TimeSpan.FromSeconds(estSeconds) : null;
                return (true, pwr.BatteryLifePercent, charging, watts, eta);
            }
            return (false, 100, true, null, null);
        }
        catch
        {
            return (false, 100, true, null, null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Volatile.Write(ref _samplingActive, 0);
        Volatile.Write(ref _extendedMetricsActive, 0);
        Volatile.Write(ref _pingMonitorActive, 0);

        var cts = Interlocked.Exchange(ref _primeCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        Timer? timer = Interlocked.Exchange(ref _timer, null);
        Timer? pingTimer = Interlocked.Exchange(ref _pingTimer, null);
        lock (_sync)
        {
            if (_disposed)
            {
                timer?.Dispose();
                pingTimer?.Dispose();
                return;
            }
            _disposed = true;
            ClosePdhLocked();
        }

        lock (_gpuHwSync)
        {
            CloseGpuThermalBackendsLocked();
        }

        timer?.Dispose();
        pingTimer?.Dispose();
    }
}
