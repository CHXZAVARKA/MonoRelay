# MonoRelay

MonoRelay is a small native macOS launcher for mirroring Meta Quest to your Mac with scrcpy. Open it to start finding your headset immediately. It remembers the headset and working addresses locally, and retries once if a running stream loses the connection.

## Run on your Mac

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

The defaults are 30 fps, 8 Mbps, software decoding, and duplicated playback audio. MonoRelay includes a one-eye framing preset for a Quest 3S with the 10992×8000 display override. Other sizes use the left half of the capture. Framing can vary with Horizon OS, apps, and capture sizes. For a full capture, turn off the one-eye option in **MonoRelay → Settings**. You can also turn off Mac audio there. Settings take effect at the next connection.

## Distribution

MonoRelay is focused on launching a single trusted headset. For Meta's built-in eye selection and capture options, see [Meta VR Developer Hub](https://developers.meta.com/vr/documentation/spatial-sdk/ts-mqdh-media/). For managing multiple Android devices, see [Scrcpy GUI](https://github.com/SimonAKing/scrcpy-gui) or [Escrcpy](https://github.com/viarotel-org/escrcpy).

`./build.sh /absolute/path/MonoRelay.app` builds for the current Mac architecture and signs the local bundle ad hoc. You can supply a Developer ID identity via `MONORELAY_SIGN_IDENTITY`. Public macOS binaries can be Developer ID signed and notarized with your own Apple account.

MonoRelay uses the system-installed `adb` and [Genymobile scrcpy](https://github.com/Genymobile/scrcpy). They are maintained independently and aren't included in the bundle. The app does not use an online account or send telemetry. MonoRelay's own code is [MIT licensed](LICENSE). This is an independent project, unaffiliated with Meta, Apple, or Genymobile.
