# MonoRelay for Android

A phone or tablet on Android 8 or later can receive the Quest stream directly over local Wi-Fi.

## Install the APK

Download the Android APK from [Releases](https://github.com/CHXZAVARKA/MonoRelay/releases/latest). Open the file on your phone and approve installation from the app that opened it.

To install from a computer, enable USB debugging on the phone, plug it in, and approve the debugging prompt. Use the phone's serial from `adb devices`.

```sh
adb devices
adb -s SERIAL install -r path/to/MonoRelay-Android.apk
```

[ADB documentation](https://developer.android.com/tools/adb)

## Connect a Quest

1. [Set up ADB over Wi-Fi on the headset](../SETUP.md). Use a computer for the first setup. Connect the phone and Quest to the same local network.
2. Open MonoRelay. It looks for the Quest and connects when it finds one candidate. If the search comes up empty or finds several devices, tap Enter IP and enter the headset's address. Add `:port` if the debugging port differs from 5555.
3. Approve the debugging prompt on the Quest. The app tries to find the same headset the next time you open it.

The video opens fullscreen. Tap it to show the quality controls and Stop. Tap the video again to hide the controls. Changing quality briefly reconnects the stream. Android Back hides the controls if they are open and ends the stream otherwise. If you leave the app while a stream is running, it reconnects when you return.
