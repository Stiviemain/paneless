using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paneless.App.Platform;

/// <summary>Windows shortcuts (.lnk) and taskbar identity for remote app windows. Built-in COM interop is off in this
/// app, so the few COM calls needed go straight through the interfaces' function tables.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ShellLinks
{
    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);
    [DllImport("ole32.dll")] static extern int PropVariantClear(PropVariant* pv);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(nint hwnd, Guid* iid, nint* store);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    static readonly Guid IidPersistFile = new("0000010b-0000-0000-C000-000000000046");
    static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    const uint RelaunchCommand = 2, RelaunchIconResource = 3, RelaunchDisplayName = 4, AppId = 5;

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Fmt; public uint Pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort Vt; [FieldOffset(8)] public nint Ptr; }

    static void** Table(nint p) => *(void***)p;
    static void Release(nint p) { if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Table(p)[2])(p); }
    static nint Query(nint p, Guid iid)
    {
        nint result = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Table(p)[0])(p, &iid, &result);
        return hr >= 0 ? result : 0;
    }
    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})"); }

    // IShellLinkW slots: SetDescription 7, SetWorkingDirectory 9, SetArguments 11, SetIconLocation 17, SetPath 20.
    static void SetText(nint link, int slot, string value, string what)
    {
        fixed (char* s = value) Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)Table(link)[slot])(link, s), what);
    }

    /// <summary>IPropertyStore::SetValue (slot 6) with a string.</summary>
    static void SetString(nint store, uint pid, string value)
    {
        var key = new PropertyKey { Fmt = AppUserModel, Pid = pid };
        var pv = new PropVariant { Vt = 31 /* VT_LPWSTR */, Ptr = Marshal.StringToCoTaskMemUni(value) };
        try { Check(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)Table(store)[6])(store, &key, &pv), "SetValue"); }
        finally { PropVariantClear(&pv); }
    }

    static void Commit(nint store) => ((delegate* unmanaged[Stdcall]<nint, int>)Table(store)[7])(store);

    /// <summary>Writes a shortcut file. Its app id matches the remote app's window, so a pinned taskbar button and the
    /// open window are one button.</summary>
    public static void CreateShortcut(string file, string target, string arguments, string iconFile, string description, string appId)
    {
        CoInitializeEx(0, 2 /* apartment threaded; fine if already set up */);
        nint link = 0, store = 0, persist = 0;
        try
        {
            var clsid = ClsidShellLink; var iid = IidShellLinkW;
            Check(CoCreateInstance(&clsid, 0, 1 /* in-process */, &iid, &link), "Creating the shortcut");
            SetText(link, 20, target, "SetPath");
            SetText(link, 11, arguments, "SetArguments");
            SetText(link, 9, Path.GetDirectoryName(target) ?? "", "SetWorkingDirectory");
            SetText(link, 7, description.Length > 250 ? description[..250] : description, "SetDescription");
            fixed (char* icon = iconFile) Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Table(link)[17])(link, icon, 0), "SetIconLocation");
            store = Query(link, IidPropertyStore);
            if (store != 0) { SetString(store, AppId, appId); Commit(store); }
            persist = Query(link, IidPersistFile);
            if (persist == 0) throw new InvalidOperationException("The shortcut can't be saved.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            fixed (char* path = file) Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Table(persist)[6])(persist, path, 1), "Saving the shortcut");
        }
        finally { Release(persist); Release(store); Release(link); }
    }

    /// <summary>Gives a remote app's window its own taskbar button (app id), and tells Windows how to start it again when
    /// that button is pinned. Best effort: false if Windows refused.</summary>
    public static bool SetWindowIdentity(nint hwnd, string appId, string relaunchCommand, string displayName, string iconFile)
    {
        nint store = 0;
        try
        {
            var iid = IidPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, &iid, &store) < 0 || store == 0) return false;
            SetString(store, RelaunchCommand, relaunchCommand);
            SetString(store, RelaunchDisplayName, displayName);
            SetString(store, RelaunchIconResource, iconFile + ",0");
            SetString(store, AppId, appId);
            Commit(store);
            return true;
        }
        catch { return false; }
        finally { Release(store); }
    }

    /// <summary>Sets a window's title-bar and taskbar icon from an .ico file (works for another program's window).</summary>
    public static void SetWindowIcon(nint hwnd, string iconFile)
    {
        var big = Math.Max(32, GetSystemMetrics(11 /* SM_CXICON */)) * 2;
        var small = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
        var hBig = LoadImage(0, iconFile, 1 /* icon */, big, big, 0x10 /* from file */);
        var hSmall = LoadImage(0, iconFile, 1, small, small, 0x10);
        if (hBig != 0) SendMessage(hwnd, 0x80 /* WM_SETICON */, 1, hBig);
        if (hSmall != 0) SendMessage(hwnd, 0x80, 0, hSmall);
        // The icons stay loaded while the window shows them; a handful of small icons is not worth tracking.
    }

    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string StartMenuFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Paneless Remote apps");
}
