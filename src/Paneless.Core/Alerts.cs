namespace Paneless.Core;

public enum AlertKind { Offline, LowBattery, LowStorage, Crash, Hot }

/// <summary>An alert rule: what to watch, on which devices.</summary>
public sealed class AlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public AlertKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>LowBattery: %, LowStorage: % free, Hot: °C, Offline: minutes away.</summary>
    public int Threshold { get; set; }
    /// <summary>Empty = every device; otherwise group names.</summary>
    public List<string> Groups { get; set; } = new();
    /// <summary>Also show a Windows notification (not only the list in Paneless).</summary>
    public bool Notify { get; set; } = true;

    public static AlertRule Default(AlertKind k) => new()
    {
        Kind = k,
        Threshold = k switch { AlertKind.LowBattery => 15, AlertKind.LowStorage => 10, AlertKind.Offline => 10, AlertKind.Hot => 45, _ => 0 },
        Enabled = k is AlertKind.LowBattery or AlertKind.Offline,
    };
}

/// <summary>Something that happened.</summary>
public sealed class AlertEvent
{
    public DateTime Time { get; set; } = DateTime.Now;
    public AlertKind Kind { get; set; }
    public string Device { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Resolved { get; set; }
}

public sealed class AlertData
{
    public List<AlertRule> Rules { get; set; } = new();
    public List<AlertEvent> Events { get; set; } = new();
}

public sealed class AlertStore
{
    public const string FileName = "alerts.json";
    readonly bool _persist;
    public AlertStore() : this(JsonFile.Load<AlertData>(FileName), true) { }
    public AlertStore(AlertData data, bool persist)
    {
        Data = data; _persist = persist;
        foreach (var k in Enum.GetValues<AlertKind>())
            if (!Data.Rules.Any(r => r.Kind == k)) Data.Rules.Add(AlertRule.Default(k));
    }
    public AlertData Data { get; }
    public event Action? Changed;

    public void Add(AlertEvent e)
    {
        Data.Events.Insert(0, e);
        if (Data.Events.Count > 500) Data.Events.RemoveRange(500, Data.Events.Count - 500);
        Save();
    }

    public void Save()
    {
        if (_persist) { try { JsonFile.Save(FileName, Data); } catch { } }
        Changed?.Invoke();
    }
}

/// <summary>Decides when an alert fires: once when a condition starts, again only after it cleared.</summary>
public sealed class AlertWatcher
{
    readonly HashSet<string> _active = new();          // "kind|device" currently raised
    readonly Dictionary<string, DateTime> _lastSeen = new();

    /// <summary>A device's latest readings. Returns the alerts that start now.</summary>
    public IEnumerable<(AlertRule Rule, string Text)> Check(AlertData data, string deviceId, string name, string? group, bool online,
        int? battery, bool charging, double? storageFreePercent, double? tempC, DateTime now)
    {
        if (online) _lastSeen[deviceId] = now;
        foreach (var rule in data.Rules.Where(r => r.Enabled && (r.Groups.Count == 0 || (group != null && r.Groups.Contains(group)))))
        {
            var key = rule.Kind + "|" + deviceId;
            string? text = rule.Kind switch
            {
                AlertKind.LowBattery when online && battery is { } b && b <= rule.Threshold && !charging => Text.F("{0}: battery at {1} %", name, b),
                AlertKind.LowStorage when online && storageFreePercent is { } f && f <= rule.Threshold => Text.F("{0}: only {1:0} % storage free", name, f),
                AlertKind.Hot when online && tempC is { } t && t >= rule.Threshold => Text.F("{0}: battery at {1:0.0} °C", name, t),
                AlertKind.Offline when !online && _lastSeen.TryGetValue(deviceId, out var seen) && now - seen >= TimeSpan.FromMinutes(Math.Max(1, rule.Threshold))
                    => Text.F("{0}: not on the network for {1} minutes", name, (int)(now - seen).TotalMinutes),
                _ => null,
            };
            if (text != null) { if (_active.Add(key)) yield return (rule, text); }
            else if (rule.Kind != AlertKind.Crash) _active.Remove(key);
        }
    }

    /// <summary>An app crashed (from the log). Crashes always fire.</summary>
    public static bool CrashRuleOn(AlertData data, string? group) =>
        data.Rules.Any(r => r.Kind == AlertKind.Crash && r.Enabled && (r.Groups.Count == 0 || (group != null && r.Groups.Contains(group))));
}
