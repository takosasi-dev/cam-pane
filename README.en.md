# CamPane

Shows the camera of an Android device in a window on a Windows PC. [日本語](README.md)

> **Work in progress. Nothing here is usable yet.**
> The repository currently contains only the PC-side measurement tool (probe) used to settle the design. The PC app and the Android app are not written yet.

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
| Phase 0 | Probes | PC side builds. **It has never been run yet.** Android side not written |
| Phase 1–3 | The app | Not started |

## What is here now: the PC-side probe

Requirements: Windows 11 (x64), .NET 10 SDK.

```powershell
dotnet build pc\CamPane.sln -c Release
```

Run `CamPane.Probe.exe` with no arguments to see the usage.

| Subcommand | What it does |
|---|---|
| `decoders` | Lists H.264 decoders |
| `decode <file>` | Decodes a raw H.264 (Annex B) file and reports frame count and size |
| `show <file>` | Decodes the same file and loops it in a window (to check that screen share can capture it) |

The probe never writes decoded images to disk. Delete the video file you feed it after checking (`.gitignore` keeps video files out of the repository).

## License

MIT ([LICENSE](LICENSE)). Third-party licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
