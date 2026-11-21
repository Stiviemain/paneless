using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paneless.App.Platform;

/// <summary>A Windows notification ("toast"), e.g. for alerts. Uses a short-lived notification-area entry,
/// which Windows 10/11 turn into a normal notification. Does nothing on macOS/Linux (alerts still show in Paneless).</summary>
public static class Toast
{
    public static void Show(string title, string text)
    {
        if (OperatingSystem.IsWindows()) WinToast.Show(title, text);
    }
}

[SupportedOSPlatform("windows")]
static class WinToast
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int ExtractIconEx(string file, int index, out nint large, out nint small, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hWnd);

    const int NIM_ADD = 0, NIM_DELETE = 2, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIIF_USER = 4, NIIF_LARGE_ICON = 0x20;
    static readonly object Gate = new();
    static int _nextId = 100;

    public static void Show(string title, string text)
    {
        var t = new Thread(() =>
        {
            try
            {
                var hwnd = CreateWindowEx(0, "STATIC", "Paneless notification", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, 0, 0);
                ExtractIconEx(Environment.ProcessPath ?? "", 0, out var large, out var small, 1);
                int id;
                lock (Gate) id = _nextId++;
                var data = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = hwnd, uID = id,
                    uFlags = NIF_ICON | NIF_TIP | NIF_INFO, hIcon = small, szTip = "Paneless",
                    szInfoTitle = Trim(title, 63), szInfo = Trim(text, 255), dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON,
                    hBalloonIcon = large,
                };
                Shell_NotifyIcon(NIM_ADD, ref data);
                Thread.Sleep(9000);
                Shell_NotifyIcon(NIM_DELETE, ref data);
                DestroyWindow(hwnd);
            }
            catch { /* a notification is a nicety */ }
        }) { IsBackground = true, Name = "Paneless notification" };
        t.Start();
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
