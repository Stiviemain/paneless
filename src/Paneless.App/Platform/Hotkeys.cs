using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Input;
using Paneless.Core;

namespace Paneless.App;

/// <summary>Shortcut text like "Ctrl+Alt+P" ↔ modifiers and a Windows virtual-key code.</summary>
public static class Hotkeys
{
    public const uint ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8;

    /// <summary>The text for a pressed combination, or null if it isn't usable (needs Ctrl, Alt or Win, plus a normal key).</summary>
    public static string? Format(KeyModifiers mods, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return null;
        if ((mods & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0) return null;
        if (Views.AvaloniaKeys.Name(key) is not { } name || name.Contains("Shift") || name.Contains("Ctrl") || name.Contains("Alt")) return null;
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(name);
        return string.Join('+', parts);
    }

    public static (uint Mods, int Vk)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+');
        uint mods = 0;
        foreach (var p in parts[..^1])
            mods |= p switch { "Ctrl" => ModCtrl, "Alt" => ModAlt, "Shift" => ModShift, "Win" => ModWin, _ => 0 };
        // A trailing "+" key is written as "...++"
        var keyName = text.EndsWith("++") ? "+" : parts[^1];
        return KeyNames.Code(keyName) is { } vk && mods != 0 ? (mods, vk) : null;
    }
}

/// <summary>System-wide shortcuts (Windows RegisterHotKey) on a thread of their own.</summary>
[SupportedOSPlatform("windows")]
public static class GlobalHotkeys
{
    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    const uint WM_HOTKEY = 0x312, WM_APP_RELOAD = 0x8001, MOD_NOREPEAT = 0x4000;

    static Thread? _thread;
    static uint _threadId;
    static Dictionary<string, string> _wanted = new();     // shortcut text → device id
    static readonly Dictionary<int, string> Registered = new();
    static readonly object Gate = new();

    /// <summary>Raised (on the hotkey thread) with the device id whose shortcut was pressed.</summary>
    public static event Action<string>? Pressed;
    /// <summary>Shortcuts that couldn't be registered (another program already uses them).</summary>
    public static IReadOnlyList<string> Failed { get; private set; } = [];

    /// <summary>Replaces all shortcuts: text ("Ctrl+Alt+P") → device id.</summary>
    public static void Set(IReadOnlyDictionary<string, string> shortcuts)
    {
        lock (Gate)
        {
            _wanted = new Dictionary<string, string>(shortcuts);
            if (_thread == null)
            {
                if (_wanted.Count == 0) return;
                var ready = new ManualResetEventSlim();
                _thread = new Thread(() =>
                {
                    _threadId = GetCurrentThreadId();
                    Reload();
                    ready.Set();
                    while (GetMessage(out var m, 0, 0, 0) > 0)
                    {
                        if (m.Message == WM_HOTKEY && Registered.TryGetValue((int)m.WParam, out var id)) Pressed?.Invoke(id);
                        else if (m.Message == WM_APP_RELOAD) Reload();
                    }
                }) { IsBackground = true, Name = "Paneless hotkeys" };
                _thread.Start();
                ready.Wait(2000);
            }
            else PostThreadMessage(_threadId, WM_APP_RELOAD, 0, 0);
        }
    }

    static void Reload()
    {
        foreach (var id in Registered.Keys) UnregisterHotKey(0, id);
        Registered.Clear();
        Dictionary<string, string> wanted;
        lock (Gate) wanted = _wanted;
        var failed = new List<string>();
        var next = 1;
        foreach (var (text, device) in wanted)
        {
            if (Hotkeys.Parse(text) is not { } hk) continue;
            if (RegisterHotKey(0, next, hk.Mods | MOD_NOREPEAT, (uint)hk.Vk)) Registered[next++] = device;
            else failed.Add(text);
        }
        Failed = failed;
    }
}
