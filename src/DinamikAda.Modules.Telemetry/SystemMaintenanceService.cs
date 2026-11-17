using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Modules.Telemetry;

public sealed record TempCleanupResult(
    long ScannedBytes,
    long FreedBytes,
    int DeletedFileCount,
    int SkippedLockedCount,
    int DeletedDirectoryCount);

public sealed record MemoryTrimResult(
    int TrimmedProcessCount,
    long WorkingSetReducedBytes,
    long AvailableMemoryBeforeBytes,
    long AvailableMemoryAfterBytes,
    uint MemoryLoadBeforePercent,
    uint MemoryLoadAfterPercent)
{
    public long AvailableMemoryDeltaBytes => AvailableMemoryAfterBytes - AvailableMemoryBeforeBytes;
}

public sealed record IdleBackgroundAppCandidate(
    int PrimaryPid,
    ImmutableArray<int> Pids,
    string ProcessName,
    double MemoryMb,
    double CpuPercent,
    bool IsHung,
    int InstanceCount,
    string? WindowTitle);

public sealed record CloseBackgroundAppsResult(
    int ClosedProcessCount,
    int FailedCount,
    double EstimatedMemoryMb);

/// <summary>
/// Real Windows maintenance operations for System tab:
/// user Temp/CrashDump cleanup, OS working-set trimming, and user-confirmed idle background app closing.
/// All reported numbers come strictly from filesystem / Win32 APIs (never fabricated).
/// </summary>
public sealed class SystemMaintenanceService
{
    // Installers/archive extractors keep original timestamps and reuse %TEMP% files across stages,
    // so only files untouched (created AND written) for a full day are considered stale.
    private static readonly TimeSpan ActiveTempFileGraceAge = TimeSpan.FromHours(24);

    public static readonly HashSet<string> ExtraExcludedInfrastructureProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dllhost", "taskhostw", "ApplicationFrameHost", "SystemSettings", "TextInputHost",
        "SearchIndexer", "Widgets", "PhoneExperienceHost", "ShellHost", "AggregatorHost",
        "smartscreen", "spoolsv", "taskmgr", "RtkAudUService64", "nvcontainer",
        "AMDRSServ", "radeonsoftware", "igfxEM", "msedgewebview2", "OneDrive",
        "crashpad_handler", "GameBar", "GameBarFTServer", "LockApp", "UserOOBEBroker"
    };

    public static readonly HashSet<string> InteractiveDevAndBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "devenv", "code", "cursor", "windsurf", "rider64", "idea64", "clion64", "pycharm64", "webstorm64",
        "antigravity", "language_server", "dotnet", "msbuild", "vbcscompiler", "cargo", "rustc", "node", "python",
        "pwsh", "powershell", "cmd", "WindowsTerminal", "wt", "conemu64", "mintty",
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi"
    };

    private readonly object _sync = new();
    private Dictionary<int, (long CreateTime, long Total100ns)> _prevCpuSamples = new();
    private long _prevSampleTicks;
    private uint _procBufferSize = 512 * 1024;

    public static bool IsExcludedBackgroundApp(
        int pid,
        string? processName,
        uint sessionId,
        bool hasVisibleWindow,
        bool isForeground,
        bool hasActiveAudio,
        bool isHung)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return true;
        if (sessionId == 0) return true;

        string normalized = HungProcessService.NormalizeImageName(processName);
        if (HungProcessService.IsProtectedProcessName(normalized)) return true;
        if (ExtraExcludedInfrastructureProcesses.Contains(normalized)) return true;
        if (hasActiveAudio) return true;
        if (isForeground) return true;
        if (hasVisibleWindow && !isHung) return true;
        if (InteractiveDevAndBrowserProcesses.Contains(normalized) && !isHung) return true;

        return false;
    }

    public static string FormatBytes(long bytes, string lang = "tr")
    {
        double abs = Math.Abs((double)bytes);
        string sign = bytes < 0 ? "-" : string.Empty;
        if (abs >= 1024.0 * 1024.0 * 1024.0)
        {
            return $"{sign}{abs / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
        if (abs >= 1024.0 * 1024.0)
        {
            return $"{sign}{abs / (1024.0 * 1024.0):F1} MB";
        }
        if (abs >= 1024.0)
        {
            return $"{sign}{abs / 1024.0:F0} KB";
        }
        string byteUnit = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "B" : "B";
        return $"{sign}{abs:F0} {byteUnit}";
    }

    public static IReadOnlyList<string> GetDefaultTempCleanupDirectories()
    {
        var dirs = new List<string>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            try
            {
                string full = Path.GetFullPath(raw);
                if (Directory.Exists(full) && seen.Add(full))
                {
                    dirs.Add(full);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or UnauthorizedAccessException or IOException) { }
        }

        TryAdd(Path.GetTempPath());
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            TryAdd(Path.Combine(localAppData, "Temp"));
            TryAdd(Path.Combine(localAppData, "CrashDumps"));
        }

        return dirs;
    }

    public TempCleanupResult ExecuteTempCleanup(IReadOnlyList<string>? targetDirectories = null, TimeSpan? minFileAge = null, CancellationToken ct = default)
    {
        var dirs = targetDirectories ?? GetDefaultTempCleanupDirectories();
        TimeSpan grace = minFileAge ?? ActiveTempFileGraceAge;
        DateTime cutoffUtc = DateTime.UtcNow - grace;

        long scannedBytes = 0;
        long freedBytes = 0;
        int deletedFiles = 0;
        int skippedLocked = 0;
        int deletedDirs = 0;

        foreach (string root in dirs)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            try
            {
                var rootInfo = new DirectoryInfo(root);
                if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                CleanDirectoryRecursive(rootInfo, isRoot: true, cutoffUtc, ct, ref scannedBytes, ref freedBytes, ref deletedFiles, ref skippedLocked, ref deletedDirs);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skippedLocked++;
            }
        }

        return new TempCleanupResult(scannedBytes, freedBytes, deletedFiles, skippedLocked, deletedDirs);
    }

    public Task<TempCleanupResult> ExecuteTempCleanupAsync(IReadOnlyList<string>? targetDirectories = null, TimeSpan? minFileAge = null, CancellationToken ct = default) =>
        Task.Run(() => ExecuteTempCleanup(targetDirectories, minFileAge, ct), ct);

    private static void CleanDirectoryRecursive(
        DirectoryInfo dir,
        bool isRoot,
        DateTime cutoffUtc,
        CancellationToken ct,
        ref long scannedBytes,
        ref long freedBytes,
        ref int deletedFiles,
        ref int skippedLocked,
        ref int deletedDirs)
    {
        if (ct.IsCancellationRequested) return;
        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return;

        DirectoryInfo[] subDirs;
        try
        {
            subDirs = dir.GetDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            skippedLocked++;
            return;
        }

        foreach (var sub in subDirs)
        {
            if (ct.IsCancellationRequested) break;
            CleanDirectoryRecursive(sub, isRoot: false, cutoffUtc, ct, ref scannedBytes, ref freedBytes, ref deletedFiles, ref skippedLocked, ref deletedDirs);
        }

        FileInfo[] files;
        try
        {
            files = dir.GetFiles();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            skippedLocked++;
            return;
        }

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                long len = file.Length;
                scannedBytes += len;

                DateTime newestStampUtc = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
                if (newestStampUtc > cutoffUtc)
                {
                    skippedLocked++;
                    continue;
                }

                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    file.Attributes &= ~FileAttributes.ReadOnly;
                }

                file.Delete();
                freedBytes += len;
                deletedFiles++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skippedLocked++;
            }
        }

        if (!isRoot)
        {
            try
            {
                if (!dir.EnumerateFileSystemInfos().Any())
                {
                    dir.Delete(recursive: false);
                    deletedDirs++;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }

    public Task<MemoryTrimResult> TrimWorkingSetsAsync(CancellationToken ct = default) =>
        Task.Run(() => TrimWorkingSets(ct), ct);

    public MemoryTrimResult TrimWorkingSets(CancellationToken ct = default)
    {
        ReadMemoryStatus(out ulong totalBefore, out ulong availBefore, out uint loadBefore);

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        int trimmedCount = 0;
        long workingSetReducedBytes = 0;
        int selfPid = Environment.ProcessId;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (ct.IsCancellationRequested) break;
                int pid = proc.Id;
                if (pid <= 4) continue;

                string clean = HungProcessService.NormalizeImageName(proc.ProcessName);
                if (pid != selfPid && HungProcessService.IsProtectedProcessName(clean)) continue;

                long wsBefore = proc.WorkingSet64;
                if (wsBefore < 4 * 1024 * 1024 && pid != selfPid) continue;

                nint hProc = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    (uint)pid);
                if (hProc == 0) continue;

                try
                {
                    bool trimmed = NativeMethods.EmptyWorkingSet(hProc)
                        || NativeMethods.SetProcessWorkingSetSize(hProc, new nint(-1), new nint(-1));
                    if (trimmed)
                    {
                        trimmedCount++;
                        try
                        {
                            proc.Refresh();
                            long wsAfter = proc.WorkingSet64;
                            if (wsBefore > wsAfter)
                            {
                                workingSetReducedBytes += (wsBefore - wsAfter);
                            }
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(hProc);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally
            {
                proc.Dispose();
            }
        }

        if (!ct.IsCancellationRequested)
        {
            Thread.Sleep(60);
        }

        ReadMemoryStatus(out _, out ulong availAfter, out uint loadAfter);

        return new MemoryTrimResult(
            trimmedCount,
            workingSetReducedBytes,
            (long)availBefore,
            (long)availAfter,
            loadBefore,
            loadAfter);
    }

    public Task<ImmutableArray<IdleBackgroundAppCandidate>> ScanIdleBackgroundAppsAsync(
        IReadOnlySet<int>? activeAudioPids = null,
        CancellationToken ct = default) =>
        Task.Run(() => ct.IsCancellationRequested
            ? ImmutableArray<IdleBackgroundAppCandidate>.Empty
            : ScanIdleBackgroundApps(activeAudioPids), ct);

    public ImmutableArray<IdleBackgroundAppCandidate> ScanIdleBackgroundApps(IReadOnlySet<int>? activeAudioPids = null)
    {
        var visibleWindowPids = new HashSet<int>();
        var windowTitles = new Dictionary<int, string>();
        var hungPids = new HashSet<int>();

        nint fgHwnd = NativeMethods.GetForegroundWindow();
        int foregroundPid = 0;
        if (fgHwnd != 0)
        {
            _ = NativeMethods.GetWindowThreadProcessId(fgHwnd, out uint fgPidRaw);
            foregroundPid = (int)fgPidRaw;
        }

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0)
                return true;

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pidRaw);
            int pid = (int)pidRaw;
            if (pid <= 4 || pid == Environment.ProcessId) return true;

            if (NativeMethods.GetWindowRect(hwnd, out var rect) && (rect.Width <= 1 || rect.Height <= 1))
                return true;

            visibleWindowPids.Add(pid);
            string title = ReadWindowTitle(hwnd);
            if (!string.IsNullOrWhiteSpace(title) && !windowTitles.ContainsKey(pid))
            {
                windowTitles[pid] = title;
            }

            if (NativeMethods.IsHungAppWindow(hwnd))
            {
                hungPids.Add(pid);
            }
            return true;
        }, 0);

        var rows = QueryProcessesWithSessionAndMemory();
        long nowTicks = Stopwatch.GetTimestamp();
        int logicalCores = Math.Max(1, Environment.ProcessorCount);

        var grouped = new Dictionary<string, (List<int> Pids, int PrimaryPid, double TotalMb, double TotalCpu, bool IsHung, string? Title)>(
            StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            double elapsedSec = _prevSampleTicks > 0
                ? (nowTicks - _prevSampleTicks) / (double)Stopwatch.Frequency
                : 0.0;
            bool measureCpu = _prevSampleTicks > 0 && elapsedSec >= 0.40;
            var nextSamples = new Dictionary<int, (long CreateTime, long Total100ns)>(rows.Count);

            foreach (var p in rows)
            {
                nextSamples[p.Pid] = (p.CreateTime, p.TotalCpu100ns);

                double cpuPct = 0.0;
                if (measureCpu && _prevCpuSamples.TryGetValue(p.Pid, out var prev) && prev.CreateTime == p.CreateTime)
                {
                    cpuPct = HungProcessService.ComputeProcessCpuPercent(p.TotalCpu100ns - prev.Total100ns, elapsedSec, logicalCores);
                }

                bool isHung = hungPids.Contains(p.Pid);
                bool hasVisible = visibleWindowPids.Contains(p.Pid);
                bool isFg = p.Pid == foregroundPid;
                bool hasAudio = activeAudioPids is not null && activeAudioPids.Contains(p.Pid);

                if (IsExcludedBackgroundApp(p.Pid, p.Name, p.SessionId, hasVisible, isFg, hasAudio, isHung))
                    continue;

                double mb = p.PrivateWorkingSetBytes / (1024.0 * 1024.0);
                if (mb < 15.0 && !isHung) continue;
                if (cpuPct > 3.0 && !isHung) continue;

                string clean = HungProcessService.NormalizeImageName(p.Name);
                windowTitles.TryGetValue(p.Pid, out string? title);

                if (grouped.TryGetValue(clean, out var acc))
                {
                    acc.Pids.Add(p.Pid);
                    grouped[clean] = (
                        acc.Pids,
                        acc.PrimaryPid,
                        acc.TotalMb + mb,
                        acc.TotalCpu + cpuPct,
                        acc.IsHung || isHung,
                        !string.IsNullOrWhiteSpace(acc.Title) ? acc.Title : title);
                }
                else
                {
                    grouped[clean] = (new List<int> { p.Pid }, p.Pid, mb, cpuPct, isHung, title);
                }
            }

            _prevCpuSamples = nextSamples;
            _prevSampleTicks = nowTicks;
        }

        return grouped
            .Select(kvp => new IdleBackgroundAppCandidate(
                kvp.Value.PrimaryPid,
                kvp.Value.Pids.ToImmutableArray(),
                kvp.Key,
                Math.Round(kvp.Value.TotalMb, 0),
                Math.Round(Math.Clamp(kvp.Value.TotalCpu, 0.0, 100.0), 1),
                kvp.Value.IsHung,
                kvp.Value.Pids.Count,
                kvp.Value.Title))
            .OrderByDescending(x => x.IsHung)
            .ThenByDescending(x => x.MemoryMb)
            .Take(12)
            .ToImmutableArray();
    }

    public Task<CloseBackgroundAppsResult> CloseBackgroundAppsAsync(
        IReadOnlyList<IdleBackgroundAppCandidate> selectedCandidates,
        CancellationToken ct = default) =>
        Task.Run(() => CloseBackgroundApps(selectedCandidates, ct), ct);

    public CloseBackgroundAppsResult CloseBackgroundApps(
        IReadOnlyList<IdleBackgroundAppCandidate> selectedCandidates,
        CancellationToken ct = default)
    {
        if (selectedCandidates is null || selectedCandidates.Count == 0)
            return new CloseBackgroundAppsResult(0, 0, 0);

        int closed = 0;
        int failed = 0;
        double estMb = 0;
        var seenPids = new HashSet<int>();

        foreach (var candidate in selectedCandidates)
        {
            if (ct.IsCancellationRequested) break;
            if (IsExcludedBackgroundApp(candidate.PrimaryPid, candidate.ProcessName, sessionId: 1, hasVisibleWindow: false, isForeground: false, hasActiveAudio: false, isHung: candidate.IsHung))
            {
                failed++;
                continue;
            }

            bool anyClosedForCandidate = false;
            var pids = candidate.Pids.IsDefaultOrEmpty ? ImmutableArray.Create(candidate.PrimaryPid) : candidate.Pids;
            foreach (int pid in pids)
            {
                if (!seenPids.Add(pid)) continue;
                if (pid <= 4 || pid == Environment.ProcessId) continue;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    string clean = HungProcessService.NormalizeImageName(proc.ProcessName);
                    if (HungProcessService.IsProtectedProcessName(clean)
                        || ExtraExcludedInfrastructureProcesses.Contains(clean))
                    {
                        continue;
                    }

                    // The PID was captured at scan time and may have been recycled by another app.
                    if (!string.Equals(clean, HungProcessService.NormalizeImageName(candidate.ProcessName), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Every instance PID is listed in candidate.Pids, so killing just this process is
                    // enough; tree-kill would also take down unrelated child apps it launched.
                    proc.Kill(entireProcessTree: false);
                    anyClosedForCandidate = true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Process may already have exited cleanly with its parent tree.
                }
            }

            if (anyClosedForCandidate)
            {
                closed++;
                estMb += candidate.MemoryMb;
            }
            else
            {
                failed++;
            }
        }

        return new CloseBackgroundAppsResult(closed, failed, Math.Round(estMb, 0));
    }

    private readonly record struct RawMaintenanceProcRow(
        int Pid,
        uint SessionId,
        long CreateTime,
        long TotalCpu100ns,
        long PrivateWorkingSetBytes,
        string Name);

    private unsafe List<RawMaintenanceProcRow> QueryProcessesWithSessionAndMemory()
    {
        var list = new List<RawMaintenanceProcRow>(256);
        uint bufSize = Math.Max(512 * 1024u, _procBufferSize);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            nint mem = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                int status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemProcessInformation,
                    mem,
                    bufSize,
                    out uint retLen);

                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                {
                    bufSize = Math.Max(bufSize * 2, retLen + 65536u);
                    continue;
                }

                if (status != 0) break;

                _procBufferSize = Math.Max(bufSize, retLen + 32768u);

                nint cursor = mem;
                while (true)
                {
                    NativeMethods.SYSTEM_PROCESS_INFORMATION* spi = (NativeMethods.SYSTEM_PROCESS_INFORMATION*)cursor;
                    int pid = (int)spi->UniqueProcessId;
                    string name = spi->ImageNameLength > 0 && spi->ImageNameBuffer != 0
                        ? Marshal.PtrToStringUni(spi->ImageNameBuffer, spi->ImageNameLength / 2) ?? string.Empty
                        : "Idle";

                    long privBytes = spi->WorkingSetPrivateSize > 0
                        ? spi->WorkingSetPrivateSize
                        : (long)spi->WorkingSetSize;

                    list.Add(new RawMaintenanceProcRow(
                        pid,
                        spi->SessionId,
                        spi->CreateTime,
                        spi->KernelTime + spi->UserTime,
                        privBytes,
                        name));

                    if (spi->NextEntryOffset == 0) break;
                    cursor += (nint)spi->NextEntryOffset;
                }

                return list;
            }
            catch (Exception ex) when (ex is AccessViolationException or OutOfMemoryException)
            {
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                list.Add(new RawMaintenanceProcRow(
                    proc.Id,
                    (uint)Math.Max(0, proc.SessionId),
                    0,
                    proc.TotalProcessorTime.Ticks,
                    proc.WorkingSet64,
                    proc.ProcessName));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally
            {
                proc.Dispose();
            }
        }

        return list;
    }

    private static void ReadMemoryStatus(out ulong totalPhys, out ulong availPhys, out uint memLoadPct)
    {
        var ms = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref ms))
        {
            totalPhys = ms.ullTotalPhys;
            availPhys = ms.ullAvailPhys;
            memLoadPct = ms.dwMemoryLoad;
        }
        else
        {
            totalPhys = 0;
            availPhys = 0;
            memLoadPct = 0;
        }
    }

    private static unsafe string ReadWindowTitle(nint hwnd)
    {
        int len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0 || len >= 256) return string.Empty;
        char* buf = stackalloc char[len + 1];
        int read = NativeMethods.GetWindowTextW(hwnd, buf, len + 1);
        return read > 0 ? new string(buf, 0, read) : string.Empty;
    }
}
