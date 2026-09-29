// UI/Default compatible shader that fills the graphic with the blurred background (UiBlurFeature pyramid).
// Shape comes from the sprite alpha, tint and opacity from the Graphic color, blur radius from _BlurStrength.
Shader "RottenEagle/UI/Blur Panel"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _BlurStrength ("Blur Strength", Range(0, 1)) = 1

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255

        _ColorMask ("Color Mask", Float) = 15

        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        // Only used by the capture pass of UiBlurFeature (depth mark). The final UI pass overrides depth state.
        ZWrite On
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            // Set globally by UiBlurFeature while UI is drawn into the blur pyramid.
            #pragma multi_compile _ _UI_BLUR_CAPTURE

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            float _BlurStrength;

            // Set by UiBlurFeature before each UI range: pyramid levels 1..7 (1/2 .. 1/128 resolution).
            sampler2D _UIBlurLevel1;
            sampler2D _UIBlurLevel2;
            sampler2D _UIBlurLevel3;
            sampler2D _UIBlurLevel4;
            sampler2D _UIBlurLevel5;
            sampler2D _UIBlurLevel6;
            sampler2D _UIBlurLevel7;
            // xy: camera target size in pixels, zw: 1 / size
            float4 _UIBlurScreenParams;
            // x: log2(target height / reference height), y: authored max levels, z: available levels
            float4 _UIBlurLodParams;

            // Cubic B-spline filtering with 4 bilinear taps: smooth magnification of low resolution levels.
            half3 SampleBicubic(sampler2D tex, float2 uv, float2 size)
            {
                float2 texel = uv * size - 0.5;
                float2 index = floor(texel);
                float2 f = texel - index;
                float2 f2 = f * f;
                float2 f3 = f2 * f;

                float2 w0 = (1.0 / 6.0) * (-f3 + 3.0 * f2 - 3.0 * f + 1.0);
                float2 w1 = (1.0 / 6.0) * (3.0 * f3 - 6.0 * f2 + 4.0);
                float2 w2 = (1.0 / 6.0) * (-3.0 * f3 + 3.0 * f2 + 3.0 * f + 1.0);
                float2 w3 = (1.0 / 6.0) * f3;

                float2 g0 = w0 + w1;
                float2 g1 = w2 + w3;
                float2 invSize = 1.0 / size;
                float2 uv0 = (index - 0.5 + w1 / g0) * invSize;
                float2 uv1 = (index + 1.5 + w3 / g1) * invSize;

                half3 c00 = tex2Dlod(tex, float4(uv0.x, uv0.y, 0, 0)).rgb;
                half3 c10 = tex2Dlod(tex, float4(uv1.x, uv0.y, 0, 0)).rgb;
                half3 c01 = tex2Dlod(tex, float4(uv0.x, uv1.y, 0, 0)).rgb;
                half3 c11 = tex2Dlod(tex, float4(uv1.x, uv1.y, 0, 0)).rgb;

                return g0.y * (g0.x * c00 + g1.x * c10) + g1.y * (g0.x * c01 + g1.x * c11);
            }

            half3 SampleLevel(int level, float2 uv)
            {
                float2 size = max(1.0, floor(_UIBlurScreenParams.xy / exp2(level)));

                [branch] switch (level)
                {
                    case 1: return SampleBicubic(_UIBlurLevel1, uv, size);
                    case 2: return SampleBicubic(_UIBlurLevel2, uv, size);
                    case 3: return SampleBicubic(_UIBlurLevel3, uv, size);
                    case 4: return SampleBicubic(_UIBlurLevel4, uv, size);
                    case 5: return SampleBicubic(_UIBlurLevel5, uv, size);
                    case 6: return SampleBicubic(_UIBlurLevel6, uv, size);
                    default: return SampleBicubic(_UIBlurLevel7, uv, size);
                }
            }

            half3 SampleBlur(float2 uv)
            {
                // Level k has a blur radius of about 2^k pixels. Strength is linear in radius at the reference
                // height (radius = 1 + strength * (2^maxLevels - 1)); the resolution term keeps the radius
                // constant in screen proportion.
                float levelCount = max(_UIBlurLodParams.z, 1.0);
                float radius = 1.0 + saturate(_BlurStrength) * (exp2(_UIBlurLodParams.y) - 1.0);
                float lod = clamp(log2(radius) + _UIBlurLodParams.x, 1.0, levelCount);

                int level = (int)floor(lod);
                float blend = lod - level;

                half3 color = SampleLevel(level, uv);
                [branch] if (blend > 0.001)
                {
                    color = lerp(color, SampleLevel(min(level + 1, (int)levelCount), uv), blend);
                }

                return color;
            }

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            half GetAlpha(v2f IN)
            {
                half alpha = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd).a * IN.color.a;

                #ifdef UNITY_UI_CLIP_RECT
                alpha *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                return alpha;
            }

            #ifdef _UI_BLUR_CAPTURE

            struct CaptureOutput
            {
                fixed4 color : SV_Target;
                float depth  : SV_Depth;
            };

            // Capture pass: no color, a nearest depth mark in the panel shape. UI drawn later fails the depth
            // test there, so the pyramid holds only what is under the panel.
            CaptureOutput frag(v2f IN)
            {
                clip(GetAlpha(IN) - 0.01);

                CaptureOutput output;
                output.color = fixed4(0, 0, 0, 0);
                #if UNITY_REVERSED_Z
                output.depth = 1.0;
                #else
                output.depth = 0.0;
                #endif
                return output;
            }

            #else

            fixed4 frag(v2f IN) : SV_Target
            {
                half alpha = GetAlpha(IN);

                #ifdef UNITY_UI_ALPHACLIP
                clip(alpha - 0.001);
                #endif

                // SV_POSITION is in render target pixels, the pyramid was produced from the same
                // render target, so no platform specific flip is required.
                float2 screenUv = IN.vertex.xy * _UIBlurScreenParams.zw;
                half3 blur = SampleBlur(screenUv);

                return fixed4(blur * IN.color.rgb, alpha);
            }

            #endif
            ENDCG
        }
    }
}
