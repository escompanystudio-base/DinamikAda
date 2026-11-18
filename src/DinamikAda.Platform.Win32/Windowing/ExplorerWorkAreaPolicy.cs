using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

public static class ExplorerWorkAreaPolicy
{
    // Windows can reserve a two-DIP activation strip when the native bar auto-hides.
    // Release that strip only; never overwrite the space of another appbar.
    public static bool TryReleaseActivationStrip(NativeMethods.RECT monitor, NativeMethods.RECT work,
        uint dpi, out NativeMethods.RECT expanded)
    {
        expanded = work;
        int gap = monitor.Bottom - work.Bottom;
        int activationStrip = Math.Max(2, (int)Math.Ceiling(2.0 * dpi / 96.0));
        if (gap <= 0 || gap > activationStrip || work.Right <= work.Left || work.Bottom <= work.Top)
            return false;
        expanded.Bottom = monitor.Bottom;
        return true;
    }
}
