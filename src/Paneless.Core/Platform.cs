namespace Paneless.Core;

/// <summary>Where Paneless keeps its data, and where it finds adb and scrcpy, on each OS.</summary>
public static class AppPaths
{
    public static string DataDir
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Paneless");
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Application Support", "Paneless");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "paneless");
        }
    }

    public static string LogDir => Path.Combine(Path.GetTempPath(), "paneless");

    /// <summary>Creates the data folder.</summary>
    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}

public sealed record ToolPaths(string Adb, string Scrcpy);

public static class ToolLocator
{
    static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>Finds scrcpy and the adb that belongs with it. Returns null if either is missing.</summary>
    public static ToolPaths? Find()
    {
        // A copy of scrcpy shipped with Paneless (the Windows installer bundles one) wins over anything else.
        var dirs = new List<string> { Path.Combine(AppContext.BaseDirectory, "scrcpy") };
        if (OperatingSystem.IsWindows())
        {
            var pkgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgs))
            {
                dirs.AddRange(Directory.EnumerateDirectories(pkgs, "Genymobile.scrcpy*")
                    .SelectMany(d => SafeFiles(d, "scrcpy.exe"))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Select(f => Path.GetDirectoryName(f)!));
            }
        }
        else
        {
            // GUI apps don't inherit the shell's PATH on macOS, so list the usual install places too.
            dirs.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin" });
        }
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        string? scrcpy = null, adb = null;
        foreach (var d in dirs)
        {
            var s = Path.Combine(d, Exe("scrcpy"));
            if (scrcpy == null && File.Exists(s))
            {
                scrcpy = s;
                var bundled = Path.Combine(d, Exe("adb"));   // the Windows build of scrcpy ships its own adb
                if (File.Exists(bundled)) adb = bundled;
            }
        }
        if (adb == null)
            foreach (var d in dirs)
            {
                var a = Path.Combine(d, Exe("adb"));
                if (File.Exists(a)) { adb = a; break; }
            }
        return scrcpy != null && adb != null ? new ToolPaths(adb, scrcpy) : null;
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    public static string InstallHint =>
        OperatingSystem.IsWindows() ? Text.T("Install it with:  winget install Genymobile.scrcpy") :
        OperatingSystem.IsMacOS() ? Text.T("Install it with:  brew install scrcpy android-platform-tools") :
        Text.T("Install it with your package manager, e.g.  sudo apt install scrcpy adb  (or snap install scrcpy)");
}
