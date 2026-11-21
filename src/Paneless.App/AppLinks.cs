using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Paneless.App;

/// <summary>Web pages the app links to. Opened in the default browser, only when clicked.</summary>
public static class AppLinks
{
    public const string Repository = "https://github.com/Stiviemain/paneless";
    public const string Releases = Repository + "/releases";
    public const string KoFi = "https://ko-fi.com/Stiviemain";

    /// <summary>Click handler for buttons whose Tag is a web address.</summary>
    public static void OpenTag(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string url } c && TopLevel.GetTopLevel(c) is { } top)
            _ = top.Launcher.LaunchUriAsync(new Uri(url));
    }
}
