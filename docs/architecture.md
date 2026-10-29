# Architecture

## Camera Roles (current design)

The sections further down describe the original two-camera design; the
transport, the native media source and the Frame Server facts are unchanged.
What changed is how cameras map to virtual cameras:

```
DevicePresenceMonitor (DeviceWatcher, by device ID)
        │ present / removed
        ▼
CameraEngine ──▶ RoleChannel "CAM-01" ──▶ CameraSource (exact device ID only)
             │        │  supervisor tick 500 ms: open / reconnect / classify failures
             │        │  FrameDelayBuffer ─▶ FramePublisher  \\.\pipe\WebcamSwitcher.Frames.v1.r.<VirtualCameraId>
             │        │  DiagnosticFrame when no live picture (MISSING, NO SIGNAL, ERROR …)
             │        └─▶ (active role) switcher pipe \\.\pipe\WebcamSwitcher.Frames.v1
             └─▶ RoleChannel "CAM-02" ...

VirtualCameraService: key = VirtualCameraId; friendly name = VirtualCameraName;
vcams.ini "<name>\t<pipe>" lets the native source (which only learns its
friendly name from the Frame Server) find its role pipe.
```

- Identity is the role's persistent `VirtualCameraId`, never a list index.
  `VirtualCameraService.Prepare` stops only slots whose role vanished or was
  renamed; `CameraEngine.ApplyRolesAsync` rebuilds only roles whose capture
  settings changed.
- Windows derives the virtual camera's device instance
  (`SWD#VCAMDEVAPI#<hash>`) from the registered name: re-registering the same
  name yields the same device ID (observed across runs), so consumers keep it.
- Failures are classified at the source (`CaptureFailure`): device not found,
  open failed, in use, format failed, no frames, USB bandwidth (including
  `MediaCapture.Failed` 0x800718CF while streaming).

## Overview (original)

WebcamSwitcher is a Windows 11 desktop application that composites two physical
USB webcams into a single user-mode **Media Foundation virtual camera**, with
instant switching via global hotkeys.

```
 Physical USB cams                    WebcamSwitcher.App  (WPF, user session, standard user)
 ┌──────────┐  ┌──────────┐        ┌─────────────────────────────────────────────────────┐
 │ Cam A    │  │ Cam B    │        │ CameraPipeline A ─┐                                  │
 └────┬─────┘  └────┬─────┘        │ CameraPipeline B ─┼─▶ ActiveCamSelector              │
      │  MediaCapture / MF reader (exclusive control)            │                       │
      └─────────┬───┘             │              VideoProcessor MFT ─▶ 1080p30 NV12     │
                │                 │                              │                       │
                │                 │                     FramePublisher ──▶ Named-pipe server
                │                 │  Tray ▸ Hotkeys ▸ Settings ▸ Live previews           │
                │                 └─────────────────────────────────────────────────────┘
                │                                        │  \\.\pipe\WebcamSwitcher.Frames.v1  (cross-session)
                │                 ┌──────────────────────┼──────────────────────────────────┐
                │                 │ Frame Server (svchost, session 0, LocalService)      │
                │                 │   WebcamSwitcher.Source.dll  (C++ COM IMFMediaSource)│
                │                 │     pipe client → emits NV12 1080p30 samples         │
                │                 └──────────────────────┼──────────────────────────────────┘
                │                                        │  MF / DirectShow camera
                │                                        ▼
                │                  Consumer apps (Zoom/Teams/OBS)  +  NVIDIA Broadcast
                │                     (Broadcast consumes our VCam as a camera input)
                └── while capturing, a physical cam is held exclusively by WebcamSwitcher
```

## Components

- **`WebcamSwitcher.Source`** — native C++ COM DLL implementing the Media
  Foundation virtual-camera media source (`IMFMediaSource` / `IMFMediaStream`,
  activated via `IMFActivate`). It advertises a fixed output format (default
  NV12 1920x1080@30) and emits samples from frames it reads over a named pipe.
  It is registered in `HKLM` via `regsvr32` (one-time, admin).

- **`WebcamSwitcher.App`** — C# (.NET 9) WPF app. Captures both physical cameras
  under exclusive control, normalizes the active camera via the Video
  Processor MFT, publishes frames over the named pipe, and hosts the tray icon,
  global hotkeys, settings, and live previews. It also calls
  `MFCreateVirtualCamera` with **Session** lifetime so the virtual camera exists
  only while the app runs.

- **`WebcamSwitcher.Shared`** — the frame transport protocol (see
  `WebcamSwitcherProtocol.h`), shared by both sides.

## Frame transport

A single named pipe `\\.\pipe\WebcamSwitcher.Frames.v1`:

- The **app is the pipe server**. It creates the pipe with a security descriptor
  granting `SYSTEM` and `LOCAL SERVICE` read/write (the Frame Server runs as
  LocalService in session 0), plus the current user.
- The **source is the pipe client**. On connect it receives a `WsConfigMsg`
  describing the fixed output format, then a stream of `WsFrameInfo + pixels`
  frame messages.
- The source keeps the most recent frame in memory and emits samples on a
  30 fps scheduler, repeating the last frame if the pipe stalls (so the virtual
  camera never freezes) and emitting a black frame when no frame has arrived yet.

A named pipe (rather than `Global\` shared memory) is used because it requires
no special privileges — a standard user process can create and serve it, while
`Global\` object creation needs `SeCreateGlobalPrivilege`.

## Video delay (A/V sync)

`CameraEngine` runs every arriving frame through the slot's `FrameDelayBuffer`
before it is published — ahead of both the slot's passthrough pipe and the
switcher pipe — so every virtual camera this app exposes can present the same,
operator-controlled latency and switching sources in OBS never shifts A/V sync.

The ring holds `round((DelayMs + CommonDelayMs) × fps / 1000)` NV12 copies (0-60
frames, 0 by default). Depth 0 is a complete bypass: no allocation, no copy, so
an unconfigured install behaves exactly as before. The stage is **content only**:
it changes which frame is published, never the sample timing of the native
source, whose `MediaStream` keeps its own monotonic `SetSampleTime` clock — the
delay cannot touch the virtual camera's stream contract. While a ring is filling
(fewer frames than its depth, including right after a depth increase) it reports
"publish the newest frame", so it can never stall a virtual camera; once full it
is strict FIFO with one buffer in and one out, no per-frame allocation. The
switch from "newest" to "depth frames ago" is a single rewind of up to the delay
length when a ring fills — a one-off on enable, not a running cost.

## Device capture mode

Each physical camera is opened with WinRT `MediaCapture` in **exclusive control**
(`MediaCaptureSharingMode.ExclusiveControl`, see `CameraSource.StartAsync`); the
Media Foundation source-reader path used for MJPG sets no share-mode attribute
either. The app needs control of the device — it selects the capture format,
pins exposure and reads frames from the frame reader — so a camera that
WebcamSwitcher is capturing cannot be opened directly by another application at
the same time. The supported way to hand a physical camera to Zoom, Teams or OBS
is its passthrough virtual camera (or the switcher virtual camera for the active
one).

The app therefore consumes the camera's native mode and normalizes it itself
(Video Processor MFT → NV12 1080p30).

## Key findings from the feasibility spike (M0)

1. `MFCreateVirtualCamera` works on the target machine (Windows 11 Pro 25H2,
   build 26200). A registered media source appears in `DeviceInformation` /
   VideoCapture enumeration as `<name> (Windows Virtual Camera)`.
2. The virtual camera enumerates only from the **interactive session** (session
   1), not from a non-interactive (session 0) context — the app already runs in
   the interactive session, so this is not a limitation.
3. Two cameras (Logitech C920 640x480 YUY2@30 and DJI OsmoPocket3 1280x720
   NV12@30) open simultaneously in `SharedReadOnly` mode.
4. Non-blocking verified: an exclusive/controlling `MediaCapture` instance opens
   the same camera while a shared instance holds it.
5. `MediaCapture.GetPreviewFrameAsync` is disallowed in `SharedReadOnly`; frame
   capture must use `MediaFrameReader` or the MF source reader.
6. The media source DLL must be registered in `HKLM` and live in a path readable
   by `LOCAL SERVICE` / `SYSTEM` (e.g. `C:\Program Files\...` or a non-profile
   path such as `C:\src\...`).

## Registration & install

- `regsvr32 WebcamSwitcher.Source.dll` registers the COM media source in `HKLM`
  (admin required, one-time).
- `WebcamSwitcher.App` calls `MFCreateVirtualCamera(SoftwareCameraSource,
  Session, CurrentUser, "WebcamSwitcher Virtual Camera", "{CLSID}", ...)` then
  `Start`, at runtime.
- The Inno Setup installer performs the `regsvr32` (install) and `regsvr32 /u`
  (uninstall) steps elevated.

## Hotkeys

- Implemented via Win32 `RegisterHotKey` on a hidden `HwndSource` window in the
  app.
- Defaults: `Ctrl+Alt+1`..`N` for direct selection, `Ctrl+Alt+N` to rotate.
- Configurable in settings; conflicts are detected and reported.

## See also

- `WebcamSwitcherProtocol.h` — wire protocol.
- MFCreateVirtualCamera: https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera
- Reference implementation (MIT): https://github.com/smourier/VCamSample
