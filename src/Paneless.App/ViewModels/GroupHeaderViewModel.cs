using Paneless.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

/// <summary>The heading of one group in the device list: click to collapse or expand.
/// <see cref="Group"/> is null for "Other devices" (the devices that aren't in a group).</summary>
public sealed partial class GroupHeaderViewModel(MainViewModel main, DeviceGroup? group) : ObservableObject
{
    public DeviceGroup? Group { get; } = group;
    public bool IsUserGroup => Group != null;
    public string Name => Group?.Name ?? Loc.T("Other devices");

    [ObservableProperty] public partial bool IsCollapsed { get; set; }
    [ObservableProperty] public partial string CountText { get; set; } = "";
    [ObservableProperty] public partial bool CanMirrorAll { get; set; }

    partial void OnIsCollapsedChanged(bool value) => OnPropertyChanged(nameof(ChevronAngle));
    public double ChevronAngle => IsCollapsed ? -90 : 0;

    public void Refresh() => OnPropertyChanged(nameof(Name));

    [RelayCommand] void Toggle() => main.ToggleGroup(this);
    [RelayCommand] void SelectAll() => main.SelectGroup(this);
    [RelayCommand] Task MirrorAll() => main.MirrorGroupAsync(this);
    [RelayCommand] void RunScript() => main.RunScriptOnGroup(this);
    [RelayCommand] Task Rename() => main.RenameGroupAsync(this);
    [RelayCommand] Task Delete() => main.DeleteGroupAsync(this);
    [RelayCommand] void MoveUp() => main.MoveGroup(this, -1);
    [RelayCommand] void MoveDown() => main.MoveGroup(this, 1);
    [RelayCommand] void Manage() => main.ShowGroups?.Invoke(Group);
    [RelayCommand] void Settings() => main.GroupSettings(this);
    [RelayCommand] void Arrange() => main.ArrangeGroup(this);
    [RelayCommand] Task SendFiles() => main.SendFilesToGroupAsync(this);
}
