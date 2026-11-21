using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Paneless.Core;

namespace Paneless.App;

/// <summary>Paneless theme and colours. Follows the system theme by default, or Light / Dark from Settings.
/// The brushes are mutable SolidColorBrushes whose colours update live when the theme changes,
/// so open windows and view models repaint without restarting.</summary>
public static class Theme
{
    public static readonly (string Code, string Name)[] Modes =
    [
        ("", "System default"),
        ("light", "Light"),
        ("dark", "Dark"),
    ];

    static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    static readonly SolidColorBrush _text = B("#eceef2");
    static readonly SolidColorBrush _dim = B("#9a9ca6");
    static readonly SolidColorBrush _accent = B("#4f8cff");
    static readonly SolidColorBrush _blue = B("#8db4ff");
    static readonly SolidColorBrush _green = B("#4cc38a");
    static readonly SolidColorBrush _amber = B("#e8b75a");
    static readonly SolidColorBrush _red = B("#f07272");

    public static IBrush Text => _text;
    public static IBrush Dim => _dim;
    public static IBrush Accent => _accent;
    public static IBrush Blue => _blue;
    public static IBrush Green => _green;
    public static IBrush Amber => _amber;
    public static IBrush Red => _red;

    static bool _subscribed;

    public static ThemeVariant Parse(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>Applies the chosen theme ("", "light", "dark") immediately and keeps C# brushes in sync
    /// when the system theme changes.</summary>
    public static void Apply(string? mode)
    {
        if (Application.Current is not { } app) return;
        if (!_subscribed)
        {
            _subscribed = true;
            app.ActualThemeVariantChanged += (_, _) => Sync(app.ActualThemeVariant);
        }
        app.RequestedThemeVariant = Parse(mode);
        Sync(app.ActualThemeVariant);
    }

    static void Sync(ThemeVariant variant)
    {
        var light = variant == ThemeVariant.Light;
        void Update()
        {
            _text.Color = Color.Parse(light ? "#181a20" : "#eceef2");
            _dim.Color = Color.Parse(light ? "#5b606e" : "#9a9ca6");
            _accent.Color = Color.Parse(light ? "#2563eb" : "#4f8cff");
            _blue.Color = Color.Parse(light ? "#1d56d8" : "#8db4ff");
            _green.Color = Color.Parse(light ? "#157f4b" : "#4cc38a");
            _amber.Color = Color.Parse(light ? "#b4690e" : "#e8b75a");
            _red.Color = Color.Parse(light ? "#c93434" : "#f07272");
        }
        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

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
