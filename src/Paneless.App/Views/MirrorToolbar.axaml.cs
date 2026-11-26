using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Paneless.App.Platform;
using Paneless.App.ViewModels;
using Paneless.Core;

namespace Paneless.App.Views;

/// <summary>Sits on the right edge of a scrcpy mirror window and follows it (Windows).
/// On macOS/Linux it's a small normal window, because other apps' windows can't be tracked there.</summary>
public partial class MirrorToolbar : Window
{
    readonly MirrorSession? _session;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    nint _mirror;
    bool _mirrorWasFocused;
    bool _owned;
    bool _closing;

    public MirrorToolbar() => InitializeComponent();

    public MirrorToolbar(MirrorSession session, ToolbarViewModel vm) : this()
    {
        _session = session;
        DataContext = vm;
        vm.MirrorWindow = () => _mirror;
        vm.ReadClipboardAsync = async () => Clipboard is { } cb ? await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(cb) : null;
        if (OperatingSystem.IsWindows())
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;
            Topmost = false;
        }
        else
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
            Height = 600;
        }
        _timer.Tick += (_, _) => Track();
        Closed += async (_, _) => { _timer.Stop(); await vm.DisposeAsync(); };
    }

    /// <summary>Starts following the mirror; the toolbar appears once scrcpy's window does.</summary>
    public void Start()
    {
        _timer.Start();
        if (!OperatingSystem.IsWindows()) Show();
    }

    void Track()
    {
        if (_session == null || _closing) return;
        if (!_session.IsRunning) { _closing = true; Close(); return; }
        if (!OperatingSystem.IsWindows()) return;

        if (_mirror == 0)
        {
            _session.Process.Refresh();
            _mirror = _session.Process.MainWindowHandle;
            if (_mirror == 0) return;
        }
        if (!Win32.IsWindow(_mirror)) { _closing = true; Close(); return; }
        if (Win32.IsIconic(_mirror) || !Win32.IsWindowVisible(_mirror) || Win32.Bounds(_mirror) is not { } b)
        {
            if (IsVisible) Hide();
            return;
        }

        var scaling = Screens.ScreenFromPoint(new PixelPoint(b.Right, b.Top))?.Scaling ?? 1.0;
        var pos = new PixelPoint(b.Right + (int)Math.Round(4 * scaling), b.Top);
        if (Position != pos) Position = pos;
        var h = (b.Bottom - b.Top) / scaling;
        if (Math.Abs(Height - h) > 0.5) Height = h;
        if (!IsVisible)
        {
            Show();
            if (TryGetPlatformHandle()?.Handle is { } own)
            {
                Win32.MakeToolWindow(own);
                // Owned by the mirror: Windows keeps it right above the mirror in the stacking order (no polling,
                // so it can't end up in front of other windows or flicker over them).
                if (!_owned) { Win32.SetOwner(own, _mirror); _owned = true; }
                Win32.KeepJustAbove(own, _mirror);   // once: from now on Windows keeps owned windows above their owner
            }
        }

        // Lift held fingers when the mirror stops being the window the keys go to.
        var focused = Win32.GetForegroundWindow() == _mirror;
        if (_mirrorWasFocused && !focused && DataContext is ToolbarViewModel vm) vm.MirrorLostFocus();
        _mirrorWasFocused = focused;
    }
}
