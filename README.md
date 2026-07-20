# Tello4Quest2

Fly and watch the live video feed of a **DJI/Ryze Tello** drone (the consumer model, SDK 1.3) directly inside a **Meta Quest 2** headset, in passthrough, using a Bluetooth gamepad. The video screen, telemetry banners, and pre-flight menu float in front of you, locked in place relative to where you were looking when the app launched.

This repo contains every Unity script used, plus the YUV→RGB conversion shader and the two materials it depends on.

## What it does

- Automatic connection to the Tello over WiFi direct, no manual setup, with automatic reconnection if the link drops.
- Live H.264 video decoded and displayed on a floating screen, world-locked (it does **not** follow your head).
- Bluetooth gamepad piloting: sticks fly the drone, shoulder buttons/triggers adjust speed and stick sensitivity live, dpad triggers flips, one button for takeoff/land, one for emergency stop.
- Photo capture (PNG) and raw video recording (.h264) to the headset's storage.
- Live telemetry banners: battery, altitude, speed, flight time, video signal quality.
- Safety features: automatic landing on critical battery, a software altitude ceiling, crash detection (acceleration spike), and a rough dead-reckoning estimate of the way back to the takeoff point (no GPS on the consumer Tello).
- A pre-flight gate screen that waits for gamepad + Tello WiFi + video feed to all be ready before revealing the flight UI.

## Sources

- **Tello protocol (consumer SDK 1.3)**: the official Ryze/DJI SDK documentation, plus a few undocumented behaviors confirmed through community reverse-engineering — most notably how video access units are framed by UDP packet size, and the fact that the drone only emits its H.264 SPS/PPS/IDR parameter-set burst once, right when the encoder starts (more on that below).
- **[PopH264](https://github.com/SoylentGraham/PopH264)** for hardware H.264 decoding on Android/Quest (a wrapper around MediaCodec).
- **Meta XR SDK / OVRPlugin** for passthrough and headset tracking.
- **Unity Input System** for gamepad handling.
- **Claude working with me

## Architecture

| Script | Role |
|---|---|
| `TelloConnection.cs` | Singleton managing the UDP connection (command port 8889, state port 8890), the sequential one-shot command queue, continuous `rc` sending, safety thresholds, CSV flight logging, and automatic reconnection. |
| `TelloVideoReceiver.cs` | Raw UDP reception of the video stream (port 11111) and reassembly of H.264 (Annex-B) access units on a dedicated background thread. |
| `TelloVideoDecoder.cs` | Hardware decoding via PopH264; handles both possible output formats (direct RGBA/BGRA, or 2-plane YUV NV12, depending on the device). |
| `TelloVideoDisplay.cs` | Displays the feed on a world-locked quad, handles zoom, and provides photo capture. |
| `TelloYuvNV12ToRGB.shader` + `TelloVideoYUV.mat` / `TelloVideoRGBA.mat` | GPU-side YUV→RGB conversion (BT.601), and the direct-RGBA material. |
| `TelloVideoRecorder.cs` | Records the raw H.264 stream to disk with zero re-encoding. |
| `TelloGamepadController.cs` | Gamepad input (Unity Input System), command mapping, stick calibration, haptic feedback. |
| `TelloInitGate.cs` | The pre-flight gate (three status checks), then an explicit hand-off to the flight display. |
| `TelloStatusPanel.cs` / `TelloOptionsPanel.cs` | Telemetry banners above/below the video screen. |
| `TelloUiKit.cs` | Shared UI utilities (procedural rounded-sprite generation, card-shell building, fixed camera-relative placement math) - keeps that logic out of three separate copies. |

## Building the project

### Prerequisites
- **Unity 6** (tested on 6000.5.2f1) with the **Android** build module installed.
- **Universal Render Pipeline (URP)** — the YUV shader targets `RenderPipeline=UniversalPipeline`.
- **Unity Input System** package (`com.unity.inputsystem`) — under *Project Settings > Player > Active Input Handling*, pick *Input System Package* (or *Both*).
- **Meta XR SDK / Meta XR Core** (for `OVRPassthroughLayer`, headset tracking, etc.).
- **TextMeshPro** (import the essential resources if prompted on first use).
- **[PopH264](https://github.com/SoylentGraham/PopH264)** — grab the Unity package and import it; the native Android plugin needs to be present under `Plugins/Android`.

### Importing the scripts
1. Copy all `.cs` files from this repo into your project's `Assets/Scripts/` folder (or wherever you keep scripts).
2. Copy `TelloYuvNV12ToRGB.shader` into the project, then create two materials:
   - `TelloVideoRGBA`: shader *Universal Render Pipeline/Unlit*, Render Face = Both.
   - `TelloVideoYUV`: shader `TelloQuest/YuvNV12ToRGB` (included here, double-sided by the shader itself, no extra culling settings needed).
   (The two `.mat` files in this repo can also be imported directly instead.)

### Building the scene
GameObjects to create, with their components:

| GameObject | Component(s) | Notes |
|---|---|---|
| `TelloConnection` | `TelloConnection` | Default IP `192.168.10.1` (the Tello's WiFi-direct address). |
| `TelloGamepadController` | `TelloGamepadController` | Reference `TelloConnection`, `TelloVideoDisplay` (for photo capture), and `TelloVideoRecorder`. |
| `TelloVideo` | `TelloVideoReceiver` + `TelloVideoDecoder` + `TelloVideoRecorder` | Must stay **active** from launch — the pre-flight gate's video check depends on it. |
| `TelloVideoScreen` | `TelloVideoDisplay` | Assign both materials, check **Positioned Externally**, and start this GameObject **inactive**. |
| `TelloStatusPanel` | `TelloStatusPanel` | Check **Positioned Externally**. |
| `TelloOptionsPanel` | `TelloOptionsPanel` | Check **Positioned Externally**. |
| `TelloInitGate` | `TelloInitGate` | Reference everything above (see the "What To Reveal Once Ready" fields). |
| `PassthroughLayer` | `OVRPassthroughLayer` | Placement = Underlay. |

Every cross-reference (Tello, Gamepad Controller, Video Decoder/Display, Video Screen...) is wired by drag-and-drop in the Inspector — no `Shader.Find` or `FindObjectOfType` at runtime.

### Meta Quest build (Unity 6 Build Profiles)
1. *Build Profiles* window → select the **Meta Quest** profile in the Platforms list, then set it **Active**.
2. Platform Settings (Meta Quest):
   - Texture Compression: **ASTC**
   - Debug Symbols: **Debugging (Full)**
   - Compression Method: **LZ4**
   - Export Project / Symlink Sources / Development Build: off (unless you specifically need them)
3. Under *Player Settings* (still scoped to the Meta Quest profile): Scripting Backend **IL2CPP**, Minimum API Level **Android 12L (API level 32)**, Target API Level **Android 14.0 (API level 34)**.
4. Enable XR (Meta XR / OpenXR) under *XR Plug-in Management*.
5. Build & Run from the Meta Quest profile, or export the APK for install via SideQuest/ADB.

## Known limitations / ideas for improvement

- `sdk?` returns `unknown command: sdk?` on some Tello firmware versions — cosmetic only, doesn't affect flight or video.
- No disk-space management for recordings/photos.
- Reconnection after a signal loss is automatic but can take a few seconds depending on how stable the Tello's WiFi is.
- Feedback and PRs are very welcome, especially around the initial connection's reliability (the WiFi association delay before the first successful connect still varies from test to test) and gamepad ergonomics/haptics.

## License and contributions

This is a community project, open to contributions. Use it, modify it, improve it for your own Tello — the goal is for every Tello + Quest 2 owner to benefit and help push this further. If you fork it, a mention back to this repo is appreciated.

Found a bug, have an idea, or got it working better on your setup? Open an issue or a PR — feedback is exactly what this needs to get better.
