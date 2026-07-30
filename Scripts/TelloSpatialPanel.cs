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
        [Tooltip("Fait pivoter le panneau autour de son BORD INTERNE (celui qui longe l'ecran video) au lieu de son centre, de sorte que ce bord reste exactement dans le plan de l'ecran. Sans ca, l'inclinaison cockpit envoie toute la moitie interne derriere l'ecran, qui la masque.")]
        [SerializeField] private bool pinInnerEdgeToScreenDepth = true;
        [Tooltip("Decalage de profondeur supplementaire du panneau entier, en Z local de l'ecran video. 0 laisse le bord interne pile dans le plan de l'ecran. Si un reglage non nul deplace le panneau du mauvais cote, inverse simplement le signe.")]
        [SerializeField] private float panelDepthOffset = 0f;
        [SerializeField] private bool positionedExternally = false;

        [Header("=== GRAPH TIMING ===")]
        [Tooltip("How often a new point is sampled, in seconds.")]
        [SerializeField] private float sampleIntervalSeconds = 1f;
        [Tooltip("The X axis starts at this many seconds wide and only ever grows (never shrinks mid-flight) once flight time exceeds it - same non-shrinking-zoom idea as the mini-map.")]
        [SerializeField] private float initialWindowSeconds = 60f;
        [Tooltip("How quickly the X axis eases toward its target width - higher = faster.")]
        [SerializeField] private float windowSmoothing = 3f;
        [Tooltip("Nombre MAXIMUM de points conserves par graphe. Au-dela, la serie est decimee par MOYENNE de paires (et non en jetant un point sur deux) et l'intervalle represente par un point double : le graphe continue de couvrir tout le vol, mais son cout de redessin reste borne. A 1 point/seconde, la decimation ne se declenche donc qu'apres 10 minutes.")]
        [SerializeField] private int maxSamplesPerGraph = 600;
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

            /// <summary>Duree representee par un point. Double a chaque decimation, pour
            /// que l'axe des abscisses reste juste quand la serie est compressee.</summary>
            public float effectiveInterval;

            /// <summary>Le redessin ne se fait plus a chaque frame : uniquement quand un
            /// point est ajoute, ou tant que l'animation de largeur de fenetre n'a pas
            /// converge.</summary>
            public bool needsRedraw = true;

            // Graduations d'axes
            public TextMeshProUGUI yMaxLabel;
            public TextMeshProUGUI yMidLabel;
            public TextMeshProUGUI xLabel;
            public string lastYMaxText;
            public string lastXText;
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
        public float Gap { get => gap; set { gap = value; PositionLeftOfScreen(); } }
        public float CockpitAngleDegrees { get => cockpitAngleDegrees; set { cockpitAngleDegrees = value; PositionLeftOfScreen(); } }
        public bool PinInnerEdgeToScreenDepth { get => pinInnerEdgeToScreenDepth; set { pinInnerEdgeToScreenDepth = value; PositionLeftOfScreen(); } }
        public float PanelDepthOffset { get => panelDepthOffset; set { panelDepthOffset = value; PositionLeftOfScreen(); } }
        public float GraphWindowSeconds { get => initialWindowSeconds; set => initialWindowSeconds = Mathf.Max(10f, value); }
        public float GraphSampleIntervalSeconds { get => sampleIntervalSeconds; set => sampleIntervalSeconds = Mathf.Clamp(value, 0.2f, 10f); }

        private const string PrefsPrefix = "TelloQuest_Settings_Spatial_";

        private void LoadPersistedSettings()
        {
            gap = PlayerPrefs.GetFloat(PrefsPrefix + "Gap", gap);
            cockpitAngleDegrees = PlayerPrefs.GetFloat(PrefsPrefix + "CockpitAngle", cockpitAngleDegrees);
            pinInnerEdgeToScreenDepth = PlayerPrefs.GetInt(PrefsPrefix + "PinInnerEdge", pinInnerEdgeToScreenDepth ? 1 : 0) == 1;
            panelDepthOffset = PlayerPrefs.GetFloat(PrefsPrefix + "PanelDepth", panelDepthOffset);
            initialWindowSeconds = PlayerPrefs.GetFloat(PrefsPrefix + "GraphWindow", initialWindowSeconds);
            sampleIntervalSeconds = PlayerPrefs.GetFloat(PrefsPrefix + "GraphInterval", sampleIntervalSeconds);
        }

        /// <summary>Called by TelloSettingsScreen after writing new values via the properties above, to persist them for next launch.</summary>
        public void SavePersistedSettings()
        {
            PlayerPrefs.SetFloat(PrefsPrefix + "Gap", gap);
            PlayerPrefs.SetFloat(PrefsPrefix + "CockpitAngle", cockpitAngleDegrees);
            PlayerPrefs.SetInt(PrefsPrefix + "PinInnerEdge", pinInnerEdgeToScreenDepth ? 1 : 0);
            PlayerPrefs.SetFloat(PrefsPrefix + "PanelDepth", panelDepthOffset);
            PlayerPrefs.SetFloat(PrefsPrefix + "GraphWindow", initialWindowSeconds);
            PlayerPrefs.SetFloat(PrefsPrefix + "GraphInterval", sampleIntervalSeconds);
            PlayerPrefs.Save();
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

            float halfBandWidth = CanvasPixelWidth * scale * 0.5f;

            // Negated - confirmed in a real headset test that a positive angle here
            // turned the panel AWAY from the pilot rather than toward them. Positive
            // values in the Inspector field still mean "toward the pilot" - only the
            // sign applied to the actual rotation flipped.
            Quaternion rotation = Quaternion.Euler(0f, -cockpitAngleDegrees, 0f);
            transform.localRotation = rotation;

            // Bord interne du panneau GAUCHE = son bord droit, donc +halfBandWidth dans
            // le repere local du panneau. Voir TelloUiKit.SolvePinnedPanelPosition pour
            // le detail de la geometrie.
            transform.localPosition = TelloUiKit.SolvePinnedPanelPosition(
                rotation,
                new Vector3(halfBandWidth, 0f, 0f),
                videoScreen.QuadWidth * 0.5f + gap,
                halfBandWidth,
                pinInnerEdgeToScreenDepth,
                panelDepthOffset);
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

            // ---- Axes ----
            // Sans reperes, une courbe normalisee entre 0 et yMax ne dit rien de la
            // valeur reelle qu'elle represente.
            BuildAxisLine(parent, center + new Vector2(-PlotWidth * 0.5f, 0f), new Vector2(1f, PlotHeight), AxisColor);
            BuildAxisLine(parent, center + new Vector2(0f, -PlotHeight * 0.5f), new Vector2(PlotWidth, 1f), AxisColor);
            BuildAxisLine(parent, center, new Vector2(PlotWidth, 1f), GridColor);

            var plotGO = new GameObject($"Plot_{title}", typeof(RectTransform));
            plotGO.transform.SetParent(parent, false);
            RectTransform plotRect = plotGO.GetComponent<RectTransform>();
            plotRect.sizeDelta = new Vector2(PlotWidth, PlotHeight);
            plotRect.anchoredPosition = center;

            TextMeshProUGUI yMaxLabel = BuildAxisLabel(parent, center + new Vector2(-PlotWidth * 0.5f - 4f, PlotHeight * 0.5f - 6f), TextAlignmentOptions.MidlineRight);
            TextMeshProUGUI yMidLabel = BuildAxisLabel(parent, center + new Vector2(-PlotWidth * 0.5f - 4f, 0f), TextAlignmentOptions.MidlineRight);
            TextMeshProUGUI yZeroLabel = BuildAxisLabel(parent, center + new Vector2(-PlotWidth * 0.5f - 4f, -PlotHeight * 0.5f + 6f), TextAlignmentOptions.MidlineRight);
            yZeroLabel.text = "0";

            // Abscisse : le bord gauche est le point le plus ancien de la fenetre, le
            // bord droit est l'instant present.
            TextMeshProUGUI xLabel = BuildAxisLabel(parent, center + new Vector2(-PlotWidth * 0.5f + 2f, -PlotHeight * 0.5f - 9f), TextAlignmentOptions.MidlineLeft);
            TextMeshProUGUI xNowLabel = BuildAxisLabel(parent, center + new Vector2(PlotWidth * 0.5f - 2f, -PlotHeight * 0.5f - 9f), TextAlignmentOptions.MidlineRight);
            xNowLabel.text = "maintenant";

            return new TimeSeriesGraph
            {
                plotArea = plotRect,
                valueText = valueText,
                yMax = yMax,
                valueFormat = valueFormat,
                color = color,
                yMaxLabel = yMaxLabel,
                yMidLabel = yMidLabel,
                xLabel = xLabel
            };
        }

        private static readonly Color AxisColor = new Color(0.55f, 0.55f, 0.58f, 0.9f);
        private static readonly Color GridColor = new Color(0.45f, 0.45f, 0.48f, 0.35f);
        private static readonly Color AxisLabelColor = new Color(0.72f, 0.72f, 0.75f);

        private static void BuildAxisLine(Transform parent, Vector2 position, Vector2 size, Color color)
        {
            var go = new GameObject("Axis", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            go.GetComponent<Image>().color = color;
        }

        private static TextMeshProUGUI BuildAxisLabel(Transform parent, Vector2 position, TextAlignmentOptions alignment)
        {
            var go = new GameObject("AxisLabel", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(64f, 14f);
            rect.anchoredPosition = position;
            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = 9f;
            text.color = AxisLabelColor;
            text.alignment = alignment;
            text.text = "";
            return text;
        }

        /// <summary>Met a jour les graduations. Les textes ne sont reecrits que quand leur
        /// contenu change reellement : ecrire dans un TextMeshProUGUI invalide le canvas,
        /// et ni yMax ni la largeur de fenetre ne bougent souvent.</summary>
        private static void RefreshAxisLabels(TimeSeriesGraph graph)
        {
            if (graph.yMaxLabel != null)
            {
                string yMaxText = FormatAxisValue(graph, graph.yMax);
                if (yMaxText != graph.lastYMaxText)
                {
                    graph.lastYMaxText = yMaxText;
                    graph.yMaxLabel.text = yMaxText;
                    if (graph.yMidLabel != null) graph.yMidLabel.text = FormatAxisValue(graph, graph.yMax * 0.5f);
                }
            }

            if (graph.xLabel == null) return;
            float seconds = graph.currentWindowSeconds;
            string xText = seconds < 90f
                ? $"-{Mathf.RoundToInt(seconds)}s"
                : $"-{Mathf.FloorToInt(seconds / 60f)}m{Mathf.RoundToInt(seconds % 60f):00}";
            if (xText == graph.lastXText) return;
            graph.lastXText = xText;
            graph.xLabel.text = xText;
        }

        private static string FormatAxisValue(TimeSeriesGraph graph, float value)
        {
            try { return string.Format(graph.valueFormat, value); }
            catch { return value.ToString("F0"); }
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

            // ANCIEN COMPORTEMENT : les 4 graphes etaient integralement redessines a
            // CHAQUE frame, en ecrivant sizeDelta/anchoredPosition/localRotation sur un
            // RectTransform par segment. Comme la liste d'echantillons n'etait jamais
            // tronquee, le cout grandissait pendant tout le vol et le framerate se
            // degradait progressivement. On ne redessine plus que quand il y a
            // reellement quelque chose de nouveau a montrer.
            RedrawIfNeeded(fpsGraph);
            RedrawIfNeeded(batteryGraph);
            RedrawIfNeeded(altitudeGraph);
            RedrawIfNeeded(temperatureGraph);
        }

        private void RedrawIfNeeded(TimeSeriesGraph graph)
        {
            if (graph == null) return;

            // L'animation d'elargissement de l'axe X continue de tourner par frame, mais
            // elle est bien plus legere qu'un redessin complet et s'arrete des qu'elle a
            // converge.
            float targetWindow = Mathf.Max(initialWindowSeconds, graph.samples.Count * graph.effectiveInterval);
            if (graph.currentWindowSeconds <= 0f)
            {
                graph.currentWindowSeconds = targetWindow;
                graph.needsRedraw = true;
            }
            else if (Mathf.Abs(graph.currentWindowSeconds - targetWindow) > 0.01f)
            {
                graph.currentWindowSeconds = Mathf.Lerp(graph.currentWindowSeconds, targetWindow, Time.deltaTime * windowSmoothing);
                graph.needsRedraw = true;
            }

            if (!graph.needsRedraw) return;
            graph.needsRedraw = false;
            RedrawGraph(graph);
            RefreshAxisLabels(graph);
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

        private void AddSample(TimeSeriesGraph graph, float value)
        {
            if (graph.effectiveInterval <= 0f) graph.effectiveInterval = sampleIntervalSeconds;

            graph.samples.Add(value);

            if (graph.samples.Count > maxSamplesPerGraph)
            {
                // Decimation par MOYENNE de paires, et non en jetant un point sur deux.
                // La difference se voit surtout sur la batterie : c'est une valeur
                // entiere qui bouge par marches de 1%, donc supprimer un point sur deux
                // supprimait litteralement la moitie des marches et transformait la
                // courbe en longs segments droits. La moyenne conserve la trajectoire,
                // et produit meme des valeurs intermediaires - donc une courbe plus fine
                // que la marche d'origine.
                int count = graph.samples.Count;
                int write = 0;
                int read = 0;
                for (; read + 1 < count; read += 2)
                    graph.samples[write++] = (graph.samples[read] + graph.samples[read + 1]) * 0.5f;
                if (read < count) graph.samples[write++] = graph.samples[read]; // dernier point impair
                graph.samples.RemoveRange(write, graph.samples.Count - write);
                graph.effectiveInterval *= 2f;
            }

            graph.needsRedraw = true;
        }

        /// <summary>Position d'un point, en pixels de la zone de trace. Etait une
        /// fonction locale capturant "graph" : cela allouait une closure par graphe et
        /// par appel, soit 4 allocations par frame pour rien.</summary>
        private static Vector2 PointPos(TimeSeriesGraph graph, int index)
        {
            float t = index * graph.effectiveInterval;
            float x = (t / graph.currentWindowSeconds) * PlotWidth - PlotWidth * 0.5f;
            float normalizedValue = graph.yMax > 0f ? Mathf.Clamp01(graph.samples[index] / graph.yMax) : 0f;
            float y = normalizedValue * PlotHeight - PlotHeight * 0.5f;
            return new Vector2(x, y);
        }

        private void RedrawGraph(TimeSeriesGraph graph)
        {
            if (graph.currentWindowSeconds <= 0f) graph.currentWindowSeconds = initialWindowSeconds;

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

                Vector2 p0 = PointPos(graph, i);
                Vector2 p1 = PointPos(graph, i + 1);
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
