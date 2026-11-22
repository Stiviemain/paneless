using Paneless.Core;

namespace Paneless.App;

/// <summary>Examples for demo mode (screenshots, trying the app without devices). Never saved.</summary>
public static class DemoData
{
    public static List<Recipe> Recipes() =>
    [
        new Recipe
        {
            Id = "demo-lab", Name = "New lab phone", Description = "Test app, developer settings and no animations.",
            Steps =
            [
                new RecipeStep { Kind = RecipeStepKind.InstallApk, Path = @"C:\Builds\testapp-release.apk" },
                new RecipeStep { Kind = RecipeStepKind.RunScript, ScriptId = "builtin:Animations off" },
                new RecipeStep { Kind = RecipeStepKind.RunScript, ScriptId = "builtin:Stay awake while charging" },
                new RecipeStep { Kind = RecipeStepKind.Shell, Command = "settings put global auto_time 1" },
            ],
        },
        new Recipe
        {
            Id = "demo-kiosk", Name = "Lobby tablet", Description = "Kiosk browser, brightness, files.",
            Steps =
            [
                new RecipeStep { Kind = RecipeStepKind.InstallApk, Path = @"C:\Builds\kiosk-browser.apk" },
                new RecipeStep { Kind = RecipeStepKind.CopyFile, Path = @"C:\Builds\welcome.mp4", Target = "/sdcard/Movies/" },
                new RecipeStep { Kind = RecipeStepKind.Shell, Command = "settings put system screen_brightness 200" },
                new RecipeStep { Kind = RecipeStepKind.Reboot },
            ],
        },
    ];

    public static List<ScheduledTask> Schedules() =>
    [
        new ScheduledTask { Name = "Restart lobby tablets", Action = ScheduleAction.Reboot, Groups = ["Lobby tablets"], Repeat = ScheduleRepeat.Daily, Time = "04:00",
                            LastRun = DateTime.Today.AddDays(-1).AddHours(4), LastResult = "5 of 5 devices" },
        new ScheduledTask { Name = "Doze off on the lab", Action = ScheduleAction.RunScript, Target = "builtin:Turn off Doze (deep sleep)", Groups = ["Test lab"],
                            Repeat = ScheduleRepeat.EveryHours, Hours = 6, LastRun = DateTime.Now.AddHours(-2), LastResult = "6 of 6 devices" },
        new ScheduledTask { Name = "Weekly set-up check", Action = ScheduleAction.RunRecipe, Target = "demo-lab", Groups = ["Test lab"], Repeat = ScheduleRepeat.Weekly,
                            Days = [DayOfWeek.Monday], Time = "07:30", Enabled = false },
    ];

    public static AlertData Alerts() => new()
    {
        Events =
        [
            new AlertEvent { Kind = AlertKind.LowBattery, Device = "Lab phone 05", Text = "Lab phone 05: battery at 12 %", Time = DateTime.Now.AddMinutes(-18) },
            new AlertEvent { Kind = AlertKind.Offline, Device = "Lab phone 07", Text = "Lab phone 07: not on the network for 14 minutes", Time = DateTime.Now.AddHours(-1) },
            new AlertEvent { Kind = AlertKind.Crash, Device = "Lab phone 02", Text = "Lab phone 02: com.example.testapp crashed (NullPointerException)", Time = DateTime.Now.AddHours(-3) },
        ],
    };

    /// <summary>Made-up inventory for demo rows.</summary>
    /// <summary>Example remote apps (letter tiles, no real logos).</summary>
    public static List<RemoteApp> RemoteApps() =>
    [
        new() { Id = "demo-chat", DeviceId = "A", Package = "com.example.chat", Name = "Chat", ShowToolbar = false,
                Shortcuts = [@"C:\Users\you\Desktop\Chat.lnk"] },
        new() { Id = "demo-music", DeviceId = "B", Package = "com.example.music", Name = "Music", Shape = "tablet" },
        new() { Id = "demo-notes", DeviceId = "A", Package = "com.example.notes", Name = "Notes", OnTop = true },
        new() { Id = "demo-kiosk", DeviceId = "K01", Package = "com.example.kiosk", Name = "Lobby kiosk", Shape = "tablet", ScreenOff = false },
    ];

    public static DeviceInventory Inventory(DeviceRow r)
    {
        var h = Math.Abs(r.Key.GetHashCode());
        var phone = r.Type != "Tablet";
        return new DeviceInventory(r.Name, r.Sn ?? r.Key, r.Model.StartsWith("Pixel") ? "Google" : r.Model.StartsWith("Galaxy") ? "Samsung" : "Xiaomi",
            r.Model, r.Model.StartsWith("Pixel 11") ? "17" : "15", r.Model.StartsWith("Pixel 11") ? 37 : 35, "2026-11-05",
            35 + h % 64, h % 3 == 0, "Good", 28 + h % 9, phone ? 256 * 1024 : 128 * 1024, (phone ? 256 : 128) * 1024 * (5 + h % 60) / 100,
            phone ? 12 * 1024 : 8 * 1024, phone ? "1280x2856" : "1600x2560", r.Address.Split(':')[0], TimeSpan.FromHours(3 + h % 200), DateTime.Now);
    }
}
