using UnityEngine;

namespace TelloQuest
{
    /// <summary>
    /// Displays the decoded Tello video feed on a flat quad. World-locked:
    /// positioned once at Start relative to where you were looking when the
    /// app launched, then never moves again - turning your head does NOT move
    /// it, same as a monitor bolted to a wall.
    ///
    /// Orientation: the Tello's raw feed comes in upside-down but NOT
    /// mirrored left/right, so a fixed vertical flip (applied as a UV
    /// transform, not by rotating the quad) is baked in below. This isn't a
    /// dropdown anymore - a prior pass through this file exposed a 4-option
    /// Orientation setting because it wasn't clear yet which single transform
    /// was actually correct; now that it's confirmed, there's nothing left to
    /// configure.
    ///
    /// Setup required in the Editor (once):
    /// 1. Create Assets/Materials/TelloVideoRGBA.mat, shader "Universal Render
    ///    Pipeline/Unlit", Render Face = Both.
    /// 2. Create Assets/Materials/TelloVideoYUV.mat, shader
    ///    "TelloQuest/YuvNV12ToRGB" (already double-sided by the shader itself).
    /// 3. Assign both below.
    /// </summary>
    public class TelloVideoDisplay : MonoBehaviour
    {
        [SerializeField] private TelloVideoDecoder decoder;
        [SerializeField] private Transform vrCamera;

        [Header("=== MATERIALS (project assets - create in Editor, never Shader.Find) ===")]
        [SerializeField] private Material rgbaMaterial;
        [SerializeField] private Material yuvMaterial;

        [Header("=== SIZE / PLACEMENT ===")]
        [SerializeField] private float distanceFromCamera = 1.2f;
        [Tooltip("Shifts the whole screen+banners ensemble down (negative) or up (positive) from dead-center eye level.")]
        [SerializeField] private float verticalOffset = -0.3f;
        [SerializeField] private float quadWidth = 1.9f;
        [SerializeField] private float quadHeight = 1.4f;

        [Header("=== ZOOM (screen size, adjustable at runtime) ===")]
        [Tooltip("Size multipliers applied to Quad Width/Height, index 0 = level 1.")]
        [SerializeField] private float[] zoomMultipliers = { 0.6f, 0.8f, 1f, 1.25f, 1.5f };
        [SerializeField] private int defaultZoomLevel = 3;

        [Header("=== SETTINGS-SCREEN ADJUSTABLE (continuous, on top of zoom above) ===")]
        [Tooltip("Extra continuous size multiplier stacked on top of the discrete zoom level - this is what the Settings screen's size slider drives.")]
        [SerializeField, Range(0.5f, 10f)] private float sizeMultiplier = 1f;
        [Tooltip("0 = fully see-through, 1 = fully opaque. Requires rgbaMaterial/yuvMaterial's Surface Type set to Transparent in the Editor - alpha has no visible effect on an Opaque-surface material.")]
        [SerializeField, Range(0.15f, 1f)] private float opacity = 1f;

        [Header("=== TEST MODE ===")]
        [Tooltip("Show a generated checker pattern instead of the real feed - validate placement/material first.")]
        [SerializeField] private bool useTestTexture = false;
        [Tooltip("Optional: use your own texture instead of the generated checker pattern.")]
        [SerializeField] private Texture2D customTestTexture;

        private int zoomLevel = 1; // safe non-zero default - see EffectiveZoomIndex
        private MeshRenderer screenRenderer;
        private Transform quadTransform;

        public int ZoomLevel => zoomLevel;
        public int MaxZoomLevel => zoomMultipliers.Length;
        private int EffectiveZoomIndex => Mathf.Clamp(zoomLevel - 1, 0, zoomMultipliers.Length - 1);

        /// <summary>Effective width/height, base size x current zoom multiplier x the continuous
        /// Settings-screen size multiplier - always reflects what's actually on screen right now.</summary>
        public float QuadWidth => quadWidth * zoomMultipliers[EffectiveZoomIndex] * sizeMultiplier;
        public float QuadHeight => quadHeight * zoomMultipliers[EffectiveZoomIndex] * sizeMultiplier;

        public float DistanceFromCamera { get => distanceFromCamera; set => distanceFromCamera = value; }
        public float VerticalOffset { get => verticalOffset; set => verticalOffset = value; }
        public float AssumedEyeHeightMeters { get => assumedEyeHeightMeters; set => assumedEyeHeightMeters = value; }
        public float SizeMultiplier => sizeMultiplier;
        public float Opacity => opacity;

        /// <summary>Raised whenever the zoom level changes - banners listen to this to stay glued to the resized screen.</summary>
        public event System.Action OnSizeChanged;

        public void SetZoomLevel(int level)
        {
            zoomLevel = Mathf.Clamp(level, 1, zoomMultipliers.Length);
            ApplyZoomScale();
            OnSizeChanged?.Invoke();
        }

        /// <summary>Called by TelloSettingsScreen on save - continuous size, independent of the discrete zoom levels above.</summary>
        public void SetSizeMultiplier(float multiplier)
        {
            sizeMultiplier = Mathf.Clamp(multiplier, 0.5f, 10f);
            ApplyZoomScale();
            OnSizeChanged?.Invoke();
        }

        /// <summary>Called by TelloSettingsScreen on save. See the opacity field's tooltip - the
        /// material's Surface Type must be Transparent in the Editor for this to be visible.</summary>
        public void SetOpacity(float value)
        {
            opacity = Mathf.Clamp(value, 0.15f, 1f);
            ApplyOpacity();
        }

        private void ApplyOpacity()
        {
            if (rgbaMaterial != null)
            {
                Color c = rgbaMaterial.color;
                c.a = opacity;
                rgbaMaterial.color = c;
            }
            if (yuvMaterial != null)
            {
                yuvMaterial.SetFloat("_Opacity", opacity);
            }
        }

        private void ApplyZoomScale()
        {
            if (quadTransform != null) quadTransform.localScale = new Vector3(QuadWidth, QuadHeight, 1f);
        }

        private const string PrefsPrefix = "TelloQuest_Settings_";

        private void LoadPersistedSettings()
        {
            distanceFromCamera = PlayerPrefs.GetFloat(PrefsPrefix + "Distance", distanceFromCamera);
            verticalOffset = PlayerPrefs.GetFloat(PrefsPrefix + "VerticalOffset", verticalOffset);
            assumedEyeHeightMeters = PlayerPrefs.GetFloat(PrefsPrefix + "EyeHeight", assumedEyeHeightMeters);
            sizeMultiplier = PlayerPrefs.GetFloat(PrefsPrefix + "SizeMultiplier", sizeMultiplier);
            opacity = PlayerPrefs.GetFloat(PrefsPrefix + "Opacity", opacity);
            defaultZoomLevel = PlayerPrefs.GetInt(PrefsPrefix + "ZoomLevel", defaultZoomLevel);
        }

        /// <summary>Called by TelloSettingsScreen after writing new values via the setters above, to persist them for next launch.</summary>
        public void SavePersistedSettings()
        {
            PlayerPrefs.SetFloat(PrefsPrefix + "Distance", distanceFromCamera);
            PlayerPrefs.SetFloat(PrefsPrefix + "VerticalOffset", verticalOffset);
            PlayerPrefs.SetFloat(PrefsPrefix + "EyeHeight", assumedEyeHeightMeters);
            PlayerPrefs.SetFloat(PrefsPrefix + "SizeMultiplier", sizeMultiplier);
            PlayerPrefs.SetFloat(PrefsPrefix + "Opacity", opacity);
            PlayerPrefs.SetInt(PrefsPrefix + "ZoomLevel", zoomLevel);
        }

        private void Awake()
        {
            if (decoder == null) decoder = GetComponent<TelloVideoDecoder>();
            LoadPersistedSettings();
            BuildQuad();

            zoomLevel = Mathf.Clamp(defaultZoomLevel, 1, zoomMultipliers.Length);
            ApplyZoomScale();
            ApplyOpacity();

            if (rgbaMaterial == null || yuvMaterial == null)
                Debug.LogError("[TelloVideoDisplay] rgbaMaterial or yuvMaterial not assigned in the inspector - create them as project assets first (see class doc comment).");

            ApplyOrientation();

            if (useTestTexture) ShowTestTexture();
        }

        private void OnEnable()
        {
            if (decoder != null) decoder.OnTextureUpdated += HandleTextureUpdated;
        }

        private void OnDisable()
        {
            if (decoder != null) decoder.OnTextureUpdated -= HandleTextureUpdated;
        }

        /// <summary>Applies a fixed vertical flip as a UV transform - RGBA material via the standard mainTextureScale/Offset, YUV material via explicit shader properties. See the class doc comment for why this one fixed transform is correct (not a per-project setting).</summary>
        private void ApplyOrientation()
        {
            if (rgbaMaterial != null)
            {
                rgbaMaterial.mainTextureScale = new Vector2(1f, -1f);
                rgbaMaterial.mainTextureOffset = new Vector2(0f, 1f);
            }
            if (yuvMaterial != null)
            {
                yuvMaterial.SetFloat("_FlipU", 0f);
                yuvMaterial.SetFloat("_FlipV", 1f);
            }
        }

        private void ShowTestTexture()
        {
            if (rgbaMaterial == null) return;
            Texture2D tex = customTestTexture != null ? customTestTexture : GenerateCheckerTexture();
            rgbaMaterial.mainTexture = tex;
            screenRenderer.sharedMaterial = rgbaMaterial;
        }

        private void HandleTextureUpdated()
        {
            if (useTestTexture) return; // ignore real frames while validating the test pattern

            if (decoder.IsYuvNv12)
            {
                if (yuvMaterial == null) return;
                screenRenderer.sharedMaterial = yuvMaterial;
                yuvMaterial.SetTexture("_YTex", decoder.YPlane);
                yuvMaterial.SetTexture("_UVTex", decoder.UVPlane);
                yuvMaterial.SetFloat("_SwapUV", decoder.UvChannelsSwapped ? 1f : 0f);
            }
            else
            {
                if (rgbaMaterial == null) return;
                screenRenderer.sharedMaterial = rgbaMaterial;
                if (rgbaMaterial.mainTexture != decoder.VideoTexture)
                    rgbaMaterial.mainTexture = decoder.VideoTexture;
            }
        }

        /// <summary>
        /// Returns a still frame of what's currently on screen, as a readable
        /// Texture2D ready for EncodeToPNG. Handles both decoder output paths:
        /// - Direct RGBA/BGRA: the decoder's texture is already a plain,
        ///   readable Texture2D, so it's returned as-is.
        /// - YUV NV12 (the path PopH264 actually takes on Quest hardware):
        ///   decoder.VideoTexture is never populated in this case, so this
        ///   blits _YTex/_UVTex through the same yuvMaterial/shader used for
        ///   on-screen display into a temporary RenderTexture, then reads
        ///   that back into a Texture2D. Costs one GPU blit + one readback,
        ///   only when a photo is actually taken - not every frame.
        /// Returns null if no frame has been decoded yet or the required
        /// material is missing.
        /// </summary>
        public Texture2D CaptureSnapshot()
        {
            if (decoder == null) return null;

            if (!decoder.IsYuvNv12)
                return decoder.VideoTexture;

            if (yuvMaterial == null || decoder.YPlane == null || decoder.UVPlane == null) return null;

            int width = decoder.YPlane.width;
            int height = decoder.YPlane.height;

            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(null, rt, yuvMaterial);
                RenderTexture.active = rt;

                var snapshot = new Texture2D(width, height, TextureFormat.RGB24, false);
                snapshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                snapshot.Apply();
                return snapshot;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private void BuildQuad()
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "TelloVideoQuad";
            Destroy(quad.GetComponent<Collider>()); // no physics needed on a video screen

            quadTransform = quad.transform;
            quadTransform.SetParent(transform, false);
            quadTransform.localPosition = Vector3.zero;
            quadTransform.localRotation = Quaternion.identity; // orientation is fixed via UV, not geometry - see ApplyOrientation()
            // Scale is applied by ApplyZoomScale() right after this call returns (Awake) -
            // not set here, so there's a single source of truth for quad size.

            screenRenderer = quad.GetComponent<MeshRenderer>();
        }

        /// <summary>8x8 black/white checker - lets you confirm the quad isn't stretched or mis-scaled at a glance.</summary>
        private static Texture2D GenerateCheckerTexture()
        {
            const int size = 256, cells = 8;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false) { filterMode = FilterMode.Point };
            int cellSize = size / cells;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool white = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                    tex.SetPixel(x, y, white ? Color.white : Color.black);
                }
            }
            tex.Apply();
            return tex;
        }

        [Header("=== FIXED PLACEMENT ===")]
        [Tooltip("Fixed height above the real floor (requires Tracking Origin Type = Floor Level in OVR Manager). Used instead of the camera's Y position for stability.")]
        [SerializeField] private float assumedEyeHeightMeters = 1.6f;
        [Tooltip("If true, this component does NOT position itself in Start() - an external controller (e.g. TelloInitGate) calls RevealAt() instead, once it's ready to hand off. Prevents the self-positioning logic from running (and overwriting) before/after the external reveal.")]
        [SerializeField] private bool positionedExternally = false;

        private void Start()
        {
            if (positionedExternally) return; // an external controller positions this instead - see RevealAt()
            if (vrCamera == null) return;

            transform.position = TelloUiKit.ComputeFixedPosition(vrCamera, distanceFromCamera, assumedEyeHeightMeters, verticalOffset);
            transform.rotation = TelloUiKit.ComputeFixedRotation(vrCamera);
        }

        /// <summary>
        /// Called by an external controller (TelloInitGate) once it's ready to hand
        /// off. Computes its OWN position from its own distanceFromCamera/
        /// verticalOffset/assumedEyeHeightMeters (all settings-adjustable) rather
        /// than trusting the position the caller passes in - a previous version
        /// just snapped to whatever transform TelloInitGate happened to be at,
        /// which meant the Settings screen's "screen distance" slider silently had
        /// no effect (it was changing a field nothing ever read). rotation is still
        /// taken from the caller as a fallback for the rare case vrCamera isn't
        /// assigned here. Plays a short scale-in "pop" instead of a material alpha
        /// fade (keeps things simple/robust, no shader changes needed).
        /// </summary>
        public void RevealAt(Vector3 fallbackPosition, Quaternion fallbackRotation)
        {
            if (vrCamera != null)
            {
                transform.position = TelloUiKit.ComputeFixedPosition(vrCamera, distanceFromCamera, assumedEyeHeightMeters, verticalOffset);
                transform.rotation = TelloUiKit.ComputeFixedRotation(vrCamera);
            }
            else
            {
                transform.position = fallbackPosition;
                transform.rotation = fallbackRotation;
            }
            StopAllCoroutines();
            StartCoroutine(PopIn());
        }

        private System.Collections.IEnumerator PopIn()
        {
            Vector3 targetScale = quadTransform.localScale; // already the correct zoomed size, set in Awake()
            float duration = 0.35f;
            float elapsed = 0f;
            quadTransform.localScale = Vector3.zero;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
                quadTransform.localScale = targetScale * t;
                yield return null;
            }
            quadTransform.localScale = targetScale;
        }
    }
}
