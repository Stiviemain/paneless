using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Paneless.Core;

/// <summary>One logcat line ("threadtime" format), parsed.</summary>
public sealed record LogLine(string Time, int Pid, char Level, string Tag, string Message, string Raw)
{
    public bool IsCrash => Tag == "AndroidRuntime" && (Level == 'E' || Message.StartsWith("FATAL EXCEPTION"));
}

/// <summary>Streams a device's log (adb logcat) until stopped.</summary>
public sealed partial class LogcatSession : IDisposable
{
    readonly Process _p;

    /// <summary>Raised on a background thread for every line.</summary>
    public event Action<LogLine>? Line;
    public event Action? Ended;

    LogcatSession(Process p) => _p = p;

    /// <param name="tail">How many recent lines to show first.</param>
    public static LogcatSession Start(string adb, string serial, int tail = 500)
    {
        var psi = new ProcessStartInfo(adb)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-s", serial, "logcat", "-v", "threadtime", "-T", tail.ToString() }) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var s = new LogcatSession(p);
        p.OutputDataReceived += (_, e) => { if (e.Data != null) s.Line?.Invoke(Parse(e.Data)); };
        p.Exited += (_, _) => s.Ended?.Invoke();
        p.Start();
        p.BeginOutputReadLine();
        return s;
    }

    // "11-21 09:30:06.123  1234  1250 E Tag     : message"
    public static LogLine Parse(string raw)
    {
        var m = LineRx().Match(raw);
        return m.Success
            ? new LogLine(m.Groups[1].Value, int.Parse(m.Groups[2].Value), m.Groups[3].Value[0], m.Groups[4].Value.Trim(), m.Groups[5].Value, raw)
            : new LogLine("", 0, ' ', "", raw, raw);
    }

    /// <summary>The process id of an app, for filtering ("pidof"). Null if it isn't running.</summary>
    public static async Task<int?> PidOfAsync(AdbClient adb, string serial, string package)
    {
        var r = await adb.ShellAsync(serial, "pidof " + ScriptText.ShellQuote(package));
        return int.TryParse(r.Output.Trim().Split(' ')[0], out var pid) ? pid : null;
    }

    public void Dispose()
    {
        try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
        _p.Dispose();
    }

    [GeneratedRegex(@"^(\d\d-\d\d \d\d:\d\d:\d\d\.\d+)\s+(\d+)\s+\d+\s+([VDIWEFA])\s+(.*?)\s*:\s?(.*)$")]
    private static partial Regex LineRx();
}
