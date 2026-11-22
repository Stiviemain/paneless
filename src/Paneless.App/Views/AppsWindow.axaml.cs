using Paneless.App.Localization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class AppsWindow : Window
{
    public AppsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        AppList.DoubleTapped += (_, _) => { if (DataContext is AppsViewModel vm && vm.OpenCommand.CanExecute(null)) vm.OpenCommand.Execute(null); };
        Opened += (_, _) =>
        {
            if (DataContext is not AppsViewModel vm) return;
            vm.ConfirmAsync = (title, message) => ConfirmWindow.AskAsync(this, title, message, null);
            vm.PickApksAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Loc.T("APK to install"),
                    AllowMultiple = true,
                    FileTypeFilter = [new FilePickerFileType(Loc.T("Android apps")) { Patterns = ["*.apk"] }],
                });
                return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            };
        };
        Closed += (_, _) => { if (DataContext is AppsViewModel vm) vm.StopLoading(); };
    }
}
