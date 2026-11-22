using System.Globalization;
using System.Text.Json.Nodes;

namespace Paneless.Core;

/// <summary>Device facts as AI assistants get them: plain numbers and words, no display symbols or raw Android codes.
/// Kept in English on purpose: these texts are for the model, not for the user.</summary>
public static class AssistantFacts
{
    public static JsonObject Inventory(DeviceInventory d) => new()
    {
        ["name"] = d.Name,
        ["serial"] = d.Serial,
        ["manufacturer"] = Opt(d.Manufacturer),
        ["model"] = Opt(d.Model),
        ["android_version"] = Opt(d.AndroidVersion),
        ["sdk"] = d.Sdk > 0 ? d.Sdk : null,
        ["security_patch"] = Opt(d.SecurityPatch),
        ["battery_percent"] = d.BatteryLevel,
        ["charging"] = d.Charging,
        ["battery_health"] = Opt(d.BatteryHealth),
        ["battery_temperature_c"] = d.BatteryTempC,
        ["storage_total_gb"] = Gb(d.StorageTotalMb),
        ["storage_free_gb"] = Gb(d.StorageFreeMb),
        ["storage_free_percent"] = d.StorageFreePercent is { } p ? Math.Round(p) : null,
        ["ram_gb"] = Gb(d.RamTotalMb),
        ["screen"] = Opt(d.Screen),
        ["ip_address"] = Opt(d.IpAddress),
        ["uptime"] = d.Uptime is { } u ? Uptime(u) : null,
        ["collected_at"] = d.CollectedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
    };

    /// <summary>"9 days, 7 hours", "5 hours, 12 minutes", "3 minutes".</summary>
    public static string Uptime(TimeSpan u)
    {
        static string N(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        if (u.TotalDays >= 1) return u.Hours > 0 ? $"{N(u.Days, "day")}, {N(u.Hours, "hour")}" : N(u.Days, "day");
        if (u.TotalHours >= 1) return u.Minutes > 0 ? $"{N(u.Hours, "hour")}, {N(u.Minutes, "minute")}" : N(u.Hours, "hour");
        return N(Math.Max(0, u.Minutes), "minute");
    }

    /// <summary>Android's battery health codes (BatteryManager and the health HAL) as words.</summary>
    public static string HealthName(string code) => code switch
    {
        "1" => "Unknown",
        "2" => "Good",
        "3" => "Overheat",
        "4" => "Dead",
        "5" => "Over voltage",
        "6" => "Failure",
        "7" => "Cold",
        "11" => "Not available yet",
        "12" => "Needs calibration",
        _ => code,
    };

    static string? Opt(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    static double? Gb(long? mb) => mb is { } m ? Math.Round(m / 1024.0, 1) : null;
}
