# MonoRelay for Android

A phone or tablet on Android 8 or later can receive the Quest stream directly over local Wi-Fi.

## Install the APK

Download the Android preview APK from [Releases](https://github.com/CHXZAVARKA/MonoRelay/releases/latest). Open the file on your phone and approve installation from the app that opened it.

To install from a computer, enable USB debugging on the phone, plug it in, and approve the debugging prompt. Use the phone's serial from `adb devices`.

```sh
adb devices
adb -s SERIAL install -r path/to/MonoRelay-Android-preview.apk
```

[ADB documentation](https://developer.android.com/tools/adb)

## Connect a Quest

1. Turn on ADB over Wi-Fi on the headset. Connect the phone and Quest to the same local network. [Meta's guide](https://developers.meta.com/vr/documentation/native/android/ts-mqdh-basic-usage/).
2. Open MonoRelay. It looks for the Quest and connects when it finds one candidate. If the search comes up empty or finds several devices, tap Enter IP (`Ввести IP`) and enter the headset's address. Add `:port` if the debugging port differs from 5555.
3. Approve the debugging prompt on the Quest. The app tries to find the same headset the next time you open it.

Change quality below the video. The stream reconnects briefly when you switch profiles. Tap Stop (`Стоп`) or use Android Back to end the stream. If you leave the app while a stream is running, it reconnects when you return.
