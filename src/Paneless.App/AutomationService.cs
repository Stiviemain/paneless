using System.Text.RegularExpressions;
using Avalonia.Threading;
using Paneless.App.Localization;
using Paneless.App.ViewModels;
using Paneless.Core;

namespace Paneless.App;

/// <summary>Runs in the background while Paneless is open (also in the notification area):
/// starts scheduled tasks when they're due and watches the devices for alerts.</summary>
public sealed partial class AutomationService
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly DispatcherTimer _timer;
    readonly AlertWatcher _watcher = new();
    readonly Dictionary<string, DateTime> _inventoryAt = new();
    readonly Dictionary<string, DateTime> _crashCheckedAt = new();
    DateTime _lastScheduleCheck = DateTime.Now;
    DateTime _lastAlertCheck = DateTime.MinValue;
    bool _busy;

    public AutomationService(AppServices s, MainViewModel main)
    {
        _s = s; _main = main;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>Raised (UI thread) when a scheduled task finished: task, summary.</summary>
    public event Action<ScheduledTask, string>? TaskRan;

    public void Start() { if (!_s.Demo) _timer.Start(); }

    async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var now = DateTime.Now;
            foreach (var task in _s.Schedules.All.Where(t => t.IsDue(_lastScheduleCheck, now)).ToList())
                await RunAsync(task);
            _lastScheduleCheck = now;
            if (now - _lastAlertCheck >= TimeSpan.FromSeconds(60))
            {
                _lastAlertCheck = now;
                await CheckAlertsAsync(now);
            }
        }
        catch { /* never let automation take Paneless down */ }
        finally { _busy = false; }
    }

    // ---------------------------------------------------------------- scheduled tasks

    /// <summary>Runs a task now (also "Run now" on the Automation page).</summary>
    public async Task<string> RunAsync(ScheduledTask task)
    {
        var rows = _main.Actions.Resolve(task.Groups, task.Devices);
        IReadOnlyList<DeviceActions.DeviceResult> results;
        switch (task.Action)
        {
            case ScheduleAction.RunScript when _s.FindScript(task.Target) is { } script:
                results = await _main.Actions.RunScriptAsync(script, rows); break;
            case ScheduleAction.RunRecipe when _s.Recipes.Get(task.Target) is { } recipe:
                results = await _main.Actions.RunRecipeAsync(recipe, rows); break;
            case ScheduleAction.Reboot:
                results = await _main.Actions.RebootAsync(rows); break;
            case ScheduleAction.Shell when task.Target.Trim().Length > 0:
                results = await _main.Actions.ShellAsync(task.Target, rows); break;
            default:
                task.LastRun = DateTime.Now;
                task.LastResult = Loc.T("Nothing to run: the script or recipe doesn’t exist any more.");
                _s.Schedules.Save();
                return task.LastResult;
        }
        task.LastRun = DateTime.Now;
        task.LastResult = DeviceActions.Summary(results);
        _s.Schedules.Save();
        _main.Report(Loc.F("Scheduled task “{0}”: {1}", task.Name, task.LastResult), results.All(r => r.Ok) && results.Count > 0 ? StatusKind.Ok : StatusKind.Warn);
        TaskRan?.Invoke(task, task.LastResult);
        return task.LastResult;
    }

    // ---------------------------------------------------------------- alerts

    async Task CheckAlertsAsync(DateTime now)
    {
        var data = _s.Alerts.Data;
        if (!data.Rules.Any(r => r.Enabled)) return;
        var rows = _main.NetworkRows;
        var needsInventory = data.Rules.Any(r => r.Enabled && r.Kind is AlertKind.LowStorage or AlertKind.Hot);
        var crashRule = data.Rules.Any(r => r.Enabled && r.Kind == AlertKind.Crash);

        // Every device we know of: on the network now, or a member of a group.
        var ids = rows.Where(r => r.Sn != null).Select(r => r.GroupId).ToHashSet();
        foreach (var g in _s.Groups.Groups) ids.UnionWith(g.Members);

        foreach (var id in ids)
        {
            var row = rows.FirstOrDefault(r => r.GroupId == id);
            var connected = row is { Serial: not null, State: DeviceState.Connected or DeviceState.Mirroring };
            var online = row != null && row.State != DeviceState.NotFound;
            var name = row?.Name ?? _s.KnownFor(id)?.Name ?? id;
            var group = _s.Groups.GroupOf(id)?.Name;

            if (connected && needsInventory && (!_inventoryAt.TryGetValue(id, out var at) || now - at > TimeSpan.FromMinutes(15)))
            {
                _inventoryAt[id] = now;
                try { if (await _s.Adb!.GetInventoryAsync(row!.Serial!, name) is { } inv) _s.Inventory[id] = inv; } catch { }
            }
            _s.Inventory.TryGetValue(id, out var info);
            var battery = connected ? _main.BatteryOf(row!.Serial!) : null;

            foreach (var (rule, text) in _watcher.Check(data, id, name, group, online,
                         battery?.Level ?? (connected ? info?.BatteryLevel : null), battery?.Charging ?? info?.Charging ?? false,
                         connected ? info?.StorageFreePercent : null, connected ? info?.BatteryTempC : null, now))
                Raise(rule, rule.Kind, name, id, text);

            if (connected && crashRule && AlertWatcher.CrashRuleOn(data, group))
                await CheckCrashesAsync(row!, id, name, now);
        }
    }

    async Task CheckCrashesAsync(DeviceRow row, string id, string name, DateTime now)
    {
        var first = !_crashCheckedAt.TryGetValue(id, out var since);
        if (!first && now - since < TimeSpan.FromMinutes(2)) return;
        _crashCheckedAt[id] = now;
        if (first) return;   // start watching from now; don't report old crashes
        try
        {
            // The crash buffer holds only crashes, so this is cheap.
            var r = await _s.Adb!.ShellAsync(row.Serial!, $"logcat -b crash -d -v threadtime -T '{since:MM-dd HH:mm:ss}.000'", TimeSpan.FromSeconds(15));
            foreach (Match m in CrashRx().Matches(r.Output))
            {
                var pkg = m.Groups[1].Value;
                var rule = _s.Alerts.Data.Rules.First(x => x.Kind == AlertKind.Crash);
                Raise(rule, AlertKind.Crash, name, id, Loc.F("{0}: {1} crashed", name, pkg));
            }
        }
        catch { }
    }

    void Raise(AlertRule rule, AlertKind kind, string device, string id, string text)
    {
        _s.Alerts.Add(new AlertEvent { Kind = kind, Device = device, DeviceId = id, Text = text });
        _main.Report(text, StatusKind.Warn);
        if (rule.Notify) Platform.Toast.Show(Loc.T("Paneless alert"), text);
    }

    [GeneratedRegex(@"Process: ([\w.]+), PID")]
    private static partial Regex CrashRx();
}
