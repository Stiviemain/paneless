using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Paneless.App.ViewModels;
using Paneless.Core;

namespace Paneless.App.Assistants;

/// <summary>What a tool call returns: text and/or an image.</summary>
public sealed class ToolResult
{
    public List<JsonObject> Content { get; } = new();
    public bool IsError { get; init; }

    public static ToolResult Text(string text, bool error = false)
    {
        var r = new ToolResult { IsError = error };
        r.Content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return r;
    }

    public static ToolResult Json(JsonNode node) => Text(node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    public JsonObject ToJson() => new() { ["content"] = new JsonArray(Content.Select(c => (JsonNode)c.DeepClone()).ToArray()), ["isError"] = IsError };
}

public enum ConfirmChoice { Deny, Once, For15Minutes }

/// <summary>One line in the AI page's activity list; <paramref name="Client"/> is the assistant that asked.</summary>
public sealed record AssistantActivity(DateTime Time, string Client, string Tool, string Target, bool Ok, string Detail);

/// <summary>The tools AI assistants can use, each needing a permission level on the device it touches.
/// Changes (Control and Manage) can ask in Paneless first.</summary>
public sealed class AssistantTools
{
    public const string Instructions =
        "Paneless manages the user's Android devices (phones, tablets, watches) over Wi-Fi or USB with adb and scrcpy. " +
        "Start with list_devices. Devices are named by their name or id. Some actions need the user's approval in Paneless, " +
        "and each device has a permission level the user set (read, screen, control, manage): if a tool is refused, tell the user " +
        "they can change it on the AI page in Paneless. If a device is locked or its screen is off, use unlock_device (uses the " +
        "code saved in Paneless without revealing it) and lock_device when done. To operate a screen: take a screenshot, then tap " +
        "or swipe using the pixel coordinates of that screenshot. Never guess coordinates without a recent screenshot.";

    sealed record Tool(string Name, string Description, AssistantAccess Level, JsonObject Schema, Func<JsonObject, Task<ToolResult>> Run, bool Changes = false);

    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly List<Tool> _tools = new();
    readonly ConcurrentDictionary<string, DateTime> _grants = new();          // "deviceId|level" -> until
    readonly ConcurrentDictionary<string, (double Sx, double Sy)> _shotScale = new();  // deviceId -> screenshot px to device px
    readonly ConcurrentDictionary<string, (DeviceControl Control, DateTime Used)> _controls = new();

    public AssistantTools(AppServices s, MainViewModel main)
    {
        _s = s; _main = main;
        Register();
    }

    /// <summary>Set by the app: asks the user (title, message).</summary>
    public Func<string, string, Task<ConfirmChoice>>? AskAsync { get; set; }
    /// <summary>The user paused AI access on the AI page.</summary>
    public bool Paused { get; set; }
    public event Action<AssistantActivity>? Activity;

    // ---------------------------------------------------------------- protocol glue

    public JsonArray ListJson() => new(_tools.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name,
        ["description"] = t.Description + $" (Needs the device permission \"{t.Level}\" in Paneless.)",
        ["inputSchema"] = t.Schema.DeepClone(),
    }).ToArray());

    /// <summary>The assistant whose call is running (for the confirmation and the activity list).</summary>
    static readonly AsyncLocal<string> Client = new();

    public async Task<ToolResult> CallAsync(string name, JsonObject args, string client)
    {
        Client.Value = client;
        var tool = _tools.FirstOrDefault(t => t.Name == name);
        if (tool == null) return ToolResult.Text("Unknown tool: " + name, true);
        if (Paused) return ToolResult.Text("The user paused AI access in Paneless. Ask them to resume it on the AI page.", true);
        ToolResult result;
        try { result = await tool.Run(args); }
        catch (RefusedException ex) { result = ToolResult.Text(ex.Message, true); }
        catch (Exception ex) { result = ToolResult.Text("Failed: " + ex.Message, true); }
        var target = args["device"]?.ToString() ?? args["group"]?.ToString() ?? "";
        var detail = result.Content.FirstOrDefault(c => (string?)c["type"] == "text")?["text"]?.ToString() ?? "(image)";
        Log(new AssistantActivity(DateTime.Now, client, name, target, !result.IsError, detail.Length > 160 ? detail[..160] + "…" : detail));
        return result;
    }

    void Log(AssistantActivity a)
    {
        Activity?.Invoke(a);
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            File.AppendAllText(Path.Combine(AppPaths.LogDir, "assistant-activity.log"),
                $"[{a.Time:yyyy-MM-dd HH:mm:ss}] {(a.Ok ? "ok " : "ERR")} {a.Client}: {a.Tool} {a.Target}: {a.Detail.Replace('\n', ' ')}{Environment.NewLine}");
        }
        catch { }
    }

    sealed class RefusedException(string message) : Exception(message);

    // ---------------------------------------------------------------- devices and permissions

    AssistantAccess AccessOf(string id) => _s.EffectiveFor(id).Assistant;

    IReadOnlyList<DeviceRow> Visible() =>
        _main.NetworkRows.Where(r => r.Sn != null && AccessOf(r.GroupId) != AssistantAccess.Off).ToList();

    DeviceRow FindDevice(JsonObject args)
    {
        var q = args["device"]?.ToString()?.Trim() ?? throw new RefusedException("Say which device (its name or id from list_devices).");
        var row = Visible().FirstOrDefault(r => r.GroupId == q || r.Serial == q || string.Equals(r.Name, q, StringComparison.OrdinalIgnoreCase))
                  ?? Visible().FirstOrDefault(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        return row ?? throw new RefusedException($"No device \"{q}\" that you may use is on the network. Use list_devices.");
    }

    /// <summary>The devices of "device" or "group".</summary>
    IReadOnlyList<DeviceRow> FindTargets(JsonObject args)
    {
        if (args["group"]?.ToString() is { Length: > 0 } g)
        {
            var group = _s.Groups.Find(g) ?? throw new RefusedException($"No group \"{g}\". Use list_groups.");
            var rows = Visible().Where(r => group.Members.Contains(r.GroupId) && r.CanMirror).ToList();
            return rows.Count > 0 ? rows : throw new RefusedException($"No device of \"{group.Name}\" that you may use is on the network.");
        }
        return [FindDevice(args)];
    }

    /// <summary>Checks the device's permission level and, for changes, the user's approval.</summary>
    async Task Allow(Tool tool, IReadOnlyList<DeviceRow> rows, string what)
    {
        foreach (var r in rows)
            if (AccessOf(r.GroupId) < tool.Level)
                throw new RefusedException($"The user allows only \"{AccessOf(r.GroupId)}\" on {r.Name}; {tool.Name} needs \"{tool.Level}\". They can change it on the AI page in Paneless.");
        if (!tool.Changes || !_s.Data.Settings.AssistantConfirm) return;
        var now = DateTime.Now;
        var missing = rows.Where(r => !(_grants.TryGetValue(r.GroupId + "|" + tool.Level, out var until) && until > now)).ToList();
        if (missing.Count == 0) return;
        if (AskAsync == null) throw new RefusedException("Paneless couldn't ask the user.");
        var names = string.Join(", ", rows.Select(r => r.Name));
        var who = Client.Value ?? AssistantApps.Friendly(null, null);
        var choice = await AskAsync(Localization.Loc.F("{0} wants to do something", who), Localization.Loc.F("{0} wants to: {1}\nOn: {2}", who, what, names));
        if (choice == ConfirmChoice.Deny) throw new RefusedException("The user said no in Paneless.");
        if (choice == ConfirmChoice.For15Minutes)
            foreach (var r in rows) _grants[r.GroupId + "|" + tool.Level] = now.AddMinutes(15);
    }

    async Task<string> SerialOf(DeviceRow r)
    {
        if (_s.Demo) return r.Serial ?? r.Key;
        return await _s.Directory!.ConnectAsync(r) ?? throw new RefusedException($"Couldn't connect to {r.Name}.");
    }

    async Task<DeviceControl> ControlOf(string serial)
    {
        foreach (var (k, v) in _controls)   // close idle connections
            if (DateTime.Now - v.Used > TimeSpan.FromMinutes(3) && _controls.TryRemove(k, out var old)) _ = old.Control.DisposeAsync().AsTask();
        if (_controls.TryGetValue(serial, out var c) && c.Control.IsConnected) { _controls[serial] = (c.Control, DateTime.Now); return c.Control; }
        var server = await ScrcpyServerLocator.FindAsync(_s.Tools!) ?? throw new RefusedException("scrcpy-server wasn't found.");
        var control = await DeviceControl.StartAsync(_s.Adb!, server, serial);
        _controls[serial] = (control, DateTime.Now);
        return control;
    }

    // ---------------------------------------------------------------- schema helpers

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var p = new JsonObject();
        foreach (var x in props) p[x.Name] = new JsonObject { ["type"] = x.Type, ["description"] = x.Description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = p,
            ["required"] = new JsonArray(props.Where(x => x.Required).Select(x => (JsonNode)x.Name).ToArray()),
        };
    }

    static readonly (string, string, string, bool) DeviceArg = ("device", "string", "Device name or id (from list_devices).", true);
    static readonly (string, string, string, bool) DeviceOpt = ("device", "string", "Device name or id. Give this or group.", false);
    static readonly (string, string, string, bool) GroupOpt = ("group", "string", "Group name: runs on all of its devices on the network.", false);

    static string Str(JsonObject a, string key) => a[key]?.ToString() ?? throw new RefusedException($"Missing \"{key}\".");
    static double Num(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d
        : double.TryParse(a[key]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e
        : throw new RefusedException($"Missing number \"{key}\".");

    static ToolResult Results(IReadOnlyList<DeviceActions.DeviceResult> results) =>
        ToolResult.Json(new JsonArray(results.Select(r => (JsonNode)new JsonObject { ["device"] = r.Device, ["ok"] = r.Ok, ["output"] = r.Detail }).ToArray()));

    void Add(string name, string description, AssistantAccess level, JsonObject schema, Func<Tool, JsonObject, Task<ToolResult>> run, bool changes = false)
    {
        Tool? self = null;
        self = new Tool(name, description, level, schema, a => run(self!, a), changes);
        _tools.Add(self);
    }

    // ---------------------------------------------------------------- the tools

    void Register()
    {
        Add("list_devices", "Lists the user's Android devices you may see: name, id, model, status, battery (percent, power), group, and what you may do on each (allowed). Devices that still need pairing can't be used until the user pairs them in Paneless.",
            AssistantAccess.Read, Schema(), (_, _) =>
            {
                var arr = new JsonArray();
                foreach (var r in _main.NetworkRows.Where(r => r.Sn != null && AccessOf(r.GroupId) != AssistantAccess.Off).OrderBy(r => r.Name, NaturalComparer.Instance))
                {
                    var battery = r.Serial != null ? _main.BatteryOf(r.Serial) : null;
                    arr.Add(new JsonObject
                    {
                        ["name"] = r.Name, ["id"] = r.GroupId,
                        ["model"] = r.Model.Length > 0 ? r.Model : null, ["type"] = r.Type.Length > 0 ? r.Type : null,
                        ["status"] = r.State.ToString(),
                        ["battery_percent"] = battery?.Level,
                        ["power"] = battery is not { } b ? null : b.Full ? "Full, plugged in" : b.Charging ? "Charging" : "On battery",
                        ["group"] = _s.Groups.GroupOf(r.GroupId)?.Name,
                        // An unpaired device can't run anything yet, whatever level the user set.
                        ["allowed"] = r.NeedsPairing || r.State == DeviceState.Unauthorized ? "Nothing yet: the user has to pair it in Paneless first" : AccessOf(r.GroupId).ToString(),
                    });
                }
                return Task.FromResult(ToolResult.Json(arr));
            });

        Add("list_groups", "Lists the device groups and their devices.", AssistantAccess.Read, Schema(), (_, _) =>
            Task.FromResult(ToolResult.Json(new JsonArray(_s.Groups.Groups.Select(g => (JsonNode)new JsonObject
            {
                ["name"] = g.Name,
                ["devices"] = new JsonArray(g.Members.Where(m => AccessOf(m) != AssistantAccess.Off)
                    .Select(m => (JsonNode)(_main.NetworkRows.FirstOrDefault(r => r.GroupId == m)?.Name ?? _s.KnownFor(m)?.Name ?? m)).ToArray()),
            }).ToArray()))));

        Add("device_info", "Facts about a device: manufacturer, model, Android version, security patch, battery (level, health, temperature), storage, RAM, screen size, IP address, uptime.",
            AssistantAccess.Read, Schema(DeviceArg), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("read device facts"));
                var inv = _s.Demo ? DemoData.Inventory(r) : await _s.Adb!.GetInventoryAsync(await SerialOf(r), r.Name);
                if (inv == null) return ToolResult.Text("Couldn't read the device.", true);
                _s.Inventory[r.GroupId] = inv;
                return ToolResult.Json(AssistantFacts.Inventory(inv));
            });

        Add("list_apps", "Lists the installed app packages of a device (only apps the user installed, unless include_system is true).",
            AssistantAccess.Read, Schema(DeviceArg, ("include_system", "boolean", "Also list system apps.", false)), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("list apps"));
                var system = a["include_system"]?.GetValue<bool>() == true;
                var apps = await _s.Adb!.GetPackagesAsync(await SerialOf(r), system);
                return ToolResult.Json(new JsonArray(apps.Select(p => (JsonNode)(p.Disabled ? p.Name + " (disabled)" : p.Name)).ToArray()));
            });

        Add("read_log", "Reads the recent log (logcat) of a device. Optionally only lines containing a text, of a minimum level (V, D, I, W, E), or of one app package.",
            AssistantAccess.Read, Schema(DeviceArg, ("lines", "integer", "How many recent lines (default 200, max 2000).", false),
                ("contains", "string", "Only lines containing this text.", false), ("min_level", "string", "V, D, I, W or E.", false),
                ("package", "string", "Only lines of this app's process.", false), ("crashes_only", "boolean", "Only the crash buffer (app crashes).", false)), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("read the log"));
                var serial = await SerialOf(r);
                var n = Math.Clamp((int)(a["lines"] is { } l ? Num(a, "lines") : 200), 10, 2000);
                var crash = a["crashes_only"]?.GetValue<bool>() == true;
                var res = await _s.Adb!.ShellAsync(serial, $"logcat -d -v threadtime{(crash ? " -b crash" : "")} -t {(a["contains"] != null || a["package"] != null ? 5000 : n)}", TimeSpan.FromSeconds(30));
                var lines = res.Output.Split('\n').Select(x => x.TrimEnd('\r')).Select(LogcatSession.Parse);
                if (a["min_level"]?.ToString() is { Length: > 0 } ml)
                {
                    var rank = "VDIWEF".IndexOf(char.ToUpperInvariant(ml[0]));
                    lines = lines.Where(x => "VDIWEF".IndexOf(x.Level) >= rank);
                }
                if (a["package"]?.ToString() is { Length: > 0 } pkg)
                {
                    if (await LogcatSession.PidOfAsync(_s.Adb, serial, pkg) is { } pid) lines = lines.Where(x => x.Pid == pid);
                    else lines = lines.Where(x => x.Raw.Contains(pkg, StringComparison.OrdinalIgnoreCase));
                }
                if (a["contains"]?.ToString() is { Length: > 0 } c) lines = lines.Where(x => x.Raw.Contains(c, StringComparison.OrdinalIgnoreCase));
                var text = string.Join('\n', lines.Select(x => x.Raw).TakeLast(n));
                return ToolResult.Text(text.Length == 0 ? "(no matching lines)" : text);
            });

        Add("screenshot", "Takes a screenshot of a device's screen. Use its pixel coordinates for tap and swipe.",
            AssistantAccess.Screen, Schema(DeviceArg), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("see the screen"));
                var png = await _s.Adb!.ScreencapAsync(await SerialOf(r)) ?? throw new RefusedException("The device didn't send a picture (screen off?).");
                using var src = new MemoryStream(png);
                using var full = new Bitmap(src);
                var (w, h) = (full.PixelSize.Width, full.PixelSize.Height);
                var scale = Math.Min(1.0, 1280.0 / Math.Max(w, h));
                byte[] outPng = png;
                int sw = w, sh = h;
                if (scale < 1)
                {
                    src.Position = 0;
                    using var small = Bitmap.DecodeToWidth(src, (int)Math.Round(w * scale));
                    using var ms = new MemoryStream();
                    small.Save(ms, new PngBitmapEncoderOptions());
                    outPng = ms.ToArray();
                    (sw, sh) = (small.PixelSize.Width, small.PixelSize.Height);
                }
                _shotScale[r.GroupId] = ((double)w / sw, (double)h / sh);
                var result = new ToolResult();
                result.Content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(outPng), ["mimeType"] = "image/png" });
                result.Content.Add(new JsonObject { ["type"] = "text", ["text"] = $"Screenshot of {r.Name}: {sw}x{sh} pixels (screen {w}x{h}). tap/swipe take coordinates in this screenshot." });
                return result;
            });

        Add("tap", "Taps the screen at x,y (pixel coordinates of the latest screenshot of this device).",
            AssistantAccess.Control, Schema(DeviceArg, ("x", "number", "X in screenshot pixels.", true), ("y", "number", "Y in screenshot pixels.", true)), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("tap the screen"));
                var (x, y, w, h) = await Map(r, Num(a, "x"), Num(a, "y"));
                var c = await ControlOf(await SerialOf(r));
                await c.TouchAsync(DeviceControl.ActionDown, 77, x, y, w, h);
                await Task.Delay(60);
                await c.TouchAsync(DeviceControl.ActionUp, 77, x, y, w, h);
                return ToolResult.Text($"Tapped {r.Name} at {x},{y}. Take a screenshot to see the result.");
            }, changes: true);

        Add("swipe", "Swipes from x1,y1 to x2,y2 (screenshot pixels), e.g. to scroll. duration_ms default 300.",
            AssistantAccess.Control, Schema(DeviceArg, ("x1", "number", "Start X.", true), ("y1", "number", "Start Y.", true), ("x2", "number", "End X.", true),
                ("y2", "number", "End Y.", true), ("duration_ms", "integer", "How long the swipe takes.", false)), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("swipe on the screen"));
                var (x1, y1, w, h) = await Map(r, Num(a, "x1"), Num(a, "y1"));
                var (x2, y2, _, _) = await Map(r, Num(a, "x2"), Num(a, "y2"));
                var ms = Math.Clamp((int)(a["duration_ms"] != null ? Num(a, "duration_ms") : 300), 50, 3000);
                var c = await ControlOf(await SerialOf(r));
                const int steps = 12;
                await c.TouchAsync(DeviceControl.ActionDown, 78, x1, y1, w, h);
                for (var i = 1; i <= steps; i++)
                {
                    await Task.Delay(ms / steps);
                    await c.TouchAsync(DeviceControl.ActionMove, 78, x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps, w, h);
                }
                await c.TouchAsync(DeviceControl.ActionUp, 78, x2, y2, w, h);
                return ToolResult.Text($"Swiped on {r.Name}.");
            }, changes: true);

        Add("type_text", "Types text into the focused field of a device, like a keyboard.",
            AssistantAccess.Control, Schema(DeviceArg, ("text", "string", "The text to type.", true)), async (t, a) =>
            {
                var r = FindDevice(a);
                var text = Str(a, "text");
                await Allow(t, [r], Localization.Loc.F("type \"{0}\"", text.Length > 60 ? text[..60] + "…" : text));
                await (await ControlOf(await SerialOf(r))).TypeTextAsync(text);
                return ToolResult.Text($"Typed {text.Length} characters on {r.Name}.");
            }, changes: true);

        Add("press_key", "Presses a button: back, home, recents, enter, delete, tab, escape, up, down, left, right, volume_up, volume_down, power.",
            AssistantAccess.Control, Schema(DeviceArg, ("key", "string", "The button.", true)), async (t, a) =>
            {
                var r = FindDevice(a);
                var key = Str(a, "key").ToLowerInvariant();
                int code = key switch
                {
                    "back" => 4, "home" => 3, "recents" or "app_switch" => 187, "enter" => 66, "delete" or "backspace" => 67, "tab" => 61,
                    "escape" => 111, "up" => 19, "down" => 20, "left" => 21, "right" => 22, "volume_up" => 24, "volume_down" => 25, "power" => 26,
                    _ => throw new RefusedException("Unknown key: " + key),
                };
                await Allow(t, [r], Localization.Loc.F("press {0}", key));
                await (await ControlOf(await SerialOf(r))).PressKeyAsync(code);
                return ToolResult.Text($"Pressed {key} on {r.Name}.");
            }, changes: true);

        Add("open_app", "Opens an app on a device by its package name (see list_apps).",
            AssistantAccess.Control, Schema(DeviceArg, ("package", "string", "e.g. com.android.settings", true)), async (t, a) =>
            {
                var r = FindDevice(a);
                var pkg = Str(a, "package");
                await Allow(t, [r], Localization.Loc.F("open {0}", pkg));
                var res = await _s.Adb!.LaunchAsync(await SerialOf(r), pkg);
                return res.Output.Contains("No activities found") ? ToolResult.Text($"{pkg} has no screen to open.", true) : ToolResult.Text($"Opened {pkg} on {r.Name}.");
            }, changes: true);

        Add("mirror_device", "Opens the device's screen in a mirror window on the user's PC.",
            AssistantAccess.Control, Schema(DeviceArg), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("open a mirror window"));
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _main.MirrorByIdAsync(r.GroupId));
                return ToolResult.Text($"Mirroring {r.Name}.");
            }, changes: true);

        Add("unlock_device", "Unlocks a locked device (or wakes its screen if already unlocked) using the unlock code saved in Paneless. The code is never revealed to you.",
            AssistantAccess.Control, Schema(DeviceArg), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("unlock the device"));
                if (_s.Demo) return ToolResult.Text($"Demo mode: would unlock {r.Name}.");
                var serial = await SerialOf(r);
                if (_s.Unlock == null || !_s.Unlock.HasCode(r.Sn))
                {
                    var st = _s.Unlock != null ? await _s.Unlock.GetLockInfoAsync(serial) : null;
                    if (st?.Locked == false)
                    {
                        await _s.Adb!.ShellAsync(serial, "input keyevent KEYCODE_WAKEUP", TimeSpan.FromSeconds(5));
                        return ToolResult.Text($"{r.Name} is already unlocked (screen woken).");
                    }
                    return ToolResult.Text($"No unlock code is saved for {r.Name} in Paneless. Ask the user to unlock it manually or set up auto-unlock in Paneless.", true);
                }
                var res = await _s.Unlock.UnlockAsync(serial, r.Sn!, r.Name);
                return ToolResult.Text(res.Message, !res.Ok);
            }, changes: true);

        Add("lock_device", "Turns the device's screen off and puts it to sleep (locks it).",
            AssistantAccess.Control, Schema(DeviceArg), async (t, a) =>
            {
                var r = FindDevice(a);
                await Allow(t, [r], Localization.Loc.T("lock the device"));
                if (_s.Demo) return ToolResult.Text($"Demo mode: would lock {r.Name}.");
                var res = await _s.Adb!.ShellAsync(await SerialOf(r), "input keyevent KEYCODE_SLEEP", TimeSpan.FromSeconds(5));
                return res.Ok ? ToolResult.Text($"Locked {r.Name}.") : ToolResult.Text($"Couldn't lock {r.Name}: {res.LastLine}", true);
            }, changes: true);

        Add("install_app", "Installs (or updates) an APK file from the user's PC on a device or a whole group.",
            AssistantAccess.Manage, Schema(DeviceOpt, GroupOpt, ("apk_path", "string", "Full path of the .apk file on the PC.", true)), async (t, a) =>
            {
                var path = Str(a, "apk_path");
                if (!File.Exists(path)) return ToolResult.Text("That file doesn't exist on the PC.", true);
                var rows = FindTargets(a);
                await Allow(t, rows, Localization.Loc.F("install {0}", Path.GetFileName(path)));
                return Results(await _main.Actions.OnEachAsync(rows, async (r, serial) =>
                {
                    var res = await _s.Adb!.InstallAsync(serial, path);
                    return new DeviceActions.DeviceResult(r.Name, res.Ok, res.LastLine);
                }));
            }, changes: true);

        Add("uninstall_app", "Uninstalls an app (by package name) from a device or a whole group.",
            AssistantAccess.Manage, Schema(DeviceOpt, GroupOpt, ("package", "string", "Package name.", true)), async (t, a) =>
            {
                var pkg = Str(a, "package");
                var rows = FindTargets(a);
                await Allow(t, rows, Localization.Loc.F("uninstall {0}", pkg));
                return Results(await _main.Actions.ShellAsync("pm uninstall --user 0 " + ScriptText.ShellQuote(pkg), rows));
            }, changes: true);

        Add("copy_file", "Copies a file from the user's PC to a device or group (default folder: the device's drop folder, usually /sdcard/Download/).",
            AssistantAccess.Manage, Schema(DeviceOpt, GroupOpt, ("path", "string", "Full path of the file on the PC.", true), ("folder", "string", "Folder on the device.", false)), async (t, a) =>
            {
                var path = Str(a, "path");
                if (!File.Exists(path)) return ToolResult.Text("That file doesn't exist on the PC.", true);
                var rows = FindTargets(a);
                await Allow(t, rows, Localization.Loc.F("copy {0} to the device", Path.GetFileName(path)));
                return Results(await _main.Actions.OnEachAsync(rows, async (r, serial) =>
                {
                    var res = await _s.Adb!.PushAsync(serial, path, a["folder"]?.ToString() is { Length: > 0 } f ? f : _s.EffectiveFor(r.GroupId).PushTarget);
                    return new DeviceActions.DeviceResult(r.Name, res.Ok, res.LastLine);
                }));
            }, changes: true);

        Add("run_command", "Runs a shell command on a device or group (like \"adb shell <command>\") and returns its output.",
            AssistantAccess.Manage, Schema(DeviceOpt, GroupOpt, ("command", "string", "The command, without \"adb shell\".", true)), async (t, a) =>
            {
                var cmd = Str(a, "command");
                var rows = FindTargets(a);
                await Allow(t, rows, Localization.Loc.F("run the command: {0}", cmd));
                return Results(await _main.Actions.ShellAsync(cmd, rows));
            }, changes: true);

        Add("list_scripts", "Lists the saved scripts (the user's and the built-in ones) with their descriptions and fields.",
            AssistantAccess.Read, Schema(), (_, _) => Task.FromResult(ToolResult.Json(new JsonArray(_s.AllScripts().Select(sc => (JsonNode)new JsonObject
            {
                ["name"] = sc.Name, ["category"] = sc.Category, ["description"] = sc.Description,
                ["fields"] = new JsonArray(ScriptText.Placeholders(sc.Commands).Select(p => (JsonNode)p.Name).ToArray()),
            }).ToArray()))));

        Add("run_script", "Runs a saved script (by name, see list_scripts) on a device or group. values: the script's fields.",
            AssistantAccess.Manage, Schema(DeviceOpt, GroupOpt, ("script", "string", "Script name.", true), ("values", "object", "Values for the script's fields.", false)), async (t, a) =>
            {
                var name = Str(a, "script");
                var script = _s.AllScripts().FirstOrDefault(sc => string.Equals(sc.Name, name, StringComparison.OrdinalIgnoreCase))
                             ?? throw new RefusedException($"No script \"{name}\". Use list_scripts.");
                var values = (a["values"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "") ?? new();
                var rows = FindTargets(a);
                await Allow(t, rows, Localization.Loc.F("run the script \"{0}\"", script.Name));
                return Results(await _main.Actions.RunScriptAsync(script, rows, values));
            }, changes: true);

        Add("list_recipes", "Lists the provisioning recipes (sets of apps, files, scripts and commands).", AssistantAccess.Read, Schema(), (_, _) =>
            Task.FromResult(ToolResult.Json(new JsonArray(_s.Recipes.All.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name, ["description"] = r.Description,
                ["steps"] = new JsonArray(r.Steps.Select(st => (JsonNode)RecipeRunner.Describe(st, _s.FindScript)).ToArray()),
            }).ToArray()))));

        Add("run_recipe", "Runs a provisioning recipe (by name) on a device or group.",
            AssistantAccess.Manage, Schema(DeviceOpt, GroupOpt, ("recipe", "string", "Recipe name.", true)), async (t, a) =>
            {
                var recipe = _s.Recipes.Find(Str(a, "recipe")) ?? throw new RefusedException("No such recipe. Use list_recipes.");
                var rows = FindTargets(a);
                await Allow(t, rows, Localization.Loc.F("run the recipe \"{0}\"", recipe.Name));
                return Results(await _main.Actions.RunRecipeAsync(recipe, rows));
            }, changes: true);

        Add("reboot", "Restarts a device or every device of a group.",
            AssistantAccess.Manage, Schema(DeviceOpt, GroupOpt), async (t, a) =>
            {
                var rows = FindTargets(a);
                await Allow(t, rows, Localization.Loc.T("restart the device"));
                return Results(await _main.Actions.RebootAsync(rows));
            }, changes: true);
    }

    /// <summary>Screenshot pixels to device pixels (the screen's current orientation).</summary>
    async Task<(int X, int Y, int W, int H)> Map(DeviceRow r, double x, double y)
    {
        var size = await _s.Adb!.GetDisplaySizeAsync(await SerialOf(r)) ?? throw new RefusedException("Couldn't read the screen size.");
        var (sx, sy) = _shotScale.TryGetValue(r.GroupId, out var s) ? s : (1.0, 1.0);
        var px = (int)Math.Clamp(Math.Round(x * sx), 0, size.W - 1);
        var py = (int)Math.Clamp(Math.Round(y * sy), 0, size.H - 1);
        return (px, py, size.W, size.H);
    }
}
