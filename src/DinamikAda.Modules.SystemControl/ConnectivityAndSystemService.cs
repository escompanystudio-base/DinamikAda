using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using DinamikAda.Core.Abstractions;
using DinamikAda.Core.Localization;
using DinamikAda.Platform.Win32.Audio;
using DinamikAda.Platform.Win32.Identity;
using DinamikAda.Platform.Win32.Interop;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace DinamikAda.Modules.SystemControl;

public sealed class ConnectivityAndSystemService : IConnectivityBackend
{
    private const string BtBatteryProp = BluetoothBatteryResolver.BtBatteryProp;
    private const string PersonalizeSubKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Guid EfficiencyModeGuid = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedModeGuid = Guid.Empty;
    private static readonly Guid PerformanceModeGuid = new("ded574b5-45a0-4f42-8737-46345c09c238");

    private static readonly Guid PowerSaverSchemeGuid = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid BalancedSchemeGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerfSchemeGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerfSchemeGuid = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private readonly bool _hasIdentity = PackageIdentityDetector.Detect().HasIdentity;
    private readonly AutoResetEvent _caffeineChanged = new(false);
    private readonly object _caffeineLock = new();
    private readonly ConcurrentDictionary<string, int> _gattBatteryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownConnectedBtKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _btSync = new();
    private DeviceWatcher? _btClassicWatcher;
    private DeviceWatcher? _btBleWatcher;
    private Timer? _btDebounceTimer;
    private bool _btBaselineCaptured;
    private Thread? _caffeineThread;
    private volatile bool _caffeineActive;
    private volatile bool _disposed;
    private int _powerModeIndex = 1;

    private ConnectivitySnapshot _current;

    public event EventHandler<ConnectivitySnapshot>? StateChanged;
    public event EventHandler<BluetoothDeviceSnapshot>? BluetoothDeviceConnected;
    public event EventHandler<string>? OperationFailed;
    public string Language { get; set; } = "tr";
    public string? LastError { get; private set; }
    public ConnectivitySnapshot Current => _current;

    public ConnectivityAndSystemService()
    {
        bool hasBattery = ReadHasBattery();
        (bool ethConnected, string? ethName, string? ethSpeed) = ReadEthernetStatus();
        (int powerIdx, string? planName) = ReadPowerModeAndPlan(hasBattery);

        _current = new ConnectivitySnapshot(
            IsPackagedIdentity: _hasIdentity,
            IsWifiAvailable: HasWlanInterface(),
            IsWifiOn: false,
            WifiSsid: null,
            WifiSignalPercent: null,
            IsBluetoothAvailable: false,
            IsBluetoothOn: false,
            PairedBluetoothDevices: ImmutableArray<BluetoothDeviceSnapshot>.Empty,
            WindowsFocusProfile: WnfFocusParser.QueryWindowsFocusProfile(),
            IsDarkTheme: ReadIsDarkTheme(),
            IsCaffeineActive: false,
            PowerModeIndex: powerIdx,
            IsEnergySaverOn: ReadEnergySaverOn(),
            IsEthernetConnected: ethConnected,
            EthernetName: ethName,
            EthernetSpeedText: ethSpeed,
            HasBattery: hasBattery,
            ActivePowerPlanName: planName);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        bool wifiAvail = false, wifiOn = false, btAvail = false, btOn = false;
        try
        {
            var radios = await Radio.GetRadiosAsync().AsTask(ct);
            foreach (var r in radios)
            {
                if (r.Kind == RadioKind.WiFi)
                {
                    wifiAvail = true;
                    wifiOn = r.State == RadioState.On;
                }
                else if (r.Kind == RadioKind.Bluetooth)
                {
                    btAvail = true;
                    btOn = r.State == RadioState.On;
                }
            }
        }
        catch
        {
            wifiAvail = HasWlanInterface();
        }

        string? wifiSsid = null;
        int? wifiSignal = null;
        if (wifiOn)
        {
            (wifiSsid, wifiSignal) = await ReadWifiConnectionDetailsAsync(ct);
        }

        var btDevices = btAvail
            ? await BluetoothBatteryResolver.QueryAllAsync(_gattBatteryCache, ct)
            : ImmutableArray<BluetoothDeviceSnapshot>.Empty;
        List<BluetoothDeviceSnapshot>? newlyConnected = null;
        lock (_btSync)
        {
            var currentConnectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dev in btDevices)
            {
                if (!dev.IsConnected) continue;
                string key = string.IsNullOrWhiteSpace(dev.Name) ? dev.Id : dev.Name.Trim();
                currentConnectedKeys.Add(key);
                if (_btBaselineCaptured && !_knownConnectedBtKeys.Contains(key))
                {
                    newlyConnected ??= new List<BluetoothDeviceSnapshot>();
                    newlyConnected.Add(dev);
                }
            }
            _knownConnectedBtKeys.Clear();
            foreach (string k in currentConnectedKeys) _knownConnectedBtKeys.Add(k);
            _btBaselineCaptured = true;
        }
        EnsureBluetoothWatchersStarted(btAvail);
        var focus = WnfFocusParser.QueryWindowsFocusProfile();
        bool dark = ReadIsDarkTheme();
        bool hasBattery = ReadHasBattery();
        bool energySaver = ReadEnergySaverOn();
        (int powerIdx, string? planName) = ReadPowerModeAndPlan(hasBattery);
        (bool ethConnected, string? ethName, string? ethSpeed) = ReadEthernetStatus();

        _current = new ConnectivitySnapshot(
            IsPackagedIdentity: _hasIdentity,
            IsWifiAvailable: wifiAvail,
            IsWifiOn: wifiOn,
            WifiSsid: wifiSsid,
            WifiSignalPercent: wifiSignal,
            IsBluetoothAvailable: btAvail,
            IsBluetoothOn: btOn,
            PairedBluetoothDevices: btDevices,
            WindowsFocusProfile: focus,
            IsDarkTheme: dark,
            IsCaffeineActive: _caffeineActive,
            PowerModeIndex: powerIdx,
            IsEnergySaverOn: energySaver,
            IsEthernetConnected: ethConnected,
            EthernetName: ethName,
            EthernetSpeedText: ethSpeed,
            HasBattery: hasBattery,
            ActivePowerPlanName: planName);

        StateChanged?.Invoke(this, _current);
        if (newlyConnected is not null)
        {
            foreach (var dev in newlyConnected)
            {
                BluetoothDeviceConnected?.Invoke(this, dev);
            }
        }
    }

    private void EnsureBluetoothWatchersStarted(bool btAvail)
    {
        if (!btAvail || _disposed || _btClassicWatcher is not null) return;
        lock (_btSync)
        {
            if (_disposed || _btClassicWatcher is not null) return;
            try
            {
                string[] props = ["System.Devices.Aep.IsConnected", BtBatteryProp];
                _btDebounceTimer = new Timer(_ =>
                {
                    if (!_disposed) _ = RefreshAsync();
                }, null, Timeout.Infinite, Timeout.Infinite);

                _btClassicWatcher = DeviceInformation.CreateWatcher(
                    BluetoothBatteryResolver.ClassicProtocolSelector,
                    props,
                    DeviceInformationKind.AssociationEndpoint);
                AttachWatcher(_btClassicWatcher);
                _btClassicWatcher.Start();

                _btBleWatcher = DeviceInformation.CreateWatcher(
                    BluetoothBatteryResolver.BleProtocolSelector,
                    props,
                    DeviceInformationKind.AssociationEndpoint);
                AttachWatcher(_btBleWatcher);
                _btBleWatcher.Start();
            }
            catch
            {
                // Watcher unsupported in current sandbox/VM; fallback remains explicit/manual refresh.
            }
        }
    }

    private void AttachWatcher(DeviceWatcher watcher)
    {
        void ScheduleRefresh()
        {
            if (_disposed) return;
            try { _btDebounceTimer?.Change(350, Timeout.Infinite); } catch { }
        }

        watcher.Updated += (_, _) => ScheduleRefresh();
        watcher.Added += (_, _) => ScheduleRefresh();
        watcher.Removed += (_, _) => ScheduleRefresh();
    }

    public async Task<bool> ToggleWifiAsync(CancellationToken ct = default)
    {
        if (!_current.IsWifiAvailable)
        {
            OpenWindowsSettings("ms-settings:network-ethernet");
            return true;
        }

        var (ok, reason) = await TryToggleRadioAsync(RadioKind.WiFi, Language, ct);
        if (ok)
        {
            await RefreshAsync(ct);
            return true;
        }

        ReportOperationError(reason ?? Strings.Pick(Language, "Wi-Fi radyo anahtarı engellendi veya kullanılamıyor.", "Wi-Fi radio toggle denied or unavailable."));
        OpenWindowsSettings("ms-settings:network-wifi");
        return false;
    }

    public async Task<bool> ToggleBluetoothAsync(CancellationToken ct = default)
    {
        var (ok, reason) = await TryToggleRadioAsync(RadioKind.Bluetooth, Language, ct);
        if (ok)
        {
            await RefreshAsync(ct);
            return true;
        }

        ReportOperationError(reason ?? Strings.Pick(Language, "Bluetooth radyo anahtarı engellendi veya kullanılamıyor.", "Bluetooth radio toggle denied or unavailable."));
        OpenWindowsSettings("ms-settings:bluetooth");
        return false;
    }

    public async Task<bool> ToggleBluetoothAudioDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        var dev = _current.PairedBluetoothDevices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (dev is null)
        {
            ReportOperationError(Strings.Pick(Language, "Bluetooth ses aygıtı bulunamadı.", "Bluetooth audio device was not found."));
            return false;
        }

        bool connect = !dev.IsConnected;
        bool ok = await Task.Run(() => TrySendBtAudioKsProperty(dev.Name, connect), ct);
        if (ok)
        {
            await Task.Delay(350, ct);
            await RefreshAsync(ct);
            return true;
        }

        ReportOperationError(Strings.Pick(
            Language,
            $"Bluetooth aygıtı {(connect ? "yeniden bağlanamadı" : "bağlantısı kesilemedi")}: {dev.Name}.",
            $"Bluetooth endpoint {(connect ? "reconnect" : "disconnect")} failed for {dev.Name}."));
        OpenWindowsSettings("ms-settings:bluetooth");
        return false;
    }

    public void ToggleDarkTheme() => _ = ToggleDarkThemeAsync();

    public Task<bool> NotifyThemeChangedAsync(CancellationToken ct = default) => ToggleDarkThemeAsync(ct);

    public async Task<bool> ToggleDarkThemeAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        string lang = Language;

        var result = await Task.Run<(bool RegistryOk, bool BroadcastOk, bool NextDark, string? Error)>(() =>
        {
            bool nextDark = !ReadIsDarkTheme();
            int val = nextDark ? 0 : 1; // 0 = Dark, 1 = Light
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(PersonalizeSubKey);
                if (key is null)
                {
                    return (false, false, nextDark, Strings.Pick(lang, "Personalize kayıt defteri anahtarı açılamadı.", "Unable to open Personalize registry key."));
                }
                key.SetValue("AppsUseLightTheme", val, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", val, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                return (false, false, nextDark, Strings.Pick(lang, $"Tema kayıt defteri güncellenemedi: {ex.Message}", $"Theme registry update failed: {ex.Message}"));
            }

            nint notifyRes = NativeMethods.SendMessageTimeoutW(
                new nint(0xFFFF),
                NativeMethods.WM_SETTINGCHANGE,
                0,
                "ImmersiveColorSet",
                0x0002,
                1500,
                out _);
            if (notifyRes == 0)
            {
                return (true, false, nextDark, Strings.Pick(lang, "Tema güncellendi ancak bazı pencereler zaman aşımına uğradı.", "Theme updated, though some windows timed out responding."));
            }

            return (true, true, nextDark, null);
        }, ct);

        if (!result.RegistryOk)
        {
            ReportOperationError(result.Error ?? Strings.Pick(lang, "Koyu tema değiştirilemedi.", "Dark theme toggle failed."));
            return false;
        }

        _current = _current with { IsDarkTheme = result.NextDark };
        StateChanged?.Invoke(this, _current);

        if (!result.BroadcastOk)
        {
            ReportOperationError(result.Error ?? Strings.Pick(lang, "Tema yayını zaman aşımına uğradı.", "Theme change broadcast timed out."));
        }
        return true;
    }

    public void SetPowerMode(int modeIndex)
    {
        int clamped = Math.Clamp(modeIndex, 0, 2);
        bool hasBattery = ReadHasBattery();
        Guid? activeScheme = TryGetActivePowerScheme();

        // Laptops on the Balanced scheme support Windows overlay power modes; Desktop PCs and custom plans use active Power Schemes.
        if (hasBattery && activeScheme == BalancedSchemeGuid)
        {
            Guid overlayGuid = clamped switch
            {
                0 => EfficiencyModeGuid,
                2 => PerformanceModeGuid,
                _ => BalancedModeGuid
            };

            try
            {
                uint acRes = NativeMethods.PowerSetUserConfiguredACPowerMode(ref overlayGuid);
                uint dcRes = NativeMethods.PowerSetUserConfiguredDCPowerMode(ref overlayGuid);
                if (acRes == 0 || dcRes == 0)
                {
                    _powerModeIndex = clamped;
                    (_, string? planName) = ReadPowerModeAndPlan(hasBattery);
                    _current = _current with { PowerModeIndex = _powerModeIndex, HasBattery = hasBattery, ActivePowerPlanName = planName };
                    StateChanged?.Invoke(this, _current);
                    return;
                }
            }
            catch { }
        }

        try
        {
            var schemes = EnumeratePowerSchemes();
            Guid? target = PickSchemeForMode(schemes, clamped, activeScheme);
            if (target is null)
            {
                ReportOperationError(Strings.Pick(Language, "Uygun Windows güç planı bulunamadı.", "No matching Windows power scheme was found."));
                return;
            }

            Guid chosen = target.Value;
            uint res = NativeMethods.PowerSetActiveScheme(0, ref chosen);
            if (res != 0)
            {
                ReportOperationError(Strings.Pick(Language, $"Güç planı değiştirilemedi (Win32={res}).", $"Power scheme switch failed (Win32={res})."));
                return;
            }

            (int actualIdx, string? actualName) = ReadPowerModeAndPlan(hasBattery);
            _powerModeIndex = actualIdx;
            _current = _current with
            {
                PowerModeIndex = _powerModeIndex,
                HasBattery = hasBattery,
                ActivePowerPlanName = actualName
            };
            StateChanged?.Invoke(this, _current);
        }
        catch (Exception ex)
        {
            ReportOperationError(Strings.Pick(Language, $"Güç planı değiştirilemedi: {ex.Message}", $"Power scheme change failed: {ex.Message}"));
        }
    }

    public void ToggleCaffeine()
    {
        bool active;
        lock (_caffeineLock)
        {
            if (_disposed) return;
            _caffeineActive = !_caffeineActive;
            active = _caffeineActive;
            if (active && _caffeineThread is null)
            {
                _caffeineThread = new Thread(RunCaffeineThread)
                {
                    Name = "DinamikAda.Caffeine",
                    IsBackground = true
                };
                _caffeineThread.Start();
            }
            _caffeineChanged.Set();
        }

        _current = _current with { IsCaffeineActive = active };
        StateChanged?.Invoke(this, _current);
    }

    private void ReportOperationError(string message)
    {
        LastError = message;
        OperationFailed?.Invoke(this, message);
    }

    public void LockWorkstation() => NativeMethods.LockWorkStation();

    public void SleepWorkstation() => NativeMethods.SetSuspendState(false, false, false);

    public string? GetActivePowerSchemeGuid() => TryGetActivePowerScheme()?.ToString("D");

    public bool TrySwitchToHighPerformance(out string? previousSchemeGuid)
    {
        previousSchemeGuid = null;
        if (_disposed) return false;

        Guid? active = TryGetActivePowerScheme();
        previousSchemeGuid = active?.ToString("D");

        try
        {
            bool hasBattery = ReadHasBattery();
            var schemes = EnumeratePowerSchemes();
            Guid? target = PickSchemeForMode(schemes, 2, active);
            if (target.HasValue)
            {
                Guid chosen = target.Value;
                uint res = NativeMethods.PowerSetActiveScheme(0, ref chosen);
                if (res == 0)
                {
                    (int actualIdx, string? actualName) = ReadPowerModeAndPlan(hasBattery);
                    _powerModeIndex = actualIdx;
                    _current = _current with
                    {
                        PowerModeIndex = _powerModeIndex,
                        HasBattery = hasBattery,
                        ActivePowerPlanName = actualName
                    };
                    StateChanged?.Invoke(this, _current);
                    return true;
                }
            }

            if (hasBattery && active == BalancedSchemeGuid)
            {
                Guid perfOverlay = PerformanceModeGuid;
                uint acRes = NativeMethods.PowerSetUserConfiguredACPowerMode(ref perfOverlay);
                uint dcRes = NativeMethods.PowerSetUserConfiguredDCPowerMode(ref perfOverlay);
                if (acRes == 0 || dcRes == 0)
                {
                    (int actualIdx, string? actualName) = ReadPowerModeAndPlan(hasBattery);
                    _powerModeIndex = actualIdx;
                    _current = _current with
                    {
                        PowerModeIndex = _powerModeIndex,
                        HasBattery = hasBattery,
                        ActivePowerPlanName = actualName
                    };
                    StateChanged?.Invoke(this, _current);
                    return true;
                }
            }

            ReportOperationError(Strings.Pick(Language, "Yüksek performans güç planına geçilemedi.", "Failed to switch to high performance power scheme."));
            return false;
        }
        catch (Exception ex)
        {
            ReportOperationError(Strings.Pick(Language, $"Güç planı değiştirilemedi: {ex.Message}", $"Power scheme switch failed: {ex.Message}"));
            return false;
        }
    }

    public bool TryRestorePowerScheme(string schemeGuid)
    {
        if (_disposed || string.IsNullOrWhiteSpace(schemeGuid)) return false;
        if (!Guid.TryParse(schemeGuid.Trim(), out Guid target)) return false;

        try
        {
            bool hasBattery = ReadHasBattery();
            uint res = NativeMethods.PowerSetActiveScheme(0, ref target);
            if (res != 0)
            {
                ReportOperationError(Strings.Pick(Language, $"Güç planı geri yüklenemedi (Win32={res}).", $"Power scheme restore failed (Win32={res})."));
                return false;
            }

            if (hasBattery && target == BalancedSchemeGuid)
            {
                try
                {
                    Guid balancedOverlay = BalancedModeGuid;
                    _ = NativeMethods.PowerSetUserConfiguredACPowerMode(ref balancedOverlay);
                    _ = NativeMethods.PowerSetUserConfiguredDCPowerMode(ref balancedOverlay);
                }
                catch { }
            }

            (int actualIdx, string? actualName) = ReadPowerModeAndPlan(hasBattery);
            _powerModeIndex = actualIdx;
            _current = _current with
            {
                PowerModeIndex = _powerModeIndex,
                HasBattery = hasBattery,
                ActivePowerPlanName = actualName
            };
            StateChanged?.Invoke(this, _current);
            return true;
        }
        catch (Exception ex)
        {
            ReportOperationError(Strings.Pick(Language, $"Güç planı geri yüklenemedi: {ex.Message}", $"Power scheme restore failed: {ex.Message}"));
            return false;
        }
    }

    public bool TryShutdownWorkstation(out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "Service is disposed.";
            return false;
        }

        const uint TOKEN_QUERY = 0x0008;
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        const uint EWX_SHUTDOWN = 0x00000001;
        const uint EWX_POWEROFF = 0x00000008;

        if (!NativeMethods.OpenProcessToken(new nint(-1), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out nint tokenHandle) || tokenHandle == 0)
        {
            int winErr = Marshal.GetLastWin32Error();
            error = Strings.Pick(Language, $"Kapatma belirteci açılamadı (Win32={winErr}).", $"Failed to open process token for shutdown (Win32={winErr}).");
            ReportOperationError(error);
            return false;
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValueW(null, "SeShutdownPrivilege", out long luid))
            {
                int winErr = Marshal.GetLastWin32Error();
                error = Strings.Pick(Language, $"Kapatma ayrıcalığı bulunamadı (Win32={winErr}).", $"Failed to look up SeShutdownPrivilege (Win32={winErr}).");
                ReportOperationError(error);
                return false;
            }

            var privileges = new NativeMethods.TOKEN_PRIVILEGES_SINGLE
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            if (!NativeMethods.AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, 0, 0))
            {
                int winErr = Marshal.GetLastWin32Error();
                error = Strings.Pick(Language, $"Kapatma ayrıcalığı etkinleştirilemedi (Win32={winErr}).", $"Failed to enable SeShutdownPrivilege (Win32={winErr}).");
                ReportOperationError(error);
                return false;
            }

            int adjustErr = Marshal.GetLastWin32Error();
            if (adjustErr != 0)
            {
                error = Strings.Pick(Language, $"Kapatma ayrıcalığı atanamadı (Win32={adjustErr}).", $"SeShutdownPrivilege was not assigned (Win32={adjustErr}).");
                ReportOperationError(error);
                return false;
            }
        }
        finally
        {
            NativeMethods.CloseHandle(tokenHandle);
        }

        if (NativeMethods.ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF, 0))
        {
            return true;
        }

        int exitErr = Marshal.GetLastWin32Error();
        if (NativeMethods.InitiateSystemShutdownExW(null, null, 0, false, false, 0))
        {
            return true;
        }

        int initErr = Marshal.GetLastWin32Error();
        error = Strings.Pick(Language,
            $"Sistem kapatılamadı (ExitWindowsEx={exitErr}, InitiateShutdown={initErr}).",
            $"System shutdown failed (ExitWindowsEx={exitErr}, InitiateShutdown={initErr}).");
        ReportOperationError(error);
        return false;
    }

    public void TurnOffDisplay()
    {
        if (_disposed) return;
        _ = Task.Run(async () =>
        {
            // Short delay avoids immediate wake from mouse button release over the island.
            await Task.Delay(380).ConfigureAwait(false);
            if (!_disposed)
            {
                NativeMethods.PostMessageW(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND, NativeMethods.SC_MONITORPOWER, 2);
            }
        });
    }

    public void LaunchScreenSnip() => OpenWindowsSettings("ms-screenclip:");

    public void OpenWindowsSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch { }
    }

    private void RunCaffeineThread()
    {
        while (!_disposed)
        {
            uint flags = _caffeineActive
                ? NativeMethods.ES_CONTINUOUS | NativeMethods.ES_DISPLAY_REQUIRED | NativeMethods.ES_SYSTEM_REQUIRED
                : NativeMethods.ES_CONTINUOUS;
            NativeMethods.SetThreadExecutionState(flags);
            try { _caffeineChanged.WaitOne(5000); }
            catch (ObjectDisposedException) { break; }
        }
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
    }

    private static async Task<(bool Ok, string? Reason)> TryToggleRadioAsync(RadioKind kind, string lang, CancellationToken ct)
    {
        string kindName = kind switch
        {
            RadioKind.WiFi => "Wi-Fi",
            RadioKind.Bluetooth => "Bluetooth",
            _ => kind.ToString()
        };

        try
        {
            var access = await Radio.RequestAccessAsync().AsTask(ct);
            if (access != RadioAccessStatus.Allowed)
            {
                return (false, Strings.Pick(lang, $"{kindName} radyo izni durumu: {access}.", $"{kindName} radio permission status: {access}."));
            }
            var radios = await Radio.GetRadiosAsync().AsTask(ct);
            var target = radios.FirstOrDefault(r => r.Kind == kind);
            if (target is null)
            {
                return (false, Strings.Pick(lang, $"{kindName} radyo bağdaştırıcısı bulunamadı.", $"{kindName} radio adapter not found."));
            }
            var next = target.State == RadioState.On ? RadioState.Off : RadioState.On;
            var status = await target.SetStateAsync(next).AsTask(ct);
            return status == RadioAccessStatus.Allowed
                ? (true, null)
                : (false, Strings.Pick(lang, $"{kindName} radyo değişikliği sonucu: {status}.", $"{kindName} radio change returned: {status}."));
        }
        catch (Exception ex)
        {
            return (false, $"{kind} radio toggle failed: {ex.Message}");
        }
    }

    private static async Task<(string? Ssid, int? SignalPercent)> ReadWifiConnectionDetailsAsync(CancellationToken ct)
    {
        string? ssid = null;
        int? signal = null;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is not null && profile.IsWlanConnectionProfile)
            {
                string? connectedSsid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                if (!string.IsNullOrWhiteSpace(connectedSsid))
                {
                    ssid = connectedSsid.Trim();
                }

                byte? bars = profile.GetSignalBars();
                if (bars.HasValue)
                {
                    signal = Math.Clamp(bars.Value * 20, 0, 100);
                }
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(ssid) && signal.HasValue)
        {
            return (ssid, signal);
        }

        // Fallback to netsh wlan show interfaces off-thread with short timeout
        try
        {
            var (fallbackSsid, fallbackSig) = await Task.Run(() => ReadWifiFromNetsh(), ct);
            if (string.IsNullOrWhiteSpace(ssid)) ssid = fallbackSsid;
            signal ??= fallbackSig;
        }
        catch { }

        return (ssid, signal);
    }

    private static (string? Ssid, int? SignalPercent) ReadWifiFromNetsh()
    {
        string? ssid = null;
        int? signal = null;
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        if (!proc.Start()) return (null, null);

        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(1500);

        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = rawLine.IndexOf(':');
            if (colon <= 0) continue;
            string key = rawLine[..colon].Trim();
            string val = rawLine[(colon + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(val)) continue;

            if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase))
            {
                ssid = val;
            }
            else if (val.EndsWith('%') && int.TryParse(val.TrimEnd('%', ' '), out int pct))
            {
                signal = Math.Clamp(pct, 0, 100);
            }
        }

        return (ssid, signal);
    }

    private static unsafe bool TrySendBtAudioKsProperty(string deviceName, bool connect)
    {
        // Sends KSPROPERTY_ONESHOT_RECONNECT (0) / DISCONNECT (1) via CoreAudio topology on matching endpoint
        try
        {
            Type? enumType = Type.GetTypeFromCLSID(CoreAudioGuids.CLSID_MMDeviceEnumerator);
            if (enumType is null) return false;
            var enumerator = Activator.CreateInstance(enumType) as IMMDeviceEnumerator;
            if (enumerator is null) return false;

            try
            {
                const uint DEVICE_STATE_MASK_ALL = 0x0000000F;
                if (enumerator.EnumAudioEndpoints(0 /* eRender */, DEVICE_STATE_MASK_ALL, out var coll) != 0 || coll is null)
                    return false;

                try
                {
                    coll.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (coll.Item(i, out var dev) != 0 || dev is null) continue;
                        try
                        {
                            bool nameMatched = false;
                            if (dev.OpenPropertyStore(0, out var store) == 0 && store is not null)
                            {
                                try
                                {
                                    var k = CoreAudioGuids.PKEY_Device_FriendlyName;
                                    if (store.GetValue(ref k, out var pv) == 0)
                                    {
                                        try
                                        {
                                            string? name = pv.vt == 31 && pv.p != 0 ? Marshal.PtrToStringUni(pv.p) : null;
                                            nameMatched = !string.IsNullOrWhiteSpace(name) &&
                                                          name.Contains(deviceName, StringComparison.OrdinalIgnoreCase);
                                        }
                                        finally
                                        {
                                            NativeMethods.PropVariantClear((nint)(&pv));
                                        }
                                    }
                                }
                                finally
                                {
                                    if (Marshal.IsComObject(store)) Marshal.ReleaseComObject(store);
                                }
                            }
                            if (!nameMatched) continue;

                            Guid topoIid = CoreAudioGuids.IID_IDeviceTopology;
                            if (dev.Activate(ref topoIid, 0x17, 0, out object topoObj) == 0 && topoObj is IDeviceTopology topo)
                            {
                                try
                                {
                                    if (topo.GetConnector(0, out var conn) == 0 && conn is not null)
                                    {
                                        try
                                        {
                                            if (conn.GetDeviceIdConnectedTo(out string connectedDevId) == 0 && !string.IsNullOrWhiteSpace(connectedDevId))
                                            {
                                                if (enumerator.GetDevice(connectedDevId, out var sysDev) == 0 && sysDev is not null)
                                                {
                                                    try
                                                    {
                                                        Guid ksIid = CoreAudioGuids.IID_IKsControl;
                                                        if (sysDev.Activate(ref ksIid, 0x17, 0, out object ksObj) == 0 && ksObj is IKsControl ks)
                                                        {
                                                            try
                                                            {
                                                                var prop = new KsProperty
                                                                {
                                                                    Set = CoreAudioGuids.KSPROPSETID_BtAudio,
                                                                    Id = connect ? 0u : 1u,
                                                                    Flags = 0x00000001 /* KSPROPERTY_TYPE_GET */
                                                                };
                                                                int hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KsProperty>(), 0, 0, out _);
                                                                if (hr == 0) return true;
                                                            }
                                                            finally
                                                            {
                                                                if (Marshal.IsComObject(ks)) Marshal.ReleaseComObject(ks);
                                                            }
                                                        }
                                                    }
                                                    finally
                                                    {
                                                        if (Marshal.IsComObject(sysDev)) Marshal.ReleaseComObject(sysDev);
                                                    }
                                                }
                                            }
                                        }
                                        finally
                                        {
                                            if (Marshal.IsComObject(conn)) Marshal.ReleaseComObject(conn);
                                        }
                                    }
                                }
                                finally
                                {
                                    if (Marshal.IsComObject(topo)) Marshal.ReleaseComObject(topo);
                                }
                            }
                        }
                        finally
                        {
                            if (Marshal.IsComObject(dev)) Marshal.ReleaseComObject(dev);
                        }
                    }
                }
                finally
                {
                    if (Marshal.IsComObject(coll)) Marshal.ReleaseComObject(coll);
                }
            }
            finally
            {
                if (Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
            }
        }
        catch { }

        return false;
    }

    private static unsafe bool HasWlanInterface()
    {
        try
        {
            if (NativeMethods.WlanOpenHandle(2, 0, out _, out nint hClient) != 0) return false;
            try
            {
                if (NativeMethods.WlanEnumInterfaces(hClient, 0, out nint pList) != 0 || pList == 0) return false;
                uint numItems = *(uint*)pList;
                NativeMethods.WlanFreeMemory(pList);
                return numItems > 0;
            }
            finally
            {
                NativeMethods.WlanCloseHandle(hClient, 0);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadIsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeSubKey);
            object? val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool ReadHasBattery()
    {
        return NativeMethods.GetSystemPowerStatus(out var st) && (st.BatteryFlag & 128) == 0 && st.BatteryFlag != 255;
    }

    private static bool ReadEnergySaverOn()
    {
        return NativeMethods.GetSystemPowerStatus(out var st) && (st.SystemStatusFlag & 1) != 0;
    }

    private (int ModeIndex, string? PlanName) ReadPowerModeAndPlan(bool hasBattery)
    {
        Guid? active = TryGetActivePowerScheme();
        string? friendly = active.HasValue ? TryReadSchemeFriendlyName(active.Value) : null;

        if (hasBattery && active == BalancedSchemeGuid)
        {
            try
            {
                if (NativeMethods.PowerGetActualOverlayScheme(out Guid overlay) == 0)
                {
                    if (overlay == EfficiencyModeGuid) return (0, friendly);
                    if (overlay == PerformanceModeGuid) return (2, friendly);
                    return (1, friendly);
                }
            }
            catch { }
        }

        if (active.HasValue)
        {
            return (ClassifyPowerScheme(active.Value, friendly), friendly);
        }

        return (_powerModeIndex, friendly);
    }

    public static int ClassifyPowerScheme(Guid guid, string? friendlyName)
    {
        if (guid == PowerSaverSchemeGuid) return 0;
        if (guid == HighPerfSchemeGuid || guid == UltimatePerfSchemeGuid) return 2;
        if (guid == BalancedSchemeGuid) return 1;

        if (!string.IsNullOrWhiteSpace(friendlyName))
        {
            string lower = friendlyName.ToLowerInvariant();
            if (lower.Contains("tasarruf") || lower.Contains("saver") || lower.Contains("eco") || lower.Contains("eko"))
                return 0;
            if (lower.Contains("yüksek") || lower.Contains("yuksek") || lower.Contains("high") ||
                lower.Contains("nihai") || lower.Contains("ultimate") || lower.Contains("perf"))
                return 2;
        }

        return 1;
    }

    public static Guid? PickSchemeForMode(
        IReadOnlyList<(Guid Guid, string FriendlyName)> schemes,
        int modeIndex,
        Guid? currentScheme = null)
    {
        int target = Math.Clamp(modeIndex, 0, 2);
        if (target == 0)
        {
            foreach (var s in schemes) if (s.Guid == PowerSaverSchemeGuid) return s.Guid;
            foreach (var s in schemes) if (ClassifyPowerScheme(s.Guid, s.FriendlyName) == 0) return s.Guid;
            return PowerSaverSchemeGuid;
        }
        if (target == 2)
        {
            // Keep Ultimate if already active; otherwise allow explicit High/Ultimate preference.
            if (currentScheme.HasValue && schemes.Any(s => s.Guid == currentScheme.Value && ClassifyPowerScheme(s.Guid, s.FriendlyName) == 2))
            {
                // If multiple perf plans exist (High + Ultimate) and user clicks Perf while already on one, keep High as primary default.
                return currentScheme.Value;
            }
            foreach (var s in schemes) if (s.Guid == HighPerfSchemeGuid) return s.Guid;
            foreach (var s in schemes) if (s.Guid == UltimatePerfSchemeGuid) return s.Guid;
            foreach (var s in schemes) if (ClassifyPowerScheme(s.Guid, s.FriendlyName) == 2) return s.Guid;
            return HighPerfSchemeGuid;
        }

        foreach (var s in schemes) if (s.Guid == BalancedSchemeGuid) return s.Guid;
        foreach (var s in schemes) if (ClassifyPowerScheme(s.Guid, s.FriendlyName) == 1) return s.Guid;
        return BalancedSchemeGuid;
    }

    private static Guid? TryGetActivePowerScheme()
    {
        nint pGuid = 0;
        try
        {
            if (NativeMethods.PowerGetActiveScheme(0, out pGuid) == 0 && pGuid != 0)
            {
                return Marshal.PtrToStructure<Guid>(pGuid);
            }
        }
        catch { }
        finally
        {
            if (pGuid != 0) NativeMethods.LocalFree(pGuid);
        }
        return null;
    }

    private static unsafe List<(Guid Guid, string FriendlyName)> EnumeratePowerSchemes()
    {
        var list = new List<(Guid, string)>(4);
        Span<byte> guidBuf = stackalloc byte[16];
        for (uint idx = 0; idx < 32; idx++)
        {
            uint size = 16;
            uint res;
            fixed (byte* pBuf = guidBuf)
            {
                res = NativeMethods.PowerEnumerate(0, 0, 0, NativeMethods.ACCESS_SCHEME, idx, pBuf, ref size);
            }
            if (res == 259) break; // ERROR_NO_MORE_ITEMS
            if (res != 0) continue;

            var guid = new Guid(guidBuf);
            string name = TryReadSchemeFriendlyName(guid) ?? guid.ToString();
            list.Add((guid, name));
        }
        return list;
    }

    private static unsafe string? TryReadSchemeFriendlyName(Guid guid)
    {
        uint size = 0;
        uint res = NativeMethods.PowerReadFriendlyName(0, ref guid, 0, 0, null, ref size);
        if (size == 0 || (res != 0 && res != 234)) return null;

        byte[] buf = new byte[size];
        fixed (byte* pBuf = buf)
        {
            res = NativeMethods.PowerReadFriendlyName(0, ref guid, 0, 0, pBuf, ref size);
        }
        if (res != 0) return null;

        string decoded = Encoding.Unicode.GetString(buf, 0, (int)Math.Min(buf.Length, size)).TrimEnd('\0', ' ');
        return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
    }

    public static string? FormatLinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return null;
        if (bitsPerSecond >= 1_000_000_000)
        {
            double gbps = bitsPerSecond / 1_000_000_000.0;
            return string.Create(CultureInfo.InvariantCulture, $"{gbps:0.#} Gbps");
        }
        if (bitsPerSecond >= 1_000_000)
        {
            double mbps = bitsPerSecond / 1_000_000.0;
            return string.Create(CultureInfo.InvariantCulture, $"{mbps:0} Mbps");
        }
        return null;
    }

    private static (bool IsConnected, string? AdapterName, string? SpeedText) ReadEthernetStatus()
    {
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is
                    NetworkInterfaceType.Ethernet or
                    NetworkInterfaceType.GigabitEthernet or
                    NetworkInterfaceType.FastEthernetFx or
                    NetworkInterfaceType.FastEthernetT or
                    NetworkInterfaceType.Ethernet3Megabit)
                .ToList();

            if (nics.Count == 0) return (false, null, null);

            static bool IsVirtual(NetworkInterface nic)
            {
                string combined = $"{nic.Name} {nic.Description}";
                return combined.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                    || combined.Contains("WSL", StringComparison.OrdinalIgnoreCase);
            }

            var physical = nics.Where(n => !IsVirtual(n)).ToList();
            var pool = physical.Count > 0 ? physical : nics;
            var best = pool.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up) ?? pool[0];

            string cleanName = best.Description
                .Replace(" Family Controller", "", StringComparison.OrdinalIgnoreCase)
                .Replace(" Controller", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (string.IsNullOrWhiteSpace(cleanName)) cleanName = best.Name;

            bool up = best.OperationalStatus == OperationalStatus.Up;
            return (up, cleanName, up ? FormatLinkSpeed(best.Speed) : null);
        }
        catch
        {
            return (false, null, null);
        }
    }

    public void Dispose()
    {
        lock (_btSync)
        {
            try { _btClassicWatcher?.Stop(); } catch { }
            try { _btBleWatcher?.Stop(); } catch { }
            _btClassicWatcher = null;
            _btBleWatcher = null;
            _btDebounceTimer?.Dispose();
            _btDebounceTimer = null;
        }

        Thread? threadToJoin;
        lock (_caffeineLock)
        {
            if (_disposed) return;
            _disposed = true;
            _caffeineActive = false;
            _caffeineChanged.Set();
            threadToJoin = _caffeineThread;
        }
        threadToJoin?.Join(500);
        _caffeineChanged.Dispose();
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
    }
}
