using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>One device's picture on the Wall.</summary>
public sealed partial class WallTile(WallViewModel wall, string id, string name) : ObservableObject
{
    /// <summary>For the right-click menu, whose items bind to the Wall's commands.</summary>
    public WallViewModel Wall { get; } = wall;
    public string Id { get; } = id;
    [ObservableProperty] public partial string Name { get; set; } = name;
    [ObservableProperty] public partial Bitmap? Image { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsMirroring { get; set; }
    [ObservableProperty] public partial double Width { get; set; } = WallLayout.DefaultWidth;
    /// <summary>The screen is off or dozing, so a picture would only be black.</summary>
    [ObservableProperty] public partial bool ScreenOff { get; set; }
    /// <summary>An auto-unlock code is saved (and not paused) for this device.</summary>
    [ObservableProperty] public partial bool CanUnlock { get; set; }
    /// <summary>The user resized this picture on its own.</summary>
    [ObservableProperty] public partial bool HasOwnSize { get; set; }
    [ObservableProperty] public partial bool IsDragging { get; set; }

    public bool HasImage => Image != null;
    public bool ShowWaiting => Image == null && !ScreenOff;
    /// <summary>Lets a phone picture grow with its tile, while a landscape tablet stays short.</summary>
    public double MaxImageHeight => Width * 2.4;

    partial void OnImageChanged(Bitmap? value) { OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(ShowWaiting)); }
    partial void OnScreenOffChanged(bool value) => OnPropertyChanged(nameof(ShowWaiting));
    partial void OnWidthChanged(double value) => OnPropertyChanged(nameof(MaxImageHeight));
}

/// <summary>The Wall page: a picture of every connected device's screen, refreshed every few seconds while the page is
/// open. Click a picture to mirror that device, drag it to rearrange, drag its corner to resize, right-click for more.
/// Screens that are off aren't photographed (the picture would be black); they show a Wake button instead.</summary>
public sealed partial class WallViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    CancellationTokenSource? _loop;
    readonly SemaphoreSlim _gate = new(4);
    readonly ConcurrentDictionary<string, byte> _capturing = new();

    WallLayout Layout => _s.Data.Settings.Wall;

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
        if (s.Helpers != null)
            s.Helpers.EventReceived += ev =>
            {
                if (ev.Name != "screen_state" || _loop == null) return;
                var on = ev.Data["on"]?.GetValue<bool>();
                if (on == null) return;
                Dispatcher.UIThread.Post(() =>
                {
                    var row = _main.NetworkRows.FirstOrDefault(r => r.Serial == ev.Serial);
                    var tile = row != null ? Tiles.FirstOrDefault(t => t.Id == row.GroupId) : null;
                    if (tile == null) return;
                    tile.ScreenOff = !on.Value;
                    if (on.Value && !IsPaused) _ = CaptureAsync(row!, tile, CancellationToken.None);
                });
            };
    }

    public ObservableCollection<WallTile> Tiles { get; } = new();
    public ObservableCollection<string> GroupChoices { get; } = new();
    public IReadOnlyList<int> SecondChoices { get; } = [2, 5, 10, 30];

    [ObservableProperty] public partial string GroupFilter { get; set; } = "";
    [ObservableProperty] public partial int Seconds { get; set; } = 5;
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial int HiddenCount { get; set; }

    public bool IsEmpty => Tiles.Count == 0 && HiddenCount == 0;
    public bool AllHidden => Tiles.Count == 0 && HiddenCount > 0;
    public string ShowHiddenText => Loc.Count(HiddenCount, "Show {0} hidden device", "Show {0} hidden devices");

    partial void OnGroupFilterChanged(string value) => SyncTiles();
    partial void OnSecondsChanged(int value) { _s.Data.Settings.WallSeconds = value; _s.SaveSettings(); }
    partial void OnHiddenCountChanged(int value) { OnPropertyChanged(nameof(ShowHiddenText)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(AllHidden)); }

    [RelayCommand] void TogglePause() => IsPaused = !IsPaused;

    // ---------------------------------------------------------------- the pictures

    DeviceRow? RowOf(WallTile t) => _main.NetworkRows.FirstOrDefault(r => r.GroupId == t.Id && r.Serial != null);

    IReadOnlyList<DeviceRow> Rows()
    {
        var rows = _main.NetworkRows.Where(r => r.Serial != null && r.State is DeviceState.Connected or DeviceState.Mirroring);
        if (GroupFilter != GroupChoices.FirstOrDefault() && _s.Groups.Find(GroupFilter) is { } g) rows = rows.Where(r => g.Members.Contains(r.GroupId));
        return rows.GroupBy(r => r.GroupId).Select(x => x.First()).OrderBy(r => r.Name, NaturalComparer.Instance).ToList();
    }

    /// <summary>Brings the tiles in line with the connected devices, in the user's order. Returns what to photograph.</summary>
    List<(DeviceRow Row, WallTile Tile)> SyncTiles()
    {
        var rows = Rows();
        var shown = rows.Where(r => !Layout.IsHidden(r.GroupId)).ToDictionary(r => r.GroupId);
        var order = Layout.Sort(shown.Keys.ToList());
        HiddenCount = rows.Count - shown.Count;
        for (var i = Tiles.Count - 1; i >= 0; i--) if (!shown.ContainsKey(Tiles[i].Id)) Tiles.RemoveAt(i);
        var result = new List<(DeviceRow, WallTile)>();
        for (var i = 0; i < order.Count; i++)
        {
            var r = shown[order[i]];
            var t = Tiles.FirstOrDefault(x => x.Id == r.GroupId);
            if (t == null) { t = new WallTile(this, r.GroupId, r.Name); Tiles.Insert(Math.Min(i, Tiles.Count), t); }
            else if (Tiles.IndexOf(t) is var at && at != i) Tiles.Move(at, i);
            t.Name = r.Name;
            t.IsMirroring = r.State == DeviceState.Mirroring;
            t.CanUnlock = _s.Unlock?.IsReady(r.Sn) == true;
            t.HasOwnSize = Layout.HasOwnWidth(t.Id);
            t.Width = Layout.WidthOf(t.Id);
            result.Add((r, t));
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(AllHidden));
        return result;
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
            if (_s.Helpers != null)
                foreach (var serial in _s.Helpers.ConnectedSerials)
                    if (_s.Helpers.TryGet(serial) is { } h && h.Supports("display_watch"))
                        _ = Task.Run(async () => { try { await h.WatchDisplayAsync(false); } catch { } });
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var work = SyncTiles();
            if (_s.Demo) ShowDemoPictures(work);
            else if (!IsPaused) await Task.WhenAll(work.Select(w => CaptureAsync(w.Row, w.Tile, ct)));
            try { await Task.Delay(TimeSpan.FromSeconds(Seconds), ct); } catch { return; }
        }
    }

    /// <summary>One picture of one device, unless its screen is off (then it shows a Wake button instead).</summary>
    async Task CaptureAsync(DeviceRow r, WallTile tile, CancellationToken ct)
    {
        if (_s.Adb is not { } adb || r.Serial is not { } serial) return;
        if (!_capturing.TryAdd(tile.Id, 0)) return;   // the last picture of this device is still on its way
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                var width = (int)Math.Clamp(tile.Width * 2, 360, 1280);
                // Fast path: on-device downscaled JPEG via persistent helper (~20 KB instead of ~3 MB PNG).
                if (_s.Helpers != null && await _s.Helpers.GetOrStartAsync(serial, r.Type == "Watch", ct) is { } helper && helper.Supports("capture"))
                {
                    if (helper.Supports("display_watch")) _ = Task.Run(async () => { try { await helper.WatchDisplayAsync(true); } catch { } });
                    var cap = await helper.CaptureJpegAsync(width, 0, 78, ct);
                    if (cap != null)
                    {
                        if (cap.ScreenOff)
                        {
                            Dispatcher.UIThread.Post(() => tile.ScreenOff = true);
                            return;
                        }
                        if (cap.Jpeg != null)
                        {
                            var fastBmp = await Task.Run(() => { using var ms = new MemoryStream(cap.Jpeg); return new Bitmap(ms); }, ct);
                            Dispatcher.UIThread.Post(() =>
                            {
                                var old = tile.Image;
                                tile.Image = fastBmp; tile.ScreenOff = false; tile.Status = DateTime.Now.ToString("T");
                                old?.Dispose();
                            });
                            return;
                        }
                    }
                }

                if (await adb.IsScreenOnAsync(serial) == false)
                {
                    Dispatcher.UIThread.Post(() => tile.ScreenOff = true);
                    return;
                }
                var png = await adb.ScreencapAsync(serial);
                if (png == null) { Dispatcher.UIThread.Post(() => tile.Status = Loc.T("No picture")); return; }
                // Sharp on high-DPI screens and in large tiles, without decoding the full-size picture.
                var bmp = await Task.Run(() => { using var ms = new MemoryStream(png); return Bitmap.DecodeToWidth(ms, width); }, ct);
                Dispatcher.UIThread.Post(() =>
                {
                    var old = tile.Image;
                    tile.Image = bmp; tile.ScreenOff = false; tile.Status = DateTime.Now.ToString("T");
                    old?.Dispose();
                });
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch { /* the next round tries again */ }
        finally { _capturing.TryRemove(tile.Id, out _); }
    }

    Task RefreshAsync(WallTile tile) => RowOf(tile) is { } r && !_s.Demo ? CaptureAsync(r, tile, CancellationToken.None) : Task.CompletedTask;

    // ---------------------------------------------------------------- right-click menu

    [RelayCommand]
    Task Mirror(WallTile tile) => _main.MirrorByIdAsync(tile.Id);

    [RelayCommand]
    async Task Wake(WallTile tile)
    {
        if (_s.Demo) { tile.ScreenOff = false; return; }
        if (RowOf(tile) is not { Serial: { } serial } || _s.Adb is not { } adb) return;
        await adb.ShellAsync(serial, "input keyevent KEYCODE_WAKEUP", TimeSpan.FromSeconds(5));
        tile.ScreenOff = false;
        await Task.Delay(700);   // let the lock screen draw
        await RefreshAsync(tile);
    }

    [RelayCommand]
    async Task Unlock(WallTile tile)
    {
        if (_s.Demo) { tile.ScreenOff = false; _main.Report(Loc.F("Demo mode: would unlock {0}.", tile.Name), StatusKind.Ok); return; }
        if (RowOf(tile) is not { Serial: { } serial, Sn: { } sn } r || _s.Unlock is not { } unlock) return;
        _main.Report(Loc.F("Unlocking {0}…", r.Name));
        var res = await unlock.UnlockAsync(serial, sn, r.Name);
        _main.Report(res.Message, res.Ok ? StatusKind.Ok : StatusKind.Warn);
        tile.ScreenOff = false;
        await RefreshAsync(tile);
    }

    [RelayCommand]
    async Task TurnScreenOff(WallTile tile)
    {
        if (_s.Demo) { tile.ScreenOff = true; return; }
        if (RowOf(tile) is not { Serial: { } serial } || _s.Adb is not { } adb) return;
        await adb.ShellAsync(serial, "input keyevent KEYCODE_SLEEP", TimeSpan.FromSeconds(5));
        tile.ScreenOff = true;
    }

    [RelayCommand]
    Task RefreshPicture(WallTile tile) => RefreshAsync(tile);

    [RelayCommand]
    async Task SaveScreenshot(WallTile tile)
    {
        if (_s.Demo) { _main.Report(Loc.T("Demo mode: no screenshot was saved."), StatusKind.Ok); return; }
        if (RowOf(tile) is not { Serial: { } serial } r || _s.Adb is not { } adb) return;
        var png = await adb.ScreencapAsync(serial);
        if (png == null) { _main.Report(Loc.T("Screenshot failed: the device didn't send an image."), StatusKind.Error); return; }
        try
        {
            Directory.CreateDirectory(ScriptRunner.OutputDir);
            var name = string.Concat(r.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            var file = Path.Combine(ScriptRunner.OutputDir, name);
            await File.WriteAllBytesAsync(file, png);
            _main.Report(Loc.F("Screenshot saved: {0}", file), StatusKind.Ok);
            ToolbarViewModel.Reveal(file);
        }
        catch (Exception ex) { _main.Report(Loc.T("Couldn't save the screenshot: ") + ex.Message, StatusKind.Error); }
    }

    [RelayCommand]
    Task SecondScreen(WallTile tile) => _main.OpenSecondScreenAsync(RowOf(tile));

    [RelayCommand]
    void Apps(WallTile tile) { if (RowOf(tile) is { } r) _main.ShowApps?.Invoke(r); }

    [RelayCommand]
    void Files(WallTile tile) { if (RowOf(tile) is { } r) _main.ShowFiles?.Invoke(r); }

    [RelayCommand]
    Task Locate(WallTile tile) => RowOf(tile) is { } r ? _main.LocateAsync(r) : Task.CompletedTask;

    [RelayCommand]
    void DeviceSettings(WallTile tile) { if (RowOf(tile) is { } r) _main.ShowDeviceSettings?.Invoke(r.GroupId, r.Name, null); }

    [RelayCommand] void Larger(WallTile tile) { ResizeTile(tile, tile.Width * 1.25); SaveLayout(); }
    [RelayCommand] void Smaller(WallTile tile) { ResizeTile(tile, tile.Width / 1.25); SaveLayout(); }

    [RelayCommand]
    void SameSize(WallTile tile)
    {
        Layout.ClearWidth(tile.Id);
        tile.HasOwnSize = false;
        tile.Width = Layout.WidthOf(tile.Id);
        SaveLayout();
    }

    /// <summary>Every picture (also of devices not on screen now) takes this one's size.</summary>
    [RelayCommand]
    void MakeAllThisSize(WallTile tile)
    {
        Layout.SetAllWidths(tile.Width);
        foreach (var t in Tiles) { t.HasOwnSize = false; t.Width = Layout.WidthOf(t.Id); }
        SaveLayout();
    }

    [RelayCommand] void MoveFirst(WallTile tile) { MoveTile(tile, 0); SaveLayout(); }
    [RelayCommand] void MoveLeft(WallTile tile) { MoveTile(tile, Tiles.IndexOf(tile) - 1); SaveLayout(); }
    [RelayCommand] void MoveRight(WallTile tile) { MoveTile(tile, Tiles.IndexOf(tile) + 1); SaveLayout(); }
    [RelayCommand] void MoveLast(WallTile tile) { MoveTile(tile, Tiles.Count - 1); SaveLayout(); }

    [RelayCommand]
    void SortByName()
    {
        Layout.ResetOrder();
        SyncTiles();
        SaveLayout();
    }

    [RelayCommand]
    void Hide(WallTile tile)
    {
        Layout.Hide(tile.Id);
        SyncTiles();
        SaveLayout();
        _main.Report(Loc.F("{0} is hidden from the Wall. You can show it again at the top of the page.", tile.Name));
    }

    [RelayCommand]
    async Task ShowHidden()
    {
        Layout.ShowAll();
        var work = SyncTiles();
        SaveLayout();
        if (_s.Demo) ShowDemoPictures(work);
        else if (!IsPaused) await Task.WhenAll(work.Where(w => w.Tile.Image == null).Select(w => CaptureAsync(w.Row, w.Tile, CancellationToken.None)));
    }

    // ---------------------------------------------------------------- dragging (from the page)

    /// <summary>Puts a tile at another place (while it's dragged); <see cref="SaveLayout"/> when the drag ends.</summary>
    public void MoveTile(WallTile tile, int index)
    {
        var from = Tiles.IndexOf(tile);
        if (from < 0) return;
        index = Math.Clamp(index, 0, Tiles.Count - 1);
        if (index == from) return;
        Layout.Move(Tiles.Select(t => t.Id).ToList(), tile.Id, index);
        Tiles.Move(from, index);
    }

    /// <summary>Gives one tile its own width (while its corner is dragged).</summary>
    public void ResizeTile(WallTile tile, double width)
    {
        Layout.SetWidth(tile.Id, width);
        tile.HasOwnSize = true;
        tile.Width = Layout.WidthOf(tile.Id);
    }

    public void SaveLayout() => _s.SaveSettings();

    // ---------------------------------------------------------------- demo mode

    /// <summary>Made-up home screens, so the Wall can be tried (and photographed for the README) without devices.</summary>
    void ShowDemoPictures(List<(DeviceRow Row, WallTile Tile)> work)
    {
        foreach (var (r, t) in work)
        {
            if (t.Image != null) continue;
            if (r.Name == "Lab phone 03") t.ScreenOff = true;
            t.Image = DemoPicture(r.Name.Sum(c => c) * 37 % 360, r.Type == "Tablet");   // same colours every run
            t.Status = DateTime.Today.AddHours(9).AddMinutes(41).ToString("T");
        }
    }

    static Bitmap DemoPicture(int hue, bool landscape)
    {
        int w = landscape ? 480 : 240, h = landscape ? 300 : 520;
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        var row = new byte[fb.RowBytes];
        var icon = landscape ? 34 : 30;
        var gap = landscape ? 26 : 22;
        var cols = landscape ? 8 : 4;
        var left = (w - cols * icon - (cols - 1) * gap) / 2;
        for (var y = 0; y < h; y++)
        {
            var (br, bg, bb) = Hsv(hue + y * 40.0 / h, 0.55, 0.62 - 0.3 * y / h);
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = (br, bg, bb);
                if (y < 18) (r, g, b) = ((byte)(r * 0.6), (byte)(g * 0.6), (byte)(b * 0.6));   // status bar
                var gy = y - 60;
                var inIcons = gy >= 0 && gy % (icon + gap) < icon && gy / (icon + gap) < (landscape ? 3 : 5);
                var gx = x - left;
                if (inIcons && gx >= 0 && gx % (icon + gap) < icon && gx / (icon + gap) < cols)
                    (r, g, b) = Hsv(hue + 140 + (gx / (icon + gap)) * 35 + (gy / (icon + gap)) * 20, 0.45, 0.95);
                row[x * 4] = b; row[x * 4 + 1] = g; row[x * 4 + 2] = r; row[x * 4 + 3] = 255;
            }
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, fb.RowBytes);
        }
        return bmp;
    }

    static (byte R, byte G, byte B) Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        var c = v * s; var x = c * (1 - Math.Abs(h / 60 % 2 - 1)); var m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0d) : h < 120 ? (x, c, 0d) : h < 180 ? (0d, c, x) : h < 240 ? (0d, x, c) : h < 300 ? (x, 0d, c) : (c, 0d, x);
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
