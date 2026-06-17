Shader "Custom/UiBlurPanelBlit"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _PanelAtlasUv ("Panel Atlas UV", Vector) = (0, 0, 1, 1)
        _AtlasTexelSize ("Atlas Texel Size", Vector) = (0.0008, 0.0014, 1280, 720)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "UiBlurPanelBlit"

            HLSLPROGRAM
            #pragma vertex VertPanelBlit
            #pragma fragment FragmentPanelBlit

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_GlobalUiBlurTexture);
            SAMPLER(sampler_GlobalUiBlurTexture);
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float4 _PanelAtlasUv;
            float4 _AtlasTexelSize;
            float4 _Color;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            float2 GetBlitUv(uint vertexID)
            {
                #if UNITY_UV_STARTS_AT_TOP
                return float2((vertexID << 1) & 2, 1.0 - (vertexID & 2));
                #else
                return float2((vertexID << 1) & 2, vertexID & 2);
                #endif
            }

            Varyings VertPanelBlit(Attributes input)
            {
                Varyings output;
                float2 uv = float2((input.vertexID << 1) & 2, input.vertexID & 2);
                output.positionCS = float4(uv * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
                output.uv = GetBlitUv(input.vertexID);
                return output;
            }

            half4 FragmentPanelBlit(Varyings input) : SV_Target
            {
                float2 halfTexel = _AtlasTexelSize.xy * 0.5;
                float2 atlasUvMin = _PanelAtlasUv.xy + halfTexel;
                float2 atlasUvMax = _PanelAtlasUv.xy + _PanelAtlasUv.zw - halfTexel;
                float2 atlasUv = lerp(atlasUvMin, atlasUvMax, input.uv);
                half4 blurColor = SAMPLE_TEXTURE2D(_GlobalUiBlurTexture, sampler_GlobalUiBlurTexture, atlasUv);

                float2 maskUv = input.uv;
                #if UNITY_UV_STARTS_AT_TOP
                maskUv.y = 1.0 - maskUv.y;
                #endif

                half4 maskColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, maskUv) * _Color;

                return half4(blurColor.rgb * maskColor.rgb, maskColor.a);
            }
            ENDHLSL
        }
    }
}
