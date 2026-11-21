using Paneless.App.Localization;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>A group in the manager's list.</summary>
public sealed partial class GroupListItem(DeviceGroup group) : ObservableObject
{
    public DeviceGroup Group { get; } = group;
    public string Name => Group.Name;
    public string CountText => Loc.Count(Group.Members.Count, "{0} device", "{0} devices");
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A device in the manager: ticked = in the selected group.</summary>
public sealed partial class GroupDeviceItem(string id, string name, string detail) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; set; } = name;
    public string Detail { get; set; } = detail;
    public Action<GroupDeviceItem, bool>? Toggled { get; set; }

    [ObservableProperty] public partial bool IsMember { get; set; }
    [ObservableProperty] public partial bool Online { get; set; }
    [ObservableProperty] public partial string WhereText { get; set; } = "";

    public IBrush OnlineBrush => Online ? Theme.Green : Theme.Dim;
    public string OnlineText => Online ? "●" : "○";
    partial void OnOnlineChanged(bool value) { OnPropertyChanged(nameof(OnlineBrush)); OnPropertyChanged(nameof(OnlineText)); }

    bool _quiet;
    /// <summary>Updates the tick without treating it as a click.</summary>
    public void SetQuietly(bool member) { _quiet = true; IsMember = member; _quiet = false; }
    partial void OnIsMemberChanged(bool value) { if (!_quiet) Toggled?.Invoke(this, value); }

    public bool Matches(string q) => Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Detail.Contains(q, StringComparison.OrdinalIgnoreCase) || WhereText.Contains(q, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The "Manage groups" window: create, rename, reorder and delete groups, and tick which devices are in each.
/// Lists every device Paneless knows (also ones that aren't on the network right now).</summary>
public sealed partial class GroupsViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly Dictionary<string, GroupDeviceItem> _devices = new();

    public GroupsViewModel(AppServices services, MainViewModel main, DeviceGroup? select)
    {
        _s = services;
        _main = main;
        _s.Groups.Changed += OnGroupsChanged;
        _main.DevicesChanged += OnDevicesChanged;
        _main.SelectionChanged += OnMainSelectionChanged;
        LoadGroups();
        SelectedGroup = Groups.FirstOrDefault(g => g.Group == select) ?? Groups.FirstOrDefault();
        LoadDevices();
    }

    /// <summary>Set by the window.</summary>
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<GroupListItem> Groups { get; } = new();
    public ObservableCollection<GroupDeviceItem> Devices { get; } = new();

    [ObservableProperty] public partial GroupListItem? SelectedGroup { get; set; }
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial IBrush MessageBrush { get; set; } = Theme.Dim;

    public bool HasGroup => SelectedGroup != null;
    public bool NoGroups => Groups.Count == 0;
    public string DevicesTitle => SelectedGroup is { } g ? Loc.F("Devices in {0}", g.Name) : Loc.T("Devices");
    public string AddSelectedLabel => _main.SelectedRows.Count is var n and > 0
        ? Loc.Count(n, "Add the {0} device selected in the main window", "Add the {0} devices selected in the main window") : Loc.T("Add the devices selected in the main window");

    partial void OnSelectedGroupChanged(GroupListItem? value)
    {
        OnPropertyChanged(nameof(HasGroup));
        OnPropertyChanged(nameof(DevicesTitle));
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        AddSelectedCommand.NotifyCanExecuteChanged();
        UpdateTicks();
    }

    partial void OnFilterChanged(string value) => ShowDevices();

    public void Detach()
    {
        _s.Groups.Changed -= OnGroupsChanged;
        _main.DevicesChanged -= OnDevicesChanged;
        _main.SelectionChanged -= OnMainSelectionChanged;
    }

    void OnMainSelectionChanged()
    {
        OnPropertyChanged(nameof(AddSelectedLabel));
        AddSelectedCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- loading

    void OnGroupsChanged()
    {
        LoadGroups();
        LoadDevices();
    }

    void OnDevicesChanged()
    {
        LoadDevices();
        OnMainSelectionChanged();
    }

    void LoadGroups()
    {
        var keep = SelectedGroup?.Group;
        var items = _s.Groups.Groups.Select(g => Groups.FirstOrDefault(i => i.Group == g) ?? new GroupListItem(g)).ToList();
        if (!items.SequenceEqual(Groups))
        {
            Groups.Clear();
            foreach (var i in items) Groups.Add(i);
        }
        foreach (var i in Groups) i.Refresh();
        if (SelectedGroup == null || !Groups.Contains(SelectedGroup)) SelectedGroup = Groups.FirstOrDefault(i => i.Group == keep) ?? Groups.FirstOrDefault();
        OnPropertyChanged(nameof(NoGroups));
        OnPropertyChanged(nameof(DevicesTitle));
    }

    /// <summary>Every device Paneless knows: on the network now, connected before, or already in a group.</summary>
    void LoadDevices()
    {
        var online = new HashSet<string>();
        foreach (var r in _main.NetworkRows)
        {
            if (r.NeedsPairing && r.Sn == null) continue;   // nothing stable to remember it by
            online.Add(r.GroupId);
            Upsert(r.GroupId, r.Name, Detail(r.Model, r.Type));
        }
        foreach (var (sn, k) in _s.KnownDevices()) Upsert(sn, k.Name, Detail(k.Model, k.Type), onlyIfNew: true);
        foreach (var g in _s.Groups.Groups)
            foreach (var id in g.Members)
                if (!_devices.ContainsKey(id)) { var k = _s.KnownFor(id); Upsert(id, k?.Name is { Length: > 0 } n ? n : id, Detail(k?.Model, k?.Type)); }
        foreach (var d in _devices.Values) d.Online = online.Contains(d.Id);
        UpdateTicks();
        ShowDevices();
    }

    static string Detail(string? model, string? type) => string.Join(" · ", new[] { string.IsNullOrEmpty(type) ? null : Loc.T(type), model }.Where(s => !string.IsNullOrEmpty(s)));

    void Upsert(string id, string name, string detail, bool onlyIfNew = false)
    {
        if (_devices.TryGetValue(id, out var d))
        {
            if (onlyIfNew) return;
            d.Name = name; d.Detail = detail;
            return;
        }
        _devices[id] = new GroupDeviceItem(id, name, detail) { Toggled = OnToggled };
    }

    void UpdateTicks()
    {
        var group = SelectedGroup?.Group;
        foreach (var d in _devices.Values)
        {
            var inGroup = _s.Groups.GroupOf(d.Id);
            d.SetQuietly(group != null && inGroup == group);
            d.WhereText = inGroup == null ? "" : inGroup == group ? "" : Loc.F("in {0}", inGroup.Name);
        }
    }

    void ShowDevices()
    {
        var q = Filter.Trim();
        var want = _devices.Values.Where(d => q.Length == 0 || d.Matches(q))
            .OrderBy(d => d.Name, NaturalComparer.Instance).ThenBy(d => d.Id).ToList();
        if (want.SequenceEqual(Devices)) return;
        Devices.Clear();
        foreach (var d in want) Devices.Add(d);
    }

    // ---------------------------------------------------------------- membership

    void OnToggled(GroupDeviceItem d, bool member)
    {
        if (SelectedGroup?.Group is not { } g) { d.SetQuietly(false); return; }
        var from = _s.Groups.GroupOf(d.Id);
        _s.Groups.Assign([d.Id], member ? g : null);
        Say(member
            ? from != null && from != g ? Loc.F("Moved {0} from {1} to {2}.", d.Name, from.Name, g.Name) : Loc.F("Added {0} to {1}.", d.Name, g.Name)
            : Loc.F("Took {0} out of {1}.", d.Name, g.Name), StatusKind.Ok);
    }

    bool CanAddSelected() => SelectedGroup != null && _main.SelectedRows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAddSelected))]
    void AddSelected()
    {
        if (SelectedGroup?.Group is not { } g) return;
        var rows = _main.SelectedRows;
        _s.Groups.Assign(rows.Select(r => r.GroupId), g);
        Say(Loc.F("Added {0} to {1}.", (rows.Count == 1 ? rows[0].Name : Loc.Count(rows.Count, "{0} device", "{0} devices")), g.Name), StatusKind.Ok);
    }

    [RelayCommand]
    void TickAllShown()
    {
        if (SelectedGroup?.Group is not { } g) return;
        var ids = Devices.Select(d => d.Id).ToList();
        if (ids.Count > 0) { _s.Groups.Assign(ids, g); Say(Loc.F("Added {0} to {1}.", Loc.Count(ids.Count, "{0} device", "{0} devices"), g.Name), StatusKind.Ok); }
    }

    [RelayCommand]
    void UntickAllShown()
    {
        if (SelectedGroup?.Group is not { } g) return;
        var ids = Devices.Where(d => d.IsMember).Select(d => d.Id).ToList();
        if (ids.Count > 0) { _s.Groups.Assign(ids, null); Say(Loc.F("Took {0} out of {1}.", Loc.Count(ids.Count, "{0} device", "{0} devices"), g.Name), StatusKind.Ok); }
    }

    // ---------------------------------------------------------------- groups

    [RelayCommand]
    async Task NewGroup()
    {
        if (PromptAsync == null) return;
        var name = await PromptAsync(Loc.T("New group"), Loc.T("A name for the group, e.g. Test lab or Family tablets."), "");
        if (name == null) return;
        if (GroupStore.Clean(name) is not { } clean) { Say(Loc.T("A group needs a name (up to 60 characters)."), StatusKind.Warn); return; }
        if (_s.Groups.Find(clean) is { } existing) { SelectedGroup = Groups.FirstOrDefault(i => i.Group == existing); Say(Loc.F("There's already a group called {0}.", existing.Name), StatusKind.Warn); return; }
        var g = _s.Groups.Create(clean);
        SelectedGroup = Groups.FirstOrDefault(i => i.Group == g);
        Say(Loc.F("Created {0}. Tick the devices that belong in it.", g.Name), StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(HasGroup))]
    async Task Rename()
    {
        if (SelectedGroup?.Group is not { } g || PromptAsync == null) return;
        var name = await PromptAsync(Loc.T("Rename group"), Loc.T("A new name for the group."), g.Name);
        if (name == null || GroupStore.Clean(name) == g.Name) return;
        if (_s.Groups.Rename(g, name)) Say(Loc.F("Renamed to {0}.", g.Name), StatusKind.Ok);
        else Say(GroupStore.Clean(name) == null ? Loc.T("A group needs a name (up to 60 characters).") : Loc.F("There's already a group called {0}.", GroupStore.Clean(name)), StatusKind.Warn);
    }

    [RelayCommand(CanExecute = nameof(HasGroup))]
    async Task Delete()
    {
        if (SelectedGroup?.Group is not { } g) return;
        if (g.Members.Count > 0 && ConfirmAsync != null
            && !await ConfirmAsync(Loc.T("Delete group"), Loc.F("Delete the group \"{0}\"? Its {1} move to Other devices; nothing changes on the devices themselves.", g.Name, Loc.Count(g.Members.Count, "{0} device", "{0} devices"))))
            return;
        _s.Groups.Delete(g);
        Say(Loc.F("Deleted {0}.", g.Name));
    }

    bool CanMoveUp() => SelectedGroup != null && Groups.IndexOf(SelectedGroup) > 0;
    bool CanMoveDown() => SelectedGroup != null && Groups.IndexOf(SelectedGroup) < Groups.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    void MoveDown() => Move(1);

    void Move(int delta)
    {
        if (SelectedGroup is not { } sel) return;
        _s.Groups.Move(sel.Group, delta);
        SelectedGroup = sel;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    void Say(string text, StatusKind kind = StatusKind.Info) { Message = text; MessageBrush = Theme.For(kind); }
}
