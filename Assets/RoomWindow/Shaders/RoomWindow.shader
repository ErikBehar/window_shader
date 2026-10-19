// Flat glass. The fragment ray-marches captured depth so the room parallax.
// Each photo is the lit room. Alpha of Lighting A is eye depth, and Light Blend fades to Lighting B.
Shader "RoomWindow/Interior"
{
    Properties
    {
        [NoScaleOffset][MainTexture] _BaseMap ("Lighting A", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        [NoScaleOffset] _LightMapB ("Lighting B", 2D) = "white" {}
        [NoScaleOffset] _BackMap ("Background Lighting A", 2D) = "white" {}
        [NoScaleOffset] _BackLightMapB ("Background Lighting B", 2D) = "white" {}
        [HideInInspector] _HasBackground ("Has Background", Float) = 0

        [HideInInspector] _DepthRange ("Near, Far, Window Z, Ortho", Vector) = (0.3, 10, 0.3, 0)
        [HideInInspector] _Frustum ("P00, P11, P02, P12", Vector) = (1, 1, 0, 0)
        [HideInInspector] _OrthoTerms ("P03, P13", Vector) = (0, 0, 0, 0)

        _EdgeThreshold ("Silhouette Threshold (m)", Range(0, 2)) = 0.25
        _EdgeCutout ("Edge Cutout (texels)", Range(0, 16)) = 3
        _BackgroundCutout ("Background Cutout (texels)", Range(0, 128)) = 24
        _BackBias ("Background Push (m)", Range(0, 0.5)) = 0.02
        [Toggle] _FlipY ("Flip Capture Y", Float) = 0
        [Enum(Color, 0, Depth, 1, Layers, 3, Lighting A, 4, Lighting B, 5)] _DebugView ("Debug View", Float) = 0
        _LightBlend ("Light Blend", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ZTest LEqual
            Cull Off
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #define ROOM_DEPTH_ONLY 1
            #include "RoomWindowPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "RoomWindowPass.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
