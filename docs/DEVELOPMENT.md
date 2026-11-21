# Developing Paneless

## Layout

| Path | What |
|---|---|
| `src/Paneless.Core` | adb and scrcpy, device discovery, settings, groups, scripts, auto-unlock, the scrcpy control protocol. No UI code. |
| `src/Paneless.App` | The [Avalonia](https://avaloniaui.net/) app (MVVM with CommunityToolkit.Mvvm): windows, pages, view models, the mirror toolbar, Windows integration (`Platform/`), the MCP server for AI assistants (`Assistants/`), translations (`Localization/`). |
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
- **Pages:** the main window has a navigation rail (Devices, Wall, Logs, Automation, Inventory, AI). Each page has its own view model; `MainWindow.SetUpPages` creates them. A page's `IsVisible` sits on a wrapping `Panel`, because compiled bindings on the page itself would resolve against the page's own view model.
- **Wall:** before each picture it asks `dumpsys power` for `mWakefulness` (`AdbClient.IsScreenOnAsync`); a screen that is asleep or dozing gives an all-black screenshot, so it isn't photographed and the tile shows Wake instead. The layout (order, the shared width set by "Make all this size", single tiles' widths, hidden devices) is `AppSettings.Wall` (`WallLayout`, in Core, tested by `--groups`). Tile buttons capture the pointer when pressed, so `WallPage` watches press/move/release on the way down (tunnel) to tell a click from a drag (move) or a corner drag (resize), and swallows the click that follows a drag.
- **Automation:** `RecipeRunner` (Core) runs a recipe's steps on one device; `DeviceActions` runs scripts, recipes, restarts and commands on many devices (6 at a time). `AutomationService` checks scheduled tasks every 20 s (`ScheduledTask.IsDue`) and alerts every minute (`AlertWatcher` fires once per condition until it clears; crashes come from `logcat -b crash`). Notifications are a notification-area balloon (`Platform/Toast`).
- **AI assistants (MCP):** `McpServer` serves MCP (JSON-RPC 2.0, one message per line) on the named pipe `paneless-mcp-<user>`, limited to the current user, and remembers each client's name from `initialize` (`McpConfig.Friendly`). AI apps start `Paneless.exe --mcp` (`McpBridge`), which forwards stdin/stdout to that pipe and starts Paneless with `--tray` if it isn't running. `AssistantTools` defines the tools and checks the device's `AssistantAccess` level (device over group over default) and, for changes, asks the user, naming the app (`AssistantConfirmWindow`). Screenshots are scaled to 1280 px; taps are mapped back to device pixels. `AssistantApps` lists the supported apps; `McpConfig` (Core, tested by `--assistants`) adds Paneless to their settings: JSON `mcpServers` (Claude Desktop, Gemini CLI) or Codex's `config.toml`.
- **Single instance:** a per-user mutex; a second start asks the first one (over a pipe) to show its window.
- **Updates:** Velopack. `Updates.CheckAsync` reads GitHub Releases (`GithubSource`; betas when *Include beta versions* is on) or another source, downloads in the background and applies on exit.
- **Translations:** English text is the key. `Loc.T` / `Loc.F` / `Loc.Count` in C#, `{l:T '…'}` in XAML, `Text.T` / `Text.F` in Core. `Localization/de.json` and `fr.json` are embedded; missing entries stay English. Built-in script names and descriptions are translated when the list is first built.
- **Theme:** `App.axaml` defines Light (`Default`) and `Dark` resource dictionaries plus `FluentTheme` palettes, referenced with `{DynamicResource}`. `Theme.Apply` sets `RequestedThemeVariant` (System default, Light, or Dark) and updates mutable C# `SolidColorBrush` instances on `ActualThemeVariantChanged` so open windows switch live without restarting.

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
- `page-wall`, `page-logs`, `page-inventory`, `page-assistants`, `page-automation-0` (recipes), `-1` (scheduled tasks), `-2` (alerts)
- `wall-menu`: the Wall's right-click menu (is every item wired?) plus moving, resizing and hiding tiles, logged to `<png>.wall.txt`. In demo mode the Wall shows made-up home screens; "Lab phone 03" shows the screen-off state.
- `mcp-selftest`, which talks to Paneless through the real `--mcp` bridge like an AI app does and writes the conversation to `<png>.mcp.txt`

Add `--lang de` or `--lang fr` to see a translation, and `--theme light` or `--theme dark` to test a theme, without changing your settings.

**Smoke tests** (`tests/Paneless.Smoke`):

| Flag | Needs a device | What |
|---|---|---|
| `--groups` | no | Group store and settings resolution, in memory |
| `--scripts-parse` | no | Script parsing and quoting |
| `--automation` | no | Schedule timing, alerts, script sharing, log parsing, inventory export |
| `--assistants` | no | Adding Paneless to AI apps' settings (JSON and Codex TOML), assistant names |
| `--translations` | no | Every `{0}` in `de.json` / `fr.json` exists in the English text |
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
4. Tag and push: `git tag v<version>` then `git push origin v<version>`. The *Release* workflow builds the installer on GitHub and attaches it to a draft release, together with `releases.win.json` and the `-full.nupkg` that automatic updates read. Check it, then publish it.
   - Installed copies only see published releases, and only while the repository is public (GitHub doesn't serve a private repository's releases without a token).

To update the bundled scrcpy, change `$ScrcpyVersion` and `$ScrcpySha256` in `build.ps1`, and check that the control protocol in `DeviceControl` still matches.

## Style

- Plain, specific UI text in sentence case.
- Say what will happen, not how it's implemented.
- Small, focused classes; comments explain *why*.
- `Paneless.Core` must not reference Avalonia.
