# MonoRelay for Android

This preview receives the headset stream directly over local Wi-Fi. It reuses [Scrcpy Remote 3.5.7](https://github.com/dhun2666/scrcpy-client-for-android/tree/889bae6a92172f4fb8fa19ca173e330ef64e2abf) for Android ADB authorization, scrcpy 5.0.1, and display/audio decoding. The MonoRelay launcher, three stream presets and one-eye cropping are the changes in `upstream.patch`. No desktop relay is needed.

On first launch, enter the Quest's IPv4 address. Add the debug port if it is different from 5555. If prompted, authorize this Android device on the Quest. Later launches try a saved Quest's local legacy ADB announcement, then its saved endpoint. If the headset is not advertising or its IP changed, enter the address again. Choose the quality in the launcher before connecting. In **Авто**, the foreground viewer adjusts the profile based on presented and skipped frames.

Build for sideloading with JDK 21 and Android SDK 36, then run `./Android/build.sh` at the repository root. The APK is debug-signed for local testing. Use a dedicated release signing key before external distribution. The base engine is Apache-2.0; its licenses and dependency notices remain in the build. The launcher patch is available under this project's MIT license.
