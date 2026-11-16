using System.Collections.Concurrent;
using System.Collections.Immutable;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using DinamikAda.Core.Abstractions;

namespace DinamikAda.Modules.SystemControl;

public static class BluetoothBatteryResolver
{
    public const string ClassicProtocolSelector =
        "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-26ab443f481d}\"";

    public const string BleProtocolSelector =
        "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";

    public const string BtBatteryProp = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string PnpBatterySelector =
        "System.Devices.Present:=System.StructuredQueryType.Boolean#True";
    private const string DeviceContainerProp = "System.Devices.ContainerId";

    public static int? TryExtractBatteryPercent(object? rawValue) => rawValue switch
    {
        byte b => Math.Clamp((int)b, 0, 100),
        sbyte sb when sb >= 0 => Math.Clamp((int)sb, 0, 100),
        ushort us => Math.Clamp((int)us, 0, 100),
        short s when s >= 0 => Math.Clamp((int)s, 0, 100),
        uint ui when ui <= 100 => (int)ui,
        int i when i is >= 0 and <= 100 => i,
        long l when l is >= 0 and <= 100 => (int)l,
        _ => null
    };

    public static int? ParseGattBatteryLevel(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return null;
        return Math.Clamp((int)payload[0], 0, 100);
    }

    public static string ClassifyDeviceIconGlyph(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "\uE702";
        string lower = deviceName.ToLowerInvariant();
        if (lower.Contains("airpod") || lower.Contains("headphone") || lower.Contains("earbud") ||
            lower.Contains("buds") || lower.Contains("kulak") || lower.Contains("sony wh") ||
            lower.Contains("sony wf") || lower.Contains("bose") || lower.Contains("jbl") ||
            lower.Contains("beats") || lower.Contains("headset") || lower.Contains("audio") ||
            lower.Contains("sennheiser") || lower.Contains("anker") || lower.Contains("soundcore"))
        {
            return "\uE7F6";
        }
        if (lower.Contains("mouse") || lower.Contains("fare") || lower.Contains("mx master") ||
            lower.Contains("superlight") || lower.Contains("deathadder") || lower.Contains("viper"))
        {
            return "\uE962";
        }
        if (lower.Contains("keyboard") || lower.Contains("klavye") || lower.Contains("keychron") ||
            lower.Contains("mx keys"))
        {
            return "\uE765";
        }
        if (lower.Contains("xbox") || lower.Contains("dualsense") || lower.Contains("dualshock") ||
            lower.Contains("controller") || lower.Contains("gamepad"))
        {
            return "\uE7FC";
        }
        return "\uE702";
    }

    public static IReadOnlyList<BluetoothDeviceSnapshot> MergeDeviceSnapshots(
        IEnumerable<BluetoothDeviceSnapshot> candidates)
    {
        var byKey = new Dictionary<string, BluetoothDeviceSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            string key = item.Name.Trim();
            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = item;
                continue;
            }

            bool connected = existing.IsConnected || item.IsConnected;
            int? battery = (existing.BatteryPercent, item.BatteryPercent) switch
            {
                (int a, int b) => Math.Max(a, b),
                (int a, null) => a,
                (null, int b) => b,
                _ => null
            };
            string preferredId = item.IsConnected && !existing.IsConnected ? item.Id : existing.Id;
            byKey[key] = new BluetoothDeviceSnapshot(
                preferredId,
                existing.Name,
                connected,
                battery,
                existing.CoreAudioEndpointId ?? item.CoreAudioEndpointId);
        }

        return byKey.Values
            .OrderByDescending(x => x.IsConnected)
            .ThenByDescending(x => x.BatteryPercent.HasValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static async Task<ImmutableArray<BluetoothDeviceSnapshot>> QueryAllAsync(
        ConcurrentDictionary<string, int> gattBatteryCache,
        CancellationToken ct)
    {
        var collected = new List<BluetoothDeviceSnapshot>();
        var containerBattery = new Dictionary<Guid, int>();

        try
        {
            // Scan present PnP devnodes for Hands-Free / HID child battery DEVPKEY
            string[] pnpProps = [BtBatteryProp, DeviceContainerProp];
            var devNodes = await DeviceInformation.FindAllAsync(PnpBatterySelector, pnpProps, DeviceInformationKind.Device).AsTask(ct);
            foreach (var node in devNodes)
            {
                if (!node.Properties.TryGetValue(BtBatteryProp, out object? rawBat)) continue;
                int? pct = TryExtractBatteryPercent(rawBat);
                if (!pct.HasValue) continue;
                if (node.Properties.TryGetValue(DeviceContainerProp, out object? cidObj) && cidObj is Guid cid)
                {
                    containerBattery[cid] = pct.Value;
                }
            }
        }
        catch { }

        await QuerySelectorAsync(ClassicProtocolSelector, false, containerBattery, gattBatteryCache, collected, ct);
        await QuerySelectorAsync(BleProtocolSelector, true, containerBattery, gattBatteryCache, collected, ct);

        return MergeDeviceSnapshots(collected).ToImmutableArray();
    }

    private static async Task QuerySelectorAsync(
        string selector,
        bool isBle,
        Dictionary<Guid, int> containerBattery,
        ConcurrentDictionary<string, int> gattBatteryCache,
        List<BluetoothDeviceSnapshot> output,
        CancellationToken ct)
    {
        try
        {
            string[] props = ["System.Devices.Aep.IsConnected", BtBatteryProp, DeviceContainerProp];
            var devices = await DeviceInformation.FindAllAsync(selector, props, DeviceInformationKind.AssociationEndpoint).AsTask(ct);
            foreach (var d in devices)
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                bool connected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? connObj)
                    && connObj is bool b && b;

                int? battery = null;
                if (d.Properties.TryGetValue(BtBatteryProp, out object? batObj))
                {
                    battery = TryExtractBatteryPercent(batObj);
                }

                if (!battery.HasValue &&
                    d.Properties.TryGetValue(DeviceContainerProp, out object? cidObj) &&
                    cidObj is Guid cid &&
                    containerBattery.TryGetValue(cid, out int cBat))
                {
                    battery = cBat;
                }

                if (!battery.HasValue && connected && isBle)
                {
                    battery = await TryReadBleGattBatteryAsync(d.Id, gattBatteryCache, ct);
                }

                if (!battery.HasValue && connected && gattBatteryCache.TryGetValue(d.Id, out int cached))
                {
                    battery = cached;
                }

                output.Add(new BluetoothDeviceSnapshot(d.Id, d.Name, connected, battery, null));
            }
        }
        catch { }
    }

    private static async Task<int?> TryReadBleGattBatteryAsync(
        string aepId,
        ConcurrentDictionary<string, int> cache,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(1100);
        try
        {
            using var ble = await BluetoothLEDevice.FromIdAsync(aepId).AsTask(timeoutCts.Token);
            if (ble is null) return null;

            var svcResult = await ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(timeoutCts.Token);
            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                return null;
            }

            foreach (var svc in svcResult.Services)
            {
                using (svc)
                {
                    var charResult = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached).AsTask(timeoutCts.Token);
                    if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
                    {
                        continue;
                    }

                    var levelChar = charResult.Characteristics[0];
                    var read = await levelChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeoutCts.Token);
                    if (read.Status == GattCommunicationStatus.Success && read.Value is not null && read.Value.Length > 0)
                    {
                        using var reader = DataReader.FromBuffer(read.Value);
                        byte raw = reader.ReadByte();
                        int pct = Math.Clamp((int)raw, 0, 100);
                        cache[aepId] = pct;
                        return pct;
                    }
                }
            }
        }
        catch
        {
            // Timeout or device asleep; keep silent and rely on cache if present.
        }
        return null;
    }
}
