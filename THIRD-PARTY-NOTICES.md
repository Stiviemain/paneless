# Third-party notices

Paneless itself is licensed under the [MIT License](LICENSE). The Windows installer and portable zip also contain the following third-party software, each under its own license.

## Bundled with the installer (`scrcpy\` folder)

These files come unchanged from the official scrcpy Windows release ([scrcpy-win64 v5.0.1](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1)). The build checks the download against a pinned SHA-256.

| Component | Files | License | Source |
|---|---|---|---|
| scrcpy | `scrcpy.exe`, `scrcpy-server` | Apache License 2.0 (`scrcpy\LICENSE.txt`). Copyright (C) Genymobile and Romain Vimont | https://github.com/Genymobile/scrcpy |
| Android Debug Bridge (adb) | `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll` | Apache License 2.0, The Android Open Source Project | https://android.googlesource.com/platform/packages/modules/adb |
| FFmpeg | `avcodec-*.dll`, `avformat-*.dll`, `avutil-*.dll`, `swresample-*.dll` | GNU LGPL 2.1 or later | https://ffmpeg.org (build scripts: scrcpy repository, `app/deps/`) |
| SDL 3 | `SDL3.dll` | zlib License | https://github.com/libsdl-org/SDL |
| libusb | `libusb-1.0.dll` | GNU LGPL 2.1 or later | https://github.com/libusb/libusb |

The LGPL libraries are dynamically linked by scrcpy and can be replaced with compatible builds. Their source code is available at the links above, and is also available on request through this project's issue tracker.

## Libraries in Paneless (NuGet packages)

| Component | License | Source |
|---|---|---|
| Avalonia (`Avalonia*.dll`) | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter: the Inter typeface | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| ANGLE (`av_libglesv2.dll`, via Avalonia.Angle.Windows.Natives) | BSD 3-Clause | https://chromium.googlesource.com/angle/angle |
| SkiaSharp / Skia (`SkiaSharp.dll`, `libSkiaSharp.dll`) | MIT / BSD 3-Clause | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp / HarfBuzz (`HarfBuzzSharp.dll`, `libHarfBuzzSharp.dll`) | MIT / Old MIT | https://github.com/mono/SkiaSharp, https://github.com/harfbuzz/harfbuzz |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Velopack (installer and updates) | MIT | https://github.com/velopack/velopack |
| MicroCom.Runtime | MIT | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol | MIT | https://github.com/tmds/Tmds.DBus |
| .NET runtime (self-contained) | MIT | https://github.com/dotnet/runtime |

The full license texts are at the linked repositories. Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0. GNU LGPL 2.1: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html. SIL OFL 1.1: https://openfontlicense.org.

## Trademarks

Android is a trademark of Google LLC. Pixel is a trademark of Google LLC. Paneless isn't affiliated with or endorsed by Google, Genymobile or any device maker.
