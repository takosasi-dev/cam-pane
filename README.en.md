# CamPane

Shows the camera of an Android device in a window on a Windows PC. [日本語](README.md)

> **Work in progress. Nothing here is usable yet.**
> The repository currently contains only the measurement tools (probes) used to settle the design, for the PC and for Android. The app itself is not written yet.

## What this app does

**It sends the device's camera video to your PC.** The point is to show your hands (an instrument, a sketch on paper, a board game) in a call or screen share, using the camera of a tablet on your desk. The PC side just shows the video in an ordinary window; in Discord screen share or a recorder you pick that window.

Because it handles a camera, the design keeps to these rules:

- While the camera is running, the device always shows "Sending" and the name of the PC it sends to.
- It only streams while the app is on screen. Sending it to the background or turning the screen off stops the camera.
- Only the "Start" button on the device can open the camera. The PC has no command to make the device open its camera.
- Video is never stored. The only thing saved is a single frame when you press "Snapshot" on the PC.
- No audio. The app does not ask for the microphone permission.
- It is not a virtual camera. No driver is written or installed. The PC side never runs as administrator.
- All traffic is TLS and only paired devices get through. Nothing is sent to the internet.

### How to stop it

- Press "Stop" on the device, or close the app on the device.
- Close the video window on the PC (the device's camera stops too).

## Status

| Phase | Content | State |
|---|---|---|
| Phase 0 | Probes | PC and Android sides written. Encoding on a dtab (Android 7.0) and decoding it on the PC are confirmed. `show` and screen share are not checked yet |
| Phase 1–3 | The app | Not started |

## Download

The v0.0.1 pre-release on [Releases](https://github.com/takosasi-dev/cam-pane/releases) has the probe APK and a Windows zip.

- **Windows zip**: bundles the .NET 10 runtime, so you do not need to install .NET. Extract it and run `CamPane.Probe.exe` from PowerShell.
- **APK**: **a provisional build signed with a debug key.** Once builds are signed with the real key, you will need to uninstall it and install again.

## What is here now: the PC-side probe

Requirements: Windows 11 (x64), .NET 10 SDK.

```powershell
dotnet build pc\CamPane.sln -c Release
```

Run `CamPane.Probe.exe` with no arguments to see the usage.

| Subcommand | What it does |
|---|---|
| `decoders` | Lists H.264 decoders |
| `decode <file>` | Decodes a raw H.264 (Annex B) file and reports frame count and size. Since v0.0.2 it compares four ways of feeding ((a) as is, (b) low-latency mode, (c) one picture per sample, (d) SPS rewritten to declare no reordering) and reports how many pictures the decoder holds back |
| `show <file>` | Decodes the same file and loops it in a window (to check that screen share can capture it) |

The probe never writes decoded images to disk. Delete the video file you feed it after checking (`.gitignore` keeps video files out of the repository).

## What is here now: the Android probe

It lists the device's cameras and H.264 encoders on screen, then encodes 5 seconds of camera video and saves it as a raw H.264 (Annex B) file. Every line on screen also goes to logcat (tag `CamPaneProbe`).

- The only permission is CAMERA. It does not use the network (no INTERNET permission).
- There are two encode paths. The default goes through GL, because on some devices the direct path produces no frames (the camera side rejects it).
- It only encodes while the app is on screen. Sending it to the background closes the camera and the encoder.

Build (JDK 17 and the Android SDK; put `sdk.dir` in `android\local.properties`):

```powershell
cd android; .\gradlew.bat assembleDebug
```

Usage:

- Tap "5 秒圧縮して保存" (encode 5 seconds and save; GL path).
- From adb:

```powershell
adb shell am start -n io.github.takosasidev.campane.probe/.MainActivity --es action encode         # via GL
adb shell am start -n io.github.takosasidev.campane.probe/.MainActivity --es action encode-direct  # direct
```

Files are saved to `/storage/emulated/0/Android/data/io.github.takosasidev.campane.probe/files/` (`campane_probe_<datetime>.h264`). Copy one to the PC and feed it to the PC-side probe:

```powershell
adb pull /storage/emulated/0/Android/data/io.github.takosasidev.campane.probe/files/campane_probe_<datetime>.h264
CamPane.Probe.exe decode campane_probe_<datetime>.h264
CamPane.Probe.exe show campane_probe_<datetime>.h264
```

**The video shows your room. Delete the file on both the device and the PC after checking** (on the device, `adb shell rm <path>`, or uninstall the app, which removes it too).

## License

MIT ([LICENSE](LICENSE)). Third-party licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
