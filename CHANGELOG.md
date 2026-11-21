# Changelog

All notable changes to Paneless. Versions follow [Semantic Versioning](https://semver.org).

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
