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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsZoomed(nint hWnd);
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
    [DllImport("user32.dll")] public static extern nint SetParent(nint hWndChild, nint hWndNewParent);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);
    [DllImport("user32.dll")] public static extern nint SetFocus(nint hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint bufferLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(nint handle);
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

    /// <summary>Resizes a top-level window so its client area matches <paramref name="targetClientW"/>×<paramref name="targetClientH"/>
    /// (scaled proportionally to fit within 88 % of the current monitor's work area) while keeping the window on screen.</summary>
    public static (int W, int H)? ResizeClientOnScreen(nint hWnd, int targetClientW, int targetClientH)
    {
        if (!IsWindow(hWnd)) return null;
        if (IsIconic(hWnd) || IsZoomed(hWnd)) ShowWindow(hWnd, 9 /* restore */);
        var work = WorkArea(hWnd);
        var vis = Bounds(hWnd);
        var cli = ClientBounds(hWnd);
        if (vis is not { } v || cli is not { } c) return null;

        int frameW = Math.Max(0, (v.Right - v.Left) - (c.Right - c.Left));
        int frameH = Math.Max(0, (v.Bottom - v.Top) - (c.Bottom - c.Top));
        int maxW = (int)((work.Right - work.Left - frameW) * 0.88);
        int maxH = (int)((work.Bottom - work.Top - frameH) * 0.88);
        var (cw, ch) = Paneless.Core.RemoteApp.FitToArea(targetClientW, targetClientH, maxW, maxH);
        int outerW = cw + frameW, outerH = ch + frameH;

        int x = Math.Clamp(v.Left, work.Left, Math.Max(work.Left, work.Right - outerW));
        int y = Math.Clamp(v.Top, work.Top, Math.Max(work.Top, work.Bottom - outerH));
        Place(hWnd, x, y, outerW, outerH);
        return (cw, ch);
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
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x00040000, WS_EX_WINDOWEDGE = 0x00000100, WS_EX_CLIENTEDGE = 0x00000200, WS_EX_DLGMODALFRAME = 0x00000001;
    const long WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    const long WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_SYSMENU = 0x00080000;

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

    /// <summary>Reparents an external window (such as scrcpy) as a borderless child inside <paramref name="parent"/>.</summary>
    public static void EmbedChild(nint child, nint parent, int x, int y, int width, int height)
    {
        var parentStyle = (long)GetWindowLongPtr(parent, GWL_STYLE);
        if ((parentStyle & WS_CLIPCHILDREN) == 0)
            SetWindowLongPtr(parent, GWL_STYLE, (nint)(parentStyle | WS_CLIPCHILDREN));

        var style = (long)GetWindowLongPtr(child, GWL_STYLE);
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
        SetWindowLongPtr(child, GWL_STYLE, (nint)style);

        var ex = (long)GetWindowLongPtr(child, GWL_EXSTYLE);
        ex &= ~(WS_EX_APPWINDOW | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME);
        SetWindowLongPtr(child, GWL_EXSTYLE, (nint)ex);

        SetParent(child, parent);
        SetWindowPos(child, 0, x, y, Math.Max(1, width), Math.Max(1, height),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
    }

    public static void ResizeChild(nint child, int x, int y, int width, int height) =>
        MoveWindow(child, x, y, Math.Max(1, width), Math.Max(1, height), true);

    /// <summary>Moves keyboard focus into an embedded cross-process child window.</summary>
    public static void FocusChild(nint child)
    {
        if (child == 0 || !IsWindow(child)) return;
        var currentThread = GetCurrentThreadId();
        var childThread = GetWindowThreadProcessId(child, out _);
        if (childThread == 0) return;
        var attached = childThread != currentThread && AttachThreadInput(currentThread, childThread, true);
        try { SetFocus(child); }
        finally { if (attached) AttachThreadInput(currentThread, childThread, false); }
    }

    /// <summary>Returns the Windows 8.3 space-free short path when available (helps external tools like RDM launch
    /// executables directly without wrapping quoted paths in cmd.exe). Falls back to <paramref name="path"/>.</summary>
    public static string GetShortPath(string path)
    {
        if (!path.Contains(' ')) return path;
        var buf = new char[1024];
        var len = GetShortPathNameW(path, buf, (uint)buf.Length);
        return len > 0 && len < buf.Length ? new string(buf, 0, (int)len) : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    /// <summary>Creates a Job Object configured to kill all assigned child processes automatically when this process exits
    /// or is force-killed (so closing or killing a host tab manager never leaves an orphaned scrcpy window behind).</summary>
    public static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return 0;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 /* JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE */ },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size))
            {
                CloseHandle(job);
                return 0;
            }
            return job;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static void AssignToJob(nint job, System.Diagnostics.Process process)
    {
        if (job == 0) return;
        try { AssignProcessToJobObject(job, process.Handle); }
        catch { /* process already exited or OS refused */ }
    }
}
