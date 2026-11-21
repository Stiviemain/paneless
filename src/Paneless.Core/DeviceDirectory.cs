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

    public async Task<IReadOnlyList<DeviceRow>> GetRowsAsync(ISet<string> mirroringSerials)
    {
        var mdns = await adb.GetMdnsServicesAsync();
        var devices = await adb.GetDevicesAsync();
        var seenSn = new HashSet<string>();
        var seenIp = new HashSet<string>();
        var rows = new List<DeviceRow>();

        foreach (var d in devices)
        {
            var info = d.State == "device" ? await InfoAsync(d.Serial) : null;
            var sn = !string.IsNullOrEmpty(info?.Sn) ? info!.Sn : AdbClient.SnFromInstance(d.Serial);
            if (sn != null && !seenSn.Add(sn)) continue;   // same device listed twice by adb
            var ip = AdbClient.IsIpPort(d.Serial) ? d.Serial.Split(':')[0] : null;
            var svc = mdns.FirstOrDefault(s => (sn != null && s.Sn == sn) || (ip != null && s.Ip == ip));
            ip ??= svc?.Ip;
            if (ip != null) seenIp.Add(ip);
            var known = sn != null && data.Known.TryGetValue(sn, out var k) ? k : null;
            var isNet = d.Serial.Contains(':') || d.Serial.Contains("._adb-tls-connect");
            var address = AdbClient.IsIpPort(d.Serial) ? d.Serial
                        : svc?.Connect ?? (isNet ? d.Serial : "USB cable");
            var state = d.State switch
            {
                "device" => mirroringSerials.Contains(d.Serial) ? DeviceState.Mirroring : DeviceState.Connected,
                "unauthorized" => DeviceState.Unauthorized,
                _ => DeviceState.Offline,
            };
            rows.Add(new DeviceRow(
                $"dev:{d.Serial}", d.Serial, sn, isNet,
                First(info?.Name, known?.Name, d.Model, "Android device"),
                First(info?.Model, known?.Model, d.Model),
                First(info?.Type, known?.Type),
                address, state, svc?.Connect, svc?.Pair, UnlockStatusFor(sn)));
        }

        foreach (var s in mdns)
        {
            if ((s.Sn != null && seenSn.Contains(s.Sn)) || seenIp.Contains(s.Ip)) continue;
            var known = s.Sn != null && data.Known.TryGetValue(s.Sn, out var k) ? k : null;
            var state = s.Connect == null ? DeviceState.Pairing : known != null ? DeviceState.Available : DeviceState.Unpaired;
            var name = known?.Name ?? (state == DeviceState.Pairing ? "Device in pairing mode" : "New device");
            rows.Add(new DeviceRow(
                $"net:{s.Ip}", null, s.Sn, true, name, known?.Model ?? "", known?.Type ?? "",
                First(s.Connect, s.Pair), state, s.Connect, s.Pair, UnlockStatusFor(s.Sn)));
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
