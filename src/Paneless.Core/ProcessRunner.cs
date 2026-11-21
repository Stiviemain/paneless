using System.Diagnostics;

namespace Paneless.Core;

public sealed record ProcessResult(bool Ok, bool TimedOut, int ExitCode, string Output)
{
    public string LastLine =>
        Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}

/// <summary>Runs a command-line tool with a timeout and collects its output.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string exe, IEnumerable<string> args, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? env = null, string? stdin = null, CancellationToken ct = default,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { return new ProcessResult(false, false, -1, ex.Message); }

        if (stdin != null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { p.Kill(); } catch { /* already gone */ }
        }

        // A background child (e.g. the adb server) can keep the pipes open; don't wait on it forever.
        var output = "";
        if (await Task.WhenAny(outTask, Task.Delay(1500, CancellationToken.None)) == outTask) output += outTask.Result;
        if (await Task.WhenAny(errTask, Task.Delay(500, CancellationToken.None)) == errTask) output += "\n" + errTask.Result;

        var code = -1;
        if (!timedOut) { try { code = p.ExitCode; } catch { /* not available */ } }
        return new ProcessResult(!timedOut && code == 0, timedOut, code, output.Trim());
    }
}
