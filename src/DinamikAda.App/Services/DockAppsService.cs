using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DinamikAda.Core.Config;
using DinamikAda.Modules.CommandHub;
using DinamikAda.Platform.Win32.Interop;
using DinamikAda.Platform.Win32.Windowing;

namespace DinamikAda.App.Services;

public sealed record DockWindowRef(
    nint Hwnd,
    uint ProcessId,
    string Title,
    string ExePath,
    bool IsMinimized);

public sealed record DockAppItem(
    string Key,
    string DisplayName,
    string LaunchTarget,
    string? ExePath,
    bool IsPinned,
    bool IsActiveForeground,
    ImageSource? Icon,
    IReadOnlyList<DockWindowRef> Windows)
{
    public bool IsRunning => Windows.Count > 0;
}

public readonly record struct RecycleBinSummary(long ItemCount, long SizeBytes)
{
    public bool HasItems => ItemCount > 0;
}

public sealed record BackgroundTrayAppItem(
    string Key,
    string DisplayName,
    string? ExePath,
    string? Aumid,
    uint ProcessId,
    nint RestoreWindowHwnd,
    string TrayWindowClass,
    ImageSource? Icon);

/// <summary>
/// Manages pinned/running bottom dock items, low-memory bounded 48x48 frozen icon cache,
/// Shell_Tray pinned shortcut import, and Recycle Bin status without polling churn.
/// </summary>
public sealed unsafe class DockAppsService
{
    private const int MaxIconCacheEntries = 64;

    private readonly AppConfig _config;
    private readonly Action _saveConfig;

    private readonly Dictionary<uint, string> _pidExeCache = new();
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _iconCacheOrder = new();
    private readonly List<PinnedEntry> _pinnedEntries = new();
    private bool _pinnedLoaded;

    private sealed record PinnedEntry(
        string Key,
        string DisplayName,
        string LaunchTarget,
        string? TargetExePath,
        string? IconSourcePath);

    public DockAppsService(AppConfig config, Action saveConfig)
    {
        _config = config;
        _saveConfig = saveConfig;
    }

    public void EnsurePinnedLoaded()
    {
        if (_pinnedLoaded) return;
        _pinnedLoaded = true;
        ReloadPinnedEntries();
    }

    public void ReloadPinnedEntries()
    {
        _pinnedEntries.Clear();

        if (_config.PinnedDockApps is { Count: > 0 } saved)
        {
            foreach (string raw in saved)
            {
                if (TryBuildPinnedEntry(raw, out var entry))
                {
                    AddUniquePinnedEntry(entry);
                }
            }
        }

        if (_pinnedEntries.Count == 0)
        {
            string pinnedDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

            if (Directory.Exists(pinnedDir))
            {
                try
                {
                    foreach (string lnk in Directory.EnumerateFiles(pinnedDir, "*.lnk").OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
                    {
                        if (TryBuildPinnedEntry(lnk, out var entry))
                        {
                            AddUniquePinnedEntry(entry);
                        }
                    }
                }
                catch
                {
                    // Ignore inaccessible shortcut folder cleanly.
                }
            }
        }

        if (_pinnedEntries.Count == 0)
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string explorer = Path.Combine(winDir, "explorer.exe");
            string notepad = Path.Combine(winDir, "System32", "notepad.exe");
            string ps = Path.Combine(winDir, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

            foreach (string fallback in new[] { explorer, ps, notepad })
            {
                if (File.Exists(fallback) && TryBuildPinnedEntry(fallback, out var entry))
                {
                    AddUniquePinnedEntry(entry);
                }
            }
        }
    }

    public IReadOnlyList<DockAppItem> BuildSnapshot(out nint foregroundHwnd)
    {
        EnsurePinnedLoaded();

        foregroundHwnd = NativeMethods.GetForegroundWindow();
        var openWindows = EnumerateAppWindows();

        // Prune dead PIDs from cache so unmanaged/process strings don't accumulate.
        if (_pidExeCache.Count > 96)
        {
            var livePids = openWindows.Select(w => w.ProcessId).ToHashSet();
            foreach (uint stale in _pidExeCache.Keys.Where(k => !livePids.Contains(k)).ToList())
            {
                _pidExeCache.Remove(stale);
            }
        }

        var usedWindowHwnds = new HashSet<nint>();
        var items = new List<DockAppItem>(_pinnedEntries.Count + 8);

        foreach (var pin in _pinnedEntries)
        {
            var matched = new List<DockWindowRef>();
            foreach (var w in openWindows)
            {
                if (usedWindowHwnds.Contains(w.Hwnd)) continue;
                if (MatchesPinnedEntry(pin, w))
                {
                    matched.Add(w);
                    usedWindowHwnds.Add(w.Hwnd);
                }
            }

            nint fg = foregroundHwnd;
            bool isFg = matched.Any(w => w.Hwnd == fg);
            string iconKey = !string.IsNullOrWhiteSpace(pin.IconSourcePath)
                ? pin.IconSourcePath
                : (!string.IsNullOrWhiteSpace(pin.TargetExePath) ? pin.TargetExePath : pin.LaunchTarget);
            nint sampleHwnd = matched.Count > 0 ? matched[0].Hwnd : 0;

            items.Add(new DockAppItem(
                pin.Key,
                pin.DisplayName,
                pin.LaunchTarget,
                pin.TargetExePath,
                IsPinned: true,
                IsActiveForeground: isFg,
                Icon: GetOrLoadIcon(iconKey, sampleHwnd),
                Windows: matched));
        }

        // Remaining unpinned running apps, grouped by normalized exe path.
        var unpinnedGroups = new Dictionary<string, List<DockWindowRef>>(StringComparer.OrdinalIgnoreCase);
        var groupOrder = new List<string>();
        foreach (var w in openWindows)
        {
            if (usedWindowHwnds.Contains(w.Hwnd)) continue;
            string groupKey = !string.IsNullOrWhiteSpace(w.ExePath)
                ? NormalizePath(w.ExePath)
                : $"pid:{w.ProcessId}";

            if (!unpinnedGroups.TryGetValue(groupKey, out var list))
            {
                list = new List<DockWindowRef>();
                unpinnedGroups[groupKey] = list;
                groupOrder.Add(groupKey);
            }
            list.Add(w);
        }

        foreach (string key in groupOrder)
        {
            var groupWindows = unpinnedGroups[key];
            var first = groupWindows[0];
            string exePath = first.ExePath;
            string displayName = !string.IsNullOrWhiteSpace(exePath)
                ? Path.GetFileNameWithoutExtension(exePath)
                : first.Title;

            nint fg = foregroundHwnd;
            bool isFg = groupWindows.Any(w => w.Hwnd == fg);

            items.Add(new DockAppItem(
                key,
                FormatFriendlyAppName(displayName),
                !string.IsNullOrWhiteSpace(exePath) ? exePath : string.Empty,
                exePath,
                IsPinned: false,
                IsActiveForeground: isFg,
                Icon: GetOrLoadIcon(!string.IsNullOrWhiteSpace(exePath) ? exePath : key, first.Hwnd),
                Windows: groupWindows));
        }

        return items;
    }

    public bool TogglePin(DockAppItem item)
    {
        EnsurePinnedLoaded();

        if (item.IsPinned)
        {
            if (_config.PinnedDockApps.Count == 0)
            {
                // Persist current auto-imported pins first before removing one.
                _config.PinnedDockApps = _pinnedEntries.Select(p => p.LaunchTarget).ToList();
            }

            _config.PinnedDockApps.RemoveAll(p =>
                string.Equals(NormalizePath(p), item.Key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, item.LaunchTarget, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(item.ExePath) && string.Equals(NormalizePath(p), NormalizePath(item.ExePath), StringComparison.OrdinalIgnoreCase)));

            // If user just removed from default TaskBar list, leave explicit list (or sentinel if empty)
            _pinnedEntries.RemoveAll(p => string.Equals(p.Key, item.Key, StringComparison.OrdinalIgnoreCase));
            _saveConfig();
            return false;
        }
        else
        {
            string target = !string.IsNullOrWhiteSpace(item.LaunchTarget)
                ? item.LaunchTarget
                : (item.ExePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(target)) return false;

            if (_config.PinnedDockApps.Count == 0 && _pinnedEntries.Count > 0)
            {
                _config.PinnedDockApps = _pinnedEntries.Select(p => p.LaunchTarget).ToList();
            }

            if (!_config.PinnedDockApps.Any(p => string.Equals(NormalizePath(p), NormalizePath(target), StringComparison.OrdinalIgnoreCase)))
            {
                _config.PinnedDockApps.Add(target);
            }

            if (TryBuildPinnedEntry(target, out var entry))
            {
                AddUniquePinnedEntry(entry);
            }

            _saveConfig();
            return true;
        }
    }

    public static bool LaunchItem(DockAppItem item)
    {
        string target = !string.IsNullOrWhiteSpace(item.LaunchTarget)
            ? item.LaunchTarget
            : (item.ExePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(target)) return false;

        try
        {
            string? workDir = Path.GetDirectoryName(target);
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = !string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir) ? workDir : string.Empty
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RestoreShowAndFocusWindow(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return;

        bool isIconic = NativeMethods.IsIconic(hwnd);
        uint style = (uint)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        bool isMaximized = NativeMethods.IsZoomed(hwnd) || (style & NativeMethods.WS_MAXIMIZE) != 0;

        if (!NativeMethods.IsWindowVisible(hwnd) || isIconic)
        {
            NativeMethods.ShowWindow(hwnd, WindowRestoreGeometry.DetermineRestoreShowCommand(isIconic, isMaximized));
        }

        WindowRestoreGeometry.EnsureChromiumRenderChildrenVisible(hwnd);
        WindowRestoreGeometry.EnsureWindowWorkAreaVisible(hwnd);
        ForceForeground(hwnd);
    }

    private static bool StartExecutableCleanly(string exePath)
    {
        try
        {
            string? workDir = Path.GetDirectoryName(exePath);
            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = !string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir) ? workDir : string.Empty
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void FocusOrToggleWindow(DockWindowRef targetWindow, nint currentForegroundHwnd)
    {
        if (targetWindow.Hwnd == 0 || !NativeMethods.IsWindow(targetWindow.Hwnd)) return;

        if (NativeMethods.IsIconic(targetWindow.Hwnd))
        {
            RestoreShowAndFocusWindow(targetWindow.Hwnd);
            return;
        }

        if (targetWindow.Hwnd == currentForegroundHwnd)
        {
            NativeMethods.ShowWindow(targetWindow.Hwnd, NativeMethods.SW_MINIMIZE);
            return;
        }

        RestoreShowAndFocusWindow(targetWindow.Hwnd);
    }

    public static void FocusWindow(nint hwnd)
    {
        RestoreShowAndFocusWindow(hwnd);
    }

    public static void CloseWindow(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return;
        NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, 0, 0);
    }

    public static RecycleBinSummary QueryRecycleBin()
    {
        try
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = sizeof(NativeMethods.SHQUERYRBINFO)
            };
            int hr = NativeMethods.SHQueryRecycleBinW(null, ref info);
            return hr == 0
                ? new RecycleBinSummary(Math.Max(0, info.i64NumItems), Math.Max(0, info.i64Size))
                : default;
        }
        catch
        {
            return default;
        }
    }

    public static void OpenRecycleBin()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell launch errors.
        }
    }

    public static void EmptyRecycleBin(nint ownerHwnd)
    {
        try
        {
            _ = NativeMethods.SHEmptyRecycleBinW(ownerHwnd, null, 0);
        }
        catch
        {
            // Ignore shell empty errors.
        }
    }

    public IReadOnlyList<BackgroundTrayAppItem> QueryBackgroundTrayApps()
    {
        int ownPid = Environment.ProcessId;
        var visibleTaskbarPids = new HashSet<uint>();
        var trayCandidates = new Dictionary<uint, string>();
        var hiddenMainWindows = new Dictionary<uint, (nint Hwnd, string Title, string Cls)>();

        NativeMethods.EnumWindows((hwnd, _lParam) =>
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == ownPid) return true;

            string cls = ReadWindowClass(hwnd);
            if (string.IsNullOrEmpty(cls)) return true;

            bool vis = NativeMethods.IsWindowVisible(hwnd);
            if (vis && NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) == 0)
            {
                long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
                if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) == 0 &&
                    !(NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0))
                {
                    string title = ReadWindowTitle(hwnd);
                    if (WindowControlMath.IsEligibleExternalWindowMetadata(cls, title))
                    {
                        visibleTaskbarPids.Add(pid);
                        if (string.Equals(cls, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
                        {
                            uint innerPid = ResolveUwpChildPid(hwnd, pid);
                            if (innerPid != 0) visibleTaskbarPids.Add(innerPid);
                        }
                    }
                }
            }

            if (IsTrayHostWindowClass(cls))
            {
                trayCandidates.TryAdd(pid, cls);
            }

            if (!vis && IsEligibleHiddenMainWindow(cls, ReadWindowTitle(hwnd), hwnd, out string winTitle))
            {
                if (!hiddenMainWindows.TryGetValue(pid, out var prev) || winTitle.Length > prev.Title.Length)
                {
                    hiddenMainWindows[pid] = (hwnd, winTitle, cls);
                }
            }

            return true;
        }, 0);

        // Windows 11 registers real system-tray executables in HKCU\Control Panel\NotifyIconSettings.
        // Matching active processes strictly against registered tray entries + explicit tray helper windows
        // prevents non-tray daemons (mysqld, MoNotificationUx, subagent helpers) from cluttering the menu.
        var registeredTrayMeta = ReadRegisteredTraySettings();

        uint steamMainPid = 0;
        string? steamMainExe = null;
        var runningExeToPid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                uint pid = (uint)proc.Id;
                if (pid == 0 || pid == ownPid) continue;
                string exe = GetExePathCached(pid);
                if (string.IsNullOrWhiteSpace(exe)) continue;
                string norm = NormalizePath(exe);
                runningExeToPid.TryAdd(norm, pid);

                if (steamMainPid == 0 && proc.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase))
                {
                    steamMainPid = pid;
                    steamMainExe = norm;
                }
            }
            catch
            {
                // Ignore exited processes.
            }
            finally
            {
                proc.Dispose();
            }
        }

        var candidatePids = new HashSet<uint>(trayCandidates.Keys);
        foreach (var kv in runningExeToPid)
        {
            if (registeredTrayMeta.ContainsKey(kv.Key))
            {
                candidatePids.Add(kv.Value);
            }
        }

        var visibleExePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (uint vpid in visibleTaskbarPids)
        {
            string vp = GetExePathCached(vpid);
            if (!string.IsNullOrWhiteSpace(vp))
            {
                visibleExePaths.Add(NormalizePath(vp));
            }
        }

        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<BackgroundTrayAppItem>(16);

        foreach (uint pid in candidatePids)
        {
            if (visibleTaskbarPids.Contains(pid)) continue;

            string exePath = GetExePathCached(pid);
            if (string.IsNullOrWhiteSpace(exePath)) continue;

            string normExe = NormalizePath(exePath);
            string exeName = Path.GetFileNameWithoutExtension(normExe);
            if (DockLayoutCalculator.IsIgnoredBackgroundTrayProcess(exeName, normExe, winDir)) continue;

            nint restoreHwnd = hiddenMainWindows.TryGetValue(pid, out var hidden) ? hidden.Hwnd : 0;
            uint effectivePid = pid;
            string effectiveExe = normExe;
            string effectiveName = exeName;

            if (string.Equals(exeName, "steamwebhelper", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(steamMainExe))
            {
                effectivePid = steamMainPid;
                effectiveExe = steamMainExe;
                effectiveName = "Steam";
            }

            if (visibleExePaths.Contains(effectiveExe)) continue;

            string? aumid = TryResolveAumid(effectivePid);
            string uniqueKey = !string.IsNullOrWhiteSpace(aumid) ? aumid : effectiveExe;
            if (!seenKeys.Add(uniqueKey)) continue;

            registeredTrayMeta.TryGetValue(effectiveExe, out string? registryTooltip);
            string friendly = ResolveFriendlyTrayDisplayName(effectiveName, effectiveExe, hidden.Title, registryTooltip);
            ImageSource? icon = GetOrLoadBackgroundIcon(effectiveExe, restoreHwnd);
            string trayCls = trayCandidates.GetValueOrDefault(pid, hidden.Cls ?? string.Empty);

            results.Add(new BackgroundTrayAppItem(
                Key: uniqueKey,
                DisplayName: friendly,
                ExePath: effectiveExe,
                Aumid: aumid,
                ProcessId: effectivePid,
                RestoreWindowHwnd: restoreHwnd,
                TrayWindowClass: trayCls,
                Icon: icon));
        }

        results.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    public static bool ActivateBackgroundTrayApp(BackgroundTrayAppItem item)
    {
        try
        {
            string exeName = Path.GetFileNameWithoutExtension(item.ExePath) ?? string.Empty;

            if (exeName.Equals("SecurityHealthSystray", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true });
                return true;
            }

            if (exeName.Equals("steam", StringComparison.OrdinalIgnoreCase))
            {
                if (item.RestoreWindowHwnd != 0 &&
                    NativeMethods.IsWindow(item.RestoreWindowHwnd) &&
                    NativeMethods.IsWindowVisible(item.RestoreWindowHwnd))
                {
                    RestoreShowAndFocusWindow(item.RestoreWindowHwnd);
                    return true;
                }

                Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });
                return true;
            }

            if (exeName.Equals("lghub_system_tray", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.ExePath))
            {
                string? dir = Path.GetDirectoryName(Path.GetDirectoryName(item.ExePath));
                string? lghubExe = string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "lghub.exe");
                if (!string.IsNullOrWhiteSpace(lghubExe) && File.Exists(lghubExe))
                {
                    Process.Start(new ProcessStartInfo(lghubExe) { UseShellExecute = true, WorkingDirectory = dir });
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.Aumid))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{item.Aumid}")
                {
                    UseShellExecute = true
                });
                return true;
            }

            if (item.RestoreWindowHwnd != 0 && NativeMethods.IsWindow(item.RestoreWindowHwnd))
            {
                nint hwnd = item.RestoreWindowHwnd;
                string cls = WindowRestoreGeometry.ReadWindowClassSafe(hwnd);
                bool preferSingleInstanceExe =
                    WindowRestoreGeometry.ShouldPreferExecutableActivation(
                        NativeMethods.IsWindowVisible(hwnd),
                        NativeMethods.IsIconic(hwnd),
                        cls,
                        item.ExePath) &&
                    File.Exists(item.ExePath);

                if (preferSingleInstanceExe && StartExecutableCleanly(item.ExePath!))
                {
                    _ = Task.Delay(550).ContinueWith(_ =>
                    {
                        if (!NativeMethods.IsWindow(hwnd)) return;
                        if (!NativeMethods.IsWindowVisible(hwnd))
                        {
                            RestoreShowAndFocusWindow(hwnd);
                            return;
                        }
                        WindowRestoreGeometry.EnsureChromiumRenderChildrenVisible(hwnd);
                        WindowRestoreGeometry.EnsureWindowWorkAreaVisible(hwnd);
                    }, TaskScheduler.Default);
                    return true;
                }

                RestoreShowAndFocusWindow(hwnd);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(item.ExePath) && File.Exists(item.ExePath))
            {
                return StartExecutableCleanly(item.ExePath);
            }
        }
        catch
        {
            // Ignore shell activation failures.
        }
        return false;
    }

    public static void OpenAppFileLocation(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore explorer launch errors.
        }
    }

    public static void QuitBackgroundTrayApp(BackgroundTrayAppItem item)
    {
        try
        {
            using var proc = Process.GetProcessById((int)item.ProcessId);
            if (!proc.HasExited)
            {
                if (item.RestoreWindowHwnd != 0 && NativeMethods.IsWindow(item.RestoreWindowHwnd))
                {
                    NativeMethods.PostMessageW(item.RestoreWindowHwnd, NativeMethods.WM_CLOSE, 0, 0);
                    if (proc.WaitForExit(1200)) return;
                }
                if (!proc.CloseMainWindow() || !proc.WaitForExit(1200))
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            // Process may have exited cleanly already.
        }
    }

    public static bool IsTrayHostWindowClass(string? className) =>
        DockLayoutCalculator.IsTrayHostWindowClass(className);

    public static bool IsEligibleHiddenMainWindow(string className, string title, nint hwnd, out string cleanTitle)
    {
        cleanTitle = title;
        if (!DockLayoutCalculator.IsEligibleHiddenMainWindowMetadata(className, title)) return false;
        if (hwnd != 0 && NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0) return false;
        return WindowControlMath.IsEligibleExternalWindowMetadata(className, title);
    }

    private static Dictionary<string, string?> ReadRegisteredTraySettings()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is null) return map;

            var guidCache = new Dictionary<Guid, string?>();
            foreach (string subName in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(subName);
                if (sub is null) continue;
                string? rawExe = sub.GetValue("ExecutablePath") as string;
                if (string.IsNullOrWhiteSpace(rawExe)) continue;

                string expanded = DockLayoutCalculator.ExpandKnownFolderPrefix(rawExe, g =>
                {
                    if (guidCache.TryGetValue(g, out string? cached)) return cached;
                    var folderGuid = g;
                    string? resolved = NativeMethods.SHGetKnownFolderPath(ref folderGuid, 0, 0, out nint ptr) == 0 && ptr != 0
                        ? MarshalPtrToStringAndFree(ptr)
                        : null;
                    guidCache[g] = resolved;
                    return resolved;
                });

                if (string.IsNullOrWhiteSpace(expanded)) continue;
                string norm = NormalizePath(expanded);
                string? tip = sub.GetValue("InitialTooltip") as string;
                if (!map.ContainsKey(norm) || !string.IsNullOrWhiteSpace(tip))
                {
                    map[norm] = tip;
                }
            }
        }
        catch
        {
            // Registry read fallback cleanly.
        }
        return map;
    }

    private static string? MarshalPtrToStringAndFree(nint ptr)
    {
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static string ResolveFriendlyTrayDisplayName(string exeName, string exePath, string? hiddenTitle, string? registryTooltip)
    {
        if (exeName.Equals("wallpaper32", StringComparison.OrdinalIgnoreCase)) return "Wallpaper Engine";
        if (exeName.Equals("WhatsApp.Root", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        if (exeName.Equals("VKTablet", StringComparison.OrdinalIgnoreCase)) return "Veikk Tablet";
        if (exeName.Equals("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
        if (exeName.Equals("SecurityHealthSystray", StringComparison.OrdinalIgnoreCase)) return "Windows Security";
        if (exeName.Equals("RadeonSoftware", StringComparison.OrdinalIgnoreCase)) return "AMD Software";
        if (exeName.Equals("lghub_system_tray", StringComparison.OrdinalIgnoreCase)) return "Logitech G HUB";
        if (exeName.Equals("ksdeui", StringComparison.OrdinalIgnoreCase)) return "Kaspersky VPN";
        if (exeName.Equals("steam", StringComparison.OrdinalIgnoreCase)) return "Steam";

        if (!string.IsNullOrWhiteSpace(registryTooltip) &&
            registryTooltip.Length <= 32 &&
            !registryTooltip.Contains('\n') &&
            !registryTooltip.Contains('\r'))
        {
            return registryTooltip.Trim();
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.ProductName) &&
                info.ProductName.Length <= 32 &&
                !info.ProductName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                return info.ProductName.Trim();
            }
        }
        catch
        {
            // Fall back cleanly.
        }

        if (exeName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hiddenTitle, "ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return "ChatGPT";
        }

        return FormatFriendlyAppName(exeName);
    }

    private static string? TryResolveAumid(uint pid)
    {
        if (pid == 0) return null;
        nint hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == 0) return null;
        try
        {
            uint len = 260;
            char* buf = stackalloc char[260];
            if (NativeMethods.GetApplicationUserModelId(hProc, ref len, buf) == 0 && len > 1)
            {
                return new string(buf, 0, (int)len - 1);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hProc);
        }
        return null;
    }

    private ImageSource? GetOrLoadBackgroundIcon(string exePath, nint restoreHwnd)
    {
        string cacheKey = $"tray:{exePath}";
        if (_iconCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        ImageSource? loaded = null;
        // MSIX/WindowsApps binaries often restrict direct file icon lookup; check window HICON first for Store apps.
        bool isWindowsApps = exePath.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
        if (isWindowsApps && restoreHwnd != 0)
        {
            loaded = ExtractWindowHwndIcon(restoreHwnd);
        }
        loaded ??= ExtractFrozenIcon(exePath);
        loaded ??= (restoreHwnd != 0 ? ExtractWindowHwndIcon(restoreHwnd) : null);

        if (loaded is null) return null;

        while (_iconCache.Count >= MaxIconCacheEntries && _iconCacheOrder.Count > 0)
        {
            string oldest = _iconCacheOrder.Dequeue();
            _iconCache.Remove(oldest);
        }
        _iconCache[cacheKey] = loaded;
        _iconCacheOrder.Enqueue(cacheKey);
        return loaded;
    }

    private static void ForceForeground(nint targetHwnd)
    {
        _ = IslandWindowController.TryFocusExternalWindow(targetHwnd);
    }

    private List<DockWindowRef> EnumerateAppWindows()
    {
        var list = new List<DockWindowRef>(24);
        int ownPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _lParam) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0) return true;

            long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0)
            {
                return true;
            }

            string cls = ReadWindowClass(hwnd);
            string title = ReadWindowTitle(hwnd);
            if (!WindowControlMath.IsEligibleExternalWindowMetadata(cls, title)) return true;

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == ownPid) return true;

            // Unwrap UWP ApplicationFrameWindow child CoreWindow PID when available.
            if (string.Equals(cls, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
            {
                uint innerPid = ResolveUwpChildPid(hwnd, pid);
                if (innerPid != 0) pid = innerPid;
            }

            string exePath = GetExePathCached(pid);
            bool minimized = NativeMethods.IsIconic(hwnd);
            list.Add(new DockWindowRef(hwnd, pid, title, exePath, minimized));
            return true;
        }, 0);

        return list;
    }

    private static uint ResolveUwpChildPid(nint frameHwnd, uint framePid)
    {
        uint found = 0;
        NativeMethods.EnumChildWindows(frameHwnd, (child, _lParam) =>
        {
            _ = NativeMethods.GetWindowThreadProcessId(child, out uint cpid);
            if (cpid != 0 && cpid != framePid)
            {
                found = cpid;
                return false;
            }
            return true;
        }, 0);
        return found;
    }

    private string GetExePathCached(uint pid)
    {
        if (_pidExeCache.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        string resolved = ResolveExePathUncached(pid);
        _pidExeCache[pid] = resolved;
        return resolved;
    }

    private static string ResolveExePathUncached(uint pid)
    {
        nint hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc != 0)
        {
            try
            {
                char* buf = stackalloc char[512];
                uint len = 512;
                if (NativeMethods.QueryFullProcessImageNameW(hProc, 0, buf, ref len) && len > 0)
                {
                    return new string(buf, 0, (int)len);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }
        }
        return string.Empty;
    }

    private ImageSource? GetOrLoadIcon(string? path, nint fallbackHwnd = 0)
    {
        if (string.IsNullOrWhiteSpace(path) && fallbackHwnd == 0) return null;
        string normalized = !string.IsNullOrWhiteSpace(path)
            ? NormalizePath(path)
            : $"hwnd:{fallbackHwnd}";
        if (_iconCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        ImageSource? loaded = (!string.IsNullOrWhiteSpace(path) ? ExtractFrozenIcon(path) : null)
            ?? (fallbackHwnd != 0 ? ExtractWindowHwndIcon(fallbackHwnd) : null);
        if (loaded is null) return null;

        while (_iconCache.Count >= MaxIconCacheEntries && _iconCacheOrder.Count > 0)
        {
            string oldest = _iconCacheOrder.Dequeue();
            _iconCache.Remove(oldest);
        }

        _iconCache[normalized] = loaded;
        _iconCacheOrder.Enqueue(normalized);
        return loaded;
    }

    private static ImageSource? ExtractWindowHwndIcon(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return null;
        try
        {
            nint hIcon = 0;
            foreach (int kind in new[] { NativeMethods.ICON_BIG, NativeMethods.ICON_SMALL2, NativeMethods.ICON_SMALL })
            {
                if (NativeMethods.SendMessageTimeoutPtr(
                    hwnd,
                    NativeMethods.WM_GETICON,
                    kind,
                    0,
                    NativeMethods.SMTO_ABORTIFHUNG,
                    60,
                    out nint res) != 0 && res != 0)
                {
                    hIcon = res;
                    break;
                }
            }

            if (hIcon == 0)
            {
                hIcon = NativeMethods.GetClassLongPtrW(hwnd, NativeMethods.GCLP_HICON);
            }
            if (hIcon == 0)
            {
                hIcon = NativeMethods.GetClassLongPtrW(hwnd, NativeMethods.GCLP_HICONSM);
            }
            if (hIcon == 0) return null;

            // Window-owned HICON must NOT be destroyed via DestroyIcon.
            // Extract exact 32x32 native icon to avoid double-resampling blur at 1:1 DIP pixels.
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? ExtractFrozenIcon(string path)
    {
        if (!File.Exists(path)) return null;

        nint hIcon = 0;
        try
        {
            NativeMethods.SHFILEINFOW shfi = default;
            nuint ok = NativeMethods.SHGetFileInfoW(
                path,
                0,
                &shfi,
                (uint)sizeof(NativeMethods.SHFILEINFOW),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (ok != 0 && shfi.hIcon != 0)
            {
                hIcon = shfi.hIcon;
            }
            else
            {
                nint hLarge = 0;
                nint hSmall = 0;
                uint count = NativeMethods.ExtractIconExW(path, 0, &hLarge, &hSmall, 1);
                if (count > 0)
                {
                    hIcon = hLarge != 0 ? hLarge : hSmall;
                    if (hSmall != 0 && hSmall != hIcon) NativeMethods.DestroyIcon(hSmall);
                }
            }

            if (hIcon == 0) return null;

            // Extract exact 32x32 native icon to avoid double-resampling blur at 1:1 DIP pixels.
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != 0)
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
    }

    private static bool MatchesPinnedEntry(PinnedEntry pin, DockWindowRef window)
        => WindowRestoreGeometry.MatchesPinnedAppIdentity(
            pin.TargetExePath,
            pin.IconSourcePath,
            pin.DisplayName,
            window.ExePath,
            window.Title);

    private void AddUniquePinnedEntry(PinnedEntry entry)
    {
        if (_pinnedEntries.Any(p =>
            string.Equals(p.Key, entry.Key, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(p.TargetExePath) && !string.IsNullOrWhiteSpace(entry.TargetExePath)
                && string.Equals(NormalizePath(p.TargetExePath), NormalizePath(entry.TargetExePath), StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }
        _pinnedEntries.Add(entry);
    }

    private static bool TryBuildPinnedEntry(string rawPath, out PinnedEntry entry)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(rawPath) || !File.Exists(rawPath)) return false;

        string fullPath = NormalizePath(rawPath);
        string ext = Path.GetExtension(fullPath);
        string displayName = Path.GetFileNameWithoutExtension(fullPath);
        string? exeTarget = string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ? fullPath : null;
        string? iconSource = fullPath;

        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            if (TryResolveShellLink(fullPath, out string? resolvedTarget, out string? resolvedIcon)
                && !string.IsNullOrWhiteSpace(resolvedTarget))
            {
                if (File.Exists(resolvedTarget))
                {
                    exeTarget = resolvedTarget;
                }
            }

            if (!string.IsNullOrWhiteSpace(resolvedIcon) && File.Exists(resolvedIcon))
            {
                iconSource = resolvedIcon;
            }
            else if (!string.IsNullOrWhiteSpace(exeTarget))
            {
                iconSource = exeTarget;
            }
        }

        string key = !string.IsNullOrWhiteSpace(exeTarget) ? NormalizePath(exeTarget) : fullPath;
        entry = new PinnedEntry(key, FormatFriendlyAppName(displayName), fullPath, exeTarget, iconSource);
        return true;
    }

    private static bool TryResolveShellLink(string lnkPath, out string? targetPath, out string? iconPath)
    {
        targetPath = null;
        iconPath = null;
        IShellLinkW? link = null;
        try
        {
            link = (IShellLinkW)new ShellLinkCoClass();
            ((IPersistFile)link).Load(lnkPath, 0);

            char* targetBuf = stackalloc char[520];
            if (link.GetPath(targetBuf, 520, 0, 0) == 0)
            {
                string rawTarget = new string(targetBuf).TrimEnd('\0').Trim();
                if (!string.IsNullOrWhiteSpace(rawTarget))
                {
                    targetPath = Environment.ExpandEnvironmentVariables(rawTarget);
                }
            }

            char* iconBuf = stackalloc char[520];
            if (link.GetIconLocation(iconBuf, 520, out _) == 0)
            {
                string rawIcon = new string(iconBuf).TrimEnd('\0').Trim();
                if (!string.IsNullOrWhiteSpace(rawIcon))
                {
                    iconPath = Environment.ExpandEnvironmentVariables(rawIcon);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string FormatFriendlyAppName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Uygulama";
        string trimmed = raw.Trim();
        return char.IsLower(trimmed[0]) ? char.ToUpper(trimmed[0]) + trimmed[1..] : trimmed;
    }

    private static string ReadWindowClass(nint hwnd)
    {
        char* buf = stackalloc char[128];
        int len = NativeMethods.GetClassNameW(hwnd, buf, 128);
        return len > 0 ? new string(buf, 0, len).Trim() : string.Empty;
    }

    private static string ReadWindowTitle(nint hwnd)
    {
        int len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        int cap = Math.Min(len + 1, 256);
        char* buf = stackalloc char[cap];
        int read = NativeMethods.GetWindowTextW(hwnd, buf, cap);
        return read > 0 ? new string(buf, 0, read).Trim() : string.Empty;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath(char* pszFile, int cch, nint pfd, uint fFlags);
        [PreserveSig] int GetIDList(out nint ppidl);
        [PreserveSig] int SetIDList(nint pidl);
        [PreserveSig] int GetDescription(char* pszName, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory(char* pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments(char* pszArgs, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out short pwHotkey);
        [PreserveSig] int SetHotkey(short wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation(char* pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(nint hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
