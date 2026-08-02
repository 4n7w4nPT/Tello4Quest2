using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Ecran de LECTURE SEULE listant ce que fait chaque commande en vol. Atteint
    /// depuis le menu par la direction GAUCHE de la croix directionnelle (voir
    /// TelloInitGate).
    ///
    /// Le contenu vient integralement de TelloControlMap : rien n'est ecrit en dur
    /// ici, donc cet ecran ne peut pas mentir sur ce que fait la manette tant que
    /// cette table reste a jour avec TelloGamepadController.
    ///
    /// Volontairement sans remapping pour l'instant. Reassigner les touches demande
    /// de gerer les conflits, la persistance et une refonte de la lecture d'entrees
    /// dans TelloGamepadController - c'est une vraie fonctionnalite, pas un
    /// supplement a celle-ci. Voir cet ecran vivre d'abord permettra de savoir si
    /// l'edition est reellement souhaitee.
    ///
    /// Reprend la mecanique de liste defilante de TelloSettingsScreen (viewport
    /// masque + contenu qui glisse + indicateur de defilement).
    /// </summary>
    public class TelloControlsScreen : MonoBehaviour
    {
        [SerializeField] private TelloInitGate initGate;

        [Header("=== PLACEMENT ===")]
        [SerializeField] private float worldWidth = 0.9f;

        [Header("=== CARD SHAPE ===")]
        [SerializeField] private float cornerRadiusPx = 20f;

        [Header("=== FONTS (optional) ===")]
        [SerializeField] private TMP_FontAsset displayFont;
        [SerializeField] private TMP_FontAsset bodyFont;
        [SerializeField] private TMP_FontAsset monoFont;

        private const float CanvasPixelWidth = 700f;
        private const float CanvasPixelHeight = 820f;
        private const float ContentWidth = CanvasPixelWidth - 60f;
        private const float ViewportHeight = 600f;
        private const float RowHeight = 62f;
        private const float SectionHeaderHeight = 44f;
        private const float BottomPadding = 30f;

        private static readonly Color PanelBg = HexColor("#15181B");
        private static readonly Color PanelEdge = HexColor("#262B30");
        private static readonly Color Ink = HexColor("#EDEAE3");
        private static readonly Color InkDim = HexColor("#8A8F94");
        private static readonly Color Amber = HexColor("#E8A33D");
        private static readonly Color Danger = HexColor("#E86A5C");
        private static readonly Color ChipBg = HexColor("#262B30");

        private static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }

        private Sprite roundedSprite;
        private Sprite chipSprite;
        private CanvasGroup canvasGroup;
        private RectTransform viewportRect;
        private RectTransform contentRect;
        private Image scrollThumb;
        private TextMeshProUGUI backPrompt;

        private float contentHeight;
        private float scrollY;
        private float scrollTarget;
        private float cursorY;

        // Les libelles des boutons faciaux dependent de la manette : on garde de quoi
        // les reecrire si le pilote change de manette pendant que l'ecran est ouvert.
        private readonly System.Collections.Generic.List<(TextMeshProUGUI label, TelloControlMap.ControlEntry entry)> faceButtonLabels
            = new System.Collections.Generic.List<(TextMeshProUGUI, TelloControlMap.ControlEntry)>();
        private TelloUiKit.GamepadBrand lastBrand = (TelloUiKit.GamepadBrand)(-1);

        private void Awake()
        {
            roundedSprite = TelloUiKit.GetRoundedSprite(cornerRadiusPx);
            chipSprite = TelloUiKit.GetRoundedSprite(8f);
            BuildUI();
        }

        /// <summary>Appele par TelloInitGate.</summary>
        public void RevealAt(Vector3 position, Quaternion rotation)
        {
            transform.position = position;
            transform.rotation = rotation;
            scrollY = 0f;
            scrollTarget = 0f;
            if (contentRect != null) contentRect.anchoredPosition = Vector2.zero;
            RefreshBrandLabels(force: true);
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

            RefreshBrandLabels(force: false);

            // Defilement libre au stick : rien a selectionner sur cet ecran, donc pas
            // de notion de ligne courante - on fait glisser la liste.
            //
            // Le signe est NEGATIF : scrollY fait monter le contenu, donc pousser le
            // stick vers le haut doit le DIMINUER pour revenir vers le debut de la
            // liste. Sans ce signe, pousser vers le haut descendait dans la liste -
            // l'inverse de ce que fait n'importe quel defilement, et l'inverse aussi
            // du choix de ligne dans l'ecran de parametres.
            float input = pad.leftStick.ReadValue().y + pad.rightStick.ReadValue().y;
            if (Mathf.Abs(input) > 0.15f)
            {
                float maxScroll = Mathf.Max(0f, contentHeight - ViewportHeight);
                scrollTarget = Mathf.Clamp(scrollTarget - input * 500f * Time.deltaTime, 0f, maxScroll);
            }

            scrollY = Mathf.Lerp(scrollY, scrollTarget, Time.deltaTime * 12f);
            contentRect.anchoredPosition = new Vector2(0f, scrollY);
            UpdateScrollThumb();

            if (pad.buttonEast.wasPressedThisFrame || pad.buttonSouth.wasPressedThisFrame)
            {
                if (initGate != null) initGate.ExitSettings();
            }
        }

        private void UpdateScrollThumb()
        {
            if (contentHeight <= ViewportHeight)
            {
                scrollThumb.enabled = false;
                return;
            }
            float maxScroll = contentHeight - ViewportHeight;
            float thumbH = ViewportHeight * Mathf.Clamp01(ViewportHeight / contentHeight);
            float frac = maxScroll > 0f ? scrollY / maxScroll : 0f;
            scrollThumb.rectTransform.sizeDelta = new Vector2(6f, thumbH);
            scrollThumb.rectTransform.anchoredPosition = new Vector2(0f, -(ViewportHeight - thumbH) * frac);
            scrollThumb.enabled = true;
        }

        /// <summary>Les libelles Croix/Rond/Carre/Triangle deviennent A/B/X/Y selon la
        /// manette : si elle change en cours de route, l'ecran doit suivre.</summary>
        private void RefreshBrandLabels(bool force)
        {
            TelloUiKit.GamepadBrand brand = TelloUiKit.CurrentGamepadBrand();
            if (!force && brand == lastBrand) return;
            lastBrand = brand;

            foreach (var (label, entry) in faceButtonLabels)
                ApplyButtonLabel(label, brand, entry.facePosition, 34f, 14f);

            if (backPrompt != null) ApplyButtonLabel(backPrompt, brand, "east", 30f, 13f);
        }

        // =================================================================
        // CONSTRUCTION
        // =================================================================
        private void BuildUI()
        {
            var canvasGO = new GameObject("TelloControlsCanvas", typeof(RectTransform));
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
            BuildRows();
        }

        private void BuildHeader(Transform parent)
        {
            const float headerY = 370f;

            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(parent, false);
            RectTransform titleRect = titleGO.GetComponent<RectTransform>();
            titleRect.sizeDelta = new Vector2(400f, 40f);
            titleRect.anchoredPosition = new Vector2(-140f, headerY);
            var title = titleGO.AddComponent<TextMeshProUGUI>();
            if (displayFont != null) title.font = displayFont;
            title.text = "CONTROLS";
            title.fontSize = 26f;
            title.color = Ink;
            title.alignment = TextAlignmentOptions.MidlineLeft;

            var subGO = new GameObject("Subtitle", typeof(RectTransform));
            subGO.transform.SetParent(parent, false);
            RectTransform subRect = subGO.GetComponent<RectTransform>();
            subRect.sizeDelta = new Vector2(260f, 30f);
            subRect.anchoredPosition = new Vector2(195f, headerY);
            var sub = subGO.AddComponent<TextMeshProUGUI>();
            if (monoFont != null) sub.font = monoFont;
            sub.text = "IN FLIGHT";
            sub.fontSize = 12f;
            sub.color = InkDim;
            sub.alignment = TextAlignmentOptions.MidlineRight;

            BuildDivider(parent, headerY - 27f);
        }

        private void BuildFooter(Transform parent)
        {
            const float dividerY = -300f;
            BuildDivider(parent, dividerY);

            var itemGO = new GameObject("Footer", typeof(RectTransform));
            itemGO.transform.SetParent(parent, false);
            RectTransform itemRect = itemGO.GetComponent<RectTransform>();
            itemRect.sizeDelta = new Vector2(400f, 90f);
            itemRect.anchoredPosition = new Vector2(0f, dividerY - 52f);

            // Meme structure a trois etages que le menu et l'ecran de parametres :
            // l'action, le mot PRESS, puis le bouton (glyphe si la police d'icones est
            // disponible). Un ecran qui s'annonce differemment des autres oblige a
            // relire ce qu'on sait deja.
            var actionGO = new GameObject("Action", typeof(RectTransform));
            actionGO.transform.SetParent(itemRect, false);
            RectTransform actionRect = actionGO.GetComponent<RectTransform>();
            actionRect.sizeDelta = new Vector2(380f, 26f);
            actionRect.anchoredPosition = new Vector2(0f, 26f);
            var actionText = actionGO.AddComponent<TextMeshProUGUI>();
            if (bodyFont != null) actionText.font = bodyFont;
            actionText.text = "Back to menu";
            actionText.fontSize = 15f;
            actionText.fontStyle = FontStyles.Bold;
            actionText.color = Ink;
            actionText.alignment = TextAlignmentOptions.Center;

            var pressGO = new GameObject("PressLabel", typeof(RectTransform));
            pressGO.transform.SetParent(itemRect, false);
            RectTransform pressRect = pressGO.GetComponent<RectTransform>();
            pressRect.sizeDelta = new Vector2(380f, 20f);
            pressRect.anchoredPosition = new Vector2(0f, 4f);
            var pressText = pressGO.AddComponent<TextMeshProUGUI>();
            if (monoFont != null) pressText.font = monoFont;
            pressText.fontSize = 11f;
            pressText.color = InkDim;
            pressText.alignment = TextAlignmentOptions.Center;
            pressText.text = "PRESS";

            var promptGO = new GameObject("Prompt", typeof(RectTransform));
            promptGO.transform.SetParent(itemRect, false);
            RectTransform promptRect = promptGO.GetComponent<RectTransform>();
            promptRect.sizeDelta = new Vector2(380f, 36f);
            promptRect.anchoredPosition = new Vector2(0f, -22f);
            backPrompt = promptGO.AddComponent<TextMeshProUGUI>();
            if (monoFont != null) backPrompt.font = monoFont;
            backPrompt.fontSize = 13f;
            backPrompt.color = Ink;
            backPrompt.alignment = TextAlignmentOptions.Center;
            backPrompt.text = "";
        }

        /// <summary>Ecrit un libelle de bouton en preferant le GLYPHE de la police
        /// d'icones quand elle est disponible, exactement comme le fait l'ecran Init.
        /// La police et la taille doivent etre reglees a chaque fois, pas seulement le
        /// texte : on peut passer d'un glyphe a du texte (ou l'inverse) quand la
        /// manette change de marque en cours de route.</summary>
        private void ApplyButtonLabel(TextMeshProUGUI target, TelloUiKit.GamepadBrand brand, string position, float glyphSize, float textSize)
        {
            if (initGate != null)
            {
                string text = initGate.ResolveButtonText(brand, position, out bool isIconGlyph);
                if (isIconGlyph)
                {
                    target.font = initGate.IconFont;
                    target.fontSize = glyphSize;
                    target.text = text;
                    return;
                }
            }
            if (monoFont != null) target.font = monoFont;
            target.fontSize = textSize;
            target.text = TelloUiKit.ButtonName(brand, position);
        }

        private void BuildDivider(Transform parent, float y)
        {
            var lineGO = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(parent, false);
            RectTransform lineRect = lineGO.GetComponent<RectTransform>();
            lineRect.sizeDelta = new Vector2(CanvasPixelWidth - 40f, 1f);
            lineRect.anchoredPosition = new Vector2(0f, y);
            lineGO.GetComponent<Image>().color = PanelEdge;
        }

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
            contentRect.sizeDelta = new Vector2(ContentWidth, 10f);
            contentRect.anchoredPosition = Vector2.zero;

            var trackGO = new GameObject("ScrollTrack", typeof(RectTransform), typeof(Image));
            trackGO.transform.SetParent(parent, false);
            RectTransform trackRect = trackGO.GetComponent<RectTransform>();
            trackRect.sizeDelta = new Vector2(4f, ViewportHeight);
            trackRect.anchoredPosition = new Vector2(340f, 335f - ViewportHeight * 0.5f);
            trackGO.GetComponent<Image>().color = PanelEdge;

            var thumbGO = new GameObject("ScrollThumb", typeof(RectTransform), typeof(Image));
            thumbGO.transform.SetParent(trackRect, false);
            RectTransform thumbRect = thumbGO.GetComponent<RectTransform>();
            thumbRect.pivot = new Vector2(0.5f, 1f);
            thumbRect.anchorMin = new Vector2(0.5f, 1f);
            thumbRect.anchorMax = new Vector2(0.5f, 1f);
            scrollThumb = thumbGO.GetComponent<Image>();
            scrollThumb.color = Amber;
        }

        private void BuildRows()
        {
            cursorY = 0f;
            TelloControlMap.ControlKind? lastKind = null;

            foreach (var entry in TelloControlMap.Piloting)
            {
                // Un titre de groupe a chaque changement de type de commande : la liste
                // se lit alors comme "les sticks / les boutons / les gachettes", ce qui
                // correspond a la facon dont on cherche une touche sur une manette.
                if (lastKind != entry.kind)
                {
                    lastKind = entry.kind;
                    AddSection(SectionTitle(entry.kind));
                }
                AddRow(entry);
            }

            contentHeight = -cursorY + BottomPadding;
            contentRect.sizeDelta = new Vector2(ContentWidth, contentHeight);
        }

        private static string SectionTitle(TelloControlMap.ControlKind kind) => kind switch
        {
            TelloControlMap.ControlKind.Stick => "Sticks",
            TelloControlMap.ControlKind.FaceButton => "Buttons",
            TelloControlMap.ControlKind.Dpad => "D-pad",
            TelloControlMap.ControlKind.Shoulder => "Shoulders & triggers",
            TelloControlMap.ControlKind.System => "System",
            _ => ""
        };

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

            var labelGO = new GameObject("Text", typeof(RectTransform));
            labelGO.transform.SetParent(r, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(ContentWidth, 22f);
            labelRect.anchoredPosition = new Vector2(0f, -16f);
            var t = labelGO.AddComponent<TextMeshProUGUI>();
            if (monoFont != null) t.font = monoFont;
            t.fontSize = 13f;
            t.color = Amber;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.characterSpacing = 6f;
            t.text = title.ToUpperInvariant();

            var ruleGO = new GameObject("Rule", typeof(RectTransform), typeof(Image));
            ruleGO.transform.SetParent(r, false);
            RectTransform ruleRect = ruleGO.GetComponent<RectTransform>();
            ruleRect.sizeDelta = new Vector2(ContentWidth, 1f);
            ruleRect.anchoredPosition = new Vector2(0f, -33f);
            ruleGO.GetComponent<Image>().color = PanelEdge;

            cursorY -= SectionHeaderHeight;
        }

        private void AddRow(TelloControlMap.ControlEntry entry)
        {
            var rowGO = new GameObject($"Row_{entry.action}", typeof(RectTransform));
            rowGO.transform.SetParent(contentRect, false);
            RectTransform rowRect = rowGO.GetComponent<RectTransform>();
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchorMin = new Vector2(0.5f, 1f);
            rowRect.anchorMax = new Vector2(0.5f, 1f);
            rowRect.sizeDelta = new Vector2(ContentWidth, RowHeight);
            rowRect.anchoredPosition = new Vector2(0f, cursorY);

            // Pastille du nom de commande, a gauche.
            var chipGO = new GameObject("Chip", typeof(RectTransform), typeof(Image));
            chipGO.transform.SetParent(rowRect, false);
            RectTransform chipRect = chipGO.GetComponent<RectTransform>();
            chipRect.sizeDelta = new Vector2(150f, 36f);
            chipRect.anchoredPosition = new Vector2(-ContentWidth * 0.5f + 80f, -16f);
            Image chip = chipGO.GetComponent<Image>();
            chip.sprite = chipSprite;
            chip.type = Image.Type.Sliced;
            chip.color = ChipBg;

            var chipTextGO = new GameObject("ChipText", typeof(RectTransform));
            chipTextGO.transform.SetParent(chipRect, false);
            RectTransform chipTextRect = chipTextGO.GetComponent<RectTransform>();
            chipTextRect.sizeDelta = new Vector2(146f, 34f);
            chipTextRect.anchoredPosition = Vector2.zero;
            var chipText = chipTextGO.AddComponent<TextMeshProUGUI>();
            if (monoFont != null) chipText.font = monoFont;
            chipText.fontSize = 13f;
            chipText.color = Ink;
            chipText.alignment = TextAlignmentOptions.Center;
            chipText.textWrappingMode = TextWrappingModes.NoWrap;
            chipText.overflowMode = TextOverflowModes.Ellipsis;
            if (entry.kind == TelloControlMap.ControlKind.FaceButton)
            {
                // Bouton facial : glyphe de la police d'icones si possible, comme sur
                // l'ecran Init. La pastille est un peu plus haute pour le laisser
                // respirer.
                ApplyButtonLabel(chipText, TelloUiKit.CurrentGamepadBrand(), entry.facePosition, 34f, 14f);
                faceButtonLabels.Add((chipText, entry));
            }
            else
            {
                chipText.text = TelloControlMap.ResolveLabel(entry, TelloUiKit.CurrentGamepadBrand());
            }

            // Action, a droite de la pastille.
            var actionGO = new GameObject("Action", typeof(RectTransform));
            actionGO.transform.SetParent(rowRect, false);
            RectTransform actionRect = actionGO.GetComponent<RectTransform>();
            actionRect.sizeDelta = new Vector2(400f, 22f);
            actionRect.anchoredPosition = new Vector2(90f, -12f);
            var actionText = actionGO.AddComponent<TextMeshProUGUI>();
            if (bodyFont != null) actionText.font = bodyFont;
            actionText.fontSize = 15f;
            actionText.fontStyle = FontStyles.Bold;
            // L'arret d'urgence est le seul element de cette liste dont on veut qu'il
            // saute aux yeux sans etre cherche.
            actionText.color = entry.action.Contains("EMERGENCY") ? Danger : Ink;
            actionText.alignment = TextAlignmentOptions.MidlineLeft;
            actionText.textWrappingMode = TextWrappingModes.NoWrap;
            actionText.overflowMode = TextOverflowModes.Ellipsis;
            actionText.text = entry.action;

            var detailGO = new GameObject("Detail", typeof(RectTransform));
            detailGO.transform.SetParent(rowRect, false);
            RectTransform detailRect = detailGO.GetComponent<RectTransform>();
            detailRect.sizeDelta = new Vector2(400f, 24f);
            detailRect.anchoredPosition = new Vector2(90f, -36f);
            var detailText = detailGO.AddComponent<TextMeshProUGUI>();
            if (bodyFont != null) detailText.font = bodyFont;
            detailText.fontSize = 11f;
            detailText.color = InkDim;
            detailText.alignment = TextAlignmentOptions.TopLeft;
            detailText.overflowMode = TextOverflowModes.Ellipsis;
            detailText.text = entry.detail;

            cursorY -= RowHeight;
        }
    }
}
