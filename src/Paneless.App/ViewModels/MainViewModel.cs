using Paneless.App.Localization;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly DispatcherTimer _timer;
    bool _refreshing;
    IReadOnlyList<DeviceRow> _rows = [];
    readonly Dictionary<string, DeviceItemViewModel> _items = new();          // by key: on the network + missing group members
    readonly Dictionary<DeviceGroup, GroupHeaderViewModel> _headers = new();
    GroupHeaderViewModel? _othersHeader;
    readonly HashSet<string> _selectedKeys = new();

    public MainViewModel(AppServices services)
    {
        _s = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => { if (!IsBusy) await RefreshListAsync(); };
        if (_s.Mirrors != null)
            _s.Mirrors.Failed += (session, why) => Dispatcher.UIThread.Post(() =>
                SetStatus(Loc.F("Mirroring {0} stopped. {1}", session.Name, why), StatusKind.Error));
        _s.Groups.Changed += () => Dispatcher.UIThread.Post(Rebuild);
        _s.Profiles.Changed += () => Dispatcher.UIThread.Post(UpdateHotkeys);
        if (OperatingSystem.IsWindows())
            GlobalHotkeys.Pressed += id => Dispatcher.UIThread.Post(() => _ = MirrorByIdAsync(id));
    }

    // ---------------------------------------------------------------- set by the window

    /// <summary>Opens the pair dialog (optionally pre-filled) and waits for it to close.</summary>
    public Func<string?, Task>? ShowPairDialog { get; set; }
    /// <summary>Opens the auto-unlock dialog for a device.</summary>
    public Func<DeviceRow, Task>? ShowUnlockDialog { get; set; }
    /// <summary>Opens (or brings forward) the Scripts window; with keys, exactly those devices are ticked.</summary>
    public Action<IReadOnlyCollection<string>?>? ShowScripts { get; set; }
    /// <summary>A mirror just started (the window adds its toolbar).</summary>
    public Action<MirrorSession, DeviceRow>? MirrorStarted { get; set; }
    /// <summary>Asks for a line of text (title, message, initial value); null = cancelled.</summary>
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }
    /// <summary>Asks to confirm (title, message); true = go ahead.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Opens (or brings forward) the group manager, showing this group.</summary>
    public Action<DeviceGroup?>? ShowGroups { get; set; }
    /// <summary>Raised when the selected devices change.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised after the list was rebuilt: the window re-applies <see cref="SelectedKeys"/>.</summary>
    public event Action? ListSynced;
    /// <summary>Raised after every refresh (the Scripts window keeps its device list up to date with it).</summary>
    public event Action? DevicesChanged;

    // ---------------------------------------------------------------- pages (navigation bar)

    /// <summary>Devices, Wall, Logs, Automation, Inventory or Claude.</summary>
    [ObservableProperty] public partial string Page { get; set; } = "Devices";
    public bool IsDevicesPage => Page == "Devices";
    public bool IsWallPage => Page == "Wall";
    public bool IsLogsPage => Page == "Logs";
    public bool IsAutomationPage => Page == "Automation";
    public bool IsInventoryPage => Page == "Inventory";
    public bool IsAssistantsPage => Page == "Assistants";

    partial void OnPageChanged(string value)
    {
        foreach (var p in new[] { nameof(IsDevicesPage), nameof(IsWallPage), nameof(IsLogsPage), nameof(IsAutomationPage), nameof(IsInventoryPage), nameof(IsAssistantsPage) })
            OnPropertyChanged(p);
    }

    [RelayCommand]
    void Go(string page) => Page = page;

    /// <summary>Runs scripts, recipes, restarts and commands on devices (Recipes, Scheduled tasks, Claude).</summary>
    public DeviceActions Actions => _actions ??= new DeviceActions(_s, () => _rows);
    DeviceActions? _actions;

    /// <summary>The battery reading of a device (by adb serial), if known.</summary>
    public BatteryInfo? BatteryOf(string serial) => _battery.TryGetValue(serial, out var b) ? b.Info : null;

    /// <summary>Lets other windows (toolbars) show a message in the status line.</summary>
    public void Report(string text, StatusKind kind = StatusKind.Info) => SetStatus(text, kind);

    // ---------------------------------------------------------------- what the window shows

    public string Title => $"Paneless {AppServices.Version}";
    public GroupStore Groups => _s.Groups;

    /// <summary>The list as shown: group headings and the devices under them.</summary>
    public ObservableCollection<object> Items { get; } = new();

    /// <summary>Devices on the network (not missing group members), for the Scripts window.</summary>
    public IReadOnlyList<DeviceRow> NetworkRows => _rows;

    public IReadOnlyList<SizeChoice> MaxSizes { get; } = [new("Full resolution", Loc.T("Full resolution")), new("1920 px", "1920 px"), new("1600 px", "1600 px"), new("1280 px", "1280 px"), new("1024 px", "1024 px")];
    public string Tip => OperatingSystem.IsMacOS()
        ? Loc.T("⌘- or Shift-click to select several · double-click to mirror · right-click for more")
        : Loc.T("Ctrl- or Shift-click to select several · double-click to mirror · right-click for more");

    [ObservableProperty] public partial string StatusText { get; set; } = Loc.T("Starting…");
    [ObservableProperty] public partial IBrush StatusBrush { get; set; } = Theme.Dim;
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial string EmptyText { get; set; } = Loc.T("Looking for devices…");
    [ObservableProperty] public partial string ManualAddress { get; set; } = "";
    [ObservableProperty] public partial string MirrorLabel { get; set; } = Loc.T("Mirror");
    [ObservableProperty] public partial string Summary { get; set; } = Loc.T("Plug in a device via USB or turn on Wireless debugging");
    [ObservableProperty] public partial string SelectionText { get; set; } = "";
    [ObservableProperty] public partial string Search { get; set; } = "";

    /// <summary>True while the list is being rebuilt: selection changes then come from the rebuild, not the user.</summary>
    public bool Syncing { get; private set; }

    partial void OnSearchChanged(string value) => Rebuild();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MirrorCommand), nameof(PairCommand), nameof(AutoUnlockCommand),
                                nameof(UnlockNowCommand), nameof(RefreshCommand), nameof(ConnectCommand),
                                nameof(SwitchToWifiCommand))]
    public partial bool IsBusy { get; set; }

    bool Ready => _s.Tools != null || _s.Demo;

    // ---------------------------------------------------------------- mirror options (saved in settings.json)

    AppSettings S => _s.Data.Settings;
    void SaveSettings() { if (_s.Demo) return; try { _s.Data.SaveSettings(); } catch { /* not fatal */ } }

    public bool ScreenOff
    {
        get => S.ScreenOff;
        set
        {
            if (S.ScreenOff == value) return;
            S.ScreenOff = value; SaveSettings();
            OnPropertyChanged(); OnPropertyChanged(nameof(KeepAwake)); OnPropertyChanged(nameof(KeepAwakeEnabled));
        }
    }

    /// <summary>A dark screen still counts as "on" for Android and would time out and lock mid-mirror,
    /// so Keep awake is forced on while the screen is kept off.</summary>
    public bool KeepAwake
    {
        get => S.ScreenOff || S.KeepAwake;
        set { if (S.ScreenOff || S.KeepAwake == value) return; S.KeepAwake = value; SaveSettings(); OnPropertyChanged(); }
    }

    public bool KeepAwakeEnabled => !S.ScreenOff;
    public bool LockOnClose { get => S.LockOnClose; set { if (S.LockOnClose == value) return; S.LockOnClose = value; SaveSettings(); OnPropertyChanged(); } }
    public bool Audio { get => S.Audio; set { if (S.Audio == value) return; S.Audio = value; SaveSettings(); OnPropertyChanged(); } }
    public bool OnTop { get => S.OnTop; set { if (S.OnTop == value) return; S.OnTop = value; SaveSettings(); OnPropertyChanged(); } }

    public SizeChoice MaxSize
    {
        get => MaxSizes.FirstOrDefault(c => c.Value == S.MaxSize) ?? MaxSizes[1];
        set { if (value == null || S.MaxSize == value.Value) return; S.MaxSize = value.Value; SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>Re-reads the mirror options (after the Settings window changed them).</summary>
    public void SettingsChanged()
    {
        OnPropertyChanged(nameof(ScreenOff)); OnPropertyChanged(nameof(KeepAwake)); OnPropertyChanged(nameof(KeepAwakeEnabled));
        OnPropertyChanged(nameof(LockOnClose));
        OnPropertyChanged(nameof(Audio)); OnPropertyChanged(nameof(OnTop)); OnPropertyChanged(nameof(MaxSize));
        UpdateHotkeys();
    }

    /// <summary>A device's own settings, else its group's, else the defaults.</summary>
    MirrorOptions Options(DeviceRow r) => _s.EffectiveFor(r.GroupId).ToMirrorOptions();

    // ---------------------------------------------------------------- start-up and refresh

    public async Task StartAsync()
    {
        if (!Ready)
        {
            EmptyText = Loc.F("scrcpy wasn't found on this computer.\n\n{0}\n\nThen restart Paneless.", ToolLocator.InstallHint);
            SetStatus(Loc.T("scrcpy and adb are needed to mirror devices."), StatusKind.Error);
            return;
        }
        if (!_s.Demo)
        {
            SetStatus(Loc.T("Starting…"));
            await _s.Adb!.StartServerAsync();
            var discovery = await _s.Adb.IsDiscoveryAvailableAsync();
            await RefreshListAsync();
            SetStatus(discovery
                ? Loc.T("Watching the network. The list updates every few seconds.")
                : Loc.T("Automatic discovery isn't available here; use Add device › Connect by address."),
                discovery ? StatusKind.Info : StatusKind.Warn);
        }
        else
        {
            await RefreshListAsync();
            SetStatus(Loc.T("Watching the network. The list updates every few seconds."));
        }
        UpdateHotkeys();
        _timer.Start();
    }

    public async Task RefreshListAsync()
    {
        if (_refreshing || !Ready) return;
        _refreshing = true;
        try
        {
            _rows = _s.Demo ? AppServices.DemoRows() : await _s.Directory!.GetRowsAsync(_s.Mirrors!.ActiveSerials);
            Rebuild();
            Automate();
            UpdateBatteries();
        }
        catch (Exception ex) { SetStatus(Loc.T("Refresh failed: ") + ex.Message, StatusKind.Error); }
        finally { _refreshing = false; }
    }

    /// <summary>Builds the list as shown: one section per group (in your order), then "Other devices".
    /// Without any groups it's a plain list, as before.</summary>
    void Rebuild()
    {
        var groups = _s.Groups;
        var q = Search.Trim();
        var searching = q.Length > 0;

        // Device items: one per device on the network, plus group members that aren't on it.
        var keep = new HashSet<string>();
        foreach (var r in _rows) keep.Add(Upsert(r).Key);
        var onNetwork = _rows.Select(r => r.GroupId).ToHashSet();
        var groupOf = new Dictionary<string, DeviceGroup>();
        foreach (var g in groups.Groups)
            foreach (var id in g.Members)
            {
                groupOf.TryAdd(id, g);
                if (!onNetwork.Contains(id)) keep.Add(Upsert(DeviceRow.Missing(id, _s.KnownFor(id))).Key);
            }
        foreach (var k in _items.Keys.Where(k => !keep.Contains(k)).ToList()) _items.Remove(k);
        foreach (var g in _headers.Keys.Where(g => !groups.Groups.Contains(g)).ToList()) _headers.Remove(g);

        var want = new List<object>();
        void Section(GroupHeaderViewModel? header, List<DeviceItemViewModel> members, bool collapsed)
        {
            var shown = !searching || (header?.IsUserGroup == true && header.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                ? members : members.Where(m => m.Matches(q)).ToList();
            if (searching && shown.Count == 0) return;
            if (header != null)
            {
                header.IsCollapsed = collapsed && !searching;
                header.CountText = CountText(members);
                header.CanMirrorAll = members.Any(m => m.Row.CanMirror && m.Row.State != DeviceState.Mirroring);
                header.Refresh();
                want.Add(header);
                if (header.IsCollapsed) return;
            }
            foreach (var m in shown.OrderBy(Rank).ThenBy(m => m.Name, NaturalComparer.Instance))
            {
                m.InGroup = header != null;
                want.Add(m);
            }
        }

        var all = _items.Values.ToList();
        foreach (var g in groups.Groups)
        {
            if (!_headers.TryGetValue(g, out var h)) _headers[g] = h = new GroupHeaderViewModel(this, g);
            Section(h, all.Where(i => groupOf.GetValueOrDefault(i.Row.GroupId) == g).ToList(), g.Collapsed);
        }
        var others = all.Where(i => !groupOf.ContainsKey(i.Row.GroupId)).ToList();
        if (groups.Any)
        {
            _othersHeader ??= new GroupHeaderViewModel(this, null);
            if (others.Count > 0) Section(_othersHeader, others, groups.Data.OthersCollapsed);
        }
        else Section(null, others, false);

        Syncing = true;
        try
        {
            SyncList(Items, want);
            _selectedKeys.IntersectWith(want.OfType<DeviceItemViewModel>().Select(d => d.Key));
            ListSynced?.Invoke();
        }
        finally { Syncing = false; }

        IsEmpty = Items.Count == 0;
        if (IsEmpty)
            EmptyText = searching ? Loc.F("No devices match \"{0}\".", q)
                : Loc.T("No devices found yet.\n\nPlug a device in via USB (with USB debugging on), or turn on\nSettings › System › Developer options › Wireless debugging.\n\nFirst time over Wi-Fi? Click Add device › Pair with a code.");
        var mirroring = _rows.Count(r => r.State == DeviceState.Mirroring);
        Summary = _rows.Count == 0 ? Loc.T("Plug in a device via USB or turn on Wireless debugging")
            : Loc.Count(_rows.Count, "{0} device", "{0} devices")
              + (mirroring > 0 ? Loc.F(" · {0} mirroring", mirroring) : "")
              + (groups.Any ? $" · {Loc.Count(groups.Groups.Count, "{0} group", "{0} groups")}" : "");
        UpdateSelectionState();
        DevicesChanged?.Invoke();
    }

    DeviceItemViewModel Upsert(DeviceRow r)
    {
        if (_items.TryGetValue(r.Key, out var item)) item.Row = r;
        else _items[r.Key] = item = new DeviceItemViewModel(r);
        if (r.Serial != null && _battery.TryGetValue(r.Serial, out var b) && r.State is DeviceState.Connected or DeviceState.Mirroring)
            item.Battery = b.Info;
        else if (r.State is not (DeviceState.Connected or DeviceState.Mirroring)) item.Battery = null;
        return item;
    }

    // ---------------------------------------------------------------- battery

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (BatteryInfo? Info, DateTime At)> _battery = new();
    readonly HashSet<string> _batteryBusy = new();

    /// <summary>Reads the battery of each connected device every 30 seconds, in the background.</summary>
    void UpdateBatteries()
    {
        foreach (var r in _rows.Where(r => r.Serial != null && r.State is DeviceState.Connected or DeviceState.Mirroring))
        {
            var serial = r.Serial!;
            if (_batteryBusy.Contains(serial) || (_battery.TryGetValue(serial, out var b) && DateTime.UtcNow - b.At < TimeSpan.FromSeconds(30))) continue;
            if (_s.Demo)
            {
                var level = 35 + Math.Abs(serial.GetHashCode() % 64);
                _battery[serial] = (new BatteryInfo(level, level % 3 == 0, false), DateTime.UtcNow);
                if (_items.TryGetValue(r.Key, out var di)) di.Battery = _battery[serial].Info;
                continue;
            }
            _batteryBusy.Add(serial);
            _ = Task.Run(async () =>
            {
                BatteryInfo? info = null;
                try { info = await _s.Adb!.GetBatteryAsync(serial); } catch { }
                Dispatcher.UIThread.Post(() =>
                {
                    _batteryBusy.Remove(serial);
                    _battery[serial] = (info, DateTime.UtcNow);
                    if (_items.TryGetValue(r.Key, out var item)) item.Battery = info;
                });
            });
        }
    }

    // ---------------------------------------------------------------- when devices connect

    readonly HashSet<string> _handledSerials = new();             // connections whose on-connect actions already ran
    readonly Dictionary<string, DateTime> _connectTried = new();  // by device id: last automatic connect attempt

    /// <summary>Connects known devices by themselves, runs their on-connect scripts once per connection,
    /// and starts mirroring for devices set to mirror by themselves.</summary>
    void Automate()
    {
        if (_s.Demo || _s.Directory == null) return;
        var connected = _rows.Where(r => r.Serial != null && r.State is DeviceState.Connected or DeviceState.Mirroring).ToList();
        _handledSerials.IntersectWith(connected.Select(r => r.Serial!));   // a device that left counts as new when it's back

        if (S.AutoConnect && !IsBusy)
            foreach (var r in _rows.Where(r => r.State == DeviceState.Available && r.Sn != null))
            {
                if (_connectTried.TryGetValue(r.GroupId, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(30)) continue;
                _connectTried[r.GroupId] = DateTime.UtcNow;
                _ = AutoConnectAsync(r);
            }

        foreach (var r in connected)
        {
            if (!_handledSerials.Add(r.Serial!)) continue;
            var p = _s.EffectiveFor(r.GroupId);
            if (p.OnConnectScripts.Count > 0) _ = RunOnConnectAsync(r, p.OnConnectScripts);
            if (p.AutoMirror && r.State == DeviceState.Connected) _ = MirrorRowsAsync([r]);
        }
    }

    async Task AutoConnectAsync(DeviceRow r)
    {
        try
        {
            if (await _s.Directory!.ConnectAsync(r) != null)
            {
                SetStatus(Loc.F("Connected to {0}.", r.Name), StatusKind.Ok);
                await RefreshListAsync();
            }
        }
        catch { /* tried again in 30 s */ }
    }

    async Task RunOnConnectAsync(DeviceRow r, IReadOnlyList<string> scriptIds)
    {
        var all = _s.AllScripts();
        var scripts = scriptIds.Select(id => all.FirstOrDefault(s => s.Id == id)).OfType<AdbScript>().ToList();
        if (scripts.Count == 0 || _s.Adb == null) return;
        var runner = new ScriptRunner(_s.Adb);
        var results = new List<string>();
        var log = new System.Text.StringBuilder();
        foreach (var sc in scripts)
        {
            var values = ScriptText.Placeholders(sc.Commands).ToDictionary(p => p.Name, p => p.Default ?? "");
            try
            {
                var res = await runner.RunAsync(r.Serial!, r.Name, sc, values);
                results.Add($"{sc.Name} {(res.Ok ? "✓" : "✗")}");
                log.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {r.Name}: {sc.Name} {(res.Ok ? "ok" : "FAILED")}");
                foreach (var st in res.Steps) log.AppendLine($"  $ {st.Command}\n  {st.Result.Output.Trim().Replace("\n", "\n  ")}{(st.Error != null ? "\n  ! " + st.Error : "")}");
            }
            catch (Exception ex) { results.Add($"{sc.Name} ✗"); log.AppendLine($"{r.Name}: {sc.Name} error {ex.Message}"); }
        }
        try { Directory.CreateDirectory(AppPaths.LogDir); await File.AppendAllTextAsync(Path.Combine(AppPaths.LogDir, "on-connect.log"), log.ToString()); } catch { }
        var ok = results.All(x => x.EndsWith('✓'));
        Dispatcher.UIThread.Post(() => SetStatus(Loc.F("{0} connected: ran {1}", r.Name, string.Join(", ", results)), ok ? StatusKind.Ok : StatusKind.Warn));
    }

    // ---------------------------------------------------------------- files

    /// <summary>Installs APKs and copies other files to devices (from a drop on the list or "Send files…").</summary>
    public async Task SendFilesAsync(IReadOnlyList<string> files, IReadOnlyList<DeviceRow> targets)
    {
        var rows = targets.Where(t => t.CanMirror).ToList();
        if (files.Count == 0) return;
        if (rows.Count == 0) { SetStatus(Loc.T("None of those devices is on the network."), StatusKind.Warn); return; }
        if (_s.Demo) { SetStatus(Loc.F("Demo mode: would send {0} to {1}.", Loc.Count(files.Count, "{0} file", "{0} files"), Loc.Count(rows.Count, "{0} device", "{0} devices")), StatusKind.Ok); return; }
        var apks = files.Count(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        SetStatus(Loc.F("Sending {0} to {1}…", Loc.Count(files.Count, "{0} file", "{0} files"), (rows.Count == 1 ? rows[0].Name : Loc.Count(rows.Count, "{0} device", "{0} devices"))));
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(rows.Select(async r =>
        {
            var serial = await _s.Directory!.ConnectAsync(r);
            if (serial == null) { failures.Add(Loc.F("{0}: couldn't connect", r.Name)); return; }
            var target = _s.EffectiveFor(r.GroupId).PushTarget;
            foreach (var f in files)
            {
                var res = f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                    ? await _s.Adb!.InstallAsync(serial, f)
                    : await _s.Adb!.PushAsync(serial, f, target);
                if (!res.Ok) failures.Add($"{r.Name}: {Path.GetFileName(f)} – {res.LastLine}");
            }
        }));
        var what = apks == files.Count ? Loc.F("Installed {0}", Loc.Count(apks, "{0} app", "{0} apps")) : apks == 0 ? Loc.F("Copied {0}", Loc.Count(files.Count, "{0} file", "{0} files")) : Loc.F("Installed {0} and copied {1}", Loc.Count(apks, "{0} app", "{0} apps"), Loc.Count(files.Count - apks, "{0} file", "{0} files"));
        if (failures.IsEmpty) SetStatus(Loc.F("{0} on {1}.", what, (rows.Count == 1 ? rows[0].Name : Loc.Count(rows.Count, "{0} device", "{0} devices"))), StatusKind.Ok);
        else SetStatus(Loc.F("Some didn't work: {0}", string.Join("; ", failures.Take(3))), StatusKind.Error);
    }

    /// <summary>The devices a drop on this list item means: a group's devices, or the selection if the item is part of it.</summary>
    public IReadOnlyList<DeviceRow> DropTargets(object? item) => item switch
    {
        GroupHeaderViewModel h => Members(h).Select(m => m.Row).ToList(),
        DeviceItemViewModel d when _selectedKeys.Contains(d.Key) => SelectedRows,
        DeviceItemViewModel d => [d.Row],
        _ => [],
    };

    /// <summary>Usable devices first, then ones that still need pairing, then group members that aren't around.</summary>
    static int Rank(DeviceItemViewModel d) => d.Row.State == DeviceState.NotFound ? 2 : d.Row.NeedsPairing ? 1 : 0;

    static string CountText(List<DeviceItemViewModel> members)
    {
        if (members.Count == 0) return Loc.T("empty: right-click a device › Move to group");
        var online = members.Count(m => !m.IsMissing);
        var mirroring = members.Count(m => m.Row.State == DeviceState.Mirroring);
        var text = online == members.Count ? Loc.Count(members.Count, "{0} device", "{0} devices") : Loc.F("{0} of {1} on the network", online, members.Count);
        return mirroring > 0 ? Loc.F("{0} · {1} mirroring", text, mirroring) : text;
    }


    /// <summary>Makes <paramref name="target"/> equal to <paramref name="want"/> with as few changes as possible,
    /// so the list keeps its scroll position.</summary>
    static void SyncList(ObservableCollection<object> target, List<object> want)
    {
        var wanted = new HashSet<object>(want, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < want.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], want[i])) continue;
            var at = -1;
            for (var j = i + 1; j < target.Count; j++) if (ReferenceEquals(target[j], want[i])) { at = j; break; }
            if (at >= 0) target.RemoveAt(at);
            target.Insert(i, want[i]);
        }
        while (target.Count > want.Count) target.RemoveAt(target.Count - 1);
    }

    void SetStatus(string text, StatusKind kind = StatusKind.Info)
    {
        StatusText = text;
        StatusBrush = Theme.For(kind);
    }

    // ---------------------------------------------------------------- selection

    public IReadOnlyCollection<string> SelectedKeys => _selectedKeys;

    public void SetSelection(IEnumerable<DeviceItemViewModel> items)
    {
        _selectedKeys.Clear();
        foreach (var i in items) _selectedKeys.Add(i.Key);
        UpdateSelectionState();
    }

    List<DeviceItemViewModel> Selected => _selectedKeys.Select(k => _items.GetValueOrDefault(k)).OfType<DeviceItemViewModel>().ToList();
    public IReadOnlyList<DeviceRow> SelectedRows => Selected.Select(s => s.Row).ToList();
    public bool SelectionHasGroup => Selected.Any(s => _s.Groups.GroupOf(s.Row.GroupId) != null);

    /// <summary>The selected devices; with nothing selected and just one device around, that one.</summary>
    IReadOnlyList<DeviceRow> Targets =>
        _selectedKeys.Count > 0 ? SelectedRows
        : _rows.Count == 1 ? [_rows[0]] : [];

    DeviceRow? UnlockTarget => Selected is [{ Row.CanSetUpUnlock: true } one] ? one.Row : null;

    void UpdateSelectionState()
    {
        var sel = Selected;
        MirrorLabel = sel is [{ Row.NeedsPairing: true }] ? Loc.T("Pair…") : sel.Count > 1 ? Loc.F("Mirror {0}", sel.Count) : Loc.T("Mirror");
        SelectionText = sel.Count > 1 ? Loc.F("{0} selected", sel.Count) : "";
        MirrorCommand.NotifyCanExecuteChanged();
        AutoUnlockCommand.NotifyCanExecuteChanged();
        UnlockNowCommand.NotifyCanExecuteChanged();
        MoveToGroupCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSwitchToWifi));
        SwitchToWifiCommand.NotifyCanExecuteChanged();
        SelectionChanged?.Invoke();
    }

    /// <summary>Opens the group manager at the group of the first selected device.</summary>
    [RelayCommand]
    void ManageGroups() => ShowGroups?.Invoke(Selected.Select(s => _s.Groups.GroupOf(s.Row.GroupId)).FirstOrDefault(g => g != null));

    /// <summary>Selects exactly these devices (expanding their group so they can be seen).</summary>
    void Select(IEnumerable<string> keys)
    {
        _selectedKeys.Clear();
        _selectedKeys.UnionWith(keys);
        Rebuild();
    }

    // ---------------------------------------------------------------- mirroring

    bool CanMirror() => Ready && !IsBusy && Targets.Count > 0;

    [RelayCommand(CanExecute = nameof(CanMirror))]
    async Task MirrorAsync()
    {
        var targets = Targets;
        var mirrorable = targets.Where(r => r.CanMirror).ToList();
        if (mirrorable.Count == 0)
        {
            var r = targets[0];
            if (r.NeedsPairing) { if (ShowPairDialog != null) await ShowPairDialog(r.PairAddr); await RefreshListAsync(); }
            else if (r.State == DeviceState.Unauthorized) SetStatus(Loc.F("Unlock {0} and tap Allow on the debugging prompt.", r.Name), StatusKind.Warn);
            else if (r.State == DeviceState.NotFound) SetStatus(Loc.F("{0} isn't connected. Plug it in via USB or turn on Wireless debugging (same Wi-Fi as this computer).", r.Name), StatusKind.Warn);
            else SetStatus(!r.IsNetwork
                ? Loc.F("{0} is offline. Unplug and reconnect the USB cable, or toggle USB debugging.", r.Name)
                : Loc.F("{0} is offline. Turn Wireless debugging off and on again on the device.", r.Name), StatusKind.Warn);
            return;
        }
        await MirrorRowsAsync(mirrorable);
    }

    async Task MirrorRowsAsync(IReadOnlyList<DeviceRow> rows)
    {
        IsBusy = true;
        try { foreach (var r in rows) await MirrorOneAsync(r); }
        catch (Exception ex) { SetStatus(Loc.T("Couldn't start mirroring: ") + ex.Message, StatusKind.Error); }
        finally { IsBusy = false; }
        await RefreshListAsync();
    }

    async Task MirrorOneAsync(DeviceRow r)
    {
        if (r.State == DeviceState.Mirroring) { SetStatus(Loc.F("{0} is already being mirrored.", r.Name), StatusKind.Warn); return; }
        if (_s.Demo) { SetStatus(Loc.F("Demo mode: would mirror {0}.", r.Name), StatusKind.Ok); return; }
        if (r.State == DeviceState.Available) SetStatus(Loc.F("Connecting to {0}…", r.Name));
        var serial = await _s.Directory!.ConnectAsync(r);
        if (serial == null)
        {
            SetStatus(Loc.F("Couldn't connect to {0}. If Wireless debugging was reset on it, pair it again.", r.Name), StatusKind.Error);
            return;
        }
        string? note = null;
        var opts = Options(r);
        if (_s.Unlock!.IsReady(r.Sn))
        {
            SetStatus(Loc.F("Unlocking {0}…", r.Name));
            var res = await _s.Unlock.UnlockAsync(serial, r.Sn!, r.Name, wakeIfUnlocked: !opts.ScreenOff);
            if (!res.Ok) note = res.Message;
        }
        var session = _s.Mirrors!.Start(serial, r.Name, opts);
        MirrorStarted?.Invoke(session, r);
        if (note != null) SetStatus(Loc.F("Mirroring {0}, but: {1}", r.Name, note), StatusKind.Warn);
        else SetStatus(Loc.F("Mirroring {0}. Close its window to stop.", r.Name), StatusKind.Ok);
    }

    // ---------------------------------------------------------------- other device actions

    bool CanAct() => Ready && !IsBusy;

    [RelayCommand]
    void RunScript() => ShowScripts?.Invoke(_selectedKeys.Count > 0 ? SelectedRows.Select(r => r.Key).ToList() : null);

    [RelayCommand]
    void OpenScripts() => ShowScripts?.Invoke(null);

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task PairAsync()
    {
        if (ShowPairDialog == null) return;
        await ShowPairDialog(Selected.Select(s => s.Row.PairAddr).FirstOrDefault(a => a != null));
        await RefreshListAsync();
    }

    bool CanSetUpUnlock() => Ready && !IsBusy && UnlockTarget != null;

    [RelayCommand(CanExecute = nameof(CanSetUpUnlock))]
    async Task AutoUnlockAsync()
    {
        if (UnlockTarget is { } row && ShowUnlockDialog != null) await ShowUnlockDialog(row);
        await RefreshListAsync();
    }

    bool CanUnlockNow() => CanSetUpUnlock() && _s.Unlock != null && _s.Unlock.IsReady(UnlockTarget!.Sn);

    [RelayCommand(CanExecute = nameof(CanUnlockNow))]
    async Task UnlockNowAsync()
    {
        var row = UnlockTarget!;
        IsBusy = true;
        try
        {
            if (row.State == DeviceState.Available) SetStatus(Loc.F("Connecting to {0}…", row.Name));
            var serial = await _s.Directory!.ConnectAsync(row);
            if (serial == null) { SetStatus(Loc.F("Couldn't connect to {0}.", row.Name), StatusKind.Error); return; }
            SetStatus(Loc.F("Unlocking {0}…", row.Name));
            var res = await _s.Unlock!.UnlockAsync(serial, row.Sn!, row.Name);
            SetStatus(res.Message, res.Ok ? StatusKind.Ok : StatusKind.Warn);
        }
        finally { IsBusy = false; }
        await RefreshListAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task RefreshAsync()
    {
        await RefreshListAsync();
        SetStatus(Loc.T("List refreshed."));
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    async Task ConnectAsync()
    {
        var addr = ManualAddress.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(addr, @"^\d{1,3}(\.\d{1,3}){3}$")) addr += ":5555";
        if (!AdbClient.IsIpPort(addr))
        {
            SetStatus(Loc.T("Type the IP address and port shown under Wireless debugging, e.g. 192.168.2.40:37123."), StatusKind.Warn);
            return;
        }
        if (_s.Demo) { SetStatus(Loc.F("Demo mode: would connect to {0}.", addr), StatusKind.Ok); return; }
        IsBusy = true;
        try
        {
            SetStatus(Loc.F("Connecting to {0}…", addr));
            var r = await _s.Adb!.ConnectAsync(addr);
            if (await _s.Adb.WaitReadyAsync(addr, TimeSpan.FromSeconds(4)))
            {
                SetStatus(Loc.F("Connected to {0}.", addr), StatusKind.Ok);
                ManualAddress = "";
            }
            else
            {
                var why = r.TimedOut ? Loc.T("no answer (same Wi-Fi? a VPN blocking the local network?)") : r.LastLine;
                SetStatus(Loc.F("Couldn't connect to {0}: {1}", addr, why), StatusKind.Error);
            }
        }
        finally { IsBusy = false; }
        await RefreshListAsync();
    }

    // ---------------------------------------------------------------- settings, apps, windows

    /// <summary>Set by the window.</summary>
    public Action? ShowSettings { get; set; }
    public Action<string?, string, DeviceGroup?>? ShowDeviceSettings { get; set; }
    public Action<DeviceRow>? ShowApps { get; set; }
    public Func<Task<IReadOnlyList<string>>>? PickFilesAsync { get; set; }
    public Action<IReadOnlyList<MirrorSession>>? ArrangeWindows { get; set; }
    public Action<MirrorSession>? BringToFront { get; set; }

    /// <summary>The one device an action is for: the single selected one, or the only device around.</summary>
    DeviceRow? One => Selected is [var only] ? only.Row : _selectedKeys.Count == 0 && _rows.Count == 1 ? _rows[0] : null;

    DeviceRow? SwitchToWifiTarget =>
        One is { IsNetwork: false, Serial: { } s, State: DeviceState.Connected or DeviceState.Mirroring } r
        && !DeviceDirectory.IsEmulatorSerial(s) ? r : null;

    public bool CanSwitchToWifi => SwitchToWifiTarget != null;

    bool CanRunSwitchToWifi() => Ready && !IsBusy && SwitchToWifiTarget != null;

    [RelayCommand(CanExecute = nameof(CanRunSwitchToWifi))]
    async Task SwitchToWifiAsync()
    {
        if (SwitchToWifiTarget is not { Serial: { } serial } r) return;
        if (_s.Demo) { SetStatus(Loc.F("Demo mode: would switch {0} to Wi-Fi.", r.Name), StatusKind.Ok); return; }
        IsBusy = true;
        try
        {
            SetStatus(Loc.F("Switching {0} to Wi-Fi…", r.Name));
            var (ok, addr, msg) = await _s.Adb!.SwitchToWifiAsync(serial);
            if (ok && addr != null)
                SetStatus(Loc.F("Switched {0} to Wi-Fi ({1}). You can unplug the USB cable now.", r.Name, addr), StatusKind.Ok);
            else
                SetStatus(addr != null ? Loc.F("{0} ({1}): {2}", r.Name, addr, msg) : Loc.F("{0}: {1}", r.Name, msg), StatusKind.Error);
        }
        finally { IsBusy = false; }
        await RefreshListAsync();
    }

    [RelayCommand]
    void OpenSettings() => ShowSettings?.Invoke();

    [RelayCommand]
    void DeviceSettings()
    {
        if (One is not { } r) { SetStatus(Loc.T("Select one device first."), StatusKind.Warn); return; }
        if (r.Sn == null) { SetStatus(Loc.F("{0} needs to be paired first.", r.Name), StatusKind.Warn); return; }
        ShowDeviceSettings?.Invoke(r.GroupId, r.Name, null);
    }

    public void GroupSettings(GroupHeaderViewModel h)
    {
        if (h.Group is { } g) ShowDeviceSettings?.Invoke(null, g.Name, g);
    }

    [RelayCommand]
    void Apps()
    {
        if (One is not { CanMirror: true } r) { SetStatus(Loc.T("Select one available device first."), StatusKind.Warn); return; }
        ShowApps?.Invoke(r);
    }

    /// <summary>Starts a scrcpy window of another kind (app, second screen, camera) for one device.</summary>
    async Task StartWindowAsync(DeviceRow? r, string what, Func<string, MirrorSession> start, Func<string, Task>? prepare = null, bool unlock = false)
    {
        if (r is not { CanMirror: true }) { SetStatus(Loc.T("Select one available device first."), StatusKind.Warn); return; }
        if (_s.Demo) { SetStatus(Loc.F("Demo mode: would open {0} of {1}.", what, r.Name), StatusKind.Ok); return; }
        IsBusy = true;
        var connected = false;
        try
        {
            if (r.State == DeviceState.Available) SetStatus(Loc.F("Connecting to {0}…", r.Name));
            var serial = await _s.Directory!.ConnectAsync(r);
            if (serial == null) { SetStatus(Loc.F("Couldn't connect to {0}.", r.Name), StatusKind.Error); return; }
            connected = r.State == DeviceState.Available;
            string? note = null;
            if (unlock && _s.Unlock!.IsReady(r.Sn))
            {
                SetStatus(Loc.F("Unlocking {0}…", r.Name));
                var res = await _s.Unlock.UnlockAsync(serial, r.Sn!, r.Name, wakeIfUnlocked: !Options(r).ScreenOff);
                if (!res.Ok) note = res.Message;
            }
            if (prepare != null) await prepare(serial);
            var session = start(serial);
            // Second screens and app windows get a toolbar of their own.
            if (session.Kind is SessionKind.Screen or SessionKind.App) MirrorStarted?.Invoke(session, r);
            if (note != null) SetStatus(Loc.F("Opening {0} of {1}, but: {2}", what, r.Name, note), StatusKind.Warn);
            else SetStatus(Loc.F("Opening {0} of {1}…", what, r.Name), StatusKind.Ok);
        }
        finally { IsBusy = false; }
        if (connected) await RefreshListAsync();
    }

    [RelayCommand]
    Task SecondScreen() => OpenSecondScreenAsync(One);

    /// <summary>Opens a second screen of a device (also from its mirror's toolbar).</summary>
    public Task OpenSecondScreenAsync(DeviceRow? r) =>
        StartWindowAsync(r, Loc.T("a second screen"), serial => _s.Mirrors!.StartScreen(serial, r!.Name, Options(r!)), unlock: true);

    [RelayCommand]
    Task CameraBack() => OpenCameraAsync(One, false);

    [RelayCommand]
    Task CameraFront() => OpenCameraAsync(One, true);

    /// <summary>Opens a camera, turned so the picture is upright for how the device is held (Alt+←/→ turn it further).</summary>
    public Task OpenCameraAsync(DeviceRow? r, bool front)
    {
        int? turn = null;
        return StartWindowAsync(r, front ? Loc.T("the front camera") : Loc.T("the back camera"),
            serial => _s.Mirrors!.StartCamera(serial, r!.Name, front, Options(r!), turn),
            async serial => { try { turn = await _s.Adb!.GetCameraUprightTurnAsync(serial, front); } catch { } });
    }

    /// <summary>Opens one app of a device in a window of its own (from the app manager).</summary>
    public Task OpenAppWindowAsync(DeviceRow r, string package) =>
        StartWindowAsync(r, package, serial => _s.Mirrors!.StartApp(serial, r.Name, package, Options(r)), unlock: true);

    [RelayCommand]
    async Task SendFiles()
    {
        var targets = Targets;
        if (targets.Count == 0) { SetStatus(Loc.T("Select the devices to send files to."), StatusKind.Warn); return; }
        if (PickFilesAsync == null) return;
        var files = await PickFilesAsync();
        await SendFilesAsync(files, targets);
    }

    public async Task SendFilesToGroupAsync(GroupHeaderViewModel h)
    {
        if (PickFilesAsync == null) return;
        var files = await PickFilesAsync();
        await SendFilesAsync(files, Members(h).Select(m => m.Row).ToList());
    }

    [RelayCommand]
    void ArrangeAll() => Arrange(null);

    public void ArrangeGroup(GroupHeaderViewModel h) => Arrange(Members(h).Select(m => m.Row.Serial).OfType<string>().ToHashSet());

    void Arrange(ISet<string>? serials)
    {
        if (!OperatingSystem.IsWindows()) { SetStatus(Loc.T("Arranging windows works on Windows for now."), StatusKind.Warn); return; }
        var sessions = (_s.Mirrors?.Sessions ?? []).Where(x => x.Kind != SessionKind.Recording && (serials == null || serials.Contains(x.Serial)))
            .OrderBy(x => x.Name, NaturalComparer.Instance).ToList();
        if (sessions.Count == 0) { SetStatus(Loc.T("No mirror windows are open."), StatusKind.Warn); return; }
        ArrangeWindows?.Invoke(sessions);
        SetStatus(Loc.F("Arranged {0}.", Loc.Count(sessions.Count, "{0} window", "{0} windows")), StatusKind.Ok);
    }

    // ---------------------------------------------------------------- shortcuts

    /// <summary>Registers every device's shortcut (Windows).</summary>
    public void UpdateHotkeys()
    {
        if (!OperatingSystem.IsWindows() || _s.Demo) return;
        var map = new Dictionary<string, string>();
        foreach (var (id, p) in _s.Profiles.All)
            if (p.Hotkey is { Length: > 0 } hk) map.TryAdd(hk, id);
        GlobalHotkeys.Set(map);
        if (GlobalHotkeys.Failed.Count > 0)
            SetStatus(Loc.F("Shortcut {0} is already used by another program.", string.Join(", ", GlobalHotkeys.Failed)), StatusKind.Warn);
    }

    /// <summary>A device's shortcut was pressed: mirror it, or bring its mirror to the front.</summary>
    public async Task MirrorByIdAsync(string id)
    {
        var r = _rows.FirstOrDefault(x => x.GroupId == id);
        if (r == null) { SetStatus(Loc.F("{0} isn't on the network.", _s.KnownFor(id)?.Name ?? id), StatusKind.Warn); return; }
        if (r.State == DeviceState.Mirroring && _s.Mirrors?.Sessions.FirstOrDefault(x => x.Serial == r.Serial && x.Kind == SessionKind.Mirror) is { } session)
        {
            BringToFront?.Invoke(session);
            return;
        }
        if (!r.CanMirror) { SetStatus(Loc.F("{0} can't be mirrored right now.", r.Name), StatusKind.Warn); return; }
        await MirrorRowsAsync([r]);
    }

    // ---------------------------------------------------------------- groups

    bool HasSelection() => _selectedKeys.Count > 0;

    /// <summary>Moves the selected devices into a group; null takes them out of their group.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    void MoveToGroup(DeviceGroup? group)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        _s.Groups.Assign(rows.Select(r => r.GroupId), group);
        var who = rows.Count == 1 ? rows[0].Name : Loc.Count(rows.Count, "{0} device", "{0} devices");
        SetStatus(group == null ? Loc.F(rows.Count == 1 ? "Took {0} out of its group." : "Took {0} out of their groups.", who) : Loc.F("Moved {0} to {1}.", who, group.Name), StatusKind.Ok);
    }

    /// <summary>Creates a group, with the selected devices in it.</summary>
    [RelayCommand]
    async Task NewGroupAsync()
    {
        if (PromptAsync == null) return;
        var rows = SelectedRows;
        var name = await PromptAsync(Loc.T("New group"),
            rows.Count > 0 ? Loc.F("A name for the group. The {0} go into it.", Loc.Count(rows.Count, "{0} selected device", "{0} selected devices")) : Loc.T("A name for the group, e.g. Test lab or Family tablets."),
            "");
        if (GroupStore.Clean(name) is not { } clean) return;
        var existed = _s.Groups.Find(clean) != null;
        var group = _s.Groups.Create(clean);
        if (rows.Count > 0) _s.Groups.Assign(rows.Select(r => r.GroupId), group);
        SetStatus(existed ? Loc.F("Moved the selected devices to {0}.", group.Name) : Loc.F("Created the group {0}.", group.Name), StatusKind.Ok);
    }

    [RelayCommand]
    void CollapseAll() => SetAllCollapsed(true);

    [RelayCommand]
    void ExpandAll() => SetAllCollapsed(false);

    void SetAllCollapsed(bool collapsed)
    {
        foreach (var g in _s.Groups.Groups) g.Collapsed = collapsed;
        _s.Groups.Data.OthersCollapsed = collapsed;
        _s.Groups.Save();
    }

    IEnumerable<DeviceItemViewModel> Members(GroupHeaderViewModel h)
    {
        if (h.Group is { } g) return _items.Values.Where(i => g.Members.Contains(i.Row.GroupId));
        return _items.Values.Where(i => _s.Groups.GroupOf(i.Row.GroupId) == null);
    }

    public void ToggleGroup(GroupHeaderViewModel h) => _s.Groups.SetCollapsed(h.Group, !h.IsCollapsed);

    public void SelectGroup(GroupHeaderViewModel h)
    {
        if (h.IsCollapsed) _s.Groups.SetCollapsed(h.Group, false);
        Select(Members(h).Select(m => m.Key));
    }

    public Task MirrorGroupAsync(GroupHeaderViewModel h)
    {
        var rows = Members(h).Select(m => m.Row).Where(r => r.CanMirror && r.State != DeviceState.Mirroring)
            .OrderBy(r => r.Name, NaturalComparer.Instance).ToList();
        if (rows.Count == 0) { SetStatus(Loc.F("Nothing in {0} to mirror right now.", h.Name), StatusKind.Warn); return Task.CompletedTask; }
        return MirrorRowsAsync(rows);
    }

    public void RunScriptOnGroup(GroupHeaderViewModel h) =>
        ShowScripts?.Invoke(Members(h).Where(m => m.Row.CanMirror).Select(m => m.Key).ToList());

    public async Task RenameGroupAsync(GroupHeaderViewModel h)
    {
        if (h.Group is not { } g || PromptAsync == null) return;
        var name = await PromptAsync(Loc.T("Rename group"), Loc.T("A new name for the group."), g.Name);
        if (name == null || GroupStore.Clean(name) == g.Name) return;
        if (!_s.Groups.Rename(g, name))
            SetStatus(GroupStore.Clean(name) == null ? Loc.T("A group needs a name.") : Loc.F("There's already a group called {0}.", GroupStore.Clean(name)), StatusKind.Warn);
    }

    public async Task DeleteGroupAsync(GroupHeaderViewModel h)
    {
        if (h.Group is not { } g) return;
        if (g.Members.Count > 0 && ConfirmAsync != null
            && !await ConfirmAsync(Loc.T("Delete group"), Loc.F("Delete the group \"{0}\"? Its {1} move to Other devices; nothing changes on the devices themselves.", g.Name, Loc.Count(g.Members.Count, "{0} device", "{0} devices"))))
            return;
        _s.Groups.Delete(g);
        SetStatus(Loc.F("Deleted the group {0}.", g.Name));
    }

    public void MoveGroup(GroupHeaderViewModel h, int delta)
    {
        if (h.Group is { } g) _s.Groups.Move(g, delta);
    }
}

/// <summary>Sorts "Lab phone 2" before "Lab phone 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                var c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                var c = string.Compare(a[i].ToString(), b[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

/// <summary>A maximum mirror size: the stored value and what the list shows.</summary>
public sealed record SizeChoice(string Value, string Label)
{
    public override string ToString() => Label;
}
