// Smoke test for Paneless.Core against the real devices on the network. Read-only:
// lists devices and lock states, and checks saved unlock codes can be decrypted (never prints them).
using Paneless.Core;

// --groups and --scripts-parse need no device and no scrcpy (they also run in CI).
var offline = args.Contains("--groups") || args.Contains("--scripts-parse") || args.Contains("--automation") || args.Contains("--translations") || args.Contains("--assistants") || args.Contains("--remote-apps") || args.Contains("--helper");
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
    Check(EffectiveProfile.Resolve(new AppSettings(), null, null).ToMirrorOptions().KeepScreensOn
          && !EffectiveProfile.Resolve(new AppSettings(), new DeviceProfile { KeepScreensOn = false }, null).ToMirrorOptions().KeepScreensOn
          && EffectiveProfile.Resolve(new AppSettings { KeepScreensOn = false }, new DeviceProfile { KeepScreensOn = false }, new DeviceProfile { KeepScreensOn = true }).ToMirrorOptions().KeepScreensOn,
          "keeping screens on while locked: on by default, device > group > global");

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
    Check(wall.WidthOf("a") == WallLayout.MaxWidth && wall.WidthOf("b") == 300 && wall.HasOwnWidth("a") && !wall.HasOwnWidth("b"), "wall: own sizes are clamped, others use the shared size");
    var wall2 = new WallLayout(); wall2.SetWidth("a", 500); wall2.SetWidth("b", 150); wall2.SetAllWidths(180);
    Check(wall2.WidthOf("a") == 180 && wall2.WidthOf("b") == 180 && wall2.WidthOf("new") == 180 && !wall2.HasOwnWidth("a"), "wall: make all this size");
    wall.ClearWidth("a"); wall.Hide("b"); wall.Hide("b");
    Check(wall.WidthOf("a") == 300 && wall.Hidden.Count == 1 && wall.IsHidden("b"), "wall: same size again, hiding twice hides once");
    wall.ShowAll(); wall.ResetOrder();
    Check(!wall.IsHidden("b") && string.Join(",", wall.Sort(["a", "b", "c"])) == "a,b,c", "wall: show all and sort by name");
    var wallJson = System.Text.Json.JsonSerializer.Serialize(new AppSettings { Wall = { Width = 260, Order = ["x"], Hidden = ["y"] } });
    var wallBack = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(wallJson)!.Wall;
    Check(wallBack.Width == 260 && wallBack.Order.SequenceEqual(["x"]) && wallBack.IsHidden("y"), "wall: layout is saved with the settings");

    // 10. UnlockService.ParseLockInfo: virtual displays listed before Display 0 must not shadow Display 0's lock screen focus.
    var multiDisplayLock = """
        User "User" (id=0) (current): deviceLocked=1
          Display: mDisplayId=243 (organized)
          mCurrentFocus=null
          Display: mDisplayId=244 (organized)
          mCurrentFocus=Window{7c140ca u0 com.android.settings/com.android.settings.Settings}
          Display: mDisplayId=0 (organized)
          mCurrentFocus=Window{e3b79ba u0 NotificationShade}
        """;
    var parsedLock = UnlockService.ParseLockInfo(multiDisplayLock);
    Check(parsedLock.Locked == true && parsedLock.OnLockScreen, "UnlockService reads Display 0 focus even when virtual displays appear first");

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
    Check(!McpConfig.HasJsonServer("{}") && McpConfig.HasJsonServer(McpConfig.WithJsonServer("{}", exe)), "HasJsonServer detects paneless entry");
    Check(!McpConfig.HasCodexServer("") && McpConfig.HasCodexServer(toml), "HasCodexServer detects paneless table");

    Check(McpConfig.Friendly("claude-ai", null) == "Claude" && McpConfig.Friendly("claude-code", null) == "Claude Code"
          && McpConfig.Friendly("codex-mcp-client", null) == "Codex" && McpConfig.Friendly("gemini-cli-mcp-client", null) == "Gemini"
          && McpConfig.Friendly("my-tool", "My Tool") == "My Tool" && McpConfig.Friendly(null, null).Length > 0, "assistants get readable names");
    var withBom = McpConfig.Message("﻿{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\r");
    Check(withBom != null && System.Text.Json.Nodes.JsonNode.Parse(withBom)?["method"]?.ToString() == "ping"
          && McpConfig.Message("  ﻿ ") == null && McpConfig.Message("") == null, "MCP messages: a leading byte-order mark is ignored");

    // Device facts for assistants: words and rounded numbers, no raw Android codes or display symbols.
    Check(AssistantFacts.HealthName("11") == "Not available yet" && AssistantFacts.HealthName("2") == "Good" && AssistantFacts.HealthName("6") == "Failure"
          && AssistantFacts.HealthName("99") == "99", "battery health codes become words (11 = not available yet)");
    Check(AssistantFacts.Uptime(new TimeSpan(9, 7, 39, 14)) == "9 days, 7 hours" && AssistantFacts.Uptime(TimeSpan.FromHours(1)) == "1 hour"
          && AssistantFacts.Uptime(new TimeSpan(0, 5, 12, 0)) == "5 hours, 12 minutes" && AssistantFacts.Uptime(TimeSpan.FromSeconds(30)) == "0 minutes", "uptime reads like a sentence");
    var facts = AssistantFacts.Inventory(new DeviceInventory("Phone", "SN1", "Google", "Pixel", "17", 37, "2026-11-05", 96, false, "Not available yet", 26.7,
        229510, 66192, 11646, "1280x2856", "192.168.2.141", new TimeSpan(9, 7, 39, 14), new DateTime(2026, 11, 22, 9, 27, 24)));
    Check((double?)facts["storage_free_percent"] == 29 && (double?)facts["storage_free_gb"] == 64.6 && (double?)facts["ram_gb"] == 11.4
          && (string?)facts["uptime"] == "9 days, 7 hours" && (string?)facts["collected_at"] == "2026-11-22 09:27", "device facts are rounded and readable");
    Check(AssistantFacts.Inventory(new DeviceInventory("W", "S", "", "", "", 0, "", null, false, "", null, null, null, null, "", "", null, DateTime.Now))["model"] is null,
          "missing device facts are left empty, not \"\"");
    return okAll ? 0 : 1;
}

if (args.Contains("--remote-apps"))
{
    // Remote apps: window options, shortcut names, the device helper's output, the shortcut icon file. No device needed.
    var okAll = true;
    void Check(bool ok, string what) { if (!ok) okAll = false; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }
    var quality = new MirrorOptions(false, true, false, true, null, "h265", 300, 60);
    var app = new RemoteApp { Package = "com.example.chat", Name = "Chat" };
    var a = app.ScrcpyArgs("Chat – Pixel", quality);
    Check(a.Contains("--new-display=1080x1920/400") && a.Contains("--start-app=com.example.chat") && a.Contains("--flex-display") && a.Contains("--render-fit=letterbox") && a.Contains("--no-vd-system-decorations")
          && a.Contains("--display-ime-policy=hide") && a.Contains("--turn-screen-off") && a.Contains("--video-bit-rate=200M") && a.Contains("--max-fps=60") && a.Contains("--keep-active")
          && !a.Any(x => x.StartsWith("--window-x")) && a[0] == "--window-title" && a[1] == "Chat – Pixel", "a phone app: tall screen with phone DPI, PC keyboard, device screen off");
    app.Shape = "tablet"; app.PcKeyboardOnly = false; app.ScreenOff = false; app.OnTop = true;
    a = app.ScrcpyArgs("t", quality);
    Check(!app.ScrcpyArgs("t", quality with { KeepScreensOn = false }).Contains("--keep-active"), "a remote app keeps its screen on while the device is locked, unless switched off");
    Check(a.Contains("--new-display=1920x1200/240") && !a.Contains("--display-ime-policy=hide") && !a.Contains("--turn-screen-off") && a.Contains("--always-on-top"), "a tablet app: wide screen with tablet DPI, options off");
    app.Window = new WindowPlace { X = -8, Y = 40, W = 500, H = 1000 };
    a = app.ScrcpyArgs("t", quality);
    Check(a.Any(x => x.StartsWith("--new-display=500x1000/")) && a.Contains("--render-fit=letterbox") && a.Contains("--window-x=0") && a.Contains("--window-y=40")
          && !a.Any(x => x.StartsWith("--window-width") || x.StartsWith("--window-height")),
          "a remembered window: same place and exact display size with natural DPI (no --window-width/height with --flex-display)");
    app.Window = null; app.Shape = "custom"; app.CustomWidth = 99999; app.CustomHeight = 801;
    Check(app.ScrcpyArgs("t", quality).Any(x => x.StartsWith("--new-display=3840x800/")), "a custom size is kept within limits and even");
    Check(RemoteApp.ShortcutFileName("Chat: \"work\" / home?") == "Chat   work    home.lnk" && RemoteApp.ShortcutFileName("...") == "App.lnk"
          && RemoteApp.ShortcutFileName(new string('x', 200)).Length == 84, "shortcut file names are safe for Windows");

    var listed = AdbClient.ParseLauncherApps("com.b.music\tMusic\r\nno application stand-in: x\ncom.a.chat\tChat\nbad line\ncom.c.empty\t\n");
    Check(listed.Count == 3 && listed[0].Name == "Chat" && listed.Any(x => x.Package == "com.c.empty" && x.Name == "com.c.empty"), "the helper's app list is read, other output ignored");
    var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 1, 2 };
    var icons = AdbClient.ParseIcons($"com.a.chat\tChat\t{Convert.ToBase64String(png)}\ncom.missing\t\t\ncom.bad\tBad\t!!notbase64\ncom.text\tText\t{Convert.ToBase64String("hello"u8.ToArray())}\n").ToList();
    Check(icons.Count == 1 && icons[0].Package == "com.a.chat" && icons[0].Png.SequenceEqual(png), "the helper's icons are read, broken ones skipped");

    var ico = IcoFile.FromPngs([(16, png), (256, png)]);
    Check(ico.Length == 6 + 32 + 2 * png.Length && BitConverter.ToUInt16(ico, 2) == 1 && BitConverter.ToUInt16(ico, 4) == 2 && ico[6] == 16 && ico[22] == 0
          && BitConverter.ToInt32(ico, 6 + 12) == 38 && BitConverter.ToInt32(ico, 22 + 12) == 38 + png.Length, "the shortcut icon file is laid out right");

    var store = new RemoteAppStore([new RemoteApp { Id = "x", DeviceId = "SN1", Package = "p" }], persist: false);
    var changed = 0; store.Changed += () => changed++; store.Save();
    Check(store.Get("x") != null && store.Find("SN1", "p") != null && store.Find("SN2", "p") == null && changed == 1, "remote apps are found by device and package");
    using (var helper = typeof(AdbClient).Assembly.GetManifestResourceStream("Paneless.Core.paneless-helper.jar"))
        Check(helper is { Length: > 1000 }, "the device helper is built into Paneless");
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

if (args.Contains("--helper-live"))
{
    // Read-only live verification of the persistent helper daemon against a real device (counts/status only; zero private data).
    var prefix = args[Array.IndexOf(args, "--helper-live") + 1];
    var ac = new AdbClient(tools.Adb);
    var dev = (await ac.GetDevicesAsync()).First(d => d.Serial.StartsWith(prefix) && d.State == "device");
    await using var h = await DeviceHelperClient.StartAsync(ac, dev.Serial);
    Console.WriteLine($"connected: v{h.ProtocolVersion} sdk={h.DeviceSdk} commands={h.Commands.Count}");
    var apps = await h.GetLauncherAppsAsync();
    Console.WriteLine($"apps: {apps?.Count ?? -1}");
    var files = await h.ListFilesAsync("/sdcard");
    Console.WriteLine($"files(/sdcard): {files?.Count ?? -1}");
    var tasks = await h.ListTasksAsync();
    Console.WriteLine($"tasks: {tasks?.Count ?? -1}");
    var scrOn = await h.IsScreenOnAsync();
    Console.WriteLine($"screen_state: on={scrOn}");
    var inv = await h.GetInventoryAsync("smoke");
    Console.WriteLine($"inventory: {(inv == null ? "FAIL" : $"{inv.Manufacturer} {inv.Model} Android {inv.AndroidVersion} bat={inv.BatteryLevel}% ram={inv.RamTotalMb}MB")}");
    var det = await h.GetDetailedAppsAsync(false);
    Console.WriteLine($"apps_detail: {det?.Count ?? -1}");
    var cam = await h.GetCameraOrientationsAsync();
    Console.WriteLine($"camera_info: back={cam?.Back} front={cam?.Front}");
    var sys = await h.GetSystemStateAsync();
    Console.WriteLine($"sys_get: {(sys == null ? "FAIL" : $"dark={sys.DarkMode} bright={sys.Brightness} dnd={sys.Dnd} music={sys.MusicVolume}/{sys.MusicMax}")}");
    var media = await h.GetMediaStateAsync();
    Console.WriteLine($"media_state: {(media == null ? "FAIL" : $"playing={media.Playing}")}");
    var clipOk = (await h.CallAsync("clipboard_get")).Ok;
    Console.WriteLine($"clipboard_get: ok={clipOk}");
    var uiRes = await h.GetUiTreeAsync(maxNodes: 20);
    Console.WriteLine($"ui_tree: ok={uiRes.Ok} count={uiRes.Data["count"]} err={uiRes.Error}");
    await h.CallAsync("ui_detach");
    return 0;
}

if (args.Contains("--helper"))
{
    var okAll = true;
    void Check(bool ok, string what) { if (!ok) okAll = false; Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); }

    // Pair two in-memory streams (client <-> fake daemon) to exercise the exact framing + DeviceHelperClient logic offline.
    var c2s = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    var s2c = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    Stream MakeStream(System.Threading.Channels.ChannelReader<byte[]> r, System.Threading.Channels.ChannelWriter<byte[]> w) =>
        new PipePairStream(r, w);

    var serverStream = MakeStream(c2s.Reader, s2c.Writer);
    var clientStream = MakeStream(s2c.Reader, c2s.Writer);

    var serverTask = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var frame = await HelperProtocol.ReadFrameAsync(serverStream, CancellationToken.None);
                if (frame == null) break;
                var id = frame.Header["id"]!.GetValue<long>();
                var cmd = frame.Header["cmd"]!.ToString();
                switch (cmd)
                {
                    case "ping":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["version"] = HelperProtocol.ProtocolVersion, ["sdk"] = 37,
                            ["commands"] = new System.Text.Json.Nodes.JsonArray(
                                "ping", "apps", "echo_bin", "push_test", "fail_test",
                                "ui_tree", "ui_action", "clipboard_get", "clipboard_set", "clipboard_watch",
                                "files_ls", "files_thumb", "files_read", "files_write", "files_delete", "files_mkdir", "media_watch",
                                "tasks_list", "task_move", "launch_intent",
                                "capture", "screen_state", "display_watch",
                                "inventory", "apps_detail", "telemetry_watch",
                                "camera_info", "camera_watch", "locate",
                                "sys_get", "sys_set", "media_state", "media_control", "notifications_watch"),
                        }, null, CancellationToken.None);
                        break;
                    case "ui_tree":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["displayId"] = 0, ["package"] = "com.example.app", ["count"] = 1,
                            ["nodes"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = 1, ["text"] = "Save", ["clickable"] = true }),
                        }, null, CancellationToken.None);
                        break;
                    case "ui_action":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["performed"] = true, ["bounds"] = new System.Text.Json.Nodes.JsonArray(0, 0, 100, 40),
                        }, null, CancellationToken.None);
                        break;
                    case "clipboard_get":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["ok"] = true, ["text"] = "clip-val" }, null, CancellationToken.None);
                        break;
                    case "clipboard_set":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["ok"] = true }, null, CancellationToken.None);
                        break;
                    case "clipboard_watch":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["ok"] = true, ["watching"] = frame.Header["enabled"]!.GetValue<bool>() }, null, CancellationToken.None);
                        break;
                    case "files_ls":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["path"] = "/sdcard/DCIM/Camera",
                            ["entries"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                            {
                                ["name"] = "IMG_1.jpg", ["path"] = "/sdcard/DCIM/Camera/IMG_1.jpg", ["isDir"] = false, ["size"] = 5, ["modifiedMs"] = 1700000000000L, ["kind"] = "image",
                            }),
                        }, null, CancellationToken.None);
                        break;
                    case "files_thumb":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["ok"] = true, ["mime"] = "image/jpeg" }, [0xFF, 0xD8, 0xFF, 0xD9], CancellationToken.None);
                        break;
                    case "files_read":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["ok"] = true, ["totalSize"] = 5, ["offset"] = 0, ["eof"] = true }, "hello"u8.ToArray(), CancellationToken.None);
                        break;
                    case "tasks_list":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true,
                            ["tasks"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                            {
                                ["taskId"] = 77, ["displayId"] = 2, ["package"] = "com.example.chat", ["activity"] = ".Main", ["label"] = "Chat", ["visible"] = true,
                            }),
                        }, null, CancellationToken.None);
                        break;
                    case "task_move":
                    case "launch_intent":
                    case "display_watch":
                    case "telemetry_watch":
                    case "camera_watch":
                    case "sys_set":
                    case "media_control":
                    case "notifications_watch":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["ok"] = true }, null, CancellationToken.None);
                        break;
                    case "capture":
                        var off = frame.Header["displayId"]?.GetValue<int>() == 99;
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["screenOff"] = off, ["sourceWidth"] = 1080, ["sourceHeight"] = 2400,
                        }, off ? null : [0xFF, 0xD8, 0xFF, 0xD9], CancellationToken.None);
                        break;
                    case "inventory":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["manufacturer"] = "Google", ["model"] = "Pixel", ["androidVersion"] = "17", ["sdk"] = 37,
                            ["securityPatch"] = "2026-11-05", ["serial"] = "SN99", ["batteryLevel"] = 84, ["charging"] = true, ["batteryHealthCode"] = 2,
                            ["batteryTempC"] = 31.5, ["storageTotalMb"] = 100000L, ["storageFreeMb"] = 40000L, ["ramTotalMb"] = 12288L,
                            ["screen"] = "1280x2856", ["ipAddress"] = "192.168.1.40", ["uptimeSeconds"] = 7200L,
                        }, null, CancellationToken.None);
                        break;
                    case "apps_detail":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true,
                            ["apps"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                            {
                                ["package"] = "com.example.chat", ["label"] = "Chat", ["versionName"] = "2.1.0", ["versionCode"] = 210L, ["disabled"] = false, ["isSystem"] = false, ["apkSizeBytes"] = 5_000_000L,
                            }),
                        }, null, CancellationToken.None);
                        break;
                    case "camera_info":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["ok"] = true, ["backOrientation"] = 90, ["frontOrientation"] = 270 }, null, CancellationToken.None);
                        break;
                    case "locate":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject { ["id"] = id, ["ok"] = true, ["flashed"] = true, ["vibrated"] = true, ["chimed"] = true }, null, CancellationToken.None);
                        break;
                    case "sys_get":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["darkMode"] = true, ["brightness"] = 180, ["autoBrightness"] = false,
                            ["autoRotate"] = true, ["dnd"] = false, ["wifi"] = true, ["bluetooth"] = true,
                            ["volumes"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["music"] = new System.Text.Json.Nodes.JsonObject { ["current"] = 10, ["max"] = 15 },
                                ["ring"] = new System.Text.Json.Nodes.JsonObject { ["current"] = 5, ["max"] = 7 },
                            },
                        }, null, CancellationToken.None);
                        break;
                    case "media_state":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["playing"] = true, ["title"] = "Song", ["artist"] = "Band", ["album"] = "LP", ["package"] = "com.spotify.music",
                        }, null, CancellationToken.None);
                        break;
                    case "apps":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true,
                            ["apps"] = new System.Text.Json.Nodes.JsonArray(
                                new System.Text.Json.Nodes.JsonObject { ["package"] = "com.example.b", ["name"] = "Beta" },
                                new System.Text.Json.Nodes.JsonObject { ["package"] = "com.example.a", ["name"] = "Alpha" }),
                        }, null, CancellationToken.None);
                        break;
                    case "echo_bin":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true, ["len"] = frame.Binary.Length,
                        }, frame.Binary.Reverse().ToArray(), CancellationToken.None);
                        break;
                    case "push_test":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["event"] = "clipboard", ["text"] = "hello-from-phone",
                        }, [1, 2, 3], CancellationToken.None);
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true,
                        }, null, CancellationToken.None);
                        break;
                    case "fail_test":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = false, ["error"] = "simulated error",
                        }, null, CancellationToken.None);
                        break;
                    case "shutdown":
                        await HelperProtocol.WriteFrameAsync(serverStream, new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = id, ["ok"] = true,
                        }, null, CancellationToken.None);
                        return;
                }
            }
        }
        finally { s2c.Writer.TryComplete(); }
    });

    await using var client = await DeviceHelperClient.FromStreamAsync("test-serial", clientStream);
    Check(client.IsConnected && client.ProtocolVersion == HelperProtocol.ProtocolVersion && client.DeviceSdk == 37, "helper handshake version & sdk");
    Check(client.Supports("apps") && client.Supports("echo_bin") && !client.Supports("nope"), "helper capabilities set");

    var apps = await client.GetLauncherAppsAsync();
    Check(apps != null && apps.Count == 2 && apps[0].Name == "Alpha" && apps[1].Name == "Beta", "helper apps RPC sorts by name");

    var echo = await client.CallAsync("echo_bin", new System.Text.Json.Nodes.JsonObject { ["tag"] = "x" }, [10, 20, 30, 40]);
    Check(echo.Ok && echo.Data["len"]?.GetValue<int>() == 4 && echo.Binary.SequenceEqual(new byte[] { 40, 30, 20, 10 }), "helper binary round-trip");

    var gotEvent = new TaskCompletionSource<HelperEvent>();
    client.EventReceived += ev => gotEvent.TrySetResult(ev);
    var pushRes = await client.CallAsync("push_test");
    var ev = await gotEvent.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(pushRes.Ok && ev.Name == "clipboard" && ev.Data["text"]?.ToString() == "hello-from-phone" && ev.Binary.SequenceEqual(new byte[] { 1, 2, 3 }), "helper push events delivered with binary");

    var fail = await client.CallAsync("fail_test");
    Check(!fail.Ok && fail.Error == "simulated error", "helper error responses propagated");

    // Feature 1: UI tree + action
    var tree = await client.GetUiTreeAsync();
    var act = await client.PerformUiActionAsync("click", nodeId: 1);
    Check(tree.Ok && tree.Data["count"]?.GetValue<int>() == 1 && act.Ok, "helper Feature 1: ui_tree & ui_action");

    // Feature 2: Clipboard
    Check(await client.GetClipboardAsync() == "clip-val" && await client.SetClipboardAsync("abc") && await client.WatchClipboardAsync(true), "helper Feature 2: clipboard get/set/watch");

    // Feature 3: Files & thumbnails + download
    var files = await client.ListFilesAsync("/sdcard/DCIM/Camera");
    var thumb = await client.GetThumbnailAsync("/sdcard/DCIM/Camera/IMG_1.jpg");
    var tmpFile = Path.Combine(Path.GetTempPath(), $"paneless-helper-test-{Guid.NewGuid():n}.txt");
    var dlOk = await client.DownloadFileAsync("/sdcard/DCIM/Camera/IMG_1.jpg", tmpFile);
    var dlText = dlOk && File.Exists(tmpFile) ? await File.ReadAllTextAsync(tmpFile) : "";
    try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }
    Check(files is { Count: 1 } && thumb is { Length: 4 } && dlText == "hello", "helper Feature 3: files_ls, thumb & chunked download");

    // Feature 4: Tasks & handoff
    var tasks = await client.ListTasksAsync();
    Check(tasks is [{ TaskId: 77, DisplayId: 2, Label: "Chat" }] && await client.MoveTaskToDisplayAsync(77, 0) && (await client.LaunchIntentAsync(uri: "https://example.com")).Ok, "helper Feature 4: tasks_list, task_move & launch_intent");

    // Feature 5: Fast Wall capture & screenOff
    var capOn = await client.CaptureJpegAsync(480, 0);
    var capOff = await client.CaptureJpegAsync(480, 99);
    Check(capOn is { ScreenOff: false, Jpeg.Length: 4 } && capOff is { ScreenOff: true, Jpeg: null }, "helper Feature 5: capture JPEG & screenOff");

    // Feature 6: Telemetry inventory & detailed apps
    var inv = await client.GetInventoryAsync("My Pixel");
    var det = await client.GetDetailedAppsAsync();
    Check(inv is { Manufacturer: "Google", BatteryLevel: 84, BatteryHealth: "Good", StorageFreeMb: 40000 } && det is [{ VersionName: "2.1.0", ApkSizeBytes: 5_000_000 }], "helper Feature 6: inventory & apps_detail");

    // Feature 7: Camera orientations & locate
    var cam = await client.GetCameraOrientationsAsync();
    var loc = await client.LocateDeviceAsync();
    Check(cam is { Back: 90, Front: 270 } && await client.WatchCameraOrientationAsync(true) && loc.Ok, "helper Feature 7: camera_info, camera_watch & locate");

    // Feature 8: System controls, media & OTP extraction
    var sys = await client.GetSystemStateAsync();
    var media = await client.GetMediaStateAsync();
    Check(sys is { DarkMode: true, Brightness: 180, MusicVolume: 10, MusicMax: 15 } && await client.SetSystemSettingAsync("darkMode", false), "helper Feature 8: sys_get & sys_set");
    Check(media is { Playing: true, Title: "Song", Artist: "Band" } && await client.ControlMediaAsync("play_pause") && await client.WatchNotificationsAsync(true), "helper Feature 8: media_state, media_control & notifications_watch");
    Check(OtpExtractor.Extract("Your Google verification code is 482910") == "482910"
          && OtpExtractor.Extract("Ihr Sicherheitscode lautet: 918273.") == "918273"
          && OtpExtractor.Extract("Code de vérification : 5512") == "5512"
          && OtpExtractor.Extract("Order #123456 shipped today") == null
          && OtpExtractor.Extract("Call me at 5551234") == null, "helper Feature 8: OtpExtractor EN/DE/FR + false-positive guard");

    // EffectiveProfile resolution for new background sync settings
    var effSync = EffectiveProfile.Resolve(new AppSettings { ClipboardSync = true }, new DeviceProfile { ClipboardSync = false, AutoDownloadMedia = true }, new DeviceProfile { AutoCopyOtp = true });
    Check(!effSync.ClipboardSync && effSync.AutoDownloadMedia && effSync.AutoCopyOtp, "EffectiveProfile resolves ClipboardSync/AutoDownloadMedia/AutoCopyOtp");

    // Check embedded jar is present in Paneless.Core.
    var jarStream = typeof(DeviceHelperClient).Assembly.GetManifestResourceStream("Paneless.Core.paneless-helper.jar");
    Check(jarStream is { Length: > 10000 }, $"embedded paneless-helper.jar present ({jarStream?.Length ?? 0} bytes)");
    jarStream?.Dispose();

    c2s.Writer.TryComplete();
    await serverTask;
    return okAll ? 0 : 1;
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

sealed class PipePairStream(
    System.Threading.Channels.ChannelReader<byte[]> reader,
    System.Threading.Channels.ChannelWriter<byte[]> writer) : Stream
{
    byte[] _buf = [];
    int _pos;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => writer.TryWrite(buffer.AsSpan(offset, count).ToArray());
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { writer.TryWrite(buffer.ToArray()); return ValueTask.CompletedTask; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pos >= _buf.Length)
        {
            if (!await reader.WaitToReadAsync(cancellationToken)) return 0;
            if (reader.TryRead(out var next) && next.Length > 0) { _buf = next; _pos = 0; }
        }
        var n = Math.Min(buffer.Length, _buf.Length - _pos);
        _buf.AsSpan(_pos, n).CopyTo(buffer.Span);
        _pos += n;
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { writer.TryComplete(); base.Dispose(disposing); }
}
