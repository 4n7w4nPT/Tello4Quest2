# Tello4Quest2

Fly and watch the live video feed of a **DJI/Ryze Tello** drone (the consumer model, SDK 1.3) directly inside a **Meta Quest 2** headset, in passthrough, using a Bluetooth gamepad. The video screen, telemetry banners, and menu float in front of you, locked in place relative to where you were looking when the app launched.

This repo contains every Unity script used, plus the YUV→RGB conversion shader and the two materials it depends on.

**v0.5 — the video pipeline release.** The colour path was rebuilt from the ground up after tracing a washed-out, undersaturated image back to two concrete bugs: chroma was never range-expanded (12% of saturation silently lost), and the shader output gamma-encoded RGB into a Linear-colour-space project, so the hardware sRGB-encoded it a second time. The shader also had no stereo instancing boilerplate at all, meaning both eyes were rendering the left eye's projection. Alongside that: the decoder now drains its full output queue (latency was climbing monotonically through a flight), waits for a keyframe before feeding, and restarts itself if it stalls; the UI panels no longer rebuild their canvases every frame (framerate used to degrade steadily as a flight went on, and dynamic resolution followed it down); and Settings became two gamepad-navigable pages — **Video** and **Flight** — with proper graduated sliders and toggle switches instead of the old fill-bar. See [Video pipeline](#video-pipeline) for the technical detail.

<details>
<summary>v0.4 — landscape Menu screen, cockpit side bands, manual image controls</summary>

A landscape Menu screen with a five-item pre-flight checklist; the left/right telemetry bands reworked into a cockpit console layout (live time-series graphs on the left, mini-map + activity log on the right, both angled toward the pilot); and manual white balance / brightness / contrast controls alongside the automatic night mode and sharpening from v0.3.
</details>

<details>
<summary>v0.3 — real .mp4 recording, mini-map, automatic video enhancement</summary>

Real `.mp4` recording via Android's MediaMuxer (no more raw `.h264` needing ffmpeg to become watchable), a flight-path mini-map, and automatic video enhancement (night mode + sharpening).
</details>

<details>
<summary>v0.2 — Menu/Piloting/Settings screens, activity log, visual redesign</summary>

A full Menu/Piloting/Settings screen system, a scrollable settings screen with ~30 adjustable parameters, a drone "activity log" narrating what's happening in short first-person lines, and a visual redesign (aviation-instrument look, custom fonts).
</details>

<details>
<summary>v0.1 — first release</summary>

Automatic connection to the Tello over WiFi direct, live H.264 video on a world-locked floating screen, Bluetooth gamepad piloting (sticks, flips, takeoff/land, emergency stop), photo/video capture, live telemetry banners, and the first safety features: automatic landing on critical battery, a software altitude ceiling, crash detection, and a dead-reckoning estimate of the way back to the takeoff point.
</details>

## What it does

- **Three-screen flow**: a Menu screen (landscape, pre-flight checklist + button legend), the Piloting screen (video + telemetry, gamepad live), and two Settings pages — see [Controls](#controls).
- Automatic connection to the Tello over WiFi direct, no manual setup, with automatic reconnection if the link drops.
- Live H.264 video on a floating, world-locked screen (it does **not** follow your head). Distance, size, and vertical position are adjustable, alongside a full image chain: bicubic upscaling, edge-preserving noise reduction, sharpening, automatic night mode, and manual white balance / brightness / contrast. All of it tunable in-headset, mid-flight — see [Video pipeline](#video-pipeline).
- Bluetooth gamepad piloting: sticks fly the drone, shoulder buttons/triggers adjust speed and stick sensitivity live, dpad triggers flips (one at a time — a new flip request is ignored, not queued, until the previous is confirmed done), one button for takeoff/land, one for emergency stop (always live, on every screen).
- Button prompts are brand-aware: PlayStation and Xbox controllers get the correct button name automatically, with a generic positional fallback for anything unrecognized. Optional support for showing an actual button *icon* via an icon font (see [Fonts](#fonts)).
- Photo capture (PNG) and video recording — real **.mp4** files via Android's MediaMuxer, zero re-encoding, playable directly from the headset's Files app or Quest gallery. Recording waits for the next keyframe before writing its first sample, so the file never opens on a corrupt half-second. Saved to **shared** storage (`Pictures/Tello4Quest2`, `Movies/Tello4Quest2`), with flight logs (CSV) under `Download/Tello4Quest2`.
- Live telemetry: a top banner (gamepad/Tello/video/last-command status + temperature), a bottom banner (altitude, flight time, ground speed, estimated time remaining, batteries), and two side bands angled ~20° toward the pilot like a cockpit console — pivoted around their **inner edge**, so the edge nearest the video screen sits exactly in its plane and nothing disappears behind it:
  - **Left**: four live time-series graphs (video FPS, battery %, altitude, temperature) with labelled axes. The time axis starts at a 1-minute window and smoothly widens as a flight runs longer, so the whole flight stays visible rather than scrolling the start away.
  - **Right**: a north-up flight-path **mini-map** (75% of the band) with a persistent connected trail and a zoom that never shrinks mid-flight, plus an **activity log** below (25%) — a first-person transcript of what the drone is doing and noticing ("Alright, taking off." / "Getting warm up here, keep an eye on me.").
- Safety features: automatic landing on critical battery, a software altitude ceiling, crash detection, dead-reckoning estimate of the way back to the takeoff point (no GPS on the consumer Tello), and one-shot alerts (battery, temperature, fast descent, wind drift, degrading signal) — each fires once per episode rather than every telemetry tick, with hysteresis.
- Two Settings pages, ~47 parameters total, gamepad-navigable, with one-press reset-to-defaults and everything persisted across restarts.
- A pre-flight gate that waits for five checks (Bluetooth enabled, gamepad connected, Wi-Fi enabled, Tello Wi-Fi connected, video feed connected) before allowing takeoff, kept live in the background even while flying.

## Video pipeline

The heart of v0.5. Every stage below was either wrong or missing before.

**Reception.** The Tello sends raw H.264 over UDP with no RTP and no container. Frame boundaries are inferred from packet size — every datagram in an access unit is exactly 1460 bytes except the last. The socket now requests a **4 MB receive buffer**: the OS default (~200 KB) overflows on any main-thread hitch past ~30 ms at 3 Mbit/s, and every lost packet is a corrupted access unit. Reassembled units that don't begin with an Annex-B start code are dropped rather than fed to the decoder — a corrupt unit costs several frames to recover from, so discarding is cheaper than displaying.

**Decoding.** PopH264 drives Android's MediaCodec. The decoder waits for an SPS **and then an IDR** before feeding anything (feeding P-slices that reference frames never decoded is what produced the macroblock mush in the first second). Its output queue is drained fully every frame rather than one frame per `Update()` — MediaCodec delivers in bursts, and consuming one at a time meant latency climbed monotonically for the whole flight. A watchdog recreates the decoder if access units are still arriving but nothing has come out for a few seconds, which is what used to require restarting the app after a Wi-Fi dropout.

**Stream metadata.** The SPS is parsed live (exp-Golomb, emulation-prevention bytes stripped) for the real resolution, the frame crop rectangle, and — when a VUI is present — the colour range and matrix coefficients. Nothing about the stream's geometry or colourimetry is assumed any more, and the recorder's `.mp4` dimensions come from the same source instead of a hardcoded 960×720.

**Colour conversion.** The two bugs that made the image look wrong:

- **Chroma range.** Luma was expanded from limited range (16–235) but chroma was left untouched, while the shader used full-range BT.601 coefficients (1.402 / 0.344 / 0.714 / 1.772). Those two conventions don't mix: the result was every colour undersaturated by exactly 255/224 = 1.1384, about 12%. Chroma is now expanded explicitly.
- **Gamma.** The RGB coming out of a YUV conversion is gamma-encoded. Returning it directly from a fragment shader in a **Linear** colour-space project means the hardware sRGB-encodes it a second time — lifted blacks, milky contrast. It's now converted to linear before output. (The manual brightness/contrast sliders were, in hindsight, compensating for this.)

Chroma siting is also corrected: in 4:2:0, a chroma texel centre sits half a luma texel to the right of what it describes, which shows as a half-pixel colour fringe on contrasty vertical edges.

**Presentation.** Catmull-Rom bicubic upscaling (a 960×720 source blown up across a large virtual screen — the resampling filter matters, and unsharp-masking a bilinear upscale just amplifies its artefacts), then an **edge-preserving smoothing** pass that weights each neighbour by how close its luma is to the centre: flat areas holding compression noise and blocking get smoothed, edges stay intact. It reuses the same four taps the sharpen and night-mode paths already fetch, so it costs no extra texture reads. Order matters — denoise, *then* sharpen.

The shader also gained the **stereo instancing boilerplate** it never had. Without `UNITY_VERTEX_OUTPUT_STEREO` and friends, Single Pass Instanced (OpenXR's default) renders the left eye's projection into both eyes — perceived as blur, double vision, or a vague discomfort that's hard to name. Shader keywords now strip the sharpen/night-mode/smoothing taps entirely when those effects are at zero, and the material switches to opaque rendering at full opacity instead of paying for alpha blending permanently.

## Controls

Button prompts adapt automatically to whichever gamepad is connected. Positions are Unity's North/South/East/West, which map to the same physical location on every standard gamepad.

**Menu screen**

| Button | Action |
|---|---|
| South | Enter Piloting (only once all five pre-flight checks are green) |
| North | Open **Video settings** |
| West | Open **Flight settings** |
| East | Quit the app |

**Piloting screen**

| Input | Action |
|---|---|
| Left stick | Yaw + throttle/altitude |
| Right stick | Roll + pitch |
| South | Takeoff / Land (toggle) |
| West, East | Take a photo (both do the same thing right now — see Known limitations) |
| North | Start/stop video recording |
| D-pad | Flip forward/back/left/right — one at a time, a new flip is ignored (not queued) until the previous is confirmed |
| L1 / R1 | Speed level −/+ |
| L2 / R2 | Sensitivity level −/+ |
| Share/Select | Emergency stop — live on every screen, not just Piloting |
| Options/Start | Return to Menu (only if landed — blocked with a haptic pulse if still flying) |

**Settings screens** (both pages behave identically)

| Input | Action |
|---|---|
| Left stick (up/down) | Select a row |
| Right stick (left/right) | Adjust the selected row's value, or flip a switch |
| South | Save and exit |
| North | Reset every value on this page to its default (doesn't exit — review, then Save or Cancel) |
| East | Exit without saving |

Both pages live on the same component and are built at launch; each keeps its own selected row and scroll position.

- **Video settings** (North) — screen placement, colour (white balance, brightness, contrast, BT.709 override, sRGB sampling override, chroma siting), sharpness & noise (bicubic, smoothing, smoothing threshold, sharpening), night mode, and decoding (signal-meter nominal FPS, decoder restart timeout).
- **Flight settings** (West) — battery, temperature, proximity, altitude ceiling, crash detection, navigation & logging, gamepad feel, cockpit layout (panel gap, angle, depth pinning), and instrument graphs (window width, sample interval).

## Fonts

The aviation-instrument look uses three Google Fonts, all optional — without them everything falls back cleanly to TextMeshPro's default.

| Role | Font | Weight |
|---|---|---|
| Titles | Big Shoulders Stencil | Bold |
| Body/labels | IBM Plex Sans | Regular |
| Status/mono text | IBM Plex Mono | Medium |

Download the `.ttf`, drop it in `Assets/Fonts/`, right-click → *Create → TextMeshPro → Font Asset* (choose **SDF**), then assign the three Font Assets to the matching fields on `TelloInitGate` and `TelloSettingsScreen`.

### Optional: button icon glyphs

`TelloInitGate` can show an actual PlayStation/Xbox button *icon* instead of text, via an icon font where specific characters render as button glyphs (tested with [Stephan Dube's free PS4/Xbox icon font](https://stephandube.com)). Assign it to **Icon Font**; the 8 glyph characters already default to the correct values for that font (`D`/`B`/`C`/`A` for PlayStation, `d`/`b`/`c`/`a` for Xbox). Leave it unassigned to keep plain text prompts.

> That font's license (verbatim from stephandube.com): free to use and modify for personal and commercial projects; redistribution allowed only in its original, unmodified form; attribution appreciated but not required; provided as-is, no warranty.

## Storage & permissions

Photos, videos, and flight logs are written via Android's **MediaStore** API into the shared collections (`Pictures/`, `Movies/`, `Download/`), each under a `Tello4Quest2` subfolder — not the app's private storage. Inserting new items into MediaStore needs no special manifest permission under scoped storage, and everything the app produces is immediately visible from the headset's Files app, from MQDH on a connected PC, and over plain USB.

Reading the headset's Bluetooth/Wi-Fi enabled state (for two of the five pre-flight checks) **does** need two manifest permissions Unity doesn't add automatically — see the `AndroidManifest.xml` fragment in this repo. Without them, those two checks throw a `SecurityException` internally and stay permanently red regardless of the headset's actual state.

## Sources

- **Tello protocol (consumer SDK 1.3)**: the official Ryze/DJI SDK documentation, plus undocumented behaviours confirmed through community reverse-engineering and our own testing — how video access units are framed by UDP packet size, the fact that the drone only emits its SPS/PPS/IDR parameter-set burst once (when the encoder starts), and that a `flip` isn't acknowledged until the maneuver has physically finished (~3s later), not on receipt.
- **ITU-T H.264 / ISO 14496-10** for the SPS syntax (exp-Golomb parsing, frame cropping, VUI colour signalling) and **ITU-R BT.601 / BT.709** for the conversion matrices and the limited-range scaling that turned out to be the source of the washed-out image.
- **[PopH264](https://github.com/SoylentGraham/PopH264)** for hardware H.264 decoding on Android/Quest (a MediaCodec wrapper).
- **Android MediaMuxer** for real-time `.mp4` muxing — no re-encoding, the same access units repackaged as they're written.
- **Meta XR SDK / OVRPlugin** for passthrough and headset tracking, including the Tracking Origin / Guardian interaction documented under [Building the project](#building-the-project) — not something we'd have gotten right by guessing.
- **Unity Input System** for gamepad handling, including its Android quirks around controllers that pair *after* launch.
- **Android MediaStore** for shared-storage photo/video/log saving.
- Claude working with me.

## Architecture

| Script | Role |
|---|---|
| `TelloConnection.cs` | Singleton managing the UDP connection (command port 8889, state port 8890), the sequential command queue, continuous `rc` sending, safety thresholds and one-shot alerts, CSV flight logging, and automatic reconnection. |
| `TelloVideoReceiver.cs` | Raw UDP reception (port 11111) and reassembly of Annex-B access units on a dedicated background thread, with a 4 MB receive buffer, start-code validation, and a flush on headset resume. |
| `TelloVideoDecoder.cs` | Hardware decoding via PopH264. Handles both output formats (direct RGBA/BGRA, or 2-plane NV12). Gates on SPS + IDR, drains the full output queue each frame, restarts itself on stall, parses the SPS for real resolution / crop / colour signalling, and captures the live SPS/PPS bytes for the recorder. |
| `TelloVideoDisplay.cs` | Displays the feed on a world-locked quad; owns zoom, opacity, and every image control. Works on **runtime copies** of its two materials so the project assets are never mutated. |
| `TelloYuvNV12ToRGB.shader` + `TelloVideoYUV.mat` / `TelloVideoRGBA.mat` | GPU-side NV12→RGB conversion with correct range expansion, BT.601/709 selection, linear-space output, chroma siting, bicubic upscaling, edge-preserving smoothing, sharpening, night mode, and stereo instancing. |
| `TelloVideoRecorder.cs` | Records to real `.mp4` via MediaMuxer (zero re-encoding), starting on the first keyframe, sized from the parsed SPS, with its JNI objects cached across frames. |
| `TelloGamepadController.cs` | Gamepad input, command mapping, stick calibration, haptics, photo trigger, flip lock. |
| `TelloInitGate.cs` | Owns the Menu/Piloting/Settings state machine, the five-check pre-flight list, the X-cross button legend, and hand-off to the flight display and to either Settings page. |
| `TelloSettingsScreen.cs` | Both Settings pages (Video and Flight) on one component — graduated sliders, toggle switches, gamepad navigation, per-page reset-to-defaults, persistence. |
| `TelloStatusPanel.cs` / `TelloOptionsPanel.cs` | Telemetry banners above/below the video screen, refreshed on change rather than every frame. |
| `TelloSpatialPanel.cs` | Left band: four live time-series graphs with labelled axes, bounded sample history, redrawn only when something changed. |
| `TelloActionLogPanel.cs` | Right band: flight-path mini-map (top 75%) and first-person activity log (bottom 25%). |
| `TelloUiKit.cs` | Shared UI utilities — procedural sprite generation, card shells, fixed camera-relative placement, gamepad brand detection, and the inner-edge pinning math the two side panels use. |
| `TelloWifiConnector.java` | Java helper for connecting to the Tello's hotspot by SSID prefix. Built and functional but **not wired to any input** (see Known limitations); still required for the project to compile. |

## Building the project

### Prerequisites
- **Unity 6** (tested on 6000.5.2f1) with the **Android** build module.
- **Universal Render Pipeline (URP)** — the YUV shader targets `RenderPipeline=UniversalPipeline`.
- **Unity Input System** (`com.unity.inputsystem`) — *Project Settings > Player > Active Input Handling* set to *Input System Package* or *Both*.
- **Meta XR SDK / Meta XR Core** (for `OVRPassthroughLayer`, headset tracking).
- **TextMeshPro** (import the essential resources if prompted).
- **[PopH264](https://github.com/SoylentGraham/PopH264)** — the native Android plugin must be present under `Plugins/Android`.
- Optional: the three Google Fonts and/or the icon font — see [Fonts](#fonts).

### Importing the scripts
1. Copy all `.cs` files into `Assets/Scripts/`.
2. Copy `TelloYuvNV12ToRGB.shader` into the project, then create two materials **from scratch** (don't shader-swap an existing Lit material — it carries `_Metallic`/`_BumpMap`/lightmap baggage, and Editor play sessions can bake stale values into the asset):
   - `TelloVideoRGBA`: shader *Universal Render Pipeline/**Unlit***, Surface Type = Transparent, Render Face = Both.
   - `TelloVideoYUV`: shader `TelloQuest/YuvNV12ToRGB` — already transparent and double-sided by the shader itself, nothing else to set.
3. Copy `TelloWifiConnector.java` to `Assets/Plugins/Android/src/main/java/com/tello4quest2/TelloWifiConnector.java`. Unity compiles any `.java` found there as part of the Gradle build.
4. Add the two Wi-Fi permissions to your manifest — copy `AndroidManifest.xml` from this repo to `Assets/Plugins/Android/`, or add its two `<uses-permission>` lines to your existing one.

### Building the scene
Everything marked **Positioned Externally + starts inactive** must actually be *unchecked/inactive in the Hierarchy* at edit time — `TelloInitGate` activates and positions each one at the right moment. If any start active, their self-positioning runs too early and things end up in the wrong place, or stacked on each other.

| GameObject | Component(s) | Notes |
|---|---|---|
| `TelloConnection` | `TelloConnection` | Default IP `192.168.10.1`. |
| `TelloGamepadController` | `TelloGamepadController` | Reference `TelloConnection`, `TelloVideoDisplay`, `TelloVideoRecorder`, and **App State Gate** = the `TelloInitGate` GameObject. |
| `TelloVideo` | `TelloVideoReceiver` + `TelloVideoDecoder` + `TelloVideoRecorder` | Must stay **active** from launch — the pre-flight video check depends on it. |
| `TelloVideoScreen` | `TelloVideoDisplay` | Assign both materials and **Vr Camera**. Positioned Externally, starts inactive. |
| `TelloStatusPanel` | `TelloStatusPanel` | Positioned Externally, starts inactive. |
| `TelloOptionsPanel` | `TelloOptionsPanel` | Positioned Externally, starts inactive. |
| `TelloSpatialPanel` | `TelloSpatialPanel` | Reference `TelloConnection`/`TelloVideoDecoder`/`TelloVideoDisplay`. Positioned Externally, starts inactive. |
| `TelloActionLogPanel` | `TelloActionLogPanel` | Reference `TelloConnection`/`TelloGamepadController`/`TelloVideoRecorder`/`TelloVideoDisplay`. Positioned Externally, starts inactive. |
| `TelloSettingsScreen` | `TelloSettingsScreen` | Reference `TelloInitGate` and every panel above. `Video Decoder` and `Action Log Panel` are optional (found automatically if left empty). Starts inactive. |
| `TelloInitGate` | `TelloInitGate` | References everything above, plus **Vr Camera**. |
| `PassthroughLayer` | `OVRPassthroughLayer` | Placement = Underlay. |

### Rendering settings that matter

| Setting | Where | Value | Why |
|---|---|---|---|
| Color Space | Player > Other Settings | **Linear** | The shader's gamma handling assumes it. On Gamma, remove the `SRGBToLinear` call at the end of the fragment shader. |
| Render Mode | XR Plug-in Management > OpenXR (Android) | **Single Pass Instanced \ Multi-view** | The shader now supports it properly; Multi-pass roughly doubles GPU cost for nothing. |
| Render Scale | URP Asset > Quality | **~1.1** | 1.6 on a Quest 2 is 2.56× the pixels. Saturating the GPU makes dynamic resolution scale *down*, so a stable 1.1 looks sharper than an unstable 1.6. |
| Cast Shadows | URP Asset > Lighting | **off** | Nothing in the scene casts shadows; it was a wasted pass per frame. |
| Sharpen Type | OVRManager | **Quality** | Compositor-side sharpening at native panel resolution, applied after your render — better than any pre-resample sharpen, and free. |

### ⚠️ Tracking Origin setup — can prevent the app from launching at all
If the app uses **Floor Level** tracking origin *and* Passthrough is enabled, Quest's OS treats it as a Roomscale experience requiring a configured Guardian boundary. If the headset doesn't have one (or is in Stationary mode), the app can fail to launch entirely with no error shown — it just returns to the launcher. Confirmed via a device log showing `GuardianLaunchCheckHandler ... RequiresGuardian ... Launch failed`.

This project doesn't need floor calibration: `TelloUiKit.ComputeFixedPosition` takes its own `assumedEyeHeightMeters` rather than relying on the runtime's. So:
- On `Camera Offset`: **Requested Tracking Mode → Device**
- On `OVR Manager`: **Tracking Origin Type → Eye Level** (not *Stationary* — still experimental, with known accuracy issues against Unity's OpenXR components per Meta's own docs)

Both must be changed together; they're meant to agree with each other.

### Meta Quest build (Unity 6 Build Profiles)
1. *Build Profiles* → select the **Meta Quest** profile, set it **Active**.
2. Platform Settings: Texture Compression **ASTC**, Debug Symbols **Debugging (Full)**, Compression Method **LZ4**. Export Project / Symlink Sources / Development Build off unless needed.
3. *Player Settings* (scoped to Meta Quest): Scripting Backend **IL2CPP**, Minimum API Level **Android 12L (32)**, Target API Level **Android 14.0 (34)**.
4. Enable XR (Meta XR / OpenXR) under *XR Plug-in Management*.
5. **App icon**: Android Adaptive Icons need *both* a Background and a Foreground layer per size under *Player Settings > Icon* — Unity flags it in red if only one is set, and the icon silently won't show correctly on-device.
6. Build & Run, or export the APK for SideQuest/ADB. If you rebuild over an existing install and the icon doesn't update, uninstall completely first — the Quest launcher's icon cache is aggressive.

## Known limitations / ideas for improvement

- **Gamepad not detected if it pairs after the app has already launched** — still open. Logs across three sessions confirm the OS-level Bluetooth HID connection succeeds, but the controller sometimes never appears in Unity's Input System device list at all (`InputSystem.devices` itself stays empty, not just `Gamepad.current`). Scanning the device list directly helps in some cases but doesn't fully solve it; likely needs a native Android bridge to catch the HID connect event. Turning the gamepad on *before* launching is the reliable workaround.
- **Macroblock artefacts from packet loss can be reduced but not eliminated** — the Tello's Wi-Fi is 2.4 GHz only, and no shader can reconstruct data that never arrived. The 4 MB receive buffer and access-unit validation cut the damage substantially; range and interference are the rest of the story.
- **`setbitrate` / `setresolution` / `setfps` are SDK 2.0 only** — the consumer Tello answers `unknown command`. The commands are implemented and exposed on `TelloConnection`, but ship **disabled by default**; enable them only if `sdk?` reports 20 or 30. On an EDU/Talent this is the single biggest available quality win, because it's the only one that acts at the source.
- **Wi-Fi auto-connect is built but not wired** — `TelloWifiConnector.java` + `TelloInitGate.ConnectToTelloWifi()` implement connecting to the Tello's hotspot by SSID prefix via `WifiNetworkSpecifier`, falling back to the system Wi-Fi panel. It proved more trouble than it was worth in real testing, and the West button now opens Flight settings instead. The code is still present and referenced.
- **In-headset gallery doesn't work** — two approaches were tried (a generic `ACTION_VIEW` intent, and the Android system Photo Picker) and both failed cleanly: the first resolves to a Meta system component that opens and immediately self-closes, the second doesn't exist on Quest's OS. Photos and videos are still fully accessible via the Files app, MQDH, or USB.
- **East and West both take a photo in Piloting mode** — a leftover from consolidating an older menu-mode toggle. Decoupling Takeoff (South) from Land (East) is planned: one button, one action, regardless of the drone's state, which removes a small but real safety risk in the current toggle.
- Wind-drift detection is an indirect estimate (commanded stick input vs. actual telemetry velocity) — the Tello has no wind sensor, so thresholds may need tuning against real flight data.
- No disk-space management for recordings and photos.
- Reconnection after signal loss is automatic but can take a few seconds depending on how stable the Tello's Wi-Fi is.
- The BT.601 vs BT.709 choice is a judgement call: the Tello's SPS carries no VUI, so nothing in the stream declares which matrix it used. 601 is the default; a switch in Video settings lets you compare on foliage and skin tones, where the difference actually shows.
- Feedback and PRs are very welcome, especially around gamepad detection reliability, wind-alert threshold tuning, and general ergonomics.

## License and contributions

This is a community project, open to contributions. Use it, modify it, improve it for your own Tello — the goal is for every Tello + Quest 2 owner to benefit and help push this further. If you fork it, a mention back to this repo is appreciated.

Found a bug, have an idea, or got it working better on your setup? Open an issue or a PR — feedback is exactly what this needs to get better.
