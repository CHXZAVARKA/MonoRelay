# MonoRelay

MonoRelay shows a single-eye view from a Meta Quest in a flat window over local Wi-Fi. It remembers the headset and tries to connect when you open the app.

## Install

Download a build from [Releases](https://github.com/CHXZAVARKA/MonoRelay/releases/latest).

- macOS 14+. [Install `scrcpy` and `adb`](BUILD.md#macos), then open `MonoRelay.app`. The ZIP is for Apple Silicon and is signed ad hoc. If macOS blocks the downloaded app, build it locally.
- Windows. Use x64 for Intel and AMD PCs or ARM64 for ARM PCs. Extract the entire ZIP and open `MonoRelay.exe`.
- Android 8+. Install the preview APK on your phone or tablet. See the [Android guide](Android/README.md).

## Connect a Quest

1. Enable developer mode on the headset. Open Device Manager in Meta VR Developer Hub and turn on ADB over Wi-Fi. The first setup may require a USB cable. [Meta's guide](https://developers.meta.com/vr/documentation/native/android/ts-mqdh-basic-usage/).
2. Connect the headset and the receiving device to the same local network.
3. Open MonoRelay. It looks for a wireless Quest and connects when it finds one candidate. If it cannot choose an address, select Enter IP (`Ввести IP`) and enter the address from the headset's Wi-Fi settings. Add `:port` if the debugging port differs from 5555. Approve the debugging prompt on the headset.

You may need to turn ADB over Wi-Fi back on after restarting the Quest. A guest network may block connections between devices. Use wireless debugging on a network you trust.

## Quality

Choose a profile in the main window. The phone shows the video on that same screen. On desktop, the video opens in its own window. Applying a different profile briefly reconnects the stream.

| Profile in the app | Max. long edge | Max. FPS | Video bitrate | Desktop video buffer | Audio buffer |
| --- | ---: | ---: | ---: | ---: | ---: |
| Speed (`Скорость`) | 1280 px | 30 | 5 Mbps | 0 ms | 50 ms |
| Balanced (`Баланс`) | 1600 px | 30 | 8 Mbps | 30 ms | 80 ms |
| Quality (`Качество`) | 1920 px | 60 | 16 Mbps | 60 ms | 120 ms |

Auto (`Авто`) starts at Balanced. It lowers quality after repeated frame skips and may try a higher setting once after 45 seconds of steady, active video. It adjusts while the player is in the foreground.

The connection and one-eye crop were tested on a Quest 3S. The crop may differ on other headsets.

[Build from source](BUILD.md) · [MIT license](LICENSE)

MonoRelay uses [scrcpy](https://github.com/Genymobile/scrcpy) and [Scrcpy Remote](https://github.com/dhun2666/scrcpy-client-for-android). The stream stays on your local network.
