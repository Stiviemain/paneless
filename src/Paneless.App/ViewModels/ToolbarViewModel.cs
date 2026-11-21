using Paneless.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Platform;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>The toolbar next to one mirror window. Uses the fast control connection when it's up,
/// and falls back to plain adb commands for the basic buttons when it isn't.</summary>
public sealed partial class ToolbarViewModel : ObservableObject, IAsyncDisposable
{
    readonly AppServices _s;
    readonly MirrorSession _session;
    readonly DeviceRow _row;
    readonly Task<DeviceControl?> _control;
    readonly CancellationTokenSource _cts = new();
    string? _controlError;
    (int W, int H) _size;
    KeymapEngine? _engine;
    nint _engineWindow;

    public ToolbarViewModel(AppServices services, MirrorSession session, DeviceRow row)
    {
        _s = services;
        _session = session;
        _row = row;
        IsSecondary = session.Kind is SessionKind.Screen or SessionKind.App;
        _control = StartControlAsync();
        if (!IsSecondary) _ = WatchScreenSizeAsync();
    }

    /// <summary>A toolbar for a second screen or an app window (a virtual display of the device): its buttons act
    /// on that display, and the ones only meaningful for the device's own screen are hidden.</summary>
    public bool IsSecondary { get; }
    public bool IsPrimary => !IsSecondary;
    /// <summary>Set by the main window: opens a second screen of this device.</summary>
    public Action? OpenSecondScreen { get; set; }

    [RelayCommand]
    void SecondScreen()
    {
        OpenSecondScreen?.Invoke();
        Hint = Loc.T("Opening a second screen (it gets a toolbar of its own).");
    }

    /// <summary>The virtual display of a second screen or app window, once scrcpy has created it.</summary>
    async Task<int?> DisplayIdAsync()
    {
        for (var i = 0; i < 150 && _session.DisplayId == null && _session.IsRunning; i++)
            try { await Task.Delay(100, _cts.Token); } catch { return null; }
        return _session.DisplayId;
    }

    public string DeviceName => _row.Name;
    public string Serial => _session.Serial;
    public DeviceRow Row => _row;
    public bool KeymapSupported => OperatingSystem.IsWindows();

    /// <summary>Set by the window: the mirror's native window handle (Windows), for key mapping.</summary>
    public Func<nint>? MirrorWindow { get; set; }
    public Action? OpenKeymapEditor { get; set; }
    public Action? OpenScripts { get; set; }

    [ObservableProperty] public partial bool IsKeymapOn { get; set; }
    [ObservableProperty] public partial bool IsScreenOff { get; set; }
    [ObservableProperty] public partial string Hint { get; set; } = "";

    async Task<DeviceControl?> StartControlAsync()
    {
        if (_s.Adb == null || _s.Tools == null) return null;
        try
        {
            var server = await ScrcpyServerLocator.FindAsync(_s.Tools);
            if (server == null) { _controlError = Loc.T("scrcpy-server wasn't found next to scrcpy."); return null; }
            var display = 0;
            if (IsSecondary)
            {
                if (await DisplayIdAsync() is not { } id) { _controlError = Loc.T("the second screen's number didn't show up in scrcpy's log."); return null; }
                display = id;
            }
            return await DeviceControl.StartAsync(_s.Adb, server, _session.Serial, display);
        }
        catch (Exception ex) { _controlError = ex.Message; return null; }
    }

    async Task<DeviceControl?> ControlAsync()
    {
        var c = await _control;
        return c is { IsConnected: true } ? c : null;
    }

    async Task WatchScreenSizeAsync()
    {
        // Key positions are fractions of the screen, so the current size (and orientation) is needed.
        while (!_cts.IsCancellationRequested && _s.Adb != null)
        {
            try { if (await _s.Adb.GetDisplaySizeAsync(_session.Serial) is { } s) _size = s; } catch { }
            try { await Task.Delay(IsKeymapOn ? 1500 : 4000, _cts.Token); } catch { return; }
        }
    }

    Task Shell(string cmd) => _s.Adb?.ShellAsync(_session.Serial, cmd) ?? Task.CompletedTask;

    async Task Key(int keycode)
    {
        if (await ControlAsync() is { } c) await c.PressKeyAsync(keycode);
        else if (IsSecondary) { if (_session.DisplayId is { } id) await Shell($"input -d {id} keyevent {keycode}"); }
        else await Shell($"input keyevent {keycode}");
        if (OperatingSystem.IsWindows() && _group is GroupControl g) await g.PressKeyAsync(keycode);
    }

    // ---------------------------------------------------------------- record, type, camera, group control

    /// <summary>Set by the window: the computer's clipboard text.</summary>
    public Func<Task<string?>>? ReadClipboardAsync { get; set; }
    /// <summary>Set by the main window: the other connected devices of this device's group, and the group's name.</summary>
    public Func<(string? Group, IReadOnlyList<DeviceRow> Others)>? GroupPeers { get; set; }
    public Action? OpenCamera { get; set; }

    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial bool IsGroupControl { get; set; }
    public bool GroupControlAvailable => OperatingSystem.IsWindows() && IsPrimary;

    MirrorSession? _recording;
    object? _group;   // GroupControl (Windows only)

    [RelayCommand]
    void Record()
    {
        if (_s.Mirrors == null) return;
        if (_recording is { IsRunning: true } rec)
        {
            MirrorManager.Stop(rec);
            IsRecording = false;
            _recording = null;
            Hint = Loc.F("Recording saved: {0}", rec.OutputFile);
            if (rec.OutputFile != null) Reveal(rec.OutputFile);
            return;
        }
        Directory.CreateDirectory(ScriptRunner.OutputDir);
        var file = Path.Combine(ScriptRunner.OutputDir,
            string.Concat(_row.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mkv");
        int? display = null;
        if (IsSecondary)
        {
            if (_session.DisplayId is not { } id) { Hint = Loc.T("This screen isn't ready yet."); return; }
            display = id;
        }
        _recording = _s.Mirrors.StartRecording(_session.Serial, _row.Name, file, _s.EffectiveFor(_row.GroupId).ToMirrorOptions(), display);
        IsRecording = true;
        Hint = Loc.T("Recording the screen (with sound if device audio is on). Click again to stop.");
        _ = WatchRecordingAsync(_recording);
    }

    async Task WatchRecordingAsync(MirrorSession rec)
    {
        try { await rec.Process.WaitForExitAsync(_cts.Token); } catch { return; }
        if (_recording != rec) return;   // stopped by the button
        _recording = null;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsRecording = false;
            var size = rec.OutputFile != null && File.Exists(rec.OutputFile) ? new FileInfo(rec.OutputFile).Length : 0;
            Hint = size > 0 ? Loc.F("Recording stopped: {0}", rec.OutputFile) : Loc.T("The recording couldn't start: ") + LastLogLine(rec.LogPath);
        });
    }

    static string LastLogLine(string log)
    {
        try { return File.ReadLines(log).LastOrDefault(l => l.Contains("ERROR"))?.Replace("ERROR:", "").Trim() ?? "see " + log; }
        catch { return ""; }
    }

    [RelayCommand]
    async Task TypeClipboard()
    {
        var text = ReadClipboardAsync != null ? await ReadClipboardAsync() : null;
        if (string.IsNullOrEmpty(text)) { Hint = Loc.T("The clipboard has no text."); return; }
        if (await ControlAsync() is not { } c) { Hint = Loc.T("Typing needs the control connection: ") + (_controlError ?? Loc.T("still starting")); return; }
        await c.TypeTextAsync(text);
        Hint = Loc.Count(text.Length, "Typed {0} character from the clipboard.", "Typed {0} characters from the clipboard.");
    }

    [RelayCommand]
    void Camera() => OpenCamera?.Invoke();

    [RelayCommand]
    async Task ToggleGroupControl()
    {
        if (!OperatingSystem.IsWindows()) { Hint = Loc.T("Group control works on Windows for now."); return; }
        if (_group is GroupControl running)
        {
            _group = null;
            IsGroupControl = false;
            await running.DisposeAsync();
            Hint = Loc.T("Group control off.");
            return;
        }
        var (groupName, others) = GroupPeers?.Invoke() ?? (null, []);
        if (groupName == null) { Hint = Loc.T("Put this device in a group first (Groups… in the main window)."); return; }
        if (others.Count == 0) { Hint = Loc.F("No other device of {0} is connected.", groupName); return; }
        var window = MirrorWindow?.Invoke() ?? 0;
        if (window == 0) { Hint = Loc.T("The mirror window isn't open yet."); return; }
        Hint = Loc.F("Connecting to {0} of {1}…", Loc.Count(others.Count, "{0} device", "{0} devices"), groupName);
        var (gc, failed) = await GroupControl.StartAsync(_s, window, () => _size, others);
        if (gc == null) { Hint = Loc.T("Couldn't connect to the other devices") + (failed.Count > 0 ? ": " + string.Join(", ", failed) : "."); return; }
        _group = gc;
        IsGroupControl = true;
        Hint = Loc.F("Group control on: what you do here also happens on {0}.", string.Join(", ", gc.Names)) + (failed.Count > 0 ? Loc.F(" Not reached: {0}.", string.Join(", ", failed)) : "");
    }

    // ---------------------------------------------------------------- buttons

    /// <summary>The device's rotation mode before the first Rotate ("free" = auto-rotate, or "lock N"); restored on close.</summary>
    string? _rotationBefore;

    [RelayCommand]
    async Task Rotate()
    {
        if (_s.Adb == null) return;
        // scrcpy's own rotate command turns auto-rotate straight back on, so the device snaps back to how it's held.
        // Lock the orientation instead (Android 12+: wm user-rotation), and give auto-rotate back when the mirror closes.
        if (await _s.Adb.GetDisplaySizeAsync(_session.Serial) is { } now) _size = now;
        var toLandscape = _size.H >= _size.W;
        var mode = await _s.Adb.ShellAsync(_session.Serial, "wm user-rotation");
        var modern = mode.Ok && (mode.Output.StartsWith("free") || mode.Output.StartsWith("lock"));
        _rotationBefore ??= modern ? mode.Output.Trim() : "legacy:" + (await _s.Adb.ShellAsync(_session.Serial, "settings get system accelerometer_rotation")).Output.Trim();
        if (modern) await Shell($"wm user-rotation lock {(toLandscape ? 1 : 0)}");
        else await Shell($"settings put system accelerometer_rotation 0; settings put system user_rotation {(toLandscape ? 1 : 0)}");

        await Task.Delay(900);
        if (await _s.Adb.GetDisplaySizeAsync(_session.Serial) is { } s) _size = s;
        var turned = (_size.W > _size.H) == toLandscape;
        Hint = turned
            ? Loc.F("Turned to {0} and locked there. Auto-rotate comes back when you close this mirror.", (toLandscape ? Loc.T("landscape") : Loc.T("portrait")))
            : Loc.F("Locked to {0}, but the app in front only supports {1} (e.g. the Pixel home screen). Open an app that rotates.", (toLandscape ? Loc.T("landscape") : Loc.T("portrait")), (toLandscape ? Loc.T("portrait") : Loc.T("landscape")));
        CheckOrientation();
    }

    async Task RestoreRotationAsync()
    {
        if (_rotationBefore == null || _s.Adb == null) return;
        var cmd = _rotationBefore.StartsWith("legacy:")
            ? $"settings put system accelerometer_rotation {(_rotationBefore.EndsWith("1") ? 1 : 0)}"
            : "wm user-rotation " + _rotationBefore;
        try { await _s.Adb.ShellAsync(_session.Serial, cmd); } catch { }
    }

    [RelayCommand] Task Back() => Key(DeviceControl.KeyBack);
    [RelayCommand] Task Home() => Key(DeviceControl.KeyHome);
    [RelayCommand] Task Recents() => Key(DeviceControl.KeyAppSwitch);
    [RelayCommand] Task VolumeUp() => Key(DeviceControl.KeyVolumeUp);
    [RelayCommand] Task VolumeDown() => Key(DeviceControl.KeyVolumeDown);
    [RelayCommand] Task Power() => Key(DeviceControl.KeyPower);

    [RelayCommand]
    async Task ToggleScreenOff()
    {
        if (await ControlAsync() is not { } c) { Hint = Loc.T("Needs the control connection: ") + (_controlError ?? Loc.T("still starting")); return; }
        IsScreenOff = !IsScreenOff;
        await c.SetDisplayPowerAsync(!IsScreenOff);
        Hint = IsScreenOff ? Loc.T("Device screen is off; mirroring continues.") : Loc.T("Device screen is on.");
    }

    [RelayCommand]
    async Task Notifications()
    {
        if (await ControlAsync() is { } c) await c.ExpandNotificationsAsync();
        else await Shell("cmd statusbar expand-notifications");
    }

    /// <summary>The shell command opens the full quick settings on new Pixel shades (Android 17), where
    /// scrcpy's control message only pulls down the normal notification shade.</summary>
    [RelayCommand]
    Task QuickSettings() => Shell("cmd statusbar expand-settings");

    [RelayCommand]
    async Task Screenshot()
    {
        if (_s.Adb == null) return;
        Hint = Loc.T("Taking a screenshot…");
        var png = await _s.Adb.ScreencapAsync(_session.Serial);
        if (png == null) { Hint = Loc.T("Screenshot failed: the device didn't send an image."); return; }
        try
        {
            Directory.CreateDirectory(ScriptRunner.OutputDir);
            var name = string.Concat(_row.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            var file = Path.Combine(ScriptRunner.OutputDir, name);
            await File.WriteAllBytesAsync(file, png);
            Hint = Loc.F("Screenshot saved: {0}", file);
            Reveal(file);
        }
        catch (Exception ex) { Hint = Loc.T("Couldn't save the screenshot: ") + ex.Message; }
    }

    /// <summary>Shows the saved file in the file manager.</summary>
    static void Reveal(string file)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("open", ["-R", file]);
            else System.Diagnostics.Process.Start("xdg-open", Path.GetDirectoryName(file)!);
        }
        catch { /* the status line still says where it is */ }
    }

    [RelayCommand] void Scripts() => OpenScripts?.Invoke();
    [RelayCommand] void EditKeymap() => OpenKeymapEditor?.Invoke();

    // ---------------------------------------------------------------- key mapping

    [RelayCommand]
    async Task ToggleKeymap()
    {
        if (!OperatingSystem.IsWindows()) { Hint = Loc.T("Key mapping works on Windows for now."); return; }
        if (IsKeymapOn) { StopKeymap(); Hint = Loc.T("Key mapping off."); return; }

        var profile = _s.Keymaps.ProfileFor(_row.Sn);
        if (profile == null || profile.Mappings.Count == 0)
        {
            Hint = Loc.T("Set up keys for this device first.");
            OpenKeymapEditor?.Invoke();
            return;
        }
        if (await ControlAsync() is not { } c) { Hint = Loc.T("Key mapping needs the control connection: ") + (_controlError ?? Loc.T("still starting")); return; }
        var window = MirrorWindow?.Invoke() ?? 0;
        if (window == 0) { Hint = Loc.T("The mirror window isn't open yet."); return; }

        _engine = new KeymapEngine(c, profile, () => _size);
        _engineWindow = window;
        KeymapRouter.Register(window, _engine);
        IsKeymapOn = true;
        Hint = Loc.F("Key mapping on ({0}). Click the mirror window and use your keys.", profile.Name);
        CheckOrientation();
    }

    void StopKeymap()
    {
        if (OperatingSystem.IsWindows() && _engine != null) KeymapRouter.Unregister(_engineWindow);
        _engine = null;
        IsKeymapOn = false;
    }

    /// <summary>Called after the key layout was edited: picks up the changes immediately.</summary>
    public async Task ReloadKeymapAsync()
    {
        if (!IsKeymapOn) return;
        StopKeymap();
        await ToggleKeymap();
    }

    /// <summary>The mirror window lost focus: lift any fingers still held down.</summary>
    public void MirrorLostFocus()
    {
        if (OperatingSystem.IsWindows() && _engine != null) KeymapRouter.Release(_engine);
    }

    void CheckOrientation()
    {
        if (_engine == null || _size.W == 0) return;
        var landscape = _size.W > _size.H;
        if (landscape != _engine.Profile.Landscape)
            Hint = Loc.F("This key layout was made in {0}; the device is in {1}. Rotate it for the keys to line up.", (_engine.Profile.Landscape ? Loc.T("landscape") : Loc.T("portrait")), (landscape ? Loc.T("landscape") : Loc.T("portrait")));
    }

    public async ValueTask DisposeAsync()
    {
        StopKeymap();
        if (_recording is { } rec) { MirrorManager.Stop(rec); _recording = null; }
        if (OperatingSystem.IsWindows() && _group is GroupControl g) { _group = null; await g.DisposeAsync(); }
        _cts.Cancel();
        await RestoreRotationAsync();
        if (await _control is { } c) await c.DisposeAsync();
    }
}
