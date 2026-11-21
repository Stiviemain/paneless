using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed record DeviceChoice(string Id, string Name, string Serial)
{
    public override string ToString() => Name;
}

public sealed class LogItem(LogLine l)
{
    public LogLine Line { get; } = l;
    public string Time => Line.Time.Length > 6 ? Line.Time[6..] : Line.Time;
    public string Level => Line.Level.ToString();
    public string Tag => Line.Tag;
    public string Message => Line.Message;
    public IBrush Brush => Line.IsCrash ? Theme.Red : Line.Level switch { 'E' or 'F' or 'A' => Theme.Red, 'W' => Theme.Amber, 'I' => Theme.Text, _ => Theme.Dim };
    public FontWeight Weight => Line.IsCrash ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>The Logs page: a device's live log, filterable by level, text and app; crashes stand out.</summary>
public sealed partial class LogsViewModel : ObservableObject, IDisposable
{
    const int MaxKept = 20000, MaxShown = 4000;
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly List<LogLine> _all = new();
    readonly ConcurrentQueue<LogLine> _incoming = new();
    readonly DispatcherTimer _flush;
    LogcatSession? _session;
    int? _pid;

    public LogsViewModel(AppServices s, MainViewModel main)
    {
        _s = s; _main = main;
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        main.DevicesChanged += UpdateDevices;
        Level = Levels[0];
    }

    public ObservableCollection<DeviceChoice> Devices { get; } = new();
    public ObservableCollection<LogItem> Lines { get; } = new();
    public IReadOnlyList<string> Levels { get; } = [Loc.T("Everything"), Loc.T("Debug and up"), Loc.T("Info and up"), Loc.T("Warnings and errors"), Loc.T("Errors only")];

    /// <summary>Set by the view: asks where to save (suggested name, extension).</summary>
    public Func<string, string, Task<string?>>? SaveAsAsync { get; set; }
    /// <summary>Set by the view: scrolls to the newest line.</summary>
    public Action? ScrollToEnd { get; set; }

    [ObservableProperty] public partial DeviceChoice? Device { get; set; }
    [ObservableProperty] public partial string Level { get; set; } = "";
    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial string App { get; set; } = "";
    [ObservableProperty] public partial bool CrashesOnly { get; set; }
    [ObservableProperty] public partial bool FollowNewest { get; set; } = true;
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial int Crashes { get; set; }
    public bool IsEmpty => Lines.Count == 0;
    public bool HasCrashes => Crashes > 0;
    partial void OnCrashesChanged(int value) => OnPropertyChanged(nameof(HasCrashes));
    public string StartLabel => IsRunning ? Loc.T("Stop") : Loc.T("Start");

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StartLabel));
    partial void OnLevelChanged(string value) => Rebuild();
    partial void OnFilterChanged(string value) => Rebuild();
    partial void OnCrashesOnlyChanged(bool value) => Rebuild();
    partial void OnAppChanged(string value) => _ = ResolveAppAsync();
    partial void OnDeviceChanged(DeviceChoice? value) { if (IsRunning) { Stop(); _ = StartAsync(); } }

    void UpdateDevices()
    {
        var rows = _main.NetworkRows.Where(r => r.Serial != null && r.State is DeviceState.Connected or DeviceState.Mirroring)
            .OrderBy(r => r.Name, NaturalComparer.Instance).Select(r => new DeviceChoice(r.GroupId, r.Name, r.Serial!)).ToList();
        if (rows.Select(r => r.Id + r.Serial).SequenceEqual(Devices.Select(d => d.Id + d.Serial))) return;
        var keep = Device?.Id;
        Devices.Clear();
        foreach (var r in rows) Devices.Add(r);
        Device = Devices.FirstOrDefault(d => d.Id == keep) ?? Devices.FirstOrDefault();
    }

    char MinLevel => Array.IndexOf(Levels.ToArray(), Level) switch { 1 => 'D', 2 => 'I', 3 => 'W', 4 => 'E', _ => 'V' };
    static int Rank(char c) => c switch { 'V' => 0, 'D' => 1, 'I' => 2, 'W' => 3, 'E' => 4, 'F' or 'A' => 5, _ => 2 };

    bool Matches(LogLine l)
    {
        if (CrashesOnly && !l.IsCrash) return false;
        if (Rank(l.Level) < Rank(MinLevel)) return false;
        if (_pid is { } pid && l.Pid != pid) return false;
        var q = Filter.Trim();
        return q.Length == 0 || l.Raw.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void Rebuild()
    {
        Lines.Clear();
        foreach (var l in _all.Where(Matches).TakeLast(MaxShown)) Lines.Add(new LogItem(l));
        OnPropertyChanged(nameof(IsEmpty));
        if (FollowNewest) ScrollToEnd?.Invoke();
    }

    async Task ResolveAppAsync()
    {
        var pkg = App.Trim();
        if (pkg.Length == 0 || Device == null || _s.Adb == null) { _pid = null; Rebuild(); return; }
        _pid = await LogcatSession.PidOfAsync(_s.Adb, Device.Serial, pkg);
        Status = _pid == null ? Loc.F("{0} isn’t running right now.", pkg) : Loc.F("Showing {0} (process {1}).", pkg, _pid);
        Rebuild();
    }

    void Flush()
    {
        if (_incoming.IsEmpty) return;
        var added = false;
        while (_incoming.TryDequeue(out var l))
        {
            _all.Add(l);
            if (l.IsCrash && l.Message.StartsWith("FATAL EXCEPTION")) Crashes++;
            if (Matches(l)) { Lines.Add(new LogItem(l)); added = true; }
        }
        if (_all.Count > MaxKept) _all.RemoveRange(0, _all.Count - MaxKept);
        while (Lines.Count > MaxShown) Lines.RemoveAt(0);
        if (added) { OnPropertyChanged(nameof(IsEmpty)); if (FollowNewest) ScrollToEnd?.Invoke(); }
    }

    [RelayCommand]
    async Task Toggle()
    {
        if (IsRunning) Stop();
        else await StartAsync();
    }

    async Task StartAsync()
    {
        if (Device == null) { Status = Loc.T("Choose a connected device first."); return; }
        IsRunning = true;
        if (_s.Demo)
        {
            foreach (var raw in DemoLines()) _incoming.Enqueue(LogcatSession.Parse(raw));
            Status = Loc.F("Showing the log of {0}.", Device.Name);
            return;
        }
        if (_s.Adb == null) return;
        _session = LogcatSession.Start(_s.Adb.AdbPath, Device.Serial);
        _session.Line += l => _incoming.Enqueue(l);
        _session.Ended += () => Dispatcher.UIThread.Post(() => { if (IsRunning) { IsRunning = false; Status = Loc.T("The log stopped (device disconnected?)."); } });
        Status = Loc.F("Showing the log of {0}.", Device.Name);
        await ResolveAppAsync();
    }

    void Stop()
    {
        _session?.Dispose();
        _session = null;
        IsRunning = false;
        Status = Loc.T("Stopped.");
    }

    [RelayCommand]
    void Clear()
    {
        _all.Clear(); Lines.Clear(); Crashes = 0;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    async Task Save()
    {
        if (SaveAsAsync == null || _all.Count == 0) return;
        var path = await SaveAsAsync($"{Device?.Name ?? "device"}-log-{DateTime.Now:yyyyMMdd-HHmmss}".Replace(' ', '-'), "txt");
        if (path == null) return;
        try
        {
            await File.WriteAllLinesAsync(path, _all.Where(Matches).Select(l => l.Raw));
            Status = Loc.F("Saved {0}", path);
            Reveal.File(path);
        }
        catch (Exception ex) { Status = Loc.F("Couldn’t save: {0}", ex.Message); }
    }

    static IEnumerable<string> DemoLines()
    {
        var t = DateTime.Now;
        string L(int s, char lv, string tag, string msg, int pid = 4120) => $"{t.AddSeconds(s):MM-dd HH:mm:ss.fff}  {pid}  {pid + 7} {lv} {tag,-14}: {msg}";
        yield return L(-40, 'I', "ActivityManager", "Start proc 4120:com.example.testapp/u0a312 for top-activity", 1520);
        yield return L(-39, 'D', "testapp", "MainActivity.onCreate()");
        yield return L(-38, 'I', "testapp", "Loaded 24 items from cache");
        yield return L(-30, 'W', "testapp", "Slow network response: 2.4 s for /api/items");
        yield return L(-20, 'D', "WifiService", "RSSI -58 dBm", 1520);
        yield return L(-12, 'E', "testapp", "Failed to parse item 17: missing field 'price'");
        yield return L(-11, 'E', "AndroidRuntime", "FATAL EXCEPTION: main");
        yield return L(-11, 'E', "AndroidRuntime", "Process: com.example.testapp, PID: 4120");
        yield return L(-11, 'E', "AndroidRuntime", "java.lang.NullPointerException: Attempt to invoke virtual method 'int java.lang.String.length()' on a null object reference");
        yield return L(-11, 'E', "AndroidRuntime", "    at com.example.testapp.ItemAdapter.bind(ItemAdapter.kt:42)");
        yield return L(-10, 'I', "ActivityManager", "Process com.example.testapp (pid 4120) has died", 1520);
    }

    public void Dispose()
    {
        _flush.Stop();
        _session?.Dispose();
    }
}
