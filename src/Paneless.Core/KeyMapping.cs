using System.Collections.Concurrent;

namespace Paneless.Core;

public enum MappingKind { Tap, Joystick }

/// <summary>One mapping. Positions are fractions of the screen (0–1), so a profile works at any resolution.</summary>
public sealed class KeyMapping
{
    public MappingKind Kind { get; set; } = MappingKind.Tap;
    /// <summary>Tap: the key. Joystick: unused.</summary>
    public string Key { get; set; } = "";
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    /// <summary>Joystick keys (default WASD).</summary>
    public string Up { get; set; } = "W";
    public string Left { get; set; } = "A";
    public string Down { get; set; } = "S";
    public string Right { get; set; } = "D";
    /// <summary>Joystick reach, as a fraction of the screen's shorter side.</summary>
    public double Radius { get; set; } = 0.11;

    public IEnumerable<string> Keys => Kind == MappingKind.Tap ? [Key] : [Up, Left, Down, Right];
}

public sealed class KeymapProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "New profile";
    /// <summary>Made on a landscape screen (most games) or a portrait one.</summary>
    public bool Landscape { get; set; } = true;
    public List<KeyMapping> Mappings { get; set; } = new();
}

public sealed class KeymapData
{
    public List<KeymapProfile> Profiles { get; set; } = new();
    /// <summary>Device serial number → id of the profile used for it.</summary>
    public Dictionary<string, string> ActiveProfile { get; set; } = new();
}

public sealed class KeymapStore
{
    const string File = "keymaps.json";
    readonly bool _persist;
    public KeymapStore() : this(JsonFile.Load<KeymapData>(File), persist: true) { }
    public KeymapStore(KeymapData data, bool persist) { Data = data; _persist = persist; }
    public KeymapData Data { get; }
    public void Save() { if (_persist) JsonFile.Save(File, Data); }

    public KeymapProfile? ProfileFor(string? sn) =>
        sn != null && Data.ActiveProfile.TryGetValue(sn, out var id) ? Data.Profiles.FirstOrDefault(p => p.Id == id) : null;
}

/// <summary>Keys that can be mapped: display name ↔ Windows virtual-key code.</summary>
public static class KeyNames
{
    static readonly Dictionary<string, int> ByName = Build();
    static readonly Dictionary<int, string> ByCode = ByName.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    static Dictionary<string, int> Build()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
        for (var n = 0; n <= 9; n++) { d[n.ToString()] = '0' + n; d["Num" + n] = 0x60 + n; }
        for (var f = 1; f <= 12; f++) d["F" + f] = 0x70 + f - 1;
        foreach (var (name, vk) in new (string, int)[]
        {
            ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Backspace", 0x08), ("CapsLock", 0x14),
            ("LeftShift", 0xA0), ("RightShift", 0xA1), ("LeftCtrl", 0xA2), ("RightCtrl", 0xA3), ("LeftAlt", 0xA4), ("RightAlt", 0xA5),
            ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
            ("Insert", 0x2D), ("Delete", 0x2E), ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
            ("`", 0xC0), ("-", 0xBD), ("=", 0xBB), ("[", 0xDB), ("]", 0xDD), ("\\", 0xDC), (";", 0xBA), ("'", 0xDE),
            (",", 0xBC), (".", 0xBE), ("/", 0xBF),
        }) d[name] = vk;
        return d;
    }

    public static int? Code(string name) => ByName.TryGetValue(name, out var vk) ? vk : null;
    public static string? Name(int vk) => ByCode.GetValueOrDefault(vk);

    /// <summary>Generic Shift/Ctrl/Alt codes from some sources map to the left-hand key.</summary>
    public static int Normalize(int vk) => vk switch { 0x10 => 0xA0, 0x11 => 0xA2, 0x12 => 0xA4, _ => vk };
}

/// <summary>Turns key presses into touches on the device. Holding a key holds the finger down,
/// so tap keys also work for buttons that must be held. Several keys at once = multi-touch.</summary>
public sealed class KeymapEngine(DeviceControl control, KeymapProfile profile, Func<(int W, int H)> screenSize)
{
    readonly HashSet<int> _down = new();
    readonly ConcurrentDictionary<long, (int X, int Y)> _active = new();
    readonly Dictionary<int, List<int>> _byKey = Index(profile);

    static Dictionary<int, List<int>> Index(KeymapProfile p)
    {
        var map = new Dictionary<int, List<int>>();
        for (var i = 0; i < p.Mappings.Count; i++)
            foreach (var k in p.Mappings[i].Keys)
                if (KeyNames.Code(k) is { } vk) (map.TryGetValue(vk, out var l) ? l : map[vk] = new()).Add(i);
        return map;
    }

    public KeymapProfile Profile => profile;
    public bool Handles(int vk) => _byKey.ContainsKey(KeyNames.Normalize(vk));

    public async Task OnKeyAsync(int vk, bool isDown)
    {
        vk = KeyNames.Normalize(vk);
        if (!_byKey.TryGetValue(vk, out var mappings)) return;
        if (isDown && !_down.Add(vk)) return;     // auto-repeat while held
        if (!isDown && !_down.Remove(vk)) return;

        var (w, h) = screenSize();
        foreach (var i in mappings)
        {
            var m = profile.Mappings[i];
            long pointer = 100 + i;
            var cx = (int)Math.Round(m.X * w);
            var cy = (int)Math.Round(m.Y * h);
            if (m.Kind == MappingKind.Tap)
            {
                await Touch(isDown ? DeviceControl.ActionDown : DeviceControl.ActionUp, pointer, cx, cy, w, h);
                continue;
            }
            // Joystick: put a finger on the centre, then move it towards the pressed direction(s).
            int Pressed(string key) => KeyNames.Code(key) is { } c && _down.Contains(c) ? 1 : 0;
            double dx = Pressed(m.Right) - Pressed(m.Left), dy = Pressed(m.Down) - Pressed(m.Up);
            if (dx == 0 && dy == 0)
            {
                if (_active.TryGetValue(pointer, out var last)) await Touch(DeviceControl.ActionUp, pointer, last.X, last.Y, w, h);
                continue;
            }
            var len = Math.Sqrt(dx * dx + dy * dy);
            var reach = m.Radius * Math.Min(w, h);
            var tx = (int)Math.Round(cx + dx / len * reach);
            var ty = (int)Math.Round(cy + dy / len * reach);
            if (!_active.ContainsKey(pointer))
            {
                await Touch(DeviceControl.ActionDown, pointer, cx, cy, w, h);
                // a couple of steps so games register a drag rather than a jump
                await Touch(DeviceControl.ActionMove, pointer, (cx + tx) / 2, (cy + ty) / 2, w, h);
            }
            await Touch(DeviceControl.ActionMove, pointer, tx, ty, w, h);
        }
    }

    async Task Touch(int action, long pointer, int x, int y, int w, int h)
    {
        if (action == DeviceControl.ActionUp) _active.TryRemove(pointer, out _); else _active[pointer] = (x, y);
        await control.TouchAsync(action, pointer, x, y, w, h);
    }

    /// <summary>Lifts every finger, e.g. when the mirror window loses focus.</summary>
    public async Task ReleaseAllAsync()
    {
        var (w, h) = screenSize();
        foreach (var (pointer, pos) in _active.ToArray()) await Touch(DeviceControl.ActionUp, pointer, pos.X, pos.Y, w, h);
        _down.Clear();
    }
}
