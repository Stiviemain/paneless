# Developing Paneless

## Layout

| Path | What |
|---|---|
| `src/Paneless.Core` | adb and scrcpy, device discovery, settings, groups, scripts, auto-unlock, the scrcpy control protocol. No UI code. |
| `src/Paneless.App` | The [Avalonia](https://avaloniaui.net/) app (MVVM with CommunityToolkit.Mvvm): windows, view models, the mirror toolbar, Windows integration (`Platform/`). |
| `tests/Paneless.Smoke` | A console app with checks: some run in memory, some against real devices. |
| `build.ps1` | Builds the Windows installer with [Velopack](https://velopack.io). |
| `tools/make-icon.ps1` | Draws the logo: `Paneless.ico` (16–256 px) and `Paneless.png`. |
| `docs/` | This guide and the README screenshots. |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- scrcpy 5.x for running from source (`winget install Genymobile.scrcpy`). Paneless looks next to itself first (`scrcpy\`, as in the installer), then in winget's and Homebrew's folders, then in PATH.
- Windows for the installer and the Windows-only parts (docked toolbar, key mapping, group control, hotkeys, tray autostart).

```bash
dotnet run --project src/Paneless.App
```

## How it works

- **Discovery:** `AdbClient` runs `adb mdns services` and `adb devices -l`. `DeviceDirectory` merges both into `DeviceRow`s.
  - Devices are identified by serial number (`ro.serialno`), so groups and settings survive IP and port changes.
- **Mirroring:** `MirrorManager` starts `scrcpy` windows:
  - the device's screen;
  - app windows (`--new-display --flex-display --start-app`);
  - second screens;
  - cameras (`--video-source=camera --orientation`);
  - recordings (`--no-window --record`), which are stopped with a Ctrl+C sent to scrcpy's console (`ProcessSignals`) so the file is complete.
- **Toolbar:** a small window owned by the scrcpy window (`Win32.SetOwner`), so Windows keeps it directly above its mirror.
  - Its buttons use `DeviceControl`, a control-only connection to scrcpy's own device-side server (scrcpy 5.x control protocol, `display_id` for virtual displays), with `adb shell` as a fallback.
- **Key mapping / group control:** low-level keyboard and mouse hooks (`KeymapRouter`, `MouseRouter`) see input for a mirror window only while it's in front.
  - Key mapping takes only mapped keys; group control just watches and repeats on the other devices.
- **Settings:** global ones in `settings.json`, per device in `profiles.json`, per group in `groups.json`. `EffectiveProfile.Resolve` combines them: device over group over global.
- **On connect:** after each refresh, `MainViewModel.Automate` connects known devices, runs on-connect scripts once per connection and starts auto-mirrors.
- **Camera orientation:** `AdbClient.GetHeldRotationAsync` uses the screen's rotation when the screen follows the device, else the *Device Orientation* motion sensor (`dumpsys sensorservice`), combined with the sensor mounting from `dumpsys media.camera`.
- **Secrets:** `SecretVault`: DPAPI (Windows), the macOS keychain, the Linux Secret Service (`secret-tool`).

## Testing

**Demo mode** shows made-up devices and groups and never writes your data:

```bash
dotnet run --project src/Paneless.App -- --demo
```

**Snapshots** render a window to a PNG and exit:

```bash
dotnet run --project src/Paneless.App -- --demo --snapshot out.png --snapshot-view main
```

Views:
- `main`, `main-tall`, `pair`, `unlock`, `scripts`, `scripts-run`
- `toolbar`, `toolbar2` (second screen), `keymap`
- `groups`, `settings`, `device-settings`, `group-settings`, `apps`
- `groups-flow` and `menus`, which drive features and write a `.txt` log next to the PNG
- `dock`, which docks a toolbar to a stand-in window and checks position and stacking

**Smoke tests** (`tests/Paneless.Smoke`):

| Flag | Needs a device | What |
|---|---|---|
| `--groups` | no | Group store and settings resolution, in memory |
| `--scripts-parse` | no | Script parsing and quoting |
| `--camreopen` | no | Camera reopen and re-turn with a stand-in scrcpy |
| `--mirror-error` | no | A failing mirror is reported with scrcpy's reason |
| *(none)* | yes | Read-only: lists devices and lock states, checks unlock codes decrypt (never prints them) |
| `--camturn` | yes | Read-only: rotation, sensor and camera turn per device |
| `--control <serial prefix>` | yes | Control connection up and down, no-op keys |
| `--vdcontrol <prefix>` | yes | Control connection on a virtual display (no window) |
| `--features <prefix>` | yes | Battery, app list (counts only), recording, second screen, app window. Recordings are deleted unopened |

```bash
dotnet run --project tests/Paneless.Smoke -- --groups
```

## Releasing

1. Set `<Version>` in `src/Paneless.App/Paneless.App.csproj`: the window title, installer and updates all read it.
2. Add a section to `CHANGELOG.md`.
3. Build: `powershell -File build.ps1`.
   - It publishes self-contained for win-x64.
   - It downloads the pinned scrcpy release and checks its SHA-256.
   - It packs with `vpk` into `releases\`.
4. Tag and push: `git tag v<version>` then `git push origin v<version>`. The *Release* workflow builds the installer on GitHub and attaches it to a draft release. Check it, then publish it.

To update the bundled scrcpy, change `$ScrcpyVersion` and `$ScrcpySha256` in `build.ps1`, and check that the control protocol in `DeviceControl` still matches.

## Style

- Plain, specific UI text in sentence case.
- Say what will happen, not how it's implemented.
- Small, focused classes; comments explain *why*.
- `Paneless.Core` must not reference Avalonia.
