namespace Paneless.Core;

/// <summary>A named set of devices, shown as a collapsible section in the device list and offered as a
/// target in Scripts. Members are device serial numbers (ro.serialno), which stay the same across
/// reconnects and IP changes.</summary>
public sealed class DeviceGroup
{
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    public List<string> Members { get; set; } = new();
    /// <summary>Settings for every device in the group (a device's own settings win).</summary>
    public DeviceProfile? Profile { get; set; }
}

public sealed class GroupData
{
    public List<DeviceGroup> Groups { get; set; } = new();
    /// <summary>Collapsed state of the "Other devices" section (devices in no group).</summary>
    public bool OthersCollapsed { get; set; }
}

/// <summary>groups.json. A device is in at most one group, like a file in a folder.</summary>
public sealed class GroupStore
{
    public const string FileName = "groups.json";
    readonly bool _persist;

    public GroupStore() : this(JsonFile.Load<GroupData>(FileName), persist: true) { }

    /// <summary>For demo mode and tests: nothing is written to disk.</summary>
    public GroupStore(GroupData data, bool persist)
    {
        Data = data;
        _persist = persist;
        Data.Groups.RemoveAll(g => string.IsNullOrWhiteSpace(g.Name));
    }

    public GroupData Data { get; }
    public IReadOnlyList<DeviceGroup> Groups => Data.Groups;
    public bool Any => Data.Groups.Count > 0;

    /// <summary>Raised after every change (the device list and the Scripts window both listen).</summary>
    public event Action? Changed;

    public DeviceGroup? GroupOf(string? id) => id == null ? null : Data.Groups.FirstOrDefault(g => g.Members.Contains(id));

    public DeviceGroup? Find(string name) =>
        Data.Groups.FirstOrDefault(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A usable group name, or null (empty, or too long to show).</summary>
    public static string? Clean(string? name)
    {
        var n = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return n.Length is > 0 and <= 60 ? n : null;
    }

    /// <summary>Returns the group with this name, creating it if needed.</summary>
    public DeviceGroup Create(string name)
    {
        var clean = Clean(name) ?? throw new ArgumentException(Text.T("A group needs a name."));
        if (Find(clean) is { } existing) return existing;
        var g = new DeviceGroup { Name = clean };
        Data.Groups.Add(g);
        Save();
        return g;
    }

    /// <summary>Moves devices into a group, or out of every group when <paramref name="group"/> is null.</summary>
    public void Assign(IEnumerable<string> ids, DeviceGroup? group)
    {
        var list = ids.Distinct().ToList();
        foreach (var g in Data.Groups) g.Members.RemoveAll(list.Contains);
        if (group != null)
        {
            group.Members.AddRange(list);
            group.Collapsed = false;   // show where they went
        }
        Save();
    }

    /// <summary>False when another group already has that name.</summary>
    public bool Rename(DeviceGroup group, string name)
    {
        var clean = Clean(name);
        if (clean == null) return false;
        if (Find(clean) is { } other && other != group) return false;
        group.Name = clean;
        Save();
        return true;
    }

    /// <summary>Deletes the group; its devices go back to "Other devices".</summary>
    public void Delete(DeviceGroup group)
    {
        Data.Groups.Remove(group);
        Save();
    }

    public void Move(DeviceGroup group, int delta)
    {
        var i = Data.Groups.IndexOf(group);
        var j = Math.Clamp(i + delta, 0, Data.Groups.Count - 1);
        if (i < 0 || i == j) return;
        Data.Groups.RemoveAt(i);
        Data.Groups.Insert(j, group);
        Save();
    }

    /// <summary>Collapses or expands a group; null means the "Other devices" section.</summary>
    public void SetCollapsed(DeviceGroup? group, bool collapsed)
    {
        if (group == null) Data.OthersCollapsed = collapsed;
        else group.Collapsed = collapsed;
        Save();
    }

    public void Save()
    {
        if (_persist) { try { JsonFile.Save(FileName, Data); } catch { /* not fatal: the change still applies until restart */ } }
        Changed?.Invoke();
    }
}
