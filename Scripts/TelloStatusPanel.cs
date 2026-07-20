using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Thin, single-row banner ABOVE the video screen, same width as it. Eight
    /// small rounded cards in one line: Manette/Tello connection dots, a 5-bar
    /// video signal meter, then battery/altitude/flight-time/speed telemetry.
    /// Parented under the head-locked video screen's transform, so it follows
    /// automatically. Deliberately compact - stays inside comfortable view.
    /// </summary>
    public class TelloStatusPanel : MonoBehaviour
    {
        [SerializeField] private TelloConnection tello;
        [SerializeField] private TelloGamepadController gamepadController;
        [SerializeField] private TelloVideoDecoder videoDecoder;
        [Tooltip("The head-locked video screen - used both as the parent to follow and to read its exact width.")]
        [SerializeField] private TelloVideoDisplay videoScreen;

        [Header("=== CARD SHAPE ===")]
        [SerializeField] private float cardCornerRadiusPx = 14f;
        [SerializeField] private Vector2 shadowOffsetPx = new Vector2(3f, -3f);
        [Tooltip("Vertical gap between the banner and the video screen, in world units.")]
        [SerializeField] private float gap = 0.01f;

        [Header("=== VIDEO SIGNAL THRESHOLDS ===")]
        [SerializeField] private float nominalFps = 25f;

        [Tooltip("If true, this component does NOT position/show itself in Start() - an external controller (e.g. TelloInitGate) calls RevealNow() instead.")]
        [SerializeField] private bool positionedExternally = false;

        private CanvasGroup canvasGroup;

        private const float CanvasPixelWidth = 900f;
        private const float CanvasPixelHeight = 140f;
        private const int CardCount = 5;
        private const int SignalBarCount = 5;

        private static readonly Color PanelBackground = new Color(0.11f, 0.11f, 0.11f, 0.92f);
        private static readonly Color CardBackground = new Color(0.20f, 0.20f, 0.20f, 1f);
        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color ConnectedColor = new Color(0.3f, 0.85f, 0.45f);
        private static readonly Color DisconnectedColor = new Color(0.9f, 0.3f, 0.3f);
        private static readonly Color BarOnColor = Color.white;
        private static readonly Color BarOffColor = new Color(0.45f, 0.45f, 0.45f);

        private Sprite roundedSprite;

        private Image gamepadDot;
        private Image telloDot;
        private readonly List<Image> signalBars = new List<Image>();
        private TextMeshProUGUI signalFpsText;
        private TextMeshProUGUI batteryTelloText;
        private TextMeshProUGUI batteryHeadsetText;

        private long lastFrameCount;
        private float windowTimer;
        private float measuredFps;

        private void Awake()
        {
            if (tello == null) tello = TelloConnection.Instance;
            roundedSprite = TelloUiKit.GetRoundedSprite(cardCornerRadiusPx);
            BuildUI();
            // NOT positioned here: TelloVideoDisplay.Awake() sets its zoom level
            // (which QuadWidth/QuadHeight depend on) and Unity doesn't guarantee
            // Awake() order across different GameObjects. Positioning happens in
            // Start() instead, which Unity guarantees runs after every Awake() in
            // the scene has completed - no race possible.
        }

        private void Start()
        {
            if (positionedExternally) return; // an external controller reveals this instead - see RevealNow()
            PositionAboveScreen();
        }

        /// <summary>Called by an external controller (TelloInitGate) once the video screen is ready - positions this banner then fades it in.</summary>
        public void RevealNow()
        {
            PositionAboveScreen();
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
            if (videoScreen != null) videoScreen.OnSizeChanged += PositionAboveScreen;
        }

        private void OnDisable()
        {
            if (videoScreen != null) videoScreen.OnSizeChanged -= PositionAboveScreen;
        }

        private void PositionAboveScreen()
        {
            if (videoScreen == null)
            {
                Debug.LogWarning("[TelloStatusPanel] Video Screen not assigned - can't compute width/position, staying at default transform.");
                return;
            }

            transform.SetParent(videoScreen.transform, false);

            float scale = videoScreen.QuadWidth / CanvasPixelWidth;
            transform.localScale = Vector3.one * scale;

            float y = videoScreen.QuadHeight * 0.5f + gap + (CanvasPixelHeight * scale) * 0.5f;
            transform.localPosition = new Vector3(0f, y, 0f);
            transform.localRotation = Quaternion.identity;
        }

        // =================================================================
        // UI CONSTRUCTION - single row of CardCount evenly spaced cards
        // =================================================================
        private void BuildUI()
        {
            var canvasGO = new GameObject("TelloStatusCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(CanvasPixelWidth, CanvasPixelHeight);
            canvasGO.transform.localPosition = Vector3.zero;

            canvasGroup = canvasGO.AddComponent<CanvasGroup>();
            canvasGroup.alpha = positionedExternally ? 0f : 1f; // externally-revealed: starts invisible, RevealNow() fades it in

            TelloUiKit.BuildFullRectBackground(canvasGO.transform, roundedSprite, PanelBackground);

            float columnSpacing = CanvasPixelWidth / CardCount;
            Vector2 cardSize = new Vector2(columnSpacing - 10f, CanvasPixelHeight - 16f);
            float ColumnX(int index) => (index - (CardCount - 1) / 2f) * columnSpacing;

            gamepadDot = BuildDotCard(canvasGO.transform, "Gamepad", new Vector2(ColumnX(0), 0f), cardSize);
            telloDot = BuildDotCard(canvasGO.transform, "Tello", new Vector2(ColumnX(1), 0f), cardSize);
            BuildSignalCard(canvasGO.transform, "Video", new Vector2(ColumnX(2), 0f), cardSize);
            batteryHeadsetText = BuildValueCard(canvasGO.transform, "Headset", new Vector2(ColumnX(3), 0f), cardSize);
            batteryTelloText = BuildValueCard(canvasGO.transform, "Battery", new Vector2(ColumnX(4), 0f), cardSize);
        }

        private RectTransform BuildCardShell(Transform parent, Vector2 anchoredPosition, Vector2 size) =>
            TelloUiKit.BuildCardShell(parent, "Card", anchoredPosition, size, roundedSprite, CardBackground, ShadowColor, shadowOffsetPx);

        private TextMeshProUGUI BuildLabel(Transform parent, string text, float yOffset, float fontSize)
        {
            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(parent, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(100f, 20f);
            labelRect.anchoredPosition = new Vector2(0f, yOffset);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            label.fontSize = fontSize;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            label.text = text;
            return label;
        }

        private Image BuildDotCard(Transform parent, string label, Vector2 anchoredPosition, Vector2 size)
        {
            RectTransform card = BuildCardShell(parent, anchoredPosition, size);
            BuildLabel(card, label, 24f, 12f);

            var dotGO = new GameObject("Dot", typeof(RectTransform), typeof(Image));
            dotGO.transform.SetParent(card, false);
            RectTransform dotRect = dotGO.GetComponent<RectTransform>();
            dotRect.sizeDelta = new Vector2(22f, 22f);
            dotRect.anchoredPosition = new Vector2(0f, -14f);
            Image dot = dotGO.GetComponent<Image>();
            dot.sprite = roundedSprite;
            dot.type = Image.Type.Sliced;
            dot.color = DisconnectedColor;
            return dot;
        }

        private TextMeshProUGUI BuildValueCard(Transform parent, string label, Vector2 anchoredPosition, Vector2 size)
        {
            RectTransform card = BuildCardShell(parent, anchoredPosition, size);
            BuildLabel(card, label, 24f, 12f);

            var valueGO = new GameObject("Value", typeof(RectTransform));
            valueGO.transform.SetParent(card, false);
            RectTransform valueRect = valueGO.GetComponent<RectTransform>();
            valueRect.sizeDelta = new Vector2(100f, 26f);
            valueRect.anchoredPosition = new Vector2(0f, -14f);
            var value = valueGO.AddComponent<TextMeshProUGUI>();
            value.fontSize = 16f;
            value.color = Color.white;
            value.alignment = TextAlignmentOptions.Center;
            value.text = "--";
            return value;
        }

        private void BuildSignalCard(Transform parent, string label, Vector2 anchoredPosition, Vector2 size)
        {
            RectTransform card = BuildCardShell(parent, anchoredPosition, size);
            BuildLabel(card, label, 24f, 12f);

            var barsGO = new GameObject("Bars", typeof(RectTransform));
            barsGO.transform.SetParent(card, false);
            RectTransform barsRect = barsGO.GetComponent<RectTransform>();
            barsRect.sizeDelta = new Vector2(size.x - 16f, 18f);
            barsRect.anchoredPosition = new Vector2(0f, -6f);

            float barWidth = 10f;
            float barSpacing = 4f;
            float totalWidth = SignalBarCount * barWidth + (SignalBarCount - 1) * barSpacing;
            float startX = -totalWidth * 0.5f + barWidth * 0.5f;

            for (int i = 0; i < SignalBarCount; i++)
            {
                var barGO = new GameObject($"Bar{i}", typeof(RectTransform), typeof(Image));
                barGO.transform.SetParent(barsGO.transform, false);
                RectTransform barRect = barGO.GetComponent<RectTransform>();
                barRect.sizeDelta = new Vector2(barWidth, 16f);
                barRect.anchoredPosition = new Vector2(startX + i * (barWidth + barSpacing), 0f);
                Image barImage = barGO.GetComponent<Image>();
                barImage.sprite = roundedSprite;
                barImage.type = Image.Type.Sliced;
                barImage.color = BarOffColor;
                signalBars.Add(barImage);
            }

            signalFpsText = BuildLabel(card, "--fps", -24f, 11f);
        }

        // =================================================================
        // LIVE UPDATE
        // =================================================================
        private void Update()
        {
            gamepadDot.color = (gamepadController != null && gamepadController.IsGamepadConnected)
                ? ConnectedColor : DisconnectedColor;

            telloDot.color = (tello != null && tello.IsConnected)
                ? ConnectedColor : DisconnectedColor;

            windowTimer += Time.deltaTime;
            if (windowTimer >= 1f)
            {
                long currentCount = videoDecoder != null ? videoDecoder.FramesDecodedTotal : 0;
                measuredFps = (currentCount - lastFrameCount) / windowTimer;
                lastFrameCount = currentCount;
                windowTimer = 0f;
            }

            bool signalLost = tello != null && tello.IsSignalLost;
            float ratio = signalLost ? 0f : Mathf.Clamp01(measuredFps / nominalFps);
            int level = signalLost ? 0 : Mathf.Clamp(Mathf.RoundToInt(ratio * SignalBarCount), 0, SignalBarCount);
            for (int i = 0; i < signalBars.Count; i++)
                signalBars[i].color = i < level ? BarOnColor : BarOffColor;
            signalFpsText.text = $"{measuredFps:F0}fps";

            batteryHeadsetText.text = SystemInfo.batteryLevel >= 0f
                ? $"{SystemInfo.batteryLevel * 100f:F0}%"
                : "N/A";

            if (tello == null) return;

            batteryTelloText.text = $"{tello.Battery}%";
        }
    }
}
