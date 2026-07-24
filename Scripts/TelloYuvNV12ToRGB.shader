Shader "TelloQuest/YuvNV12ToRGB"
{
    // Converts the 2-plane YUV output some hardware decoders (including the
    // Quest's) return - a greyscale Y (luma) plane plus an interleaved U/V
    // (chroma) plane, aka NV12 - into RGB for display. PopH264 hands back
    // this layout instead of RGBA on platforms where the OS decoder doesn't
    // do that conversion itself; see TelloVideoDecoder.cs. This is the path
    // actually used on Quest hardware, confirmed via our own decode
    // diagnostics - the enhancements below only apply here, not to the
    // RGBA fallback material (TelloVideoRGBA.mat), which uses Unity's
    // built-in Unlit shader and can't be edited the same way.
    //
    // _Opacity (added for the Settings screen's transparency slider) blends
    // the video against whatever's behind it - passthrough, in this app's
    // case - so the shader runs in the Transparent queue with standard alpha
    // blending rather than Opaque. At _Opacity = 1 this looks identical to a
    // fully opaque video screen.
    //
    // Image quality pass: automatic "night mode" and sharpening, both driven
    // per-pixel from the luma value already being sampled - no separate
    // frame-average/luminance-analysis pass (which would need a compute
    // shader and a readback) is needed for either effect:
    //   - Night mode: darker pixels get lifted toward white, self-limiting
    //     (the boost fades out as a pixel gets brighter, so well-lit footage
    //     is untouched) - approximates "the app noticed this scene is dark"
    //     without actually needing a whole-frame average.
    //   - The same brightness lift also amplifies sensor noise, which is
    //     exactly why a small drone camera looks grainy in low light in the
    //     first place - so a soft blur blends in proportionally to how much
    //     night-mode boost was applied, using the same four neighbor samples
    //     already fetched for the effect below.
    //   - Sharpening (unsharp mask): counters the softness H.264 compression
    //     already carries, most visible in daylight footage where night
    //     mode isn't doing anything. Chosen over a bicubic upscale filter -
    //     bicubic would improve the upscale interpolation curve, but doesn't
    //     address compression softness at all, which is the more noticeable
    //     issue on this specific feed; a sharpen pass is also meaningfully
    //     simpler to get right than a correct bicubic kernel, worth
    //     preferring given there's no way to visually preview shader changes
    //     before a real headset test.
    //   - Both effects only touch luma (Y) - chroma is left alone, since
    //     sharpening or blurring color information tends to cause fringing
    //     rather than a visible quality improvement.
    //
    // _WhiteBalanceShift: manual, user-controlled correction for a color cast
    // coming from the Tello's own camera/sensor (cheap CMOS sensors commonly
    // skew warm/yellow under indoor lighting) - deliberately NOT automatic.
    // An automatic "gray world" correction was considered, but doing it
    // properly needs a real frame-average estimate (impossible to get from a
    // single pixel in isolation the way the brightness/sharpen effects above
    // do), and getting it wrong risks overcorrecting a scene that's
    // genuinely warm-toned rather than actually mis-balanced - with no way
    // to visually verify the result before a real headset test, a manual
    // slider the pilot can see and adjust live is the safer choice. Defaults
    // to 0 (neutral, byte-for-byte the same output as before this existed).
    Properties
    {
        _YTex ("Y Plane (luma)", 2D) = "black" {}
        _UVTex ("UV Plane (chroma)", 2D) = "grey" {}
        [Toggle] _SwapUV ("Swap U/V channels", Float) = 0
        [Toggle] _FlipU ("Flip horizontally", Float) = 0
        [Toggle] _FlipV ("Flip vertically", Float) = 0
        _Opacity ("Opacity", Range(0,1)) = 1

        [Header(Sharpening)]
        _SharpenStrength ("Sharpen Strength", Range(0, 1.5)) = 0.4

        [Header(Automatic Night Mode)]
        _NightModeThreshold ("Night Mode Threshold (higher = kicks in on brighter footage)", Range(0.5, 4)) = 2.0
        _NightModeStrength ("Night Mode Max Brightness Lift", Range(0, 1)) = 0.35
        _NightModeBlurStrength ("Night Mode Max Blur Blend", Range(0, 1)) = 0.6

        [Header(Manual White Balance)]
        _WhiteBalanceShift ("White Balance Shift (negative = cooler/blue, positive = warmer/yellow)", Range(-1, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_YTex);
            SAMPLER(sampler_YTex);
            float4 _YTex_TexelSize; // Unity auto-provides this for any texture named _YTex: (1/width, 1/height, width, height)
            TEXTURE2D(_UVTex);
            SAMPLER(sampler_UVTex);
            float _SwapUV;
            float _FlipU;
            float _FlipV;
            float _Opacity;
            float _SharpenStrength;
            float _NightModeThreshold;
            float _NightModeStrength;
            float _NightModeBlurStrength;
            float _WhiteBalanceShift;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                float2 uv = IN.uv;
                if (_FlipU > 0.5) uv.x = 1.0 - uv.x;
                if (_FlipV > 0.5) uv.y = 1.0 - uv.y;
                OUT.uv = uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 texel = _YTex_TexelSize.xy;
                float yC = SAMPLE_TEXTURE2D(_YTex, sampler_YTex, IN.uv).r;
                float yL = SAMPLE_TEXTURE2D(_YTex, sampler_YTex, IN.uv - float2(texel.x, 0)).r;
                float yR = SAMPLE_TEXTURE2D(_YTex, sampler_YTex, IN.uv + float2(texel.x, 0)).r;
                float yU = SAMPLE_TEXTURE2D(_YTex, sampler_YTex, IN.uv - float2(0, texel.y)).r;
                float yD = SAMPLE_TEXTURE2D(_YTex, sampler_YTex, IN.uv + float2(0, texel.y)).r;
                float yBlurred = (yC * 4.0 + yL + yR + yU + yD) / 8.0; // soft, center-weighted box blur

                // Night mode boost: 0 for well-lit pixels, ramping up as luma drops.
                // Squared so the falloff is gentle near the threshold rather than a
                // hard edge - well-lit footage stays completely untouched.
                float nightBoost = saturate(1.0 - yC * _NightModeThreshold);
                nightBoost *= nightBoost;

                // Sharpen when there's little/no night-mode boost (i.e. well-lit
                // footage), fade toward the soft blur instead as the boost ramps up -
                // the same four neighbor taps serve both, just recombined differently.
                float sharpenedY = yC + (yC - yBlurred) * _SharpenStrength;
                float finalY = lerp(sharpenedY, yBlurred, nightBoost * _NightModeBlurStrength);

                // Shadow lift - self-limiting since nightBoost is already ~0 for
                // bright pixels, so this has no effect on well-lit footage.
                finalY = saturate(finalY + nightBoost * _NightModeStrength * (1.0 - finalY));

                float2 uvSample = SAMPLE_TEXTURE2D(_UVTex, sampler_UVTex, IN.uv).rg;
                float u = (_SwapUV > 0.5) ? uvSample.g : uvSample.r;
                float v = (_SwapUV > 0.5) ? uvSample.r : uvSample.g;

                // BT.601 limited-range YUV -> RGB (standard for H.264 video sources)
                float yy = (finalY - 16.0 / 255.0) * (255.0 / 219.0);
                u -= 0.5;
                v -= 0.5;

                float r = yy + 1.402 * v;
                float g = yy - 0.344136 * u - 0.714136 * v;
                float b = yy + 1.772 * u;

                // Manual white balance: shifts red up/blue down for "warmer"
                // (positive), or the reverse for "cooler" (negative) - green is
                // left alone, matching how a real color-temperature control
                // works. Small fixed range (+/-0.15 at the slider's extremes) so
                // it corrects a cast without being able to wash out the image.
                r = saturate(r + _WhiteBalanceShift * 0.15);
                b = saturate(b - _WhiteBalanceShift * 0.15);

                return half4(r, saturate(g), b, _Opacity);
            }
            ENDHLSL
        }
    }
}
