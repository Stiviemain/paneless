using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Paneless.App.ViewModels;
using Paneless.App.Views;
using Paneless.Core;

namespace Paneless.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>A mistake in one feature must not close Paneless (and every mirror's toolbar with it):
    /// unexpected errors are written to %TEMP%\paneless\errors.log and shown in the status line.</summary>
    static void KeepRunningOnErrors(Func<MainViewModel?> vm)
    {
        void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Paneless.Core.AppPaths.LogDir);
                File.AppendAllText(Path.Combine(Paneless.Core.AppPaths.LogDir, "errors.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}" + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log(e.Exception);
            e.Handled = true;
            vm()?.Report(Localization.Loc.F("Something went wrong: {0} (details in %TEMP%\\paneless\\errors.log)", e.Exception.Message), StatusKind.Error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log(ex); };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var demo = args.Contains("--demo");
            var snapshot = ArgAfter(args, "--snapshot");
            var view = ArgAfter(args, "--snapshot-view") ?? "main";

            var services = new AppServices(demo);
            // Language and theme first: every window reads its texts and colours while it's built.
            Localization.Loc.Init(ArgAfter(args, "--lang") ?? services.Data.Settings.Language);
            Paneless.Core.Text.Translate = Localization.Loc.T;
            Theme.Apply(ArgAfter(args, "--theme") ?? services.Data.Settings.Theme);

            // One Paneless per user: a second start brings the first one forward.
            if (snapshot == null && !demo && !SingleInstance.TryClaim())
            {
                desktop.Shutdown();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var vm = new MainViewModel(services);
            KeepRunningOnErrors(() => vm);
            var window = new MainWindow(services) { DataContext = vm };
            desktop.MainWindow = window;

            if (snapshot != null)
                window.Opened += async (_, _) => await SnapshotAsync(desktop, window, services, view, snapshot);
            else
            {
                SetUpTray(desktop, window, vm, services);
                // Started with Windows: stay in the notification area (once, so clicking the tray icon can show it later).
                if (args.Contains("--tray"))
                {
                    EventHandler? hideOnce = null;
                    hideOnce = (_, _) => { window.Opened -= hideOnce; window.Hide(); };
                    window.Opened += hideOnce;
                }
                window.Closing += (_, e) =>
                {
                    if (_quitting || !services.Data.Settings.CloseToTray) return;
                    e.Cancel = true;
                    window.Hide();
                };
                if (!demo)
                {
                    SingleInstance.Listen(() => Dispatcher.UIThread.Post(() =>
                    {
                        window.Show();
                        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                        window.Activate();
                    }));
                    // Updates from GitHub Releases: shortly after start-up, then every 6 hours.
                    EventHandler? startUpdates = null;
                    startUpdates = async (_, _) =>
                    {
                        window.Opened -= startUpdates;
                        await Task.Delay(TimeSpan.FromSeconds(20));
                        while (true)
                        {
                            if (services.Data.Settings.AutoUpdate)
                            {
                                var check = await Updates.CheckAsync(services, interactive: false);
                                if (check.Downloaded) vm.Report(check.Message, StatusKind.Ok);
                            }
                            await Task.Delay(TimeSpan.FromHours(6));
                        }
                    };
                    window.Opened += startUpdates;
                }
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    bool _quitting;

    /// <summary>The notification-area icon: open Paneless, mirror a device, settings, quit.</summary>
    void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel vm, AppServices services)
    {
        void ShowWindow()
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        var mirrorMenu = new NativeMenu();
        var mirrorItem = new NativeMenuItem(Localization.Loc.T("Mirror")) { Menu = mirrorMenu };
        var open = new NativeMenuItem(Localization.Loc.T("Open Paneless"));
        open.Click += (_, _) => ShowWindow();
        var settings = new NativeMenuItem(Localization.Loc.T("Settings…"));
        settings.Click += (_, _) => { ShowWindow(); window.ShowSettings(); };
        var quit = new NativeMenuItem(Localization.Loc.T("Quit Paneless"));
        quit.Click += (_, _) => { _quitting = true; desktop.Shutdown(); };

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(mirrorItem);
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        // The Mirror submenu lists the devices; rebuilt only when they change.
        var shape = "";
        void UpdateMirrorMenu()
        {
            var rows = vm.NetworkRows.Where(r => r.CanMirror).OrderBy(r => r.Name, NaturalComparer.Instance).ToList();
            var now = string.Join('|', rows.Select(r => r.Key + r.Name + r.State));
            if (now == shape) return;
            shape = now;
            mirrorMenu.Items.Clear();
            if (rows.Count == 0) mirrorMenu.Items.Add(new NativeMenuItem(Localization.Loc.T("No devices found")) { IsEnabled = false });
            foreach (var r in rows)
            {
                var item = new NativeMenuItem(r.State == DeviceState.Mirroring ? Localization.Loc.F("{0} (mirroring)", r.Name) : r.Name);
                var id = r.GroupId;
                item.Click += (_, _) => _ = vm.MirrorByIdAsync(id);
                mirrorMenu.Items.Add(item);
            }
        }
        UpdateMirrorMenu();
        vm.DevicesChanged += UpdateMirrorMenu;

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Paneless/Assets/Paneless.ico"))),
            ToolTipText = "Paneless",
            Menu = menu,
            IsVisible = true,
        };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, [tray]);
        desktop.Exit += (_, _) => tray.Dispose();
    }

    static string? ArgAfter(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Testing aid: docks a toolbar to a stand-in window (no device involved) and reports where it ends up.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static async Task DockTestAsync(MainWindow main, string path)
    {
        var log = new System.Text.StringBuilder();
        var fake = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
            "$f.Text = 'Stand-in mirror'; $f.StartPosition = 'Manual'; $f.Left = 200; $f.Top = 150; $f.Width = 420; $f.Height = 800; [void]$f.ShowDialog()\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        var session = new Paneless.Core.MirrorSession { Serial = "dock-test", Name = "Stand-in", Process = fake, LogPath = "" };
        var toolbar = main.AddToolbar(session, AppServices.DemoRows()[0]);
        nint hwnd = 0;
        for (var i = 0; i < 50 && hwnd == 0; i++) { await Task.Delay(200); fake.Refresh(); hwnd = fake.MainWindowHandle; }

        void Report(string step)
        {
            var b = Platform.Win32.Bounds(hwnd);
            var scale = toolbar.Screens.ScreenFromVisual(toolbar)?.Scaling ?? 1;
            log.AppendLine($"{step}: mirror={(b is { } r ? $"{r.Left},{r.Top}-{r.Right},{r.Bottom}" : "none")} " +
                           $"toolbar visible={toolbar.IsVisible} pos={toolbar.Position.X},{toolbar.Position.Y} heightPx={Math.Round(toolbar.Height * scale)}");
        }
        await Task.Delay(800); Report("docked");
        log.AppendLine($"docked: order top->bottom = {Platform.Win32.ZOrder([hwnd, toolbar.TryGetPlatformHandle()?.Handle ?? 0], ["mirror", "toolbar"])}");

        // Stacking order: cover the mirror with another window and check the toolbar stays right above the mirror
        // (below the covering window), then bring the mirror back to the front.
        var cover = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
            "$f.Text = 'Cover'; $f.StartPosition = 'Manual'; $f.Left = 150; $f.Top = 120; $f.Width = 700; $f.Height = 700; $f.TopMost = $false; [void]$f.ShowDialog()\"")
            { UseShellExecute = false, CreateNoWindow = true })!;
        nint coverHwnd = 0;
        for (var i = 0; i < 50 && coverHwnd == 0; i++) { await Task.Delay(200); cover.Refresh(); coverHwnd = cover.MainWindowHandle; }
        Platform.Win32.Activate(coverHwnd);
        await Task.Delay(700);
        var tb = toolbar.TryGetPlatformHandle()?.Handle ?? 0;
        log.AppendLine($"toolbar owner = {Platform.Win32.GetWindow(tb, 4):X}, mirror = {hwnd:X}");
        log.AppendLine($"covered: order top->bottom = {Platform.Win32.ZOrder([coverHwnd, tb, hwnd], ["cover", "toolbar", "mirror"])}");
        Platform.Win32.Raise(hwnd);
        await Task.Delay(700);
        log.AppendLine($"mirror raised: order top->bottom = {Platform.Win32.ZOrder([coverHwnd, tb, hwnd], ["cover", "toolbar", "mirror"])}");
        cover.Kill();
        Platform.Win32.Move(hwnd, 700, 100); await Task.Delay(500); Report("after move");
        Platform.Win32.ShowWindow(hwnd, 6 /* minimise */); await Task.Delay(500); Report("minimised");
        Platform.Win32.ShowWindow(hwnd, 9 /* restore */); await Task.Delay(500); Report("restored");
        fake.Kill(); await Task.Delay(600);
        log.AppendLine($"mirror closed: toolbar still open={toolbar.IsVisible || toolbar.IsActive}");
        await File.WriteAllTextAsync(path + ".dock.txt", log.ToString());
    }

    /// <summary>Testing aid: talks to Paneless through the real Claude bridge ("Paneless.exe --mcp") in demo mode,
    /// like Claude Desktop does, and writes the conversation to a log. Touches no device.</summary>
    static async Task McpSelfTestAsync(MainWindow main, string path)
    {
        var log = new System.Text.StringBuilder();
        main.StartAssistantServerForTest();
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--mcp")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        async Task<string> Ask(string json)
        {
            log.AppendLine(">> " + json);
            await p.StandardInput.WriteLineAsync(json);
            await p.StandardInput.FlushAsync();
            var readTask = p.StandardOutput.ReadLineAsync();
            var line = await Task.WhenAny(readTask, Task.Delay(15000)) == readTask ? await readTask ?? "(closed)" : "(no answer in 15 s)";
            log.AppendLine("<< " + (line.Length > 1500 ? line[..1500] + "…" : line));
            log.AppendLine();
            return line;
        }
        await Ask("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"selftest","version":"1"}}}""");
        await p.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await Ask("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await Ask("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_devices","arguments":{}}}""");
        await Ask("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_groups","arguments":{}}}""");
        await Ask("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"device_info","arguments":{"device":"Lab phone 03"}}}""");
        await Ask("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"run_command","arguments":{"device":"Pixel 11 Pro","command":"echo hi"}}}""");
        await Ask("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"no_such_tool","arguments":{}}}""");
        await Ask("""{"jsonrpc":"2.0","id":8,"method":"ping"}""");
        p.StandardInput.Close();
        p.WaitForExit(5000);
        try { if (!p.HasExited) p.Kill(); } catch { }
        await File.WriteAllTextAsync(path + ".mcp.txt", log.ToString());
    }

    /// <summary>Testing aid: renders a window to a PNG and exits (--snapshot file.png [--snapshot-view main|pair|unlock]).</summary>
    static async Task SnapshotAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow main, AppServices s, string view, string path)
    {
        await Task.Delay(2500);   // let the first refresh finish
        Window target = main;
        if (view == "pair")
        {
            var w = new PairWindow { DataContext = new PairViewModel(s.Adb, "192.168.2.41:41234") };
            w.Show(main); target = w;
        }
        else if (view == "unlock")
        {
            var w = new UnlockWindow { DataContext = new UnlockViewModel(null, AppServices.DemoRows()[0]) };
            w.Show(main); target = w;
        }
        else if (view == "dock" && OperatingSystem.IsWindows())
        {
            await DockTestAsync(main, path);
            desktop.Shutdown();
            return;
        }
        else if (view is "toolbar" or "toolbar2")
        {
            var row = AppServices.DemoRows()[0];
            var session = new Paneless.Core.MirrorSession { Serial = "demo", Name = row.Name, Process = System.Diagnostics.Process.GetCurrentProcess(), LogPath = "",
                Kind = view == "toolbar2" ? Paneless.Core.SessionKind.Screen : Paneless.Core.SessionKind.Mirror };
            var tb = new MirrorToolbar(session, new ToolbarViewModel(s, session, row)) { Height = 640, Position = new PixelPoint(60, 60) };
            tb.Show(); target = tb;
        }
        else if (view == "keymap")
        {
            var row = AppServices.DemoRows()[0];
            var ed = main.OpenKeymapEditor("demo", row, null);
            ed.ShowDemo(); target = ed;
        }
        else if (view is "scripts" or "scripts-run")
        {
            if (main.DeviceListCount > 0) main.SelectFirst();
            target = main.ShowScripts();
            if (target.DataContext is ScriptsViewModel svm)
            {
                var name = view == "scripts" ? "Exempt an app from battery optimisation" : "Battery status";
                svm.SelectedEntry = svm.Items.OfType<ScriptEntry>().FirstOrDefault(e => e.Script.Name == name);
                if (view == "scripts-run") await svm.RunCommand.ExecuteAsync(null);   // read-only script, first selected device
            }
        }
        else if (view.StartsWith("page-") && main.DataContext is MainViewModel pvm)
        {
            // A page of the main window: page-wall, page-logs, page-inventory, page-claude, page-automation[-1|-2] (tab).
            main.Height = 780; main.Width = 1240;
            var parts = view[5..].Split('-');
            pvm.Page = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
            await Task.Delay(400);
            if (parts.Length > 1 && main.GetVisualDescendants().OfType<TabControl>().FirstOrDefault() is { } tabs) tabs.SelectedIndex = int.Parse(parts[1]);
            if (pvm.IsLogsPage && main.Logs is { } logs) { logs.Device = logs.Devices.FirstOrDefault(); await logs.ToggleCommand.ExecuteAsync(null); }
            if (pvm.IsInventoryPage && main.Inventory is { } inv) await inv.Refresh();
            await Task.Delay(800);
        }
        else if (view == "wall-menu" && main.DataContext is MainViewModel wvm)
        {
            // The Wall in demo mode: a tile's right-click menu (is every item wired?), then moving, resizing and hiding, logged.
            main.Height = 780; main.Width = 1240;
            wvm.Page = "Wall";
            await Task.Delay(1500);
            var log = new System.Text.StringBuilder();
            var wall = main.Wall!;
            string Order() => string.Join(", ", wall.Tiles.Select(t => $"{t.Name} {t.Width:0}{(t.HasOwnSize ? "*" : "")}{(t.ScreenOff ? " off" : "")}"));
            log.AppendLine("tiles: " + Order());
            if (main.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("tile")) is { ContextMenu: { } menu } tile)
            {
                menu.Open(tile);
                await Task.Delay(400);
                async Task Dump(IEnumerable<object> items, string indent)
                {
                    foreach (var mi in items.OfType<MenuItem>().ToList())
                    {
                        log.AppendLine($"{indent}{mi.Header} visible={mi.IsVisible} enabled={mi.IsEffectivelyEnabled} command={(mi.Command != null ? "yes" : mi.Items.Count > 0 ? "submenu" : "NO")}");
                        if (mi.Items.Count == 0) continue;
                        mi.Open();   // submenu items are only enabled once they're shown
                        await Task.Delay(300);
                        await Dump(mi.Items.OfType<object>(), indent + "  ");
                        mi.Close();
                    }
                }
                log.AppendLine($"menu open={menu.IsOpen} for {(tile.DataContext as WallTile)?.Name}");
                await Dump(menu.Items.OfType<object>(), "  ");
                menu.Close();
            }
            else log.AppendLine("FAIL no tile with a context menu");
            var first = wall.Tiles[0];
            wall.MoveLastCommand.Execute(first); log.AppendLine("moved first to the end: " + Order());
            wall.LargerCommand.Execute(first); log.AppendLine("made it larger: " + Order());
            wall.SmallerCommand.Execute(wall.Tiles[0]); wall.SmallerCommand.Execute(wall.Tiles[0]);
            wall.MakeAllThisSizeCommand.Execute(wall.Tiles[0]); log.AppendLine("made all the size of a smaller one: " + Order());
            wall.HideCommand.Execute(wall.Tiles[0]); log.AppendLine($"hid the first: {Order()} | hidden={wall.HiddenCount} button=\"{wall.ShowHiddenText}\"");
            await wall.ShowHiddenCommand.ExecuteAsync(null); log.AppendLine("show hidden: " + Order());
            wall.LargerCommand.Execute(first); wall.SameSizeCommand.Execute(first); wall.SortByNameCommand.Execute(null);
            log.AppendLine("larger, then same size as the others, sorted by name: " + Order());
            await File.WriteAllTextAsync(path + ".wall.txt", log.ToString());
            await Task.Delay(500);
        }
        else if (view == "mcp-selftest" && main.DataContext is MainViewModel)
        {
            await McpSelfTestAsync(main, path);
            desktop.Shutdown();
            return;
        }
        else if (view == "settings") target = main.ShowSettings();
        else if (view == "device-settings") target = main.ShowDeviceSettings("A", "Pixel 11 Pro", null);
        else if (view == "group-settings") target = main.ShowDeviceSettings(null, "Test lab", s.Groups.Find("Test lab"));
        else if (view == "apps") { target = main.ShowApps(AppServices.DemoRows()[0]); await Task.Delay(500); }
        else if (view == "menus")
        {
            // Opens every drop-down of the main window and reports whether it showed up with items.
            var log = new System.Text.StringBuilder();
            foreach (var b in main.GetVisualDescendants().OfType<DropDownButton>().ToList())
            {
                b.Flyout!.ShowAt(b);
                await Task.Delay(300);
                var open = b.Flyout is Avalonia.Controls.Primitives.PopupFlyoutBase p && p.IsOpen;
                var items = b.Flyout is MenuFlyout mf ? mf.Items.Count : -1;
                log.AppendLine($"{b.Content}: open={open} menuItems={items}");
                b.Flyout.Hide();
                await Task.Delay(200);
            }
            await File.WriteAllTextAsync(path + ".menus.txt", log.ToString());
        }
        else if (view == "groups" && main.DataContext is MainViewModel gm)
        {
            // The group manager on "Test lab"; then a few clicks' worth of changes, logged.
            var w = main.ShowGroups(s.Groups.Find("Test lab"));
            target = w;
            if (w.DataContext is GroupsViewModel gvm)
            {
                var log = new System.Text.StringBuilder();
                void Dump(string step) => log.AppendLine($"{step}: groups=[{string.Join(", ", gvm.Groups.Select(g => $"{g.Name} ({g.CountText})"))}] " +
                    $"selected={gvm.SelectedGroup?.Name} ticked=[{string.Join(", ", gvm.Devices.Where(d => d.IsMember).Select(d => d.Name))}] " +
                    $"elsewhere=[{string.Join(", ", gvm.Devices.Where(d => d.WhereText.Length > 0).Select(d => $"{d.Name} {d.WhereText}"))}] msg=\"{gvm.Message}\"");
                Dump("opened");
                gvm.Devices.First(d => d.Name == "Pixel Watch 5").IsMember = true; Dump("ticked Pixel Watch 5 (was in My devices)");
                gvm.Devices.First(d => d.Name == "Lab phone 07").IsMember = false; Dump("unticked Lab phone 07");
                gvm.MoveUpCommand.Execute(null); Dump("moved Test lab up");
                gvm.Filter = "lobby"; Dump("filter lobby");
                gvm.Filter = "";
                gvm.Devices.First(d => d.Name == "Lab phone 07").IsMember = true; Dump("ticked Lab phone 07 again");
                await Task.Delay(400);
                log.AppendLine($"main list: [{string.Join(", ", gm.Items.Select(i => i is GroupHeaderViewModel h ? $"<{h.Name}>" : ((DeviceItemViewModel)i).Name))}]");
                await File.WriteAllTextAsync(path + ".groups.txt", log.ToString());
                gvm.SelectedGroup = gvm.Groups.First(g => g.Name == "Test lab");
            }
        }
        else if (view == "groups-flow" && main.DataContext is MainViewModel mvm)
        {
            // Drives the group features like a user would (demo groups live in memory only) and logs the result.
            var log = new System.Text.StringBuilder();
            void Dump(string step) => log.AppendLine($"{step}: list=[{string.Join(", ", mvm.Items.Select(i => i is GroupHeaderViewModel h ? $"<{h.Name}{(h.IsCollapsed ? " +" : "")}>" : ((DeviceItemViewModel)i).Name))}] " +
                $"selected=[{string.Join(", ", mvm.SelectedRows.Select(r => r.Name))}] listbox={main.SelectedCount} mirror=\"{mvm.MirrorLabel}\"");
            main.Height = 1000; await Task.Delay(300);
            main.SelectFirst(2); await Task.Delay(200); Dump("selected 2");
            mvm.PromptAsync = (_, _, _) => Task.FromResult<string?>("Desk");
            await mvm.NewGroupCommand.ExecuteAsync(null); await Task.Delay(300); Dump("new group Desk");
            var lab = mvm.Items.OfType<GroupHeaderViewModel>().First(h => h.Name == "Test lab");
            lab.ToggleCommand.Execute(null); await Task.Delay(300); Dump("collapsed Test lab");
            await mvm.RefreshListAsync(); await Task.Delay(300); Dump("after a refresh");
            mvm.Search = "lobby"; await Task.Delay(300); Dump("search lobby");
            mvm.Search = ""; await Task.Delay(300);
            mvm.MoveToGroupCommand.Execute(null); await Task.Delay(300); Dump("took selection out of group");
            await File.WriteAllTextAsync(path + ".groups.txt", log.ToString());
        }
        else
        {
            if (view == "main-tall") { main.Height = 1000; await Task.Delay(400); }
            if (main.DeviceListCount > 0) main.SelectFirst(view == "main-tall" ? 3 : 1);
        }
        await Task.Delay(600);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var size = new PixelSize((int)target.Bounds.Width, (int)target.Bounds.Height);
            using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(target);
#pragma warning disable CS0618   // the PNG default of the old overload is exactly what a test snapshot needs
            rtb.Save(path);
#pragma warning restore CS0618
        });
        // Which adb/scrcpy this build picked up, for checking packaged builds.
        await File.WriteAllTextAsync(path + ".tools.txt",
            $"adb={s.Tools?.Adb}\nscrcpy={s.Tools?.Scrcpy}\nversion={AppServices.Version}\nbase={AppContext.BaseDirectory}\n");
        desktop.Shutdown();
    }
}
