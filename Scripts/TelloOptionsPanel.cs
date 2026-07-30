using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Thin, single-row banner BELOW the video screen. Eight cards, all plain
    /// numeric/text readouts (this row is the "scalar" tier - if it has a
    /// precise value to read, it lives here): speed level, sensitivity,
    /// altitude, flight time, ground speed, estimated time remaining, Tello
    /// battery, headset battery. Wider than the video screen itself on purpose
    /// (see class comment history) so every card keeps the same physical size
    /// rather than shrinking to fit.
    ///
    /// Speed and sensitivity are read-only displays here - they're only ever
    /// changed via the gamepad's L1/R1 (speed) and L2/R2 (sensitivity)
    /// shortcuts (see TelloGamepadController), which this panel picks up
    /// through the onSpeedLevelChanged/onSensitivityLevelChanged events.
    /// There used to also be a dedicated "Menu mode" for navigating/editing
    /// these two cards with the dpad/stick, but with everything else already
    /// pre-tuned and nothing left to configure in flight, that whole second
    /// control mode was removed - see TelloGamepadController.
    /// </summary>
    public class TelloOptionsPanel : MonoBehaviour
    {
        [SerializeField] private TelloConnection tello;
        [SerializeField] private TelloGamepadController gamepadController;
        [Tooltip("The video screen - used both as the parent to follow and to read its exact width.")]
        [SerializeField] private TelloVideoDisplay videoScreen;

        [Header("=== CARD SHAPE (matches TelloStatusPanel) ===")]
        [SerializeField] private float cardCornerRadiusPx = 14f;
        [SerializeField] private Vector2 shadowOffsetPx = new Vector2(3f, -3f);
        [Tooltip("Vertical gap between the banner and the video screen, in world units.")]
        [SerializeField] private float gap = 0.01f;
        [Tooltip("If true, this component does NOT position/show itself in Start() - an external controller (e.g. TelloInitGate) calls RevealNow() instead.")]
        [SerializeField] private bool positionedExternally = false;

        private CanvasGroup canvasGroup;

        // Canvas is wider than TelloStatusPanel's (same CanvasPixelHeight, same
        // per-card pixel footprint) since it now holds 8 cards instead of 5.
        // ReferenceCanvasPixelWidth (below) is what the scale is anchored to -
        // NOT CanvasPixelWidth - so each card keeps the exact same physical
        // (world) size as before, and the bar now correctly extends past the
        // video screen's edges on both sides instead of everything shrinking
        // to stay within it.
        private const float CanvasPixelWidth = 1440f;
        private const float ReferenceCanvasPixelWidth = 900f;
        private const float CanvasPixelHeight = 140f;
        private const int CardCount = 8;
        private const int MaxSpeedLevel = 5;
        private const int MaxSensitivityLevel = 3;

        private static readonly Color PanelBackground = new Color(0.11f, 0.11f, 0.11f, 0.92f);
        private static readonly Color CardBackground = new Color(0.20f, 0.20f, 0.20f, 1f);
        private static readonly Color ShadowColor = new Color(0f, 0f, 0f, 0.45f);

        private Sprite roundedSprite;

        private TextMeshProUGUI speedValue;
        private TextMeshProUGUI sensitivityValue;
        private TextMeshProUGUI altitudeText;
        private TextMeshProUGUI flightTimeText;
        private TextMeshProUGUI groundSpeedText;
        private TextMeshProUGUI timeRemainingText;
        private TextMeshProUGUI batteryTelloText;
        private TextMeshProUGUI batteryHeadsetText;

        private void Awake()
        {
            if (tello == null) tello = TelloConnection.Instance;
            // Pas de fallback GetComponent ici : dans la scene, TelloGamepadController
            // vit sur son PROPRE GameObject, donc ce fallback rendait toujours null et
            // masquait une reference oubliee derriere un affichage "--" silencieux.
            if (gamepadController == null)
                Debug.LogError("[TelloOptionsPanel] gamepadController non assigne dans l'Inspector - les cartes vitesse/sensibilite resteront a '--'.");
            roundedSprite = TelloUiKit.GetRoundedSprite(cardCornerRadiusPx);
            BuildUI();
            // NOT positioned here - Unity doesn't guarantee Awake() order across
            // GameObjects, and this needs TelloVideoDisplay's zoom level to
            // already be initialized. Positioning happens in Start() instead.
        }

        private void Start()
        {
            if (tello == null) tello = TelloConnection.Instance; // see TelloInitGate's Start() comment - Awake() order isn't guaranteed across GameObjects
            RefreshValues();
            if (positionedExternally) return; // an external controller reveals this instead - see RevealNow()
            PositionBelowScreen();
        }

        /// <summary>Called by an external controller (TelloInitGate) once the video screen is ready - positions this banner then fades it in.</summary>
        public void RevealNow()
        {
            PositionBelowScreen();
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
            if (videoScreen != null)
            {
                videoScreen.OnSizeChanged += PositionBelowScreen;
                // (RefreshValues etait aussi abonne ici : residu du "Menu mode"
                // supprime. Un changement de taille d'ecran n'a aucune raison de
                // rafraichir le niveau de vitesse.)
            }

            if (gamepadController == null) return;
            gamepadController.onSpeedLevelChanged.AddListener(OnSpeedLevelChanged);
            gamepadController.onSensitivityLevelChanged.AddListener(OnSensitivityLevelChanged);
        }

        private void OnDisable()
        {
            if (videoScreen != null)
            {
                videoScreen.OnSizeChanged -= PositionBelowScreen;
            }

            if (gamepadController == null) return;
            gamepadController.onSpeedLevelChanged.RemoveListener(OnSpeedLevelChanged);
            gamepadController.onSensitivityLevelChanged.RemoveListener(OnSensitivityLevelChanged);
        }

        private void PositionBelowScreen()
        {
            if (videoScreen == null)
            {
                Debug.LogWarning("[TelloOptionsPanel] Video Screen not assigned - can't compute width/position, staying at default transform.");
                return;
            }

            transform.SetParent(videoScreen.transform, false);

            float scale = videoScreen.QuadWidth / ReferenceCanvasPixelWidth;
            transform.localScale = Vector3.one * scale;

            float y = -(videoScreen.QuadHeight * 0.5f + gap + (CanvasPixelHeight * scale) * 0.5f);
            transform.localPosition = new Vector3(0f, y, 0f);
            transform.localRotation = Quaternion.identity;
        }

        // =================================================================
        // UI CONSTRUCTION - single row of CardCount evenly spaced cards
        // =================================================================
        private void BuildUI()
        {
            var canvasGO = new GameObject("TelloOptionsCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(CanvasPixelWidth, CanvasPixelHeight);
            canvasGO.transform.localPosition = Vector3.zero;

            canvasGroup = canvasGO.AddComponent<CanvasGroup>();
            canvasGroup.alpha = positionedExternally ? 0f : 1f;

            TelloUiKit.BuildFullRectBackground(canvasGO.transform, roundedSprite, PanelBackground);

            float columnSpacing = CanvasPixelWidth / CardCount;
            Vector2 cardSize = new Vector2(columnSpacing - 10f, CanvasPixelHeight - 16f);
            float ColumnX(int index) => (index - (CardCount - 1) / 2f) * columnSpacing;

            speedValue = BuildValueCard(canvasGO.transform, "Speed lvl", new Vector2(ColumnX(0), 0f), cardSize);
            sensitivityValue = BuildValueCard(canvasGO.transform, "Sensitivity", new Vector2(ColumnX(1), 0f), cardSize);
            altitudeText = BuildValueCard(canvasGO.transform, "Altitude", new Vector2(ColumnX(2), 0f), cardSize);
            flightTimeText = BuildValueCard(canvasGO.transform, "Flight time", new Vector2(ColumnX(3), 0f), cardSize);
            groundSpeedText = BuildValueCard(canvasGO.transform, "Speed", new Vector2(ColumnX(4), 0f), cardSize);
            timeRemainingText = BuildValueCard(canvasGO.transform, "Time left", new Vector2(ColumnX(5), 0f), cardSize);
            batteryTelloText = BuildValueCard(canvasGO.transform, "Tello batt.", new Vector2(ColumnX(6), 0f), cardSize);
            batteryHeadsetText = BuildValueCard(canvasGO.transform, "Headset batt.", new Vector2(ColumnX(7), 0f), cardSize);
        }

        private TextMeshProUGUI BuildValueCard(Transform parent, string label, Vector2 anchoredPosition, Vector2 size)
        {
            RectTransform card = TelloUiKit.BuildCardShell(parent, "Card", anchoredPosition, size, roundedSprite, CardBackground, ShadowColor, shadowOffsetPx);

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(card, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(size.x - 16f, 20f);
            labelRect.anchoredPosition = new Vector2(0f, 24f);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            labelText.fontSize = 12f;
            labelText.color = Color.white;
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.text = label;

            var valueGO = new GameObject("Value", typeof(RectTransform));
            valueGO.transform.SetParent(card, false);
            RectTransform valueRect = valueGO.GetComponent<RectTransform>();
            valueRect.sizeDelta = new Vector2(size.x - 12f, 26f);
            valueRect.anchoredPosition = new Vector2(0f, -14f);
            var value = valueGO.AddComponent<TextMeshProUGUI>();
            value.fontSize = 16f;
            value.color = Color.white;
            value.alignment = TextAlignmentOptions.Center;
            value.text = "--";
            return value;
        }

        // =================================================================
        // VALUES
        // =================================================================
        private void OnSpeedLevelChanged(int level) => RefreshValues();
        private void OnSensitivityLevelChanged(int level) => RefreshValues();

        private void RefreshValues()
        {
            if (gamepadController == null) return;
            speedValue.SetText("{0}/{1}", gamepadController.SpeedLevel, MaxSpeedLevel);
            sensitivityValue.SetText("{0}/{1}", gamepadController.SensitivityLevel, MaxSensitivityLevel);
        }

        // Rafraichissement des valeurs continues.
        //
        // ANCIEN COMPORTEMENT : 6 interpolations de chaine PAR FRAME (donc 6
        // allocations, et 6 reconstructions de mesh TextMeshPro a 72 Hz), pour des
        // valeurs qui viennent d'un flux telemetrie a ~10 Hz. C'etait du temps
        // main-thread et du rebuild de canvas voles au pipeline video, pour afficher
        // exactement la meme chose 7 fois de suite.
        //
        // Maintenant : cadence a UiRefreshHz, et SetText(format, arg) de TMP qui ne
        // fait aucune allocation (contrairement a "text = $"...""). La batterie du
        // casque, elle, est lue encore plus rarement : SystemInfo.batteryLevel est un
        // appel natif et sa valeur ne bouge qu'a l'echelle de la minute.
        private const float UiRefreshHz = 8f;
        private float refreshTimer;
        private float headsetBatteryTimer;
        private const float HeadsetBatteryIntervalSeconds = 10f;

        private void Update()
        {
            if (tello == null) return;

            refreshTimer += Time.deltaTime;
            if (refreshTimer < 1f / UiRefreshHz) return;
            refreshTimer = 0f;

            altitudeText.SetText("{0:1}m", tello.HeightM);
            flightTimeText.SetText(tello.FlightTimeFormatted);
            groundSpeedText.SetText("{0:0}cm/s", tello.VelocityMagnitude);
            timeRemainingText.SetText(tello.EstimatedRemainingFlightTimeFormatted);
            batteryTelloText.SetText("{0}%", tello.Battery);

            headsetBatteryTimer -= 1f / UiRefreshHz;
            if (headsetBatteryTimer <= 0f)
            {
                headsetBatteryTimer = HeadsetBatteryIntervalSeconds;
                float level = SystemInfo.batteryLevel;
                if (level >= 0f) batteryHeadsetText.SetText("{0:0}%", level * 100f);
                else batteryHeadsetText.SetText("N/A");
            }
        }
    }
}
