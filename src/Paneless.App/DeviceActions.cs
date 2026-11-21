using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App;

/// <summary>Runs things on several devices at once: scripts, recipes, restarts and shell commands.
/// Shared by Recipes, Scheduled tasks and Claude.</summary>
public sealed class DeviceActions(AppServices s, Func<IReadOnlyList<DeviceRow>> networkRows)
{
    /// <summary>The devices on the network that belong to these groups or have these ids (serial numbers).</summary>
    public IReadOnlyList<DeviceRow> Resolve(IEnumerable<string> groups, IEnumerable<string> deviceIds)
    {
        var ids = new HashSet<string>(deviceIds);
        foreach (var g in groups)
            if (s.Groups.Find(g) is { } group) ids.UnionWith(group.Members);
        return networkRows().Where(r => ids.Contains(r.GroupId) && r.CanMirror).ToList();
    }

    public sealed record DeviceResult(string Device, bool Ok, string Detail);

    /// <summary>Runs <paramref name="work"/> on each device (connecting first), up to 6 at a time.</summary>
    public async Task<IReadOnlyList<DeviceResult>> OnEachAsync(IReadOnlyList<DeviceRow> rows, Func<DeviceRow, string, Task<DeviceResult>> work, CancellationToken ct = default)
    {
        if (s.Demo) return rows.Select(r => new DeviceResult(r.Name, true, Loc.T("Demo mode: nothing was run."))).ToList();
        var gate = new SemaphoreSlim(6);
        var tasks = rows.Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var serial = await s.Directory!.ConnectAsync(r);
                if (serial == null) return new DeviceResult(r.Name, false, Loc.T("Couldn’t connect."));
                return await work(r, serial);
            }
            catch (Exception ex) { return new DeviceResult(r.Name, false, ex.Message); }
            finally { gate.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    public Task<IReadOnlyList<DeviceResult>> RunScriptAsync(AdbScript script, IReadOnlyList<DeviceRow> rows, IReadOnlyDictionary<string, string>? values = null) =>
        OnEachAsync(rows, async (r, serial) =>
        {
            var v = ScriptText.Placeholders(script.Commands).ToDictionary(p => p.Name, p => values?.GetValueOrDefault(p.Name) ?? p.Default ?? "");
            var res = await new ScriptRunner(s.Adb!).RunAsync(serial, r.Name, script, v);
            var output = string.Join("\n", res.Steps.Select(st => st.Error ?? st.Result.Output.Trim()).Where(x => x.Length > 0));
            return new DeviceResult(r.Name, res.Ok, output.Length > 4000 ? output[..4000] + "…" : output);
        });

    public Task<IReadOnlyList<DeviceResult>> RunRecipeAsync(Recipe recipe, IReadOnlyList<DeviceRow> rows, IProgress<(string Device, StepOutcome Step)>? progress = null) =>
        OnEachAsync(rows, async (r, serial) =>
        {
            var runner = new RecipeRunner(s.Adb!, s.FindScript);
            var p = progress == null ? null : new Progress<StepOutcome>(o => progress.Report((r.Name, o)));
            var steps = await runner.RunAsync(recipe, serial, r.Name, s.EffectiveFor(r.GroupId).PushTarget, p);
            var failed = steps.FirstOrDefault(x => !x.Ok);
            var ok = failed == null && steps.Count == recipe.Steps.Count;
            return new DeviceResult(r.Name, ok, failed == null ? Loc.F("{0} of {1} steps done", steps.Count, recipe.Steps.Count) : $"{failed.What}: {failed.Detail}");
        });

    public Task<IReadOnlyList<DeviceResult>> RebootAsync(IReadOnlyList<DeviceRow> rows) =>
        OnEachAsync(rows, async (r, serial) =>
        {
            var res = await s.Adb!.RunAsync(TimeSpan.FromSeconds(20), "-s", serial, "reboot");
            return new DeviceResult(r.Name, res.Ok, res.Ok ? Loc.T("Restarting") : res.LastLine);
        });

    public Task<IReadOnlyList<DeviceResult>> ShellAsync(string command, IReadOnlyList<DeviceRow> rows) =>
        OnEachAsync(rows, async (r, serial) =>
        {
            var res = await s.Adb!.ShellAsync(serial, command, TimeSpan.FromSeconds(60));
            var output = res.Output.Trim();
            return new DeviceResult(r.Name, res.Ok, output.Length > 4000 ? output[..4000] + "…" : output);
        });

    /// <summary>"6 of 6 devices", "4 of 6 devices (2 failed: …)".</summary>
    public static string Summary(IReadOnlyList<DeviceResult> results)
    {
        if (results.Count == 0) return Loc.T("No device of those was on the network.");
        var ok = results.Count(r => r.Ok);
        var text = Loc.F("{0} of {1} devices", ok, results.Count);
        var bad = results.Where(r => !r.Ok).Take(3).Select(r => r.Device).ToList();
        return bad.Count == 0 ? text : text + " · " + Loc.F("not on: {0}", string.Join(", ", bad));
    }
}
