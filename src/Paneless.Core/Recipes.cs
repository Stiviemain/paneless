namespace Paneless.Core;

public enum RecipeStepKind { InstallApk, CopyFile, RunScript, Shell, Reboot }

/// <summary>One step of a recipe. Which fields matter depends on <see cref="Kind"/>.</summary>
public sealed class RecipeStep
{
    public RecipeStepKind Kind { get; set; }
    /// <summary>InstallApk / CopyFile: a file on this computer.</summary>
    public string Path { get; set; } = "";
    /// <summary>CopyFile: folder on the device (empty = the device's drop folder).</summary>
    public string Target { get; set; } = "";
    /// <summary>RunScript: the script's id; values for its fields.</summary>
    public string ScriptId { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = new();
    /// <summary>Shell: a command run with "adb shell".</summary>
    public string Command { get; set; } = "";
}

/// <summary>A provisioning recipe: apps, files, scripts and settings applied to devices in one go,
/// e.g. to set up a new or reset tablet.</summary>
public sealed class Recipe
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<RecipeStep> Steps { get; set; } = new();
    /// <summary>Keep going on other steps when one fails.</summary>
    public bool ContinueOnError { get; set; }
}

public sealed class RecipeStore
{
    public const string FileName = "recipes.json";
    readonly bool _persist;
    public RecipeStore() : this(JsonFile.Load<List<Recipe>>(FileName), true) { }
    public RecipeStore(List<Recipe> recipes, bool persist) { All = recipes; _persist = persist; }
    public List<Recipe> All { get; }
    public event Action? Changed;
    public Recipe? Get(string id) => All.FirstOrDefault(r => r.Id == id);
    public Recipe? Find(string name) => All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    public void Save()
    {
        if (_persist) { try { JsonFile.Save(FileName, All); } catch { } }
        Changed?.Invoke();
    }
}

/// <summary>Result of one step on one device.</summary>
public sealed record StepOutcome(int Index, string What, bool Ok, string Detail);

/// <summary>Runs a recipe on one device.</summary>
public sealed class RecipeRunner(AdbClient adb, Func<string, AdbScript?> findScript)
{
    public static string Describe(RecipeStep s, Func<string, AdbScript?> findScript) => s.Kind switch
    {
        RecipeStepKind.InstallApk => Text.F("Install {0}", System.IO.Path.GetFileName(s.Path)),
        RecipeStepKind.CopyFile => s.Target.Length > 0 ? Text.F("Copy {0} to {1}", System.IO.Path.GetFileName(s.Path), s.Target) : Text.F("Copy {0}", System.IO.Path.GetFileName(s.Path)),
        RecipeStepKind.RunScript => Text.F("Run the script “{0}”", findScript(s.ScriptId)?.Name ?? Text.T("(missing)")),
        RecipeStepKind.Shell => "adb shell " + s.Command,
        RecipeStepKind.Reboot => Text.T("Restart the device"),
        _ => s.Kind.ToString(),
    };

    public async Task<IReadOnlyList<StepOutcome>> RunAsync(Recipe recipe, string serial, string deviceName, string pushTarget,
        IProgress<StepOutcome>? progress = null, CancellationToken ct = default)
    {
        var results = new List<StepOutcome>();
        for (var i = 0; i < recipe.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = recipe.Steps[i];
            var what = Describe(s, findScript);
            StepOutcome outcome;
            try
            {
                outcome = s.Kind switch
                {
                    RecipeStepKind.InstallApk => FromResult(i, what, await adb.InstallAsync(serial, s.Path)),
                    RecipeStepKind.CopyFile => FromResult(i, what, await adb.PushAsync(serial, s.Path, s.Target.Length > 0 ? s.Target : pushTarget)),
                    RecipeStepKind.Shell => FromResult(i, what, await adb.ShellAsync(serial, s.Command, TimeSpan.FromSeconds(60))),
                    RecipeStepKind.Reboot => FromResult(i, what, await adb.RunAsync(TimeSpan.FromSeconds(20), "-s", serial, "reboot")),
                    RecipeStepKind.RunScript => await RunScriptAsync(i, what, s, serial, deviceName),
                    _ => new StepOutcome(i, what, false, Text.T("Unknown step")),
                };
            }
            catch (Exception ex) { outcome = new StepOutcome(i, what, false, ex.Message); }
            results.Add(outcome);
            progress?.Report(outcome);
            if (!outcome.Ok && !recipe.ContinueOnError) break;
        }
        return results;
    }

    static StepOutcome FromResult(int i, string what, ProcessResult r) =>
        new(i, what, r.Ok && !r.Output.Contains("Failure", StringComparison.OrdinalIgnoreCase), r.LastLine);

    async Task<StepOutcome> RunScriptAsync(int i, string what, RecipeStep s, string serial, string name)
    {
        if (findScript(s.ScriptId) is not { } script) return new StepOutcome(i, what, false, Text.T("The script doesn’t exist any more."));
        var values = ScriptText.Placeholders(script.Commands).ToDictionary(p => p.Name, p => s.Values.GetValueOrDefault(p.Name) ?? p.Default ?? "");
        var res = await new ScriptRunner(adb).RunAsync(serial, name, script, values);
        return new StepOutcome(i, what, res.Ok, res.Steps.LastOrDefault()?.Result.LastLine ?? "");
    }
}
