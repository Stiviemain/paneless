using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Paneless.App.Localization;
using Paneless.App.Platform;
using Paneless.App.ViewModels;
using Paneless.Core;

namespace Paneless.App.Views;

public enum EmbeddedTargetKind { Mirror, RemoteApp, Screen }

/// <summary>Foreground host window for third-party tab managers (such as Remote Desktop Manager): stays alive for the
/// session's lifetime so the host manager owns one concrete HWND, while Paneless embeds scrcpy inside its client area
/// alongside an optional inline mirror toolbar.</summary>
public partial class EmbeddedSessionWindow : Window
{
    readonly AppServices? _services;
    readonly EmbeddedTargetKind _kind;
    readonly string _targetId = "";
    readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    MirrorSession? _session;
    ToolbarViewModel? _toolbarVm;
    nint _job;
    nint _childHwnd;
    int _lastW, _lastH;
    string? _lastFailure;
    bool _starting;
    bool _closing;
    bool _firstStart = true;
    bool _fullscreenEnsured;
    string? _remoteAppPackage;

    public EmbeddedSessionWindow() => InitializeComponent();   // for the XAML previewer

    public EmbeddedSessionWindow(AppServices services, EmbeddedTargetKind kind, string targetId) : this()
    {
        _services = services;
        _kind = kind;
        _targetId = targetId;

        if (OperatingSystem.IsWindows())
            _job = Win32.CreateKillOnCloseJob();

        var label = InitialLabel();
        Title = label;
        TitleText.Text = label;

        if (_services.Mirrors != null)
            _services.Mirrors.Failed += (session, why) => Dispatcher.UIThread.Post(() =>
            {
                if (session == _session && !string.IsNullOrWhiteSpace(why))
                    _lastFailure = why;
            });

        _syncTimer.Tick += (_, _) => SyncTick();
        Opened += (_, _) => _ = StartSessionAsync();
        Resized += (_, _) => SyncChildBounds();
        Activated += (_, _) => FocusEmbedded();
        GotFocus += (_, _) => FocusEmbedded();
        HostArea.PointerPressed += (_, _) => FocusEmbedded();
        ToolbarPanel.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { Flyout: null })
                Dispatcher.UIThread.Post(FocusEmbedded, DispatcherPriority.Background);
        }, RoutingStrategies.Bubble);
        ToolbarStrip.FlyoutClosed += () => Dispatcher.UIThread.Post(FocusEmbedded, DispatcherPriority.Background);
        Closing += (_, _) => ShutdownSession();
        Closed += (_, _) => ShutdownSession();
    }

    string InitialLabel()
    {
        if (_services == null) return "Paneless";
        if (_kind == EmbeddedTargetKind.RemoteApp)
            return _services.RemoteApps.Get(_targetId)?.Name ?? "Paneless";
        return _services.KnownFor(_targetId)?.Name ?? _targetId;
    }

    void OnRetryClick(object? sender, RoutedEventArgs e) => _ = StartSessionAsync();

    void ShowStatus(string status, bool canRetry = false)
    {
        Overlay.IsVisible = true;
        StatusText.Text = status;
        RetryButton.IsVisible = canRetry;
    }

    int ToolbarWidthPx()
    {
        if (!ToolbarPanel.IsVisible) return 0;
        var w = ToolbarPanel.Bounds.Width > 0 ? ToolbarPanel.Bounds.Width : ToolbarPanel.Width;
        var scale = RenderScaling > 0 ? RenderScaling : 1.0;
        return (int)Math.Round(w * scale);
    }

    (int W, int H)? GetHostClientSize()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var host = TryGetPlatformHandle()?.Handle ?? 0;
        if (host == 0 || !Win32.GetClientRect(host, out var rc)) return null;
        var w = rc.Right - ToolbarWidthPx();
        return w > 100 && rc.Bottom > 100 ? (w, rc.Bottom) : null;
    }

    void AttachToolbar(MirrorSession session, DeviceRow row, bool show)
    {
        DetachToolbar();
        if (!show || _services == null) return;
        var vm = new ToolbarViewModel(_services, session, row)
        {
            IsStandaloneEmbedded = true,
            MirrorWindow = () => _childHwnd,
            ReadClipboardAsync = async () => Clipboard is { } cb ? await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(cb) : null,
        };
        _toolbarVm = vm;
        ToolbarPanel.DataContext = vm;
        ToolbarPanel.IsVisible = true;
    }

    void DetachToolbar()
    {
        ToolbarStrip.HideToast();
        ToolbarPanel.IsVisible = false;
        ToolbarPanel.DataContext = null;
        if (_toolbarVm is { } vm)
        {
            _toolbarVm = null;
            _ = vm.DisposeAsync();
        }
    }

    async Task StartSessionAsync()
    {
        if (_services == null || _starting || _closing) return;
        _starting = true;
        _lastFailure = null;
        _childHwnd = 0;
        _lastW = _lastH = 0;
        _fullscreenEnsured = false;
        _syncTimer.Stop();
        DetachToolbar();

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                ShowStatus(Loc.T("Embedded sessions are currently supported on Windows."), canRetry: false);
                return;
            }
            if (_services.Tools == null || _services.Directory == null || _services.Mirrors == null || _services.Adb == null || _services.Unlock == null)
            {
                ShowStatus(Loc.T("scrcpy and adb are needed to mirror devices."), canRetry: false);
                return;
            }

            RemoteApp? remoteApp = null;
            string deviceId;
            if (_kind == EmbeddedTargetKind.RemoteApp)
            {
                remoteApp = _services.RemoteApps.Get(_targetId);
                if (remoteApp == null)
                {
                    ShowStatus(Loc.T("This shortcut's app isn't in Paneless any more. Add it again on the Remote apps page."), canRetry: false);
                    return;
                }
                deviceId = remoteApp.DeviceId;
                _remoteAppPackage = remoteApp.Package;
                Title = remoteApp.Name;
                TitleText.Text = remoteApp.Name;
            }
            else
            {
                deviceId = _targetId;
                _remoteAppPackage = null;
            }

            var deviceName = _services.KnownFor(deviceId)?.Name ?? deviceId;
            ShowStatus(Loc.F("Connecting to {0}…", deviceName));

            // Give a parent manager (like RDM) a brief moment to reparent and resize this host window to the tab
            // before scrcpy creates a second screen, so --new-display starts at the tab's real size.
            if (_firstStart)
            {
                _firstStart = false;
                await Task.Delay(450);
                if (_closing) return;
            }

            DeviceRow? row = null;
            for (var attempt = 0; attempt < 15 && !_closing; attempt++)
            {
                var rows = await _services.Directory.GetRowsAsync(new HashSet<string>());
                row = rows.FirstOrDefault(r => (r.GroupId == deviceId || r.Serial == deviceId) && r.CanMirror);
                if (row != null) break;
                await Task.Delay(1000);
            }
            if (_closing) return;
            if (row == null)
            {
                ShowStatus(Loc.F("{0} isn't on the network.", deviceName), canRetry: true);
                return;
            }

            if (_kind != EmbeddedTargetKind.RemoteApp)
            {
                Title = row.Name;
                TitleText.Text = row.Name;
            }

            var serial = await _services.Directory.ConnectAsync(row);
            if (_closing) return;
            if (serial == null)
            {
                ShowStatus(Loc.F("Couldn't connect to {0}. If Wireless debugging was reset on it, pair it again.", row.Name), canRetry: true);
                return;
            }

            var opts = _services.EffectiveFor(row.GroupId).ToMirrorOptions();
            if (_services.Unlock.IsReady(row.Sn))
            {
                ShowStatus(Loc.F("Unlocking {0}…", row.Name));
                await _services.Unlock.UnlockAsync(serial, row.Sn!, row.Name, wakeIfUnlocked: _kind == EmbeddedTargetKind.Mirror);
            }
            else if (_kind == EmbeddedTargetKind.Mirror && await _services.Adb.IsScreenOnAsync(serial) == false)
            {
                await _services.Adb.ShellAsync(serial, "input keyevent KEYCODE_WAKEUP", TimeSpan.FromSeconds(5));
                await Task.Delay(350);
            }
            if (_closing) return;

            var startLabel = remoteApp?.Name ?? row.Name;
            ShowStatus(Loc.F("Starting {0}…", startLabel));

            var showToolbar = _kind is EmbeddedTargetKind.Mirror or EmbeddedTargetKind.Screen || (remoteApp?.ShowToolbar ?? false);
            var size = _kind == EmbeddedTargetKind.Screen ? GetHostClientSize() : null;
            var session = _kind switch
            {
                EmbeddedTargetKind.RemoteApp => _services.Mirrors.StartRemoteApp(serial, remoteApp!, $"{remoteApp!.Name} – {row.Name}", opts, embedded: true),
                EmbeddedTargetKind.Screen => _services.Mirrors.StartScreen(serial, row.Name, opts, embedded: true, embeddedSize: size),
                _ => _services.Mirrors.Start(serial, row.Name, opts, embedded: true),
            };
            _session = session;
            Win32.AssignToJob(_job, session.Process);

            nint child = 0;
            for (var i = 0; i < 375 && session.IsRunning && !_closing; i++)
            {
                try
                {
                    session.Process.Refresh();
                    child = session.Process.MainWindowHandle;
                }
                catch { break; }
                if (child != 0) break;
                await Task.Delay(40);
            }
            if (_closing) { MirrorManager.Stop(session); return; }

            var host = TryGetPlatformHandle()?.Handle ?? 0;
            if (child == 0 || host == 0 || !session.IsRunning)
            {
                ShowStatus(_lastFailure ?? Loc.T("The session ended."), canRetry: true);
                return;
            }

            AttachToolbar(session, row, showToolbar);
            _childHwnd = child;
            Win32.GetClientRect(host, out var rc);
            _lastW = Math.Max(1, rc.Right - ToolbarWidthPx());
            _lastH = Math.Max(1, rc.Bottom);
            Win32.EmbedChild(child, host, 0, 0, _lastW, _lastH);
            Overlay.IsVisible = false;
            Win32.FocusChild(child);
            _syncTimer.Start();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, canRetry: true);
        }
        finally
        {
            _starting = false;
        }
    }

    void SyncTick()
    {
        if (!OperatingSystem.IsWindows() || _closing) return;
        if (_session == null || !_session.IsRunning || _childHwnd == 0 || !Win32.IsWindow(_childHwnd))
        {
            _syncTimer.Stop();
            _childHwnd = 0;
            DetachToolbar();
            ShowStatus(_lastFailure ?? Loc.T("The session ended."), canRetry: true);
            return;
        }
        SyncChildBounds();
    }

    void SyncChildBounds()
    {
        if (!OperatingSystem.IsWindows() || _closing || _childHwnd == 0 || !Win32.IsWindow(_childHwnd)) return;
        var host = TryGetPlatformHandle()?.Handle ?? 0;
        if (host == 0 || !Win32.GetClientRect(host, out var rc)) return;
        var w = rc.Right - ToolbarWidthPx();
        var h = rc.Bottom;
        if (w <= 20 || h <= 20) return;
        if (w == _lastW && h == _lastH) return;

        _lastW = w;
        _lastH = h;
        Win32.ResizeChild(_childHwnd, 0, 0, _lastW, _lastH);

        if (!_fullscreenEnsured && _session is { DisplayId: { } did and > 0 } s && _remoteAppPackage is { Length: > 0 } pkg && _services?.Helpers != null)
        {
            _fullscreenEnsured = true;
            var serial = s.Serial;
            var cw = _lastW; var ch = _lastH;
            _ = Task.Run(async () =>
            {
                var helper = await _services.Helpers.GetOrStartAsync(serial);
                if (helper != null && helper.Supports("display_fullscreen"))
                    try { await helper.EnsureDisplayFullscreenAsync(did, pkg, cw, ch); } catch { }
            });
        }
    }

    void FocusEmbedded()
    {
        if (OperatingSystem.IsWindows() && _childHwnd != 0 && Win32.IsWindow(_childHwnd))
            Win32.FocusChild(_childHwnd);
    }

    public void ShutdownSession()
    {
        if (_closing) return;
        _closing = true;
        _syncTimer.Stop();
        DetachToolbar();
        if (_session is { } s)
        {
            _session = null;
            MirrorManager.Stop(s);
        }
        if (OperatingSystem.IsWindows() && _job != 0)
        {
            Win32.CloseHandle(_job);
            _job = 0;
        }
        if (_services?.Helpers != null)
            try { _services.Helpers.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
}
