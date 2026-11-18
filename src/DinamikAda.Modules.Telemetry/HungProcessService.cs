using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Modules.Telemetry;

public sealed class HungProcessService
{
    private readonly record struct ProcCpuSample(long CreateTime, long Total100ns, double LastCpuPct);

    public static readonly HashSet<string> ProtectedSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "svchost", "dwm", "explorer", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "fontdrvhost", "sihost", "ctfmon",
        "audiodg", "conhost", "RuntimeBroker", "SecurityHealthService", "MsMpEng",
        "WmiPrvSE", "DinamikAda.App"
    };

    private readonly object _sync = new();
    private Dictionary<int, ProcCpuSample> _prevCpuSamples = new();
    private long _prevScanTicks;
    private uint _cachedBufferSize = 512 * 1024;

    public static bool IsProtectedProcessName(string? name) =>
        string.IsNullOrWhiteSpace(name) || ProtectedSystemProcesses.Contains(name);

    public static string NormalizeImageName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
        string trimmed = rawName.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    public static double ComputeProcessCpuPercent(long deltaProc100ns, double elapsedSeconds, int logicalProcessorCount)
    {
        if (deltaProc100ns <= 0 || elapsedSeconds <= 0.01) return 0.0;
        double denom = elapsedSeconds * 10_000_000.0 * Math.Max(1, logicalProcessorCount);
        return Math.Clamp(deltaProc100ns * 100.0 / denom, 0.0, 100.0);
    }

    public void ResetCpuBaseline()
    {
        lock (_sync)
        {
            _prevCpuSamples.Clear();
            _prevScanTicks = 0;
        }
    }

    public void PrimeCpuBaseline()
    {
        var rawProcs = QuerySystemProcesses();
        long nowTicks = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            var next = new Dictionary<int, ProcCpuSample>(rawProcs.Count);
            foreach (var p in rawProcs)
            {
                double prevPct = _prevCpuSamples.TryGetValue(p.Pid, out var prev) && prev.CreateTime == p.CreateTime
                    ? prev.LastCpuPct
                    : 0.0;
                next[p.Pid] = new ProcCpuSample(p.CreateTime, p.TotalCpu100ns, prevPct);
            }
            _prevCpuSamples = next;
            _prevScanTicks = nowTicks;
        }
    }

    public Task<ImmutableArray<ProcessSummary>> ScanHeavyAndHungProcessesAsync(CancellationToken ct = default) =>
        Task.Run(() => ct.IsCancellationRequested ? ImmutableArray<ProcessSummary>.Empty : ScanHeavyAndHungProcesses(), ct);

    public ImmutableArray<ProcessSummary> ScanHeavyAndHungProcesses()
    {
        var candidates = new List<(nint Hwnd, int Pid)>();
        var windowTitles = new Dictionary<int, string>();
        var hungPids = new ConcurrentDictionary<int, string>();

        NativeMethods.EnumWindows((hwnd, _lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0)
                return true;

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;

            string title = ReadWindowTitle(hwnd);
            if (!string.IsNullOrWhiteSpace(title) && !windowTitles.ContainsKey((int)pid))
            {
                windowTitles[(int)pid] = title;
            }

            if (NativeMethods.IsHungAppWindow(hwnd))
            {
                hungPids.TryAdd((int)pid, title);
            }
            else
            {
                candidates.Add((hwnd, (int)pid));
            }
            return true;
        }, 0);

        if (candidates.Count > 0)
        {
            Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
            {
                if (hungPids.ContainsKey(item.Pid)) return;
                nint sent = NativeMethods.SendMessageTimeoutPtr(
                    item.Hwnd,
                    NativeMethods.WM_NULL,
                    0,
                    0,
                    NativeMethods.SMTO_ABORTIFHUNG,
                    // 50 ms flagged every briefly-busy UI thread (games, IDE builds) as hung, which also
                    // made it eligible for killing. Truly hung windows return instantly via ABORTIFHUNG.
                    1500,
                    out _);
                if (sent == 0)
                {
                    hungPids.TryAdd(item.Pid, ReadWindowTitle(item.Hwnd));
                }
            });
        }

        var rawProcs = QuerySystemProcesses();
        var items = new List<ProcessSummary>(Math.Min(32, rawProcs.Count));
        var groupedNonHung = new Dictionary<string, (int PrimaryPid, double BestPrimaryScore, string CleanName, string WindowTitle, double TotalMemoryMb, double TotalCpuPct, int Count)>(StringComparer.OrdinalIgnoreCase);
        long nowTicks = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            double elapsedSec = _prevScanTicks > 0
                ? (nowTicks - _prevScanTicks) / (double)Stopwatch.Frequency
                : 0.0;
            bool advanceCpuBaseline = _prevScanTicks == 0 || elapsedSec >= 0.40;
            int logicalCores = Math.Max(1, Environment.ProcessorCount);
            Dictionary<int, ProcCpuSample>? nextSamples = advanceCpuBaseline
                ? new Dictionary<int, ProcCpuSample>(rawProcs.Count)
                : null;

            foreach (var p in rawProcs)
            {
                double cpuPct = 0.0;
                if (_prevCpuSamples.TryGetValue(p.Pid, out var prev) && prev.CreateTime == p.CreateTime)
                {
                    if (_prevScanTicks > 0 && elapsedSec >= 0.40)
                    {
                        cpuPct = ComputeProcessCpuPercent(p.TotalCpu100ns - prev.Total100ns, elapsedSec, logicalCores);
                    }
                    else
                    {
                        cpuPct = prev.LastCpuPct;
                    }
                }

                if (nextSamples is not null)
                {
                    nextSamples[p.Pid] = new ProcCpuSample(p.CreateTime, p.TotalCpu100ns, cpuPct);
                }

                if (p.Pid <= 4 || p.Pid == Environment.ProcessId) continue;

                string cleanName = NormalizeImageName(p.Name);
                bool isProtected = IsProtectedProcessName(cleanName);
                bool isHung = hungPids.TryGetValue(p.Pid, out string? hungTitle);
                if (isProtected && !isHung) continue;

                double mb = p.PrivateWorkingSetBytes / (1024.0 * 1024.0);
                string title = !string.IsNullOrWhiteSpace(hungTitle)
                    ? hungTitle
                    : (windowTitles.TryGetValue(p.Pid, out string? visTitle) ? visTitle : string.Empty);

                if (isHung)
                {
                    items.Add(new ProcessSummary(
                        p.Pid,
                        cleanName,
                        title,
                        Math.Round(mb, 0),
                        true,
                        isProtected,
                        Math.Round(cpuPct, 1),
                        1));
                    continue;
                }

                double candidateScore = (!string.IsNullOrWhiteSpace(title) ? 100_000.0 : 0.0) + cpuPct * 1_000.0 + mb;
                if (groupedNonHung.TryGetValue(cleanName, out var acc))
                {
                    int primaryPid = candidateScore > acc.BestPrimaryScore ? p.Pid : acc.PrimaryPid;
                    double bestScore = Math.Max(candidateScore, acc.BestPrimaryScore);
                    string chosenTitle = !string.IsNullOrWhiteSpace(acc.WindowTitle) ? acc.WindowTitle : title;
                    groupedNonHung[cleanName] = (
                        primaryPid,
                        bestScore,
                        acc.CleanName,
                        chosenTitle,
                        acc.TotalMemoryMb + mb,
                        acc.TotalCpuPct + cpuPct,
                        acc.Count + 1);
                }
                else
                {
                    groupedNonHung[cleanName] = (p.Pid, candidateScore, cleanName, title, mb, cpuPct, 1);
                }
            }

            if (nextSamples is not null)
            {
                _prevCpuSamples = nextSamples;
                _prevScanTicks = nowTicks;
            }
        }

        foreach (var grp in groupedNonHung.Values)
        {
            double roundedMb = Math.Round(grp.TotalMemoryMb, 0);
            double roundedCpu = Math.Round(Math.Clamp(grp.TotalCpuPct, 0.0, 100.0), 1);
            if (roundedMb < 60 && roundedCpu < 0.3) continue;

            items.Add(new ProcessSummary(
                grp.PrimaryPid,
                grp.CleanName,
                grp.WindowTitle,
                roundedMb,
                false,
                false,
                roundedCpu,
                grp.Count));
        }

        if (items.Count == 0)
        {
            // No eligible heavy processes may be visible on a restricted or quiet desktop.
            // Keep the host's real memory visible and protect it from termination.
            try
            {
                using var self = Process.GetCurrentProcess();
                items.Add(new ProcessSummary(self.Id, NormalizeImageName(self.ProcessName), string.Empty,
                    Math.Round(self.WorkingSet64 / (1024.0 * 1024.0)), false, true));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
        }

        return items
            .OrderByDescending(x => x.IsHung)
            .ThenByDescending(x => x.CpuPercent >= 0.3)
            .ThenByDescending(x => x.CpuPercent >= 0.3 ? x.CpuPercent * 250.0 + x.MemoryMb : x.MemoryMb)
            .Take(8)
            .ToImmutableArray();
    }

    private readonly record struct RawProcRow(
        int Pid,
        long CreateTime,
        long TotalCpu100ns,
        long PrivateWorkingSetBytes,
        string Name);

    private unsafe List<RawProcRow> QuerySystemProcesses()
    {
        var list = new List<RawProcRow>(256);
        uint bufSize = Math.Max(512 * 1024u, _cachedBufferSize);

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

                if (status != 0)
                {
                    return QuerySystemProcessesManagedFallback();
                }

                _cachedBufferSize = Math.Max(bufSize, retLen + 32768u);

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

                    list.Add(new RawProcRow(
                        pid,
                        spi->CreateTime,
                        spi->KernelTime + spi->UserTime,
                        privBytes,
                        name));

                    if (spi->NextEntryOffset == 0) break;
                    cursor += (nint)spi->NextEntryOffset;
                }

                return list;
            }
            catch
            {
                return QuerySystemProcessesManagedFallback();
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }

        return QuerySystemProcessesManagedFallback();
    }

    private static List<RawProcRow> QuerySystemProcessesManagedFallback()
    {
        var list = new List<RawProcRow>(128);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                list.Add(new RawProcRow(
                    proc.Id,
                    0,
                    proc.TotalProcessorTime.Ticks,
                    proc.WorkingSet64,
                    proc.ProcessName));
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
        return list;
    }

    private static unsafe string ReadWindowTitle(nint hwnd)
    {
        int len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        len = Math.Min(len, 512);
        char* buf = stackalloc char[len + 1];
        int read = NativeMethods.GetWindowTextW(hwnd, buf, len + 1);
        return read > 0 ? new string(buf, 0, read) : string.Empty;
    }

    public bool TryKillProcessGroup(int primaryPid, string? processName, int processCount, out string? error)
    {
        if (processCount <= 1 || string.IsNullOrWhiteSpace(processName))
        {
            return TryKillProcess(primaryPid, processName, out error);
        }

        error = null;
        string normalized = NormalizeImageName(processName);
        if (IsProtectedProcessName(normalized))
        {
            error = $"Protected OS process '{normalized}' cannot be terminated.";
            return false;
        }

        bool killedAny = false;
        string? lastErr = null;
        foreach (var proc in Process.GetProcessesByName(normalized))
        {
            try
            {
                if (proc.Id <= 4 || proc.Id == Environment.ProcessId) continue;
                proc.Kill(entireProcessTree: true);
                killedAny = true;
            }
            catch (Exception ex)
            {
                lastErr = ex.Message;
            }
            finally
            {
                proc.Dispose();
            }
        }

        if (killedAny) return true;
        bool fallbackOk = TryKillProcess(primaryPid, normalized, out error);
        if (!fallbackOk && !string.IsNullOrWhiteSpace(lastErr))
        {
            error = lastErr;
        }
        return fallbackOk;
    }

    public bool TryKillProcess(int processId, out string? error) =>
        TryKillProcess(processId, expectedProcessName: null, out error);

    /// <summary>
    /// Kills <paramref name="processId"/> only if it still belongs to <paramref name="expectedProcessName"/>.
    /// PIDs come from an earlier scan; Windows recycles them, so without this check a stale PID could
    /// terminate an unrelated application (and its whole process tree).
    /// </summary>
    public bool TryKillProcess(int processId, string? expectedProcessName, out string? error)
    {
        error = null;
        if (processId <= 4 || processId == Environment.ProcessId)
        {
            error = "Protected OS or self process.";
            return false;
        }

        try
        {
            using var proc = Process.GetProcessById(processId);
            if (IsProtectedProcessName(proc.ProcessName))
            {
                error = $"Protected OS process '{proc.ProcessName}' cannot be terminated.";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(expectedProcessName) &&
                !string.Equals(NormalizeImageName(proc.ProcessName), NormalizeImageName(expectedProcessName), StringComparison.OrdinalIgnoreCase))
            {
                error = $"Process {processId} is no longer '{NormalizeImageName(expectedProcessName)}'.";
                return false;
            }
            proc.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
