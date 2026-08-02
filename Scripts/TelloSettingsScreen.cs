using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace TelloQuest
{
    /// <summary>
    /// Ecran de parametres, atteint depuis le menu (voir TelloInitGate). Il porte
    /// DEUX pages distinctes, construites toutes les deux au demarrage et affichees
    /// l'une ou l'autre selon le bouton presse dans le menu :
    ///
    ///   - SettingsPage.Video   (Nord)  : tout ce qui touche au rendu de l'image -
    ///     placement de l'ecran, colorimetrie, nettete, mode nuit, decodage.
    ///   - SettingsPage.General (Ouest) : tout le reste - securite de vol, manette,
    ///     cockpit, journalisation.
    ///
    /// Les deux pages vivent sur le MEME GameObject : aucune manipulation de scene
    /// n'est necessaire, il suffit que TelloInitGate appelle RevealAt() avec la page
    /// voulue.
    ///
    /// Chaque page conserve sa propre selection de ligne et sa propre position de
    /// defilement, donc revenir sur une page la retrouve la ou on l'avait laissee.
    ///
    /// Commandes : stick gauche haut/bas = changer de ligne (avec repetition
    /// temporisee), stick droit gauche/droite = regler la valeur de la ligne
    /// selectionnee (continu sur un curseur, bascule au-dela d'une petite zone morte
    /// sur un interrupteur). Sud = appliquer et enregistrer (composants + PlayerPrefs,
    /// donc ca survit a un redemarrage). Est = sortir sans rien toucher. Nord =
    /// remettre toute la page a ses valeurs par defaut (visible immediatement, mais
    /// pas applique tant que Sud n'a pas ete presse).
    ///
    /// Les reglages purement techniques (cadence d'envoi rc, timeouts de commande,
    /// bufferisation UDP) restent volontairement dans l'Inspector : ce sont des
    /// leviers de fiabilite, pas des reglages a exposer a un pilote en session.
    /// </summary>
    public class TelloSettingsScreen : MonoBehaviour
    {
        public enum SettingsPage { Video, General }

        [SerializeField] private TelloInitGate initGate;
        [SerializeField] private TelloConnection tello;
        [SerializeField] private TelloGamepadController gamepadController;
        [SerializeField] private TelloVideoDisplay videoScreen;
        [SerializeField] private TelloSpatialPanel spatialPanel;
        [SerializeField] private TelloStatusPanel statusPanel;
        [Tooltip("Optionnel - retrouve automatiquement dans la scene s'il n'est pas assigne.")]
        [SerializeField] private TelloVideoDecoder videoDecoder;
        [Tooltip("Optionnel - retrouve automatiquement dans la scene s'il n'est pas assigne.")]
        [SerializeField] private TelloActionLogPanel actionLogPanel;

        [Tooltip("Vitesse de balayage du stick droit : duree, en secondes, pour parcourir toute la plage d'un curseur a fond de manche.")]
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
        private const float RowHeight = 56f;
        private const float SectionHeaderHeight = 44f;
        private const float BottomPadding = 40f;

        // --- Geometrie d'une ligne ---
        private const float LabelX = -200f;
        private const float LabelWidth = 270f;
        private const float ValueX = 252f;
        private const float SliderCentreX = 55f;
        private const float RailWidth = 220f;
        private const float RailHeight = 6f;
        private const float ControlY = -38f;
        private const int TickCount = 11;

        // Meme palette instrument que TelloInitGate.
        private static readonly Color PanelBg = HexColor("#15181B");
        private static readonly Color PanelEdge = HexColor("#262B30");
        private static readonly Color Ink = HexColor("#EDEAE3");
        private static readonly Color InkDim = HexColor("#8A8F94");
        private static readonly Color Amber = HexColor("#E8A33D");
        private static readonly Color AmberDeep = HexColor("#8A5F1F");
        private static readonly Color TrackBg = HexColor("#262B30");
        private static readonly Color RowSelectedBg = new Color(0.910f, 0.639f, 0.239f, 0.10f);
        private static readonly Color RowClearBg = new Color(0f, 0f, 0f, 0f);
        private static readonly Color SwitchOffTrack = HexColor("#2E3439");
        private static readonly Color TickColor = HexColor("#3A4149");

        private static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }

        private Sprite roundedSprite;
        private Sprite circleSprite;
        private Sprite handleSprite;
        private CanvasGroup canvasGroup;
        private RectTransform viewportRect;
        private Image scrollThumb;
        private TextMeshProUGUI savePrompt;
        private TextMeshProUGUI resetPrompt;
        private TextMeshProUGUI cancelPrompt;
        private TextMeshProUGUI subtitleText;

        private enum RowKind { Float, Bool, Choice }

        private class SettingsRow
        {
            /// <summary>Identifiant stable, utilise par les presets pour retrouver une
            /// ligne precise sans dependre de son libelle affiche. Null si la ligne
            /// n'a pas besoin d'etre adressee.</summary>
            public string id;

            /// <summary>Renvoie false quand la ligne existe mais n'est pas utilisable
            /// sur ce materiel (typiquement : commandes SDK 2.0 sur un Tello grand
            /// public). La ligne reste VISIBLE et selectionnable - on peut donc lire
            /// pourquoi elle est indisponible - mais le stick droit ne la modifie
            /// plus. Masquer purement et simplement aurait ete pire : l'utilisateur
            /// aurait cherche un reglage documente qui n'apparait nulle part.</summary>
            public Func<bool> isAvailable;
            public string lockedNote;

            // Lignes a choix multiples (presets)
            public string[] choiceLabels;
            public int choiceIndex;
            public Action<int> onChoiceChanged;
            public readonly List<Image> segments = new List<Image>();

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

            public Image selectionBg;
            public TextMeshProUGUI labelText;
            public TextMeshProUGUI valueText;

            // Curseur
            public GameObject sliderRoot;
            public RectTransform fillRect;
            public Image fillImage;
            public RectTransform handleRect;
            public RectTransform handleEdge;
            public Image handleImage;

            // Interrupteur
            public GameObject switchRoot;
            public Image switchTrack;
            public RectTransform switchKnob;
            public Image switchKnobImage;

            public float rowY;
        }

        /// <summary>Une page complete : son conteneur defilant, ses lignes, ses actions
        /// de sauvegarde, et son propre etat de navigation.</summary>
        private class PageData
        {
            public SettingsPage page;
            public string subtitle;
            public GameObject root;
            public RectTransform contentRect;
            public readonly List<SettingsRow> rows = new List<SettingsRow>();
            public readonly Dictionary<string, SettingsRow> byId = new Dictionary<string, SettingsRow>();
            public readonly List<Action> saveActions = new List<Action>();
            public float contentHeight;
            public int selectedRow;
            public float scrollY;
        }

        private PageData videoPage;
        private PageData generalPage;
        private PageData activePage;

        private const float RowSelectRepeatDelay = 0.22f;
        private float rowSelectCooldown;
        private float choiceCooldown;

        private void Awake()
        {
            roundedSprite = TelloUiKit.GetRoundedSprite(cornerRadiusPx);
            circleSprite = TelloUiKit.GetRoundedSprite(10000f);
            handleSprite = TelloUiKit.GetRoundedSprite(7f);

            // FindAnyObjectByType et pas FindFirstObjectByType : ce dernier est lui
            // aussi deprecie (il dependait de l'ordre des instance IDs). "Any" convient
            // parfaitement ici puisqu'il n'existe qu'une seule instance de chacun dans
            // la scene - et si un jour il y en avait plusieurs, c'est la reference
            // assignee dans l'Inspector qui devrait trancher, pas cette recherche.
            if (videoDecoder == null) videoDecoder = FindAnyObjectByType<TelloVideoDecoder>();
            if (actionLogPanel == null) actionLogPanel = FindAnyObjectByType<TelloActionLogPanel>();

            BuildUI();
        }

        /// <summary>Appele par TelloInitGate. Bascule sur la page demandee et
        /// re-echantillonne toutes ses lignes depuis les valeurs live.</summary>
        public void RevealAt(Vector3 position, Quaternion rotation, SettingsPage page)
        {
            transform.position = position;
            transform.rotation = rotation;

            SetActivePage(page);

            foreach (var row in activePage.rows)
            {
                if (row.kind == RowKind.Float && row.getFloat != null) row.floatValue = row.getFloat();
                else if (row.kind == RowKind.Bool && row.getBool != null) row.boolValue = row.getBool();
                else if (row.kind == RowKind.Choice) row.choiceIndex = 0; // on rouvre toujours sur Custom
            }

            RefreshRows();

            canvasGroup.alpha = 0f;
            StartCoroutine(FadeIn());
        }

        private void SetActivePage(SettingsPage page)
        {
            activePage = page == SettingsPage.Video ? videoPage : generalPage;
            if (videoPage != null) videoPage.root.SetActive(activePage == videoPage);
            if (generalPage != null) generalPage.root.SetActive(activePage == generalPage);
            if (subtitleText != null) subtitleText.text = activePage.subtitle;
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
            if (activePage == null) return;

            Gamepad pad = TelloUiKit.GetActiveGamepad();
            if (pad == null) return;

            Vector2 left = pad.leftStick.ReadValue();
            Vector2 right = pad.rightStick.ReadValue();

            rowSelectCooldown -= Time.deltaTime;
            if (rowSelectCooldown <= 0f && activePage.rows.Count > 0)
            {
                if (left.y > 0.5f) { activePage.selectedRow = Mathf.Max(0, activePage.selectedRow - 1); rowSelectCooldown = RowSelectRepeatDelay; }
                else if (left.y < -0.5f) { activePage.selectedRow = Mathf.Min(activePage.rows.Count - 1, activePage.selectedRow + 1); rowSelectCooldown = RowSelectRepeatDelay; }
            }

            choiceCooldown -= Time.deltaTime;

            if (activePage.rows.Count > 0)
            {
                SettingsRow row = activePage.rows[activePage.selectedRow];
                bool available = row.isAvailable == null || row.isAvailable();

                if (available && Mathf.Abs(right.x) > 0.15f)
                {
                    if (row.kind == RowKind.Float)
                    {
                        float t = Time.deltaTime / secondsForFullSweep;
                        row.floatValue = Mathf.Clamp(row.floatValue + right.x * t * (row.max - row.min), row.min, row.max);
                        NoteManualEdit(row);
                    }
                    else if (row.kind == RowKind.Bool)
                    {
                        bool next = right.x > 0f;
                        if (next != row.boolValue) NoteManualEdit(row);
                        row.boolValue = next;
                    }
                    else if (row.kind == RowKind.Choice && choiceCooldown <= 0f)
                    {
                        // Pas de balayage continu ici : un choix est discret, on avance
                        // d'un cran par poussee, avec la meme temporisation que le
                        // deplacement de ligne.
                        choiceCooldown = RowSelectRepeatDelay;
                        int step = right.x > 0f ? 1 : -1;
                        int count = row.choiceLabels.Length;
                        row.choiceIndex = ((row.choiceIndex + step) % count + count) % count;
                        row.onChoiceChanged?.Invoke(row.choiceIndex);
                    }
                }
            }

            UpdateScroll();
            RefreshRows();

            if (pad.buttonSouth.wasPressedThisFrame) SaveAndExit();
            else if (pad.buttonEast.wasPressedThisFrame) CancelAndExit();
            else if (pad.buttonNorth.wasPressedThisFrame) ResetToDefaults();
        }

        private void UpdateScroll()
        {
            if (activePage.rows.Count == 0) return;

            float rowY = activePage.rows[activePage.selectedRow].rowY;
            float maxScroll = Mathf.Max(0f, activePage.contentHeight - ViewportHeight);
            float target = Mathf.Clamp(-rowY - ViewportHeight * 0.5f + RowHeight * 0.5f, 0f, maxScroll);
            activePage.scrollY = Mathf.Lerp(activePage.scrollY, target, Time.deltaTime * 10f);
            activePage.contentRect.anchoredPosition = new Vector2(0f, activePage.scrollY);

            if (activePage.contentHeight > ViewportHeight)
            {
                float thumbHeightFrac = Mathf.Clamp01(ViewportHeight / activePage.contentHeight);
                float scrollFrac = maxScroll > 0f ? activePage.scrollY / maxScroll : 0f;
                float thumbH = ViewportHeight * thumbHeightFrac;
                float thumbY = -(ViewportHeight - thumbH) * scrollFrac;
                scrollThumb.rectTransform.sizeDelta = new Vector2(6f, thumbH);
                scrollThumb.rectTransform.anchoredPosition = new Vector2(0f, thumbY);
                scrollThumb.enabled = true;
            }
            else
            {
                scrollThumb.enabled = false;
            }
        }

        private void SaveAndExit()
        {
            foreach (var row in activePage.rows)
            {
                if (row.kind == RowKind.Float) row.setFloat?.Invoke(row.floatValue);
                else if (row.kind == RowKind.Bool) row.setBool?.Invoke(row.boolValue);
                // Les lignes Choice n'ont pas de valeur propre a ecrire : elles ont
                // deja agi sur les autres lignes au moment de la selection.
            }
            foreach (var save in activePage.saveActions) save?.Invoke();
            Close();
        }

        private void CancelAndExit() => Close();

        /// <summary>Remet les valeurs EN ATTENTE de la page courante a leur defaut -
        /// visible immediatement, mais applique et persiste seulement si Sud est
        /// presse ensuite. L'ecran ne se ferme pas, pour laisser relire.</summary>
        private void ResetToDefaults()
        {
            foreach (var row in activePage.rows)
            {
                if (row.kind == RowKind.Float) row.floatValue = row.defaultFloat;
                else if (row.kind == RowKind.Bool) row.boolValue = row.defaultBool;
                else row.choiceIndex = 0; // retour a Custom
            }
            RefreshRows();
        }

        private void Close()
        {
            if (initGate != null) initGate.ExitSettings();
        }

        // =================================================================
        // CONSTRUCTION
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

            videoPage = BuildPage(SettingsPage.Video, "VIDEO PARAMETERS");
            generalPage = BuildPage(SettingsPage.General, "FLIGHT PARAMETERS");
            SetActivePage(SettingsPage.Video);
        }

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
            subtitleRect.sizeDelta = new Vector2(240f, 30f);
            subtitleRect.anchoredPosition = new Vector2(205f, headerY);
            subtitleText = subtitleGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(subtitleText, monoFont);
            subtitleText.text = "PARAMETERS";
            subtitleText.fontSize = 12f;
            subtitleText.color = InkDim;
            subtitleText.alignment = TextAlignmentOptions.MidlineRight;
            subtitleText.textWrappingMode = TextWrappingModes.NoWrap;
            subtitleText.overflowMode = TextOverflowModes.Ellipsis;

            BuildDivider(parent, headerY - 27f);
        }

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
            lineGO.GetComponent<Image>().color = PanelEdge;
        }

        private void BuildVerticalDivider(Transform parent, float x, float centerY)
        {
            var lineGO = new GameObject("VDivider", typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(parent, false);
            RectTransform lineRect = lineGO.GetComponent<RectTransform>();
            lineRect.sizeDelta = new Vector2(1f, 90f);
            lineRect.anchoredPosition = new Vector2(x, centerY);
            lineGO.GetComponent<Image>().color = PanelEdge;
        }

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



        // =================================================================
        // PRESETS DE QUALITE VIDEO
        //
        // La page video porte maintenant une dizaine de reglages d'image qui
        // interagissent entre eux (bicubique, lissage + son seuil, accentuation,
        // mode nuit et ses trois parametres). C'est beaucoup de surface de reglage a
        // parcourir a la main, casque sur la tete, drone en vol. Un preset pose
        // d'un coup un point de depart coherent, que le pilote peut ensuite affiner
        // ligne par ligne - des qu'il touche a autre chose, le preset repasse a
        // "Custom" pour ne pas pretendre decrire un etat qui n'est plus le sien.
        //
        // Ce que les presets NE touchent PAS, volontairement : balance des blancs,
        // luminosite et contraste. Ce ne sont pas des reglages de "qualite" mais des
        // corrections propres a un eclairage donne - les ecraser en changeant de
        // preset detruirait un reglage que le pilote a fait pour de bonnes raisons.
        // =================================================================
        private static readonly string[] PresetLabels = { "Custom", "Sharp", "Balanced", "Smooth", "Low light" };

        private const string PresetRowId = "video_preset";

        private struct ImagePreset
        {
            public bool bicubic;
            public float smoothing;
            public float smoothingEdge;
            public float sharpen;
            public float nightStrength;
            public float nightThreshold;
            public float nightBlur;
        }

        // Index 0 (Custom) n'a pas d'entree : il ne decrit rien, il signale juste que
        // l'etat courant ne correspond a aucun preset.
        private static readonly ImagePreset[] Presets =
        {
            // Sharp - detail maximum, aucun lissage. Bon par forte lumiere, ou le
            // bruit de capteur est faible et le blocking H.264 discret.
            new ImagePreset { bicubic = true, smoothing = 0f,    smoothingEdge = 0.08f, sharpen = 0.60f, nightStrength = 0f,    nightThreshold = 2f, nightBlur = 0f },
            // Balanced - le point de depart recommande. Assez de lissage pour effacer
            // le blocking dans les aplats, assez d'accentuation pour garder du piquant.
            new ImagePreset { bicubic = true, smoothing = 0.45f, smoothingEdge = 0.08f, sharpen = 0.35f, nightStrength = 0f,    nightThreshold = 2f, nightBlur = 0f },
            // Smooth - priorite au confort. Utile quand la liaison est mauvaise et que
            // les artefacts de compression fatiguent plus que le detail ne manque.
            new ImagePreset { bicubic = true, smoothing = 0.80f, smoothingEdge = 0.15f, sharpen = 0.15f, nightStrength = 0f,    nightThreshold = 2f, nightBlur = 0f },
            // Low light - releve les zones sombres et lisse davantage, puisque le boost
            // de luminosite amplifie mecaniquement le bruit du capteur avec lui.
            new ImagePreset { bicubic = true, smoothing = 0.60f, smoothingEdge = 0.12f, sharpen = 0.25f, nightStrength = 0.45f, nightThreshold = 2f, nightBlur = 0.40f },
        };

        /// <summary>Ecrit les valeurs du preset dans les valeurs EN ATTENTE des lignes
        /// concernees. Rien n'est applique au materiau ni persiste tant que Sud n'a pas
        /// ete presse - exactement comme "Reset to Defaults".</summary>
        private void ApplyPreset(int index)
        {
            if (index <= 0 || index > Presets.Length) return; // 0 = Custom, rien a faire
            ImagePreset preset = Presets[index - 1];

            SetPendingBool("video_bicubic", preset.bicubic);
            SetPendingFloat("video_smoothing", preset.smoothing);
            SetPendingFloat("video_smoothing_edge", preset.smoothingEdge);
            SetPendingFloat("video_sharpen", preset.sharpen);
            SetPendingFloat("video_night_strength", preset.nightStrength);
            SetPendingFloat("video_night_threshold", preset.nightThreshold);
            SetPendingFloat("video_night_blur", preset.nightBlur);
        }

        private void SetPendingFloat(string id, float value)
        {
            if (activePage.byId.TryGetValue(id, out SettingsRow row))
                row.floatValue = Mathf.Clamp(value, row.min, row.max);
        }

        private void SetPendingBool(string id, bool value)
        {
            if (activePage.byId.TryGetValue(id, out SettingsRow row))
                row.boolValue = value;
        }

        /// <summary>Appelee des qu'une ligne est modifiee a la main. Si cette ligne fait
        /// partie de celles qu'un preset pilote, le preset ne decrit plus l'etat reel :
        /// il repasse a "Custom".</summary>
        private void NoteManualEdit(SettingsRow row)
        {
            if (row.id == null || !PresetControlledIds.Contains(row.id)) return;
            if (!activePage.byId.TryGetValue(PresetRowId, out SettingsRow presetRow)) return;
            presetRow.choiceIndex = 0;
        }

        private static readonly HashSet<string> PresetControlledIds = new HashSet<string>
        {
            "video_bicubic", "video_smoothing", "video_smoothing_edge", "video_sharpen",
            "video_night_strength", "video_night_threshold", "video_night_blur"
        };

        // =================================================================
        // PAGES ET LIGNES
        // =================================================================
        private PageData buildingPage;
        private float cursorY;

        private PageData BuildPage(SettingsPage page, string subtitle)
        {
            var rootGO = new GameObject($"Page_{page}", typeof(RectTransform));
            rootGO.transform.SetParent(viewportRect, false);
            RectTransform rootRect = rootGO.GetComponent<RectTransform>();
            rootRect.pivot = new Vector2(0.5f, 1f);
            rootRect.anchorMin = new Vector2(0.5f, 1f);
            rootRect.anchorMax = new Vector2(0.5f, 1f);
            rootRect.sizeDelta = new Vector2(ContentWidth, 10f);
            rootRect.anchoredPosition = Vector2.zero;

            var data = new PageData { page = page, subtitle = subtitle, root = rootGO, contentRect = rootRect };

            buildingPage = data;
            cursorY = 0f;

            if (page == SettingsPage.Video) BuildVideoRows();
            else BuildGeneralRows();

            data.contentHeight = -cursorY + BottomPadding;
            rootRect.sizeDelta = new Vector2(ContentWidth, data.contentHeight);
            buildingPage = null;
            return data;
        }

        // -----------------------------------------------------------------
        // PAGE VIDEO - tout ce qui influe sur l'image
        // -----------------------------------------------------------------
        private void BuildVideoRows()
        {
            AddSection("Quality preset");
            AddChoiceRow(PresetRowId, "Image preset", PresetLabels, ApplyPreset);

            AddSection("Screen placement");
            AddFloatRow("Screen distance", 0.6f, 10f, 1.2f, "{0:F2}m",
                () => videoScreen != null ? videoScreen.DistanceFromCamera : 1.2f,
                v => { if (videoScreen != null) videoScreen.DistanceFromCamera = v; });
            AddFloatRow("Screen size", 0.5f, 10f, 1f, "{0:F2}x",
                () => videoScreen != null ? videoScreen.SizeMultiplier : 1f,
                v => videoScreen?.SetSizeMultiplier(v));
            AddFloatRow("Vertical offset", -1f, 1f, -0.3f, "{0:F2}m",
                () => videoScreen != null ? videoScreen.VerticalOffset : -0.3f,
                v => { if (videoScreen != null) videoScreen.VerticalOffset = v; });
            AddFloatRow("Eye height", 1.2f, 2f, 1.6f, "{0:F2}m",
                () => videoScreen != null ? videoScreen.AssumedEyeHeightMeters : 1.6f,
                v => { if (videoScreen != null) videoScreen.AssumedEyeHeightMeters = v; });
            AddFloatRow("Transparency", 0.15f, 1f, 1f, "{0:P0}",
                () => videoScreen != null ? videoScreen.Opacity : 1f,
                v => videoScreen?.SetOpacity(v));

            AddSection("Colour");
            AddFloatRow("White balance", -1f, 1f, 0f, "{0:F2}",
                () => videoScreen != null ? videoScreen.WhiteBalanceShift : 0f,
                v => videoScreen?.SetWhiteBalanceShift(v));
            AddFloatRow("Brightness", -1f, 1f, 0f, "{0:F2}",
                () => videoScreen != null ? videoScreen.Brightness : 0f,
                v => videoScreen?.SetBrightness(v));
            AddFloatRow("Contrast", 0.5f, 2f, 1f, "{0:F2}",
                () => videoScreen != null ? videoScreen.Contrast : 1f,
                v => videoScreen?.SetContrast(v));
            // Le SPS du Tello ne porte pas de VUI : la matrice est donc devinee. Ce
            // switch permet de trancher a l'oeil, sur de la vegetation ou des carnations.
            AddBoolRow("Force BT.709 matrix", false,
                () => videoScreen != null && videoScreen.ForceBt709,
                v => videoScreen?.SetForceBt709(v));
            // A n'activer que si l'image parait trop contrastee/sombre : signifie que
            // les plans Y/UV subissent une conversion sRGB parasite au sampling.
            AddBoolRow("Undo sRGB plane sampling", false,
                () => videoScreen != null && videoScreen.PlanesSampledAsSRGB,
                v => videoScreen?.SetPlanesSampledAsSRGB(v));
            AddFloatRow("Chroma site offset", -1f, 1f, 0.5f, "{0:F2}px",
                () => videoScreen != null ? videoScreen.ChromaSiteOffset : 0.5f,
                v => videoScreen?.SetChromaSiteOffset(v));

            AddSection("Sharpness & noise");
            AddBoolRow("Bicubic upscale", true,
                () => videoScreen == null || videoScreen.BicubicUpscale,
                v => videoScreen?.SetBicubicUpscale(v), "video_bicubic");
            AddFloatRow("Smoothing", 0f, 1f, 0f, "{0:P0}",
                () => videoScreen != null ? videoScreen.SmoothStrength : 0f,
                v => videoScreen?.SetSmoothStrength(v), "video_smoothing");
            AddFloatRow("Smoothing edge threshold", 0.01f, 0.5f, 0.08f, "{0:F2}",
                () => videoScreen != null ? videoScreen.SmoothEdgeThreshold : 0.08f,
                v => videoScreen?.SetSmoothEdgeThreshold(v), "video_smoothing_edge");
            AddFloatRow("Sharpening", 0f, 1.5f, 0f, "{0:F2}",
                () => videoScreen != null ? videoScreen.SharpenStrength : 0f,
                v => videoScreen?.SetSharpenStrength(v), "video_sharpen");

            AddSection("Night mode");
            AddFloatRow("Night mode strength", 0f, 1f, 0f, "{0:P0}",
                () => videoScreen != null ? videoScreen.NightModeStrength : 0f,
                v => videoScreen?.SetNightModeStrength(v), "video_night_strength");
            AddFloatRow("Night mode threshold", 0.5f, 4f, 2f, "{0:F2}",
                () => videoScreen != null ? videoScreen.NightModeThreshold : 2f,
                v => videoScreen?.SetNightModeThreshold(v), "video_night_threshold");
            AddFloatRow("Night mode blur", 0f, 1f, 0f, "{0:P0}",
                () => videoScreen != null ? videoScreen.NightModeBlurStrength : 0f,
                v => videoScreen?.SetNightModeBlurStrength(v), "video_night_blur");
            if (videoScreen != null) buildingPage.saveActions.Add(videoScreen.SavePersistedSettings);

            AddSection("Decoding");
            AddFloatRow("Signal meter nominal FPS", 10f, 60f, 25f, "{0:F0}fps",
                () => statusPanel != null ? statusPanel.NominalFps : 25f,
                v => { if (statusPanel != null) statusPanel.NominalFps = v; });
            if (statusPanel != null) buildingPage.saveActions.Add(statusPanel.SavePersistedSettings);

            AddFloatRow("Decoder restart timeout", 0f, 10f, 3f, "{0:F1}s",
                () => videoDecoder != null ? videoDecoder.DecoderStallTimeoutSeconds : 3f,
                v => { if (videoDecoder != null) videoDecoder.DecoderStallTimeoutSeconds = v; });

            // Ces trois commandes n'existent QUE sur Tello EDU / Talent (SDK 2.0+).
            // Le drone est interroge par "sdk?" a la connexion ; tant qu'il n'a pas
            // repondu un numero de version exploitable, les lignes restent verrouillees
            // et affichent pourquoi. Auparavant, cocher la case sur un Tello grand
            // public envoyait trois commandes qui repondaient "error" et allumaient la
            // pastille "Last cmd" en rouge, sans que rien n'explique la cause.
            AddSection("Stream quality (Tello EDU only)");
            AddBoolRow("Request higher bitrate", false,
                () => tello != null && tello.SendStreamQualityCommands,
                v => { if (tello != null) tello.SendStreamQualityCommands = v; });
            MarkLastRowConditional(Sdk20Available, Sdk20LockedNote);

            AddFloatRow("Video bitrate", 0f, 5f, 5f, "{0:F0}Mb",
                () => tello != null ? tello.VideoBitrateMbps : 5f,
                v => { if (tello != null) tello.VideoBitrateMbps = v; });
            MarkLastRowConditional(Sdk20Available, Sdk20LockedNote);

            AddBoolRow("Request 720p / high FPS", false,
                () => tello != null && tello.RequestHighResolutionAndFps,
                v => { if (tello != null) tello.RequestHighResolutionAndFps = v; });
            MarkLastRowConditional(Sdk20Available, Sdk20LockedNote);
        }

        private bool Sdk20Available() => tello != null && tello.SupportsSdk20Commands;

        /// <summary>Distingue "on sait que ce drone ne sait pas faire" de "on n'a pas
        /// encore la reponse" - deux situations differentes qui meritent deux messages
        /// differents.</summary>
        private string Sdk20LockedNote =>
            tello == null || tello.SdkVersionUnknown ? "SDK 1.3" : $"SDK {tello.SdkVersion}";

        // -----------------------------------------------------------------
        // PAGE GENERALE - tout le reste, classe par theme
        // -----------------------------------------------------------------
        private void BuildGeneralRows()
        {
            AddSection("Battery");
            AddFloatRow("Low battery warning", 5f, 40f, 20f, "{0:F0}%",
                () => tello != null ? tello.BatteryLowThreshold : 20f,
                v => { if (tello != null) tello.BatteryLowThreshold = Mathf.RoundToInt(v); });
            AddFloatRow("Critical battery", 5f, 25f, 10f, "{0:F0}%",
                () => tello != null ? tello.BatteryCriticalThreshold : 10f,
                v => { if (tello != null) tello.BatteryCriticalThreshold = Mathf.RoundToInt(v); });
            AddBoolRow("Auto-land on critical battery", true,
                () => tello == null || tello.AutoLandOnCriticalBattery,
                v => { if (tello != null) tello.AutoLandOnCriticalBattery = v; });

            AddSection("Temperature");
            AddFloatRow("Temperature warning", 50f, 100f, 80f, "{0:F0}\u00B0C",
                () => tello != null ? tello.TemperatureWarningThreshold : 80f,
                v => { if (tello != null) tello.TemperatureWarningThreshold = v; });
            AddFloatRow("Temperature critical", 60f, 110f, 90f, "{0:F0}\u00B0C",
                () => tello != null ? tello.TemperatureCriticalThreshold : 90f,
                v => { if (tello != null) tello.TemperatureCriticalThreshold = v; });

            AddSection("Proximity");
            AddFloatRow("Proximity warning", 10f, 200f, 50f, "{0:F0}cm",
                () => tello != null ? tello.ProximityWarningCm : 50f,
                v => { if (tello != null) tello.ProximityWarningCm = Mathf.RoundToInt(v); });
            AddFloatRow("Proximity critical", 5f, 100f, 20f, "{0:F0}cm",
                () => tello != null ? tello.ProximityCriticalCm : 20f,
                v => { if (tello != null) tello.ProximityCriticalCm = Mathf.RoundToInt(v); });

            AddSection("Altitude ceiling");
            AddBoolRow("Altitude ceiling", false,
                () => tello != null && tello.EnableAltitudeCeiling,
                v => { if (tello != null) tello.EnableAltitudeCeiling = v; });
            AddFloatRow("Max height", 50f, 1000f, 300f, "{0:F0}cm",
                () => tello != null ? tello.MaxHeightCm : 300f,
                v => { if (tello != null) tello.MaxHeightCm = v; });
            AddFloatRow("Soft margin", 10f, 200f, 50f, "{0:F0}cm",
                () => tello != null ? tello.AltitudeCeilingSoftMarginCm : 50f,
                v => { if (tello != null) tello.AltitudeCeilingSoftMarginCm = v; });

            AddSection("Crash detection");
            AddBoolRow("Crash detection", true,
                () => tello == null || tello.EnableCrashDetection,
                v => { if (tello != null) tello.EnableCrashDetection = v; });
            AddFloatRow("Crash sensitivity", 1000f, 8000f, 3500f, "{0:F0}",
                () => tello != null ? tello.CrashAccelerationThreshold : 3500f,
                v => { if (tello != null) tello.CrashAccelerationThreshold = v; });
            AddBoolRow("Auto-land if crash suspected", false,
                () => tello != null && tello.AutoLandOnCrashSuspected,
                v => { if (tello != null) tello.AutoLandOnCrashSuspected = v; });

            AddSection("Navigation & logging");
            AddBoolRow("Position estimation", true,
                () => tello == null || tello.EnableDeadReckoning,
                v => { if (tello != null) tello.EnableDeadReckoning = v; });
            AddBoolRow("Flight log (CSV)", false,
                () => tello != null && tello.EnableFlightLog,
                v => { if (tello != null) tello.EnableFlightLog = v; });
            if (tello != null) buildingPage.saveActions.Add(tello.SavePersistedSettings);

            AddSection("Gamepad");
            AddFloatRow("Loss timeout (safety hover)", 0.1f, 3f, 0.5f, "{0:F1}s",
                () => gamepadController != null ? gamepadController.GamepadTimeoutSeconds : 0.5f,
                v => { if (gamepadController != null) gamepadController.GamepadTimeoutSeconds = v; });
            AddBoolRow("Auto-calibrate on connect", true,
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
            if (gamepadController != null) buildingPage.saveActions.Add(gamepadController.SavePersistedSettings);

            AddSection("Cockpit layout");
            AddFloatRow("Side panels gap", 0f, 0.1f, 0.01f, "{0:F3}m",
                () => spatialPanel != null ? spatialPanel.Gap : 0.01f,
                v => { if (spatialPanel != null) spatialPanel.Gap = v; if (actionLogPanel != null) actionLogPanel.Gap = v; });
            AddFloatRow("Cockpit angle", 0f, 60f, 20f, "{0:F0}\u00B0",
                () => spatialPanel != null ? spatialPanel.CockpitAngleDegrees : 20f,
                v => { if (spatialPanel != null) spatialPanel.CockpitAngleDegrees = v; if (actionLogPanel != null) actionLogPanel.CockpitAngleDegrees = v; });
            AddBoolRow("Pin panels to screen depth", true,
                () => spatialPanel == null || spatialPanel.PinInnerEdgeToScreenDepth,
                v => { if (spatialPanel != null) spatialPanel.PinInnerEdgeToScreenDepth = v; if (actionLogPanel != null) actionLogPanel.PinInnerEdgeToScreenDepth = v; });
            AddFloatRow("Panels depth offset", -0.3f, 0.3f, 0f, "{0:F3}m",
                () => spatialPanel != null ? spatialPanel.PanelDepthOffset : 0f,
                v => { if (spatialPanel != null) spatialPanel.PanelDepthOffset = v; if (actionLogPanel != null) actionLogPanel.PanelDepthOffset = v; });

            AddSection("Instrument graphs");
            AddFloatRow("Graph window", 30f, 300f, 60f, "{0:F0}s",
                () => spatialPanel != null ? spatialPanel.GraphWindowSeconds : 60f,
                v => { if (spatialPanel != null) spatialPanel.GraphWindowSeconds = v; });
            AddFloatRow("Graph sample interval", 0.5f, 5f, 1f, "{0:F1}s",
                () => spatialPanel != null ? spatialPanel.GraphSampleIntervalSeconds : 1f,
                v => { if (spatialPanel != null) spatialPanel.GraphSampleIntervalSeconds = v; });
            if (spatialPanel != null) buildingPage.saveActions.Add(spatialPanel.SavePersistedSettings);
            if (actionLogPanel != null) buildingPage.saveActions.Add(actionLogPanel.SavePersistedSettings);
        }

        private void BuildScrollArea(Transform parent)
        {
            var viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGO.transform.SetParent(parent, false);
            viewportRect = viewportGO.GetComponent<RectTransform>();
            viewportRect.sizeDelta = new Vector2(ContentWidth + 20f, ViewportHeight);
            viewportRect.anchoredPosition = new Vector2(-10f, 335f - ViewportHeight * 0.5f);

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

        // =================================================================
        // WIDGETS DE LIGNE
        //
        // Les curseurs ne sont plus une "bulle" arrondie remplie de jaune : chaque
        // ligne numerique porte maintenant une VRAIE echelle - un rail sombre, onze
        // graduations (les reperes 0 / 50 / 100 % etant plus hauts), une portion
        // remplie jusqu'a la valeur courante, et une poignee franche posee dessus,
        // detachee du fond par un lisere sombre pour rester lisible quelle que soit
        // la position. Les booleens ont leur propre widget : un interrupteur a
        // bascule dont le bouton glisse d'un cote a l'autre, plutot qu'une jauge
        // remplie a 0 % ou 100 % qui ne se lisait pas comme un interrupteur.
        // La ligne selectionnee recoit en plus un fond ambre tres discret, pour
        // qu'on sache toujours ou l'on est sans avoir a chercher.
        // =================================================================
        private void AddSection(string title)
        {
            var go = new GameObject($"Section_{title}", typeof(RectTransform));
            go.transform.SetParent(buildingPage.contentRect, false);
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
            ApplyFont(t, monoFont);
            t.fontSize = 13f;
            t.color = Amber;
            t.alignment = TextAlignmentOptions.MidlineLeft;
            t.characterSpacing = 6f;
            t.text = title.ToUpperInvariant();

            var lineGO = new GameObject("Rule", typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(r, false);
            RectTransform lineRect = lineGO.GetComponent<RectTransform>();
            lineRect.sizeDelta = new Vector2(ContentWidth, 1f);
            lineRect.anchoredPosition = new Vector2(0f, -33f);
            lineGO.GetComponent<Image>().color = PanelEdge;

            cursorY -= SectionHeaderHeight;
        }

        private void AddChoiceRow(string id, string label, string[] options, Action<int> onChanged)
        {
            SettingsRow row = BuildRowVisual(label, RowKind.Choice, options.Length);
            row.kind = RowKind.Choice;
            row.id = id;
            row.choiceLabels = options;
            row.choiceIndex = 0;
            row.onChoiceChanged = onChanged;
            Register(row);
            cursorY -= RowHeight;
        }

        /// <summary>Marque la derniere ligne ajoutee comme indisponible sur ce materiel.
        /// Elle reste visible et selectionnable, mais ne se regle plus et affiche la
        /// note a la place de sa valeur.</summary>
        private void MarkLastRowConditional(Func<bool> isAvailable, string lockedNote)
        {
            if (buildingPage.rows.Count == 0) return;
            SettingsRow row = buildingPage.rows[buildingPage.rows.Count - 1];
            row.isAvailable = isAvailable;
            row.lockedNote = lockedNote;
        }

        private void Register(SettingsRow row)
        {
            buildingPage.rows.Add(row);
            if (row.id != null) buildingPage.byId[row.id] = row;
        }

        private void AddFloatRow(string label, float min, float max, float defaultValue, string format, Func<float> getter, Action<float> setter, string id = null)
        {
            SettingsRow row = BuildRowVisual(label, RowKind.Float);
            row.kind = RowKind.Float;
            row.id = id;
            row.min = min;
            row.max = max;
            row.defaultFloat = defaultValue;
            row.format = format;
            row.getFloat = getter;
            row.setFloat = setter;
            Register(row);
            cursorY -= RowHeight;
        }

        private void AddBoolRow(string label, bool defaultValue, Func<bool> getter, Action<bool> setter, string id = null)
        {
            SettingsRow row = BuildRowVisual(label, RowKind.Bool);
            row.kind = RowKind.Bool;
            row.id = id;
            row.defaultBool = defaultValue;
            row.getBool = getter;
            row.setBool = setter;
            Register(row);
            cursorY -= RowHeight;
        }

        private SettingsRow BuildRowVisual(string label, RowKind kind, int segmentCount = 0)
        {
            var rowGO = new GameObject($"Row_{label}", typeof(RectTransform));
            rowGO.transform.SetParent(buildingPage.contentRect, false);
            RectTransform rowRect = rowGO.GetComponent<RectTransform>();
            rowRect.pivot = new Vector2(0.5f, 1f);
            rowRect.anchorMin = new Vector2(0.5f, 1f);
            rowRect.anchorMax = new Vector2(0.5f, 1f);
            rowRect.sizeDelta = new Vector2(ContentWidth, RowHeight);
            rowRect.anchoredPosition = new Vector2(0f, cursorY);

            // Cree en premier : reste donc derriere tout le reste de la ligne.
            var bgGO = new GameObject("SelectionBg", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(rowRect, false);
            RectTransform bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.sizeDelta = new Vector2(ContentWidth, RowHeight - 4f);
            bgRect.anchoredPosition = new Vector2(0f, -RowHeight * 0.5f);
            Image bgImage = bgGO.GetComponent<Image>();
            bgImage.sprite = roundedSprite;
            bgImage.type = Image.Type.Sliced;
            bgImage.color = RowClearBg;

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(rowRect, false);
            RectTransform labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(LabelWidth, 24f);
            labelRect.anchoredPosition = new Vector2(LabelX, -18f);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(labelText, bodyFont);
            labelText.fontSize = 14f;
            labelText.color = InkDim;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;
            labelText.textWrappingMode = TextWrappingModes.NoWrap;
            labelText.overflowMode = TextOverflowModes.Ellipsis;
            labelText.text = label;

            var valueGO = new GameObject("Value", typeof(RectTransform));
            valueGO.transform.SetParent(rowRect, false);
            RectTransform valueRect = valueGO.GetComponent<RectTransform>();
            valueRect.sizeDelta = new Vector2(100f, 26f);
            valueRect.anchoredPosition = new Vector2(ValueX, -18f);
            var valueText = valueGO.AddComponent<TextMeshProUGUI>();
            ApplyFont(valueText, monoFont);
            valueText.fontSize = 14f;
            valueText.color = Ink;
            valueText.alignment = TextAlignmentOptions.MidlineRight;

            var row = new SettingsRow
            {
                selectionBg = bgImage,
                labelText = labelText,
                valueText = valueText,
                rowY = cursorY
            };

            if (kind == RowKind.Float) BuildSlider(rowRect, row);
            else if (kind == RowKind.Bool) BuildSwitch(rowRect, row);
            else BuildSegments(rowRect, row, segmentCount);

            return row;
        }

        /// <summary>Rail + graduations + remplissage + poignee.</summary>
        private void BuildSlider(RectTransform rowRect, SettingsRow row)
        {
            var sliderGO = new GameObject("Slider", typeof(RectTransform));
            sliderGO.transform.SetParent(rowRect, false);
            RectTransform sliderRect = sliderGO.GetComponent<RectTransform>();
            sliderRect.sizeDelta = new Vector2(RailWidth, 30f);
            sliderRect.anchoredPosition = new Vector2(SliderCentreX, ControlY);
            row.sliderRoot = sliderGO;

            float half = RailWidth * 0.5f;

            // Graduations, SOUS le rail pour rester visibles meme quand la poignee
            // passe dessus. Reperes plus hauts au debut, au milieu et a la fin.
            for (int i = 0; i < TickCount; i++)
            {
                bool major = i == 0 || i == TickCount / 2 || i == TickCount - 1;
                var tickGO = new GameObject($"Tick{i}", typeof(RectTransform), typeof(Image));
                tickGO.transform.SetParent(sliderRect, false);
                RectTransform tickRect = tickGO.GetComponent<RectTransform>();
                tickRect.sizeDelta = new Vector2(major ? 2f : 1f, major ? 11f : 7f);
                tickRect.anchoredPosition = new Vector2(-half + i * (RailWidth / (TickCount - 1)), major ? -14f : -12f);
                tickGO.GetComponent<Image>().color = major ? PanelEdge : TickColor;
            }

            var railGO = new GameObject("Rail", typeof(RectTransform), typeof(Image));
            railGO.transform.SetParent(sliderRect, false);
            RectTransform railRect = railGO.GetComponent<RectTransform>();
            railRect.sizeDelta = new Vector2(RailWidth, RailHeight);
            railRect.anchoredPosition = Vector2.zero;
            Image railImage = railGO.GetComponent<Image>();
            railImage.sprite = circleSprite;
            railImage.type = Image.Type.Simple;
            railImage.color = TrackBg;

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(sliderRect, false);
            row.fillRect = fillGO.GetComponent<RectTransform>();
            row.fillRect.pivot = new Vector2(0f, 0.5f);
            row.fillRect.sizeDelta = new Vector2(0f, RailHeight);
            row.fillRect.anchoredPosition = new Vector2(-half, 0f);
            row.fillImage = fillGO.GetComponent<Image>();
            row.fillImage.sprite = circleSprite;
            row.fillImage.type = Image.Type.Simple;
            row.fillImage.color = AmberDeep;

            // Lisere sombre derriere la poignee : sans lui, une poignee claire posee
            // sur une portion remplie claire devient impossible a localiser.
            var haloGO = new GameObject("HandleEdge", typeof(RectTransform), typeof(Image));
            haloGO.transform.SetParent(sliderRect, false);
            RectTransform haloRect = haloGO.GetComponent<RectTransform>();
            haloRect.sizeDelta = new Vector2(18f, 24f);
            haloRect.anchoredPosition = Vector2.zero;
            Image haloImage = haloGO.GetComponent<Image>();
            haloImage.sprite = handleSprite;
            haloImage.type = Image.Type.Sliced;
            haloImage.color = PanelBg;

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(sliderRect, false);
            row.handleRect = handleGO.GetComponent<RectTransform>();
            row.handleRect.sizeDelta = new Vector2(14f, 20f);
            row.handleRect.anchoredPosition = Vector2.zero;
            row.handleImage = handleGO.GetComponent<Image>();
            row.handleImage.sprite = handleSprite;
            row.handleImage.type = Image.Type.Sliced;
            row.handleImage.color = InkDim;

            // Le lisere doit suivre la poignee : on le stocke comme enfant logique en
            // le repositionnant depuis RefreshRows via handleRect (voir plus bas).
            row.handleRect.SetAsLastSibling();
            haloRect.SetSiblingIndex(row.handleRect.GetSiblingIndex());
            row.handleEdge = haloRect;
        }

        /// <summary>Selecteur a segments : une pastille par option, la courante remplie.
        /// Visuellement parent du curseur (meme rail, meme largeur), mais discret : un
        /// choix parmi 5 ne se lit pas comme une valeur continue.</summary>
        private void BuildSegments(RectTransform rowRect, SettingsRow row, int count)
        {
            if (count <= 0) return;

            var groupGO = new GameObject("Segments", typeof(RectTransform));
            groupGO.transform.SetParent(rowRect, false);
            RectTransform groupRect = groupGO.GetComponent<RectTransform>();
            groupRect.sizeDelta = new Vector2(RailWidth, 10f);
            groupRect.anchoredPosition = new Vector2(SliderCentreX, ControlY);

            const float gap = 5f;
            float segWidth = (RailWidth - gap * (count - 1)) / count;
            float startX = -RailWidth * 0.5f + segWidth * 0.5f;

            for (int i = 0; i < count; i++)
            {
                var segGO = new GameObject($"Seg{i}", typeof(RectTransform), typeof(Image));
                segGO.transform.SetParent(groupRect, false);
                RectTransform segRect = segGO.GetComponent<RectTransform>();
                segRect.sizeDelta = new Vector2(segWidth, 6f);
                segRect.anchoredPosition = new Vector2(startX + i * (segWidth + gap), 0f);
                Image segImage = segGO.GetComponent<Image>();
                segImage.sprite = circleSprite;
                segImage.type = Image.Type.Simple;
                segImage.color = TrackBg;
                row.segments.Add(segImage);
            }
        }

        /// <summary>Interrupteur a bascule : piste en pilule + bouton qui glisse.</summary>
        private void BuildSwitch(RectTransform rowRect, SettingsRow row)
        {
            var switchGO = new GameObject("Switch", typeof(RectTransform));
            switchGO.transform.SetParent(rowRect, false);
            RectTransform switchRect = switchGO.GetComponent<RectTransform>();
            switchRect.sizeDelta = new Vector2(56f, 26f);
            switchRect.anchoredPosition = new Vector2(SliderCentreX - RailWidth * 0.5f + 28f, ControlY);
            row.switchRoot = switchGO;

            var trackGO = new GameObject("Track", typeof(RectTransform), typeof(Image));
            trackGO.transform.SetParent(switchRect, false);
            RectTransform trackRect = trackGO.GetComponent<RectTransform>();
            trackRect.sizeDelta = new Vector2(56f, 26f);
            trackRect.anchoredPosition = Vector2.zero;
            row.switchTrack = trackGO.GetComponent<Image>();
            row.switchTrack.sprite = circleSprite;
            row.switchTrack.type = Image.Type.Simple;
            row.switchTrack.color = SwitchOffTrack;

            var knobGO = new GameObject("Knob", typeof(RectTransform), typeof(Image));
            knobGO.transform.SetParent(switchRect, false);
            row.switchKnob = knobGO.GetComponent<RectTransform>();
            row.switchKnob.sizeDelta = new Vector2(20f, 20f);
            row.switchKnob.anchoredPosition = new Vector2(-14f, 0f);
            row.switchKnobImage = knobGO.GetComponent<Image>();
            row.switchKnobImage.sprite = circleSprite;
            row.switchKnobImage.type = Image.Type.Simple;
            row.switchKnobImage.color = InkDim;
        }

        private static readonly Color LockedInk = HexColor("#565C62");

        /// <summary>Estompe le controle d'une ligne indisponible sans le supprimer -
        /// la ligne garde sa forme, on voit juste qu'elle est hors service.</summary>
        private static void DimControl(SettingsRow row)
        {
            if (row.fillImage != null) row.fillImage.color = TrackBg;
            if (row.handleImage != null) row.handleImage.color = LockedInk;
            if (row.switchTrack != null) row.switchTrack.color = SwitchOffTrack;
            if (row.switchKnobImage != null) row.switchKnobImage.color = LockedInk;
            for (int i = 0; i < row.segments.Count; i++) row.segments[i].color = TrackBg;
        }

        // =================================================================
        // RAFRAICHISSEMENT
        // =================================================================
        private void RefreshRows()
        {
            var rows = activePage.rows;
            for (int i = 0; i < rows.Count; i++)
            {
                SettingsRow row = rows[i];
                bool selected = i == activePage.selectedRow;
                bool available = row.isAvailable == null || row.isAvailable();

                row.selectionBg.color = selected ? RowSelectedBg : RowClearBg;
                row.labelText.color = !available ? LockedInk : (selected ? Ink : InkDim);
                row.labelText.fontStyle = selected && available ? FontStyles.Bold : FontStyles.Normal;
                row.valueText.color = !available ? LockedInk : (selected ? Amber : Ink);

                // Ligne indisponible : on affiche la raison a la place de la valeur, et
                // le controle est estompe. Le pilote comprend donc pourquoi il ne peut
                // rien regler, au lieu de pousser le stick sans effet.
                if (!available)
                {
                    row.valueText.text = row.lockedNote ?? "N/A";
                    DimControl(row);
                    continue;
                }

                if (row.kind == RowKind.Choice)
                {
                    for (int seg = 0; seg < row.segments.Count; seg++)
                        row.segments[seg].color = seg == row.choiceIndex
                            ? (selected ? Amber : AmberDeep)
                            : TrackBg;
                    row.valueText.text = row.choiceLabels[row.choiceIndex];
                    continue;
                }

                if (row.kind == RowKind.Float)
                {
                    float normalized = row.max > row.min ? Mathf.InverseLerp(row.min, row.max, row.floatValue) : 0f;
                    float half = RailWidth * 0.5f;
                    float handleX = -half + normalized * RailWidth;

                    row.fillRect.sizeDelta = new Vector2(normalized * RailWidth, RailHeight);
                    row.fillImage.color = selected ? Amber : AmberDeep;

                    row.handleRect.anchoredPosition = new Vector2(handleX, 0f);
                    row.handleEdge.anchoredPosition = new Vector2(handleX, 0f);
                    row.handleImage.color = selected ? Amber : Ink;

                    row.valueText.text = string.Format(row.format, row.floatValue);
                }
                else
                {
                    row.switchKnob.anchoredPosition = new Vector2(row.boolValue ? 14f : -14f, 0f);
                    row.switchTrack.color = row.boolValue ? (selected ? Amber : AmberDeep) : SwitchOffTrack;
                    row.switchKnobImage.color = row.boolValue ? PanelBg : InkDim;
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
