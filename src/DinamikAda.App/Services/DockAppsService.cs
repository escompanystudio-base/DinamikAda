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
    long ProcessStartedUtcTicks,
    nint RestoreWindowHwnd,
    ImageSource? Icon);

public enum TrayAppActionResult { Succeeded, Stale, Failed }

/// <summary>
/// Manages pinned/running bottom dock items, low-memory bounded 48x48 frozen icon cache,
/// Shell_Tray pinned shortcut import, and Recycle Bin status without polling churn.
/// </summary>
public sealed unsafe class DockAppsService
{
    private const int MaxIconCacheEntries = 64;
    private const int MaxStartIconCacheEntries = 96;

    private readonly AppConfig _config;
    private readonly Action _saveConfig;

    private readonly Dictionary<uint, string> _pidExeCache = new();
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _iconCacheOrder = new();
    private readonly Dictionary<string, ImageSource> _startIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _startIconCacheOrder = new();
    private readonly Dictionary<string, ImageSource> _trayIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _trayIconCacheOrder = new();
    private readonly List<PinnedEntry> _pinnedEntries = new();
    private bool _pinnedLoaded;
    private string _loadedPinsSignature = string.Empty;

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
        // Migration: a non-empty saved list means the user already customized the dock.
        if (_config.PinnedDockApps.Count > 0) _config.DockPinsCustomized = true;

        _pinnedEntries.Clear();
        _pinnedEntries.AddRange(BuildEffectivePinnedEntries(_config));
        _loadedPinsSignature = ComputePinsSignature(_config);
    }

    /// <summary>
    /// Reloads pinned entries when another component (e.g. the Settings window) rewrote
    /// <see cref="AppConfig.PinnedDockApps"/> since the last load. Returns true when reloaded.
    /// </summary>
    public bool ReloadIfConfigChanged()
    {
        if (!_pinnedLoaded) return false;
        if (string.Equals(ComputePinsSignature(_config), _loadedPinsSignature, StringComparison.Ordinal)) return false;
        ReloadPinnedEntries();
        return true;
    }

    /// <summary>
    /// Launch targets currently shown as pinned, in Dock order. When the user never customized
    /// the Dock this is the auto-imported Windows taskbar pin list (or the built-in fallbacks).
    /// </summary>
    public static IReadOnlyList<string> ResolveEffectivePinnedTargets(AppConfig config)
        => BuildEffectivePinnedEntries(config).Select(e => e.LaunchTarget).ToList();

    public static bool IsPinnableTarget(string? path)
        => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    /// <summary>Friendly name and icon for a pin target, for list editors.</summary>
    public static bool TryDescribePinTarget(string path, out string displayName, out ImageSource? icon)
    {
        displayName = string.Empty;
        icon = null;
        if (!TryBuildPinnedEntry(path, out var entry)) return false;
        displayName = entry.DisplayName;
        icon = ExtractFrozenIcon(entry.IconSourcePath ?? entry.LaunchTarget);
        return true;
    }

    private static List<PinnedEntry> BuildEffectivePinnedEntries(AppConfig config)
    {
        var entries = new List<PinnedEntry>();

        foreach (string raw in config.PinnedDockApps)
        {
            if (TryBuildPinnedEntry(raw, out var entry))
            {
                AddUniquePinnedEntry(entries, entry);
            }
        }

        if (config.PinnedDockApps.Count > 0 || config.DockPinsCustomized) return entries;

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
                        AddUniquePinnedEntry(entries, entry);
                    }
                }
            }
            catch
            {
                // Ignore inaccessible shortcut folder cleanly.
            }
        }

        if (entries.Count == 0)
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string explorer = Path.Combine(winDir, "explorer.exe");
            string notepad = Path.Combine(winDir, "System32", "notepad.exe");
            string ps = Path.Combine(winDir, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

            foreach (string fallback in new[] { explorer, ps, notepad })
            {
                if (File.Exists(fallback) && TryBuildPinnedEntry(fallback, out var entry))
                {
                    AddUniquePinnedEntry(entries, entry);
                }
            }
        }

        return entries;
    }

    private static string ComputePinsSignature(AppConfig config)
        => (config.DockPinsCustomized ? "C|" : "A|") + string.Join("\n", config.PinnedDockApps);

    /// <summary>
    /// Pins a file, shortcut, executable or folder (e.g. dropped from Explorer).
    /// Inserts at <paramref name="insertIndex"/> among pinned items, or appends when null.
    /// Returns false when the path is not pinnable or already pinned (an existing pin is moved
    /// to <paramref name="insertIndex"/> instead).
    /// </summary>
    public bool PinPath(string path, int? insertIndex = null)
    {
        EnsurePinnedLoaded();
        if (!TryBuildPinnedEntry(path, out var entry)) return false;

        int existing = FindPinnedIndex(entry);
        if (existing >= 0)
        {
            // insertIndex is an insertion slot (0..n); removing the item first shifts later slots left.
            if (insertIndex is int idx) MovePinnedAt(existing, idx > existing ? idx - 1 : idx);
            return false;
        }

        int at = insertIndex is int i ? Math.Clamp(i, 0, _pinnedEntries.Count) : _pinnedEntries.Count;
        _pinnedEntries.Insert(at, entry);
        PersistPinnedOrder();
        return true;
    }

    /// <summary>Moves the pinned item with <paramref name="key"/> to <paramref name="newIndex"/>.</summary>
    public bool MovePinned(string key, int newIndex)
    {
        EnsurePinnedLoaded();
        int from = _pinnedEntries.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        return from >= 0 && MovePinnedAt(from, newIndex);
    }

    private bool MovePinnedAt(int from, int newIndex)
    {
        int to = Math.Clamp(newIndex, 0, _pinnedEntries.Count - 1);
        if (to == from) return false;
        var entry = _pinnedEntries[from];
        _pinnedEntries.RemoveAt(from);
        _pinnedEntries.Insert(to, entry);
        PersistPinnedOrder();
        return true;
    }

    private int FindPinnedIndex(PinnedEntry entry)
        => _pinnedEntries.FindIndex(p =>
            string.Equals(p.Key, entry.Key, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(p.TargetExePath) && !string.IsNullOrWhiteSpace(entry.TargetExePath)
                && string.Equals(NormalizePath(p.TargetExePath), NormalizePath(entry.TargetExePath), StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Writes the in-memory pin order to config. Saved targets that are currently unavailable
    /// (e.g. on an unplugged drive) are kept at the end instead of being silently dropped.
    /// </summary>
    private void PersistPinnedOrder()
    {
        var ordered = _pinnedEntries.Select(p => p.LaunchTarget).ToList();
        foreach (string raw in _config.PinnedDockApps)
        {
            if (!IsPinnableTarget(raw)
                && !ordered.Any(o => string.Equals(NormalizePath(o), NormalizePath(raw), StringComparison.OrdinalIgnoreCase)))
            {
                ordered.Add(raw);
            }
        }

        _config.PinnedDockApps = ordered;
        _config.DockPinsCustomized = true;
        _loadedPinsSignature = ComputePinsSignature(_config);
        _saveConfig();
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
        _config.DockPinsCustomized = true;

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
            _loadedPinsSignature = ComputePinsSignature(_config);
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

            _loadedPinsSignature = ComputePinsSignature(_config);
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
        var visiblePids = new HashSet<uint>();
        var trayPids = new HashSet<uint>();
        var hiddenWindows = new Dictionary<uint, (nint Hwnd, string Title)>();
        var paths = new Dictionary<uint, string>();
        var registered = ReadRegisteredTraySettings();
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        string ReadPath(uint pid)
        {
            if (paths.TryGetValue(pid, out var cached)) return cached;
            var raw = ResolveExePathUncached(pid);
            return paths[pid] = string.IsNullOrWhiteSpace(raw) ? string.Empty : NormalizePath(raw);
        }

        NativeMethods.EnumWindows((hwnd, _lParam) =>
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId) return true;

            string cls = ReadWindowClass(hwnd);
            if (IsTrayHostWindowClass(cls)) trayPids.Add(pid);

            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != 0 ||
                (NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_TOOLWINDOW) != 0 ||
                (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            {
                return true;
            }

            string title = ReadWindowTitle(hwnd);
            bool frameHost = cls.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase);
            uint ownerPid = frameHost ? ResolveUwpChildPid(hwnd, pid) : pid;
            if (ownerPid == 0) ownerPid = pid;

            if (NativeMethods.IsWindowVisible(hwnd))
            {
                if (WindowControlMath.IsEligibleExternalWindowMetadata(cls, title)) visiblePids.Add(ownerPid);
            }
            else if (IsEligibleHiddenMainWindow(cls, title, hwnd, out string usableTitle) &&
                     (!hiddenWindows.TryGetValue(ownerPid, out var previous) || usableTitle.Length > previous.Title.Length))
            {
                hiddenWindows[ownerPid] = (hwnd, usableTitle);
            }
            return true;
        }, 0);

        // The registry remembers old icons. Only use it to name an app or corroborate an
        // existing hidden window. Windows Security is a shell entry with a documented URI.
        var candidates = new HashSet<uint>(trayPids);
        foreach (uint pid in hiddenWindows.Keys)
            if (registered.ContainsKey(ReadPath(pid))) candidates.Add(pid);

        var steamPids = new List<uint>();
        string securityExe = Path.Combine(winDir, "System32", "SecurityHealthSystray.exe");
        using var self = Process.GetCurrentProcess();
        CollectDesktopProcesses("SecurityHealthSystray", pid =>
        {
            if (ReadPath(pid).Equals(securityExe, StringComparison.OrdinalIgnoreCase)) candidates.Add(pid);
        });

        if (trayPids.Any(pid => string.Equals(Path.GetFileName(ReadPath(pid)), "steamwebhelper.exe", StringComparison.OrdinalIgnoreCase)))
            CollectDesktopProcesses("steam", steamPids.Add);

        void CollectDesktopProcesses(string name, Action<uint> collect)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                using (proc)
                {
                    try
                    {
                        if (proc.SessionId == self.SessionId) collect((uint)proc.Id);
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }

        var visiblePaths = visiblePids.Select(ReadPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new Dictionary<string, BackgroundTrayAppItem>(StringComparer.OrdinalIgnoreCase);
        foreach (uint candidate in candidates.Order())
        {
            uint pid = candidate;
            string path = ReadPath(pid);
            if (string.IsNullOrWhiteSpace(path)) continue;
            string exeName = Path.GetFileNameWithoutExtension(path);
            if (exeName.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase))
            {
                pid = steamPids.FirstOrDefault(steamPid =>
                {
                    string? root = Path.GetDirectoryName(ReadPath(steamPid));
                    return !string.IsNullOrEmpty(root) && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                });
                if (pid == 0) continue;
                path = ReadPath(pid);
                exeName = Path.GetFileNameWithoutExtension(path);
            }

            if (visiblePids.Contains(pid) || visiblePaths.Contains(path) ||
                DockLayoutCalculator.IsIgnoredBackgroundTrayProcess(exeName, path, winDir))
            {
                continue;
            }

            long started = ReadProcessStartTicks(pid);
            if (started == 0) continue;
            string? aumid = TryResolveAumid(pid);
            string key = string.IsNullOrWhiteSpace(aumid) ? path : aumid;
            hiddenWindows.TryGetValue(pid, out var hidden);
            if (matches.TryGetValue(key, out var current) && (current.RestoreWindowHwnd != 0 || hidden.Hwnd == 0)) continue;
            registered.TryGetValue(path, out string? registryTooltip);
            matches[key] = new BackgroundTrayAppItem(key,
                ResolveFriendlyTrayDisplayName(exeName, path, hidden.Title, registryTooltip),
                path, aumid, pid, started, hidden.Hwnd, null);
        }

        return matches.Values
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item with { Icon = GetOrLoadBackgroundIcon(item.ExePath!, item.RestoreWindowHwnd) })
            .ToArray();
    }

    public static Task<TrayAppActionResult> ActivateBackgroundTrayAppAsync(BackgroundTrayAppItem item) =>
        Task.Run(() => ActivateBackgroundTrayApp(item));

    private static TrayAppActionResult ActivateBackgroundTrayApp(BackgroundTrayAppItem item)
    {
        try
        {
            if (!IsCurrentTrayProcess(item)) return TrayAppActionResult.Stale;
            if (IsOwnedTrayWindow(item))
            {
                nint hwnd = item.RestoreWindowHwnd;
                if (NativeMethods.IsHungAppWindow(hwnd)) return TrayAppActionResult.Failed;

                bool isMinimized = NativeMethods.IsIconic(hwnd);
                bool preferExecutable = WindowRestoreGeometry.ShouldPreferExecutableActivation(
                    NativeMethods.IsWindowVisible(hwnd), isMinimized, ReadWindowClass(hwnd), item.ExePath);
                if (preferExecutable && item.ExePath is not null && File.Exists(item.ExePath) &&
                    StartExecutableCleanly(item.ExePath))
                {
                    for (int i = 0; i < 12; i++)
                    {
                        Thread.Sleep(40);
                        if (!IsOwnedTrayWindow(item) || !IsCurrentTrayProcess(item)) return TrayAppActionResult.Stale;
                        if (NativeMethods.IsWindowVisible(hwnd)) break;
                    }
                }
                if (isMinimized || !NativeMethods.IsWindowVisible(hwnd))
                {
                    uint style = (uint)NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE).ToInt64();
                    bool isMaximized = NativeMethods.IsZoomed(hwnd) || (style & NativeMethods.WS_MAXIMIZE) != 0;
                    int show = WindowRestoreGeometry.DetermineRestoreShowCommand(isMinimized, isMaximized);
                    if (!NativeMethods.ShowWindowAsync(hwnd, show)) return TrayAppActionResult.Failed;
                    for (int i = 0; i < 12; i++)
                    {
                        Thread.Sleep(40);
                        if (!IsOwnedTrayWindow(item)) return TrayAppActionResult.Stale;
                        if (NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd)) break;
                    }
                }

                if (!IsCurrentTrayProcess(item) || !IsOwnedTrayWindow(item)) return TrayAppActionResult.Stale;
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return TrayAppActionResult.Failed;
                WindowRestoreGeometry.EnsureChromiumRenderChildrenVisible(hwnd);
                WindowRestoreGeometry.EnsureWindowWorkAreaVisible(hwnd);
                ForceForeground(hwnd);
                return TrayAppActionResult.Succeeded;
            }

            string name = Path.GetFileNameWithoutExtension(item.ExePath) ?? string.Empty;
            if (name.Equals("SecurityHealthSystray", StringComparison.OrdinalIgnoreCase))
                return OpenTrayTarget("windowsdefender:");
            if (name.Equals("steam", StringComparison.OrdinalIgnoreCase))
                return OpenTrayTarget("steam://open/main");
            if (name.Equals("lghub_system_tray", StringComparison.OrdinalIgnoreCase) && item.ExePath is not null)
            {
                string? dir = Path.GetDirectoryName(Path.GetDirectoryName(item.ExePath));
                string? target = dir is null ? null : Path.Combine(dir, "lghub.exe");
                if (target is not null && File.Exists(target))
                    return StartExecutableCleanly(target) ? TrayAppActionResult.Succeeded : TrayAppActionResult.Failed;
            }

            if (!string.IsNullOrWhiteSpace(item.Aumid))
                return OpenTrayTarget("explorer.exe", $"shell:AppsFolder\\{item.Aumid}");

            return item.ExePath is not null && File.Exists(item.ExePath) && StartExecutableCleanly(item.ExePath)
                ? TrayAppActionResult.Succeeded : TrayAppActionResult.Failed;
        }
        catch (Exception)
        {
            return TrayAppActionResult.Failed;
        }
    }

    private static TrayAppActionResult OpenTrayTarget(string target, string arguments = "")
    {
        using var opened = Process.Start(new ProcessStartInfo(target, arguments) { UseShellExecute = true });
        return TrayAppActionResult.Succeeded;
    }

    public static bool OpenAppFileLocation(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return false;
        try
        {
            using var opened = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exePath}\"") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static TrayAppActionResult RequestCloseTrayWindow(BackgroundTrayAppItem item)
    {
        if (!IsCurrentTrayProcess(item) || !IsOwnedTrayWindow(item)) return TrayAppActionResult.Stale;
        return NativeMethods.PostMessageW(item.RestoreWindowHwnd, NativeMethods.WM_CLOSE, 0, 0)
            ? TrayAppActionResult.Succeeded : TrayAppActionResult.Failed;
    }

    private static bool IsOwnedTrayWindow(BackgroundTrayAppItem item)
    {
        nint hwnd = item.RestoreWindowHwnd;
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == item.ProcessId ||
            (ReadWindowClass(hwnd).Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) &&
             ResolveUwpChildPid(hwnd, pid) == item.ProcessId);
    }

    private static bool IsCurrentTrayProcess(BackgroundTrayAppItem item)
    {
        return item.ProcessId != 0 && item.ProcessId != Environment.ProcessId && item.ProcessStartedUtcTicks > 0 &&
               ReadProcessStartTicks(item.ProcessId) == item.ProcessStartedUtcTicks &&
               string.Equals(ResolveExePathUncached(item.ProcessId), item.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    private static long ReadProcessStartTicks(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.StartTime.ToUniversalTime().Ticks;
        }
        catch (ArgumentException) { return 0; }
        catch (InvalidOperationException) { return 0; }
        catch (System.ComponentModel.Win32Exception) { return 0; }
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
        if (_trayIconCache.TryGetValue(cacheKey, out var cached))
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

        if (loaded is not { IsFrozen: true }) return null;

        while (_trayIconCache.Count >= MaxIconCacheEntries && _trayIconCacheOrder.Count > 0)
        {
            _trayIconCache.Remove(_trayIconCacheOrder.Dequeue());
        }
        _trayIconCache[cacheKey] = loaded;
        _trayIconCacheOrder.Enqueue(cacheKey);
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

    public bool IsStartAppPinned(AppLaunchItem app)
    {
        EnsurePinnedLoaded();
        string targetNorm = !string.IsNullOrWhiteSpace(app.LaunchTarget) && !app.IsStoreApp
            ? NormalizePath(app.LaunchTarget)
            : string.Empty;

        foreach (var pin in _pinnedEntries)
        {
            if (!string.IsNullOrWhiteSpace(targetNorm) &&
                (string.Equals(NormalizePath(pin.LaunchTarget), targetNorm, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(pin.TargetExePath) && string.Equals(NormalizePath(pin.TargetExePath), targetNorm, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
            if (string.Equals(pin.DisplayName, app.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public bool TryPinStartApp(AppLaunchItem app)
    {
        if (app.IsStoreApp || string.IsNullOrWhiteSpace(app.LaunchTarget) || !File.Exists(app.LaunchTarget))
        {
            return false;
        }
        var dummy = new DockAppItem(
            NormalizePath(app.LaunchTarget),
            app.DisplayName,
            app.LaunchTarget,
            string.Equals(Path.GetExtension(app.LaunchTarget), ".exe", StringComparison.OrdinalIgnoreCase) ? app.LaunchTarget : null,
            IsPinned: false,
            IsActiveForeground: false,
            Icon: null,
            Windows: []);
        return TogglePin(dummy);
    }

    public ImageSource? GetOrLoadStartAppIcon(AppLaunchItem app)
    {
        lock (_startIconCache)
        {
            return LoadStartAppIcon(app);
        }
    }

    private ImageSource? LoadStartAppIcon(AppLaunchItem app)
    {
        string key = app.IsStoreApp && !string.IsNullOrWhiteSpace(app.StoreAumid)
            ? $"aumid:{app.StoreAumid}"
            : NormalizePath(app.LaunchTarget);

        if (_startIconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? loaded = null;
        if (app.IsStoreApp && !string.IsNullOrWhiteSpace(app.StoreAumid))
        {
            loaded = TryExtractStoreAppIcon(app.StoreAumid);
        }
        else if (!string.IsNullOrWhiteSpace(app.LaunchTarget))
        {
            // Crisp 64 px shell images without the shortcut-arrow overlay: for a shortcut, prefer its target app.
            bool isLink = string.Equals(Path.GetExtension(app.LaunchTarget), ".lnk", StringComparison.OrdinalIgnoreCase);
            if (isLink && TryResolveShellLink(app.LaunchTarget, out string? resolvedExe, out string? resolvedIcon))
            {
                if (!string.IsNullOrWhiteSpace(resolvedExe) && File.Exists(resolvedExe))
                    loaded = TryExtractShellImage(resolvedExe, 64);
                if (loaded is null && !string.IsNullOrWhiteSpace(resolvedIcon))
                    loaded = ExtractFrozenIcon(resolvedIcon);
            }
            loaded ??= File.Exists(app.LaunchTarget) ? TryExtractShellImage(app.LaunchTarget, 64) : null;
            loaded ??= ExtractFrozenIcon(app.LaunchTarget);
        }

        if (loaded is not { IsFrozen: true }) return null;

        while (_startIconCache.Count >= MaxStartIconCacheEntries && _startIconCacheOrder.Count > 0)
        {
            string oldest = _startIconCacheOrder.Dequeue();
            _startIconCache.Remove(oldest);
        }

        _startIconCache[key] = loaded;
        _startIconCacheOrder.Enqueue(key);
        return loaded;
    }

    private static ImageSource? TryExtractStoreAppIcon(string aumid)
    {
        Guid iidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
        nint unk = 0;
        IShellItemImageFactory? factory = null;
        nint hBitmap = 0;

        try
        {
            int hr = NativeMethods.SHCreateItemFromParsingName(
                $"shell:AppsFolder\\{aumid}",
                0,
                iidShellItemImageFactory,
                out unk);
            if (hr != 0 || unk == 0) return null;

            factory = Marshal.GetObjectForIUnknown(unk) as IShellItemImageFactory;
            if (factory is null) return null;

            var sz = new ShellSize(32, 32);
            // SIIGBF_ICONONLY (0x04) gives transparent logos for UWP Store packages; fall back to default (0) if needed.
            if (factory.GetImage(sz, 0x04, out hBitmap) != 0 || hBitmap == 0)
            {
                if (factory.GetImage(sz, 0x00, out hBitmap) != 0 || hBitmap == 0)
                {
                    return null;
                }
            }

            var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                0,
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
            if (hBitmap != 0) NativeMethods.DeleteObject(hBitmap);
            if (factory is not null && Marshal.IsComObject(factory))
            {
                Marshal.FinalReleaseComObject(factory);
            }
            if (unk != 0)
            {
                Marshal.Release(unk);
            }
        }
    }

    /// <summary>
    /// The shell's own image for a file (icon only, no overlays) at <paramref name="px"/> pixels, with its alpha
    /// channel preserved. Null when the shell cannot provide one.
    /// </summary>
    private static ImageSource? TryExtractShellImage(string path, int px)
    {
        Guid iid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
        nint unk = 0;
        IShellItemImageFactory? factory = null;
        nint hBitmap = 0;
        try
        {
            if (NativeMethods.SHCreateItemFromParsingName(path, 0, iid, out unk) != 0 || unk == 0) return null;
            factory = Marshal.GetObjectForIUnknown(unk) as IShellItemImageFactory;
            if (factory is null) return null;
            // SIIGBF_ICONONLY (0x04) | SIIGBF_BIGGERSIZEOK (0x01): the icon itself, never a thumbnail or overlay.
            if (factory.GetImage(new ShellSize(px, px), 0x04 | 0x01, out hBitmap) != 0 || hBitmap == 0) return null;
            return HBitmapToAlphaSource(hBitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != 0) NativeMethods.DeleteObject(hBitmap);
            if (factory is not null && Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
            if (unk != 0) Marshal.Release(unk);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiBitmap
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetGdiObject(nint h, int cb, out GdiBitmap bm);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, byte[] bits, ref BitmapInfoHeader bmi, uint usage);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    /// <summary>Copies a 32-bit HBITMAP into a frozen premultiplied-BGRA image (top-down), keeping transparency.</summary>
    private static ImageSource? HBitmapToAlphaSource(nint hBitmap)
    {
        if (GetGdiObject(hBitmap, Marshal.SizeOf<GdiBitmap>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight), stride = w * 4;
        var bits = new byte[stride * h];
        var bmi = new BitmapInfoHeader
        {
            biSize = Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = w,
            biHeight = -h, // negative: top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        nint hdc = GetDC(0);
        try
        {
            if (GetDIBits(hdc, hBitmap, 0, (uint)h, bits, ref bmi, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(0, hdc);
        }
        // Some providers return 32-bit images with an empty alpha channel: treat those as opaque.
        bool anyAlpha = false;
        for (int i = 3; i < bits.Length; i += 4)
        {
            if (bits[i] != 0) { anyAlpha = true; break; }
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
        }
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, stride);
        src.Freeze();
        return src;
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

    private void AddUniquePinnedEntry(PinnedEntry entry) => AddUniquePinnedEntry(_pinnedEntries, entry);

    private static void AddUniquePinnedEntry(List<PinnedEntry> list, PinnedEntry entry)
    {
        if (list.Any(p =>
            string.Equals(p.Key, entry.Key, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(p.TargetExePath) && !string.IsNullOrWhiteSpace(entry.TargetExePath)
                && string.Equals(NormalizePath(p.TargetExePath), NormalizePath(entry.TargetExePath), StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }
        list.Add(entry);
    }

    private static bool TryBuildPinnedEntry(string rawPath, out PinnedEntry entry)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(rawPath)) return false;

        if (Directory.Exists(rawPath))
        {
            string dirPath = NormalizePath(rawPath);
            string dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dirPath));
            if (string.IsNullOrWhiteSpace(dirName)) dirName = dirPath; // drive root such as D:\
            entry = new PinnedEntry(dirPath, dirName, dirPath, null, dirPath);
            return true;
        }

        if (!File.Exists(rawPath)) return false;

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
        return char.IsLower(trimmed[0]) ? char.ToUpperInvariant(trimmed[0]) + trimmed[1..] : trimmed;
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ShellSize(int cx, int cy)
    {
        public readonly int cx = cx;
        public readonly int cy = cy;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(ShellSize size, uint flags, out nint phbm);
    }
}
