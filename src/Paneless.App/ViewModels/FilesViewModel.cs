using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>One file or folder row in the Files &amp; photos window.</summary>
public sealed partial class DeviceFileItem(DeviceFileEntry entry) : ObservableObject
{
    public DeviceFileEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsDir => Entry.IsDir;
    public string Kind => Entry.Kind;
    public bool CanPreview => Kind is "image" or "video";
    public string IconGlyph => Kind switch
    {
        "dir" => "📁",
        "image" => "🖼",
        "video" => "🎬",
        "apk" => "📦",
        "audio" => "🎵",
        "doc" => "📄",
        _ => "📃",
    };
    public string SizeText => IsDir ? "" : FormatSize(Entry.Size);
    public string DateText => Entry.Modified > DateTime.MinValue ? Entry.Modified.ToString("g") : "";

    [ObservableProperty] public partial Bitmap? Thumbnail { get; set; }
    public bool HasThumbnail => Thumbnail != null;
    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));

    static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>Visual file &amp; photo browser powered by the on-device helper daemon (with on-device thumbnails).</summary>
public sealed partial class FilesViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly DeviceRow _row;
    CancellationTokenSource? _thumbCts;

    public FilesViewModel(AppServices services, MainViewModel main, DeviceRow row)
    {
        _s = services;
        _main = main;
        _row = row;
        Title = Loc.F("Files & photos on {0}", row.Name);
    }

    public string Title { get; }
    public ObservableCollection<DeviceFileItem> Items { get; } = new();

    public Func<string, Task<string?>>? PickSavePathAsync { get; set; }
    public Func<Task<IReadOnlyList<string>>>? PickUploadFilesAsync { get; set; }
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] public partial string CurrentPath { get; set; } = "/sdcard/DCIM/Camera";
    [ObservableProperty] public partial string SortMode { get; set; } = "date";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial IBrush MessageBrush { get; set; } = Theme.Dim;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand), nameof(OpenFolderCommand))]
    public partial DeviceFileItem? Selected { get; set; }

    public bool CanGoUp => CurrentPath.TrimEnd('/').Count(c => c == '/') > 1;
    partial void OnCurrentPathChanged(string value) => OnPropertyChanged(nameof(CanGoUp));

    void Say(string text, StatusKind kind = StatusKind.Info) { Message = text; MessageBrush = Theme.For(kind); }

    [RelayCommand] Task GoCamera() => NavigateAsync("/sdcard/DCIM/Camera", "date");
    [RelayCommand] Task GoScreenshots() => NavigateAsync("/sdcard/Pictures/Screenshots", "date");
    [RelayCommand] Task GoDownloads() => NavigateAsync("/sdcard/Download", "date");
    [RelayCommand] Task GoStorage() => NavigateAsync("/sdcard", "name");

    [RelayCommand]
    Task GoUp()
    {
        var trimmed = CurrentPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var parent = slash > 0 ? trimmed[..slash] : "/sdcard";
        return NavigateAsync(parent, SortMode);
    }

    [RelayCommand] Task Refresh() => LoadAsync();

    public Task NavigateAsync(string path, string? sort = null)
    {
        CurrentPath = path;
        if (sort != null) SortMode = sort;
        return LoadAsync();
    }

    public async Task LoadAsync()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        IsLoading = true;
        foreach (var old in Items) old.Thumbnail?.Dispose();
        Items.Clear();
        Selected = null;
        Say(Loc.F("Reading {0}…", CurrentPath));

        try
        {
            if (_s.Demo)
            {
                var now = DateTime.Now;
                var demo = new[]
                {
                    new DeviceFileEntry("IMG_20261122_142011.jpg", CurrentPath + "/IMG_20261122_142011.jpg", false, 3_420_000, now.AddMinutes(-8), "image"),
                    new DeviceFileEntry("Screenshot_20261122-140502.png", CurrentPath + "/Screenshot_20261122-140502.png", false, 845_000, now.AddMinutes(-24), "image"),
                    new DeviceFileEntry("VID_20261121_193012.mp4", CurrentPath + "/VID_20261121_193012.mp4", false, 24_800_000, now.AddHours(-18), "video"),
                    new DeviceFileEntry("receipt.pdf", CurrentPath + "/receipt.pdf", false, 210_000, now.AddDays(-2), "doc"),
                };
                foreach (var e in demo) Items.Add(new DeviceFileItem(e));
                Say(Loc.Count(Items.Count, "{0} item", "{0} items"));
                return;
            }

            var serial = await _s.Directory!.ConnectAsync(_row);
            if (serial == null || _s.Helpers == null) { Say(Loc.F("Couldn't connect to {0}.", _row.Name), StatusKind.Error); return; }
            var helper = await _s.Helpers.GetOrStartAsync(serial, _row.Type == "Watch", ct);
            if (helper == null || !helper.Supports("files_ls")) { Say(Loc.T("Couldn't start the on-device file helper."), StatusKind.Error); return; }

            var entries = await helper.ListFilesAsync(CurrentPath, SortMode, false, ct);
            if (entries == null)
            {
                // Screenshots folder differs on some OEMs (/sdcard/DCIM/Screenshots).
                if (CurrentPath == "/sdcard/Pictures/Screenshots")
                {
                    entries = await helper.ListFilesAsync("/sdcard/DCIM/Screenshots", SortMode, false, ct);
                    if (entries != null) CurrentPath = "/sdcard/DCIM/Screenshots";
                }
            }
            if (entries == null) { Say(Loc.F("Folder not found: {0}", CurrentPath), StatusKind.Warn); return; }

            foreach (var e in entries) Items.Add(new DeviceFileItem(e));
            Say(Loc.Count(Items.Count, "{0} item", "{0} items"));
            IsLoading = false;

            // Load thumbnails lazily on-device (fast ~15 KB JPEGs) without blocking the UI.
            var previewItems = Items.Where(i => i.CanPreview).Take(80).ToList();
            _ = Task.Run(async () =>
            {
                foreach (var item in previewItems)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var jpg = await helper.GetThumbnailAsync(item.Path, 200, ct);
                        if (jpg == null || ct.IsCancellationRequested) continue;
                        using var ms = new MemoryStream(jpg);
                        var bmp = new Bitmap(ms);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ct.IsCancellationRequested) bmp.Dispose();
                            else item.Thumbnail = bmp;
                        });
                    }
                    catch { }
                }
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Say(Loc.T("Couldn't read folder: ") + ex.Message, StatusKind.Error); }
        finally { IsLoading = false; }
    }

    bool CanOpenFolder() => Selected is { IsDir: true };

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    Task OpenFolder() => Selected is { IsDir: true } d ? NavigateAsync(d.Path) : Task.CompletedTask;

    public Task ActivateItemAsync(DeviceFileItem item) => item.IsDir ? NavigateAsync(item.Path) : DownloadAsync(item);

    bool CanDownload() => Selected is { IsDir: false };

    [RelayCommand(CanExecute = nameof(CanDownload))]
    Task Download() => Selected is { IsDir: false } f ? DownloadAsync(f) : Task.CompletedTask;

    async Task DownloadAsync(DeviceFileItem item)
    {
        if (_s.Demo) { Say(Loc.F("Demo mode: would save {0}.", item.Name), StatusKind.Ok); return; }
        var target = PickSavePathAsync != null
            ? await PickSavePathAsync(item.Name)
            : Path.Combine(ScriptRunner.OutputDir, item.Name);
        if (string.IsNullOrEmpty(target)) return;

        var serial = await _s.Directory!.ConnectAsync(_row);
        if (serial == null || _s.Helpers == null) return;
        var helper = await _s.Helpers.GetOrStartAsync(serial, _row.Type == "Watch");
        if (helper == null) return;

        Say(Loc.F("Saving {0}…", item.Name));
        var ok = await helper.DownloadFileAsync(item.Path, target);
        if (ok)
        {
            Say(Loc.F("Saved: {0}", target), StatusKind.Ok);
            _main.Report(Loc.F("Saved {0} from {1}.", item.Name, _row.Name), StatusKind.Ok);
            ToolbarViewModel.Reveal(target);
        }
        else Say(Loc.F("Couldn't save {0}.", item.Name), StatusKind.Error);
    }

    [RelayCommand]
    async Task Upload()
    {
        if (PickUploadFilesAsync == null) return;
        var files = await PickUploadFilesAsync();
        if (files.Count == 0) return;
        if (_s.Demo) { Say(Loc.F("Demo mode: would upload {0} file(s).", files.Count), StatusKind.Ok); return; }

        var serial = await _s.Directory!.ConnectAsync(_row);
        if (serial == null || _s.Helpers == null) return;
        var helper = await _s.Helpers.GetOrStartAsync(serial, _row.Type == "Watch");
        if (helper == null) return;

        var okCount = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            Say(Loc.F("Uploading {0}…", name));
            var remote = CurrentPath.TrimEnd('/') + "/" + name;
            if (await helper.UploadFileAsync(file, remote)) okCount++;
        }
        Say(Loc.F("Uploaded {0} of {1}.", okCount, files.Count), okCount == files.Count ? StatusKind.Ok : StatusKind.Warn);
        await LoadAsync();
    }

    [RelayCommand]
    async Task NewFolder()
    {
        if (PromptAsync == null) return;
        var name = await PromptAsync(Loc.T("New folder"), Loc.T("Folder name:"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().Replace('/', '_');
        if (_s.Demo) { Say(Loc.F("Demo mode: would create {0}.", name), StatusKind.Ok); return; }

        var serial = await _s.Directory!.ConnectAsync(_row);
        if (serial == null || _s.Helpers == null) return;
        var helper = await _s.Helpers.GetOrStartAsync(serial, _row.Type == "Watch");
        if (helper == null) return;

        var ok = await helper.MakeDirAsync(CurrentPath.TrimEnd('/') + "/" + name);
        Say(ok ? Loc.F("Created {0}.", name) : Loc.T("Couldn't create the folder."), ok ? StatusKind.Ok : StatusKind.Error);
        if (ok) await LoadAsync();
    }

    bool CanDelete() => Selected != null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    async Task Delete()
    {
        if (Selected is not { } item) return;
        if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Delete from device"), Loc.F("Delete “{0}” from {1}? This can’t be undone.", item.Name, _row.Name)))
            return;
        if (_s.Demo) { Items.Remove(item); Say(Loc.T("Demo mode: nothing was deleted."), StatusKind.Ok); return; }

        var serial = await _s.Directory!.ConnectAsync(_row);
        if (serial == null || _s.Helpers == null) return;
        var helper = await _s.Helpers.GetOrStartAsync(serial, _row.Type == "Watch");
        if (helper == null) return;

        var ok = await helper.DeleteFileAsync(item.Path);
        if (ok) { item.Thumbnail?.Dispose(); Items.Remove(item); Say(Loc.F("Deleted {0}.", item.Name), StatusKind.Ok); }
        else Say(Loc.F("Couldn't delete {0}.", item.Name), StatusKind.Error);
    }

    public void Dispose()
    {
        _thumbCts?.Cancel();
        foreach (var item in Items) item.Thumbnail?.Dispose();
    }
}
