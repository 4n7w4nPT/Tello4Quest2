using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Band to the LEFT of the video screen. v0.4: replaced the G-meter +
    /// home-direction instruments (rarely looked at once the mini-map existed,
    /// per direct pilot feedback) with four live time-series graphs, 2x2:
    ///
    ///   Top-left:     video FPS (quality of the decoded feed)
    ///   Top-right:    battery % (fixed 0-100 axis)
    ///   Bottom-left:  altitude (axis max = the Settings-configured altitude ceiling)
    ///   Bottom-right: temperature (axis max = the Settings-configured critical threshold)
    ///
    /// All four share the same X axis behavior: a fixed 2-minute window that
    /// fills up first, then - once flight time exceeds that - the window
    /// smoothly widens to keep showing the whole flight instead of scrolling,
    /// exactly like the mini-map's zoom (which never shrinks mid-flight either).
    /// Sampled once a second - cheap; redrawn every frame so the window's smooth
    /// widening animates properly, same relationship as the mini-map's sampling
    /// (occasional) vs. its per-frame redraw (continuous).
    ///
    /// The whole panel sits at a 20-degree "cockpit" angle toward the pilot
    /// (rotated around the vertical axis) rather than flat/parallel to the video
    /// screen, so it reads more easily with a glance/head-turn - the reason a
    /// literal ship's-console side panel is angled toward the seat, not flat
    /// against the hull.
    ///
    /// Graph history persists for the whole app session (not reset per-flight,
    /// unlike the mini-map's trail) - a graph that cleared itself on every
    /// landing would lose the ability to compare across flights within the
    /// same session, which is the more useful behavior here.
    /// </summary>
    public class TelloSpatialPanel : MonoBehaviour
    {
        [SerializeField] private TelloConnection tello;
        [Tooltip("Used only to compute the FPS graph - reads FramesDecodedTotal directly.")]
        [SerializeField] private TelloVideoDecoder videoDecoder;
        [Tooltip("The video screen - used both as the parent to follow and to read its exact height.")]
        [SerializeField] private TelloVideoDisplay videoScreen;

        [Header("=== PANEL SHAPE ===")]
        [SerializeField] private float cardCornerRadiusPx = 14f;
        [Tooltip("Horizontal gap between this panel and the video screen, in world units - same convention as every other banner's gap.")]
        [SerializeField] private float gap = 0.01f;
        [Tooltip("Rotation around the vertical axis, angling the panel toward the pilot like a cockpit side console rather than sitting flat/parallel to the video screen.")]
        [SerializeField] private float cockpitAngleDegrees = 20f;
        [SerializeField] private bool positionedExternally = false;

        [Header("=== GRAPH TIMING ===")]
        [Tooltip("How often a new point is sampled, in seconds.")]
        [SerializeField] private float sampleIntervalSeconds = 1f;
        [Tooltip("The X axis starts at this many seconds wide and only ever grows (never shrinks mid-flight) once flight time exceeds it - same non-shrinking-zoom idea as the mini-map.")]
        [SerializeField] private float initialWindowSeconds = 120f;
        [Tooltip("How quickly the X axis eases toward its target width - higher = faster.")]
        [SerializeField] private float windowSmoothing = 3f;
        [Tooltip("Fixed Y axis max for the FPS graph - set above the Tello's normal ~30fps so a healthy signal sits near the top with headroom, rather than pinned right at the ceiling.")]
        [SerializeField] private float fpsGraphMax = 40f;

        // Same internal-resolution convention as before (world HEIGHT is always
        // pinned to the video screen's own height via the scale in
        // PositionLeftOfScreen) - width just grew from 260 to fit a 2x2 grid
        // instead of one stacked column.
        private const float CanvasPixelWidth = 560f;
        private const float CanvasPixelHeight = 640f;
        private const float PlotWidth = 220f;
        private const float PlotHeight = 160f;

        private static readonly Color PanelBackground = new Color(0.11f, 0.11f, 0.11f, 0.92f);
        private static readonly Color InstrumentBackground = new Color(0.20f, 0.20f, 0.20f, 1f);
        private static readonly Color PanelEdge = new Color(0.15f, 0.17f, 0.19f, 1f);
        private static readonly Color FpsColor = new Color(0.4f, 0.75f, 0.95f);
        private static readonly Color BatteryColor = new Color(0.4f, 0.85f, 0.5f);
        private static readonly Color AltitudeColor = new Color(0.85f, 0.75f, 0.3f);
        private static readonly Color TemperatureColor = new Color(0.91f, 0.45f, 0.3f);

        private Sprite roundedSprite;
        private Sprite circleSprite;
        private CanvasGroup canvasGroup;

        private class TimeSeriesGraph
        {
            public RectTransform plotArea;
            public TextMeshProUGUI valueText;
            public float yMax;
            public string valueFormat;
            public Color color;
            public float currentWindowSeconds;
            public readonly List<float> samples = new List<float>();
            public readonly List<Image> linePool = new List<Image>();
        }

        private TimeSeriesGraph fpsGraph;
        private TimeSeriesGraph batteryGraph;
        private TimeSeriesGraph altitudeGraph;
        private TimeSeriesGraph temperatureGraph;

        private float sampleTimer;
        private long lastFramesDecoded;
        private float lastFpsSampleTime;

        // =================================================================
        // RUNTIME-ADJUSTABLE SETTINGS (Settings screen)
        // =================================================================
        public float Gap { get => gap; set => gap = value; }

        private const string PrefsPrefix = "TelloQuest_Settings_Spatial_";

        private void LoadPersistedSettings()
        {
            gap = PlayerPrefs.GetFloat(PrefsPrefix + "Gap", gap);
        }

        /// <summary>Called by TelloSettingsScreen after writing new values via the properties above, to persist them for next launch.</summary>
        public void SavePersistedSettings()
        {
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

        /// <summary>Same formula pattern as TelloStatusPanel/TelloOptionsPanel - world
        /// height pinned to the screen's QuadHeight, positioned to the LEFT with the
        /// standard gap, then angled toward the pilot around the vertical axis.</summary>
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
            // Negated - confirmed in a real headset test that a positive angle here
            // turned the panel AWAY from the pilot rather than toward them. Positive
            // values in the Inspector field still mean "toward the pilot" - only the
            // sign applied to the actual rotation flipped.
            transform.localRotation = Quaternion.Euler(0f, -cockpitAngleDegrees, 0f);
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

            // Center cross-dividers, matching the visual language already used
            // elsewhere (thin hairlines) rather than introducing a new motif.
            var hDividerGO = new GameObject("HDivider", typeof(RectTransform), typeof(Image));
            hDividerGO.transform.SetParent(canvasGO.transform, false);
            RectTransform hDividerRect = hDividerGO.GetComponent<RectTransform>();
            hDividerRect.sizeDelta = new Vector2(CanvasPixelWidth - 20f, 1f);
            hDividerRect.anchoredPosition = Vector2.zero;
            hDividerGO.GetComponent<Image>().color = PanelEdge;

            var vDividerGO = new GameObject("VDivider", typeof(RectTransform), typeof(Image));
            vDividerGO.transform.SetParent(canvasGO.transform, false);
            RectTransform vDividerRect = vDividerGO.GetComponent<RectTransform>();
            vDividerRect.sizeDelta = new Vector2(1f, CanvasPixelHeight - 20f);
            vDividerRect.anchoredPosition = Vector2.zero;
            vDividerGO.GetComponent<Image>().color = PanelEdge;

            fpsGraph = BuildGraphQuadrant(canvasGO.transform, "FPS", new Vector2(-140f, 160f), fpsGraphMax, "{0:F0}", FpsColor);
            batteryGraph = BuildGraphQuadrant(canvasGO.transform, "Battery", new Vector2(140f, 160f), 100f, "{0:F0}%", BatteryColor);
            altitudeGraph = BuildGraphQuadrant(canvasGO.transform, "Altitude", new Vector2(-140f, -160f), 3f, "{0:F1}m", AltitudeColor);
            temperatureGraph = BuildGraphQuadrant(canvasGO.transform, "Temperature", new Vector2(140f, -160f), 90f, "{0:F0}\u00B0C", TemperatureColor);
        }

        private TimeSeriesGraph BuildGraphQuadrant(Transform parent, string title, Vector2 center, float yMax, string valueFormat, Color color)
        {
            var labelGO = new GameObject($"Label_{title}", typeof(RectTransform));
            labelGO.transform.SetParent(parent, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(PlotWidth, 24f);
            labelRect.anchoredPosition = center + new Vector2(0f, PlotHeight * 0.5f + 16f);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.fontSize = 13f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.text = title;

            var valueGO = new GameObject($"Value_{title}", typeof(RectTransform));
            valueGO.transform.SetParent(parent, false);
            RectTransform valueRect = valueGO.GetComponent<RectTransform>();
            valueRect.sizeDelta = new Vector2(80f, 20f);
            valueRect.anchoredPosition = center + new Vector2(PlotWidth * 0.5f - 40f, PlotHeight * 0.5f - 6f);
            var valueText = valueGO.AddComponent<TextMeshProUGUI>();
            valueText.fontSize = 12f;
            valueText.color = color;
            valueText.alignment = TextAlignmentOptions.MidlineRight;
            valueText.text = "--";

            var bgGO = new GameObject($"PlotBg_{title}", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(parent, false);
            RectTransform bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.sizeDelta = new Vector2(PlotWidth, PlotHeight);
            bgRect.anchoredPosition = center;
            bgGO.GetComponent<Image>().color = InstrumentBackground;

            var plotGO = new GameObject($"Plot_{title}", typeof(RectTransform));
            plotGO.transform.SetParent(parent, false);
            RectTransform plotRect = plotGO.GetComponent<RectTransform>();
            plotRect.sizeDelta = new Vector2(PlotWidth, PlotHeight);
            plotRect.anchoredPosition = center;

            return new TimeSeriesGraph
            {
                plotArea = plotRect,
                valueText = valueText,
                yMax = yMax,
                valueFormat = valueFormat,
                color = color
            };
        }

        // =================================================================
        // LIVE UPDATE
        // =================================================================
        private void Update()
        {
            if (tello == null) return;

            sampleTimer += Time.deltaTime;
            if (sampleTimer >= sampleIntervalSeconds)
            {
                sampleTimer = 0f;
                SampleAll();
            }

            // Redrawn every frame (not just at sample ticks) so the window's
            // smooth widening animates properly - same relationship as the
            // mini-map's occasional sampling vs. its per-frame redraw.
            RedrawGraph(fpsGraph);
            RedrawGraph(batteryGraph);
            RedrawGraph(altitudeGraph);
            RedrawGraph(temperatureGraph);
        }

        private void SampleAll()
        {
            AddSample(fpsGraph, ComputeFps());
            AddSample(batteryGraph, tello.Battery);
            AddSample(altitudeGraph, tello.HeightM);
            AddSample(temperatureGraph, tello.TemperatureHigh);

            // Axis maxes for altitude/temperature follow the Settings-configured
            // limits directly, so a mid-flight change in Settings is reflected
            // immediately rather than needing a restart.
            altitudeGraph.yMax = Mathf.Max(0.1f, tello.MaxHeightCm / 100f);
            temperatureGraph.yMax = Mathf.Max(1f, tello.TemperatureCriticalThreshold);
        }

        private float ComputeFps()
        {
            if (videoDecoder == null) return 0f;
            float dt = Time.time - lastFpsSampleTime;
            if (dt <= 0f) return 0f;
            long delta = videoDecoder.FramesDecodedTotal - lastFramesDecoded;
            lastFramesDecoded = videoDecoder.FramesDecodedTotal;
            lastFpsSampleTime = Time.time;
            return delta / dt;
        }

        private static void AddSample(TimeSeriesGraph graph, float value)
        {
            graph.samples.Add(value);
        }

        private void RedrawGraph(TimeSeriesGraph graph)
        {
            float elapsedSeconds = graph.samples.Count * sampleIntervalSeconds;
            float targetWindow = Mathf.Max(initialWindowSeconds, elapsedSeconds);
            graph.currentWindowSeconds = graph.currentWindowSeconds <= 0f
                ? targetWindow
                : Mathf.Lerp(graph.currentWindowSeconds, targetWindow, Time.deltaTime * windowSmoothing);

            Vector2 PointPos(int index)
            {
                float t = index * sampleIntervalSeconds;
                float x = (t / graph.currentWindowSeconds) * PlotWidth - PlotWidth * 0.5f;
                float normalizedValue = graph.yMax > 0f ? Mathf.Clamp01(graph.samples[index] / graph.yMax) : 0f;
                float y = normalizedValue * PlotHeight - PlotHeight * 0.5f;
                return new Vector2(x, y);
            }

            for (int i = 0; i < graph.samples.Count - 1; i++)
            {
                if (i >= graph.linePool.Count)
                {
                    var lineGO = new GameObject($"Line{i}", typeof(RectTransform), typeof(Image));
                    lineGO.transform.SetParent(graph.plotArea, false);
                    Image lineImage = lineGO.GetComponent<Image>();
                    lineImage.color = graph.color;
                    graph.linePool.Add(lineImage);
                }

                Vector2 p0 = PointPos(i);
                Vector2 p1 = PointPos(i + 1);
                Vector2 mid = (p0 + p1) * 0.5f;
                float length = Vector2.Distance(p0, p1);
                float angle = Mathf.Atan2(p1.y - p0.y, p1.x - p0.x) * Mathf.Rad2Deg;

                RectTransform segRect = graph.linePool[i].rectTransform;
                segRect.sizeDelta = new Vector2(length, 2f);
                segRect.anchoredPosition = mid;
                segRect.localRotation = Quaternion.Euler(0f, 0f, angle);
                graph.linePool[i].enabled = true;
            }
            for (int i = Mathf.Max(0, graph.samples.Count - 1); i < graph.linePool.Count; i++) graph.linePool[i].enabled = false;

            if (graph.samples.Count > 0)
            {
                graph.valueText.text = string.Format(graph.valueFormat, graph.samples[graph.samples.Count - 1]);
            }
        }
    }
}
