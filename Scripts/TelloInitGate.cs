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
    /// four-item pre-flight checklist + status readout, RIGHT half is the
    /// X-cross button legend.
    ///
    ///   Menu screen:
    ///     South - enter piloting mode, only once all four checks are green.
    ///     West  - ouvre les parametres de VOL (securite, manette, cockpit,
    ///             instruments). Ce bouton affichait auparavant "Controller"
    ///             sans aucune action liee : le prompt existait, mais rien ne
    ///             se produisait quand on appuyait dessus.
    ///     East  - quit the app. Only reachable from this screen, which means
    ///             the drone is always grounded when this fires.
    ///     North - ouvre les parametres VIDEO (placement de l'ecran,
    ///             colorimetrie, nettete, mode nuit, decodage).
    ///
    ///   Les deux ouvrent le meme composant TelloSettingsScreen, sur deux pages
    ///   differentes - voir TelloSettingsScreen.SettingsPage.
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
    /// quitting the app (from the menu) closes it. All four checks keep
    /// evaluating every frame regardless of which screen is showing, so the
    /// menu screen always reflects live status the instant it's shown again.
    ///
    /// World-locked, same as the flight display: positioned once in Start(),
    /// never moves after that.
    ///
    /// NOTE ON REMOVED FEATURES: an earlier pass added a fifth check ("Wi-Fi
    /// enabled", reading Android's WifiManager directly) and a West-button
    /// Wi-Fi auto-connect flow (TelloWifiConnector.java + WifiNetworkSpecifier).
    /// Both were removed - the Wi-Fi-enabled check needed the
    /// ACCESS_WIFI_STATE permission, which repeatedly failed to reliably take
    /// effect through manifest merging in testing (kept throwing
    /// SecurityException at runtime despite being declared), and the
    /// auto-connect flow proved more trouble than it was worth. If
    /// TelloWifiConnector.java and the custom AndroidManifest.xml permission
    /// lines are still in the project, they're now unused and can be removed
    /// - nothing in this file references them anymore.
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
        [SerializeField] private float worldWidth = 2.1f;

        [Header("=== CARD SHAPE (matches the flight display banners) ===")]
        [SerializeField] private float cornerRadiusPx = 20f;

        [Header("=== FONTS (optional - falls back to TMP default if unassigned) ===")]
        [SerializeField] private TMP_FontAsset displayFont;
        [SerializeField] private TMP_FontAsset bodyFont;
        [SerializeField] private TMP_FontAsset monoFont;

        [Header("=== OPTIONAL ICON FONT (PS4/Xbox button glyphs) ===")]
        [SerializeField] private TMP_FontAsset iconFont;
        [SerializeField] private string iconGlyphPlayStationSouth = "D";
        [SerializeField] private string iconGlyphPlayStationNorth = "B";
        [SerializeField] private string iconGlyphPlayStationEast = "C";
        [SerializeField] private string iconGlyphPlayStationWest = "A";
        [SerializeField] private string iconGlyphXboxSouth = "d";
        [SerializeField] private string iconGlyphXboxNorth = "b";
        [SerializeField] private string iconGlyphXboxEast = "c";
        [SerializeField] private string iconGlyphXboxWest = "a";

        private const float CanvasPixelWidth = 1240f;
        private const float CanvasPixelHeight = 620f;
        private const float LeftCenterX = -310f;
        private const float RightCenterX = 310f;
        private const float CrossSize = 400f;

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
            public float anim;
        }

        private SwitchRow bluetoothSwitch;
        private SwitchRow gamepadSwitch;
        private SwitchRow wifiSwitch;
        private SwitchRow telloSwitch;
        private SwitchRow videoSwitch;

        private AppState state = AppState.Menu;
        private bool bluetoothOk, gamepadOk, wifiOk, telloOk, videoOk;

        private float nativeCheckTimer;
        private const float NativeCheckInterval = 0.5f;

        private TextMeshProUGUI flyPrompt;
        private TextMeshProUGUI controllerPrompt;
        private TextMeshProUGUI quitPrompt;
        private TextMeshProUGUI settingsPrompt;

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
            circleSprite = TelloUiKit.GetRoundedSprite(10000f);
            BuildUI();
        }

        private void Start()
        {
            if (tello == null) tello = TelloConnection.Instance;
            if (vrCamera == null) return;
            transform.position = TelloUiKit.ComputeFixedPosition(vrCamera, distanceFromCamera, assumedEyeHeightMeters, verticalOffset);
            transform.rotation = TelloUiKit.ComputeFixedRotation(vrCamera);
        }

        private void RefreshButtonPrompts()
        {
            TelloUiKit.GamepadBrand brand = TelloUiKit.CurrentGamepadBrand();
            SetPrompt(flyPrompt, brand, "south");
            SetPrompt(settingsPrompt, brand, "north");
            SetPrompt(quitPrompt, brand, "east");
            SetPrompt(controllerPrompt, brand, "west");
        }

        public TMP_FontAsset IconFont => iconFont;

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
            return "";
        }

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

            TelloUiKit.BuildFullRectBackground(canvasGO.transform, roundedSprite, PanelBg);

            var centerDividerGO = new GameObject("CenterDivider", typeof(RectTransform), typeof(Image));
            centerDividerGO.transform.SetParent(canvasGO.transform, false);
            RectTransform centerDividerRect = centerDividerGO.GetComponent<RectTransform>();
            centerDividerRect.sizeDelta = new Vector2(1f, CanvasPixelHeight - 30f);
            centerDividerRect.anchoredPosition = Vector2.zero;
            centerDividerGO.GetComponent<Image>().color = PanelEdge;

            BuildLeftChecklist(canvasGO.transform);
            BuildRightLegend(canvasGO.transform);
        }

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
            cursorY -= 35f;

            const float rowHeight = 56f;
            const float rowGap = 26f;

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
            cursorY -= 25f;

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

        private void BuildRightLegend(Transform parent)
        {
            float half = CrossSize / 2f;
            float diagLength = CrossSize * 1.41421356f;

            BuildDiagonalLine(parent, RightCenterX, 0f, diagLength, 45f);
            BuildDiagonalLine(parent, RightCenterX, 0f, diagLength, -45f);

            settingsPrompt = BuildCrossItem(parent, "Video settings", RightCenterX, half * 0.55f);
            controllerPrompt = BuildCrossItem(parent, "Flight settings", RightCenterX - half * 0.55f, 0f);
            quitPrompt = BuildCrossItem(parent, "Quit app", RightCenterX + half * 0.55f, 0f);
            flyPrompt = BuildCrossItem(parent, "Fly", RightCenterX, -half * 0.55f);
        }

        private void ApplyFont(TextMeshProUGUI text, TMP_FontAsset font)
        {
            if (font != null) text.font = font;
        }

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
            track.type = Image.Type.Simple;
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
                    else if (pad.buttonEast.wasPressedThisFrame) { QuitApp(); lastMenuActionTime = Time.time; }
                    else if (pad.buttonNorth.wasPressedThisFrame) { EnterSettings(TelloSettingsScreen.SettingsPage.Video); lastMenuActionTime = Time.time; }
                    else if (pad.buttonWest.wasPressedThisFrame) { EnterSettings(TelloSettingsScreen.SettingsPage.General); lastMenuActionTime = Time.time; }
                }
            }
            else if (state == AppState.Piloting)
            {
                if (pad != null && pad.startButton.wasPressedThisFrame)
                {
                    if (tello != null && tello.IsFlying)
                    {
                        Debug.Log("[TelloInitGate] Can't return to the menu while flying - land first.");
                        gamepadController?.TriggerHaptics(0.6f, 0.2f);
                    }
                    else
                    {
                        ReturnToMenu();
                    }
                }
            }
        }

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
            return true;
#endif
        }

        /// <summary>Reads the headset's system Wi-Fi-enabled state directly. Needs the
        /// ACCESS_WIFI_STATE permission declared in the manifest - without it, this
        /// throws SecurityException every call and this check can never report true.
        /// See the class comment and the project's AndroidManifest.xml for the current
        /// status of getting that permission to actually take effect.</summary>
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
            canvasGO.SetActive(false);

            RevealFlightDisplay();
        }

        private void ReturnToMenu()
        {
            state = AppState.Menu;
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

        /// <summary>Ouvre l'ecran de parametres sur la page demandee. Le meme
        /// GameObject sert aux deux pages : elles sont construites toutes les deux au
        /// demarrage, seule celle qui est demandee est affichee.</summary>
        private void EnterSettings(TelloSettingsScreen.SettingsPage page)
        {
            state = AppState.Settings;
            canvasGO.SetActive(false);

            if (settingsScreenObject != null) settingsScreenObject.SetActive(true);
            else Debug.LogWarning("[TelloInitGate] Settings Screen Object not assigned - Settings screen will never appear.");

            if (settingsScreen != null) settingsScreen.RevealAt(transform.position, transform.rotation, page);
            else Debug.LogWarning("[TelloInitGate] Settings Screen (component) not assigned - Settings screen will never appear.");
        }

        public void ExitSettings()
        {
            state = AppState.Menu;
            if (settingsScreenObject != null) settingsScreenObject.SetActive(false);
            canvasGO.SetActive(true);
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
