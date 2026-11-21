using Avalonia.Controls;

namespace Paneless.App.Views;

public partial class UnlockWindow : Window
{
    public UnlockWindow()
    {
        InitializeComponent();
        Opened += (_, _) => CodeBox.Focus();
    }
}
