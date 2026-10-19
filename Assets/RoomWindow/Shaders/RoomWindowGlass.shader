Shader "RoomWindow/Glass"
{
    Properties
    {
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

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Opacity;
                float _ReflectionStrength;
                float _FresnelPower;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionWS = positionWS;
                float near = max(_ProjectionParams.y + 0.001, _ProjectionParams.y * 1.01);
                float3 cameraForward = mul((float3x3)UNITY_MATRIX_I_V, float3(0.0, 0.0, -1.0));
                float3 offset = positionWS - _WorldSpaceCameraPos.xyz;
                float inFront = dot(offset, cameraForward);
                if (inFront < near)
                {
                    if (inFront > 0.05)
                        positionWS = _WorldSpaceCameraPos.xyz + offset * (near / inFront);
                    else
                        positionWS = _WorldSpaceCameraPos.xyz + cameraForward * near + (offset - cameraForward * inFront);
                }
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(TransformObjectToWorldNormal(float3(0, 0, -1)));
                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                float fresnel = pow(1.0 - saturate(dot(normalWS, viewWS)), _FresnelPower);
                float3 reflectWS = reflect(-viewWS, normalWS);
                float4 encoded = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, reflectWS, 0);
                float3 probe = DecodeHDREnvironment(encoded, unity_SpecCube0_HDR);

                float3 rgb = _Tint.rgb * fresnel + probe * fresnel * _ReflectionStrength;
                float alpha = saturate(_Opacity + fresnel * _ReflectionStrength);
                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
