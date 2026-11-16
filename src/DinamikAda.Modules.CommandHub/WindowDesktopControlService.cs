using System.Runtime.InteropServices;
using System.Text;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Modules.CommandHub;

public sealed record TargetWindowState(
    nint Hwnd,
    string Title,
    bool IsTopMost,
    int OpacityPercent);

public sealed record WindowSnapBounds(
    int LeftPx,
    int TopPx,
    int WidthPx,
    int HeightPx);

public static unsafe class WindowControlMath
{
    private static readonly HashSet<string> ExcludedShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "NotifyIconOverflowWindow",
        "TopLevelWindowForXamlOverflow",
        "TopLevelWindowForOverflowXamlIsland",
        "TopLevelOverflowWindow",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "Windows.UI.Core.CoreWindow",
        "Xaml_WindowedPopupClass",
        "TaskListThumbnailWnd",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "SysShadow",
        "#32768",
        "DinamikAdaPureDimmerOverlay"
    };

    private static readonly string[] ExcludedTitleExact =
    [
        "Sistem tepsisi taşma penceresi.",
        "System tray overflow window.",
        "Program Manager",
        "DinamikAda — Island",
        "DinamikAda Dock",
        "DinamikAda Ayarlar",
        "Windows Input Experience",
        "Task Switching"
    ];
    public static byte OpacityPercentToByte(int percent)
    {
        int clamped = Math.Clamp(percent, 30, 100);
        return (byte)Math.Clamp((int)Math.Round(clamped / 100.0 * 255.0), 77, 255);
    }

    public static int ByteToOpacityPercent(byte alpha)
    {
        return Math.Clamp((int)Math.Round(alpha / 255.0 * 100.0), 30, 100);
    }

    public static bool IsExternalHandleAndPid(nint hwnd, uint windowPid, nint ownPid, nint islandHwnd)
    {
        if (hwnd == 0 || hwnd == islandHwnd) return false;
        return windowPid != 0 && (nint)windowPid != ownPid;
    }

    public static bool IsEligibleExternalWindowMetadata(string? className, string? title)
    {
        if (!string.IsNullOrWhiteSpace(className))
        {
            string trimmedClass = className.Trim();
            if (ExcludedShellClasses.Contains(trimmedClass)
                || trimmedClass.Contains("DinamikAda.App", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string trimmed = title.Trim();
        foreach (string exact in ExcludedTitleExact)
        {
            if (string.Equals(trimmed, exact, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (trimmed.Contains("Sistem tepsisi taşma", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("System tray overflow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static bool IsExternalTargetWindow(nint hwnd, nint ownPid, nint islandHwnd)
    {
        if (hwnd == 0 || hwnd == islandHwnd) return false;
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (!IsExternalHandleAndPid(hwnd, pid, ownPid, islandHwnd)) return false;

        string cls = ReadWindowClass(hwnd);
        string title = ReadWindowTitleStatic(hwnd);
        if (string.IsNullOrWhiteSpace(title)) return false;
        return IsEligibleExternalWindowMetadata(cls, title);
    }

    private static string ReadWindowClass(nint hwnd)
    {
        char* clsBuf = stackalloc char[128];
        int clsLen = NativeMethods.GetClassNameW(hwnd, clsBuf, 128);
        return clsLen > 0 ? new string(clsBuf, 0, clsLen).Trim() : string.Empty;
    }

    internal static string ReadWindowTitleStatic(nint hwnd)
    {
        int len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        int cap = Math.Min(len + 1, 256);
        char* buf = stackalloc char[cap];
        int read = NativeMethods.GetWindowTextW(hwnd, buf, cap);
        return read > 0 ? new string(buf, 0, read).Trim() : string.Empty;
    }

    /// <summary>
    /// Computes target work-area snap bounds for the specified monitor and preset.
    /// </summary>
    public static WindowSnapBounds ComputeSnapBounds(MonitorDescriptor monitor, WindowSnapPreset preset)
        => ComputeSnapBounds(monitor, preset, 0, 0);

    /// <summary>
    /// Computes target work-area snap bounds for the specified monitor and preset,
    /// preserving and clamping the window's current size when moving across monitors.
    /// </summary>
    public static WindowSnapBounds ComputeSnapBounds(
        MonitorDescriptor monitor,
        WindowSnapPreset preset,
        int currentWidthPx,
        int currentHeightPx)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        int workLeft = monitor.EffectiveWorkLeftPx;
        int workTop = monitor.EffectiveWorkTopPx;
        int workWidth = Math.Max(1, monitor.EffectiveWorkWidthPx);
        int workHeight = Math.Max(1, monitor.EffectiveWorkHeightPx);

        switch (preset)
        {
            case WindowSnapPreset.LeftHalf:
            {
                int width = Math.Max(1, workWidth / 2);
                return new WindowSnapBounds(workLeft, workTop, width, workHeight);
            }
            case WindowSnapPreset.RightHalf:
            {
                int leftHalf = workWidth / 2;
                int width = Math.Max(1, workWidth - leftHalf);
                return new WindowSnapBounds(workLeft + leftHalf, workTop, width, workHeight);
            }
            case WindowSnapPreset.MoveToNextMonitor when currentWidthPx > 0 && currentHeightPx > 0:
            {
                int width = Math.Clamp(currentWidthPx, 1, workWidth);
                int height = Math.Clamp(currentHeightPx, 1, workHeight);
                int left = workLeft + (workWidth - width) / 2;
                int top = workTop + (workHeight - height) / 2;
                return new WindowSnapBounds(left, top, width, height);
            }
            case WindowSnapPreset.CenterFocus70:
            case WindowSnapPreset.MoveToNextMonitor:
            default:
            {
                int width = Math.Clamp((int)Math.Round(workWidth * 0.70), 1, workWidth);
                int height = Math.Clamp((int)Math.Round(workHeight * 0.82), 1, workHeight);
                int left = workLeft + (workWidth - width) / 2;
                int top = workTop + (workHeight - height) / 2;
                return new WindowSnapBounds(left, top, width, height);
            }
        }
    }

    /// <summary>
    /// Selects the target monitor for a window snap or cross-monitor move operation.
    /// Monitors are ordered left-to-right by <see cref="MonitorDescriptor.LeftPx"/>.
    /// </summary>
    public static MonitorDescriptor SelectTargetMonitor(
        IReadOnlyList<MonitorDescriptor> monitors,
        int winLeft,
        int winTop,
        int winWidth,
        int winHeight,
        WindowSnapPreset preset)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
            throw new ArgumentException("At least one monitor descriptor is required.", nameof(monitors));

        var ordered = monitors
            .OrderBy(m => m.LeftPx)
            .ThenBy(m => m.TopPx)
            .ThenBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int currentIdx = FindContainingMonitorIndex(ordered, winLeft, winTop, winWidth, winHeight);
        if (preset == WindowSnapPreset.MoveToNextMonitor)
        {
            return ordered[(currentIdx + 1) % ordered.Count];
        }

        return ordered[currentIdx];
    }

    private static int FindContainingMonitorIndex(
        IReadOnlyList<MonitorDescriptor> ordered,
        int winLeft,
        int winTop,
        int winWidth,
        int winHeight)
    {
        int effectiveW = Math.Max(1, winWidth);
        int effectiveH = Math.Max(1, winHeight);
        double centerX = winLeft + effectiveW / 2.0;
        double centerY = winTop + effectiveH / 2.0;

        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            if (centerX >= m.LeftPx && centerX < m.LeftPx + Math.Max(1, m.WidthPx) &&
                centerY >= m.TopPx && centerY < m.TopPx + Math.Max(1, m.HeightPx))
            {
                return i;
            }
        }

        long bestArea = -1;
        int bestIdx = -1;
        long winRight = (long)winLeft + effectiveW;
        long winBottom = (long)winTop + effectiveH;

        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            long mRight = (long)m.LeftPx + Math.Max(1, m.WidthPx);
            long mBottom = (long)m.TopPx + Math.Max(1, m.HeightPx);

            long overlapW = Math.Min(winRight, mRight) - Math.Max((long)winLeft, m.LeftPx);
            long overlapH = Math.Min(winBottom, mBottom) - Math.Max((long)winTop, m.TopPx);
            if (overlapW > 0 && overlapH > 0)
            {
                long area = overlapW * overlapH;
                if (area > bestArea)
                {
                    bestArea = area;
                    bestIdx = i;
                }
            }
        }

        if (bestIdx >= 0) return bestIdx;

        double bestDistSq = double.MaxValue;
        bestIdx = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            double clampedX = Math.Clamp(centerX, m.LeftPx, m.LeftPx + Math.Max(1, m.WidthPx));
            double clampedY = Math.Clamp(centerY, m.TopPx, m.TopPx + Math.Max(1, m.HeightPx));
            double dx = centerX - clampedX;
            double dy = centerY - clampedY;
            double distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
            }
        }

        return bestIdx;
    }
}

/// <summary>
/// Controls foreground external window pinning (HWND_TOPMOST), opacity layering (WS_EX_LAYERED),
/// and smooth Windows Virtual Desktop switching (Ctrl + Win + Left / Right).
/// Safely ignores DinamikAda's own windows.
/// </summary>
public sealed unsafe class WindowDesktopControlService
{
    private readonly nint _ownPid = Environment.ProcessId;
    private readonly object _sync = new();
    private nint _islandHwnd;
    private nint _lastExternalHwnd;
    private string _lastExternalTitle = string.Empty;

    public void SetIslandHandle(nint islandHwnd)
    {
        _islandHwnd = islandHwnd;
    }

    public void CaptureForegroundIfExternal()
    {
        nint fg = NativeMethods.GetForegroundWindow();
        if (fg == 0 || fg == _islandHwnd) return;
        lock (_sync)
        {
            if (fg == _lastExternalHwnd) return;
        }
        if (!WindowControlMath.IsExternalTargetWindow(fg, _ownPid, _islandHwnd)) return;

        string title = ReadWindowTitle(fg);
        if (string.IsNullOrWhiteSpace(title)) return;

        lock (_sync)
        {
            _lastExternalHwnd = fg;
            _lastExternalTitle = title;
        }
    }

    public TargetWindowState? GetTargetWindowState()
    {
        CaptureForegroundIfExternal();
        nint hwnd;
        string title;
        lock (_sync)
        {
            hwnd = _lastExternalHwnd;
            title = _lastExternalTitle;
        }

        if (hwnd == 0 || !WindowControlMath.IsExternalTargetWindow(hwnd, _ownPid, _islandHwnd))
        {
            lock (_sync)
            {
                if (_lastExternalHwnd == hwnd)
                {
                    _lastExternalHwnd = 0;
                    _lastExternalTitle = string.Empty;
                }
            }
            return null;
        }

        long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
        bool isTopMost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;
        int opacity = 100;
        if ((exStyle & NativeMethods.WS_EX_LAYERED) != 0 &&
            NativeMethods.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint flags) &&
            (flags & NativeMethods.LWA_ALPHA) != 0)
        {
            opacity = WindowControlMath.ByteToOpacityPercent(alpha);
        }

        string currentTitle = ReadWindowTitle(hwnd);
        if (!string.IsNullOrWhiteSpace(currentTitle))
        {
            title = currentTitle;
            lock (_sync) _lastExternalTitle = title;
        }

        return new TargetWindowState(hwnd, title, isTopMost, opacity);
    }

    public (bool Success, bool IsTopMost, string Title) ToggleTopMost()
    {
        var st = GetTargetWindowState();
        if (st is null) return (false, false, string.Empty);

        bool nextTopMost = !st.IsTopMost;
        nint insertAfter = nextTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
        bool ok = NativeMethods.SetWindowPos(
            st.Hwnd,
            insertAfter,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        return (ok, nextTopMost, st.Title);
    }

    public bool SetWindowOpacity(int percent)
    {
        var st = GetTargetWindowState();
        if (st is null) return false;

        int clamped = Math.Clamp(percent, 30, 100);
        long exStyle = NativeMethods.GetWindowLongPtrW(st.Hwnd, NativeMethods.GWL_EXSTYLE);
        if (clamped >= 100)
        {
            if ((exStyle & NativeMethods.WS_EX_LAYERED) != 0)
            {
                NativeMethods.SetLayeredWindowAttributes(st.Hwnd, 0, 255, NativeMethods.LWA_ALPHA);
                NativeMethods.SetWindowLongPtrW(st.Hwnd, NativeMethods.GWL_EXSTYLE, (nint)(exStyle & ~NativeMethods.WS_EX_LAYERED));
            }
            return true;
        }

        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            NativeMethods.SetWindowLongPtrW(st.Hwnd, NativeMethods.GWL_EXSTYLE, (nint)(exStyle | NativeMethods.WS_EX_LAYERED));
        }

        byte alpha = WindowControlMath.OpacityPercentToByte(clamped);
        return NativeMethods.SetLayeredWindowAttributes(st.Hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
    }

    public bool SwitchVirtualDesktop(bool next)
    {
        ushort arrow = (ushort)(next ? NativeMethods.VK_RIGHT : NativeMethods.VK_LEFT);
        var inputs = stackalloc NativeMethods.INPUT[6];

        inputs[0] = MakeKeyInput((ushort)NativeMethods.VK_CONTROL, keyUp: false);
        inputs[1] = MakeKeyInput((ushort)NativeMethods.VK_LWIN, keyUp: false);
        inputs[2] = MakeKeyInput(arrow, keyUp: false);
        inputs[3] = MakeKeyInput(arrow, keyUp: true);
        inputs[4] = MakeKeyInput((ushort)NativeMethods.VK_LWIN, keyUp: true);
        inputs[5] = MakeKeyInput((ushort)NativeMethods.VK_CONTROL, keyUp: true);

        uint sent = NativeMethods.SendInput(6, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == 6;
    }

    /// <summary>
    /// Snaps the foreground external window (or tracked fallback if the island holds focus)
    /// to the requested DPI-aware work-area preset or moves it to the next monitor.
    /// </summary>
    public bool TrySnapForegroundWindow(
        WindowSnapPreset preset,
        IReadOnlyList<MonitorDescriptor> monitors,
        IntPtr islandHwnd)
    {
        if (monitors is null || monitors.Count == 0) return false;

        nint effectiveIsland = islandHwnd != 0 ? (nint)islandHwnd : _islandHwnd;
        nint target = NativeMethods.GetForegroundWindow();

        if (WindowControlMath.IsExternalTargetWindow(target, _ownPid, effectiveIsland))
        {
            string title = ReadWindowTitle(target);
            if (!string.IsNullOrWhiteSpace(title))
            {
                lock (_sync)
                {
                    _lastExternalHwnd = target;
                    _lastExternalTitle = title;
                }
            }
        }
        else
        {
            lock (_sync)
            {
                target = _lastExternalHwnd;
            }

            if (!WindowControlMath.IsExternalTargetWindow(target, _ownPid, effectiveIsland))
            {
                return false;
            }
        }

        if (NativeMethods.IsZoomed(target) || NativeMethods.IsIconic(target))
        {
            NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);
        }

        if (!NativeMethods.GetWindowRect(target, out var rect))
        {
            return false;
        }

        var targetMonitor = WindowControlMath.SelectTargetMonitor(
            monitors,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            preset);

        var bounds = WindowControlMath.ComputeSnapBounds(
            targetMonitor,
            preset,
            rect.Width,
            rect.Height);

        return NativeMethods.SetWindowPos(
            target,
            0,
            bounds.LeftPx,
            bounds.TopPx,
            bounds.WidthPx,
            bounds.HeightPx,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static NativeMethods.INPUT MakeKeyInput(ushort vk, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
            time = 0,
            dwExtraInfo = 0
        }
    };

    private static string ReadWindowTitle(nint hwnd)
    {
        int len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        char* buf = stackalloc char[Math.Min(len + 1, 256)];
        int read = NativeMethods.GetWindowTextW(hwnd, buf, Math.Min(len + 1, 256));
        return read > 0 ? new string(buf, 0, read).Trim() : string.Empty;
    }
}
