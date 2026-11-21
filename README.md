<p align="center">
  <img src="Paneless.png" width="96" alt="Paneless logo">
</p>

<h1 align="center">Paneless</h1>

<p align="center">
  <a href="https://github.com/Stiviemain/paneless/releases"><b>⬇&nbsp;&nbsp;Download Paneless for Windows</b></a>
  &nbsp;·&nbsp; <a href="#install">How to install</a>
  &nbsp;·&nbsp; <a href="CHANGELOG.md">What's new</a>
</p>

<p align="center">
  <b>Painless screen mirroring and device management for your Android devices.</b><br>
  Finds the phones, tablets and watches on your Wi-Fi, mirrors any of them to your PC with one click,<br>
  and manages one device or a whole fleet at once: install apps, run scripts, change settings, control groups together.<br>
  Built on <a href="https://github.com/Genymobile/scrcpy">scrcpy</a>, with an interface made with <a href="https://avaloniaui.net/">Avalonia</a>.
</p>

<p align="center">
  <a href="https://github.com/Stiviemain/paneless/releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/Stiviemain/paneless?include_prereleases&label=release"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue"></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4">
  <img alt="Android 11+" src="https://img.shields.io/badge/Android-11%2B-3ddc84">
  <a href="https://avaloniaui.net/"><img alt="Built with Avalonia" src="https://img.shields.io/badge/UI-Avalonia-8b44ac"></a>
</p>

<p align="center">
  <img src="docs/images/main-tall.png" width="760" alt="The Paneless device list with groups">
</p>

---

## Contents

- [Why Paneless](#why-paneless)
- [Features](#features)
- [Install](#install)
- [First use](#first-use)
- [Using Paneless](#using-paneless)
- [Privacy and security](#privacy-and-security)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)
- [Credits and license](#credits-and-license)

## Why Paneless

[scrcpy](https://github.com/Genymobile/scrcpy) is excellent, but it's a command-line tool: you have to find each device's address, pair it, connect it and remember the right options. Paneless does that for you, and goes beyond watching a screen:

- **See:** a live list of every device on your network, with status and battery, and their screens on your PC with a toolbar on every mirror.
- **Manage:** organise devices in groups, then act on one device or a whole group at once.
  - Install or remove apps.
  - Copy files.
  - Run adb scripts.
  - Apply settings.
- **Control:** use your keyboard and mouse on a device, map keys for games, or do something once and have it happen on every device of a group.
- **Automate:** connect known devices by themselves, run scripts every time a device connects (e.g. turn off Doze), start mirrors by themselves.

That makes it handy at home with a phone and a tablet, and just as useful with a shelf of test devices, kiosk tablets or a family's phones.

Everything runs locally between your PC and your devices. Paneless has no account, no cloud and no app on the phone.

The interface is built with [Avalonia](https://avaloniaui.net/), a cross-platform .NET UI framework. That keeps Paneless a fast, native desktop app (no browser engine inside) and lets the same code run on macOS and Linux later.

## Features

**Finding and organising devices**
- **Finds devices by itself** (wireless debugging over mDNS) and shows name, model, status, battery and address. The list refreshes every few seconds.
- **Pairs new devices** with the 6-digit code. The pairing address fills itself in.
- **Groups:** collapsible sections, *Mirror all*, and a group manager. Devices of a group that aren't around are still listed.
- **Search and natural sort** ("Lab phone 2" before "Lab phone 10") for long lists.
- **Connects known devices by itself** when they show up on the network.

**Mirroring**
- One click (or double-click, or Enter) to mirror one or many devices.
- Keep the device's own screen off while mirroring, keep it awake, play its audio on the PC, keep the window on top.
- **Settings per device or per group:** max size, video codec (H.264, H.265, AV1), bit rate and frame-rate limit.
- **Auto-unlock:** a saved PIN or password is entered when you mirror a locked device. Swipe patterns aren't supported.
- **Second screen:** a separate, resizable screen of the device next to its own.
- **Apps in their own window,** on a virtual display, so the device's screen is left alone.
- **Camera:** the back or front camera in a window. The picture follows how the phone is held.
- **Arrange windows:** tile all mirrors, or one group's, across the screen.

**Toolbar on every mirror**
- Rotate, back, home, recent apps, volume, power.
- Screen off, notifications, quick settings.
- Screenshot, record, camera, type the PC clipboard.
- **Key mapping** for games: keys become taps and a WASD or arrow-key joystick.
- **Group control:** what you do on one device happens on the whole group.

<p align="center"><img src="docs/images/toolbar.png" height="420" alt="Mirror toolbar">&nbsp;&nbsp;<img src="docs/images/groups.png" width="560" alt="Group manager"></p>

**Managing devices, one or a whole fleet**
- **Act on a group in one go:** drop an APK on a group heading to install it on every device in it; send files the same way; run a script on the whole group.
- **Scripts:** your own adb commands and about 45 built-in ones with explanations (battery and Doze, display, apps, network, device info, …), run on any devices or groups at once.
  - Per-device output.
  - Fields like `{package}` are asked for when a script runs.
  - Risky scripts ask first.
- **Run scripts automatically** every time a device connects, e.g. *Turn off Doze*, so settings that reset on restart stay the way you want.
- **App manager:** open, force-stop, disable, clear data, uninstall, install APKs (also on a whole group).
- **Settings per device or group:** mirror quality, behaviour on connect, a folder for files.
- **Group control:** do something on one device and it happens on every connected device of its group.

<p align="center"><img src="docs/images/scripts.png" width="760" alt="Scripts window"></p>

**Around the app**
- Notification-area icon, optional start with Windows.
- A global shortcut per device (e.g. Ctrl+Alt+P).
- Optional update checks.

## Install

**Windows 10 or 11 (64-bit).** Download `Paneless-<version>-Setup.exe` from [Releases](https://github.com/Stiviemain/paneless/releases) and run it.

- Installs for your Windows user only, so it needs **no admin rights**.
- Includes everything it needs: .NET, scrcpy and adb. Nothing else to install.
- Adds Start menu and desktop shortcuts, and an entry under **Settings › Apps › Installed apps** to uninstall it.

There's also a portable zip if you don't want to install it.

> **Windows SmartScreen** may warn about an unknown publisher, because the installer isn't code-signed yet. Click *More info › Run anyway*, or check the SHA-256 checksum published with the release first.

macOS and Linux builds are planned: the code is already cross-platform thanks to Avalonia, but those builds aren't packaged or tested yet.

## First use

On each device, once:

1. **Settings › About phone**, tap **Build number** 7 times to unlock developer options.
2. **Settings › System › Developer options**, turn on **Wireless debugging**.
3. In Paneless, click **+ Add device › Pair with a code**. On the device, tap **Pair device with pairing code** and type the 6-digit code into Paneless.

From then on, the device appears whenever Wireless debugging is on and it's on the same Wi-Fi. Android 17 turns wireless debugging back on by itself on trusted networks.

## Using Paneless

| To… | Do this |
|---|---|
| Mirror a device | Double-click it, or select it and press **Mirror** / Enter |
| Mirror several | Ctrl- or Shift-click them, then **Mirror** |
| Mirror a whole group | **Mirror all** on the group's heading |
| Make or edit groups | **Groups…**, or right-click devices › **Move to group** |
| Change settings for one device | Right-click › **Device settings…** (a group: its ⋯ › **Group settings…**) |
| Run a command on devices | **Run script…** (on a group: its ⋯ menu) |
| Run something every time a device connects | **Device settings › When it connects** |
| Install an app | Drop the APK on a device or group, or **More › Apps…** |
| Turn the camera picture | It follows the phone; Alt+← / Alt+→ in the camera window turn it by hand |
| Repeat your taps on a group | Toolbar › **Group control** |

<p align="center"><img src="docs/images/device-settings.png" width="640" alt="Device settings"></p>

**Where your data is** (Windows): `%LOCALAPPDATA%\Paneless`.
- `known-devices.json`, `settings.json`, `groups.json`, `profiles.json`, `scripts.json`, `keymaps.json`
- `unlock-codes.json`, encrypted (see below)

Screenshots and recordings go to `Documents\Paneless`. Logs are in `%TEMP%\paneless`.

## Privacy and security

- **Local only.** Paneless talks to your devices over your own network with adb and scrcpy. It sends nothing anywhere else.
  - The one exception is update checks, and only if you set an update source.
- **Unlock codes** are encrypted with Windows DPAPI, the protection Credential Manager and browsers use. Only your Windows account on this PC can read them (on macOS the keychain, on Linux the Secret Service).
  - A code is typed only after Paneless confirms the device is locked and its lock screen is in front.
  - One failed try pauses auto-unlock for that device.
- **Wireless debugging** gives full control of a device to any computer you paired. Only pair computers you trust, and turn it off on networks you don't.
- Found a vulnerability? Please see [SECURITY.md](SECURITY.md).

## Troubleshooting

<details>
<summary><b>A device doesn't show up</b></summary>

- Wireless debugging must be on, and the device on the same network as the PC.
- Some routers and VPNs block local-network discovery (mDNS). Use **+ Add device › Connect by address** with the IP:port shown under Wireless debugging.
- After a reset of Wireless debugging, pair the device again.
</details>

<details>
<summary><b>"Allow on device" / unauthorized</b></summary>

Unlock the device and accept the debugging prompt ("Always allow from this computer").
</details>

<details>
<summary><b>The mirror is choppy</b></summary>

In **Settings** (or Device settings), lower *Max size* or *Bit rate*, or try *H.265*. 5 GHz Wi-Fi helps a lot.
</details>

<details>
<summary><b>The camera turns off when the phone rotates</b></summary>

Some phones (e.g. Pixels) cut a camera off when they rotate. Paneless reopens it by itself, turned to match, in the same place.
</details>

<details>
<summary><b>Where are the logs?</b></summary>

`%TEMP%\paneless`: one log per mirror window, `on-connect.log` for automatic scripts, and `errors.log` for unexpected errors. Please attach the relevant one to bug reports, after checking it for anything private.
</details>

## Building from source

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Windows 10/11 for the installer.

```bash
git clone https://github.com/Stiviemain/paneless.git
```
```bash
dotnet run --project paneless/src/Paneless.App
```

For running from source, scrcpy must be installed, e.g. with `winget install Genymobile.scrcpy`. The installer build bundles it.

To build the installer: `powershell -File build.ps1`, which writes `releases\PanelessApp-win-Setup.exe`. Architecture, test flags and the release process are in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Credits and license

Paneless is [MIT licensed](LICENSE). It stands on the shoulders of:
- [scrcpy](https://github.com/Genymobile/scrcpy) by Genymobile and Romain Vimont, which does the actual mirroring;
- Android's adb;
- [Avalonia](https://avaloniaui.net/), the UI framework every window is built with;
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) and [Velopack](https://velopack.io) (installer and updates).

Their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Paneless isn't affiliated with Google, Genymobile, AvaloniaUI or any device maker. Android is a trademark of Google LLC.
