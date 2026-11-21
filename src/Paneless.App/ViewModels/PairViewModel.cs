using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paneless.Core;

namespace Paneless.App.ViewModels;

public sealed partial class PairViewModel : ObservableObject
{
    readonly AdbClient? _adb;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _autoFilling;
    bool _userEdited;

    public PairViewModel(AdbClient? adb, string? prefill)
    {
        _adb = adb;
        if (!string.IsNullOrEmpty(prefill))
        {
            SetAddress(prefill);
            SetStatus($"Found a device waiting to pair at {prefill}. Type its 6-digit code.", Theme.Green);
        }
        _poll.Tick += async (_, _) => await PollAsync();
    }

    /// <summary>Set by the window: closes it; true when a device was paired.</summary>
    public Action<bool>? Close { get; set; }

    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string Code { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial IBrush StatusBrush { get; set; } = Theme.Dim;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    public partial bool IsBusy { get; set; }

    partial void OnAddressChanged(string value) { if (!_autoFilling) _userEdited = true; }

    void SetAddress(string a) { _autoFilling = true; Address = a; _autoFilling = false; }
    void SetStatus(string text, IBrush brush) { Status = text; StatusBrush = brush; }

    public void StartWatching() => _poll.Start();
    public void StopWatching() => _poll.Stop();

    async Task PollAsync()
    {
        // While the user hasn't typed an address, fill in any device that's showing its pairing screen.
        if (_adb == null || IsBusy || _userEdited) return;
        var svc = (await _adb.GetMdnsServicesAsync()).FirstOrDefault(s => s.Pair != null);
        if (svc?.Pair != null && svc.Pair != Address)
        {
            SetAddress(svc.Pair);
            SetStatus($"Found a device waiting to pair at {svc.Pair}. Type its 6-digit code.", Theme.Green);
        }
    }

    bool CanPair() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPair))]
    async Task PairAsync()
    {
        var addr = Address.Trim();
        var code = new string(Code.Where(char.IsDigit).ToArray());
        if (!AdbClient.IsIpPort(addr)) { SetStatus("Enter the IP address and port shown on the pairing screen, e.g. 192.168.2.40:41234.", Theme.Red); return; }
        if (code.Length != 6) { SetStatus("The pairing code has 6 digits.", Theme.Red); return; }
        if (_adb == null) { SetStatus("Demo mode: nothing to pair with.", Theme.Amber); return; }

        IsBusy = true;
        SetStatus("Pairing…", Theme.Dim);
        try
        {
            var r = await _adb.PairAsync(addr, code);
            if (!r.Output.Contains("Successfully paired"))
            {
                var why = r.TimedOut ? "no answer. Is the device on the same Wi-Fi with the pairing screen still open?" : r.LastLine;
                SetStatus("Pairing failed: " + why, Theme.Red);
                return;
            }
            SetStatus("Paired! Connecting…", Theme.Green);
            var ip = addr.Split(':')[0];
            for (var i = 0; i < 12; i++)
            {
                var svc = (await _adb.GetMdnsServicesAsync()).FirstOrDefault(s => s.Ip == ip && s.Connect != null);
                if (svc?.Connect != null) { await _adb.ConnectAsync(svc.Connect); break; }
                await Task.Delay(500);
            }
            Close?.Invoke(true);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    void Cancel() => Close?.Invoke(false);
}
