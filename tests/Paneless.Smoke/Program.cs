// Smoke test for Paneless.Core against the real devices on the network. Read-only:
// lists devices and lock states, and checks saved unlock codes can be decrypted (never prints them).
using Paneless.Core;

// --groups and --scripts-parse need no device and no scrcpy (they also run in CI).
var offline = args.Contains("--groups") || args.Contains("--scripts-parse");
var tools = ToolLocator.Find() ?? (offline ? new ToolPaths("adb", "scrcpy") : null);
if (tools == null) { Console.WriteLine("scrcpy/adb not found"); return 1; }
Console.WriteLine($"adb:    {tools.Adb}\nscrcpy: {tools.Scrcpy}\ndata:   {AppPaths.DataDir}");

if (args.Contains("--groups"))
{
    // Device groups, in memory only (nothing is written to groups.json).
    var gs = new GroupStore(new GroupData(), persist: false);
    var changes = 0;
    gs.Changed += () => changes++;
    void Check(bool ok, string what) => Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    var lab = gs.Create("  Test   lab ");
    Check(lab.Name == "Test lab", "name is tidied");
    Check(gs.Create("test LAB") == lab, "same name (any case) gives the same group");
    Check(GroupStore.Clean("   ") == null && GroupStore.Clean(new string('x', 61)) == null, "empty and over-long names refused");
    var home = gs.Create("Home");
    gs.Assign(["A", "B", "C"], lab);
    gs.Assign(["B"], home);
    Check(gs.GroupOf("B") == home && !lab.Members.Contains("B"), "a device is in one group only");
    gs.SetCollapsed(home, true);
    gs.Assign(["C"], home);
    Check(!home.Collapsed, "moving devices into a collapsed group opens it");
    Check(!gs.Rename(home, "test lab") && gs.Rename(home, "Family"), "rename refuses a taken name, accepts a new one");
    gs.Move(home, -1);
    Check(gs.Groups[0] == home, "move up");
    gs.Assign(["A"], null);
    Check(gs.GroupOf("A") == null, "take out of group");
    gs.Delete(home);
    Check(gs.Groups.Count == 1 && gs.GroupOf("B") == null, "deleting a group frees its devices");
    Check(changes > 0, $"Changed raised ({changes}x)");
    var json = System.Text.Json.JsonSerializer.Serialize(gs.Data);
    var back = System.Text.Json.JsonSerializer.Deserialize<GroupData>(json)!;
    Check(back.Groups.Single().Name == "Test lab", "round-trips through JSON");

    // Settings: device beats group beats global.
    var global = new AppSettings { Audio = true, BitrateMbps = 8, VideoCodec = "h264", PushTarget = "/sdcard/Download/" };
    var groupP = new DeviceProfile { Audio = false, BitrateMbps = 16, OnConnectScripts = ["a", "b"], AutoMirror = true };
    var devP = new DeviceProfile { BitrateMbps = 4, OnConnectScripts = ["b", "c"], PushTarget = "/sdcard/Movies/" };
    var eff = EffectiveProfile.Resolve(global, groupP, devP);
    Check(eff.BitrateMbps == 4 && !eff.Audio && eff.VideoCodec == "h264" && eff.AutoMirror && eff.PushTarget == "/sdcard/Movies/", "device > group > global");
    Check(eff.OnConnectScripts.SequenceEqual(["a", "b", "c"]), "on-connect scripts of group and device, no doubles");
    Check(EffectiveProfile.Resolve(global, null, null).BitrateMbps == 8 && !EffectiveProfile.Resolve(global, null, null).AutoMirror, "no profiles = global defaults");
    Check(new DeviceProfile().IsEmpty && !devP.IsEmpty, "empty profiles are dropped when saved");
    return 0;
}

if (args.Contains("--mirror-error"))
{
    // Starts scrcpy for a device that doesn't exist and checks the failure is reported with scrcpy's reason.
    var mm = new MirrorManager(tools);
    var failed = new TaskCompletionSource<string>();
    mm.Failed += (_, why) => failed.TrySetResult(why);
    mm.Start("0.0.0.0:9", "Nobody", new MirrorOptions(false, true, false, true, 1920));
    var done = await Task.WhenAny(failed.Task, Task.Delay(TimeSpan.FromSeconds(20)));
    Console.WriteLine(done == failed.Task ? $"failure reported: {failed.Task.Result}" : "NO failure reported within 20 s");
    return 0;
}

if (args.Contains("--features"))
{
    // Device features against a real device. Prints counts and sizes only; the test recording is deleted unopened,
    // and the virtual screens are created without a window and closed after a few seconds.
    var prefix = args[Array.IndexOf(args, "--features") + 1];
    var ac = new AdbClient(tools.Adb);
    var dev = (await ac.GetDevicesAsync()).First(d => d.Serial.StartsWith(prefix) && d.State == "device");
    var bat = await ac.GetBatteryAsync(dev.Serial);
    Console.WriteLine($"battery: {(bat == null ? "FAILED" : $"{bat.Level} %, charging={bat.Charging}, text \"{bat.Text}\"")}");
    var user = await ac.GetPackagesAsync(dev.Serial, system: false);
    var all = await ac.GetPackagesAsync(dev.Serial, system: true);
    Console.WriteLine($"apps: {user.Count} installed by the user, {all.Count} in total, settings app listed={all.Any(p => p.Name == "com.android.settings")}");

    var mm = new MirrorManager(tools);
    var opts = new MirrorOptions(false, true, false, false, 1280, "h264", 8, 0);
    var file = Path.Combine(Path.GetTempPath(), $"paneless-smoke-{Guid.NewGuid():n}.mkv");
    var rec = mm.StartRecording(dev.Serial, "smoke", file, opts);
    await Task.Delay(4000);
    var running = rec.IsRunning;
    MirrorManager.Stop(rec);
    await Task.Delay(800);
    var len = File.Exists(file) ? new FileInfo(file).Length : 0;
    Console.WriteLine($"recording: still running after 4 s={running}, file {len / 1024} KB -> deleted unopened");
    if (!running) Console.WriteLine("  log: " + string.Join(" | ", File.ReadLines(rec.LogPath).TakeLast(4)));
    if (File.Exists(file)) File.Delete(file);

    // Recording a virtual second screen: it draws even while the device's own screen is off.
    var vfile = Path.Combine(Path.GetTempPath(), $"paneless-smoke-{Guid.NewGuid():n}.mkv");
    var vpsi = new System.Diagnostics.ProcessStartInfo(tools.Scrcpy) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    vpsi.Environment["ADB"] = tools.Adb;
    foreach (var a in new[] { "-s", dev.Serial, "--no-window", "--no-playback", "--no-audio", "--no-control", "--new-display=720x1280", "--record=" + vfile }) vpsi.ArgumentList.Add(a);
    var vp = new System.Diagnostics.Process { StartInfo = vpsi };
    vp.OutputDataReceived += (_, _) => { }; vp.ErrorDataReceived += (_, _) => { };
    vp.Start(); vp.BeginOutputReadLine(); vp.BeginErrorReadLine();
    await Task.Delay(5000);
    var graceful = ProcessSignals.Interrupt(vp, TimeSpan.FromSeconds(6));
    if (!graceful) { try { vp.Kill(true); } catch { } }
    var vlen = File.Exists(vfile) ? new FileInfo(vfile).Length : 0;
    var head = File.Exists(vfile) ? File.ReadAllBytes(vfile).Take(4).ToArray() : [];
    Console.WriteLine($"second-screen recording: stopped with Ctrl+C={graceful} ({ProcessSignals.LastError}), exit code {(vp.HasExited ? vp.ExitCode : -1)}, file {vlen / 1024} KB, Matroska header={head.SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })} -> deleted unopened");
    if (File.Exists(vfile)) File.Delete(vfile);

    foreach (var (label, extra) in new[]
    {
        ("app on its own screen", new[] { "--new-display=1080x1920", "--start-app=com.android.settings", "--record=" + Path.Combine(Path.GetTempPath(), "paneless-smoke-app.mkv") }),
    })
    {
        var psi = new System.Diagnostics.ProcessStartInfo(tools.Scrcpy) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.Environment["ADB"] = tools.Adb;
        foreach (var a in new[] { "-s", dev.Serial, "--no-window", "--no-playback", "--no-audio" }.Concat(extra)) psi.ArgumentList.Add(a);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var p = new System.Diagnostics.Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await Task.Delay(4500);
        var alive = !p.HasExited;
        try { p.Kill(entireProcessTree: true); } catch { }
        p.WaitForExit(3000);
        var log = lines.Where(l => l.Contains("ERROR") || l.Contains("WARN") || l.Contains("display")).Take(4);
        Console.WriteLine($"{label}: running after 4.5 s={alive}  {string.Join(" | ", log).Trim()}");
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), "paneless-smoke-app.mkv")) File.Delete(f);
    }
    var fwd = await ac.RunAsync("-s", dev.Serial, "forward", "--list");
    Console.WriteLine($"leftover forwards: {(fwd.Output.Contains("scrcpy_") ? fwd.Output : "none")}");
    return 0;
}

if (args.Contains("--camreopen"))
{
    // Camera reopening, without a device: a stand-in for scrcpy that reports "Camera disconnected" the first time
    // and then exits (like a phone that's turned), and keeps running on later starts.
    var fakeDir = Path.Combine(Path.GetTempPath(), "paneless-fake-scrcpy");
    Directory.CreateDirectory(fakeDir);
    var marker = Path.Combine(fakeDir, "cut-off-done");
    if (File.Exists(marker)) File.Delete(marker);
    var fake = Path.Combine(fakeDir, "scrcpy.cmd");
    File.WriteAllText(fake,
        "@echo off\r\n" +
        "echo [server] INFO: Using camera '0'\r\n" +
        $"if exist \"{marker}\" ( ping -n 30 127.0.0.1 >nul & exit /b 0 )\r\n" +
        $"echo done> \"{marker}\"\r\n" +
        "ping -n 5 127.0.0.1 >nul\r\n" +
        "echo [server] WARN: Camera disconnected\r\n" +
        "ping -n 2 127.0.0.1 >nul\r\n" +
        "echo WARN: Device disconnected\r\n" +
        "exit /b 0\r\n");
    var mm = new MirrorManager(new ToolPaths(tools.Adb, fake));
    var turnAsked = 0;
    mm.CameraTurn = (_, _) => { Interlocked.Increment(ref turnAsked); return 90; };
    var reopened = new List<MirrorSession>();
    mm.Reopened += (_, next) => { lock (reopened) reopened.Add(next); };
    var failures = new List<string>();
    mm.Failed += (_, why) => { lock (failures) failures.Add(why); };
    void Check(bool ok, string what) => Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");

    var cam = mm.StartCamera("fake-serial", "Fake phone", false, new MirrorOptions(false, false, false, false, null), 0);
    mm.LastWindow[cam] = (100, 100, 400, 700);
    for (var i = 0; i < 120 && reopened.Count == 0; i++) await Task.Delay(100);
    Check(reopened.Count == 1 && reopened[0].IsRunning, $"cut off by the device -> reopened and running (asked for the turn {turnAsked}x)");
    var second = reopened.FirstOrDefault();
    Check(second != null && MirrorManager.TurnOf(second) == 90, "reopened turned 90°");
    Check(second != null && second.Args.Contains("--window-width=700") && second.Args.Contains("--window-height=400"), "window kept its place, shape turned");

    // Turning twice in quick succession (what crashed Paneless): must not throw, and end with one running camera.
    Exception? error = null;
    MirrorSession? third = null, fourth = null;
    try
    {
        third = await Task.Run(() => mm.TurnCamera(second!, 0));
        fourth = await Task.Run(() => mm.TurnCamera(third!, 90));
    }
    catch (Exception ex) { error = ex; }
    Check(error == null, "turning twice quickly: no error" + (error != null ? ": " + error.Message : ""));
    Check(third is { IsRunning: false } && fourth is { IsRunning: true }, "only the newest camera window is running");
    Check(failures.Count == 0, "no failures reported" + (failures.Count > 0 ? ": " + string.Join("; ", failures) : ""));
    foreach (var s in mm.Sessions) MirrorManager.Stop(s);
    await Task.Delay(500);
    Check(mm.Sessions.Count == 0, "all stopped");
    return 0;
}

if (args.Contains("--camturn"))
{
    // Read-only: how each device is held and the turn a camera picture gets so it shows upright.
    var ac = new AdbClient(tools.Adb);
    foreach (var d in (await ac.GetDevicesAsync()).Where(d => d.State == "device"))
    {
        var rot = $"screen {await ac.GetRotationAsync(d.Serial)}, sensor {(await ac.GetPhysicalRotationAsync(d.Serial))?.ToString() ?? "unknown"}, screen follows={await ac.ScreenFollowsDeviceAsync(d.Serial)} => held {await ac.GetHeldRotationAsync(d.Serial)}";
        Console.WriteLine($"{d.Model,-14} rotation={rot}  back sensor={await ac.GetCameraSensorOrientationAsync(d.Serial, false)}° -> turn {await ac.GetCameraUprightTurnAsync(d.Serial, false)}°" +
                          $"  front sensor={await ac.GetCameraSensorOrientationAsync(d.Serial, true)}° -> turn {await ac.GetCameraUprightTurnAsync(d.Serial, true)}°");
    }
    return 0;
}

if (args.Contains("--vdcontrol"))
{
    // A control connection aimed at a virtual display (as the toolbar of a second screen uses). The display is created
    // without a window, gets only no-op keys, and is closed after a few seconds; its test recording is deleted unopened.
    var prefix = args[Array.IndexOf(args, "--vdcontrol") + 1];
    var ac = new AdbClient(tools.Adb);
    var dev = (await ac.GetDevicesAsync()).First(d => d.Serial.StartsWith(prefix) && d.State == "device");
    var file = Path.Combine(Path.GetTempPath(), $"paneless-smoke-{Guid.NewGuid():n}.mkv");
    var psi = new System.Diagnostics.ProcessStartInfo(tools.Scrcpy) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    psi.Environment["ADB"] = tools.Adb;
    foreach (var a in new[] { "-s", dev.Serial, "--no-window", "--no-playback", "--no-audio", "--new-display=720x1280", "--record=" + file }) psi.ArgumentList.Add(a);
    int? id = null;
    var rx = new System.Text.RegularExpressions.Regex(@"\(id=(\d+)\)");
    var p = new System.Diagnostics.Process { StartInfo = psi };
    void Line(string? l) { if (l != null && l.Contains("New display") && rx.Match(l) is { Success: true } m) id = int.Parse(m.Groups[1].Value); }
    p.OutputDataReceived += (_, e) => Line(e.Data); p.ErrorDataReceived += (_, e) => Line(e.Data);
    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
    for (var i = 0; i < 100 && id == null; i++) await Task.Delay(100);
    Console.WriteLine($"virtual display id from the log: {id?.ToString() ?? "NOT FOUND"}");
    if (id != null)
    {
        var server = await ScrcpyServerLocator.FindAsync(tools);
        try
        {
            await using var ctl = await DeviceControl.StartAsync(ac, server!, dev.Serial, id.Value);
            for (var i = 0; i < 5; i++) await ctl.PressKeyAsync(0);   // KEYCODE_UNKNOWN: ignored
            await Task.Delay(300);
            Console.WriteLine($"control connection on display {id}: up, still connected after keys = {ctl.IsConnected}");
        }
        catch (Exception ex) { Console.WriteLine("control connection on the virtual display FAILED: " + ex.Message); }
    }
    Console.WriteLine($"stopped with Ctrl+C: {ProcessSignals.Interrupt(p, TimeSpan.FromSeconds(6))}");
    try { p.Kill(true); } catch { }
    if (File.Exists(file)) File.Delete(file);
    var fwd = await ac.RunAsync("-s", dev.Serial, "forward", "--list");
    Console.WriteLine($"leftover forwards: {(fwd.Output.Contains("scrcpy_") ? fwd.Output : "none")}");
    return 0;
}

if (args.Contains("--control"))
{
    // Starts the control-only helper on a device, sends a no-op key, and stops it again. Changes nothing on the device.
    var prefix = args[Array.IndexOf(args, "--control") + 1];
    var ac = new AdbClient(tools.Adb);
    var dev = (await ac.GetDevicesAsync()).First(d => d.Serial.StartsWith(prefix) && d.State == "device");
    var server = await ScrcpyServerLocator.FindAsync(tools);
    Console.WriteLine($"server: {server?.Path} (version {server?.Version})");
    var size = await ac.GetDisplaySizeAsync(dev.Serial);
    Console.WriteLine($"display 0 now: {size?.W}x{size?.H}");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await using (var ctl = await DeviceControl.StartAsync(ac, server!, dev.Serial))
    {
        Console.WriteLine($"control connection up in {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        for (var i = 0; i < 20; i++) await ctl.PressKeyAsync(0);   // KEYCODE_UNKNOWN: ignored by Android
        Console.WriteLine($"40 key messages sent in {sw.ElapsedMilliseconds} ms; still connected: {ctl.IsConnected}");
    }
    var png = await ac.ScreencapAsync(dev.Serial);
    Console.WriteLine(png == null ? "screenshot: FAILED" :
        $"screenshot: {png.Length / 1024} KB PNG, {System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))}x{System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))}");
    var fwd = await ac.RunAsync("-s", dev.Serial, "forward", "--list");
    Console.WriteLine($"leftover forwards: {(fwd.Output.Contains("scrcpy_") ? fwd.Output : "none")}");
    return 0;
}

if (args.Contains("--screenshot"))
{
    // Runs the toolbar's "Screenshot to PC" script, prints only its text output, then deletes the image it saved.
    var serial = args[Array.IndexOf(args, "--screenshot") + 1];
    var a3 = new AdbClient(tools.Adb);
    var before = Directory.Exists(ScriptRunner.OutputDir) ? Directory.GetFiles(ScriptRunner.OutputDir).ToHashSet() : new HashSet<string>();
    var res = await new ScriptRunner(a3).RunAsync(serial, "Pixel 11 Pro", BuiltInScripts.All.First(x => x.Name == "Screenshot to PC"), new Dictionary<string, string>());
    Console.WriteLine($"ok={res.Ok}  output dir={ScriptRunner.OutputDir}");
    foreach (var st in res.Steps) Console.WriteLine($"  $ {st.Command}\n    exit={st.Result.ExitCode} timedOut={st.Result.TimedOut} err={st.Error}\n    out={st.Result.Output.Replace("\n", " | ")}");
    foreach (var f in Directory.Exists(ScriptRunner.OutputDir) ? Directory.GetFiles(ScriptRunner.OutputDir) : [])
        if (!before.Contains(f)) { Console.WriteLine($"  new file: {Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB) -> deleted"); File.Delete(f); }
    return 0;
}

if (args.Contains("--scripts-parse"))
{
    var vals = new Dictionary<string, string> { ["package"] = "com.example.app", ["evil"] = "x; reboot", ["dpi"] = "400" };
    foreach (var line in new[] {
        "adb shell dumpsys deviceidle disable", "shell wm density {dpi}", "adb shell am force-stop {evil}",
        "adb pull /sdcard/a.png \"my file.png\"", "adb -s 123 shell ls", "adb kill-server", "adb shell",
        "adb shell dumpsys window | grep -E 'mCurrentFocus|mFocusedApp'", "logcat -d -t 5 *:E" })
    {
        var (a, err) = ScriptText.ToAdbArgs(line, vals);
        Console.WriteLine($"{line,-62} => {(a != null ? string.Join(" | ", a) : "REJECTED: " + err)}");
    }
    Console.WriteLine("placeholders: " + string.Join(", ", ScriptText.Placeholders("x {package} {dpi=420} {device} {package}").Select(p => p.Name + (p.Default != null ? "=" + p.Default : ""))));
    return 0;
}

if (args.Contains("--scripts-run"))
{
    // Runs only read-only built-ins on the first connected device whose address starts with the given prefix.
    var prefix = args[Array.IndexOf(args, "--scripts-run") + 1];
    var a2 = new AdbClient(tools.Adb);
    var target = (await a2.GetDevicesAsync()).First(d => d.Serial.StartsWith(prefix) && d.State == "device");
    var runner = new ScriptRunner(a2);
    string[] readOnly = ["Battery status", "List battery-optimisation exemptions", "Screen size and density", "App in front right now",
                         "Wi-Fi IP address", "Device info", "Storage space", "Memory (RAM)", "Busiest processes"];
    foreach (var name in readOnly)
    {
        var sc = BuiltInScripts.All.First(x => x.Name == name);
        var res = await runner.RunAsync(target.Serial, "Pixel", sc, new Dictionary<string, string>());
        var outText = string.Join(" / ", res.Steps.Select(st => st.Error ?? st.Result.Output.Replace("\r", "").Replace("\n", " ⏎ ")));
        Console.WriteLine($"[{(res.Ok ? "ok" : "FAIL")} {res.Elapsed.TotalSeconds:0.0}s] {name}: {(outText.Length > 150 ? outText[..150] + "…" : outText)}");
    }
    return 0;
}

var adb = new AdbClient(tools.Adb);
await adb.StartServerAsync();
Console.WriteLine($"network discovery: {(await adb.IsDiscoveryAvailableAsync() ? "available" : "unavailable")}");

var data = new DataStore();
Console.WriteLine($"settings: screenOff={data.Settings.ScreenOff} audio={data.Settings.Audio} keepAwake={data.Settings.KeepAwake} maxSize={data.Settings.MaxSize}");
Console.WriteLine($"known devices: {data.Known.Count}, unlock entries: {data.Unlock.Count}");

var dir = new DeviceDirectory(adb, data);
var unlock = new UnlockService(adb, data, SecretVault.ForThisOs());
foreach (var r in await dir.GetRowsAsync(new HashSet<string>()))
{
    Console.WriteLine($"- {r.Name,-16} {r.Type,-7} {r.Address,-22} {r.State,-10} unlock={r.Unlock}");
    if (r.Serial != null && r.State == DeviceState.Connected)
    {
        var li = await unlock.GetLockInfoAsync(r.Serial);
        Console.WriteLine($"    locked={li.Locked} lockScreenInFront={li.OnLockScreen}");
    }
}

foreach (var (sn, e) in data.Unlock)
{
    var code = await unlock.Vault.RevealAsync(sn, e.Secret ?? "");
    Console.WriteLine($"unlock code for {sn[..4]}…: {(code is { Length: >= 4 } ? "decrypts OK" : "could NOT be read")}, enabled={e.Enabled}");
    code = null;
}
return 0;
