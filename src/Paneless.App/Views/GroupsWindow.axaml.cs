using Paneless.App.Localization;
using Avalonia.Controls;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class GroupsWindow : Window
{
    public GroupsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            if (DataContext is not GroupsViewModel vm) return;
            vm.PromptAsync = (title, message, initial) => PromptWindow.AskAsync(this, title, message, initial);
            vm.ConfirmAsync = (title, message) => ConfirmWindow.AskAsync(this, title, message, null, Loc.T("Delete"));
        };
        Closed += (_, _) => (DataContext as GroupsViewModel)?.Detach();
    }
}
