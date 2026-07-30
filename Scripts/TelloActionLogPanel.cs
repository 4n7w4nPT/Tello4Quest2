using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Band to the RIGHT of the video screen - same width, same sizing
    /// convention (world height pinned to the video screen's own height), and
    /// the same cockpit angle as TelloSpatialPanel on the left, mirrored (see
    /// PositionRightOfScreen).
    ///
    /// Top (75% of height): a mini-map of the flight path since the last
    /// takeoff, built from TelloConnection's dead-reckoning trail
    /// (TelloConnection.FlightTrail). North-up and fixed - the map itself
    /// never rotates, only the drone icon does, deliberately: rotating an
    /// entire visual field is a much stronger motion-sickness trigger in VR
    /// than a small icon spinning in place. Zoom is based on the largest
    /// distance-from-home ever reached this flight (never shrinks mid-flight,
    /// so the scale doesn't jitter as the drone moves closer and farther from
    /// home) and eases toward its target smoothly rather than snapping. The
    /// trail is drawn as small dots connected by line segments (dots alone
    /// don't visually read as a path) - both persistent (no fade), since the
    /// point of the map is to honestly show the whole shape of the flight,
    /// drift included. Scaling stays uniform in X and Y even though the plot
    /// area is now a wide rectangle rather than square - a non-uniform scale
    /// would distort the true shape of the path, which defeats the point.
    ///
    /// Bottom (25% of height): the activity log - player actions (photo
    /// taken, recording started/stopped, speed/sensitivity changed, takeoff/
    /// land) and system alerts. Entries read top (oldest) to bottom (newest),
    /// fading older ones out so the newest stays the most prominent line.
    /// Capped to MaxEntries, sized to fit this now-smaller quarter of the
    /// band without needing to truncate a line.
    ///
    /// No per-line/per-map interactive UI - this is a passive readout, not
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
        [Tooltip("Rotation around the vertical axis, mirroring TelloSpatialPanel's cockpit angle on the left - same magnitude, opposite sign, so both panels angle toward the pilot symmetrically.")]
        [SerializeField] private float cockpitAngleDegrees = 20f;
        [Tooltip("Fait pivoter le panneau autour de son BORD INTERNE (celui qui longe l'ecran video) au lieu de son centre, de sorte que ce bord reste exactement dans le plan de l'ecran. Sans ca, l'inclinaison cockpit envoie toute la moitie interne derriere l'ecran, qui la masque.")]
        [SerializeField] private bool pinInnerEdgeToScreenDepth = true;
        [Tooltip("Decalage de profondeur supplementaire du panneau entier, en Z local de l'ecran video. 0 laisse le bord interne pile dans le plan de l'ecran. Si un reglage non nul deplace le panneau du mauvais cote, inverse simplement le signe - la convention de Z depend de l'orientation du quad.")]
        [SerializeField] private float panelDepthOffset = 0f;
        [SerializeField] private bool positionedExternally = false;

        [Tooltip("How many lines to keep, oldest dropped first once exceeded - sized to comfortably fit the log's quarter of the band (25% of height) without needing to truncate a line.")]
        [SerializeField] private int maxEntries = 8;
        [Tooltip("Opacity of the oldest visible entry (top) - the newest (bottom) is always full opacity.")]
        [SerializeField, Range(0f, 1f)] private float oldestEntryAlpha = 0.3f;

        private const float CanvasPixelWidth = 560f; // matches TelloSpatialPanel
        private const float CanvasPixelHeight = 640f; // same internal-resolution convention as TelloSpatialPanel

        private static readonly Color PanelBackground = new Color(0.11f, 0.11f, 0.11f, 0.92f);
        private static readonly Color InstrumentBackground = new Color(0.20f, 0.20f, 0.20f, 1f);
        private static readonly Color Ink = new Color(0.93f, 0.92f, 0.89f);
        private static readonly Color InkDim = new Color(0.54f, 0.56f, 0.58f);
        private static readonly Color Amber = new Color(0.91f, 0.64f, 0.24f);
        private static readonly Color PanelEdge = new Color(0.15f, 0.17f, 0.19f, 1f);

        private Sprite roundedSprite;
        private CanvasGroup canvasGroup;
        private TextMeshProUGUI logText;

        private struct LogEntry
        {
            public string timestamp;
            public string message;
            public Color color;
        }

        // Oldest at index 0, newest at the end - matches the top-to-bottom reading
        // order on screen (see class comment for why newest-at-bottom was chosen).
        private readonly List<LogEntry> entries = new List<LogEntry>();

        [Header("=== MINI-MAP ===")]
        [Tooltip("The display never zooms in tighter than this radius (cm), even right after takeoff when the flown distance is near zero.")]
        [SerializeField] private float miniMapMinDisplayRadiusCm = 100f;
        [Tooltip("How quickly the zoom eases toward its target scale - higher = faster.")]
        [SerializeField] private float miniMapZoomSmoothing = 3f;

        private Sprite circleSprite;
        private Sprite arrowSprite;
        private RectTransform miniMapContainer;
        private RectTransform droneIconTransform;
        private float miniMapRadiusPx;
        private float miniMapPixelsPerCm = 1f; // current, smoothed toward the target each frame
        private readonly List<Image> trailDotPool = new List<Image>(); // grows as the trail grows, never shrinks - excess dots just get hidden
        private readonly List<Image> trailLinePool = new List<Image>(); // one fewer element than trailDotPool - a segment connects each consecutive pair

        private bool lastIsFlying;
        private bool hasLoggedInitialFlyingState;

        private void Awake()
        {
            LoadPersistedSettings();
            if (tello == null) tello = TelloConnection.Instance;
            roundedSprite = TelloUiKit.GetRoundedSprite(cardCornerRadiusPx);
            circleSprite = TelloUiKit.GetRoundedSprite(10000f); // deliberately huge - clamps to a circle inside GetRoundedSprite
            arrowSprite = TelloUiKit.GetArrowSprite();
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
        /// the screen's QuadHeight, positioned to the RIGHT with the standard gap,
        /// then angled toward the pilot - the mirror image of the left panel's
        /// angle (same magnitude, opposite sign, since this panel needs to turn the
        /// opposite way to still face the pilot from the other side).</summary>
        // =================================================================
        // RUNTIME-ADJUSTABLE SETTINGS (ecran de parametres)
        // =================================================================
        public float Gap { get => gap; set { gap = value; PositionRightOfScreen(); } }
        public float CockpitAngleDegrees { get => cockpitAngleDegrees; set { cockpitAngleDegrees = value; PositionRightOfScreen(); } }
        public bool PinInnerEdgeToScreenDepth { get => pinInnerEdgeToScreenDepth; set { pinInnerEdgeToScreenDepth = value; PositionRightOfScreen(); } }
        public float PanelDepthOffset { get => panelDepthOffset; set { panelDepthOffset = value; PositionRightOfScreen(); } }

        private const string PrefsPrefix = "TelloQuest_Settings_ActionLog_";

        private void LoadPersistedSettings()
        {
            gap = PlayerPrefs.GetFloat(PrefsPrefix + "Gap", gap);
            cockpitAngleDegrees = PlayerPrefs.GetFloat(PrefsPrefix + "CockpitAngle", cockpitAngleDegrees);
            pinInnerEdgeToScreenDepth = PlayerPrefs.GetInt(PrefsPrefix + "PinInnerEdge", pinInnerEdgeToScreenDepth ? 1 : 0) == 1;
            panelDepthOffset = PlayerPrefs.GetFloat(PrefsPrefix + "PanelDepth", panelDepthOffset);
        }

        /// <summary>Called by TelloSettingsScreen after writing new values via the properties above.</summary>
        public void SavePersistedSettings()
        {
            PlayerPrefs.SetFloat(PrefsPrefix + "Gap", gap);
            PlayerPrefs.SetFloat(PrefsPrefix + "CockpitAngle", cockpitAngleDegrees);
            PlayerPrefs.SetInt(PrefsPrefix + "PinInnerEdge", pinInnerEdgeToScreenDepth ? 1 : 0);
            PlayerPrefs.SetFloat(PrefsPrefix + "PanelDepth", panelDepthOffset);
            PlayerPrefs.Save();
        }

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

            float halfBandWidth = CanvasPixelWidth * scale * 0.5f;
            Quaternion rotation = Quaternion.Euler(0f, cockpitAngleDegrees, 0f);
            transform.localRotation = rotation;

            // Bord interne du panneau DROIT = son bord gauche, donc -halfBandWidth
            // dans le repere local du panneau.
            transform.localPosition = TelloUiKit.SolvePinnedPanelPosition(
                rotation,
                new Vector3(-halfBandWidth, 0f, 0f),
                videoScreen.QuadWidth * 0.5f + gap,
                halfBandWidth,
                pinInnerEdgeToScreenDepth,
                panelDepthOffset);
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

            UpdateMiniMap();
        }

        /// <summary>Eases the zoom toward its target scale (derived from
        /// MaxDistanceFromHomeEverCm, which never shrinks mid-flight - see that
        /// property's doc comment), then repositions every trail dot, connecting
        /// line segment, and the drone icon to match. Both pools only ever grow -
        /// once created, an element is reused (just hidden) rather than destroyed,
        /// since the trail itself only ever grows too within a flight.
        ///
        /// The connecting lines are what actually make this read as a path rather
        /// than a scatter of dots - a series of small, evenly-colored dots doesn't
        /// visually stitch itself into a route the way an explicit line between
        /// each pair of points does, especially once there are more than a
        /// handful of them.</summary>
        private int lastTrailCount = -1;
        private float lastDrawnPixelsPerCm = -1f;

        private void UpdateMiniMap()
        {
            if (miniMapContainer == null) return;

            float radius = Mathf.Max(tello.MaxDistanceFromHomeEverCm, miniMapMinDisplayRadiusCm);
            float targetPixelsPerCm = miniMapRadiusPx / radius;
            miniMapPixelsPerCm = Mathf.Lerp(miniMapPixelsPerCm, targetPixelsPerCm, Time.deltaTime * miniMapZoomSmoothing);

            IReadOnlyList<Vector2> trail = tello.FlightTrail;

            // L'icone du drone bouge en continu : elle, on la met a jour chaque frame.
            droneIconTransform.anchoredPosition = tello.EstimatedPositionCm * miniMapPixelsPerCm;
            droneIconTransform.localRotation = Quaternion.Euler(0f, 0f, -tello.Yaw);

            // La TRACE, en revanche, ne change que quand un point est ajoute ou quand le
            // zoom bouge encore. Avant, tous les points (jusqu'a 500) etaient
            // repositionnes a chaque frame, ET chacun appelait SetAsLastSibling() - une
            // reorganisation de hierarchie qui force un rebuild complet du canvas. 500
            // rebuilds par frame etait de loin l'appel le plus couteux du projet.
            // L'ordre de dessin est desormais fixe une fois pour toutes a la
            // construction (les lignes sont creees avant les points).
            bool zoomStillMoving = Mathf.Abs(miniMapPixelsPerCm - lastDrawnPixelsPerCm) > 0.0001f;
            if (trail.Count == lastTrailCount && !zoomStillMoving) return;
            lastTrailCount = trail.Count;
            lastDrawnPixelsPerCm = miniMapPixelsPerCm;

            // Line segments: one between each consecutive pair of points, so
            // trail.Count points need trail.Count - 1 segments.
            for (int i = 0; i < trail.Count - 1; i++)
            {
                if (i >= trailLinePool.Count)
                {
                    var lineGO = new GameObject($"TrailLine{i}", typeof(RectTransform), typeof(Image));
                    lineGO.transform.SetParent(miniMapContainer, false);
                    RectTransform lineRect = lineGO.GetComponent<RectTransform>();
                    Image lineImage = lineGO.GetComponent<Image>(); // no sprite set - default flat white UI rect, exactly what a thin line needs
                    lineImage.color = Color.white;
                    trailLinePool.Add(lineImage);
                }

                Vector2 p0 = trail[i] * miniMapPixelsPerCm;
                Vector2 p1 = trail[i + 1] * miniMapPixelsPerCm;
                Vector2 mid = (p0 + p1) * 0.5f;
                float length = Vector2.Distance(p0, p1);
                float angle = Mathf.Atan2(p1.y - p0.y, p1.x - p0.x) * Mathf.Rad2Deg;

                RectTransform segRect = trailLinePool[i].rectTransform;
                segRect.sizeDelta = new Vector2(length, 2f);
                segRect.anchoredPosition = mid;
                segRect.localRotation = Quaternion.Euler(0f, 0f, angle);
                trailLinePool[i].enabled = true;
            }
            for (int i = Mathf.Max(0, trail.Count - 1); i < trailLinePool.Count; i++) trailLinePool[i].enabled = false;

            // Dots: small markers at each sampled point, secondary to the lines now.
            for (int i = 0; i < trail.Count; i++)
            {
                if (i >= trailDotPool.Count)
                {
                    var dotGO = new GameObject($"TrailDot{i}", typeof(RectTransform), typeof(Image));
                    dotGO.transform.SetParent(miniMapContainer, false);
                    RectTransform dotRect = dotGO.GetComponent<RectTransform>();
                    dotRect.sizeDelta = new Vector2(2.5f, 2.5f);
                    Image dotImage = dotGO.GetComponent<Image>();
                    dotImage.sprite = circleSprite;
                    dotImage.color = Color.white; // persistent trail - always full opacity, unlike the log above (see class comment)
                    trailDotPool.Add(dotImage);
                }
                trailDotPool[i].enabled = true;
                trailDotPool[i].rectTransform.anchoredPosition = trail[i] * miniMapPixelsPerCm;
            }
            for (int i = trail.Count; i < trailDotPool.Count; i++) trailDotPool[i].enabled = false;

            // Une seule fois par redessin de trace, et non une fois par point.
            droneIconTransform.SetAsLastSibling();
        }

        private void AddEntry(string message, Color color)
        {
            entries.Add(new LogEntry
            {
                timestamp = DateTime.Now.ToString("HH:mm:ss"),
                message = message,
                color = color
            });
            while (entries.Count > maxEntries) entries.RemoveAt(0); // drop the oldest (top) first

            RebuildLogText();
        }

        /// <summary>Redraws every visible line each time an entry is added - cheap at
        /// this scale (max ~8 short lines), and simplest way to keep every line's
        /// fade correct as its position (age rank) shifts with each new entry.</summary>
        private void RebuildLogText()
        {
            if (logText == null) return;

            var lines = new List<string>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                float t = entries.Count > 1 ? i / (float)(entries.Count - 1) : 1f; // 0 = oldest/top, 1 = newest/bottom
                float alpha = Mathf.Lerp(oldestEntryAlpha, 1f, t);
                Color c = entries[i].color;
                c.a = alpha;
                string hex = ColorUtility.ToHtmlStringRGBA(c);
                lines.Add($"<color=#{hex}>{entries[i].timestamp}  {entries[i].message}</color>");
            }
            logText.text = string.Join("\n", lines);
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

            // ---- Top (75% of height): flight-path mini-map ----
            BuildLabel(canvasGO.transform, "Mini-map", 295f);

            var mapMaskGO = new GameObject("MiniMapMask", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            mapMaskGO.transform.SetParent(canvasGO.transform, false);
            RectTransform mapMaskRect = mapMaskGO.GetComponent<RectTransform>();
            mapMaskRect.sizeDelta = new Vector2(CanvasPixelWidth - 40f, 420f);
            mapMaskRect.anchoredPosition = new Vector2(0f, 60f);
            mapMaskGO.GetComponent<Image>().color = InstrumentBackground;
            // Deliberately uniform (X and Y share the same pixels-per-cm scale)
            // even though this area is a wide rectangle now, not square - a
            // non-uniform scale would stretch the true shape of the flight path.
            // Using the SMALLER dimension as the constraint (Min, not the
            // rectangle's own aspect ratio) means some horizontal margin goes
            // unused on a wide area like this - intentional, not a bug.
            miniMapRadiusPx = Mathf.Min(mapMaskRect.sizeDelta.x, mapMaskRect.sizeDelta.y) * 0.5f - 10f;

            var mapContainerGO = new GameObject("MiniMapContainer", typeof(RectTransform));
            mapContainerGO.transform.SetParent(mapMaskRect, false);
            miniMapContainer = mapContainerGO.GetComponent<RectTransform>();
            miniMapContainer.anchoredPosition = Vector2.zero;

            // Home marker - fixed at the container's origin (drawn once; unlike the
            // trail dots and drone icon, it never needs to move).
            var homeGO = new GameObject("HomeMarker", typeof(RectTransform), typeof(Image));
            homeGO.transform.SetParent(miniMapContainer, false);
            RectTransform homeRect = homeGO.GetComponent<RectTransform>();
            homeRect.sizeDelta = new Vector2(10f, 10f);
            homeRect.anchoredPosition = Vector2.zero;
            Image homeImage = homeGO.GetComponent<Image>();
            homeImage.sprite = circleSprite;
            homeImage.color = Amber;

            // Drone heading icon - the dart/kite arrow from TelloUiKit, not the
            // near-equilateral triangle glyph used elsewhere, since this one needs
            // to clearly show a precise heading rather than just "a direction".
            // Map itself never rotates (north-up, fixed) - only this icon spins
            // with yaw - see class comment on why a rotating map was avoided in VR.
            var droneIconGO = new GameObject("DroneIcon", typeof(RectTransform), typeof(Image));
            droneIconGO.transform.SetParent(miniMapContainer, false);
            droneIconTransform = droneIconGO.GetComponent<RectTransform>();
            droneIconTransform.sizeDelta = new Vector2(16f, 22f);
            Image droneIconImage = droneIconGO.GetComponent<Image>();
            droneIconImage.sprite = arrowSprite;
            droneIconImage.color = Ink;

            // ---- Divider, at the 75/25 boundary ----
            var dividerGO = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            dividerGO.transform.SetParent(canvasGO.transform, false);
            RectTransform dividerRect = dividerGO.GetComponent<RectTransform>();
            dividerRect.sizeDelta = new Vector2(CanvasPixelWidth - 40f, 1f);
            dividerRect.anchoredPosition = new Vector2(0f, -160f);
            dividerGO.GetComponent<Image>().color = PanelEdge;

            // ---- Bottom (25% of height): activity log ----
            BuildLabel(canvasGO.transform, "Activity Log", -185f);

            var logGO = new GameObject("LogText", typeof(RectTransform));
            logGO.transform.SetParent(canvasGO.transform, false);
            RectTransform logRect = logGO.GetComponent<RectTransform>();
            logRect.sizeDelta = new Vector2(CanvasPixelWidth - 24f, 110f);
            logRect.anchoredPosition = new Vector2(0f, -255f);
            logText = logGO.AddComponent<TextMeshProUGUI>();
            logText.fontSize = 11f;
            logText.color = InkDim;
            // Bottom-anchored on purpose: entries read oldest (top) to newest
            // (bottom) - see class comment. If there's ever more text than fits,
            // it should be the oldest line pushed off the TOP, never the newest
            // clipped off the bottom - BottomLeft alignment does that naturally,
            // where TopLeft would do the opposite.
            logText.alignment = TextAlignmentOptions.BottomLeft;
            logText.textWrappingMode = TextWrappingModes.Normal;
            logText.overflowMode = TextOverflowModes.Truncate;
            logText.text = "";
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
    }
}
