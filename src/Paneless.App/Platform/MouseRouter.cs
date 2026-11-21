using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Paneless.App.Platform;

/// <summary>Watches the mouse over a mirror window (Windows, low-level hook) without taking anything away from it:
/// the mirror still gets every click. Used by group control to repeat touches on the other devices.</summary>
[SupportedOSPlatform("windows")]
public static class MouseRouter
{
    public enum Kind { Down, Move, Up, Wheel }

    /// <summary>A mouse event in screen pixels; Wheel carries the wheel delta (120 per notch).</summary>
    public readonly record struct Event(Kind Kind, int X, int Y, int Wheel);

    delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MsLlHookStruct { public int X, Y; public uint MouseData, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern nint GetModuleHandle(string? name);

    const int WH_MOUSE_LL = 14, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEWHEEL = 0x20A;
    const uint LLMHF_INJECTED = 1, WM_QUIT = 0x12;

    static readonly ConcurrentDictionary<nint, Action<Event>> Watchers = new();
    static readonly Channel<(Action<Event>, Event)> Queue = Channel.CreateUnbounded<(Action<Event>, Event)>();
    static readonly object Gate = new();
    static HookProc? _proc;
    static Thread? _thread;
    static uint _threadId;
    static Task? _pump;
    static nint _dragging;   // window that got the button-down, so moves and the release follow it even outside

    public static void Watch(nint window, Action<Event> onEvent)
    {
        Watchers[window] = onEvent;
        lock (Gate)
        {
            _pump ??= Task.Run(async () =>
            {
                await foreach (var (handler, e) in Queue.Reader.ReadAllAsync())
                    try { handler(e); } catch { }
            });
            if (_thread != null) return;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _proc = Callback;
                var hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
                ready.Set();
                while (GetMessage(out _, 0, 0, 0) > 0) { }
                UnhookWindowsHookEx(hook);
            }) { IsBackground = true, Name = "Paneless mouse hook" };
            _thread.Start();
            ready.Wait(2000);
        }
    }

    public static void Unwatch(nint window)
    {
        Watchers.TryRemove(window, out _);
        if (!Watchers.IsEmpty) return;
        lock (Gate)
        {
            if (_thread == null) return;
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
            _thread = null;
        }
    }

    static nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            var msg = (int)wParam;
            if ((m.Flags & LLMHF_INJECTED) == 0)
            {
                var fg = Win32.GetForegroundWindow();
                if (msg == WM_LBUTTONDOWN && Watchers.TryGetValue(fg, out var h) && Inside(fg, m.X, m.Y))
                {
                    _dragging = fg;
                    Queue.Writer.TryWrite((h, new Event(Kind.Down, m.X, m.Y, 0)));
                }
                else if (msg == WM_MOUSEMOVE && _dragging != 0 && Watchers.TryGetValue(_dragging, out h))
                    Queue.Writer.TryWrite((h, new Event(Kind.Move, m.X, m.Y, 0)));
                else if (msg == WM_LBUTTONUP && _dragging != 0)
                {
                    if (Watchers.TryGetValue(_dragging, out h)) Queue.Writer.TryWrite((h, new Event(Kind.Up, m.X, m.Y, 0)));
                    _dragging = 0;
                }
                else if (msg == WM_MOUSEWHEEL && Watchers.TryGetValue(fg, out h) && Inside(fg, m.X, m.Y))
                    Queue.Writer.TryWrite((h, new Event(Kind.Wheel, m.X, m.Y, (short)(m.MouseData >> 16))));
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    static bool Inside(nint hwnd, int x, int y) =>
        Win32.ClientBounds(hwnd) is { } c && x >= c.Left && x < c.Right && y >= c.Top && y < c.Bottom;
}
