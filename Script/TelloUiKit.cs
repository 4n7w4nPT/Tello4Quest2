using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Shared building blocks for the flat-card world-space UI used across
    /// TelloInitGate, TelloStatusPanel and TelloOptionsPanel: the procedural
    /// rounded-rect sprite (previously generated separately, and slightly
    /// differently, in each of those three scripts) and the drop-shadow card
    /// shell they all build cards out of.
    ///
    /// Also centralizes the "world-locked, fixed relative to where the
    /// headset was looking at Start()" placement formula shared by
    /// TelloVideoDisplay and TelloInitGate, so the two can't silently drift
    /// out of sync with each other.
    /// </summary>
    public static class TelloUiKit
    {
        private static readonly Dictionary<int, Sprite> SpriteCache = new Dictionary<int, Sprite>();

        // =================================================================
        // PROCEDURAL ROUNDED-RECT SPRITE
        // =================================================================

        /// <summary>
        /// Returns a cached 64x64 rounded-rect sprite for the given corner
        /// radius (in the same "pixel at CanvasPixelWidth=130ish" units the
        /// callers already used), generating it once per distinct radius.
        /// </summary>
        public static Sprite GetRoundedSprite(float cornerRadiusPx)
        {
            const int size = 64;
            int radius = Mathf.RoundToInt(cornerRadiusPx * size / 130f);
            radius = Mathf.Clamp(radius, 3, size / 2 - 1);

            if (SpriteCache.TryGetValue(radius, out Sprite cached) && cached != null)
                return cached;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool inside = IsInsideRoundedRect(x, y, size, size, radius);
                    pixels[y * size + x] = inside ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            var border = new Vector4(radius, radius, radius, radius);
            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
            SpriteCache[radius] = sprite;
            return sprite;
        }

        private static bool IsInsideRoundedRect(int x, int y, int w, int h, int r)
        {
            if (x < r && y < r) return Vector2.Distance(new Vector2(x, y), new Vector2(r, r)) <= r;
            if (x >= w - r && y < r) return Vector2.Distance(new Vector2(x, y), new Vector2(w - r - 1, r)) <= r;
            if (x < r && y >= h - r) return Vector2.Distance(new Vector2(x, y), new Vector2(r, h - r - 1)) <= r;
            if (x >= w - r && y >= h - r) return Vector2.Distance(new Vector2(x, y), new Vector2(w - r - 1, h - r - 1)) <= r;
            return true;
        }

        // =================================================================
        // CARD SHELL (drop shadow + rounded background)
        // =================================================================

        /// <summary>
        /// Builds a "Card" GameObject (shadow + rounded background) under
        /// parent at the given anchored position/size, and returns its
        /// RectTransform so the caller can parent its own label/value/dot
        /// content into it.
        /// </summary>
        public static RectTransform BuildCardShell(
            Transform parent, string name, Vector2 anchoredPosition, Vector2 size,
            Sprite roundedSprite, Color backgroundColor, Color shadowColor, Vector2 shadowOffsetPx)
        {
            var cardGO = new GameObject(name, typeof(RectTransform));
            cardGO.transform.SetParent(parent, false);
            RectTransform cardRect = cardGO.GetComponent<RectTransform>();
            cardRect.sizeDelta = size;
            cardRect.anchoredPosition = anchoredPosition;

            var shadowGO = new GameObject("Shadow", typeof(RectTransform), typeof(Image));
            shadowGO.transform.SetParent(cardGO.transform, false);
            RectTransform shadowRect = shadowGO.GetComponent<RectTransform>();
            shadowRect.anchorMin = Vector2.zero;
            shadowRect.anchorMax = Vector2.one;
            shadowRect.offsetMin = Vector2.zero;
            shadowRect.offsetMax = Vector2.zero;
            shadowRect.anchoredPosition = shadowOffsetPx;
            Image shadowImage = shadowGO.GetComponent<Image>();
            shadowImage.sprite = roundedSprite;
            shadowImage.type = Image.Type.Sliced;
            shadowImage.color = shadowColor;

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(cardGO.transform, false);
            RectTransform bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            Image bgImage = bgGO.GetComponent<Image>();
            bgImage.sprite = roundedSprite;
            bgImage.type = Image.Type.Sliced;
            bgImage.color = backgroundColor;

            return cardRect;
        }

        /// <summary>Fills the given rect (typically the whole canvas) with a plain rounded background image - no shadow, no card content.</summary>
        public static void BuildFullRectBackground(Transform parent, Sprite roundedSprite, Color backgroundColor)
        {
            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(parent, false);
            RectTransform bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            Image bgImage = bgGO.GetComponent<Image>();
            bgImage.sprite = roundedSprite;
            bgImage.type = Image.Type.Sliced;
            bgImage.color = backgroundColor;
        }

        // =================================================================
        // WORLD-LOCKED FIXED PLACEMENT
        // =================================================================
        // Shared by TelloVideoDisplay and TelloInitGate so the pre-flight
        // gate and the flight display always compute the exact same
        // position/rotation from the same camera transform - the hand-off
        // between them (see TelloInitGate.RevealFlightDisplay) depends on
        // that being true.

        public static Quaternion ComputeFixedRotation(Transform vrCamera)
        {
            Vector3 flatForward = Vector3.ProjectOnPlane(vrCamera.forward, Vector3.up).normalized;
            if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
            return Quaternion.LookRotation(flatForward, Vector3.up);
        }

        public static UnityEngine.InputSystem.Gamepad GetActiveGamepad()
        {
            var current = UnityEngine.InputSystem.Gamepad.current;
            if (current != null) return current;

            // Gamepad.current is only set once the device has sent at least one
            // input event - a controller that was paired over Bluetooth but never
            // touched yet can already be fully enumerated in InputSystem.devices
            // without Gamepad.current ever being set. Scan the device list
            // directly instead of waiting on the user to press something first.
            foreach (var device in UnityEngine.InputSystem.InputSystem.devices)
            {
                if (device is UnityEngine.InputSystem.Gamepad gp && gp.enabled) return gp;
            }
            return null;
        }

        public static Vector3 ComputeFixedPosition(Transform vrCamera, float distanceFromCamera, float assumedEyeHeightMeters, float verticalOffset)
        {
            Vector3 flatForward = Vector3.ProjectOnPlane(vrCamera.forward, Vector3.up).normalized;
            if (flatForward.sqrMagnitude < 0.001f) flatForward = Vector3.forward;
            Vector3 basePosition = new Vector3(vrCamera.position.x, assumedEyeHeightMeters, vrCamera.position.z);
            return basePosition + flatForward * distanceFromCamera + Vector3.up * verticalOffset;
        }

        // =================================================================
        // GAMEPAD BRAND DETECTION (shared by TelloInitGate and TelloSettingsScreen,
        // so every screen's button prompts stay consistent with each other)
        // =================================================================
        public enum GamepadBrand { PlayStation, Xbox, Generic }

        // Sticky cache: once a real gamepad is detected, a transient null or an
        // unrelated device reference (deviceId differs) won't silently downgrade
        // the cached brand back to Generic - fixes prompts flip-flopping between
        // brand-specific and generic text if Gamepad.current is momentarily
        // unstable (e.g. right after a reconnect).
        private static UnityEngine.InputSystem.Gamepad lastDetectedPad;
        private static GamepadBrand cachedBrand = GamepadBrand.Generic;

        public static GamepadBrand CurrentGamepadBrand()
        {
            var pad = GetActiveGamepad();
            if (pad != null && (lastDetectedPad == null || pad.deviceId != lastDetectedPad.deviceId))
            {
                lastDetectedPad = pad;
                cachedBrand = DetectGamepadBrand(pad);
            }
            return cachedBrand;
        }

        private static GamepadBrand DetectGamepadBrand(UnityEngine.InputSystem.Gamepad pad)
        {
            if (pad == null) return GamepadBrand.Generic;

            string signature = $"{pad.displayName} {pad.description.product} {pad.description.manufacturer}".ToLowerInvariant();

            if (signature.Contains("dualsense") || signature.Contains("dualshock") ||
                signature.Contains("ps4") || signature.Contains("ps5") ||
                signature.Contains("sony") || signature.Contains("wireless controller"))
                return GamepadBrand.PlayStation;

            if (signature.Contains("xbox") || signature.Contains("xinput"))
                return GamepadBrand.Xbox;

            return GamepadBrand.Generic;
        }

        /// <summary>position: "south"/"north"/"east"/"west" - matches Unity's own Gamepad
        /// button naming, which corresponds to the same physical position on every
        /// brand's controller.</summary>
        public static string ButtonPrompt(GamepadBrand brand, string position)
        {
            switch (brand)
            {
                case GamepadBrand.PlayStation:
                    return position switch { "south" => "Press X", "north" => "Press Triangle", "east" => "Press Circle", "west" => "Press Square", _ => "Press" };
                case GamepadBrand.Xbox:
                    return position switch { "south" => "Press A", "north" => "Press Y", "east" => "Press B", "west" => "Press X", _ => "Press" };
                default:
                    return position switch { "south" => "Press the bottom button", "north" => "Press the top button", "east" => "Press the right button", "west" => "Press the left button", _ => "Press" };
            }
        }

        /// <summary>Same button naming as ButtonPrompt, without the "Press " prefix - for
        /// layouts where "Press" is shown as its own separate, always-visible label.</summary>
        public static string ButtonName(GamepadBrand brand, string position)
        {
            switch (brand)
            {
                case GamepadBrand.PlayStation:
                    return position switch { "south" => "X", "north" => "Triangle", "east" => "Circle", "west" => "Square", _ => "" };
                case GamepadBrand.Xbox:
                    return position switch { "south" => "A", "north" => "Y", "east" => "B", "west" => "X", _ => "" };
                default:
                    return position switch { "south" => "the bottom button", "north" => "the top button", "east" => "the right button", "west" => "the left button", _ => "" };
            }
        }
    }
}
