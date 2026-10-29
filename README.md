# WebcamSwitcher — Camera Roles edition

A Windows 11 app that turns USB webcams into **custom Media Foundation virtual
cameras with fixed names** — `CAM-01`, `CAM-02`, … — so that identical camera
models can be used side by side and never swap places.

Based on [TheRealAlexV/WebcamSwitcher](https://github.com/TheRealAlexV/WebcamSwitcher)
(MIT) at commit `943fe8e`. The original per-camera passthrough cameras,
switcher camera, hotkeys and video delay are kept; the switcher is now off by
default.

> 日本語の運用ガイド: **[docs/camera-roles.md](docs/camera-roles.md)**
> (導入・カメラ登録・展示時の起動・トラブルシューティング・アンインストール・既知の制約)
> 変更点と元プロジェクトとの差分: [docs/CHANGES-camera-roles.md](docs/CHANGES-camera-roles.md)
> 動作確認・性能測定結果: [docs/test-report.md](docs/test-report.md)

## How it works

```
 physical device ID  ──▶  Camera Role "CAM-01"  ──▶  pipe ...Frames.v1.r.<VirtualCameraId>  ──▶  virtual camera "CAM-01"
 (\\?\USB#VID_..#6&..)      (config, by ID only)        (one per role, persistent ID)              (Windows: "CAM-01 (Windows Virtual Camera)")
```

- A physical camera is identified **only by its Windows device ID** (symbolic
  link). Never by list position, device index, enumeration number, friendly
  name, or "first camera found".
- Each **Camera Role** owns a fixed virtual camera name, a persistent virtual
  camera ID and its own frame pipe. Reordering roles, a different enumeration
  order after a reboot, or identical friendly names change nothing.
- A role whose device ID is not present is **MISSING**. It is never given
  another camera; its virtual camera stays registered and shows a diagnostic
  picture (`CAM-01 / PHYSICAL CAMERA MISSING / last frame …`).
- Reconnects target the same device ID only (configurable interval, count,
  "retry forever", startup wait). One role failing never stops another.
- Default output: **1280×720 @ 30 fps NV12**. A 640×480 camera is fitted by
  **Letterbox** (default), Crop or Stretch — done by the frame reader's scaler,
  so the app itself copies, never rescales. A camera that already delivers
  1280×720 is passed through.
- Capture format **Auto** never assumes MJPEG. It prefers a mode that reaches
  the output frame rate; when an uncompressed mode would saturate a USB 2.0 bus
  (> 160 Mbps, e.g. 720p30 YUY2 = 442 Mbps) and the camera offers MJPEG, MJPEG is
  used and decoded by Media Foundation.
- Status per role: UNASSIGNED, CONNECTING, CONNECTED, MISSING, RECONNECTING,
  NO SIGNAL, FORMAT ERROR, VIRTUAL CAMERA STOPPED, DUPLICATED, ERROR — with
  input/output format and fps, frames, drops, last frame time, processing time,
  reconnects and whether a consumer is reading the virtual camera. USB problems
  are reported as such (e.g. Windows' `0x800718CF` "insufficient bandwidth").

## Measured added latency

Measured with a timestamp barcode written at the physical camera's frame
callback and decoded in a consumer's frame callback (`tools/CaptureTest
consume CAM-xx --latency`, app setting `LatencyProbe: true`), 1280×720@30, on a
Windows 11 laptop (Intel), see [docs/test-report.md](docs/test-report.md):

| Condition | avg | p50 | p95 | max |
|---|---|---|---|---|
| 1 role, integrated webcam (NV12 passthrough) | 52.7–53.1 ms (1.6 frames) | 52–54 ms | 75–77 ms | 89–91 ms |
| 8 roles running, 6 held by Chromium, real camera role | 49.5–55.3 ms (1.5–1.7 frames) | 49–58 ms | 76–77 ms | 80–83 ms |
| same, synthetic-source role | 57.8–64.4 ms (1.7–1.9 frames) | 48–64 ms | 78–80 ms | 80–82 ms |

This is the delay from the moment the app receives a camera frame until a
consumer application receives it from the virtual camera (pipe, Frame Server's
30 fps clock and the consumer's own Media Foundation reader included). The
average stays under 2 frames; p95 is about 2.3 frames. Sensor exposure and USB
transfer before the app's callback are not included.

## Requirements

- Windows 11 (22H2+) x64. No driver, no code-signing.
- Administrator rights once, to register the media source DLL (installer does it).
- The DLL must be outside user profiles (the Frame Server runs as LOCAL SERVICE):
  install to `C:\Program Files\WebcamSwitcher`.

## Installation

Run `WebcamSwitcher-Setup-<version>.exe`, or unzip the portable build outside
your user profile and run `regsvr32 WebcamSwitcher.Source.dll` once from an
elevated prompt. Details: [docs/camera-roles.md](docs/camera-roles.md).

## Usage

1. **Register cameras** (main window or tray): every connected physical camera
   is listed with a live picture, its friendly name, the distinguishing device
   ID tail (copy the full ID with one click), input format and state. Pick a
   Camera Role for each, add roles as needed (no fixed maximum), and **Save &
   apply** — the role's virtual camera is registered at once.
2. In TouchDesigner / the MediaPipe TouchDesigner plugin / a browser, select
   `CAM-01`, `CAM-02`, … — never the physical camera, which the app holds.
3. Renaming a role stops and unregisters only that role's old virtual camera
   before registering the new name. Other roles keep running.

Duplicates are refused: one device ID in two roles, two roles with the same
virtual camera name, or the same virtual camera ID twice.

### Settings

`%APPDATA%\WebcamSwitcher\config.json` (schema 2; sample:
[docs/config.sample.json](docs/config.sample.json)). Saved atomically (temp
file + flush + replace) with a `.bak`; an unreadable file falls back to the
backup and is kept as `.corrupt-*`; unknown fields are preserved; schema 1
files (`FriendlyName`) migrate automatically.

Startup options: start at sign-in, start minimized, start capture
automatically, register virtual cameras automatically, restart after a crash
(Windows restart manager), startup wait for slow USB enumeration. Only one
instance runs; a second launch activates the first. `WebcamSwitcher.exe --exit`
stops a running instance.

### Logs

`%LOCALAPPDATA%\WebcamSwitcher\logs\WebcamSwitcher.log` — `date time [CAM-01]
message`, rotated at 5 MB × 10 files (configurable). Covers start/exit, device
detection and ID matching, connect/disconnect/reconnect, input formats, virtual
camera registration, stalls, drops, errors, config load/save, role changes, and
a periodic `PERF` line (CPU, memory, per-role fps/drops).

## Building

Prerequisites: Visual Studio 2022 (C++ toolset), .NET 9 SDK, and Inno Setup 6
for the installer.

```
powershell -File build.ps1
```

Produces `artifacts/app` (self-contained app + `WebcamSwitcher.Source.dll`)
and tries to register the DLL (needs elevation; the script now verifies the
registration in HKLM instead of trusting regsvr32's exit code). Installer:

```
ISCC.exe /DMyAppVersion=2.0 installer\WebcamSwitcher.iss
```

## Tests

| Harness | What it checks | Needs |
|---|---|---|
| `tools/RoleTest` | role naming, duplicate rules, schema 1 migration, atomic save / backup / corrupt recovery / unknown fields, role-keyed virtual camera service (reorder, rename, remove, duplicates), letterbox/crop geometry, diagnostic frame, latency stamp | nothing (CI) |
| `tools/VcamTest` | per-slot virtual camera contract of the controller with a fake native layer | nothing |
| `tools/MfProbe --delaytest/--pubtest/--copytest/--visibilitytest/--abuse` | delay buffer, publisher, NV12 copy, preview flags, pipe abuse | nothing |
| `tools/CaptureTest [--latency] [--roles N]` | real cameras → real virtual cameras: 1280×720, own-pipe routing, fps, added latency | registered DLL, cameras |
| `tools/ChromiumCheck/run-chromium-check.ps1` | `enumerateDevices()` lists every CAM-xx separately and each opens at 1280×720 | Edge or Chrome |
| `tools/soak-monitor.ps1` | CPU, GPU, memory of app and Frame Server over a long run | — |

`tools/UiTest` (FlaUI) still targets the old Settings camera dropdowns, which
moved to the registration window; it is not updated.

## Project layout

```
src/WebcamSwitcher.Source/   C++ COM media source (the virtual camera; routes by friendly name via vcams.ini)
src/WebcamSwitcher.Core/     capture, roles, config store, logging, frame publisher, vcam service
src/WebcamSwitcher.App/      WPF app (tray, main window, registration, settings, hotkeys)
src/WebcamSwitcher.Shared/   frame transport protocol (C header)
installer/                   Inno Setup script
tools/                       test harnesses
docs/                        operations guide (ja), change list, test report, sample config
```

## License

MIT — see [LICENSE](LICENSE). Portions derived from MIT-licensed reference
code; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
