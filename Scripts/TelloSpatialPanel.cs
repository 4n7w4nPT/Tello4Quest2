using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Single band to the LEFT of the video screen, replacing the two separate
    /// TelloAccelGaugePanel/TelloHomeDirectionPanel floating panels. Combines both
    /// instruments (G-meter on top, home-direction arrow on bottom) in one tall
    /// band whose height always equals the video screen's own height, and whose
    /// horizontal gap to the screen matches TelloStatusPanel/TelloOptionsPanel's
    /// convention exactly.
    ///
    /// The old panels used a fixed absolute world size (e.g. "0.35m"), completely
    /// independent of how big the video screen was - fine when the screen only
    /// ever ranged up to 2x, but once the Settings screen's size/distance sliders
    /// were widened to go much further, that fixed size started looking
    /// "ridiculously small" next to a much bigger screen. This band instead scales
    /// proportionally with the screen, exactly like the top/bottom banners already
    /// did - see PositionLeftOfScreen below.
    /// </summary>
    public class TelloSpatialPanel : MonoBehaviour
    {
        [SerializeField] private TelloConnection tello;
        [Tooltip("The video screen - used both as the parent to follow and to read its exact width/height.")]
        [SerializeField] private TelloVideoDisplay videoScreen;

        [Header("=== PANEL SHAPE ===")]
        [SerializeField] private float cardCornerRadiusPx = 14f;
        [Tooltip("Horizontal gap between this panel and the video screen, in world units - same convention as TelloStatusPanel/TelloOptionsPanel's gap.")]
        [SerializeField] private float gap = 0.01f;
        [Tooltip("If true, this component does NOT position/show itself in Start() - an external controller (e.g. TelloInitGate) calls RevealNow() instead.")]
        [SerializeField] private bool positionedExternally = false;

        [Header("=== GAUGE SCALE ===")]
        [Tooltip("Raw agx/agy value (Tello telemetry units) that maps to full deflection (edge of the gauge). Tune from observed flight data - this is not a calibrated g-force reading, just a relative indicator.")]
        [SerializeField] private float maxDisplayAcceleration = 400f;
        [Tooltip("How often a new trail sample is taken, in seconds.")]
        [SerializeField] private float trailSampleInterval = 0.09f;
        [Tooltip("How many past samples the trail keeps (trailSampleInterval * this = trail duration).")]
        [SerializeField] private int trailLength = 8;

        // Fixed internal resolution - the band's WORLD width/height always come out
        // to (CanvasPixelWidth * scale) x (CanvasPixelHeight * scale), and scale is
        // chosen (see PositionLeftOfScreen) so CanvasPixelHeight * scale == the video
        // screen's own QuadHeight. These two constants just set the internal aspect
        // ratio the two instruments are laid out against.
        private const float CanvasPixelWidth = 260f;
        private const float CanvasPixelHeight = 640f;
        private const float GaugeRadiusPx = 95f;
        private const float DialRadiusPx = 95f;
        private const float DotRadiusPx = 13f;

        private static readonly Color PanelBackground = new Color(0.11f, 0.11f, 0.11f, 0.92f);
        private static readonly Color InstrumentBackground = new Color(0.20f, 0.20f, 0.20f, 1f);
        private static readonly Color DotColor = new Color(0.95f, 0.75f, 0.25f);
        private static readonly Color ArrowColor = new Color(0.4f, 0.75f, 0.95f);
        private static readonly Color PanelEdge = new Color(0.15f, 0.17f, 0.19f, 1f);

        private Sprite roundedSprite;
        private Sprite circleSprite;
        private CanvasGroup canvasGroup;
        private RectTransform gaugeArea;
        private Image[] trailDots;
        private readonly System.Collections.Generic.List<Vector2> samples = new System.Collections.Generic.List<Vector2>();
        private float sampleTimer;

        private RectTransform arrowTransform;
        private TextMeshProUGUI distanceText;

        // =================================================================
        // RUNTIME-ADJUSTABLE SETTINGS (Settings screen)
        // trailSampleInterval/trailLength are deliberately NOT exposed here -
        // they size a fixed array of dot GameObjects at BuildUI() time, so
        // changing them after Awake would need a full UI rebuild. Adjust those
        // two in the Inspector only.
        // =================================================================
        public float MaxDisplayAcceleration { get => maxDisplayAcceleration; set => maxDisplayAcceleration = value; }
        public float Gap { get => gap; set => gap = value; }

        private const string PrefsPrefix = "TelloQuest_Settings_Spatial_";

        private void LoadPersistedSettings()
        {
            maxDisplayAcceleration = PlayerPrefs.GetFloat(PrefsPrefix + "MaxAccel", maxDisplayAcceleration);
            gap = PlayerPrefs.GetFloat(PrefsPrefix + "Gap", gap);
        }

        /// <summary>Called by TelloSettingsScreen after writing new values via the properties above, to persist them for next launch.</summary>
        public void SavePersistedSettings()
        {
            PlayerPrefs.SetFloat(PrefsPrefix + "MaxAccel", maxDisplayAcceleration);
            PlayerPrefs.SetFloat(PrefsPrefix + "Gap", gap);
        }

        private void Awake()
        {
            if (tello == null) tello = TelloConnection.Instance;
            LoadPersistedSettings();
            roundedSprite = TelloUiKit.GetRoundedSprite(cardCornerRadiusPx);
            circleSprite = TelloUiKit.GetRoundedSprite(10000f); // deliberately huge - clamps to a circle inside GetRoundedSprite
            BuildUI();
        }

        private void Start()
        {
            if (positionedExternally) return;
            PositionLeftOfScreen();
        }

        /// <summary>Called by an external controller (TelloInitGate) once the video screen is ready - positions this panel then fades it in.</summary>
        public void RevealNow()
        {
            PositionLeftOfScreen();
            if (canvasGroup != null) StartCoroutine(FadeIn());
        }

        private System.Collections.IEnumerator FadeIn()
        {
            float duration = 0.35f;
            float elapsed = 0f;
            canvasGroup.alpha = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                canvasGroup.alpha = Mathf.Clamp01(elapsed / duration);
                yield return null;
            }
            canvasGroup.alpha = 1f;
        }

        private void OnEnable()
        {
            if (videoScreen != null) videoScreen.OnSizeChanged += PositionLeftOfScreen;
        }

        private void OnDisable()
        {
            if (videoScreen != null) videoScreen.OnSizeChanged -= PositionLeftOfScreen;
        }

        /// <summary>Same formula pattern as TelloStatusPanel.PositionAboveScreen, just
        /// rotated 90 degrees: this band's world HEIGHT is pinned to the screen's
        /// own QuadHeight (scale is derived from that), and it sits at that same
        /// scale to the LEFT of the screen with the standard gap - exactly like the
        /// top/bottom banners already do relative to QuadWidth.</summary>
        private void PositionLeftOfScreen()
        {
            if (videoScreen == null)
            {
                Debug.LogWarning("[TelloSpatialPanel] Video Screen not assigned - can't compute position, staying at default transform.");
                return;
            }

            transform.SetParent(videoScreen.transform, false);

            float scale = videoScreen.QuadHeight / CanvasPixelHeight;
            transform.localScale = Vector3.one * scale;

            float bandWorldWidth = CanvasPixelWidth * scale;
            float x = -(videoScreen.QuadWidth * 0.5f + gap + bandWorldWidth * 0.5f);
            transform.localPosition = new Vector3(x, 0f, 0f);
            transform.localRotation = Quaternion.identity;
        }

        // =================================================================
        // UI CONSTRUCTION
        // =================================================================
        private void BuildUI()
        {
            var canvasGO = new GameObject("TelloSpatialCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(CanvasPixelWidth, CanvasPixelHeight);
            canvasGO.transform.localPosition = Vector3.zero;

            canvasGroup = canvasGO.AddComponent<CanvasGroup>();
            canvasGroup.alpha = positionedExternally ? 0f : 1f;

            TelloUiKit.BuildFullRectBackground(canvasGO.transform, roundedSprite, PanelBackground);

            // ---- Top half: accel gauge ----
            BuildLabel(canvasGO.transform, "Accel", 280f);

            var gaugeGO = new GameObject("GaugeCircle", typeof(RectTransform), typeof(Image));
            gaugeGO.transform.SetParent(canvasGO.transform, false);
            gaugeArea = gaugeGO.GetComponent<RectTransform>();
            gaugeArea.sizeDelta = new Vector2(GaugeRadiusPx * 2f, GaugeRadiusPx * 2f);
            gaugeArea.anchoredPosition = new Vector2(0f, 155f);
            Image gaugeImage = gaugeGO.GetComponent<Image>();
            gaugeImage.sprite = circleSprite;
            gaugeImage.type = Image.Type.Sliced;
            gaugeImage.color = InstrumentBackground;

            trailDots = new Image[trailLength];
            for (int i = 0; i < trailLength; i++)
            {
                var dotGO = new GameObject($"TrailDot{i}", typeof(RectTransform), typeof(Image));
                dotGO.transform.SetParent(gaugeArea, false);
                RectTransform dotRect = dotGO.GetComponent<RectTransform>();
                float t = (i + 1) / (float)trailLength;
                float size = Mathf.Lerp(DotRadiusPx * 0.5f, DotRadiusPx, t);
                dotRect.sizeDelta = new Vector2(size, size);
                Image dotImage = dotGO.GetComponent<Image>();
                dotImage.sprite = circleSprite;
                dotImage.color = new Color(DotColor.r, DotColor.g, DotColor.b, Mathf.Lerp(0.15f, 1f, t));
                dotImage.enabled = false;
                trailDots[i] = dotImage;
            }

            // ---- Divider ----
            var dividerGO = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            dividerGO.transform.SetParent(canvasGO.transform, false);
            RectTransform dividerRect = dividerGO.GetComponent<RectTransform>();
            dividerRect.sizeDelta = new Vector2(CanvasPixelWidth - 40f, 1f);
            dividerRect.anchoredPosition = new Vector2(0f, 0f);
            dividerGO.GetComponent<Image>().color = PanelEdge;

            // ---- Bottom half: home direction ----
            BuildLabel(canvasGO.transform, "Home direction", -40f);

            var dialGO = new GameObject("DialCircle", typeof(RectTransform), typeof(Image));
            dialGO.transform.SetParent(canvasGO.transform, false);
            RectTransform dialRect = dialGO.GetComponent<RectTransform>();
            dialRect.sizeDelta = new Vector2(DialRadiusPx * 2f, DialRadiusPx * 2f);
            dialRect.anchoredPosition = new Vector2(0f, -195f);
            Image dialImage = dialGO.GetComponent<Image>();
            dialImage.sprite = circleSprite;
            dialImage.type = Image.Type.Sliced;
            dialImage.color = InstrumentBackground;

            var arrowGO = new GameObject("Arrow", typeof(RectTransform));
            arrowGO.transform.SetParent(dialRect, false);
            arrowTransform = arrowGO.GetComponent<RectTransform>();
            arrowTransform.sizeDelta = new Vector2(70f, 70f);
            arrowTransform.anchoredPosition = Vector2.zero;
            var arrowText = arrowGO.AddComponent<TextMeshProUGUI>();
            arrowText.text = "\u25B2"; // "▲"
            arrowText.fontSize = 56f;
            arrowText.color = ArrowColor;
            arrowText.alignment = TextAlignmentOptions.Center;

            var distanceGO = new GameObject("Distance", typeof(RectTransform));
            distanceGO.transform.SetParent(canvasGO.transform, false);
            RectTransform distanceRect = distanceGO.GetComponent<RectTransform>();
            distanceRect.sizeDelta = new Vector2(CanvasPixelWidth - 20f, 24f);
            distanceRect.anchoredPosition = new Vector2(0f, -320f);
            distanceText = distanceGO.AddComponent<TextMeshProUGUI>();
            distanceText.fontSize = 13f;
            distanceText.color = Color.white;
            distanceText.alignment = TextAlignmentOptions.Center;
            distanceText.text = "--";
        }

        private void BuildLabel(Transform parent, string text, float y)
        {
            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(parent, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(CanvasPixelWidth - 20f, 24f);
            labelRect.anchoredPosition = new Vector2(0f, y);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.fontSize = 14f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.text = text;
        }

        // =================================================================
        // LIVE UPDATE
        // =================================================================
        private void Update()
        {
            if (tello == null) return;

            // Accel gauge trail
            sampleTimer += Time.deltaTime;
            if (sampleTimer >= trailSampleInterval)
            {
                sampleTimer = 0f;
                samples.Add(new Vector2(tello.AccelerationX, tello.AccelerationY));
                while (samples.Count > trailLength) samples.RemoveAt(0);
            }

            for (int i = 0; i < trailDots.Length; i++)
            {
                int sampleIndex = samples.Count - trailDots.Length + i;
                if (sampleIndex < 0)
                {
                    trailDots[i].enabled = false;
                    continue;
                }

                trailDots[i].enabled = true;
                Vector2 accel = samples[sampleIndex];
                Vector2 offset = Vector2.ClampMagnitude(accel * (GaugeRadiusPx / maxDisplayAcceleration), GaugeRadiusPx - DotRadiusPx * 0.5f);
                trailDots[i].rectTransform.anchoredPosition = offset;
            }

            // Home direction
            float relativeBearing = Wrap180(tello.BearingToHomeDegrees - tello.Yaw);
            arrowTransform.localRotation = Quaternion.Euler(0f, 0f, -relativeBearing);

            float distanceM = tello.DistanceFromHomeCm / 100f;
            distanceText.text = $"{distanceM:F1}m";
        }

        private static float Wrap180(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            if (degrees < -180f) degrees += 360f;
            return degrees;
        }
    }
}
