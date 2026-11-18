using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Audio;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Modules.Media;

/// <summary>
/// Single-MTA dedicated CoreAudio pump managing master volume/mute, mic mute,
/// endpoint switching, grouped per-app sessions, and gated WASAPI loopback FFT.
/// </summary>
public sealed unsafe class CoreAudioHost : IAudioBackend
{
    private const uint CLSCTX_ALL = 0x17;
    private const int eRender = 0;
    private const int eCapture = 1;
    private const int eConsole = 0;
    private const int eMultimedia = 1;
    private const int eCommunications = 2;
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const int STGM_READ = 0;

    private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    private const int AudioSessionStateExpired = 2;
    private const int MaxHfpRetries = 6;
    private const long PolicyCacheTtlMs = 2500;

    private sealed class TrackedSession
    {
        public required string SessionInstanceId;
        public required string GroupKey;
        public required int ProcessId;
        public required bool IsSystemSounds;
        public required string ProcessName;
        public required string DisplayName;
        public required string? IconPath;
        public required IAudioSessionControl Control;
        public required IAudioSessionControl2 Control2;
        public required ISimpleAudioVolume SimpleVolume;
        public IAudioMeterInformation? Meter;
        public SessionEventsSink? EventsSink;
    }

    private sealed class BoundRenderSessionManager
    {
        public required string DeviceId;
        public required IMMDevice Device;
        public required IAudioSessionManager2 Manager;
        public required SessionCreatedSink CreatedSink;
    }

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _mtaThread;
    private readonly double[] _smoothedBands = new double[Radix2Fft.BandCount];
    private readonly float[] _ringMono = new float[Radix2Fft.FftSize];
    private int _ringWritePos;

    private readonly object _routingLock = new();
    private readonly Dictionary<string, string> _appRoutingRules = new(StringComparer.OrdinalIgnoreCase);

    // MTA-thread-only per-PID caches to avoid redundant COM writes/reads
    private readonly Dictionary<int, string?> _appliedRouteTokensByPid = new();
    private readonly Dictionary<int, string?> _failedRouteTokensByPid = new();
    private readonly Dictionary<int, (string? RawEndpoint, long ReadTickMs)> _policyEndpointCache = new();

    private IMMDeviceEnumerator? _enumerator;
    private EndpointNotifySink? _endpointNotifySink;

    private IMMDevice? _renderDevice;
    private IAudioEndpointVolume? _renderVolume;
    private IAudioMeterInformation? _renderMeter;
    private EndpointVolumeSink? _renderVolSink;
    private IAudioSessionManager2? _sessionMgr;
    private SessionCreatedSink? _sessionCreatedSink;
    private readonly List<BoundRenderSessionManager> _secondarySessionManagers = new();
    private readonly List<TrackedSession> _sessions = new();

    private IMMDevice? _captureDevice;
    private IAudioEndpointVolume? _captureVolume;
    private EndpointVolumeSink? _captureVolSink;

    private IAudioClient? _loopbackClient;
    private IAudioCaptureClient? _loopbackCapture;
    private nint _loopbackEventHandle;
    private int _loopbackChannels;
    private bool _loopbackIsFloat;
    private int _loopbackBitsPerSample;
    private long _lastSignalTickMs;

    private volatile bool _disposed;
    private volatile bool _spectrumRequested;
    private bool _isHfpFallback;
    private int _hfpRetryAttempts;
    private long _hfpNextRetryTickMs;
    private int _rebindNeeded;
    private long _lastSpectrumTickMs;
    private string? _outputFormatLabel;
    private ImmutableArray<AudioFormatOption> _supportedOutputFormats = ImmutableArray<AudioFormatOption>.Empty;
    private AudioMixerSnapshot _current = EmptySnapshot();

    // Unique context stamped on our own volume/mute COM writes so echo callbacks don't cause lag/jitter
    private readonly Guid _selfEventContext = Guid.NewGuid();
    private int _pendingMasterVolBits = -1;
    private int _pendingMicVolBits = -1;
    private readonly ConcurrentDictionary<string, float> _pendingAppVolumes = new(StringComparer.OrdinalIgnoreCase);
    private int _volumeFlushQueued;

    public event EventHandler<AudioMixerSnapshot>? SnapshotChanged;
    public event EventHandler<double[]>? SpectrumUpdated;
    public event EventHandler<string>? AudioActionFailed;
    public AudioMixerSnapshot Current => _current;

    public CoreAudioHost()
    {
        _mtaThread = new Thread(RunMtaLoop)
        {
            Name = "DinamikAda.CoreAudioMTA",
            IsBackground = true
        };
        _mtaThread.SetApartmentState(ApartmentState.MTA);
        _mtaThread.Start();
    }

    public static AudioMixerSnapshot EmptySnapshot() => new(
        MasterVolume01: 0.5f,
        IsMasterMuted: false,
        DefaultOutputId: string.Empty,
        DefaultOutputName: "Default Audio",
        MicVolume01: 1.0f,
        IsMicMuted: false,
        HasMicDevice: false,
        IsLoopbackActive: false,
        IsBluetoothHfpFallback: false,
        SpectrumBands: ImmutableArray.Create(new double[Radix2Fft.BandCount]),
        AppSessions: ImmutableArray<AudioAppSessionSnapshot>.Empty,
        OutputDevices: ImmutableArray<AudioEndpointSnapshot>.Empty,
        InputDevices: ImmutableArray<AudioEndpointSnapshot>.Empty,
        OutputFormatLabel: null,
        SupportedOutputFormats: ImmutableArray<AudioFormatOption>.Empty);

    public void SetSpectrumRequested(bool requested)
    {
        _spectrumRequested = requested;
        Enqueue(() =>
        {
            bool wasActive = _loopbackClient is not null;
            bool wasHfp = _isHfpFallback;
            if (requested)
            {
                _lastSignalTickMs = Environment.TickCount64;
                // A user re-enabling the spectrum is an explicit retry: reset the exhausted backoff so a
                // transient init failure does not leave the visualizer stuck in fallback forever.
                if (_isHfpFallback && _hfpRetryAttempts > MaxHfpRetries)
                {
                    _hfpRetryAttempts = 0;
                    _hfpNextRetryTickMs = 0;
                }
            }
            EnsureLoopbackStateOnMta();
            if ((_loopbackClient is not null) != wasActive || _isHfpFallback != wasHfp)
            {
                PublishSnapshotOnMta();
            }
        });
    }

    public void SetMasterVolume(float volume01)
    {
        float clamped = Math.Clamp(volume01, 0f, 1f);
        Interlocked.Exchange(ref _pendingMasterVolBits, BitConverter.SingleToInt32Bits(clamped));
        ScheduleVolumeFlush();
    }

    public void SetMasterMute(bool muted)
    {
        Enqueue(() =>
        {
            if (_renderVolume is null) return;
            Guid ctx = _selfEventContext;
            _renderVolume.SetMute(muted, ref ctx);
            PublishSnapshotOnMta();
        });
    }

    public void SetMicVolume(float volume01)
    {
        float clamped = Math.Clamp(volume01, 0f, 1f);
        Interlocked.Exchange(ref _pendingMicVolBits, BitConverter.SingleToInt32Bits(clamped));
        ScheduleVolumeFlush();
    }

    public void SetMicMute(bool muted)
    {
        Enqueue(() =>
        {
            if (_captureVolume is null) return;
            Guid ctx = _selfEventContext;
            int hr = _captureVolume.SetMute(muted, ref ctx);
            if (hr < 0)
            {
                try { AudioActionFailed?.Invoke(this, $"Mic mute failed (0x{hr:X8})"); } catch { }
            }
            PublishSnapshotOnMta();
        });
    }

    public void SetAppVolume(string groupKey, float volume01)
    {
        if (string.IsNullOrWhiteSpace(groupKey)) return;
        float clamped = Math.Clamp(volume01, 0f, 1f);
        _pendingAppVolumes[groupKey] = clamped;
        ScheduleVolumeFlush();
    }

    public void SetAppMute(string groupKey, bool muted)
    {
        Enqueue(() =>
        {
            Guid ctx = _selfEventContext;
            foreach (var s in _sessions.Where(x => string.Equals(x.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase)))
            {
                s.SimpleVolume.SetMute(muted, ref ctx);
            }
            PublishSnapshotOnMta();
        });
    }

    private void ScheduleVolumeFlush()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _volumeFlushQueued, 1, 0) == 0)
        {
            if (!Enqueue(FlushPendingVolumesOnMta))
            {
                Interlocked.Exchange(ref _volumeFlushQueued, 0);
            }
        }
    }

    private void FlushPendingVolumesOnMta()
    {
        Interlocked.Exchange(ref _volumeFlushQueued, 0);
        if (_disposed) return;

        Guid ctx = _selfEventContext;
        bool anyWritten = false;
        float? updatedMaster = null;
        float? updatedMic = null;
        Dictionary<string, float>? updatedApps = null;

        int masterBits = Interlocked.Exchange(ref _pendingMasterVolBits, -1);
        if (masterBits >= 0 && _renderVolume is not null)
        {
            float val = Math.Clamp(BitConverter.Int32BitsToSingle(masterBits), 0f, 1f);
            _renderVolume.SetMasterVolumeLevelScalar(val, ref ctx);
            updatedMaster = val;
            anyWritten = true;
        }

        int micBits = Interlocked.Exchange(ref _pendingMicVolBits, -1);
        if (micBits >= 0 && _captureVolume is not null)
        {
            float val = Math.Clamp(BitConverter.Int32BitsToSingle(micBits), 0f, 1f);
            _captureVolume.SetMasterVolumeLevelScalar(val, ref ctx);
            updatedMic = val;
            anyWritten = true;
        }

        if (!_pendingAppVolumes.IsEmpty)
        {
            foreach (string key in _pendingAppVolumes.Keys.ToArray())
            {
                if (!_pendingAppVolumes.TryRemove(key, out float val)) continue;
                float clamped = Math.Clamp(val, 0f, 1f);
                foreach (var s in _sessions)
                {
                    if (string.Equals(s.GroupKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        s.SimpleVolume.SetMasterVolume(clamped, ref ctx);
                    }
                }
                updatedApps ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                updatedApps[key] = clamped;
                anyWritten = true;
            }
        }

        if (anyWritten)
        {
            PublishFastVolumeSnapshotOnMta(updatedMaster, updatedMic, updatedApps);
        }
    }

    private void PublishFastVolumeSnapshotOnMta(
        float? updatedMaster,
        float? updatedMic,
        Dictionary<string, float>? updatedApps)
    {
        var cur = _current;
        var nextSessions = cur.AppSessions;
        if (updatedApps is { Count: > 0 } && !cur.AppSessions.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<AudioAppSessionSnapshot>(cur.AppSessions.Length);
            for (int i = 0; i < cur.AppSessions.Length; i++)
            {
                var item = cur.AppSessions[i];
                if (updatedApps.TryGetValue(item.GroupKey, out float newVol))
                {
                    item = item with { Volume01 = newVol };
                }
                builder.Add(item);
            }
            nextSessions = builder.MoveToImmutable();
        }

        var next = cur with
        {
            MasterVolume01 = updatedMaster ?? cur.MasterVolume01,
            MicVolume01 = updatedMic ?? cur.MicVolume01,
            AppSessions = nextSessions
        };

        if (AreSnapshotsEquivalent(cur, next)) return;
        _current = next;
        SnapshotChanged?.Invoke(this, next);
    }

    internal static bool ShouldIgnoreEventContext(Guid incomingContext, Guid selfContext) =>
        selfContext != Guid.Empty && incomingContext == selfContext;

    public void SetAppOutputDevice(string processName, string? deviceId)
    {
        if (_disposed) return;

        string normalizedProc = AudioSessionGrouper.NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(normalizedProc) ||
            string.Equals(normalizedProc, "System Sounds", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? cleanDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();

        lock (_routingLock)
        {
            if (cleanDeviceId is null)
            {
                _appRoutingRules.Remove(normalizedProc);
            }
            else
            {
                _appRoutingRules[normalizedProc] = cleanDeviceId;
            }
        }

        Enqueue(() =>
        {
            if (_disposed) return;
            PruneDisconnectedSessionsOnMta();

            string? currentTarget;
            lock (_routingLock)
            {
                _appRoutingRules.TryGetValue(normalizedProc, out currentTarget);
            }

            if (currentTarget is not null)
            {
                ApplyRouteToProcessSessionsOnMta(normalizedProc, currentTarget, forceWrite: true);
            }
            else if (cleanDeviceId is null)
            {
                ApplyRouteToProcessSessionsOnMta(normalizedProc, null, forceWrite: true);
            }

            PublishSnapshotOnMta();
        });
    }

    public void SyncAppRoutingRules(IReadOnlyDictionary<string, string> routes)
    {
        if (_disposed || routes is null) return;

        var normalized = NormalizeRoutingRules(routes);
        List<string> removedProcesses;
        List<string> changedOrAddedProcesses;

        lock (_routingLock)
        {
            removedProcesses = _appRoutingRules.Keys
                .Where(k => !normalized.ContainsKey(k))
                .ToList();

            changedOrAddedProcesses = normalized
                .Where(kv => !_appRoutingRules.TryGetValue(kv.Key, out string? existing) ||
                             !string.Equals(existing, kv.Value, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();

            _appRoutingRules.Clear();
            foreach (var kv in normalized)
            {
                _appRoutingRules[kv.Key] = kv.Value;
            }
        }

        Enqueue(() =>
        {
            if (_disposed) return;
            PruneDisconnectedSessionsOnMta();

            foreach (string removed in removedProcesses)
            {
                bool stillRemoved;
                lock (_routingLock)
                {
                    stillRemoved = !_appRoutingRules.ContainsKey(removed);
                }

                if (stillRemoved)
                {
                    ApplyRouteToProcessSessionsOnMta(removed, null, forceWrite: true);
                }
            }

            var currentRules = CopyRoutingRulesThreadSafe();
            var forceSet = new HashSet<string>(changedOrAddedProcesses, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in currentRules)
            {
                ApplyRouteToProcessSessionsOnMta(kv.Key, kv.Value, forceWrite: forceSet.Contains(kv.Key));
            }

            PublishSnapshotOnMta();
        });
    }

    public IReadOnlyDictionary<string, string> GetAppRoutingRulesSnapshot() =>
        CopyRoutingRulesThreadSafe();

    public static Dictionary<string, string> NormalizeRoutingRules(IReadOnlyDictionary<string, string>? routes)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (routes is null) return normalized;

        foreach (var kv in routes)
        {
            string key = AudioSessionGrouper.NormalizeProcessName(kv.Key);
            if (string.IsNullOrEmpty(key) ||
                string.Equals(key, "System Sounds", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(kv.Value))
            {
                continue;
            }

            normalized[key] = kv.Value.Trim();
        }

        return normalized;
    }

    private Dictionary<string, string> CopyRoutingRulesThreadSafe()
    {
        lock (_routingLock)
        {
            return new Dictionary<string, string>(_appRoutingRules, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetDefaultOutputDevice(string deviceId) => SetDefaultDeviceInternal(deviceId);
    public void SetDefaultInputDevice(string deviceId) => SetDefaultDeviceInternal(deviceId);

    public Task<bool> SetDefaultOutputDeviceAsync(string deviceId, CancellationToken ct = default) =>
        SetDefaultDeviceAsync(deviceId, ct);

    public Task<bool> SetDefaultInputDeviceAsync(string deviceId, CancellationToken ct = default) =>
        SetDefaultDeviceAsync(deviceId, ct);

    public Task<bool> SetDefaultOutputFormatAsync(int sampleRateHz, ushort bitsPerSample, bool isFloat, CancellationToken ct = default)
    {
        if (_disposed || ct.IsCancellationRequested || sampleRateHz < 8000 || bitsPerSample is not (16 or 24 or 32))
        {
            return Task.FromResult(false);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = Enqueue(() =>
        {
            if (_disposed)
            {
                tcs.TrySetResult(false);
                return;
            }
            bool ok = ApplyDefaultOutputFormatOnMta(sampleRateHz, bitsPerSample, isFloat);
            tcs.TrySetResult(ok);
        });

        if (!queued)
        {
            tcs.TrySetResult(false);
        }
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    private Task<bool> SetDefaultDeviceAsync(string deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || _disposed || ct.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetDefaultDeviceInternal(deviceId, tcs);
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    private void SetDefaultDeviceInternal(string deviceId, TaskCompletionSource<bool>? tcs = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            tcs?.TrySetResult(false);
            return;
        }

        bool queued = Enqueue(() =>
        {
            object? pcObj = null;
            bool succeeded = false;
            string? failureReason = null;
            try
            {
                Type? pcType = Type.GetTypeFromCLSID(CoreAudioGuids.CLSID_PolicyConfigClient);
                if (pcType is null)
                {
                    failureReason = "PolicyConfig CLSID unavailable";
                    return;
                }

                pcObj = Activator.CreateInstance(pcType);
                if (pcObj is IPolicyConfig policy)
                {
                    int hrConsole = policy.SetDefaultEndpoint(deviceId, eConsole);
                    int hrMedia = policy.SetDefaultEndpoint(deviceId, eMultimedia);
                    int hrComm = policy.SetDefaultEndpoint(deviceId, eCommunications);

                    if (hrConsole >= 0 && hrMedia >= 0 && hrComm >= 0)
                    {
                        succeeded = true;
                    }
                    else
                    {
                        int firstErr = hrConsole < 0 ? hrConsole : (hrMedia < 0 ? hrMedia : hrComm);
                        failureReason = $"SetDefaultEndpoint HRESULT 0x{firstErr:X8}";
                    }
                }
                else
                {
                    failureReason = "IPolicyConfig interface query failed";
                }
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
            }
            finally
            {
                if (pcObj is not null && Marshal.IsComObject(pcObj))
                {
                    Marshal.ReleaseComObject(pcObj);
                }

                Interlocked.Exchange(ref _rebindNeeded, 1);
                if (!succeeded && !string.IsNullOrWhiteSpace(failureReason))
                {
                    try { AudioActionFailed?.Invoke(this, failureReason); } catch { }
                }
                tcs?.TrySetResult(succeeded);
            }
        });

        if (!queued)
        {
            tcs?.TrySetResult(false);
        }
    }

    private bool Enqueue(Action action)
    {
        if (_disposed) return false;
        try
        {
            _queue.Add(action);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RunMtaLoop()
    {
        int coInit = NativeMethods.CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
        try
        {
            InitializeEnumeratorOnMta();
            BindDevicesOnMta();
            PublishSnapshotOnMta(force: true);

            while (!_disposed)
            {
                if (Interlocked.Exchange(ref _rebindNeeded, 0) == 1)
                {
                    TeardownLoopbackOnMta();
                    TeardownBoundDevicesOnMta();
                    _isHfpFallback = false;
                    _hfpRetryAttempts = 0;
                    _hfpNextRetryTickMs = 0;
                    _failedRouteTokensByPid.Clear();
                    _policyEndpointCache.Clear();
                    BindDevicesOnMta();
                    PublishSnapshotOnMta();
                }

                if (_loopbackClient is not null && _loopbackEventHandle != 0)
                {
                    while (_queue.TryTake(out var fastAction))
                    {
                        ExecuteSafe(fastAction);
                    }

                    uint wait = NativeMethods.WaitForSingleObject(_loopbackEventHandle, 20);
                    if (wait == 0 /* WAIT_OBJECT_0 */)
                    {
                        PumpLoopbackOrPeakOnMta();
                    }

                    long now = Environment.TickCount64;
                    if (now - _lastSpectrumTickMs >= 33)
                    {
                        EmitSpectrumUpdatedOnMta();
                        _lastSpectrumTickMs = now;
                    }

                    if (!_spectrumRequested || now - _lastSignalTickMs > 1800)
                    {
                        bool wasActive = _loopbackClient is not null;
                        EnsureLoopbackStateOnMta();
                        if (wasActive && _loopbackClient is null)
                        {
                            EmitSpectrumUpdatedOnMta();
                            PublishSnapshotOnMta();
                        }
                    }
                }
                else if (_spectrumRequested && _isHfpFallback)
                {
                    while (_queue.TryTake(out var fastAction))
                    {
                        ExecuteSafe(fastAction);
                    }
                    Thread.Sleep(30);
                    PumpPeakFallbackBandsOnMta();
                    EmitSpectrumUpdatedOnMta();

                    long now = Environment.TickCount64;
                    if (_hfpRetryAttempts <= MaxHfpRetries && now >= _hfpNextRetryTickMs)
                    {
                        EnsureLoopbackStateOnMta();
                        if (!_isHfpFallback && _loopbackClient is not null)
                        {
                            PublishSnapshotOnMta();
                        }
                    }
                }
                else
                {
                    if (_queue.TryTake(out var action, 250))
                    {
                        ExecuteSafe(action);
                        while (_queue.TryTake(out var queuedAction))
                        {
                            ExecuteSafe(queuedAction);
                        }
                    }
                    else if (_spectrumRequested)
                    {
                        bool wasActive = _loopbackClient is not null;
                        bool wasHfp = _isHfpFallback;
                        EnsureLoopbackStateOnMta();
                        if ((_loopbackClient is not null) != wasActive || _isHfpFallback != wasHfp)
                        {
                            PublishSnapshotOnMta();
                        }
                    }
                }
            }
        }
        finally
        {
            TeardownLoopbackOnMta();
            TeardownBoundDevicesOnMta();
            TeardownEnumeratorOnMta();
            if (coInit >= 0) NativeMethods.CoUninitialize();
        }
    }

    private static void ExecuteSafe(Action action)
    {
        try { action(); } catch { }
    }

    private void InitializeEnumeratorOnMta()
    {
        try
        {
            Type? enumType = Type.GetTypeFromCLSID(CoreAudioGuids.CLSID_MMDeviceEnumerator);
            if (enumType is null) return;
            _enumerator = Activator.CreateInstance(enumType) as IMMDeviceEnumerator;
            if (_enumerator is not null)
            {
                _endpointNotifySink = new EndpointNotifySink(new WeakReference<CoreAudioHost>(this));
                _enumerator.RegisterEndpointNotificationCallback(_endpointNotifySink);
            }
        }
        catch
        {
            _enumerator = null;
        }
    }

    private void BindDevicesOnMta()
    {
        if (_enumerator is null) return;

        string? defaultRenderId = null;

        // Render endpoint
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev) == 0 && dev is not null)
            {
                _renderDevice = dev;
                dev.GetId(out defaultRenderId);

                Guid volIid = CoreAudioGuids.IID_IAudioEndpointVolume;
                if (dev.Activate(ref volIid, CLSCTX_ALL, 0, out object volObj) == 0 && volObj is IAudioEndpointVolume vol)
                {
                    _renderVolume = vol;
                    _renderVolSink = new EndpointVolumeSink(new WeakReference<CoreAudioHost>(this), isCapture: false);
                    _renderVolume.RegisterControlChangeNotify(_renderVolSink);
                }

                Guid meterIid = CoreAudioGuids.IID_IAudioMeterInformation;
                if (dev.Activate(ref meterIid, CLSCTX_ALL, 0, out object meterObj) == 0 && meterObj is IAudioMeterInformation meter)
                {
                    _renderMeter = meter;
                }

                Guid mgrIid = CoreAudioGuids.IID_IAudioSessionManager2;
                if (dev.Activate(ref mgrIid, CLSCTX_ALL, 0, out object mgrObj) == 0 && mgrObj is IAudioSessionManager2 mgr)
                {
                    _sessionMgr = mgr;
                    _sessionCreatedSink = new SessionCreatedSink(new WeakReference<CoreAudioHost>(this), isDefaultRender: true);
                    _sessionMgr.RegisterSessionNotification(_sessionCreatedSink);
                }

                (_outputFormatLabel, _supportedOutputFormats) = ReadOutputFormatAndSupportedOnMta(dev);
            }
        }
        catch { }

        // Secondary active render endpoints so per-app routed sessions remain visible & controllable
        BindSecondaryRenderSessionManagersOnMta(defaultRenderId);

        // Capture endpoint (prefer eCommunications, fall back to eMultimedia / eConsole)
        try
        {
            IMMDevice? capDev = TryGetDefaultCaptureEndpointOnMta();
            if (capDev is not null)
            {
                _captureDevice = capDev;
                Guid volIid = CoreAudioGuids.IID_IAudioEndpointVolume;
                if (capDev.Activate(ref volIid, CLSCTX_ALL, 0, out object capVolObj) == 0 && capVolObj is IAudioEndpointVolume capVol)
                {
                    _captureVolume = capVol;
                    _captureVolSink = new EndpointVolumeSink(new WeakReference<CoreAudioHost>(this), isCapture: true);
                    _captureVolume.RegisterControlChangeNotify(_captureVolSink);
                }
            }
        }
        catch { }

        EnumerateSessionsOnMta();
        EnsureLoopbackStateOnMta();
    }

    private IMMDevice? TryGetDefaultCaptureEndpointOnMta()
    {
        if (_enumerator is null) return null;
        foreach (int role in new[] { eCommunications, eMultimedia, eConsole })
        {
            if (_enumerator.GetDefaultAudioEndpoint(eCapture, role, out var capDev) == 0 && capDev is not null)
            {
                return capDev;
            }
            ReleaseComIfNeeded(capDev);
        }
        return null;
    }

    private void BindSecondaryRenderSessionManagersOnMta(string? defaultRenderId)
    {
        if (_enumerator is null) return;
        if (_enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var coll) != 0 || coll is null)
            return;

        try
        {
            coll.GetCount(out uint count);
            Guid mgrIid = CoreAudioGuids.IID_IAudioSessionManager2;
            for (uint i = 0; i < count; i++)
            {
                if (coll.Item(i, out var dev) != 0 || dev is null) continue;
                bool keepDevice = false;
                try
                {
                    dev.GetId(out string devId);
                    if (!string.IsNullOrWhiteSpace(defaultRenderId) &&
                        string.Equals(devId, defaultRenderId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (dev.Activate(ref mgrIid, CLSCTX_ALL, 0, out object mgrObj) == 0 &&
                        mgrObj is IAudioSessionManager2 mgr)
                    {
                        var sink = new SessionCreatedSink(new WeakReference<CoreAudioHost>(this), isDefaultRender: false);
                        if (mgr.RegisterSessionNotification(sink) == 0)
                        {
                            _secondarySessionManagers.Add(new BoundRenderSessionManager
                            {
                                DeviceId = devId,
                                Device = dev,
                                Manager = mgr,
                                CreatedSink = sink
                            });
                            keepDevice = true;
                        }
                        else
                        {
                            ReleaseComIfNeeded(mgr);
                        }
                    }
                    else if (mgrObj is not null)
                    {
                        ReleaseComIfNeeded(mgrObj);
                    }
                }
                catch
                {
                    // Ignore transient activation errors on secondary endpoints
                }
                finally
                {
                    if (!keepDevice)
                    {
                        ReleaseComIfNeeded(dev);
                    }
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(coll)) Marshal.ReleaseComObject(coll);
        }
    }

    private void EnumerateSessionsOnMta()
    {
        ClearTrackedSessionsOnMta();
        EnumerateSessionManagerOnMta(_sessionMgr, allowSystemSounds: true);
        foreach (var secondary in _secondarySessionManagers)
        {
            EnumerateSessionManagerOnMta(secondary.Manager, allowSystemSounds: false);
        }
        PruneStalePidTrackingOnMta();
        ApplyConfiguredRoutesToSessionsOnMta();
    }

    private void EnumerateSessionManagerOnMta(IAudioSessionManager2? mgr, bool allowSystemSounds)
    {
        if (mgr is null) return;
        if (mgr.GetSessionEnumerator(out var enumerator) != 0 || enumerator is null) return;

        try
        {
            if (enumerator.GetCount(out int count) != 0) return;
            for (int i = 0; i < count; i++)
            {
                if (enumerator.GetSession(i, out var ctrl) != 0 || ctrl is null) continue;
                TrackSingleSessionOnMta(ctrl, allowSystemSounds);
            }
        }
        finally
        {
            if (Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
        }
    }

    private TrackedSession? TrackSingleSessionOnMta(IAudioSessionControl ctrl, bool allowSystemSounds = true)
    {
        if (_disposed)
        {
            ReleaseSessionComObjects(ctrl, null, null, null);
            return null;
        }

        if (ctrl is not IAudioSessionControl2 ctrl2 || ctrl is not ISimpleAudioVolume simpleVol)
        {
            ReleaseSessionComObjects(ctrl, null, null, null);
            return null;
        }

        if (ctrl.GetState(out int state) == 0 && state == AudioSessionStateExpired)
        {
            ReleaseSessionComObjects(ctrl, ctrl2, simpleVol, null);
            return null;
        }

        ctrl2.GetProcessId(out uint pid);
        bool isSys = ctrl2.IsSystemSoundsSession() == 0;
        if ((pid == 0 && !isSys) || (isSys && !allowSystemSounds))
        {
            ReleaseSessionComObjects(ctrl, ctrl2, simpleVol, null);
            return null;
        }

        string procName = isSys ? "System Sounds" : ResolveProcessName((int)pid);
        ctrl2.GetDisplayName(out string rawDisplay);
        ctrl2.GetIconPath(out string rawIcon);
        ctrl2.GetGroupingParam(out Guid groupGuid);
        string sessionInstId = ctrl2.GetSessionInstanceIdentifier(out string rawInstId) == 0 && !string.IsNullOrWhiteSpace(rawInstId)
            ? rawInstId
            : $"{pid}:{(groupGuid != Guid.Empty ? groupGuid.ToString("N") : procName)}";

        foreach (var existing in _sessions)
        {
            if (ReferenceEquals(existing.Control, ctrl) ||
                string.Equals(existing.SessionInstanceId, sessionInstId, StringComparison.OrdinalIgnoreCase))
            {
                if (!ReferenceEquals(existing.Control, ctrl))
                {
                    ReleaseSessionComObjects(ctrl, ctrl2, simpleVol, null);
                }
                return null;
            }
        }

        string displayName = ResolveIndirectString(rawDisplay);
        if (string.IsNullOrWhiteSpace(displayName)) displayName = procName;
        string? iconPath = ResolveIndirectString(rawIcon);
        string groupKey = isSys
            ? "sys:sounds"
            : (groupGuid != Guid.Empty ? $"grp:{groupGuid:N}:{procName}" : $"proc:{procName}");

        IAudioMeterInformation? meter = ctrl as IAudioMeterInformation;
        var sink = new SessionEventsSink(new WeakReference<CoreAudioHost>(this));
        ctrl.RegisterAudioSessionNotification(sink);

        var tracked = new TrackedSession
        {
            SessionInstanceId = sessionInstId,
            GroupKey = groupKey,
            ProcessId = (int)pid,
            IsSystemSounds = isSys,
            ProcessName = procName,
            DisplayName = displayName,
            IconPath = string.IsNullOrWhiteSpace(iconPath) ? null : iconPath,
            Control = ctrl,
            Control2 = ctrl2,
            SimpleVolume = simpleVol,
            Meter = meter,
            EventsSink = sink
        };
        _sessions.Add(tracked);
        return tracked;
    }

    private void PruneDisconnectedSessionsOnMta()
    {
        bool removedAny = false;
        for (int i = _sessions.Count - 1; i >= 0; i--)
        {
            var s = _sessions[i];
            int hr = -1;
            int state = AudioSessionStateExpired;
            try { hr = s.Control.GetState(out state); } catch { }
            if (hr != 0 || state == AudioSessionStateExpired)
            {
                ReleaseTrackedSessionOnMta(s);
                _sessions.RemoveAt(i);
                removedAny = true;
            }
        }

        if (removedAny)
        {
            PruneStalePidTrackingOnMta();
        }
    }

    private void ApplyConfiguredRoutesToSessionsOnMta()
    {
        var rules = CopyRoutingRulesThreadSafe();
        if (rules.Count == 0 || _sessions.Count == 0) return;

        HashSet<int>? visitedPids = null;
        foreach (var s in _sessions)
        {
            if (s.IsSystemSounds || s.ProcessId <= 0) continue;
            if (!rules.TryGetValue(s.ProcessName, out string? targetDeviceId)) continue;

            visitedPids ??= new HashSet<int>();
            if (!visitedPids.Add(s.ProcessId)) continue;

            ApplyRouteToPidOnMta(s.ProcessId, targetDeviceId, forceWrite: false);
        }
    }

    private void ApplyConfiguredRouteForSessionOnMta(TrackedSession session)
    {
        if (session.IsSystemSounds || session.ProcessId <= 0) return;

        string? targetDeviceId;
        lock (_routingLock)
        {
            if (!_appRoutingRules.TryGetValue(session.ProcessName, out targetDeviceId))
            {
                return;
            }
        }

        ApplyRouteToPidOnMta(session.ProcessId, targetDeviceId, forceWrite: false);
    }

    private void ApplyRouteToProcessSessionsOnMta(string normalizedProcessName, string? targetDeviceId, bool forceWrite)
    {
        HashSet<int>? pids = null;
        foreach (var s in _sessions)
        {
            if (s.IsSystemSounds || s.ProcessId <= 0) continue;
            if (string.Equals(s.ProcessName, normalizedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                pids ??= new HashSet<int>();
                pids.Add(s.ProcessId);
            }
        }

        if (pids is null) return;
        foreach (int pid in pids)
        {
            ApplyRouteToPidOnMta(pid, targetDeviceId, forceWrite);
        }
    }

    private void ApplyRouteToPidOnMta(int pid, string? targetDeviceId, bool forceWrite)
    {
        if (pid <= 0) return;

        string? targetToken = string.IsNullOrWhiteSpace(targetDeviceId)
            ? null
            : (AudioPolicyConfigClient.ExtractEndpointGuidToken(targetDeviceId) ?? targetDeviceId.Trim());

        if (!forceWrite)
        {
            if (_appliedRouteTokensByPid.TryGetValue(pid, out string? appliedToken) &&
                string.Equals(appliedToken, targetToken, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_failedRouteTokensByPid.TryGetValue(pid, out string? failedToken) &&
                string.Equals(failedToken, targetToken, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? existingEndpoint = GetCachedOrQueryProcessOutputEndpointOnMta(pid);
            string? existingToken = string.IsNullOrWhiteSpace(existingEndpoint)
                ? null
                : (AudioPolicyConfigClient.ExtractEndpointGuidToken(existingEndpoint) ?? existingEndpoint.Trim());
            if (string.Equals(existingToken, targetToken, StringComparison.OrdinalIgnoreCase))
            {
                _appliedRouteTokensByPid[pid] = targetToken;
                _failedRouteTokensByPid.Remove(pid);
                return;
            }
        }

        if (AudioPolicyConfigClient.TrySetProcessOutputEndpoint(pid, targetDeviceId, out string? error))
        {
            if (targetToken is null)
            {
                _appliedRouteTokensByPid.Remove(pid);
            }
            else
            {
                _appliedRouteTokensByPid[pid] = targetToken;
            }
            _failedRouteTokensByPid.Remove(pid);
            _policyEndpointCache.Remove(pid);
            _ = GetCachedOrQueryProcessOutputEndpointOnMta(pid);
        }
        else
        {
            _failedRouteTokensByPid[pid] = targetToken;
            _policyEndpointCache.Remove(pid);
            if (forceWrite && !string.IsNullOrWhiteSpace(error))
            {
                try { AudioActionFailed?.Invoke(this, error); } catch { }
            }
        }
    }

    private string? GetCachedOrQueryProcessOutputEndpointOnMta(int pid)
    {
        if (pid <= 0) return null;
        long now = Environment.TickCount64;
        if (_policyEndpointCache.TryGetValue(pid, out var cached) &&
            now - cached.ReadTickMs < PolicyCacheTtlMs)
        {
            return cached.RawEndpoint;
        }

        string? raw = AudioPolicyConfigClient.TryGetProcessOutputEndpoint(pid);
        _policyEndpointCache[pid] = (raw, now);
        return raw;
    }

    private void PruneStalePidTrackingOnMta()
    {
        if (_appliedRouteTokensByPid.Count == 0 &&
            _failedRouteTokensByPid.Count == 0 &&
            _policyEndpointCache.Count == 0)
        {
            return;
        }

        var activePids = new HashSet<int>(_sessions.Count);
        foreach (var s in _sessions)
        {
            if (s.ProcessId > 0) activePids.Add(s.ProcessId);
        }

        PruneDictionaryByKeys(_appliedRouteTokensByPid, activePids);
        PruneDictionaryByKeys(_failedRouteTokensByPid, activePids);
        PruneDictionaryByKeys(_policyEndpointCache, activePids);
    }

    private static void PruneDictionaryByKeys<TValue>(Dictionary<int, TValue> dict, HashSet<int> keepKeys)
    {
        if (dict.Count == 0) return;
        List<int>? toRemove = null;
        foreach (int key in dict.Keys)
        {
            if (!keepKeys.Contains(key))
            {
                toRemove ??= new List<int>();
                toRemove.Add(key);
            }
        }
        if (toRemove is null) return;
        foreach (int key in toRemove) dict.Remove(key);
    }

    private void EnterHfpFallbackOnMta()
    {
        _isHfpFallback = true;
        _hfpRetryAttempts = Math.Min(_hfpRetryAttempts + 1, MaxHfpRetries + 1);
        long backoffMs = Math.Min(30_000L, 1_500L << Math.Min(_hfpRetryAttempts - 1, 5));
        _hfpNextRetryTickMs = Environment.TickCount64 + backoffMs;
    }

    private void EnsureLoopbackStateOnMta()
    {
        if (!_spectrumRequested)
        {
            TeardownLoopbackOnMta();
            return;
        }

        float peak = 0f;
        if (_renderMeter is not null)
        {
            _renderMeter.GetPeakValue(out peak);
        }

        if (peak > 0.001f)
        {
            _lastSignalTickMs = Environment.TickCount64;
        }

        if (_loopbackClient is not null)
        {
            if (Environment.TickCount64 - _lastSignalTickMs > 1800)
            {
                TeardownLoopbackOnMta();
            }
            return;
        }

        if (peak <= 0.001f && Environment.TickCount64 - _lastSignalTickMs > 1800)
        {
            return;
        }

        if (_renderDevice is null) return;

        nint fmtPtr = 0;
        IAudioClient? client = null;
        try
        {
            Guid clientIid = CoreAudioGuids.IID_IAudioClient;
            if (_renderDevice.Activate(ref clientIid, CLSCTX_ALL, 0, out object clientObj) != 0 || clientObj is not IAudioClient activatedClient)
            {
                EnterHfpFallbackOnMta();
                return;
            }
            client = activatedClient;

            if (client.GetMixFormat(out fmtPtr) != 0 || fmtPtr == 0)
            {
                ReleaseComIfNeeded(client);
                EnterHfpFallbackOnMta();
                return;
            }

            var wfx = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
            if (wfx.nSamplesPerSec <= 16000)
            {
                // Bluetooth Hands-Free telephony profile: avoid opening loopback stream on HFP
                ReleaseComIfNeeded(client);
                EnterHfpFallbackOnMta();
                return;
            }

            bool isFloat = false;
            bool formatSupported = false;
            if (wfx.wFormatTag == 3 /* WAVE_FORMAT_IEEE_FLOAT */)
            {
                isFloat = wfx.wBitsPerSample == 32;
                formatSupported = isFloat;
            }
            else if (wfx.wFormatTag == 1 /* WAVE_FORMAT_PCM */)
            {
                isFloat = false;
                formatSupported = wfx.wBitsPerSample is 8 or 16 or 24 or 32;
            }
            else if (wfx.wFormatTag == 0xFFFE /* WAVE_FORMAT_EXTENSIBLE */ && wfx.cbSize >= 22)
            {
                var wfext = Marshal.PtrToStructure<WaveFormatExtensible>(fmtPtr);
                if (wfext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
                {
                    isFloat = wfx.wBitsPerSample == 32;
                    formatSupported = isFloat;
                }
                else if (wfext.SubFormat == KSDATAFORMAT_SUBTYPE_PCM)
                {
                    isFloat = false;
                    formatSupported = wfx.wBitsPerSample is 8 or 16 or 24 or 32;
                }
            }

            if (!formatSupported)
            {
                ReleaseComIfNeeded(client);
                EnterHfpFallbackOnMta();
                return;
            }

            _loopbackChannels = Math.Max(1, (int)wfx.nChannels);
            _loopbackBitsPerSample = wfx.wBitsPerSample;
            _loopbackIsFloat = isFloat;

            Guid sessionGuid = Guid.Empty;
            int hr = client.Initialize(0 /* SHARED */, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 400_000, 0, fmtPtr, ref sessionGuid);
            if (hr != 0)
            {
                ReleaseComIfNeeded(client);
                EnterHfpFallbackOnMta();
                return;
            }

            nint hEvt = NativeMethods.CreateEventW(0, false, false, null);
            if (hEvt == 0 || client.SetEventHandle(hEvt) != 0)
            {
                if (hEvt != 0) NativeMethods.CloseHandle(hEvt);
                ReleaseComIfNeeded(client);
                EnterHfpFallbackOnMta();
                return;
            }

            Guid capIid = CoreAudioGuids.IID_IAudioCaptureClient;
            if (client.GetService(ref capIid, out object capObj) != 0 || capObj is not IAudioCaptureClient cap)
            {
                if (capObj is not null) ReleaseComIfNeeded(capObj);
                NativeMethods.CloseHandle(hEvt);
                ReleaseComIfNeeded(client);
                EnterHfpFallbackOnMta();
                return;
            }

            client.Start();
            _loopbackClient = client;
            _loopbackCapture = cap;
            _loopbackEventHandle = hEvt;
            _isHfpFallback = false;
            _hfpRetryAttempts = 0;
            _hfpNextRetryTickMs = 0;
        }
        catch
        {
            if (client is not null && !ReferenceEquals(client, _loopbackClient))
            {
                ReleaseComIfNeeded(client);
            }
            EnterHfpFallbackOnMta();
        }
        finally
        {
            if (fmtPtr != 0) NativeMethods.CoTaskMemFree(fmtPtr);
        }
    }

    private void PumpLoopbackOrPeakOnMta()
    {
        if (_loopbackCapture is null) return;
        bool sawAudio = false;
        try
        {
            while (_loopbackCapture.GetNextPacketSize(out uint packetFrames) == 0 && packetFrames > 0)
            {
                if (_loopbackCapture.GetBuffer(out nint dataPtr, out uint numFrames, out uint flags, out _, out _) != 0)
                    break;

                bool silent = (flags & 0x2 /* AUDCLNT_BUFFERFLAGS_SILENT */) != 0 || dataPtr == 0;
                int ch = _loopbackChannels;

                if (silent)
                {
                    for (uint i = 0; i < numFrames; i++)
                    {
                        _ringMono[_ringWritePos] = 0f;
                        _ringWritePos = (_ringWritePos + 1) % Radix2Fft.FftSize;
                    }
                }
                else if (_loopbackIsFloat && _loopbackBitsPerSample == 32)
                {
                    float* samples = (float*)dataPtr;
                    for (uint f = 0; f < numFrames; f++)
                    {
                        float sum = 0f;
                        int baseIdx = (int)f * ch;
                        for (int c = 0; c < ch; c++) sum += samples[baseIdx + c];
                        float mono = sum / ch;
                        if (Math.Abs(mono) > 0.0008f) sawAudio = true;
                        _ringMono[_ringWritePos] = mono;
                        _ringWritePos = (_ringWritePos + 1) % Radix2Fft.FftSize;
                    }
                }
                else if (!_loopbackIsFloat && _loopbackBitsPerSample == 16)
                {
                    short* samples = (short*)dataPtr;
                    for (uint f = 0; f < numFrames; f++)
                    {
                        float sum = 0f;
                        int baseIdx = (int)f * ch;
                        for (int c = 0; c < ch; c++) sum += samples[baseIdx + c] / 32768f;
                        float mono = sum / ch;
                        if (Math.Abs(mono) > 0.0008f) sawAudio = true;
                        _ringMono[_ringWritePos] = mono;
                        _ringWritePos = (_ringWritePos + 1) % Radix2Fft.FftSize;
                    }
                }
                else if (!_loopbackIsFloat && _loopbackBitsPerSample == 24)
                {
                    byte* raw = (byte*)dataPtr;
                    int stride = ch * 3;
                    for (uint f = 0; f < numFrames; f++)
                    {
                        float sum = 0f;
                        int frameOffset = (int)f * stride;
                        for (int c = 0; c < ch; c++)
                        {
                            int off = frameOffset + c * 3;
                            int sample24 = raw[off] | (raw[off + 1] << 8) | ((sbyte)raw[off + 2] << 16);
                            sum += sample24 / 8388608f;
                        }
                        float mono = sum / ch;
                        if (Math.Abs(mono) > 0.0008f) sawAudio = true;
                        _ringMono[_ringWritePos] = mono;
                        _ringWritePos = (_ringWritePos + 1) % Radix2Fft.FftSize;
                    }
                }
                else if (!_loopbackIsFloat && _loopbackBitsPerSample == 32)
                {
                    int* samples = (int*)dataPtr;
                    for (uint f = 0; f < numFrames; f++)
                    {
                        float sum = 0f;
                        int baseIdx = (int)f * ch;
                        for (int c = 0; c < ch; c++) sum += (float)(samples[baseIdx + c] / 2147483648.0);
                        float mono = sum / ch;
                        if (Math.Abs(mono) > 0.0008f) sawAudio = true;
                        _ringMono[_ringWritePos] = mono;
                        _ringWritePos = (_ringWritePos + 1) % Radix2Fft.FftSize;
                    }
                }
                else if (!_loopbackIsFloat && _loopbackBitsPerSample == 8)
                {
                    byte* samples = (byte*)dataPtr;
                    for (uint f = 0; f < numFrames; f++)
                    {
                        float sum = 0f;
                        int baseIdx = (int)f * ch;
                        for (int c = 0; c < ch; c++) sum += (samples[baseIdx + c] - 128) / 128f;
                        float mono = sum / ch;
                        if (Math.Abs(mono) > 0.0008f) sawAudio = true;
                        _ringMono[_ringWritePos] = mono;
                        _ringWritePos = (_ringWritePos + 1) % Radix2Fft.FftSize;
                    }
                }

                _loopbackCapture.ReleaseBuffer(numFrames);
            }
        }
        catch { }

        if (sawAudio)
        {
            _lastSignalTickMs = Environment.TickCount64;
        }

        Span<float> ordered = stackalloc float[Radix2Fft.FftSize];
        for (int i = 0; i < Radix2Fft.FftSize; i++)
        {
            ordered[i] = _ringMono[(_ringWritePos + i) % Radix2Fft.FftSize];
        }
        Span<double> tmp = stackalloc double[Radix2Fft.BandCount];
        Radix2Fft.ComputeLogBands(ordered, tmp, _smoothedBands);
    }

    private void PumpPeakFallbackBandsOnMta()
    {
        float peak = 0f;
        _renderMeter?.GetPeakValue(out peak);
        double level = Math.Clamp(peak * 1.4, 0.0, 1.0);
        for (int i = 0; i < Radix2Fft.BandCount; i++)
        {
            double shape = Math.Sin((i + 1) / (double)(Radix2Fft.BandCount + 1) * Math.PI);
            double target = level * (0.35 + 0.65 * shape);
            _smoothedBands[i] = _smoothedBands[i] * 0.6 + target * 0.4;
        }
    }

    private void EmitSpectrumUpdatedOnMta()
    {
        var handler = SpectrumUpdated;
        if (handler is null) return;
        double[] copy = new double[_smoothedBands.Length];
        Array.Copy(_smoothedBands, copy, _smoothedBands.Length);
        try
        {
            handler.Invoke(this, copy);
        }
        catch
        {
            // Protect dedicated MTA capture pump from transient UI listener exceptions
        }
    }

    private void PublishSnapshotOnMta() => PublishSnapshotOnMta(force: false);

    private void PublishSnapshotOnMta(bool force)
    {
        if (_disposed) return;

        PruneDisconnectedSessionsOnMta();

        float masterVol = 0.5f;
        bool masterMuted = false;
        if (_renderVolume is not null)
        {
            _renderVolume.GetMasterVolumeLevelScalar(out masterVol);
            _renderVolume.GetMute(out masterMuted);
        }

        string defOutId = string.Empty;
        string defOutName = "Default Output";
        if (_renderDevice is not null)
        {
            _renderDevice.GetId(out defOutId);
            defOutName = ReadDeviceFriendlyName(_renderDevice) ?? "Default Output";
        }

        var outputs = EnumerateEndpointsOnMta(eRender, defOutId);
        var routingRules = CopyRoutingRulesThreadSafe();
        var routedDeviceByPid = new Dictionary<int, string?>();

        float micVol = 1.0f;
        bool micMuted = false;
        bool hasMic = _captureVolume is not null;
        if (_captureVolume is not null)
        {
            _captureVolume.GetMasterVolumeLevelScalar(out micVol);
            _captureVolume.GetMute(out micMuted);
        }

        var rawSessions = new List<RawAudioSessionEntry>(_sessions.Count);
        foreach (var s in _sessions)
        {
            s.SimpleVolume.GetMasterVolume(out float sv);
            s.SimpleVolume.GetMute(out bool sm);
            float peak = 0f;
            s.Meter?.GetPeakValue(out peak);

            string? routedOutputId = null;
            if (!s.IsSystemSounds && s.ProcessId > 0 && !routedDeviceByPid.TryGetValue(s.ProcessId, out routedOutputId))
            {
                string? policyEndpointId = GetCachedOrQueryProcessOutputEndpointOnMta(s.ProcessId);
                routingRules.TryGetValue(s.ProcessName, out string? configuredRuleId);
                routedOutputId = AudioSessionGrouper.ResolveRoutedOutputDeviceId(policyEndpointId, outputs, configuredRuleId);
                routedDeviceByPid[s.ProcessId] = routedOutputId;
            }

            rawSessions.Add(new RawAudioSessionEntry(s.GroupKey, s.ProcessId, s.ProcessName, s.DisplayName, sv, sm, peak, s.IconPath, routedOutputId));
        }

        var grouped = AudioSessionGrouper.GroupSessions(rawSessions);
        string defInId = string.Empty;
        _captureDevice?.GetId(out defInId);
        var inputs = EnumerateEndpointsOnMta(eCapture, defInId);

        var next = new AudioMixerSnapshot(
            MasterVolume01: Math.Clamp(masterVol, 0f, 1f),
            IsMasterMuted: masterMuted,
            DefaultOutputId: defOutId,
            DefaultOutputName: defOutName,
            MicVolume01: Math.Clamp(micVol, 0f, 1f),
            IsMicMuted: micMuted,
            HasMicDevice: hasMic,
            IsLoopbackActive: _loopbackClient is not null,
            IsBluetoothHfpFallback: _isHfpFallback,
            SpectrumBands: ImmutableArray.Create(_smoothedBands),
            AppSessions: grouped,
            OutputDevices: outputs,
            InputDevices: inputs,
            OutputFormatLabel: _outputFormatLabel,
            SupportedOutputFormats: _supportedOutputFormats);

        if (!force && AreSnapshotsEquivalent(_current, next))
        {
            return;
        }

        _current = next;
        SnapshotChanged?.Invoke(this, next);
    }

    internal static WaveFormatExtensible BuildWaveFormatExtensible(
        uint sampleRateHz,
        ushort containerBits,
        ushort validBits,
        ushort channels,
        uint channelMask,
        bool isFloat)
    {
        ushort ch = Math.Max((ushort)1, channels);
        ushort cBits = containerBits == 24 ? (ushort)24 : containerBits;
        ushort vBits = validBits > 0 ? validBits : cBits;
        ushort blockAlign = (ushort)(ch * (cBits / 8));
        uint avgBytesPerSec = sampleRateHz * blockAlign;
        uint mask = channelMask != 0
            ? channelMask
            : ch switch
            {
                1 => 0x4u,
                2 => 0x3u,
                _ => (1u << Math.Min(18, (int)ch)) - 1u
            };

        return new WaveFormatExtensible
        {
            Format = new WaveFormatEx
            {
                wFormatTag = 0xFFFE, // WAVE_FORMAT_EXTENSIBLE
                nChannels = ch,
                nSamplesPerSec = sampleRateHz,
                nAvgBytesPerSec = avgBytesPerSec,
                nBlockAlign = blockAlign,
                wBitsPerSample = cBits,
                cbSize = 22
            },
            Samples = vBits,
            dwChannelMask = mask,
            SubFormat = isFloat ? KSDATAFORMAT_SUBTYPE_IEEE_FLOAT : KSDATAFORMAT_SUBTYPE_PCM
        };
    }

    public static AudioFormatOption? SelectHighestQualityFormat(IEnumerable<AudioFormatOption>? options)
    {
        if (options is null) return null;
        return options
            .OrderByDescending(o => o.SampleRateHz)
            .ThenByDescending(o => o.BitsPerSample)
            .ThenByDescending(o => o.IsFloat ? 1 : 0)
            .FirstOrDefault();
    }

    private static string FormatOptionLabel(uint sampleRateHz, ushort bitsPerSample, bool isFloat)
    {
        string baseLabel = AudioFormatFormatter.FormatMixFormat(sampleRateHz, bitsPerSample, bitsPerSample, isFloat)
            ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{sampleRateHz / 1000.0:0.#} kHz \u00B7 {bitsPerSample}-bit");
        return isFloat ? $"{baseLabel} Float" : baseLabel;
    }

    private static (string? ActiveLabel, ImmutableArray<AudioFormatOption> Supported) ReadOutputFormatAndSupportedOnMta(IMMDevice dev)
    {
        nint fmtPtr = 0;
        IAudioClient? client = null;
        try
        {
            Guid iid = CoreAudioGuids.IID_IAudioClient;
            if (dev.Activate(ref iid, CLSCTX_ALL, 0, out object obj) != 0 || obj is not IAudioClient ac)
                return (null, ImmutableArray<AudioFormatOption>.Empty);
            client = ac;
            if (client.GetMixFormat(out fmtPtr) != 0 || fmtPtr == 0)
                return (null, ImmutableArray<AudioFormatOption>.Empty);

            var wfx = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
            bool activeIsFloat = wfx.wFormatTag == 3;
            ushort activeValidBits = wfx.wBitsPerSample;
            ushort channels = Math.Max((ushort)1, wfx.nChannels);
            uint channelMask = 0;
            if (wfx.wFormatTag == 0xFFFE && wfx.cbSize >= 22)
            {
                var wfext = Marshal.PtrToStructure<WaveFormatExtensible>(fmtPtr);
                activeIsFloat = wfext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
                if (wfext.Samples > 0) activeValidBits = wfext.Samples;
                channelMask = wfext.dwChannelMask;
            }

            string activeLabel = AudioFormatFormatter.FormatMixFormat(wfx.nSamplesPerSec, wfx.wBitsPerSample, activeValidBits, activeIsFloat)
                ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{wfx.nSamplesPerSec / 1000.0:0.#} kHz \u00B7 {activeValidBits}-bit");
            int activeRate = (int)wfx.nSamplesPerSec;
            int epRate = activeRate;
            ushort epValidBits = activeValidBits;
            bool epIsFloat = activeIsFloat;

            IPolicyConfig? policy = null;
            nint epFmtPtr = 0;
            try
            {
                dev.GetId(out string devId);
                Type? policyType = !string.IsNullOrWhiteSpace(devId) ? Type.GetTypeFromCLSID(CoreAudioGuids.CLSID_PolicyConfigClient) : null;
                if (policyType is not null && Activator.CreateInstance(policyType) is IPolicyConfig pc)
                {
                    policy = pc;
                    if (policy.GetDeviceFormat(devId, 0, out epFmtPtr) == 0 && epFmtPtr != 0)
                    {
                        var ewfx = Marshal.PtrToStructure<WaveFormatEx>(epFmtPtr);
                        epRate = (int)ewfx.nSamplesPerSec;
                        epIsFloat = ewfx.wFormatTag == 3;
                        epValidBits = ewfx.wBitsPerSample;
                        if (ewfx.wFormatTag == 0xFFFE && ewfx.cbSize >= 22)
                        {
                            var ewfext = Marshal.PtrToStructure<WaveFormatExtensible>(epFmtPtr);
                            epIsFloat = ewfext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
                            if (ewfext.Samples > 0) epValidBits = ewfext.Samples;
                        }
                        if (epRate > 0 && epValidBits > 0)
                        {
                            string? epLabel = AudioFormatFormatter.FormatMixFormat((uint)epRate, ewfx.wBitsPerSample, epValidBits, epIsFloat);
                            if (!string.IsNullOrWhiteSpace(epLabel))
                            {
                                activeLabel = epLabel;
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (epFmtPtr != 0) NativeMethods.CoTaskMemFree(epFmtPtr);
                if (policy is not null && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy);
            }

            var found = new List<AudioFormatOption>();
            var seenKeys = new HashSet<(int Rate, ushort Bits, bool IsFloat)>();

            int[] candidateRates = [384000, 192000, 176400, 96000, 88200, 48000, 44100];
            (ushort Bits, ushort ContainerBits, bool IsFloat)[] candidateDepths =
            [
                (32, 32, true),
                (32, 32, false),
                (24, 24, false),
                (24, 32, false),
                (16, 16, false)
            ];

            foreach (int rate in candidateRates)
            {
                foreach (var (bits, containerBits, isFloat) in candidateDepths)
                {
                    if (seenKeys.Contains((rate, bits, isFloat))) continue;

                    var candidate = BuildWaveFormatExtensible((uint)rate, containerBits, bits, channels, channelMask, isFloat);
                    WaveFormatExtensible* pCand = &candidate;

                    bool supported = false;
                    // Test Shared Mode (0) and Exclusive Mode (1) cleanly; always release any closest match returned.
                    nint closestShared = 0;
                    int hrShared = client.IsFormatSupported(0 /* AUDCLNT_SHAREMODE_SHARED */, (nint)pCand, out closestShared);
                    if (closestShared != 0) NativeMethods.CoTaskMemFree(closestShared);
                    if (hrShared == 0)
                    {
                        supported = true;
                    }
                    else if (!isFloat)
                    {
                        nint closestExcl = 0;
                        int hrExcl = client.IsFormatSupported(1 /* AUDCLNT_SHAREMODE_EXCLUSIVE */, (nint)pCand, out closestExcl);
                        if (closestExcl != 0) NativeMethods.CoTaskMemFree(closestExcl);
                        if (hrExcl == 0)
                        {
                            supported = true;
                        }
                    }

                    if (supported)
                    {
                        seenKeys.Add((rate, bits, isFloat));
                        bool isActive = (rate == epRate && bits == epValidBits && isFloat == epIsFloat)
                                     || (rate == activeRate && bits == activeValidBits && isFloat == activeIsFloat && epRate == activeRate && epValidBits == activeValidBits);
                        found.Add(new AudioFormatOption(rate, bits, isFloat, channels, FormatOptionLabel((uint)rate, bits, isFloat), isActive));
                    }
                }
            }

            // Always guarantee the currently active format is included in the list
            if (!seenKeys.Contains((epRate, epValidBits, epIsFloat)))
            {
                found.Add(new AudioFormatOption(
                    epRate,
                    epValidBits,
                    epIsFloat,
                    channels,
                    FormatOptionLabel((uint)epRate, epValidBits, epIsFloat),
                    IsActive: true));
            }

            found.Sort(static (a, b) =>
            {
                int cmp = b.SampleRateHz.CompareTo(a.SampleRateHz);
                if (cmp != 0) return cmp;
                cmp = b.BitsPerSample.CompareTo(a.BitsPerSample);
                if (cmp != 0) return cmp;
                return (b.IsFloat ? 1 : 0).CompareTo(a.IsFloat ? 1 : 0);
            });

            return (activeLabel, [.. found]);
        }
        catch
        {
            return (null, ImmutableArray<AudioFormatOption>.Empty);
        }
        finally
        {
            if (fmtPtr != 0) NativeMethods.CoTaskMemFree(fmtPtr);
            if (client is not null && Marshal.IsComObject(client)) Marshal.ReleaseComObject(client);
        }
    }

    private bool ApplyDefaultOutputFormatOnMta(int sampleRateHz, ushort bitsPerSample, bool isFloat)
    {
        if (_renderDevice is null) return false;

        nint prevDevFmt = 0;
        nint prevMixFmt = 0;
        nint epFmtPtr = 0;
        nint mixFmtPtr = 0;
        IPolicyConfig? policy = null;
        try
        {
            _renderDevice.GetId(out string devId);
            if (string.IsNullOrWhiteSpace(devId)) return false;

            Type? policyType = Type.GetTypeFromCLSID(CoreAudioGuids.CLSID_PolicyConfigClient);
            if (policyType is null) return false;
            policy = Activator.CreateInstance(policyType) as IPolicyConfig;
            if (policy is null) return false;

            ushort channels = 2;
            uint channelMask = 0x3;
            if (policy.GetDeviceFormat(devId, 0, out prevDevFmt) == 0 && prevDevFmt != 0)
            {
                var curWfx = Marshal.PtrToStructure<WaveFormatEx>(prevDevFmt);
                channels = Math.Max((ushort)1, curWfx.nChannels);
                if (curWfx.wFormatTag == 0xFFFE && curWfx.cbSize >= 22)
                {
                    var curWfext = Marshal.PtrToStructure<WaveFormatExtensible>(prevDevFmt);
                    if (curWfext.dwChannelMask != 0) channelMask = curWfext.dwChannelMask;
                }
            }
            policy.GetMixFormat(devId, out prevMixFmt);

            int size = Marshal.SizeOf<WaveFormatExtensible>();
            mixFmtPtr = Marshal.AllocCoTaskMem(size);
            var targetMix = BuildWaveFormatExtensible((uint)sampleRateHz, 32, 32, channels, channelMask, isFloat: true);
            Marshal.StructureToPtr(targetMix, mixFmtPtr, false);

            epFmtPtr = Marshal.AllocCoTaskMem(size);
            var attempts = new List<(ushort ContainerBits, ushort ValidBits, bool EpIsFloat)>
            {
                (bitsPerSample, bitsPerSample, isFloat)
            };
            if (bitsPerSample == 24)
            {
                attempts.Add((32, 24, false));
            }
            if (isFloat)
            {
                // Many hardware codecs expect integer PCM on the endpoint pin while mixing in 32-bit IEEE float.
                attempts.Add((32, 32, false));
            }

            bool applied = false;
            foreach (var (containerBits, validBits, epFloat) in attempts)
            {
                var targetEp = BuildWaveFormatExtensible((uint)sampleRateHz, containerBits, validBits, channels, channelMask, epFloat);
                Marshal.StructureToPtr(targetEp, epFmtPtr, false);

                int hr = policy.SetDeviceFormat(devId, epFmtPtr, mixFmtPtr);
                if (hr < 0)
                {
                    // Fallback passing matching endpoint pointer for both args when driver refuses separate mix format
                    hr = policy.SetDeviceFormat(devId, epFmtPtr, epFmtPtr);
                }

                if (hr == 0)
                {
                    applied = true;
                    break;
                }
            }

            if (!applied)
            {
                if (prevDevFmt != 0)
                {
                    try { policy.SetDeviceFormat(devId, prevDevFmt, prevMixFmt != 0 ? prevMixFmt : prevDevFmt); } catch { }
                }
                return false;
            }

            (_outputFormatLabel, _supportedOutputFormats) = ReadOutputFormatAndSupportedOnMta(_renderDevice);
            TeardownLoopbackOnMta();
            EnsureLoopbackStateOnMta();
            PublishSnapshotOnMta(force: true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (prevDevFmt != 0) NativeMethods.CoTaskMemFree(prevDevFmt);
            if (prevMixFmt != 0) NativeMethods.CoTaskMemFree(prevMixFmt);
            if (epFmtPtr != 0) Marshal.FreeCoTaskMem(epFmtPtr);
            if (mixFmtPtr != 0) Marshal.FreeCoTaskMem(mixFmtPtr);
            if (policy is not null && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy);
        }
    }

    private static bool AreSnapshotsEquivalent(AudioMixerSnapshot a, AudioMixerSnapshot b) =>
        Math.Abs(a.MasterVolume01 - b.MasterVolume01) < 1e-4f
        && a.IsMasterMuted == b.IsMasterMuted
        && string.Equals(a.DefaultOutputId, b.DefaultOutputId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.DefaultOutputName, b.DefaultOutputName, StringComparison.Ordinal)
        && Math.Abs(a.MicVolume01 - b.MicVolume01) < 1e-4f
        && a.IsMicMuted == b.IsMicMuted
        && a.HasMicDevice == b.HasMicDevice
        && a.IsLoopbackActive == b.IsLoopbackActive
        && a.IsBluetoothHfpFallback == b.IsBluetoothHfpFallback
        && a.AppSessions.SequenceEqual(b.AppSessions)
        && a.OutputDevices.SequenceEqual(b.OutputDevices)
        && a.InputDevices.SequenceEqual(b.InputDevices)
        && string.Equals(a.OutputFormatLabel, b.OutputFormatLabel, StringComparison.Ordinal)
        && a.SupportedOutputFormats.SequenceEqual(b.SupportedOutputFormats);

    private ImmutableArray<AudioEndpointSnapshot> EnumerateEndpointsOnMta(int flow, string defaultId)
    {
        if (_enumerator is null) return ImmutableArray<AudioEndpointSnapshot>.Empty;
        var builder = ImmutableArray.CreateBuilder<AudioEndpointSnapshot>();
        if (_enumerator.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var coll) != 0 || coll is null)
            return ImmutableArray<AudioEndpointSnapshot>.Empty;

        try
        {
            coll.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (coll.Item(i, out var dev) != 0 || dev is null) continue;
                try
                {
                    dev.GetId(out string id);
                    string name = ReadDeviceFriendlyName(dev) ?? $"Audio {i + 1}";
                    bool isBt = name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Headphones", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Buds", StringComparison.OrdinalIgnoreCase);
                    builder.Add(new AudioEndpointSnapshot(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), flow == eCapture, isBt));
                }
                finally
                {
                    if (Marshal.IsComObject(dev)) Marshal.ReleaseComObject(dev);
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(coll)) Marshal.ReleaseComObject(coll);
        }

        return builder.ToImmutable();
    }

    private static string? ReadDeviceFriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(STGM_READ, out var store) != 0 || store is null) return null;
        try
        {
            var key = CoreAudioGuids.PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var pv) == 0)
            {
                try
                {
                    if (pv.vt == 31 /* VT_LPWSTR */ && pv.p != 0)
                    {
                        return Marshal.PtrToStringUni(pv.p);
                    }
                }
                finally
                {
                    nint pPv = (nint)(&pv);
                    NativeMethods.PropVariantClear(pPv);
                }
            }
            return null;
        }
        finally
        {
            if (Marshal.IsComObject(store)) Marshal.ReleaseComObject(store);
        }
    }

    private static string ResolveProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return AudioSessionGrouper.NormalizeProcessName(p.ProcessName);
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    private static string ResolveIndirectString(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        if (!source.StartsWith('@')) return source;

        char* buf = stackalloc char[512];
        int hr = NativeMethods.SHLoadIndirectString(source, buf, 512, 0);
        return hr == 0 ? new string(buf).TrimEnd('\0') : string.Empty;
    }

    private void ClearTrackedSessionsOnMta()
    {
        foreach (var s in _sessions)
        {
            ReleaseTrackedSessionOnMta(s);
        }
        _sessions.Clear();
    }

    private static void ReleaseTrackedSessionOnMta(TrackedSession s)
    {
        if (s.EventsSink is not null)
        {
            try { s.Control.UnregisterAudioSessionNotification(s.EventsSink); } catch { }
        }
        ReleaseSessionComObjects(s.Control, s.Control2, s.SimpleVolume, s.Meter);
    }

    private static void ReleaseSessionComObjects(object? control, object? control2, object? simpleVol, object? meter)
    {
        var seen = new HashSet<object?>(ReferenceEqualityComparer.Instance);
        foreach (object? obj in new[] { control, control2, simpleVol, meter })
        {
            if (obj is not null && seen.Add(obj) && Marshal.IsComObject(obj))
            {
                try { Marshal.ReleaseComObject(obj); } catch { }
            }
        }
    }

    private static void ReleaseComIfNeeded(object? obj)
    {
        if (obj is not null && Marshal.IsComObject(obj))
        {
            try { Marshal.ReleaseComObject(obj); } catch { }
        }
    }

    private void TeardownLoopbackOnMta()
    {
        try { _loopbackClient?.Stop(); } catch { }
        if (_loopbackCapture is not null && Marshal.IsComObject(_loopbackCapture)) Marshal.ReleaseComObject(_loopbackCapture);
        _loopbackCapture = null;
        if (_loopbackClient is not null && Marshal.IsComObject(_loopbackClient)) Marshal.ReleaseComObject(_loopbackClient);
        _loopbackClient = null;
        if (_loopbackEventHandle != 0)
        {
            NativeMethods.CloseHandle(_loopbackEventHandle);
            _loopbackEventHandle = 0;
        }
        Array.Clear(_smoothedBands);
    }

    private void TeardownBoundDevicesOnMta()
    {
        // Strict reverse order: sessions -> sessionMgr -> endpointVol -> renderDevice -> captureVol -> captureDevice
        ClearTrackedSessionsOnMta();

        foreach (var secondary in _secondarySessionManagers)
        {
            try { secondary.Manager.UnregisterSessionNotification(secondary.CreatedSink); } catch { }
            if (Marshal.IsComObject(secondary.Manager)) Marshal.ReleaseComObject(secondary.Manager);
            if (Marshal.IsComObject(secondary.Device)) Marshal.ReleaseComObject(secondary.Device);
        }
        _secondarySessionManagers.Clear();

        if (_sessionMgr is not null)
        {
            if (_sessionCreatedSink is not null)
            {
                try { _sessionMgr.UnregisterSessionNotification(_sessionCreatedSink); } catch { }
            }
            if (Marshal.IsComObject(_sessionMgr)) Marshal.ReleaseComObject(_sessionMgr);
            _sessionMgr = null;
            _sessionCreatedSink = null;
        }

        if (_renderMeter is not null && Marshal.IsComObject(_renderMeter))
        {
            Marshal.ReleaseComObject(_renderMeter);
            _renderMeter = null;
        }

        if (_renderVolume is not null)
        {
            if (_renderVolSink is not null)
            {
                try { _renderVolume.UnregisterControlChangeNotify(_renderVolSink); } catch { }
            }
            if (Marshal.IsComObject(_renderVolume)) Marshal.ReleaseComObject(_renderVolume);
            _renderVolume = null;
            _renderVolSink = null;
        }

        if (_renderDevice is not null)
        {
            if (Marshal.IsComObject(_renderDevice)) Marshal.ReleaseComObject(_renderDevice);
            _renderDevice = null;
        }
        _outputFormatLabel = null;
        _supportedOutputFormats = ImmutableArray<AudioFormatOption>.Empty;

        if (_captureVolume is not null)
        {
            if (_captureVolSink is not null)
            {
                try { _captureVolume.UnregisterControlChangeNotify(_captureVolSink); } catch { }
            }
            if (Marshal.IsComObject(_captureVolume)) Marshal.ReleaseComObject(_captureVolume);
            _captureVolume = null;
            _captureVolSink = null;
        }

        if (_captureDevice is not null)
        {
            if (Marshal.IsComObject(_captureDevice)) Marshal.ReleaseComObject(_captureDevice);
            _captureDevice = null;
        }
    }

    private void TeardownEnumeratorOnMta()
    {
        if (_enumerator is not null)
        {
            if (_endpointNotifySink is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_endpointNotifySink); } catch { }
            }
            if (Marshal.IsComObject(_enumerator)) Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
            _endpointNotifySink = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { }
        if (_mtaThread.Join(1200))
        {
            _queue.Dispose();
        }
        // else: the MTA loop is still finishing a slow COM call and will touch _queue again;
        // disposing it now would throw ObjectDisposedException on that thread and crash shutdown.
        return ValueTask.CompletedTask;
    }

    private sealed class EndpointNotifySink(WeakReference<CoreAudioHost> owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, uint newState) => RequestRebind();
        public void OnDeviceAdded(string pwstrDeviceId) => RequestRebind();
        public void OnDeviceRemoved(string deviceId) => RequestRebind();
        public void OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId) => RequestRebind();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        private void RequestRebind()
        {
            if (owner.TryGetTarget(out var host))
            {
                if (Interlocked.Exchange(ref host._rebindNeeded, 1) == 0)
                {
                    host.Enqueue(static () => { });
                }
            }
        }
    }

    private sealed class EndpointVolumeSink(WeakReference<CoreAudioHost> owner, bool isCapture) : IAudioEndpointVolumeCallback
    {
        private readonly bool _isCapture = isCapture;

        public void OnNotify(nint pNotify)
        {
            if (owner.TryGetTarget(out var host))
            {
                _ = _isCapture;
                if (pNotify != 0 && ShouldIgnoreEventContext(*(Guid*)pNotify, host._selfEventContext))
                {
                    return;
                }
                host.Enqueue(host.PublishSnapshotOnMta);
            }
        }
    }

    private sealed class SessionCreatedSink(WeakReference<CoreAudioHost> owner, bool isDefaultRender = true) : IAudioSessionNotification
    {
        public void OnSessionCreated(IAudioSessionControl newSession)
        {
            if (newSession is null) return;
            if (!owner.TryGetTarget(out var host) || host._disposed)
            {
                ReleaseComIfNeeded(newSession);
                return;
            }

            if (!host.Enqueue(() =>
            {
                if (host._disposed)
                {
                    ReleaseComIfNeeded(newSession);
                    return;
                }
                TrackedSession? tracked = host.TrackSingleSessionOnMta(newSession, allowSystemSounds: isDefaultRender);
                if (tracked is not null && !tracked.IsSystemSounds && tracked.ProcessId > 0)
                {
                    host._policyEndpointCache.Remove(tracked.ProcessId);
                    host.ApplyConfiguredRouteForSessionOnMta(tracked);
                }
                host.PublishSnapshotOnMta();
            }))
            {
                ReleaseComIfNeeded(newSession);
            }
        }
    }

    private sealed class SessionEventsSink(WeakReference<CoreAudioHost> owner) : IAudioSessionEvents
    {
        public void OnDisplayNameChanged(string newDisplayName, ref Guid eventContext) => Refresh();
        public void OnIconPathChanged(string newIconPath, ref Guid eventContext) => Refresh();
        public void OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext)
        {
            if (owner.TryGetTarget(out var host) && !ShouldIgnoreEventContext(eventContext, host._selfEventContext))
            {
                host.Enqueue(host.PublishSnapshotOnMta);
            }
        }
        public void OnChannelVolumeChanged(uint channelCount, nint newChannelVolumeArray, uint changedChannel, ref Guid eventContext) { }
        public void OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) => Refresh();
        public void OnStateChanged(int newState) => Refresh();
        public void OnSessionDisconnected(int disconnectReason)
        {
            if (owner.TryGetTarget(out var host))
            {
                host.Enqueue(() =>
                {
                    host._policyEndpointCache.Clear();
                    host.EnumerateSessionsOnMta();
                    host.PublishSnapshotOnMta();
                });
            }
        }

        private void Refresh()
        {
            if (owner.TryGetTarget(out var host))
            {
                host.Enqueue(host.PublishSnapshotOnMta);
            }
        }
    }
}
