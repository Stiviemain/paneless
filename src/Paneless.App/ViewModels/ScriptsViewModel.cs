using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed record CategoryHeader(string Title);
public sealed record ListHint(string Text);

public sealed partial class ScriptEntry(AdbScript script) : ObservableObject
{
    public AdbScript Script { get; } = script;
    public string Name => string.IsNullOrWhiteSpace(Script.Name) ? "Untitled script" : Script.Name;
    public bool Confirm => Script.Confirm;
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed partial class FieldInput(string name, string? defaultValue) : ObservableObject
{
    public string Name { get; } = name;
    public string Hint { get; } = defaultValue ?? "";
    public bool Required => defaultValue == null;
    [ObservableProperty] public partial string Value { get; set; } = "";
    public string Effective => Value.Length > 0 ? Value : Hint;
}

public sealed partial class TargetDevice(DeviceRow row, bool isChecked) : ObservableObject
{
    public DeviceRow Row { get; set; } = row;
    public string Name => Row.Name;
    [ObservableProperty] public partial bool IsChecked { get; set; } = isChecked;
    public Action? Changed { get; set; }
    partial void OnIsCheckedChanged(bool value) => Changed?.Invoke();
}

/// <summary>A device group in the "Run on" list. Ticking it ticks every member that's on the network.</summary>
public sealed partial class TargetGroup : ObservableObject
{
    public TargetGroup(string name, bool showHeader, IEnumerable<TargetDevice> members, int total)
    {
        Name = name;
        ShowHeader = showHeader;
        Total = total;
        Members = new(members);
        foreach (var m in Members) m.Changed = Update;
    }

    public string Name { get; }
    public bool ShowHeader { get; }
    /// <summary>Members including the ones not on the network.</summary>
    public int Total { get; }
    public ObservableCollection<TargetDevice> Members { get; }
    public bool HasMembers => Members.Count > 0;
    public Avalonia.Thickness MemberMargin => new(ShowHeader ? 26 : 4, 0, 0, 4);
    public Action? Changed { get; set; }

    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(MembersVisible));
    public bool MembersVisible => IsExpanded || !ShowHeader;

    public bool? IsChecked
    {
        get
        {
            var n = Members.Count(m => m.IsChecked);
            return n == 0 ? false : n == Members.Count ? true : null;
        }
        set
        {
            var on = value == true;
            foreach (var m in Members) { m.Changed = null; m.IsChecked = on; m.Changed = Update; }
            Update();
        }
    }

    public string CountText
    {
        get
        {
            if (Members.Count == 0) return Total == 0 ? "empty" : "none on the network";
            var n = Members.Count(m => m.IsChecked);
            return n > 0 ? $"{n} of {Members.Count} ticked"
                : Members.Count == Total ? Members.Count.ToString() : $"{Members.Count} of {Total} on the network";
        }
    }

    void Update()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(CountText));
        Changed?.Invoke();
    }
}

public sealed partial class ScriptsViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly ScriptStore _store = new();
    ScriptEntry? _current;
    bool _loading;
    object? _lastValid;

    /// <param name="tickKeys">Devices to tick at the start; null = the ones selected in the main window.</param>
    public ScriptsViewModel(AppServices services, MainViewModel main, IReadOnlyCollection<string>? tickKeys = null)
    {
        _s = services;
        _main = main;
        RebuildList();
        RebuildTargets((tickKeys ?? main.SelectedRows.Select(r => r.Key).ToList()).ToHashSet(), firstTime: true);
        main.DevicesChanged += OnDevicesChanged;
        SelectedEntry = Items.OfType<ScriptEntry>().FirstOrDefault();
    }

    /// <summary>Set by the window: asks the user to confirm; true = go ahead.</summary>
    public Func<string, string, string?, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<object> Items { get; } = new();
    public ObservableCollection<FieldInput> Fields { get; } = new();
    public ObservableCollection<TargetGroup> TargetGroups { get; } = new();
    IEnumerable<TargetDevice> AllTargets => TargetGroups.SelectMany(g => g.Members);

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial object? SelectedEntry { get; set; }

    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string EditDescription { get; set; } = "";
    [ObservableProperty] public partial string EditCommands { get; set; } = "";
    [ObservableProperty] public partial bool EditConfirm { get; set; }
    [ObservableProperty] public partial string Category { get; set; } = "";
    [ObservableProperty] public partial string? Warning { get; set; }
    [ObservableProperty] public partial bool IsMine { get; set; }
    [ObservableProperty] public partial bool HasScript { get; set; }

    [ObservableProperty] public partial string Output { get; set; } = "";
    [ObservableProperty] public partial string RunStatus { get; set; } = "";
    [ObservableProperty] public partial IBrush RunStatusBrush { get; set; } = Theme.Dim;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsRunning { get; set; }

    public bool IsBuiltIn => HasScript && !IsMine;
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool HasFields => Fields.Count > 0;
    public bool NoTargets => !AllTargets.Any();
    public string RunLabel => AllTargets.Count(t => t.IsChecked) is var n and > 0 ? $"Run on {n} device{(n == 1 ? "" : "s")}" : "Run";
    public string OutputFolder => ScriptRunner.OutputDir;

    partial void OnIsMineChanged(bool value) => OnPropertyChanged(nameof(IsBuiltIn));
    partial void OnHasScriptChanged(bool value) => OnPropertyChanged(nameof(IsBuiltIn));
    partial void OnWarningChanged(string? value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnSearchChanged(string value) => RebuildList();

    // ---------------------------------------------------------------- list

    void RebuildList()
    {
        var q = Search.Trim();
        bool Match(AdbScript s) => q.Length == 0
            || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || s.Commands.Contains(q, StringComparison.OrdinalIgnoreCase);

        var keep = _current;
        Items.Clear();
        var mine = _store.Mine.Where(Match).ToList();
        Items.Add(new CategoryHeader("My scripts"));
        if (_store.Mine.Count == 0) Items.Add(new ListHint("None yet. Click + New, or Copy to My scripts on any built-in."));
        foreach (var s in mine) Items.Add(keep?.Script == s ? keep : new ScriptEntry(s));
        foreach (var group in BuiltInScripts.All.Where(Match).GroupBy(s => s.Category))
        {
            Items.Add(new CategoryHeader(group.Key));
            foreach (var s in group) Items.Add(keep?.Script == s ? keep : new ScriptEntry(s));
        }
        if (keep != null && Items.Contains(keep)) SelectedEntry = keep;
    }

    partial void OnSelectedEntryChanged(object? value)
    {
        // Category titles aren't selectable: stay on the previous script.
        if (value is CategoryHeader or ListHint) { SelectedEntry = _lastValid; return; }
        _lastValid = value;
        Load(value as ScriptEntry);
    }

    void Load(ScriptEntry? entry)
    {
        _loading = true;
        _current = entry;
        var s = entry?.Script;
        HasScript = s != null;
        IsMine = s is { BuiltIn: false };
        EditName = s?.Name ?? "";
        EditDescription = s?.Description ?? "";
        EditCommands = s?.Commands ?? "";
        EditConfirm = s?.Confirm ?? false;
        Category = s?.Category ?? "";
        Warning = s?.Warning;
        _loading = false;
        RebuildFields();
    }

    // Edits to your own scripts are saved as you type.
    partial void OnEditNameChanged(string value) => SaveEdit(s => s.Name = value);
    partial void OnEditDescriptionChanged(string value) => SaveEdit(s => s.Description = value);
    partial void OnEditConfirmChanged(bool value) => SaveEdit(s => s.Confirm = value);
    partial void OnEditCommandsChanged(string value) { SaveEdit(s => s.Commands = value); if (!_loading) RebuildFields(); }

    void SaveEdit(Action<AdbScript> apply)
    {
        if (_loading || _current is not { Script.BuiltIn: false } entry) return;
        apply(entry.Script);
        entry.Refresh();
        try { _store.Save(); } catch (Exception ex) { SetRunStatus("Couldn't save: " + ex.Message, StatusKind.Error); }
    }

    void RebuildFields()
    {
        var old = Fields.ToDictionary(f => f.Name, f => f.Value);
        Fields.Clear();
        foreach (var p in ScriptText.Placeholders(EditCommands))
            Fields.Add(new FieldInput(p.Name, p.Default) { Value = old.GetValueOrDefault(p.Name, "") });
        OnPropertyChanged(nameof(HasFields));
    }

    [RelayCommand]
    void NewScript()
    {
        var s = new AdbScript { Name = "New script", Commands = "adb shell " };
        _store.Mine.Insert(0, s);
        _store.Save();
        Search = "";
        RebuildList();
        SelectedEntry = Items.OfType<ScriptEntry>().First(e => e.Script == s);
    }

    [RelayCommand]
    void CopyToMine()
    {
        if (_current == null) return;
        var src = _current.Script;
        var s = new AdbScript { Name = src.Name + " (copy)", Description = src.Description, Commands = src.Commands, Confirm = src.Confirm };
        _store.Mine.Insert(0, s);
        _store.Save();
        Search = "";
        RebuildList();
        SelectedEntry = Items.OfType<ScriptEntry>().First(e => e.Script == s);
    }

    [RelayCommand]
    async Task DeleteScript()
    {
        if (_current is not { Script.BuiltIn: false } entry) return;
        if (ConfirmAsync != null && !await ConfirmAsync("Delete script", $"Delete \"{entry.Name}\"?", null)) return;
        _store.Mine.Remove(entry.Script);
        _store.Save();
        _current = null;
        RebuildList();
        SelectedEntry = Items.OfType<ScriptEntry>().FirstOrDefault();
    }

    // ---------------------------------------------------------------- devices

    string _targetsShape = "";

    /// <summary>The device list refreshes every few seconds; the checkboxes are only rebuilt when
    /// devices come or go or the groups change, so ticks and scrolling stay put.</summary>
    void OnDevicesChanged()
    {
        if (Shape() == _targetsShape)
        {
            // Same devices: just keep their details (state, serial) current for running.
            var rows = _main.NetworkRows.ToDictionary(r => r.Key);
            foreach (var t in AllTargets) if (rows.TryGetValue(t.Row.Key, out var r)) t.Row = r;
            return;
        }
        RebuildTargets(AllTargets.Where(t => t.IsChecked).Select(t => t.Row.Key).ToHashSet(), firstTime: false);
    }

    string Shape() =>
        string.Join('|', _s.Groups.Groups.Select(g => g.Name + ":" + string.Join(',', g.Members)))
        + "#" + string.Join(',', _main.NetworkRows.Where(r => r.CanMirror).Select(r => r.Key + "=" + r.Name).Order());

    void RebuildTargets(ISet<string> checkedKeys, bool firstTime)
    {
        var expanded = TargetGroups.ToDictionary(g => g.Name, g => g.IsExpanded);
        var rows = _main.NetworkRows.Where(r => r.CanMirror).ToList();
        var groups = _s.Groups;
        var single = firstTime && rows.Count == 1;
        TargetDevice Make(DeviceRow r) => new(r, checkedKeys.Contains(r.Key) || single);
        IEnumerable<TargetDevice> Sorted(IEnumerable<DeviceRow> rs) => rs.OrderBy(r => r.Name, NaturalComparer.Instance).Select(Make);

        TargetGroups.Clear();
        foreach (var g in groups.Groups)
        {
            var tg = new TargetGroup(g.Name, true, Sorted(rows.Where(r => g.Members.Contains(r.GroupId))), g.Members.Count);
            // Start like the main window, but open a collapsed group when something in it is ticked.
            tg.IsExpanded = !g.Collapsed || tg.Members.Any(m => m.IsChecked);
            TargetGroups.Add(tg);
        }
        var others = rows.Where(r => groups.GroupOf(r.GroupId) == null).ToList();
        if (others.Count > 0 || !groups.Any)
            TargetGroups.Add(new TargetGroup(groups.Any ? "Other devices" : "Devices", groups.Any, Sorted(others), others.Count));
        foreach (var tg in TargetGroups)
        {
            if (expanded.TryGetValue(tg.Name, out var e)) tg.IsExpanded = e;
            tg.Changed = () => OnPropertyChanged(nameof(RunLabel));
        }
        _targetsShape = Shape();
        OnPropertyChanged(nameof(NoTargets));
        OnPropertyChanged(nameof(RunLabel));
    }

    public void Detach() => _main.DevicesChanged -= OnDevicesChanged;

    /// <summary>Ticks exactly these devices (e.g. when opened from a device's toolbar or a group's menu).</summary>
    public void SelectTargets(IReadOnlyCollection<string> keys)
    {
        foreach (var t in AllTargets) t.IsChecked = keys.Contains(t.Row.Key);
    }

    [RelayCommand]
    void TickAll() { foreach (var g in TargetGroups) g.IsChecked = true; }

    [RelayCommand]
    void TickNone() { foreach (var g in TargetGroups) g.IsChecked = false; }

    // ---------------------------------------------------------------- run

    bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task RunAsync()
    {
        if (_current == null) return;
        var script = _current.Script;
        var lines = ScriptText.Lines(script.Commands);
        if (lines.Count == 0) { SetRunStatus("This script has no commands yet.", StatusKind.Warn); return; }

        var missing = Fields.Where(f => f.Required && f.Value.Trim().Length == 0).Select(f => f.Name).ToList();
        if (missing.Count > 0) { SetRunStatus("Fill in: " + string.Join(", ", missing), StatusKind.Warn); return; }
        var values = Fields.ToDictionary(f => f.Name, f => f.Effective.Trim());

        foreach (var line in lines)
        {
            var (_, error) = ScriptText.ToAdbArgs(line, values);
            if (error != null) { SetRunStatus($"\"{line}\": {error}", StatusKind.Error); return; }
        }

        var targets = AllTargets.Where(t => t.IsChecked).ToList();
        if (targets.Count == 0) { SetRunStatus("Tick at least one device.", StatusKind.Warn); return; }
        if (_s.Adb == null || _s.Directory == null) { SetRunStatus("Demo mode: scripts don't run.", StatusKind.Warn); return; }

        if (script.Confirm && ConfirmAsync != null)
        {
            var who = string.Join(", ", targets.Select(t => t.Name));
            if (!await ConfirmAsync("Run script", $"Run \"{script.Name}\" on {who}?", script.Warning)) return;
        }

        IsRunning = true;
        Output = "";
        SetRunStatus($"Running on {targets.Count} device{(targets.Count == 1 ? "" : "s")}…");
        var runner = new ScriptRunner(_s.Adb);
        var ok = 0;
        try
        {
            await Task.WhenAll(targets.Select(async t =>
            {
                var serial = await _s.Directory.ConnectAsync(t.Row);
                if (serial == null) { Append($"━━ {t.Name}   ✗ couldn't connect\n\n"); return; }
                var res = await runner.RunAsync(serial, t.Name, script, values);
                if (res.Ok) Interlocked.Increment(ref ok);
                Append(Format(res));
            }));
            SetRunStatus($"Done: worked on {ok} of {targets.Count} device{(targets.Count == 1 ? "" : "s")}.",
                ok == targets.Count ? StatusKind.Ok : StatusKind.Warn);
        }
        catch (Exception ex) { SetRunStatus("Stopped: " + ex.Message, StatusKind.Error); }
        finally { IsRunning = false; }
    }

    void Append(string text) => Output += text;

    static string Format(DeviceRunResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"━━ {r.Device}   {(r.Ok ? "✓" : "✗")}  {r.Elapsed.TotalSeconds:0.0} s\n");
        foreach (var st in r.Steps)
        {
            sb.Append("$ ").Append(st.Command).Append('\n');
            if (st.Result.Output.Length > 0) sb.Append(st.Result.Output.Replace("\r", "")).Append('\n');
            if (st.Error != null) sb.Append("! ").Append(st.Error).Append('\n');
            else if (!st.Result.Ok) sb.Append($"! exit code {st.Result.ExitCode}\n");
        }
        return sb.Append('\n').ToString();
    }

    void SetRunStatus(string text, StatusKind kind = StatusKind.Info) { RunStatus = text; RunStatusBrush = Theme.For(kind); }

    [RelayCommand]
    void OpenOutputFolder()
    {
        Directory.CreateDirectory(OutputFolder);
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputFolder}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", OutputFolder);
            else Process.Start("xdg-open", OutputFolder);
        }
        catch (Exception ex) { SetRunStatus("Couldn't open the folder: " + ex.Message, StatusKind.Error); }
    }
}
