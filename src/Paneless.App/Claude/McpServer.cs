using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paneless.App.Claude;

/// <summary>The part of Paneless Claude talks to: a Model Context Protocol (MCP) server on a local named pipe
/// that only this Windows user can open. Claude Desktop / Claude Code reach it through "Paneless.exe --mcp"
/// (<see cref="McpBridge"/>), which forwards their stdio messages here. Messages are JSON-RPC, one per line.</summary>
public sealed class McpServer(ClaudeTools tools) : IDisposable
{
    public static string PipeName => "paneless-mcp-" + Sanitize(Environment.UserName);
    const string ProtocolVersion = "2025-06-18";

    CancellationTokenSource? _cts;
    int _clients;

    public bool IsRunning => _cts != null;
    public int Clients => _clients;
    /// <summary>Raised when a client connects or disconnects.</summary>
    public event Action? ClientsChanged;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(1000, CancellationToken.None); continue; }
            _ = ServeAsync(pipe, ct);
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Interlocked.Increment(ref _clients);
        ClientsChanged?.Invoke();
        var writeGate = new SemaphoreSlim(1, 1);
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                // Requests run concurrently (a confirmation dialog mustn't block a "ping").
                _ = Task.Run(async () =>
                {
                    var reply = await HandleAsync(line);
                    if (reply == null) return;
                    await writeGate.WaitAsync();
                    try { await writer.WriteLineAsync(reply.ToJsonString()); }
                    catch { }
                    finally { writeGate.Release(); }
                });
            }
        }
        catch { /* client went away */ }
        finally
        {
            try { pipe.Dispose(); } catch { }
            Interlocked.Decrement(ref _clients);
            ClientsChanged?.Invoke();
        }
    }

    async Task<JsonObject?> HandleAsync(string line)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(line); }
        catch { return Error(null, -32700, "Parse error"); }
        if (msg is not JsonObject req) return Error(null, -32600, "Invalid request");
        var id = req["id"]?.DeepClone();
        var method = req["method"]?.GetValue<string>();
        if (method == null) return null;            // a response to something we never send
        if (id == null) return null;                // a notification (e.g. notifications/initialized)

        try
        {
            switch (method)
            {
                case "initialize":
                    var requested = req["params"]?["protocolVersion"]?.GetValue<string>();
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = requested ?? ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "paneless", ["title"] = "Paneless", ["version"] = AppServices.Version },
                        ["instructions"] = ClaudeTools.Instructions,
                    });
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = tools.ListJson() });
                case "tools/call":
                    var name = req["params"]?["name"]?.GetValue<string>() ?? "";
                    var args = req["params"]?["arguments"] as JsonObject ?? new JsonObject();
                    var result = await tools.CallAsync(name, args);
                    return Result(id, result.ToJson());
                default:
                    return Error(id, -32601, "Method not found: " + method);
            }
        }
        catch (Exception ex) { return Error(id, -32603, ex.Message); }
    }

    static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
    static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    public void Dispose() => Stop();
}

/// <summary>"Paneless.exe --mcp": started by Claude Desktop / Claude Code. Forwards their stdio messages to the running
/// Paneless (starting it in the notification area if needed). Runs without any window.</summary>
public static class McpBridge
{
    public static int Run()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var pipe = Connect();
        if (pipe == null) return ServeUnavailable(stdin, stdout);

        using (pipe)
        {
            var toPaneless = Task.Run(async () =>
            {
                try { await stdin.CopyToAsync(pipe); } catch { }
                try { pipe.Close(); } catch { }
            });
            var toClaude = Task.Run(async () =>
            {
                try { await pipe.CopyToAsync(stdout); await stdout.FlushAsync(); } catch { }
            });
            Task.WaitAny(toPaneless, toClaude);
        }
        return 0;
    }

    static NamedPipeClientStream? Connect()
    {
        var started = false;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTime.UtcNow < until)
        {
            var pipe = new NamedPipeClientStream(".", McpServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { pipe.Connect(800); return pipe; }
            catch { pipe.Dispose(); }
            if (!started && !SingleInstance.IsRunning)
            {
                // Paneless isn't open: start it quietly in the notification area.
                started = true;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--tray") { UseShellExecute = false }); }
                catch { return null; }
            }
            Thread.Sleep(500);
        }
        return null;
    }

    /// <summary>Paneless isn't reachable (Claude access is off): answer every request with an explanation.</summary>
    static int ServeUnavailable(Stream stdin, Stream stdout)
    {
        using var reader = new StreamReader(stdin);
        using var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (JsonNode.Parse(line) is not JsonObject req || req["id"] is not { } id) continue;
                var err = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Paneless isn't letting Claude in: open Paneless, go to the Claude page and turn on \"Let Claude use Paneless\"." },
                };
                writer.WriteLine(err.ToJsonString());
            }
            catch { }
        }
        return 1;
    }
}
