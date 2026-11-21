using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Paneless.Core;

/// <summary>The scrcpy-server file that runs on the device, and its version (they must match exactly).</summary>
public sealed record ScrcpyServerInfo(string Path, string Version);

public static partial class ScrcpyServerLocator
{
    public static async Task<ScrcpyServerInfo?> FindAsync(ToolPaths tools)
    {
        var dir = System.IO.Path.GetDirectoryName(tools.Scrcpy)!;
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SCRCPY_SERVER_PATH"),
            System.IO.Path.Combine(dir, "scrcpy-server"),                                  // Windows release zip
            System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, "..", "share", "scrcpy", "scrcpy-server")),  // Homebrew, /usr/local
            "/opt/homebrew/share/scrcpy/scrcpy-server",
            "/usr/local/share/scrcpy/scrcpy-server",
            "/usr/share/scrcpy/scrcpy-server",
        };
        var path = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (path == null) return null;
        var r = await ProcessRunner.RunAsync(tools.Scrcpy, ["--version"], TimeSpan.FromSeconds(10));
        var m = VersionRx().Match(r.Output);
        return m.Success ? new ScrcpyServerInfo(path, m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"scrcpy\s+v?(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionRx();
}

/// <summary>A fast, control-only connection to a device: Paneless runs its own copy of scrcpy's device-side
/// server (no video, no audio) next to the mirror and sends touches, keys and commands over a local socket.
/// Message format follows scrcpy 5.x (ControlMessageReader.java).</summary>
public sealed class DeviceControl : IAsyncDisposable
{
    const string DevicePath = "/data/local/tmp/paneless-server.jar";
    const byte TypeKeycode = 0, TypeInjectText = 1, TypeTouch = 2, TypeInjectScroll = 3, TypeBackOrScreenOn = 4, TypeExpandNotifications = 5,
               TypeExpandSettings = 6, TypeCollapsePanels = 7, TypeSetDisplayPower = 10, TypeRotateDevice = 11;

    public const int ActionDown = 0, ActionUp = 1, ActionMove = 2;

    // Android key codes used by the toolbar.
    public const int KeyHome = 3, KeyBack = 4, KeyVolumeUp = 24, KeyVolumeDown = 25, KeyPower = 26, KeyAppSwitch = 187;

    readonly AdbClient _adb;
    readonly string _serial;
    readonly Process _server;
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly int _port;
    readonly SemaphoreSlim _write = new(1, 1);
    readonly CancellationTokenSource _cts = new();

    DeviceControl(AdbClient adb, string serial, Process server, TcpClient tcp, int port)
    {
        _adb = adb; _serial = serial; _server = server; _tcp = tcp; _port = port;
        _stream = tcp.GetStream();
        _ = DrainAsync();
    }

    public bool IsConnected => _tcp.Connected && !_cts.IsCancellationRequested;

    /// <param name="displayId">Display that touches and keys go to: 0 = the device's own screen, otherwise a
    /// virtual display (second screen or app window).</param>
    public static async Task<DeviceControl> StartAsync(AdbClient adb, ScrcpyServerInfo server, string serial, int displayId = 0)
    {
        var scid = Random.Shared.Next(1, int.MaxValue);
        var socketName = $"scrcpy_{scid:x8}";

        var push = await adb.RunAsync(TimeSpan.FromSeconds(30), "-s", serial, "push", server.Path, DevicePath);
        if (!push.Ok) throw new InvalidOperationException("Couldn't copy the control helper to the device: " + push.LastLine);

        var fwd = await adb.RunAsync("-s", serial, "forward", "tcp:0", "localabstract:" + socketName);
        if (!fwd.Ok || !int.TryParse(fwd.Output.Trim().Split('\n')[0].Trim(), out var port))
            throw new InvalidOperationException("Couldn't open a connection to the device: " + fwd.LastLine);

        var args = $"CLASSPATH={DevicePath} app_process / com.genymobile.scrcpy.Server {server.Version} scid={scid:x8} " +
                   "log_level=warn video=false audio=false control=true tunnel_forward=true send_device_meta=false " +
                   "cleanup=false power_on=false clipboard_autosync=false" + (displayId != 0 ? $" display_id={displayId}" : "");
        var psi = new ProcessStartInfo(adb.AdbPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in new[] { "-s", serial, "shell", args }) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        var log = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // The server needs a moment to start listening; adb accepts the TCP connection right away but closes it
        // until then. The first byte (a "dummy byte") arrives once the server is really there.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (proc.HasExited) break;
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync("127.0.0.1", port);
                var buf = new byte[1];
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var n = await tcp.GetStream().ReadAsync(buf, t.Token);
                if (n == 1) return new DeviceControl(adb, serial, proc, tcp, port);
            }
            catch { /* not ready yet */ }
            tcp.Dispose();
            await Task.Delay(100);
        }

        try { proc.Kill(); } catch { }
        await adb.RunAsync("-s", serial, "forward", "--remove", $"tcp:{port}");
        string why; lock (log) why = log.ToString().Trim();
        throw new InvalidOperationException("The control helper didn't start on the device." + (why.Length > 0 ? " " + why.Split('\n').Last() : ""));
    }

    async Task DrainAsync()
    {
        // The device can send messages back (clipboard, acknowledgements); read and ignore them so the socket never fills.
        var buf = new byte[4096];
        try { while (!_cts.IsCancellationRequested && await _stream.ReadAsync(buf, _cts.Token) > 0) { } }
        catch { }
        _cts.Cancel();
    }

    async Task SendAsync(byte[] msg)
    {
        await _write.WaitAsync();
        try { await _stream.WriteAsync(msg); }
        finally { _write.Release(); }
    }

    public Task KeyAsync(int keycode, int action, int metaState = 0)
    {
        var b = new byte[14];
        b[0] = TypeKeycode; b[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(2), keycode);
        // repeat = 0
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(10), metaState);
        return SendAsync(b);
    }

    public const int MetaShift = 0x41;   // META_SHIFT_ON | META_SHIFT_LEFT_ON

    /// <summary>Android key code for a Windows virtual-key code (letters, digits, punctuation, editing and arrow keys).</summary>
    public static int? AndroidKeyFor(int vk) => vk switch
    {
        >= 'A' and <= 'Z' => 29 + (vk - 'A'),
        >= '0' and <= '9' => 7 + (vk - '0'),
        >= 0x60 and <= 0x69 => 144 + (vk - 0x60),   // numpad 0-9
        0x20 => 62, 0x0D => 66, 0x08 => 67, 0x09 => 61, 0x1B => 111, 0x2E => 112,
        0x25 => 21, 0x26 => 19, 0x27 => 22, 0x28 => 20, 0x24 => 122, 0x23 => 123, 0x21 => 92, 0x22 => 93,
        0xBC => 55, 0xBE => 56, 0xBD => 69, 0xBB => 70, 0xBF => 76, 0xBA => 74, 0xDE => 75, 0xDB => 71, 0xDD => 72, 0xDC => 73, 0xC0 => 68,
        _ => null,
    };

    public async Task PressKeyAsync(int keycode)
    {
        await KeyAsync(keycode, ActionDown);
        await KeyAsync(keycode, ActionUp);
    }

    /// <summary>A finger event in device screen pixels (current orientation).</summary>
    public Task TouchAsync(int action, long pointerId, int x, int y, int screenW, int screenH)
    {
        var b = new byte[32];
        b[0] = TypeTouch; b[1] = (byte)action;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(2), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(10), x);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(14), y);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), (ushort)Math.Clamp(screenW, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(20), (ushort)Math.Clamp(screenH, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), action == ActionUp ? (ushort)0 : (ushort)0xffff);   // pressure
        // action button = 0, buttons = 0
        return SendAsync(b);
    }

    /// <summary>Types text as if from a keyboard (works where pasting is blocked). Sent in pieces of up to 300 bytes.</summary>
    public async Task TypeTextAsync(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
        for (var i = 0; i < bytes.Length;)
        {
            var n = Math.Min(300, bytes.Length - i);
            while (n < bytes.Length - i && n > 0 && (bytes[i + n] & 0xC0) == 0x80) n--;   // don't split a character
            var b = new byte[5 + n];
            b[0] = TypeInjectText;
            BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), n);
            Array.Copy(bytes, i, b, 5, n);
            await SendAsync(b);
            i += n;
        }
    }

    /// <summary>Scrolls at a point (device pixels); positive v scrolls up, like a mouse wheel.</summary>
    public Task ScrollAsync(int x, int y, int screenW, int screenH, float h, float v)
    {
        var b = new byte[21];
        b[0] = TypeInjectScroll;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), x);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(5), y);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(9), (ushort)Math.Clamp(screenW, 0, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(11), (ushort)Math.Clamp(screenH, 0, 65535));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(13), ToI16(h));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(15), ToI16(v));
        // buttons = 0
        return SendAsync(b);
        static short ToI16(float f) => (short)Math.Clamp(Math.Round(Math.Clamp(f / 16f, -1f, 1f) * 0x7fff), short.MinValue, short.MaxValue);
    }

    /// <summary>Turns the device between portrait and landscape (like scrcpy's own rotate shortcut).</summary>
    public Task RotateAsync() => SendAsync([TypeRotateDevice]);
    /// <summary>Screen off (or back on) while mirroring continues.</summary>
    public Task SetDisplayPowerAsync(bool on) => SendAsync([TypeSetDisplayPower, (byte)(on ? 1 : 0)]);
    public Task ExpandNotificationsAsync() => SendAsync([TypeExpandNotifications]);
    public Task ExpandQuickSettingsAsync() => SendAsync([TypeExpandSettings]);
    public Task CollapsePanelsAsync() => SendAsync([TypeCollapsePanels]);
    /// <summary>Back if the screen is on, otherwise turn it on.</summary>
    public async Task BackOrScreenOnAsync()
    {
        await SendAsync([TypeBackOrScreenOn, ActionDown]);
        await SendAsync([TypeBackOrScreenOn, ActionUp]);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _tcp.Dispose(); } catch { }
        try { if (!_server.HasExited) _server.Kill(); } catch { }
        try { await _adb.RunAsync("-s", _serial, "forward", "--remove", $"tcp:{_port}"); } catch { }
    }
}
