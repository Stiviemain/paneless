using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.Assistants;

/// <summary>An AI app that can use Paneless through MCP, and how to connect it.
/// <paramref name="Snippet"/> is a terminal command or settings text for the program path given.</summary>
public sealed record AssistantApp(string Id, string Name, string Hint, bool CanAdd, string AddLabel, Func<string, string> Snippet)
{
    public override string ToString() => Name;
}

/// <summary>The assistants Paneless knows how to connect. They all start "Paneless.exe --mcp" (see <see cref="McpBridge"/>);
/// only where each keeps that setting differs.</summary>
public static class AssistantApps
{
    const string ServerName = McpConfig.ServerName;

    public static IReadOnlyList<AssistantApp> All { get; } =
    [
        new("claude-desktop", "Claude Desktop",
            Loc.T("Adds Paneless to Claude Desktop’s settings. Restart Claude Desktop afterwards (quit it from the notification area)."),
            true, Loc.T("Add to Claude Desktop"), JsonSettings),
        new("claude-code", "Claude Code",
            Loc.T("Run this once in a terminal; Claude Code then has Paneless in every project."),
            false, "", exe => $"claude mcp add {ServerName} --scope user -- \"{exe}\" --mcp"),
        new("codex", Loc.T("Codex (also in the ChatGPT desktop app)"),
            Loc.T("Adds Paneless to Codex’s settings, which the Codex CLI, its editor extension and Codex in the ChatGPT desktop app share. Or run this command once in a terminal."),
            true, Loc.T("Add to Codex"), exe => $"codex mcp add {ServerName} -- \"{exe}\" --mcp"),
        new("gemini-cli", "Gemini CLI",
            Loc.T("Adds Paneless to Gemini CLI’s settings. Start a new Gemini CLI session afterwards; /mcp lists Paneless."),
            true, Loc.T("Add to Gemini CLI"), JsonSettings),
        new("other", Loc.T("Other MCP apps (Cursor, VS Code, LM Studio, …)"),
            Loc.T("Most apps that support MCP accept these settings: paste them into the app’s MCP settings. The program is Paneless.exe with --mcp."),
            false, "", JsonSettings),
    ];

    static string JsonSettings(string exe) => McpConfig.JsonSettings(exe);

    // ---------------------------------------------------------------- settings files

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The settings files "Add" changes for an app.</summary>
    public static IReadOnlyList<string> ConfigFiles(string appId) => appId switch
    {
        "claude-desktop" => ClaudeDesktopConfigs(),
        "codex" => [Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } ch ? ch : Path.Combine(Home, ".codex"), "config.toml")],
        "gemini-cli" => [Path.Combine(Home, ".gemini", "settings.json")],
        _ => [],
    };

    /// <summary>Claude Desktop: %APPDATA%\Claude, and the Store version's own copy of it.</summary>
    static List<string> ClaudeDesktopConfigs()
    {
        var list = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"));
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            try
            {
                foreach (var pkg in Directory.EnumerateDirectories(packages, "Claude_*"))
                    list.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
            }
            catch { }
        }
        else if (OperatingSystem.IsMacOS())
            list.Add(Path.Combine(Home, "Library", "Application Support", "Claude", "claude_desktop_config.json"));
        else
            list.Add(Path.Combine(Home, ".config", "Claude", "claude_desktop_config.json"));
        return list;
    }

    /// <summary>Adds (or updates) Paneless in an app's settings file, keeping everything else. A copy of the old file is kept as .bak.</summary>
    public static async Task AddAsync(string appId, string exe)
    {
        foreach (var file in ConfigFiles(appId))
        {
            var old = File.Exists(file) ? await File.ReadAllTextAsync(file) : "";
            var updated = appId == "codex" ? McpConfig.WithCodexServer(old, exe) : McpConfig.WithJsonServer(old, exe);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file)) File.Copy(file, file + ".bak", overwrite: true);
            await File.WriteAllTextAsync(file, updated);
        }
    }

    // ---------------------------------------------------------------- who is asking

    /// <summary>A readable name for an MCP client (see <see cref="McpConfig.Friendly"/>).</summary>
    public static string Friendly(string? name, string? title) => McpConfig.Friendly(name, title);
}
