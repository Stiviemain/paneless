using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>One row in the device list. The row data is replaced on every refresh; the item stays,
/// so the list keeps its selection.</summary>
public sealed partial class DeviceItemViewModel(DeviceRow row) : ObservableObject
{
    DeviceRow _row = row;
    bool _inGroup;

    public DeviceRow Row
    {
        get => _row;
        set
        {
            if (!SetProperty(ref _row, value)) return;
            OnPropertyChanged(string.Empty);   // every display property depends on the row
        }
    }

    /// <summary>Shown under a group heading (indented).</summary>
    public bool InGroup
    {
        get => _inGroup;
        set { if (SetProperty(ref _inGroup, value)) OnPropertyChanged(nameof(NameMargin)); }
    }

    BatteryInfo? _battery;
    public BatteryInfo? Battery
    {
        get => _battery;
        set { if (SetProperty(ref _battery, value)) { OnPropertyChanged(nameof(BatteryText)); OnPropertyChanged(nameof(BatteryBrush)); } }
    }
    public string BatteryText => _battery?.Text ?? "";
    public IBrush BatteryBrush => _battery is { Level: <= 15, Charging: false } ? Theme.Red : _battery is { Charging: true } ? Theme.Green : Theme.Dim;

    public string Key => _row.Key;
    public string Name => _row.Name;
    public string Model => _row.Model;
    public string Type => _row.Type;
    public string Address => _row.Address;
    public bool IsMissing => _row.State == DeviceState.NotFound;
    public IBrush NameBrush => IsMissing ? Theme.Dim : Theme.Text;
    public Thickness NameMargin => new(InGroup ? 34 : 12, 0, 8, 0);
    public string StatusText => Theme.Look(_row.State).Text;
    public IBrush StatusBrush => Theme.Look(_row.State).Brush;
    public string UnlockText => _row.Unlock switch { UnlockStatus.On => "On", UnlockStatus.Paused => "Paused", _ => "" };
    public IBrush UnlockBrush => _row.Unlock == UnlockStatus.Paused ? Theme.Amber : Theme.Green;

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase) || Model.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Type.Contains(q, StringComparison.OrdinalIgnoreCase) || Address.Contains(q, StringComparison.OrdinalIgnoreCase);
}
