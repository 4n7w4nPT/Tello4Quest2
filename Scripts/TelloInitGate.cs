using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Pre-flight gate: three status dots (gamepad, Tello WiFi, video feed),
    /// pulsing red while waiting, green once each check passes. The moment all
    /// three are green, this screen fades out and the flight display (video
    /// screen + banners) fades in at the exact same fixed position - the
    /// hand-off is explicit and controller-driven (RevealAt/RevealNow), not
    /// left to Unity's Awake/Start ordering, which is what caused the banners
    /// to size themselves wrong before the video screen had initialized.
    ///
    /// World-locked, same as the flight display: positioned once in Start(),
    /// never moves after that.
    ///
    /// Setup: TelloVideoScreen, and the TelloStatusPanel/TelloOptionsPanel
    /// GameObjects, should all start INACTIVE in the scene, each with their
    /// own "Positioned Externally" checkbox turned ON. This script activates
    /// and reveals them once ready - see the class comment in TelloVideoDisplay.
    /// </summary>
    public class TelloInitGate : MonoBehaviour
    {
        [SerializeField] private TelloConnection tello;
        [SerializeField] private TelloGamepadController gamepadController;
        [SerializeField] private TelloVideoDecoder videoDecoder;
        [SerializeField] private Transform vrCamera;

        [Header("=== WHAT TO REVEAL ONCE READY ===")]
        [SerializeField] private GameObject videoScreenObject;
        [SerializeField] private TelloVideoDisplay videoScreen;
        [SerializeField] private GameObject statusPanelObject;
        [SerializeField] private TelloStatusPanel statusPanel;
        [SerializeField] private GameObject optionsPanelObject;
        [SerializeField] private TelloOptionsPanel optionsPanel;

        [Header("=== FIXED PLACEMENT (same formula as TelloVideoDisplay) ===")]
        [SerializeField] private float distanceFromCamera = 1.2f;
        [SerializeField] private float assumedEyeHeightMeters = 1.6f;
        [SerializeField] private float verticalOffset = -0.3f;
        [SerializeField] private float worldWidth = 0.9f;

        [Header("=== CARD SHAPE (matches the flight display banners) ===")]
        [SerializeField] private float cornerRadiusPx = 20f;

        private const float CanvasPixelWidth = 700f;
        private const float CanvasPixelHeight = 460f;

        private static readonly Color OkColor = new Color(0.3f, 0.85f, 0.45f);
        private static readonly Color NotOkColor = new Color(0.9f, 0.3f, 0.3f);
        private static readonly Color NotOkColorDim = new Color(0.5f, 0.15f, 0.15f);

        private Sprite roundedSprite;

        private Image gamepadDot;
        private Image telloDot;
        private Image videoDot;
        private TextMeshProUGUI statusText;
        private CanvasGroup canvasGroup;
        private bool ready;

        private void Awake()
        {
            if (tello == null) tello = TelloConnection.Instance;
            roundedSprite = TelloUiKit.GetRoundedSprite(cornerRadiusPx);
            BuildUI();
        }

        private void Start()
        {
            if (vrCamera == null) return;

            transform.position = TelloUiKit.ComputeFixedPosition(vrCamera, distanceFromCamera, assumedEyeHeightMeters, verticalOffset);
            transform.rotation = TelloUiKit.ComputeFixedRotation(vrCamera);
        }

        // =================================================================
        // UI CONSTRUCTION
        // =================================================================
        private void BuildUI()
        {
            var canvasGO = new GameObject("TelloInitCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(CanvasPixelWidth, CanvasPixelHeight);
            canvasGO.transform.localScale = Vector3.one * (worldWidth / CanvasPixelWidth);
            canvasGroup = canvasGO.AddComponent<CanvasGroup>();

            TelloUiKit.BuildFullRectBackground(canvasGO.transform, roundedSprite, new Color(0.08f, 0.08f, 0.08f, 0.95f));

            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(canvasGO.transform, false);
            RectTransform titleRect = titleGO.GetComponent<RectTransform>();
            titleRect.sizeDelta = new Vector2(620f, 60f);
            titleRect.anchoredPosition = new Vector2(0f, 170f);
            var title = titleGO.AddComponent<TextMeshProUGUI>();
            title.text = "Pre-flight check";
            title.fontSize = 30f;
            title.color = Color.white;
            title.alignment = TextAlignmentOptions.Center;

            gamepadDot = BuildCheckRow(canvasGO.transform, "Bluetooth Gamepad", 70f);
            telloDot = BuildCheckRow(canvasGO.transform, "Tello WiFi", 0f);
            videoDot = BuildCheckRow(canvasGO.transform, "Video Feed", -70f);

            var statusGO = new GameObject("StatusText", typeof(RectTransform));
            statusGO.transform.SetParent(canvasGO.transform, false);
            RectTransform statusRect = statusGO.GetComponent<RectTransform>();
            statusRect.sizeDelta = new Vector2(620f, 60f);
            statusRect.anchoredPosition = new Vector2(0f, -160f);
            statusText = statusGO.AddComponent<TextMeshProUGUI>();
            statusText.fontSize = 20f;
            statusText.color = new Color(0.8f, 0.8f, 0.8f);
            statusText.alignment = TextAlignmentOptions.Center;
        }

        private Image BuildCheckRow(Transform parent, string label, float y)
        {
            var rowGO = new GameObject($"Row_{label}", typeof(RectTransform));
            rowGO.transform.SetParent(parent, false);
            RectTransform rowRect = rowGO.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(600f, 50f);
            rowRect.anchoredPosition = new Vector2(0f, y);

            var dotGO = new GameObject("Dot", typeof(RectTransform), typeof(Image));
            dotGO.transform.SetParent(rowGO.transform, false);
            RectTransform dotRect = dotGO.GetComponent<RectTransform>();
            dotRect.sizeDelta = new Vector2(36f, 36f);
            dotRect.anchoredPosition = new Vector2(-220f, 0f);
            Image dot = dotGO.GetComponent<Image>();
            dot.sprite = roundedSprite;
            dot.type = Image.Type.Sliced;
            dot.color = NotOkColor;

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(rowGO.transform, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(400f, 50f);
            labelRect.anchoredPosition = new Vector2(20f, 0f);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            labelText.fontSize = 24f;
            labelText.color = Color.white;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;
            labelText.text = label;

            return dot;
        }

        // =================================================================
        // CHECKS
        // =================================================================
        private void Update()
        {
            if (ready) return;

            bool gamepadOk = gamepadController != null && gamepadController.IsGamepadConnected;
            bool telloOk = tello != null && tello.IsConnected;
            bool videoOk = videoDecoder != null && videoDecoder.FramesDecodedTotal > 0;

            float pulse = (Mathf.Sin(Time.time * 4f) + 1f) * 0.5f;
            gamepadDot.color = gamepadOk ? OkColor : Color.Lerp(NotOkColorDim, NotOkColor, pulse);
            telloDot.color = telloOk ? OkColor : Color.Lerp(NotOkColorDim, NotOkColor, pulse);
            videoDot.color = videoOk ? OkColor : Color.Lerp(NotOkColorDim, NotOkColor, pulse);

            if (gamepadOk && telloOk && videoOk)
            {
                ready = true;
                StartCoroutine(FadeOutThenReveal());
                return;
            }

            string missing = "";
            if (!gamepadOk) missing += "gamepad ";
            if (!telloOk) missing += "Tello wifi ";
            else if (!videoOk) missing += "video ";
            statusText.text = $"Waiting for: {missing.Trim()}";
        }

        private System.Collections.IEnumerator FadeOutThenReveal()
        {
            float duration = 0.35f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                canvasGroup.alpha = 1f - Mathf.Clamp01(elapsed / duration);
                yield return null;
            }
            canvasGroup.alpha = 0f;

            RevealFlightDisplay();
            gameObject.SetActive(false); // this gate is done for good
        }

        private void RevealFlightDisplay()
        {
            if (videoScreenObject != null) videoScreenObject.SetActive(true);
            else Debug.LogWarning("[TelloInitGate] Video Screen Object not assigned - video screen will never appear.");

            // Awake() on TelloVideoDisplay runs synchronously the instant
            // SetActive(true) above executes, so quadTransform/materials are
            // already valid by the time RevealAt() runs on the next line.
            if (videoScreen != null) videoScreen.RevealAt(transform.position, transform.rotation);
            else Debug.LogWarning("[TelloInitGate] Video Screen (component) not assigned - video screen will never appear.");

            if (statusPanelObject != null) statusPanelObject.SetActive(true);
            else Debug.LogWarning("[TelloInitGate] Status Panel Object not assigned - top banner will never appear.");

            if (statusPanel != null) statusPanel.RevealNow();
            else Debug.LogWarning("[TelloInitGate] Status Panel (component) not assigned - top banner will never appear.");

            if (optionsPanelObject != null) optionsPanelObject.SetActive(true);
            else Debug.LogWarning("[TelloInitGate] Options Panel Object not assigned - bottom banner will never appear.");

            if (optionsPanel != null) optionsPanel.RevealNow();
            else Debug.LogWarning("[TelloInitGate] Options Panel (component) not assigned - bottom banner will never appear.");
        }
    }
}
