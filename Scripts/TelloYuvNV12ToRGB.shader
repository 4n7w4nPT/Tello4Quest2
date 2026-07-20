Shader "TelloQuest/YuvNV12ToRGB"
{
    // Converts the 2-plane YUV output some hardware decoders (including the
    // Quest's) return - a greyscale Y (luma) plane plus an interleaved U/V
    // (chroma) plane, aka NV12 - into RGB for display. PopH264 hands back
    // this layout instead of RGBA on platforms where the OS decoder doesn't
    // do that conversion itself; see TelloVideoDecoder.cs.
    Properties
    {
        _YTex ("Y Plane (luma)", 2D) = "black" {}
        _UVTex ("UV Plane (chroma)", 2D) = "grey" {}
        [Toggle] _SwapUV ("Swap U/V channels", Float) = 0
        [Toggle] _FlipU ("Flip horizontally", Float) = 0
        [Toggle] _FlipV ("Flip vertically", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" }
        Cull Off

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
            TEXTURE2D(_UVTex);
            SAMPLER(sampler_UVTex);
            float _SwapUV;
            float _FlipU;
            float _FlipV;

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
                float y = SAMPLE_TEXTURE2D(_YTex, sampler_YTex, IN.uv).r;
                float2 uvSample = SAMPLE_TEXTURE2D(_UVTex, sampler_UVTex, IN.uv).rg;
                float u = (_SwapUV > 0.5) ? uvSample.g : uvSample.r;
                float v = (_SwapUV > 0.5) ? uvSample.r : uvSample.g;

                // BT.601 limited-range YUV -> RGB (standard for H.264 video sources)
                float yy = (y - 16.0 / 255.0) * (255.0 / 219.0);
                u -= 0.5;
                v -= 0.5;

                float r = yy + 1.402 * v;
                float g = yy - 0.344136 * u - 0.714136 * v;
                float b = yy + 1.772 * u;

                return half4(saturate(r), saturate(g), saturate(b), 1.0);
            }
            ENDHLSL
        }
    }
}
