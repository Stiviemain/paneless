using Avalonia.Controls;
using Avalonia.Threading;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class LogsPage : UserControl
{
    public LogsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LogsViewModel vm)
                vm.ScrollToEnd = () => Dispatcher.UIThread.Post(() => { if (LogList.ItemCount > 0) LogList.ScrollIntoView(LogList.ItemCount - 1); }, DispatcherPriority.Background);
        };
    }
}
