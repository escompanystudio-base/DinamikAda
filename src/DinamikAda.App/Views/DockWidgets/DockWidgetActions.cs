using System.Diagnostics;
using System.Runtime.InteropServices;
using DinamikAda.App.Services;

namespace DinamikAda.App.Views.DockWidgets;

/// <summary>OS side effects used by tiles. Every call is a no-op in audit/smoke mode.</summary>
public static class DockWidgetActions
{
    /// <summary>Starts an executable, document, folder or URI through the shell.</summary>
    public static void Run(DockWidgetContext ctx, string target, string? arguments = null)
    {
        if (ctx.IsAuditMode || string.IsNullOrWhiteSpace(target)) return;
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(arguments)) psi.Arguments = arguments;
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("DockWidget", ex, $"Could not start '{target}'.");
        }
    }

    /// <summary>Opens an http(s) URL in the default browser.</summary>
    public static void OpenUrl(DockWidgetContext ctx, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        Run(ctx, uri.AbsoluteUri);
    }

    /// <summary>Opens a ms-settings: page.</summary>
    public static void OpenSettings(DockWidgetContext ctx, string uri)
    {
        if (ctx.IsAuditMode) return;
        ctx.Orch.Connectivity.OpenWindowsSettings(uri);
    }

    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    /// <summary>Sends a key chord such as Win+D or Win+N (modifiers pressed in order, released in reverse).</summary>
    public static void SendChord(DockWidgetContext ctx, params ushort[] keys)
    {
        if (ctx.IsAuditMode || keys.Length == 0) return;
        foreach (var k in keys) keybd_event((byte)k, 0, 0, 0);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event((byte)keys[i], 0, KEYEVENTF_KEYUP, 0);
    }

    public static void WinPlus(DockWidgetContext ctx, char key) => SendChord(ctx, VK_LWIN, char.ToUpperInvariant(key));
    public static void CtrlShiftPlus(DockWidgetContext ctx, ushort vk) => SendChord(ctx, VK_CONTROL, VK_SHIFT, vk);
    public static void AltShift(DockWidgetContext ctx) => SendChord(ctx, VK_MENU, VK_SHIFT);
}
