Shader "Custom/UiBlurPanelBlit"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _PanelAtlasUv ("Panel Atlas UV", Vector) = (0, 0, 1, 1)
        _AtlasTexelSize ("Atlas Texel Size", Vector) = (0.0008, 0.0014, 1280, 720)
        
        _Border ("Border", Vector) = (10,10,10,10)
        _SpriteSize ("Sprite Size", Vector) = (100,100,0,0)
        _PanelSize ("Panel Size", Vector) = (100,100,0,0)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

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
            
            float4 _Border;
            float2 _SpriteSize;
            float2 _PanelSize;

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

            float RemapSlice(float value, float srcMin, float srcMax, float dstMin, float dstMax)
            {
                return dstMin + (value - srcMin) * (dstMax - dstMin) / (srcMax - srcMin);
            }

            float ComputeSliceAxis(
            float uv,
            float borderStart,
            float borderEnd,
            float spriteSize,
            float panelSize)
            {
                float leftPanel = borderStart / panelSize;
                float rightPanel = 1.0 - borderEnd / panelSize;

                float leftUV = borderStart / spriteSize;
                float rightUV = 1.0 - borderEnd / spriteSize;

                if (uv < leftPanel)
                return RemapSlice(uv, 0.0, leftPanel, 0.0, leftUV);

                if (uv > rightPanel)
                return RemapSlice(uv, rightPanel, 1.0, rightUV, 1.0);

                return RemapSlice(uv, leftPanel, rightPanel, leftUV, rightUV);
            }

            float2 ComputeSlicedUV(float2 uv)
            {
                return float2(
                ComputeSliceAxis(
                uv.x,
                _Border.x,
                _Border.z,
                _SpriteSize.x,
                _PanelSize.x),

                ComputeSliceAxis(
                uv.y,
                _Border.y,
                _Border.w,
                _SpriteSize.y,
                _PanelSize.y)
                );
            }            

            half4 FragmentPanelBlit(Varyings input) : SV_Target
            {
                float2 halfTexel = _AtlasTexelSize.xy * 0.5;
                float2 atlasUvMin = _PanelAtlasUv.xy + halfTexel;
                float2 atlasUvMax = _PanelAtlasUv.xy + _PanelAtlasUv.zw - halfTexel;
                float2 atlasUv = lerp(atlasUvMin, atlasUvMax, input.uv);
                half4 blurColor = SAMPLE_TEXTURE2D(_GlobalUiBlurTexture, sampler_GlobalUiBlurTexture, atlasUv);

                float2 maskUv = ComputeSlicedUV(input.uv);
                #if UNITY_UV_STARTS_AT_TOP
                    maskUv.y = 1.0 - maskUv.y;
                #endif

                half4 maskColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, maskUv) * _Color;

                float3 color = lerp(blurColor.rgb, half3(1,1,1), maskColor.r);

                return half4(color, maskColor.a);
            }
            ENDHLSL
        }
    }
}
