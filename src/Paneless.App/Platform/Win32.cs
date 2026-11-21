using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paneless.App.Platform;

[SupportedOSPlatform("windows")]
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hWnd, int attr, out RECT rect, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(nint hWnd, int cmd);
    public static void Move(nint hWnd, int x, int y) => SetWindowPos(hWnd, 0, x, y, 0, 0, SWP_NOSIZE | 0x4 /* NOZORDER */ | SWP_NOACTIVATE);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hWnd, uint cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ClientToScreen(nint hWnd, ref POINT pt);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hWnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    /// <summary>The usable area (without the taskbar) of the screen a window is on.</summary>
    public static RECT WorkArea(nint hWnd)
    {
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hWnd, 2 /* nearest */), ref mi);
        return mi.Work;
    }

    /// <summary>Moves and resizes so the visible frame is exactly at the given rectangle.</summary>
    public static void Place(nint hWnd, int x, int y, int w, int h)
    {
        // The window rectangle includes an invisible resize border; compensate for it.
        GetWindowRect(hWnd, out var outer);
        var visible = Bounds(hWnd) ?? outer;
        int l = visible.Left - outer.Left, t = visible.Top - outer.Top, r = outer.Right - visible.Right, b = outer.Bottom - visible.Bottom;
        if (IsIconic(hWnd)) ShowWindow(hWnd, 9 /* restore */);
        SetWindowPos(hWnd, 0, x - l, y - t, w + l + r, h + t + b, 0x4 /* NOZORDER */ | SWP_NOACTIVATE);
    }

    /// <summary>The client area (inside the title bar and borders) in screen pixels.</summary>
    public static RECT? ClientBounds(nint hWnd)
    {
        if (!GetClientRect(hWnd, out var c)) return null;
        var p = new POINT();
        if (!ClientToScreen(hWnd, ref p)) return null;
        return new RECT { Left = p.X, Top = p.Y, Right = p.X + c.Right, Bottom = p.Y + c.Bottom };
    }

    /// <summary>Brings a window to the front (restoring it if minimised).</summary>
    public static void Activate(nint hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, 9);
        SetForegroundWindow(hWnd);
    }

    /// <summary>Testing aid: moves a window to the top of the stacking order without activating it.</summary>
    public static void Raise(nint hWnd) => SetWindowPos(hWnd, 0 /* HWND_TOP */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Testing aid: the given windows in stacking order, top first.</summary>
    public static string ZOrder(nint[] windows, string[] names)
    {
        var found = new List<string>();
        for (var h = GetWindow(windows[0], 0 /* GW_HWNDFIRST */); h != 0; h = GetWindow(h, 2 /* GW_HWNDNEXT */))
        {
            var i = Array.IndexOf(windows, h);
            if (i >= 0) found.Add(names[i]);
        }
        return string.Join(" > ", found);
    }

    const uint GW_HWNDPREV = 3;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>The visible window bounds in screen pixels (without the invisible resize border).</summary>
    public static RECT? Bounds(nint hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<RECT>()) == 0) return r;
        return GetWindowRect(hWnd, out r) ? r : null;
    }

    /// <summary>Clicking the window doesn't take focus away from the mirror, and it stays out of Alt+Tab.</summary>
    public static void MakeToolWindow(nint hWnd)
    {
        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    /// <summary>Makes <paramref name="window"/> owned by <paramref name="owner"/>: Windows then always keeps it directly
    /// above its owner, hides it with it and brings it along when the owner comes to the front.</summary>
    public static void SetOwner(nint window, nint owner) => SetWindowLongPtr(window, -8 /* GWLP_HWNDPARENT */, owner);

    /// <summary>Puts <paramref name="window"/> directly above <paramref name="anchor"/> in the z-order.</summary>
    public static void KeepJustAbove(nint window, nint anchor)
    {
        var prev = GetWindow(anchor, GW_HWNDPREV);
        if (prev == window) return;
        SetWindowPos(window, prev, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
