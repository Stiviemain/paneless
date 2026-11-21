using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed partial class WallTile(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] public partial string Name { get; set; } = name;
    [ObservableProperty] public partial Bitmap? Image { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsMirroring { get; set; }
    [ObservableProperty] public partial double Width { get; set; } = 220;
    public bool HasImage => Image != null;
    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));
}

/// <summary>The Wall page: a picture of every connected device's screen, refreshed every few seconds
/// while the page is open. Click a picture to mirror that device.</summary>
public sealed partial class WallViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    CancellationTokenSource? _loop;

    public WallViewModel(AppServices s, MainViewModel main)
    {
        _s = s; _main = main;
        GroupChoices.Add(Loc.T("All devices"));
        foreach (var g in s.Groups.Groups) GroupChoices.Add(g.Name);
        GroupFilter = GroupChoices[0];
        Seconds = Math.Clamp(s.Data.Settings.WallSeconds, 2, 60);
        s.Groups.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            var keep = GroupFilter;
            GroupChoices.Clear(); GroupChoices.Add(Loc.T("All devices"));
            foreach (var g in s.Groups.Groups) GroupChoices.Add(g.Name);
            GroupFilter = GroupChoices.Contains(keep) ? keep : GroupChoices[0];
        });
    }

    public ObservableCollection<WallTile> Tiles { get; } = new();
    public ObservableCollection<string> GroupChoices { get; } = new();
    public IReadOnlyList<int> SecondChoices { get; } = [2, 5, 10, 30];
    public IReadOnlyList<string> SizeChoices { get; } = [Loc.T("Small"), Loc.T("Medium"), Loc.T("Large")];

    [ObservableProperty] public partial string GroupFilter { get; set; } = "";
    [ObservableProperty] public partial int Seconds { get; set; } = 5;
    [ObservableProperty] public partial string Size { get; set; } = Loc.T("Medium");
    [ObservableProperty] public partial bool IsPaused { get; set; }
    public bool IsEmpty => Tiles.Count == 0;

    double TileWidth => Size == SizeChoices[0] ? 150 : Size == SizeChoices[2] ? 320 : 220;
    partial void OnSizeChanged(string value) { foreach (var t in Tiles) t.Width = TileWidth; }
    partial void OnGroupFilterChanged(string value) => SyncTiles();
    partial void OnSecondsChanged(int value) { _s.Data.Settings.WallSeconds = value; _s.SaveSettings(); }

    [RelayCommand] void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    Task Mirror(WallTile tile) => _main.MirrorByIdAsync(tile.Id);

    IReadOnlyList<DeviceRow> Rows()
    {
        var rows = _main.NetworkRows.Where(r => r.Serial != null && r.State is DeviceState.Connected or DeviceState.Mirroring);
        if (GroupFilter != GroupChoices.FirstOrDefault() && _s.Groups.Find(GroupFilter) is { } g) rows = rows.Where(r => g.Members.Contains(r.GroupId));
        return rows.OrderBy(r => r.Name, NaturalComparer.Instance).ToList();
    }

    void SyncTiles()
    {
        var rows = Rows();
        var want = rows.Select(r => r.GroupId).ToList();
        for (var i = Tiles.Count - 1; i >= 0; i--) if (!want.Contains(Tiles[i].Id)) Tiles.RemoveAt(i);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var t = Tiles.FirstOrDefault(x => x.Id == r.GroupId);
            if (t == null) { t = new WallTile(r.GroupId, r.Name) { Width = TileWidth }; Tiles.Insert(Math.Min(i, Tiles.Count), t); }
            t.Name = r.Name;
            t.IsMirroring = r.State == DeviceState.Mirroring;
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Starts or stops taking pictures (only while the page is on screen).</summary>
    public void SetActive(bool active)
    {
        if (active && _loop == null)
        {
            _loop = new CancellationTokenSource();
            _ = RunAsync(_loop.Token);
        }
        else if (!active && _loop != null)
        {
            _loop.Cancel();
            _loop = null;
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SyncTiles();
            if (!IsPaused && !_s.Demo && _s.Adb is { } adb)
            {
                var rows = Rows();
                var gate = new SemaphoreSlim(4);
                await Task.WhenAll(rows.Select(async r =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var png = await adb.ScreencapAsync(r.Serial!);
                        var tile = Tiles.FirstOrDefault(t => t.Id == r.GroupId);
                        if (tile == null) return;
                        if (png == null) { Dispatcher.UIThread.Post(() => tile.Status = Loc.T("No picture")); return; }
                        var bmp = await Task.Run(() => { using var ms = new MemoryStream(png); return Bitmap.DecodeToWidth(ms, 360); }, ct);
                        Dispatcher.UIThread.Post(() => { var old = tile.Image; tile.Image = bmp; tile.Status = DateTime.Now.ToString("T"); old?.Dispose(); });
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                    finally { gate.Release(); }
                }));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Seconds), ct); } catch { return; }
        }
    }
}
