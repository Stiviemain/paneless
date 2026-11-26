using Avalonia.Controls;

namespace Paneless.App.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    void OpenLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => AppLinks.OpenTag(sender, e);
}
