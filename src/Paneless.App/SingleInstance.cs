using System.IO.Pipes;

namespace Paneless.App;

/// <summary>Only one Paneless runs per Windows user. Starting it again brings the open one to the front
/// (and Claude's bridge finds the right one).</summary>
public static class SingleInstance
{
    static readonly string Name = "Paneless-" + string.Concat(Environment.UserName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    static Mutex? _mutex;

    /// <summary>True while a Paneless (other than the asking process) holds the lock.</summary>
    public static bool IsRunning
    {
        get
        {
            try
            {
                if (Mutex.TryOpenExisting("Local\\" + Name, out var m)) { m.Dispose(); return true; }
            }
            catch { }
            return false;
        }
    }

    /// <summary>Takes the lock. False if another Paneless already has it: it's then sent <paramref name="message"/>
    /// ("show" to show its window, or "open-app &lt;id&gt;" from a remote app's shortcut).</summary>
    public static bool TryClaim(string message = "show")
    {
        _mutex = new Mutex(true, "Local\\" + Name, out var created);
        if (created) return true;
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            using var w = new StreamWriter(pipe);
            w.WriteLine(message);
        }
        catch { }
        return false;
    }

    /// <summary>Listens for messages from a second start.</summary>
    public static void Listen(Action<string> received)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync();
                    using var r = new StreamReader(pipe);
                    if (await r.ReadLineAsync() is { Length: > 0 } message) received(message);
                }
                catch { await Task.Delay(1000); }
            }
        });
    }
}
