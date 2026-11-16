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

    public static DockHostBounds ComputeHostWindowBounds(MonitorDescriptor monitor, double hostHeightDip = DefaultHostHeightDip)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        double scale = Math.Max(1.0, monitor.DpiScale);
        double clampedHostH = Math.Clamp(hostHeightDip, 80.0, Math.Max(80.0, monitor.HeightPx / scale));
        int heightPx = (int)Math.Round(clampedHostH * scale, MidpointRounding.AwayFromZero);
        if ((heightPx & 1) != 0) heightPx += 1;
        int widthPx = Math.Max(2, monitor.WidthPx);
        int leftPx = monitor.LeftPx;
        int topPx = monitor.TopPx + monitor.HeightPx - heightPx;
        return new DockHostBounds(leftPx, topPx, widthPx, heightPx, scale);
    }

    public static int ComputeReservedBottomHeightPx(double dpiScale, int iconSizeDip)
    {
        double scale = Math.Clamp(dpiScale, 1.0, 4.0);
        int clampedIcon = Math.Clamp(iconSizeDip, 26, 48);
        double reservedDip = clampedIcon + 21.0; // slim ~46dp capsule + 5dp bottom gap + 2dp margin
        return (int)Math.Round(reservedDip * scale, MidpointRounding.AwayFromZero);
    }

    public static NativeMethods.RECT ComputeReservedWorkArea(MonitorDescriptor monitor, int reservedBottomPx)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        int maxReserve = Math.Max(0, monitor.HeightPx / 3);
        int clampedReserve = Math.Clamp(reservedBottomPx, 0, maxReserve);
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
}
