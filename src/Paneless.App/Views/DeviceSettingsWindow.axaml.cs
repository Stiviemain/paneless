using Avalonia.Controls;
using Avalonia.Input;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

public partial class DeviceSettingsWindow : Window
{
    public DeviceSettingsWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        // The shortcut box records the key combination you press.
        HotkeyBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not ProfileEditorViewModel vm) return;
            e.Handled = true;
            if (e.Key is Key.Escape) return;
            if (e.Key is Key.Back or Key.Delete) { vm.Hotkey = ""; return; }
            if (Hotkeys.Format(e.KeyModifiers, e.Key) is { } text) vm.Hotkey = text;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}
