using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace TelloQuest
{
    /// <summary>
    /// Displays the decoded Tello video feed on a flat quad. World-locked:
    /// positioned once at Start relative to where you were looking when the
    /// app launched, then never moves again - turning your head does NOT move
    /// it, same as a monitor bolted to a wall.
    ///
    /// Orientation: the Tello's raw feed comes in upside-down but NOT
    /// mirrored left/right, so a fixed vertical flip (applied as a UV
    /// transform, not by rotating the quad) is baked in below. This isn't a
    /// dropdown anymore - a prior pass through this file exposed a 4-option
    /// Orientation setting because it wasn't clear yet which single transform
    /// was actually correct; now that it's confirmed, there's nothing left to
    /// configure.
    ///
    /// Setup required in the Editor (once):
    /// 1. Create Assets/Materials/TelloVideoRGBA.mat, shader "Universal Render
    ///    Pipeline/Unlit", Render Face = Both.
    /// 2. Create Assets/Materials/TelloVideoYUV.mat, shader
    ///    "TelloQuest/YuvNV12ToRGB" (already double-sided by the shader itself).
    /// 3. Assign both below.
    ///
    /// ATTENTION sur ces deux materiaux : les assets livres avaient tous les deux
    /// ete obtenus en changeant le shader d'un materiau URP/Lit, et TelloVideoRGBA
    /// etait en realite reste sur "Universal Render Pipeline/Lit" (verifie via son
    /// GUID de shader) - donc le chemin de secours RGBA passait la video a travers
    /// un shader PBR eclaire, affecte par la lumiere directionnelle et l'ambiante.
    /// Ils trainent aussi tout le bagage d'un Lit (_Metallic, _BumpMap, lightmaps)
    /// et, pour le YUV, des valeurs gravees par d'anciennes sessions de playmode
    /// (_SharpenStrength=0.4, _NightModeStrength=0.35, alors que le shader les
    /// declare a 0). A recreer proprement tous les deux.
    ///
    /// Ce composant ne modifie plus les assets : il en fait des copies runtime dans
    /// Awake() (rgbaInstance / yuvInstance), donc plus aucune derive silencieuse.
    /// </summary>
    public class TelloVideoDisplay : MonoBehaviour
    {
        [SerializeField] private TelloVideoDecoder decoder;
        [SerializeField] private Transform vrCamera;

        [Header("=== MATERIALS (project assets - create in Editor, never Shader.Find) ===")]
        [SerializeField] private Material rgbaMaterial;
        [SerializeField] private Material yuvMaterial;

        [Header("=== SIZE / PLACEMENT ===")]
        [SerializeField] private float distanceFromCamera = 1.2f;
        [Tooltip("Shifts the whole screen+banners ensemble down (negative) or up (positive) from dead-center eye level.")]
        [SerializeField] private float verticalOffset = -0.3f;
        [SerializeField] private float quadWidth = 1.9f;
        [SerializeField] private float quadHeight = 1.4f;

        [Header("=== ZOOM (screen size, adjustable at runtime) ===")]
        [Tooltip("Size multipliers applied to Quad Width/Height, index 0 = level 1.")]
        [SerializeField] private float[] zoomMultipliers = { 0.6f, 0.8f, 1f, 1.25f, 1.5f };
        [SerializeField] private int defaultZoomLevel = 3;

        [Header("=== SETTINGS-SCREEN ADJUSTABLE (continuous, on top of zoom above) ===")]
        [Tooltip("Extra continuous size multiplier stacked on top of the discrete zoom level - this is what the Settings screen's size slider drives.")]
        [SerializeField, Range(0.5f, 10f)] private float sizeMultiplier = 1f;
        [Tooltip("0 = fully see-through, 1 = fully opaque. Requires rgbaMaterial/yuvMaterial's Surface Type set to Transparent in the Editor - alpha has no visible effect on an Opaque-surface material.")]
        [SerializeField, Range(0.15f, 1f)] private float opacity = 1f;
        [Tooltip("Manual correction for a color cast coming from the Tello's own camera/sensor - negative = cooler/blue, positive = warmer/yellow. 0 = neutral, no change. Only affects the YUV material (the RGBA path uses Unity's built-in Unlit shader, which can't be edited the same way).")]
        [SerializeField, Range(-1f, 1f)] private float whiteBalanceShift = 0f;
        [Tooltip("Manual overall brightness - negative = darker, positive = brighter. 0 = neutral. Applied after white balance, before contrast. YUV material only.")]
        [SerializeField, Range(-1f, 1f)] private float brightness = 0f;
        [Tooltip("Manual contrast, scaled around the midpoint. 1 = neutral. YUV material only.")]
        [SerializeField, Range(0.5f, 2f)] private float contrast = 1f;
        [Tooltip("How strongly automatic night mode brightens dark footage - 0 by default (no processing at all until the pilot raises this). Turn this down if it's over-brightening in your conditions rather than fighting it with negative Brightness above. YUV material only.")]
        [SerializeField, Range(0f, 1f)] private float nightModeStrength = 0f;
        [Tooltip("How strongly the automatic sharpening pass counters H.264 softness - 0 by default (no processing at all until the pilot raises this). YUV material only.")]
        [SerializeField, Range(0f, 1.5f)] private float sharpenStrength = 0f;
        [Tooltip("A partir de quelle luminance le night mode commence a relever l'image. Plus haut = seul le vraiment sombre est releve ; plus bas = l'effet mord aussi sur les demi-teintes.")]
        [SerializeField, Range(0.5f, 4f)] private float nightModeThreshold = 2f;
        [Tooltip("Flou melange proportionnellement au boost night mode, pour masquer le bruit capteur que ce boost amplifie. Sans effet si Night Mode Strength = 0.")]
        [SerializeField, Range(0f, 1f)] private float nightModeBlurStrength = 0f;
        [Tooltip("Lissage a preservation de contours. Attenue le bruit et le blocking H.264 dans les zones plates tout en gardant les contours nets - c'est le reglage a monter pour une image plus douce SANS perdre de detail. 0.4 a 0.6 est un bon point de depart.")]
        [SerializeField, Range(0f, 1f)] private float smoothStrength = 0f;
        [Tooltip("A partir de quel ecart de luminance le lissage considere qu'il y a un contour a preserver. Bas = preserve plus de detail mais lisse moins ; haut = lisse plus mais commence a manger les contours faibles.")]
        [SerializeField, Range(0.01f, 0.5f)] private float smoothEdgeThreshold = 0.08f;
        [Tooltip("Correction du positionnement chroma 4:2:0, en texels luma. 0.5 correspond au siting 'left' standard du H.264 et supprime une frange de couleur d'un demi-pixel sur les contours verticaux. Ne toucher que si tu vois un lisere colore decale.")]
        [SerializeField, Range(-1f, 1f)] private float chromaSiteOffset = 0.5f;

        [Header("=== IMAGE QUALITY (materiau YUV) ===")]
        [Tooltip("Upscale bicubique Catmull-Rom au lieu du bilineaire. Nettement plus propre pour agrandir du 960x720 sur un grand ecran virtuel.")]
        [SerializeField] private bool bicubicUpscale = true;
        [Tooltip("A cocher UNIQUEMENT si l'image parait trop contrastee/sombre apres correction : signifie que PopH264 cree ses Texture2D sans le flag 'linear' et qu'Unity leur applique une conversion sRGB parasite au sampling.")]
        [SerializeField] private bool planesSampledAsSRGB = false;
        [Tooltip("Force BT.709 meme si le SPS n'en dit rien. Laisser decoche : la valeur vient du SPS quand une VUI est presente.")]
        [SerializeField] private bool forceBt709 = false;

        [Header("=== TEST MODE ===")]
        [Tooltip("Show a generated checker pattern instead of the real feed - validate placement/material first.")]
        [SerializeField] private bool useTestTexture = false;
        [Tooltip("Optional: use your own texture instead of the generated checker pattern.")]
        [SerializeField] private Texture2D customTestTexture;

        private int zoomLevel = 1; // safe non-zero default - see EffectiveZoomIndex
        private MeshRenderer screenRenderer;
        private Transform quadTransform;

        // Instances RUNTIME des deux materiaux. Les champs serialises ci-dessus
        // referencent des ASSETS de projet : ecrire dedans avec SetFloat/SetTexture
        // modifiait l'asset lui-meme, et en Editor ces modifications etaient
        // sauvegardees definitivement. C'est ainsi que TelloVideoYUV.mat s'est
        // retrouve avec _SharpenStrength=0.4 et _NightModeStrength=0.35 grave
        // dedans alors que le shader les declare a 0. On travaille desormais sur
        // des copies, les assets restent intacts.
        private Material rgbaInstance;
        private Material yuvInstance;

        private static readonly int YTexId = Shader.PropertyToID("_YTex");
        private static readonly int UVTexId = Shader.PropertyToID("_UVTex");
        private static readonly int CropScaleId = Shader.PropertyToID("_CropScale");
        private static readonly int SwapUVId = Shader.PropertyToID("_SwapUV");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWriteId = Shader.PropertyToID("_ZWrite");

        public int ZoomLevel => zoomLevel;
        public int MaxZoomLevel => zoomMultipliers.Length;
        private int EffectiveZoomIndex => Mathf.Clamp(zoomLevel - 1, 0, zoomMultipliers.Length - 1);

        /// <summary>Effective width/height, base size x current zoom multiplier x the continuous
        /// Settings-screen size multiplier - always reflects what's actually on screen right now.</summary>
        public float QuadWidth => quadWidth * zoomMultipliers[EffectiveZoomIndex] * sizeMultiplier;
        public float QuadHeight => quadHeight * zoomMultipliers[EffectiveZoomIndex] * sizeMultiplier;

        public float DistanceFromCamera { get => distanceFromCamera; set => distanceFromCamera = value; }
        public float VerticalOffset { get => verticalOffset; set => verticalOffset = value; }
        public float AssumedEyeHeightMeters { get => assumedEyeHeightMeters; set => assumedEyeHeightMeters = value; }
        public float SizeMultiplier => sizeMultiplier;
        public float Opacity => opacity;
        public float WhiteBalanceShift => whiteBalanceShift;
        public float Brightness => brightness;
        public float Contrast => contrast;
        public float NightModeStrength => nightModeStrength;
        public float SharpenStrength => sharpenStrength;
        public float SmoothStrength => smoothStrength;
        public float SmoothEdgeThreshold => smoothEdgeThreshold;
        public float NightModeThreshold => nightModeThreshold;
        public float NightModeBlurStrength => nightModeBlurStrength;
        public float ChromaSiteOffset => chromaSiteOffset;
        public bool BicubicUpscale => bicubicUpscale;
        public bool PlanesSampledAsSRGB => planesSampledAsSRGB;
        public bool ForceBt709 => forceBt709;

        /// <summary>Raised whenever the zoom level changes - banners listen to this to stay glued to the resized screen.</summary>
        public event System.Action OnSizeChanged;

        public void SetZoomLevel(int level)
        {
            zoomLevel = Mathf.Clamp(level, 1, zoomMultipliers.Length);
            ApplyZoomScale();
            OnSizeChanged?.Invoke();
        }

        /// <summary>Called by TelloSettingsScreen on save - continuous size, independent of the discrete zoom levels above.</summary>
        public void SetSizeMultiplier(float multiplier)
        {
            sizeMultiplier = Mathf.Clamp(multiplier, 0.5f, 10f);
            ApplyZoomScale();
            OnSizeChanged?.Invoke();
        }

        /// <summary>Called by TelloSettingsScreen on save. See the opacity field's tooltip - the
        /// material's Surface Type must be Transparent in the Editor for this to be visible.</summary>
        public void SetOpacity(float value)
        {
            opacity = Mathf.Clamp(value, 0.15f, 1f);
            ApplyOpacity();
        }

        private void ApplyOpacity()
        {
            if (rgbaInstance != null)
            {
                Color c = rgbaInstance.color;
                c.a = opacity;
                rgbaInstance.color = c;
            }
            if (yuvInstance == null) return;

            yuvInstance.SetFloat(OpacityId, opacity);

            // A opacite pleine on repasse en rendu opaque. Le shader tournait en
            // permanence en file Transparent avec alpha blending et ZWrite Off,
            // meme a opacite 1 : sur le GPU tuile du Quest c'est de la bande
            // passante de blending payee pour rien, et aucun early-Z possible.
            bool opaque = opacity >= 0.999f;
            yuvInstance.SetFloat(SrcBlendId, (float)(opaque ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.SrcAlpha));
            yuvInstance.SetFloat(DstBlendId, (float)(opaque ? UnityEngine.Rendering.BlendMode.Zero : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            yuvInstance.SetFloat(ZWriteId, opaque ? 1f : 0f);
            yuvInstance.renderQueue = opaque
                ? (int)UnityEngine.Rendering.RenderQueue.Geometry
                : (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>Recopie dans le materiau ce que le decodeur a reellement lu dans
        /// le flux : crop du padding MediaCodec, plage full/limited, matrice
        /// BT.601/709, ordre des canaux chroma. Appele au demarrage et a chaque fois
        /// que le decodeur signale un changement de format.</summary>
        private void ApplyDecoderFormat()
        {
            if (yuvInstance == null) return;

            Vector2 crop = decoder != null ? decoder.CropScale : Vector2.one;
            yuvInstance.SetVector(CropScaleId, new Vector4(crop.x, crop.y, 0f, 0f));

            if (decoder != null)
            {
                yuvInstance.SetFloat(SwapUVId, decoder.UvChannelsSwapped ? 1f : 0f);
                SetKeyword(yuvInstance, "_FULLRANGE_ON", decoder.IsFullRange);
                SetKeyword(yuvInstance, "_BT709_ON", forceBt709 || decoder.IsBt709);
            }

            SetKeyword(yuvInstance, "_BICUBIC_ON", bicubicUpscale);
            SetKeyword(yuvInstance, "_PLANES_SRGB_ON", planesSampledAsSRGB);
            ApplyEnhanceKeyword();
        }

        /// <summary>Les 4 taps voisins du sharpen / night mode etaient payes a chaque
        /// pixel meme quand les deux effets etaient a zero (leur valeur par defaut).
        /// Le mot-cle les supprime completement dans ce cas.</summary>
        private void ApplyEnhanceKeyword()
        {
            if (yuvInstance == null) return;
            bool enhance = sharpenStrength > 0.001f || nightModeStrength > 0.001f || smoothStrength > 0.001f;
            SetKeyword(yuvInstance, "_ENHANCE_ON", enhance);
        }

        private static void SetKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV
        /// material - see the field's tooltip.</summary>
        public void SetWhiteBalanceShift(float value)
        {
            whiteBalanceShift = Mathf.Clamp(value, -1f, 1f);
            ApplyWhiteBalanceShift();
        }

        private void ApplyWhiteBalanceShift()
        {
            if (yuvInstance != null) yuvInstance.SetFloat("_WhiteBalanceShift", whiteBalanceShift);
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetBrightness(float value)
        {
            brightness = Mathf.Clamp(value, -1f, 1f);
            if (yuvInstance != null) yuvInstance.SetFloat("_Brightness", brightness);
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetContrast(float value)
        {
            contrast = Mathf.Clamp(value, 0.5f, 2f);
            if (yuvInstance != null) yuvInstance.SetFloat("_Contrast", contrast);
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetNightModeStrength(float value)
        {
            nightModeStrength = Mathf.Clamp01(value);
            if (yuvInstance != null) yuvInstance.SetFloat("_NightModeStrength", nightModeStrength);
            ApplyEnhanceKeyword();
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetSharpenStrength(float value)
        {
            sharpenStrength = Mathf.Clamp(value, 0f, 1.5f);
            if (yuvInstance != null) yuvInstance.SetFloat("_SharpenStrength", sharpenStrength);
            ApplyEnhanceKeyword();
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetNightModeThreshold(float value)
        {
            nightModeThreshold = Mathf.Clamp(value, 0.5f, 4f);
            if (yuvInstance != null) yuvInstance.SetFloat("_NightModeThreshold", nightModeThreshold);
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetNightModeBlurStrength(float value)
        {
            nightModeBlurStrength = Mathf.Clamp01(value);
            if (yuvInstance != null) yuvInstance.SetFloat("_NightModeBlurStrength", nightModeBlurStrength);
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetChromaSiteOffset(float value)
        {
            chromaSiteOffset = Mathf.Clamp(value, -1f, 1f);
            if (yuvInstance != null) yuvInstance.SetFloat("_ChromaSiteOffset", chromaSiteOffset);
        }

        /// <summary>Called by TelloSettingsScreen on save. Bascule le mot-cle de shader.</summary>
        public void SetBicubicUpscale(bool value)
        {
            bicubicUpscale = value;
            if (yuvInstance != null) SetKeyword(yuvInstance, "_BICUBIC_ON", bicubicUpscale);
        }

        /// <summary>Called by TelloSettingsScreen on save. Bascule le mot-cle de shader.</summary>
        public void SetPlanesSampledAsSRGB(bool value)
        {
            planesSampledAsSRGB = value;
            if (yuvInstance != null) SetKeyword(yuvInstance, "_PLANES_SRGB_ON", planesSampledAsSRGB);
        }

        /// <summary>Called by TelloSettingsScreen on save. Bascule le mot-cle de shader.
        /// N'a d'effet que si le SPS ne declare pas deja explicitement une matrice.</summary>
        public void SetForceBt709(bool value)
        {
            forceBt709 = value;
            if (yuvInstance != null) SetKeyword(yuvInstance, "_BT709_ON", forceBt709 || (decoder != null && decoder.IsBt709));
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetSmoothStrength(float value)
        {
            smoothStrength = Mathf.Clamp01(value);
            if (yuvInstance != null) yuvInstance.SetFloat("_SmoothStrength", smoothStrength);
            ApplyEnhanceKeyword();
        }

        /// <summary>Called by TelloSettingsScreen on save. Only applies to the YUV material.</summary>
        public void SetSmoothEdgeThreshold(float value)
        {
            smoothEdgeThreshold = Mathf.Clamp(value, 0.01f, 0.5f);
            if (yuvInstance != null) yuvInstance.SetFloat("_SmoothEdgeThreshold", smoothEdgeThreshold);
        }

        private void ApplyZoomScale()
        {
            if (quadTransform != null) quadTransform.localScale = new Vector3(QuadWidth, QuadHeight, 1f);
        }

        private const string PrefsPrefix = "TelloQuest_Settings_";

        private void LoadPersistedSettings()
        {
            distanceFromCamera = PlayerPrefs.GetFloat(PrefsPrefix + "Distance", distanceFromCamera);
            verticalOffset = PlayerPrefs.GetFloat(PrefsPrefix + "VerticalOffset", verticalOffset);
            assumedEyeHeightMeters = PlayerPrefs.GetFloat(PrefsPrefix + "EyeHeight", assumedEyeHeightMeters);
            sizeMultiplier = PlayerPrefs.GetFloat(PrefsPrefix + "SizeMultiplier", sizeMultiplier);
            opacity = PlayerPrefs.GetFloat(PrefsPrefix + "Opacity", opacity);
            whiteBalanceShift = PlayerPrefs.GetFloat(PrefsPrefix + "WhiteBalanceShift", whiteBalanceShift);
            brightness = PlayerPrefs.GetFloat(PrefsPrefix + "Brightness", brightness);
            contrast = PlayerPrefs.GetFloat(PrefsPrefix + "Contrast", contrast);
            nightModeStrength = PlayerPrefs.GetFloat(PrefsPrefix + "NightModeStrength", nightModeStrength);
            sharpenStrength = PlayerPrefs.GetFloat(PrefsPrefix + "SharpenStrength", sharpenStrength);
            nightModeBlurStrength = PlayerPrefs.GetFloat(PrefsPrefix + "NightModeBlur", nightModeBlurStrength);
            nightModeThreshold = PlayerPrefs.GetFloat(PrefsPrefix + "NightModeThreshold", nightModeThreshold);
            chromaSiteOffset = PlayerPrefs.GetFloat(PrefsPrefix + "ChromaSiteOffset", chromaSiteOffset);
            bicubicUpscale = PlayerPrefs.GetInt(PrefsPrefix + "Bicubic", bicubicUpscale ? 1 : 0) == 1;
            planesSampledAsSRGB = PlayerPrefs.GetInt(PrefsPrefix + "PlanesSRGB", planesSampledAsSRGB ? 1 : 0) == 1;
            forceBt709 = PlayerPrefs.GetInt(PrefsPrefix + "ForceBt709", forceBt709 ? 1 : 0) == 1;
            smoothStrength = PlayerPrefs.GetFloat(PrefsPrefix + "SmoothStrength", smoothStrength);
            smoothEdgeThreshold = PlayerPrefs.GetFloat(PrefsPrefix + "SmoothEdge", smoothEdgeThreshold);
            defaultZoomLevel = PlayerPrefs.GetInt(PrefsPrefix + "ZoomLevel", defaultZoomLevel);
        }

        /// <summary>Called by TelloSettingsScreen after writing new values via the setters above, to persist them for next launch.</summary>
        public void SavePersistedSettings()
        {
            PlayerPrefs.SetFloat(PrefsPrefix + "Distance", distanceFromCamera);
            PlayerPrefs.SetFloat(PrefsPrefix + "VerticalOffset", verticalOffset);
            PlayerPrefs.SetFloat(PrefsPrefix + "EyeHeight", assumedEyeHeightMeters);
            PlayerPrefs.SetFloat(PrefsPrefix + "SizeMultiplier", sizeMultiplier);
            PlayerPrefs.SetFloat(PrefsPrefix + "Opacity", opacity);
            PlayerPrefs.SetFloat(PrefsPrefix + "WhiteBalanceShift", whiteBalanceShift);
            PlayerPrefs.SetFloat(PrefsPrefix + "Brightness", brightness);
            PlayerPrefs.SetFloat(PrefsPrefix + "Contrast", contrast);
            PlayerPrefs.SetFloat(PrefsPrefix + "NightModeStrength", nightModeStrength);
            PlayerPrefs.SetFloat(PrefsPrefix + "SharpenStrength", sharpenStrength);
            PlayerPrefs.SetFloat(PrefsPrefix + "NightModeBlur", nightModeBlurStrength);
            PlayerPrefs.SetFloat(PrefsPrefix + "NightModeThreshold", nightModeThreshold);
            PlayerPrefs.SetFloat(PrefsPrefix + "ChromaSiteOffset", chromaSiteOffset);
            PlayerPrefs.SetInt(PrefsPrefix + "Bicubic", bicubicUpscale ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "PlanesSRGB", planesSampledAsSRGB ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "ForceBt709", forceBt709 ? 1 : 0);
            PlayerPrefs.SetFloat(PrefsPrefix + "SmoothStrength", smoothStrength);
            PlayerPrefs.SetFloat(PrefsPrefix + "SmoothEdge", smoothEdgeThreshold);
            PlayerPrefs.SetInt(PrefsPrefix + "ZoomLevel", zoomLevel);
            PlayerPrefs.Save(); // manquait : les reglages n'etaient pas garantis ecrits sur disque
        }

        private void Awake()
        {
            if (decoder == null) decoder = GetComponent<TelloVideoDecoder>();

            if (rgbaMaterial == null || yuvMaterial == null)
                Debug.LogError("[TelloVideoDisplay] rgbaMaterial or yuvMaterial not assigned in the inspector - create them as project assets first (see class doc comment).");

            // Copies runtime : on ne touche plus jamais aux assets de projet.
            if (rgbaMaterial != null) rgbaInstance = new Material(rgbaMaterial) { name = rgbaMaterial.name + " (runtime)" };
            if (yuvMaterial != null) yuvInstance = new Material(yuvMaterial) { name = yuvMaterial.name + " (runtime)" };

            LoadPersistedSettings();
            BuildQuad();

            zoomLevel = Mathf.Clamp(defaultZoomLevel, 1, zoomMultipliers.Length);
            ApplyZoomScale();
            ApplyOpacity();
            ApplyWhiteBalanceShift();
            if (yuvInstance != null)
            {
                yuvInstance.SetFloat("_Brightness", brightness);
                yuvInstance.SetFloat("_Contrast", contrast);
                yuvInstance.SetFloat("_NightModeStrength", nightModeStrength);
                yuvInstance.SetFloat("_SharpenStrength", sharpenStrength);
                yuvInstance.SetFloat("_NightModeBlurStrength", nightModeBlurStrength);
                yuvInstance.SetFloat("_NightModeThreshold", nightModeThreshold);
                yuvInstance.SetFloat("_SmoothStrength", smoothStrength);
                yuvInstance.SetFloat("_SmoothEdgeThreshold", smoothEdgeThreshold);
                yuvInstance.SetFloat("_ChromaSiteOffset", chromaSiteOffset);
            }
            ApplyDecoderFormat();

            ApplyOrientation();

            if (useTestTexture) ShowTestTexture();
        }

        private void OnEnable()
        {
            if (decoder == null) return;
            decoder.OnTextureUpdated += HandleTextureUpdated;
            decoder.OnVideoFormatChanged += ApplyDecoderFormat;
        }

        private void OnDisable()
        {
            if (decoder == null) return;
            decoder.OnTextureUpdated -= HandleTextureUpdated;
            decoder.OnVideoFormatChanged -= ApplyDecoderFormat;
        }

        private void OnDestroy()
        {
            if (rgbaInstance != null) Destroy(rgbaInstance);
            if (yuvInstance != null) Destroy(yuvInstance);
        }

        /// <summary>Applies a fixed vertical flip as a UV transform - RGBA material via the standard mainTextureScale/Offset, YUV material via explicit shader properties. See the class doc comment for why this one fixed transform is correct (not a per-project setting).</summary>
        private void ApplyOrientation()
        {
            if (rgbaInstance != null)
            {
                rgbaInstance.mainTextureScale = new Vector2(1f, -1f);
                rgbaInstance.mainTextureOffset = new Vector2(0f, 1f);
            }
            if (yuvInstance != null)
            {
                yuvInstance.SetFloat("_FlipU", 0f);
                yuvInstance.SetFloat("_FlipV", 1f);
            }
        }

        private void ShowTestTexture()
        {
            if (rgbaInstance == null) return;
            Texture2D tex = customTestTexture != null ? customTestTexture : GenerateCheckerTexture();
            rgbaInstance.mainTexture = tex;
            screenRenderer.sharedMaterial = rgbaInstance;
        }

        private void HandleTextureUpdated()
        {
            if (useTestTexture) return; // ignore real frames while validating the test pattern

            if (decoder.IsYuvNv12)
            {
                if (yuvInstance == null) return;
                if (screenRenderer.sharedMaterial != yuvInstance) screenRenderer.sharedMaterial = yuvInstance;
                // Les Texture2D de PopH264 sont reutilisees d'une frame a l'autre :
                // on ne reassigne que si l'objet a reellement change, sinon c'est un
                // SetTexture inutile a chaque frame decodee.
                if (yuvInstance.GetTexture(YTexId) != decoder.YPlane) yuvInstance.SetTexture(YTexId, decoder.YPlane);
                if (yuvInstance.GetTexture(UVTexId) != decoder.UVPlane) yuvInstance.SetTexture(UVTexId, decoder.UVPlane);
            }
            else
            {
                if (rgbaInstance == null) return;
                if (screenRenderer.sharedMaterial != rgbaInstance) screenRenderer.sharedMaterial = rgbaInstance;
                if (rgbaInstance.mainTexture != decoder.VideoTexture)
                    rgbaInstance.mainTexture = decoder.VideoTexture;
            }
        }

        /// <summary>
        /// Returns a still frame of what's currently on screen, as a readable
        /// Texture2D ready for EncodeToPNG. Handles both decoder output paths:
        /// - Direct RGBA/BGRA: the decoder's texture is already a plain,
        ///   readable Texture2D, so it's returned as-is.
        /// - YUV NV12 (the path PopH264 actually takes on Quest hardware):
        ///   decoder.VideoTexture is never populated in this case, so this
        ///   blits _YTex/_UVTex through the same yuvMaterial/shader used for
        ///   on-screen display into a temporary RenderTexture, then reads
        ///   that back into a Texture2D. Costs one GPU blit + one readback,
        ///   only when a photo is actually taken - not every frame.
        /// Returns null if no frame has been decoded yet or the required
        /// material is missing.
        /// </summary>
        /// <summary>
        /// Recupere les pixels bruts de l'image courante, prets a etre encodes en PNG
        /// SUR UN AUTRE THREAD (voir TelloGamepadController.CapturePhotoToDisk).
        ///
        /// Pourquoi ne pas simplement rendre la Texture2D : tout ce qui touche a un
        /// objet Texture2D (ReadPixels, Apply, EncodeToPNG) doit rester sur le main
        /// thread. En sortant les octets ici, l'encodage PNG - la partie lente,
        /// 50-150 ms - peut partir sur un thread de fond via
        /// ImageConversion.EncodeArrayToPNG, qui travaille sur un tableau et pas sur
        /// une texture.
        ///
        /// Cette methode gere aussi la propriete des textures, ce que l'ancienne
        /// CaptureSnapshot() laissait a l'appelant sans le dire : dans le chemin YUV
        /// elle CREE une Texture2D (que personne ne detruisait - une fuite par photo),
        /// alors que dans le chemin RGBA elle renvoyait la texture du DECODEUR, qu'il
        /// ne faut surtout pas detruire. Les deux cas sont traites ici, a l'interieur.
        /// </summary>
        public bool TryCaptureSnapshotPixels(out byte[] pixels, out int width, out int height, out GraphicsFormat format)
        {
            pixels = null; width = 0; height = 0; format = GraphicsFormat.None;
            if (decoder == null) return false;

            // Chemin RGBA : la texture appartient au decodeur, on se contente de lire.
            if (!decoder.IsYuvNv12)
            {
                Texture2D source = decoder.VideoTexture;
                if (source == null) return false;
                try
                {
                    pixels = source.GetRawTextureData();
                    width = source.width;
                    height = source.height;
                    format = source.graphicsFormat;
                    return pixels != null && pixels.Length > 0;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[TelloVideoDisplay] Could not read decoder texture for snapshot: {e.Message}");
                    return false;
                }
            }

            if (yuvInstance == null || decoder.YPlane == null || decoder.UVPlane == null) return false;

            // Dimensions REELLES (SPS), pas celles de la texture : celle-ci peut
            // inclure le padding d'alignement MediaCodec, qui se retrouverait dans le
            // PNG sous forme de bandes vertes.
            width = decoder.VideoWidth > 0 ? decoder.VideoWidth : decoder.YPlane.width;
            height = decoder.VideoHeight > 0 ? decoder.VideoHeight : decoder.YPlane.height;

            // sRGB explicite : le shader sort du lineaire, donc sans ce flag le PNG
            // serait beaucoup plus sombre que ce qu'on voit a l'ecran.
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previousActive = RenderTexture.active;
            Texture2D scratch = null;
            try
            {
                Graphics.Blit(null, rt, yuvInstance);
                RenderTexture.active = rt;

                scratch = new Texture2D(width, height, TextureFormat.RGB24, false);
                scratch.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                scratch.Apply();

                pixels = scratch.GetRawTextureData();
                format = scratch.graphicsFormat;
                return pixels != null && pixels.Length > 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TelloVideoDisplay] Snapshot failed: {e.Message}");
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
                // La texture de travail est detruite ICI, systematiquement. C'est la
                // fuite que l'ancienne API laissait a l'appelant, qui ne la faisait pas.
                if (scratch != null) Destroy(scratch);
            }
        }

        /// <summary>Ancienne API, conservee pour compatibilite. ATTENTION : dans le
        /// chemin YUV elle renvoie une Texture2D neuve dont l'appelant devient
        /// responsable (Destroy obligatoire), alors que dans le chemin RGBA elle
        /// renvoie la texture du decodeur, qu'il ne faut PAS detruire. Cette asymetrie
        /// est precisement pourquoi TryCaptureSnapshotPixels ci-dessus existe -
        /// preferer celle-la.</summary>
        public Texture2D CaptureSnapshot()
        {
            if (decoder == null) return null;

            if (!decoder.IsYuvNv12)
                return decoder.VideoTexture;

            if (yuvInstance == null || decoder.YPlane == null || decoder.UVPlane == null) return null;

            // Dimensions REELLES (SPS), pas celles de la texture : celle-ci peut
            // inclure le padding d'alignement MediaCodec, qui se retrouverait dans
            // le PNG sous forme de bandes vertes.
            int width = decoder.VideoWidth > 0 ? decoder.VideoWidth : decoder.YPlane.width;
            int height = decoder.VideoHeight > 0 ? decoder.VideoHeight : decoder.YPlane.height;

            // sRGB explicite : le shader sort desormais du lineaire, donc sans ce
            // flag le PNG serait beaucoup plus sombre que ce qu'on voit a l'ecran.
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(null, rt, yuvInstance);
                RenderTexture.active = rt;

                var snapshot = new Texture2D(width, height, TextureFormat.RGB24, false);
                snapshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                snapshot.Apply();
                return snapshot;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private void BuildQuad()
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "TelloVideoQuad";
            Destroy(quad.GetComponent<Collider>()); // no physics needed on a video screen

            quadTransform = quad.transform;
            quadTransform.SetParent(transform, false);
            quadTransform.localPosition = Vector3.zero;
            quadTransform.localRotation = Quaternion.identity; // orientation is fixed via UV, not geometry - see ApplyOrientation()
            // Scale is applied by ApplyZoomScale() right after this call returns (Awake) -
            // not set here, so there's a single source of truth for quad size.

            screenRenderer = quad.GetComponent<MeshRenderer>();
        }

        /// <summary>8x8 black/white checker - lets you confirm the quad isn't stretched or mis-scaled at a glance.</summary>
        private static Texture2D GenerateCheckerTexture()
        {
            const int size = 256, cells = 8;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false) { filterMode = FilterMode.Point };
            int cellSize = size / cells;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool white = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                    tex.SetPixel(x, y, white ? Color.white : Color.black);
                }
            }
            tex.Apply();
            return tex;
        }

        [Header("=== FIXED PLACEMENT ===")]
        [Tooltip("Fixed height above the real floor (requires Tracking Origin Type = Floor Level in OVR Manager). Used instead of the camera's Y position for stability.")]
        [SerializeField] private float assumedEyeHeightMeters = 1.6f;
        [Tooltip("If true, this component does NOT position itself in Start() - an external controller (e.g. TelloInitGate) calls RevealAt() instead, once it's ready to hand off. Prevents the self-positioning logic from running (and overwriting) before/after the external reveal.")]
        [SerializeField] private bool positionedExternally = false;

        private void Start()
        {
            if (positionedExternally) return; // an external controller positions this instead - see RevealAt()
            if (vrCamera == null) return;

            transform.position = TelloUiKit.ComputeFixedPosition(vrCamera, distanceFromCamera, assumedEyeHeightMeters, verticalOffset);
            transform.rotation = TelloUiKit.ComputeFixedRotation(vrCamera);
        }

        /// <summary>
        /// Called by an external controller (TelloInitGate) once it's ready to hand
        /// off. Computes its OWN position from its own distanceFromCamera/
        /// verticalOffset/assumedEyeHeightMeters (all settings-adjustable) rather
        /// than trusting the position the caller passes in - a previous version
        /// just snapped to whatever transform TelloInitGate happened to be at,
        /// which meant the Settings screen's "screen distance" slider silently had
        /// no effect (it was changing a field nothing ever read). rotation is still
        /// taken from the caller as a fallback for the rare case vrCamera isn't
        /// assigned here. Plays a short scale-in "pop" instead of a material alpha
        /// fade (keeps things simple/robust, no shader changes needed).
        /// </summary>
        public void RevealAt(Vector3 fallbackPosition, Quaternion fallbackRotation)
        {
            if (vrCamera != null)
            {
                transform.position = TelloUiKit.ComputeFixedPosition(vrCamera, distanceFromCamera, assumedEyeHeightMeters, verticalOffset);
                transform.rotation = TelloUiKit.ComputeFixedRotation(vrCamera);
            }
            else
            {
                transform.position = fallbackPosition;
                transform.rotation = fallbackRotation;
            }
            StopAllCoroutines();
            StartCoroutine(PopIn());
        }

        private System.Collections.IEnumerator PopIn()
        {
            Vector3 targetScale = quadTransform.localScale; // already the correct zoomed size, set in Awake()
            float duration = 0.35f;
            float elapsed = 0f;
            quadTransform.localScale = Vector3.zero;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
                quadTransform.localScale = targetScale * t;
                yield return null;
            }
            quadTransform.localScale = targetScale;
        }
    }
}
