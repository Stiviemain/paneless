using Avalonia.Controls;

namespace Paneless.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }
}
