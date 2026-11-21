using Paneless.App.Localization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed partial class UnlockViewModel : ObservableObject
{
    readonly UnlockService? _unlock;
    readonly DeviceRow _row;

    public UnlockViewModel(UnlockService? unlock, DeviceRow row)
    {
        _unlock = unlock;
        _row = row;
        HasCode = unlock?.HasCode(row.Sn) ?? row.Unlock != UnlockStatus.None;
        Status = !HasCode ? Loc.T("No code saved yet.")
               : unlock?.IsReady(row.Sn) ?? row.Unlock == UnlockStatus.On
                   ? Loc.T("A code is saved and auto-unlock is on. Type a new one to replace it.")
                   : Loc.T("Auto-unlock is paused after a failed try. Save the code again to turn it back on.");
    }

    public Action? Close { get; set; }

    public string Title => $"Auto-unlock {_row.Name}";
    public string Intro =>
        Loc.T("When you mirror this device while it's locked, Paneless wakes it and enters its PIN or password for you. Swipe patterns can't be stored.");
    public string Warning =>
        Loc.F("It's kept in {0}. Anyone who can use your account on this computer can then unlock the device. ", _unlock?.Vault.Description ?? Loc.T("your system's protected storage")) +
        Loc.T("If an unlock ever fails, auto-unlock pauses itself so repeated tries can't lock you out.");
    public bool VaultAvailable => _unlock?.Vault.IsAvailable ?? true;

    [ObservableProperty] public partial string Code { get; set; } = "";
    [ObservableProperty] public partial string Confirm { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial IBrush StatusBrush { get; set; } = Theme.Dim;
    [ObservableProperty] public partial bool HasCode { get; set; }

    [RelayCommand]
    async Task SaveAsync()
    {
        StatusBrush = Theme.Red;
        if (!VaultAvailable) { Status = Loc.T("This computer has no keyring to store the code in (on Linux, install libsecret-tools)."); return; }
        if (Code.Length < 4) { Status = Loc.T("Android PINs and passwords have at least 4 characters."); return; }
        if (Code != Confirm) { Status = Loc.T("The two entries don't match."); return; }
        try
        {
            if (_unlock != null) await _unlock.SaveCodeAsync(_row.Sn!, Code);
            Code = ""; Confirm = "";
            Close?.Invoke();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand]
    async Task RemoveAsync()
    {
        if (_unlock != null) await _unlock.RemoveCodeAsync(_row.Sn!);
        Close?.Invoke();
    }

    [RelayCommand]
    void Cancel() { Code = ""; Confirm = ""; Close?.Invoke(); }
}
