using System.Text.Json;

namespace Paneless.Core;

/// <summary>Serializable backup bundle for Paneless settings and (optionally) saved devices, groups,
/// per-device settings and remote apps. Unlock codes are intentionally never exported.</summary>
public sealed class SettingsBackupData
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string App { get; set; } = "Paneless";
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public bool IncludesDevices { get; set; }

    public AppSettings? Settings { get; set; }
    public List<AdbScript>? Scripts { get; set; }
    public KeymapData? Keymaps { get; set; }
    public List<Recipe>? Recipes { get; set; }
    public List<ScheduledTask>? Schedules { get; set; }
    public AlertData? Alerts { get; set; }

    // Included only when IncludesDevices == true (unlock codes are never exported).
    public Dictionary<string, KnownDevice>? KnownDevices { get; set; }
    public GroupData? Groups { get; set; }
    public Dictionary<string, DeviceProfile>? Profiles { get; set; }
    public List<RemoteApp>? RemoteApps { get; set; }
}

/// <summary>Export, import and factory reset for Paneless configuration (zero UI dependencies).</summary>
public static class SettingsBackup
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    static T Clone<T>(T value) where T : new() =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions) ?? new T();

    /// <summary>Serializes Paneless settings (and optionally saved devices, groups, profiles and remote apps) to JSON.
    /// Unlock codes are never included.</summary>
    public static string ExportJson(
        AppSettings settings,
        IReadOnlyList<AdbScript> scripts,
        KeymapData keymaps,
        IReadOnlyList<Recipe> recipes,
        IReadOnlyList<ScheduledTask> schedules,
        AlertData alerts,
        bool includeDevices,
        IReadOnlyDictionary<string, KnownDevice>? knownDevices = null,
        GroupData? groups = null,
        IReadOnlyDictionary<string, DeviceProfile>? profiles = null,
        IReadOnlyList<RemoteApp>? remoteApps = null)
    {
        var keymapCopy = Clone(keymaps);
        if (!includeDevices) keymapCopy.ActiveProfile.Clear();

        var alertCopy = new AlertData
        {
            Rules = Clone(alerts.Rules),
            Events = [], // transient history is not part of settings
        };

        var bundle = new SettingsBackupData
        {
            Version = SettingsBackupData.CurrentVersion,
            App = "Paneless",
            ExportedAt = DateTime.UtcNow,
            IncludesDevices = includeDevices,
            Settings = Clone(settings),
            Scripts = Clone(scripts.ToList()),
            Keymaps = keymapCopy,
            Recipes = Clone(recipes.ToList()),
            Schedules = Clone(schedules.ToList()),
            Alerts = alertCopy,
        };

        if (includeDevices)
        {
            bundle.KnownDevices = knownDevices != null
                ? Clone(knownDevices.ToDictionary(kv => kv.Key, kv => kv.Value))
                : new();
            bundle.Groups = groups != null ? Clone(groups) : new();
            bundle.Profiles = profiles != null
                ? Clone(profiles.ToDictionary(kv => kv.Key, kv => kv.Value))
                : new();
            bundle.RemoteApps = remoteApps != null ? Clone(remoteApps.ToList()) : [];
        }

        return JsonSerializer.Serialize(bundle, JsonOptions);
    }

    /// <summary>Validates and parses a backup JSON string.</summary>
    public static bool TryParse(string json, out SettingsBackupData? data, out string? error)
    {
        data = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = Text.T("The file is empty.");
            return false;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<SettingsBackupData>(json, JsonOptions);
            if (parsed == null || parsed.Settings == null || !string.Equals(parsed.App, "Paneless", StringComparison.OrdinalIgnoreCase))
            {
                error = Text.T("This file doesn't look like a Paneless settings backup.");
                return false;
            }
            if (parsed.Version < 1 || parsed.Version > SettingsBackupData.CurrentVersion)
            {
                error = Text.F("Backup version {0} isn't supported by this version of Paneless.", parsed.Version);
                return false;
            }
            data = parsed;
            return true;
        }
        catch (JsonException)
        {
            error = Text.T("The file isn't valid JSON.");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Copies all settings fields from <paramref name="source"/> onto <paramref name="target"/> in place.</summary>
    public static void CopySettings(AppSettings source, AppSettings target)
    {
        target.ScreenOff = source.ScreenOff;
        target.Audio = source.Audio;
        target.OnTop = source.OnTop;
        target.KeepAwake = source.KeepAwake;
        target.LockOnClose = source.LockOnClose;
        target.KeepScreensOn = source.KeepScreensOn;
        target.MaxSize = string.IsNullOrWhiteSpace(source.MaxSize) ? "1920 px" : source.MaxSize;
        target.VideoCodec = string.IsNullOrWhiteSpace(source.VideoCodec) ? "h264" : source.VideoCodec;
        target.BitrateMbps = source.BitrateMbps > 0 ? source.BitrateMbps : 8;
        target.MaxFps = Math.Max(0, source.MaxFps);
        target.PushTarget = string.IsNullOrWhiteSpace(source.PushTarget) ? "/sdcard/Download/" : source.PushTarget;
        target.AutoConnect = source.AutoConnect;
        target.DisconnectInBackground = source.DisconnectInBackground;
        target.CloseToTray = source.CloseToTray;
        target.UpdateSource = source.UpdateSource ?? "";
        target.AutoUpdate = source.AutoUpdate;
        target.BetaUpdates = source.BetaUpdates;
        target.Language = source.Language ?? "";
        target.Theme = source.Theme ?? "";
        target.AssistantsEnabled = source.AssistantsEnabled;
        target.AssistantDefaultAccess = source.AssistantDefaultAccess;
        target.AssistantConfirm = source.AssistantConfirm;
        target.WallSeconds = Math.Clamp(source.WallSeconds, 1, 300);
        target.Wall = source.Wall != null ? Clone(source.Wall) : new();
        target.ClipboardSync = source.ClipboardSync;
        target.AutoDownloadMedia = source.AutoDownloadMedia;
        target.AutoCopyOtp = source.AutoCopyOtp;
    }

    /// <summary>Applies a parsed backup to the live stores (saving each affected store).</summary>
    public static void ApplyTo(
        SettingsBackupData backup,
        DataStore data,
        ScriptStore scripts,
        KeymapStore keymaps,
        RecipeStore recipes,
        ScheduleStore schedules,
        AlertStore alerts,
        GroupStore groups,
        ProfileStore profiles,
        RemoteAppStore remoteApps)
    {
        if (backup.Settings != null)
        {
            CopySettings(backup.Settings, data.Settings);
            data.SaveSettings();
        }

        if (backup.Scripts != null)
        {
            scripts.Mine.Clear();
            scripts.Mine.AddRange(Clone(backup.Scripts));
            scripts.Save();
        }

        if (backup.Keymaps != null)
        {
            var km = Clone(backup.Keymaps);
            keymaps.Data.Profiles.Clear();
            keymaps.Data.Profiles.AddRange(km.Profiles);
            if (backup.IncludesDevices)
            {
                keymaps.Data.ActiveProfile.Clear();
                foreach (var (k, v) in km.ActiveProfile) keymaps.Data.ActiveProfile[k] = v;
            }
            keymaps.Save();
        }

        if (backup.Recipes != null)
        {
            recipes.All.Clear();
            recipes.All.AddRange(Clone(backup.Recipes));
            recipes.Save();
        }

        if (backup.Schedules != null)
        {
            schedules.All.Clear();
            schedules.All.AddRange(Clone(backup.Schedules));
            schedules.Save();
        }

        if (backup.Alerts != null)
        {
            var rules = Clone(backup.Alerts.Rules);
            alerts.Data.Rules.Clear();
            alerts.Data.Rules.AddRange(rules);
            foreach (var k in Enum.GetValues<AlertKind>())
                if (!alerts.Data.Rules.Any(r => r.Kind == k)) alerts.Data.Rules.Add(AlertRule.Default(k));
            alerts.Save();
        }

        if (backup.IncludesDevices)
        {
            if (backup.KnownDevices != null)
            {
                data.Known.Clear();
                foreach (var (k, v) in Clone(backup.KnownDevices)) data.Known[k] = v;
                data.SaveKnown();
            }

            if (backup.Groups != null)
            {
                var gd = Clone(backup.Groups);
                groups.Data.Groups.Clear();
                groups.Data.Groups.AddRange(gd.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)));
                groups.Data.OthersCollapsed = gd.OthersCollapsed;
                groups.Save();
            }

            if (backup.Profiles != null)
            {
                profiles.ReplaceAll(Clone(backup.Profiles));
            }

            if (backup.RemoteApps != null)
            {
                remoteApps.All.Clear();
                remoteApps.All.AddRange(Clone(backup.RemoteApps));
                remoteApps.Save();
            }
        }
    }

    /// <summary>Resets all settings, scripts, automation, saved devices, groups, profiles, remote apps and unlock codes
    /// back to factory defaults.</summary>
    public static void ResetToDefaults(
        DataStore data,
        ScriptStore scripts,
        KeymapStore keymaps,
        RecipeStore recipes,
        ScheduleStore schedules,
        AlertStore alerts,
        GroupStore groups,
        ProfileStore profiles,
        RemoteAppStore remoteApps,
        string? remoteAppIconDir = null)
    {
        CopySettings(new AppSettings(), data.Settings);
        data.SaveSettings();

        data.Known.Clear();
        data.SaveKnown();

        data.Unlock.Clear();
        data.SaveUnlock();

        scripts.Mine.Clear();
        scripts.Save();

        keymaps.Data.Profiles.Clear();
        keymaps.Data.ActiveProfile.Clear();
        keymaps.Save();

        recipes.All.Clear();
        recipes.Save();

        schedules.All.Clear();
        schedules.Save();

        alerts.Data.Rules.Clear();
        alerts.Data.Events.Clear();
        foreach (var k in Enum.GetValues<AlertKind>()) alerts.Data.Rules.Add(AlertRule.Default(k));
        alerts.Save();

        groups.Data.Groups.Clear();
        groups.Data.OthersCollapsed = false;
        groups.Save();

        profiles.ReplaceAll(new Dictionary<string, DeviceProfile>());

        remoteApps.All.Clear();
        remoteApps.Save();

        if (!string.IsNullOrEmpty(remoteAppIconDir) && Directory.Exists(remoteAppIconDir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(remoteAppIconDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }
    }
}
