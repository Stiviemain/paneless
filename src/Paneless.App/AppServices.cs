using Paneless.Core;

namespace Paneless.App;

/// <summary>Everything the windows need, created once at start-up.</summary>
public sealed class AppServices
{
    public ToolPaths? Tools { get; }
    public DataStore Data { get; }
    public KeymapStore Keymaps { get; } = new();
    public AdbClient? Adb { get; }
    public DeviceDirectory? Directory { get; }
    public MirrorManager? Mirrors { get; }
    public UnlockService? Unlock { get; }
    public bool Demo { get; }

    public AppServices(bool demo)
    {
        Demo = demo;
        AppPaths.EnsureDataDir();
        Data = new DataStore();
        Tools = ToolLocator.Find();
        if (Tools == null) return;
        Adb = new AdbClient(Tools.Adb);
        Directory = new DeviceDirectory(Adb, Data);
        Mirrors = new MirrorManager(Tools);
        Unlock = new UnlockService(Adb, Data, SecretVault.ForThisOs());
    }

    public static string Version =>
        typeof(AppServices).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "1.0";

    /// <summary>Device groups. In demo mode an in-memory example set, so screenshots never touch your real groups.</summary>
    public GroupStore Groups => _groups ??= Demo ? DemoGroups() : new GroupStore();
    GroupStore? _groups;

    /// <summary>Per-device settings. In demo mode in memory only.</summary>
    public ProfileStore Profiles => _profiles ??= Demo ? new ProfileStore(new(), persist: false) : new ProfileStore();
    ProfileStore? _profiles;

    /// <summary>Provisioning recipes, scheduled tasks and alerts. In demo mode examples in memory only.</summary>
    public RecipeStore Recipes => _recipes ??= Demo ? new RecipeStore(DemoData.Recipes(), false) : new RecipeStore();
    RecipeStore? _recipes;
    public ScheduleStore Schedules => _schedules ??= Demo ? new ScheduleStore(DemoData.Schedules(), false) : new ScheduleStore();
    ScheduleStore? _schedules;
    public AlertStore Alerts => _alerts ??= Demo ? new AlertStore(DemoData.Alerts(), false) : new AlertStore();
    AlertStore? _alerts;

    /// <summary>Remote apps: Android apps in windows of their own. In demo mode examples in memory only.</summary>
    public RemoteAppStore RemoteApps => _remoteApps ??= Demo ? new RemoteAppStore(DemoData.RemoteApps(), false) : new RemoteAppStore();
    RemoteAppStore? _remoteApps;

    /// <summary>The latest inventory read-out per device id (serial number), shared by Inventory, Alerts and Claude.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, DeviceInventory> Inventory { get; } = new();

    /// <summary>A script by id (yours or built-in).</summary>
    public AdbScript? FindScript(string id) => AllScripts().FirstOrDefault(s => s.Id == id);

    /// <summary>Saves settings.json (not in demo mode).</summary>
    public void SaveSettings() { if (Demo) return; try { Data.SaveSettings(); } catch { /* not fatal */ } }

    /// <summary>The settings that apply to a device: its own, else its group's, else the defaults.</summary>
    public EffectiveProfile EffectiveFor(string groupId) =>
        EffectiveProfile.Resolve(Data.Settings, Groups.GroupOf(groupId)?.Profile, Profiles.Get(groupId));

    /// <summary>User scripts. In demo mode in memory only so real scripts.json is never touched.</summary>
    public ScriptStore Scripts => _scripts ??= Demo ? new ScriptStore([], persist: false) : new ScriptStore();
    ScriptStore? _scripts;

    /// <summary>All scripts (yours first, then the built-ins).</summary>
    public IReadOnlyList<AdbScript> AllScripts() => [.. Scripts.Mine, .. BuiltInScripts.All];

    /// <summary>Name/model/type of a device that has been connected before (by serial number).</summary>
    public KnownDevice? KnownFor(string sn) =>
        Demo ? new KnownDevice { Name = $"Lab phone {sn[1..]}", Model = "Pixel 9a", Type = "Phone" }
             : Data.Known.GetValueOrDefault(sn);

    /// <summary>Every device that has been connected before, by serial number (none in demo mode).</summary>
    public IEnumerable<KeyValuePair<string, KnownDevice>> KnownDevices() => Demo ? [] : Data.Known;

    public static IReadOnlyList<DeviceRow> DemoRows()
    {
        var rows = new List<DeviceRow>
        {
            new("d1", "192.168.2.41:37105", "A", true, "Pixel 11 Pro", "Pixel 11 Pro", "Phone", "192.168.2.41:37105", DeviceState.Mirroring, "192.168.2.41:37105", null, UnlockStatus.On),
            new("d2", "192.168.2.52:40211", "B", true, "Redmi Pad Pro", "2405CRPFDG", "Tablet", "192.168.2.52:40211", DeviceState.Connected, "192.168.2.52:40211", null, UnlockStatus.Paused),
            new("d3", null, "C", true, "Pixel Watch 5", "Pixel Watch 5", "Watch", "192.168.2.57:41877", DeviceState.Available, "192.168.2.57:41877", null, UnlockStatus.None),
            new("d4", null, "D", true, "New device", "", "", "192.168.2.63:38211", DeviceState.Unpaired, "192.168.2.63:38211", null, UnlockStatus.None),
        };
        // A bigger fleet, for trying out groups.
        for (var i = 1; i <= 6; i++)
            rows.Add(new($"l{i}", $"192.168.3.{10 + i}:5555", $"L0{i}", true, $"Lab phone 0{i}", "Pixel 9a", "Phone", $"192.168.3.{10 + i}:5555",
                i == 2 ? DeviceState.Mirroring : i == 5 ? DeviceState.Available : DeviceState.Connected, $"192.168.3.{10 + i}:5555", null, UnlockStatus.On));
        for (var i = 1; i <= 5; i++)
            rows.Add(new($"k{i}", $"192.168.4.{20 + i}:5555", $"K0{i}", true, $"Lobby tablet {i}", "Galaxy Tab A9", "Tablet", $"192.168.4.{20 + i}:5555",
                DeviceState.Connected, $"192.168.4.{20 + i}:5555", null, UnlockStatus.None));
        return rows;
    }

    static GroupStore DemoGroups() => new(new GroupData
    {
        Groups =
        [
            new DeviceGroup { Name = "My devices", Members = ["A", "B", "C"] },
            new DeviceGroup { Name = "Test lab", Members = ["L01", "L02", "L03", "L04", "L05", "L06", "L07"] },
            new DeviceGroup { Name = "Lobby tablets", Collapsed = true, Members = ["K01", "K02", "K03", "K04", "K05"] },
        ],
    }, persist: false);
}
