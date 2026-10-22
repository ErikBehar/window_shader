#ifndef ROOM_WINDOW_PLANE_INCLUDED
#define ROOM_WINDOW_PLANE_INCLUDED

// Device depth of the window card (object-space z = 0) through this pixel.
// The card's vertices are pushed to the near plane when the camera clips them, which
// changes raster depth but not the plane the room and glass actually sit on.
float WindowPlaneDeviceDepth(float4 positionCS, float rasterDepth)
{
    float2 screenUV = positionCS.xy * rcp(_ScaledScreenParams.xy);
    float3 nearWS = ComputeWorldSpacePosition(screenUV, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
    float3 farWS = ComputeWorldSpacePosition(screenUV, UNITY_RAW_FAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
    float3 nearOS = TransformWorldToObject(nearWS);
    float3 farOS = TransformWorldToObject(farWS);
    float3 ray = farOS - nearOS;
    if (abs(ray.z) < 1e-6)
        return rasterDepth;

    float t = -nearOS.z / ray.z;
    if (t < -0.001 || t > 1.001)
        return rasterDepth;

    float4 clip = TransformWorldToHClip(TransformObjectToWorld(nearOS + ray * t));
    if (clip.w <= 1e-6)
        return rasterDepth;

    float depth = clip.z * rcp(clip.w);
#if !UNITY_REVERSED_Z
    depth = depth * 0.5 + 0.5;
#endif
    return saturate(depth);
}

#endif
