using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Settings screen, reached from the Menu screen via North (see TelloInitGate).
    /// Same visual language as the Menu screen: same nameplate header style (title
    /// swapped for "PARAMETERS"), and a matching 3-card footer bar (Confirm & Exit /
    /// Reset to Defaults / Exit Without Saving) using the exact same action/PRESS/
    /// identifier card format as the Menu screen's X-cross legend, separated by thin
    /// vertical dividers. In between, a scrollable, sectioned list of every
    /// non-technical tunable in the app (flight safety thresholds, gamepad feel,
    /// panel placement) - deliberately excludes networking/protocol internals (rc
    /// send rate, command timeouts, UDP frame buffering...), which stay
    /// Inspector-only: those are reliability knobs, not something to expose to a
    /// pilot mid-session.
    ///
    /// Controls: Left stick Y moves the row selector up/down one row at a time
    /// (debounced), auto-scrolling the list to keep the selection in view. Right
    /// stick X adjusts the selected row's value continuously while held (numeric
    /// rows), or snaps a boolean row true/false past a small deadzone. South saves
    /// every pending value to its owning component AND to PlayerPrefs (so it
    /// survives an app restart), then returns to the menu. East discards
    /// everything and returns without touching anything. North resets every pending
    /// value back to its default (visible immediately, not applied/saved until
    /// South is pressed afterward) - a quick way back to a known-good state without
    /// having to remember what everything used to be.
    /// </summary>
    public class TelloSettingsScreen : MonoBehaviour
    {
        [SerializeField] private TelloInitGate initGate;
        [SerializeField] private TelloConnection tello;
        [SerializeField] private TelloGamepadController gamepadController;
        [SerializeField] private TelloVideoDisplay videoScreen;
        [SerializeField] private TelloSpatialPanel spatialPanel;
        [SerializeField] private TelloStatusPanel statusPanel;

        [Tooltip("How fast Right Stick X moves the selected value across its full range, in seconds for a full sweep at max deflection.")]
        [SerializeField] private float secondsForFullSweep = 1.5f;

        [Header("=== PLACEMENT ===")]
        [SerializeField] private float worldWidth = 0.9f;

        [Header("=== CARD SHAPE (matches the Menu screen) ===")]
        [SerializeField] private float cornerRadiusPx = 20f;

        [Header("=== FONTS (optional - falls back to TMP default if unassigned) ===")]
        [SerializeField] private TMP_FontAsset displayFont;
        [SerializeField] private TMP_FontAsset bodyFont;
        [SerializeField] private TMP_FontAsset monoFont;

        private const float CanvasPixelWidth = 700f;
        private const float CanvasPixelHeight = 820f;
        private const float ContentWidth = CanvasPixelWidth - 60f;
        private const float ViewportHeight = 560f;
        private const float RowHeight = 52f;
        private const float SectionHeaderHeight = 34f;
        private const float BottomPadding = 40f; // extra slack under the last row - see UpdateScroll comment

        // Same aviation-instrument palette as TelloInitGate.
        private static readonly Color PanelBg = HexColor("#15181B");
        private static readonly Color PanelEdge = HexColor("#262B30");
        private static readonly Color Ink = HexColor("#EDEAE3");
        private static readonly Color InkDim = HexColor("#8A8F94");
        private static readonly Color Amber = HexColor("#E8A33D");
        private static readonly Color TrackBg = HexColor("#262B30");

        private static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }

        private Sprite roundedSprite;
        private Sprite circleSprite;
        private CanvasGroup canvasGroup;
        private RectTransform viewportRect;
        private RectTransform contentRect;
        private Image scrollThumb;
        private TextMeshProUGUI savePrompt;
        private TextMeshProUGUI resetPrompt;
        private TextMeshProUGUI cancelPrompt;

        private enum RowKind { Float, Bool }

        private class SettingsRow
        {
            public RowKind kind;
            public float min, max;
            public float floatValue;
            public float defaultFloat;
            public bool boolValue;
            public bool defaultBool;
            public string format;
            public Func<float> getFloat;
            public Action<float> setFloat;
            public Func<bool> getBool;
            public Action<bool> setBool;

            public TextMeshProUGUI labelText;
            public Image fillImage;
            public RectTransform trackRect;
            public TextMeshProUGUI valueText;
            public float rowY; // this row's Y position within Content, captured at build time
        }

        private readonly List<SettingsRow> rows = new List<SettingsRow>();
        private readonly List<Action> saveActions = new List<Action>(); // one per owning component, called once each on save
        private int selectedRow;
        private float contentScrollY; // current (animated) Content.anchoredPosition.y
        private float contentHeight;

        private const float RowSelectRepeatDelay = 0.22f;
        private float rowSelectCooldown;

        private void Awake()
        {
            roundedSprite = TelloUiKit.GetRoundedSprite(cornerRadiusPx);
            circleSprite = TelloUiKit.GetRoundedSprite(10000f);
            BuildUI();
        }

        /// <summary>Called by TelloInitGate when entering Settings - snapshots every
        /// row's current live value as the starting point for editing.</summary>
        public void RevealAt(Vector3 position, Quaternion rotation)
        {
            transform.position = position;
            transform.rotation = rotation;

            foreach (var row in rows)
            {
                if (row.kind == RowKind.Float && row.getFloat != null) row.floatValue = row.getFloat();
                else if (row.kind == RowKind.Bool && row.getBool != null) row.boolValue = row.getBool();
            }

            selectedRow = 0;
            contentScrollY = 0f;
            RefreshRows();

            canvasGroup.alpha = 0f;
            StartCoroutine(FadeIn());
        }

        private System.Collections.IEnumerator FadeIn()
        {
            float duration = 0.3f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                canvasGroup.alpha = Mathf.Clamp01(elapsed / duration);
                yield return null;
            }
            canvasGroup.alpha = 1f;
        }

        private void Update()
        {
            Gamepad pad = TelloUiKit.GetActiveGamepad();
            if (pad == null) return;

            Vector2 left = pad.leftStick.ReadValue();
            Vector2 right = pad.rightStick.ReadValue();

            rowSelectCooldown -= Time.deltaTime;
            if (rowSelectCooldown <= 0f && rows.Count > 0)
            {
                if (left.y > 0.5f) { selectedRow = Mathf.Max(0, selectedRow - 1); rowSelectCooldown = RowSelectRepeatDelay; }
                else if (left.y < -0.5f) { selectedRow = Mathf.Min(rows.Count - 1, selectedRow + 1); rowSelectCooldown = RowSelectRepeatDelay; }
            }

            if (rows.Count > 0)
            {
                SettingsRow row = rows[selectedRow];
                if (Mathf.Abs(right.x) > 0.15f)
                {
                    if (row.kind == RowKind.Float)
                    {
                        float t = Time.deltaTime / secondsForFullSweep;
                        row.floatValue = Mathf.Clamp(row.floatValue + right.x * t * (row.max - row.min), row.min, row.max);
                    }
                    else if (row.kind == RowKind.Bool)
                    {
                        row.boolValue = right.x > 0f;
                    }
                }
            }

            UpdateScroll();
            RefreshRows();

            if (pad.buttonSouth.wasPressedThisFrame) SaveAndExit();
            else if (pad.buttonEast.wasPressedThisFrame) CancelAndExit();
            else if (pad.buttonNorth.wasPressedThisFrame) ResetToDefaults();
        }

        /// <summary>Keeps the selected row inside the viewport, animating Content's
        /// position toward the target rather than snapping - and drives the visual
        /// scroll-position indicator on the right edge from the same value.
        /// contentHeight includes BottomPadding beyond the last row, so scrolling to
        /// the very end always leaves the last row fully clear of the viewport's
        /// bottom edge instead of sitting flush against it.</summary>
        private void UpdateScroll()
        {
            if (rows.Count == 0) return;

            float rowY = rows[selectedRow].rowY;
            float maxScroll = Mathf.Max(0f, contentHeight - ViewportHeight);
            float target = Mathf.Clamp(-rowY - ViewportHeight * 0.5f + RowHeight * 0.5f, 0f, maxScroll);
            contentScrollY = Mathf.Lerp(contentScrollY, target, Time.deltaTime * 10f);
            contentRect.anchoredPosition = new Vector2(0f, contentScrollY);

            if (contentHeight > ViewportHeight)
            {
                float thumbHeightFrac = Mathf.Clamp01(ViewportHeight / contentHeight);
                float scrollFrac = maxScroll > 0f ? contentScrollY / maxScroll : 0f;
                float thumbH = ViewportHeight * thumbHeightFrac;
                float thumbY = -(ViewportHeight - thumbH) * scrollFrac;
                scrollThumb.rectTransform.sizeDelta = new Vector2(6f, thumbH);
                scrollThumb.rectTransform.anchoredPosition = new Vector2(0f, thumbY);
                scrollThumb.enabled = true;
            }
            else
            {
                scrollThumb.enabled = false; // everything fits - no need for a thumb at all
            }
        }

        private void SaveAndExit()
        {
            foreach (var row in rows)
            {
                if (row.kind == RowKind.Float) row.setFloat?.Invoke(row.floatValue);
                else if (row.kind == RowKind.Bool) row.setBool?.Invoke(row.boolValue);
            }
            foreach (var save in saveActions) save?.Invoke();
            Close();
        }

        private void CancelAndExit() => Close();

        /// <summary>Resets every row's PENDING value to its default - visible
        /// immediately on the sliders/toggles, but not applied to the live
        /// components or persisted until South (Save) is pressed afterward. Doesn't
        /// exit the screen, so the pilot can review the restored values first.</summary>
        private void ResetToDefaults()
        {
            foreach (var row in rows)
            {
                if (row.kind == RowKind.Float) row.floatValue = row.defaultFloat;
                else row.boolValue = row.defaultBool;
            }
            RefreshRows();
        }

        private void Close()
        {
            if (initGate != null) initGate.ExitSettings();
        }

        // =================================================================
        // UI CONSTRUCTION
        // =================================================================
        private void BuildUI()
        {
            var canvasGO = new GameObject("TelloSettingsCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(CanvasPixelWidth, CanvasPixelHeight);
            canvasGO.transform.localScale = Vector3.one * (worldWidth / CanvasPixelWidth);
            canvasGroup = canvasGO.AddComponent<CanvasGroup>();

            TelloUiKit.BuildFullRectBackground(canvasGO.transform, roundedSprite, PanelBg);

            BuildHeader(canvasGO.transform);
            BuildScrollArea(canvasGO.transform);
            BuildFooter(canvasGO.transform);
            BuildAllRows();
        }

        /// <summary>Same nameplate layout as TelloInitGate's header - amber mark, title,
        /// subtitle - just with "PARAMETERS" instead of "PRE FLIGHT CHECK".</summary>
        private void BuildHeader(Transform parent)
        {
            const float headerY = 370f;

            var markGO = new GameObject("Mark", typeof(RectTransform), typeof(Image));
            markGO.transform.SetParent(parent, false);
            RectTransform markRect = markGO.GetComponent<RectTransform>();
            markRect.sizeDelta = new Vector2(10f, 10f);
            markRect.anchoredPosition = new Vector2(-330f, headerY);
            Image markImage = markGO.GetComponent<Image>();
            markImage.sprite = circleSprite;
            markImage.type = Image.Type.Simple;
            markImage.color = Amber;

            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(parent, false);
            RectTransform titleRect = titleGO.GetComponent<RectTransform>();
            titleRect.sizeDelta = new Vector2(300f, 40f);
            titleRect.anchoredPosition = new Vector2(-155f, headerY);
            var title = titleGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(title, displayFont);
            title.text = "TELLO4QUEST2";
            title.fontSize = 26f;
            title.color = Ink;
            title.alignment = TextAlignmentOptions.MidlineLeft;

            var subtitleGO = new GameObject("Subtitle", typeof(RectTransform));
            subtitleGO.transform.SetParent(parent, false);
            RectTransform subtitleRect = subtitleGO.GetComponent<RectTransform>();
            subtitleRect.sizeDelta = new Vector2(180f, 30f);
            subtitleRect.anchoredPosition = new Vector2(235f, headerY);
            var subtitle = subtitleGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(subtitle, monoFont);
            subtitle.text = "PARAMETERS";
            subtitle.fontSize = 12f;
            subtitle.color = InkDim;
            subtitle.alignment = TextAlignmentOptions.MidlineRight;
            subtitle.textWrappingMode = TextWrappingModes.NoWrap;
            subtitle.overflowMode = TextOverflowModes.Ellipsis;

            BuildDivider(parent, headerY - 27f);
        }

        /// <summary>Footer: three equal cards (Confirm & Exit / Reset to Defaults /
        /// Exit Without Saving) separated by thin vertical dividers, each using the
        /// same action/PRESS/identifier format as the Menu screen's X-cross wedges -
        /// a previous version showed a single combined "Press X" line here, which
        /// didn't match and couldn't show an icon glyph alongside the word "Press".</summary>
        private void BuildFooter(Transform parent)
        {
            const float footerDividerY = -240f;
            BuildDivider(parent, footerDividerY);

            float spacing = CanvasPixelWidth / 3f;
            float itemY = footerDividerY - 55f;

            savePrompt = BuildFooterCard(parent, "Confirm & Exit", -spacing, itemY);
            resetPrompt = BuildFooterCard(parent, "Reset to Defaults", 0f, itemY);
            cancelPrompt = BuildFooterCard(parent, "Exit Without Saving", spacing, itemY);

            BuildVerticalDivider(parent, -spacing / 2f, itemY);
            BuildVerticalDivider(parent, spacing / 2f, itemY);
        }

        private void ApplyFont(TextMeshProUGUI text, TMP_FontAsset font)
        {
            if (font != null) text.font = font;
        }

        private void BuildDivider(Transform parent, float y)
        {
            var lineGO = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(parent, false);
            RectTransform lineRect = lineGO.GetComponent<RectTransform>();
            lineRect.sizeDelta = new Vector2(CanvasPixelWidth - 40f, 1f);
            lineRect.anchoredPosition = new Vector2(0f, y);
            Image lineImage = lineGO.GetComponent<Image>();
            lineImage.color = PanelEdge;
        }

        private void BuildVerticalDivider(Transform parent, float x, float centerY)
        {
            var lineGO = new GameObject("VDivider", typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(parent, false);
            RectTransform lineRect = lineGO.GetComponent<RectTransform>();
            lineRect.sizeDelta = new Vector2(1f, 90f);
            lineRect.anchoredPosition = new Vector2(x, centerY);
            Image lineImage = lineGO.GetComponent<Image>();
            lineImage.color = PanelEdge;
        }

        /// <summary>One footer card's content: action word, static "PRESS" label, and
        /// the button identifier row (icon glyph or bare button name, resolved via
        /// TelloInitGate.ResolveButtonText - see SetFooterPrompt), matching
        /// TelloInitGate's X-cross wedge layout exactly.</summary>
        private TextMeshProUGUI BuildFooterCard(Transform parent, string action, float x, float y)
        {
            var itemGO = new GameObject($"Footer_{action}", typeof(RectTransform));
            itemGO.transform.SetParent(parent, false);
            RectTransform itemRect = itemGO.GetComponent<RectTransform>();
            itemRect.sizeDelta = new Vector2(220f, 90f);
            itemRect.anchoredPosition = new Vector2(x, y);

            var actionGO = new GameObject("Action", typeof(RectTransform));
            actionGO.transform.SetParent(itemGO.transform, false);
            RectTransform actionRect = actionGO.GetComponent<RectTransform>();
            actionRect.sizeDelta = new Vector2(210f, 26f);
            actionRect.anchoredPosition = new Vector2(0f, 24f);
            var actionText = actionGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(actionText, bodyFont);
            actionText.fontSize = 15f;
            actionText.fontStyle = FontStyles.Bold;
            actionText.color = Ink;
            actionText.alignment = TextAlignmentOptions.Center;
            actionText.textWrappingMode = TextWrappingModes.NoWrap;
            actionText.overflowMode = TextOverflowModes.Ellipsis;
            actionText.text = action;

            var pressGO = new GameObject("PressLabel", typeof(RectTransform));
            pressGO.transform.SetParent(itemGO.transform, false);
            RectTransform pressRect = pressGO.GetComponent<RectTransform>();
            pressRect.sizeDelta = new Vector2(210f, 20f);
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
            promptRect.sizeDelta = new Vector2(210f, 36f);
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

        /// <summary>Resolves the button identifier the same way TelloInitGate does
        /// (icon glyph if available, bare button name otherwise) via the shared
        /// method on TelloInitGate - avoids duplicating the icon font + 8 glyph
        /// fields on this screen too.</summary>
        private void SetFooterPrompt(TextMeshProUGUI target, TelloUiKit.GamepadBrand brand, string position)
        {
            if (initGate != null)
            {
                string text = initGate.ResolveButtonText(brand, position, out bool isIconGlyph);
                if (isIconGlyph)
                {
                    target.font = initGate.IconFont;
                    target.fontSize = 30f;
                }
                else
                {
                    ApplyFont(target, monoFont);
                    target.fontSize = 13f;
                }
                target.text = text;
            }
            else
            {
                ApplyFont(target, monoFont);
                target.fontSize = 13f;
                target.text = TelloUiKit.ButtonName(brand, position);
            }
        }

        /// <summary>Viewport (clipped) + Content (scrolled) + a thin scroll-position
        /// indicator on the right edge.</summary>
        private void BuildScrollArea(Transform parent)
        {
            var viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGO.transform.SetParent(parent, false);
            viewportRect = viewportGO.GetComponent<RectTransform>();
            viewportRect.sizeDelta = new Vector2(ContentWidth + 20f, ViewportHeight);
            viewportRect.anchoredPosition = new Vector2(-10f, 335f - ViewportHeight * 0.5f);

            var contentGO = new GameObject("Content", typeof(RectTransform));
            contentGO.transform.SetParent(viewportRect, false);
            contentRect = contentGO.GetComponent<RectTransform>();
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchorMin = new Vector2(0.5f, 1f);
            contentRect.anchorMax = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(ContentWidth, 10f); // height finalized once all rows are built
            contentRect.anchoredPosition = Vector2.zero;

            // Scroll-position indicator ("ascenseur") - a thin track the full height
            // of the viewport, with a shorter thumb sized/positioned proportionally
            // to how much of Content is currently visible. Purely visual (there's no
            // pointer input in this world-space gamepad-driven canvas to drag it
            // with) - driven directly from UpdateScroll() each frame instead.
            var trackGO = new GameObject("ScrollTrack", typeof(RectTransform), typeof(Image));
            trackGO.transform.SetParent(parent, false);
            RectTransform trackRect = trackGO.GetComponent<RectTransform>();
            trackRect.sizeDelta = new Vector2(4f, ViewportHeight);
            trackRect.anchoredPosition = new Vector2(340f, 335f - ViewportHeight * 0.5f);
            Image trackImage = trackGO.GetComponent<Image>();
            trackImage.color = PanelEdge;

            var thumbGO = new GameObject("ScrollThumb", typeof(RectTransform), typeof(Image));
            thumbGO.transform.SetParent(trackRect, false);
            RectTransform thumbRect = thumbGO.GetComponent<RectTransform>();
            thumbRect.pivot = new Vector2(0.5f, 1f);
            thumbRect.anchorMin = new Vector2(0.5f, 1f);
            thumbRect.anchorMax = new Vector2(0.5f, 1f);
            scrollThumb = thumbGO.GetComponent<Image>();
            scrollThumb.color = Amber;
        }

        // =================================================================
        // ROWS - all ~32 parameters, grouped by theme
        // =================================================================
        private float cursorY;

        private void BuildAllRows()
        {
            cursorY = 0f;

            AddSection("Display");
            AddFloatRow("Screen distance", 0.6f, 10f, 1.2f, "{0:F2}m",
                () => videoScreen != null ? videoScreen.DistanceFromCamera : 1.2f,
                v => { if (videoScreen != null) videoScreen.DistanceFromCamera = v; });
            AddFloatRow("Screen size", 0.5f, 10f, 1f, "{0:F2}x",
                () => videoScreen != null ? videoScreen.SizeMultiplier : 1f,
                v => videoScreen?.SetSizeMultiplier(v));
            AddFloatRow("Transparency", 0.15f, 1f, 1f, "{0:P0}",
                () => videoScreen != null ? videoScreen.Opacity : 1f,
                v => videoScreen?.SetOpacity(v));
            AddFloatRow("Vertical offset", -1f, 1f, -0.3f, "{0:F2}m",
                () => videoScreen != null ? videoScreen.VerticalOffset : -0.3f,
                v => { if (videoScreen != null) videoScreen.VerticalOffset = v; });
            AddFloatRow("Eye height", 1.2f, 2.0f, 1.6f, "{0:F2}m",
                () => videoScreen != null ? videoScreen.AssumedEyeHeightMeters : 1.6f,
                v => { if (videoScreen != null) videoScreen.AssumedEyeHeightMeters = v; });
            if (videoScreen != null) saveActions.Add(videoScreen.SavePersistedSettings);

            AddSection("Safety");
            AddFloatRow("Battery low threshold", 5f, 40f, 20f, "{0:F0}%",
                () => tello != null ? tello.BatteryLowThreshold : 20f,
                v => { if (tello != null) tello.BatteryLowThreshold = Mathf.RoundToInt(v); });
            AddFloatRow("Battery critical threshold", 5f, 25f, 10f, "{0:F0}%",
                () => tello != null ? tello.BatteryCriticalThreshold : 10f,
                v => { if (tello != null) tello.BatteryCriticalThreshold = Mathf.RoundToInt(v); });
            AddFloatRow("Temperature warning", 50f, 100f, 80f, "{0:F0}\u00B0C",
                () => tello != null ? tello.TemperatureWarningThreshold : 80f,
                v => { if (tello != null) tello.TemperatureWarningThreshold = v; });
            AddFloatRow("Temperature critical", 60f, 110f, 90f, "{0:F0}\u00B0C",
                () => tello != null ? tello.TemperatureCriticalThreshold : 90f,
                v => { if (tello != null) tello.TemperatureCriticalThreshold = v; });
            AddFloatRow("Proximity warning", 10f, 200f, 50f, "{0:F0}cm",
                () => tello != null ? tello.ProximityWarningCm : 50f,
                v => { if (tello != null) tello.ProximityWarningCm = Mathf.RoundToInt(v); });
            AddFloatRow("Proximity critical", 5f, 100f, 20f, "{0:F0}cm",
                () => tello != null ? tello.ProximityCriticalCm : 20f,
                v => { if (tello != null) tello.ProximityCriticalCm = Mathf.RoundToInt(v); });
            AddBoolRow("Auto-land on critical battery", true,
                () => tello == null || tello.AutoLandOnCriticalBattery,
                v => { if (tello != null) tello.AutoLandOnCriticalBattery = v; });
            AddBoolRow("Altitude ceiling", false,
                () => tello != null && tello.EnableAltitudeCeiling,
                v => { if (tello != null) tello.EnableAltitudeCeiling = v; });
            AddFloatRow("Max height", 50f, 1000f, 300f, "{0:F0}cm",
                () => tello != null ? tello.MaxHeightCm : 300f,
                v => { if (tello != null) tello.MaxHeightCm = v; });
            AddFloatRow("Altitude soft margin", 10f, 200f, 50f, "{0:F0}cm",
                () => tello != null ? tello.AltitudeCeilingSoftMarginCm : 50f,
                v => { if (tello != null) tello.AltitudeCeilingSoftMarginCm = v; });
            AddBoolRow("Crash detection", true,
                () => tello == null || tello.EnableCrashDetection,
                v => { if (tello != null) tello.EnableCrashDetection = v; });
            AddFloatRow("Crash sensitivity", 1000f, 8000f, 3500f, "{0:F0}",
                () => tello != null ? tello.CrashAccelerationThreshold : 3500f,
                v => { if (tello != null) tello.CrashAccelerationThreshold = v; });
            AddBoolRow("Auto-land if crash suspected", false,
                () => tello != null && tello.AutoLandOnCrashSuspected,
                v => { if (tello != null) tello.AutoLandOnCrashSuspected = v; });
            AddBoolRow("Position estimation (dead reckoning)", true,
                () => tello == null || tello.EnableDeadReckoning,
                v => { if (tello != null) tello.EnableDeadReckoning = v; });
            AddBoolRow("Flight log (CSV)", false,
                () => tello != null && tello.EnableFlightLog,
                v => { if (tello != null) tello.EnableFlightLog = v; });
            if (tello != null) saveActions.Add(tello.SavePersistedSettings);

            AddSection("Gamepad");
            AddFloatRow("Loss timeout (safety hover)", 0.1f, 3f, 0.5f, "{0:F1}s",
                () => gamepadController != null ? gamepadController.GamepadTimeoutSeconds : 0.5f,
                v => { if (gamepadController != null) gamepadController.GamepadTimeoutSeconds = v; });
            AddBoolRow("Auto-calibrate sticks on connect", true,
                () => gamepadController == null || gamepadController.AutoCalibrateOnConnect,
                v => { if (gamepadController != null) gamepadController.AutoCalibrateOnConnect = v; });
            AddFloatRow("Auto-calibrate delay", 0f, 2f, 0.3f, "{0:F1}s",
                () => gamepadController != null ? gamepadController.AutoCalibrateDelay : 0.3f,
                v => { if (gamepadController != null) gamepadController.AutoCalibrateDelay = v; });
            AddBoolRow("Haptic feedback", true,
                () => gamepadController == null || gamepadController.EnableHaptics,
                v => { if (gamepadController != null) gamepadController.EnableHaptics = v; });
            AddFloatRow("Haptic duration", 0.05f, 1f, 0.2f, "{0:F2}s",
                () => gamepadController != null ? gamepadController.WarningHapticDuration : 0.2f,
                v => { if (gamepadController != null) gamepadController.WarningHapticDuration = v; });
            AddFloatRow("Haptic strength", 0f, 1f, 0.6f, "{0:P0}",
                () => gamepadController != null ? gamepadController.WarningHapticStrength : 0.6f,
                v => { if (gamepadController != null) gamepadController.WarningHapticStrength = v; });
            if (gamepadController != null) saveActions.Add(gamepadController.SavePersistedSettings);

            AddSection("Panels");
            AddFloatRow("Accel gauge sensitivity", 100f, 1000f, 400f, "{0:F0}",
                () => spatialPanel != null ? spatialPanel.MaxDisplayAcceleration : 400f,
                v => { if (spatialPanel != null) spatialPanel.MaxDisplayAcceleration = v; });
            AddFloatRow("Side panels gap", 0f, 0.1f, 0.01f, "{0:F2}m",
                () => spatialPanel != null ? spatialPanel.Gap : 0.01f,
                v => { if (spatialPanel != null) spatialPanel.Gap = v; });
            if (spatialPanel != null) saveActions.Add(spatialPanel.SavePersistedSettings);

            AddFloatRow("Video signal nominal FPS", 10f, 30f, 25f, "{0:F0}fps",
                () => statusPanel != null ? statusPanel.NominalFps : 25f,
                v => { if (statusPanel != null) statusPanel.NominalFps = v; });
            if (statusPanel != null) saveActions.Add(statusPanel.SavePersistedSettings);

            // BottomPadding leaves genuine slack under the last row, so scrolling all
            // the way down never leaves it sitting flush against - or clipped by -
            // the viewport's bottom edge.
            contentHeight = -cursorY + BottomPadding;
            contentRect.sizeDelta = new Vector2(ContentWidth, contentHeight);
        }

        private void AddSection(string title)
        {
            var go = new GameObject($"Section_{title}", typeof(RectTransform));
            go.transform.SetParent(contentRect, false);
            RectTransform r = go.GetComponent<RectTransform>();
            r.pivot = new Vector2(0.5f, 1f);
            r.anchorMin = new Vector2(0.5f, 1f);
            r.anchorMax = new Vector2(0.5f, 1f);
            r.sizeDelta = new Vector2(ContentWidth, SectionHeaderHeight);
            r.anchoredPosition = new Vector2(0f, cursorY);
            var t = go.AddComponent<TextMeshProUGUI>();
            ApplyFont(t, monoFont);
            t.fontSize = 13f;
            t.color = Amber;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.text = title.ToUpperInvariant();

            cursorY -= SectionHeaderHeight;
        }

        private void AddFloatRow(string label, float min, float max, float defaultValue, string format, Func<float> getter, Action<float> setter)
        {
            SettingsRow row = BuildRowVisual(label);
            row.kind = RowKind.Float;
            row.min = min;
            row.max = max;
            row.defaultFloat = defaultValue;
            row.format = format;
            row.getFloat = getter;
            row.setFloat = setter;
            rows.Add(row);
            cursorY -= RowHeight;
        }

        private void AddBoolRow(string label, bool defaultValue, Func<bool> getter, Action<bool> setter)
        {
            SettingsRow row = BuildRowVisual(label);
            row.kind = RowKind.Bool;
            row.defaultBool = defaultValue;
            row.getBool = getter;
            row.setBool = setter;
            rows.Add(row);
            cursorY -= RowHeight;
        }

        private SettingsRow BuildRowVisual(string label)
        {
            var rowGO = new GameObject($"Row_{label}", typeof(RectTransform));
            rowGO.transform.SetParent(contentRect, false);
            RectTransform rowRect = rowGO.GetComponent<RectTransform>();
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchorMin = new Vector2(0.5f, 1f);
            rowRect.anchorMax = new Vector2(0.5f, 1f);
            rowRect.sizeDelta = new Vector2(ContentWidth, RowHeight);
            rowRect.anchoredPosition = new Vector2(0f, cursorY);

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(rowRect, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(280f, 40f);
            labelRect.anchoredPosition = new Vector2(-190f, -RowHeight * 0.5f);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(labelText, bodyFont);
            labelText.fontSize = 14f;
            labelText.color = InkDim;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;
            labelText.textWrappingMode = TextWrappingModes.NoWrap;
            labelText.overflowMode = TextOverflowModes.Ellipsis;
            labelText.text = label;

            var trackGO = new GameObject("Track", typeof(RectTransform), typeof(Image));
            trackGO.transform.SetParent(rowRect, false);
            RectTransform trackRect = trackGO.GetComponent<RectTransform>();
            trackRect.sizeDelta = new Vector2(160f, 12f);
            trackRect.anchoredPosition = new Vector2(40f, -RowHeight * 0.5f);
            Image trackImage = trackGO.GetComponent<Image>();
            trackImage.sprite = circleSprite;
            trackImage.type = Image.Type.Simple;
            trackImage.color = TrackBg;

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(trackRect, false);
            RectTransform fillRect = fillGO.GetComponent<RectTransform>();
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = new Vector2(0f, 0f);
            Image fillImage = fillGO.GetComponent<Image>();
            fillImage.sprite = circleSprite;
            fillImage.type = Image.Type.Simple;
            fillImage.color = Amber;

            var valueGO = new GameObject("Value", typeof(RectTransform));
            valueGO.transform.SetParent(rowRect, false);
            RectTransform valueRect = valueGO.GetComponent<RectTransform>();
            valueRect.sizeDelta = new Vector2(80f, 40f);
            valueRect.anchoredPosition = new Vector2(220f, -RowHeight * 0.5f);
            var valueText = valueGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(valueText, monoFont);
            valueText.fontSize = 13f;
            valueText.color = Ink;
            valueText.alignment = TextAlignmentOptions.MidlineRight;

            return new SettingsRow
            {
                labelText = labelText,
                fillImage = fillImage,
                trackRect = trackRect,
                valueText = valueText,
                rowY = cursorY
            };
        }

        // =================================================================
        // LIVE UPDATE
        // =================================================================
        private void RefreshRows()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                SettingsRow row = rows[i];
                bool selected = i == selectedRow;
                row.labelText.color = selected ? Amber : InkDim;
                row.labelText.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;

                if (row.kind == RowKind.Float)
                {
                    float normalized = row.max > row.min ? Mathf.InverseLerp(row.min, row.max, row.floatValue) : 0f;
                    row.fillImage.rectTransform.sizeDelta = new Vector2(row.trackRect.sizeDelta.x * normalized, 0f);
                    row.fillImage.enabled = true;
                    row.valueText.text = string.Format(row.format, row.floatValue);
                }
                else
                {
                    row.fillImage.rectTransform.sizeDelta = new Vector2(row.boolValue ? row.trackRect.sizeDelta.x : 0f, 0f);
                    row.valueText.text = row.boolValue ? "ON" : "OFF";
                }
            }

            TelloUiKit.GamepadBrand brand = TelloUiKit.CurrentGamepadBrand();
            SetFooterPrompt(savePrompt, brand, "south");
            SetFooterPrompt(resetPrompt, brand, "north");
            SetFooterPrompt(cancelPrompt, brand, "east");
        }
    }
}
