# MonoRelay for Android

A phone or tablet on Android 8 or later can receive the Quest stream directly over local Wi-Fi.

## Install the APK

Download `MonoRelay-0.4.0-Android-preview.apk` from [Releases](https://github.com/CHXZAVARKA/MonoRelay/releases/latest). Open the file on your phone and approve installation from the app that opened it.

To install from a computer, enable USB debugging on the phone, plug it in, and approve the debugging prompt. Use the phone's serial from `adb devices`.

```sh
adb devices
adb -s SERIAL install -r MonoRelay-0.4.0-Android-preview.apk
```

[ADB documentation](https://developer.android.com/tools/adb)

## Connect a Quest

1. Turn on ADB over Wi-Fi on the headset. Connect the phone and Quest to the same local network. [Meta's guide](https://developers.meta.com/vr/documentation/native/android/ts-mqdh-basic-usage/).
2. Enter the Quest's IP address in MonoRelay, choose a quality profile, and tap Connect (`Подключить`). Add `:port` to the IP address if the debugging port differs from 5555.
3. Approve the debugging prompt on the Quest. The app tries to find the same headset the next time you open it.

To change the profile manually, return to the main screen and connect again.
