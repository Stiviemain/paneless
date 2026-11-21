using Avalonia.Controls;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class PairWindow : Window
{
    public PairWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is not PairViewModel vm) return;
            vm.StartWatching();
            if (!string.IsNullOrEmpty(vm.Address)) CodeBox.Focus();
        };
        Closed += (_, _) => (DataContext as PairViewModel)?.StopWatching();
    }
}
