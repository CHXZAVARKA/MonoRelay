# MonoRelay

MonoRelay shows a single-eye view from a Meta Quest in a flat window over local Wi-Fi. It remembers the headset and tries to connect when you open the app.

## Install

Download a build from [Releases](https://github.com/CHXZAVARKA/MonoRelay/releases/latest).

- macOS 14+. [Install `scrcpy` and `adb`](BUILD.md#macos), then open `MonoRelay.app`. The ZIP is for Apple Silicon and is signed ad hoc. If macOS blocks the downloaded app, build it locally.
- Windows. Use x64 for Intel and AMD PCs or ARM64 for ARM PCs. Extract the entire ZIP and open `MonoRelay.exe`.
- Android 8+. Install the APK on your phone or tablet. See the [Android guide](Android/README.md).

## Connect a Quest

[Set up ADB over Wi-Fi on the headset](SETUP.md) before your first stream. Once it's enabled, open MonoRelay on a device on the same local network. The app looks for the headset and shows what to do next if it gets stuck.

## Quality

Choose a profile in the main window. On desktop, the video opens in its own window. On Android, video fills the screen. Tap the video to show or hide quality controls and Stop. Applying a different profile briefly reconnects the stream.

| Profile in the app | Max. long edge | Max. FPS | Video bitrate | Desktop video buffer | Audio buffer |
| --- | ---: | ---: | ---: | ---: | ---: |
| Speed | 1280 px | 30 | 5 Mbps | 0 ms | 50 ms |
| Balanced | 1600 px | 30 | 8 Mbps | 30 ms | 80 ms |
| Quality | 1920 px | 60 | 16 Mbps | 60 ms | 120 ms |

Auto starts at Balanced. It lowers quality after repeated frame skips and may try a higher setting once after 45 seconds of steady, active video. It adjusts while the player is in the foreground.

The connection and one-eye crop were tested on a Quest 3S. The crop may differ on other headsets.

[Build from source](BUILD.md) · [MIT license](LICENSE)

MonoRelay uses [scrcpy](https://github.com/Genymobile/scrcpy) and [Scrcpy Remote](https://github.com/dhun2666/scrcpy-client-for-android). The stream stays on your local network.
