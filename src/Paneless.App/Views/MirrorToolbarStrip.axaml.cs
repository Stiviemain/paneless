using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Paneless.App.ViewModels;

namespace Paneless.App.Views;

/// <summary>Compact vertical tool strip shared by the floating mirror toolbar and embedded session windows.</summary>
public partial class MirrorToolbarStrip : UserControl
{
    readonly DispatcherTimer _hideToast = new() { Interval = TimeSpan.FromSeconds(5) };
    ToolbarViewModel? _vm;

    public event Action? FlyoutClosed;

    public MirrorToolbarStrip()
    {
        InitializeComponent();
        _hideToast.Tick += (_, _) => HideToast();
        if (MoreButton.Flyout is { } flyout)
            flyout.Closed += (_, _) => FlyoutClosed?.Invoke();
    }

    public void HideToast()
    {
        _hideToast.Stop();
        Toast.IsOpen = false;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as ToolbarViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        HideToast();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        HideToast();
        base.OnDetachedFromVisualTree(e);
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ToolbarViewModel vm || e.PropertyName != nameof(ToolbarViewModel.Hint) || vm.Hint.Length == 0 || !IsEffectivelyVisible)
            return;
        ToastText.Text = vm.Hint;
        Toast.IsOpen = true;
        _hideToast.Stop();
        _hideToast.Start();
    }
}
