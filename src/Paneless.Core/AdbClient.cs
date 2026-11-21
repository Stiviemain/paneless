using System.Text.RegularExpressions;

namespace Paneless.Core;

public sealed record AdbDevice(string Serial, string State, string Model);

/// <summary>One device seen on the network: its "connect" endpoint (Wireless debugging on)
/// and/or its "pairing" endpoint (showing "Pair device with pairing code").</summary>
public sealed class MdnsService
{
    public required string Ip { get; init; }
    public string? Sn { get; set; }
    public string? Connect { get; set; }
    public string? Pair { get; set; }
}

public sealed record DeviceInfo(string Sn, string Model, string Type, string Name);

public sealed record BatteryInfo(int Level, bool Charging, bool Full)
{
    public string Text => Full ? $"{Level} % ✓" : Charging ? $"{Level} % ⚡" : $"{Level} %";
}

public sealed record AppPackage(string Name, bool Disabled);

public sealed partial class AdbClient(string adbPath)
{
    readonly IReadOnlyDictionary<string, string> _env = new Dictionary<string, string> { ["ADB_MDNS_OPENSCREEN"] = "1" };

    public string AdbPath { get; } = adbPath;

    public Task<ProcessResult> RunAsync(TimeSpan timeout, params string[] args) =>
        ProcessRunner.RunAsync(AdbPath, args, timeout, _env);

    public Task<ProcessResult> RunAsync(params string[] args) => RunAsync(TimeSpan.FromSeconds(8), args);

    public Task<ProcessResult> ShellAsync(string serial, string command, TimeSpan? timeout = null) =>
        RunAsync(timeout ?? TimeSpan.FromSeconds(8), "-s", serial, "shell", command);

    public Task StartServerAsync() => RunAsync(TimeSpan.FromSeconds(20), "start-server");

    public async Task<bool> IsDiscoveryAvailableAsync()
    {
        var r = await RunAsync(TimeSpan.FromSeconds(5), "mdns", "check");
        return !UnavailableRx().IsMatch(r.Output);
    }

    public Task<ProcessResult> ConnectAsync(string address) => RunAsync(TimeSpan.FromSeconds(10), "connect", address);

    public Task<ProcessResult> PairAsync(string address, string code) => RunAsync(TimeSpan.FromSeconds(20), "pair", address, code);

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync()
    {
        var r = await RunAsync("devices", "-l");
        var list = new List<AdbDevice>();
        foreach (var line in Lines(r.Output))
        {
            var m = DeviceLineRx().Match(line);
            if (!m.Success) continue;
            var model = ModelRx().Match(m.Groups[3].Value);
            list.Add(new AdbDevice(m.Groups[1].Value, m.Groups[2].Value, model.Success ? model.Groups[1].Value.Replace('_', ' ') : ""));
        }
        return list;
    }

    public async Task<IReadOnlyList<MdnsService>> GetMdnsServicesAsync()
    {
        var r = await RunAsync("mdns", "services");
        var map = new Dictionary<string, MdnsService>();
        var order = new List<string>();
        foreach (var line in Lines(r.Output))
        {
            var m = MdnsLineRx().Match(line);
            if (!m.Success) continue;
            var (inst, type, ip, port) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            if (!map.TryGetValue(ip, out var e)) { e = new MdnsService { Ip = ip }; map[ip] = e; order.Add(ip); }
            if (type.StartsWith("_adb-tls-connect", StringComparison.Ordinal))
            {
                e.Connect = $"{ip}:{port}";
                if (SnFromInstance(inst) is { } sn) e.Sn = sn;
            }
            else if (type.StartsWith("_adb-tls-pairing", StringComparison.Ordinal))
            {
                e.Pair = $"{ip}:{port}";
            }
        }
        return order.Select(ip => map[ip]).ToList();
    }

    public async Task<DeviceInfo?> GetDeviceInfoAsync(string serial)
    {
        var r = await ShellAsync(serial,
            "getprop ro.serialno; getprop ro.product.model; getprop ro.build.characteristics; settings get global device_name",
            TimeSpan.FromSeconds(6));
        if (!r.Ok) return null;
        var l = r.Output.Split('\n').Select(s => s.Trim()).ToArray();
        if (l.Length < 3 || l[1].Length == 0) return null;
        var name = l.Length >= 4 && l[3].Length > 0 && l[3] != "null" ? l[3] : l[1];
        var c = l[2];
        var type = c.Contains("tablet") ? "Tablet" : c.Contains("watch") ? "Watch" : TvRx().IsMatch(c) ? "TV" : "Phone";
        return new DeviceInfo(l[0], l[1], type, name);
    }

    /// <summary>Size of the main display (id 0) in its current orientation, e.g. 2410×1080 in landscape.</summary>
    public async Task<(int W, int H)?> GetDisplaySizeAsync(string serial)
    {
        var r = await ShellAsync(serial, "dumpsys window displays");
        // The dump has one "Display: mDisplayId=N" section per display; take display 0's "cur=WxH".
        var sections = r.Output.Split("Display: mDisplayId=");
        foreach (var s in sections.Skip(1))
        {
            if (!s.StartsWith("0")) continue;
            var m = CurSizeRx().Match(s);
            if (m.Success) return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }
        return null;
    }

    /// <summary>A PNG screenshot of the device's screen.</summary>
    public async Task<byte[]?> ScreencapAsync(string serial)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(AdbPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-s", serial, "exec-out", "screencap", "-p" }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await p.StandardOutput.BaseStream.CopyToAsync(ms, cts.Token);
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } return null; }
        var bytes = ms.ToArray();
        return bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' ? bytes : null;
    }

    /// <summary>How the device is turned: 0 = its natural way up, 1 = turned left (90°), 2 = upside down, 3 = turned right.</summary>
    public async Task<int> GetRotationAsync(string serial)
    {
        var r = await ShellAsync(serial, "dumpsys input");
        // "Viewport INTERNAL: displayId=0, ..., orientation=1, ..."
        foreach (var line in Lines(r.Output))
            if (line.Contains("Viewport INTERNAL") && line.Contains("displayId=0,") && RotationRx().Match(line) is { Success: true } m)
                return int.Parse(m.Groups[1].Value);
        return 0;
    }

    /// <summary>How the camera sensor is mounted, in degrees (dumpsys media.camera "Orientation"), e.g. 90 for most back cameras.</summary>
    public async Task<int?> GetCameraSensorOrientationAsync(string serial, bool front)
    {
        var r = await ShellAsync(serial, "dumpsys media.camera");
        var facing = front ? "Front" : "Back";
        var seen = false;
        foreach (var raw in Lines(r.Output))
        {
            var line = raw.Trim();
            if (line.StartsWith("Facing:")) seen = line.EndsWith(facing);
            else if (seen && line.StartsWith("Orientation:") && int.TryParse(line[12..].Trim(), out var deg)) return deg;
        }
        return null;
    }

    /// <summary>How the device is physically held, from its "Device Orientation" motion sensor (0 = natural way up,
    /// 1 = turned left, 2 = upside down, 3 = turned right), even when the screen doesn't rotate (auto-rotate off, or an
    /// app that stays upright). Null if the device doesn't report it.</summary>
    public async Task<int?> GetPhysicalRotationAsync(string serial)
    {
        var r = await ShellAsync(serial, "dumpsys sensorservice | grep -A 14 -E 'Device Orientation.*last [0-9]+ events'");
        // "Device Orientation: last 10 events" then lines like "	 8 (ts=718582.198172762, wall=09:24:34.182) 3.00,"
        double bestTs = -1;
        int? value = null;
        var inList = false;
        foreach (var raw in Lines(r.Output))
        {
            if (raw.Contains("Device Orientation") && raw.Contains("events")) { inList = true; continue; }
            if (!inList) continue;
            var m = SensorEventRx().Match(raw);
            if (!m.Success) { inList = false; continue; }
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ts)
                && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                && ts > bestTs && v is >= 0 and <= 3)
            {
                bestTs = ts;
                value = (int)v;
            }
        }
        return value;
    }

    /// <summary>Whether the screen turns with the device right now: auto-rotate on, and the app in front doesn't fix
    /// its orientation (the Pixel home screen, for example, stays upright).</summary>
    public async Task<bool> ScreenFollowsDeviceAsync(string serial)
    {
        var r = await ShellAsync(serial, "dumpsys window displays");
        var section = r.Output.Split("Display: mDisplayId=").Skip(1).FirstOrDefault(x => x.Length > 1 && x[0] == '0' && !char.IsDigit(x[1]));
        if (section == null) return true;
        var free = !section.Contains("mUserRotationMode=USER_ROTATION_LOCKED");
        var app = AppOrientationRx().Match(section) is { Success: true } m ? m.Groups[1].Value : "UNSPECIFIED";
        return free && app is "UNSPECIFIED" or "UNSET" or "SENSOR" or "FULL_SENSOR" or "USER" or "FULL_USER" or "BEHIND";
    }

    /// <summary>How the device is held: the screen's rotation when the screen follows the device (the motion sensor's
    /// last value can be old, e.g. lying flat), otherwise the motion sensor.</summary>
    public async Task<int> GetHeldRotationAsync(string serial)
    {
        var screen = await GetRotationAsync(serial);
        if (await ScreenFollowsDeviceAsync(serial)) return screen;
        return await GetPhysicalRotationAsync(serial) ?? screen;
    }

    /// <summary>The clockwise turn that shows the camera picture upright for how the device is held right now.</summary>
    public async Task<int?> GetCameraUprightTurnAsync(string serial, bool front)
    {
        if (await GetCameraSensorOrientationAsync(serial, front) is not { } sensor) return null;
        var degrees = await GetHeldRotationAsync(serial) * 90;
        return front ? (sensor + degrees) % 360 : (sensor - degrees + 360) % 360;
    }

    /// <summary>Battery level (0–100) and whether it's charging, from dumpsys battery.</summary>
    public async Task<BatteryInfo?> GetBatteryAsync(string serial)
    {
        var r = await ShellAsync(serial, "dumpsys battery");
        if (!r.Ok) return null;
        int? level = null, scale = 100, status = null;
        bool plugged = false;
        foreach (var raw in Lines(r.Output))
        {
            var line = raw.Trim();
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            var key = line[..i].Trim();
            var val = line[(i + 1)..].Trim();
            switch (key)
            {
                case "level": if (int.TryParse(val, out var l)) level = l; break;
                case "scale": if (int.TryParse(val, out var s) && s > 0) scale = s; break;
                case "status": if (int.TryParse(val, out var st)) status = st; break;
                case "AC powered" or "USB powered" or "Wireless powered" or "Dock powered": if (val == "true") plugged = true; break;
            }
        }
        if (level == null) return null;
        // status 2 = charging, 5 = full
        return new BatteryInfo((int)Math.Round(level.Value * 100.0 / scale!.Value), status == 2 || (plugged && status != 3), status == 5);
    }

    /// <summary>Installed packages; third-party only unless <paramref name="system"/>.</summary>
    public async Task<IReadOnlyList<AppPackage>> GetPackagesAsync(string serial, bool system)
    {
        var all = await ShellAsync(serial, "pm list packages" + (system ? "" : " -3"), TimeSpan.FromSeconds(20));
        var disabled = await ShellAsync(serial, "pm list packages -d", TimeSpan.FromSeconds(20));
        var off = Lines(disabled.Output).Where(l => l.StartsWith("package:")).Select(l => l[8..].Trim()).ToHashSet();
        return Lines(all.Output).Where(l => l.StartsWith("package:")).Select(l => l[8..].Trim())
            .Where(p => p.Length > 0).Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => new AppPackage(p, off.Contains(p))).ToList();
    }

    /// <summary>Installs (or updates) an APK from this computer.</summary>
    public Task<ProcessResult> InstallAsync(string serial, string apk) =>
        RunAsync(TimeSpan.FromMinutes(5), "-s", serial, "install", "-r", "-d", apk);

    /// <summary>Copies a file or folder from this computer to the device.</summary>
    public Task<ProcessResult> PushAsync(string serial, string localPath, string remoteDir) =>
        RunAsync(TimeSpan.FromMinutes(10), "-s", serial, "push", localPath, remoteDir.EndsWith('/') ? remoteDir : remoteDir + "/");

    /// <summary>Starts an app's main screen.</summary>
    public Task<ProcessResult> LaunchAsync(string serial, string package) =>
        ShellAsync(serial, $"monkey -p {ScriptText.ShellQuote(package)} -c android.intent.category.LAUNCHER 1");

    public async Task<bool> WaitReadyAsync(string serial, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var d = (await GetDevicesAsync()).FirstOrDefault(x => x.Serial == serial);
            if (d?.State == "device") return true;
            await Task.Delay(400);
        }
        return false;
    }

    /// <summary>mDNS names look like adb-&lt;device serial&gt;-&lt;random&gt;[._adb-tls-connect._tcp].</summary>
    public static string? SnFromInstance(string name)
    {
        var m = InstanceRx().Match(name);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool IsIpPort(string s) => IpPortRx().IsMatch(s);

    [GeneratedRegex(@"orientation=(\d)")]
    private static partial Regex RotationRx();

    [GeneratedRegex(@"^\s*\d+ \(ts=([\d.]+),[^)]*\)\s*(-?[\d.]+)")]
    private static partial Regex SensorEventRx();

    [GeneratedRegex(@"mCurrentAppOrientation=SCREEN_ORIENTATION_(\w+)")]
    private static partial Regex AppOrientationRx();

    static IEnumerable<string> Lines(string s) => s.Split('\n').Select(l => l.TrimEnd('\r'));

    [GeneratedRegex(@"^(\S+)\s+(device|offline|unauthorized|authorizing|connecting|recovery|sideload|bootloader)\b(.*)$")]
    private static partial Regex DeviceLineRx();
    [GeneratedRegex(@"model:(\S+)")]
    private static partial Regex ModelRx();
    [GeneratedRegex(@"^(\S+)\s+(_adb[\w.-]*)\s+([\d.]+):(\d+)")]
    private static partial Regex MdnsLineRx();
    [GeneratedRegex(@"^adb-(.+)-[^-.]+(\._adb.*)?$")]
    private static partial Regex InstanceRx();
    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}:\d{1,5}$")]
    private static partial Regex IpPortRx();
    [GeneratedRegex(@"\bcur=(\d+)x(\d+)")]
    private static partial Regex CurSizeRx();
    [GeneratedRegex(@"\btv\b")]
    private static partial Regex TvRx();
    [GeneratedRegex("unavailable|ERROR")]
    private static partial Regex UnavailableRx();
}
