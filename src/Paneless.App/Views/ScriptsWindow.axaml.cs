using Avalonia.Controls;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class ScriptsWindow : Window
{
    public ScriptsWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (DataContext is ScriptsViewModel vm) vm.ConfirmAsync = (title, message, warning) => ConfirmWindow.AskAsync(this, title, message, warning);
        };
        Closed += (_, _) => (DataContext as ScriptsViewModel)?.Detach();
    }
}
