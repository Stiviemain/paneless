using System.Reflection;

namespace Paneless.Core;

/// <summary>An Android app you use like a Windows program: it opens in a window of its own (a separate virtual
/// screen of the device), can have its own shortcut, and remembers its window.</summary>
public sealed class RemoteApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>The device's id (its serial number), so it survives IP and port changes.</summary>
    public string DeviceId { get; set; } = "";
    public string Package { get; set; } = "";
    /// <summary>What it's called in Paneless, its window and its shortcut.</summary>
    public string Name { get; set; } = "";
    /// <summary>"phone" (tall), "tablet" (wide) or "custom".</summary>
    public string Shape { get; set; } = "phone";
    public int CustomWidth { get; set; } = 1080;
    public int CustomHeight { get; set; } = 1920;
    /// <summary>UI scale in percent (e.g. 65, 75, 85, 100, 125, 150). Lower lowers DPI so more fits without scrolling.</summary>
    public int ScalePercent { get; set; } = 100;
    public bool ShowToolbar { get; set; }
    /// <summary>Hide Android's on-screen keyboard in the window: type with the PC's keyboard.</summary>
    public bool PcKeyboardOnly { get; set; } = true;
    public bool OnTop { get; set; }
    /// <summary>Keep the device's own screen off while the app is open.</summary>
    public bool ScreenOff { get; set; } = true;
    /// <summary>Where the window was last (screen pixels, inside its frame); null until it was open once.</summary>
    public WindowPlace? Window { get; set; }
    /// <summary>Shortcut files Paneless made for it (to update or delete them later).</summary>
    public List<string> Shortcuts { get; set; } = [];

    /// <summary>The size of the virtual screen the app gets (it follows the window once open).</summary>
    public (int W, int H) DisplaySize() => Shape switch
    {
        "tablet" => (1920, 1200),
        "custom" => (Math.Clamp(CustomWidth, 320, 3840), Math.Clamp(CustomHeight, 320, 3840)),
        _ => (1080, 1920),
    };

    /// <summary>Scales <paramref name="targetW"/>×<paramref name="targetH"/> down proportionally if needed so it fits
    /// inside <paramref name="maxW"/>×<paramref name="maxH"/>, keeping both sides 8-pixel aligned (encoder-friendly)
    /// and at least 320 px.</summary>
    public static (int W, int H) FitToArea(int targetW, int targetH, int maxW, int maxH)
    {
        int limW = Math.Max(320, maxW), limH = Math.Max(320, maxH);
        double fit = Math.Min(1.0, Math.Min((double)limW / targetW, (double)limH / targetH));
        int w = Math.Max(320, ((int)Math.Round(targetW * fit)) & ~7);
        int h = Math.Max(320, ((int)Math.Round(targetH * fit)) & ~7);
        return (w, h);
    }

    /// <summary>Natural display density (DPI) for a virtual screen of <paramref name="w"/>×<paramref name="h"/> pixels:
    /// scrcpy defaults virtual displays to 160 dpi, which makes 1080×1920 look like a giant 1080dp desktop monitor
    /// (tiny UI, and triggers FREEFORM floating windows on tablets). Scaling around ~412 dp for portrait windows and
    /// ~800 dp for landscape tablet windows (interpolated smoothly across aspect ratios for custom/tablet windows),
    /// multiplied by <see cref="ScalePercent"/>, keeps layouts crisp down to Android's 120 dpi floor.</summary>
    public int DisplayDpi(int w, int h)
    {
        var shortSide = Math.Min(w, h);
        double targetDp;
        int maxDpi;
        if (Shape == "phone")
        {
            targetDp = 412.0;
            maxDpi = 480;
        }
        else
        {
            var aspect = (double)w / Math.Max(1, h);
            var t = Math.Clamp((aspect - 0.75) / 0.60, 0.0, 1.0);
            targetDp = 412.0 + t * (800.0 - 412.0);
            maxDpi = t >= 0.5 ? 360 : 480;
        }
        var scale = (ScalePercent is >= 50 and <= 200 ? ScalePercent : 100) / 100.0;
        var raw = (int)Math.Round(shortSide * 160.0 / targetDp * scale);
        return Math.Clamp((raw / 20) * 20, 120, maxDpi);
    }

    /// <summary>The scrcpy options for this app's window (after "-s serial").</summary>
    public List<string> ScrcpyArgs(string title, MirrorOptions quality, bool embedded = false)
    {
        var (rawW, rawH) = !embedded && Window is { W: > 100, H: > 100 } place
            ? (Math.Clamp(place.W, 320, 3840), Math.Clamp(place.H, 320, 3840))
            : DisplaySize();
        int w = Math.Max(320, rawW & ~7);
        int h = Math.Max(320, rawH & ~7);
        var dpi = DisplayDpi(w, h);
        // Standalone windows use --flex-display so dragging the window border adapts the virtual display.
        // Embedded tabs (e.g. RDM) are much wider than a phone, so omit --flex-display and let --render-fit=letterbox
        // preserve the configured phone/tablet/custom shape crisply centered inside the tab.
        var args = new List<string> { "--window-title", title, $"--new-display={w}x{h}/{dpi}", "--render-fit=letterbox",
            "--start-app=" + Package, "--no-audio", "--no-vd-system-decorations" };
        if (embedded) args.Add("--window-borderless");
        else args.Add("--flex-display");
        if (PcKeyboardOnly) args.Add("--display-ime-policy=hide");
        args.Add("--video-codec=" + quality.VideoCodec);
        args.Add($"--video-bit-rate={Math.Clamp(quality.BitrateMbps, 1, 200)}M");
        if (quality.MaxFps > 0) args.Add($"--max-fps={quality.MaxFps}");
        if (ScreenOff) args.Add("--turn-screen-off");
        if (!embedded && OnTop) args.Add("--always-on-top");
        // Android (e.g. 17 on Pixels) turns a virtual screen off a few seconds after the device's own screen goes off;
        // simulated user activity keeps just this screen on (the device still locks and its own screen stays off).
        if (quality.KeepScreensOn) args.Add("--keep-active");
        // scrcpy 5.x rejects --window-width/--window-height when --flex-display is on;
        // --new-display=WxH above already restores the remembered size, so only pass position here.
        if (!embedded && Window is { W: > 100, H: > 100 } p)
            args.AddRange([$"--window-x={Math.Max(0, p.X)}", $"--window-y={Math.Max(0, p.Y)}"]);
        return args;
    }

    /// <summary>A file name for its shortcut ("WhatsApp.lnk"), without characters Windows doesn't allow.</summary>
    public static string ShortcutFileName(string name)
    {
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']));
        var clean = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "App";
        return (clean.Length > 80 ? clean[..80] : clean) + ".lnk";
    }
}

public sealed class WindowPlace
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

/// <summary>Your remote apps (remote-apps.json); in demo mode in memory only.</summary>
public sealed class RemoteAppStore
{
    public const string FileName = "remote-apps.json";
    readonly bool _persist;
    public RemoteAppStore() : this(JsonFile.Load<List<RemoteApp>>(FileName), true) { }
    public RemoteAppStore(List<RemoteApp> apps, bool persist) { All = apps; _persist = persist; }
    public List<RemoteApp> All { get; }
    public event Action? Changed;
    public RemoteApp? Get(string id) => All.FirstOrDefault(a => a.Id == id);
    public RemoteApp? Find(string deviceId, string package) => All.FirstOrDefault(a => a.DeviceId == deviceId && a.Package == package);

    public void Save()
    {
        if (_persist) { try { JsonFile.Save(FileName, All); } catch { } }
        Changed?.Invoke();
    }

    /// <summary>Where its icons live: id.png (the app's own) and id.ico (for the shortcut, with the Paneless badge).</summary>
    public static string IconDir => Path.Combine(AppPaths.DataDir, "remote-apps");
    public static string IconPng(string id) => Path.Combine(IconDir, id + ".png");
    public static string IconIco(string id) => Path.Combine(IconDir, id + ".ico");
}

/// <summary>An app with a launcher icon on a device, as the helper reports it.</summary>
public sealed record LauncherApp(string Package, string Name);

/// <summary>Reads app names and launcher icons with Paneless' small helper on the device (tools/android-helper):
/// started like scrcpy's own server with app_process, it only reads, and nothing gets installed.</summary>
public sealed partial class AdbClient
{
    internal const string HelperRemote = "/data/local/tmp/paneless-helper.jar";
    const string HelperRun = "CLASSPATH=" + HelperRemote + " app_process / app.paneless.helper.Main";
    readonly HashSet<string> _helperPushed = new();
    readonly SemaphoreSlim _helperLock = new(1, 1);

    internal async Task<bool> PushHelperAsync(string serial, bool force = false)
    {
        if (!force) lock (_helperPushed) if (_helperPushed.Contains(serial)) return true;
        await _helperLock.WaitAsync();
        try
        {
            if (!force) lock (_helperPushed) if (_helperPushed.Contains(serial)) return true;
            var local = Path.Combine(Path.GetTempPath(), "paneless", "paneless-helper.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Paneless.Core.paneless-helper.jar"))
            {
                if (res == null) return false;
                using var f = File.Create(local);
                await res.CopyToAsync(f);
            }
            var r = await RunAsync(TimeSpan.FromSeconds(20), "-s", serial, "push", local, HelperRemote);
            if (!r.Ok) return false;
            lock (_helperPushed) _helperPushed.Add(serial);
            return true;
        }
        finally { _helperLock.Release(); }
    }

    /// <summary>Changes the density (DPI) of a virtual display (&gt; 0) live without restarting the app.</summary>
    public async Task SetDisplayDensityAsync(string serial, int displayId, int dpi)
    {
        if (displayId <= 0) return;
        await ShellAsync(serial, $"wm density {Math.Clamp(dpi, 120, 480)} -d {displayId}", TimeSpan.FromSeconds(5));
    }

    /// <summary>Every app with a launcher icon, with its name; null if the helper couldn't run.</summary>
    public async Task<List<LauncherApp>?> GetLauncherAppsAsync(string serial)
    {
        if (!await PushHelperAsync(serial)) return null;
        var r = await ShellAsync(serial, HelperRun + " apps", TimeSpan.FromSeconds(30));
        var apps = ParseLauncherApps(r.Output);
        return apps.Count > 0 ? apps : null;
    }

    /// <summary>Launcher icons (PNG, size × size, in the device's icon shape) and names of some apps. Apps the device
    /// doesn't know, or can't draw, are left out.</summary>
    public async Task<Dictionary<string, (string Name, byte[] Png)>> GetAppIconsAsync(string serial, int size, IEnumerable<string> packages)
    {
        var result = new Dictionary<string, (string, byte[])>();
        if (!await PushHelperAsync(serial)) return result;
        foreach (var chunk in packages.Where(IsPackageName).Distinct().Chunk(60))
        {
            var r = await ShellAsync(serial, $"{HelperRun} icons {size} {string.Join(' ', chunk)}", TimeSpan.FromSeconds(60));
            foreach (var (pkg, name, png) in ParseIcons(r.Output)) result[pkg] = (name, png);
        }
        return result;
    }

    static bool IsPackageName(string p) => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_');

    public static List<LauncherApp> ParseLauncherApps(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r').Split('\t'))
            .Where(p => p.Length == 2 && IsPackageName(p[0]))
            .Select(p => new LauncherApp(p[0], p[1].Length > 0 ? p[1] : p[0]))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static IEnumerable<(string Package, string Name, byte[] Png)> ParseIcons(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var p = line.TrimEnd('\r').Split('\t');
            if (p.Length != 3 || !IsPackageName(p[0]) || p[2].Length == 0) continue;
            byte[] png;
            try { png = Convert.FromBase64String(p[2]); } catch (FormatException) { continue; }
            if (png.Length > 8 && png[0] == 0x89 && png[1] == (byte)'P') yield return (p[0], p[1], png);
        }
    }
}

/// <summary>Writes a Windows .ico file from PNG pictures (one per size; Windows Vista and later read PNG inside ICO).</summary>
public static class IcoFile
{
    public static byte[] FromPngs(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);   // reserved, type icon, count
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
            w.Write((byte)0); w.Write((byte)0);        // no palette, reserved
            w.Write((ushort)1); w.Write((ushort)32);   // planes, bits per pixel
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        w.Flush();
        return ms.ToArray();
    }
}
