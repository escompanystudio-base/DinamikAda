using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DinamikAda.Core.Localization;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.App.Views;

public sealed record CapturedRegionPixels(byte[] BgraPixels, int Width, int Height);

public sealed record PickedColorResult(string Hex, string RgbText, string HslText, byte R, byte G, byte B);

/// <summary>
/// Pure pixel sampling, RGB->HEX/RGB/HSL formatting, DPI coordinate conversion,
/// and loupe HUD clamping helpers for <see cref="RegionCaptureWindow"/>.
/// </summary>
public static class ColorPickerFormatter
{
    public static (byte R, byte G, byte B) ReadPixelFromBgra(
        byte[] bgra,
        int widthPx,
        int heightPx,
        int strideBytes,
        int xPx,
        int yPx)
    {
        if (bgra is null || bgra.Length < 4 || widthPx <= 0 || heightPx <= 0 || strideBytes < 4)
        {
            return (0, 0, 0);
        }

        int cx = Math.Clamp(xPx, 0, widthPx - 1);
        int cy = Math.Clamp(yPx, 0, heightPx - 1);
        long offset = (long)cy * strideBytes + (long)cx * 4;
        if (offset < 0 || offset + 2 >= bgra.Length)
        {
            return (0, 0, 0);
        }

        return (bgra[offset + 2], bgra[offset + 1], bgra[offset]);
    }

    public static PickedColorResult FormatColor(byte r, byte g, byte b)
    {
        string hex = $"#{r:X2}{g:X2}{b:X2}";
        string rgbText = $"rgb({r}, {g}, {b})";

        double rf = r / 255.0;
        double gf = g / 255.0;
        double bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;
        double l = (max + min) * 0.5;

        double hDeg = 0.0;
        double s = 0.0;
        if (delta > 0.0)
        {
            s = l <= 0.5
                ? delta / (max + min)
                : delta / (2.0 - max - min);

            if (max == rf)
            {
                hDeg = 60.0 * ((gf - bf) / delta);
                if (hDeg < 0.0) hDeg += 360.0;
            }
            else if (max == gf)
            {
                hDeg = 60.0 * ((bf - rf) / delta + 2.0);
            }
            else
            {
                hDeg = 60.0 * ((rf - gf) / delta + 4.0);
            }
        }

        int h = (int)Math.Round(hDeg, MidpointRounding.AwayFromZero) % 360;
        if (h < 0) h += 360;
        int sPct = Math.Clamp((int)Math.Round(s * 100.0, MidpointRounding.AwayFromZero), 0, 100);
        int lPct = Math.Clamp((int)Math.Round(l * 100.0, MidpointRounding.AwayFromZero), 0, 100);
        string hslText = $"hsl({h}, {sPct}%, {lPct}%)";

        return new PickedColorResult(hex, rgbText, hslText, r, g, b);
    }

    public static (int XPx, int YPx) ConvertDipToPixel(
        double xDip,
        double yDip,
        double actualWidthDip,
        double actualHeightDip,
        int virtualWidthPx,
        int virtualHeightPx,
        double dpiScaleX = 1.0,
        double dpiScaleY = 1.0)
    {
        if (virtualWidthPx <= 0 || virtualHeightPx <= 0)
        {
            return (0, 0);
        }

        double rawX = actualWidthDip > 0.0
            ? xDip / actualWidthDip * virtualWidthPx
            : xDip * dpiScaleX;
        double rawY = actualHeightDip > 0.0
            ? yDip / actualHeightDip * virtualHeightPx
            : yDip * dpiScaleY;

        int xPx = double.IsFinite(rawX)
            ? Math.Clamp((int)Math.Round(rawX, MidpointRounding.AwayFromZero), 0, virtualWidthPx - 1)
            : 0;
        int yPx = double.IsFinite(rawY)
            ? Math.Clamp((int)Math.Round(rawY, MidpointRounding.AwayFromZero), 0, virtualHeightPx - 1)
            : 0;
        return (xPx, yPx);
    }

    public static void CopyMagnifierPatchBgra(
        byte[] sourceBgra,
        int sourceWidthPx,
        int sourceHeightPx,
        int sourceStrideBytes,
        int centerXPx,
        int centerYPx,
        byte[] patchBgra,
        int patchSizePx)
    {
        if (sourceBgra is null || patchBgra is null || patchSizePx <= 0) return;
        int needed = patchSizePx * patchSizePx * 4;
        if (patchBgra.Length < needed) return;

        int half = patchSizePx / 2;
        for (int py = 0; py < patchSizePx; py++)
        {
            int sy = centerYPx - half + py;
            for (int px = 0; px < patchSizePx; px++)
            {
                int sx = centerXPx - half + px;
                int dstOffset = (py * patchSizePx + px) * 4;

                if ((uint)sx < (uint)sourceWidthPx && (uint)sy < (uint)sourceHeightPx)
                {
                    long srcOffset = (long)sy * sourceStrideBytes + (long)sx * 4;
                    if (srcOffset >= 0 && srcOffset + 3 < sourceBgra.Length)
                    {
                        patchBgra[dstOffset + 0] = sourceBgra[srcOffset + 0];
                        patchBgra[dstOffset + 1] = sourceBgra[srcOffset + 1];
                        patchBgra[dstOffset + 2] = sourceBgra[srcOffset + 2];
                        patchBgra[dstOffset + 3] = 0xFF;
                        continue;
                    }
                }

                // Out-of-bounds desktop edge renders as dark #0A0B0E canvas rather than smeared border pixels.
                patchBgra[dstOffset + 0] = 0x0E;
                patchBgra[dstOffset + 1] = 0x0B;
                patchBgra[dstOffset + 2] = 0x0A;
                patchBgra[dstOffset + 3] = 0xFF;
            }
        }
    }

    public static (double LeftDip, double TopDip) ComputeLoupePosition(
        double cursorDipX,
        double cursorDipY,
        double hudWidthDip,
        double hudHeightDip,
        double boundsLeftDip,
        double boundsTopDip,
        double boundsRightDip,
        double boundsBottomDip,
        double offsetDip = 20.0,
        double edgePaddingDip = 10.0)
    {
        double left = cursorDipX + offsetDip;
        if (left + hudWidthDip + edgePaddingDip > boundsRightDip)
        {
            double flippedLeft = cursorDipX - offsetDip - hudWidthDip;
            left = flippedLeft >= boundsLeftDip + edgePaddingDip
                ? flippedLeft
                : boundsRightDip - hudWidthDip - edgePaddingDip;
        }

        double top = cursorDipY + offsetDip;
        if (top + hudHeightDip + edgePaddingDip > boundsBottomDip)
        {
            double flippedTop = cursorDipY - offsetDip - hudHeightDip;
            top = flippedTop >= boundsTopDip + edgePaddingDip
                ? flippedTop
                : boundsBottomDip - hudHeightDip - edgePaddingDip;
        }

        double minLeft = boundsLeftDip + edgePaddingDip;
        double maxLeft = Math.Max(minLeft, boundsRightDip - hudWidthDip - edgePaddingDip);
        double minTop = boundsTopDip + edgePaddingDip;
        double maxTop = Math.Max(minTop, boundsBottomDip - hudHeightDip - edgePaddingDip);

        return (Math.Clamp(left, minLeft, maxLeft), Math.Clamp(top, minTop, maxTop));
    }
}

/// <summary>
/// Full-virtual-screen DPI-accurate overlay supporting both OCR region capture
/// and 8x live-magnifier eyedropper color picking over a frozen desktop snapshot.
/// </summary>
public partial class RegionCaptureWindow : Window
{
    private const int LoupeSampleSize = 15; // 15x15 source pixels rendered at 8x (120x120 DIPs)

    private enum CaptureMode
    {
        Region = 0,
        Eyedropper = 1
    }

    private sealed record ScreenCaptureData(
        byte[] BgraPixels,
        int VirtualLeftPx,
        int VirtualTopPx,
        int VirtualWidthPx,
        int VirtualHeightPx,
        int StrideBytes,
        BitmapSource Preview);

    private readonly CaptureMode _mode;
    private readonly byte[] _screenBgra;
    private readonly int _virtualLeftPx;
    private readonly int _virtualTopPx;
    private readonly int _virtualWidthPx;
    private readonly int _virtualHeightPx;
    private readonly int _strideBytes;
    private double _dpiX = 96.0;
    private double _dpiY = 96.0;

    // Region mode state
    private System.Windows.Point _startDip;
    private bool _dragging;
    private Rect _selectedDip;

    // Eyedropper mode state
    private readonly WriteableBitmap? _loupeBitmap;
    private readonly byte[]? _loupeBuffer;
    private readonly SolidColorBrush _swatchBrush = new(Colors.Black);
    private bool _eyedropperPressed;
    private int _lastSampledXPx = int.MinValue;
    private int _lastSampledYPx = int.MinValue;
    private PickedColorResult? _currentColor;

    public CapturedRegionPixels? Result { get; private set; }
    public PickedColorResult? PickedColor { get; private set; }

    private RegionCaptureWindow(CaptureMode mode, ScreenCaptureData capture, string lang)
    {
        _mode = mode;
        _screenBgra = capture.BgraPixels;
        _virtualLeftPx = capture.VirtualLeftPx;
        _virtualTopPx = capture.VirtualTopPx;
        _virtualWidthPx = capture.VirtualWidthPx;
        _virtualHeightPx = capture.VirtualHeightPx;
        _strideBytes = capture.StrideBytes;

        InitializeComponent();
        BackdropImage.Source = capture.Preview;
        EyedropperSwatch.Background = _swatchBrush;

        if (_mode == CaptureMode.Eyedropper)
        {
            DimOverlayPath.Visibility = Visibility.Collapsed;
            HintPill.Visibility = Visibility.Collapsed;
            SelectionBorder.Visibility = Visibility.Collapsed;
            EyedropperHud.Visibility = Visibility.Visible;

            _loupeBitmap = new WriteableBitmap(LoupeSampleSize, LoupeSampleSize, 96, 96, PixelFormats.Bgra32, null);
            _loupeBuffer = new byte[LoupeSampleSize * LoupeSampleSize * 4];
            LoupeImage.Source = _loupeBitmap;
            EyedropperHintText.Text = Strings.Pick(
                lang,
                "Sol Tık: #HEX Kopyala · Sağ Tık / Esc: İptal",
                "Left Click: Copy #HEX · Right Click / Esc: Cancel");
        }
        else
        {
            DimOverlayPath.Visibility = Visibility.Visible;
            HintPill.Visibility = Visibility.Visible;
            EyedropperHud.Visibility = Visibility.Collapsed;
            HintText.Text = Strings.Pick(
                lang,
                "OCR + Raf için alanı sürükleyin · Esc: İptal",
                "Drag region for OCR + Shelf · Esc: Cancel");
        }

        Loaded += (_, _) =>
        {
            ApplyVirtualScreenBounds();
            Activate();
            Focus();

            if (_mode == CaptureMode.Eyedropper)
            {
                if (NativeMethods.GetCursorPos(out var cursorPt))
                {
                    UpdateEyedropperAtPhysicalPoint(cursorPt.X, cursorPt.Y);
                }
                else
                {
                    UpdateEyedropperAtDip(Mouse.GetPosition(RootGrid));
                }
            }
        };

        DpiChanged += (_, e) =>
        {
            _dpiX = e.NewDpi.PixelsPerInchX > 0 ? e.NewDpi.PixelsPerInchX : 96.0;
            _dpiY = e.NewDpi.PixelsPerInchY > 0 ? e.NewDpi.PixelsPerInchY : 96.0;

            double scaleX = e.NewDpi.DpiScaleX > 0 ? e.NewDpi.DpiScaleX : 1.0;
            double scaleY = e.NewDpi.DpiScaleY > 0 ? e.NewDpi.DpiScaleY : 1.0;

            Left = _virtualLeftPx / scaleX;
            Top = _virtualTopPx / scaleY;
            Width = _virtualWidthPx / scaleX;
            Height = _virtualHeightPx / scaleY;

            UpdateOverlayLayoutBounds();
        };

        SizeChanged += (_, _) => UpdateOverlayLayoutBounds();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CancelAndClose();
                e.Handled = true;
            }
            else if (_mode == CaptureMode.Eyedropper && e.Key == Key.Enter && _currentColor is not null)
            {
                PickedColor = _currentColor;
                e.Handled = true;
                Close();
            }
        };
    }

    public static CapturedRegionPixels? CaptureInteractiveRegion(string lang)
    {
        var capture = CaptureVirtualScreen();
        if (capture is null) return null;

        var win = new RegionCaptureWindow(CaptureMode.Region, capture, lang);
        win.ShowDialog();
        return win.Result;
    }

    public static PickedColorResult? PickColorInteractive() =>
        PickColorInteractive("tr");

    public static PickedColorResult? PickColorInteractive(string lang)
    {
        var capture = CaptureVirtualScreen();
        if (capture is null) return null;

        var win = new RegionCaptureWindow(CaptureMode.Eyedropper, capture, lang);
        win.ShowDialog();
        return win.PickedColor;
    }

    private static ScreenCaptureData? CaptureVirtualScreen()
    {
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return null;

        byte[]? screenPixels = CaptureVirtualScreenBgra(vx, vy, vw, vh);
        if (screenPixels is null) return null;

        int strideBytes = vw * 4;
        var preview = BitmapSource.Create(
            vw,
            vh,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            screenPixels,
            strideBytes);
        preview.Freeze();

        return new ScreenCaptureData(screenPixels, vx, vy, vw, vh, strideBytes, preview);
    }

    private static unsafe byte[]? CaptureVirtualScreenBgra(int vx, int vy, int vw, int vh)
    {
        nint screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0) return null;
        nint memDc = 0;
        nint hBmp = 0;
        nint oldObj = 0;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == 0) return null;

            var bmi = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = vw,
                biHeight = -vh, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            hBmp = NativeMethods.CreateDIBSection(screenDc, ref bmi, 0, out nint bitsPtr, 0, 0);
            if (hBmp == 0 || bitsPtr == 0) return null;

            oldObj = NativeMethods.SelectObject(memDc, hBmp);
            if (!NativeMethods.BitBlt(memDc, 0, 0, vw, vh, screenDc, vx, vy, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                return null;
            }

            int byteLen = vw * vh * 4;
            byte[] pixels = new byte[byteLen];
            Marshal.Copy(bitsPtr, pixels, 0, byteLen);
            // Force opaque alpha channel (0xFF) so SoftwareBitmap/PNG displays cleanly even if desktop DWM clears alpha bits.
            for (int i = 3; i < byteLen; i += 4)
            {
                pixels[i] = 0xFF;
            }
            return pixels;
        }
        finally
        {
            if (oldObj != 0 && memDc != 0) NativeMethods.SelectObject(memDc, oldObj);
            if (hBmp != 0) NativeMethods.DeleteObject(hBmp);
            if (memDc != 0) NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    private void ApplyVirtualScreenBounds()
    {
        var source = PresentationSource.FromVisual(this);
        double fromDeviceX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double fromDeviceY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
        double toDeviceX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double toDeviceY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        _dpiX = toDeviceX > 0 ? 96.0 * toDeviceX : 96.0;
        _dpiY = toDeviceY > 0 ? 96.0 * toDeviceY : 96.0;

        Left = _virtualLeftPx * fromDeviceX;
        Top = _virtualTopPx * fromDeviceY;
        Width = _virtualWidthPx * fromDeviceX;
        Height = _virtualHeightPx * fromDeviceY;

        UpdateOverlayLayoutBounds();
    }

    private void UpdateOverlayLayoutBounds()
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return;

        FullScreenRectGeom.Rect = new Rect(0, 0, w, h);
        if (_mode == CaptureMode.Region)
        {
            Canvas.SetLeft(HintPill, Math.Max(20, (w - 320) * 0.5));
            Canvas.SetTop(HintPill, 42);
        }
    }

    private (int XPx, int YPx) ConvertDipToPixel(System.Windows.Point dip)
    {
        double viewportW = ActualWidth > 0 ? ActualWidth : Width;
        double viewportH = ActualHeight > 0 ? ActualHeight : Height;
        return ColorPickerFormatter.ConvertDipToPixel(
            dip.X,
            dip.Y,
            viewportW,
            viewportH,
            _virtualWidthPx,
            _virtualHeightPx,
            _dpiX / 96.0,
            _dpiY / 96.0);
    }

    private void UpdateEyedropperAtPhysicalPoint(int screenXPx, int screenYPx)
    {
        double viewportW = ActualWidth > 0 ? ActualWidth : Width;
        double viewportH = ActualHeight > 0 ? ActualHeight : Height;
        int xPx = Math.Clamp(screenXPx - _virtualLeftPx, 0, _virtualWidthPx - 1);
        int yPx = Math.Clamp(screenYPx - _virtualTopPx, 0, _virtualHeightPx - 1);

        double dipX = viewportW > 0 && _virtualWidthPx > 0
            ? (double)xPx / _virtualWidthPx * viewportW
            : xPx * (96.0 / _dpiX);
        double dipY = viewportH > 0 && _virtualHeightPx > 0
            ? (double)yPx / _virtualHeightPx * viewportH
            : yPx * (96.0 / _dpiY);

        UpdateEyedropper(dipX, dipY, xPx, yPx, viewportW, viewportH);
    }

    private void UpdateEyedropperAtDip(System.Windows.Point dip)
    {
        double viewportW = ActualWidth > 0 ? ActualWidth : Width;
        double viewportH = ActualHeight > 0 ? ActualHeight : Height;
        var (xPx, yPx) = ColorPickerFormatter.ConvertDipToPixel(
            dip.X,
            dip.Y,
            viewportW,
            viewportH,
            _virtualWidthPx,
            _virtualHeightPx,
            _dpiX / 96.0,
            _dpiY / 96.0);

        UpdateEyedropper(dip.X, dip.Y, xPx, yPx, viewportW, viewportH);
    }

    private void UpdateEyedropper(
        double dipX,
        double dipY,
        int xPx,
        int yPx,
        double viewportW,
        double viewportH)
    {
        if (xPx != _lastSampledXPx || yPx != _lastSampledYPx)
        {
            _lastSampledXPx = xPx;
            _lastSampledYPx = yPx;

            var (r, g, b) = ColorPickerFormatter.ReadPixelFromBgra(
                _screenBgra,
                _virtualWidthPx,
                _virtualHeightPx,
                _strideBytes,
                xPx,
                yPx);
            var formatted = ColorPickerFormatter.FormatColor(r, g, b);
            _currentColor = formatted;

            _swatchBrush.Color = System.Windows.Media.Color.FromRgb(r, g, b);
            EyedropperHexText.Text = formatted.Hex;
            EyedropperRgbText.Text = formatted.RgbText;
            EyedropperHslText.Text = formatted.HslText;
            EyedropperCoordText.Text = $"{_virtualLeftPx + xPx}, {_virtualTopPx + yPx} px";

            if (_loupeBitmap is not null && _loupeBuffer is not null)
            {
                ColorPickerFormatter.CopyMagnifierPatchBgra(
                    _screenBgra,
                    _virtualWidthPx,
                    _virtualHeightPx,
                    _strideBytes,
                    xPx,
                    yPx,
                    _loupeBuffer,
                    LoupeSampleSize);
                _loupeBitmap.WritePixels(
                    new Int32Rect(0, 0, LoupeSampleSize, LoupeSampleSize),
                    _loupeBuffer,
                    LoupeSampleSize * 4,
                    0);
            }
        }

        if (EyedropperHud.ActualWidth <= 0 || EyedropperHud.ActualHeight <= 0)
        {
            EyedropperHud.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        double hudW = EyedropperHud.ActualWidth > 0
            ? EyedropperHud.ActualWidth
            : Math.Max(220.0, EyedropperHud.DesiredSize.Width);
        double hudH = EyedropperHud.ActualHeight > 0
            ? EyedropperHud.ActualHeight
            : Math.Max(246.0, EyedropperHud.DesiredSize.Height);

        var (monLeftDip, monTopDip, monRightDip, monBottomDip) = GetMonitorBoundsInDip(xPx, yPx, viewportW, viewportH);
        var (hudLeft, hudTop) = ColorPickerFormatter.ComputeLoupePosition(
            dipX,
            dipY,
            hudW,
            hudH,
            monLeftDip,
            monTopDip,
            monRightDip,
            monBottomDip);

        Canvas.SetLeft(EyedropperHud, hudLeft);
        Canvas.SetTop(EyedropperHud, hudTop);
    }

    private unsafe (double LeftDip, double TopDip, double RightDip, double BottomDip) GetMonitorBoundsInDip(
        int xPx,
        int yPx,
        double viewportW,
        double viewportH)
    {
        if (viewportW <= 0 || viewportH <= 0 || _virtualWidthPx <= 0 || _virtualHeightPx <= 0)
        {
            return (0.0, 0.0, Math.Max(1.0, viewportW), Math.Max(1.0, viewportH));
        }

        int screenX = _virtualLeftPx + xPx;
        int screenY = _virtualTopPx + yPx;
        nint hMon = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = screenX, Y = screenY },
            NativeMethods.MONITOR_DEFAULTTONEAREST);

        if (hMon != 0)
        {
            var info = new NativeMethods.MONITORINFOEXW
            {
                cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW)
            };
            if (NativeMethods.GetMonitorInfoW(hMon, ref info))
            {
                double pxToDipX = viewportW / _virtualWidthPx;
                double pxToDipY = viewportH / _virtualHeightPx;
                double left = Math.Clamp((info.rcMonitor.Left - _virtualLeftPx) * pxToDipX, 0.0, viewportW);
                double top = Math.Clamp((info.rcMonitor.Top - _virtualTopPx) * pxToDipY, 0.0, viewportH);
                double right = Math.Clamp((info.rcMonitor.Right - _virtualLeftPx) * pxToDipX, left, viewportW);
                double bottom = Math.Clamp((info.rcMonitor.Bottom - _virtualTopPx) * pxToDipY, top, viewportH);
                if (right - left >= 100.0 && bottom - top >= 100.0)
                {
                    return (left, top, right, bottom);
                }
            }
        }

        return (0.0, 0.0, viewportW, viewportH);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);
        if (_mode == CaptureMode.Eyedropper)
        {
            _eyedropperPressed = true;
            RootGrid.CaptureMouse();
            UpdateEyedropperAtDip(pos);
            e.Handled = true;
            return;
        }

        _startDip = pos;
        _dragging = true;
        _selectedDip = new Rect(_startDip, new System.Windows.Size(0, 0));
        SelectionBorder.Visibility = Visibility.Visible;
        RootGrid.CaptureMouse();
        UpdateSelectionVisual();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var cur = e.GetPosition(RootGrid);
        if (_mode == CaptureMode.Eyedropper)
        {
            UpdateEyedropperAtDip(cur);
            return;
        }

        if (!_dragging) return;
        double x = Math.Clamp(Math.Min(_startDip.X, cur.X), 0, ActualWidth);
        double y = Math.Clamp(Math.Min(_startDip.Y, cur.Y), 0, ActualHeight);
        double right = Math.Clamp(Math.Max(_startDip.X, cur.X), 0, ActualWidth);
        double bottom = Math.Clamp(Math.Max(_startDip.Y, cur.Y), 0, ActualHeight);
        _selectedDip = new Rect(x, y, right - x, bottom - y);
        UpdateSelectionVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == CaptureMode.Eyedropper)
        {
            if (!_eyedropperPressed) return;
            _eyedropperPressed = false;
            if (RootGrid.IsMouseCaptured)
            {
                RootGrid.ReleaseMouseCapture();
            }

            UpdateEyedropperAtDip(e.GetPosition(RootGrid));
            PickedColor = _currentColor;
            e.Handled = true;
            Close();
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        if (RootGrid.IsMouseCaptured)
        {
            RootGrid.ReleaseMouseCapture();
        }

        if (_selectedDip.Width < 8 || _selectedDip.Height < 8 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            Result = null;
            Close();
            return;
        }

        var (px, py) = ConvertDipToPixel(_selectedDip.TopLeft);
        int pw = Math.Clamp((int)Math.Round(_selectedDip.Width / ActualWidth * _virtualWidthPx, MidpointRounding.AwayFromZero), 1, _virtualWidthPx - px);
        int ph = Math.Clamp((int)Math.Round(_selectedDip.Height / ActualHeight * _virtualHeightPx, MidpointRounding.AwayFromZero), 1, _virtualHeightPx - py);

        byte[] cropped = new byte[pw * ph * 4];
        int dstStride = pw * 4;
        for (int row = 0; row < ph; row++)
        {
            Buffer.BlockCopy(_screenBgra, (py + row) * _strideBytes + px * 4, cropped, row * dstStride, dstStride);
        }

        Result = new CapturedRegionPixels(cropped, pw, ph);
        Close();
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelAndClose();
        e.Handled = true;
    }

    private void CancelAndClose()
    {
        _dragging = false;
        _eyedropperPressed = false;
        if (RootGrid.IsMouseCaptured)
        {
            RootGrid.ReleaseMouseCapture();
        }

        Result = null;
        PickedColor = null;
        Close();
    }

    private void UpdateSelectionVisual()
    {
        SelectionHoleGeom.Rect = _selectedDip;
        Canvas.SetLeft(SelectionBorder, _selectedDip.X);
        Canvas.SetTop(SelectionBorder, _selectedDip.Y);
        SelectionBorder.Width = Math.Max(0, _selectedDip.Width);
        SelectionBorder.Height = Math.Max(0, _selectedDip.Height);
    }
}
