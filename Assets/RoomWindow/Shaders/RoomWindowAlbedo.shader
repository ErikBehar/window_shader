Shader "Hidden/RoomWindow/Albedo"
{
    // Keeps only the surface that matches the depth photo, and writes its unlit base color.
    Properties
    {
        _BaseMap ("Base", 2D) = "white" {}
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Cutoff", Float) = 0.5
        _UseCutout ("Use Cutout", Float) = 0
        _HasMap ("Has Map", Float) = 0
        _RoomWindowId ("Renderer Id", Float) = 0
        _RoomWindowOutputId ("Output Id", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Cutoff;
                float _UseCutout;
                float _HasMap;
                float _RoomWindowId;
                float _RoomWindowOutputId;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_RoomWindowEyeDepth);
            SAMPLER(sampler_RoomWindowEyeDepth);

            float4x4 _RoomWindowWorldToView;
            float4x4 _RoomWindowViewProj;
            float4 _RoomWindowScreenSize;

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

                float2 depthUv = input.positionCS.xy / max(_RoomWindowScreenSize.xy, 1.0);
                if (_RoomWindowScreenSize.z > 0.5)
                    depthUv.y = 1.0 - depthUv.y;

                float stored = SAMPLE_TEXTURE2D_LOD(_RoomWindowEyeDepth, sampler_RoomWindowEyeDepth, depthUv, 0).r;
                float tolerance = max(0.04, stored * 0.015);
                if (abs(stored - input.eyeDepth) > tolerance)
                    clip(-1);

                if (_UseCutout > 0.5)
                    clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a - _Cutoff);

                // Which renderer owns this depth. Used to sort background from foreground.
                if (_RoomWindowOutputId > 0.5)
                    return float4(_RoomWindowId, 0.0, 0.0, 1.0);

                float3 color = _BaseColor.rgb;
                if (_HasMap > 0.5)
                    color *= SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb;
                return float4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
