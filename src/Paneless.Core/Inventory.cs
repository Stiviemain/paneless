using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Paneless.Core;

/// <summary>Facts about one device, for the inventory report, alerts and Claude.</summary>
public sealed record DeviceInventory(
    string Name, string Serial, string Manufacturer, string Model, string AndroidVersion, int Sdk, string SecurityPatch,
    int? BatteryLevel, bool Charging, string BatteryHealth, double? BatteryTempC,
    long? StorageTotalMb, long? StorageFreeMb, long? RamTotalMb, string Screen, string IpAddress, TimeSpan? Uptime,
    DateTime CollectedAt)
{
    public double? StorageFreePercent => StorageTotalMb is > 0 && StorageFreeMb is { } f ? 100.0 * f / StorageTotalMb.Value : null;
}

public sealed partial class AdbClient
{
    // One shell call with markers, so a whole fleet can be read quickly.
    const string InventoryScript =
        "echo '#manufacturer'; getprop ro.product.manufacturer; echo '#model'; getprop ro.product.model; " +
        "echo '#release'; getprop ro.build.version.release; echo '#sdk'; getprop ro.build.version.sdk; " +
        "echo '#patch'; getprop ro.build.version.security_patch; echo '#serial'; getprop ro.serialno; " +
        "echo '#battery'; dumpsys battery; echo '#df'; df -k /data; echo '#mem'; grep MemTotal /proc/meminfo; " +
        "echo '#size'; wm size; echo '#ip'; ip -f inet addr show wlan0; echo '#uptime'; cat /proc/uptime; echo '#end'";

    public async Task<DeviceInventory?> GetInventoryAsync(string serial, string name)
    {
        var r = await ShellAsync(serial, InventoryScript, TimeSpan.FromSeconds(20));
        if (!r.Ok) return null;
        var parts = new Dictionary<string, List<string>>();
        List<string>? cur = null;
        foreach (var raw in Lines(r.Output))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('#') && line.Length > 1 && !line.Contains(' ')) { cur = parts[line[1..]] = new(); continue; }
            cur?.Add(line);
        }
        string One(string k) => parts.TryGetValue(k, out var l) ? (l.FirstOrDefault(x => x.Trim().Length > 0) ?? "").Trim() : "";

        int? level = null; var charging = false; var health = ""; double? temp = null;
        foreach (var l in parts.GetValueOrDefault("battery") ?? [])
        {
            var i = l.IndexOf(':'); if (i <= 0) continue;
            var key = l[..i].Trim(); var val = l[(i + 1)..].Trim();
            switch (key)
            {
                case "level": if (int.TryParse(val, out var lv)) level = lv; break;
                case "status": charging = val == "2"; break;
                case "health": health = AssistantFacts.HealthName(val); break;
                case "temperature": if (int.TryParse(val, out var t)) temp = t / 10.0; break;
            }
        }

        long? total = null, free = null;
        var df = (parts.GetValueOrDefault("df") ?? []).Skip(1).FirstOrDefault(x => x.Trim().Length > 0);
        if (df != null)
        {
            var cols = df.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length >= 4 && long.TryParse(cols[1], out var t) && long.TryParse(cols[3], out var a)) { total = t / 1024; free = a / 1024; }
        }
        long? ram = MemRx().Match(One("mem")) is { Success: true } mm && long.TryParse(mm.Groups[1].Value, out var kb) ? kb / 1024 : null;
        var size = (parts.GetValueOrDefault("size") ?? []).Select(x => x.Trim()).LastOrDefault(x => x.Contains("size:")) is { } sz ? sz[(sz.IndexOf(':') + 1)..].Trim() : "";
        var ip = IpRx().Match(string.Join('\n', parts.GetValueOrDefault("ip") ?? [])) is { Success: true } im ? im.Groups[1].Value : "";
        TimeSpan? up = double.TryParse(One("uptime").Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? TimeSpan.FromSeconds(sec) : null;

        return new DeviceInventory(name, One("serial") is { Length: > 0 } sn ? sn : serial, One("manufacturer"), One("model"), One("release"),
            int.TryParse(One("sdk"), out var sdk) ? sdk : 0, One("patch"), level, charging, health, temp, total, free, ram, size, ip, up, DateTime.Now);
    }

    [GeneratedRegex(@"MemTotal:\s+(\d+)")]
    private static partial Regex MemRx();
    [GeneratedRegex(@"inet (\d+\.\d+\.\d+\.\d+)")]
    private static partial Regex IpRx();
}

/// <summary>Writes an inventory as CSV (for Excel) or a self-contained HTML page.</summary>
public static class InventoryExport
{
    public static readonly string[] Columns =
        ["Name", "Group", "Manufacturer", "Model", "Android", "SDK", "Security patch", "Battery %", "Charging", "Battery health", "Battery °C",
         "Storage total (GB)", "Storage free (GB)", "Storage free %", "RAM (GB)", "Screen", "IP address", "Uptime (h)", "Serial", "Collected"];

    public static IReadOnlyList<string> Row(DeviceInventory d, string group) =>
    [
        d.Name, group, d.Manufacturer, d.Model, d.AndroidVersion, d.Sdk > 0 ? d.Sdk.ToString() : "", d.SecurityPatch,
        d.BatteryLevel?.ToString() ?? "", d.Charging ? "yes" : "no", d.BatteryHealth, d.BatteryTempC?.ToString("0.0", CultureInfo.InvariantCulture) ?? "",
        Gb(d.StorageTotalMb), Gb(d.StorageFreeMb), d.StorageFreePercent?.ToString("0", CultureInfo.InvariantCulture) ?? "", Gb(d.RamTotalMb),
        d.Screen, d.IpAddress, d.Uptime?.TotalHours.ToString("0", CultureInfo.InvariantCulture) ?? "", d.Serial, d.CollectedAt.ToString("yyyy-MM-dd HH:mm"),
    ];

    static string Gb(long? mb) => mb is { } v ? (v / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) : "";

    public static string Csv(IEnumerable<(DeviceInventory D, string Group)> rows)
    {
        static string Q(string s) => s.IndexOfAny([',', '"', '\n', ';']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Columns.Select(Q)));
        foreach (var (d, g) in rows) sb.AppendLine(string.Join(',', Row(d, g).Select(Q)));
        return sb.ToString();
    }

    public static string Html(IEnumerable<(DeviceInventory D, string Group)> rows, string title)
    {
        static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
        var list = rows.ToList();
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html><head><meta charset=\"utf-8\"><title>{H(title)}</title><style>")
          .Append("body{font-family:Segoe UI,system-ui,sans-serif;background:#17181c;color:#eceef2;margin:24px}")
          .Append("h1{font-size:20px;margin:0 0 4px}p{color:#9a9ca6;margin:0 0 16px}")
          .Append("table{border-collapse:collapse;font-size:13px;width:100%}th,td{padding:7px 10px;border-bottom:1px solid #33353d;text-align:left;white-space:nowrap}")
          .Append("th{color:#9a9ca6;font-weight:600;position:sticky;top:0;background:#1b1c21}tr:hover td{background:#212228}")
          .Append(".warn{color:#e8b75a}.bad{color:#f07272}@media print{body{background:#fff;color:#000}th{background:#eee;color:#333}}")
          .Append("</style></head><body>")
          .Append($"<h1>{H(title)}</h1><p>{list.Count} devices · {DateTime.Now:yyyy-MM-dd HH:mm} · Paneless</p><table><thead><tr>");
        foreach (var c in Columns) sb.Append($"<th>{H(c)}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var (d, g) in list)
        {
            sb.Append("<tr>");
            var cells = Row(d, g);
            for (var i = 0; i < cells.Count; i++)
            {
                var cls = (Columns[i], d) switch
                {
                    ("Battery %", { BatteryLevel: <= 15, Charging: false }) => " class=\"bad\"",
                    ("Storage free %", _) when d.StorageFreePercent is < 10 => " class=\"bad\"",
                    ("Storage free %", _) when d.StorageFreePercent is < 20 => " class=\"warn\"",
                    _ => "",
                };
                sb.Append($"<td{cls}>{H(cells[i])}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }
}
