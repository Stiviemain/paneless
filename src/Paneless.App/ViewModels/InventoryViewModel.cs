using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed class InventoryRow(DeviceInventory d, string group, bool online)
{
    public DeviceInventory Data { get; } = d;
    public string Group { get; } = group;
    public string Name => Data.Name;
    public string Model => string.IsNullOrEmpty(Data.Manufacturer) ? Data.Model : $"{Data.Manufacturer} {Data.Model}";
    public string Android => Data.AndroidVersion.Length > 0 ? Loc.F("{0} (SDK {1})", Data.AndroidVersion, Data.Sdk) : "";
    public string Patch => Data.SecurityPatch;
    public IBrush PatchBrush => PatchAgeDays is > 120 ? Theme.Red : PatchAgeDays is > 60 ? Theme.Amber : Theme.Dim;
    public int? PatchAgeDays => DateTime.TryParse(Data.SecurityPatch, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? (int)(DateTime.Today - p).TotalDays : null;
    public string Battery => Data.BatteryLevel is { } b ? $"{b} %{(Data.Charging ? " ⚡" : "")}" : "";
    public IBrush BatteryBrush => Data.BatteryLevel is <= 15 && !Data.Charging ? Theme.Red : Theme.Dim;
    public string Storage => Data.StorageFreeMb is { } f && Data.StorageTotalMb is { } t ? Loc.F("{0} of {1} GB free", (f / 1024.0).ToString("0.#"), (t / 1024.0).ToString("0")) : "";
    public IBrush StorageBrush => Data.StorageFreePercent is < 10 ? Theme.Red : Data.StorageFreePercent is < 20 ? Theme.Amber : Theme.Dim;
    public string Ram => Data.RamTotalMb is { } r ? $"{r / 1024.0:0} GB" : "";
    public string Ip => Data.IpAddress;
    public string Uptime => Data.Uptime is { } u ? (u.TotalDays >= 1 ? Loc.F("{0} d", (int)u.TotalDays) : Loc.F("{0} h", (int)u.TotalHours)) : "";
    public string Seen => online ? "" : Loc.F("last seen {0:g}", Data.CollectedAt);
    public IBrush NameBrush => online ? Theme.Text : Theme.Dim;
}

/// <summary>The Inventory page: a table of every device's facts, exportable as CSV or HTML.</summary>
public sealed partial class InventoryViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;

    public InventoryViewModel(AppServices s, MainViewModel main)
    {
        _s = s; _main = main;
        GroupChoices = new();
        RebuildGroups();
        _s.Groups.Changed += RebuildGroups;
    }

    /// <summary>Set by the view: asks where to save (suggested file name, extension) and returns the path or null.</summary>
    public Func<string, string, Task<string?>>? SaveAsAsync { get; set; }

    public ObservableCollection<InventoryRow> Rows { get; } = new();
    public ObservableCollection<string> GroupChoices { get; }
    [ObservableProperty] public partial string GroupFilter { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    public bool IsEmpty => Rows.Count == 0;

    string AllDevices => Loc.T("All devices");
    partial void OnGroupFilterChanged(string value) => Show();

    void RebuildGroups()
    {
        var keep = GroupFilter;
        GroupChoices.Clear();
        GroupChoices.Add(AllDevices);
        foreach (var g in _s.Groups.Groups) GroupChoices.Add(g.Name);
        GroupFilter = GroupChoices.Contains(keep) ? keep : AllDevices;
    }

    string GroupOf(string id) => _s.Groups.GroupOf(id)?.Name ?? "";

    void Show()
    {
        var online = _main.NetworkRows.Select(r => r.GroupId).ToHashSet();
        var rows = _s.Inventory.Select(kv => new InventoryRow(kv.Value, GroupOf(kv.Key), online.Contains(kv.Key)))
            .Where(r => GroupFilter == AllDevices || GroupFilter.Length == 0 || r.Group == GroupFilter)
            .OrderBy(r => r.Group.Length == 0).ThenBy(r => r.Group).ThenBy(r => r.Name, NaturalComparer.Instance).ToList();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        OnPropertyChanged(nameof(IsEmpty));
        var lowStorage = rows.Count(r => r.Data.StorageFreePercent is < 20);
        var oldPatch = rows.Count(r => r.PatchAgeDays is > 90);
        Summary = Loc.Count(rows.Count, "{0} device", "{0} devices")
                  + (lowStorage > 0 ? " · " + Loc.F("{0} with less than 20 % storage free", lowStorage) : "")
                  + (oldPatch > 0 ? " · " + Loc.F("{0} with a security patch older than 3 months", oldPatch) : "");
    }

    /// <summary>Reads every device on the network again (up to 6 at a time).</summary>
    [RelayCommand]
    public async Task Refresh()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var rows = _main.NetworkRows.Where(r => r.CanMirror && r.Sn != null).ToList();
            if (GroupFilter != AllDevices && _s.Groups.Find(GroupFilter) is { } g) rows = rows.Where(r => g.Members.Contains(r.GroupId)).ToList();
            Status = Loc.F("Reading {0}…", Loc.Count(rows.Count, "{0} device", "{0} devices"));
            if (_s.Demo)
                foreach (var r in rows) _s.Inventory[r.GroupId] = DemoData.Inventory(r);
            else
            {
                var gate = new SemaphoreSlim(6);
                await Task.WhenAll(rows.Select(async r =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var serial = await _s.Directory!.ConnectAsync(r);
                        if (serial != null && await _s.Adb!.GetInventoryAsync(serial, r.Name) is { } inv) _s.Inventory[r.GroupId] = inv;
                    }
                    catch { }
                    finally { gate.Release(); }
                }));
            }
            Show();
            Status = Loc.F("Updated {0:t}.", DateTime.Now);
        }
        finally { IsBusy = false; }
    }

    public void ShowCached() => Show();

    [RelayCommand]
    Task ExportCsv() => Export("csv");

    [RelayCommand]
    Task ExportHtml() => Export("html");

    async Task Export(string ext)
    {
        if (Rows.Count == 0 || SaveAsAsync == null) { Status = Loc.T("Read the devices first."); return; }
        var path = await SaveAsAsync($"Paneless-inventory-{DateTime.Now:yyyy-MM-dd}", ext);
        if (path == null) return;
        var data = Rows.Select(r => (r.Data, r.Group));
        try
        {
            await File.WriteAllTextAsync(path, ext == "csv" ? InventoryExport.Csv(data) : InventoryExport.Html(data, Loc.T("Device inventory")),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: ext == "csv"));   // BOM so Excel reads umlauts
            Status = Loc.F("Saved {0}", path);
            Reveal.File(path);
        }
        catch (Exception ex) { Status = Loc.F("Couldn’t save: {0}", ex.Message); }
    }
}
