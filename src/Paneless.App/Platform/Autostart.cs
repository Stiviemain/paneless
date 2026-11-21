namespace Paneless.App.Platform;

/// <summary>"Start with Windows": a value under HKCU\...\Run that starts Paneless in the notification area.
/// Only changed when you tick or untick it in Settings.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Paneless";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }
    }

    public static bool Set(bool on)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                // Velopack keeps the app in ...\PanelessApp\current\, which stays the same across updates.
                var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Paneless.exe");
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }
}
