using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed partial class AppItem(AppPackage p) : ObservableObject
{
    public string Package { get; } = p.Name;
    [ObservableProperty] public partial bool Disabled { get; set; } = p.Disabled;
    partial void OnDisabledChanged(bool value) => OnPropertyChanged(nameof(Note));
    public string Note => Disabled ? "disabled" : "";
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

    public string Title => $"Apps on {_row.Name}";
    public string? GroupName { get; }
    public bool InGroup => GroupName != null;
    public string AlsoOnGroupText => $"Also install on the other devices in {GroupName}";
    public ObservableCollection<AppItem> Apps { get; } = new();

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial bool ShowSystem { get; set; }
    [ObservableProperty] public partial bool AlsoOnGroup { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial IBrush MessageBrush { get; set; } = Theme.Dim;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(OpenInWindowCommand), nameof(ForceStopCommand), nameof(ClearDataCommand),
                                nameof(ToggleEnabledCommand), nameof(UninstallCommand))]
    public partial AppItem? Selected { get; set; }

    public string ToggleLabel => Selected?.Disabled == true ? "Enable" : "Disable";
    partial void OnSelectedChanged(AppItem? value) => OnPropertyChanged(nameof(ToggleLabel));
    partial void OnFilterChanged(string value) => Show();
    partial void OnShowSystemChanged(bool value) => _ = LoadAsync();

    void Say(string text, StatusKind kind = StatusKind.Info) { Message = text; MessageBrush = Theme.For(kind); }

    public async Task LoadAsync()
    {
        IsLoading = true;
        Say("Loading the app list…");
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
                if (_serial == null) { Say($"Couldn't connect to {_row.Name}.", StatusKind.Error); return; }
                _all = (await _s.Adb!.GetPackagesAsync(_serial, ShowSystem)).Select(p => new AppItem(p)).ToList();
            }
            Show();
            Say($"{_all.Count} {(ShowSystem ? "apps including system apps" : "apps you installed")}. Double-click to open one.");
        }
        catch (Exception ex) { Say("Couldn't read the app list: " + ex.Message, StatusKind.Error); }
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
        if (_s.Demo) { Say($"Demo mode: {done}", StatusKind.Ok); return true; }
        if (_serial == null) return false;
        var r = await _s.Adb!.ShellAsync(_serial, command, TimeSpan.FromSeconds(30));
        var failed = !r.Ok || r.Output.Contains("Failure", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("Exception");
        Say(failed ? $"Didn't work: {r.LastLine}" : done, failed ? StatusKind.Error : StatusKind.Ok);
        return !failed;
    }

    static string Q(string p) => ScriptText.ShellQuote(p);

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task Open()
    {
        var p = Selected!.Package;
        if (_s.Demo) { Say($"Demo mode: would open {p}.", StatusKind.Ok); return; }
        if (_serial == null) return;
        var r = await _s.Adb!.LaunchAsync(_serial, p);
        Say(r.Output.Contains("No activities found") ? $"{p} has no screen to open." : $"Opened {p} on {_row.Name}.",
            r.Output.Contains("No activities found") ? StatusKind.Warn : StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task OpenInWindow()
    {
        await _main.OpenAppWindowAsync(_row, Selected!.Package);
        Say($"Opening {Selected!.Package} in a window of its own (a separate screen of the device, so its own screen stays as it is).", StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    Task ForceStop() => Shell($"am force-stop {Q(Selected!.Package)}", $"Stopped {Selected!.Package}.");

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task ClearData()
    {
        var p = Selected!.Package;
        if (ConfirmAsync != null && !await ConfirmAsync("Clear app data", $"Delete all data of {p} on {_row.Name} (logins, settings, downloads in the app)? This can't be undone.")) return;
        await Shell($"pm clear {Q(p)}", $"Cleared the data of {p}.");
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    async Task ToggleEnabled()
    {
        var a = Selected!;
        var enable = a.Disabled;
        if (!enable && ConfirmAsync != null && !await ConfirmAsync("Disable app", $"Disable {a.Package}? It disappears from the launcher and doesn't run until you enable it again here.")) return;
        if (await Shell(enable ? $"pm enable {Q(a.Package)}" : $"pm disable-user --user 0 {Q(a.Package)}", enable ? $"Enabled {a.Package}." : $"Disabled {a.Package}."))
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
            ? $"Remove {a.Package} for your user on {_row.Name}? System apps stay on the device and can be brought back with the built-in script \"Restore a removed system app\"."
            : $"Uninstall {a.Package} from {_row.Name}? Its data is deleted.";
        if (ConfirmAsync != null && !await ConfirmAsync("Uninstall", text)) return;
        if (await Shell($"pm uninstall --user 0 {Q(a.Package)}", $"Uninstalled {a.Package}."))
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
        Say($"Installing on {targets.Count} device{(targets.Count == 1 ? "" : "s")}…");
        await _main.SendFilesAsync(files, targets);
        Say($"Done. See the main window's status line for the result.", StatusKind.Ok);
        await LoadAsync();
    }

    [RelayCommand]
    Task Reload() => LoadAsync();
}
