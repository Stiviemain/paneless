using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>A script or recipe offered in a drop-down.</summary>
public sealed record NamedChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A group that can be ticked as a target.</summary>
public sealed partial class GroupPick(string name, bool isChecked, Action changed) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] public partial bool IsChecked { get; set; } = isChecked;
    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>The Automation page: recipes, scheduled tasks and alerts.</summary>
public sealed class AutomationViewModel(AppServices s, MainViewModel main, AutomationService service)
{
    public RecipesViewModel Recipes { get; } = new(s, main);
    public SchedulesViewModel Schedules { get; } = new(s, main, service);
    public AlertsViewModel Alerts { get; } = new(s);
}

// ===================================================================== recipes

public sealed partial class StepItem : ObservableObject
{
    readonly Action _save;
    public StepItem(RecipeStep step, IReadOnlyList<NamedChoice> scripts, Action save)
    {
        Step = step; Scripts = scripts; _save = save;
        _script = scripts.FirstOrDefault(c => c.Id == step.ScriptId);
    }

    public RecipeStep Step { get; }
    public IReadOnlyList<NamedChoice> Scripts { get; }
    public string Title => Step.Kind switch
    {
        RecipeStepKind.InstallApk => Loc.T("Install an app"),
        RecipeStepKind.CopyFile => Loc.T("Copy a file"),
        RecipeStepKind.RunScript => Loc.T("Run a script"),
        RecipeStepKind.Shell => Loc.T("Command"),
        _ => Loc.T("Restart the device"),
    };
    public bool HasPath => Step.Kind is RecipeStepKind.InstallApk or RecipeStepKind.CopyFile;
    public bool HasTarget => Step.Kind == RecipeStepKind.CopyFile;
    public bool HasScript => Step.Kind == RecipeStepKind.RunScript;
    public bool HasCommand => Step.Kind == RecipeStepKind.Shell;
    public bool FileMissing => HasPath && Step.Path.Length > 0 && !File.Exists(Step.Path);

    public string Path { get => Step.Path; set { Step.Path = value; _save(); OnPropertyChanged(); OnPropertyChanged(nameof(FileMissing)); } }
    public string Target { get => Step.Target; set { Step.Target = value; _save(); OnPropertyChanged(); } }
    public string Command { get => Step.Command; set { Step.Command = value; _save(); OnPropertyChanged(); } }

    NamedChoice? _script;
    public NamedChoice? Script
    {
        get => _script;
        set { _script = value; Step.ScriptId = value?.Id ?? ""; _save(); OnPropertyChanged(); }
    }
}

public sealed partial class RecipeItem(Recipe r) : ObservableObject
{
    public Recipe Recipe { get; } = r;
    public string Name => string.IsNullOrWhiteSpace(Recipe.Name) ? Loc.T("Untitled recipe") : Recipe.Name;
    public string Summary => Loc.Count(Recipe.Steps.Count, "{0} step", "{0} steps");
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed partial class RecipesViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    bool _loading;

    public RecipesViewModel(AppServices s, MainViewModel main)
    {
        _s = s; _main = main;
        foreach (var r in s.Recipes.All) Items.Add(new RecipeItem(r));
        Selected = Items.FirstOrDefault();
        RebuildTargets();
        s.Groups.Changed += RebuildTargets;
    }

    /// <summary>Set by the view.</summary>
    public Func<string, string[], Task<string?>>? PickFileAsync { get; set; }
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<RecipeItem> Items { get; } = new();
    public ObservableCollection<StepItem> Steps { get; } = new();
    public ObservableCollection<string> Targets { get; } = new();
    public ObservableCollection<string> Output { get; } = new();

    [ObservableProperty] public partial RecipeItem? Selected { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial bool ContinueOnError { get; set; }
    [ObservableProperty] public partial string? Target { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    public bool HasRecipe => Selected != null;
    public bool NoRecipes => Items.Count == 0;

    string SelectedDevicesChoice => Loc.T("The devices selected on the Devices page");

    void RebuildTargets()
    {
        var keep = Target;
        Targets.Clear();
        Targets.Add(SelectedDevicesChoice);
        foreach (var g in _s.Groups.Groups) Targets.Add(Loc.F("Group: {0}", g.Name));
        Target = Targets.Contains(keep ?? "") ? keep : Targets.FirstOrDefault();
    }

    IReadOnlyList<NamedChoice> ScriptChoices() =>
        _s.AllScripts().Where(sc => ScriptText.Lines(sc.Commands).Count > 0).Select(sc => new NamedChoice(sc.Id, sc.Name)).ToList();

    partial void OnSelectedChanged(RecipeItem? value)
    {
        _loading = true;
        Name = value?.Recipe.Name ?? "";
        Description = value?.Recipe.Description ?? "";
        ContinueOnError = value?.Recipe.ContinueOnError ?? false;
        _loading = false;
        Steps.Clear();
        if (value != null)
        {
            var scripts = ScriptChoices();
            foreach (var st in value.Recipe.Steps) Steps.Add(new StepItem(st, scripts, Save));
        }
        OnPropertyChanged(nameof(HasRecipe));
    }

    partial void OnNameChanged(string value) { if (!_loading && Selected != null) { Selected.Recipe.Name = value; Save(); } }
    partial void OnDescriptionChanged(string value) { if (!_loading && Selected != null) { Selected.Recipe.Description = value; Save(); } }
    partial void OnContinueOnErrorChanged(bool value) { if (!_loading && Selected != null) { Selected.Recipe.ContinueOnError = value; Save(); } }

    void Save()
    {
        _s.Recipes.Save();
        Selected?.Refresh();
    }

    [RelayCommand]
    void New()
    {
        var r = new Recipe { Name = Loc.T("New recipe") };
        _s.Recipes.All.Add(r);
        _s.Recipes.Save();
        var item = new RecipeItem(r);
        Items.Add(item);
        Selected = item;
        OnPropertyChanged(nameof(NoRecipes));
    }

    [RelayCommand]
    void Duplicate()
    {
        if (Selected == null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(Selected.Recipe);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Recipe>(json)!;
        copy.Id = Guid.NewGuid().ToString("n");
        copy.Name = Loc.F("{0} (copy)", Selected.Recipe.Name);
        _s.Recipes.All.Add(copy);
        _s.Recipes.Save();
        var item = new RecipeItem(copy);
        Items.Add(item);
        Selected = item;
    }

    [RelayCommand]
    async Task Delete()
    {
        if (Selected is not { } item) return;
        if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Delete recipe"), Loc.F("Delete the recipe “{0}”? Scheduled tasks that use it stop working.", item.Name))) return;
        _s.Recipes.All.Remove(item.Recipe);
        _s.Recipes.Save();
        Items.Remove(item);
        Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(NoRecipes));
    }

    void AddStep(RecipeStep step)
    {
        if (Selected == null) return;
        Selected.Recipe.Steps.Add(step);
        Steps.Add(new StepItem(step, ScriptChoices(), Save));
        Save();
    }

    [RelayCommand]
    async Task AddApk()
    {
        var path = PickFileAsync == null ? null : await PickFileAsync(Loc.T("App to install (APK)"), ["*.apk"]);
        if (path != null) AddStep(new RecipeStep { Kind = RecipeStepKind.InstallApk, Path = path });
    }

    [RelayCommand]
    async Task AddFile()
    {
        var path = PickFileAsync == null ? null : await PickFileAsync(Loc.T("File to copy to the devices"), ["*"]);
        if (path != null) AddStep(new RecipeStep { Kind = RecipeStepKind.CopyFile, Path = path });
    }

    [RelayCommand] void AddScript() => AddStep(new RecipeStep { Kind = RecipeStepKind.RunScript });
    [RelayCommand] void AddCommand() => AddStep(new RecipeStep { Kind = RecipeStepKind.Shell, Command = "settings put " });
    [RelayCommand] void AddReboot() => AddStep(new RecipeStep { Kind = RecipeStepKind.Reboot });

    [RelayCommand]
    async Task Browse(StepItem step)
    {
        var path = PickFileAsync == null ? null : await PickFileAsync(step.Title, step.Step.Kind == RecipeStepKind.InstallApk ? ["*.apk"] : ["*"]);
        if (path != null) step.Path = path;
    }

    [RelayCommand]
    void RemoveStep(StepItem step)
    {
        Selected?.Recipe.Steps.Remove(step.Step);
        Steps.Remove(step);
        Save();
    }

    [RelayCommand]
    void MoveUp(StepItem step) => Move(step, -1);

    [RelayCommand]
    void MoveDown(StepItem step) => Move(step, 1);

    void Move(StepItem step, int delta)
    {
        if (Selected == null) return;
        var list = Selected.Recipe.Steps;
        var i = list.IndexOf(step.Step);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.RemoveAt(i); list.Insert(j, step.Step);
        Steps.Move(i, j);
        Save();
    }

    IReadOnlyList<DeviceRow> TargetRows() =>
        Target == SelectedDevicesChoice
            ? _main.SelectedRows.Where(r => r.CanMirror).ToList()
            : _main.Actions.Resolve([_s.Groups.Groups.FirstOrDefault(g => Loc.F("Group: {0}", g.Name) == Target)?.Name ?? ""], []);

    [RelayCommand]
    async Task Run()
    {
        if (Selected is not { } item || IsRunning) return;
        if (item.Recipe.Steps.Count == 0) { Status = Loc.T("Add some steps first."); return; }
        var rows = TargetRows();
        if (rows.Count == 0) { Status = Loc.T("None of those devices is on the network (or none is selected)."); return; }
        if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Run recipe"),
                Loc.F("Run “{0}” ({1}) on {2}: {3}?", item.Name, item.Summary, Loc.Count(rows.Count, "{0} device", "{0} devices"), string.Join(", ", rows.Take(6).Select(r => r.Name)) + (rows.Count > 6 ? "…" : ""))))
            return;
        IsRunning = true;
        Output.Clear();
        Status = Loc.T("Running…");
        try
        {
            var progress = new Progress<(string Device, StepOutcome Step)>(p =>
                Output.Add($"{(p.Step.Ok ? "✓" : "✗")} {p.Device}: {p.Step.What}{(p.Step.Ok || p.Step.Detail.Length == 0 ? "" : " – " + p.Step.Detail)}"));
            var results = await _main.Actions.RunRecipeAsync(item.Recipe, rows, progress);
            Status = DeviceActions.Summary(results);
        }
        finally { IsRunning = false; }
    }
}

// ===================================================================== scheduled tasks

public sealed partial class ScheduleItem(ScheduledTask t) : ObservableObject
{
    public ScheduledTask Task { get; } = t;
    public string Name => string.IsNullOrWhiteSpace(Task.Name) ? Loc.T("Untitled task") : Task.Name;
    public string When => Task.Repeat switch
    {
        ScheduleRepeat.Daily => Loc.F("Every day at {0}", Task.Time),
        ScheduleRepeat.Weekly => Loc.F("{0} at {1}", string.Join(", ", Task.Days.OrderBy(d => ((int)d + 6) % 7).Select(d => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(d))), Task.Time),
        _ => Loc.F("Every {0} hours", Task.Hours),
    };
    public string Next => Task.Enabled && Task.Next(DateTime.Now) is { } n ? Loc.F("next {0:g}", n) : Loc.T("off");
    public string Last => Task.LastRun is { } l ? Loc.F("last {0:g}: {1}", l, Task.LastResult) : Loc.T("hasn’t run yet");
    public IBrush NameBrush => Task.Enabled ? Theme.Text : Theme.Dim;
    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed partial class SchedulesViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly AutomationService _service;
    bool _loading;

    public SchedulesViewModel(AppServices s, MainViewModel main, AutomationService service)
    {
        _s = s; _main = main; _service = service;
        foreach (var t in s.Schedules.All) Items.Add(new ScheduleItem(t));
        Selected = Items.FirstOrDefault();
        service.TaskRan += (t, _) => Items.FirstOrDefault(i => i.Task == t)?.Refresh();
    }

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public ObservableCollection<ScheduleItem> Items { get; } = new();
    public ObservableCollection<GroupPick> Groups { get; } = new();
    public ObservableCollection<NamedChoice> TargetChoices { get; } = new();
    public IReadOnlyList<string> ActionChoices { get; } = [Loc.T("Run a script"), Loc.T("Run a recipe"), Loc.T("Restart the devices"), Loc.T("Run a command")];
    public IReadOnlyList<string> RepeatChoices { get; } = [Loc.T("Every day"), Loc.T("Every week"), Loc.T("Every few hours")];
    public IReadOnlyList<DayPick> Days { get; private set; } = [];

    [ObservableProperty] public partial ScheduleItem? Selected { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string Action { get; set; } = "";
    [ObservableProperty] public partial NamedChoice? TargetChoice { get; set; }
    [ObservableProperty] public partial string Command { get; set; } = "";
    [ObservableProperty] public partial string Repeat { get; set; } = "";
    [ObservableProperty] public partial string Time { get; set; } = "03:00";
    [ObservableProperty] public partial decimal Hours { get; set; } = 6;
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsRunning { get; set; }

    public bool HasTask => Selected != null;
    public bool NoTasks => Items.Count == 0;
    public bool ShowTargetChoice => Action == ActionChoices[0] || Action == ActionChoices[1];
    public bool ShowCommand => Action == ActionChoices[3];
    public bool ShowTime => Repeat != RepeatChoices[2];
    public bool ShowDays => Repeat == RepeatChoices[1];
    public bool ShowHours => Repeat == RepeatChoices[2];
    public string Hint => Loc.T("Tasks run while Paneless is open, also when it’s only in the notification area (Settings › Start-up). A run that was missed while it was closed is skipped.");

    partial void OnSelectedChanged(ScheduleItem? value)
    {
        _loading = true;
        var t = value?.Task;
        Name = t?.Name ?? "";
        Enabled = t?.Enabled ?? false;
        Action = ActionChoices[(int)(t?.Action ?? ScheduleAction.RunScript)];
        Repeat = RepeatChoices[(int)(t?.Repeat ?? ScheduleRepeat.Daily)];
        Time = t?.Time ?? "03:00";
        Hours = t?.Hours ?? 6;
        Command = t?.Action == ScheduleAction.Shell ? t.Target : "";
        FillTargetChoices();
        TargetChoice = TargetChoices.FirstOrDefault(c => c.Id == t?.Target);
        Groups.Clear();
        foreach (var g in _s.Groups.Groups) Groups.Add(new GroupPick(g.Name, t?.Groups.Contains(g.Name) == true, SaveGroups));
        Days = Enum.GetValues<DayOfWeek>().OrderBy(d => ((int)d + 6) % 7)
            .Select(d => new DayPick(d, t?.Days.Contains(d) == true, SaveDays)).ToList();
        OnPropertyChanged(nameof(Days));
        _loading = false;
        OnPropertyChanged(nameof(HasTask));
        ShowFlags();
    }

    void ShowFlags()
    {
        foreach (var p in new[] { nameof(ShowTargetChoice), nameof(ShowCommand), nameof(ShowTime), nameof(ShowDays), nameof(ShowHours) }) OnPropertyChanged(p);
    }

    void FillTargetChoices()
    {
        TargetChoices.Clear();
        if (Action == ActionChoices[1]) foreach (var r in _s.Recipes.All) TargetChoices.Add(new NamedChoice(r.Id, r.Name));
        else foreach (var sc in _s.AllScripts().Where(sc => ScriptText.Placeholders(sc.Commands).All(p => p.Default != null)))
                TargetChoices.Add(new NamedChoice(sc.Id, sc.Name));
    }

    void Edit(Action<ScheduledTask> change)
    {
        if (_loading || Selected == null) return;
        change(Selected.Task);
        _s.Schedules.Save();
        Selected.Refresh();
    }

    partial void OnNameChanged(string value) => Edit(t => t.Name = value);
    partial void OnEnabledChanged(bool value) => Edit(t => t.Enabled = value);
    partial void OnActionChanged(string value)
    {
        if (!_loading) { FillTargetChoices(); TargetChoice = TargetChoices.FirstOrDefault(); }
        Edit(t => { t.Action = (ScheduleAction)Math.Max(0, ActionChoices.ToList().IndexOf(value)); if (t.Action == ScheduleAction.Reboot) t.Target = ""; });
        ShowFlags();
    }
    partial void OnTargetChoiceChanged(NamedChoice? value) { if (value != null) Edit(t => t.Target = value.Id); }
    partial void OnCommandChanged(string value) { if (Action == ActionChoices[3]) Edit(t => t.Target = value); }
    partial void OnRepeatChanged(string value) { Edit(t => t.Repeat = (ScheduleRepeat)Math.Max(0, RepeatChoices.ToList().IndexOf(value))); ShowFlags(); }
    partial void OnTimeChanged(string value) { if (TimeSpan.TryParse(value, out var ts) && ts < TimeSpan.FromDays(1)) Edit(t => t.Time = ts.ToString(@"hh\:mm")); }
    partial void OnHoursChanged(decimal value) => Edit(t => t.Hours = (int)Math.Clamp(value, 1, 168));
    void SaveGroups() => Edit(t => t.Groups = Groups.Where(g => g.IsChecked).Select(g => g.Name).ToList());
    void SaveDays() => Edit(t => t.Days = Days.Where(d => d.IsChecked).Select(d => d.Day).ToList());

    [RelayCommand]
    void New()
    {
        var t = new ScheduledTask { Name = Loc.T("New task"), Enabled = false };
        _s.Schedules.All.Add(t);
        _s.Schedules.Save();
        var item = new ScheduleItem(t);
        Items.Add(item);
        Selected = item;
        OnPropertyChanged(nameof(NoTasks));
    }

    [RelayCommand]
    async Task Delete()
    {
        if (Selected is not { } item) return;
        if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Delete task"), Loc.F("Delete the scheduled task “{0}”?", item.Name))) return;
        _s.Schedules.All.Remove(item.Task);
        _s.Schedules.Save();
        Items.Remove(item);
        Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(NoTasks));
    }

    [RelayCommand]
    async Task RunNow()
    {
        if (Selected is not { } item || IsRunning) return;
        IsRunning = true;
        Status = Loc.T("Running…");
        try { Status = await _service.RunAsync(item.Task); item.Refresh(); }
        finally { IsRunning = false; }
    }
}

public sealed partial class DayPick(DayOfWeek day, bool isChecked, Action changed) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    public string Name { get; } = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day);
    [ObservableProperty] public partial bool IsChecked { get; set; } = isChecked;
    partial void OnIsCheckedChanged(bool value) => changed();
}

// ===================================================================== alerts

public sealed partial class RuleItem : ObservableObject
{
    readonly Action _save;
    public RuleItem(AlertRule rule, IReadOnlyList<string> scopes, Action save)
    {
        Rule = rule; Scopes = scopes; _save = save;
        _scope = rule.Groups.FirstOrDefault() ?? scopes[0];
    }

    public AlertRule Rule { get; }
    public IReadOnlyList<string> Scopes { get; }
    public string Title => Rule.Kind switch
    {
        AlertKind.LowBattery => Loc.T("Battery low"),
        AlertKind.LowStorage => Loc.T("Storage almost full"),
        AlertKind.Offline => Loc.T("Device gone from the network"),
        AlertKind.Crash => Loc.T("An app crashed"),
        _ => Loc.T("Device too hot"),
    };
    public string ThresholdLabel => Rule.Kind switch
    {
        AlertKind.LowBattery => Loc.T("at or below (%)"),
        AlertKind.LowStorage => Loc.T("free space at or below (%)"),
        AlertKind.Offline => Loc.T("for at least (minutes)"),
        AlertKind.Hot => Loc.T("battery at or above (°C)"),
        _ => "",
    };
    public bool HasThreshold => Rule.Kind != AlertKind.Crash;
    public bool Enabled { get => Rule.Enabled; set { Rule.Enabled = value; _save(); OnPropertyChanged(); } }
    public bool Notify { get => Rule.Notify; set { Rule.Notify = value; _save(); OnPropertyChanged(); } }
    public decimal Threshold { get => Rule.Threshold; set { Rule.Threshold = (int)value; _save(); OnPropertyChanged(); } }

    string _scope;
    public string Scope
    {
        get => _scope;
        set { _scope = value; Rule.Groups = value == Scopes[0] ? new() : [value]; _save(); OnPropertyChanged(); }
    }
}

public sealed class EventItem(AlertEvent e)
{
    public string Time => e.Time.Date == DateTime.Today ? e.Time.ToString("t") : e.Time.ToString("g");
    public string Text => e.Text;
    public string Icon => e.Kind switch { AlertKind.LowBattery => "🔋", AlertKind.LowStorage => "💾", AlertKind.Offline => "📴", AlertKind.Crash => "💥", _ => "🌡" };
}

public sealed partial class AlertsViewModel : ObservableObject
{
    readonly AppServices _s;

    public AlertsViewModel(AppServices s)
    {
        _s = s;
        IReadOnlyList<string> scopes = [Loc.T("All devices"), .. s.Groups.Groups.Select(g => g.Name)];
        foreach (var r in s.Alerts.Data.Rules.OrderBy(r => (int)r.Kind)) Rules.Add(new RuleItem(r, scopes, s.Alerts.Save));
        LoadEvents();
        s.Alerts.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(LoadEvents);
    }

    public ObservableCollection<RuleItem> Rules { get; } = new();
    public ObservableCollection<EventItem> Events { get; } = new();
    public bool NoEvents => Events.Count == 0;
    public string Hint => Loc.T("Paneless checks every minute while it’s open (also in the notification area) and tells you once when something starts.");

    void LoadEvents()
    {
        Events.Clear();
        foreach (var e in _s.Alerts.Data.Events.Take(200)) Events.Add(new EventItem(e));
        OnPropertyChanged(nameof(NoEvents));
    }

    [RelayCommand]
    void ClearEvents()
    {
        _s.Alerts.Data.Events.Clear();
        _s.Alerts.Save();
    }

    [RelayCommand]
    void TestNotification() => Platform.Toast.Show(Loc.T("Paneless alert"), Loc.T("This is how alerts look. You can turn notifications off per alert."));
}
