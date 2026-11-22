using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>A window shape a remote app can have; Value is what's saved, Label what's shown.</summary>
public sealed record ShapeChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One remote app in the list; its settings are edited here and saved right away.</summary>
public sealed partial class RemoteAppItem : ObservableObject
{
    readonly RemoteAppsViewModel _page;
    public RemoteApp App { get; }

    public RemoteAppItem(RemoteAppsViewModel page, RemoteApp app)
    {
        _page = page;
        App = app;
        _loading = true;   // filling in the values isn't a change (it would forget the window's place)
        Name = app.Name;
        Shape = RemoteAppsViewModel.Shapes.FirstOrDefault(s => s.Value == app.Shape) ?? RemoteAppsViewModel.Shapes[0];
        CustomWidth = app.CustomWidth;
        CustomHeight = app.CustomHeight;
        ShowToolbar = app.ShowToolbar;
        PcKeyboardOnly = app.PcKeyboardOnly;
        OnTop = app.OnTop;
        ScreenOff = app.ScreenOff;
        _loading = false;
        ReloadIcon();
    }

    readonly bool _loading;

    [ObservableProperty] public partial Bitmap? Icon { get; set; }
    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial ShapeChoice Shape { get; set; }
    [ObservableProperty] public partial decimal? CustomWidth { get; set; }
    [ObservableProperty] public partial decimal? CustomHeight { get; set; }
    [ObservableProperty] public partial bool ShowToolbar { get; set; }
    [ObservableProperty] public partial bool PcKeyboardOnly { get; set; }
    [ObservableProperty] public partial bool OnTop { get; set; }
    [ObservableProperty] public partial bool ScreenOff { get; set; }
    [ObservableProperty] public partial string DeviceText { get; set; } = "";
    [ObservableProperty] public partial IBrush DeviceBrush { get; set; } = Theme.Dim;
    [ObservableProperty] public partial bool IsOpen { get; set; }

    public bool IsCustom => Shape.Value == "custom";
    public bool HasShortcut => App.Shortcuts.Count > 0;
    public bool HasPlace => App.Window != null;
    public string ShortcutButton => HasShortcut ? Loc.T("Update shortcuts") : Loc.T("Create shortcut");

    partial void OnNameChanged(string value)
    {
        if (_loading || string.IsNullOrWhiteSpace(value) || value == App.Name) return;
        App.Name = value.Trim();
        _page.Save();
    }

    partial void OnShapeChanged(ShapeChoice value)
    {
        OnPropertyChanged(nameof(IsCustom));
        if (_loading || App.Shape == value.Value) return;
        App.Shape = value.Value;
        ForgetPlace();
    }

    partial void OnCustomWidthChanged(decimal? value) { if (!_loading && value is { } v && (int)v != App.CustomWidth) { App.CustomWidth = (int)v; ForgetPlace(); } }
    partial void OnCustomHeightChanged(decimal? value) { if (!_loading && value is { } v && (int)v != App.CustomHeight) { App.CustomHeight = (int)v; ForgetPlace(); } }
    partial void OnShowToolbarChanged(bool value) { if (!_loading) { App.ShowToolbar = value; _page.Save(); } }
    partial void OnPcKeyboardOnlyChanged(bool value) { if (!_loading) { App.PcKeyboardOnly = value; _page.Save(); } }
    partial void OnOnTopChanged(bool value) { if (!_loading) { App.OnTop = value; _page.Save(); } }
    partial void OnScreenOffChanged(bool value) { if (!_loading) { App.ScreenOff = value; _page.Save(); } }

    /// <summary>A new size: the old window's shape doesn't fit any more.</summary>
    void ForgetPlace()
    {
        App.Window = null;
        OnPropertyChanged(nameof(HasPlace));
        _page.Save();
    }

    public void ReloadIcon() => Icon = RemoteAppIcons.Load(App.Id) ?? RemoteAppIcons.LetterTile(App.Name, 128);

    public void Changed()
    {
        OnPropertyChanged(nameof(HasShortcut));
        OnPropertyChanged(nameof(HasPlace));
        OnPropertyChanged(nameof(ShortcutButton));
    }
}

/// <summary>An app on the device that can be added (the "Add an app" list).</summary>
public sealed partial class AddableApp(string package, string name, bool added) : ObservableObject
{
    public string Package { get; } = package;
    public string Name { get; } = name;
    public bool Added { get; } = added;
    public string Note => Added ? Loc.T("added") : Package;
    [ObservableProperty] public partial Bitmap? Icon { get; set; }
}

/// <summary>The Remote apps page: Android apps that open in windows of their own, with their own shortcuts.</summary>
public sealed partial class RemoteAppsViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    List<AddableApp> _addAll = [];
    int _loadGeneration;

    public static readonly IReadOnlyList<ShapeChoice> Shapes =
    [
        new("phone", Loc.T("Phone (tall)")),
        new("tablet", Loc.T("Tablet (wide)")),
        new("custom", Loc.T("Custom size")),
    ];
    public IReadOnlyList<ShapeChoice> ShapeChoices => Shapes;

    public RemoteAppsViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        foreach (var a in _s.RemoteApps.All) Items.Add(new RemoteAppItem(this, a));
        Selected = Items.FirstOrDefault();
        UpdateStates();
        _main.DevicesChanged += UpdateStates;
    }

    /// <summary>Set by the window.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<RemoteAppItem> Items { get; } = new();
    public bool IsEmpty => Items.Count == 0;
    public bool CanCreateShortcuts => OperatingSystem.IsWindows();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(RemoveCommand), nameof(ShortcutCommand), nameof(ForgetPlaceCommand), nameof(ReloadIconCommand))]
    public partial RemoteAppItem? Selected { get; set; }
    public bool HasSelected => Selected != null;
    partial void OnSelectedChanged(RemoteAppItem? value) => OnPropertyChanged(nameof(HasSelected));

    [ObservableProperty] public partial bool OnDesktop { get; set; } = true;
    [ObservableProperty] public partial bool InStartMenu { get; set; } = true;
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial IBrush MessageBrush { get; set; } = Theme.Dim;

    void Say(string text, StatusKind kind = StatusKind.Info) { Message = text; MessageBrush = Theme.For(kind); }

    public void Save() => _s.RemoteApps.Save();

    /// <summary>Device and open/closed state of every app (devices come and go, windows open and close).</summary>
    public void UpdateStates()
    {
        foreach (var item in Items)
        {
            var row = _main.RowFor(item.App);
            item.IsOpen = _s.Mirrors?.RemoteAppSession(item.App.Id) != null;
            var (state, brush) = row == null ? (Loc.T("Not on the network"), Theme.Dim) : Theme.Look(row.State);
            item.DeviceText = $"{_main.DeviceNameFor(item.App)} · {(item.IsOpen ? Loc.T("Open") : state)}";
            item.DeviceBrush = item.IsOpen ? Theme.Accent : brush;
            item.Changed();   // a window that just closed has a place to remember now
        }
    }

    bool CanAct() => Selected != null;

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task Open()
    {
        var item = Selected!;
        Say(Loc.F("Opening {0}…", item.App.Name));
        var why = await _main.OpenRemoteAppAsync(item.App);
        if (why != null) Say(Loc.F("Can't open {0}: {1}", item.App.Name, why), StatusKind.Warn);
        else Say(_s.Demo ? Loc.F("Demo mode: would open {0}.", item.App.Name) : Loc.F("{0} is open in its own window.", item.App.Name), StatusKind.Ok);
        UpdateStates();
    }

    /// <summary>Opens a remote app from the list by double-click.</summary>
    public Task OpenItemAsync(RemoteAppItem item) { Selected = item; return Open(); }

    [RelayCommand(CanExecute = nameof(CanAct))]
    void ForgetPlace()
    {
        Selected!.App.Window = null;
        Selected.Changed();
        Save();
        Say(Loc.F("{0} opens in its usual size and place next time.", Selected.App.Name), StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task ReloadIcon()
    {
        var item = Selected!;
        if (_s.Demo) { Say(Loc.T("Demo mode: nothing was changed."), StatusKind.Ok); return; }
        var row = _main.RowFor(item.App);
        if (row is not { CanMirror: true }) { Say(Loc.F("{0} needs to be on the network to read the icon.", _main.DeviceNameFor(item.App)), StatusKind.Warn); return; }
        Say(Loc.F("Reading the icon of {0}…", item.App.Name));
        var png = await ReadIconAsync(row, item.App.Package);
        RemoteAppIcons.Save(item.App, png);
        item.ReloadIcon();
        Say(png != null ? Loc.F("Updated the icon of {0}.", item.App.Name) : Loc.F("{0} didn't give its icon; it gets a letter instead.", _main.DeviceNameFor(item.App)),
            png != null ? StatusKind.Ok : StatusKind.Warn);
    }

    async Task<byte[]?> ReadIconAsync(DeviceRow row, string package)
    {
        try
        {
            var serial = await _s.Directory!.ConnectAsync(row);
            if (serial == null) return null;
            var icons = await _s.Adb!.GetAppIconsAsync(serial, 256, [package]);
            return icons.TryGetValue(package, out var i) ? i.Png : null;
        }
        catch { return null; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task Shortcut()
    {
        var item = Selected!;
        var app = item.App;
        if (!OperatingSystem.IsWindows()) { Say(Loc.T("Shortcuts work on Windows for now."), StatusKind.Warn); return; }
        if (!OnDesktop && !InStartMenu) { Say(Loc.T("Tick where the shortcut should go: the desktop, the Start menu or both."), StatusKind.Warn); return; }
        if (_s.Demo) { Say(Loc.T("Demo mode: nothing was changed."), StatusKind.Ok); return; }
        try
        {
            if (!File.Exists(RemoteAppStore.IconPng(app.Id))) RemoteAppIcons.Save(app, null);
            else RemoteAppIcons.WriteIco(app);   // the name (letter tile) may have changed
            foreach (var old in app.Shortcuts) { try { File.Delete(old); } catch { } }
            app.Shortcuts.Clear();
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Paneless.exe");
            var file = RemoteApp.ShortcutFileName(app.Name);
            var description = Loc.F("{0} on {1}, opened with Paneless", app.Name, _main.DeviceNameFor(app));
            var folders = new List<string>();
            if (OnDesktop) folders.Add(Platform.ShellLinks.DesktopFolder);
            if (InStartMenu) folders.Add(Platform.ShellLinks.StartMenuFolder);
            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, file);
                Platform.ShellLinks.CreateShortcut(path, exe, "--open-app " + app.Id, RemoteAppStore.IconIco(app.Id), description, AppUserModelId(app));
                app.Shortcuts.Add(path);
            }
            Save();
            item.Changed();
            Say(OnDesktop && InStartMenu ? Loc.F("Shortcuts for {0} are on the desktop and in the Start menu (folder “Paneless Remote apps”).", app.Name)
                : OnDesktop ? Loc.F("A shortcut for {0} is on the desktop.", app.Name)
                : Loc.F("A shortcut for {0} is in the Start menu (folder “Paneless Remote apps”).", app.Name), StatusKind.Ok);
        }
        catch (Exception ex) { Say(Loc.T("Couldn't create the shortcut: ") + ex.Message, StatusKind.Error); }
    }

    /// <summary>The taskbar identity of a remote app: its window and its shortcuts share it, so they're one taskbar button.</summary>
    public static string AppUserModelId(RemoteApp app) => "Paneless.RemoteApp." + app.Id;

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task Remove()
    {
        var item = Selected!;
        var text = item.HasShortcut
            ? Loc.F("Remove {0} from Remote apps? Its shortcuts are deleted too. The app stays on the device.", item.App.Name)
            : Loc.F("Remove {0} from Remote apps? The app stays on the device.", item.App.Name);
        if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Remove remote app"), text)) return;
        if (!_s.Demo)
        {
            foreach (var f in item.App.Shortcuts) { try { File.Delete(f); } catch { } }
            RemoteAppIcons.Delete(item.App.Id);
        }
        _s.RemoteApps.All.Remove(item.App);
        Save();
        var index = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        OnPropertyChanged(nameof(IsEmpty));
        Say(Loc.F("Removed {0}.", item.App.Name), StatusKind.Ok);
    }

    // ---------------------------------------------------------------- adding

    [ObservableProperty] public partial bool IsAdding { get; set; }
    [ObservableProperty] public partial bool IsLoadingApps { get; set; }
    [ObservableProperty] public partial string AddFilter { get; set; } = "";
    [ObservableProperty] public partial string AddMessage { get; set; } = "";
    [ObservableProperty] public partial DeviceRow? AddDevice { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmAddCommand))]
    public partial AddableApp? AddSelected { get; set; }
    public ObservableCollection<DeviceRow> AddDevices { get; } = new();
    public ObservableCollection<AddableApp> AddApps { get; } = new();

    partial void OnAddFilterChanged(string value) => ShowAddApps();
    partial void OnAddDeviceChanged(DeviceRow? value) { if (value != null && IsAdding) _ = LoadAddAppsAsync(value); }

    [RelayCommand]
    void StartAdd()
    {
        AddDevices.Clear();
        foreach (var r in _main.NetworkRows.Where(r => r.CanMirror && r.Sn != null).OrderBy(r => r.Name, NaturalComparer.Instance)) AddDevices.Add(r);
        IsAdding = true;
        AddFilter = "";
        if (AddDevices.Count == 0) { AddApps.Clear(); AddMessage = Loc.T("No device is connected. Connect one on the Devices page first."); return; }
        var preferred = Selected != null ? AddDevices.FirstOrDefault(r => r.GroupId == Selected.App.DeviceId) : null;
        if (AddDevice == (preferred ?? AddDevices[0]) && AddDevice != null) _ = LoadAddAppsAsync(AddDevice);
        else AddDevice = preferred ?? AddDevices[0];
    }

    [RelayCommand]
    void CancelAdd() { IsAdding = false; _loadGeneration++; }

    async Task LoadAddAppsAsync(DeviceRow row)
    {
        var generation = ++_loadGeneration;
        IsLoadingApps = true;
        AddApps.Clear();
        AddMessage = Loc.F("Reading the apps of {0}…", row.Name);
        try
        {
            bool Added(string p) => _s.RemoteApps.Find(row.GroupId, p) != null;
            if (_s.Demo)
            {
                string[] demo = ["Calculator", "Calendar", "Camera", "Chat", "Clock", "Files", "Maps", "Music", "Notes", "Photos", "Settings", "Weather"];
                _addAll = demo.Select(n => new AddableApp("com.example." + n.ToLowerInvariant(), n, Added("com.example." + n.ToLowerInvariant()))
                    { Icon = RemoteAppIcons.LetterTile(n, 64) }).ToList();
                ShowAddApps();
                AddMessage = Loc.F("{0} apps on {1}. Pick one and click Add.", _addAll.Count, row.Name);
                return;
            }
            var serial = await _s.Directory!.ConnectAsync(row);
            if (generation != _loadGeneration) return;
            if (serial == null) { AddMessage = Loc.F("Couldn't connect to {0}.", row.Name); return; }
            var apps = await _s.Adb!.GetLauncherAppsAsync(serial);
            var namesKnown = apps != null;
            // The helper couldn't run: fall back to the package names of the apps you installed.
            apps ??= (await _s.Adb.GetPackagesAsync(serial, false)).Select(p => new LauncherApp(p.Name, p.Name)).ToList();
            if (generation != _loadGeneration) return;
            _addAll = apps.Select(a => new AddableApp(a.Package, a.Name, Added(a.Package)) { Icon = RemoteAppIcons.LetterTile(a.Name, 64) }).ToList();
            ShowAddApps();
            AddMessage = namesKnown ? Loc.F("{0} apps on {1}. Pick one and click Add.", _addAll.Count, row.Name)
                : Loc.F("{0} apps on {1} (names and icons couldn't be read). Pick one and click Add.", _addAll.Count, row.Name);
            IsLoadingApps = false;
            if (!namesKnown) return;
            // Icons arrive in batches, so the list is usable right away.
            foreach (var chunk in _addAll.Chunk(25))
            {
                var icons = await _s.Adb.GetAppIconsAsync(serial, 64, chunk.Select(a => a.Package));
                if (generation != _loadGeneration) return;
                foreach (var a in chunk)
                    if (icons.TryGetValue(a.Package, out var i) && RemoteAppIcons.FromPng(i.Png) is { } bmp) a.Icon = bmp;
            }
        }
        catch (Exception ex) { if (generation == _loadGeneration) AddMessage = Loc.T("Couldn't read the app list: ") + ex.Message; }
        finally { if (generation == _loadGeneration) IsLoadingApps = false; }
    }

    void ShowAddApps()
    {
        var q = AddFilter.Trim();
        AddApps.Clear();
        foreach (var a in _addAll.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Package.Contains(q, StringComparison.OrdinalIgnoreCase)))
            AddApps.Add(a);
    }

    bool CanAdd() => AddSelected != null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    async Task ConfirmAdd()
    {
        if (AddSelected is not { } a || AddDevice is not { } row) return;
        await AddAsync(row, a.Package, a.Name);
        IsAdding = false;
    }

    /// <summary>Adds an app of a device (also from the app manager). Returns what happened, for a status line.</summary>
    public async Task<string> AddAsync(DeviceRow row, string package, string? name = null)
    {
        if (_s.RemoteApps.Find(row.GroupId, package) is { } existing)
        {
            Selected = Items.FirstOrDefault(i => i.App == existing);
            var already = Loc.F("{0} is already one of your remote apps.", existing.Name);
            Say(already, StatusKind.Info);
            return already;
        }
        var app = new RemoteApp
        {
            DeviceId = row.GroupId, Package = package, Name = name ?? package,
            Shape = row.Type == "Tablet" ? "tablet" : "phone",
        };
        byte[]? png = null;
        if (!_s.Demo)
        {
            Say(Loc.F("Reading the icon of {0}…", app.Name));
            var serial = await _s.Directory!.ConnectAsync(row);
            if (serial != null)
            {
                try
                {
                    var icons = await _s.Adb!.GetAppIconsAsync(serial, 256, [package]);
                    if (icons.TryGetValue(package, out var i)) { png = i.Png; if (name == null && i.Name.Length > 0) app.Name = i.Name; }
                }
                catch { }
            }
            RemoteAppIcons.Save(app, png);
        }
        _s.RemoteApps.All.Add(app);
        Save();
        var item = new RemoteAppItem(this, app);
        Items.Add(item);
        Selected = item;
        OnPropertyChanged(nameof(IsEmpty));
        UpdateStates();
        var done = Loc.F("Added {0}. Open it, or create a shortcut for it.", app.Name);
        Say(done, StatusKind.Ok);
        return done;
    }
}
