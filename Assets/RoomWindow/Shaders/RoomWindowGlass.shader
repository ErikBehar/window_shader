Shader "RoomWindow/Glass"
{
    Properties
    {
        [Header(Foreground)]
        _Foreground ("Image", 2D) = "black" {}

        [Header(Glass)]
        _Tint ("Tint", Color) = (0.75, 0.88, 1, 1)
        _Opacity ("Opacity", Range(0, 0.5)) = 0.03
        _ReflectionStrength ("Reflection", Range(0, 1)) = 0.35
        _FresnelPower ("Fresnel Power", Range(0.5, 8)) = 4
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"
            #include "RoomWindowPlane.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Foreground_ST;
                float4 _Tint;
                float _Opacity;
                float _ReflectionStrength;
                float _FresnelPower;
            CBUFFER_END

            // Opaque texels are the wall and ledges. Clear texels leave the glass, and the room shows through it.
            TEXTURE2D(_Foreground);
            SAMPLER(sampler_Foreground);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                // Stay on the real window plane. Pushing vertices onto the near plane warps the
                // pane and its foreground as the camera moves, and it no longer matches the room.
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionWS = positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _Foreground);
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            struct FragOut
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            FragOut frag(Varyings input)
            {
                float3 normalWS = normalize(TransformObjectToWorldNormal(float3(0, 0, -1)));
                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                float fresnel = pow(1.0 - saturate(dot(normalWS, viewWS)), _FresnelPower);
                float3 reflectWS = reflect(-viewWS, normalWS);
                float4 encoded = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, reflectWS, 0);
                float3 probe = DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);

                float3 rgb = _Tint.rgb * fresnel + probe * fresnel * _ReflectionStrength;
                float alpha = saturate(_Opacity + fresnel * _ReflectionStrength);

                half4 front = SAMPLE_TEXTURE2D(_Foreground, sampler_Foreground, input.uv);
                float cover = saturate(front.a);
                float outAlpha = cover + alpha * (1.0 - cover);
                float3 outRgb = (front.rgb * cover + rgb * alpha * (1.0 - cover)) / max(outAlpha, 1e-4);

                FragOut output;
                output.color = half4(outRgb, outAlpha);
                // Same plane as the interior, one step closer, so the pane stays on top
                // without fighting the room as the camera moves.
                float planeDepth = WindowPlaneDeviceDepth(input.positionCS, input.positionCS.z);
#if UNITY_REVERSED_Z
                output.depth = min(planeDepth + 2.0e-5, 1.0);
#else
                output.depth = max(planeDepth - 2.0e-5, 0.0);
#endif
                return output;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
