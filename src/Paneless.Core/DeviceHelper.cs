using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Paneless.Core;

/// <summary>One framed message over the helper daemon stream.</summary>
public sealed record HelperFrame(JsonObject Header, byte[] Binary);

/// <summary>Result of one RPC call to the on-device helper daemon.</summary>
public sealed record HelperResponse(bool Ok, JsonObject Data, byte[] Binary, string? Error);

/// <summary>One file or folder returned by the on-device helper's files_ls command.</summary>
public sealed record DeviceFileEntry(string Name, string Path, bool IsDir, long Size, DateTime Modified, string Kind);

/// <summary>One running app task on a device display.</summary>
public sealed record DeviceTask(int TaskId, int DisplayId, string Package, string Activity, string Label, bool Visible);

/// <summary>Result of a fast on-device screen capture (downscaled JPEG).</summary>
public sealed record CaptureResult(bool ScreenOff, byte[]? Jpeg, int SourceWidth, int SourceHeight);

/// <summary>Rich package metadata from the on-device helper's apps_detail command.</summary>
public sealed record DetailedAppPackage(string Package, string Label, string VersionName, long VersionCode, bool Disabled, bool IsSystem, long ApkSizeBytes);

/// <summary>Snapshot of typed system toggles &amp; volumes from sys_get.</summary>
public sealed record DeviceControlsState(bool DarkMode, int Brightness, bool AutoBrightness, bool AutoRotate, bool Dnd, bool Wifi, bool Bluetooth,
    int MusicVolume, int MusicMax, int RingVolume, int RingMax);

/// <summary>Currently playing media session info from media_state.</summary>
public sealed record MediaState(bool Playing, string Title, string Artist, string Album, string Package);

/// <summary>Extracts 4-8 digit 2FA/OTP codes from notification text when accompanied by verification keywords.</summary>
public static partial class OtpExtractor
{
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!KeywordRx().IsMatch(text)) return null;
        var m = DigitsRx().Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(code|otp|pin|passcode|token|verify|verification|confirm|confirmation|2fa|two[- ]factor|login|sign[- ]?in|security|sicherheitscode|bestätigungscode|einmalcode|prüfcode|vérification|authentification)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex KeywordRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\d)(\d{4,8})(?!\d)")]
    private static partial System.Text.RegularExpressions.Regex DigitsRx();
}

/// <summary>Unsolicited push event emitted by the on-device helper daemon.</summary>
public sealed record HelperEvent(string Serial, string Name, JsonObject Data, byte[] Binary);

/// <summary>Length-prefixed JSON + optional binary payload framing matching FrameChannel.java.</summary>
public static class HelperProtocol
{
    public const int ProtocolVersion = 2;
    public const int MaxHeaderBytes = 16 * 1024 * 1024;
    public const int MaxBinaryBytes = 64 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, JsonObject header, byte[]? binary, CancellationToken ct)
    {
        var headerBytes = Encoding.UTF8.GetBytes(header.ToJsonString());
        var bin = binary ?? [];
        var lenBuf = new byte[4];

        BinaryPrimitives.WriteInt32BigEndian(lenBuf, headerBytes.Length);
        await stream.WriteAsync(lenBuf, ct);
        await stream.WriteAsync(headerBytes, ct);

        BinaryPrimitives.WriteInt32BigEndian(lenBuf, bin.Length);
        await stream.WriteAsync(lenBuf, ct);
        if (bin.Length > 0) await stream.WriteAsync(bin, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<HelperFrame?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactOrEofAsync(stream, lenBuf, ct)) return null;
        var headerLen = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (headerLen <= 0 || headerLen > MaxHeaderBytes)
            throw new IOException($"Invalid helper frame header length: {headerLen}");

        var headerBytes = new byte[headerLen];
        await ReadExactAsync(stream, headerBytes, ct);

        await ReadExactAsync(stream, lenBuf, ct);
        var binLen = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (binLen < 0 || binLen > MaxBinaryBytes)
            throw new IOException($"Invalid helper frame binary length: {binLen}");

        var bin = binLen == 0 ? [] : new byte[binLen];
        if (binLen > 0) await ReadExactAsync(stream, bin, ct);

        var node = JsonNode.Parse(headerBytes) as JsonObject
                   ?? throw new IOException("Helper frame header was not a JSON object.");
        return new HelperFrame(node, bin);
    }

    static async Task<bool> ReadExactOrEofAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(offset), ct);
            if (n == 0)
            {
                if (offset == 0) return false;
                throw new EndOfStreamException("Unexpected EOF mid-frame.");
            }
            offset += n;
        }
        return true;
    }

    static async Task ReadExactAsync(Stream stream, byte[] buf, CancellationToken ct)
    {
        if (!await ReadExactOrEofAsync(stream, buf, ct))
            throw new EndOfStreamException("Unexpected EOF while reading helper frame.");
    }
}

/// <summary>Persistent connection to Paneless' on-device helper daemon (paneless-helper.jar).
/// Multiplexes typed JSON+binary RPCs and unsolicited push events over one adb-forwarded socket,
/// holds no wakelocks, sends no keepalives, and exits cleanly when disconnected.</summary>
public sealed class DeviceHelperClient : IAsyncDisposable
{
    readonly AdbClient? _adb;
    readonly Process? _proc;
    readonly TcpClient? _tcp;
    readonly Stream _stream;
    readonly int _port;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<HelperResponse>> _pending = new();
    readonly CancellationTokenSource _cts = new();
    long _nextId;
    int _disposed;

    DeviceHelperClient(string serial, AdbClient? adb, Process? proc, TcpClient? tcp, Stream stream, int port)
    {
        Serial = serial;
        _adb = adb;
        _proc = proc;
        _tcp = tcp;
        _stream = stream;
        _port = port;
        LastUsed = DateTime.UtcNow;
        _ = ReadLoopAsync();
    }

    public string Serial { get; }
    public int ProtocolVersion { get; private set; }
    public int DeviceSdk { get; private set; }
    public IReadOnlySet<string> Commands { get; private set; } = new HashSet<string>();
    public DateTime LastUsed { get; private set; }
    public bool IsConnected => _disposed == 0 && !_cts.IsCancellationRequested && (_tcp == null || _tcp.Connected);

    public event Action<HelperEvent>? EventReceived;
    public event Action? Disconnected;

    /// <summary>Starts the helper daemon on <paramref name="serial"/> and completes the ping handshake.</summary>
    public static async Task<DeviceHelperClient> StartAsync(AdbClient adb, string serial, CancellationToken ct = default)
    {
        if (!await adb.PushHelperAsync(serial))
            throw new InvalidOperationException(Text.T("Couldn't copy the helper to the device."));

        var scid = Random.Shared.Next(1, int.MaxValue);
        var socketName = $"paneless_{scid:x8}";
        var fwd = await adb.RunAsync("-s", serial, "forward", "tcp:0", "localabstract:" + socketName);
        if (!fwd.Ok || !int.TryParse(fwd.Output.Trim().Split('\n')[0].Trim(), out var port))
            throw new InvalidOperationException(Text.T("Couldn't open a helper connection to the device: ") + fwd.LastLine);

        var cmd = $"CLASSPATH={AdbClient.HelperRemote} app_process / app.paneless.helper.Main daemon {socketName}";
        var psi = new ProcessStartInfo(adb.AdbPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in new[] { "-s", serial, "shell", cmd }) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start adb for helper daemon.");

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith("READY ", StringComparison.Ordinal)) ready.TrySetResult();
            lock (log) log.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Wait briefly for the daemon's READY line (usually ~350-600 ms for ART boot), then connect over TCP.
        var waitReady = Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(6), ct));
        await waitReady;

        TcpClient? tcp = null;
        for (var attempt = 0; attempt < 30 && !ct.IsCancellationRequested; attempt++)
        {
            if (proc.HasExited) break;
            var candidate = new TcpClient { NoDelay = true };
            try
            {
                await candidate.ConnectAsync("127.0.0.1", port, ct);
                tcp = candidate;
                break;
            }
            catch
            {
                candidate.Dispose();
                await Task.Delay(100, ct);
            }
        }

        if (tcp == null)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
            _ = adb.RunAsync("-s", serial, "forward", "--remove", $"tcp:{port}");
            string why;
            lock (log) why = log.ToString().Trim();
            throw new InvalidOperationException("Helper daemon didn't start." + (why.Length > 0 ? " " + why.Split('\n').Last() : ""));
        }

        var client = new DeviceHelperClient(serial, adb, proc, tcp, tcp.GetStream(), port);
        try
        {
            await client.HandshakeAsync(ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates a client over an existing stream (used by offline smoke tests).</summary>
    public static async Task<DeviceHelperClient> FromStreamAsync(string serial, Stream stream, CancellationToken ct = default)
    {
        var client = new DeviceHelperClient(serial, null, null, null, stream, 0);
        try
        {
            await client.HandshakeAsync(ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    async Task HandshakeAsync(CancellationToken ct)
    {
        var ping = await CallAsync("ping", timeout: TimeSpan.FromSeconds(8), ct: ct);
        if (!ping.Ok) throw new InvalidOperationException(ping.Error ?? "Helper ping failed.");
        ProtocolVersion = ping.Data["version"]?.GetValue<int>() ?? 0;
        DeviceSdk = ping.Data["sdk"]?.GetValue<int>() ?? 0;
        var cmds = new HashSet<string>(StringComparer.Ordinal);
        if (ping.Data["commands"] is JsonArray arr)
            foreach (var n in arr)
                if (n?.ToString() is { Length: > 0 } c) cmds.Add(c);
        Commands = cmds;
    }

    public bool Supports(string cmd) => Commands.Contains(cmd);

    /// <summary>Sends one RPC request and awaits the correlated response.</summary>
    public async Task<HelperResponse> CallAsync(string cmd, JsonObject? args = null, byte[]? binary = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsConnected) throw new IOException("Helper daemon is disconnected.");
        var id = Interlocked.Increment(ref _nextId);
        var hdr = args != null ? (JsonObject)args.DeepClone() : new JsonObject();
        hdr["id"] = id;
        hdr["cmd"] = cmd;

        var tcs = new TaskCompletionSource<HelperResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        LastUsed = DateTime.UtcNow;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        using var reg = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var p))
                p.TrySetException(ct.IsCancellationRequested
                    ? new OperationCanceledException(ct)
                    : new TimeoutException($"Helper command '{cmd}' timed out."));
        });

        try
        {
            await _writeLock.WaitAsync(linked.Token);
            try { await HelperProtocol.WriteFrameAsync(_stream, hdr, binary, linked.Token); }
            finally { _writeLock.Release(); }
            return await tcs.Task;
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    // ---------------------------------------------------------------- typed helpers (Step 0: apps + icons)

    public async Task<List<LauncherApp>?> GetLauncherAppsAsync(CancellationToken ct = default)
    {
        var res = await CallAsync("apps", timeout: TimeSpan.FromSeconds(20), ct: ct);
        if (!res.Ok || res.Data["apps"] is not JsonArray arr) return null;
        var list = new List<LauncherApp>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject obj) continue;
            var pkg = obj["package"]?.ToString() ?? "";
            var name = obj["name"]?.ToString() ?? "";
            if (pkg.Length > 0) list.Add(new LauncherApp(pkg, name.Length > 0 ? name : pkg));
        }
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Dictionary<string, (string Name, byte[] Png)>> GetAppIconsAsync(int size, IEnumerable<string> packages, CancellationToken ct = default)
    {
        var result = new Dictionary<string, (string, byte[])>();
        var list = packages.Where(p => p.Length > 0).Distinct().ToList();
        if (list.Count == 0) return result;
        foreach (var chunk in list.Chunk(60))
        {
            var args = new JsonObject
            {
                ["size"] = size,
                ["packages"] = new JsonArray(chunk.Select(p => (JsonNode)p).ToArray()),
            };
            var res = await CallAsync("icons", args, timeout: TimeSpan.FromSeconds(45), ct: ct);
            if (!res.Ok || res.Data["icons"] is not JsonArray arr) continue;
            foreach (var node in arr)
            {
                if (node is not JsonObject obj) continue;
                var pkg = obj["package"]?.ToString() ?? "";
                var name = obj["name"]?.ToString() ?? "";
                var b64 = obj["png"]?.ToString() ?? "";
                if (pkg.Length == 0 || b64.Length == 0) continue;
                try
                {
                    var png = Convert.FromBase64String(b64);
                    if (png.Length > 8 && png[0] == 0x89 && png[1] == (byte)'P') result[pkg] = (name, png);
                }
                catch (FormatException) { }
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- typed helpers (Step 1: UiAutomation)

    public Task<HelperResponse> GetUiTreeAsync(int displayId = 0, bool interactiveOnly = false, int maxNodes = 300, CancellationToken ct = default) =>
        CallAsync("ui_tree", new JsonObject
        {
            ["displayId"] = displayId,
            ["interactiveOnly"] = interactiveOnly,
            ["maxNodes"] = maxNodes,
        }, timeout: TimeSpan.FromSeconds(15), ct: ct);

    public Task<HelperResponse> PerformUiActionAsync(string action, int displayId = 0, int nodeId = 0,
        string? text = null, string? desc = null, string? resId = null, int index = 0, string? value = null, CancellationToken ct = default)
    {
        var args = new JsonObject
        {
            ["action"] = action,
            ["displayId"] = displayId,
            ["index"] = index,
        };
        if (nodeId > 0) args["nodeId"] = nodeId;
        if (!string.IsNullOrEmpty(text)) args["text"] = text;
        if (!string.IsNullOrEmpty(desc)) args["desc"] = desc;
        if (!string.IsNullOrEmpty(resId)) args["resId"] = resId;
        if (value != null) args["value"] = value;
        return CallAsync("ui_action", args, timeout: TimeSpan.FromSeconds(15), ct: ct);
    }

    // ---------------------------------------------------------------- typed helpers (Step 2: Clipboard)

    public async Task<string?> GetClipboardAsync(CancellationToken ct = default)
    {
        var res = await CallAsync("clipboard_get", timeout: TimeSpan.FromSeconds(8), ct: ct);
        return res.Ok ? res.Data["text"]?.ToString() ?? "" : null;
    }

    public async Task<bool> SetClipboardAsync(string text, CancellationToken ct = default)
    {
        var res = await CallAsync("clipboard_set", new JsonObject { ["text"] = text }, timeout: TimeSpan.FromSeconds(8), ct: ct);
        return res.Ok;
    }

    public async Task<bool> WatchClipboardAsync(bool enabled, CancellationToken ct = default)
    {
        var res = await CallAsync("clipboard_watch", new JsonObject { ["enabled"] = enabled }, timeout: TimeSpan.FromSeconds(8), ct: ct);
        return res.Ok && res.Data["watching"]?.GetValue<bool>() == enabled;
    }

    // ---------------------------------------------------------------- typed helpers (Step 3: Files & media)

    public async Task<List<DeviceFileEntry>?> ListFilesAsync(string path, string sort = "name", bool includeHidden = false, CancellationToken ct = default)
    {
        var res = await CallAsync("files_ls", new JsonObject
        {
            ["path"] = path,
            ["sort"] = sort,
            ["includeHidden"] = includeHidden,
        }, timeout: TimeSpan.FromSeconds(15), ct: ct);
        if (!res.Ok || res.Data["entries"] is not JsonArray arr) return null;
        var list = new List<DeviceFileEntry>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var name = o["name"]?.ToString() ?? "";
            var full = o["path"]?.ToString() ?? "";
            var isDir = o["isDir"]?.GetValue<bool>() == true;
            var size = o["size"]?.GetValue<long>() ?? 0;
            var ms = o["modifiedMs"]?.GetValue<long>() ?? 0;
            var kind = o["kind"]?.ToString() ?? (isDir ? "dir" : "file");
            var mod = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : DateTime.MinValue;
            list.Add(new DeviceFileEntry(name, full, isDir, size, mod, kind));
        }
        return list;
    }

    public async Task<byte[]?> GetThumbnailAsync(string remotePath, int maxSize = 240, CancellationToken ct = default)
    {
        var res = await CallAsync("files_thumb", new JsonObject
        {
            ["path"] = remotePath,
            ["maxSize"] = maxSize,
        }, timeout: TimeSpan.FromSeconds(15), ct: ct);
        return res.Ok && res.Binary.Length > 0 ? res.Binary : null;
    }

    public async Task<bool> DownloadFileAsync(string remotePath, string localPath, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        const int chunk = 1024 * 1024;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
        var tmp = localPath + ".part";
        try
        {
            long offset = 0;
            await using (var fs = File.Create(tmp))
            {
                while (true)
                {
                    var res = await CallAsync("files_read", new JsonObject
                    {
                        ["path"] = remotePath,
                        ["offset"] = offset,
                        ["length"] = chunk,
                    }, timeout: TimeSpan.FromSeconds(30), ct: ct);
                    if (!res.Ok) return false;
                    var total = res.Data["totalSize"]?.GetValue<long>() ?? 0;
                    if (res.Binary.Length > 0)
                    {
                        await fs.WriteAsync(res.Binary, ct);
                        offset += res.Binary.Length;
                    }
                    progress?.Invoke(offset, total);
                    if (res.Data["eof"]?.GetValue<bool>() == true || res.Binary.Length == 0) break;
                }
            }
            File.Move(tmp, localPath, overwrite: true);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    public async Task<bool> UploadFileAsync(string localPath, string remotePath, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        const int chunk = 1024 * 1024;
        var fi = new FileInfo(localPath);
        if (!fi.Exists) return false;
        var buf = new byte[chunk];
        long offset = 0;
        await using var fs = File.OpenRead(localPath);
        do
        {
            var n = await fs.ReadAsync(buf, ct);
            var slice = n == buf.Length ? buf : buf[..n];
            var res = await CallAsync("files_write", new JsonObject
            {
                ["path"] = remotePath,
                ["offset"] = offset,
                ["truncate"] = offset == 0,
            }, slice, timeout: TimeSpan.FromSeconds(30), ct: ct);
            if (!res.Ok) return false;
            offset += n;
            progress?.Invoke(offset, fi.Length);
        } while (offset < fi.Length);
        return true;
    }

    public async Task<bool> DeleteFileAsync(string remotePath, CancellationToken ct = default) =>
        (await CallAsync("files_delete", new JsonObject { ["path"] = remotePath }, timeout: TimeSpan.FromSeconds(15), ct: ct)).Ok;

    public async Task<bool> MakeDirAsync(string remotePath, CancellationToken ct = default) =>
        (await CallAsync("files_mkdir", new JsonObject { ["path"] = remotePath }, timeout: TimeSpan.FromSeconds(10), ct: ct)).Ok;

    public async Task<bool> WatchMediaAsync(bool enabled, CancellationToken ct = default) =>
        (await CallAsync("media_watch", new JsonObject { ["enabled"] = enabled }, timeout: TimeSpan.FromSeconds(10), ct: ct)).Ok;

    // ---------------------------------------------------------------- typed helpers (Step 4: Tasks & handoff)

    public async Task<List<DeviceTask>?> ListTasksAsync(int maxNum = 25, CancellationToken ct = default)
    {
        var res = await CallAsync("tasks_list", new JsonObject { ["maxNum"] = maxNum }, timeout: TimeSpan.FromSeconds(10), ct: ct);
        if (!res.Ok || res.Data["tasks"] is not JsonArray arr) return null;
        var list = new List<DeviceTask>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            list.Add(new DeviceTask(
                o["taskId"]?.GetValue<int>() ?? -1,
                o["displayId"]?.GetValue<int>() ?? 0,
                o["package"]?.ToString() ?? "",
                o["activity"]?.ToString() ?? "",
                o["label"]?.ToString() ?? "",
                o["visible"]?.GetValue<bool>() == true));
        }
        return list;
    }

    public async Task<bool> MoveTaskToDisplayAsync(int taskId, int displayId, CancellationToken ct = default) =>
        (await CallAsync("task_move", new JsonObject { ["taskId"] = taskId, ["displayId"] = displayId }, timeout: TimeSpan.FromSeconds(10), ct: ct)).Ok;

    public Task<HelperResponse> LaunchIntentAsync(string? package = null, string? uri = null, int displayId = 0, CancellationToken ct = default)
    {
        var args = new JsonObject { ["displayId"] = displayId };
        if (!string.IsNullOrEmpty(package)) args["package"] = package;
        if (!string.IsNullOrEmpty(uri)) args["uri"] = uri;
        return CallAsync("launch_intent", args, timeout: TimeSpan.FromSeconds(10), ct: ct);
    }

    // ---------------------------------------------------------------- typed helpers (Step 5: Screen & Wall capture)

    public async Task<CaptureResult?> CaptureJpegAsync(int maxWidth = 480, int displayId = 0, int quality = 78, CancellationToken ct = default)
    {
        var res = await CallAsync("capture", new JsonObject
        {
            ["displayId"] = displayId,
            ["maxWidth"] = maxWidth,
            ["quality"] = quality,
        }, timeout: TimeSpan.FromSeconds(12), ct: ct);
        if (!res.Ok) return null;
        if (res.Data["screenOff"]?.GetValue<bool>() == true) return new CaptureResult(true, null, 0, 0);
        return res.Binary.Length > 0
            ? new CaptureResult(false, res.Binary, res.Data["sourceWidth"]?.GetValue<int>() ?? 0, res.Data["sourceHeight"]?.GetValue<int>() ?? 0)
            : null;
    }

    public async Task<bool?> IsScreenOnAsync(CancellationToken ct = default)
    {
        var res = await CallAsync("screen_state", timeout: TimeSpan.FromSeconds(6), ct: ct);
        return res.Ok ? res.Data["on"]?.GetValue<bool>() : null;
    }

    public async Task<bool> WatchDisplayAsync(bool enabled, CancellationToken ct = default) =>
        (await CallAsync("display_watch", new JsonObject { ["enabled"] = enabled }, timeout: TimeSpan.FromSeconds(6), ct: ct)).Ok;

    // ---------------------------------------------------------------- typed helpers (Step 6: Telemetry & rich app details)

    public async Task<DeviceInventory?> GetInventoryAsync(string name, CancellationToken ct = default)
    {
        var res = await CallAsync("inventory", timeout: TimeSpan.FromSeconds(10), ct: ct);
        if (!res.Ok) return null;
        var d = res.Data;
        var sn = d["serial"]?.ToString() is { Length: > 0 } s ? s : Serial;
        var healthCode = d["batteryHealthCode"]?.GetValue<int>() ?? 0;
        var health = healthCode > 0 ? AssistantFacts.HealthName(healthCode.ToString()) : "";
        int? level = d["batteryLevel"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? l : null;
        double? temp = d["batteryTempC"] is JsonValue tv && tv.TryGetValue<double>(out var tc) ? tc : null;
        long? sTotal = d["storageTotalMb"] is JsonValue stv && stv.TryGetValue<long>(out var st) ? st : null;
        long? sFree = d["storageFreeMb"] is JsonValue sfv && sfv.TryGetValue<long>(out var sf) ? sf : null;
        long? ram = d["ramTotalMb"] is JsonValue rv && rv.TryGetValue<long>(out var rmb) ? rmb : null;
        TimeSpan? up = d["uptimeSeconds"] is JsonValue uv && uv.TryGetValue<long>(out var us) ? TimeSpan.FromSeconds(us) : null;
        return new DeviceInventory(
            name, sn,
            d["manufacturer"]?.ToString() ?? "",
            d["model"]?.ToString() ?? "",
            d["androidVersion"]?.ToString() ?? "",
            d["sdk"]?.GetValue<int>() ?? 0,
            d["securityPatch"]?.ToString() ?? "",
            level,
            d["charging"]?.GetValue<bool>() == true,
            health,
            temp,
            sTotal, sFree, ram,
            d["screen"]?.ToString() ?? "",
            d["ipAddress"]?.ToString() ?? "",
            up,
            DateTime.Now);
    }

    public async Task<List<DetailedAppPackage>?> GetDetailedAppsAsync(bool includeSystem = false, CancellationToken ct = default)
    {
        var res = await CallAsync("apps_detail", new JsonObject { ["includeSystem"] = includeSystem }, timeout: TimeSpan.FromSeconds(20), ct: ct);
        if (!res.Ok || res.Data["apps"] is not JsonArray arr) return null;
        var list = new List<DetailedAppPackage>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var pkg = o["package"]?.ToString() ?? "";
            if (pkg.Length == 0) continue;
            list.Add(new DetailedAppPackage(
                pkg,
                o["label"]?.ToString() ?? pkg,
                o["versionName"]?.ToString() ?? "",
                o["versionCode"]?.GetValue<long>() ?? 0,
                o["disabled"]?.GetValue<bool>() == true,
                o["isSystem"]?.GetValue<bool>() == true,
                o["apkSizeBytes"]?.GetValue<long>() ?? 0));
        }
        return list.OrderBy(a => a.Package, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<bool> WatchTelemetryAsync(bool enabled, CancellationToken ct = default) =>
        (await CallAsync("telemetry_watch", new JsonObject { ["enabled"] = enabled }, timeout: TimeSpan.FromSeconds(6), ct: ct)).Ok;

    // ---------------------------------------------------------------- typed helpers (Step 7: Camera orientation & locate)

    public async Task<(int? Back, int? Front)?> GetCameraOrientationsAsync(CancellationToken ct = default)
    {
        var res = await CallAsync("camera_info", timeout: TimeSpan.FromSeconds(6), ct: ct);
        if (!res.Ok) return null;
        int? back = res.Data["backOrientation"] is JsonValue b && b.TryGetValue<int>(out var bv) ? bv : null;
        int? front = res.Data["frontOrientation"] is JsonValue f && f.TryGetValue<int>(out var fv) ? fv : null;
        return (back, front);
    }

    public async Task<bool> WatchCameraOrientationAsync(bool enabled, CancellationToken ct = default) =>
        (await CallAsync("camera_watch", new JsonObject { ["enabled"] = enabled }, timeout: TimeSpan.FromSeconds(6), ct: ct)).Ok;

    public async Task<HelperResponse> LocateDeviceAsync(string mode = "all", int durationMs = 2800, CancellationToken ct = default) =>
        await CallAsync("locate", new JsonObject { ["mode"] = mode, ["durationMs"] = durationMs }, timeout: TimeSpan.FromSeconds(12), ct: ct);

    // ---------------------------------------------------------------- typed helpers (Step 8: System controls, media & opt-in OTP notifications)

    public async Task<DeviceControlsState?> GetSystemStateAsync(CancellationToken ct = default)
    {
        var res = await CallAsync("sys_get", timeout: TimeSpan.FromSeconds(8), ct: ct);
        if (!res.Ok) return null;
        var d = res.Data;
        var vols = d["volumes"] as JsonObject;
        var music = vols?["music"] as JsonObject;
        var ring = vols?["ring"] as JsonObject;
        return new DeviceControlsState(
            d["darkMode"]?.GetValue<bool>() == true,
            d["brightness"]?.GetValue<int>() ?? 128,
            d["autoBrightness"]?.GetValue<bool>() == true,
            d["autoRotate"]?.GetValue<bool>() == true,
            d["dnd"]?.GetValue<bool>() == true,
            d["wifi"]?.GetValue<bool>() == true,
            d["bluetooth"]?.GetValue<bool>() == true,
            music?["current"]?.GetValue<int>() ?? 0,
            music?["max"]?.GetValue<int>() ?? 15,
            ring?["current"]?.GetValue<int>() ?? 0,
            ring?["max"]?.GetValue<int>() ?? 7);
    }

    public async Task<bool> SetSystemSettingAsync(string setting, JsonNode value, string? stream = null, CancellationToken ct = default)
    {
        var args = new JsonObject { ["setting"] = setting, ["value"] = value };
        if (!string.IsNullOrEmpty(stream)) args["stream"] = stream;
        return (await CallAsync("sys_set", args, timeout: TimeSpan.FromSeconds(8), ct: ct)).Ok;
    }

    public async Task<MediaState?> GetMediaStateAsync(CancellationToken ct = default)
    {
        var res = await CallAsync("media_state", timeout: TimeSpan.FromSeconds(8), ct: ct);
        if (!res.Ok) return null;
        return new MediaState(
            res.Data["playing"]?.GetValue<bool>() == true,
            res.Data["title"]?.ToString() ?? "",
            res.Data["artist"]?.ToString() ?? "",
            res.Data["album"]?.ToString() ?? "",
            res.Data["package"]?.ToString() ?? "");
    }

    public async Task<bool> ControlMediaAsync(string action, CancellationToken ct = default) =>
        (await CallAsync("media_control", new JsonObject { ["action"] = action }, timeout: TimeSpan.FromSeconds(6), ct: ct)).Ok;

    public async Task<bool> WatchNotificationsAsync(bool enabled, CancellationToken ct = default) =>
        (await CallAsync("notifications_watch", new JsonObject { ["enabled"] = enabled }, timeout: TimeSpan.FromSeconds(8), ct: ct)).Ok;

    // ---------------------------------------------------------------- reader & teardown

    async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await HelperProtocol.ReadFrameAsync(_stream, _cts.Token);
                if (frame == null) break;
                LastUsed = DateTime.UtcNow;

                if (frame.Header["id"] is JsonValue idVal && idVal.TryGetValue<long>(out var id))
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        var ok = frame.Header["ok"]?.GetValue<bool>() ?? false;
                        var err = frame.Header["error"]?.ToString();
                        tcs.TrySetResult(new HelperResponse(ok, frame.Header, frame.Binary, err));
                    }
                }
                else if (frame.Header["event"]?.ToString() is { Length: > 0 } ev)
                {
                    try { EventReceived?.Invoke(new HelperEvent(Serial, ev, frame.Header, frame.Binary)); }
                    catch { /* never let a subscriber crash the reader */ }
                }
            }
        }
        catch { /* disconnect handled in finally */ }
        finally
        {
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var tcs))
                    tcs.TrySetException(new IOException("Helper daemon disconnected."));
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { _cts.Cancel(); } catch { }
                Disconnected?.Invoke();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                if (_tcp?.Connected == true)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                    await HelperProtocol.WriteFrameAsync(_stream, new JsonObject { ["id"] = 0, ["cmd"] = "shutdown" }, null, cts.Token);
                }
            }
            catch { }
        }
        try { _cts.Cancel(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        if (_proc != null)
        {
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
            try { _proc.Dispose(); } catch { }
        }
        if (_adb != null && _port > 0)
            _ = _adb.RunAsync("-s", Serial, "forward", "--remove", $"tcp:{_port}");
        _writeLock.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Manages one persistent <see cref="DeviceHelperClient"/> per connected Android device.
/// Battery-friendly: watches (Wear OS) auto-disconnect after short idle periods, and connections
/// are only opened lazily when needed (or when a user enables a background watcher).</summary>
public sealed class DeviceHelperManager(AdbClient adb) : IAsyncDisposable
{
    readonly AdbClient _adb = adb;
    readonly ConcurrentDictionary<string, DeviceHelperClient> _clients = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    readonly ConcurrentDictionary<string, bool> _isWatch = new();

    public event Action<HelperEvent>? EventReceived;
    public event Action<string>? Connected;
    public event Action<string>? Disconnected;

    public DeviceHelperClient? TryGet(string serial) =>
        _clients.TryGetValue(serial, out var c) && c.IsConnected ? c : null;

    public IReadOnlyCollection<string> ConnectedSerials =>
        _clients.Where(kv => kv.Value.IsConnected).Select(kv => kv.Key).ToList();

    /// <summary>Returns an existing helper connection or starts one for <paramref name="serial"/>.</summary>
    public async Task<DeviceHelperClient?> GetOrStartAsync(string serial, bool isWatch = false, CancellationToken ct = default)
    {
        if (_clients.TryGetValue(serial, out var existing) && existing.IsConnected) return existing;
        var gate = _locks.GetOrAdd(serial, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_clients.TryGetValue(serial, out existing) && existing.IsConnected) return existing;
            _isWatch[serial] = isWatch;
            var client = await DeviceHelperClient.StartAsync(_adb, serial, ct);
            client.EventReceived += ev => EventReceived?.Invoke(ev);
            client.Disconnected += () =>
            {
                if (_clients.TryRemove(serial, out var old) && ReferenceEquals(old, client))
                    Disconnected?.Invoke(serial);
            };
            _clients[serial] = client;
            Connected?.Invoke(serial);
            return client;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(Path.Combine(AppPaths.LogDir, "errors.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Helper start failed ({serial}): {ex.Message}{Environment.NewLine}");
            }
            catch { }
            return null;
        }
        finally { gate.Release(); }
    }

    /// <summary>Fast inventory via helper daemon, falling back seamlessly to adb shell script.</summary>
    public async Task<DeviceInventory?> ReadInventoryAsync(string serial, string name, bool isWatch = false, CancellationToken ct = default)
    {
        if (await GetOrStartAsync(serial, isWatch, ct) is { } helper && helper.Supports("inventory"))
            try { if (await helper.GetInventoryAsync(name, ct) is { } fast) return fast; } catch { }
        return await _adb.GetInventoryAsync(serial, name);
    }

    /// <summary>Closes idle watch daemons (called periodically from refresh) to protect small watch batteries.</summary>
    public void PruneIdleWatches(TimeSpan? maxIdle = null)
    {
        var limit = maxIdle ?? TimeSpan.FromSeconds(60);
        var now = DateTime.UtcNow;
        foreach (var (serial, client) in _clients)
        {
            if (_isWatch.TryGetValue(serial, out var watch) && watch && now - client.LastUsed > limit)
                _ = DisconnectAsync(serial);
        }
    }

    public async Task DisconnectAsync(string serial)
    {
        if (_clients.TryRemove(serial, out var client))
            await client.DisposeAsync();
    }

    /// <summary>Disconnects helpers for devices that are no longer present in <paramref name="activeSerials"/>.</summary>
    public void PruneDisconnected(ISet<string> activeSerials)
    {
        foreach (var serial in _clients.Keys.ToList())
            if (!activeSerials.Contains(serial))
                _ = DisconnectAsync(serial);
    }

    public async ValueTask DisposeAsync()
    {
        var all = _clients.Keys.ToList();
        foreach (var s in all) await DisconnectAsync(s);
    }
}
