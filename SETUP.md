# Set up a Quest for MonoRelay

## First connection

1. Enable Developer Mode for the headset in the Meta Horizon phone app. [Meta's setup guide](https://developers.meta.com/vr/documentation/native/android/mobile-device-setup/).
2. Connect the Quest to your computer with a USB-C data cable. Put on the headset and approve the USB debugging prompt.
3. In Meta VR Developer Hub, open Device Manager and turn on ADB over Wi-Fi. Once it's enabled, unplug the cable. [Meta's Wi-Fi guide](https://developers.meta.com/vr/documentation/native/android/ts-mqdh-basic-usage/).
4. Put the Quest and the device running MonoRelay on the same local network. Open MonoRelay. If the headset asks you to authorize this computer or phone, approve it in the headset.

## Status messages

| In the app | What to do |
| --- | --- |
| Connect Quest by USB | Plug the headset into your computer to set up ADB over Wi-Fi. |
| Confirm in the headset | Approve the debugging prompt on your Quest. Tap Retry if the attempt timed out. |
| Enable ADB over Wi-Fi | Turn it on in Meta VR Developer Hub, unplug USB, then click Retry. |
| Looking for Quest | Keep the Quest on and connected to the same network. |
| Streaming | The video is ready. |

If the headset isn't found, use Enter IP and the address shown in the Quest's Wi-Fi settings. The default port is `5555`. You may need to re-enable ADB over Wi-Fi after a headset restart. Use wireless debugging on a network you trust.
