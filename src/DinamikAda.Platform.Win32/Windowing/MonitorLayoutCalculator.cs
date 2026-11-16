using System.Runtime.InteropServices;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

public sealed record WindowPlacementResult(
    int LeftPx,
    int TopPx,
    int WidthPx,
    int HeightPx,
    double LeftDip,
    double TopDip,
    double WidthDip,
    double HeightDip,
    double DpiScale);

/// <summary>
/// Pure DIP/physical-pixel layout calculator supporting mixed-DPI monitors,
/// negative virtual desktop origins, magnetic anchors, and monitor unplug clamping.
/// </summary>
public static class MonitorLayoutCalculator
{
    public static double ResolveAnchorRatio(HorizontalAnchorDetent detent, double customRatio) => detent switch
    {
        HorizontalAnchorDetent.LeftSafe => 0.28,
        HorizontalAnchorDetent.Center => 0.50,
        HorizontalAnchorDetent.RightSafe => 0.72,
        HorizontalAnchorDetent.Custom => Math.Clamp(customRatio, 0.08, 0.92),
        _ => 0.50
    };

    public static (HorizontalAnchorDetent Detent, double Ratio) SnapHorizontalDetent(
        double centerXDip,
        double monitorWidthDip,
        double snapRadiusDip = 28.0)
    {
        if (monitorWidthDip <= 1.0) return (HorizontalAnchorDetent.Center, 0.5);

        double leftX = monitorWidthDip * 0.28;
        double centerX = monitorWidthDip * 0.50;
        double rightX = monitorWidthDip * 0.72;

        if (Math.Abs(centerXDip - centerX) <= snapRadiusDip) return (HorizontalAnchorDetent.Center, 0.50);
        if (Math.Abs(centerXDip - leftX) <= snapRadiusDip) return (HorizontalAnchorDetent.LeftSafe, 0.28);
        if (Math.Abs(centerXDip - rightX) <= snapRadiusDip) return (HorizontalAnchorDetent.RightSafe, 0.72);

        double ratio = Math.Clamp(centerXDip / monitorWidthDip, 0.08, 0.92);
        return (HorizontalAnchorDetent.Custom, ratio);
    }

    public static WindowPlacementResult ComputeHostWindowBounds(
        MonitorDescriptor monitor,
        double hostWidthDip,
        double hostHeightDip,
        HorizontalAnchorDetent detent,
        double customRatio,
        DockPlacementMode dockMode,
        double freeTopOffsetDip)
    {
        double scale = Math.Max(1.0, monitor.DpiScale);
        double monLeftDip = monitor.LeftPx / scale;
        double monTopDip = monitor.TopPx / scale;
        double monWidthDip = monitor.WidthPx / scale;
        double monHeightDip = monitor.HeightPx / scale;

        double anchorRatio = ResolveAnchorRatio(detent, customRatio);
        double targetCenterDip = monLeftDip + monWidthDip * anchorRatio;
        double edgeMarginDip = dockMode == DockPlacementMode.NotchFlush ? 0.0 : 4.0;
        double minLeftDip = monLeftDip + edgeMarginDip;
        double maxLeftDip = Math.Max(minLeftDip, monLeftDip + monWidthDip - hostWidthDip - edgeMarginDip);
        double clampedLeftDip = Math.Clamp(targetCenterDip - hostWidthDip * 0.5, minLeftDip, maxLeftDip);

        double rawTopOffsetDip = dockMode switch
        {
            DockPlacementMode.NotchFlush => 0.0,
            DockPlacementMode.FloatingTop => 8.0,
            DockPlacementMode.FreeFloat => Math.Clamp(freeTopOffsetDip, 0.0, Math.Max(0.0, monHeightDip - hostHeightDip - 16.0)),
            _ => 8.0
        };

        double topDip = monTopDip + rawTopOffsetDip;
        int leftPx = (int)Math.Round(clampedLeftDip * scale, MidpointRounding.AwayFromZero);
        int topPx = dockMode == DockPlacementMode.NotchFlush
            ? monitor.TopPx
            : (int)Math.Round(topDip * scale, MidpointRounding.AwayFromZero);
        int widthPx = (int)Math.Round(hostWidthDip * scale, MidpointRounding.AwayFromZero);
        if ((widthPx & 1) != 0) widthPx += 1;
        int heightPx = (int)Math.Round(hostHeightDip * scale, MidpointRounding.AwayFromZero);
        if (monitor.WidthPx >= widthPx)
        {
            leftPx = Math.Clamp(leftPx, monitor.LeftPx, monitor.LeftPx + monitor.WidthPx - widthPx);
        }

        return new WindowPlacementResult(
            leftPx,
            topPx,
            widthPx,
            heightPx,
            clampedLeftDip,
            topDip,
            hostWidthDip,
            hostHeightDip,
            scale);
    }
}

public sealed unsafe class Win32MonitorLayoutService : IMonitorLayoutService
{
    private readonly object _cacheLock = new();
    private IReadOnlyList<MonitorDescriptor>? _cachedMonitors;

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedMonitors = null;
        }
    }

    public IReadOnlyList<MonitorDescriptor> GetMonitors()
    {
        lock (_cacheLock)
        {
            if (_cachedMonitors is { Count: > 0 } cached)
            {
                return cached;
            }
        }

        var results = EnumerateMonitors();
        lock (_cacheLock)
        {
            _cachedMonitors = results;
        }
        return results;
    }

    public MonitorDescriptor GetTargetMonitor(MonitorPlacementMode mode, string? pinnedDeviceName, int cursorXPx, int cursorYPx)
    {
        var all = GetMonitors();
        if (all.Count == 0)
        {
            return new MonitorDescriptor(0, "\\\\.\\DISPLAY1", "Primary Display", true, 0, 0, 1920, 1080, 1.0, 60);
        }

        if (mode == MonitorPlacementMode.PinnedMonitor && !string.IsNullOrWhiteSpace(pinnedDeviceName))
        {
            var pinned = all.FirstOrDefault(m => string.Equals(m.DeviceName, pinnedDeviceName, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null) return pinned;
        }

        if (mode == MonitorPlacementMode.FollowCursor)
        {
            nint hMon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = cursorXPx, Y = cursorYPx }, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var atCursor = all.FirstOrDefault(m => m.Handle == hMon)
                ?? all.FirstOrDefault(m =>
                    cursorXPx >= m.LeftPx && cursorXPx < m.LeftPx + m.WidthPx &&
                    cursorYPx >= m.TopPx && cursorYPx < m.TopPx + m.HeightPx);
            if (atCursor is not null) return atCursor;
        }

        return all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
    }

    public int GetMonitorRefreshRateHz(nint hwnd)
    {
        var all = GetMonitors();
        if (all.Count == 0) return 60;

        if (hwnd != 0)
        {
            nint hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var match = all.FirstOrDefault(m => m.Handle == hMon);
            if (match is not null && match.RefreshRateHz > 0)
            {
                return match.RefreshRateHz;
            }
        }

        var primary = all.FirstOrDefault(m => m.IsPrimary) ?? all[0];
        return primary.RefreshRateHz > 0 ? primary.RefreshRateHz : 60;
    }

    public bool TryGetDwmRefreshPeriodSeconds(nint hwnd, out double periodSec)
    {
        var timing = new NativeMethods.DWM_TIMING_INFO
        {
            cbSize = (uint)sizeof(NativeMethods.DWM_TIMING_INFO)
        };

        // On Windows 8+, DwmGetCompositionTimingInfo requires hwnd == 0 and returns DWM's active compose/refresh cadence.
        if (NativeMethods.DwmGetCompositionTimingInfo(0, ref timing) == 0)
        {
            if (TryRatioToSeconds(timing.rateCompose, out periodSec) ||
                TryRatioToSeconds(timing.rateRefresh, out periodSec))
            {
                return true;
            }
        }

        int fallbackHz = Math.Clamp(GetMonitorRefreshRateHz(hwnd), 24, 500);
        periodSec = 1.0 / fallbackHz;
        return false;
    }

    private static bool TryRatioToSeconds(NativeMethods.UNSIGNED_RATIO ratio, out double periodSec)
    {
        if (ratio.uiNumerator > 0 && ratio.uiDenominator > 0)
        {
            double hz = (double)ratio.uiNumerator / ratio.uiDenominator;
            if (hz is >= 24.0 and <= 500.0)
            {
                periodSec = 1.0 / hz;
                return true;
            }
        }

        periodSec = 0;
        return false;
    }

    private static List<MonitorDescriptor> EnumerateMonitors()
    {
        var results = new List<MonitorDescriptor>();
        GCHandle handle = GCHandle.Alloc(results);
        try
        {
            NativeMethods.EnumDisplayMonitors(0, 0, EnumCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return results;
    }

    private static bool EnumCallback(nint hMonitor, nint hdc, NativeMethods.RECT* rect, nint data)
    {
        var list = (List<MonitorDescriptor>?)GCHandle.FromIntPtr(data).Target;
        if (list is null) return false;

        var info = new NativeMethods.MONITORINFOEXW { cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW) };
        if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info)) return true;

        string devName = new string(info.szDevice).TrimEnd('\0');
        uint dpiX = 96;
        _ = NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out _);
        double scale = Math.Max(1.0, dpiX / 96.0);
        bool isPrimary = (info.dwFlags & 1) != 0;

        int refreshHz = 60;
        if (!string.IsNullOrWhiteSpace(devName))
        {
            var dm = new NativeMethods.DEVMODEW { dmSize = (ushort)sizeof(NativeMethods.DEVMODEW) };
            if (NativeMethods.EnumDisplaySettingsW(devName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm) &&
                dm.dmDisplayFrequency is >= 24 and <= 500)
            {
                refreshHz = (int)dm.dmDisplayFrequency;
            }
        }

        list.Add(new MonitorDescriptor(
            hMonitor,
            devName,
            string.IsNullOrWhiteSpace(devName) ? $"Display {list.Count + 1}" : devName.Replace("\\\\.\\", string.Empty, StringComparison.Ordinal),
            isPrimary,
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            info.rcMonitor.Width,
            info.rcMonitor.Height,
            scale,
            refreshHz,
            info.rcWork.Left,
            info.rcWork.Top,
            info.rcWork.Width,
            info.rcWork.Height));

        return true;
    }
}
