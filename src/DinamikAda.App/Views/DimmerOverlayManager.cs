using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DinamikAda.Core.Abstractions;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.App.Views;

/// <summary>
/// Pure Win32 layered overlay math helpers, isolated for deterministic unit testing.
/// Never clamps above 75% (alpha 191) so the display can never turn solid #000000 black.
/// </summary>
internal static class DimmerMath
{
    public const int MaxDimPercent = 75;
    public const byte MaxAlphaByte = 191;

    public static int ClampDimPercent(int dimPercent) => Math.Clamp(dimPercent, 0, MaxDimPercent);

    public static byte ComputeAlphaByte(int dimPercent)
    {
        int clamped = ClampDimPercent(dimPercent);
        if (clamped <= 0) return 0;
        return (byte)Math.Clamp((int)Math.Round(clamped * 2.55), 1, MaxAlphaByte);
    }

    public static uint ComputePremultipliedBlackPixel(int dimPercent)
    {
        byte alpha = ComputeAlphaByte(dimPercent);
        // Premultiplied BGRA with R=0, G=0, B=0 and A=alpha: safe for DWM AC_SRC_ALPHA under Night Light / HDR / MPO.
        return (uint)alpha << 24;
    }
}

/// <summary>
/// Renders subtle per-monitor dimming overlays using pure Win32 WS_POPUP + WS_EX_LAYERED + UpdateLayeredWindow.
/// Bypasses WPF Window / HwndTarget DXGI swapchains completely, avoiding solid-black rendering under Windows 11 Night Light & AMD MPO.
/// </summary>
public sealed unsafe partial class DimmerOverlayManager : IDisposable
{
    private const string WindowClassName = "DinamikAdaPureDimmerOverlay";

    private static readonly object ClassLock = new();
    private static bool s_classRegistered;

    private sealed class DimmerSurface : IDisposable
    {
        public nint Hwnd;
        public nint HdcMem;
        public nint HBitmap;
        public nint HPrevObj;
        public void* Bits;
        public int Width;
        public int Height;
        public int LastLeft;
        public int LastTop;
        public byte LastAlpha;
        public bool IsShown;

        public void ReleaseBitmap()
        {
            if (HdcMem != 0 && HPrevObj != 0)
            {
                NativeMethods.SelectObject(HdcMem, HPrevObj);
                HPrevObj = 0;
            }
            if (HBitmap != 0)
            {
                NativeMethods.DeleteObject(HBitmap);
                HBitmap = 0;
            }
            if (HdcMem != 0)
            {
                NativeMethods.DeleteDC(HdcMem);
                HdcMem = 0;
            }
            Bits = null;
            Width = 0;
            Height = 0;
            LastAlpha = 0;
        }

        public void Dispose()
        {
            if (Hwnd != 0)
            {
                NativeMethods.ShowWindow(Hwnd, NativeMethods.SW_HIDE);
                NativeMethods.DestroyWindow(Hwnd);
                Hwnd = 0;
            }
            ReleaseBitmap();
            IsShown = false;
        }
    }

    private readonly IMonitorLayoutService _monitors;
    private readonly Dictionary<string, DimmerSurface> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    public DimmerOverlayManager(IMonitorLayoutService monitors)
    {
        _monitors = monitors;
    }

    public void UpdateDimmer(string deviceId, nint monitorHandle, int dimPercent)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        int clamped = DimmerMath.ClampDimPercent(dimPercent);
        byte alphaByte = DimmerMath.ComputeAlphaByte(clamped);

        lock (_sync)
        {
            if (_disposed) return;

            if (alphaByte == 0)
            {
                RemoveSurfaceLocked(deviceId);
                return;
            }

            MonitorDescriptor? mon = ResolveMonitor(deviceId, monitorHandle);
            if (mon is null)
            {
                RemoveSurfaceLocked(deviceId);
                return;
            }

            int w = Math.Max(1, mon.WidthPx);
            int h = Math.Max(1, mon.HeightPx);

            if (!EnsureClassRegistered())
                return;

            if (!_surfaces.TryGetValue(deviceId, out var surface))
            {
                surface = new DimmerSurface();
                _surfaces[deviceId] = surface;
            }

            if (surface.Hwnd == 0 || !NativeMethods.IsWindow(surface.Hwnd))
            {
                surface.Hwnd = CreateOverlayWindow(mon.LeftPx, mon.TopPx, w, h);
                if (surface.Hwnd == 0)
                {
                    RemoveSurfaceLocked(deviceId);
                    return;
                }
                surface.IsShown = false;
            }

            bool sizeChanged = surface.Width != w || surface.Height != h || surface.HBitmap == 0 || surface.Bits == null;
            if (sizeChanged)
            {
                if (!RecreateSurfaceBitmap(surface, w, h))
                {
                    RemoveSurfaceLocked(deviceId);
                    return;
                }
            }

            if (sizeChanged || surface.LastAlpha != alphaByte)
            {
                uint premulPixel = DimmerMath.ComputePremultipliedBlackPixel(clamped);
                int totalPixels = checked(w * h);
                new Span<uint>(surface.Bits, totalPixels).Fill(premulPixel);
                surface.LastAlpha = alphaByte;
            }

            var ptDst = new NativeMethods.POINT { X = mon.LeftPx, Y = mon.TopPx };
            var sz = new NativeMethods.SIZE { cx = w, cy = h };
            var ptSrc = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA
            };

            bool updated = NativeMethods.UpdateLayeredWindow(
                surface.Hwnd,
                0,
                ref ptDst,
                ref sz,
                surface.HdcMem,
                ref ptSrc,
                0,
                ref blend,
                NativeMethods.ULW_ALPHA);

            if (!updated)
            {
                // Fail safe: never leave an unlayered or opaque black window visible.
                RemoveSurfaceLocked(deviceId);
                return;
            }

            surface.LastLeft = mon.LeftPx;
            surface.LastTop = mon.TopPx;

            uint swpFlags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE;
            NativeMethods.SetWindowPos(surface.Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, swpFlags);

            if (!surface.IsShown)
            {
                NativeMethods.ShowWindow(surface.Hwnd, NativeMethods.SW_SHOWNA);
                surface.IsShown = true;
            }
        }
    }

    private MonitorDescriptor? ResolveMonitor(string deviceId, nint monitorHandle)
    {
        var liveMonitors = _monitors.GetMonitors();
        for (int i = 0; i < liveMonitors.Count; i++)
        {
            var m = liveMonitors[i];
            if (string.Equals(m.DeviceName, deviceId, StringComparison.OrdinalIgnoreCase)
                || (monitorHandle != 0 && m.Handle == monitorHandle))
            {
                return m;
            }
        }
        return null;
    }

    private void RemoveSurfaceLocked(string deviceId)
    {
        if (_surfaces.TryGetValue(deviceId, out var surface))
        {
            _surfaces.Remove(deviceId);
            surface.Dispose();
        }
    }

    private static bool EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (s_classRegistered) return true;

            nint hInst = NativeMethods.GetModuleHandleW(null);
            fixed (char* clsPtr = WindowClassName)
            {
                var wc = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    style = 0,
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&DimmerWndProc,
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = hInst,
                    hIcon = 0,
                    hCursor = 0,
                    hbrBackground = 0,
                    lpszMenuName = null,
                    lpszClassName = clsPtr,
                    hIconSm = 0
                };

                ushort atom = NativeMethods.RegisterClassExW(ref wc);
                if (atom == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                    if (err != ERROR_CLASS_ALREADY_EXISTS)
                        return false;
                }

                s_classRegistered = true;
                return true;
            }
        }
    }

    private static nint CreateOverlayWindow(int x, int y, int width, int height)
    {
        uint exStyle = unchecked((uint)(
            NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TOPMOST));

        nint hInst = NativeMethods.GetModuleHandleW(null);
        return NativeMethods.CreateWindowExW(
            exStyle,
            WindowClassName,
            null,
            NativeMethods.WS_POPUP,
            x,
            y,
            width,
            height,
            0,
            0,
            hInst,
            0);
    }

    private static bool RecreateSurfaceBitmap(DimmerSurface surface, int width, int height)
    {
        surface.ReleaseBitmap();

        nint screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0) return false;

        try
        {
            nint memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == 0) return false;

            var bmi = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(NativeMethods.BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            nint hBmp = NativeMethods.CreateDIBSection(memDc, ref bmi, 0, out nint pBits, 0, 0);
            if (hBmp == 0 || pBits == 0)
            {
                if (hBmp != 0) NativeMethods.DeleteObject(hBmp);
                NativeMethods.DeleteDC(memDc);
                return false;
            }

            nint prev = NativeMethods.SelectObject(memDc, hBmp);
            surface.HdcMem = memDc;
            surface.HBitmap = hBmp;
            surface.HPrevObj = prev;
            surface.Bits = (void*)pBits;
            surface.Width = width;
            surface.Height = height;
            surface.LastAlpha = 0;
            return true;
        }
        finally
        {
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint DimmerWndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        switch ((int)msg)
        {
            case NativeMethods.WM_ERASEBKGND:
                return 1;
            case NativeMethods.WM_NCHITTEST:
                return NativeMethods.HTTRANSPARENT;
            case NativeMethods.WM_MOUSEACTIVATE:
                return NativeMethods.MA_NOACTIVATE;
            default:
                return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var surface in _surfaces.Values)
            {
                surface.Dispose();
            }
            _surfaces.Clear();
        }
    }
}
