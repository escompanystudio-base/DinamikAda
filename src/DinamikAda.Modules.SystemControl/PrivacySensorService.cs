using System.Collections.Immutable;
using System.Diagnostics;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;
using Microsoft.Win32;

namespace DinamikAda.Modules.SystemControl;

public sealed record ConsentStoreRecord(string KeyName, bool IsPackaged, long LastUsedTimeStart, long LastUsedTimeStop);

/// <summary>
/// Pure testable evaluator cross-checking Windows ConsentStore active timestamps
/// against live running processes so crashed apps never leave phantom privacy dots.
/// </summary>
public static class PrivacySensorEvaluator
{
    public static ImmutableArray<string> FilterActiveConsumers(
        IEnumerable<ConsentStoreRecord> records,
        ISet<string> runningProcessNames)
    {
        var active = ImmutableArray.CreateBuilder<string>();
        foreach (var r in records)
        {
            bool timestampActive = r.LastUsedTimeStart > 0 && (r.LastUsedTimeStop == 0 || r.LastUsedTimeStart > r.LastUsedTimeStop);
            if (!timestampActive) continue;

            string friendly = ResolveDisplayName(r.KeyName, r.IsPackaged, out string? exeStem);
            if (r.IsPackaged)
            {
                if (IsPackagedAppRunning(r.KeyName, exeStem, runningProcessNames))
                {
                    active.Add(friendly);
                }
            }
            else if (!string.IsNullOrWhiteSpace(exeStem) && runningProcessNames.Contains(exeStem))
            {
                active.Add(friendly);
            }
        }
        return active.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    private static bool IsPackagedAppRunning(string keyName, string? exeStem, ISet<string> runningProcessNames)
    {
        if (runningProcessNames.Contains(keyName)) return true;
        if (!string.IsNullOrWhiteSpace(exeStem) && runningProcessNames.Contains(exeStem)) return true;

        int us = keyName.IndexOf('_');
        string familyPrefix = us > 0 ? keyName[..us] : keyName;
        if (runningProcessNames.Contains(familyPrefix)) return true;

        foreach (string token in familyPrefix.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 4) continue;
            if (token.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (runningProcessNames.Contains(token)) return true;
        }

        return false;
    }

    public static string ResolveDisplayName(string keyName, bool isPackaged, out string? exeStem)
    {
        if (!isPackaged)
        {
            string normalized = keyName.Replace('#', '\\');
            string fileName = Path.GetFileNameWithoutExtension(normalized);
            exeStem = fileName;
            return string.IsNullOrWhiteSpace(fileName) ? keyName : fileName;
        }

        int us = keyName.IndexOf('_');
        string raw = us > 0 ? keyName[..us] : keyName;
        int dot = raw.LastIndexOf('.');
        exeStem = dot >= 0 && dot < raw.Length - 1 ? raw[(dot + 1)..] : raw;
        return exeStem;
    }
}

public sealed class PrivacySensorService : IDisposable
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const string MicKeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string CamKeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private readonly object _sync = new();
    private RegistryKey? _micKey;
    private RegistryKey? _camKey;
    private AutoResetEvent? _micEvent;
    private AutoResetEvent? _camEvent;
    private RegisteredWaitHandle? _micWait;
    private RegisteredWaitHandle? _camWait;
    private CancellationTokenSource? _debounceCts;
    private PrivacySensorSnapshot _current = new(false, false, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
    private bool _disposed;

    public event EventHandler<PrivacySensorSnapshot>? PrivacyChanged;
    public PrivacySensorSnapshot Current => _current;

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) return;
            StartWatcherLocked(MicKeyPath, ref _micKey, ref _micEvent, ref _micWait);
            StartWatcherLocked(CamKeyPath, ref _camKey, ref _camEvent, ref _camWait);
        }
        _ = Task.Run(RefreshNow);
    }

    private void ScheduleDebouncedRefresh()
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_disposed) return;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(90, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    RefreshNow();
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    public void RefreshNow()
    {
        lock (_sync)
        {
            if (_disposed) return;
        }

        var running = CollectRunningProcessAndPackageTokens();

        var micRecords = ReadConsentStore(MicKeyPath);
        var camRecords = ReadConsentStore(CamKeyPath);

        var micConsumers = PrivacySensorEvaluator.FilterActiveConsumers(micRecords, running);
        var camConsumers = PrivacySensorEvaluator.FilterActiveConsumers(camRecords, running);

        var next = new PrivacySensorSnapshot(
            IsMicActive: micConsumers.Length > 0,
            IsCamActive: camConsumers.Length > 0,
            MicConsumers: micConsumers,
            CamConsumers: camConsumers);

        bool changed = false;
        lock (_sync)
        {
            if (_disposed) return;
            if (next != _current)
            {
                _current = next;
                changed = true;
            }
        }

        if (changed)
        {
            PrivacyChanged?.Invoke(this, next);
        }
    }

    private static unsafe HashSet<string> CollectRunningProcessAndPackageTokens()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        char* pathBuf = stackalloc char[512];
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(p.ProcessName))
                    {
                        running.Add(p.ProcessName);
                    }

                    nint hProc = NativeMethods.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)p.Id);
                    if (hProc != 0)
                    {
                        try
                        {
                            uint len = 512;
                            if (NativeMethods.QueryFullProcessImageNameW(hProc, 0, pathBuf, ref len) && len > 0)
                            {
                                string fullPath = new(pathBuf, 0, (int)len);
                                AddWindowsAppsPackageTokens(fullPath, running);
                            }
                        }
                        finally
                        {
                            NativeMethods.CloseHandle(hProc);
                        }
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        return running;
    }

    private static void AddWindowsAppsPackageTokens(string fullPath, HashSet<string> running)
    {
        const string marker = @"\WindowsApps\";
        int idx = fullPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return;

        int start = idx + marker.Length;
        int slash = fullPath.IndexOf('\\', start);
        if (slash <= start) return;

        string pkgFullName = fullPath[start..slash];
        int firstUnderscore = pkgFullName.IndexOf('_');
        int lastDoubleUnderscore = pkgFullName.LastIndexOf("__", StringComparison.Ordinal);
        if (firstUnderscore > 0)
        {
            string pkgName = pkgFullName[..firstUnderscore];
            running.Add(pkgName);
            if (lastDoubleUnderscore > firstUnderscore && lastDoubleUnderscore + 2 < pkgFullName.Length)
            {
                string publisherId = pkgFullName[(lastDoubleUnderscore + 2)..];
                running.Add($"{pkgName}_{publisherId}");
            }
        }
    }

    private void StartWatcherLocked(string path, ref RegistryKey? key, ref AutoResetEvent? evt, ref RegisteredWaitHandle? wait)
    {
        if (wait is not null) return;
        try
        {
            key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            if (key is null) return;
            evt = new AutoResetEvent(false);
            var capturedKey = key;
            var capturedEvt = evt;
            wait = ThreadPool.RegisterWaitForSingleObject(
                evt,
                (_, _) =>
                {
                    lock (_sync)
                    {
                        if (_disposed) return;
                        ArmNotify(capturedKey, capturedEvt);
                    }
                    ScheduleDebouncedRefresh();
                },
                null,
                -1,
                executeOnlyOnce: false);
            ArmNotify(key, evt);
        }
        catch { }
    }

    private static void ArmNotify(RegistryKey key, AutoResetEvent evt)
    {
        try
        {
            _ = NativeMethods.RegNotifyChangeKeyValue(
                key.Handle.DangerousGetHandle(),
                bWatchSubtree: true,
                NativeMethods.REG_NOTIFY_CHANGE_NAME | NativeMethods.REG_NOTIFY_CHANGE_LAST_SET,
                evt.SafeWaitHandle.DangerousGetHandle(),
                fAsynchronous: true);
        }
        catch { }
    }

    private static List<ConsentStoreRecord> ReadConsentStore(string rootPath)
    {
        var list = new List<ConsentStoreRecord>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(rootPath, false);
            if (root is null) return list;

            foreach (string subName in root.GetSubKeyNames())
            {
                if (string.Equals(subName, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                {
                    using var nonPkg = root.OpenSubKey(subName, false);
                    if (nonPkg is null) continue;
                    foreach (string appName in nonPkg.GetSubKeyNames())
                    {
                        using var appKey = nonPkg.OpenSubKey(appName, false);
                        if (appKey is not null)
                            list.Add(ReadEntry(appName, isPackaged: false, appKey));
                    }
                }
                else
                {
                    using var pkgKey = root.OpenSubKey(subName, false);
                    if (pkgKey is not null)
                        list.Add(ReadEntry(subName, isPackaged: true, pkgKey));
                }
            }
        }
        catch { }

        return list;
    }

    private static ConsentStoreRecord ReadEntry(string name, bool isPackaged, RegistryKey key)
    {
        long start = key.GetValue("LastUsedTimeStart") is long s ? s : 0;
        long stop = key.GetValue("LastUsedTimeStop") is long e ? e : 0;
        return new ConsentStoreRecord(name, isPackaged, start, stop);
    }

    public void Dispose()
    {
        RegisteredWaitHandle? micWait;
        RegisteredWaitHandle? camWait;
        AutoResetEvent? micEvent;
        AutoResetEvent? camEvent;
        RegistryKey? micKey;
        RegistryKey? camKey;
        CancellationTokenSource? debounceCts;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            micWait = _micWait; _micWait = null;
            camWait = _camWait; _camWait = null;
            micEvent = _micEvent; _micEvent = null;
            camEvent = _camEvent; _camEvent = null;
            micKey = _micKey; _micKey = null;
            camKey = _camKey; _camKey = null;
            debounceCts = _debounceCts; _debounceCts = null;
        }

        try { debounceCts?.Cancel(); } catch { }
        debounceCts?.Dispose();

        UnregisterAndWait(micWait);
        UnregisterAndWait(camWait);

        micEvent?.Dispose();
        camEvent?.Dispose();
        micKey?.Dispose();
        camKey?.Dispose();
    }

    private static void UnregisterAndWait(RegisteredWaitHandle? waitHandle)
    {
        if (waitHandle is null) return;
        try
        {
            using var completed = new ManualResetEvent(false);
            if (waitHandle.Unregister(completed))
            {
                completed.WaitOne(500);
            }
        }
        catch { }
    }
}
