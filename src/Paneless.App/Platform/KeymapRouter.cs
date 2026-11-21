using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Paneless.Core;

namespace Paneless.App.Platform;

/// <summary>Sends key presses to the right device while its mirror window is in front (Windows).
/// A low-level keyboard hook sees every key; only mapped keys of the focused mirror are taken,
/// everything else passes through untouched.</summary>
[SupportedOSPlatform("windows")]
public static class KeymapRouter
{
    delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KbdLlHookStruct { public int VkCode, ScanCode, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern nint GetModuleHandle(string? name);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const int LLKHF_INJECTED = 0x10;
    const uint WM_QUIT = 0x12;

    static readonly ConcurrentDictionary<nint, KeymapEngine> Engines = new();
    static readonly Channel<(KeymapEngine Engine, int Vk, bool Down)> Queue = Channel.CreateUnbounded<(KeymapEngine, int, bool)>();
    static readonly object Gate = new();
    static HookProc? _proc;        // kept alive so the GC doesn't collect the callback
    static Thread? _thread;
    static uint _threadId;
    static Task? _pump;

    public static void Register(nint mirrorWindow, KeymapEngine engine)
    {
        Engines[mirrorWindow] = engine;
        EnsureHook();
    }

    public static void Unregister(nint mirrorWindow)
    {
        if (Engines.TryRemove(mirrorWindow, out var engine)) Queue.Writer.TryWrite((engine, -1, false));
        if (Engines.IsEmpty && Observers.IsEmpty) RemoveHook();
    }

    /// <summary>Observers see the keys typed into a mirror window (they pass through untouched); used by group control.</summary>
    static readonly ConcurrentDictionary<nint, Action<int, bool>> Observers = new();
    static readonly Channel<(Action<int, bool> Observer, int Vk, bool Down)> ObserverQueue = CreateObserverQueue();

    static Channel<(Action<int, bool>, int, bool)> CreateObserverQueue()
    {
        var ch = Channel.CreateUnbounded<(Action<int, bool>, int, bool)>();
        _ = Task.Run(async () =>
        {
            await foreach (var (observer, vk, down) in ch.Reader.ReadAllAsync())
                try { observer(vk, down); } catch { }
        });
        return ch;
    }

    public static void Observe(nint mirrorWindow, Action<int, bool> onKey)
    {
        Observers[mirrorWindow] = onKey;
        EnsureHook();
    }

    public static void StopObserving(nint mirrorWindow)
    {
        Observers.TryRemove(mirrorWindow, out _);
        if (Engines.IsEmpty && Observers.IsEmpty) RemoveHook();
    }

    /// <summary>Lifts all fingers of an engine (queued, so it never races with key events).</summary>
    public static void Release(KeymapEngine engine) => Queue.Writer.TryWrite((engine, -1, false));

    static void EnsureHook()
    {
        lock (Gate)
        {
            _pump ??= Task.Run(PumpAsync);
            if (_thread != null) return;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _proc = Callback;
                var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                ready.Set();
                while (GetMessage(out _, 0, 0, 0) > 0) { }   // a hook needs a message loop on its thread
                UnhookWindowsHookEx(hook);
            }) { IsBackground = true, Name = "Paneless keyboard hook" };
            _thread.Start();
            ready.Wait(2000);
        }
    }

    static void RemoveHook()
    {
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
            var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var msg = (int)wParam;
            var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            var up = msg is WM_KEYUP or WM_SYSKEYUP;
            if ((down || up) && (k.Flags & LLKHF_INJECTED) == 0
                && Engines.TryGetValue(Win32.GetForegroundWindow(), out var engine) && engine.Handles(k.VkCode))
            {
                Queue.Writer.TryWrite((engine, k.VkCode, down));
                return 1;   // taken: the mirror window doesn't also get this key
            }
            if ((down || up) && (k.Flags & LLKHF_INJECTED) == 0 && Observers.TryGetValue(Win32.GetForegroundWindow(), out var observer))
            {
                ObserverQueue.Writer.TryWrite((observer, k.VkCode, down));   // in order, off the hook thread
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    static async Task PumpAsync()
    {
        await foreach (var (engine, vk, down) in Queue.Reader.ReadAllAsync())
        {
            try
            {
                if (vk < 0) await engine.ReleaseAllAsync();
                else await engine.OnKeyAsync(vk, down);
            }
            catch { /* connection gone: the toolbar notices and turns mapping off */ }
        }
    }
}
