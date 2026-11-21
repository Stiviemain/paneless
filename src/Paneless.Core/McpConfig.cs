using System.Text.Json;
using System.Text.Json.Nodes;

namespace Paneless.Core;

/// <summary>Adding Paneless to AI apps' MCP settings, and naming the apps that connect. Pure text in, text out,
/// so it can be tested without touching anyone's real settings.</summary>
public static class McpConfig
{
    public const string ServerName = "paneless";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonObject Server(string exe) => new() { ["command"] = exe, ["args"] = new JsonArray("--mcp") };

    /// <summary>The usual "mcpServers" settings (Claude Desktop, Gemini CLI, Cursor, LM Studio …).</summary>
    public static string JsonSettings(string exe) =>
        new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = Server(exe) } }.ToJsonString(Indented);

    /// <summary>A JSON settings file with Paneless added to "mcpServers". Everything else stays; comments and
    /// trailing commas in the old file are accepted but not kept.</summary>
    public static string WithJsonServer(string json, string exe)
    {
        var root = json.Trim().Length == 0 ? new JsonObject()
            : JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new FormatException(Text.T("The settings file isn’t a JSON object."));
        if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
        servers[ServerName] = Server(exe);
        return root.ToJsonString(Indented);
    }

    /// <summary>Codex's config.toml: drops any [mcp_servers.paneless] table (and its sub-tables) and appends a fresh one.
    /// Everything else stays as it was.</summary>
    public static string WithCodexServer(string toml, string exe)
    {
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in toml.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('['))
                skipping = t == $"[mcp_servers.{ServerName}]" || t.StartsWith($"[mcp_servers.{ServerName}.");
            if (!skipping) kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        if (kept.Count > 0) kept.Add("");
        kept.Add($"[mcp_servers.{ServerName}]");
        kept.Add($"command = {TomlString(exe)}");
        kept.Add("args = [\"--mcp\"]");
        return string.Join(Environment.NewLine, kept) + Environment.NewLine;
    }

    /// <summary>A TOML literal string ('…', so Windows paths need no escaping), or a basic string if the text has a '.</summary>
    static string TomlString(string s) => s.Contains('\'')
        ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        : "'" + s + "'";

    /// <summary>A readable name for an MCP client, from the clientInfo it sends when it connects.</summary>
    public static string Friendly(string? name, string? title)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("claude-code") || n.Contains("claude code")) return "Claude Code";
        if (n.Contains("claude")) return "Claude";
        if (n.Contains("codex")) return "Codex";
        if (n.Contains("chatgpt") || n.Contains("openai")) return "ChatGPT";
        if (n.Contains("gemini")) return "Gemini";
        if (n.Contains("cursor")) return "Cursor";
        if (n.Contains("visual studio code") || n.Contains("vscode")) return "VS Code";
        if (n.Contains("lm studio") || n.Contains("lmstudio")) return "LM Studio";
        var shown = (!string.IsNullOrWhiteSpace(title) ? title! : name ?? "").Trim();
        return shown.Length == 0 ? Text.T("An AI assistant") : shown[..Math.Min(shown.Length, 40)];
    }
}
