namespace Paneless.Core;

/// <summary>NotFound: a device in one of your groups that isn't on the network right now.</summary>
public enum DeviceState { Mirroring, Connected, Available, Unpaired, Pairing, Unauthorized, Offline, NotFound }

public enum UnlockStatus { None, On, Paused }

public sealed record DeviceRow(
    string Key, string? Serial, string? Sn, bool IsNetwork,
    string Name, string Model, string Type, string Address, DeviceState State,
    string? ConnectAddr, string? PairAddr, UnlockStatus Unlock)
{
    public bool CanMirror => State is DeviceState.Connected or DeviceState.Available or DeviceState.Mirroring;
    public bool NeedsPairing => State is DeviceState.Pairing or DeviceState.Unpaired;
    public bool CanSetUpUnlock => Sn != null && State is DeviceState.Connected or DeviceState.Mirroring or DeviceState.Available;
    /// <summary>What group membership is stored under: the serial number, which survives reconnects and IP changes.</summary>
    public string GroupId => Sn ?? Key;

    /// <summary>A placeholder row for a group member that isn't on the network.</summary>
    public static DeviceRow Missing(string sn, KnownDevice? known) =>
        new($"missing:{sn}", null, sn, true, known?.Name is { Length: > 0 } n ? n : sn, known?.Model ?? "", known?.Type ?? "",
            "", DeviceState.NotFound, null, null, UnlockStatus.None);
}

/// <summary>Merges what adb has connected with what's visible on the network into one list of devices.</summary>
public sealed class DeviceDirectory(AdbClient adb, DataStore data)
{
    readonly Dictionary<string, DeviceInfo> _info = new();

    public UnlockStatus UnlockStatusFor(string? sn) =>
        sn == null || !data.Unlock.TryGetValue(sn, out var u) ? UnlockStatus.None : u.Enabled ? UnlockStatus.On : UnlockStatus.Paused;

    async Task<DeviceInfo?> InfoAsync(string serial)
    {
        if (_info.TryGetValue(serial, out var cached)) return cached;
        var info = await adb.GetDeviceInfoAsync(serial);
        if (info == null) return null;
        _info[serial] = info;
        if (info.Sn.Length > 0)
        {
            data.Known.TryGetValue(info.Sn, out var old);
            if (old == null || old.Name != info.Name || old.Type != info.Type || old.Model != info.Model)
            {
                data.Known[info.Sn] = new KnownDevice { Name = info.Name, Model = info.Model, Type = info.Type };
                data.SaveKnown();
            }
        }
        return info;
    }

    public static bool IsNetworkSerial(string serial) =>
        serial.Contains(':') || serial.Contains("._adb-tls-connect");

    public static bool IsEmulatorSerial(string serial) =>
        serial.StartsWith("emulator-", StringComparison.Ordinal);

    public async Task<IReadOnlyList<DeviceRow>> GetRowsAsync(ISet<string> mirroringSerials)
    {
        var mdns = await adb.GetMdnsServicesAsync();
        var devices = await adb.GetDevicesAsync();
        var infos = new Dictionary<string, DeviceInfo>();
        foreach (var d in devices)
            if (d.State == "device" && await InfoAsync(d.Serial) is { } info) infos[d.Serial] = info;
        return Merge(devices, mdns, infos, data.Known, mirroringSerials, UnlockStatusFor);
    }

    /// <summary>Merges adb devices and mDNS services into one deduplicated list. Pure and deterministic so it can be tested offline.</summary>
    public static IReadOnlyList<DeviceRow> Merge(
        IReadOnlyList<AdbDevice> devices,
        IReadOnlyList<MdnsService> mdns,
        IReadOnlyDictionary<string, DeviceInfo> infos,
        IReadOnlyDictionary<string, KnownDevice> knownDevices,
        ISet<string> mirroringSerials,
        Func<string?, UnlockStatus> unlockStatusFor)
    {
        // When the same device is connected over both USB and Wi-Fi at once, keep any actively mirrored
        // transport first, then prefer ready USB (lower latency) over ready Wi-Fi, then unauthorized/offline.
        var ordered = devices
            .OrderBy(d => mirroringSerials.Contains(d.Serial) ? 0
                        : d.State == "device" ? (!IsNetworkSerial(d.Serial) ? 1 : 2)
                        : 3)
            .ToList();

        var seenSn = new HashSet<string>();
        var seenIp = new HashSet<string>();
        var rows = new List<DeviceRow>();

        foreach (var d in ordered)
        {
            var isNet = IsNetworkSerial(d.Serial);
            var info = d.State == "device" && infos.TryGetValue(d.Serial, out var di) ? di : null;
            // For USB devices, adb's serial already is the hardware serial, so known names still match while unauthorized.
            var sn = !string.IsNullOrEmpty(info?.Sn) ? info!.Sn
                   : isNet ? AdbClient.SnFromInstance(d.Serial)
                   : (!IsEmulatorSerial(d.Serial) ? d.Serial : null);
            if (sn != null && !seenSn.Add(sn)) continue;   // same device listed twice by adb
            var ip = AdbClient.IsIpPort(d.Serial) ? d.Serial.Split(':')[0] : null;
            var svc = mdns.FirstOrDefault(s => (sn != null && s.Sn == sn) || (ip != null && s.Ip == ip));
            ip ??= svc?.Ip;
            if (ip != null) seenIp.Add(ip);
            var known = sn != null && knownDevices.TryGetValue(sn, out var k) ? k : null;
            var address = !isNet ? (IsEmulatorSerial(d.Serial) ? d.Serial : Text.T("USB cable"))
                        : AdbClient.IsIpPort(d.Serial) ? d.Serial
                        : svc?.Connect ?? d.Serial;
            var state = d.State switch
            {
                "device" => mirroringSerials.Contains(d.Serial) ? DeviceState.Mirroring : DeviceState.Connected,
                "unauthorized" => DeviceState.Unauthorized,
                _ => DeviceState.Offline,
            };
            rows.Add(new DeviceRow(
                $"dev:{d.Serial}", d.Serial, sn, isNet,
                First(info?.Name, known?.Name, d.Model, Text.T("Android device")),
                First(info?.Model, known?.Model, d.Model),
                First(info?.Type, known?.Type),
                address, state, svc?.Connect, svc?.Pair, unlockStatusFor(sn)));
        }

        foreach (var s in mdns)
        {
            if ((s.Sn != null && seenSn.Contains(s.Sn)) || seenIp.Contains(s.Ip)) continue;
            var known = s.Sn != null && knownDevices.TryGetValue(s.Sn, out var k) ? k : null;
            var state = s.Connect == null ? DeviceState.Pairing : known != null ? DeviceState.Available : DeviceState.Unpaired;
            var name = known?.Name ?? (state == DeviceState.Pairing ? Text.T("Device in pairing mode") : Text.T("New device"));
            rows.Add(new DeviceRow(
                $"net:{s.Ip}", null, s.Sn, true, name, known?.Model ?? "", known?.Type ?? "",
                First(s.Connect, s.Pair), state, s.Connect, s.Pair, unlockStatusFor(s.Sn)));
        }
        return rows;
    }

    /// <summary>Returns an adb serial that is ready to use, or null.</summary>
    public async Task<string?> ConnectAsync(DeviceRow row)
    {
        if (row.State is DeviceState.Connected or DeviceState.Mirroring) return row.Serial;
        if (row.State != DeviceState.Available || row.ConnectAddr == null) return null;
        await adb.ConnectAsync(row.ConnectAddr);
        return await adb.WaitReadyAsync(row.ConnectAddr, TimeSpan.FromSeconds(5)) ? row.ConnectAddr : null;
    }

    static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
}
