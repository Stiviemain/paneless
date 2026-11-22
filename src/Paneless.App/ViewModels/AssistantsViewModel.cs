using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Assistants;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>A permission level in a drop-down; Value null = "use the default".</summary>
public sealed record AccessChoice(string Label, AssistantAccess? Value)
{
    public override string ToString() => Label;
}

/// <summary>A device or group and what AI assistants may do on it.</summary>
public sealed partial class AccessRow : ObservableObject
{
    readonly Action<AssistantAccess?> _set;
    public AccessRow(string name, string detail, AssistantAccess? current, IReadOnlyList<AccessChoice> choices, Action<AssistantAccess?> set)
    {
        Name = name; Detail = detail; Choices = choices; _set = set;
        _selected = choices.FirstOrDefault(c => c.Value == current) ?? choices[0];
    }

    public string Name { get; }
    public string Detail { get; }
    public IReadOnlyList<AccessChoice> Choices { get; }
    AccessChoice _selected;
    public AccessChoice Selected { get => _selected; set { if (value == null) return; _selected = value; _set(value.Value); OnPropertyChanged(); } }
}

public sealed class ActivityItem(AssistantActivity a)
{
    public string Time => a.Time.ToString("T");
    public string Client => a.Client;
    public string Tool => a.Tool;
    public string Target => a.Target;
    public string Detail => a.Detail;
    public IBrush Brush => a.Ok ? Theme.Dim : Theme.Amber;
    public string Mark => a.Ok ? "✓" : "✗";
}

/// <summary>The AI page: let AI assistants use Paneless (MCP), connect them, decide what they may do, watch what they did.</summary>
public sealed partial class AssistantsViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly McpServer _server;
    readonly AssistantTools _tools;

    public AssistantsViewModel(AppServices s, MainViewModel main, McpServer server, AssistantTools tools)
    {
        _s = s; _main = main; _server = server; _tools = tools;
        DefaultChoices = Levels(null);
        _defaultAccess = DefaultChoices.First(c => c.Value == s.Data.Settings.AssistantDefaultAccess);
        tools.Activity += a => Dispatcher.UIThread.Post(() =>
        {
            Activity.Insert(0, new ActivityItem(a));
            while (Activity.Count > 300) Activity.RemoveAt(Activity.Count - 1);
            OnPropertyChanged(nameof(NoActivity));
        });
        server.ClientsChanged += () => Dispatcher.UIThread.Post(UpdateStatus);
        main.DevicesChanged += RebuildDevicesIfChanged;
        s.Groups.Changed += () => Dispatcher.UIThread.Post(RebuildAccess);
        RebuildAccess();
        if (Enabled && !s.Demo) server.Start();
        UpdateStatus();
    }

    public IReadOnlyList<AccessChoice> DefaultChoices { get; }
    public ObservableCollection<AccessRow> Devices { get; } = new();
    public ObservableCollection<AccessRow> GroupRows { get; } = new();
    public ObservableCollection<ActivityItem> Activity { get; } = new();
    public bool NoActivity => Activity.Count == 0;

    /// <summary>Set by the view.</summary>
    public Func<string, Task>? CopyAsync { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial IBrush StatusBrush { get; set; } = Theme.Dim;
    [ObservableProperty] public partial string Message { get; set; } = "";

    static string LevelName(AssistantAccess a) => a switch
    {
        AssistantAccess.Off => Loc.T("Nothing (hidden from AI)"),
        AssistantAccess.Read => Loc.T("Read: status, apps, logs"),
        AssistantAccess.Screen => Loc.T("See: also screenshots"),
        AssistantAccess.Control => Loc.T("Control: also taps, typing, apps"),
        _ => Loc.T("Manage: also installs, files, commands"),
    };

    /// <summary>The levels, optionally starting with "Default (…)".</summary>
    IReadOnlyList<AccessChoice> Levels(AssistantAccess? inherited) =>
        [.. inherited is { } i ? [new AccessChoice(Loc.F("Default ({0})", LevelName(i).Split(':')[0]), null)] : Array.Empty<AccessChoice>(),
         .. Enum.GetValues<AssistantAccess>().Select(a => new AccessChoice(LevelName(a), a))];

    public bool Enabled
    {
        get => _s.Data.Settings.AssistantsEnabled;
        set
        {
            _s.Data.Settings.AssistantsEnabled = value;
            _s.SaveSettings();
            if (value && !_s.Demo) _server.Start(); else _server.Stop();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    public bool Confirm
    {
        get => _s.Data.Settings.AssistantConfirm;
        set { _s.Data.Settings.AssistantConfirm = value; _s.SaveSettings(); OnPropertyChanged(); }
    }

    public bool Paused
    {
        get => _tools.Paused;
        set { _tools.Paused = value; OnPropertyChanged(); UpdateStatus(); }
    }

    AccessChoice _defaultAccess;
    public AccessChoice DefaultAccess
    {
        get => _defaultAccess;
        set
        {
            if (value?.Value is not { } v) return;
            _defaultAccess = value;
            _s.Data.Settings.AssistantDefaultAccess = v;
            _s.SaveSettings();
            OnPropertyChanged();
            RebuildAccess();
        }
    }

    void UpdateStatus()
    {
        if (!Enabled) { Status = Loc.T("Off: AI assistants can’t use Paneless."); StatusBrush = Theme.Dim; return; }
        if (Paused) { Status = Loc.T("Paused: AI requests are refused until you resume."); StatusBrush = Theme.Amber; return; }
        Status = _server.Clients > 0
            ? Loc.F("Connected: {0}.", string.Join(", ", _server.ClientNames))
            : Loc.T("Ready: waiting for an AI assistant to connect.");
        StatusBrush = _server.Clients > 0 ? Theme.Green : Theme.Blue;
    }

    string _devicesShape = "";
    void RebuildDevicesIfChanged()
    {
        var shape = string.Join('|', _main.NetworkRows.Where(r => r.Sn != null).Select(r => r.GroupId + r.Name));
        if (shape != _devicesShape) RebuildAccess();
    }

    void RebuildAccess()
    {
        _devicesShape = string.Join('|', _main.NetworkRows.Where(r => r.Sn != null).Select(r => r.GroupId + r.Name));
        Devices.Clear();
        foreach (var r in _main.NetworkRows.Where(r => r.Sn != null).OrderBy(r => r.Name, NaturalComparer.Instance))
        {
            var group = _s.Groups.GroupOf(r.GroupId);
            var inherited = group?.Profile?.Assistant ?? _s.Data.Settings.AssistantDefaultAccess;
            var id = r.GroupId;
            Devices.Add(new AccessRow(r.Name, group != null ? Loc.F("in {0}", group.Name) : r.Model, _s.Profiles.Get(id)?.Assistant, Levels(inherited), v =>
            {
                _s.Profiles.GetOrCreate(id).Assistant = v;
                _s.Profiles.Save();
            }));
        }
        GroupRows.Clear();
        foreach (var g in _s.Groups.Groups)
        {
            var grp = g;
            GroupRows.Add(new AccessRow(g.Name, Loc.Count(g.Members.Count, "{0} device", "{0} devices"), g.Profile?.Assistant, Levels(_s.Data.Settings.AssistantDefaultAccess), v =>
            {
                (grp.Profile ??= new DeviceProfile()).Assistant = v;
                _s.Groups.Save();
            }));
        }
    }

    // ---------------------------------------------------------------- setup

    /// <summary>The program assistants start. Demo mode shows a stand-in, so screenshots don't show a real user folder.</summary>
    string Exe => _s.Demo ? @"C:\Users\you\AppData\Local\PanelessApp\current\Paneless.exe"
        : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Paneless.exe");

    public IReadOnlyList<AssistantApp> Apps => AssistantApps.All;

    [ObservableProperty] public partial AssistantApp App { get; set; } = AssistantApps.All[0];

    partial void OnAppChanged(AssistantApp value)
    {
        Message = "";
        OnPropertyChanged(nameof(Snippet));
        OnPropertyChanged(nameof(IsAppConfigured));
        OnPropertyChanged(nameof(ConfiguredText));
    }

    public bool IsAppConfigured => !_s.Demo && App.CanAdd && AssistantApps.IsConfigured(App.Id);
    public string ConfiguredText => IsAppConfigured ? Loc.F("✓ Configured in {0}", App.Name) : "";

    /// <summary>The command or settings for the chosen app.</summary>
    public string Snippet => App.Snippet(Exe);

    public string ExamplePrompts => Loc.T("Which of my devices have less than 20 % battery?\nInstall C:\\Builds\\app.apk on the Test lab group.\nUnlock the Pixel, open Settings and turn on dark mode.\nWhy does my app crash on the tablet? Read its log.");

    [RelayCommand]
    async Task Copy()
    {
        if (CopyAsync != null) await CopyAsync(Snippet);
        if (!Enabled)
            Message = Snippet.StartsWith('{')
                ? Loc.T("Copied. Turn on “AI assistants may use Paneless” above, then paste it into the app’s MCP settings and restart the app.")
                : Loc.T("Copied. Turn on “AI assistants may use Paneless” above, then run it once in a terminal.");
        else
            Message = Snippet.StartsWith('{')
                ? Loc.T("Copied. Paste it into the app’s MCP settings, then restart the app.")
                : Loc.T("Copied. Run it once in a terminal.");
    }

    /// <summary>Adds Paneless to the chosen app's settings file (keeps everything else in it).</summary>
    [RelayCommand]
    async Task Add()
    {
        if (!App.CanAdd) return;
        if (_s.Demo) { Message = Loc.T("Demo mode: nothing was changed."); return; }
        var files = AssistantApps.ConfigFiles(App.Id);
        if (ConfirmAsync != null && !await ConfirmAsync(App.AddLabel,
                Loc.F("Paneless adds itself to {0} (a copy of the old file is kept as .bak). Restart {1} afterwards.", string.Join(", ", files), App.Name)))
            return;
        try { await AssistantApps.AddAsync(App.Id, Exe); }
        catch (Exception ex) { Message = Loc.F("Couldn’t change the settings: {0}", ex.Message); return; }
        if (!Enabled) Enabled = true;
        OnPropertyChanged(nameof(IsAppConfigured));
        OnPropertyChanged(nameof(ConfiguredText));
        Message = Loc.F("Added. Restart {0}, then ask it about your devices.", App.Name);
    }

    [RelayCommand]
    void ClearActivity()
    {
        Activity.Clear();
        OnPropertyChanged(nameof(NoActivity));
    }
}
