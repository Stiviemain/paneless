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
        DeviceState.Mirroring => ("● " + Localization.Loc.T("Mirroring"), Accent),
        DeviceState.Connected => ("● " + Localization.Loc.T("Connected"), Green),
        DeviceState.Available => ("○ " + Localization.Loc.T("Available"), Blue),
        DeviceState.Unpaired => ("○ " + Localization.Loc.T("Not paired yet"), Amber),
        DeviceState.Pairing => ("◌ " + Localization.Loc.T("Ready to pair"), Amber),
        DeviceState.Unauthorized => ("! " + Localization.Loc.T("Allow on device"), Amber),
        DeviceState.NotFound => ("– " + Localization.Loc.T("Not around"), Dim),
        _ => ("× " + Localization.Loc.T("Offline"), Red),
    };
}

public enum StatusKind { Info, Ok, Warn, Error }
