# Сборка MonoRelay

## macOS

Нужны macOS 14+, Xcode Command Line Tools, `scrcpy` 5.0.1 или новее и Android Platform Tools. Установи зависимости даже для готовой сборки.

```sh
brew install scrcpy
brew install --cask android-platform-tools
```

Сборка для текущей архитектуры Mac появится в `.build/MonoRelay.app`.

```sh
./build.sh
open .build/MonoRelay.app
```

Первый аргумент `build.sh` задаёт путь к приложению. Скрипт подписывает его локально. Для подписи Developer ID передай имя сертификата через `MONORELAY_SIGN_IDENTITY`.

## Windows

Установи .NET 10 SDK и Python 3. Скачай официальный ZIP [scrcpy 5.0.1](https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1) для нужной архитектуры. `package.py` проверяет хеш архива и кладёт `scrcpy`, ADB и .NET в ZIP рядом с приложением.

```sh
python Windows/package.py win-x64 scrcpy-win64-v5.0.1.zip MonoRelay-Windows-x64.zip
python Windows/package.py win-arm64 scrcpy-winarm64-v5.0.1.zip MonoRelay-Windows-arm64.zip
```

## Android

Установи JDK 21 и Android SDK 36. Скрипт берёт [Scrcpy Remote 3.5.7](https://github.com/dhun2666/scrcpy-client-for-android/tree/889bae6a92172f4fb8fa19ca173e330ef64e2abf), применяет изменения MonoRelay и собирает APK с отладочной подписью.

```sh
./Android/build.sh MonoRelay-Android-preview.apk
```

[Установка APK и подключение](Android/README.md)
