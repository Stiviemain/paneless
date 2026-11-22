using Avalonia.Controls;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class RemoteAppsPage : UserControl
{
    public RemoteAppsPage()
    {
        InitializeComponent();
        // Double-click: open a remote app, or add the app picked in "Add an app".
        AppList.DoubleTapped += async (_, _) =>
        {
            if (DataContext is RemoteAppsViewModel vm && vm.Selected is { } item) await vm.OpenItemAsync(item);
        };
        AddList.DoubleTapped += (_, _) =>
        {
            if (DataContext is RemoteAppsViewModel vm && vm.ConfirmAddCommand.CanExecute(null)) vm.ConfirmAddCommand.Execute(null);
        };
    }
}
