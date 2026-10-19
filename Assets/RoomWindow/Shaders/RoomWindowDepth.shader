Shader "Hidden/RoomWindow/Depth"
{
    // Editor-only capture. Min blending keeps the closest eye depth without using
    // the hardware depth buffer, which does not match this custom projection.
    // Pass 1 skips that closest surface and keeps the next one behind it.
    Properties
    {
        _BaseMap ("Base", 2D) = "white" {}
        _Cutoff ("Cutoff", Float) = 0.5
        _UseCutout ("Use Cutout", Float) = 0
        _RoomWindowFrontDepth ("Front Depth", 2D) = "black" {}
        _RoomWindowPeel ("Peel", Float) = 0
        _RoomWindowScreenSize ("Screen", Vector) = (1, 1, 0, 0)
        // 0 Off, 1 Front, 2 Back. Back keeps only faces that look at the camera,
        // which skips an object's far side when peeling to the surface behind it.
        _RoomWindowCull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float _Cutoff;
            float _UseCutout;
            float _RoomWindowPeel;
            float4 _RoomWindowScreenSize;
        CBUFFER_END

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_RoomWindowFrontDepth);

        float4x4 _RoomWindowWorldToView;
        float4x4 _RoomWindowViewProj;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float eyeDepth : TEXCOORD0;
            float2 uv : TEXCOORD1;
        };

        Varyings vert(Attributes input)
        {
            Varyings output;
            float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS = mul(_RoomWindowViewProj, float4(positionWS, 1.0));
            float3 positionVS = mul(_RoomWindowWorldToView, float4(positionWS, 1.0)).xyz;
            output.eyeDepth = -positionVS.z;
            output.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
            return output;
        }

        float4 frag(Varyings input) : SV_Target
        {
            if (input.eyeDepth <= 0.0)
                clip(-1);

            if (_UseCutout > 0.5)
            {
                float alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a;
                clip(alpha - _Cutoff);
            }

            if (_RoomWindowPeel > 0.5)
            {
                float2 screenUv = input.positionCS.xy / max(_RoomWindowScreenSize.xy, float2(1.0, 1.0));
                if (_RoomWindowScreenSize.z > 0.5)
                    screenUv.y = 1.0 - screenUv.y;

                float front = SAMPLE_TEXTURE2D_LOD(_RoomWindowFrontDepth, sampler_PointClamp, screenUv, 0).r;
                float gap = max(0.005, front * 0.001);
                if (input.eyeDepth <= front + gap)
                    clip(-1);
            }

            return input.eyeDepth;
        }
        ENDHLSL

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull [_RoomWindowCull]
            BlendOp Min
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
