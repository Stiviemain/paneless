using Paneless.App.Localization;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>One entry of a settings drop-down. Value null = "use the default".</summary>
public sealed record Choice(string Label, object? Value)
{
    public override string ToString() => Label;
}

/// <summary>A labelled drop-down that writes its value straight into a profile or the global settings.</summary>
public sealed partial class SettingRow : ObservableObject
{
    readonly Action<object?> _apply;

    public SettingRow(string label, string? tip, IReadOnlyList<Choice> choices, object? current, Action<object?> apply)
    {
        Label = label;
        Tip = tip;
        Choices = choices;
        _apply = apply;
        Selected = choices.FirstOrDefault(c => Equals(c.Value, current)) ?? choices[0];
        _ready = true;
    }

    readonly bool _ready;
    public string Label { get; }
    public string? Tip { get; }
    public IReadOnlyList<Choice> Choices { get; }
    [ObservableProperty] public partial Choice Selected { get; set; }
    partial void OnSelectedChanged(Choice value) { if (_ready && value != null) _apply(value.Value); }
}

/// <summary>A script that can run when a device connects.</summary>
public sealed partial class ScriptPick : ObservableObject
{
    public ScriptPick(AdbScript script, bool isChecked, string? inheritedFrom, Action changed)
    {
        Script = script;
        _isChecked = isChecked;
        InheritedFrom = inheritedFrom;
        _changed = changed;
    }

    readonly Action _changed;
    bool _isChecked;
    public AdbScript Script { get; }
    public string Name => Script.Name;
    public string Category => Script.BuiltIn ? Script.Category : Loc.T("My scripts");
    /// <summary>Set when the group already runs it: shown ticked and greyed.</summary>
    public string? InheritedFrom { get; }
    public bool Editable => InheritedFrom == null;
    public string Note => InheritedFrom != null ? Loc.F("from {0}", InheritedFrom) : Script.Confirm ? Loc.T("asks first when run by hand") : "";
    public bool IsChecked { get => _isChecked; set { if (SetProperty(ref _isChecked, value)) _changed(); } }
}

/// <summary>Option lists shared by the device, group and global settings.</summary>
public static class SettingChoices
{
    public static readonly string[] Sizes = ["Full resolution", "1920 px", "1600 px", "1280 px", "1024 px"];
    public static readonly (string Label, string Value)[] Codecs = [("H.264", "h264"), (Loc.T("H.265 (sharper)"), "h265"), (Loc.T("AV1 (newest devices)"), "av1")];
    public static readonly int[] Bitrates = [2, 4, 8, 12, 16, 24, 32, 50];
    public static readonly int[] Fps = [0, 30, 60, 90, 120];

    public static string OnOff(bool b) => b ? Loc.T("On") : Loc.T("Off");
    public static string Codec(string v) => Codecs.FirstOrDefault(c => c.Value == v).Label?.Split(' ')[0] ?? v;
    public static string FpsText(int v) => v == 0 ? Loc.T("No limit") : $"{v} fps";

    /// <summary>Mirroring display &amp; behavior rows. With <paramref name="inherited"/>, each row starts with "Default (…)".</summary>
    public static List<SettingRow> MirrorBehaviorRows(Func<DeviceProfile>? profile, EffectiveProfile? inherited, AppSettings? global, Action save)
    {
        var rows = new List<SettingRow>();
        void Bool(string label, string? tip, bool? current, bool fallback, Action<bool?> set)
        {
            var choices = new List<Choice>();
            if (inherited != null) choices.Add(new Choice(Loc.F("Default ({0})", OnOff(fallback)), null));
            choices.Add(new Choice(Loc.T("On"), true));
            choices.Add(new Choice(Loc.T("Off"), false));
            rows.Add(new SettingRow(label, tip, choices, inherited != null ? current : fallback, v => { set((bool?)v); save(); }));
        }

        var p = profile?.Invoke();
        var g = global;
        var e = inherited;
        Bool(Loc.T("Keep device screen off"), Loc.T("The device's own screen stays dark while it's mirrored."), p?.ScreenOff, e?.ScreenOff ?? g!.ScreenOff,
            v => { if (p != null) p.ScreenOff = v; else g!.ScreenOff = v ?? false; });
        Bool(Loc.T("Keep awake"), Loc.T("The device doesn't time out and lock while it's mirrored."), p?.KeepAwake, e?.KeepAwake ?? g!.KeepAwake,
            v => { if (p != null) p.KeepAwake = v; else g!.KeepAwake = v ?? false; });
        Bool(Loc.T("Lock device when mirror closes"), Loc.T("Puts the device to sleep (and locks it) when its last mirror or second-screen window closes."), p?.LockOnClose, e?.LockOnClose ?? g!.LockOnClose,
            v => { if (p != null) p.LockOnClose = v; else g!.LockOnClose = v ?? false; });
        Bool(Loc.T("Keep app windows on when locked"),
            Loc.T("Second screens and app windows (also remote apps) stay visible while the device is locked. Without it, some devices turn them black a few seconds after their own screen goes off. The device still locks and its own screen stays off."),
            p?.KeepScreensOn, e?.KeepScreensOn ?? g!.KeepScreensOn,
            v => { if (p != null) p.KeepScreensOn = v; else g!.KeepScreensOn = v ?? true; });
        Bool(Loc.T("Play device audio here"), null, p?.Audio, e?.Audio ?? g!.Audio,
            v => { if (p != null) p.Audio = v; else g!.Audio = v ?? false; });
        Bool(Loc.T("Mirror window always on top"), null, p?.OnTop, e?.OnTop ?? g!.OnTop,
            v => { if (p != null) p.OnTop = v; else g!.OnTop = v ?? false; });
        return rows;
    }

    /// <summary>Mirroring video quality rows. With <paramref name="inherited"/>, each row starts with "Default (…)".</summary>
    public static List<SettingRow> MirrorQualityRows(Func<DeviceProfile>? profile, EffectiveProfile? inherited, AppSettings? global, Action save)
    {
        var rows = new List<SettingRow>();
        void Pick<T>(string label, string? tip, IEnumerable<(string Label, T Value)> options, T? current, string fallbackLabel, Action<T?> set) where T : notnull
        {
            var choices = new List<Choice>();
            if (inherited != null) choices.Add(new Choice(Loc.F("Default ({0})", fallbackLabel), null));
            choices.AddRange(options.Select(o => new Choice(o.Label, o.Value)));
            rows.Add(new SettingRow(label, tip, choices, current, v => { set(v is T t ? t : default); save(); }));
        }

        var p = profile?.Invoke();
        var g = global;
        var e = inherited;
        Pick(Loc.T("Max size"), Loc.T("Longer side of the picture. Smaller is smoother on slow Wi-Fi."), Sizes.Select(s => (Loc.T(s), s)),
            p != null ? p.MaxSize : g!.MaxSize, Loc.T(e?.MaxSize ?? ""), v => { if (p != null) p.MaxSize = v; else if (v != null) g!.MaxSize = v; });
        Pick(Loc.T("Video codec"), Loc.T("H.265 and AV1 look better at the same bit rate if the device can encode them."), Codecs.Select(c => (c.Label, c.Value)),
            p != null ? p.VideoCodec : g!.VideoCodec, e != null ? Codec(e.VideoCodec) : "", v => { if (p != null) p.VideoCodec = v; else if (v != null) g!.VideoCodec = v; });
        Pick(Loc.T("Bit rate"), Loc.T("Higher is sharper but needs a stronger Wi-Fi connection."), Bitrates.Select(b => ($"{b} Mbit/s", (object)b)),
            p != null ? p.BitrateMbps : g!.BitrateMbps, e != null ? $"{e.BitrateMbps} Mbit/s" : "", v => { if (p != null) p.BitrateMbps = (int?)v; else if (v is int i) g!.BitrateMbps = i; });
        Pick(Loc.T("Frame rate limit"), Loc.T("Lower saves battery and Wi-Fi."), Fps.Select(f => (FpsText(f), (object)f)),
            p != null ? p.MaxFps : g!.MaxFps, e != null ? FpsText(e.MaxFps) : "", v => { if (p != null) p.MaxFps = (int?)v; else if (v is int i) g!.MaxFps = i; });
        return rows;
    }

    public static string AssistantLevelName(AssistantAccess a) => a switch
    {
        AssistantAccess.Off => Loc.T("Nothing (hidden from AI)"),
        AssistantAccess.Read => Loc.T("Read: status, apps, logs"),
        AssistantAccess.Screen => Loc.T("See: also screenshots"),
        AssistantAccess.Control => Loc.T("Control: also taps, typing, apps"),
        _ => Loc.T("Manage: also installs, files, commands"),
    };

    public static SettingRow AssistantRow(Func<DeviceProfile> profile, AssistantAccess inherited, Action save)
    {
        var choices = new List<Choice> { new(Loc.F("Default ({0})", AssistantLevelName(inherited).Split(':')[0]), null) };
        choices.AddRange(Enum.GetValues<AssistantAccess>().Select(a => new Choice(AssistantLevelName(a), a)));
        return new SettingRow(Loc.T("AI assistants"), null, choices, profile().Assistant, v => { profile().Assistant = (AssistantAccess?)v; save(); });
    }

    /// <summary>Opt-in background features powered by the on-device helper daemon (global, group or per-device).</summary>
    public static List<SettingRow> SyncRows(Func<DeviceProfile>? profile, EffectiveProfile? inherited, AppSettings? global, Action save)
    {
        var rows = new List<SettingRow>();
        void Bool(string label, string? tip, bool? current, bool fallback, Action<bool?> set)
        {
            var choices = new List<Choice>();
            if (inherited != null) choices.Add(new Choice(Loc.F("Default ({0})", OnOff(fallback)), null));
            choices.Add(new Choice(Loc.T("On"), true));
            choices.Add(new Choice(Loc.T("Off"), false));
            rows.Add(new SettingRow(label, tip, choices, inherited != null ? current : fallback, v => { set((bool?)v); save(); }));
        }

        var p = profile?.Invoke();
        var g = global;
        var e = inherited;
        Bool(Loc.T("Sync clipboard automatically"),
            Loc.T("Keeps plain-text clipboard in sync between this PC and the device even without a mirror open. Off on watches unless turned on per device."),
            p?.ClipboardSync, e?.ClipboardSync ?? g!.ClipboardSync,
            v => { if (p != null) p.ClipboardSync = v; else g!.ClipboardSync = v ?? false; });
        Bool(Loc.T("Auto-save new photos & screenshots"),
            Loc.T("Copies newly taken photos and screenshots to Documents\\Paneless\\Media automatically as you take them."),
            p?.AutoDownloadMedia, e?.AutoDownloadMedia ?? g!.AutoDownloadMedia,
            v => { if (p != null) p.AutoDownloadMedia = v; else g!.AutoDownloadMedia = v ?? false; });
        Bool(Loc.T("Auto-copy 2FA codes from notifications"),
            Loc.T("Opt-in: watches incoming notifications on the device to copy 2FA verification codes straight to your PC clipboard (uses on-device accessibility while connected)."),
            p?.AutoCopyOtp, e?.AutoCopyOtp ?? g!.AutoCopyOtp,
            v => { if (p != null) p.AutoCopyOtp = v; else g!.AutoCopyOtp = v ?? false; });
        return rows;
    }
}

/// <summary>Settings of one device or one group (the window opened with "Device settings…" / "Group settings…").</summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly DeviceGroup? _group;
    readonly string? _deviceId;

    /// <summary>For a device (<paramref name="deviceId"/> = serial number) or a group.</summary>
    public ProfileEditorViewModel(AppServices services, string? deviceId, string? deviceName, DeviceGroup? group)
    {
        _s = services;
        _deviceId = deviceId;
        _group = deviceId == null ? group : null;
        var memberOf = deviceId != null ? _s.Groups.GroupOf(deviceId) : null;

        Title = deviceId != null ? Loc.F("Settings for {0}", deviceName) : Loc.F("Settings for the group {0}", group!.Name);
        Subtitle = deviceId != null
            ? memberOf != null ? Loc.F("Anything left on Default uses the group {0}'s setting, or else the one in Paneless settings.", memberOf.Name) : Loc.T("Anything left on Default uses the setting in Paneless settings.")
            : Loc.T("These apply to every device in the group. A device's own settings win.");
        IsDevice = deviceId != null;

        // What "Default" means here: for a device, its group's settings over the global ones; for a group, the global ones.
        var inherited = EffectiveProfile.Resolve(_s.Data.Settings, deviceId != null ? memberOf?.Profile : null, null);
        BehaviorRows = new(SettingChoices.MirrorBehaviorRows(Profile, inherited, null, Save));
        QualityRows = new(SettingChoices.MirrorQualityRows(Profile, inherited, null, Save));
        SyncRows = new(SettingChoices.SyncRows(Profile, inherited, null, Save));
        AutoMirror = new SettingRow(Loc.T("Mirror by itself when it connects"), Loc.T("Opens the mirror as soon as the device is on the network and connected. Once per connection."),
            [new Choice(Loc.F("Default ({0})", SettingChoices.OnOff(inherited.AutoMirror)), null), new Choice(Loc.T("On"), true), new Choice(Loc.T("Off"), false)],
            Profile().AutoMirror, v => { Profile().AutoMirror = (bool?)v; Save(); });
        AssistantRow = SettingChoices.AssistantRow(Profile, inherited.Assistant, Save);
        RefreshUnlockStatus();

        var p = Profile();
        _loading = true;
        PushTarget = p.PushTarget ?? "";
        PushPlaceholder = Loc.F("Default: {0}", inherited.PushTarget);
        Hotkey = p.Hotkey ?? "";
        _loading = false;

        // Only scripts that can run unattended: no values to fill in. Ticked ones first.
        var fromGroup = deviceId != null ? memberOf?.Profile?.OnConnectScripts ?? [] : [];
        var picks = new List<ScriptPick>();
        foreach (var sc in _s.AllScripts().Where(sc => ScriptText.Placeholders(sc.Commands).All(ph => ph.Default != null) && ScriptText.Lines(sc.Commands).Count > 0))
        {
            var inheritedScript = fromGroup.Contains(sc.Id);
            picks.Add(new ScriptPick(sc, inheritedScript || p.OnConnectScripts.Contains(sc.Id), inheritedScript ? memberOf!.Name : null, SaveScripts));
        }
        foreach (var pick in picks.OrderByDescending(x => x.IsChecked)) Scripts.Add(pick);
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool IsDevice { get; }
    public ObservableCollection<SettingRow> BehaviorRows { get; }
    public ObservableCollection<SettingRow> QualityRows { get; }
    public ObservableCollection<SettingRow> SyncRows { get; }
    public SettingRow AutoMirror { get; }
    public SettingRow AssistantRow { get; }
    public string AssistantHeading => IsDevice
        ? Loc.T("What AI assistants may do on this device")
        : Loc.T("What AI assistants may do on this group");
    public Func<Task>? OpenAutoUnlockAsync { get; set; }
    public ObservableCollection<ScriptPick> Scripts { get; } = new();
    public string PushPlaceholder { get; }

    [ObservableProperty] public partial string UnlockStatusText { get; set; } = "";
    [ObservableProperty] public partial IBrush UnlockStatusBrush { get; set; } = Theme.Dim;
    [ObservableProperty] public partial string PushTarget { get; set; }
    [ObservableProperty] public partial string Hotkey { get; set; }
    [ObservableProperty] public partial string ScriptFilter { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial IBrush MessageBrush { get; set; } = Theme.Dim;

    public void RefreshUnlockStatus()
    {
        if (!IsDevice || _deviceId == null) return;
        if (_s.Demo)
        {
            var st = AppServices.DemoRows().FirstOrDefault(r => r.GroupId == _deviceId)?.Unlock ?? UnlockStatus.None;
            (UnlockStatusText, UnlockStatusBrush) = st switch
            {
                UnlockStatus.On => (Loc.T("Auto-unlock is on (code saved)."), Theme.Green),
                UnlockStatus.Paused => (Loc.T("Auto-unlock is paused after a failed try. Save the code again to turn it back on."), Theme.Amber),
                _ => (Loc.T("No unlock code saved yet."), Theme.Dim),
            };
            return;
        }
        if (_s.Unlock?.IsReady(_deviceId) == true)
        {
            UnlockStatusText = Loc.T("Auto-unlock is on (code saved).");
            UnlockStatusBrush = Theme.Green;
        }
        else if (_s.Unlock?.HasCode(_deviceId) == true)
        {
            UnlockStatusText = Loc.T("Auto-unlock is paused after a failed try. Save the code again to turn it back on.");
            UnlockStatusBrush = Theme.Amber;
        }
        else
        {
            UnlockStatusText = Loc.T("No unlock code saved yet.");
            UnlockStatusBrush = Theme.Dim;
        }
    }

    [RelayCommand]
    async Task OpenAutoUnlock()
    {
        if (OpenAutoUnlockAsync == null) return;
        await OpenAutoUnlockAsync();
        RefreshUnlockStatus();
    }

    public IEnumerable<ScriptPick> ShownScripts => ScriptFilter.Trim() is { Length: > 0 } q
        ? Scripts.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Category.Contains(q, StringComparison.OrdinalIgnoreCase))
        : Scripts;
    partial void OnScriptFilterChanged(string value) => OnPropertyChanged(nameof(ShownScripts));

    DeviceProfile Profile()
    {
        if (_deviceId != null) return _s.Profiles.GetOrCreate(_deviceId);
        return _group!.Profile ??= new DeviceProfile();
    }

    void Save()
    {
        if (_deviceId != null) _s.Profiles.Save();
        else _s.Groups.Save();
        Message = Loc.T("Saved. Applies the next time a mirror starts.");
        MessageBrush = Theme.Green;
    }

    bool _loading;

    partial void OnPushTargetChanged(string value)
    {
        if (_loading) return;
        var v = value.Trim();
        Profile().PushTarget = v.Length == 0 ? null : v.StartsWith('/') ? v : "/" + v;
        Save();
    }

    partial void OnHotkeyChanged(string value)
    {
        if (_loading) return;
        Profile().Hotkey = value.Length == 0 ? null : value;
        Save();
    }

    void SaveScripts()
    {
        var p = Profile();
        p.OnConnectScripts = Scripts.Where(s => s.IsChecked && s.Editable).Select(s => s.Script.Id).ToList();
        Save();
    }

    [RelayCommand]
    void ClearHotkey() => Hotkey = "";

    [RelayCommand]
    void ResetAll()
    {
        var p = Profile();
        var keepHotkey = p.Hotkey;
        p.ScreenOff = p.Audio = p.OnTop = p.KeepAwake = p.LockOnClose = p.KeepScreensOn = p.AutoMirror = null;
        p.ClipboardSync = p.AutoDownloadMedia = p.AutoCopyOtp = null;
        p.Assistant = null;
        p.MaxSize = p.VideoCodec = p.PushTarget = null;
        p.BitrateMbps = p.MaxFps = null;
        p.OnConnectScripts.Clear();
        p.Hotkey = keepHotkey;
        foreach (var r in BehaviorRows) r.Selected = r.Choices[0];
        foreach (var r in QualityRows) r.Selected = r.Choices[0];
        foreach (var r in SyncRows) r.Selected = r.Choices[0];
        AutoMirror.Selected = AutoMirror.Choices[0];
        AssistantRow.Selected = AssistantRow.Choices[0];
        foreach (var s in Scripts.Where(s => s.Editable)) s.IsChecked = false;
        PushTarget = "";
        Save();
        Message = Loc.T("Everything is back to Default.");
    }
}

/// <summary>Paneless settings: defaults for every device, background sync, updates, backup and diagnostics.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly AppServices _s;

    public SettingsViewModel(AppServices services)
    {
        _s = services;
        var g = _s.Data.Settings;
        QualityRows = new(SettingChoices.MirrorQualityRows(null, null, g, Save));
        _pushTarget = g.PushTarget;
        _updateSource = g.UpdateSource;
        _startLanguage = g.Language;
        _startWithSystem = Platform.Autostart.IsEnabled;
    }

    AppSettings G => _s.Data.Settings;
    void Save() { _s.SaveSettings(); Saved?.Invoke(); }

    /// <summary>Raised after every change (the main window refreshes its Mirror options).</summary>
    public event Action? Saved;
    /// <summary>Raised after importing a backup or resetting to defaults (the main window refreshes devices & pages).</summary>
    public event Action? Reloaded;
    /// <summary>Set by the app: checks the update source now and reports back.</summary>
    public Func<Task<string>>? CheckUpdatesAsync { get; set; }
    /// <summary>Set by the window: file pickers and confirmation dialog for backup & reset.</summary>
    public Func<string, Task<string?>>? PickSaveFileAsync { get; set; }
    public Func<Task<string?>>? PickOpenFileAsync { get; set; }
    public Func<string, string, string?, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty] public partial string Category { get; set; } = "General";
    public bool IsGeneral => Category == "General";
    public bool IsMirroring => Category == "Mirroring";
    public bool IsSync => Category == "Sync";
    public bool IsConnecting => Category == "Connecting";
    public bool IsUpdates => Category == "Updates";
    public bool IsAbout => Category == "About";

    partial void OnCategoryChanged(string value)
    {
        foreach (var p in new[] { nameof(IsGeneral), nameof(IsMirroring), nameof(IsSync), nameof(IsConnecting), nameof(IsUpdates), nameof(IsAbout) })
            OnPropertyChanged(p);
    }

    [RelayCommand]
    void SelectCategory(string category) => Category = category;

    public ObservableCollection<SettingRow> QualityRows { get; }
    public bool AutostartSupported => Platform.Autostart.IsSupported;

    public bool ScreenOff
    {
        get => G.ScreenOff;
        set { G.ScreenOff = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(KeepAwake)); OnPropertyChanged(nameof(KeepAwakeEnabled)); }
    }
    public bool KeepAwake { get => G.ScreenOff || G.KeepAwake; set { G.KeepAwake = value; Save(); OnPropertyChanged(); } }
    public bool KeepAwakeEnabled => !G.ScreenOff;
    public bool LockOnClose { get => G.LockOnClose; set { G.LockOnClose = value; Save(); OnPropertyChanged(); } }
    public bool KeepScreensOn { get => G.KeepScreensOn; set { G.KeepScreensOn = value; Save(); OnPropertyChanged(); } }
    public bool Audio { get => G.Audio; set { G.Audio = value; Save(); OnPropertyChanged(); } }
    public bool OnTop { get => G.OnTop; set { G.OnTop = value; Save(); OnPropertyChanged(); } }

    public bool ClipboardSync { get => G.ClipboardSync; set { G.ClipboardSync = value; Save(); OnPropertyChanged(); } }
    public bool AutoDownloadMedia { get => G.AutoDownloadMedia; set { G.AutoDownloadMedia = value; Save(); OnPropertyChanged(); } }
    public bool AutoCopyOtp { get => G.AutoCopyOtp; set { G.AutoCopyOtp = value; Save(); OnPropertyChanged(); } }

    public bool AutoConnect { get => G.AutoConnect; set { G.AutoConnect = value; Save(); OnPropertyChanged(); } }
    public bool DisconnectInBackground { get => G.DisconnectInBackground; set { G.DisconnectInBackground = value; Save(); OnPropertyChanged(); } }
    public bool CloseToTray { get => G.CloseToTray; set { G.CloseToTray = value; Save(); OnPropertyChanged(); } }
    public bool AutoUpdate { get => G.AutoUpdate; set { G.AutoUpdate = value; Save(); OnPropertyChanged(); } }
    public bool BetaUpdates { get => G.BetaUpdates; set { G.BetaUpdates = value; Save(); OnPropertyChanged(); } }

    public string ScreenOffHint => Loc.T("The device's own screen stays dark while it's mirrored.");
    public string KeepAwakeHint => Loc.T("The device doesn't time out and lock while it's mirrored.");
    public string LockOnCloseHint => Loc.T("Puts the device to sleep (and locks it) when its last mirror or second-screen window closes.");
    public string KeepScreensOnHint => Loc.T("Second screens and app windows (also remote apps) stay visible while the device is locked. Without it, some devices turn them black a few seconds after their own screen goes off. The device still locks and its own screen stays off.");
    public string ClipboardSyncHint => Loc.T("Keeps plain-text clipboard in sync between this PC and the device even without a mirror open. Off on watches unless turned on per device.");
    public string AutoDownloadMediaHint => Loc.T("Copies newly taken photos and screenshots to Documents\\Paneless\\Media automatically as you take them.");
    public string AutoCopyOtpHint => Loc.T("Opt-in: watches incoming notifications on the device to copy 2FA verification codes straight to your PC clipboard (uses on-device accessibility while connected).");
    public string AutoConnectHint => Loc.T("Devices you’ve paired before are connected as soon as they show up on the network. On Android 17, wireless debugging also turns itself back on when the device returns to a trusted Wi-Fi.");
    public string DisconnectInBackgroundHint => Loc.T("Saves phone battery when Paneless is minimized or in the notification area by dropping idle Wi-Fi connections after 5 minutes (unless a mirror, app window, tool or background sync is active). Reconnects automatically when you open Paneless or launch a shortcut.");
    public string CloseToTrayHint => Loc.T("Shortcuts, auto-connect and on-connect scripts keep working. Quit from the notification-area icon.");
    public string AutoUpdateHint => Loc.T("Paneless looks for a new version at start-up and every few hours, downloads it in the background and installs it when you close Paneless.");

    public string VersionText => $"Paneless {AppServices.Version}";
    [RelayCommand] void OpenLogsFolder() => Reveal.Folder(AppPaths.LogDir);
    [RelayCommand] void OpenDataFolder() => Reveal.Folder(AppPaths.DataDir);

    /// <summary>Appearance: applies immediately to every open window.</summary>
    public IReadOnlyList<Choice> ThemeChoices { get; } = Paneless.App.Theme.Modes.Select(m => new Choice(Loc.T(m.Name), m.Code)).ToList();
    public Choice Theme
    {
        get => ThemeChoices.FirstOrDefault(c => (string?)c.Value == G.Theme) ?? ThemeChoices[0];
        set
        {
            if (value == null) return;
            G.Theme = (string?)value.Value ?? "";
            Paneless.App.Theme.Apply(G.Theme);
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Language: takes effect after a restart (every window reads its texts once).</summary>
    public IReadOnlyList<Choice> LanguageChoices { get; } = Localization.Loc.Languages.Select(l => new Choice(l.Code == "" ? Localization.Loc.T(l.Name) : l.Name, l.Code)).ToList();
    readonly string _startLanguage = "";
    public Choice Language
    {
        get => LanguageChoices.FirstOrDefault(c => (string?)c.Value == G.Language) ?? LanguageChoices[0];
        set
        {
            if (value == null) return;
            G.Language = (string?)value.Value ?? "";
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LanguageChanged));
        }
    }
    public bool LanguageChanged => G.Language != _startLanguage;
    public string LanguageNote => Localization.Loc.T("Restart Paneless to switch the language.");

    bool _startWithSystem;
    public bool StartWithSystem
    {
        get => _startWithSystem;
        set
        {
            if (_s.Demo) { _startWithSystem = value; OnPropertyChanged(); return; }
            var ok = Platform.Autostart.Set(value);
            _startWithSystem = Platform.Autostart.IsEnabled;
            UpdateStatus = ok ? "" : Loc.T("Couldn't change the start-up setting.");
            OnPropertyChanged();
        }
    }

    string _pushTarget;
    public string PushTarget
    {
        get => _pushTarget;
        set
        {
            _pushTarget = value;
            var v = value.Trim();
            if (v.Length > 0) { G.PushTarget = v.StartsWith('/') ? v : "/" + v; Save(); }
            OnPropertyChanged();
        }
    }

    string _updateSource;
    public string UpdateSource
    {
        get => _updateSource;
        set { _updateSource = value; G.UpdateSource = value.Trim(); Save(); OnPropertyChanged(); }
    }

    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";
    [ObservableProperty] public partial bool IsChecking { get; set; }

    [ObservableProperty] public partial bool ExportIncludeDevices { get; set; } = true;
    [ObservableProperty] public partial string BackupStatus { get; set; } = "";
    [ObservableProperty] public partial IBrush BackupStatusBrush { get; set; } = Paneless.App.Theme.Dim;
    public bool HasBackupStatus => BackupStatus.Length > 0;
    partial void OnBackupStatusChanged(string value) => OnPropertyChanged(nameof(HasBackupStatus));

    void SetBackupStatus(string text, StatusKind kind = StatusKind.Info)
    {
        BackupStatus = text;
        BackupStatusBrush = Paneless.App.Theme.For(kind);
    }

    public void RefreshFromStore()
    {
        QualityRows.Clear();
        foreach (var r in SettingChoices.MirrorQualityRows(null, null, G, Save)) QualityRows.Add(r);
        _pushTarget = G.PushTarget;
        _updateSource = G.UpdateSource;
        _startWithSystem = Platform.Autostart.IsEnabled;
        OnPropertyChanged(nameof(ScreenOff));
        OnPropertyChanged(nameof(KeepAwake));
        OnPropertyChanged(nameof(KeepAwakeEnabled));
        OnPropertyChanged(nameof(LockOnClose));
        OnPropertyChanged(nameof(KeepScreensOn));
        OnPropertyChanged(nameof(Audio));
        OnPropertyChanged(nameof(OnTop));
        OnPropertyChanged(nameof(ClipboardSync));
        OnPropertyChanged(nameof(AutoDownloadMedia));
        OnPropertyChanged(nameof(AutoCopyOtp));
        OnPropertyChanged(nameof(AutoConnect));
        OnPropertyChanged(nameof(DisconnectInBackground));
        OnPropertyChanged(nameof(CloseToTray));
        OnPropertyChanged(nameof(AutoUpdate));
        OnPropertyChanged(nameof(BetaUpdates));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(LanguageChanged));
        OnPropertyChanged(nameof(PushTarget));
        OnPropertyChanged(nameof(UpdateSource));
        OnPropertyChanged(nameof(StartWithSystem));
    }

    [RelayCommand]
    async Task CheckUpdates()
    {
        if (CheckUpdatesAsync == null) return;
        IsChecking = true;
        UpdateStatus = Loc.T("Checking…");
        try { UpdateStatus = await CheckUpdatesAsync(); }
        finally { IsChecking = false; }
    }

    [RelayCommand]
    async Task ExportSettings()
    {
        if (PickSaveFileAsync == null) return;
        var prefix = ExportIncludeDevices ? "paneless-backup" : "paneless-settings";
        var path = await PickSaveFileAsync($"{prefix}-{DateTime.Now:yyyy-MM-dd}");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var json = SettingsBackup.ExportJson(
                _s.Data.Settings,
                _s.Scripts.Mine,
                _s.Keymaps.Data,
                _s.Recipes.All,
                _s.Schedules.All,
                _s.Alerts.Data,
                includeDevices: ExportIncludeDevices,
                knownDevices: _s.Data.Known,
                groups: _s.Groups.Data,
                profiles: _s.Profiles.All,
                remoteApps: _s.RemoteApps.All);
            await File.WriteAllTextAsync(path, json);
            SetBackupStatus(
                ExportIncludeDevices
                    ? Loc.T("Settings and saved devices exported.")
                    : Loc.T("Settings exported."),
                StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetBackupStatus(Loc.F("Couldn't export settings: {0}", ex.Message), StatusKind.Error);
        }
    }

    [RelayCommand]
    async Task ImportSettings()
    {
        if (PickOpenFileAsync == null) return;
        var path = await PickOpenFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            if (!SettingsBackup.TryParse(json, out var backup, out var error) || backup == null)
            {
                SetBackupStatus(error ?? Loc.T("This file doesn't look like a Paneless settings backup."), StatusKind.Error);
                return;
            }

            var prompt = backup.IncludesDevices
                ? Loc.F("Replace your Paneless settings, scripts, automation, saved devices ({0}), groups ({1}) and remote apps ({2}) with this backup? Unlock codes are kept as they are.",
                    backup.KnownDevices?.Count ?? 0,
                    backup.Groups?.Groups.Count ?? 0,
                    backup.RemoteApps?.Count ?? 0)
                : Loc.T("Replace your Paneless settings, scripts and automation with this backup? Your saved devices, groups and remote apps stay untouched.");

            if (ConfirmAsync != null && !await ConfirmAsync(Loc.T("Import settings"), prompt, null)) return;

            if (_s.Demo)
            {
                SetBackupStatus(Loc.T("Demo mode: backup looks valid (nothing was changed)."), StatusKind.Ok);
                return;
            }

            SettingsBackup.ApplyTo(
                backup,
                _s.Data,
                _s.Scripts,
                _s.Keymaps,
                _s.Recipes,
                _s.Schedules,
                _s.Alerts,
                _s.Groups,
                _s.Profiles,
                _s.RemoteApps);

            Paneless.App.Theme.Apply(G.Theme);
            RefreshFromStore();
            Saved?.Invoke();
            Reloaded?.Invoke();

            SetBackupStatus(
                backup.IncludesDevices
                    ? Loc.T("Settings and saved devices imported.")
                    : Loc.T("Settings imported."),
                StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetBackupStatus(Loc.F("Couldn't import settings: {0}", ex.Message), StatusKind.Error);
        }
    }

    [RelayCommand]
    async Task ResetToDefaults()
    {
        if (ConfirmAsync != null && !await ConfirmAsync(
            Loc.T("Reset to factory defaults"),
            Loc.T("Reset all Paneless settings, scripts, automation, saved devices, groups, remote apps and unlock codes back to factory defaults? This can't be undone."),
            Loc.T("Reset")))
            return;

        if (_s.Demo)
        {
            SetBackupStatus(Loc.T("Demo mode: nothing was reset."), StatusKind.Ok);
            return;
        }

        try
        {
            SettingsBackup.ResetToDefaults(
                _s.Data,
                _s.Scripts,
                _s.Keymaps,
                _s.Recipes,
                _s.Schedules,
                _s.Alerts,
                _s.Groups,
                _s.Profiles,
                _s.RemoteApps,
                RemoteAppStore.IconDir);

            if (Platform.Autostart.IsSupported && Platform.Autostart.IsEnabled)
                Platform.Autostart.Set(false);

            Paneless.App.Theme.Apply(G.Theme);
            RefreshFromStore();
            Saved?.Invoke();
            Reloaded?.Invoke();
            SetBackupStatus(Loc.T("Everything was reset to factory defaults."), StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetBackupStatus(Loc.F("Couldn't reset settings: {0}", ex.Message), StatusKind.Error);
        }
    }
}
