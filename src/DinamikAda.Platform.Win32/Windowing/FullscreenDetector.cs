using System.Diagnostics;
using System.Runtime.InteropServices;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

/// <summary>
/// Event-driven composite fullscreen detector (SHQueryUserNotificationState + DWM extended frame rect vs rcMonitor/rcWork)
/// pumped on a dedicated STA Win32 message-loop thread so WINEVENT_OUTOFCONTEXT hooks always fire.
/// </summary>
public sealed unsafe class FullscreenDetector : IFullscreenDetector, IDisposable
{
    private static readonly HashSet<string> ShellExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp", "TextInputHost", "DinamikAda.App"
    };

    private static readonly HashSet<string> KnownDedicatedVideoProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc", "mpv",
        "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64",
        "potplayermini", "potplayermini64", "kmplayer", "kmplayer64x",
        "kodi", "plex", "stremio", "wmplayer", "netflix", "video.ui", "zunevideo"
    };

    private static readonly HashSet<string> KnownBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "zen", "vivaldi", "arc"
    };

    private static readonly HashSet<string> KnownProductivityProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "devenv", "rider64", "windowsterminal", "wt", "mstsc",
        "winword", "excel", "powerpnt", "obs64", "obs32", "notepad",
        "spotify", "discord", "slack", "teams", "ms-teams", "zoom",
        "notion", "obsidian", "figma", "photoshop", "premiere"
    };

    private readonly object _sync = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private nint _fgHook;
    private nint _minEndHook;
    private nint _locHook;
    private nint _destroyHook;
    private NativeMethods.WinEventDelegate? _winEventDelegate;
    private nint _lastExternalHwnd;
    private bool _started;
    private bool _disposed;
    private FullscreenSnapshot _current = new(false, FullscreenAppKind.None, null, 0, "Initial");

    public event EventHandler<FullscreenSnapshot>? FullscreenChanged;

    public FullscreenSnapshot Current
    {
        get { lock (_sync) return _current; }
    }

    public void Start() => StartMessageLoopHook();

    public void StartMessageLoopHook()
    {
        lock (_sync)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunHookMessageLoop(ready))
        {
            IsBackground = true,
            Name = "DinamikAda.FullscreenWinEventLoop"
        };
        thread.SetApartmentState(ApartmentState.STA);
        _hookThread = thread;
        thread.Start();
        ready.Task.Wait(TimeSpan.FromSeconds(2));
    }

    private void RunHookMessageLoop(TaskCompletionSource ready)
    {
        lock (_sync)
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            if (_disposed)
            {
                ready.TrySetResult();
                return;
            }
        }
        _winEventDelegate = OnWinEvent;

        const uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
        _fgHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            0, _winEventDelegate, 0, 0, flags);

        _minEndHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            0, _winEventDelegate, 0, 0, flags);

        _locHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
            0, _winEventDelegate, 0, 0, flags);

        _destroyHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_DESTROY,
            NativeMethods.EVENT_OBJECT_DESTROY,
            0, _winEventDelegate, 0, 0, flags);

        // Ensure thread message queue exists before signalling so PostThreadMessageW(WM_QUIT) never drops.
        _ = NativeMethods.PeekMessageW(out _, 0, 0, 0, 0);
        ready.TrySetResult();

        if (_disposed)
        {
            UnhookAllOnHookThread();
            return;
        }

        RefreshNow();

        try
        {
            while (!_disposed && NativeMethods.GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessageW(in msg);
            }
        }
        finally
        {
            UnhookAllOnHookThread();
        }
    }

    private void UnhookAllOnHookThread()
    {
        if (_fgHook != 0) { NativeMethods.UnhookWinEvent(_fgHook); _fgHook = 0; }
        if (_minEndHook != 0) { NativeMethods.UnhookWinEvent(_minEndHook); _minEndHook = 0; }
        if (_locHook != 0) { NativeMethods.UnhookWinEvent(_locHook); _locHook = 0; }
        if (_destroyHook != 0) { NativeMethods.UnhookWinEvent(_destroyHook); _destroyHook = 0; }
        _winEventDelegate = null;
        _hookThreadId = 0;
    }

    public void RefreshNow()
    {
        var next = EvaluateCurrent();
        bool changed = false;
        lock (_sync)
        {
            if (next != _current)
            {
                _current = next;
                changed = true;
            }
        }

        if (changed)
        {
            FullscreenChanged?.Invoke(this, next);
        }
    }

    public static FullscreenSnapshot EvaluatePure(
        int qunsState,
        string? processName,
        string? windowClass,
        uint windowStyle,
        uint windowExStyle,
        NativeMethods.RECT windowRect,
        NativeMethods.RECT monitorRect,
        nint monitorHandle) =>
        EvaluatePure(
            qunsState,
            processName,
            windowClass,
            windowStyle,
            windowExStyle,
            windowRect,
            monitorRect,
            monitorHandle,
            isZoomed: false,
            workRect: default,
            hasBrowserVideoCue: false);

    public static FullscreenSnapshot EvaluatePure(
        int qunsState,
        string? processName,
        string? windowClass,
        uint windowStyle,
        uint windowExStyle,
        NativeMethods.RECT windowRect,
        NativeMethods.RECT monitorRect,
        nint monitorHandle,
        bool isZoomed,
        NativeMethods.RECT workRect,
        bool hasBrowserVideoCue = false)
    {
        if (windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return new FullscreenSnapshot(false, FullscreenAppKind.None, processName, monitorHandle, "ShellWindow");
        }

        if (!string.IsNullOrWhiteSpace(processName) && ShellExcludedProcesses.Contains(processName))
        {
            return new FullscreenSnapshot(false, FullscreenAppKind.None, processName, monitorHandle, "ShellProcess");
        }

        if (windowRect.Width <= 0 || windowRect.Height <= 0 ||
            monitorRect.Width <= 0 || monitorRect.Height <= 0)
        {
            return new FullscreenSnapshot(false, FullscreenAppKind.None, processName, monitorHandle, "DegenerateRect");
        }

        bool coversMonitor =
            windowRect.Left <= monitorRect.Left + 2 &&
            windowRect.Top <= monitorRect.Top + 2 &&
            windowRect.Right >= monitorRect.Right - 2 &&
            windowRect.Bottom >= monitorRect.Bottom - 2;
        bool isToolWindow = (windowExStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

        bool isExclusiveQuns = qunsState is 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */ or 4 /* QUNS_PRESENTATION_MODE */;
        bool busyFullscreen = qunsState == 2 /* QUNS_BUSY */ && coversMonitor && !isToolWindow;

        bool workCoversMonitor =
            workRect.Width > 0 &&
            workRect.Height > 0 &&
            workRect.Left <= monitorRect.Left + 2 &&
            workRect.Top <= monitorRect.Top + 2 &&
            workRect.Right >= monitorRect.Right - 2 &&
            workRect.Bottom >= monitorRect.Bottom - 2;

        bool hasCaption = (windowStyle & NativeMethods.WS_CAPTION) != 0;
        bool isMaximized = isZoomed || (windowStyle & NativeMethods.WS_MAXIMIZE) != 0;
        if (!isExclusiveQuns && isMaximized && hasCaption && workCoversMonitor)
        {
            return new FullscreenSnapshot(false, FullscreenAppKind.None, processName, monitorHandle, "MaximizedWorkArea");
        }

        bool borderlessOrFullscreen = coversMonitor && !isToolWindow &&
            ((windowStyle & NativeMethods.WS_CAPTION) == 0 || isExclusiveQuns);

        if (!isExclusiveQuns && !busyFullscreen && !borderlessOrFullscreen)
        {
            return new FullscreenSnapshot(false, FullscreenAppKind.None, processName, monitorHandle, "Windowed");
        }

        FullscreenAppKind kind = ClassifyProcess(processName, qunsState, hasBrowserVideoCue);
        string reason = (isExclusiveQuns || busyFullscreen) ? $"QUNS={qunsState}" : "BorderlessMonitorCover";
        return new FullscreenSnapshot(true, kind, processName, monitorHandle, reason);
    }

    public static FullscreenAppKind ClassifyProcess(string? processName, int qunsState) =>
        ClassifyProcess(processName, qunsState, hasBrowserVideoCue: false);

    public static FullscreenAppKind ClassifyProcess(string? processName, int qunsState, bool hasBrowserVideoCue)
    {
        if (!string.IsNullOrWhiteSpace(processName))
        {
            if (KnownDedicatedVideoProcesses.Contains(processName))
            {
                return FullscreenAppKind.Video;
            }

            if (KnownBrowserProcesses.Contains(processName))
            {
                return (qunsState == 2 || hasBrowserVideoCue)
                    ? FullscreenAppKind.Video
                    : FullscreenAppKind.Productivity;
            }

            if (KnownProductivityProcesses.Contains(processName))
            {
                return FullscreenAppKind.Productivity;
            }
        }

        return qunsState == 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */
            ? FullscreenAppKind.Game
            : FullscreenAppKind.Productivity;
    }

    private FullscreenSnapshot EvaluateCurrent()
    {
        _ = NativeMethods.SHQueryUserNotificationState(out int quns);
        nint fg = NativeMethods.GetForegroundWindow();
        if (fg == 0)
        {
            lock (_sync)
            {
                _lastExternalHwnd = 0;
            }
            return new FullscreenSnapshot(false, FullscreenAppKind.None, null, 0, "NoForeground");
        }

        _ = NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        nint targetHwnd = fg;
        if (pid == Environment.ProcessId)
        {
            nint savedExternal;
            lock (_sync)
            {
                savedExternal = _lastExternalHwnd;
            }

            if (savedExternal == 0 || !NativeMethods.IsWindow(savedExternal))
            {
                return new FullscreenSnapshot(false, FullscreenAppKind.None, null, 0, "OwnProcessForeground");
            }

            targetHwnd = savedExternal;
            _ = NativeMethods.GetWindowThreadProcessId(targetHwnd, out pid);
        }
        else
        {
            lock (_sync)
            {
                _lastExternalHwnd = fg;
            }
        }

        if (!NativeMethods.IsWindow(targetHwnd) ||
            !NativeMethods.IsWindowVisible(targetHwnd) ||
            NativeMethods.IsIconic(targetHwnd))
        {
            return new FullscreenSnapshot(false, FullscreenAppKind.None, null, 0, "HiddenOrMinimized");
        }

        if (NativeMethods.DwmGetWindowAttribute(targetHwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 &&
            cloaked != 0)
        {
            return new FullscreenSnapshot(false, FullscreenAppKind.None, null, 0, "CloakedWindow");
        }

        string? procName = null;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            procName = proc.ProcessName;
        }
        catch
        {
            // Process exited mid-query
        }

        char* clsBuf = stackalloc char[128];
        int clsLen = NativeMethods.GetClassNameW(targetHwnd, clsBuf, 128);
        string? cls = clsLen > 0 ? new string(clsBuf, 0, clsLen) : null;

        NativeMethods.RECT winRect;
        if (NativeMethods.DwmGetWindowAttribute(targetHwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out winRect, sizeof(NativeMethods.RECT)) != 0 ||
            winRect.Width <= 0 || winRect.Height <= 0)
        {
            if (!NativeMethods.GetWindowRect(targetHwnd, out winRect))
            {
                return new FullscreenSnapshot(false, FullscreenAppKind.None, procName, 0, "NoWindowRect");
            }
        }

        nint hMon = NativeMethods.MonitorFromWindow(targetHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monInfo = new NativeMethods.MONITORINFOEXW { cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW) };
        bool hasMonInfo = hMon != 0 && NativeMethods.GetMonitorInfoW(hMon, ref monInfo);
        NativeMethods.RECT monRect = hasMonInfo ? monInfo.rcMonitor : default;
        NativeMethods.RECT workRect = hasMonInfo ? monInfo.rcWork : default;

        if (winRect.Width <= 0 || winRect.Height <= 0 || monRect.Width <= 0 || monRect.Height <= 0)
        {
            return new FullscreenSnapshot(false, FullscreenAppKind.None, procName, hMon, "DegenerateRect");
        }

        uint style = unchecked((uint)NativeMethods.GetWindowLongPtrW(targetHwnd, NativeMethods.GWL_STYLE).ToInt64());
        uint exStyle = unchecked((uint)NativeMethods.GetWindowLongPtrW(targetHwnd, NativeMethods.GWL_EXSTYLE).ToInt64());
        bool isZoomed = NativeMethods.IsZoomed(targetHwnd);

        bool browserVideoCue = false;
        if (!string.IsNullOrWhiteSpace(procName) && KnownBrowserProcesses.Contains(procName) && (style & NativeMethods.WS_CAPTION) == 0)
        {
            char* titleBuf = stackalloc char[256];
            int tLen = NativeMethods.GetWindowTextW(targetHwnd, titleBuf, 256);
            if (tLen > 0)
            {
                ReadOnlySpan<char> title = new(titleBuf, tLen);
                browserVideoCue =
                    title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Netflix", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Twitch", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Prime Video", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Plex", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Disney+", StringComparison.OrdinalIgnoreCase);
            }
        }

        return EvaluatePure(quns, procName, cls, style, exStyle, winRect, monRect, hMon, isZoomed, workRect, browserVideoCue);
    }

    private void OnWinEvent(nint hHook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF)
        {
            return;
        }

        if (eventType == NativeMethods.EVENT_OBJECT_DESTROY)
        {
            bool destroyedLastExternal = false;
            lock (_sync)
            {
                if (hwnd == _lastExternalHwnd)
                {
                    _lastExternalHwnd = 0;
                    destroyedLastExternal = true;
                }
            }

            if (destroyedLastExternal)
            {
                RefreshNow();
            }
            return;
        }

        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
        {
            nint savedExternal;
            lock (_sync)
            {
                savedExternal = _lastExternalHwnd;
            }

            if (hwnd != NativeMethods.GetForegroundWindow() && hwnd != savedExternal)
            {
                return;
            }
        }

        RefreshNow();
    }

    public void Dispose()
    {
        uint tid;
        Thread? thread;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lastExternalHwnd = 0;
            tid = _hookThreadId;
            thread = _hookThread;
            _hookThread = null;
        }

        if (tid != 0)
        {
            NativeMethods.PostThreadMessageW(tid, NativeMethods.WM_QUIT, 0, 0);
        }

        if (thread is not null && thread.IsAlive && Environment.CurrentManagedThreadId != thread.ManagedThreadId)
        {
            thread.Join(TimeSpan.FromMilliseconds(1500));
        }
    }
}
