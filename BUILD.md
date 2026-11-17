# Build MonoRelay

## macOS

Install Xcode Command Line Tools on macOS 14 or later. Both downloaded and locally built copies need `scrcpy` 5.0.1 or later and Android Platform Tools.

```sh
brew install scrcpy
brew install --cask android-platform-tools
```

Build the app for your Mac and open it.

```sh
./build.sh
open .build/MonoRelay.app
```

Pass an output path as the first argument to `build.sh` if you want to save the bundle elsewhere. The script signs it ad hoc. To use a Developer ID certificate, set `MONORELAY_SIGN_IDENTITY` to its name.

## Windows

Install the .NET 10 SDK and Python 3. Download the official [scrcpy 5.0.1 ZIP](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1) for your target architecture. `package.py` checks its hash and bundles scrcpy, ADB, and .NET with the app.

```sh
python Windows/package.py win-x64 scrcpy-win64-v5.0.1.zip MonoRelay-Windows-x64.zip
python Windows/package.py win-arm64 scrcpy-winarm64-v5.0.1.zip MonoRelay-Windows-arm64.zip
```

## Android

Install JDK 21 and Android SDK 36. The build script checks out [Scrcpy Remote 3.5.7](https://github.com/dhun2666/scrcpy-client-for-android/tree/889bae6a92172f4fb8fa19ca173e330ef64e2abf), applies the MonoRelay patch, and produces a release-mode APK signed with your local Android debug key.

```sh
./Android/build.sh MonoRelay-Android.apk
```

[Install the APK and connect a Quest](Android/README.md)
