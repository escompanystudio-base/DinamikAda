using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

/// <summary>
/// Pure Win32 placement and restoration helpers so showing hidden/tray or minimized
/// windows preserves maximize state and keeps titlebar/close controls on-monitor.
/// </summary>
public static class WindowRestoreGeometry
{
    public static int DetermineRestoreShowCommand(bool isIconic, bool isMaximized)
    {
        if (isIconic) return NativeMethods.SW_RESTORE;
        if (isMaximized) return NativeMethods.SW_SHOWMAXIMIZED;
        return NativeMethods.SW_SHOW;
    }

    public static bool TryComputeClampedWorkAreaRect(
        NativeMethods.RECT windowRect,
        NativeMethods.RECT workArea,
        out NativeMethods.RECT adjusted)
    {
        adjusted = windowRect;
        int workW = workArea.Width;
        int workH = workArea.Height;
        if (workW <= 0 || workH <= 0) return false;

        int winW = windowRect.Width;
        int winH = windowRect.Height;
        if (winW <= 0 || winH <= 0) return false;

        // Clamp when stale rcNormalPosition puts titlebar/close button off-screen.
        bool outOfBounds = windowRect.Top < workArea.Top
            || windowRect.Right > workArea.Right + 16
            || windowRect.Left < workArea.Left - 16
            || windowRect.Bottom > workArea.Bottom + 16
            || windowRect.Left >= workArea.Right - 48
            || windowRect.Top >= workArea.Bottom - 48;

        if (!outOfBounds) return false;

        int newW = Math.Clamp(winW, Math.Min(320, workW), workW);
        int newH = Math.Clamp(winH, Math.Min(240, workH), workH);
        int maxLeft = Math.Max(workArea.Left, workArea.Right - newW);
        int maxTop = Math.Max(workArea.Top, workArea.Bottom - newH);
        int newLeft = Math.Clamp(windowRect.Left, workArea.Left, maxLeft);
        int newTop = Math.Clamp(windowRect.Top, workArea.Top, maxTop);

        if (newLeft == windowRect.Left && newTop == windowRect.Top && newW == winW && newH == winH)
        {
            return false;
        }

        adjusted = new NativeMethods.RECT
        {
            Left = newLeft,
            Top = newTop,
            Right = newLeft + newW,
            Bottom = newTop + newH
        };
        return true;
    }

    public static bool IsChromiumWidgetClass(string? className)
        => !string.IsNullOrWhiteSpace(className) &&
           className.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldPreferExecutableActivation(
        bool isVisible,
        bool isIconic,
        string? windowClassName,
        string? exePath)
        => !isVisible &&
           !isIconic &&
           IsChromiumWidgetClass(windowClassName) &&
           !string.IsNullOrWhiteSpace(exePath);

    public static unsafe string ReadWindowClassSafe(nint hwnd)
    {
        if (hwnd == 0) return string.Empty;
        char* buf = stackalloc char[128];
        int len = NativeMethods.GetClassNameW(hwnd, buf, 128);
        return len > 0 ? new string(buf, 0, len).Trim() : string.Empty;
    }

    public static void EnsureChromiumRenderChildrenVisible(nint parentHwnd)
    {
        if (parentHwnd == 0 || !IsChromiumWidgetClass(ReadWindowClassSafe(parentHwnd))) return;
        NativeMethods.EnumChildWindows(parentHwnd, (child, _) =>
        {
            if (child != 0 &&
                !NativeMethods.IsWindowVisible(child) &&
                ReadWindowClassSafe(child).Equals("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase))
            {
                NativeMethods.ShowWindow(child, NativeMethods.SW_SHOW);
            }
            return true;
        }, 0);
    }

    public static unsafe void EnsureWindowWorkAreaVisible(nint hwnd)
    {
        if (hwnd == 0 || NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd)) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        nint hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon == 0) return;

        var mi = new NativeMethods.MONITORINFOEXW { cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW) };
        if (!NativeMethods.GetMonitorInfoW(hMon, ref mi)) return;

        if (TryComputeClampedWorkAreaRect(rect, mi.rcWork, out var clamped))
        {
            NativeMethods.SetWindowPos(
                hwnd,
                0,
                clamped.Left,
                clamped.Top,
                clamped.Width,
                clamped.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }
}
