# MonoRelay на Android

Телефон или планшет на Android 8+ принимает стрим с Quest напрямую по локальному Wi-Fi.

## Установка APK

Скачай `MonoRelay-0.4.0-Android-preview.apk` со страницы [Releases](https://github.com/CHXZAVARKA/MonoRelay/releases/latest). Открой файл на телефоне и подтверди установку из выбранного источника.

Для установки с компьютера включи отладку по USB на телефоне, подключи его кабелем и подтверди запрос. Возьми серийный номер телефона из `adb devices`.

```sh
adb devices
adb -s SERIAL install -r MonoRelay-0.4.0-Android-preview.apk
```

`SERIAL` указывает на телефон, на котором будет показан стрим. [Документация ADB](https://developer.android.com/tools/adb).

## Подключение к Quest

1. Включи ADB по Wi-Fi на Quest по [гайду Meta](https://developers.meta.com/vr/documentation/native/android/ts-mqdh-basic-usage/). Подключи телефон и Quest к одной сети.
2. В MonoRelay введи IP очков, выбери качество и нажми «Подключить». Если порт отладки отличается от 5555, укажи его в поле IP через двоеточие.
3. Подтверди запрос отладки в шлеме. При следующем запуске приложение попробует найти сохранённый Quest.

Чтобы сменить режим вручную, вернись в главное окно и подключись снова.
