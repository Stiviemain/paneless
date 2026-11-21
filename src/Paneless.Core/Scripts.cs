using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Paneless.Core;

/// <summary>A saved set of adb commands, one per line. Lines starting with # are comments.
/// Fields like {package} or {dpi=420} are asked for when the script runs.</summary>
public sealed class AdbScript
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Commands { get; set; } = "";
    /// <summary>Ask for confirmation before running.</summary>
    public bool Confirm { get; set; }

    [JsonIgnore] public bool BuiltIn { get; init; }
    [JsonIgnore] public string Category { get; init; } = "My scripts";
    /// <summary>Shown in amber, e.g. "Disconnects wireless debugging".</summary>
    [JsonIgnore] public string? Warning { get; init; }
}

public sealed record Placeholder(string Name, string? Default);

public static partial class ScriptText
{
    /// <summary>Fields the user fills in. {device}, {serial} and {time} are filled automatically.</summary>
    public static readonly string[] Automatic = ["device", "serial", "time"];

    public static IReadOnlyList<string> Lines(string commands) =>
        commands.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    public static IReadOnlyList<Placeholder> Placeholders(string commands)
    {
        var list = new List<Placeholder>();
        foreach (Match m in PlaceholderRx().Matches(commands))
        {
            var name = m.Groups[1].Value;
            if (Automatic.Contains(name, StringComparer.OrdinalIgnoreCase) || list.Any(p => p.Name == name)) continue;
            list.Add(new Placeholder(name, m.Groups[2].Success ? m.Groups[2].Value : null));
        }
        return list;
    }

    /// <summary>Turns one script line into adb arguments. "adb" in front is optional.
    /// Everything after "shell" runs on the device, so pipes like "| grep" work on Windows too.</summary>
    public static (string[]? Args, string? Error) ToAdbArgs(string line, IReadOnlyDictionary<string, string> values)
    {
        var text = line.StartsWith("adb ", StringComparison.OrdinalIgnoreCase) ? line[4..].TrimStart() : line;
        if (text.StartsWith("shell", StringComparison.OrdinalIgnoreCase) && (text.Length == 5 || char.IsWhiteSpace(text[5])))
        {
            var remote = text[5..].Trim();
            if (remote.Length == 0) return (null, Text.T("\"adb shell\" needs a command after it (an interactive shell isn't possible here)."));
            return (["shell", Fill(remote, values, forShell: true)], null);
        }
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return (null, Text.T("Empty command."));
        var first = tokens[0].ToLowerInvariant();
        if (first is "-s" or "-t" or "-d" or "-e" or "-h" or "-p" or "-l")
            return (null, Text.T("Leave out device options like -s: Paneless runs the script on the devices you tick."));
        if (first is "kill-server" or "start-server" or "connect" or "disconnect" or "pair" or "devices" or "wait-for-device")
            return (null, Text.F("\"adb {0}\" manages Paneless's own connections and can't be used in a script.", first));
        return (tokens.Select(t => Fill(t, values, forShell: false)).ToArray(), null);
    }

    static string Fill(string text, IReadOnlyDictionary<string, string> values, bool forShell) =>
        PlaceholderRx().Replace(text, m =>
        {
            var v = values.TryGetValue(m.Groups[1].Value, out var given) ? given : m.Groups[2].Success ? m.Groups[2].Value : "";
            return forShell ? ShellQuote(v) : v;
        });

    /// <summary>Plain values stay as they are; anything else is single-quoted so it can't change the command.</summary>
    public static string ShellQuote(string v) =>
        v.Length > 0 && SafeRx().IsMatch(v) ? v : "'" + v.Replace("'", "'\\''") + "'";

    static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        var any = false;
        foreach (var c in s)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); continue; }
            if (c is '"' or '\'') { quote = c; any = true; continue; }
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0 || any) { list.Add(sb.ToString()); sb.Clear(); any = false; } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0 || any) list.Add(sb.ToString());
        return list;
    }

    [GeneratedRegex(@"\{([A-Za-z][\w]*)(?:=([^}]*))?\}")]
    private static partial Regex PlaceholderRx();
    [GeneratedRegex(@"^[\w.:/@%+=,-]+$")]
    private static partial Regex SafeRx();
}

public sealed record StepResult(string Command, ProcessResult Result, string? Error);

public sealed record DeviceRunResult(string Device, IReadOnlyList<StepResult> Steps, TimeSpan Elapsed)
{
    public bool Ok => Steps.All(s => s.Error == null && s.Result.Ok);
}

public sealed class ScriptRunner(AdbClient adb)
{
    /// <summary>Where files from "adb pull" (screenshots, recordings) end up.</summary>
    public static string OutputDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs ? docs : Path.GetTempPath(), "Paneless");

    public async Task<DeviceRunResult> RunAsync(string serial, string deviceName, AdbScript script,
        IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        Directory.CreateDirectory(OutputDir);
        var all = new Dictionary<string, string>(values)
        {
            ["device"] = string.Concat(deviceName.Select(c => char.IsLetterOrDigit(c) ? c : '-')),
            ["serial"] = serial,
            ["time"] = DateTime.Now.ToString("yyyyMMdd-HHmmss"),
        };
        var sw = Stopwatch.StartNew();
        var steps = new List<StepResult>();
        foreach (var line in ScriptText.Lines(script.Commands))
        {
            ct.ThrowIfCancellationRequested();
            var (args, error) = ScriptText.ToAdbArgs(line, all);
            if (args == null) { steps.Add(new StepResult(line, new ProcessResult(false, false, -1, ""), error)); break; }
            var full = new[] { "-s", serial }.Concat(args);
            var r = await ProcessRunner.RunAsync(adb.AdbPath, full, TimeSpan.FromSeconds(45), workingDirectory: OutputDir, ct: ct);
            steps.Add(new StepResult(line, r, r.TimedOut ? Text.T("Stopped after 45 seconds.") : null));
            if (r.TimedOut) break;
        }
        return new DeviceRunResult(deviceName, steps, sw.Elapsed);
    }
}

public sealed class ScriptStore
{
    const string File = "scripts.json";
    public List<AdbScript> Mine { get; } = JsonFile.Load<List<AdbScript>>(File);
    public void Save() => JsonFile.Save(File, Mine);
}
