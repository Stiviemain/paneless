using Avalonia.Controls;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class QuickControlsWindow : Window
{
    public QuickControlsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        Closed += (_, _) => { if (DataContext is QuickControlsViewModel vm) vm.Dispose(); };
    }
}
