using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Band to the RIGHT of the video screen, mirroring TelloSpatialPanel's sizing
    /// convention (world height always equals the video screen's own height, same
    /// gap as every other banner). Shows a simple scrolling transcript: player
    /// actions (photo taken, recording started/stopped, speed/sensitivity changed,
    /// takeoff/land) and system alerts (TelloConnection's existing warning
    /// pipeline - battery, temperature, proximity, signal loss, crash suspected...).
    ///
    /// Deliberately simple: one TextMeshPro block, newest line at the top, capped
    /// to MaxEntries - no per-line interactive UI, this is a passive readout, not
    /// something the pilot navigates with the stick.
    /// </summary>
    public class TelloActionLogPanel : MonoBehaviour
    {
        [SerializeField] private TelloConnection tello;
        [SerializeField] private TelloGamepadController gamepadController;
        [SerializeField] private TelloVideoRecorder videoRecorder;
        [Tooltip("The video screen - used both as the parent to follow and to read its exact width/height.")]
        [SerializeField] private TelloVideoDisplay videoScreen;

        [Header("=== PANEL SHAPE ===")]
        [SerializeField] private float cardCornerRadiusPx = 14f;
        [Tooltip("Horizontal gap between this panel and the video screen, in world units - same convention as every other banner's gap.")]
        [SerializeField] private float gap = 0.01f;
        [SerializeField] private bool positionedExternally = false;

        [Tooltip("How many lines to keep - oldest entries drop off once this is exceeded.")]
        [SerializeField] private int maxEntries = 16;

        private const float CanvasPixelWidth = 260f;
        private const float CanvasPixelHeight = 640f; // same internal-resolution convention as TelloSpatialPanel

        private static readonly Color PanelBackground = new Color(0.11f, 0.11f, 0.11f, 0.92f);
        private static readonly Color Ink = new Color(0.93f, 0.92f, 0.89f);
        private static readonly Color InkDim = new Color(0.54f, 0.56f, 0.58f);
        private static readonly Color Amber = new Color(0.91f, 0.64f, 0.24f);

        private Sprite roundedSprite;
        private CanvasGroup canvasGroup;
        private TextMeshProUGUI logText;
        private readonly List<string> entries = new List<string>();

        private bool lastIsFlying;
        private bool hasLoggedInitialFlyingState;

        private void Awake()
        {
            if (tello == null) tello = TelloConnection.Instance;
            roundedSprite = TelloUiKit.GetRoundedSprite(cardCornerRadiusPx);
            BuildUI();
        }

        private void Start()
        {
            if (positionedExternally) return;
            PositionRightOfScreen();
        }

        /// <summary>Called by an external controller (TelloInitGate) once the video screen is ready - positions this panel then fades it in.</summary>
        public void RevealNow()
        {
            PositionRightOfScreen();
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
            if (videoScreen != null) videoScreen.OnSizeChanged += PositionRightOfScreen;

            if (tello != null) tello.OnWarningTriggered += HandleWarning;
            if (gamepadController != null)
            {
                gamepadController.onPhotoSaved.AddListener(HandlePhotoSaved);
                gamepadController.onSpeedLevelChanged.AddListener(HandleSpeedChanged);
                gamepadController.onSensitivityLevelChanged.AddListener(HandleSensitivityChanged);
            }
            if (videoRecorder != null) videoRecorder.OnRecordingStateChanged += HandleRecordingStateChanged;
            if (tello != null) tello.OnCommandResponseReceived += HandleCommandResponse;
            if (tello != null) tello.OnFlightCommandSent += HandleFlightCommandSent;
        }

        private void OnDisable()
        {
            if (videoScreen != null) videoScreen.OnSizeChanged -= PositionRightOfScreen;

            if (tello != null) tello.OnWarningTriggered -= HandleWarning;
            if (gamepadController != null)
            {
                gamepadController.onPhotoSaved.RemoveListener(HandlePhotoSaved);
                gamepadController.onSpeedLevelChanged.RemoveListener(HandleSpeedChanged);
                gamepadController.onSensitivityLevelChanged.RemoveListener(HandleSensitivityChanged);
            }
            if (videoRecorder != null) videoRecorder.OnRecordingStateChanged -= HandleRecordingStateChanged;
            if (tello != null) tello.OnCommandResponseReceived -= HandleCommandResponse;
            if (tello != null) tello.OnFlightCommandSent -= HandleFlightCommandSent;
        }

        /// <summary>Same formula pattern as TelloSpatialPanel - world height pinned to
        /// the screen's QuadHeight, positioned to the RIGHT with the standard gap.</summary>
        private void PositionRightOfScreen()
        {
            if (videoScreen == null)
            {
                Debug.LogWarning("[TelloActionLogPanel] Video Screen not assigned - can't compute position, staying at default transform.");
                return;
            }

            transform.SetParent(videoScreen.transform, false);

            float scale = videoScreen.QuadHeight / CanvasPixelHeight;
            transform.localScale = Vector3.one * scale;

            float bandWorldWidth = CanvasPixelWidth * scale;
            float x = videoScreen.QuadWidth * 0.5f + gap + bandWorldWidth * 0.5f;
            transform.localPosition = new Vector3(x, 0f, 0f);
            transform.localRotation = Quaternion.identity;
        }

        // =================================================================
        // EVENT HANDLERS -> LOG ENTRIES
        // =================================================================
        private void HandleWarning(string message) => AddEntry(message, Amber);
        private void HandlePhotoSaved(string path) => AddEntry("Photo saved", Ink);
        private void HandleSpeedChanged(int level) => AddEntry($"Speed level: {level}", InkDim);
        private void HandleSensitivityChanged(int level) => AddEntry($"Sensitivity level: {level}", InkDim);
        private void HandleRecordingStateChanged(bool recording) => AddEntry(recording ? "Recording started" : "Recording stopped", Ink);

        private void HandleFlightCommandSent(string command)
        {
            switch (command)
            {
                case "takeoff": AddEntry("Alright, taking off.", Ink); break;
                case "land": AddEntry("Bringing it down.", Ink); break;
                default:
                    if (command != null && command.StartsWith("flip")) AddEntry("Doing a flip.", Ink);
                    break;
            }
        }

        private void HandleCommandResponse(string command, string response, bool success)
        {
            if (!success) return;
            switch (command)
            {
                case "takeoff": AddEntry("I'm airborne.", Ink); break;
                case "land": AddEntry("Touched down.", Ink); break;
                case "emergency": AddEntry("Emergency stop", Amber); break;
                default:
                    if (command != null && command.StartsWith("flip")) AddEntry("Flip done.", Ink);
                    break;
            }
        }

        private void Update()
        {
            // IsFlying isn't its own event - poll for the transition instead of
            // adding one more event to TelloConnection just for this.
            if (tello == null) return;
            if (!hasLoggedInitialFlyingState) { lastIsFlying = tello.IsFlying; hasLoggedInitialFlyingState = true; return; }
            if (tello.IsFlying != lastIsFlying)
            {
                lastIsFlying = tello.IsFlying;
                // Takeoff/Land are already logged via HandleCommandResponse above -
                // this only catches the rare case IsFlying changes without either
                // command succeeding (e.g. auto-land triggered internally).
            }
        }

        private void AddEntry(string message, Color color)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string hex = ColorUtility.ToHtmlStringRGB(color);
            entries.Insert(0, $"<color=#{hex}>{timestamp}  {message}</color>");
            while (entries.Count > maxEntries) entries.RemoveAt(entries.Count - 1);

            if (logText != null) logText.text = string.Join("\n", entries);
        }

        // =================================================================
        // UI CONSTRUCTION
        // =================================================================
        private void BuildUI()
        {
            var canvasGO = new GameObject("TelloActionLogCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(CanvasPixelWidth, CanvasPixelHeight);
            canvasGO.transform.localPosition = Vector3.zero;

            canvasGroup = canvasGO.AddComponent<CanvasGroup>();
            canvasGroup.alpha = positionedExternally ? 0f : 1f;

            TelloUiKit.BuildFullRectBackground(canvasGO.transform, roundedSprite, PanelBackground);

            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(canvasGO.transform, false);
            RectTransform titleRect = titleGO.GetComponent<RectTransform>();
            titleRect.sizeDelta = new Vector2(CanvasPixelWidth - 20f, 24f);
            titleRect.anchoredPosition = new Vector2(0f, 300f);
            var title = titleGO.AddComponent<TextMeshProUGUI>();
            title.fontSize = 14f;
            title.color = Color.white;
            title.alignment = TextAlignmentOptions.Center;
            title.text = "Activity Log";

            var logGO = new GameObject("LogText", typeof(RectTransform));
            logGO.transform.SetParent(canvasGO.transform, false);
            RectTransform logRect = logGO.GetComponent<RectTransform>();
            logRect.sizeDelta = new Vector2(CanvasPixelWidth - 24f, 580f);
            logRect.anchoredPosition = new Vector2(0f, -10f);
            logText = logGO.AddComponent<TextMeshProUGUI>();
            logText.fontSize = 11f;
            logText.color = InkDim;
            logText.alignment = TextAlignmentOptions.TopLeft;
            logText.textWrappingMode = TextWrappingModes.Normal;
            logText.overflowMode = TextOverflowModes.Truncate; // oldest visible entries just clip rather than push the panel taller
            logText.text = "";
        }
    }
}
