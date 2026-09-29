Shader "Hidden/RottenEagle/UiBlurPyramid"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // xy: source texel size, zw: source size
        float4 _BlurSourceTexelSize;
        // x: tap offset in source texels (added to half a texel)
        float4 _BlurParams;

        half3 SampleSource(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
        }

        // Kawase downsample: center weighted 4, four diagonal taps weighted 1.
        half4 FragDown(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = input.texcoord;
            float2 o = _BlurSourceTexelSize.xy * (_BlurParams.x + 0.5);

            half3 sum = SampleSource(uv) * 4.0;
            sum += SampleSource(uv - o);
            sum += SampleSource(uv + o);
            sum += SampleSource(uv + float2(o.x, -o.y));
            sum += SampleSource(uv - float2(o.x, -o.y));
            return half4(sum * (1.0 / 8.0), 1.0);
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            Name "UiBlurDown"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDown
            ENDHLSL
        }
    }
}
