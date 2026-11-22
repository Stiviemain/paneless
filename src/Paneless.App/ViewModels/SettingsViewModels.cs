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

    /// <summary>The rows for the mirroring options. With <paramref name="inherited"/>, each row starts with "Default (…)".</summary>
    public static List<SettingRow> MirrorRows(Func<DeviceProfile>? profile, EffectiveProfile? inherited, AppSettings? global, Action save)
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
        Rows = new(SettingChoices.MirrorRows(Profile, inherited, null, Save));
        SyncRows = new(SettingChoices.SyncRows(Profile, inherited, null, Save));
        AutoMirror = new SettingRow(Loc.T("Mirror by itself when it connects"), Loc.T("Opens the mirror as soon as the device is on the network and connected. Once per connection."),
            [new Choice(Loc.F("Default ({0})", SettingChoices.OnOff(inherited.AutoMirror)), null), new Choice(Loc.T("On"), true), new Choice(Loc.T("Off"), false)],
            Profile().AutoMirror, v => { Profile().AutoMirror = (bool?)v; Save(); });

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
    public ObservableCollection<SettingRow> Rows { get; }
    public ObservableCollection<SettingRow> SyncRows { get; }
    public SettingRow AutoMirror { get; }
    public ObservableCollection<ScriptPick> Scripts { get; } = new();
    public string PushPlaceholder { get; }

    [ObservableProperty] public partial string PushTarget { get; set; }
    [ObservableProperty] public partial string Hotkey { get; set; }
    [ObservableProperty] public partial string ScriptFilter { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial IBrush MessageBrush { get; set; } = Theme.Dim;

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
        p.MaxSize = p.VideoCodec = p.PushTarget = null;
        p.BitrateMbps = p.MaxFps = null;
        p.OnConnectScripts.Clear();
        p.Hotkey = keepHotkey;
        foreach (var r in Rows) r.Selected = r.Choices[0];
        foreach (var r in SyncRows) r.Selected = r.Choices[0];
        AutoMirror.Selected = AutoMirror.Choices[0];
        foreach (var s in Scripts.Where(s => s.Editable)) s.IsChecked = false;
        PushTarget = "";
        Save();
        Message = Loc.T("Everything is back to Default.");
    }
}

/// <summary>Paneless settings: defaults for every device, start-up and tray, updates.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    readonly AppServices _s;

    public SettingsViewModel(AppServices services)
    {
        _s = services;
        var g = _s.Data.Settings;
        Rows = new(SettingChoices.MirrorRows(null, null, g, Save));
        SyncRows = new(SettingChoices.SyncRows(null, null, g, Save));
        _pushTarget = g.PushTarget;
        _updateSource = g.UpdateSource;
        _startLanguage = g.Language;
        _startWithSystem = Platform.Autostart.IsEnabled;
    }

    AppSettings G => _s.Data.Settings;
    void Save() { _s.SaveSettings(); Saved?.Invoke(); }

    /// <summary>Raised after every change (the main window refreshes its Mirror options).</summary>
    public event Action? Saved;
    /// <summary>Set by the app: checks the update source now and reports back.</summary>
    public Func<Task<string>>? CheckUpdatesAsync { get; set; }

    public ObservableCollection<SettingRow> Rows { get; }
    public ObservableCollection<SettingRow> SyncRows { get; }
    public bool AutostartSupported => Platform.Autostart.IsSupported;

    public bool AutoConnect { get => G.AutoConnect; set { G.AutoConnect = value; Save(); OnPropertyChanged(); } }
    public bool CloseToTray { get => G.CloseToTray; set { G.CloseToTray = value; Save(); OnPropertyChanged(); } }
    public bool AutoUpdate { get => G.AutoUpdate; set { G.AutoUpdate = value; Save(); OnPropertyChanged(); } }
    public bool BetaUpdates { get => G.BetaUpdates; set { G.BetaUpdates = value; Save(); OnPropertyChanged(); } }

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

    [RelayCommand]
    async Task CheckUpdates()
    {
        if (CheckUpdatesAsync == null) return;
        IsChecking = true;
        UpdateStatus = Loc.T("Checking…");
        try { UpdateStatus = await CheckUpdatesAsync(); }
        finally { IsChecking = false; }
    }
}
