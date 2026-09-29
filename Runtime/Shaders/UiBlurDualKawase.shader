Shader "Hidden/RottenEagle/UiBlurDualKawase"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        // xy: source texel size, zw: unused
        float4 _BlurSourceTexelSize;
        // xy: min uv, zw: max uv of the valid source region (scissor + half texel)
        float4 _BlurSourceClamp;
        // x: offset in source texels
        float4 _BlurParams;

        half3 SampleSource(float2 uv)
        {
            uv = clamp(uv, _BlurSourceClamp.xy, _BlurSourceClamp.zw);
            return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
        }

        // Dual Kawase downsample: center weighted 4, four diagonal taps weighted 1.
        half3 Downsample(float2 uv)
        {
            float2 o = _BlurSourceTexelSize.xy * (_BlurParams.x + 0.5);

            half3 sum = SampleSource(uv) * 4.0;
            sum += SampleSource(uv - o);
            sum += SampleSource(uv + o);
            sum += SampleSource(uv + float2(o.x, -o.y));
            sum += SampleSource(uv - float2(o.x, -o.y));
            return sum * (1.0 / 8.0);
        }

        // Dual Kawase upsample: four axis taps weighted 1, four diagonal taps weighted 2.
        half3 Upsample(float2 uv)
        {
            float2 o = _BlurSourceTexelSize.xy * (_BlurParams.x + 0.5);

            half3 sum = SampleSource(uv + float2(-o.x * 2.0, 0.0));
            sum += SampleSource(uv + float2(o.x * 2.0, 0.0));
            sum += SampleSource(uv + float2(0.0, -o.y * 2.0));
            sum += SampleSource(uv + float2(0.0, o.y * 2.0));
            sum += SampleSource(uv + float2(-o.x, o.y)) * 2.0;
            sum += SampleSource(uv + float2(o.x, o.y)) * 2.0;
            sum += SampleSource(uv + float2(o.x, -o.y)) * 2.0;
            sum += SampleSource(uv + float2(-o.x, -o.y)) * 2.0;
            return sum * (1.0 / 12.0);
        }

        half4 FragDown(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return half4(Downsample(input.texcoord), 1.0);
        }

        half4 FragUp(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return half4(Upsample(input.texcoord), 1.0);
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

        Pass
        {
            Name "UiBlurUp"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragUp
            ENDHLSL
        }
    }
}
