using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Paneless.Core;

/// <summary>Stops a console program like pressing Ctrl+C in it, so scrcpy finishes writing a recording.
/// (Windows: attach to the program's own hidden console and send the signal there.)</summary>
public static class ProcessSignals
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(nint handler, bool add);
    [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint group);

    const uint CtrlC = 0, AttachParent = unchecked((uint)-1);
    static readonly object Gate = new();
    /// <summary>Why the last Interrupt failed (for logs and tests).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Sends Ctrl+C and waits up to <paramref name="wait"/> for the program to end. False if it didn't.</summary>
    public static bool Interrupt(Process p, TimeSpan wait)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { if (p.HasExited) return true; } catch { return true; }
        lock (Gate)
        {
            LastError = null;
            var hadConsole = FreeConsole();   // true if we were attached to one (even a hidden one)
            try
            {
                if (!AttachConsole((uint)p.Id)) { LastError = $"attach failed ({Marshal.GetLastWin32Error()})"; return false; }
                SetConsoleCtrlHandler(0, true);          // don't stop ourselves
                if (!GenerateConsoleCtrlEvent(CtrlC, 0)) LastError = $"signal failed ({Marshal.GetLastWin32Error()})";
                FreeConsole();
                var ok = p.WaitForExit(wait);
                if (!ok) LastError ??= "didn't exit in time";
                return ok;
            }
            catch { return false; }
            finally
            {
                SetConsoleCtrlHandler(0, false);
                if (hadConsole) AttachConsole(AttachParent);
            }
        }
    }
}
