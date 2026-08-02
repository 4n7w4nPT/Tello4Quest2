Shader "TelloQuest/YuvNV12ToRGB"
{
    // NV12 (plan Y greyscale + plan UV entrelace) -> RGB, pour le chemin que
    // PopH264 emprunte reellement sur Quest (voir TelloVideoDecoder).
    //
    // ------------------------------------------------------------------
    // CE QUI A CHANGE PAR RAPPORT A LA VERSION PRECEDENTE - a lire avant
    // de toucher a quoi que ce soit ici :
    //
    // 1. EXPANSION DU CHROMA. L'ancienne version etendait le Y du limited
    //    range (16-235) mais laissait le chroma brut, tout en utilisant les
    //    coefficients FULL range (1.402 / 0.344136 / 0.714136 / 1.772).
    //    Les deux conventions etaient melangees : resultat, toute la
    //    saturation etait sous-evaluee d'un facteur 255/224 = 1.1384, soit
    //    ~12% de couleur en moins. C'est ce qui donnait le rendu "fade".
    //    Corrige ci-dessous : le chroma est etendu explicitement, et les
    //    coefficients full-range redeviennent alors corrects.
    //
    // 2. GAMMA. Le projet est en Color Space = Linear. Le RGB issu d'une
    //    conversion YUV est encode en gamma (courbe BT.601/709). Le rendre
    //    tel quel faisait ré-encoder une seconde fois en sRGB par le
    //    hardware -> noirs remontes, image laiteuse. On convertit donc
    //    explicitement en lineaire avant de sortir.
    //
    // 3. STEREO. L'ancienne version n'avait aucun boilerplate d'instancing
    //    stereo. En Single Pass Instanced (le defaut d'OpenXR), les deux
    //    yeux recevaient la projection de l'oeil gauche. Corrige.
    //
    // 4. CROP. MediaCodec aligne ses plans (stride / sliceHeight), donc la
    //    texture peut etre plus grande que l'image utile. _CropScale est
    //    calcule par TelloVideoDecoder a partir de la resolution reelle
    //    lue dans le SPS, et pilote par TelloVideoDisplay.
    //
    // 5. UPSCALE BICUBIQUE (Catmull-Rom) optionnel, sur le luma. Un
    //    unsharp mask applique par-dessus une interpolation bilineaire
    //    amplifie les artefacts de la bilineaire ; l'ordre correct est
    //    bicubique PUIS sharpen.
    //
    // 6. MOTS-CLES. Les 4 taps voisins du sharpen / night mode ne sont plus
    //    payes quand ces effets sont a zero (ils l'etaient en permanence,
    //    y compris avec les valeurs par defaut).
    // ------------------------------------------------------------------

    Properties
    {
        _YTex  ("Y Plane (luma)",   2D) = "black" {}
        _UVTex ("UV Plane (chroma)", 2D) = "grey" {}

        // x,y = fraction visible de la texture (image utile / taille texture).
        // (1,1) = aucun padding. Ecrit par TelloVideoDisplay.
        _CropScale ("Crop scale (xy)", Vector) = (1,1,0,0)

        [Toggle] _SwapUV ("Swap U/V channels", Float) = 0
        [Toggle] _FlipU  ("Flip horizontally", Float) = 0
        [Toggle] _FlipV  ("Flip vertically",   Float) = 1

        _Opacity ("Opacity", Range(0,1)) = 1

        [Header(Colour conversion)]
        [Toggle(_BT709_ON)]     _BT709     ("Use BT.709 (sinon BT.601)", Float) = 0
        [Toggle(_FULLRANGE_ON)] _FullRange ("Source en full range (sinon limited)", Float) = 0
        [Toggle(_PLANES_SRGB_ON)] _PlanesSRGB ("Plans echantillonnes en sRGB (voir doc)", Float) = 0

        [Header(Upscale)]
        [Toggle(_BICUBIC_ON)] _Bicubic ("Upscale bicubique (Catmull-Rom)", Float) = 1

        [Header(Chroma)]
        // Siting chroma 4:2:0 "left" (convention MPEG-2 / H.264 par defaut) : le
        // centre d'un texel chroma se trouve un demi-texel luma a droite de la
        // position qu'il represente. Sans correction, on obtient un franges de
        // couleur d'un demi-pixel sur les contours verticaux contrastes.
        _ChromaSiteOffset ("Chroma site offset (en texels luma)", Range(-1, 1)) = 0.5

        [Header(Enhancement)]
        [Toggle(_ENHANCE_ON)] _Enhance ("Activer lissage sharpen et night mode", Float) = 0
        _SmoothStrength ("Lissage a preservation de contours", Range(0, 1)) = 0
        _SmoothEdgeThreshold ("Seuil de contour du lissage (bas = preserve plus)", Range(0.01, 0.5)) = 0.08
        _SharpenStrength ("Sharpen Strength", Range(0, 1.5)) = 0
        _NightModeThreshold ("Night Mode Threshold", Range(0.5, 4)) = 2.0
        _NightModeStrength ("Night Mode Max Brightness Lift", Range(0, 1)) = 0
        _NightModeBlurStrength ("Night Mode Max Blur Blend", Range(0, 1)) = 0

        [Header(Manual grade)]
        _WhiteBalanceShift ("White Balance Shift", Range(-1, 1)) = 0
        _Brightness ("Brightness", Range(-1, 1)) = 0
        _Contrast ("Contrast", Range(0.5, 2)) = 1

        // Pilotes depuis TelloVideoDisplay pour basculer opaque <-> transparent
        // sans changer de materiau. A opacite 1 on repasse en opaque : sur le
        // GPU tuile du Quest, le blending permanent coute de la bande passante
        // pour rien et interdit l'early-Z.
        [HideInInspector] _SrcBlend ("", Float) = 1
        [HideInInspector] _DstBlend ("", Float) = 0
        [HideInInspector] _ZWrite   ("", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            // Indispensable sur Quest : sans ca, en Single Pass Instanced les
            // deux yeux rendent la matrice de l'oeil gauche.
            #pragma multi_compile_instancing

            #pragma multi_compile_local _ _BT709_ON
            #pragma multi_compile_local _ _FULLRANGE_ON
            #pragma multi_compile_local _ _PLANES_SRGB_ON
            #pragma multi_compile_local _ _BICUBIC_ON
            #pragma multi_compile_local _ _ENHANCE_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_YTex);   SAMPLER(sampler_YTex);
            TEXTURE2D(_UVTex);  SAMPLER(sampler_UVTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _YTex_TexelSize;   // (1/w, 1/h, w, h)
                float4 _CropScale;
                float  _SwapUV;
                float  _FlipU;
                float  _FlipV;
                float  _Opacity;
                float  _ChromaSiteOffset;
                float  _SmoothStrength;
                float  _SmoothEdgeThreshold;
                float  _SharpenStrength;
                float  _NightModeThreshold;
                float  _NightModeStrength;
                float  _NightModeBlurStrength;
                float  _WhiteBalanceShift;
                float  _Brightness;
                float  _Contrast;
                float  _SrcBlend;
                float  _DstBlend;
                float  _ZWrite;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);

                float2 uv = IN.uv;
                if (_FlipU > 0.5) uv.x = 1.0 - uv.x;
                if (_FlipV > 0.5) uv.y = 1.0 - uv.y;

                // Le crop se fait ici, en vertex : les plans sont eventuellement
                // plus grands que l'image utile (alignement MediaCodec), donc on
                // ne parcourt que la fraction reellement decodee.
                OUT.uv = uv * _CropScale.xy;
                return OUT;
            }

            // ---------------------------------------------------------------
            // Echantillonnage du luma
            // ---------------------------------------------------------------
            float SampleY(float2 uv)
            {
                float y = SAMPLE_TEXTURE2D(_YTex, sampler_YTex, uv).r;
                #ifdef _PLANES_SRGB_ON
                    // Unity a applique une conversion sRGB->lineaire au sampling
                    // parce que la Texture2D n'a pas ete creee en "linear".
                    // On l'annule : Y n'est pas une couleur sRGB.
                    y = LinearToSRGB(y.xxx).r;
                #endif
                return y;
            }

            // Catmull-Rom en 4 taps bilineaires par axe (16 taps logiques -> 9
            // acces texture reels ne sont pas necessaires ici : on reste sur la
            // version separable classique, largement assez rapide a cette
            // resolution sur Adreno).
            float4 CatmullRomWeights(float t)
            {
                float t2 = t * t;
                float t3 = t2 * t;
                return 0.5 * float4(
                    -t3 + 2.0 * t2 - t,
                     3.0 * t3 - 5.0 * t2 + 2.0,
                    -3.0 * t3 + 4.0 * t2 + t,
                     t3 - t2);
            }

            float SampleYBicubic(float2 uv)
            {
                float2 texSize  = _YTex_TexelSize.zw;
                float2 texel    = _YTex_TexelSize.xy;
                float2 coord    = uv * texSize - 0.5;
                float2 fxy      = frac(coord);
                float2 base     = (coord - fxy + 0.5) * texel;

                float4 wx = CatmullRomWeights(fxy.x);
                float4 wy = CatmullRomWeights(fxy.y);

                float result = 0.0;
                [unroll]
                for (int j = 0; j < 4; j++)
                {
                    float rowY = base.y + (float(j) - 1.0) * texel.y;
                    float row = 0.0;
                    [unroll]
                    for (int i = 0; i < 4; i++)
                    {
                        float2 s = float2(base.x + (float(i) - 1.0) * texel.x, rowY);
                        row += SampleY(s) * wx[i];
                    }
                    result += row * wy[j];
                }
                return result;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                float2 uv = IN.uv;

                #ifdef _BICUBIC_ON
                    float yC = SampleYBicubic(uv);
                #else
                    float yC = SampleY(uv);
                #endif

                float finalY = yC;

                #ifdef _ENHANCE_ON
                {
                    // Les memes 4 taps voisins servent au lissage, au sharpen ET au
                    // night mode : aucun acces texture supplementaire pour le lissage.
                    float2 texel = _YTex_TexelSize.xy;
                    float yL = SampleY(uv - float2(texel.x, 0));
                    float yR = SampleY(uv + float2(texel.x, 0));
                    float yU = SampleY(uv - float2(0, texel.y));
                    float yD = SampleY(uv + float2(0, texel.y));

                    // --- 1. Lissage a preservation de contours ---
                    // Chaque voisin est pondere par sa PROXIMITE en luminance au
                    // centre. Dans une zone plate (bruit de compression, blocking),
                    // les 4 voisins comptent plein pot et la zone se lisse ; sur un
                    // contour, le voisin de l'autre cote du contour est presque
                    // annule, donc le contour reste net. C'est ce qui permet de
                    // gagner en douceur SANS perdre d'information, contrairement a
                    // un flou uniforme.
                    float k = rcp(max(_SmoothEdgeThreshold, 1e-4));
                    k = k * k;
                    float wL = rcp(1.0 + k * (yC - yL) * (yC - yL));
                    float wR = rcp(1.0 + k * (yC - yR) * (yC - yR));
                    float wU = rcp(1.0 + k * (yC - yU) * (yC - yU));
                    float wD = rcp(1.0 + k * (yC - yD) * (yC - yD));
                    float wSum = 1.0 + wL + wR + wU + wD;
                    float ySmooth = (yC + yL * wL + yR * wR + yU * wU + yD * wD) / wSum;

                    float yBase = lerp(yC, ySmooth, _SmoothStrength);

                    // --- 2. Sharpen (unsharp mask), APRES le lissage ---
                    // L'ordre compte : debruiter puis reaccentuer redonne du piquant
                    // a la structure reelle, alors que l'inverse accentuerait d'abord
                    // le bruit avant d'essayer de l'effacer.
                    float yBlurred = (yC * 4.0 + yL + yR + yU + yD) / 8.0;
                    float sharpenedY = yBase + (yBase - yBlurred) * _SharpenStrength;

                    // --- 3. Night mode ---
                    float nightBoost = saturate(1.0 - yC * _NightModeThreshold);
                    nightBoost *= nightBoost;
                    finalY = lerp(sharpenedY, yBlurred, nightBoost * _NightModeBlurStrength);
                    finalY = saturate(finalY + nightBoost * _NightModeStrength * (1.0 - finalY));
                }
                #endif

                // Decalage de siting chroma (voir _ChromaSiteOffset) : un demi-texel
                // luma vers la gauche remet le chroma en face du luma qu'il decrit.
                float2 uvChroma = float2(uv.x - _ChromaSiteOffset * _YTex_TexelSize.x, uv.y);
                float2 uvSample = SAMPLE_TEXTURE2D(_UVTex, sampler_UVTex, uvChroma).rg;
                #ifdef _PLANES_SRGB_ON
                    uvSample = LinearToSRGB(float3(uvSample, 0)).rg;
                #endif

                float u = (_SwapUV > 0.5) ? uvSample.g : uvSample.r;
                float v = (_SwapUV > 0.5) ? uvSample.r : uvSample.g;

                // --- Expansion de plage ---
                // Limited range : Y sur 16-235, UV sur 16-240. Les deux doivent
                // etre etendus, pas seulement Y (c'etait LE bug de couleur).
                float yy;
                #ifdef _FULLRANGE_ON
                    yy = finalY;
                    u -= 0.5;
                    v -= 0.5;
                #else
                    yy = (finalY - 16.0 / 255.0) * (255.0 / 219.0);
                    u  = (u - 0.5) * (255.0 / 224.0);
                    v  = (v - 0.5) * (255.0 / 224.0);
                #endif

                // --- Matrice ---
                float r, g, b;
                #ifdef _BT709_ON
                    r = yy + 1.5748 * v;
                    g = yy - 0.187324 * u - 0.468124 * v;
                    b = yy + 1.8556 * u;
                #else
                    r = yy + 1.402 * v;
                    g = yy - 0.344136 * u - 0.714136 * v;
                    b = yy + 1.772 * u;
                #endif

                r = saturate(r + _WhiteBalanceShift * 0.15);
                b = saturate(b - _WhiteBalanceShift * 0.15);

                float3 rgb = saturate(float3(r, saturate(g), b) + _Brightness * 0.3);
                rgb = saturate((rgb - 0.5) * _Contrast + 0.5);

                // --- Gamma ---
                // rgb est encode en gamma (courbe video). Le projet est en Linear,
                // donc on convertit avant de sortir, sinon le hardware ré-encode
                // une seconde fois en sRGB (image laiteuse, noirs remontes).
                // Si un jour le projet repassait en Gamma color space, il faudrait
                // retirer cette ligne.
                rgb = SRGBToLinear(rgb);

                return half4(rgb, _Opacity);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
