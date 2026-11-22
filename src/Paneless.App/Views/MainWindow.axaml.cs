using Paneless.App.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Paneless.App.Assistants;
using Paneless.App.Platform;
using Paneless.App.ViewModels;
using Paneless.Core;

namespace Paneless.App.Views;

public partial class MainWindow : Window
{
    readonly AppServices? _services;
    bool _restoring;
    bool _opened;

    public MainWindow() => InitializeComponent();   // for the XAML previewer

    public MainWindow(AppServices services) : this()
    {
        _services = services;
        DeviceList.SelectionChanged += (_, _) => OnSelectionChanged();
        DeviceList.DoubleTapped += (_, e) =>
        {
            if (IsInHeading(e.Source)) return;
            if (Vm?.MirrorCommand.CanExecute(null) == true) Vm.MirrorCommand.Execute(null);
        };
        DeviceList.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Vm?.MirrorCommand.CanExecute(null) != true) return;
            e.Handled = true;
            Vm.MirrorCommand.Execute(null);
        };
        KeyDown += (_, e) =>
        {
            var ctrlOnly = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)) == KeyModifiers.Control;
            if (!ctrlOnly || Vm == null) return;
            if (e.Key == Key.F && Vm.IsDevicesPage) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
            if (e.Key == Key.OemComma) { Vm.OpenSettingsCommand.Execute(null); e.Handled = true; return; }
            var page = e.Key switch
            {
                Key.D1 => "Devices",
                Key.D2 => "RemoteApps",
                Key.D3 => "Wall",
                Key.D4 => "Automation",
                Key.D5 => "Inventory",
                Key.D6 => "Assistants",
                Key.D7 => "Logs",
                _ => null,
            };
            if (page != null) { Vm.Page = page; e.Handled = true; }
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Vm != null) { Vm.Search = ""; DeviceList.Focus(); e.Handled = true; }
        };

        // Files dropped on a device (or the selected devices, or a group heading) are copied, APKs installed.
        DeviceList.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var target = (e.Source as StyledElement)?.DataContext;
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && target is DeviceItemViewModel or GroupHeaderViewModel
                ? DragDropEffects.Copy : DragDropEffects.None;
        });
        DeviceList.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (Vm == null) return;
            var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            var targets = Vm.DropTargets((e.Source as StyledElement)?.DataContext);
            if (files.Count > 0 && targets.Count > 0) await Vm.SendFilesAsync(files, targets);
        });

        Opened += async (_, _) =>
        {
            if (_opened || Vm == null) return;
            _opened = true;
            Vm.ShowSettings = () => ShowSettings();
            Vm.ShowDeviceSettings = (id, name, group) => ShowDeviceSettings(id, name, group);
            Vm.ShowApps = row => ShowApps(row);
            Vm.ShowFiles = row => ShowFiles(row);
            Vm.ShowControls = row => ShowControls(row);
            Vm.PickFilesAsync = PickFilesAsync;
            Vm.ArrangeWindows = sessions => { if (OperatingSystem.IsWindows()) WindowArranger.Arrange(sessions, TryGetPlatformHandle()?.Handle ?? 0); };
            // Remember where camera windows are, so one the device cuts off (e.g. when it rotates) reopens in place.
            if (OperatingSystem.IsWindows() && _services?.Mirrors is { } mm)
            {
                var track = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                track.Tick += (_, _) =>
                {
                    if (!OperatingSystem.IsWindows()) return;
                    foreach (var cam in mm.Sessions.Where(x => x.Kind == SessionKind.Camera))
                    {
                        try { cam.Process.Refresh(); } catch { continue; }
                        if (cam.Process.MainWindowHandle is var h and not 0 && !Platform.Win32.IsIconic(h) && Platform.Win32.ClientBounds(h) is { } c)
                            mm.LastWindow[cam] = (c.Left, c.Top, c.Right - c.Left, c.Bottom - c.Top);
                    }
                };
                track.Start();

                // Follow how the phone is held: prefer instant helper sensor events while a camera window is open
                // (automatically unregistered when the camera closes so idle battery stays 0%), with dumpsys fallback.
                var asking = new HashSet<MirrorSession>();
                var pending = new Dictionary<MirrorSession, (int Turn, int Seen)>();
                var camWatched = new HashSet<string>();
                var camOrient = new Dictionary<string, (int? Back, int? Front)>();
                if (_services.Helpers != null)
                {
                    _services.Helpers.EventReceived += ev =>
                    {
                        if (ev.Name != "orientation") return;
                        var rot = ev.Data["rotation"]?.GetValue<int>() ?? -1;
                        if (rot is < 0 or > 3) return;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (!camOrient.TryGetValue(ev.Serial, out var orient)) return;
                            foreach (var cam in mm.Sessions.Where(x => x.Kind == SessionKind.Camera && x.Serial == ev.Serial))
                            {
                                if (DateTime.UtcNow - cam.Started < TimeSpan.FromSeconds(2)) continue;
                                var front = MirrorManager.IsFrontCamera(cam);
                                var sensor = front ? orient.Front : orient.Back;
                                if (sensor is not { } s) continue;
                                var deg = rot * 90;
                                var turn = front ? (s + deg) % 360 : (s - deg + 360) % 360;
                                if (turn != MirrorManager.TurnOf(cam))
                                    _ = Task.Run(() => mm.TurnCamera(cam, turn));
                            }
                        });
                    };
                }
                var follow = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                follow.Tick += (_, _) =>
                {
                    var activeCamSerials = mm.Sessions.Where(x => x.Kind == SessionKind.Camera).Select(x => x.Serial).ToHashSet();
                    // Immediately stop sensor listener when camera windows close (zero idle battery drain).
                    foreach (var old in camWatched.Where(s => !activeCamSerials.Contains(s)).ToList())
                    {
                        camWatched.Remove(old);
                        if (_services.Helpers?.TryGet(old) is { } h && h.Supports("camera_watch"))
                            _ = Task.Run(async () => { try { await h.WatchCameraOrientationAsync(false); } catch { } });
                    }
                    foreach (var cam in mm.Sessions.Where(x => x.Kind == SessionKind.Camera))
                    {
                        if (_services.Helpers != null && !camWatched.Contains(cam.Serial))
                        {
                            camWatched.Add(cam.Serial);
                            var s = cam.Serial;
                            _ = Task.Run(async () =>
                            {
                                var h = await _services.Helpers.GetOrStartAsync(s);
                                if (h == null || !h.Supports("camera_watch")) { Avalonia.Threading.Dispatcher.UIThread.Post(() => camWatched.Remove(s)); return; }
                                var o = await h.GetCameraOrientationsAsync();
                                if (o != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => camOrient[s] = o.Value);
                                await h.WatchCameraOrientationAsync(true);
                            });
                        }
                        if (camOrient.ContainsKey(cam.Serial)) continue; // handled live via push events
                        if (asking.Contains(cam) || DateTime.UtcNow - cam.Started < TimeSpan.FromSeconds(3) || _services.Adb is not { } adb) continue;
                        asking.Add(cam);
                        _ = Task.Run(async () =>
                        {
                            int? turn = null;
                            try { turn = await adb.GetCameraUprightTurnAsync(cam.Serial, MirrorManager.IsFrontCamera(cam)); } catch { }
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                asking.Remove(cam);
                                if (turn is not { } t || !cam.IsRunning || t == MirrorManager.TurnOf(cam)) { pending.Remove(cam); return; }
                                var seen = pending.TryGetValue(cam, out var p) && p.Turn == t ? p.Seen + 1 : 1;
                                pending[cam] = (t, seen);
                                if (seen < 2) return;
                                pending.Remove(cam);
                                _ = Task.Run(() => mm.TurnCamera(cam, t));   // waits for the old window to close: not on the UI thread
                            });
                        });
                    }
                };
                follow.Start();
                // When the camera reopens (the device was just turned), turn the picture for the new way it's held.
                mm.CameraTurn = (serial, front) => _services.Adb?.GetCameraUprightTurnAsync(serial, front).GetAwaiter().GetResult();
                mm.Reopened += (_, next) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    Vm?.Report(Loc.F("{0}: reopened, turned to match how the phone is held.", next.Name), StatusKind.Info));
            }
            Vm.BringToFront = session =>
            {
                if (!OperatingSystem.IsWindows()) return;
                session.Process.Refresh();
                if (session.Process.MainWindowHandle is var h and not 0) Platform.Win32.Activate(h);
            };
            // Right-click › Move to group lists your groups; it's rebuilt whenever they change
            // (filling a menu while it opens doesn't work reliably).
            FillMoveMenu();
            Vm.Groups.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(FillMoveMenu);
            Vm.ShowGroups = group => ShowGroups(group);
            Vm.ShowPairDialog = ShowPairAsync;
            Vm.ShowUnlockDialog = ShowUnlockAsync;
            Vm.ShowScripts = keys => ShowScripts(keys);
            Vm.MirrorStarted = (session, row) => AddToolbar(session, row);
            Vm.PromptAsync = (title, message, initial) => PromptWindow.AskAsync(this, title, message, initial);
            Vm.ConfirmAsync = (title, message) => ConfirmWindow.AskAsync(this, title, message, null, Loc.T("Delete"));
            Vm.ListSynced += RestoreSelection;
            SetUpPages(Vm);
            await Vm.StartAsync();
        };
    }

    MainViewModel? Vm => DataContext as MainViewModel;

    void OpenLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => AppLinks.OpenTag(sender, e);

    static bool IsInHeading(object? source) => (source as StyledElement)?.DataContext is GroupHeaderViewModel;

    // ---------------------------------------------------------------- selection

    void OnSelectionChanged()
    {
        if (Vm == null || _restoring || Vm.Syncing) return;
        var selected = DeviceList.SelectedItems?.Cast<object>().ToList() ?? [];
        if (selected.OfType<GroupHeaderViewModel>().Any())
        {
            // Group headings aren't selectable. A right-click or arrow key on one keeps the devices that were selected;
            // Ctrl+A keeps all the devices.
            if (!selected.OfType<DeviceItemViewModel>().Any()) { RestoreSelection(); return; }
            _restoring = true;
            try { foreach (var h in selected.OfType<GroupHeaderViewModel>()) DeviceList.SelectedItems!.Remove(h); }
            finally { _restoring = false; }
        }
        Vm.SetSelection(DeviceList.SelectedItems?.OfType<DeviceItemViewModel>() ?? []);
    }

    /// <summary>Makes the list's selection match the view model's (after a rebuild, or a click on a heading).</summary>
    void RestoreSelection()
    {
        if (Vm == null) return;
        var keys = Vm.SelectedKeys;
        var current = DeviceList.SelectedItems?.Cast<object>().ToList() ?? [];
        if (current.Count == keys.Count && current.All(o => o is DeviceItemViewModel d && keys.Contains(d.Key))) return;
        _restoring = true;
        try
        {
            var sel = DeviceList.Selection;
            sel.BeginBatchUpdate();
            sel.Clear();
            for (var i = 0; i < Vm.Items.Count; i++)
                if (Vm.Items[i] is DeviceItemViewModel d && keys.Contains(d.Key)) sel.Select(i);
            sel.EndBatchUpdate();
        }
        finally { _restoring = false; }
    }

    void FillMoveMenu()
    {
        if (Vm is not { } vm || DeviceList.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "MoveMenu") is not { } move) return;
        var items = move.Items;
        items.Clear();
        foreach (var g in vm.Groups.Groups)
            items.Add(new MenuItem { Header = g.Name, Command = vm.MoveToGroupCommand, CommandParameter = g });
        if (vm.Groups.Any)
        {
            items.Add(new MenuItem { Header = Loc.T("Take out of its group"), Command = vm.MoveToGroupCommand, CommandParameter = null });
            items.Add(new Separator());
        }
        items.Add(new MenuItem { Header = Loc.T("New group with these devices…"), Command = vm.NewGroupCommand });
        items.Add(new MenuItem { Header = Loc.T("Manage groups…"), Command = vm.ManageGroupsCommand });
    }

    // ---------------------------------------------------------------- pages

    public WallViewModel? Wall { get; private set; }
    public LogsViewModel? Logs { get; private set; }
    public InventoryViewModel? Inventory { get; private set; }
    public AutomationViewModel? Automation { get; private set; }
    public AssistantsViewModel? Assistants { get; private set; }
    public RemoteAppsViewModel? RemoteApps { get; private set; }
    McpServer? _mcp;
    HelperCoordinator? _helperCoord;

    void SetUpPages(MainViewModel vm)
    {
        var s = _services!;
        var automation = new AutomationService(s, vm);
        _helperCoord = new HelperCoordinator(s, vm);
        var tools = new AssistantTools(s, vm)
        {
            AskAsync = (title, message) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => AssistantConfirmWindow.AskAsync(title, message)),
        };
        _mcp = new McpServer(tools);

        WallPage.DataContext = Wall = new WallViewModel(s, vm);
        LogsPage.DataContext = Logs = new LogsViewModel(s, vm) { SaveAsAsync = SaveAsAsync };
        InventoryPage.DataContext = Inventory = new InventoryViewModel(s, vm) { SaveAsAsync = SaveAsAsync };
        Automation = new AutomationViewModel(s, vm, automation);
        Automation.Recipes.PickFileAsync = PickOneFileAsync;
        Automation.Recipes.ConfirmAsync = (t, m) => ConfirmWindow.AskAsync(this, t, m, null);
        Automation.Schedules.ConfirmAsync = (t, m) => ConfirmWindow.AskAsync(this, t, m, null, Localization.Loc.T("Delete"));
        AutomationPage.DataContext = Automation;
        AssistantsPage.DataContext = Assistants = new AssistantsViewModel(s, vm, _mcp, tools)
        {
            CopyAsync = async text => { if (Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text); },
            ConfirmAsync = (t, m) => ConfirmWindow.AskAsync(this, t, m, null, Localization.Loc.T("Add")),
        };
        RemoteAppsPage.DataContext = RemoteApps = new RemoteAppsViewModel(s, vm)
        {
            ConfirmAsync = (t, m) => ConfirmWindow.AskAsync(this, t, m, null, Localization.Loc.T("Remove")),
        };
        vm.AddRemoteApp = RemoteApps.AddAsync;
        if (OperatingSystem.IsWindows()) TrackRemoteApps(vm);
        automation.Start();

        // Subtle sidebar status dots: green on AI when active, amber on Logs when crashes were seen.
        void SyncSidebarDots()
        {
            vm.IsAiActive = Assistants is { Enabled: true, Paused: false };
            vm.HasLogCrashes = Logs is { HasCrashes: true };
        }
        Assistants.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AssistantsViewModel.Enabled) or nameof(AssistantsViewModel.Paused)) SyncSidebarDots();
        };
        Logs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogsViewModel.HasCrashes)) SyncSidebarDots();
        };
        SyncSidebarDots();

        // The wall takes pictures only while it's on screen; the inventory shows what it knows when opened.
        void UpdateActive() => Wall?.SetActive(vm.IsWallPage && IsVisible && WindowState != WindowState.Minimized);
        vm.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.Page)) return;
            UpdateActive();
            if (vm.IsInventoryPage && Inventory is { } inv)
            {
                inv.ShowCached();
                if (inv.IsEmpty) await inv.Refresh();
            }
        };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty) UpdateActive(); };
        Closed += (_, _) => { Wall?.SetActive(false); Logs?.Dispose(); _mcp?.Stop(); _helperCoord?.Dispose(); };
    }

    /// <summary>Remote app windows: give each its own icon and taskbar button once it shows, remember where it is, and
    /// save that when it closes.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    void TrackRemoteApps(MainViewModel vm)
    {
        var open = new Dictionary<MirrorSession, (RemoteApp App, int Dressed, int LastW, int LastH)>();
        vm.RemoteAppStarted = (session, app) => { open[session] = (app, 0, 0, 0); RemoteApps?.UpdateStates(); };
        if (_services?.Mirrors != null)
            _services.Mirrors.Failed += (session, why) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (session.RemoteAppId != null && !IsVisible) Toast.Show(session.Name, why);
            });
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (open.Count == 0) return;
            foreach (var (session, (app, dressed, lastW, lastH)) in open.ToList())
            {
                if (!session.IsRunning)
                {
                    open.Remove(session);
                    _services!.RemoteApps.Save();   // keeps the window's last place
                    RemoteApps?.UpdateStates();
                    continue;
                }
                try { session.Process.Refresh(); } catch { continue; }
                var h = session.Process.MainWindowHandle;
                if (h == 0) continue;
                // Dressed twice (when it shows, and a few seconds later) in case the window sets its own icon late.
                if (dressed == 0 || dressed == 4)
                {
                    try
                    {
                        if (!File.Exists(RemoteAppStore.IconIco(app.Id))) RemoteAppIcons.WriteIco(app);
                        var ico = RemoteAppStore.IconIco(app.Id);
                        ShellLinks.SetWindowIcon(h, ico);
                        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Paneless.exe");
                        ShellLinks.SetWindowIdentity(h, RemoteAppsViewModel.AppUserModelId(app), $"\"{exe}\" --open-app {app.Id}", app.Name, ico);
                    }
                    catch { /* cosmetic: the app works without it */ }
                }
                int curW = lastW, curH = lastH;
                if (!Platform.Win32.IsIconic(h) && Platform.Win32.ClientBounds(h) is { } c && c.Right - c.Left > 100 && c.Bottom - c.Top > 100)
                {
                    curW = c.Right - c.Left;
                    curH = c.Bottom - c.Top;
                    app.Window = new WindowPlace { X = c.Left, Y = c.Top, W = curW, H = curH };
                }
                // On tablets with Desktop/Freeform mode (e.g. Xiaomi HyperOS), force the virtual display & task
                // to fill the entire window once the display appears and whenever the window is resized.
                var resized = lastW > 0 && (Math.Abs(curW - lastW) > 12 || Math.Abs(curH - lastH) > 12);
                if (session.DisplayId is { } did && did > 0 && (dressed is 0 or 2 || resized) && _services?.Helpers != null)
                {
                    var serial = session.Serial;
                    var pkg = app.Package;
                    var w = curW; var ht = curH;
                    _ = Task.Run(async () =>
                    {
                        var helper = await _services.Helpers.GetOrStartAsync(serial);
                        if (helper != null && helper.Supports("display_fullscreen"))
                            try { await helper.EnsureDisplayFullscreenAsync(did, pkg, w, ht); } catch { }
                    });
                }
                open[session] = (app, Math.Min(dressed + 1, 5), curW, curH);
            }
        };
        timer.Start();
    }

    /// <summary>A shortcut asked for a remote app (Paneless may have just started hidden).</summary>
    public async Task OpenRemoteAppFromShortcutAsync(string id)
    {
        if (Vm == null) return;
        var why = await Vm.OpenRemoteAppByIdAsync(id);
        if (why == null) return;
        if (IsVisible) { Vm.Page = "RemoteApps"; return; }
        if (OperatingSystem.IsWindows()) Toast.Show("Paneless", why);
    }

    /// <summary>Testing aid (demo mode): starts the AI server even though demo mode normally keeps it off.</summary>
    public void StartAssistantServerForTest() => _mcp?.Start();

    async Task<string?> SaveAsAsync(string suggestedName, string ext)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName + "." + ext,
            DefaultExtension = ext,
            FileTypeChoices = [new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = ["*." + ext] }],
        });
        return file?.TryGetLocalPath();
    }

    async Task<string?> PickOneFileAsync(string title, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = patterns.Contains("*") ? null : [new FilePickerFileType(title) { Patterns = patterns }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = Loc.T("Files or apps (APK) to send"),
            AllowMultiple = true,
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    SettingsWindow? _settings;

    public SettingsWindow ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return _settings; }
        var vm = new SettingsViewModel(_services!);
        vm.Saved += () => { Vm?.SettingsChanged(); _ = _helperCoord?.SyncWatchersAsync(); };
        vm.CheckUpdatesAsync = async () => (await Updates.CheckAsync(_services!, interactive: true)).Message;
        _settings = new SettingsWindow { DataContext = vm };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show(this);
        return _settings;
    }

    readonly Dictionary<string, DeviceSettingsWindow> _deviceSettings = new();

    public DeviceSettingsWindow ShowDeviceSettings(string? deviceId, string name, DeviceGroup? group)
    {
        var key = deviceId ?? "group:" + group?.Name;
        if (_deviceSettings.TryGetValue(key, out var open)) { open.Activate(); return open; }
        var w = new DeviceSettingsWindow { DataContext = new ProfileEditorViewModel(_services!, deviceId, name, group) };
        _deviceSettings[key] = w;
        w.Closed += (_, _) => _deviceSettings.Remove(key);
        w.Show(this);
        return w;
    }

    readonly Dictionary<string, AppsWindow> _apps = new();

    public AppsWindow ShowApps(DeviceRow row)
    {
        if (_apps.TryGetValue(row.Key, out var open)) { open.Activate(); return open; }
        var vm = new AppsViewModel(_services!, Vm!, row);
        var w = new AppsWindow { DataContext = vm };
        _apps[row.Key] = w;
        w.Closed += (_, _) => _apps.Remove(row.Key);
        w.Show(this);
        _ = vm.LoadAsync();
        return w;
    }

    readonly Dictionary<string, FilesWindow> _files = new();

    public FilesWindow ShowFiles(DeviceRow row)
    {
        if (_files.TryGetValue(row.Key, out var open)) { open.Activate(); return open; }
        var vm = new FilesViewModel(_services!, Vm!, row);
        var w = new FilesWindow { DataContext = vm };
        w.AttachDialogs(vm);
        _files[row.Key] = w;
        w.Closed += (_, _) => _files.Remove(row.Key);
        w.Show(this);
        _ = vm.LoadAsync();
        return w;
    }

    readonly Dictionary<string, QuickControlsWindow> _controls = new();

    public QuickControlsWindow ShowControls(DeviceRow row)
    {
        if (_controls.TryGetValue(row.Key, out var open)) { open.Activate(); return open; }
        var vm = new QuickControlsViewModel(_services!, row);
        var w = new QuickControlsWindow { DataContext = vm };
        _controls[row.Key] = w;
        w.Closed += (_, _) => _controls.Remove(row.Key);
        w.Show(this);
        _ = vm.LoadAsync();
        return w;
    }

    GroupsWindow? _groups;

    /// <summary>Opens (or brings forward) the group manager.</summary>
    public GroupsWindow ShowGroups(DeviceGroup? select = null)
    {
        if (_groups == null)
        {
            _groups = new GroupsWindow { DataContext = new GroupsViewModel(_services!, Vm!, select) };
            _groups.Closed += (_, _) => _groups = null;
            _groups.Show(this);
        }
        else
        {
            _groups.Activate();
            if (select != null && _groups.DataContext is GroupsViewModel gvm) gvm.SelectedGroup = gvm.Groups.FirstOrDefault(g => g.Group == select);
        }
        return _groups;
    }

    // ---------------------------------------------------------------- other windows

    ScriptsWindow? _scripts;

    /// <summary>Opens (or brings forward) the Scripts window; with keys, exactly those devices are ticked.</summary>
    public ScriptsWindow ShowScripts(IReadOnlyCollection<string>? tickKeys = null)
    {
        if (_scripts == null)
        {
            _scripts = new ScriptsWindow { DataContext = new ScriptsViewModel(_services!, Vm!, tickKeys) };
            _scripts.Closed += (_, _) => _scripts = null;
            _scripts.Show(this);
        }
        else
        {
            _scripts.Activate();
            if (tickKeys != null && _scripts.DataContext is ScriptsViewModel svm) svm.SelectTargets(tickKeys);
        }
        return _scripts;
    }

    /// <summary>Docks a toolbar to a freshly started mirror window.</summary>
    public MirrorToolbar AddToolbar(MirrorSession session, DeviceRow row)
    {
        var vm = new ToolbarViewModel(_services!, session, row);
        var toolbar = new MirrorToolbar(session, vm);
        vm.OpenScripts = () => ShowScripts([row.Key]);
        vm.OpenKeymapEditor = () => OpenKeymapEditor(session.Serial, row, vm);
        vm.OpenSecondScreen = () => _ = Vm?.OpenSecondScreenAsync(Vm.NetworkRows.FirstOrDefault(r => r.GroupId == row.GroupId) ?? row);
        vm.PopOutCurrentApp = () => _ = Vm?.PopOutCurrentAppAsync(Vm.NetworkRows.FirstOrDefault(r => r.GroupId == row.GroupId) ?? row);
        vm.OpenCamera = () =>
        {
            _ = Vm?.OpenCameraAsync(Vm.NetworkRows.FirstOrDefault(r => r.GroupId == row.GroupId) ?? row, front: false);
            vm.Hint = Loc.T("Opening the back camera (Alt+← / Alt+→ in its window turn the picture)…");
        };
        vm.GroupPeers = () =>
        {
            var group = _services?.Groups.GroupOf(row.GroupId);
            if (group == null || Vm == null) return (null, []);
            var others = Vm.NetworkRows.Where(r => r.GroupId != row.GroupId && group.Members.Contains(r.GroupId)
                                                   && r.State is DeviceState.Connected or DeviceState.Mirroring or DeviceState.Available).ToList();
            return (group.Name, others);
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolbarViewModel.Hint) && vm.Hint.Length > 0) Vm?.Report($"{row.Name}: {vm.Hint}");
        };
        toolbar.Start();
        return toolbar;
    }

    readonly Dictionary<string, KeymapEditor> _editors = new();

    public KeymapEditor OpenKeymapEditor(string serial, DeviceRow row, ToolbarViewModel? toolbar)
    {
        if (_editors.TryGetValue(serial, out var open)) { open.Activate(); return open; }
        var editor = new KeymapEditor(_services!, serial, row, () => { if (toolbar != null) _ = toolbar.ReloadKeymapAsync(); });
        _editors[serial] = editor;
        editor.Closed += (_, _) => _editors.Remove(serial);
        editor.Show();
        return editor;
    }

    // ---------------------------------------------------------------- testing aids (snapshots)

    public int DeviceListCount => Vm?.Items.OfType<DeviceItemViewModel>().Count() ?? 0;
    public int SelectedCount => DeviceList.SelectedItems?.Count ?? 0;

    /// <summary>Selects the first n devices of the list.</summary>
    public void SelectFirst(int n = 1)
    {
        if (Vm == null) return;
        var sel = DeviceList.Selection;
        sel.Clear();
        var picked = 0;
        for (var i = 0; i < Vm.Items.Count && picked < n; i++)
            if (Vm.Items[i] is DeviceItemViewModel) { sel.Select(i); picked++; }
    }

    async Task ShowPairAsync(string? prefill)
    {
        var vm = new PairViewModel(_services?.Adb, prefill);
        var dialog = new PairWindow { DataContext = vm };
        vm.Close = ok => dialog.Close(ok);
        await dialog.ShowDialog<bool>(this);
    }

    async Task ShowUnlockAsync(DeviceRow row)
    {
        var vm = new UnlockViewModel(_services?.Unlock, row);
        var dialog = new UnlockWindow { DataContext = vm };
        vm.Close = () => dialog.Close();
        await dialog.ShowDialog(this);
    }
}
