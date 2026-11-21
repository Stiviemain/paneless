# Changelog

All notable changes to Paneless. Versions follow [Semantic Versioning](https://semver.org).

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
