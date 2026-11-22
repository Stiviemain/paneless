using System.Collections.Concurrent;
using Avalonia.Threading;
using Paneless.App.Localization;
using Paneless.App.Platform;
using Paneless.App.ViewModels;
using Paneless.Core;

namespace Paneless.App;

/// <summary>Coordinates opt-in background features powered by the persistent on-device helper daemon:
/// two-way text clipboard sync, new photo/screenshot auto-drop, push telemetry, and opt-in 2FA code capture.
/// Never auto-starts persistent watchers on Wear OS watches unless the user explicitly enabled one on that watch.</summary>
public sealed class HelperCoordinator : IDisposable
{
    readonly AppServices _s;
    readonly MainViewModel _main;
    readonly ClipboardWatcher _pcClipboard = new();
    readonly ConcurrentDictionary<string, bool> _clipboardWatched = new();
    readonly ConcurrentDictionary<string, bool> _mediaWatched = new();
    readonly ConcurrentDictionary<string, bool> _otpWatched = new();
    readonly ConcurrentDictionary<string, string> _serialToGroupId = new();
    readonly ConcurrentDictionary<string, string> _serialToName = new();
    readonly object _echoGate = new();
    string _lastRemoteClip = "";
    DateTime _lastRemoteClipAt = DateTime.MinValue;
    string _lastPcClip = "";
    DateTime _lastPcClipAt = DateTime.MinValue;

    public HelperCoordinator(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
        if (_s.Demo || _s.Helpers == null) return;

        _s.Helpers.EventReceived += OnHelperEvent;
        _s.Helpers.Connected += serial =>
        {
            if (_s.Helpers.TryGet(serial) is { } h && h.Supports("telemetry_watch"))
                _ = Task.Run(async () => { try { await h.WatchTelemetryAsync(true); } catch { } });
        };
        _s.Helpers.Disconnected += serial =>
        {
            _clipboardWatched.TryRemove(serial, out _);
            _mediaWatched.TryRemove(serial, out _);
            _otpWatched.TryRemove(serial, out _);
        };
        _pcClipboard.TextChanged += OnPcClipboardChanged;
        _pcClipboard.Start();
        _main.DevicesChanged += () => _ = SyncWatchersAsync();
        _s.Profiles.Changed += () => _ = SyncWatchersAsync();
        _s.Groups.Changed += () => _ = SyncWatchersAsync();
    }

    /// <summary>True when at least one persistent background sync watcher is active on <paramref name="serial"/>.</summary>
    public bool HasActiveWatchers(string serial) =>
        (_clipboardWatched.TryGetValue(serial, out var cw) && cw)
        || (_mediaWatched.TryGetValue(serial, out var mw) && mw)
        || (_otpWatched.TryGetValue(serial, out var ow) && ow);

    /// <summary>Reconciles per-device background watchers with current settings and connected devices.</summary>
    public async Task SyncWatchersAsync()
    {
        if (_s.Demo || _s.Helpers == null) return;
        foreach (var row in _main.NetworkRows)
        {
            if (row.Serial is not { } serial || row.State is not (DeviceState.Connected or DeviceState.Mirroring)) continue;
            _serialToGroupId[serial] = row.GroupId;
            _serialToName[serial] = row.Name;

            var isWatch = row.Type == "Watch";
            var eff = _s.EffectiveFor(row.GroupId);
            var devProfile = _s.Profiles.Get(row.GroupId);
            // Respect Wear OS battery: only enable background watchers on a watch if explicitly set on that device.
            var wantClip = eff.ClipboardSync && (!isWatch || devProfile?.ClipboardSync == true);
            var wantMedia = eff.AutoDownloadMedia && (!isWatch || devProfile?.AutoDownloadMedia == true);
            var wantOtp = eff.AutoCopyOtp && (!isWatch || devProfile?.AutoCopyOtp == true);

            var currentClip = _clipboardWatched.TryGetValue(serial, out var cw) && cw;
            if (wantClip && !currentClip)
            {
                var helper = await _s.Helpers.GetOrStartAsync(serial, isWatch);
                if (helper != null && helper.Supports("clipboard_watch") && await helper.WatchClipboardAsync(true))
                    _clipboardWatched[serial] = true;
            }
            else if (!wantClip && currentClip)
            {
                if (_s.Helpers.TryGet(serial) is { } helper && helper.Supports("clipboard_watch"))
                    try { await helper.WatchClipboardAsync(false); } catch { }
                _clipboardWatched[serial] = false;
            }

            var currentMedia = _mediaWatched.TryGetValue(serial, out var mw) && mw;
            if (wantMedia && !currentMedia)
            {
                var helper = await _s.Helpers.GetOrStartAsync(serial, isWatch);
                if (helper != null && helper.Supports("media_watch") && await helper.WatchMediaAsync(true))
                    _mediaWatched[serial] = true;
            }
            else if (!wantMedia && currentMedia)
            {
                if (_s.Helpers.TryGet(serial) is { } helper && helper.Supports("media_watch"))
                    try { await helper.WatchMediaAsync(false); } catch { }
                _mediaWatched[serial] = false;
            }

            var currentOtp = _otpWatched.TryGetValue(serial, out var ow) && ow;
            if (wantOtp && !currentOtp)
            {
                var helper = await _s.Helpers.GetOrStartAsync(serial, isWatch);
                if (helper != null && helper.Supports("notifications_watch") && await helper.WatchNotificationsAsync(true))
                    _otpWatched[serial] = true;
            }
            else if (!wantOtp && currentOtp)
            {
                if (_s.Helpers.TryGet(serial) is { } helper && helper.Supports("notifications_watch"))
                    try { await helper.WatchNotificationsAsync(false); } catch { }
                _otpWatched[serial] = false;
            }
        }
    }

    void OnPcClipboardChanged(string text)
    {
        if (_s.Demo || _s.Helpers == null || string.IsNullOrEmpty(text)) return;
        lock (_echoGate)
        {
            if (text == _lastRemoteClip && DateTime.UtcNow - _lastRemoteClipAt < TimeSpan.FromSeconds(2)) return;
            _lastPcClip = text;
            _lastPcClipAt = DateTime.UtcNow;
        }
        foreach (var (serial, watching) in _clipboardWatched)
        {
            if (!watching) continue;
            if (_s.Helpers.TryGet(serial) is { } helper && helper.Supports("clipboard_set"))
                _ = Task.Run(async () => { try { await helper.SetClipboardAsync(text); } catch { } });
        }
    }

    void OnHelperEvent(HelperEvent ev)
    {
        if (ev.Name == "clipboard")
        {
            var text = ev.Data["text"]?.ToString();
            if (string.IsNullOrEmpty(text)) return;
            lock (_echoGate)
            {
                if (text == _lastPcClip && DateTime.UtcNow - _lastPcClipAt < TimeSpan.FromSeconds(2)) return;
                _lastRemoteClip = text;
                _lastRemoteClipAt = DateTime.UtcNow;
            }
            if (ClipboardWatcher.SetText(text))
            {
                var name = _serialToName.GetValueOrDefault(ev.Serial, ev.Serial);
                Dispatcher.UIThread.Post(() => _main.Report(Loc.F("Clipboard synced from {0}.", name)));
            }
        }
        else if (ev.Name == "media_added")
        {
            var remotePath = ev.Data["path"]?.ToString();
            var fileName = ev.Data["name"]?.ToString();
            if (string.IsNullOrEmpty(remotePath) || string.IsNullOrEmpty(fileName)) return;
            _ = AutoSaveMediaAsync(ev.Serial, remotePath, fileName);
        }
        else if (ev.Name == "thermal")
        {
            var status = ev.Data["status"]?.GetValue<int>() ?? 0;
            if (status >= 3 /* SEVERE+ */)
            {
                var name = _serialToName.GetValueOrDefault(ev.Serial, ev.Serial);
                var label = ev.Data["name"]?.ToString() ?? "HOT";
                var msg = Loc.F("{0} is running hot ({1}).", name, label);
                Dispatcher.UIThread.Post(() => _main.Report(msg, StatusKind.Warn));
                Toast.Show(Loc.T("Paneless alert"), msg);
            }
        }
        else if (ev.Name == "notification")
        {
            var otp = ev.Data["otp"]?.ToString();
            if (string.IsNullOrEmpty(otp))
                otp = OtpExtractor.Extract((ev.Data["title"]?.ToString() ?? "") + " " + (ev.Data["text"]?.ToString() ?? ""));
            if (!string.IsNullOrEmpty(otp))
            {
                lock (_echoGate)
                {
                    _lastRemoteClip = otp;
                    _lastRemoteClipAt = DateTime.UtcNow;
                }
                if (ClipboardWatcher.SetText(otp))
                {
                    var name = _serialToName.GetValueOrDefault(ev.Serial, ev.Serial);
                    var msg = Loc.F("2FA code {0} copied from {1}.", otp, name);
                    Dispatcher.UIThread.Post(() => _main.Report(msg, StatusKind.Ok));
                    Toast.Show(Loc.T("2FA code copied"), msg);
                }
            }
        }
    }

    async Task AutoSaveMediaAsync(string serial, string remotePath, string fileName)
    {
        if (_s.Helpers?.TryGet(serial) is not { } helper) return;
        var devName = _serialToName.GetValueOrDefault(serial, serial);
        var safeDev = string.Concat(devName.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' ? ch : '-')).Trim();
        var folder = Path.Combine(ScriptRunner.OutputDir, "Media", safeDev.Length > 0 ? safeDev : "Device");
        var dest = Path.Combine(folder, Path.GetFileName(fileName));
        if (await helper.DownloadFileAsync(remotePath, dest))
        {
            var msg = Loc.F("Saved {0} from {1}.", fileName, devName);
            Dispatcher.UIThread.Post(() => _main.Report(msg, StatusKind.Ok));
            Toast.Show(Loc.T("New photo saved"), msg);
        }
    }

    public void Dispose() => _pcClipboard.Dispose();
}
