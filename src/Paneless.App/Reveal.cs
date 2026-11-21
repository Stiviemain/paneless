using System.Diagnostics;

namespace Paneless.App;

/// <summary>Shows a file in the system's file manager.</summary>
public static class Reveal
{
    public static void File(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else Process.Start("xdg-open", Path.GetDirectoryName(path)!);
        }
        catch { /* the status line still says where it is */ }
    }
}
