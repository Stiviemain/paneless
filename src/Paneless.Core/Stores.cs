using System.Text.Json;

namespace Paneless.Core;

public sealed class KnownDevice
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Type { get; set; } = "";
}

/// <summary>Defaults for every device (settings.json). Devices and groups can override them (see DeviceProfile).</summary>
public sealed class AppSettings
{
    public bool ScreenOff { get; set; }
    public bool Audio { get; set; } = true;
    public bool OnTop { get; set; }
    public bool KeepAwake { get; set; } = true;
    public string MaxSize { get; set; } = "1920 px";

    public string VideoCodec { get; set; } = "h264";
    public int BitrateMbps { get; set; } = 8;
    public int MaxFps { get; set; }
    public string PushTarget { get; set; } = "/sdcard/Download/";
    /// <summary>Connect to devices that were paired before as soon as they show up on the network.</summary>
    public bool AutoConnect { get; set; } = true;
    /// <summary>Closing the window keeps Paneless running in the notification area.</summary>
    public bool CloseToTray { get; set; }
    /// <summary>Velopack update feed: a web address or a folder. Empty = no update checks.</summary>
    public string UpdateSource { get; set; } = "";
}

public sealed class UnlockEntry
{
    /// <summary>Windows: the DPAPI-encrypted code (hex, as PowerShell's ConvertFrom-SecureString). macOS/Linux: a marker; the code is in the OS keychain.</summary>
    public string? Secret { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Small JSON files in the data folder, written atomically.</summary>
public static class JsonFile
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static T Load<T>(string name) where T : new()
    {
        var path = Path.Combine(AppPaths.DataDir, name);
        try
        {
            // ReadAllText also copes with a byte-order mark (e.g. a file saved by PowerShell or Notepad).
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch { /* unreadable: start fresh rather than crash */ }
        return new T();
    }

    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var path = Path.Combine(AppPaths.DataDir, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class DataStore
{
    public Dictionary<string, KnownDevice> Known { get; } = JsonFile.Load<Dictionary<string, KnownDevice>>("known-devices.json");
    public AppSettings Settings { get; } = JsonFile.Load<AppSettings>("settings.json");
    public Dictionary<string, UnlockEntry> Unlock { get; } = JsonFile.Load<Dictionary<string, UnlockEntry>>("unlock-codes.json");

    public void SaveKnown() => JsonFile.Save("known-devices.json", Known);
    public void SaveSettings() => JsonFile.Save("settings.json", Settings);
    public void SaveUnlock() => JsonFile.Save("unlock-codes.json", Unlock);
}
