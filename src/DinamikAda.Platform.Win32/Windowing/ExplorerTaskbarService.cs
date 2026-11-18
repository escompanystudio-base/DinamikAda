using System.Diagnostics;
using System.Text;
using DinamikAda.Platform.Win32.Interop;

namespace DinamikAda.Platform.Win32.Windowing;

/// <summary>Owns an isolated portable Windhawk profile, never the user's installed profile.</summary>
public sealed unsafe class ExplorerTaskbarService : IDisposable
{
    public static string HostDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DinamikAda", "ExplorerHost");
    private static string ModConfigPath => Path.Combine(HostDirectory, "AppData", "Engine", "Mods", ExplorerTaskbarTheme.ModId + ".ini");
    private string? _lastAnimationState;
    private bool? _enabled;
    private NativeMethods.RECT? _activationStripWorkArea;
    private long _revision = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private DateTime _lastStartAttemptUtc;
    public string? LastError { get; private set; }

    public bool IsInstalled => File.Exists(Path.Combine(HostDirectory, "ready.json"))
        && File.Exists(Path.Combine(HostDirectory, "windhawk.exe"))
        && File.Exists(Path.Combine(HostDirectory, "AppData", "Engine", "Mods", "64", ExplorerTaskbarTheme.SizeLibraryFileName))
        && File.Exists(Path.Combine(HostDirectory, "AppData", "Engine", "Mods", "64", ExplorerTaskbarTheme.OverlayLibraryFileName))
        && File.Exists(Path.Combine(HostDirectory, "AppData", "Engine", "Mods", "64", ExplorerTaskbarTheme.StartLibraryFileName))
        && File.Exists(Path.Combine(HostDirectory, "AppData", "Engine", "Mods", "64", ExplorerTaskbarTheme.NotificationLibraryFileName))
        && File.Exists(Path.Combine(HostDirectory, "AppData", "Engine", "Mods", "64", ExplorerTaskbarTheme.LibraryFileName));

    public void Apply(bool enabled, string? accent = null, bool playing = false,
        string? secondary = null, string? highlight = null, bool magnification = true, bool glow = true,
        bool intellihide = true, bool hasMediaSession = false)
    {
        string normalized = ExplorerTaskbarTheme.NormalizeAccent(accent);
        try
        {
            if (!enabled && !File.Exists(ModConfigPath)) return;
            if (enabled && !IsInstalled)
            {
                LastError = "Explorer bileşeni kurulmamış. DinamikAda-Explorer-Kur.bat dosyasını çalıştırın.";
                return;
            }
            // Native animations read palette/playback independently; avoid reloading XAML styles on song changes.
            string state = ExplorerTaskbarTheme.BuildAnimationState(normalized, secondary, highlight, playing, magnification, glow, intellihide, hasMediaSession);
            if (enabled && state != _lastAnimationState)
            {
                string statePath = Path.Combine(HostDirectory, "animation-state.ini");
                File.WriteAllText(statePath + ".tmp", state, Encoding.Unicode);
                File.Move(statePath + ".tmp", statePath, overwrite: true);
                _lastAnimationState = state;
            }
            if (_enabled != enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ModConfigPath)!);
                string temporary = ModConfigPath + ".tmp";
                File.WriteAllText(temporary, ExplorerTaskbarTheme.Build(enabled, normalized, ++_revision), Encoding.Unicode);
                File.Move(temporary, ModConfigPath, overwrite: true);
                string sizePath = Path.Combine(Path.GetDirectoryName(ModConfigPath)!, ExplorerTaskbarTheme.SizeModId + ".ini");
                File.WriteAllText(sizePath + ".tmp", ExplorerTaskbarTheme.BuildSizeSettings(enabled, _revision), Encoding.Unicode);
                File.Move(sizePath + ".tmp", sizePath, overwrite: true);
                string overlayPath = Path.Combine(Path.GetDirectoryName(ModConfigPath)!, ExplorerTaskbarTheme.OverlayModId + ".ini");
                File.WriteAllText(overlayPath + ".tmp", ExplorerTaskbarTheme.BuildOverlaySettings(enabled, _revision), Encoding.Unicode);
                File.Move(overlayPath + ".tmp", overlayPath, overwrite: true);
                string startPath = Path.Combine(Path.GetDirectoryName(ModConfigPath)!, ExplorerTaskbarTheme.StartModId + ".ini");
                File.WriteAllText(startPath + ".tmp", ExplorerTaskbarTheme.BuildStartMenuSettings(enabled, _revision), Encoding.Unicode);
                File.Move(startPath + ".tmp", startPath, overwrite: true);
                string notificationPath = Path.Combine(Path.GetDirectoryName(ModConfigPath)!, ExplorerTaskbarTheme.NotificationModId + ".ini");
                File.WriteAllText(notificationPath + ".tmp", ExplorerTaskbarTheme.BuildNotificationSettings(enabled, _revision), Encoding.Unicode);
                File.Move(notificationPath + ".tmp", notificationPath, overwrite: true);
                _enabled = enabled;
            }
            if (enabled && DateTime.UtcNow - _lastStartAttemptUtc > TimeSpan.FromSeconds(15))
            {
                _lastStartAttemptUtc = DateTime.UtcNow;
                string executable = Path.Combine(HostDirectory, "windhawk.exe");
                if (!IsHostRunning(executable))
                {
                    using var process = Process.Start(new ProcessStartInfo(executable, "-tray-only")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = HostDirectory
                    });
                }
            }
            if (enabled) ReleaseActivationStrip();
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LastError = ex.Message;
        }
    }

    private void ReleaseActivationStrip()
    {
        nint tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return;
        var info = new NativeMethods.MONITORINFOEXW { cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW) };
        if (!NativeMethods.GetMonitorInfoW(NativeMethods.MonitorFromWindow(tray, 2), ref info)) return;
        if (!ExplorerWorkAreaPolicy.TryReleaseActivationStrip(info.rcMonitor, info.rcWork,
                NativeMethods.GetDpiForWindow(tray), out var expanded)) return;
        _activationStripWorkArea ??= info.rcWork;
        NativeMethods.SystemParametersInfoWorkArea(NativeMethods.SPI_SETWORKAREA, 0, ref expanded, NativeMethods.SPIF_SENDCHANGE);
    }

    private static bool IsHostRunning(string executable)
    {
        foreach (var process in Process.GetProcessesByName("windhawk"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // A different user's Windhawk is outside this profile's ownership.
                }
            }
        }
        return false;
    }

    // Disabling the one profile unloads styles and restores Explorer's original XAML properties.
    public void Dispose()
    {
        if (_enabled == true)
        {
            if (_activationStripWorkArea is { } original)
            {
                nint tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
                var info = new NativeMethods.MONITORINFOEXW { cbSize = (uint)sizeof(NativeMethods.MONITORINFOEXW) };
                if (tray != 0 && NativeMethods.GetMonitorInfoW(NativeMethods.MonitorFromWindow(tray, 2), ref info)
                    && info.rcWork.Left == original.Left && info.rcWork.Top == original.Top
                    && info.rcWork.Right == original.Right && info.rcWork.Bottom == info.rcMonitor.Bottom
                    && info.rcMonitor.Bottom - original.Bottom is > 0 and <= 8)
                    NativeMethods.SystemParametersInfoWorkArea(NativeMethods.SPI_SETWORKAREA, 0, ref original, NativeMethods.SPIF_SENDCHANGE);
            }
            Apply(false);
        }
    }
}
