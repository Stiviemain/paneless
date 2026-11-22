using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paneless.App.Platform;

/// <summary>Zero-polling Windows clipboard listener (AddClipboardFormatListener on a message-only window)
/// plus direct CF_UNICODETEXT read/write helpers. No-op on non-Windows platforms.</summary>
public sealed class ClipboardWatcher : IDisposable
{
    readonly object _gate = new();
    WinClipboard? _win;
    bool _disposed;

    public event Action<string>? TextChanged;

    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_gate)
        {
            if (_disposed || _win != null) return;
            _win = new WinClipboard(text => TextChanged?.Invoke(text));
            _win.Start();
        }
    }

    public static string? GetText() => OperatingSystem.IsWindows() ? WinClipboard.ReadUnicodeText() : null;

    public static bool SetText(string text) => OperatingSystem.IsWindows() && WinClipboard.WriteUnicodeText(text);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            if (OperatingSystem.IsWindows()) _win?.Dispose();
            _win = null;
        }
    }
}

[SupportedOSPlatform("windows")]
sealed class WinClipboard(Action<string> onChanged) : IDisposable
{
    delegate nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint DefWindowProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostMessage(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint hMem);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint hMem);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint hMem);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);

    const uint WM_CLIPBOARDUPDATE = 0x031D, WM_CLOSE = 0x0010, CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x0002;
    const int MAX_CHARS = 256 * 1024;

    nint _hwnd;
    Thread? _thread;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Paneless clipboard watcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    void Run()
    {
        WndProc proc = HandleMessage;
        var cls = "PanelessClipboard_" + Guid.NewGuid().ToString("N");
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = cls,
        };
        if (RegisterClassEx(ref wc) == 0) return;
        _hwnd = CreateWindowEx(0, cls, "Paneless clipboard", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, wc.hInstance, 0);
        if (_hwnd == 0) return;
        AddClipboardFormatListener(_hwnd);
        try
        {
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.message == WM_CLOSE) break;
                DefWindowProc(msg.hwnd, msg.message, msg.wParam, msg.lParam);
            }
        }
        finally
        {
            RemoveClipboardFormatListener(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = 0;
            GC.KeepAlive(proc);
        }
    }

    nint HandleMessage(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            var text = ReadUnicodeText();
            if (!string.IsNullOrEmpty(text) && text.Length <= MAX_CHARS)
                try { onChanged(text); } catch { }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public static string? ReadUnicodeText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        for (var i = 0; i < 4; i++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    var h = GetClipboardData(CF_UNICODETEXT);
                    if (h == 0) return null;
                    var ptr = GlobalLock(h);
                    if (ptr == 0) return null;
                    try { return Marshal.PtrToStringUni(ptr); }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(25);
        }
        return null;
    }

    public static bool WriteUnicodeText(string text)
    {
        for (var i = 0; i < 4; i++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = (nuint)((text.Length + 1) * 2);
                    var h = GlobalAlloc(GMEM_MOVEABLE, bytes);
                    if (h == 0) return false;
                    var ptr = GlobalLock(h);
                    if (ptr == 0) { GlobalFree(h); return false; }
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                        Marshal.WriteInt16(ptr, text.Length * 2, 0);
                    }
                    finally { GlobalUnlock(h); }
                    if (SetClipboardData(CF_UNICODETEXT, h) == 0) { GlobalFree(h); return false; }
                    return true;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(25);
        }
        return false;
    }

    public void Dispose()
    {
        var h = _hwnd;
        if (h != 0) PostMessage(h, WM_CLOSE, 0, 0);
    }
}
