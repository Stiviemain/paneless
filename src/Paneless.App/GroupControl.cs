using System.Runtime.Versioning;
using Paneless.App.Platform;
using Paneless.Core;

namespace Paneless.App;

/// <summary>Group control: what you do in one mirror window (taps, swipes, scrolling, typing, toolbar buttons)
/// is repeated on the other connected devices of its group. Positions are relative, so devices with different
/// screen sizes get the same spot. Windows only (it watches the mirror window with input hooks).</summary>
[SupportedOSPlatform("windows")]
public sealed class GroupControl : IAsyncDisposable
{
    sealed class Follower(DeviceRow row, DeviceControl control)
    {
        public DeviceRow Row { get; } = row;
        public DeviceControl Control { get; } = control;
        public (int W, int H) Size;
    }

    const long Pointer = 900;   // pointer id for the repeated finger (key mapping uses 100+)

    readonly AppServices _s;
    readonly nint _window;
    readonly Func<(int W, int H)> _leaderSize;
    readonly List<Follower> _followers;
    readonly CancellationTokenSource _cts = new();
    bool _shift;

    GroupControl(AppServices s, nint window, Func<(int, int)> leaderSize, List<Follower> followers)
    {
        _s = s; _window = window; _leaderSize = leaderSize; _followers = followers;
    }

    public IReadOnlyList<string> Names => _followers.Select(f => f.Row.Name).ToList();

    /// <summary>Connects to the other devices and starts repeating. Returns the devices that couldn't be reached too.</summary>
    public static async Task<(GroupControl? Control, List<string> Failed)> StartAsync(
        AppServices s, nint mirrorWindow, Func<(int, int)> leaderSize, IReadOnlyList<DeviceRow> others)
    {
        var failed = new List<string>();
        if (s.Adb == null || s.Tools == null || await ScrcpyServerLocator.FindAsync(s.Tools) is not { } server) return (null, ["scrcpy-server"]);
        var started = await Task.WhenAll(others.Select(async r =>
        {
            try
            {
                var serial = await s.Directory!.ConnectAsync(r);
                if (serial == null) { lock (failed) failed.Add(r.Name); return null; }
                var f = new Follower(r with { Serial = serial }, await DeviceControl.StartAsync(s.Adb, server, serial));
                if (await s.Adb.GetDisplaySizeAsync(serial) is { } size) f.Size = size;
                return f;
            }
            catch { lock (failed) failed.Add(r.Name); return null; }
        }));
        var followers = started.OfType<Follower>().ToList();
        if (followers.Count == 0) return (null, failed);
        var gc = new GroupControl(s, mirrorWindow, leaderSize, followers);
        MouseRouter.Watch(mirrorWindow, gc.OnMouse);
        KeymapRouter.Observe(mirrorWindow, gc.OnKey);
        _ = gc.WatchSizesAsync();
        return (gc, failed);
    }

    async Task WatchSizesAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(3000, _cts.Token); } catch { return; }
            foreach (var f in _followers)
                try { if (await _s.Adb!.GetDisplaySizeAsync(f.Row.Serial!) is { } size) f.Size = size; } catch { }
        }
    }

    /// <summary>Where the picture is inside the mirror window (scrcpy keeps the device's shape, with bars around it).</summary>
    (double X, double Y)? Relative(int sx, int sy)
    {
        if (Win32.ClientBounds(_window) is not { } c) return null;
        var (dw, dh) = _leaderSize();
        double cw = c.Right - c.Left, ch = c.Bottom - c.Top;
        if (dw <= 0 || dh <= 0 || cw <= 0 || ch <= 0) return null;
        var scale = Math.Min(cw / dw, ch / dh);
        double pw = dw * scale, ph = dh * scale;
        double left = c.Left + (cw - pw) / 2, top = c.Top + (ch - ph) / 2;
        return (Math.Clamp((sx - left) / pw, 0, 1), Math.Clamp((sy - top) / ph, 0, 1));
    }

    void OnMouse(MouseRouter.Event e)
    {
        if (Relative(e.X, e.Y) is not { } p) return;
        foreach (var f in _followers)
        {
            if (f.Size.W == 0) continue;
            int x = (int)(p.X * f.Size.W), y = (int)(p.Y * f.Size.H);
            _ = e.Kind switch
            {
                MouseRouter.Kind.Down => f.Control.TouchAsync(DeviceControl.ActionDown, Pointer, x, y, f.Size.W, f.Size.H),
                MouseRouter.Kind.Move => f.Control.TouchAsync(DeviceControl.ActionMove, Pointer, x, y, f.Size.W, f.Size.H),
                MouseRouter.Kind.Up => f.Control.TouchAsync(DeviceControl.ActionUp, Pointer, x, y, f.Size.W, f.Size.H),
                _ => f.Control.ScrollAsync(x, y, f.Size.W, f.Size.H, 0, e.Wheel / 120f),
            };
        }
    }

    void OnKey(int vk, bool down)
    {
        if (vk is 0xA0 or 0xA1 or 0x10) { _shift = down; return; }
        if (DeviceControl.AndroidKeyFor(vk) is not { } key) return;
        foreach (var f in _followers) _ = f.Control.KeyAsync(key, down ? DeviceControl.ActionDown : DeviceControl.ActionUp, _shift ? DeviceControl.MetaShift : 0);
    }

    /// <summary>A toolbar button (Back, Home, …) pressed on the leader.</summary>
    public Task PressKeyAsync(int keycode) => Task.WhenAll(_followers.Select(f => f.Control.PressKeyAsync(keycode)));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        MouseRouter.Unwatch(_window);
        KeymapRouter.StopObserving(_window);
        foreach (var f in _followers) { try { await f.Control.DisposeAsync(); } catch { } }
    }
}
