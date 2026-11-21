using Paneless.App.Localization;
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
            if (DataContext is not ScriptsViewModel vm) return;
            vm.ConfirmAsync = (title, message, warning) => ConfirmWindow.AskAsync(this, title, message, warning);
            vm.PromptAsync = (title, message, initial) => PromptWindow.AskAsync(this, title, message, initial);
            vm.ReadClipboardAsync = async () => Clipboard is { } cb ? await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(cb) : null;
            vm.WriteClipboardAsync = async text => { if (Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text); };
            vm.PickImportFileAsync = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = Paneless.App.Localization.Loc.T("Script to import"),
                    FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType(Loc.T("Paneless scripts")) { Patterns = ["*.paneless-script", "*.json", "*.txt"] }],
                });
                return files.FirstOrDefault() is { } f ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(f) : null;
            };
            vm.SaveAsAsync = async (name, ext) =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    SuggestedFileName = name + "." + ext, DefaultExtension = ext,
                    FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType(Loc.T("Paneless scripts")) { Patterns = ["*." + ext] }],
                });
                return file is { } f ? Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(f) : null;
            };
        };
        Closed += (_, _) => (DataContext as ScriptsViewModel)?.Detach();
    }
}
