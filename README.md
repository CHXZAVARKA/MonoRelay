# MonoRelay

MonoRelay is a small launcher for mirroring one trusted Meta Quest over local Wi‑Fi. Open it to start finding your headset immediately. Desktop builds remember the headset and working addresses locally and retry once after a dropped stream.

## Builds

- **macOS 14+** uses the native SwiftUI app and your installed scrcpy and ADB.
- **Windows** has portable x64 and ARM64 builds with the official scrcpy and ADB included. Extract the full ZIP and open `MonoRelay.exe`. To build, install the .NET 10 SDK and run `python Windows/package.py win-x64 path/to/official-scrcpy.zip MonoRelay-Windows.zip` (or `win-arm64`). The script checks the official [scrcpy release archive](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1) before packaging it.
- **Android 8+** is a [direct receiver preview](Android/README.md) built on Scrcpy Remote. Enter the headset address once and authorize this phone or tablet on the Quest.

## Build on your Mac

Requirements: macOS 14 or later, Xcode Command Line Tools, Android Platform Tools, and scrcpy 5.0.1 or later.

```sh
brew install scrcpy
brew install --cask android-platform-tools
./build.sh
open .build/MonoRelay.app
```

1. Put the Mac and Quest on the same local network. Enable ADB over Wi-Fi using [Meta VR Developer Hub](https://developers.meta.com/vr/documentation/native/android/ts-mqdh-basic-usage/) and approve your Mac on the headset.
2. For the first connection, open MonoRelay with your headset already visible over wireless ADB, or choose **Ввести IP…** and enter its IPv4 address. Include the port if the headset shows one. Approve local network access if macOS requests it.
3. On later launches MonoRelay checks known ADB connections, discovers the remembered headset's ADB service over mDNS, and tries its saved addresses on the current network. When its connection can't be found, use the address field to connect manually.

Mac and Quest must be able to reach each other. Guest networks can prevent this. mDNS only helps while the headset advertises an active ADB service. You may need to re-enable ADB over Wi-Fi after a headset restart. Use legacy ADB only on networks you trust.

## Display and sound

Choose the quality in the main window. The three manual profiles are shared across platforms. Limits apply to the long edge of the captured frame.

| Profile | Maximum size | Maximum FPS | Video bitrate | Desktop video buffer | Audio buffer |
| --- | ---: | ---: | ---: | ---: | ---: |
| Задержка | 1280 px | 30 | 5 Mbps | 0 ms | 50 ms |
| Баланс | 1600 px | 30 | 8 Mbps | 30 ms | 80 ms |
| Качество | 1920 px | 60 | 16 Mbps | 60 ms | 120 ms |

**Авто** is the default on a new installation. It starts at **Баланс**, steps down after repeated frame skips, and can try one step up after 45 seconds of consistently delivered active video. A still scene leaves quality unchanged. It measures the foreground player and briefly reconnects when applying a change. Existing saved selections are kept.

Selecting a new profile on desktop reconnects the video stream. On Android, return to the launcher to change profiles before connecting again. Actual frame rate and quality depend on the headset output and connection. Both desktop builds use software decoding and duplicate playback audio. MonoRelay includes a one-eye framing preset for a Quest 3S with the 10992×8000 display override. Other sizes use the left half of the capture. Framing can vary with Horizon OS, apps, and capture sizes. On Mac, turn off the one-eye option in **MonoRelay → Settings** for a full capture. You can also turn off Mac audio there. Those settings take effect at the next connection.

## Distribution

MonoRelay is focused on launching a single trusted headset. For Meta's built-in eye selection and capture options, see [Meta VR Developer Hub](https://developers.meta.com/vr/documentation/spatial-sdk/ts-mqdh-media/). For managing multiple Android devices, see [Scrcpy GUI](https://github.com/SimonAKing/scrcpy-gui) or [Escrcpy](https://github.com/viarotel-org/escrcpy).

`./build.sh /absolute/path/MonoRelay.app` builds for the current Mac architecture and signs the local bundle ad hoc. You can supply a Developer ID identity via `MONORELAY_SIGN_IDENTITY`. Public macOS binaries can be Developer ID signed and notarized with your own Apple account.

MonoRelay uses [Genymobile scrcpy](https://github.com/Genymobile/scrcpy) and Android Debug Bridge. The macOS bundle uses system-installed tools. Windows packages include official binaries and their notices; the Android build retains its upstream attribution. The app does not use an online account or send telemetry. MonoRelay's own code is [MIT licensed](LICENSE). This is an independent project, unaffiliated with Meta, Apple, or Genymobile.
