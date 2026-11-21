namespace Paneless.Core;

public enum ScheduleAction { RunScript, RunRecipe, Reboot, Shell }
public enum ScheduleRepeat { Daily, Weekly, EveryHours }

/// <summary>A task that runs by itself while Paneless is running (also in the notification area).</summary>
public sealed class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ScheduleAction Action { get; set; }
    /// <summary>Script or recipe id (RunScript / RunRecipe), or the command (Shell).</summary>
    public string Target { get; set; } = "";
    /// <summary>Group names and device ids (serial numbers) it runs on.</summary>
    public List<string> Groups { get; set; } = new();
    public List<string> Devices { get; set; } = new();
    public ScheduleRepeat Repeat { get; set; }
    /// <summary>Daily / Weekly: time of day, "HH:mm".</summary>
    public string Time { get; set; } = "03:00";
    /// <summary>Weekly: the days it runs.</summary>
    public List<DayOfWeek> Days { get; set; } = new() { DayOfWeek.Monday };
    /// <summary>EveryHours: how many hours apart.</summary>
    public int Hours { get; set; } = 6;
    public DateTime? LastRun { get; set; }
    public string LastResult { get; set; } = "";

    /// <summary>When it runs next, from <paramref name="now"/>.</summary>
    public DateTime? Next(DateTime now)
    {
        if (!Enabled) return null;
        if (Repeat == ScheduleRepeat.EveryHours)
        {
            var h = Math.Clamp(Hours, 1, 24 * 7);
            return LastRun is { } last ? (last.AddHours(h) > now ? last.AddHours(h) : now) : now;
        }
        if (!TimeSpan.TryParse(Time, out var tod)) tod = TimeSpan.FromHours(3);
        for (var d = 0; d <= 7; d++)
        {
            var day = now.Date.AddDays(d);
            var at = day + tod;
            if (at <= now) continue;
            if (Repeat == ScheduleRepeat.Weekly && !Days.Contains(day.DayOfWeek)) continue;
            return at;
        }
        return null;
    }

    /// <summary>True if a run was due between <paramref name="since"/> and <paramref name="now"/> (one check, one run).</summary>
    public bool IsDue(DateTime since, DateTime now)
    {
        if (!Enabled) return false;
        if (Repeat == ScheduleRepeat.EveryHours)
            return LastRun is not { } last || now - last >= TimeSpan.FromHours(Math.Clamp(Hours, 1, 24 * 7));
        var next = Next(since);
        return next is { } n && n <= now && (LastRun is not { } lr || lr < n);
    }
}

public sealed class ScheduleStore
{
    public const string FileName = "schedules.json";
    readonly bool _persist;
    public ScheduleStore() : this(JsonFile.Load<List<ScheduledTask>>(FileName), true) { }
    public ScheduleStore(List<ScheduledTask> tasks, bool persist) { All = tasks; _persist = persist; }
    public List<ScheduledTask> All { get; }
    public event Action? Changed;
    public void Save()
    {
        if (_persist) { try { JsonFile.Save(FileName, All); } catch { } }
        Changed?.Invoke();
    }
}
