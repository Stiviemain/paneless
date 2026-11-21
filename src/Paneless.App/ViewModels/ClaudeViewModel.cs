using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Claude;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>A permission level in a drop-down; Value null = "use the default".</summary>
public sealed record AccessChoice(string Label, ClaudeAccess? Value)
{
    public override string ToString() => Label;
}

/// <summary>A device or group and what Claude may do on it.</summary>
public sealed partial class AccessRow : ObservableObject
{
    readonly Action<ClaudeAccess?> _set;
    public AccessRow(string name, string detail, ClaudeAccess? current, IReadOnlyList<AccessChoice> choices, Action<ClaudeAccess?> set)
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

public sealed class ActivityItem(ClaudeActivity a)
{
    public string Time => a.Time.ToString("T");
    public string Tool => a.Tool;
    public string Target => a.Target;
    public string Detail => a.Detail;
    public IBrush Brush => a.Ok ? Theme.Dim : Theme.Amber;
    public string Mark => a.Ok ? "✓" : "✗";
}

/// <summary>The Claude page: let Claude use Paneless (MCP), set it up, decide what it may do, watch what it did.</summary>
public sealed partial class ClaudeViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly McpServer _server;
    readonly ClaudeTools _tools;

    public ClaudeViewModel(AppServices s, MainViewModel main, McpServer server, ClaudeTools tools)
    {
        _s = s; _main = main; _server = server; _tools = tools;
        DefaultChoices = Levels(null);
        _defaultAccess = DefaultChoices.First(c => c.Value == s.Data.Settings.ClaudeDefaultAccess);
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

    static string LevelName(ClaudeAccess a) => a switch
    {
        ClaudeAccess.Off => Loc.T("Nothing (hidden from Claude)"),
        ClaudeAccess.Read => Loc.T("Read: status, apps, logs"),
        ClaudeAccess.Screen => Loc.T("See: also screenshots"),
        ClaudeAccess.Control => Loc.T("Control: also taps, typing, apps"),
        _ => Loc.T("Manage: also installs, files, commands"),
    };

    /// <summary>The levels, optionally starting with "Default (…)".</summary>
    IReadOnlyList<AccessChoice> Levels(ClaudeAccess? inherited) =>
        [.. inherited is { } i ? [new AccessChoice(Loc.F("Default ({0})", LevelName(i).Split(':')[0]), null)] : Array.Empty<AccessChoice>(),
         .. Enum.GetValues<ClaudeAccess>().Select(a => new AccessChoice(LevelName(a), a))];

    public bool Enabled
    {
        get => _s.Data.Settings.ClaudeEnabled;
        set
        {
            _s.Data.Settings.ClaudeEnabled = value;
            _s.SaveSettings();
            if (value && !_s.Demo) _server.Start(); else _server.Stop();
            OnPropertyChanged();
            UpdateStatus();
        }
    }

    public bool Confirm
    {
        get => _s.Data.Settings.ClaudeConfirm;
        set { _s.Data.Settings.ClaudeConfirm = value; _s.SaveSettings(); OnPropertyChanged(); }
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
            _s.Data.Settings.ClaudeDefaultAccess = v;
            _s.SaveSettings();
            OnPropertyChanged();
            RebuildAccess();
        }
    }

    void UpdateStatus()
    {
        if (!Enabled) { Status = Loc.T("Off: Claude can’t use Paneless."); StatusBrush = Theme.Dim; return; }
        if (Paused) { Status = Loc.T("Paused: Claude’s requests are refused until you resume."); StatusBrush = Theme.Amber; return; }
        Status = _server.Clients > 0
            ? Loc.Count(_server.Clients, "Claude is connected ({0} session).", "Claude is connected ({0} sessions).")
            : Loc.T("Ready: waiting for Claude to connect.");
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
            var inherited = group?.Profile?.Claude ?? _s.Data.Settings.ClaudeDefaultAccess;
            var id = r.GroupId;
            Devices.Add(new AccessRow(r.Name, group != null ? Loc.F("in {0}", group.Name) : r.Model, _s.Profiles.Get(id)?.Claude, Levels(inherited), v =>
            {
                _s.Profiles.GetOrCreate(id).Claude = v;
                _s.Profiles.Save();
            }));
        }
        GroupRows.Clear();
        foreach (var g in _s.Groups.Groups)
        {
            var grp = g;
            GroupRows.Add(new AccessRow(g.Name, Loc.Count(g.Members.Count, "{0} device", "{0} devices"), g.Profile?.Claude, Levels(_s.Data.Settings.ClaudeDefaultAccess), v =>
            {
                (grp.Profile ??= new DeviceProfile()).Claude = v;
                _s.Groups.Save();
            }));
        }
    }

    // ---------------------------------------------------------------- setup

    /// <summary>The program Claude starts. Demo mode shows a stand-in, so screenshots don't show a real user folder.</summary>
    string Exe => _s.Demo ? @"C:\Users\you\AppData\Local\PanelessApp\current\Paneless.exe"
        : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Paneless.exe");

    public string DesktopSnippet => new JsonObject
    {
        ["mcpServers"] = new JsonObject { ["paneless"] = new JsonObject { ["command"] = Exe, ["args"] = new JsonArray("--mcp") } },
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    public string CodeCommand => $"claude mcp add paneless --scope user -- \"{Exe}\" --mcp";

    public string ExamplePrompts => Loc.T("Which of my devices have less than 20 % battery?\nInstall C:\\Builds\\app.apk on the Test lab group.\nOpen Settings on the Pixel and turn on dark mode.\nWhy does my app crash on the tablet? Read its log.");

    [RelayCommand]
    async Task CopyDesktop()
    {
        if (CopyAsync != null) await CopyAsync(DesktopSnippet);
        Message = Loc.T("Copied. In Claude Desktop: Settings › Developer › Edit config, add it, then restart Claude.");
    }

    [RelayCommand]
    async Task CopyCode()
    {
        if (CopyAsync != null) await CopyAsync(CodeCommand);
        Message = Loc.T("Copied. Run it once in a terminal; Claude Code then has Paneless in every project.");
    }

    /// <summary>Adds Paneless to Claude Desktop's configuration file (keeps everything else in it).</summary>
    [RelayCommand]
    async Task AddToDesktop()
    {
        if (_s.Demo) { Message = Loc.T("Demo mode: nothing was changed."); return; }
        var files = ClaudeDesktopConfigs();
        if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Add to Claude Desktop"),
                Loc.F("Paneless adds itself to Claude Desktop’s settings file ({0}). Restart Claude Desktop afterwards.", string.Join(", ", files))))
            return;
        var done = 0;
        foreach (var file in files)
        {
            try
            {
                var root = File.Exists(file) ? JsonNode.Parse(await File.ReadAllTextAsync(file)) as JsonObject ?? new JsonObject() : new JsonObject();
                if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
                servers["paneless"] = new JsonObject { ["command"] = Exe, ["args"] = new JsonArray("--mcp") };
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (File.Exists(file)) File.Copy(file, file + ".bak", overwrite: true);
                await File.WriteAllTextAsync(file, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                done++;
            }
            catch (Exception ex) { Message = Loc.F("Couldn’t change {0}: {1}", file, ex.Message); return; }
        }
        if (!Enabled) Enabled = true;
        Message = Loc.T("Added. Restart Claude Desktop (quit it from the notification area), then ask it about your devices.");
    }

    /// <summary>Claude Desktop's config: %APPDATA%\Claude, and the Store version's own copy of it.</summary>
    static List<string> ClaudeDesktopConfigs()
    {
        var list = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"));
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            try
            {
                foreach (var pkg in Directory.EnumerateDirectories(packages, "Claude_*"))
                    list.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
            }
            catch { }
        }
        else if (OperatingSystem.IsMacOS())
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Claude", "claude_desktop_config.json"));
        else
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Claude", "claude_desktop_config.json"));
        return list;
    }

    [RelayCommand]
    void ClearActivity()
    {
        Activity.Clear();
        OnPropertyChanged(nameof(NoActivity));
    }
}
