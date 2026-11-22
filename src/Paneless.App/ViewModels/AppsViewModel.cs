using Paneless.App.Localization;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed partial class AppItem(AppPackage p, string? meta = null) : ObservableObject
{
    public string Package { get; } = p.Name;
    public string Meta { get; } = meta ?? "";
    [ObservableProperty] public partial bool Disabled { get; set; } = p.Disabled;
    partial void OnDisabledChanged(bool value) => OnPropertyChanged(nameof(Note));
    public string Note => Disabled ? (Meta.Length > 0 ? $"{Meta} · {Loc.T("disabled")}" : Loc.T("disabled")) : Meta;
}

/// <summary>The apps of one device: open (also in a window of its own), stop, clear, disable, uninstall, install.</summary>
public sealed partial class AppsViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly DeviceRow _row;
    List<AppItem> _all = [];
    string? _serial;

    public AppsViewModel(AppServices services, MainViewModel main, DeviceRow row)
    {
        _s = services;
        _main = main;
        _row = row;
        GroupName = _s.Groups.GroupOf(row.GroupId)?.Name;
    }

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

    public async Task LoadAsync()
    {
        IsLoading = true;
        Say(Loc.T("Loading the app list…"));
        try
        {
            if (_s.Demo)
            {
                string[] demo = ["com.whatsapp", "com.spotify.music", "org.mozilla.firefox", "com.google.android.youtube", "com.netflix.mediaclient", "de.db.navigator"];
                _all = demo.Select(p => new AppItem(new AppPackage(p, p.Contains("netflix")))).ToList();
            }
            else
            {
                _serial ??= await _s.Directory!.ConnectAsync(_row);
                if (_serial == null) { Say(Loc.F("Couldn't connect to {0}.", _row.Name), StatusKind.Error); return; }
                List<DetailedAppPackage>? detailed = null;
                if (_s.Helpers != null && await _s.Helpers.GetOrStartAsync(_serial, _row.Type == "Watch") is { } helper && helper.Supports("apps_detail"))
                    try { detailed = await helper.GetDetailedAppsAsync(ShowSystem); } catch { }
                if (detailed != null)
                {
                    _all = detailed.Select(d =>
                    {
                        var parts = new List<string>();
                        if (!string.IsNullOrEmpty(d.VersionName)) parts.Add("v" + d.VersionName);
                        if (d.ApkSizeBytes > 0) parts.Add(d.ApkSizeBytes >= 1024 * 1024 ? $"{d.ApkSizeBytes / (1024.0 * 1024.0):0.#} MB" : $"{d.ApkSizeBytes / 1024.0:0} KB");
                        return new AppItem(new AppPackage(d.Package, d.Disabled), string.Join(" · ", parts));
                    }).ToList();
                }
                else
                {
                    _all = (await _s.Adb!.GetPackagesAsync(_serial, ShowSystem)).Select(p => new AppItem(p)).ToList();
                }
            }
            Show();
            Say(Loc.F(ShowSystem ? "{0} apps including system apps. Double-click to open one." : "{0} apps you installed. Double-click to open one.", _all.Count));
        }
        catch (Exception ex) { Say(Loc.T("Couldn't read the app list: ") + ex.Message, StatusKind.Error); }
        finally { IsLoading = false; }
    }

    void Show()
    {
        var q = Filter.Trim();
        var keep = Selected?.Package;
        Apps.Clear();
        foreach (var a in _all.Where(a => q.Length == 0 || a.Package.Contains(q, StringComparison.OrdinalIgnoreCase))) Apps.Add(a);
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
        Say(Loc.F("Adding {0} to Remote apps…", Selected!.Package));
        Say(await _main.AddRemoteApp(_row, Selected!.Package, null), StatusKind.Ok);
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
