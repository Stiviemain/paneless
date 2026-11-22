using Avalonia.Controls;

namespace Paneless.App.Views;

public partial class QuickControlsWindow : Window
{
    public QuickControlsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }
}
