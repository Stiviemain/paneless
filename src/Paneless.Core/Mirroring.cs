using System.Diagnostics;

namespace Paneless.Core;

public sealed record MirrorOptions(bool ScreenOff, bool Audio, bool OnTop, bool KeepAwake, int? MaxSize,
    string VideoCodec = "h264", int BitrateMbps = 8, int MaxFps = 0);

/// <summary>Mirror: the device's screen (gets a toolbar). App: one app on its own virtual display.
/// Screen: a second screen (virtual display). Camera: a camera. Recording: no window, writes a video file.</summary>
public enum SessionKind { Mirror, App, Screen, Camera, Recording }

public sealed class MirrorSession
{
    public required string Serial { get; init; }
    public required string Name { get; init; }
    public SessionKind Kind { get; init; } = SessionKind.Mirror;
    /// <summary>Recording: the file being written.</summary>
    public string? OutputFile { get; init; }
    public bool StoppedByUs { get; set; }
    /// <summary>App and second-screen windows: the number of the virtual display scrcpy created (from its log).</summary>
    public int? DisplayId { get; set; }
    /// <summary>Camera windows: how often it was reopened after the device cut the camera off.</summary>
    public int Restarts { get; init; }
    /// <summary>The scrcpy options it was started with (to reopen it).</summary>
    public IReadOnlyList<string> Args { get; init; } = [];
    public required Process Process { get; init; }
    public required string LogPath { get; init; }
    public DateTime Started { get; } = DateTime.UtcNow;
    public bool IsRunning { get { try { return !Process.HasExited; } catch { return false; } } }
}

/// <summary>Starts scrcpy windows and reports when one stops because of an error.</summary>
public sealed partial class MirrorManager(ToolPaths tools)
{
    readonly List<MirrorSession> _sessions = new();
    readonly object _lock = new();

    /// <summary>Raised (on a background thread) when a mirror stops within 30 s with an error.</summary>
    public event Action<MirrorSession, string>? Failed;

    /// <summary>Devices with a running mirror of their own screen.</summary>
    public ISet<string> ActiveSerials
    {
        get { lock (_lock) return _sessions.Where(s => s.IsRunning && s.Kind == SessionKind.Mirror).Select(s => s.Serial).ToHashSet(); }
    }

    public IReadOnlyList<MirrorSession> Sessions
    {
        get { lock (_lock) return _sessions.Where(s => s.IsRunning).ToList(); }
    }

    public MirrorSession? Recording(string serial)
    {
        lock (_lock) return _sessions.FirstOrDefault(s => s.IsRunning && s.Serial == serial && s.Kind == SessionKind.Recording);
    }

    static IEnumerable<string> Quality(MirrorOptions o)
    {
        yield return "--video-codec=" + o.VideoCodec;
        yield return $"--video-bit-rate={Math.Clamp(o.BitrateMbps, 1, 200)}M";
        if (o.MaxFps > 0) yield return $"--max-fps={o.MaxFps}";
        if (o.MaxSize is { } max) yield return $"--max-size={max}";
    }

    bool HasDisplaySession(string serial)
    {
        lock (_lock) return _sessions.Any(s => s.IsRunning && s.Serial == serial && s.Kind is SessionKind.Mirror or SessionKind.Screen or SessionKind.App);
    }

    /// <summary>Mirrors the device's screen.</summary>
    public MirrorSession Start(string serial, string name, MirrorOptions o)
    {
        var args = new List<string> { "--window-title", name };
        args.AddRange(Quality(o));
        if (o.ScreenOff) args.Add("--turn-screen-off");
        if (!o.Audio) args.Add("--no-audio");
        if (o.OnTop) args.Add("--always-on-top");
        if ((o.KeepAwake || o.ScreenOff) && !HasDisplaySession(serial)) args.Add("--screen-off-timeout=3600");
        return StartCore(serial, name, SessionKind.Mirror, args);
    }

    /// <summary>Opens one app in a window of its own: a new virtual display that follows the window's size,
    /// so the device's own screen is left alone. Closing the window closes the app's screen.</summary>
    public MirrorSession StartApp(string serial, string name, string package, MirrorOptions o)
    {
        var title = $"{name} – {package}";
        var args = new List<string> { "--window-title", title, "--new-display=1080x1920", "--flex-display", "--start-app=" + package, "--no-audio" };
        args.AddRange(Quality(o with { MaxSize = null }));
        if (o.ScreenOff) args.Add("--turn-screen-off");
        if (o.OnTop) args.Add("--always-on-top");
        if ((o.KeepAwake || o.ScreenOff) && !HasDisplaySession(serial)) args.Add("--screen-off-timeout=3600");
        return StartCore(serial, title, SessionKind.App, args);
    }

    /// <summary>A second, resizable screen of the device (with its launcher), next to its own screen.</summary>
    public MirrorSession StartScreen(string serial, string name, MirrorOptions o)
    {
        var title = $"{name} – second screen";
        var args = new List<string> { "--window-title", title, "--new-display=1600x1000", "--flex-display", "--no-audio" };
        args.AddRange(Quality(o with { MaxSize = null }));
        if (o.ScreenOff) args.Add("--turn-screen-off");
        if (o.OnTop) args.Add("--always-on-top");
        if ((o.KeepAwake || o.ScreenOff) && !HasDisplaySession(serial)) args.Add("--screen-off-timeout=3600");
        return StartCore(serial, title, SessionKind.Screen, args);
    }

    /// <summary>Shows a camera (Android 12+). Ctrl+T / Ctrl+Shift+T switch the torch, Ctrl+↑/↓ zoom (scrcpy shortcuts).</summary>
    /// <param name="turn">Clockwise turn of the picture (0, 90, 180, 270) so it shows upright; Alt+←/→ in the window turn it further.</param>
    public MirrorSession StartCamera(string serial, string name, bool front, MirrorOptions o, int? turn = null)
    {
        var title = $"{name} – {(front ? "front" : "back")} camera";
        var args = new List<string> { "--window-title", title, "--video-source=camera", "--camera-facing=" + (front ? "front" : "back"), "--no-audio" };
        if (turn is { } t and > 0) args.Add($"--orientation={t}");
        args.AddRange(Quality(o with { MaxSize = 1920 }));
        if (o.OnTop) args.Add("--always-on-top");
        return StartCore(serial, title, SessionKind.Camera, args);
    }

    /// <summary>Records the screen to a video file without opening a window (Matroska, so the file stays
    /// playable when the recording is stopped).</summary>
    public MirrorSession StartRecording(string serial, string name, string file, MirrorOptions o, int? displayId = null)
    {
        var args = new List<string> { "--no-window", "--no-playback", "--no-control", "--record=" + file };
        if (displayId is { } id) args.Add($"--display-id={id}");   // a second screen or app window
        args.AddRange(Quality(o));
        if (o.Audio) args.Add("--audio-dup");   // keep the sound playing on the device too
        else args.Add("--no-audio");
        return StartCore(serial, name + " (recording)", SessionKind.Recording, args, file);
    }

    public static void Stop(MirrorSession s)
    {
        s.StoppedByUs = true;
        // A recording is ended like Ctrl+C so the video file is complete; anything else (or if that fails) is closed.
        if (s.Kind == SessionKind.Recording && ProcessSignals.Interrupt(s.Process, TimeSpan.FromSeconds(5))) return;
        // The whole tree: scrcpy's adb helpers would otherwise keep running (and keep its output open).
        try { if (!s.Process.HasExited) s.Process.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Set by the app: the last seen position and size of each window (screen pixels), so a camera window
    /// can reopen where it was.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<MirrorSession, (int X, int Y, int W, int H)> LastWindow { get; } = new();

    /// <summary>Set by the app: the upright turn for a camera right now (serial, front) — used when reopening, since the
    /// device has usually just been turned.</summary>
    public Func<string, bool, int?>? CameraTurn { get; set; }

    /// <summary>Raised when a camera window was reopened after the device cut the camera off (old, new).</summary>
    public event Action<MirrorSession, MirrorSession>? Reopened;

    [System.Text.RegularExpressions.GeneratedRegex(@"\(id=(\d+)\)")]
    private static partial System.Text.RegularExpressions.Regex DisplayIdPattern();
    static readonly System.Text.RegularExpressions.Regex DisplayIdRx = DisplayIdPattern();

    /// <summary>Phones cut a camera off when they rotate (and sometimes otherwise): reopen it in the same place.</summary>
    void ReopenCameraIfCutOff(MirrorSession s)
    {
        if (s.Kind != SessionKind.Camera || s.StoppedByUs || s.Restarts >= 20) return;
        if (DateTime.UtcNow - s.Started < TimeSpan.FromSeconds(3)) return;   // failing right away: don't loop
        string text;
        try { text = File.ReadAllText(s.LogPath); } catch { return; }
        if (!text.Contains("Camera disconnected")) return;   // closed by the user, or another error
        int newTurn;
        try { newTurn = CameraTurn?.Invoke(s.Serial, IsFrontCamera(s)) ?? TurnOf(s); } catch { newTurn = TurnOf(s); }
        LastWindow.TryGetValue(s, out var w);
        try { Reopened?.Invoke(s, ReopenCamera(s, newTurn, s.Restarts + 1, w)); }
        catch (Exception ex) { Failed?.Invoke(s, Text.T("Couldn't reopen the camera: ") + ex.Message); }
    }

    /// <summary>The clockwise turn a camera window was started with.</summary>
    public static int TurnOf(MirrorSession s) =>
        s.Args.FirstOrDefault(a => a.StartsWith("--orientation=")) is { } o && int.TryParse(o[14..], out var t) ? t : 0;

    public static bool IsFrontCamera(MirrorSession s) => s.Args.Contains("--camera-facing=front");

    /// <summary>Reopens a camera window turned differently (the device is held another way now), in the same place.</summary>
    public MirrorSession? TurnCamera(MirrorSession s, int turn)
    {
        LastWindow.TryGetValue(s, out var w);
        Stop(s);
        try { s.Process.WaitForExit(3000); } catch { }   // let the old window finish closing first
        try
        {
            var next = ReopenCamera(s, turn, s.Restarts, w);
            Reopened?.Invoke(s, next);
            return next;
        }
        catch (Exception ex) { Failed?.Invoke(s, Text.T("Couldn't reopen the camera: ") + ex.Message); return null; }
    }

    MirrorSession ReopenCamera(MirrorSession s, int newTurn, int restarts, (int X, int Y, int W, int H) w)
    {
        var oldTurn = TurnOf(s);
        var args = s.Args.Where(a => !a.StartsWith("--window-x") && !a.StartsWith("--window-y") && !a.StartsWith("--orientation=")
                                     && !a.StartsWith("--window-width") && !a.StartsWith("--window-height")).ToList();
        if (newTurn > 0) args.Add($"--orientation={newTurn}");
        if (w.W > 50 && w.H > 50)
        {
            // A quarter turn more or less: the picture's shape flips, so the window does too (same place, same middle).
            if ((newTurn - oldTurn + 360) % 180 == 90) w = (w.X + (w.W - w.H) / 2, w.Y + (w.H - w.W) / 2, w.H, w.W);
            args.AddRange([$"--window-x={Math.Max(0, w.X)}", $"--window-y={Math.Max(0, w.Y)}", $"--window-width={w.W}", $"--window-height={w.H}"]);
        }
        return StartCore(s.Serial, s.Name, SessionKind.Camera, args, restarts: restarts);
    }

    MirrorSession StartCore(string serial, string name, SessionKind kind, IEnumerable<string> args, string? outputFile = null, int restarts = 0)
    {
        var psi = new ProcessStartInfo(tools.Scrcpy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,          // no console window; scrcpy's own window still shows
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(tools.Scrcpy)!,
        };
        psi.Environment["ADB"] = tools.Adb;   // make scrcpy use the same adb as Paneless
        psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(serial);
        foreach (var a in args) psi.ArgumentList.Add(a);

        Directory.CreateDirectory(AppPaths.LogDir);
        var log = Path.Combine(AppPaths.LogDir, $"{kind.ToString().ToLowerInvariant()}-" + string.Concat(serial.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_')) + ".log");
        var writer = OpenLog(ref log);
        var writerClosed = false;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        MirrorSession? session = null;
        void Append(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (writer) { if (!writerClosed) writer.WriteLine(e.Data); }
            // "[server] INFO: New display: 1080x1920/283 (id=163)"
            if (session != null && session.DisplayId == null && e.Data.Contains("New display") && DisplayIdRx.Match(e.Data) is { Success: true } m)
                session.DisplayId = int.Parse(m.Groups[1].Value);
        }
        p.OutputDataReceived += Append;
        p.ErrorDataReceived += Append;

        session = new MirrorSession { Serial = serial, Name = name, Process = p, LogPath = log, Kind = kind, OutputFile = outputFile, Args = args.ToList(), Restarts = restarts };
        p.Exited += (_, _) =>
        {
            p.WaitForExit(3000);   // flush the output handlers (bounded: a leftover child could hold the pipe)
            lock (writer) { writerClosed = true; writer.Dispose(); }
            lock (_lock) _sessions.Remove(session);
            int code;
            try { code = p.ExitCode; } catch { code = 0; }
            if (code != 0 && !session.StoppedByUs && DateTime.UtcNow - session.Started < TimeSpan.FromSeconds(30))
                Failed?.Invoke(session, LastError(log));
            ReopenCameraIfCutOff(session);
            LastWindow.TryRemove(session, out _);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (_lock) _sessions.Add(session);
        return session;
    }

    /// <summary>Opens the window's log. If the usual file is still held by a window that's closing, uses log-2, log-3, ….</summary>
    static StreamWriter OpenLog(ref string log)
    {
        var path = log;
        for (var i = 2; ; i++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                log = path;
                return new StreamWriter(stream) { AutoFlush = true };
            }
            catch (IOException) when (i < 10)
            {
                path = Path.Combine(Path.GetDirectoryName(log)!, Path.GetFileNameWithoutExtension(log) + "-" + i + ".log");
            }
        }
    }

    static string LastError(string log)
    {
        try
        {
            return File.ReadLines(log).LastOrDefault(l => l.Contains("ERROR"))?.Replace("ERROR:", "").Trim() ?? "";
        }
        catch { return ""; }
    }
}
