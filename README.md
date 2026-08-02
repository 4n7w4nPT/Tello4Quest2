# Tello4Quest2

Fly a **DJI/Ryze Tello** (consumer model, SDK 1.3) from inside a **Meta Quest 2**, in passthrough, with a Bluetooth gamepad. The video screen, telemetry banners and menu float in front of you, world-locked to where you were looking at launch.

This repo contains every Unity script, the YUV→RGB shader, and the two materials it needs.

**v0.6 — main-thread hygiene + the D-pad cross.** Removed three stalls that were stealing time from the video pipeline: an unbuffered flight log writing ten times a second, PNG encoding blocking the main thread (a photo used to drop UDP packets and visibly degrade the live feed), and an unbounded decoder drain. Added a D-pad cross on the menu opening a read-only Controls legend, driven by a single shared mapping table.

<details>
<summary>Earlier versions</summary>

- **v0.5** — the video pipeline release: fixed chroma range and double gamma encoding, added the missing stereo instancing, full decoder draining, stall recovery, two-page Settings, separate Takeoff/Land buttons, quality presets. See [Video pipeline](#video-pipeline).
- **v0.4** — landscape Menu screen, cockpit side bands (graphs left, mini-map + activity log right), manual white balance / brightness / contrast.
- **v0.3** — real `.mp4` recording via MediaMuxer, flight-path mini-map, automatic night mode and sharpening.
- **v0.2** — Menu/Piloting/Settings screens, activity log, aviation-instrument visual redesign.
- **v0.1** — auto WiFi connection, live H.264 video, gamepad piloting, photo/video capture, telemetry, first safety features.
</details>

## What it does

- **Three-screen flow**: Menu (pre-flight checklist + two button crosses), Piloting (video + telemetry), and Settings (two pages).
- Automatic connection over WiFi direct, with automatic reconnection.
- Live H.264 video on a world-locked screen. Full image chain (bicubic upscale, edge-preserving denoise, sharpening, night mode, colour), tunable **in-headset, mid-flight**.
- Gamepad piloting with live speed/sensitivity, flips, separate Takeoff/Land, always-live emergency stop. Prompts adapt to PlayStation/Xbox, with optional icon glyphs.
- Photo (PNG) and real **.mp4** recording via MediaMuxer — zero re-encoding, playable straight from the headset's Files app.
- Telemetry: banners above and below the screen, plus two cockpit side bands angled toward the pilot — four live graphs on the left, north-up mini-map and a first-person activity log on the right.
- Safety: auto-land on critical battery, altitude ceiling, crash detection, dead-reckoning route home, one-shot alerts with hysteresis.
- ~47 persisted settings across two gamepad-navigable pages.
- A five-check pre-flight gate that stays live in the background while flying.

## Video pipeline

The heart of v0.5, and the part most worth understanding if the image ever looks wrong.

**Reception.** Raw H.264 over UDP, no RTP, no container. Frame boundaries come from packet size: every datagram in an access unit is 1460 bytes except the last. A **4 MB receive buffer** (the OS default overflows on any main-thread hitch past ~30 ms), and units with a bad start code are dropped rather than decoded.

**Decoding.** PopH264 drives MediaCodec. Waits for an SPS **then an IDR** before feeding, drains the full output queue every frame (MediaCodec delivers in bursts — one at a time made latency climb all flight), and a watchdog recreates the decoder if units arrive with nothing coming out. The SPS is parsed live for resolution, crop, and colour signalling.

**Colour** — the two bugs that made it look wrong:
- **Chroma range**: luma was range-expanded, chroma wasn't, and full-range BT.601 coefficients were applied anyway. Mixing conventions undersaturated everything by 255/224 ≈ 12%.
- **Gamma**: YUV conversion outputs gamma-encoded RGB. Returned as-is in a **Linear** project, the hardware sRGB-encodes it twice — lifted blacks, milky contrast. The manual brightness/contrast sliders were, in hindsight, compensating for this.

Chroma siting is corrected too (in 4:2:0 a chroma texel sits half a luma texel off, visible as fringing on vertical edges). Note the stream carries no VUI, so limited-range BT.601 is an assumption; a BT.709 switch exists for comparison.

**Presentation.** Catmull-Rom bicubic, then **edge-preserving smoothing** weighting each neighbour by luma proximity — flat compression noise smooths, edges stay. Denoise first, sharpen second. The shader also has the **stereo instancing** it never had: without it, Single Pass Instanced renders the left eye into both.

## Controls

Prompts adapt to whichever gamepad is connected. Positions are Unity's North/South/East/West.

**Menu**

| Button | Action |
|---|---|
| South | Enter Piloting (once all five checks are green) |
| North | Video settings |
| West | Flight settings |
| East | Quit |
| D-pad Left | Controls legend (read-only) |

**Piloting**

| Input | Action |
|---|---|
| Left stick | Yaw + altitude |
| Right stick | Roll + pitch |
| South | Take off (ignored if already flying) |
| East | Land (ignored if already landed) |
| West | Photo |
| North | Start/stop recording |
| D-pad | Flips — one at a time, a new one is ignored until the previous is confirmed |
| L1 / L2 | Speed level −/+ |
| R1 / R2 | Sensitivity −/+ |
| Share/Select | Emergency stop — live on every screen |
| Options/Start | Back to Menu (blocked while flying) |

**Settings** — left stick selects a row, right stick adjusts it. South saves, North resets the page, East exits without saving. Both pages keep their own row and scroll position.

- **Video** — quality preset (Custom / Sharp / Balanced / Smooth / Low light), screen placement, colour, sharpness & noise, night mode, decoding. SDK-2.0-only rows are locked automatically on a consumer Tello.
- **Flight** — battery, temperature, proximity, altitude ceiling, crash detection, navigation & logging, gamepad feel, cockpit layout, graphs.

## Fonts

Three optional Google Fonts — without them everything falls back to TextMeshPro's default.

| Role | Font |
|---|---|
| Titles | Big Shoulders Stencil Bold |
| Body | IBM Plex Sans Regular |
| Mono | IBM Plex Mono Medium |

Drop the `.ttf` in `Assets/Fonts/`, right-click → *Create → TextMeshPro → Font Asset* (**SDF**), assign to `TelloInitGate`, `TelloSettingsScreen` and `TelloControlsScreen`.

**Optional button glyphs**: assign an icon font to `TelloInitGate`'s **Icon Font** for real button icons instead of text — tested with [Stephan Dube's free PS4/Xbox font](https://stephandube.com) (free for personal and commercial use, redistribution unmodified only), whose 8 glyph characters are already the defaults.

## Storage & permissions

Photos, videos and flight logs go through **MediaStore** into shared collections (`Pictures/`, `Movies/`, `Download/`, under `Tello4Quest2`) — no permission needed, visible from the Files app, MQDH or USB.

Reading Bluetooth/WiFi state for two pre-flight checks **does** need two manifest permissions Unity doesn't add — see `AndroidManifest.xml` in this repo, or those checks stay permanently red.

## Architecture

| Script | Role |
|---|---|
| `TelloConnection.cs` | UDP connection (8889/8890), command queue, continuous `rc`, safety thresholds and alerts, buffered CSV flight log, reconnection. |
| `TelloVideoReceiver.cs` | UDP reception (11111) and Annex-B reassembly on a background thread. 4 MB buffer, start-code validation. |
| `TelloVideoDecoder.cs` | PopH264 decoding, SPS+IDR gating, full queue drain with a time budget, stall watchdog, live SPS parsing. |
| `TelloVideoDisplay.cs` | The world-locked quad, zoom, opacity, every image control. Works on **runtime copies** of its materials so project assets are never mutated. |
| `TelloYuvNV12ToRGB.shader` + 2 `.mat` | NV12→RGB with correct range expansion, BT.601/709, linear output, chroma siting, bicubic, smoothing, sharpening, night mode, stereo instancing. |
| `TelloVideoRecorder.cs` | `.mp4` via MediaMuxer, keyframe start, SPS-derived dimensions, cached JNI objects. |
| `TelloGamepadController.cs` | Input, command mapping, calibration, haptics, threaded PNG encoding, flip lock. |
| `TelloInitGate.cs` | Menu/Piloting/Settings state machine, pre-flight checks, both button crosses. |
| `TelloControlMap.cs` | **Single source of truth** for the in-flight mapping. The Controls screen reads it, so the legend can't drift from reality. |
| `TelloControlsScreen.cs` | Read-only controls legend, opened from the D-pad cross. |
| `TelloSettingsScreen.cs` | Both Settings pages — graduated sliders, toggle switches, presets, conditional locking, persistence. |
| `TelloStatusPanel.cs` / `TelloOptionsPanel.cs` | Telemetry banners, refreshed on change rather than every frame. |
| `TelloSpatialPanel.cs` | Left band: four graphs with labelled axes, bounded history, redrawn only on change. |
| `TelloActionLogPanel.cs` | Right band: mini-map + activity log. |
| `TelloUiKit.cs` | Shared UI utilities, gamepad brand detection, inner-edge pinning math, diagnostics master switch. |
| `TelloWifiConnector.java` | WiFi auto-connect helper. Built but **not wired to any input** — still needed to compile. |

## Building the project

### Prerequisites
Unity 6 (tested 6000.5.2f1) with Android module · URP · Input System · Meta XR SDK · TextMeshPro · [PopH264](https://github.com/SoylentGraham/PopH264) under `Plugins/Android`.

### Importing
1. Copy the `.cs` files into `Assets/Scripts/`, the shader into `Assets/Materials/`.
2. Create two materials **from scratch** (don't shader-swap an existing Lit material — it carries PBR baggage, and playmode can bake stale values into the asset):
   - `TelloVideoRGBA`: *Universal Render Pipeline/**Unlit***, Transparent, Render Face Both.
   - `TelloVideoYUV`: `TelloQuest/YuvNV12ToRGB` — nothing else to set.
3. Copy `TelloWifiConnector.java` to `Assets/Plugins/Android/src/main/java/com/tello4quest2/`.
4. Add the two WiFi permissions to your manifest (`AndroidManifest.xml` in this repo → `Assets/Plugins/Android/`).

### Scene setup
Everything marked **starts inactive** must actually be unchecked in the Hierarchy — `TelloInitGate` activates and positions each one at the right moment. If they start active, their self-positioning runs too early.

| GameObject | Component(s) | Notes |
|---|---|---|
| `TelloConnection` | `TelloConnection` | Default IP `192.168.10.1`. |
| `TelloGamepadController` | `TelloGamepadController` | Reference connection, display, recorder, and **App State Gate** = `TelloInitGate`. |
| `TelloVideo` | Receiver + Decoder + Recorder | Must stay **active** from launch — the pre-flight video check needs it. |
| `TelloVideoScreen` | `TelloVideoDisplay` | Assign both materials and **Vr Camera**. Starts inactive. |
| `TelloStatusPanel` / `TelloOptionsPanel` | matching component | Start inactive. |
| `TelloSpatialPanel` / `TelloActionLogPanel` | matching component | Reference connection + display. Start inactive. |
| `TelloSettingsScreen` | `TelloSettingsScreen` | Reference `TelloInitGate` and every panel. Decoder/ActionLog optional. Starts inactive. |
| `TelloControlsScreen` | `TelloControlsScreen` | Reference `TelloInitGate` + fonts. Starts inactive. |
| `TelloInitGate` | `TelloInitGate` | References everything above, plus **Vr Camera**, plus both screens. |
| `PassthroughLayer` | `OVRPassthroughLayer` | Placement = Underlay. |

### Rendering settings that matter

| Setting | Where | Value | Why |
|---|---|---|---|
| Color Space | Player | **Linear** | The shader's gamma handling assumes it. On Gamma, remove the `SRGBToLinear` call. |
| Render Mode | XR Plug-in Management > OpenXR (Android) | **Single Pass Instanced** | The shader supports it properly; Multi-pass roughly doubles GPU cost. |
| Render Scale | URP Asset | **~1.1** | 1.6 on a Quest 2 is 2.56× the pixels. A stable 1.1 looks sharper than an unstable 1.6. |
| Cast Shadows | URP Asset | **off** | Nothing casts shadows; it was a wasted pass. |
| Sharpen Type | OVRManager | **Quality** | Compositor-side sharpening at native panel resolution — better than any pre-resample sharpen, and free. |

### ⚠️ Tracking Origin — can stop the app launching entirely
With **Floor Level** + Passthrough, Quest treats the app as Roomscale and requires a configured Guardian. Without one it fails to launch with **no error shown** — it just returns to the launcher. This project doesn't need floor calibration, so set `Camera Offset` → **Device** and `OVR Manager` → **Eye Level**, together. Don't let Meta's Project Setup Tool "fix" this back.

### Quest build
Build Profiles → **Meta Quest** active. ASTC, LZ4, IL2CPP, Min API 32, Target API 34, XR enabled. App icons need **both** Background and Foreground layers per size or the icon silently won't show; if it doesn't update after a rebuild, uninstall first.

## Known limitations

- **Gamepad not detected if it pairs after launch** — the OS-level Bluetooth connection succeeds but the controller sometimes never appears in Unity's device list at all. Likely needs a native Android bridge. Workaround: turn the gamepad on *before* launching.
- **Macroblock artefacts from packet loss** can be reduced but not eliminated — the Tello's WiFi is 2.4 GHz only, and no shader reconstructs data that never arrived.
- **`setbitrate` / `setresolution` / `setfps` are SDK 2.0 only** — detected automatically from the `sdk?` reply, and those rows lock themselves on a consumer Tello. On an EDU/Talent they're the biggest quality win available, being the only one acting at the source.
- **WiFi auto-connect is built but not wired** — it proved unreliable, and its old button now opens Flight settings.
- **In-headset gallery doesn't work** — two approaches tried, both fail on Quest's OS. Files app, MQDH and USB all work fine.
- **BT.601 vs BT.709 is a judgement call** — the SPS carries no VUI, so nothing declares which matrix was used. A switch lets you compare on foliage and skin tones.
- Wind-drift detection is indirect (commanded input vs. actual velocity) — thresholds may need tuning against real flights.
- No disk-space management for recordings and photos.

## License and contributions

Community project, open to contributions. Use it, modify it, improve it for your own Tello. If you fork it, a mention back is appreciated.

Found a bug, have an idea, or got it working better on your setup? Open an issue or a PR.
