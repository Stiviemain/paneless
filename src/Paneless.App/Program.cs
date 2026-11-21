using Avalonia;
using Velopack;

namespace Paneless.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles the installer's install/update/uninstall hooks, then returns.
        VelopackApp.Build().Run();

        // Started by Claude Desktop / Claude Code: forward its messages to the running Paneless, no window.
        if (args.Contains("--mcp")) return Assistants.McpBridge.Run();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
