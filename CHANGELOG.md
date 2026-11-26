# Changelog

All notable changes to Paneless. Versions follow [Semantic Versioning](https://semver.org).

## 1.0.0-beta.7 (2026-11-26)

A full UI/UX overhaul with an in-app Settings page, 4-tab Device/Group settings, streamlined menus and toolbars, smarter Wall wake/unlock handling, and crisp Remote apps resizing.

**In-app Settings & Device/Group settings**
- **6-category Settings page:** replaced the modal Settings window with an in-app **Settings** page (`General`, `Mirroring & quality`, `Sync & files`, `Connecting & battery`, `Updates & backup`, `About & support`) featuring `ToggleSwitch` controls and quick **Open logs folder** / **Open data folder** buttons.
- **4-tab Device & Group settings:** reorganized per-device/group overrides into `Mirroring & quality`, `Sync & files`, `On connect`, and `Access & shortcut`, integrating live **Auto-unlock** setup and per-device/group **AI assistants** permissions.

**Streamlined menus, toolbars, Quick controls & Files drag-and-drop**
- **Cleaner action bars & context menus:** organized the Devices toolbar into `Mirror`, `Tools`, `Power & unlock`, and `More`, added a subtle `⋯` menu button to every device row, categorized context menus across Devices, Wall, Apps, Files, and Remote apps, and styled destructive actions consistently.
- **Shared compact mirror toolbar:** unified the floating and embedded mirror toolbars into a 4-zone strip with active state indicators and a `⋯` **More tools** menu (Notifications, Quick settings, Type clipboard, Second screen, Pop out app, Camera, **Quick controls & media…**, **Run a script…**, Group control, Key mapping).
- **Upgraded Quick controls & Files upload:** **Quick controls & media** now uses a 2×3 `ToggleSwitch` grid (adding **Auto-brightness**) and debounced live sliders for brightness and volume; **Files & photos** now supports OS drag-and-drop file uploads.

**Wall smart wake/unlock & screen-off mirroring**
- **Smart wake & crash-safe dimming:** sleeping Wall tiles show **Wake & unlock** when auto-unlock is ready (plus **Wake all** in the header) and temporarily dim devices using *Keep device screen off* to minimum brightness while awake, restoring brightness automatically when turned off, left, or closed.
- **Screen-off mirroring indicator:** tiles actively mirrored with *Keep device screen off* now show **Screen off (mirroring)** with a **Show mirror** button that brings the mirror window forward without waking the physical screen.

**Remote apps resize & scaling fix**
- Fixed stale/unclickable touch regions and visual artifacts when resizing Remote app windows or changing **UI scale** / **Window shape** (clean debounced in-place virtual-display recreate on shape/scale changes, pure `--flex-display` on border drags, one-shot tablet fullscreen enforcement, 8-pixel encoder alignment, and smooth aspect-ratio DPI scaling).

**Localization & docs**
- Completed English, German, and French translations (`1,252` keys), balanced column widths on Devices so German labels fit cleanly, and updated `README.md`, `docs/DEVELOPMENT.md`, and all 14 screenshots.

## 1.0.0-beta.6 (2026-11-24)

Embedded sessions for connection managers (Devolutions RDM), live UI scaling and live window-shape resizing for Remote apps, and a branded installer splash screen with WinGet automation.

**Embedded sessions & Devolutions RDM export**
- **Embedded session host (`--embed`):** open any device mirror, second screen, or Remote app inside a dedicated host window for external tab managers (Devolutions Remote Desktop Manager, etc.), with borderless embedded `scrcpy`, an inline right-edge mirror toolbar, and automatic child process cleanup when the tab closes.
- **Export for remote tools (`.rdm`):** export devices, selections, whole groups, or Remote apps (**Export for remote tools…** / **Export all…**) with Paneless group folders, real badged app icons, preserved Remote app shapes, and deterministic IDs so re-imports update in place.

**Remote apps live scaling & window shape**
- **Per-app UI scale (`65 %`–`150 %`):** lower virtual display density down to `120 dpi` so dense phone and tablet apps fit comfortably on PC monitors without excessive scrolling.
- **Automatic DPI sync & live shape resizing:** resizing a Remote app window automatically updates its virtual display DPI (`wm density`), and switching **Window shape** (`Phone`, `Tablet`, `Custom` W×H) or **UI scale** applies live without restarting the app.

**Installer & distribution**
- **Branded installer splash:** added a dark Paneless setup splash image with a `#4f8cff` progress bar (`tools/make-splash.ps1`, `Paneless-splash.png`) and explicit `win-x64` Velopack packaging.
- **WinGet automation:** added `.github/workflows/winget.yml` to open package updates in `microsoft/winget-pkgs` automatically on future releases.

## 1.0.0-beta.5 (2026-11-23)

Zero-install Phone-to-PC continuity (Files & photos, live clipboard sync, Quick controls, app handoff, Locate device), smarter AI/MCP tools, a cleaner UI, major Android 17 & HyperOS fixes, and idle phone battery savings.

**Phone-to-PC tools (zero Android app to install)**
- **Files & photos browser:** browse folders on any device with EXIF-aware photo and video thumbnails, upload/download files, and optionally auto-download new photos and screenshots straight into `Documents\Paneless\Media\<Device>`.
- **Always-on clipboard sync** (opt-in per device or group): instant two-way text clipboard sync between Windows and Android without needing a mirror window open.
- **Quick controls & media:** toggle Dark mode, Do Not Disturb, Auto-rotate, Wi-Fi and Bluetooth, adjust brightness and volume sliders, and control Now Playing media from your PC.
- **Locate device:** blink the rear flashlight, vibrate and chime a misplaced phone or tablet from the Devices or Wall menu.
- **Opt-in 2FA / OTP auto-copy:** copy incoming one-time verification codes from Android notifications straight to your Windows clipboard (English, German and French).
- **App handoff:** *Pop out current app* moves the app on your phone into its own PC window; *Send back to phone* returns a virtual-display app to the device screen.
- **Faster Wall & thermal alerts:** on-device downscaled JPEG capture (~20 KB instead of ~3 MB PNG), event-driven screen-off detection, instant camera orientation while a camera window is open, and push overheat alerts.

**AI assistants (MCP)**
- **Semantic UI automation:** new `read_ui_tree` and `ui_action` tools let AI assistants inspect and tap elements by text or label without taking a screenshot on every step (password fields are automatically redacted).
- **More device tools:** `unlock_device` and `lock_device` (using Paneless' locally stored DPAPI unlock code without ever revealing it to the AI, following the normal confirmation / 15-minute grant rule), `list_remote_apps`, `open_remote_app`, `get_clipboard` / `set_clipboard`, `list_files` / `pull_file`, `list_running_apps`, `move_app_to_display`, `open_url`, `get_device_controls` / `set_device_control`, `media_control`, and `locate_device`.
- **Better MCP compliance & setup:** standard MCP 2025 tool annotations (`readOnlyHint` / `destructiveHint` / `idempotentHint` / `openWorldHint`), 1-click **Cursor** setup (`~/.cursor/mcp.json`), green `✓ Configured in …` status badge, VS Code guidance, and fast fail (~2.5 s instead of 25 s) when Paneless is already running with AI access off.

**UI, apps & settings**
- **Cleaner navigation & layout:** moved **Logs** down above **Settings** with subtle dividers, added a left active accent pill, cogwheel Settings icon, subtle live status dots on **AI** and **Logs**, `Ctrl+1`–`Ctrl+7` / `Ctrl+,` shortcuts, slimmer sub-page headers, context-aware search with inline clear button, centered button labels, monospace metric columns, and a refined AI JSON code box.
- **Real app icons in the App manager:** friendly app names, lazy-loaded app icons, and version/APK size details.
- **Settings backup & factory reset:** export and import settings (optionally including saved devices, groups, profiles and remote apps; unlock codes are never exported) and reset to defaults under **Settings**.

**Battery savings & real-device fixes**
- **Idle phone battery savings:** slowed/paused background battery polling when Paneless is minimized or in the tray, automatic idle helper shutdown after 2 min (60 s on watches) when no background sync watcher or live tool needs it, and **Settings › Connecting › Disconnect idle Wi-Fi devices while in the background** (on by default; disconnects idle Wi-Fi ADB connections after 5 min in the tray so phones stay in Deep Doze, and reconnects seamlessly on demand).
- **Android 17 (Pixel) fixes:** fixed black mirror / failed auto-unlock when virtual displays existed (`dumpsys window` multi-display parsing, waking Display 0 from Doze, `--keep-active`), and fixed helper startup on Android 17 (`KeyguardManager` / `ApplicationSharedMemory`).
- **Tablet Remote apps fix (HyperOS / desktop mode):** new virtual displays now use natural phone/tablet DPI and force `WINDOWING_MODE_FULLSCREEN`, so Remote apps fill the window edge-to-edge instead of opening as small centered floating windows.

## 1.0.0-beta.4 (2026-11-22)

Remote apps, a much more capable Wall, better USB support and a few fixes.

**Remote apps** (new page)
- Use Android apps like programs on your PC: each one opens in a window of its own, on a separate screen of the device, so the device's own screen is left alone (and can stay off).
- The window has the app's name and icon and its own taskbar button, and remembers its size and place. Phone or tablet shape, or your own size; type with the PC keyboard only.
- **Shortcuts** on the desktop and in the Start menu, with the app's icon and a small Paneless logo in the corner. They start Paneless by itself if it isn't running.
- Add apps on the Remote apps page or from a device's app manager. Paneless reads the apps' names and real icons from the device.
- **Remote apps, second screens and app windows keep showing when you lock the device.** Some devices (e.g. Pixels with Android 17) turned them black a few seconds after their own screen went off. The device still locks and its own screen stays off. Setting: *Keep app windows on when locked* (on by default; also per group or device).

**Wall**
- Screens that are off show "Screen off" and a Wake button instead of a black picture.
- Drag pictures into your own order, drag a picture's corner to resize it, or make them all one size.
- Right-click a picture: mirror, wake, unlock, turn the screen off, save a screenshot, second screen, apps, device settings, size, move, hide.

**Devices and mirroring**
- **USB:** devices on a cable are shown reliably next to Wi-Fi ones, and right-click › **Switch to Wi-Fi** lets you unplug and keep going.
- Second screens and app windows unlock the device first and respect "Turn device screen off".
- **Lock device when mirror closes**, globally, per group or per device.

**AI assistants**
- Device facts read better (battery health in words, uptime in days and hours, storage in GB), and devices that still need pairing say so.
- Fixed: AI apps that send a byte-order mark (e.g. from Windows PowerShell) couldn't connect.

## 1.0.0-beta.2 (2026-11-21)

A new layout with a navigation bar on the left, automation, overview pages, AI assistants, automatic updates and two more languages.

**New layout**
- Pages for **Devices**, **Wall**, **Logs**, **Automation**, **Inventory** and **AI**, with Settings at the bottom of the bar.

**Automation**
- **Recipes:** apps, files, scripts, commands and a restart that set up a device in one go, run on selected devices or a group.
- **Scheduled tasks:** scripts, recipes, restarts or commands daily, weekly or every few hours.
- **Alerts:** low battery, storage almost full, device too hot, device gone from the network, app crashed, as Windows notifications.

**Overviews**
- **Wall:** a live picture of every connected device; click one to mirror it.
- **Logs:** a device's live log with filters by importance, text and app, crashes in red, and saving to a file.
- **Inventory:** model, Android version, security patch, battery, storage, RAM, address and uptime of every device, exported as CSV or an HTML report.

**AI assistants**
- Paneless works as a tool (MCP server) for Claude Desktop, Claude Code, Codex (also in the ChatGPT desktop app), Gemini CLI and other MCP apps. One click adds it to Claude Desktop, Codex or Gemini CLI.
- Assistants can read device facts, apps and logs, take screenshots, tap, swipe and type, install apps, copy files, and run scripts, recipes and commands.
- You choose what assistants may do per device, per group or for all, and Paneless asks before one changes anything, naming the app that asks. A pause switch and an activity list are on the AI page.

**Around the app**
- **Automatic updates** from GitHub Releases, installed when you close Paneless (betas optional).
- **Script sharing:** export scripts to a file or the clipboard, import them from a file, the clipboard or a link such as a GitHub Gist.
- **German and French**, following Windows' language or chosen in Settings.
- **Light and dark mode** in Settings, following the system theme by default and switching live.
- Starting Paneless a second time brings the open window to the front.
- **Fixes & polish:** safe restore from the notification area (`--tray` / close to tray), reliable shortcut conflict reporting, AI `list_groups` respects hidden (`Off`) devices and `read_log` still finds logs of recently crashed apps, live group/recipe updates on the Automation page, and toolbar spacing when arranging second screens and app windows.

## 1.0.0-beta.1 (2026-11-21)

The first beta of Paneless: screen mirroring and device management for Android, for one device or a whole fleet. Built on [scrcpy](https://github.com/Genymobile/scrcpy), with an [Avalonia](https://avaloniaui.net/) interface.

**Devices**
- Finds devices with Wireless debugging on by itself (mDNS), and pairs new ones with the 6-digit code.
- Shows name, model, status, battery and address in a list that refreshes every few seconds.
- **Groups:** collapsible sections, *Mirror all*, and a group manager. Devices of a group that aren't around are listed too.
- Search, natural sort, and auto-connect for devices paired before.

**Mirroring**
- One click for one or many devices.
- Screen off, keep awake, audio, always on top.
- **Settings per device or group:** size, codec (H.264/H.265/AV1), bit rate, frame rate.
- **Auto-unlock** with a saved PIN or password (encrypted with DPAPI), typed only on a confirmed lock screen.
- **Second screens** and **apps in their own window** on virtual displays, which leave the device's screen alone.
- **Camera** windows that follow how the phone is held.
- **Arrange windows:** tile all mirrors, or one group's.

**Toolbar on every mirror**
- Rotate, back, home, recent apps, volume, power.
- Screen off, notifications, quick settings.
- Screenshot, record, camera, type the PC clipboard, second screen.
- **Key mapping** for games: tap keys and a WASD/arrow joystick.
- **Group control:** taps, swipes, scrolling and typing are repeated on the group's other devices.

**Managing devices and fleets**
- About 45 built-in adb commands with explanations, plus your own. Run them on any devices or groups.
- Scripts can run automatically every time a device connects.
- **App manager:** open, force-stop, disable, clear data, uninstall, install APKs (also on a group).
- Drop APKs or files on a device or a group heading to install or copy them on all of its devices at once.

**Around the app**
- Notification-area icon, start with Windows, a global shortcut per device.
- A Ko-fi support link behind the ♥ button and in Settings. Paneless never asks by itself.
- Optional update checks.
- Unexpected errors are logged instead of closing the app.
- Windows 10/11 installer: per user, no admin rights, .NET, scrcpy and adb included.
