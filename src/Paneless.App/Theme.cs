using Avalonia.Media;
using Avalonia.Media.Immutable;
using Paneless.Core;

namespace Paneless.App;

/// <summary>Paneless colours (always dark). The same values are in App.axaml for XAML use.</summary>
public static class Theme
{
    static IBrush B(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    public static readonly IBrush Text = B("#eceef2");
    public static readonly IBrush Dim = B("#9a9ca6");
    public static readonly IBrush Accent = B("#4f8cff");
    public static readonly IBrush Blue = B("#8db4ff");
    public static readonly IBrush Green = B("#4cc38a");
    public static readonly IBrush Amber = B("#e8b75a");
    public static readonly IBrush Red = B("#f07272");

    public static IBrush For(StatusKind kind) => kind switch
    {
        StatusKind.Ok => Green,
        StatusKind.Warn => Amber,
        StatusKind.Error => Red,
        _ => Dim,
    };

    public static (string Text, IBrush Brush) Look(DeviceState s) => s switch
    {
        DeviceState.Mirroring => ("● Mirroring", Accent),
        DeviceState.Connected => ("● Connected", Green),
        DeviceState.Available => ("○ Available", Blue),
        DeviceState.Unpaired => ("○ Not paired yet", Amber),
        DeviceState.Pairing => ("◌ Ready to pair", Amber),
        DeviceState.Unauthorized => ("! Allow on device", Amber),
        DeviceState.NotFound => ("– Not around", Dim),
        _ => ("× Offline", Red),
    };
}

public enum StatusKind { Info, Ok, Warn, Error }
