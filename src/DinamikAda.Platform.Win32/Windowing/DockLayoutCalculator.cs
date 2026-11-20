using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

public sealed record DockHostBounds(
    int LeftPx,
    int TopPx,
    int WidthPx,
    int HeightPx,
    double DpiScale);

public readonly record struct DockIconTransform(
    double Scale,
    double ShiftXDip,
    double LiftYDip);

/// <summary>
/// Pure geometry and macOS-style cosine-bell magnification math for the bottom dock shell.
/// </summary>
public static class DockLayoutCalculator
{
    public const double DefaultHostHeightDip = 96.0;

    public static bool IsTaskbarWindowClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return false;
        string trimmed = className.Trim();
        return string.Equals(trimmed, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);
    }

    public static DockHostBounds ComputeHostWindowBounds(MonitorDescriptor monitor, double hostHeightDip = DefaultHostHeightDip,
        bool atTop = false, int topInsetPx = 0)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        double scale = Math.Max(1.0, monitor.DpiScale);
        double clampedHostH = Math.Clamp(hostHeightDip, 80.0, Math.Max(80.0, monitor.HeightPx / scale));
        int heightPx = (int)Math.Round(clampedHostH * scale, MidpointRounding.AwayFromZero);
        if ((heightPx & 1) != 0) heightPx += 1;
        int widthPx = Math.Max(2, monitor.WidthPx);
        int leftPx = monitor.LeftPx;
        int topPx = atTop
            ? monitor.TopPx + Math.Clamp(topInsetPx, 0, Math.Max(0, monitor.HeightPx / 4))
            : monitor.TopPx + monitor.HeightPx - heightPx;
        return new DockHostBounds(leftPx, topPx, widthPx, heightPx, scale);
    }

    public static int ComputeReservedBottomHeightPx(double dpiScale, int iconSizeDip)
    {
        double scale = Math.Clamp(dpiScale, 1.0, 4.0);
        int clampedIcon = Math.Clamp(iconSizeDip, 26, 48);
        double reservedDip = clampedIcon + 21.0; // slim ~46dp capsule + 5dp bottom gap + 2dp margin
        return (int)Math.Round(reservedDip * scale, MidpointRounding.AwayFromZero);
    }

    public static NativeMethods.RECT ComputeReservedWorkArea(MonitorDescriptor monitor, int reservedBottomPx, bool atTop = false)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        int maxReserve = Math.Max(0, monitor.HeightPx / 3);
        int clampedReserve = Math.Clamp(reservedBottomPx, 0, maxReserve);
        if (atTop)
        {
            int monBottom = monitor.TopPx + monitor.HeightPx;
            int workBottom = monitor.WorkHeightPx > 0 && monitor.WorkTopPx + monitor.WorkHeightPx <= monBottom
                ? monitor.WorkTopPx + monitor.WorkHeightPx
                : monBottom;
            int topEdge = Math.Min(workBottom - 100, monitor.TopPx + clampedReserve);
            return new NativeMethods.RECT
            {
                Left = monitor.LeftPx,
                Top = topEdge,
                Right = monitor.LeftPx + Math.Max(1, monitor.WidthPx),
                Bottom = workBottom
            };
        }
        int top = monitor.WorkHeightPx > 0 && monitor.WorkTopPx >= monitor.TopPx
            ? monitor.WorkTopPx
            : monitor.TopPx;
        int bottom = Math.Max(top + 100, monitor.TopPx + monitor.HeightPx - clampedReserve);
        return new NativeMethods.RECT
        {
            Left = monitor.LeftPx,
            Top = top,
            Right = monitor.LeftPx + Math.Max(1, monitor.WidthPx),
            Bottom = bottom
        };
    }

    /// <summary>
    /// Computes C1-smooth macOS dock icon scale, vertical lift, and zero-overlap horizontal spread.
    /// </summary>
    public static DockIconTransform[] ComputeMagnification(
        double? cursorLocalX,
        IReadOnlyList<double> slotCentersX,
        double baseSlotPitchDip,
        double maxScale = 1.48,
        double influenceRadiusDip = 136.0,
        double liftFactor = 0.48,
        double spreadFactor = 0.68)
    {
        int n = slotCentersX?.Count ?? 0;
        if (n == 0) return [];

        var result = new DockIconTransform[n];
        double pitch = Math.Max(24.0, baseSlotPitchDip);
        if (cursorLocalX is null || maxScale <= 1.001 || influenceRadiusDip <= 1.0)
        {
            for (int i = 0; i < n; i++)
            {
                result[i] = new DockIconTransform(1.0, 0.0, 0.0);
            }
            return result;
        }

        double cx = cursorLocalX.Value;
        double radius = Math.Max(24.0, influenceRadiusDip);
        double clampedSpread = Math.Clamp(spreadFactor, 0.0, 1.0);
        double[] scales = new double[n];
        double[] widths = new double[n];
        double[] pos = new double[n];

        for (int i = 0; i < n; i++)
        {
            double dist = Math.Abs(cx - slotCentersX![i]);
            double scale = 1.0;
            if (dist < radius)
            {
                double phase = Math.Cos((dist / radius) * (Math.PI * 0.5));
                scale = 1.0 + (maxScale - 1.0) * (phase * phase);
            }
            scales[i] = scale;
            widths[i] = pitch + (scale - 1.0) * pitch * clampedSpread;
        }

        pos[0] = 0.0;
        for (int i = 1; i < n; i++)
        {
            pos[i] = pos[i - 1] + 0.5 * widths[i - 1] + 0.5 * widths[i];
        }

        double expandedMid = 0.5 * pos[n - 1];
        double nominalMid = 0.5 * (slotCentersX![0] + slotCentersX[n - 1]);

        for (int i = 0; i < n; i++)
        {
            double shiftX = (pos[i] - expandedMid) - (slotCentersX[i] - nominalMid);
            double liftY = -(scales[i] - 1.0) * pitch * liftFactor;
            result[i] = new DockIconTransform(scales[i], shiftX, liftY);
        }

        return result;
    }

    /// <summary>
    /// macOS-strength magnification scales: a gaussian over the distance (in slot pitches) from the cursor,
    /// truncated smoothly to exactly 1.0 at <paramref name="cutoffPitch"/> so far icons never twitch.
    /// With the defaults the hovered icon reaches <paramref name="maxScale"/>, its neighbours ~1.49× and ~1.09×.
    /// </summary>
    public static double[] ComputeGaussianMagnificationScales(
        double? cursorLocalX,
        IReadOnlyList<double> slotCentersX,
        double baseSlotPitchDip,
        double maxScale = 1.85,
        double sigmaPitch = 0.95,
        double cutoffPitch = 3.2)
    {
        int n = slotCentersX?.Count ?? 0;
        var scales = new double[n];
        double pitch = Math.Max(24.0, baseSlotPitchDip);
        double sigma = Math.Max(0.1, sigmaPitch) * pitch;
        double cutoff = Math.Max(sigma, cutoffPitch * pitch);
        double floor = Math.Exp(-(cutoff * cutoff) / (2.0 * sigma * sigma));
        for (int i = 0; i < n; i++)
        {
            scales[i] = 1.0;
            if (cursorLocalX is null || maxScale <= 1.001) continue;
            double d = Math.Abs(cursorLocalX.Value - slotCentersX![i]);
            if (d >= cutoff) continue;
            double g = (Math.Exp(-(d * d) / (2.0 * sigma * sigma)) - floor) / (1.0 - floor);
            scales[i] = 1.0 + (maxScale - 1.0) * Math.Clamp(g, 0.0, 1.0);
        }
        return scales;
    }

    /// <summary>
    /// Extra width (DIP) that magnified slots add to the left of <paramref name="x"/>. Each slot occupies
    /// [center − pitch/2, center + pitch/2] and widens by (scale − 1) × spread. Shifting any element with
    /// nominal centre c by G(c) − G(cursor) keeps the point under the cursor fixed and the slots touching
    /// without overlap; G(cursor) and G(∞) − G(cursor) are how far the dock grows on each side.
    /// </summary>
    public static double ComputeMagnificationSpreadBefore(
        double x,
        IReadOnlyList<double> slotCentersX,
        IReadOnlyList<double> scales,
        double baseSlotPitchDip,
        double spreadFactor = 1.0)
    {
        int n = Math.Min(slotCentersX?.Count ?? 0, scales?.Count ?? 0);
        double pitch = Math.Max(24.0, baseSlotPitchDip);
        double spread = Math.Clamp(spreadFactor, 0.0, 1.0);
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            double extra = (Math.Max(1.0, scales![i]) - 1.0) * spread;
            if (extra <= 0.0) continue;
            double start = slotCentersX![i] - pitch * 0.5;
            sum += extra * Math.Clamp(x - start, 0.0, pitch);
        }
        return sum;
    }

    public static double ComputeExtraRowSpreadDip(IReadOnlyList<double> scales, double baseSlotPitchDip)
    {
        if (scales is null || scales.Count == 0) return 0.0;
        double pitch = Math.Max(24.0, baseSlotPitchDip);
        double sum = 0.0;
        for (int i = 0; i < scales.Count; i++)
        {
            double s = Math.Max(1.0, scales[i]);
            sum += (s - 1.0) * pitch * 0.68;
        }
        return sum;
    }

    public static bool IsTrayHostWindowClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return false;
        if (className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("SystemTray_Main", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return className.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase)
            || className.Contains("TrayIcon", StringComparison.OrdinalIgnoreCase)
            || className.Contains("StatusTray", StringComparison.OrdinalIgnoreCase)
            || className.Contains("SystemTray", StringComparison.OrdinalIgnoreCase)
            || className.Contains("TrayWindow", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEligibleHiddenMainWindowMetadata(string? className, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(className)) return false;

        if (className.Equals("IME", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("GDI+ Hook Window Class", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("OleDdeWndClass", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Chrome_WidgetWin_0", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith(".NET-Broadcast", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("Notify", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("InputTrap", StringComparison.OrdinalIgnoreCase) ||
            className.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("HwndWrapper[", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (title.Equals("Menu", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("SystemTray", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("T", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Overlay Primer", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Default IME", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static string ExpandKnownFolderPrefix(string? rawPath, Func<Guid, string?> resolveKnownFolder)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
        string trimmed = rawPath.Trim();
        if (!trimmed.StartsWith('{')) return trimmed;

        int closeIdx = trimmed.IndexOf('}');
        if (closeIdx <= 1) return trimmed;

        if (!Guid.TryParse(trimmed.AsSpan(0, closeIdx + 1), out var folderId))
        {
            return trimmed;
        }

        string? resolvedRoot = resolveKnownFolder(folderId);
        if (string.IsNullOrWhiteSpace(resolvedRoot)) return trimmed;

        return resolvedRoot + trimmed[(closeIdx + 1)..];
    }

    public static bool IsIgnoredBackgroundTrayProcess(string? exeName, string? normExePath, string? windowsFolderPath = null)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return true;

        // Always allow Windows Security tray app, deny OS notification/shell/helper daemons and web browsers.
        if (exeName.Equals("SecurityHealthSystray", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (exeName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("DinamikAda.App", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("MoNotificationUx", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("wlrmdr", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("VSNgenRunner", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("mysqld", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("lghub_agent", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("AMDRSSrcExt", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("AMDRSServ", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("SteelSeriesEngine", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("SteelSeriesPrism", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("SteelSeriesGGEZ", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("EpicOnlineServicesUserHelper", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("zen", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("opera", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
            exeName.StartsWith("EOSOverlayRenderer", StringComparison.OrdinalIgnoreCase) ||
            exeName.Contains(".Subagent.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(windowsFolderPath) &&
            !string.IsNullOrWhiteSpace(normExePath) &&
            normExePath.StartsWith(windowsFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static NativeMethods.RECT ComputeDockHitRectPx(
        MonitorDescriptor monitor,
        double capsuleWidthDip,
        double capsuleHeightDip,
        double bottomMarginDip = 6.0,
        int topBufferPx = 4,
        bool atTop = false,
        int topInsetPx = 0)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        double scale = Math.Max(1.0, monitor.DpiScale);
        int monW = Math.Max(1, monitor.WidthPx);
        int monH = Math.Max(1, monitor.HeightPx);
        int capW = Math.Clamp((int)Math.Round(Math.Max(80.0, capsuleWidthDip) * scale, MidpointRounding.AwayFromZero), 80, monW);
        int capH = Math.Clamp((int)Math.Round(Math.Max(36.0, capsuleHeightDip) * scale, MidpointRounding.AwayFromZero), 36, Math.Max(36, monH / 3));
        int marginPx = Math.Max(0, (int)Math.Round(bottomMarginDip * scale, MidpointRounding.AwayFromZero));

        int left = monitor.LeftPx + (monW - capW) / 2;
        int right = left + capW;
        if (atTop)
        {
            int t = monitor.TopPx + Math.Max(0, topInsetPx);
            return new NativeMethods.RECT
            {
                Left = left,
                Top = t,
                Right = right,
                Bottom = Math.Min(monitor.TopPx + monH, t + marginPx + capH + Math.Max(0, topBufferPx))
            };
        }
        int bottom = monitor.TopPx + monH;
        int top = Math.Max(monitor.TopPx, bottom - marginPx - capH - Math.Max(0, topBufferPx));

        return new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom
        };
    }

    public static bool DoesWindowOverlapDockZone(NativeMethods.RECT windowRect, NativeMethods.RECT dockZone, int minOverlapPx = 1)
    {
        if (windowRect.Width <= 0 || windowRect.Height <= 0) return false;
        int threshold = Math.Max(1, minOverlapPx);
        int overlapX = Math.Min(windowRect.Right, dockZone.Right) - Math.Max(windowRect.Left, dockZone.Left);
        int overlapY = Math.Min(windowRect.Bottom, dockZone.Bottom) - Math.Max(windowRect.Top, dockZone.Top);
        return overlapX >= threshold && overlapY >= threshold;
    }

    public static bool IsPointInBottomRevealZone(int cursorX, int cursorY, MonitorDescriptor monitor, int edgeHeightPx = 4)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        int right = monitor.LeftPx + Math.Max(1, monitor.WidthPx);
        int bottom = monitor.TopPx + Math.Max(1, monitor.HeightPx);
        int triggerH = Math.Clamp(edgeHeightPx, 1, 24);
        return cursorX >= monitor.LeftPx && cursorX < right
            && cursorY >= bottom - triggerH
            && cursorY <= bottom;
    }

    public static bool IsPointInTopRevealZone(int cursorX, int cursorY, MonitorDescriptor monitor, int edgeHeightPx = 4)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        int right = monitor.LeftPx + Math.Max(1, monitor.WidthPx);
        int triggerH = Math.Clamp(edgeHeightPx, 1, 96);
        return cursorX >= monitor.LeftPx && cursorX < right
            && cursorY >= monitor.TopPx
            && cursorY < monitor.TopPx + triggerH;
    }

    /// <summary>True when the cursor is released in the top half of the monitor (dock snaps to the top edge).</summary>
    public static bool ShouldSnapDockToTop(int cursorY, MonitorDescriptor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return cursorY < monitor.TopPx + Math.Max(1, monitor.HeightPx) / 2;
    }

    public static bool IsPointInDockKeepOpenZone(
        int cursorX,
        int cursorY,
        NativeMethods.RECT dockZone,
        int padHorizontalPx = 24,
        int padTopPx = 30,
        bool atTop = false)
    {
        int padH = Math.Max(0, padHorizontalPx);
        int padT = Math.Max(0, padTopPx);
        if (atTop)
        {
            return cursorX >= dockZone.Left - padH
                && cursorX <= dockZone.Right + padH
                && cursorY >= dockZone.Top
                && cursorY <= dockZone.Bottom + padT;
        }
        return cursorX >= dockZone.Left - padH
            && cursorX <= dockZone.Right + padH
            && cursorY >= dockZone.Top - padT
            && cursorY <= dockZone.Bottom;
    }

    public static bool IsNonBlockingShellOrWallpaperClass(string? className, string? processName = null)
    {
        if (!string.IsNullOrWhiteSpace(processName))
        {
            string trimmedProc = processName.Trim();
            if (trimmedProc.Equals("wallpaper32", StringComparison.OrdinalIgnoreCase) ||
                trimmedProc.Equals("wallpaper64", StringComparison.OrdinalIgnoreCase) ||
                trimmedProc.Equals("Lively", StringComparison.OrdinalIgnoreCase) ||
                trimmedProc.Equals("Rainmeter", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(className)) return false;
        string cls = className.Trim();
        if (IsTaskbarWindowClass(cls) || IsTrayHostWindowClass(cls)) return true;

        return cls.Equals("Progman", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("TopLevelOverflowWindow", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("TopLevelWindowForXamlOverflow", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("EdgeUiInputTopWndClass", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("Shell_LightDismissHost", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("IME", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("SysShadow", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("TaskListThumbnailWnd", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("MultitaskingViewFrame", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("ForegroundStaging", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("Xaml_WindowedPopupClass", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("#32768", StringComparison.OrdinalIgnoreCase)
            || cls.Equals("DinamikAdaPureDimmerOverlay", StringComparison.OrdinalIgnoreCase);
    }

    public static (double CenterX, double CenterY) ComputeDockAuraOrbitPoint(
        double phase,
        double freqX,
        double freqY,
        double phaseOffset,
        double ampX = 0.34,
        double ampY = 0.24)
    {
        if (!double.IsFinite(phase)) phase = 0.0;
        double x = 0.5 + Math.Cos(phase * freqX + phaseOffset) * ampX;
        double y = 0.5 + Math.Sin(phase * freqY + phaseOffset * 0.7) * ampY;
        return (Math.Clamp(x, 0.05, 0.95), Math.Clamp(y, 0.10, 0.90));
    }

    /// <summary>
    /// Index a pinned slot should land at when released at <paramref name="cursorX"/>.
    /// <paramref name="centersX"/> are the resting slot centers in Dock order. Pass
    /// <paramref name="draggedIndex"/> = -1 for an external drop (insertion, range 0..n).
    /// </summary>
    public static int ComputeReorderTargetIndex(IReadOnlyList<double> centersX, double cursorX, int draggedIndex)
    {
        int target = 0;
        for (int i = 0; i < centersX.Count; i++)
        {
            if (i == draggedIndex) continue;
            if (centersX[i] < cursorX) target++;
        }
        return target;
    }

    /// <summary>
    /// Horizontal preview offset for pinned slot <paramref name="index"/> while a reorder or
    /// external drop is in progress, so the other icons visibly make room at the target.
    /// </summary>
    public static double ComputeReorderShift(int index, int draggedIndex, int targetIndex, double pitch)
    {
        if (draggedIndex < 0)
        {
            return index < targetIndex ? -pitch * 0.5 : pitch * 0.5;
        }
        if (index == draggedIndex) return 0.0;
        if (draggedIndex < targetIndex && index > draggedIndex && index <= targetIndex) return -pitch;
        if (draggedIndex > targetIndex && index >= targetIndex && index < draggedIndex) return pitch;
        return 0.0;
    }
}
