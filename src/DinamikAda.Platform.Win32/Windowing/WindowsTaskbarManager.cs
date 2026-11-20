using System.Runtime.InteropServices;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

/// <summary>
/// Non-destructively hides the native Windows taskbar (Shell_TrayWnd / Shell_SecondaryTrayWnd)
/// while the custom Bottom Dock is active, reserves monitor bottom work area, and guarantees
/// instant full restoration when disabled, exiting, or closing.
/// </summary>
public sealed unsafe class WindowsTaskbarManager : IDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<nint> _knownTaskbarHwnds = new();
    private nint _showHook;
    private NativeMethods.WinEventDelegate? _winEventDelegate;
    private bool _hideActive;
    private nint _appBarHwnd;
    private NativeMethods.RECT _appliedReservedRect;
    private bool _hasAppBarRegistered;
    private int? _savedTaskbarState;
    private bool _disposed;
    private readonly string? _recoveryStatePath;

    /// <summary>
    /// Default crash-recovery marker location. Written while the taskbar is hidden so that a crash or
    /// forced kill (where ProcessExit never fires) can be undone on the next launch.
    /// </summary>
    public static string DefaultRecoveryStatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DinamikAda",
        "taskbar-recovery.state");

    public WindowsTaskbarManager() : this(DefaultRecoveryStatePath)
    {
    }

    /// <param name="recoveryStatePath">Crash-recovery marker path, or null to disable persistence.</param>
    public WindowsTaskbarManager(string? recoveryStatePath)
    {
        _recoveryStatePath = recoveryStatePath;
    }

    /// <summary>
    /// If a previous session crashed while the taskbar was hidden, restores the original
    /// auto-hide state and taskbar visibility. Safe to call on every launch.
    /// </summary>
    public bool RecoverFromPreviousSession()
    {
        if (_recoveryStatePath is null) return false;
        lock (_sync)
        {
            string? text;
            try
            {
                if (!File.Exists(_recoveryStatePath)) return false;
                text = File.ReadAllText(_recoveryStatePath).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int prevState)
                && prevState >= 0)
            {
                SetShellTrayState(prevState);
            }

            ShowAllTaskbarWindows();
            DeleteRecoveryMarker();
            return true;
        }
    }

    private void WriteRecoveryMarker()
    {
        if (_recoveryStatePath is null) return;
        try
        {
            string? dir = Path.GetDirectoryName(_recoveryStatePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Never overwrite an existing marker's original state with an already-modified one.
            if (File.Exists(_recoveryStatePath) && _savedTaskbarState is null) return;
            File.WriteAllText(
                _recoveryStatePath,
                (_savedTaskbarState ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DeleteRecoveryMarker()
    {
        if (_recoveryStatePath is null) return;
        try { File.Delete(_recoveryStatePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public bool IsTaskbarHidden
    {
        get { lock (_sync) return _hideActive; }
    }

    /// <summary>
    /// Must be called on the WPF UI thread so WINEVENT_OUTOFCONTEXT fires automatically on the pump.
    /// </summary>
    public void ApplyState(
        bool hideTaskbar,
        MonitorDescriptor? primaryMonitor,
        int reservedBottomPx,
        bool reserveFullWidthWorkArea = false,
        bool reserveAtTop = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (!hideTaskbar)
            {
                RestoreCore();
                return;
            }

            _hideActive = true;
            InstallHookIfNeeded();

            if (!reserveFullWidthWorkArea)
            {
                RemoveAppBarWorkArea();
                if (_savedTaskbarState is null)
                {
                    _savedTaskbarState = ReadShellTrayState();
                    WriteRecoveryMarker();
                }
                if (_savedTaskbarState is not null && ReadShellTrayState() != NativeMethods.ABS_AUTOHIDE)
                {
                    SetShellTrayState(NativeMethods.ABS_AUTOHIDE);
                }
                HideAllTaskbarWindows();
                return;
            }

            if (_savedTaskbarState is int prevState)
            {
                SetShellTrayState(prevState);
                _savedTaskbarState = null;
            }
            WriteRecoveryMarker();
            HideAllTaskbarWindows();

            // The app-bar work area is only reserved for a bottom dock; a top dock floats over windows.
            if (primaryMonitor is not null && reservedBottomPx > 0 && !reserveAtTop)
            {
                var desiredWork = DockLayoutCalculator.ComputeReservedWorkArea(primaryMonitor, reservedBottomPx);
                EnsureAppBarWorkArea(primaryMonitor, desiredWork.Bottom);
            }
        }
    }

    public void EnsureHiddenTick()
    {
        lock (_sync)
        {
            if (_disposed || !_hideActive) return;
            HideAllTaskbarWindows();
        }
    }

    public void Restore()
    {
        lock (_sync)
        {
            RestoreCore();
        }
    }

    /// <summary>
    /// Opens the Windows Start Menu cleanly via VK_LWIN injection without revealing the taskbar.
    /// </summary>
    public static bool TriggerStartMenu()
    {
        var inputs = stackalloc NativeMethods.INPUT[2];
        inputs[0] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_LWIN, dwFlags = 0 }
        };
        inputs[1] = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT { wVk = NativeMethods.VK_LWIN, dwFlags = NativeMethods.KEYEVENTF_KEYUP }
        };
        return NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) == 2;
    }

    private void InstallHookIfNeeded()
    {
        if (_showHook != 0) return;
        _winEventDelegate = OnWinEvent;
        _showHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW,
            NativeMethods.EVENT_OBJECT_SHOW,
            0,
            _winEventDelegate,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void UninstallHook()
    {
        if (_showHook != 0)
        {
            NativeMethods.UnhookWinEvent(_showHook);
            _showHook = 0;
        }
        _winEventDelegate = null;
    }

    private void OnWinEvent(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_OBJECT_SHOW
            || idObject != NativeMethods.OBJID_WINDOW
            || idChild != NativeMethods.CHILDID_SELF
            || hwnd == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed || !_hideActive) return;
            if (!_knownTaskbarHwnds.Contains(hwnd))
            {
                string cls = ReadWindowClass(hwnd);
                if (!DockLayoutCalculator.IsTaskbarWindowClass(cls)) return;
                _knownTaskbarHwnds.Add(hwnd);
            }

            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }
    }

    private void HideAllTaskbarWindows()
    {
        nint primary = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (primary != 0)
        {
            _knownTaskbarHwnds.Add(primary);
            if (NativeMethods.IsWindowVisible(primary))
            {
                NativeMethods.ShowWindow(primary, NativeMethods.SW_HIDE);
            }
        }

        nint sec = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            sec = NativeMethods.FindWindowExW(0, sec, "Shell_SecondaryTrayWnd", null);
            if (sec == 0) break;
            _knownTaskbarHwnds.Add(sec);
            if (NativeMethods.IsWindowVisible(sec))
            {
                NativeMethods.ShowWindow(sec, NativeMethods.SW_HIDE);
            }
        }
    }

    private void RestoreCore()
    {
        bool wasActive = _hideActive;
        _hideActive = false;
        UninstallHook();
        ShowAllTaskbarWindows();

        foreach (nint cached in _knownTaskbarHwnds)
        {
            if (cached != 0 && NativeMethods.IsWindow(cached) && !NativeMethods.IsWindowVisible(cached))
            {
                NativeMethods.ShowWindow(cached, NativeMethods.SW_SHOW);
            }
        }
        _knownTaskbarHwnds.Clear();
        RemoveAppBarWorkArea();

        if (_savedTaskbarState is int prevState)
        {
            SetShellTrayState(prevState);
            _savedTaskbarState = null;
        }

        if (wasActive) DeleteRecoveryMarker();
    }

    private static void ShowAllTaskbarWindows()
    {
        nint primary = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (primary != 0 && !NativeMethods.IsWindowVisible(primary))
        {
            NativeMethods.ShowWindow(primary, NativeMethods.SW_SHOW);
        }

        nint sec = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            sec = NativeMethods.FindWindowExW(0, sec, "Shell_SecondaryTrayWnd", null);
            if (sec == 0) break;
            if (!NativeMethods.IsWindowVisible(sec))
            {
                NativeMethods.ShowWindow(sec, NativeMethods.SW_SHOW);
            }
        }
    }

    private static int? ReadShellTrayState()
    {
        nint tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return null;
        var abd = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = tray
        };
        return unchecked((int)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd));
    }

    private static void SetShellTrayState(int state)
    {
        nint tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return;
        var abd = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = tray,
            lParam = new nint(state)
        };
        _ = NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);
    }

    private void EnsureAppBarWorkArea(MonitorDescriptor monitor, int desiredWorkBottomPx)
    {
        if (_hasAppBarRegistered
            && _appliedReservedRect.Left == monitor.LeftPx
            && _appliedReservedRect.Right == monitor.LeftPx + monitor.WidthPx
            && _appliedReservedRect.Top == desiredWorkBottomPx)
        {
            return;
        }

        if (_appBarHwnd == 0)
        {
            _appBarHwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
                "Static",
                "DinamikAdaDockAppBar",
                0x80000000, // WS_POPUP (never shown; pure AppBar reservation anchor)
                monitor.LeftPx,
                desiredWorkBottomPx,
                Math.Max(1, monitor.WidthPx),
                1,
                0,
                0,
                NativeMethods.GetModuleHandleW(null),
                0);
        }

        if (_appBarHwnd == 0) return;

        if (!_hasAppBarRegistered)
        {
            var newData = new NativeMethods.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = _appBarHwnd,
                uCallbackMessage = 0x8124
            };
            if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref newData) == 0)
            {
                return;
            }
            _hasAppBarRegistered = true;
        }

        var queryData = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _appBarHwnd,
            uEdge = NativeMethods.ABE_BOTTOM,
            rc = new NativeMethods.RECT
            {
                Left = monitor.LeftPx,
                Top = desiredWorkBottomPx,
                Right = monitor.LeftPx + Math.Max(1, monitor.WidthPx),
                Bottom = monitor.TopPx + monitor.HeightPx
            }
        };

        _ = NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref queryData);
        if (queryData.rc.Bottom <= desiredWorkBottomPx)
        {
            return;
        }

        int neededDelta = queryData.rc.Bottom - desiredWorkBottomPx;
        queryData.rc.Top = queryData.rc.Bottom - neededDelta;

        _ = NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref queryData);
        _appliedReservedRect = queryData.rc;
    }

    private void RemoveAppBarWorkArea()
    {
        if (_hasAppBarRegistered && _appBarHwnd != 0)
        {
            var remData = new NativeMethods.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
                hWnd = _appBarHwnd
            };
            _ = NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref remData);
            _hasAppBarRegistered = false;
            _appliedReservedRect = default;
        }

        if (_appBarHwnd != 0)
        {
            _ = NativeMethods.DestroyWindow(_appBarHwnd);
            _appBarHwnd = 0;
        }
    }

    private static string ReadWindowClass(nint hwnd)
    {
        char* buf = stackalloc char[64];
        int len = NativeMethods.GetClassNameW(hwnd, buf, 64);
        return len > 0 ? new string(buf, 0, len).Trim() : string.Empty;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            RestoreCore();
            _disposed = true;
        }
    }
}
