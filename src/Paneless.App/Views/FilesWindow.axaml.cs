using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Paneless.App.Localization;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class FilesWindow : Window
{
    public FilesWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        FileList.DoubleTapped += (_, _) =>
        {
            if (DataContext is FilesViewModel vm && vm.Selected is { } item)
                _ = vm.ActivateItemAsync(item);
        };
        FileDropArea.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var ok = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            FileDropArea.Classes.Set("dragover", ok);
        });
        FileDropArea.AddHandler(DragDrop.DragLeaveEvent, (_, _) => FileDropArea.Classes.Set("dragover", false));
        FileDropArea.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            FileDropArea.Classes.Set("dragover", false);
            if (DataContext is not FilesViewModel vm) return;
            var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            if (files.Count > 0) await vm.UploadFilesAsync(files);
        });
        Closed += (_, _) =>
        {
            if (DataContext is FilesViewModel vm) vm.Dispose();
        };
    }

    public void AttachDialogs(FilesViewModel vm)
    {
        vm.PickSavePathAsync = async suggestedName =>
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("Save file from device"),
                SuggestedFileName = suggestedName,
            });
            return f?.TryGetLocalPath();
        };
        vm.PickUploadFilesAsync = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.T("Upload files to device"),
                AllowMultiple = true,
            });
            return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        };
        vm.PromptAsync = (t, m, init) => PromptWindow.AskAsync(this, t, m, init);
        vm.ConfirmAsync = (t, m) => ConfirmWindow.AskAsync(this, t, m, null, Loc.T("Delete"));
    }
}
