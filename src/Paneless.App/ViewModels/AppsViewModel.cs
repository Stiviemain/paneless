using Paneless.App.Localization;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed partial class AppItem(AppPackage p, string? label = null, string? meta = null) : ObservableObject
{
    public string Package { get; } = p.Name;
    public string Label { get; } = string.IsNullOrWhiteSpace(label) ? p.Name : label.Trim();
    public bool HasSeparateLabel => !string.Equals(Label, Package, StringComparison.OrdinalIgnoreCase);
    public string Meta { get; } = meta ?? "";
    public bool HasMeta => Meta.Length > 0;
    [ObservableProperty] public partial Bitmap? Icon { get; set; }
    internal bool HasRealIcon { get; set; }
    [ObservableProperty] public partial bool Disabled { get; set; } = p.Disabled;
    partial void OnDisabledChanged(bool value) => OnPropertyChanged(nameof(DisabledLabel));
    public string DisabledLabel => Disabled ? Loc.T("disabled") : "";
}

/// <summary>The apps of one device: open (also in a window of its own), stop, clear, disable, uninstall, install.</summary>
public sealed partial class AppsViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly DeviceRow _row;
    readonly Dictionary<string, Bitmap> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    List<AppItem> _all = [];
    string? _serial;
    int _loadGeneration;

    public AppsViewModel(AppServices services, MainViewModel main, DeviceRow row)
    {
        _s = services;
        _main = main;
        _row = row;
        GroupName = _s.Groups.GroupOf(row.GroupId)?.Name;
    }

    /// <summary>Cancels any in-flight icon loading when the window closes.</summary>
    public void StopLoading() => _loadGeneration++;

    /// <summary>Set by the window.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }
    public Func<Task<IReadOnlyList<string>>>? PickApksAsync { get; set; }

    public string Title => Loc.F("Apps on {0}", _row.Name);
    public string? GroupName { get; }
    public bool InGroup => GroupName != null;
    public string AlsoOnGroupText => Loc.F("Also install on the other devices in {0}", GroupName);
    public ObservableCollection<AppItem> Apps { get; } = new();

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial bool ShowSystem { get; set; }
    [ObservableProperty] public partial bool AlsoOnGroup { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial IBrush MessageBrush { get; set; } = Theme.Dim;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(OpenInWindowCommand), nameof(AddToRemoteAppsCommand), nameof(ForceStopCommand), nameof(ClearDataCommand),
                                nameof(ToggleEnabledCommand), nameof(UninstallCommand))]
    public partial AppItem? Selected { get; set; }

    public string ToggleLabel => Selected?.Disabled == true ? Loc.T("Enable") : Loc.T("Disable");
    partial void OnSelectedChanged(AppItem? value) => OnPropertyChanged(nameof(ToggleLabel));
    partial void OnFilterChanged(string value) => Show();
    partial void OnShowSystemChanged(bool value) => _ = LoadAsync();

    void Say(string text, StatusKind kind = StatusKind.Info) { Message = text; MessageBrush = Theme.For(kind); }

    AppItem MakeItem(AppPackage pkg, string? label = null, string? meta = null)
    {
        var item = new AppItem(pkg, label, meta);
        if (_iconCache.TryGetValue(pkg.Name, out var cached))
        {
            item.Icon = cached;
            item.HasRealIcon = true;
        }
        else
        {
            item.Icon = RemoteAppIcons.LetterTile(item.Label, 64);
        }
        return item;
    }

    public async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        IsLoading = true;
        Say(Loc.T("Loading the app list…"));
        try
        {
            if (_s.Demo)
            {
                (string Pkg, string Label, string Meta, bool Disabled)[] demo =
                [
                    ("com.whatsapp", "WhatsApp", "v2.26.4 · 84 MB", false),
                    ("com.spotify.music", "Spotify", "v9.0.18 · 61 MB", false),
                    ("org.mozilla.firefox", "Firefox", "v136.0 · 112 MB", false),
                    ("com.google.android.youtube", "YouTube", "v20.11 · 154 MB", false),
                    ("com.netflix.mediaclient", "Netflix", "v8.141 · 73 MB", true),
                    ("de.db.navigator", "DB Navigator", "v25.4 · 48 MB", false),
                ];
                _all = demo.Select(d => MakeItem(new AppPackage(d.Pkg, d.Disabled), d.Label, d.Meta))
                    .OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Show();
                Say(Loc.F(ShowSystem ? "{0} apps including system apps. Double-click to open one." : "{0} apps you installed. Double-click to open one.", _all.Count));
                return;
            }

            _serial ??= await _s.Directory!.ConnectAsync(_row);
            if (generation != _loadGeneration) return;
            if (_serial == null) { Say(Loc.F("Couldn't connect to {0}.", _row.Name), StatusKind.Error); return; }

            List<DetailedAppPackage>? detailed = null;
            DeviceHelperClient? helper = null;
            if (_s.Helpers != null)
            {
                helper = await _s.Helpers.GetOrStartAsync(_serial, _row.Type == "Watch");
                if (generation != _loadGeneration) return;
                if (helper != null && helper.Supports("apps_detail"))
                    try { detailed = await helper.GetDetailedAppsAsync(ShowSystem); } catch { }
            }
            if (generation != _loadGeneration) return;

            if (detailed != null)
            {
                _all = detailed.Select(d =>
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(d.VersionName)) parts.Add("v" + d.VersionName);
                    if (d.ApkSizeBytes > 0) parts.Add(d.ApkSizeBytes >= 1024 * 1024 ? $"{d.ApkSizeBytes / (1024.0 * 1024.0):0.#} MB" : $"{d.ApkSizeBytes / 1024.0:0} KB");
                    return MakeItem(new AppPackage(d.Package, d.Disabled), d.Label, string.Join(" · ", parts));
                })
                .OrderBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Package, StringComparer.OrdinalIgnoreCase)
                .ToList();
            }
            else
            {
                _all = (await _s.Adb!.GetPackagesAsync(_serial, ShowSystem))
                    .Select(p => MakeItem(p))
                    .OrderBy(a => a.Package, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            if (generation != _loadGeneration) return;
            Show();
            Say(Loc.F(ShowSystem ? "{0} apps including system apps. Double-click to open one." : "{0} apps you installed. Double-click to open one.", _all.Count));
            IsLoading = false;

            // Load real app icons in the background (visible/filtered rows first so searching shows icons right away).
            if (_all.Any(a => !a.HasRealIcon))
                _ = LoadIconsAsync(_serial, generation);
        }
        catch (Exception ex) { Say(Loc.T("Couldn't read the app list: ") + ex.Message, StatusKind.Error); }
        finally { IsLoading = false; }
    }

    async Task LoadIconsAsync(string serial, int generation)
    {
        var first = true;
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (generation == _loadGeneration)
        {
            var visible = Apps.Where(a => !a.HasRealIcon && !attempted.Contains(a.Package)).Take(first ? 24 : 50).ToList();
            var batch = visible.Count > 0 ? visible : _all.Where(a => !a.HasRealIcon && !attempted.Contains(a.Package)).Take(50).ToList();
            if (batch.Count == 0) break;
            first = false;
            foreach (var a in batch) attempted.Add(a.Package);

            Dictionary<string, (string Name, byte[] Png)>? icons = null;
            if (_s.Helpers != null && await _s.Helpers.GetOrStartAsync(serial, _row.Type == "Watch") is { } h && h.Supports("icons"))
                try { icons = await h.GetAppIconsAsync(64, batch.Select(a => a.Package)); } catch { }
            icons ??= await _s.Adb!.GetAppIconsAsync(serial, 64, batch.Select(a => a.Package));
            if (generation != _loadGeneration) return;

            foreach (var a in batch)
            {
                if (icons.TryGetValue(a.Package, out var i) && RemoteAppIcons.FromPng(i.Png) is { } bmp)
                {
                    a.Icon = bmp;
                    a.HasRealIcon = true;
                    _iconCache[a.Package] = bmp;
                }
            }
        }
    }

    void Show()
    {
        var q = Filter.Trim();
        var keep = Selected?.Package;
        Apps.Clear();
        foreach (var a in _all.Where(a => q.Length == 0
            || a.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Package.Contains(q, StringComparison.OrdinalIgnoreCase)))
            Apps.Add(a);
        Selected = Apps.FirstOrDefault(a => a.Package == keep);
    }

    bool HasSelected() => Selected != null;

    async Task<bool> Shell(string command, string done)
    {
        if (_s.Demo) { Say(Loc.F("Demo mode: {0}", done), StatusKind.Ok); return true; }
        if (_serial == null) return false;
        var r = await _s.Adb!.ShellAsync(_serial, command, TimeSpan.FromSeconds(30));
        var failed = !r.Ok || r.Output.Contains("Failure", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("Exception");
        Say(failed ? Loc.F("Didn't work: {0}", r.LastLine) : done, failed ? StatusKind.Error : StatusKind.Ok);
        return !failed;
    }

    static string Q(string p) => ScriptText.ShellQuote(p);

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task Open()
    {
        var p = Selected!.Package;
        if (_s.Demo) { Say(Loc.F("Demo mode: would open {0}.", p), StatusKind.Ok); return; }
        if (_serial == null) return;
        var r = await _s.Adb!.LaunchAsync(_serial, p);
        Say(r.Output.Contains("No activities found") ? Loc.F("{0} has no screen to open.", p) : Loc.F("Opened {0} on {1}.", p, _row.Name),
            r.Output.Contains("No activities found") ? StatusKind.Warn : StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task OpenInWindow()
    {
        await _main.OpenAppWindowAsync(_row, Selected!.Package);
        Say(Loc.F("Opening {0} in a window of its own (a separate screen of the device, so its own screen stays as it is).", Selected!.Package), StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task AddToRemoteApps()
    {
        if (_main.AddRemoteApp == null) return;
        var a = Selected!;
        Say(Loc.F("Adding {0} to Remote apps…", a.Label));
        Say(await _main.AddRemoteApp(_row, a.Package, a.HasSeparateLabel ? a.Label : null), StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    Task ForceStop() => Shell($"am force-stop {Q(Selected!.Package)}", Loc.F("Stopped {0}.", Selected!.Package));

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task ClearData()
    {
        var p = Selected!.Package;
        if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Clear app data"), Loc.F("Delete all data of {0} on {1} (logins, settings, downloads in the app)? This can't be undone.", p, _row.Name))) return;
        await Shell($"pm clear {Q(p)}", Loc.F("Cleared the data of {0}.", p));
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task ToggleEnabled()
    {
        var a = Selected!;
        var enable = a.Disabled;
        if (!enable && ConfirmAsync != null && !await ConfirmAsync(Loc.T("Disable app"), Loc.F("Disable {0}? It disappears from the launcher and doesn't run until you enable it again here.", a.Package))) return;
        if (await Shell(enable ? $"pm enable {Q(a.Package)}" : $"pm disable-user --user 0 {Q(a.Package)}", enable ? Loc.F("Enabled {0}.", a.Package) : Loc.F("Disabled {0}.", a.Package)))
        {
            a.Disabled = !enable;
            OnPropertyChanged(nameof(ToggleLabel));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task Uninstall()
    {
        var a = Selected!;
        var system = ShowSystem;
        var text = system
            ? Loc.F("Remove {0} for your user on {1}? System apps stay on the device and can be brought back with the built-in script \"Restore a removed system app\".", a.Package, _row.Name)
            : Loc.F("Uninstall {0} from {1}? Its data is deleted.", a.Package, _row.Name);
        if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Uninstall"), text)) return;
        if (await Shell($"pm uninstall --user 0 {Q(a.Package)}", Loc.F("Uninstalled {0}.", a.Package)))
        {
            _all.Remove(a);
            Apps.Remove(a);
        }
    }

    [RelayCommand]
    async Task Install()
    {
        if (PickApksAsync == null) return;
        var files = (await PickApksAsync()).Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) return;
        var targets = new List<DeviceRow> { _row };
        if (AlsoOnGroup && _s.Groups.GroupOf(_row.GroupId) is { } g)
            targets.AddRange(_main.NetworkRows.Where(r => r.Key != _row.Key && g.Members.Contains(r.GroupId)));
        Say(Loc.F("Installing on {0}…", Loc.Count(targets.Count, "{0} device", "{0} devices")));
        await _main.SendFilesAsync(files, targets);
        Say(Loc.T("Done. See the main window's status line for the result."), StatusKind.Ok);
        await LoadAsync();
    }

    [RelayCommand]
    Task Reload() => LoadAsync();
}
