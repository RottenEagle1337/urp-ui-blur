Shader "Custom/UiBlurKawase"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float _BlurOffset;
        float4 _BlurTexelSize;

        float2 GetBlurTexelSize()
        {
            float2 texelSize = _BlitTexture_TexelSize.xy;

            if (_BlurTexelSize.z > 0.5)
            {
                texelSize = _BlurTexelSize.xy;
            }

            return texelSize;
        }

        half4 SampleKawaseDual(float2 uv, float2 texelSize, float blurOffset)
        {
            half4 center = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
            half2 halfPixel = texelSize * 0.5;
            half2 spread = texelSize * blurOffset + halfPixel;

            half4 topLeft = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-spread.x, spread.y));
            half4 topRight = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + spread);
            half4 bottomRight = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(spread.x, -spread.y));
            half4 bottomLeft = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-spread.x, -spread.y));

            return (center + topLeft + topRight + bottomRight + bottomLeft) * 0.2;
        }

        half4 FragmentKawase(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;

            #ifndef UNITY_UV_STARTS_AT_TOP
            uv.y = 1.0 - uv.y;
            #endif

            float2 texelSize = GetBlurTexelSize();
            float blurOffset = max(_BlurOffset, 0.5);

            half4 color = SampleKawaseDual(uv, texelSize, blurOffset);
            color += SampleKawaseDual(uv, texelSize, blurOffset * 2.0);
            color *= 0.5;
            color.a = 1.0;

            return color;
        }
    ENDHLSL

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

        Pass
        {
            Name "KawaseBlur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragmentKawase
            ENDHLSL
        }
    }
}
