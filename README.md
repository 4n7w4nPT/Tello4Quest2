# Tello4Quest2

Fly and watch the live video feed of a **DJI/Ryze Tello** drone (the consumer model, SDK 1.3) directly inside a **Meta Quest 2** headset, in passthrough, using a Bluetooth gamepad. The video screen, telemetry banners, and menu float in front of you, locked in place relative to where you were looking when the app launched.

This repo contains every Unity script used, plus the YUV→RGB conversion shader and the two materials it depends on.

**v0.3** — real `.mp4` recording (via Android's MediaMuxer, no more raw `.h264` needing ffmpeg to become watchable), a flight-path mini-map, and automatic video enhancement (night mode + sharpening). See [Known limitations](#known-limitations--ideas-for-improvement) for what's still rough around the edges.

<details>
<summary>v0.2 — Menu/Piloting/Settings screens, activity log, visual redesign</summary>

This release added a full Menu/Piloting/Settings screen system, a scrollable settings screen with ~30 adjustable parameters, a drone "activity log" that narrates what's happening in short first-person lines, and a visual redesign (aviation-instrument look, custom fonts).
</details>

<details>
<summary>v0.1 — first release</summary>

Automatic connection to the Tello over WiFi direct, live H.264 video decoded and displayed on a world-locked floating screen, Bluetooth gamepad piloting (sticks, flips, takeoff/land, emergency stop), photo/video capture, live telemetry banners (battery, altitude, speed, flight time, video signal), and the first safety features: automatic landing on critical battery, a software altitude ceiling, crash detection, and a dead-reckoning estimate of the way back to the takeoff point. A pre-flight gate screen waited for gamepad + Tello WiFi + video feed before revealing the flight UI.
</details>

## What it does

- **Three-screen flow**: a Menu screen (pre-flight checklist + button legend), the Piloting screen (video + telemetry, gamepad live), and a Settings screen — see [Controls](#controls) below for the exact button mapping on each.
- Automatic connection to the Tello over WiFi direct, no manual setup, with automatic reconnection if the link drops.
- Live H.264 video decoded and displayed on a floating screen, world-locked (it does **not** follow your head). Distance, size, transparency, and vertical position are all adjustable in Settings. Automatic image enhancement: a per-pixel night mode (brightness lift + adaptive noise blur on dark footage, self-limiting so well-lit footage is untouched) and sharpening to counter H.264 compression softness — no manual toggle, no whole-frame analysis pass, just local luma driving both effects.
- Bluetooth gamepad piloting: sticks fly the drone, shoulder buttons/triggers adjust speed and stick sensitivity live, dpad triggers flips (one at a time — a new flip request is ignored, not queued, until the previous one is confirmed done), one button for takeoff/land, one for emergency stop (always live, regardless of which screen is showing).
- Button prompts are brand-aware: PlayStation and Xbox controllers get the correct button name automatically (detected from the device's own name/manufacturer string), with a generic positional fallback ("press the bottom button") for anything unrecognized. Optional support for showing an actual button *icon* instead of text, via a Sony/Microsoft-style icon font (see [Fonts](#fonts)).
- Photo capture (PNG) and video recording — real **.mp4** files (via Android's MediaMuxer, zero re-encoding of the underlying H.264 data), playable directly from the headset's own Files app/Quest gallery, no external tool needed. Both saved to the headset's **shared** storage (`Pictures/Tello4Quest2`, `Movies/Tello4Quest2`) — visible via the headset's Files app, MQDH, or USB, the same way Quest's own screenshots are.
- Flight log (CSV) saved the same way, under `Download/Tello4Quest2`.
- Live telemetry: top banner (gamepad/Tello/video/last-command status + temperature), bottom banner (altitude, flight time, ground speed, estimated time remaining, batteries), a left-side band (accelerometer gauge + bearing back to the takeoff point), and a right-side band split in two: an **activity log** on top — a running, first-person transcript of what the drone is doing and noticing ("Alright, taking off." / "Getting warm up here, keep an eye on me." / "Something's pushing me around — might be wind."), oldest entry at the top fading toward the newest at the bottom — and a **mini-map** of the flight path below it: north-up (never rotates, only the drone's heading icon does), a persistent trail, and a zoom level based on the largest distance ever reached from the takeoff point this flight (never shrinks mid-flight, so the scale doesn't jitter).
- Safety features: automatic landing on critical battery, a software altitude ceiling, crash detection (acceleration spike), dead-reckoning estimate of the way back to the takeoff point (no GPS on the consumer Tello), and a set of one-shot alerts (battery, temperature, abnormally fast descent, estimated wind drift, degrading signal) — each fires once per episode rather than spamming every telemetry tick, with hysteresis so a value sitting right at a threshold doesn't retrigger on every small wobble.
- A Settings screen (reached from the Menu) with ~30 adjustable parameters — screen placement/size/opacity, flight safety thresholds, gamepad feel, panel placement — grouped by section, gamepad-navigable, with a one-press "reset to defaults" and everything persisted across app restarts.
- A pre-flight gate screen that waits for gamepad + Tello WiFi + video feed to all be ready before allowing takeoff, with the three checks kept live in the background even while flying, so returning to the menu never shows stale status.

## Controls

Button prompts shown on-screen adapt automatically to whichever gamepad is connected (PlayStation names, Xbox names, or a generic "top/bottom/left/right button" fallback). Positions below are given as Unity's own North/South/East/West, which correspond to the same physical location on every standard gamepad.

**Menu screen**

| Button | Action |
|---|---|
| South | Enter Piloting (only once all three pre-flight checks are green) |
| North | Open Settings |
| East | Quit the app |
| West | Open gallery — see [Known limitations](#known-limitations--ideas-for-improvement), this doesn't reliably work yet |

**Piloting screen**

| Input | Action |
|---|---|
| Left stick | Yaw + throttle/altitude |
| Right stick | Roll + pitch |
| South | Takeoff / Land (toggle) |
| West, East | Take a photo (both do the same thing right now — see Known limitations) |
| North | Start/stop video recording |
| D-pad | Flip forward/back/left/right — one at a time, a new flip is ignored (not queued) until the previous one is confirmed |
| L1 / R1 | Speed level −/+ |
| L2 / R2 | Sensitivity level −/+ |
| Share/Select | Emergency stop — live on every screen, not just Piloting |
| Options/Start | Return to Menu (only if landed — blocked with a haptic pulse if still flying) |

**Settings screen**

| Input | Action |
|---|---|
| Left stick (up/down) | Select a row |
| Right stick (left/right) | Adjust the selected row's value, or toggle a boolean |
| South | Save and exit |
| North | Reset every value to its default (doesn't exit — review, then Save or Cancel) |
| East | Exit without saving |

## Fonts

The visual style (aviation-instrument look — amber accents, stencil title, monospace status text) uses three Google Fonts, all optional: without them, everything falls back cleanly to TextMeshPro's default font, nothing breaks.

| Role | Font | Weight |
|---|---|---|
| Titles | Big Shoulders Stencil | Bold |
| Body/labels | IBM Plex Sans | Regular |
| Status/mono text | IBM Plex Mono | Medium |

To use them: download the `.ttf`, drop it in `Assets/Fonts/`, right-click → *Create → TextMeshPro → Font Asset* (choose **SDF**), then assign the three resulting Font Assets to the matching fields on `TelloInitGate` and `TelloSettingsScreen` (Display/Body/Mono Font).

### Optional: button icon glyphs

`TelloInitGate` also supports showing an actual PlayStation/Xbox button *icon* instead of text, via an icon font where specific characters render as button glyphs (tested with [Stephan Dube's free PS4/Xbox icon font](https://stephandube.com)). Assign it to the **Icon Font** field; the 8 glyph characters (`Icon Glyph PlayStation South/North/East/West`, `Icon Glyph Xbox South/North/East/West`) already default to the correct characters for that specific font (`D`/`B`/`C`/`A` for PlayStation, `d`/`b`/`c`/`a` for Xbox — confirmed against that font's own character map). Leave **Icon Font** unassigned to keep plain text prompts; nothing else needs to change either way.

> That font's license (verbatim from stephandube.com): free to use and modify for personal and commercial projects; redistribution allowed only in its original, unmodified form; attribution appreciated but not required; provided as-is, no warranty.

## Storage & permissions

Photos, videos, and flight logs are all written via Android's **MediaStore** API into the shared collections (`Pictures/`, `Movies/`, `Download/`), each under a `Tello4Quest2` subfolder — not the app's private storage. This is deliberate: inserting your own new items into MediaStore doesn't require any special manifest permission on modern Android (scoped storage), and it means everything the app produces is immediately visible from the headset's own Files app, from MQDH's File Manager on a connected PC, and over a plain USB file transfer — the same way Quest's own screenshots and recordings are.

Video recordings are raw H.264 elementary streams (`.h264`), not `.mp4` — VLC and ffplay open them directly; to get a standard container: `ffmpeg -i recording.h264 -c copy recording.mp4`.

## Sources

- **Tello protocol (consumer SDK 1.3)**: the official Ryze/DJI SDK documentation, plus a few undocumented behaviors confirmed through community reverse-engineering and our own testing — how video access units are framed by UDP packet size, the fact that the drone only emits its H.264 SPS/PPS/IDR parameter-set burst once (right when the encoder starts), and that a `flip` command isn't acknowledged until the maneuver has physically finished (~3s later), not on receipt.
- **[PopH264](https://github.com/SoylentGraham/PopH264)** for hardware H.264 decoding on Android/Quest (a wrapper around MediaCodec).
- **Android MediaMuxer API** for real-time `.mp4` muxing of the recorded stream — no re-encoding, same H.264 access units repackaged as they're written.
- **Meta XR SDK / OVRPlugin** for passthrough and headset tracking.
- **Unity Input System** for gamepad handling, including its Android-specific quirks around detecting a controller that pairs *after* the app has already launched (see [Known limitations](#known-limitations--ideas-for-improvement)).
- **Android MediaStore API** for shared-storage photo/video/log saving.
- Claude working with me

## Architecture

| Script | Role |
|---|---|
| `TelloConnection.cs` | Singleton managing the UDP connection (command port 8889, state port 8890), the sequential one-shot command queue, continuous `rc` sending, safety thresholds and one-shot alert logic, CSV flight logging (via MediaStore), and automatic reconnection. |
| `TelloVideoReceiver.cs` | Raw UDP reception of the video stream (port 11111) and reassembly of H.264 (Annex-B) access units on a dedicated background thread. |
| `TelloVideoDecoder.cs` | Hardware decoding via PopH264; handles both possible output formats (direct RGBA/BGRA, or 2-plane YUV NV12, depending on the device); waits for a paired SPS+PPS before feeding the decoder, and captures the real SPS/PPS bytes live from the stream (never hardcoded) for `TelloVideoRecorder` to reuse. |
| `TelloVideoDisplay.cs` | Displays the feed on a world-locked quad; handles zoom/continuous size, opacity, and photo capture. |
| `TelloYuvNV12ToRGB.shader` + `TelloVideoYUV.mat` / `TelloVideoRGBA.mat` | GPU-side YUV→RGB conversion (BT.601) with alpha-blend support for the transparency setting, automatic night mode, and sharpening; and the direct-RGBA material. |
| `TelloVideoRecorder.cs` | Records the video feed as a real `.mp4` via Android's MediaMuxer (zero re-encoding - the same H.264 access units are just repackaged as they're written), using the SPS/PPS `TelloVideoDecoder` captured live from the stream. |
| `TelloGamepadController.cs` | Gamepad input (Unity Input System), command mapping, stick calibration, haptic feedback, photo capture trigger, flip lock. |
| `TelloInitGate.cs` | Owns the Menu/Piloting/Settings screen state machine, the pre-flight checklist, the button legend (brand-aware prompts, optional icon glyphs), and hand-off to the flight display. |
| `TelloSettingsScreen.cs` | The scrollable Settings screen — ~30 parameters across Display/Safety/Gamepad/Panels sections, gamepad navigation, reset-to-defaults, persistence. |
| `TelloStatusPanel.cs` / `TelloOptionsPanel.cs` | Telemetry banners above/below the video screen. |
| `TelloSpatialPanel.cs` | Left-side band: accelerometer gauge + bearing back to the takeoff point, combined in one panel sized to match the video screen's height. |
| `TelloActionLogPanel.cs` | Right-side band: the drone's first-person activity log (top half) and a flight-path mini-map (bottom half). |
| `TelloUiKit.cs` | Shared UI utilities — procedural rounded-sprite generation, card-shell building, fixed camera-relative placement math, and gamepad brand/button-prompt detection. |

## Building the project

### Prerequisites
- **Unity 6** (tested on 6000.5.2f1) with the **Android** build module installed.
- **Universal Render Pipeline (URP)** — the YUV shader targets `RenderPipeline=UniversalPipeline`.
- **Unity Input System** package (`com.unity.inputsystem`) — under *Project Settings > Player > Active Input Handling*, pick *Input System Package* (or *Both*).
- **Meta XR SDK / Meta XR Core** (for `OVRPassthroughLayer`, headset tracking, etc.).
- **TextMeshPro** (import the essential resources if prompted on first use).
- **[PopH264](https://github.com/SoylentGraham/PopH264)** — grab the Unity package and import it; the native Android plugin needs to be present under `Plugins/Android`.
- Optional: the three Google Fonts and/or the icon font — see [Fonts](#fonts).

### Importing the scripts
1. Copy all `.cs` files from this repo into your project's `Assets/Scripts/` folder (or wherever you keep scripts).
2. Copy `TelloYuvNV12ToRGB.shader` into the project, then create two materials:
   - `TelloVideoRGBA`: shader *Universal Render Pipeline/Unlit*, **Surface Type = Transparent** (required for the Settings screen's transparency slider to have any visible effect), Render Face = Both.
   - `TelloVideoYUV`: shader `TelloQuest/YuvNV12ToRGB` (included here — already Transparent/double-sided by the shader itself, no extra settings needed).
   (The two `.mat` files in this repo can also be imported directly instead.)

### Building the scene
GameObjects to create, with their components. Everything marked **Positioned Externally + starts inactive** must actually be *unchecked/inactive in the Hierarchy* at edit time — `TelloInitGate` activates and positions each one at the right moment; if any of them start active, their own self-positioning logic runs too early and things end up in the wrong place (or, worse, several of them stacked on top of each other).

| GameObject | Component(s) | Notes |
|---|---|---|
| `TelloConnection` | `TelloConnection` | Default IP `192.168.10.1` (the Tello's WiFi-direct address). |
| `TelloGamepadController` | `TelloGamepadController` | Reference `TelloConnection`, `TelloVideoDisplay` (for photo capture), `TelloVideoRecorder`, and **App State Gate** = the `TelloInitGate` GameObject (flight input only applies in Piloting mode). |
| `TelloVideo` | `TelloVideoReceiver` + `TelloVideoDecoder` + `TelloVideoRecorder` | Must stay **active** from launch — the pre-flight gate's video check depends on it. |
| `TelloVideoScreen` | `TelloVideoDisplay` | Assign both materials and **Vr Camera**. Check **Positioned Externally**, start inactive. |
| `TelloStatusPanel` | `TelloStatusPanel` | Check **Positioned Externally**, start inactive. |
| `TelloOptionsPanel` | `TelloOptionsPanel` | Check **Positioned Externally**, start inactive. |
| `TelloSpatialPanel` | `TelloSpatialPanel` | Reference `TelloConnection`/`TelloVideoDisplay`. Check **Positioned Externally**, start inactive. |
| `TelloActionLogPanel` | `TelloActionLogPanel` | Reference `TelloConnection`/`TelloGamepadController`/`TelloVideoRecorder`/`TelloVideoDisplay`. Check **Positioned Externally**, start inactive. |
| `TelloSettingsScreen` | `TelloSettingsScreen` | Reference `TelloInitGate` and every panel above (for the settings rows). Start inactive. |
| `TelloInitGate` | `TelloInitGate` | References everything above (see the "What To Reveal While Piloting" and "Settings Screen" fields), plus **Vr Camera**. |
| `PassthroughLayer` | `OVRPassthroughLayer` | Placement = Underlay. |

Every cross-reference is wired by drag-and-drop in the Inspector — no `Shader.Find` or `FindObjectOfType` at runtime.

### Meta Quest build (Unity 6 Build Profiles)
1. *Build Profiles* window → select the **Meta Quest** profile in the Platforms list, then set it **Active**.
2. Platform Settings (Meta Quest):
   - Texture Compression: **ASTC**
   - Debug Symbols: **Debugging (Full)**
   - Compression Method: **LZ4**
   - Export Project / Symlink Sources / Development Build: off (unless you specifically need them)
3. Under *Player Settings* (still scoped to the Meta Quest profile): Scripting Backend **IL2CPP**, Minimum API Level **Android 12L (API level 32)**, Target API Level **Android 14.0 (API level 34)**.
4. Enable XR (Meta XR / OpenXR) under *XR Plug-in Management*.
5. **App icon**: Android Adaptive Icons need *both* a Background and a Foreground layer set for each size under *Player Settings > Icon* — Unity flags it in red if only one is filled in, and the icon silently won't show up correctly on-device until both are set.
6. Build & Run from the Meta Quest profile, or export the APK for install via SideQuest/ADB. If you rebuild over an existing install and the icon doesn't seem to update, uninstall the app completely first (not just reinstall over it) — Android/Quest launcher icon caching is aggressive, and a straight overwrite often keeps showing the old one.

## Known limitations / ideas for improvement

- **Gamepad not detected if it pairs after the app has already launched** — still open. Confirmed via logs that the OS-level Bluetooth HID connection succeeds, but the controller sometimes never shows up in Unity's Input System device list at all (not just `Gamepad.current` — `InputSystem.devices` itself stays empty). A fallback that scans the device list directly (rather than relying only on `.current`, which only updates after the pad sends its first input) helps in some cases but doesn't fully solve it. Likely needs a native Android-side bridge to catch the HID connect event directly. Turning the gamepad on *before* launching the app is the reliable workaround for now.
- **In-headset gallery doesn't work** — two different approaches were tried (a generic `ACTION_VIEW` intent, and the Android system Photo Picker) and both failed cleanly: the first resolves to a Meta system component that opens and immediately self-closes, the second doesn't exist on Quest's OS at all. There doesn't appear to be a supported way to open a photo/video viewer from a third-party Quest app right now. Photos and videos are still fully accessible — just via the headset's own Files app, MQDH, or USB, not from inside this app.
- Wind-drift detection is an indirect estimate (commanded stick input vs. actual telemetry velocity) — the Tello has no real wind sensor, so thresholds may need tuning against real flight data.
- `sdk?` returns `unknown command: sdk?` on some Tello firmware versions — cosmetic only, doesn't affect flight or video.
- No disk-space management for recordings/photos.
- Reconnection after a signal loss is automatic but can take a few seconds depending on how stable the Tello's WiFi is.
- UDP packet reassembly occasionally produces an oversized access unit with multiple NAL units concatenated together, instead of a clean one-NAL-per-unit boundary (seen once in a diagnostic log, not yet root-caused). Doesn't currently break anything observed, but worth investigating further.
- East and West both trigger a photo in Piloting mode right now (a leftover from consolidating an older "menu mode" toggle). Decoupling Takeoff (South) from Land (East) is planned — one button always does one thing regardless of the drone's current state, which removes a small but real safety risk in the current toggle (pressing South with a stale mental model of "is it flying" can do the opposite of what's intended). That change would also free West up for something non-redundant, along with letting players remap buttons themselves. Not yet implemented.
- Feedback and PRs are very welcome, especially around gamepad detection reliability, wind-alert threshold tuning, and general ergonomics.

## License and contributions

This is a community project, open to contributions. Use it, modify it, improve it for your own Tello — the goal is for every Tello + Quest 2 owner to benefit and help push this further. If you fork it, a mention back to this repo is appreciated.

Found a bug, have an idea, or got it working better on your setup? Open an issue or a PR — feedback is exactly what this needs to get better.
