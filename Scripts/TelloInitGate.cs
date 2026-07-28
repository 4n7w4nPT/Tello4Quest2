using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Owns the app's top-level screen state: Menu (pre-flight checks + button
    /// legend), Piloting (video screen + banners, gamepad controls live), and
    /// Settings (adjust screen distance/size/opacity). Full screen swaps, not a
    /// navigable menu - there is no cursor, no selection, nothing to browse.
    ///
    /// Menu screen is landscape, split down the middle: LEFT half is the
    /// five-item pre-flight checklist + status readout, RIGHT half is the
    /// X-cross button legend. Wider than earlier portrait-ish versions,
    /// specifically so both halves are readable together without needing to
    /// scan up and down.
    ///
    ///   Menu screen:
    ///     South - enter piloting mode, only once all five checks are green.
    ///     West  - request a connection to a Tello's Wi-Fi hotspot (SSID
    ///             starting with "TELLO-") - see ConnectToTelloWifi. Replaces
    ///             an earlier "open gallery" attempt that never worked (no
    ///             reliable way to open a real gallery viewer from a
    ///             third-party Quest app was ever found).
    ///     East  - quit the app. Only reachable from this screen, which means
    ///             the drone is always grounded when this fires.
    ///     North - open the Settings screen (see TelloSettingsScreen).
    ///
    ///   Piloting screen:
    ///     Options/Start - return to the menu screen, but ONLY if the drone is
    ///                     landed. Pressed mid-flight: blocked, haptic pulse only.
    ///
    ///   Settings screen: handled by TelloSettingsScreen itself (South = save
    ///   and return, East = discard and return) - this class only hands off to
    ///   it and takes screen control back via ExitSettings().
    ///
    /// The Tello connection is never torn down when swapping screens - only
    /// quitting the app (from the menu) closes it. All five checks keep
    /// evaluating every frame regardless of which screen is showing, so the
    /// menu screen always reflects live status the instant it's shown again.
    ///
    /// World-locked, same as the flight display: positioned once in Start(),
    /// never moves after that.
    ///
    /// Setup: TelloVideoScreen, TelloStatusPanel/TelloOptionsPanel/spatial/
    /// action-log panels, and TelloSettingsScreen should all start INACTIVE in
    /// the scene, each with their own "Positioned Externally" checkbox turned
    /// ON. This script activates and reveals them as needed - see
    /// TelloVideoDisplay.
    ///
    /// The Wi-Fi auto-connect (West button) requires a small compiled Java
    /// helper (TelloWifiConnector.java) alongside this script - see
    /// ConnectToTelloWifi's doc comment for exactly where it goes and why a
    /// plain C#/AndroidJavaProxy approach doesn't work for this specific API.
    /// </summary>
    public class TelloInitGate : MonoBehaviour
    {
        public enum AppState { Menu, Piloting, Settings }

        [SerializeField] private TelloConnection tello;
        [SerializeField] private TelloGamepadController gamepadController;
        [SerializeField] private TelloVideoDecoder videoDecoder;
        [SerializeField] private Transform vrCamera;

        [Header("=== WHAT TO REVEAL WHILE PILOTING ===")]
        [SerializeField] private GameObject videoScreenObject;
        [SerializeField] private TelloVideoDisplay videoScreen;
        [SerializeField] private GameObject statusPanelObject;
        [SerializeField] private TelloStatusPanel statusPanel;
        [SerializeField] private GameObject optionsPanelObject;
        [SerializeField] private TelloOptionsPanel optionsPanel;
        [SerializeField] private GameObject spatialPanelObject;
        [SerializeField] private TelloSpatialPanel spatialPanel;
        [SerializeField] private GameObject actionLogPanelObject;
        [SerializeField] private TelloActionLogPanel actionLogPanel;

        [Header("=== SETTINGS SCREEN ===")]
        [SerializeField] private GameObject settingsScreenObject;
        [SerializeField] private TelloSettingsScreen settingsScreen;

        [Header("=== FIXED PLACEMENT (same formula as TelloVideoDisplay) ===")]
        [SerializeField] private float distanceFromCamera = 1.2f;
        [SerializeField] private float assumedEyeHeightMeters = 1.6f;
        [SerializeField] private float verticalOffset = -0.3f;
        [SerializeField] private float worldWidth = 2.1f; // 1.4 * 1.5 - enlarged per feedback that the landscape layout felt small

        [Header("=== CARD SHAPE (matches the flight display banners) ===")]
        [SerializeField] private float cornerRadiusPx = 20f;

        [Header("=== WI-FI AUTO-CONNECT (West button) ===")]
        [Tooltip("SSID prefix to match - the Tello's own hotspot names always start with this.")]
        // CS0414 disabled: only used inside the Android-only branch of
        // ConnectToTelloWifi() (#if UNITY_ANDROID && !UNITY_EDITOR) - the
        // Editor's own compile pass always has UNITY_EDITOR defined regardless
        // of active platform, so from its point of view this field is never
        // read, hence the false-positive warning. Guarding the field
        // declaration itself with the same #if would silence it, but would
        // also hide it from the Inspector entirely (Inspector always runs
        // in-Editor) - worse than a suppressed warning, since the SSID prefix
        // needs to stay configurable.
#pragma warning disable 0414
        [SerializeField] private string telloSsidPrefix = "TELLO-";
#pragma warning restore 0414

        [Header("=== FONTS (optional - falls back to TMP default if unassigned) ===")]
        [Tooltip("Stencil/display face for the title - e.g. Big Shoulders Stencil, imported as a TMP Font Asset.")]
        [SerializeField] private TMP_FontAsset displayFont;
        [Tooltip("Body face for check-row labels and legend actions - e.g. IBM Plex Sans.")]
        [SerializeField] private TMP_FontAsset bodyFont;
        [Tooltip("Utility/mono face for step tags, status text, and button prompts - e.g. IBM Plex Mono.")]
        [SerializeField] private TMP_FontAsset monoFont;

        [Header("=== OPTIONAL ICON FONT (PS4/Xbox button glyphs) ===")]
        [Tooltip("A TMP Font Asset built from an icon font where specific characters render as button glyphs (e.g. the Stephan Dube PS4/Xbox font). Leave unassigned to keep plain text prompts - nothing breaks either way.")]
        [SerializeField] private TMP_FontAsset iconFont;
        [Tooltip("The character that renders as this button's icon in Icon Font, for whichever brand is detected. Confirmed against the Stephan Dube PS4/Xbox font's character map (uppercase = PlayStation face buttons, lowercase = Xbox face buttons).")]
        [SerializeField] private string iconGlyphPlayStationSouth = "D"; // Cross
        [SerializeField] private string iconGlyphPlayStationNorth = "B"; // Triangle
        [SerializeField] private string iconGlyphPlayStationEast = "C";  // Circle
        [SerializeField] private string iconGlyphPlayStationWest = "A";  // Square
        [SerializeField] private string iconGlyphXboxSouth = "d"; // A
        [SerializeField] private string iconGlyphXboxNorth = "b"; // Y
        [SerializeField] private string iconGlyphXboxEast = "c";  // B
        [SerializeField] private string iconGlyphXboxWest = "a";  // X

        // Landscape now: wider than tall, split down the middle. Was a single
        // 700x840 portrait-ish column before.
        private const float CanvasPixelWidth = 1240f;
        private const float CanvasPixelHeight = 620f;
        private const float LeftCenterX = -310f;  // center of the left (checklist) half
        private const float RightCenterX = 310f;  // center of the right (X-cross) half
        private const float CrossSize = 400f; // SQUARE cross zone - a wide/flat rectangle mathematically squishes the top/bottom wedges and over-sizes the left/right ones; a square gives four genuinely equal wedges

        // Aviation-instrument palette - a warm amber accent (cockpit warning-light
        // amber) rather than the generic dark+neon-cyan default, with red/green
        // reserved strictly for the pass/fail semantics of the checklist itself.
        private static readonly Color PanelBg = HexColor("#15181B");
        private static readonly Color PanelEdge = HexColor("#262B30");
        private static readonly Color Ink = HexColor("#EDEAE3");
        private static readonly Color InkDim = HexColor("#8A8F94");
        private static readonly Color Amber = HexColor("#E8A33D");
        private static readonly Color AmberDim = HexColor("#3A2F1A");
        private static readonly Color Ok = HexColor("#4CAF6D");
        private static readonly Color OkDim = HexColor("#1C3226");
        private static readonly Color Fail = HexColor("#D9534F");
        private static readonly Color FailDim = HexColor("#3A201F");
        private static readonly Color KnobOff = new Color(0.85f, 0.83f, 0.80f);
        private static readonly Color KnobOn = new Color(0.75f, 0.96f, 0.82f);

        private static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }

        private Sprite roundedSprite;
        private Sprite circleSprite;

        private GameObject canvasGO;
        private Image statusBg;
        private TextMeshProUGUI statusText;
        private CanvasGroup canvasGroup;

        private struct SwitchRow
        {
            public Image track;
            public RectTransform knob;
            public Image knobImage;
            public float anim; // 0 = off, 1 = on - eased toward target each frame
        }

        private SwitchRow bluetoothSwitch;
        private SwitchRow gamepadSwitch;
        private SwitchRow wifiSwitch;
        private SwitchRow telloSwitch;
        private SwitchRow videoSwitch;

        private AppState state = AppState.Menu;
        private bool bluetoothOk, gamepadOk, wifiOk, telloOk, videoOk;

        // Bluetooth/Wi-Fi-enabled are read via Android system calls, which cost
        // real JNI overhead - throttled to a few times a second rather than
        // every frame like the other (cheap, plain-C#) checks. A half-second
        // lag in reflecting a toggle change is completely fine here.
        private float nativeCheckTimer;
        private const float NativeCheckInterval = 0.5f;

        private TextMeshProUGUI flyPrompt;
        private TextMeshProUGUI wifiConnectPrompt;
        private TextMeshProUGUI quitPrompt;
        private TextMeshProUGUI settingsPrompt;

        // Debounce: a single physical button press should never fire a menu
        // action twice. Guards against Input System edge cases where
        // wasPressedThisFrame can read true across more than one Update() (e.g.
        // with "process events in both fixed and dynamic update"), and against
        // a person mashing a button that doesn't appear to respond right away.
        private const float MenuActionCooldown = 0.5f;
        private float lastMenuActionTime = -10f;
        private bool CanFireMenuAction => Time.time - lastMenuActionTime > MenuActionCooldown;

        public AppState State => state;
        public bool IsPiloting => state == AppState.Piloting;
        public bool AllChecksOk => bluetoothOk && gamepadOk && wifiOk && telloOk && videoOk;

        private void Awake()
        {
            if (tello == null) tello = TelloConnection.Instance;
            roundedSprite = TelloUiKit.GetRoundedSprite(cornerRadiusPx);
            circleSprite = TelloUiKit.GetRoundedSprite(10000f); // deliberately huge - clamps to a circle inside GetRoundedSprite; stretched non-uniformly (Image.Type.Simple) it becomes the pill shape for the switch tracks
            BuildUI();
        }

        private void Start()
        {
            // Re-check here, not just in Awake(): Unity doesn't guarantee Awake()
            // order across different GameObjects, so if this ran before
            // TelloConnection's own Awake() (which sets Instance), tello would
            // stay null forever - explains the intermittent "checklist gets
            // stuck, only fixed by a full relaunch" report, since a relaunch just
            // re-rolls the random ordering. Start() is guaranteed to run after
            // every Awake() in the scene has completed, so this always closes
            // the gap.
            if (tello == null) tello = TelloConnection.Instance;

            if (vrCamera == null) return;

            transform.position = TelloUiKit.ComputeFixedPosition(vrCamera, distanceFromCamera, assumedEyeHeightMeters, verticalOffset);
            transform.rotation = TelloUiKit.ComputeFixedRotation(vrCamera);
        }

        // =================================================================
        // BUTTON PROMPTS (brand-aware, optional icon-font glyphs)
        // =================================================================
        private void RefreshButtonPrompts()
        {
            TelloUiKit.GamepadBrand brand = TelloUiKit.CurrentGamepadBrand();

            SetPrompt(flyPrompt, brand, "south");
            SetPrompt(settingsPrompt, brand, "north");
            SetPrompt(quitPrompt, brand, "east");
            SetPrompt(wifiConnectPrompt, brand, "west");
        }

        public TMP_FontAsset IconFont => iconFont;

        /// <summary>Public entry point for other screens (TelloSettingsScreen's footer)
        /// to resolve the same button text/glyph this screen uses, without duplicating
        /// the icon font + 8 glyph fields. isIconGlyph tells the caller whether to also
        /// swap the target TextMeshPro's font to IconFont.</summary>
        public string ResolveButtonText(TelloUiKit.GamepadBrand brand, string position, out bool isIconGlyph)
        {
            string glyph = GetIconGlyph(brand, position);
            if (iconFont != null && !string.IsNullOrEmpty(glyph))
            {
                isIconGlyph = true;
                return glyph;
            }
            isIconGlyph = false;
            return TelloUiKit.ButtonName(brand, position);
        }

        private void SetPrompt(TextMeshProUGUI target, TelloUiKit.GamepadBrand brand, string position)
        {
            string glyph = GetIconGlyph(brand, position);
            if (iconFont != null && !string.IsNullOrEmpty(glyph))
            {
                target.font = iconFont;
                target.fontSize = 30f;
                target.text = glyph;
            }
            else
            {
                ApplyFont(target, monoFont);
                target.fontSize = 13f;
                target.text = TelloUiKit.ButtonName(brand, position);
            }
        }

        private string GetIconGlyph(TelloUiKit.GamepadBrand brand, string position)
        {
            if (brand == TelloUiKit.GamepadBrand.PlayStation)
            {
                return position switch
                {
                    "south" => iconGlyphPlayStationSouth,
                    "north" => iconGlyphPlayStationNorth,
                    "east" => iconGlyphPlayStationEast,
                    "west" => iconGlyphPlayStationWest,
                    _ => ""
                };
            }
            if (brand == TelloUiKit.GamepadBrand.Xbox)
            {
                return position switch
                {
                    "south" => iconGlyphXboxSouth,
                    "north" => iconGlyphXboxNorth,
                    "east" => iconGlyphXboxEast,
                    "west" => iconGlyphXboxWest,
                    _ => ""
                };
            }
            return ""; // no icon glyphs for a generic/unrecognized pad - text prompt only
        }

        // =================================================================
        // UI CONSTRUCTION
        // =================================================================
        private void BuildUI()
        {
            canvasGO = new GameObject("TelloInitCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(CanvasPixelWidth, CanvasPixelHeight);
            canvasGO.transform.localScale = Vector3.one * (worldWidth / CanvasPixelWidth);
            canvasGroup = canvasGO.AddComponent<CanvasGroup>();

            // Single background for the ENTIRE canvas - a separate fill per half
            // would create a visible seam, same lesson learned from the old
            // legend-strip background before the X-cross redesign.
            TelloUiKit.BuildFullRectBackground(canvasGO.transform, roundedSprite, PanelBg);

            // Vertical divider between the two halves.
            var centerDividerGO = new GameObject("CenterDivider", typeof(RectTransform), typeof(Image));
            centerDividerGO.transform.SetParent(canvasGO.transform, false);
            RectTransform centerDividerRect = centerDividerGO.GetComponent<RectTransform>();
            centerDividerRect.sizeDelta = new Vector2(1f, CanvasPixelHeight - 30f);
            centerDividerRect.anchoredPosition = Vector2.zero;
            centerDividerGO.GetComponent<Image>().color = PanelEdge;

            BuildLeftChecklist(canvasGO.transform);
            BuildRightLegend(canvasGO.transform);
        }

        /// <summary>LEFT half: nameplate header, five checklist rows, status readout.
        /// Cursor-based top-down layout within this half only.</summary>
        private void BuildLeftChecklist(Transform parent)
        {
            float cursorY = CanvasPixelHeight / 2f - 25f;

            var markGO = new GameObject("Mark", typeof(RectTransform), typeof(Image));
            markGO.transform.SetParent(parent, false);
            RectTransform markRect = markGO.GetComponent<RectTransform>();
            markRect.sizeDelta = new Vector2(10f, 10f);
            markRect.anchoredPosition = new Vector2(LeftCenterX - 260f, cursorY);
            Image markImage = markGO.GetComponent<Image>();
            markImage.sprite = circleSprite;
            markImage.type = Image.Type.Simple;
            markImage.color = Amber;

            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(parent, false);
            RectTransform titleRect = titleGO.GetComponent<RectTransform>();
            titleRect.sizeDelta = new Vector2(260f, 40f);
            titleRect.anchoredPosition = new Vector2(LeftCenterX - 95f, cursorY);
            var title = titleGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(title, displayFont);
            title.text = "TELLO4QUEST2";
            title.fontSize = 24f;
            title.color = Ink;
            title.alignment = TextAlignmentOptions.MidlineLeft;

            var subtitleGO = new GameObject("Subtitle", typeof(RectTransform));
            subtitleGO.transform.SetParent(parent, false);
            RectTransform subtitleRect = subtitleGO.GetComponent<RectTransform>();
            subtitleRect.sizeDelta = new Vector2(170f, 30f);
            subtitleRect.anchoredPosition = new Vector2(LeftCenterX + 175f, cursorY);
            var subtitle = subtitleGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(subtitle, monoFont);
            subtitle.text = "PRE FLIGHT CHECK";
            subtitle.fontSize = 11f;
            subtitle.color = InkDim;
            subtitle.alignment = TextAlignmentOptions.MidlineRight;
            subtitle.textWrappingMode = TextWrappingModes.NoWrap;
            subtitle.overflowMode = TextOverflowModes.Ellipsis;

            cursorY -= 50f;
            BuildDivider(parent, cursorY, 580f, LeftCenterX);
            cursorY -= 30f;

            const float rowHeight = 56f;
            const float rowGap = 20f;

            bluetoothSwitch = BuildSwitchRow(parent, "STEP 1", "Bluetooth enabled", cursorY, LeftCenterX, 580f);
            cursorY -= rowHeight;
            BuildDivider(parent, cursorY, 580f, LeftCenterX);
            cursorY -= rowGap;

            gamepadSwitch = BuildSwitchRow(parent, "STEP 2", "Gamepad connected", cursorY, LeftCenterX, 580f);
            cursorY -= rowHeight;
            BuildDivider(parent, cursorY, 580f, LeftCenterX);
            cursorY -= rowGap;

            wifiSwitch = BuildSwitchRow(parent, "STEP 3", "Wi-Fi enabled", cursorY, LeftCenterX, 580f);
            cursorY -= rowHeight;
            BuildDivider(parent, cursorY, 580f, LeftCenterX);
            cursorY -= rowGap;

            telloSwitch = BuildSwitchRow(parent, "STEP 4", "Tello Wi-Fi connected", cursorY, LeftCenterX, 580f);
            cursorY -= rowHeight;
            BuildDivider(parent, cursorY, 580f, LeftCenterX);
            cursorY -= rowGap;

            videoSwitch = BuildSwitchRow(parent, "STEP 5", "Video feed connected", cursorY, LeftCenterX, 580f);
            cursorY -= rowHeight;
            cursorY -= 20f;

            var statusBgGO = new GameObject("StatusBar", typeof(RectTransform), typeof(Image));
            statusBgGO.transform.SetParent(parent, false);
            RectTransform statusBgRect = statusBgGO.GetComponent<RectTransform>();
            statusBgRect.sizeDelta = new Vector2(560f, 46f);
            statusBgRect.anchoredPosition = new Vector2(LeftCenterX, cursorY);
            statusBg = statusBgGO.GetComponent<Image>();
            statusBg.sprite = roundedSprite;
            statusBg.type = Image.Type.Sliced;
            statusBg.color = AmberDim;

            var statusGO = new GameObject("StatusText", typeof(RectTransform));
            statusGO.transform.SetParent(statusBgRect, false);
            RectTransform statusRect = statusGO.GetComponent<RectTransform>();
            statusRect.sizeDelta = new Vector2(540f, 40f);
            statusRect.anchoredPosition = Vector2.zero;
            statusText = statusGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(statusText, monoFont);
            statusText.fontSize = 15f;
            statusText.color = Amber;
            statusText.alignment = TextAlignmentOptions.Center;
            statusText.textWrappingMode = TextWrappingModes.NoWrap;
            statusText.overflowMode = TextOverflowModes.Ellipsis;
        }

        /// <summary>RIGHT half: the X-cross button legend - unchanged geometry from
        /// before (a true square gives four genuinely equal wedges), just recentered
        /// on this half instead of the whole canvas.</summary>
        private void BuildRightLegend(Transform parent)
        {
            float half = CrossSize / 2f;
            float diagLength = CrossSize * 1.41421356f; // side * sqrt(2)

            BuildDiagonalLine(parent, RightCenterX, 0f, diagLength, 45f);
            BuildDiagonalLine(parent, RightCenterX, 0f, diagLength, -45f);

            // Wedge content, placed along each wedge's bisector at ~55% of the way
            // from center to the edge midpoint - keeps text clear of the diagonals.
            settingsPrompt = BuildCrossItem(parent, "Settings", RightCenterX, half * 0.55f);          // North
            wifiConnectPrompt = BuildCrossItem(parent, "Connect Wi-Fi", RightCenterX - half * 0.55f, 0f); // West
            quitPrompt = BuildCrossItem(parent, "Quit app", RightCenterX + half * 0.55f, 0f);          // East
            flyPrompt = BuildCrossItem(parent, "Fly", RightCenterX, -half * 0.55f);                    // South
        }

        private void ApplyFont(TextMeshProUGUI text, TMP_FontAsset font)
        {
            if (font != null) text.font = font;
        }

        /// <summary>Thin horizontal hairline, used between checklist rows and above the legend strip.</summary>
        private void BuildDivider(Transform parent, float y, float width, float centerX = 0f)
        {
            var lineGO = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(parent, false);
            RectTransform lineRect = lineGO.GetComponent<RectTransform>();
            lineRect.sizeDelta = new Vector2(width, 1f);
            lineRect.anchoredPosition = new Vector2(centerX, y);
            Image lineImage = lineGO.GetComponent<Image>();
            lineImage.color = PanelEdge;
        }

        /// <summary>A hairline rotated to form one arm of the X-cross - same line style as
        /// BuildDivider, just angled and centered on the crossing point.</summary>
        private void BuildDiagonalLine(Transform parent, float centerX, float centerY, float length, float angleDegrees)
        {
            var lineGO = new GameObject("DiagonalDivider", typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(parent, false);
            RectTransform lineRect = lineGO.GetComponent<RectTransform>();
            lineRect.sizeDelta = new Vector2(length, 1f);
            lineRect.anchoredPosition = new Vector2(centerX, centerY);
            lineRect.localEulerAngles = new Vector3(0f, 0f, angleDegrees);
            Image lineImage = lineGO.GetComponent<Image>();
            lineImage.color = PanelEdge;
        }

        private SwitchRow BuildSwitchRow(Transform parent, string stepTag, string label, float y, float centerX, float rowWidth)
        {
            var rowGO = new GameObject($"Row_{stepTag}", typeof(RectTransform));
            rowGO.transform.SetParent(parent, false);
            RectTransform rowRect = rowGO.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(rowWidth, 56f);
            rowRect.anchoredPosition = new Vector2(centerX, y);

            float half = rowWidth / 2f;

            var tagGO = new GameObject("StepTag", typeof(RectTransform));
            tagGO.transform.SetParent(rowGO.transform, false);
            RectTransform tagRect = tagGO.GetComponent<RectTransform>();
            tagRect.sizeDelta = new Vector2(70f, 40f);
            tagRect.anchoredPosition = new Vector2(-half + 40f, 0f);
            var tagText = tagGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(tagText, monoFont);
            tagText.fontSize = 12f;
            tagText.color = InkDim;
            tagText.alignment = TextAlignmentOptions.MidlineLeft;
            tagText.text = stepTag;

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(rowGO.transform, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(340f, 40f);
            labelRect.anchoredPosition = new Vector2(0f, 0f);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(labelText, bodyFont);
            labelText.fontSize = 16f;
            labelText.color = Ink;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;
            labelText.text = label;

            var trackGO = new GameObject("Track", typeof(RectTransform), typeof(Image));
            trackGO.transform.SetParent(rowGO.transform, false);
            RectTransform trackRect = trackGO.GetComponent<RectTransform>();
            trackRect.sizeDelta = new Vector2(54f, 26f);
            trackRect.anchoredPosition = new Vector2(half - 30f, 0f);
            Image track = trackGO.GetComponent<Image>();
            track.sprite = circleSprite;
            track.type = Image.Type.Simple; // stretched circle -> pill shape
            track.color = FailDim;

            var knobGO = new GameObject("Knob", typeof(RectTransform), typeof(Image));
            knobGO.transform.SetParent(trackRect, false);
            RectTransform knobRect = knobGO.GetComponent<RectTransform>();
            knobRect.sizeDelta = new Vector2(20f, 20f);
            knobRect.anchoredPosition = new Vector2(-14f, 0f);
            Image knobImage = knobGO.GetComponent<Image>();
            knobImage.sprite = circleSprite;
            knobImage.type = Image.Type.Simple;
            knobImage.color = KnobOff;

            return new SwitchRow { track = track, knob = knobRect, knobImage = knobImage, anim = 0f };
        }

        /// <summary>One wedge's content: action word, a static "Press" label, and the
        /// button identifier row (icon glyph or bare button name - see SetPrompt),
        /// stacked and centered at the given point within the X-cross.</summary>
        private TextMeshProUGUI BuildCrossItem(Transform parent, string action, float x, float y)
        {
            var itemGO = new GameObject($"Legend_{action}", typeof(RectTransform));
            itemGO.transform.SetParent(parent, false);
            RectTransform itemRect = itemGO.GetComponent<RectTransform>();
            itemRect.sizeDelta = new Vector2(160f, 84f);
            itemRect.anchoredPosition = new Vector2(x, y);

            var actionGO = new GameObject("Action", typeof(RectTransform));
            actionGO.transform.SetParent(itemGO.transform, false);
            RectTransform actionRect = actionGO.GetComponent<RectTransform>();
            actionRect.sizeDelta = new Vector2(160f, 26f);
            actionRect.anchoredPosition = new Vector2(0f, 24f);
            var actionText = actionGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(actionText, bodyFont);
            actionText.fontSize = 17f;
            actionText.fontStyle = FontStyles.Bold;
            actionText.color = Ink;
            actionText.alignment = TextAlignmentOptions.Center;
            actionText.textWrappingMode = TextWrappingModes.NoWrap;
            actionText.overflowMode = TextOverflowModes.Ellipsis;
            actionText.text = action;

            var pressGO = new GameObject("PressLabel", typeof(RectTransform));
            pressGO.transform.SetParent(itemGO.transform, false);
            RectTransform pressRect = pressGO.GetComponent<RectTransform>();
            pressRect.sizeDelta = new Vector2(160f, 20f);
            pressRect.anchoredPosition = new Vector2(0f, 2f);
            var pressText = pressGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(pressText, monoFont);
            pressText.fontSize = 11f;
            pressText.color = InkDim;
            pressText.alignment = TextAlignmentOptions.Center;
            pressText.textWrappingMode = TextWrappingModes.NoWrap;
            pressText.overflowMode = TextOverflowModes.Ellipsis;
            pressText.text = "PRESS";

            var promptGO = new GameObject("Prompt", typeof(RectTransform));
            promptGO.transform.SetParent(itemGO.transform, false);
            RectTransform promptRect = promptGO.GetComponent<RectTransform>();
            promptRect.sizeDelta = new Vector2(160f, 36f);
            promptRect.anchoredPosition = new Vector2(0f, -24f);
            var promptText = promptGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(promptText, monoFont);
            promptText.fontSize = 13f;
            promptText.color = Ink;
            promptText.alignment = TextAlignmentOptions.Center;
            promptText.textWrappingMode = TextWrappingModes.NoWrap;
            promptText.overflowMode = TextOverflowModes.Ellipsis;
            promptText.text = "";

            return promptText;
        }

        // =================================================================
        // CHECKS + INPUT
        // =================================================================
        private void Update()
        {
            UpdateChecks();

            Gamepad pad = TelloUiKit.GetActiveGamepad();

            if (state == AppState.Menu)
            {
                RefreshButtonPrompts();

                if (pad != null && CanFireMenuAction)
                {
                    if (pad.buttonSouth.wasPressedThisFrame && AllChecksOk) { EnterPiloting(); lastMenuActionTime = Time.time; }
                    // West (Connect Wi-Fi) is intentionally inactive for now - the
                    // auto-connect flow (TelloWifiConnector.java + WifiNetworkSpecifier)
                    // turned out to be more trouble than it was worth in testing. The
                    // legend card stays visible (still shows "Connect Wi-Fi" + the West
                    // prompt) so the layout doesn't need to change again if this comes
                    // back later - it's just a no-op for now.
                    else if (pad.buttonEast.wasPressedThisFrame) { QuitApp(); lastMenuActionTime = Time.time; }
                    else if (pad.buttonNorth.wasPressedThisFrame) { EnterSettings(); lastMenuActionTime = Time.time; }
                }
            }
            else if (state == AppState.Piloting)
            {
                if (pad != null && pad.startButton.wasPressedThisFrame)
                {
                    if (tello != null && tello.IsFlying)
                    {
                        // Blocked: land first. No visual alert yet (function-first pass) -
                        // a haptic pulse is enough to confirm the button press registered
                        // and nothing happened, rather than it looking unresponsive.
                        Debug.Log("[TelloInitGate] Can't return to the menu while flying - land first.");
                        gamepadController?.TriggerHaptics(0.6f, 0.2f);
                    }
                    else
                    {
                        ReturnToMenu();
                    }
                }
            }
            // Settings state: TelloSettingsScreen reads its own input directly and
            // calls back into ExitSettings() when done - nothing to do here.
        }

        /// <summary>Evaluates all five checks every frame regardless of which screen is
        /// showing, so the menu always reflects live status the instant it's revealed
        /// again - no re-waiting after a flight. Only touches the visuals (switches,
        /// status bar) while the menu is actually the one on screen.</summary>
        private void UpdateChecks()
        {
            nativeCheckTimer -= Time.deltaTime;
            if (nativeCheckTimer <= 0f)
            {
                nativeCheckTimer = NativeCheckInterval;
                bluetoothOk = CheckBluetoothEnabled();
                wifiOk = CheckWifiEnabled();
            }

            gamepadOk = gamepadController != null && gamepadController.IsGamepadConnected;
            telloOk = tello != null && tello.IsConnected;
            videoOk = videoDecoder != null && videoDecoder.FramesDecodedTotal > 0;

            if (state != AppState.Menu) return;

            UpdateSwitch(ref bluetoothSwitch, bluetoothOk);
            UpdateSwitch(ref gamepadSwitch, gamepadOk);
            UpdateSwitch(ref wifiSwitch, wifiOk);
            UpdateSwitch(ref telloSwitch, telloOk);
            UpdateSwitch(ref videoSwitch, videoOk);

            if (AllChecksOk)
            {
                statusText.text = "READY TO TAKE OFF";
                statusText.color = Ok;
                statusBg.color = OkDim;
            }
            else
            {
                string missing;
                if (!bluetoothOk) missing = "bluetooth";
                else if (!gamepadOk) missing = "gamepad";
                else if (!wifiOk) missing = "wifi";
                else if (!telloOk) missing = "Tello wifi";
                else missing = "video";
                statusText.text = $"WAITING FOR: {missing.ToUpperInvariant()}";
                statusText.color = Amber;
                statusBg.color = AmberDim;
            }
        }

        /// <summary>Slides the knob and cross-fades track/knob colors toward the on/off
        /// state, with a gentle pulse on the track while off.</summary>
        private void UpdateSwitch(ref SwitchRow sw, bool on)
        {
            float target = on ? 1f : 0f;
            sw.anim = Mathf.MoveTowards(sw.anim, target, Time.deltaTime * 4f);
            sw.knob.anchoredPosition = new Vector2(Mathf.Lerp(-14f, 14f, sw.anim), 0f);
            sw.knobImage.color = Color.Lerp(KnobOff, KnobOn, sw.anim);

            if (on)
            {
                sw.track.color = Color.Lerp(FailDim, OkDim, sw.anim);
            }
            else
            {
                float pulse = (Mathf.Sin(Time.time * 4f) + 1f) * 0.5f;
                sw.track.color = Color.Lerp(FailDim, Fail, pulse * 0.5f);
            }
        }

        /// <summary>Reads the headset's system Bluetooth-enabled state directly - not
        /// whether a gamepad happens to be connected (that's the separate,
        /// existing gamepad check), just whether the radio itself is on.</summary>
        private bool CheckBluetoothEnabled()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var bluetoothAdapterClass = new AndroidJavaClass("android.bluetooth.BluetoothAdapter");
                using var adapter = bluetoothAdapterClass.CallStatic<AndroidJavaObject>("getDefaultAdapter");
                return adapter != null && adapter.Call<bool>("isEnabled");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloInitGate] Bluetooth-enabled check failed: {e.Message}");
                return false;
            }
#else
            return true; // Editor fallback - don't block testing on a check that doesn't apply outside a real device
#endif
        }

        /// <summary>Reads the headset's system Wi-Fi-enabled state directly - not
        /// whether it's connected to the Tello specifically (that's the separate,
        /// existing "Tello Wi-Fi connected" check), just whether the radio is on.</summary>
        private bool CheckWifiEnabled()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var wifiManager = currentActivity.Call<AndroidJavaObject>("getSystemService", "wifi");
                return wifiManager != null && wifiManager.Call<bool>("isWifiEnabled");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloInitGate] Wifi-enabled check failed: {e.Message}");
                return false;
            }
#else
            return true;
#endif
        }

        // =================================================================
        // SCREEN TRANSITIONS
        // =================================================================
        private void EnterPiloting()
        {
            state = AppState.Piloting;
            StartCoroutine(FadeOutThenReveal());
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
            canvasGO.SetActive(false); // stop rendering the menu - this GameObject (and Update()) stays active so the Start-button return path keeps working

            RevealFlightDisplay();
        }

        private void ReturnToMenu()
        {
            state = AppState.Menu;

            // Clear any stale stick values so nothing lingers into the next takeoff -
            // the periodic rc sender in TelloConnection would otherwise keep re-sending
            // whatever roll/pitch/throttle/yaw were last set here.
            if (tello != null) tello.SetRC(0, 0, 0, 0);

            HideFlightDisplay();

            canvasGO.SetActive(true);
            canvasGroup.alpha = 0f;
            StartCoroutine(FadeIn());
        }

        private System.Collections.IEnumerator FadeIn()
        {
            float duration = 0.35f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                canvasGroup.alpha = Mathf.Clamp01(elapsed / duration);
                yield return null;
            }
            canvasGroup.alpha = 1f;
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

            if (spatialPanelObject != null) spatialPanelObject.SetActive(true);
            if (spatialPanel != null) spatialPanel.RevealNow();

            if (actionLogPanelObject != null) actionLogPanelObject.SetActive(true);
            if (actionLogPanel != null) actionLogPanel.RevealNow();
        }

        private void HideFlightDisplay()
        {
            if (videoScreenObject != null) videoScreenObject.SetActive(false);
            if (statusPanelObject != null) statusPanelObject.SetActive(false);
            if (optionsPanelObject != null) optionsPanelObject.SetActive(false);
            if (spatialPanelObject != null) spatialPanelObject.SetActive(false);
            if (actionLogPanelObject != null) actionLogPanelObject.SetActive(false);
        }

        private void EnterSettings()
        {
            state = AppState.Settings;
            canvasGO.SetActive(false);

            if (settingsScreenObject != null) settingsScreenObject.SetActive(true);
            else Debug.LogWarning("[TelloInitGate] Settings Screen Object not assigned - Settings screen will never appear.");

            if (settingsScreen != null) settingsScreen.RevealAt(transform.position, transform.rotation);
            else Debug.LogWarning("[TelloInitGate] Settings Screen (component) not assigned - Settings screen will never appear.");
        }

        /// <summary>Called by TelloSettingsScreen once the pilot saves or cancels - hands screen control back to the menu.</summary>
        public void ExitSettings()
        {
            state = AppState.Menu;
            if (settingsScreenObject != null) settingsScreenObject.SetActive(false);
            canvasGO.SetActive(true);
        }

        // =================================================================
        // MENU ACTIONS (West / East)
        // =================================================================
        /// <summary>
        /// Requests a connection to a Wi-Fi network whose SSID starts with
        /// telloSsidPrefix ("TELLO-" by default), via Android's modern
        /// WifiNetworkSpecifier API (API 29+) - the correct, current way for an
        /// app to connect to a specific device's own hotspot without needing the
        /// location permission the older scan-based APIs require. Android shows a
        /// one-time system confirmation dialog the first time; after that it's
        /// just this one button press instead of leaving the app, digging through
        /// system Wi-Fi settings, finding the right network, and coming back.
        ///
        /// If Wi-Fi itself is off, this can't do anything useful - apps haven't
        /// been able to silently turn Wi-Fi on since Android 10 (WifiManager.
        /// setWifiEnabled() is a no-op for regular apps on modern Android). So in
        /// that case, this opens the system's quick Wi-Fi panel instead (stays
        /// mostly in-app, one tap to enable), so the pilot can turn it on
        /// themselves and press West again. The quick panel intent isn't
        /// guaranteed present on every device/build (confirmed via research it
        /// can throw ActivityNotFoundException in some cases) - falls back to the
        /// full Wi-Fi settings screen if so, which is old enough to be reliably
        /// present everywhere.
        ///
        /// This needs a small compiled Java helper alongside this script:
        /// ConnectivityManager.NetworkCallback (the object that receives
        /// onAvailable/onLost/onUnavailable) is a concrete Java CLASS, not an
        /// interface - Unity's AndroidJavaProxy can only implement interfaces (it
        /// works via Java's dynamic Proxy mechanism, which can't subclass a
        /// concrete class), so this couldn't be done from C# reflection alone the
        /// way the rest of this project's Android interop has been. The helper -
        /// TelloWifiConnector.java - needs to live at:
        ///   Assets/Plugins/Android/src/main/java/com/tello4quest2/TelloWifiConnector.java
        /// Unity compiles any .java source dropped there automatically as part of
        /// the normal Gradle build - no separate AAR project needed.
        ///
        /// The Java side reports back via UnitySendMessage to
        /// OnTelloWifiConnected(string)/OnTelloWifiLost(string) below.
        /// </summary>
        private void ConnectToTelloWifi()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!wifiOk)
            {
                OpenWifiSettingsPanel();
                return;
            }

            try
            {
                using var connectorClass = new AndroidJavaClass("com.tello4quest2.TelloWifiConnector");
                connectorClass.CallStatic("connect", telloSsidPrefix, gameObject.name);
                Debug.Log($"[TelloInitGate] Requesting a connection to a Wi-Fi network starting with '{telloSsidPrefix}'... Android may show a one-time confirmation dialog.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloInitGate] Could not request a Tello Wi-Fi connection: {e.Message}");
            }
#else
            Debug.Log("[TelloInitGate] Wi-Fi connect requested (no-op outside an Android build).");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void OpenWifiSettingsPanel()
        {
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var intentObject = new AndroidJavaObject("android.content.Intent", "android.settings.panel.action.INTERNET_CONNECTIVITY");
                currentActivity.Call("startActivity", intentObject);
                Debug.Log("[TelloInitGate] Wi-Fi is off - opened the quick internet settings panel so it can be turned on. Press West again once it's on.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloInitGate] Quick Wi-Fi panel not available ({e.Message}) - falling back to the full Wi-Fi settings screen.");
                try
                {
                    using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    using var fallbackIntent = new AndroidJavaObject("android.content.Intent", "android.settings.WIFI_SETTINGS");
                    currentActivity.Call("startActivity", fallbackIntent);
                }
                catch (Exception fallbackException)
                {
                    Debug.LogWarning($"[TelloInitGate] Could not open Wi-Fi settings at all: {fallbackException.Message}");
                }
            }
        }
#endif

        /// <summary>Called via UnitySendMessage from TelloWifiConnector.java once the
        /// system either connects ("1") or gives up ("0"). Purely informational -
        /// the "Tello Wi-Fi connected" checklist item picks up the actual result on
        /// its own once TelloConnection's own handshake succeeds or doesn't.</summary>
        public void OnTelloWifiConnected(string success)
        {
            if (success == "1") Debug.Log("[TelloInitGate] Connected to a Tello Wi-Fi network.");
            else Debug.LogWarning("[TelloInitGate] Could not find/connect to a Tello Wi-Fi network (SSID starting with the configured prefix). Make sure the Tello is powered on and nearby.");
        }

        /// <summary>Called via UnitySendMessage from TelloWifiConnector.java if the
        /// Tello Wi-Fi connection drops after having been established.</summary>
        public void OnTelloWifiLost(string _)
        {
            Debug.LogWarning("[TelloInitGate] Lost the Tello Wi-Fi connection.");
        }

        private void QuitApp()
        {
            Debug.Log("[TelloInitGate] Quitting.");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
