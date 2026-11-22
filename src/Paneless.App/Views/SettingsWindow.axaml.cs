using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Paneless.App.Localization;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            vm.ConfirmAsync = (title, message) => ConfirmWindow.AskAsync(this, title, message, null);
            vm.PickSaveFileAsync = async suggestedName =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Loc.T("Export Paneless settings"),
                    SuggestedFileName = suggestedName + ".json",
                    DefaultExtension = "json",
                    FileTypeChoices = [new FilePickerFileType(Loc.T("Paneless settings backup")) { Patterns = ["*.json"] }],
                });
                return file?.TryGetLocalPath();
            };
            vm.PickOpenFileAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Loc.T("Import Paneless settings"),
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType(Loc.T("Paneless settings backup")) { Patterns = ["*.json"] }],
                });
                return files.FirstOrDefault()?.TryGetLocalPath();
            };
        };
    }

    void OpenLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => AppLinks.OpenTag(sender, e);
}
