// Smoke test for Paneless.Core against the real devices on the network. Read-only:
// lists devices and lock states, and checks saved unlock codes can be decrypted (never prints them).
using Paneless.Core;

// --groups and --scripts-parse need no device and no scrcpy (they also run in CI).
var offline = args.Contains("--groups") || args.Contains("--scripts-parse") || args.Contains("--automation") || args.Contains("--translations") || args.Contains("--assistants");
var tools = ToolLocator.Find() ?? (offline ? new ToolPaths("adb", "scrcpy") : null);
if (tools == null) { Console.WriteLine("scrcpy/adb not found"); return 1; }
Console.WriteLine($"adb:    {tools.Adb}\nscrcpy: {tools.Scrcpy}\ndata:   {AppPaths.DataDir}");

if (args.Contains("--groups"))
{
    // Device groups, in memory only (nothing is written to groups.json).
    var gs = new GroupStore(new GroupData(), persist: false);
    var changes = 0;
    gs.Changed += () => changes++;
    var okAll = true;
    void Check(bool ok, string what) { if (!ok) okAll = false; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
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
    Check(new DeviceProfile().IsEmpty && !devP.IsEmpty && !new DeviceProfile { LockOnClose = true }.IsEmpty, "empty profiles are dropped when saved");
    Check(EffectiveProfile.Resolve(global, new DeviceProfile { LockOnClose = true }, null).ToMirrorOptions().LockOnClose
          && !EffectiveProfile.Resolve(global, new DeviceProfile { LockOnClose = true }, new DeviceProfile { LockOnClose = false }).ToMirrorOptions().LockOnClose, "LockOnClose resolves device > group > global");

    // USB & Wi-Fi merging in DeviceDirectory.Merge + Wi-Fi IP parsing.
    var known = new Dictionary<string, KnownDevice> { ["SN123"] = new() { Name = "My Pixel", Model = "Pixel 11", Type = "Phone" } };
    var infos = new Dictionary<string, DeviceInfo>
    {
        ["SN123"] = new("SN123", "Pixel 11", "Phone", "My Pixel"),
        ["192.168.1.40:37123"] = new("SN123", "Pixel 11", "Phone", "My Pixel"),
    };
    var mdns = new List<MdnsService> { new() { Ip = "192.168.1.40", Sn = "SN123", Connect = "192.168.1.40:37123" } };

    // 1. USB + mDNS simultaneously -> single row, still labelled "USB cable" (not IP:port).
    var r1 = DeviceDirectory.Merge([new AdbDevice("SN123", "device", "Pixel_11")], mdns, infos, known, new HashSet<string>(), _ => UnlockStatus.None);
    Check(r1.Count == 1 && !r1[0].IsNetwork && r1[0].Address == "USB cable" && r1[0].Sn == "SN123", "USB + mDNS stays USB cable");

    // 2. Wi-Fi listed first + USB listed second -> prefers USB by default.
    var both = new List<AdbDevice> { new("192.168.1.40:37123", "device", "Pixel_11"), new("SN123", "device", "Pixel_11") };
    var r2 = DeviceDirectory.Merge(both, mdns, infos, known, new HashSet<string>(), _ => UnlockStatus.None);
    Check(r2.Count == 1 && !r2[0].IsNetwork && r2[0].Serial == "SN123", "USB preferred over Wi-Fi when idle");

    // 3. Actively mirroring over Wi-Fi + USB cable plugged in -> keeps active Wi-Fi mirror row.
    var r3 = DeviceDirectory.Merge(both, mdns, infos, known, new HashSet<string> { "192.168.1.40:37123" }, _ => UnlockStatus.None);
    Check(r3.Count == 1 && r3[0].IsNetwork && r3[0].State == DeviceState.Mirroring, "active Wi-Fi mirror not shadowed by USB");

    // 4. Unauthorized USB device -> falls back to hardware serial so known name is kept.
    var r4 = DeviceDirectory.Merge([new AdbDevice("SN123", "unauthorized", "")], [], new Dictionary<string, DeviceInfo>(), known, new HashSet<string>(), _ => UnlockStatus.None);
    Check(r4.Count == 1 && r4[0].State == DeviceState.Unauthorized && r4[0].Name == "My Pixel" && r4[0].Sn == "SN123", "unauthorized USB keeps known name");

    // 5. Ready Wi-Fi beats unauthorized USB for the same device.
    var r5 = DeviceDirectory.Merge([new AdbDevice("SN123", "unauthorized", ""), new AdbDevice("192.168.1.40:37123", "device", "Pixel_11")], mdns, infos, known, new HashSet<string>(), _ => UnlockStatus.None);
    Check(r5.Count == 1 && r5[0].State == DeviceState.Connected && r5[0].IsNetwork, "ready Wi-Fi beats unauthorized USB");

    // 6. Emulator serial keeps its own address and doesn't claim "USB cable".
    var r6 = DeviceDirectory.Merge([new AdbDevice("emulator-5554", "device", "sdk_gphone")], [], new Dictionary<string, DeviceInfo>(), known, new HashSet<string>(), _ => UnlockStatus.None);
    Check(r6.Count == 1 && r6[0].Address == "emulator-5554" && r6[0].Sn == null, "emulator keeps serial address");

    // 7. Wi-Fi IP parsing from wlan0 and ip route fallback.
    Check(AdbClient.ParseWifiIp("23: wlan0: <UP>\n    inet 192.168.2.40/24 brd 192.168.2.255 scope global wlan0") == "192.168.2.40", "ParseWifiIp reads wlan0 inet");
    Check(AdbClient.ParseWifiIp("192.168.2.0/24 dev wlan0 proto kernel scope link src 192.168.2.55") == "192.168.2.55", "ParseWifiIp reads ip route fallback");
    Check(AdbClient.ParseWifiIp("127.0.0.1/8 scope host lo") == null, "ParseWifiIp ignores loopback");

    // 8. Screen state for the Wall: a dozing or sleeping screen would give a black picture.
    Check(AdbClient.ParseScreenOn("  mWakefulness=Awake\n") == true && AdbClient.ParseScreenOn("mWakefulness=Dreaming") == true, "screen on: Awake and Dreaming");
    Check(AdbClient.ParseScreenOn("  mWakefulness=Dozing") == false && AdbClient.ParseScreenOn("mWakefulness=Asleep") == false, "screen off: Dozing and Asleep");
    Check(AdbClient.ParseScreenOn("") == null, "screen state unknown when dumpsys says nothing");

    // 9. Wall layout: the user's order survives devices coming and going; sizes are clamped.
    var wall = new WallLayout();
    Check(string.Join(",", wall.Sort(["a", "b", "c"])) == "a,b,c", "wall: no saved order keeps the given (name) order");
    wall.Move(["a", "b", "c"], "c", 0);
    Check(string.Join(",", wall.Sort(["a", "b", "c"])) == "c,a,b", "wall: a dragged tile keeps its new place");
    wall.Move(["c", "b"], "b", 0);   // a is offline
    Check(string.Join(",", wall.Sort(["a", "b", "c"])) == "b,a,c", "wall: an offline device comes back where it was");
    Check(string.Join(",", wall.Sort(["d", "c", "a"])) == "a,c,d", "wall: a new device goes after the placed ones");
    wall.Move(["b", "a", "c"], "b", 99);
    Check(string.Join(",", wall.Sort(["a", "b", "c"])) == "a,c,b", "wall: moving past the end puts a tile last");
    wall.SetWidth("a", 5000); wall.Width = 300;
    Check(wall.WidthOf("a") == WallLayout.MaxWidth && wall.WidthOf("b") == 300 && wall.HasOwnWidth("a") && !wall.HasOwnWidth("b"), "wall: own sizes are clamped, others follow the slider");
    wall.ClearWidth("a"); wall.Hide("b"); wall.Hide("b");
    Check(wall.WidthOf("a") == 300 && wall.Hidden.Count == 1 && wall.IsHidden("b"), "wall: same size again, hiding twice hides once");
    wall.ShowAll(); wall.ResetOrder();
    Check(!wall.IsHidden("b") && string.Join(",", wall.Sort(["a", "b", "c"])) == "a,b,c", "wall: show all and sort by name");
    var wallJson = System.Text.Json.JsonSerializer.Serialize(new AppSettings { Wall = { Width = 260, Order = ["x"], Hidden = ["y"] } });
    var wallBack = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(wallJson)!.Wall;
    Check(wallBack.Width == 260 && wallBack.Order.SequenceEqual(["x"]) && wallBack.IsHidden("y"), "wall: layout is saved with the settings");

    return okAll ? 0 : 1;
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
    var okAll = true;
    void Check(bool ok, string what) { if (!ok) okAll = false; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }

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
    return okAll ? 0 : 1;
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

if (args.Contains("--automation"))
{
    // Scheduled tasks, alerts, script sharing, the log parser and the inventory export. No device, nothing saved.
    var okAll = true;
    void Check(bool ok, string what) { if (!ok) okAll = false; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
    var mon = new DateTime(2026, 11, 23, 2, 0, 0);          // a Monday, 02:00
    var daily = new ScheduledTask { Repeat = ScheduleRepeat.Daily, Time = "03:00" };
    Check(daily.Next(mon) == mon.Date.AddHours(3), "daily: next run today at 03:00");
    Check(!daily.IsDue(mon, mon.AddMinutes(30)) && daily.IsDue(mon.AddMinutes(59), mon.AddMinutes(61)), "daily: due only once 03:00 has passed");
    daily.LastRun = mon.Date.AddHours(3);
    Check(!daily.IsDue(mon.AddMinutes(59), mon.AddMinutes(61)), "daily: not again after it ran");
    var weekly = new ScheduledTask { Repeat = ScheduleRepeat.Weekly, Time = "07:30", Days = [DayOfWeek.Wednesday] };
    Check(weekly.Next(mon) == mon.Date.AddDays(2).AddHours(7.5), "weekly: next run on the chosen day");
    var hourly = new ScheduledTask { Repeat = ScheduleRepeat.EveryHours, Hours = 6, LastRun = mon.AddHours(-5) };
    Check(!hourly.IsDue(mon, mon) && hourly.IsDue(mon, mon.AddHours(1)), "every 6 hours: due 6 hours after the last run");
    Check(new ScheduledTask { Enabled = false }.Next(mon) == null, "switched off: never runs");

    var alerts = new AlertData { Rules = [AlertRule.Default(AlertKind.LowBattery), AlertRule.Default(AlertKind.Offline)] };
    var low = alerts.Rules[0].Threshold;
    var w = new AlertWatcher();
    var t0 = mon;
    var first = w.Check(alerts, "A", "Lab 1", null, true, low, false, 50, 30, t0).ToList();
    var again = w.Check(alerts, "A", "Lab 1", null, true, low - 1, false, 50, 30, t0.AddMinutes(1)).ToList();
    Check(first.Count == 1 && again.Count == 0, "low battery: told once, not every minute");
    w.Check(alerts, "A", "Lab 1", null, true, low, true, 50, 30, t0.AddMinutes(2)).ToList();
    Check(w.Check(alerts, "A", "Lab 1", null, true, low, false, 50, 30, t0.AddMinutes(3)).Count() == 1, "told again after it was charging in between");
    var offMinutes = alerts.Rules[1].Threshold;
    Check(!w.Check(alerts, "A", "Lab 1", null, false, null, false, null, null, t0.AddMinutes(3).AddMinutes(offMinutes - 1)).Any()
          && w.Check(alerts, "A", "Lab 1", null, false, null, false, null, null, t0.AddMinutes(3).AddMinutes(offMinutes)).Any(), "offline: only after the set minutes");
    alerts.Rules[0].Groups = ["Lobby"];
    Check(!new AlertWatcher().Check(alerts, "B", "Lab 2", "Test lab", true, 1, false, 50, 30, t0).Any(r => r.Rule.Kind == AlertKind.LowBattery), "a rule for one group ignores other groups");

    var shared = ScriptSharing.Export(new AdbScript { Name = "Doze off", Description = "x", Commands = "adb shell dumpsys deviceidle disable" });
    var back = ScriptSharing.Parse(shared);
    Check(back.Count == 1 && back[0].Name == "Doze off" && back[0].Commands.Contains("deviceidle"), "script sharing round-trips");
    Check(ScriptSharing.Parse("adb shell input keyevent 26\nadb shell wm density reset")[0].Commands.Contains("density"), "plain pasted commands are accepted");
    Check(ScriptSharing.Parse("[" + shared + "," + shared + "]").Count == 2, "a list of scripts is accepted");
    var refused = 0;
    foreach (var bad in new[] { "", "{\"name\":\"empty\"}", "[]" })
        try { ScriptSharing.Parse(bad); } catch (FormatException) { refused++; }
    Check(refused == 3, "empty or command-less input is refused");

    var l = LogcatSession.Parse("11-21 09:30:06.123  1234  1250 E AndroidRuntime: FATAL EXCEPTION: main");
    Check(l.Pid == 1234 && l.Level == 'E' && l.Tag == "AndroidRuntime" && l.IsCrash, "log line parsed, crash recognised");
    Check(LogcatSession.Parse("--------- beginning of main").Level == ' ', "other lines are kept as they are");

    var inv = new DeviceInventory("Lab, 1", "S1", "Google", "Pixel 9a", "15", 35, "2026-11-05", 80, false, "Good", 29.5,
        131072, 13107, 8192, "1080x2424", "192.168.3.11", TimeSpan.FromHours(30), mon);
    var csv = InventoryExport.Csv([(inv, "Test \"lab\"")]);
    Check(csv.Contains("\"Lab, 1\"") && csv.Contains("\"Test \"\"lab\"\"\"") && inv.StorageFreePercent is > 9.9 and < 10.1, "inventory CSV quotes commas and quotes");
    Check(InventoryExport.Html([(inv, "<b>")], "Report").Contains("&lt;b&gt;"), "inventory report escapes HTML");
    return okAll ? 0 : 1;
}

if (args.Contains("--assistants"))
{
    // Adding Paneless to AI apps' settings: text in, text out, no real settings touched.
    var okAll = true;
    void Check(bool ok, string what) { if (!ok) okAll = false; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
    const string exe = @"C:\Users\x\AppData\Local\PanelessApp\current\Paneless.exe";
    var json = McpConfig.WithJsonServer("""
        {
          // a comment
          "theme": "dark",
          "mcpServers": { "other": { "command": "other.exe" } },
        }
        """, exe);
    var j = System.Text.Json.Nodes.JsonNode.Parse(json)!;
    Check((string?)j["theme"] == "dark" && j["mcpServers"]?["other"] != null, "JSON settings: everything else is kept");
    Check((string?)j["mcpServers"]?["paneless"]?["command"] == exe && (string?)j["mcpServers"]?["paneless"]?["args"]?[0] == "--mcp", "JSON settings: Paneless added with --mcp");
    Check(McpConfig.WithJsonServer(json, exe) == json, "JSON settings: adding twice changes nothing");
    Check(System.Text.Json.Nodes.JsonNode.Parse(McpConfig.WithJsonServer("", exe))?["mcpServers"]?["paneless"] != null, "JSON settings: a new file works");
    var refused = false;
    try { McpConfig.WithJsonServer("[1,2]", exe); } catch (FormatException) { refused = true; }
    Check(refused, "JSON settings: a file that isn't an object is left alone");

    var toml = McpConfig.WithCodexServer("model = \"gpt-5\"\n\n[mcp_servers.paneless]\ncommand = 'old.exe'\n\n[mcp_servers.paneless.env]\nX = \"1\"\n\n[mcp_servers.other]\ncommand = \"o\"\n", exe);
    Check(toml.Contains("model = \"gpt-5\"") && toml.Contains("[mcp_servers.other]") && !toml.Contains("old.exe") && !toml.Contains("paneless.env"), "Codex TOML: old Paneless entry replaced, others kept");
    Check(toml.Contains($"command = '{exe}'") && toml.Contains("args = [\"--mcp\"]") && toml.Split("[mcp_servers.paneless]").Length == 2, "Codex TOML: one entry, path needs no escaping");
    Check(McpConfig.WithCodexServer(toml, exe) == toml, "Codex TOML: adding twice changes nothing");
    Check(McpConfig.WithCodexServer("", @"C:\it's\Paneless.exe").Contains("command = \"C:\\\\it's\\\\Paneless.exe\""), "Codex TOML: a path with ' is escaped");

    Check(McpConfig.Friendly("claude-ai", null) == "Claude" && McpConfig.Friendly("claude-code", null) == "Claude Code"
          && McpConfig.Friendly("codex-mcp-client", null) == "Codex" && McpConfig.Friendly("gemini-cli-mcp-client", null) == "Gemini"
          && McpConfig.Friendly("my-tool", "My Tool") == "My Tool" && McpConfig.Friendly(null, null).Length > 0, "assistants get readable names");
    var withBom = McpConfig.Message("﻿{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\r");
    Check(withBom != null && System.Text.Json.Nodes.JsonNode.Parse(withBom)?["method"]?.ToString() == "ping"
          && McpConfig.Message("  ﻿ ") == null && McpConfig.Message("") == null, "MCP messages: a leading byte-order mark is ignored");
    return okAll ? 0 : 1;
}

if (args.Contains("--translations"))
{
    // German and French: every {0} in a translation must exist in the English text, or the text would break.
    var repo = AppContext.BaseDirectory;
    while (repo != null && !File.Exists(Path.Combine(repo, "Paneless.slnx"))) repo = Path.GetDirectoryName(repo);
    if (repo == null) { Console.WriteLine("FAIL repository not found"); return 1; }
    var rx = new System.Text.RegularExpressions.Regex(@"\{\d+(:[^}]*)?\}");
    foreach (var lang in new[] { "de", "fr" })
    {
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(repo, "src", "Paneless.App", "Localization", lang + ".json")))!;
        var bad = map.Where(kv => rx.Matches(kv.Value).Select(m => m.Value).Except(rx.Matches(kv.Key).Select(m => m.Value)).Any()
                                  || kv.Value.Trim().Length == 0).Select(kv => kv.Key).ToList();
        foreach (var k in bad) Console.WriteLine($"FAIL {lang}: {k}");
        Console.WriteLine($"{(bad.Count == 0 ? "ok  " : "FAIL")} {lang}.json: {map.Count} texts, placeholders match");
    }
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
