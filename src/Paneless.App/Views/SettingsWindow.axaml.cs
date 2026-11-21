using Avalonia.Controls;

namespace Paneless.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }

    void OpenLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => AppLinks.OpenTag(sender, e);
}
