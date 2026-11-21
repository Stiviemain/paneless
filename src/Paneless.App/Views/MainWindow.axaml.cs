using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Paneless.App.Platform;
using Paneless.App.ViewModels;
using Paneless.Core;

namespace Paneless.App.Views;

public partial class MainWindow : Window
{
    readonly AppServices? _services;
    bool _restoring;

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
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
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
            if (Vm == null) return;
            Vm.ShowSettings = () => ShowSettings();
            Vm.ShowDeviceSettings = (id, name, group) => ShowDeviceSettings(id, name, group);
            Vm.ShowApps = row => ShowApps(row);
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

                // Follow how the phone is held: when a camera's device has been turned another way for ~2 s, reopen the
                // camera turned to match (the window keeps its place).
                var asking = new HashSet<MirrorSession>();
                var pending = new Dictionary<MirrorSession, (int Turn, int Seen)>();
                var follow = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                follow.Tick += (_, _) =>
                {
                    foreach (var cam in mm.Sessions.Where(x => x.Kind == SessionKind.Camera))
                    {
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
                    Vm?.Report($"{next.Name}: reopened, turned to match how the phone is held.", StatusKind.Info));
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
            Vm.ConfirmAsync = (title, message) => ConfirmWindow.AskAsync(this, title, message, null, "Delete");
            Vm.ListSynced += RestoreSelection;
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
            items.Add(new MenuItem { Header = "Take out of its group", Command = vm.MoveToGroupCommand, CommandParameter = null });
            items.Add(new Separator());
        }
        items.Add(new MenuItem { Header = "New group with these devices…", Command = vm.NewGroupCommand });
        items.Add(new MenuItem { Header = "Manage groups…", Command = vm.ManageGroupsCommand });
    }

    async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Files or apps (APK) to send",
            AllowMultiple = true,
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    SettingsWindow? _settings;

    public SettingsWindow ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return _settings; }
        var vm = new SettingsViewModel(_services!);
        vm.Saved += () => Vm?.SettingsChanged();
        vm.CheckUpdatesAsync = () => Updates.CheckAsync(_services!, interactive: true);
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
        vm.OpenCamera = () =>
        {
            _ = Vm?.OpenCameraAsync(Vm.NetworkRows.FirstOrDefault(r => r.GroupId == row.GroupId) ?? row, front: false);
            vm.Hint = "Opening the back camera (Alt+← / Alt+→ in its window turn the picture)…";
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
