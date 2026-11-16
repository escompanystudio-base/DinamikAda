using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Hotkeys;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

/// <summary>
/// Controls WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_LAYERED,
/// dynamic WS_EX_TRANSPARENT strictly for S3 click-through / Ghost mode,
/// focus handoff for S5 Command Palette with prevHwnd restore, and
/// EVENT_SYSTEM_CAPTURESTART / CAPTUREEND zero-idle OLE drag pre-wake.
/// </summary>
public sealed class IslandWindowController : IWindowStyleHost, IDisposable
{
    public const double CompactTierWidthDip = 620.0;
    public const double CompactTierHeightDip = 88.0;
    public const double ExpandedTierWidthDip = 720.0;
    public const double ExpandedTierHeightDip = 424.0;

    private nint _hwnd;
    private nint _savedForegroundHwnd;
    private nint _captureStartHook;
    private nint _captureEndHook;
    private NativeMethods.WinEventDelegate? _captureDelegate;
    private readonly int _ownProcessId = Environment.ProcessId;

    private bool _hasAppliedBounds;
    private int _lastLeftPx;
    private int _lastTopPx;
    private int _lastWidthPx;
    private int _lastHeightPx;
    private bool _isTopmostConfirmed;
    private bool? _lastVisibleState;

    private bool _hasPendingCompactBounds;
    private int _pendingLeftPx;
    private int _pendingTopPx;
    private int _pendingWidthPx;
    private int _pendingHeightPx;

    public bool IsClickThrough { get; private set; }
    public bool IsActivatable { get; private set; }
    public bool IsDragCatcherArmed { get; private set; }
    public bool IsCaptureActive { get; private set; }
    public bool HasPendingCompactBounds => _hasPendingCompactBounds;

    public event EventHandler<bool>? CaptureStateChanged;
    public event EventHandler<bool>? ActivationChanged;
    public event EventHandler? DisplayOrDpiChanged;
    public event EventHandler<(int Modifiers, int Key)>? HotkeyPressed;
    public event EventHandler<(int HotkeyId, int Modifiers, int Key)>? NamedHotkeyPressed;
    public event EventHandler<(bool IsArrival, uint UnitMask)>? VolumeDeviceChanged;
    public event EventHandler? ClipboardUpdated;

    public void Attach(nint hwnd)
    {
        UninstallCaptureHooks();
        _hwnd = hwnd;
        _savedForegroundHwnd = 0;
        ResetBoundsCache();
        if (hwnd == 0) return;
        ApplyBaseExtendedStyles();
        InstallCaptureHooks();
    }

    public void InvalidateBoundsCache() => ResetBoundsCache();

    private void ResetBoundsCache()
    {
        _hasAppliedBounds = false;
        _isTopmostConfirmed = false;
        _lastVisibleState = null;
        _hasPendingCompactBounds = false;
    }

    public void ApplyBaseExtendedStyles()
    {
        if (_hwnd == 0) return;
        nint exStyle = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE);
        long next = exStyle.ToInt64()
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TOPMOST
            | NativeMethods.WS_EX_LAYERED;

        if (IsClickThrough) next |= NativeMethods.WS_EX_TRANSPARENT;
        else next &= ~NativeMethods.WS_EX_TRANSPARENT;

        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(next));
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        _isTopmostConfirmed = true;
        IsActivatable = false;
    }

    public void SetClickThrough(bool clickThrough)
    {
        if (IsClickThrough == clickThrough || _hwnd == 0) return;
        IsClickThrough = clickThrough;
        nint exStyle = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE);
        long next = clickThrough
            ? exStyle.ToInt64() | NativeMethods.WS_EX_TRANSPARENT
            : exStyle.ToInt64() & ~NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(next));
        NativeMethods.SetWindowPos(
            _hwnd,
            0,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    public void SetActivatable(bool allowFocus, nint? restoreFocusHwnd = null) =>
        SetActivatable(allowFocus, restoreFocusHwnd, restorePreviousFocus: true);

    public void SetActivatable(bool allowFocus, nint? restoreFocusHwnd, bool restorePreviousFocus)
    {
        if (_hwnd == 0) return;
        if (IsActivatable == allowFocus && restoreFocusHwnd is null or 0) return;

        if (allowFocus)
        {
            nint currentFg = NativeMethods.GetForegroundWindow();
            if (currentFg != 0 && currentFg != _hwnd)
            {
                _savedForegroundHwnd = currentFg;
            }

            nint exStyle = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE);
            long next = (exStyle.ToInt64() & ~NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(next));
            NativeMethods.SetWindowPos(
                _hwnd,
                0,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            IsClickThrough = false;
            IsActivatable = true;

            _ = ForceForeground(_hwnd);
        }
        else
        {
            bool stillForeground = NativeMethods.GetForegroundWindow() == _hwnd;

            nint exStyle = NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE);
            long next = exStyle.ToInt64() | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE, new nint(next));
            NativeMethods.SetWindowPos(
                _hwnd,
                0,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            IsActivatable = false;

            nint target = restoreFocusHwnd is not null and not 0
                ? restoreFocusHwnd.Value
                : (stillForeground && restorePreviousFocus ? _savedForegroundHwnd : 0);
            _savedForegroundHwnd = 0;
            if (target != 0 && target != _hwnd && NativeMethods.IsWindow(target) && !NativeMethods.IsHungAppWindow(target))
            {
                if (NativeMethods.IsIconic(target))
                {
                    NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);
                }
                _ = ForceForeground(target);
            }
        }
    }

    public bool TryPrepareForegroundForPopup()
    {
        if (_hwnd == 0 || !NativeMethods.IsWindow(_hwnd)) return false;
        return ForceForeground(_hwnd);
    }

    public void SetDragCatcherArmed(bool armed)
    {
        IsDragCatcherArmed = armed;
        if (armed && IsClickThrough)
        {
            SetClickThrough(false);
        }
    }

    public void SetWindowTierBounds(double widthDip, double heightDip, double leftScreenDip, double topScreenDip)
    {
        if (_hwnd == 0) return;
        var (x, y, w, h) = ComputeTargetPhysicalBounds(widthDip, heightDip, leftScreenDip, topScreenDip);
        SetPhysicalBounds(x, y, w, h, isDragging: false);
    }

    public void EnsureExpandedHwndTier(double widthDip, double heightDip, double leftScreenDip, double topScreenDip)
    {
        if (_hwnd == 0) return;
        var (x, y, w, h) = ComputeTargetPhysicalBounds(widthDip, heightDip, leftScreenDip, topScreenDip);
        EnsureExpandedHwnd(x, y, w, h);
    }

    public void EnsureExpandedHwnd(int leftPx, int topPx, int widthPx, int heightPx)
    {
        _hasPendingCompactBounds = false;
        SetPhysicalBounds(leftPx, topPx, widthPx, heightPx, isDragging: false);
    }

    public void RequestDeferredCompactHwnd(int leftPx, int topPx, int widthPx, int heightPx)
    {
        _hasPendingCompactBounds = true;
        _pendingLeftPx = leftPx;
        _pendingTopPx = topPx;
        _pendingWidthPx = widthPx;
        _pendingHeightPx = heightPx;
    }

    public bool CollapseHwndNow()
    {
        if (!_hasPendingCompactBounds) return false;
        _hasPendingCompactBounds = false;
        return SetPhysicalBoundsInternal(_pendingLeftPx, _pendingTopPx, _pendingWidthPx, _pendingHeightPx, isDragging: false);
    }

    public void CollapseHwndTierNow(double widthDip, double heightDip, double leftScreenDip, double topScreenDip)
    {
        if (_hwnd == 0) return;
        _hasPendingCompactBounds = false;
        var (x, y, w, h) = ComputeTargetPhysicalBounds(widthDip, heightDip, leftScreenDip, topScreenDip);
        SetPhysicalBoundsInternal(x, y, w, h, isDragging: false);
    }

    public void SetPhysicalBounds(int leftPx, int topPx, int widthPx, int heightPx)
    {
        SetPhysicalBounds(leftPx, topPx, widthPx, heightPx, isDragging: false);
    }

    public void SetPhysicalBounds(int leftPx, int topPx, int widthPx, int heightPx, bool isDragging)
    {
        _hasPendingCompactBounds = false;
        _ = SetPhysicalBoundsInternal(leftPx, topPx, widthPx, heightPx, isDragging);
    }

    private bool SetPhysicalBoundsInternal(int leftPx, int topPx, int widthPx, int heightPx, bool isDragging)
    {
        if (_hwnd == 0) return false;
        if (_hasAppliedBounds &&
            _lastLeftPx == leftPx &&
            _lastTopPx == topPx &&
            _lastWidthPx == widthPx &&
            _lastHeightPx == heightPx)
        {
            return false;
        }

        uint flags = NativeMethods.SWP_NOACTIVATE;
        if (_isTopmostConfirmed) flags |= NativeMethods.SWP_NOZORDER;
        if (isDragging) flags |= NativeMethods.SWP_NOSENDCHANGING;

        bool ok = NativeMethods.SetWindowPos(
            _hwnd,
            _isTopmostConfirmed ? 0 : NativeMethods.HWND_TOPMOST,
            leftPx, topPx, widthPx, heightPx,
            flags);

        if (ok)
        {
            _hasAppliedBounds = true;
            _lastLeftPx = leftPx;
            _lastTopPx = topPx;
            _lastWidthPx = widthPx;
            _lastHeightPx = heightPx;
            _isTopmostConfirmed = true;
        }

        return ok;
    }

    private (int LeftPx, int TopPx, int WidthPx, int HeightPx) ComputeTargetPhysicalBounds(
        double widthDip,
        double heightDip,
        double leftScreenDip,
        double topScreenDip)
    {
        uint initialDpi = 96;
        nint sourceMon = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (sourceMon != 0)
        {
            _ = NativeMethods.GetDpiForMonitor(sourceMon, NativeMethods.MDT_EFFECTIVE_DPI, out initialDpi, out _);
        }

        double initialScale = Math.Max(1.0, initialDpi / 96.0);
        int probeX = (int)Math.Round((leftScreenDip + widthDip * 0.5) * initialScale);
        int probeY = (int)Math.Round((topScreenDip + heightDip * 0.5) * initialScale);
        nint targetMon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = probeX, Y = probeY }, NativeMethods.MONITOR_DEFAULTTONEAREST);

        uint targetDpi = initialDpi;
        if (targetMon != 0)
        {
            _ = NativeMethods.GetDpiForMonitor(targetMon, NativeMethods.MDT_EFFECTIVE_DPI, out targetDpi, out _);
            double candidateScale = Math.Max(1.0, targetDpi / 96.0);
            if (Math.Abs(candidateScale - initialScale) > 0.01)
            {
                int refinedX = (int)Math.Round((leftScreenDip + widthDip * 0.5) * candidateScale);
                int refinedY = (int)Math.Round((topScreenDip + heightDip * 0.5) * candidateScale);
                nint refinedMon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = refinedX, Y = refinedY }, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (refinedMon != 0 && refinedMon != targetMon)
                {
                    _ = NativeMethods.GetDpiForMonitor(refinedMon, NativeMethods.MDT_EFFECTIVE_DPI, out targetDpi, out _);
                }
            }
        }

        double scale = Math.Max(1.0, targetDpi / 96.0);
        int widthPx = (int)Math.Round(widthDip * scale, MidpointRounding.AwayFromZero);
        if ((widthPx & 1) != 0) widthPx += 1;
        return (
            (int)Math.Round(leftScreenDip * scale, MidpointRounding.AwayFromZero),
            (int)Math.Round(topScreenDip * scale, MidpointRounding.AwayFromZero),
            widthPx,
            (int)Math.Round(heightDip * scale, MidpointRounding.AwayFromZero));
    }

    public void SetVisible(bool visible)
    {
        if (_hwnd == 0 || _lastVisibleState == visible) return;
        _lastVisibleState = visible;
        NativeMethods.ShowWindow(_hwnd, visible ? NativeMethods.SW_SHOWNOACTIVATE : NativeMethods.SW_HIDE);
    }

    public nint ProcessWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEACTIVATE when !IsActivatable:
                handled = true;
                return new nint(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_ACTIVATE:
                bool activated = (wParam.ToInt64() & 0xFFFF) != 0;
                ActivationChanged?.Invoke(this, activated);
                break;

            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
                ResetBoundsCache();
                DisplayOrDpiChanged?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.WM_HOTKEY:
            {
                int id = wParam.ToInt32();
                int lval = unchecked((int)lParam.ToInt64());
                int mods = lval & 0xFFFF;
                int vk = (lval >> 16) & 0xFFFF;
                if (id == GlobalHotkeyService.HotkeyId)
                {
                    HotkeyPressed?.Invoke(this, (mods, vk));
                }
                NamedHotkeyPressed?.Invoke(this, (id, mods, vk));
                handled = true;
                break;
            }

            case NativeMethods.WM_DEVICECHANGE:
            {
                int evt = unchecked((int)wParam.ToInt64());
                if ((evt == NativeMethods.DBT_DEVICEARRIVAL || evt == NativeMethods.DBT_DEVICEREMOVECOMPLETE) && lParam != 0)
                {
                    var hdr = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.DEV_BROADCAST_HDR>(lParam);
                    if (hdr.dbch_devicetype == NativeMethods.DBT_DEVTYP_VOLUME &&
                        hdr.dbch_size >= (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.DEV_BROADCAST_VOLUME>())
                    {
                        var vol = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.DEV_BROADCAST_VOLUME>(lParam);
                        VolumeDeviceChanged?.Invoke(this, (evt == NativeMethods.DBT_DEVICEARRIVAL, vol.dbcv_unitmask));
                    }
                }
                break;
            }

            case NativeMethods.WM_CLIPBOARDUPDATE:
                ClipboardUpdated?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case NativeMethods.WM_NCDESTROY:
                UninstallCaptureHooks();
                _hwnd = 0;
                ResetBoundsCache();
                break;
        }
        return 0;
    }

    public bool IsPerMonitorV2Active()
    {
        nint ctx = NativeMethods.GetThreadDpiAwarenessContext();
        return NativeMethods.AreDpiAwarenessContextsEqual(ctx, NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }

    public long ReadCurrentExtendedStyle() =>
        _hwnd == 0 ? 0 : NativeMethods.GetWindowLongPtrW(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();

    public static bool IsLeftMouseButtonDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

    public static bool IsRightMouseButtonDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RBUTTON) & 0x8000) != 0;

    public static bool IsAnyMouseButtonDown() =>
        IsLeftMouseButtonDown() || IsRightMouseButtonDown();

    public static bool IsShiftKeyDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;

    public static (int X, int Y) GetCursorScreenPos()
    {
        NativeMethods.GetCursorPos(out var pt);
        return (pt.X, pt.Y);
    }

    private void InstallCaptureHooks()
    {
        if (_captureStartHook != 0 || _captureEndHook != 0) return;

        _captureDelegate = OnCaptureWinEvent;
        _captureStartHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_CAPTURESTART,
            NativeMethods.EVENT_SYSTEM_CAPTURESTART,
            0, _captureDelegate, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _captureEndHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_CAPTUREEND,
            NativeMethods.EVENT_SYSTEM_CAPTUREEND,
            0, _captureDelegate, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void UninstallCaptureHooks()
    {
        if (_captureStartHook != 0)
        {
            NativeMethods.UnhookWinEvent(_captureStartHook);
            _captureStartHook = 0;
        }
        if (_captureEndHook != 0)
        {
            NativeMethods.UnhookWinEvent(_captureEndHook);
            _captureEndHook = 0;
        }
        _captureDelegate = null;
        IsCaptureActive = false;
    }

    private void OnCaptureWinEvent(nint hHook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        bool isCaptureStart = eventType == NativeMethods.EVENT_SYSTEM_CAPTURESTART;
        if (isCaptureStart && hwnd != 0)
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownProcessId) return;
        }

        bool active = isCaptureStart && IsAnyMouseButtonDown();
        if (IsCaptureActive != active)
        {
            IsCaptureActive = active;
            CaptureStateChanged?.Invoke(this, active);
        }
    }

    public static bool TryFocusExternalWindow(nint targetHwnd) => ForceForeground(targetHwnd);

    private static bool ForceForeground(nint targetHwnd)
    {
        if (targetHwnd == 0 || !NativeMethods.IsWindow(targetHwnd) || NativeMethods.IsHungAppWindow(targetHwnd))
        {
            return false;
        }

        if (NativeMethods.IsIconic(targetHwnd))
        {
            NativeMethods.ShowWindow(targetHwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.AllowSetForegroundWindow(-1);
        if (NativeMethods.SetForegroundWindow(targetHwnd))
        {
            return true;
        }

        nint fg = NativeMethods.GetForegroundWindow();
        if (fg != 0 && NativeMethods.IsHungAppWindow(fg))
        {
            return false;
        }

        uint fgThread = fg != 0 ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0;
        uint curThread = NativeMethods.GetCurrentThreadId();
        uint targetThread = NativeMethods.GetWindowThreadProcessId(targetHwnd, out _);

        bool attachedFg = false;
        bool attachedTarget = false;
        try
        {
            if (fgThread != 0 && fgThread != curThread)
            {
                attachedFg = NativeMethods.AttachThreadInput(curThread, fgThread, true);
            }
            if (targetThread != 0 && targetThread != curThread && targetThread != fgThread)
            {
                attachedTarget = NativeMethods.AttachThreadInput(curThread, targetThread, true);
            }

            NativeMethods.AllowSetForegroundWindow(-1);
            return NativeMethods.SetForegroundWindow(targetHwnd);
        }
        finally
        {
            if (attachedTarget)
            {
                NativeMethods.AttachThreadInput(curThread, targetThread, false);
            }
            if (attachedFg)
            {
                NativeMethods.AttachThreadInput(curThread, fgThread, false);
            }
        }
    }

    public void Dispose()
    {
        UninstallCaptureHooks();
        _hwnd = 0;
    }
}
