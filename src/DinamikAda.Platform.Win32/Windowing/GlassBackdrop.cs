using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DinamikAda.Platform.Win32.Interop;
using Microsoft.Win32;

namespace DinamikAda.Platform.Win32.Windowing;

/// <summary>Which composition effect a <see cref="GlassBackdropWindow"/> asks DWM for.</summary>
public enum GlassBackdropMode
{
    /// <summary>ACCENT_ENABLE_ACRYLICBLURBEHIND: strong blur plus noise; best for panels that rarely resize.</summary>
    Acrylic = 0,

    /// <summary>ACCENT_ENABLE_BLURBEHIND: plain blur, cheap to move and resize every frame.</summary>
    Blur = 1
}

/// <summary>An integer rectangle in physical screen pixels (right/bottom exclusive).</summary>
public readonly record struct GlassPixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public GlassPixelRect Intersect(GlassPixelRect other)
    {
        int l = Math.Max(Left, other.Left);
        int t = Math.Max(Top, other.Top);
        int r = Math.Min(Right, other.Right);
        int b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? default : new GlassPixelRect(l, t, r, b);
    }
}

/// <summary>
/// Where the backdrop window goes and which rounded region it keeps: the rounded shape is clipped to the
/// content window (so a dock sliding off screen never spills its blur onto a neighbouring monitor) and
/// inset slightly so the aliased region edge always hides under the content's anti-aliased border.
/// </summary>
public readonly record struct GlassBackdropLayout(GlassPixelRect Window, int RegionLeft, int RegionTop, int RegionWidth, int RegionHeight, int RadiusPx)
{
    public bool IsEmpty => Window.IsEmpty || RegionWidth <= 0 || RegionHeight <= 0;

    public static GlassBackdropLayout Compute(GlassPixelRect shape, GlassPixelRect clip, int radiusPx, int insetPx)
    {
        int inset = Math.Max(0, insetPx);
        var inner = new GlassPixelRect(shape.Left + inset, shape.Top + inset, shape.Right - inset, shape.Bottom - inset);
        if (inner.IsEmpty) return default;
        var window = clip.IsEmpty ? inner : inner.Intersect(clip);
        if (window.IsEmpty) return default;
        int radius = Math.Clamp(radiusPx - inset, 0, Math.Min(inner.Width, inner.Height) / 2);
        return new GlassBackdropLayout(window, inner.Left - window.Left, inner.Top - window.Top, inner.Width, inner.Height, radius);
    }
}

/// <summary>OS capability and the user's "Transparency effects" switch.</summary>
public static class GlassBackdropSupport
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Accent blur is reliable from Windows 10 1803 (build 17134) on.</summary>
    public static bool IsOsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134);

    public static bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>Settings › Personalisation › Colours › Transparency effects (missing value = on).</summary>
    public static bool IsTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }
}

/// <summary>
/// A dedicated, non-activating, click-through popup window that only shows the blurred desktop behind a
/// rounded shape of another (layered, transparent) window. It is created once, then only moved, resized,
/// shown and hidden; it always sits directly below the content window in the z-order. On Windows 11 its corners
/// are rounded by DWM (the only clip the accent blur honours); on Windows 10 a window region shapes it.
/// </summary>
public sealed unsafe partial class GlassBackdropWindow : IDisposable
{
    private const string ClassName = "DinamikAda.GlassBackdrop";
    private const int RegionInsetPx = 1;

    /// <summary>
    /// Layered alpha below 255 switches the accent blur off (DWM then shows only a flat veil), so the backdrop is
    /// never faded: it appears once its content is at least this opaque and disappears below it.
    /// </summary>
    public const byte ShowAlphaThreshold = 128;

    private static ushort s_classAtom;
    private static bool s_classFailed;

    private readonly GlassBackdropMode _mode;
    private readonly uint _tintAbgr;
    private nint _hwnd;
    private bool _disposed;
    private bool _failed;
    private bool _visible;
    private GlassPixelRect _windowRect;
    private (int L, int T, int W, int H, int R) _region = (-1, -1, -1, -1, -1);

    /// <param name="mode">Acrylic or plain blur.</param>
    /// <param name="tintArgb">Tint baked into the accent (0xAARRGGBB). Keep it nearly transparent and draw the real tint in WPF.</param>
    public GlassBackdropWindow(GlassBackdropMode mode, uint tintArgb = 0x01000000)
    {
        _mode = mode;
        uint a = (tintArgb >> 24) & 0xFF, r = (tintArgb >> 16) & 0xFF, g = (tintArgb >> 8) & 0xFF, b = tintArgb & 0xFF;
        _tintAbgr = (Math.Max(1u, a) << 24) | (b << 16) | (g << 8) | r;
    }

    public nint Handle => _hwnd;
    public bool IsVisible => _visible;

    /// <summary>True once composition refused the accent; callers should fall back to a solid tint.</summary>
    public bool HasFailed => _failed;

    /// <summary>Places the blur behind <paramref name="shape"/> and below <paramref name="insertAfter"/>; returns false when nothing is shown.</summary>
    public bool Update(GlassPixelRect shape, int radiusPx, GlassPixelRect clip, byte alpha, nint insertAfter)
    {
        if (_disposed || _failed) return false;
        if (shape.IsEmpty || alpha < ShowAlphaThreshold || insertAfter == 0)
        {
            Hide();
            return false;
        }

        if (!EnsureCreated()) return false;

        // Windows 11: DWM rounds the window itself (anti-aliased, ~8 px at 100 %) and that clip DOES apply to the
        // accent blur, whereas SetWindowRgn does not. Inset the window just enough that DWM's small corner sits
        // inside the content's larger rounded corner, so no untinted blur peeks out past the content's edge.
        int inset = RegionInsetPx;
        if (GlassBackdropSupport.IsWindows11)
        {
            uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
            double dwmRadius = 8.0 * (dpi == 0 ? 1.0 : dpi / 96.0);
            inset = Math.Max(RegionInsetPx, (int)Math.Ceiling(ComputeCornerInset(radiusPx, dwmRadius)));
        }

        var layout = GlassBackdropLayout.Compute(shape, clip, radiusPx, inset);
        if (layout.IsEmpty)
        {
            Hide();
            return false;
        }

        var w = layout.Window;
        var region = (layout.RegionLeft, layout.RegionTop, layout.RegionWidth, layout.RegionHeight, layout.RadiusPx);
        if (region != _region && !GlassBackdropSupport.IsWindows11)
        {
            // Windows 10 has no DWM corner rounding; a region at least shapes the window there.
            _region = region;
            int d = layout.RadiusPx * 2;
            nint rgn = CreateRoundRectRgn(layout.RegionLeft, layout.RegionTop,
                layout.RegionLeft + layout.RegionWidth + 1, layout.RegionTop + layout.RegionHeight + 1, d, d);
            if (rgn != 0 && SetWindowRgn(_hwnd, rgn, _visible ? 1 : 0) == 0)
            {
                NativeMethods.DeleteObject(rgn); // the system owns the region only when the call succeeds
            }
        }

        bool moved = w != _windowRect;
        bool zOk = _visible && NativeMethods.GetWindow(insertAfter, GW_HWNDNEXT) == _hwnd;
        if (moved || !zOk || !_visible)
        {
            uint flags = NativeMethods.SWP_NOACTIVATE | SWP_NOOWNERZORDER;
            if (!moved) flags |= NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE;
            if (!_visible) flags |= SWP_SHOWWINDOW;
            NativeMethods.SetWindowPos(_hwnd, insertAfter, w.Left, w.Top, w.Width, w.Height, flags);
            _windowRect = w;
            _visible = true;
        }

        return true;
    }

    public void Hide()
    {
        if (!_visible || _hwnd == 0) return;
        _visible = false;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    private bool EnsureCreated()
    {
        if (_hwnd != 0) return true;
        if (!GlassBackdropSupport.IsOsSupported || !EnsureClass())
        {
            _failed = true;
            return false;
        }

        // Layered + transparent = click-through for every process; the layered alpha stays 255 (see ShowAlphaThreshold).
        const int exStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED;
        _hwnd = CreateWindowExW(exStyle, ClassName, "DinamikAda Glass", WS_POPUP, 0, 0, 1, 1, 0, 0,
            NativeMethods.GetModuleHandleW(null), 0);
        if (_hwnd == 0)
        {
            _failed = true;
            return false;
        }

        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);

        // No DWM show/hide transitions (they would flash). On Windows 11 DWM rounds the corners: the only clip the
        // accent blur honours. DWMWA_BORDER_COLOR is left alone on purpose: "none" makes DWM drop that corner clip.
        int on = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &on, sizeof(int));
        DwmSetWindowAttribute(_hwnd, DWMWA_EXCLUDED_FROM_PEEK, &on, sizeof(int));
        if (GlassBackdropSupport.IsWindows11)
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &round, sizeof(int));
        }

        // AccentFlags must stay 0: with flag 2 and a near-transparent gradient DWM paints a black slab, no blur.
        if (!ApplyAccent(_mode == GlassBackdropMode.Acrylic ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND)
            && !(_mode == GlassBackdropMode.Acrylic && ApplyAccent(ACCENT_ENABLE_BLURBEHIND)))
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
            _failed = true;
            return false;
        }

        return true;
    }

    /// <summary>
    /// How far (px) a rectangle with DWM's small corner radius must be inset so its corners stay inside a rounded
    /// rectangle of <paramref name="shapeRadius"/>: (R − r)(1 − 1/√2).
    /// </summary>
    public static double ComputeCornerInset(double shapeRadius, double dwmRadius) =>
        Math.Max(0.0, shapeRadius - dwmRadius) * (1.0 - 1.0 / Math.Sqrt(2.0));

    private bool ApplyAccent(int state)
    {
        var accent = new AccentPolicy { AccentState = state, AccentFlags = 0, GradientColor = _tintAbgr, AnimationId = 0 };
        var data = new WindowCompositionAttribData
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = &accent,
            SizeOfData = sizeof(AccentPolicy)
        };
        try
        {
            return SetWindowCompositionAttribute(_hwnd, &data) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool EnsureClass()
    {
        if (s_classAtom != 0) return true;
        if (s_classFailed) return false;
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = NativeMethods.GetModuleHandleW(null),
                hbrBackground = GetStockObject(BLACK_BRUSH), // GDI black = alpha 0: the accent shows through
                lpszClassName = name
            };
            s_classAtom = RegisterClassExW(&wc);
        }
        if (s_classAtom == 0 && Marshal.GetLastPInvokeError() == ERROR_CLASS_ALREADY_EXISTS) s_classAtom = 1;
        s_classFailed = s_classAtom == 0;
        return !s_classFailed;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                return NativeMethods.HTTRANSPARENT;
            case NativeMethods.WM_MOUSEACTIVATE:
                return NativeMethods.MA_NOACTIVATE;
            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != 0)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        _visible = false;
    }

    // ---- Interop (local to the backdrop so the shared NativeMethods stays untouched) ---------------

    private const uint WS_POPUP = 0x80000000;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint GW_HWNDNEXT = 2;
    private const uint LWA_ALPHA = 0x2;
    private const int BLACK_BRUSH = 4;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_EXCLUDED_FROM_PEEK = 12;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attribute;
        public void* Data;
        public nint SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(int dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hWnd, nint hRgn, int bRedraw);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowCompositionAttribute(nint hwnd, WindowCompositionAttribData* data);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int i);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attr, void* value, int size);
}
