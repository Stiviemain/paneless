using System.Text.Json.Nodes;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.App.Localization;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>Quick system toggles, brightness/volume sliders, and media playback controls for one device.</summary>
public sealed partial class QuickControlsViewModel : ObservableObject
{
    readonly AppServices _s;
    readonly DeviceRow _row;
    bool _loading;

    public QuickControlsViewModel(AppServices services, DeviceRow row)
    {
        _s = services;
        _row = row;
        Title = Loc.F("Quick controls — {0}", row.Name);
    }

    public string Title { get; }

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool DarkMode { get; set; }
    [ObservableProperty] public partial bool Dnd { get; set; }
    [ObservableProperty] public partial bool AutoRotate { get; set; }
    [ObservableProperty] public partial bool Wifi { get; set; }
    [ObservableProperty] public partial bool Bluetooth { get; set; }
    [ObservableProperty] public partial double Brightness { get; set; } = 128;
    [ObservableProperty] public partial double MusicVolume { get; set; } = 8;
    [ObservableProperty] public partial double MusicMax { get; set; } = 15;
    [ObservableProperty] public partial double RingVolume { get; set; } = 4;
    [ObservableProperty] public partial double RingMax { get; set; } = 7;

    [ObservableProperty] public partial bool MediaPlaying { get; set; }
    [ObservableProperty] public partial string MediaTitle { get; set; } = Loc.T("Nothing playing");
    [ObservableProperty] public partial string MediaArtist { get; set; } = "";
    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial IBrush MessageBrush { get; set; } = Theme.Dim;

    public string PlayPauseLabel => MediaPlaying ? Loc.T("Pause") : Loc.T("Play");
    partial void OnMediaPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseLabel));

    void Say(string text, StatusKind kind = StatusKind.Info) { Message = text; MessageBrush = Theme.For(kind); }

    async Task<DeviceHelperClient?> HelperAsync()
    {
        var serial = await _s.Directory!.ConnectAsync(_row);
        if (serial == null || _s.Helpers == null) return null;
        return await _s.Helpers.GetOrStartAsync(serial, _row.Type == "Watch");
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        _loading = true;
        Say(Loc.T("Reading device controls…"));
        try
        {
            if (_s.Demo)
            {
                DarkMode = true; Dnd = false; AutoRotate = true; Wifi = true; Bluetooth = true;
                Brightness = 175; MusicMax = 25; MusicVolume = 14; RingMax = 7; RingVolume = 5;
                MediaPlaying = true; MediaTitle = "Demo Track"; MediaArtist = "Demo Artist";
                Say(Loc.T("Ready."));
                return;
            }
            var helper = await HelperAsync();
            if (helper == null || !helper.Supports("sys_get")) { Say(Loc.T("Couldn't reach the on-device helper."), StatusKind.Error); return; }
            var sys = await helper.GetSystemStateAsync();
            if (sys != null)
            {
                DarkMode = sys.DarkMode;
                Dnd = sys.Dnd;
                AutoRotate = sys.AutoRotate;
                Wifi = sys.Wifi;
                Bluetooth = sys.Bluetooth;
                Brightness = sys.Brightness;
                MusicMax = Math.Max(1, sys.MusicMax);
                MusicVolume = sys.MusicVolume;
                RingMax = Math.Max(1, sys.RingMax);
                RingVolume = sys.RingVolume;
            }
            if (helper.Supports("media_state") && await helper.GetMediaStateAsync() is { } media)
            {
                MediaPlaying = media.Playing;
                MediaTitle = media.Title.Length > 0 ? media.Title : Loc.T("Nothing playing");
                MediaArtist = media.Artist;
            }
            Say(Loc.T("Ready."), StatusKind.Ok);
        }
        catch (Exception ex) { Say(ex.Message, StatusKind.Error); }
        finally { _loading = false; IsLoading = false; }
    }

    async Task ApplyAsync(string setting, JsonNode value, string? stream = null)
    {
        if (_loading) return;
        if (_s.Demo) { Say(Loc.T("Demo mode: nothing was changed.")); return; }
        var helper = await HelperAsync();
        if (helper == null) { Say(Loc.T("Device isn't connected."), StatusKind.Error); return; }
        var ok = await helper.SetSystemSettingAsync(setting, value, stream);
        Say(ok ? Loc.T("Updated.") : Loc.T("Couldn't change that setting."), ok ? StatusKind.Ok : StatusKind.Warn);
    }

    partial void OnDarkModeChanged(bool value) => _ = ApplyAsync("darkMode", value);
    partial void OnDndChanged(bool value) => _ = ApplyAsync("dnd", value);
    partial void OnAutoRotateChanged(bool value) => _ = ApplyAsync("autoRotate", value);
    partial void OnWifiChanged(bool value) => _ = ApplyAsync("wifi", value);
    partial void OnBluetoothChanged(bool value) => _ = ApplyAsync("bluetooth", value);

    [RelayCommand] Task ApplyBrightness() => ApplyAsync("brightness", (int)Math.Round(Brightness));
    [RelayCommand] Task ApplyMusicVolume() => ApplyAsync("volume", (int)Math.Round(MusicVolume), "music");
    [RelayCommand] Task ApplyRingVolume() => ApplyAsync("volume", (int)Math.Round(RingVolume), "ring");

    [RelayCommand] Task MediaPrevious() => MediaAsync("previous");
    [RelayCommand] Task MediaPlayPause() => MediaAsync("play_pause");
    [RelayCommand] Task MediaNext() => MediaAsync("next");

    async Task MediaAsync(string action)
    {
        if (_s.Demo) { if (action == "play_pause") MediaPlaying = !MediaPlaying; Say(Loc.T("Demo mode: media button pressed.")); return; }
        var helper = await HelperAsync();
        if (helper == null) return;
        if (await helper.ControlMediaAsync(action))
        {
            await Task.Delay(350);
            if (await helper.GetMediaStateAsync() is { } m)
            {
                MediaPlaying = m.Playing;
                MediaTitle = m.Title.Length > 0 ? m.Title : Loc.T("Nothing playing");
                MediaArtist = m.Artist;
            }
            Say(Loc.T("Sent."), StatusKind.Ok);
        }
    }
}
