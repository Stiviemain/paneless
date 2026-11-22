namespace Paneless.Core;

/// <summary>What Claude may do on a device, each level including the ones before it.</summary>
public enum AssistantAccess
{
    /// <summary>Claude doesn't see the device.</summary>
    Off,
    /// <summary>Name, status, battery, apps, logs.</summary>
    Read,
    /// <summary>Also screenshots of the screen.</summary>
    Screen,
    /// <summary>Also taps, swipes, typing, buttons, opening apps.</summary>
    Control,
    /// <summary>Also installing and removing apps, files, scripts, recipes, shell commands, restarts.</summary>
    Manage,
}

/// <summary>Settings for one device or one group. Every value is optional: null means "use the group's,
/// or else the global default". The device's own value wins over its group's.</summary>
public sealed class DeviceProfile
{
    public bool? ScreenOff { get; set; }
    public bool? Audio { get; set; }
    public bool? OnTop { get; set; }
    public bool? KeepAwake { get; set; }
    public bool? LockOnClose { get; set; }
    /// <summary>Keep second screens and app windows showing while the device is locked.</summary>
    public bool? KeepScreensOn { get; set; }
    /// <summary>"1920 px", "Full resolution", …</summary>
    public string? MaxSize { get; set; }
    /// <summary>h264, h265 or av1.</summary>
    public string? VideoCodec { get; set; }
    public int? BitrateMbps { get; set; }
    /// <summary>0 = no limit.</summary>
    public int? MaxFps { get; set; }
    /// <summary>Start mirroring by itself when the device connects.</summary>
    public bool? AutoMirror { get; set; }
    /// <summary>Where files dropped on the device go, e.g. /sdcard/Download/.</summary>
    public string? PushTarget { get; set; }
    /// <summary>Scripts (by id) that run every time the device connects.</summary>
    public List<string> OnConnectScripts { get; set; } = new();
    /// <summary>What Claude may do on this device (null = the group's or the default in Settings).</summary>
    public AssistantAccess? Assistant { get; set; }
    /// <summary>Global shortcut that mirrors this device, e.g. "Ctrl+Alt+P" (devices only, not groups).</summary>
    public string? Hotkey { get; set; }
    /// <summary>Two-way text clipboard sync even when no mirror window is open.</summary>
    public bool? ClipboardSync { get; set; }
    /// <summary>Auto-save new photos and screenshots to this PC as they are taken.</summary>
    public bool? AutoDownloadMedia { get; set; }
    /// <summary>Opt-in: watch notifications to auto-copy 2FA codes to this PC's clipboard.</summary>
    public bool? AutoCopyOtp { get; set; }

    public bool IsEmpty =>
        ScreenOff == null && Audio == null && OnTop == null && KeepAwake == null && LockOnClose == null && KeepScreensOn == null && MaxSize == null && VideoCodec == null
        && BitrateMbps == null && MaxFps == null && AutoMirror == null && PushTarget == null && OnConnectScripts.Count == 0
        && string.IsNullOrEmpty(Hotkey) && Assistant == null
        && ClipboardSync == null && AutoDownloadMedia == null && AutoCopyOtp == null;
}

/// <summary>What actually applies to a device after combining device, group and global settings.</summary>
public sealed record EffectiveProfile(
    bool ScreenOff, bool Audio, bool OnTop, bool KeepAwake, bool LockOnClose, string MaxSize, string VideoCodec, int BitrateMbps, int MaxFps,
    bool AutoMirror, string PushTarget, IReadOnlyList<string> OnConnectScripts, string? Hotkey, AssistantAccess Assistant, bool KeepScreensOn = true,
    bool ClipboardSync = false, bool AutoDownloadMedia = false, bool AutoCopyOtp = false)
{
    public int? MaxSizePixels => int.TryParse(MaxSize.Split(' ')[0], out var px) ? px : null;

    public MirrorOptions ToMirrorOptions() =>
        new(ScreenOff, Audio, OnTop, KeepAwake || ScreenOff, MaxSizePixels, VideoCodec, BitrateMbps, MaxFps, LockOnClose, KeepScreensOn);

    public static EffectiveProfile Resolve(AppSettings global, DeviceProfile? group, DeviceProfile? device)
    {
        T Pick<T>(Func<DeviceProfile, T?> get, T fallback) where T : struct => (device != null ? get(device) : null) ?? (group != null ? get(group) : null) ?? fallback;
        string PickS(Func<DeviceProfile, string?> get, string fallback) =>
            (device != null ? get(device) : null) is { Length: > 0 } d ? d : (group != null ? get(group) : null) is { Length: > 0 } g ? g : fallback;

        var scripts = new List<string>();
        if (group != null) scripts.AddRange(group.OnConnectScripts);
        if (device != null) scripts.AddRange(device.OnConnectScripts.Where(s => !scripts.Contains(s)));
        return new EffectiveProfile(
            Pick(p => p.ScreenOff, global.ScreenOff),
            Pick(p => p.Audio, global.Audio),
            Pick(p => p.OnTop, global.OnTop),
            Pick(p => p.KeepAwake, global.KeepAwake),
            Pick(p => p.LockOnClose, global.LockOnClose),
            PickS(p => p.MaxSize, global.MaxSize),
            PickS(p => p.VideoCodec, global.VideoCodec),
            Pick(p => p.BitrateMbps, global.BitrateMbps),
            Pick(p => p.MaxFps, global.MaxFps),
            Pick(p => p.AutoMirror, false),
            PickS(p => p.PushTarget, global.PushTarget),
            scripts,
            device?.Hotkey is { Length: > 0 } h ? h : null,
            device?.Assistant ?? group?.Assistant ?? global.AssistantDefaultAccess,
            Pick(p => p.KeepScreensOn, global.KeepScreensOn),
            Pick(p => p.ClipboardSync, global.ClipboardSync),
            Pick(p => p.AutoDownloadMedia, global.AutoDownloadMedia),
            Pick(p => p.AutoCopyOtp, global.AutoCopyOtp));
    }
}

/// <summary>profiles.json: one profile per device, by serial number.</summary>
public sealed class ProfileStore
{
    public const string FileName = "profiles.json";
    readonly bool _persist;
    readonly Dictionary<string, DeviceProfile> _profiles;

    public ProfileStore() : this(JsonFile.Load<Dictionary<string, DeviceProfile>>(FileName), persist: true) { }
    public ProfileStore(Dictionary<string, DeviceProfile> profiles, bool persist) { _profiles = profiles; _persist = persist; }

    public event Action? Changed;

    public IReadOnlyDictionary<string, DeviceProfile> All => _profiles;
    public DeviceProfile? Get(string id) => _profiles.GetValueOrDefault(id);
    public DeviceProfile GetOrCreate(string id) => _profiles.TryGetValue(id, out var p) ? p : _profiles[id] = new DeviceProfile();

    public void ReplaceAll(IReadOnlyDictionary<string, DeviceProfile> profiles)
    {
        _profiles.Clear();
        foreach (var (k, v) in profiles) _profiles[k] = v;
        Save();
    }

    public void Save()
    {
        foreach (var k in _profiles.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) _profiles.Remove(k);
        if (_persist) { try { JsonFile.Save(FileName, _profiles); } catch { /* not fatal */ } }
        Changed?.Invoke();
    }
}
