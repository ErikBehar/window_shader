#ifndef ROOM_WINDOW_PASS_INCLUDED
#define ROOM_WINDOW_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "RoomWindowPlane.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;
    float4 _DepthRange;
    float4 _Frustum;
    float4 _OrthoTerms;
    float _EdgeThreshold;
    float _BackBias;
    float _FlipY;
    float _DebugView;
    float _LightBlend;
    float _HasBackground;
    float _EdgeCutout;
    float _BackgroundCutout;
CBUFFER_END

TEXTURE2D(_BaseMap);
SAMPLER(sampler_BaseMap);
float4 _BaseMap_TexelSize;
TEXTURE2D(_LightMapB);
SAMPLER(sampler_LightMapB);
// Second layer: the room behind foreground objects. Same layout, depth in alpha.
TEXTURE2D(_BackMap);
TEXTURE2D(_BackLightMapB);

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv : TEXCOORD0;
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionOS : TEXCOORD0;
    float2 meshUv : TEXCOORD1;
};

float2 CaptureUv(float2 uv)
{
    if (_FlipY > 0.5)
        uv.y = 1.0 - uv.y;
    return uv;
}

float EyeFrom01(float depth01)
{
    return lerp(_DepthRange.x, _DepthRange.y, saturate(depth01));
}

// Camera space is +Z forward. _Frustum is (P00, P11, P02, P12). _OrthoTerms is (P03, P13, _, _).
// _DepthRange.w > 0.5 selects orthographic. Keep in sync with RoomWindow.CameraSpacePosition.
float3 CameraSpacePosition(float2 uv, float eye)
{
    float2 ndc = uv * 2.0 - 1.0;
    if (_DepthRange.w > 0.5)
    {
        float x = (ndc.x - _OrthoTerms.x) / _Frustum.x;
        float y = (ndc.y - _OrthoTerms.y) / _Frustum.y;
        return float3(x, y, eye);
    }

    float x = (ndc.x * eye + eye * _Frustum.z) / _Frustum.x;
    float y = (ndc.y * eye + eye * _Frustum.w) / _Frustum.y;
    return float3(x, y, eye);
}

float3 MeshPosition(float2 uv, float eye)
{
    float3 position = CameraSpacePosition(uv, eye);
    position.z -= _DepthRange.z;
    return position;
}

void CaptureFromPosition(float3 positionOS, out float eye, out float2 meshUv)
{
    eye = positionOS.z + _DepthRange.z;
    float safeEye = max(eye, 1e-4);
    float2 ndc;
    if (_DepthRange.w > 0.5)
    {
        ndc.x = positionOS.x * _Frustum.x + _OrthoTerms.x;
        ndc.y = positionOS.y * _Frustum.y + _OrthoTerms.y;
    }
    else
    {
        ndc.x = positionOS.x * _Frustum.x / safeEye - _Frustum.z;
        ndc.y = positionOS.y * _Frustum.y / safeEye - _Frustum.w;
    }

    meshUv = ndc * 0.5 + 0.5;
}

// Room color alpha is the front depth, normalized across _DepthRange. Point sampled so the
// march does not slide along a filtered ramp. The background layer stores its depth the same way.
float SampleLayerDepth01(float2 meshUv, bool back)
{
    float2 uv = CaptureUv(saturate(meshUv));
    if (back)
        return SAMPLE_TEXTURE2D_LOD(_BackMap, sampler_PointClamp, uv, 0).a;
    return SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_PointClamp, uv, 0).a;
}

float SampleDepth01(float2 meshUv)
{
    return SampleLayerDepth01(meshUv, false);
}

float4 ClipPosition(float3 positionOS)
{
    // A rotated camera's near plane slices this flat quad. Vertices that cross it are
    // placed on the near plane along the same view ray, so the window keeps its coverage.
    // The fragment still receives the real glass point, and it writes that plane's depth
    // so the push cannot bury the glass layer.
    float3 positionWS = TransformObjectToWorld(positionOS);
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

    return TransformWorldToHClip(positionWS);
}

Varyings vert(Attributes input)
{
    Varyings output;
    // The grid stays on the glass. Depth is resolved per pixel, so object edges
    // are not triangles stretched between a near texel and a far one.
    float3 positionOS = MeshPosition(input.uv, _DepthRange.z);
    output.positionCS = ClipPosition(positionOS);
    output.positionOS = positionOS;
    output.meshUv = input.uv;
    return output;
}

float WindowLimit(float windowZ)
{
    return windowZ + max(_BackBias, 0.01);
}

// The glass is this room window. A depth sample sitting on it is not room geometry.
bool IsRoomWindow(float surfaceEye, float windowLimit)
{
    return surfaceEye <= windowLimit;
}

// Front depth is the room-color alpha. A sample on the glass itself is skipped.
float SurfaceEye(float depth01, float windowLimit, out bool missing)
{
    float front = EyeFrom01(depth01);
    missing = IsRoomWindow(front, windowLimit);
    return missing ? 1e8 : front;
}

float3 RayDirectionOS(float3 glassOS)
{
    float3 glassWS = TransformObjectToWorld(glassOS);
    float3 directionWS = unity_OrthoParams.w > 0.5
        ? mul((float3x3)UNITY_MATRIX_I_V, float3(0.0, 0.0, -1.0))
        : glassWS - _WorldSpaceCameraPos.xyz;
    return TransformWorldToObjectDir(directionWS);
}

// How fast capture texels slide as eye depth increases along this view ray.
// Perspective returns texels * eye^2 (divide by eye^2 for texels per meter).
// Orthographic returns texels per meter. The two are not interchangeable.
float CaptureTexelRate(float3 glassOS, float3 directionOS, float windowZ, out bool orthographic)
{
    orthographic = _DepthRange.w > 0.5;
    float kz = max(directionOS.z, 1e-4);
    float2 texelSize = max(_BaseMap_TexelSize.zw, float2(1.0, 1.0));
    if (orthographic)
    {
        float2 dUv = float2(_Frustum.x, _Frustum.y) * directionOS.xy / kz * 0.5;
        return length(dUv * texelSize);
    }

    float shift = windowZ + glassOS.z;
    float2 k = float2(_Frustum.x, _Frustum.y) * (directionOS.xy / kz * shift - glassOS.xy) * 0.5;
    return length(k * texelSize);
}

float AdvanceEye(float eye, float farZ, float texelsPerStep, float texelRate, bool orthographic)
{
    float nextEye = farZ;
    if (orthographic)
    {
        float speed = max(texelRate, 1e-5);
        nextEye = eye + texelsPerStep / speed;
    }
    else if (texelRate > 1e-4)
    {
        float inv = 1.0 / max(eye, 1e-3) - texelsPerStep / texelRate;
        nextEye = inv > 1e-5 ? 1.0 / inv : farZ;
    }

    return min(farZ, max(nextEye, eye + 1e-4));
}

bool InsidePhoto(float2 uv)
{
    return uv.x >= 0.0 && uv.y >= 0.0 && uv.x <= 1.0 && uv.y <= 1.0;
}

void RayPoint(float3 glassOS, float3 directionOS, float windowZ, float eye, out float sampleEye, out float2 sampleUv)
{
    float distance = (eye - windowZ - glassOS.z) / max(directionOS.z, 1e-4);
    CaptureFromPosition(glassOS + directionOS * max(distance, 0.0), sampleEye, sampleUv);
}

bool ShadeLayer(float windowLimit, float2 sampleUv, float sampleEye, bool back, out float2 meshUv, out float hitEye, out bool background)
{
    meshUv = saturate(sampleUv);
    bool missing;
    float surface = SurfaceEye(SampleLayerDepth01(meshUv, back), windowLimit, missing);
    hitEye = surface > 1e7 ? sampleEye : surface;
    background = missing;
    return true;
}

bool ShadePhoto(float3 glassOS, float windowLimit, float2 sampleUv, float sampleEye, out float2 meshUv, out float hitEye, out bool background)
{
    return ShadeLayer(windowLimit, sampleUv, sampleEye, false, meshUv, hitEye, background);
}

float EyeInsidePhoto(float3 glassOS, float3 directionOS, float windowZ, float insideEye, float outsideEye)
{
    [unroll]
    for (int refine = 0; refine < 8; refine++)
    {
        float midEye = 0.5 * (insideEye + outsideEye);
        float sampleEye;
        float2 sampleUv;
        RayPoint(glassOS, directionOS, windowZ, midEye, sampleEye, sampleUv);
        if (InsidePhoto(sampleUv))
            insideEye = midEye;
        else
            outsideEye = midEye;
    }

    return insideEye;
}

// An object texel within _EdgeCutout of the room. Shading it leaves the object's
// contour on the floor and back wall. The background layer covers that ring instead.
float2 RimDirection(int index)
{
    float angle = index * 0.78539816;
    return float2(cos(angle), sin(angle));
}

bool OnOccluderRim(float2 uv, float surface, float windowLimit, float cliffMeters)
{
    float radius = max(_EdgeCutout, 1.5);
    float jump = max(0.08, min(cliffMeters, surface * 0.04));

    bool missing;
    float behind = SurfaceEye(SampleLayerDepth01(uv, true), windowLimit, missing);
    if (missing || behind <= surface + jump)
        return false;

    float2 texel = max(_BaseMap_TexelSize.xy, float2(0.0005, 0.0005));
    [unroll]
    for (int k = 0; k < 8; k++)
    {
        float2 dir = RimDirection(k);
        [unroll]
        for (int s = 1; s <= 4; s++)
        {
            float2 neighborUv = uv + dir * texel * (radius * s / 4.0);
            if (!InsidePhoto(neighborUv))
                return true;

            bool neighborMissing;
            float neighbor = SurfaceEye(SampleDepth01(neighborUv), windowLimit, neighborMissing);
            if (neighborMissing || neighbor > surface + jump)
                return true;
        }
    }

    return false;
}

// Narrow a crossing until the ray depth meets the stored depth.
// A continuous surface (the floor, a wall) is interpolated so the hit is not a
// depth slice. A cliff — the empty gap beside an object — is kept only when the
// ray is actually on that card. Shading the gap is what dragged objects and the
// back wall out into bands.
bool RefineCrossing(float3 glassOS, float3 directionOS, float windowZ, float windowLimit, float cliffMeters, bool back, bool trimRim,
    float eye0, float2 uv0, float surf0, bool miss0,
    float eye1, float2 uv1, float surf1, bool miss1,
    out float2 meshUv, out float hitEye, out bool background)
{
    meshUv = saturate(uv1);
    hitEye = eye1;
    background = false;

    [unroll]
    for (int refine = 0; refine < 8; refine++)
    {
        float midEye = 0.5 * (eye0 + eye1);
        float midSample;
        float2 midUv;
        RayPoint(glassOS, directionOS, windowZ, midEye, midSample, midUv);
        bool midMiss = !InsidePhoto(midUv);
        float midSurf = midMiss ? 1e8 : SurfaceEye(SampleLayerDepth01(midUv, back), windowLimit, midMiss);
        if (!midMiss && midSample >= midSurf)
        {
            eye1 = midSample;
            uv1 = midUv;
            surf1 = midSurf;
            miss1 = false;
        }
        else
        {
            eye0 = midSample;
            uv0 = midUv;
            surf0 = midSurf;
            miss0 = midMiss;
        }
    }

    // Both ends have to sit on the same surface, with the ray in front of the
    // first and behind the second. Otherwise a long step onto the back wall
    // would paint that wall in front of the floor.
    float jump = max(0.08, min(cliffMeters, min(surf0, surf1) * 0.04));
    bool straddles = !miss0 && !miss1
        && eye0 <= surf0 + 0.002
        && eye1 >= surf1 - 0.002
        && abs(surf0 - surf1) <= jump;
    if (straddles)
    {
        float ahead = surf0 - eye0;
        float behind = eye1 - surf1;
        float weight = saturate(ahead / max(ahead + behind, 1e-5));
        float2 refinedUv = saturate(lerp(uv0, uv1, weight));
        float refinedEye = lerp(surf0, surf1, weight);
        if (!back && trimRim && OnOccluderRim(refinedUv, refinedEye, windowLimit, cliffMeters))
            return false;
        meshUv = refinedUv;
        hitEye = refinedEye;
        return true;
    }

    // Silhouette. The leaf interval is a few millimeters on a real hit and
    // meters when the step only jumped the gap beside the object.
    float span = max(eye1 - eye0, 1e-4);
    float tolerance = max(span * 2.0, 0.01);
    if (!miss1 && abs(eye1 - surf1) <= tolerance)
    {
        if (!back && trimRim && OnOccluderRim(uv1, surf1, windowLimit, cliffMeters))
            return false;
        meshUv = saturate(uv1);
        hitEye = surf1;
        return true;
    }

    if (!miss0 && eye0 <= surf0 + tolerance && eye1 >= surf0 && abs(eye0 - surf0) <= tolerance)
    {
        if (!back && trimRim && OnOccluderRim(uv0, surf0, windowLimit, cliffMeters))
            return false;
        meshUv = saturate(uv0);
        hitEye = surf0;
        return true;
    }

    meshUv = saturate(uv1);
    hitEye = eye1;
    return false;
}

// Only replace a background sample if that texel still sits on a foreground
// object instead of on the room behind it.
float2 CutForegroundFromBackground(float2 uv, float radius, bool useBack, float windowLimit, float cliffMeters)
{
    if (!useBack || radius < 0.5)
        return uv;

    bool frontMissing;
    float front = SurfaceEye(SampleDepth01(uv), windowLimit, frontMissing);
    bool backMissing;
    float behind = SurfaceEye(SampleLayerDepth01(uv, true), windowLimit, backMissing);
    float jump = max(0.08, min(cliffMeters, front * 0.04));
    if (frontMissing || (!backMissing && behind > front + jump))
        return uv;

    float2 texel = max(_BaseMap_TexelSize.xy, float2(0.0005, 0.0005));
    float best = radius + 1.0;
    float2 bestUv = uv;
    [unroll]
    for (int k = 0; k < 8; k++)
    {
        float2 dir = RimDirection(k);
        [loop]
        for (int s = 1; s <= 8; s++)
        {
            float dist = radius * s / 8.0;
            float2 neighborUv = uv + dir * texel * dist;
            if (!InsidePhoto(neighborUv))
                continue;

            bool neighborBackMissing;
            float neighborBack = SurfaceEye(SampleLayerDepth01(neighborUv, true), windowLimit, neighborBackMissing);
            if (neighborBackMissing || neighborBack <= front + jump)
                continue;

            if (dist < best)
            {
                best = dist;
                bestUv = neighborUv;
            }
            break;
        }
    }

    return saturate(bestUv);
}

bool MarchRoom(float3 glassOS, out float2 meshUv, out float hitEye, out bool background, out bool useBack, out float2 openUv)
{
    meshUv = 0.5;
    hitEye = _DepthRange.z;
    background = false;
    useBack = false;
    bool hasBack = _HasBackground > 0.5;

    float3 directionOS = RayDirectionOS(glassOS);
    float windowZ = _DepthRange.z;
    float windowLimit = WindowLimit(windowZ);
    float farZ = max(_DepthRange.y, windowZ + 0.05);
    float glassEye;
    float2 glassUv;
    CaptureFromPosition(glassOS, glassEye, glassUv);
    openUv = saturate(glassUv);

    // Looking along the glass, or from inside the room: keep the photo instead of discarding the pixel.
    if (directionOS.z < 1e-4)
        return ShadePhoto(glassOS, windowLimit, glassUv, glassEye, meshUv, hitEye, background);

    float eyeStart = max(max(glassEye, windowZ), 1e-3);
    float cliffMeters = max(_EdgeThreshold, 0.01);
    bool orthographic;
    float texelRate = CaptureTexelRate(glassOS, directionOS, windowZ, orthographic);
    float totalTexels = orthographic
        ? texelRate * max(farZ - eyeStart, 0.0)
        : texelRate * max(1.0 / eyeStart - 1.0 / farZ, 0.0);
    const float stepBudget = 256.0;
    float texelsPerStep = max(totalTexels / stepBudget, 0.75);

    float prevEye = eyeStart;
    float2 prevUv = saturate(glassUv);
    bool prevMissing = !InsidePhoto(prevUv);
    float prevSurface = prevMissing ? 1e8 : SurfaceEye(SampleDepth01(prevUv), windowLimit, prevMissing);
    bool prevRim = hasBack && !prevMissing && OnOccluderRim(prevUv, prevSurface, windowLimit, cliffMeters);
    bool prevBehind = false;
    bool prevBackMissing = true;
    float prevBackSurface = 1e8;
    if (hasBack && !prevMissing)
        prevBackSurface = SurfaceEye(SampleLayerDepth01(prevUv, true), windowLimit, prevBackMissing);
    float lastEye = prevEye;
    float2 lastUv = prevUv;
    bool lastBehind = false;
    // Last texel whose surface was still ahead of the ray. When the ray ends up
    // in the shadow of an object, this sits on the room wall or floor right beside
    // that object.
    float openEye = prevEye;

    [loop]
    for (int i = 0; i < 256; i++)
    {
        float nextEye = AdvanceEye(prevEye, farZ, texelsPerStep, texelRate, orthographic);
        float sampleEye;
        float2 sampleUv;
        RayPoint(glassOS, directionOS, windowZ, nextEye, sampleEye, sampleUv);

        // Leaving the photo used to abort the pixel, which cut away half the window as the camera turned.
        if (!InsidePhoto(sampleUv))
        {
            float edgeEye = EyeInsidePhoto(glassOS, directionOS, windowZ, prevEye, nextEye);
            float edgeSampleEye;
            float2 edgeUv;
            RayPoint(glassOS, directionOS, windowZ, edgeEye, edgeSampleEye, edgeUv);
            edgeUv = saturate(edgeUv);
            bool edgeMissing;
            float edgeSurface = SurfaceEye(SampleDepth01(edgeUv), windowLimit, edgeMissing);
            if (!prevBehind && !edgeMissing && prevEye < edgeSurface && edgeSampleEye >= edgeSurface)
            {
                if (RefineCrossing(glassOS, directionOS, windowZ, windowLimit, cliffMeters, false, hasBack,
                    prevEye, prevUv, prevSurface, prevMissing,
                    edgeSampleEye, edgeUv, edgeSurface, false,
                    meshUv, hitEye, background))
                    return true;
            }

            bool edgeBehind = !edgeMissing && edgeSampleEye > edgeSurface + 0.002;
            if (edgeBehind && hasBack)
            {
                useBack = true;
                return ShadeLayer(windowLimit, edgeUv, edgeSampleEye, true, meshUv, hitEye, background);
            }
            if (edgeBehind)
                return ShadePhoto(glassOS, windowLimit, openUv, openEye, meshUv, hitEye, background);
            return ShadePhoto(glassOS, windowLimit, edgeUv, edgeSampleEye, meshUv, hitEye, background);
        }

        bool missing;
        float surface = SurfaceEye(SampleDepth01(sampleUv), windowLimit, missing);
        bool rim = hasBack && !missing && OnOccluderRim(sampleUv, surface, windowLimit, cliffMeters);
        bool behind = !missing && sampleEye > surface + 0.002;
        lastUv = sampleUv;
        lastEye = sampleEye;
        lastBehind = behind;

        // crossedThis: the ray meets the surface at this texel.
        // passedPrev: the step went past the surface we were approaching, even if
        // the texel at the end is already a farther card (the floor falling off
        // into the back wall). Neither is allowed to fire on a rim texel, or the
        // step off the rim onto the wall refines back onto the rim and draws a
        // detached outline of the object on the background.
        bool crossedThis = !missing && !rim && prevEye < surface && sampleEye >= surface;
        bool passedPrev = !prevMissing && !prevRim && prevEye < prevSurface && sampleEye >= prevSurface;
        bool crossedFront = crossedThis || passedPrev;
        if (crossedFront && !prevBehind)
        {
            if (RefineCrossing(glassOS, directionOS, windowZ, windowLimit, cliffMeters, false, hasBack,
                prevEye, prevUv, prevSurface, prevMissing,
                sampleEye, sampleUv, surface, missing,
                meshUv, hitEye, background))
                return true;
        }

        // In an object's shadow the front photo has nothing for this ray. The
        // background layer continues the surface behind the object. A sample at
        // the same depth as the front is the object leaking into that layer, not
        // the room, so it is ignored.
        float stepJump = max(0.08, min(cliffMeters, surface * 0.04));
        bool backMissing = true;
        float backSurface = 1e8;
        if (hasBack && !missing)
        {
            backSurface = SurfaceEye(SampleLayerDepth01(sampleUv, true), windowLimit, backMissing);
            if (!backMissing && backSurface <= surface + stepJump)
                backMissing = true;
        }
        if (hasBack && (behind || prevBehind || rim))
        {
            bool crossedBack = (!backMissing && prevEye < backSurface && sampleEye >= backSurface)
                || (!prevBackMissing && prevEye < prevBackSurface && sampleEye >= prevBackSurface);
            if (crossedBack)
            {
                if (RefineCrossing(glassOS, directionOS, windowZ, windowLimit, cliffMeters, true, false,
                    prevEye, prevUv, prevBackSurface, prevBackMissing,
                    sampleEye, sampleUv, backSurface, backMissing,
                    meshUv, hitEye, background))
                {
                    useBack = true;
                    return true;
                }
            }
        }

        // Once the ray is behind an object, the front photo is that object. Shading
        // it again is the second copy, sunk to whatever farther depth the next texel has.
        // Only record openUv on the room itself (where front and background depths agree),
        // never on a foreground object such as a cabinet under a blender.
        if (!behind && !rim && (!hasBack || backMissing))
        {
            openUv = sampleUv;
            openEye = sampleEye;
        }

        prevEye = sampleEye;
        prevUv = sampleUv;
        prevSurface = missing ? 1e8 : surface;
        prevMissing = missing;
        prevRim = rim;
        prevBehind = behind;
        prevBackSurface = backSurface;
        prevBackMissing = backMissing;
        if (nextEye >= farZ - 1e-4)
            break;
    }

    // Ended behind a card: that texel is an object the ray slipped past, not the
    // wall behind it. Drawing it here is what made a second copy on the back wall.
    if (lastBehind && hasBack)
    {
        bool endMissing;
        float endBack = SurfaceEye(SampleLayerDepth01(lastUv, true), windowLimit, endMissing);
        bool endFrontMissing;
        float endFront = SurfaceEye(SampleDepth01(lastUv), windowLimit, endFrontMissing);
        float endJump = max(0.08, min(cliffMeters, endFront * 0.04));
        // Same card in both layers: the object was photographed into the background.
        if (!endMissing && (endFrontMissing || endBack > endFront + endJump))
        {
            useBack = true;
            return ShadeLayer(windowLimit, lastUv, lastEye, true, meshUv, hitEye, background);
        }
    }
    if (lastBehind)
        return ShadePhoto(glassOS, windowLimit, openUv, openEye, meshUv, hitEye, background);

    return ShadePhoto(glassOS, windowLimit, lastUv, lastEye, meshUv, hitEye, background);
}

half3 SampleFrontRoomColor(float2 meshUv, float blend)
{
    float2 uv = CaptureUv(saturate(meshUv));
    half3 litA = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_PointClamp, uv, 0).rgb;
    half3 litB = SAMPLE_TEXTURE2D_LOD(_LightMapB, sampler_PointClamp, uv, 0).rgb;
    return lerp(litA, litB, blend);
}

half3 SampleBackRoomColor(float2 meshUv, float blend)
{
    float2 uv = CaptureUv(saturate(meshUv));
    half3 litA = SAMPLE_TEXTURE2D_LOD(_BackMap, sampler_PointClamp, uv, 0).rgb;
    half3 litB = SAMPLE_TEXTURE2D_LOD(_BackLightMapB, sampler_PointClamp, uv, 0).rgb;
    return lerp(litA, litB, blend);
}

half4 FragColor(Varyings input)
{
#ifdef ROOM_DEPTH_ONLY
    return 0;
#else
    float2 meshUv;
    float hitEye;
    bool background;
    bool useBack;
    float2 openUv;
    bool hit = MarchRoom(input.positionOS, meshUv, hitEye, background, useBack, openUv);
    if (!hit)
    {
        meshUv = input.meshUv;
        openUv = input.meshUv;
        background = false;
        useBack = false;
        hitEye = EyeFrom01(SampleDepth01(meshUv));
    }

    float2 uv = CaptureUv(meshUv);
    if (_BackgroundCutout >= 0.5)
    {
        meshUv = CutForegroundFromBackground(meshUv, _BackgroundCutout, useBack, WindowLimit(_DepthRange.z), max(_EdgeThreshold, 0.01));
        uv = CaptureUv(meshUv);
    }

    // Layers: black is the front photo, green is the background layer, red had nothing.
    bool showLayers = _DebugView > 2.5 && _DebugView < 3.5;
    if (showLayers)
        return background ? half4(1, 0, 0.2, 1) : (useBack ? half4(0, 0.8, 0.2, 1) : half4(0, 0, 0, 1));

    if (_DebugView > 0.5 && _DebugView < 1.5)
    {
        float span = max(_DepthRange.y - _DepthRange.x, 1e-4);
        float t = saturate((hitEye - _DepthRange.x) / span);
        return half4(t, t, t, 1);
    }

    float blend = saturate(_LightBlend);
    // Point-sample across object silhouettes so bilinear filtering never mixes
    // a lit wall texel with the neighboring object.
    bool nearCliff = useBack;
    if (!nearCliff)
    {
        float windowLimit = WindowLimit(_DepthRange.z);
        float jump = max(max(_EdgeThreshold, 0.01), hitEye * 0.06);
        float2 texel = max(_BaseMap_TexelSize.xy, float2(0.0005, 0.0005));
        [unroll]
        for (int k = 0; k < 4; k++)
        {
            float2 offset = float2(k == 0 ? texel.x : (k == 1 ? -texel.x : 0.0), k == 2 ? texel.y : (k == 3 ? -texel.y : 0.0));
            bool neighborMissing;
            float neighbor = SurfaceEye(SampleDepth01(meshUv + offset), windowLimit, neighborMissing);
            if (neighborMissing || abs(neighbor - hitEye) > jump)
                nearCliff = true;
        }
    }

    half3 litA;
    half3 litB;
    if (useBack)
    {
        litA = SAMPLE_TEXTURE2D_LOD(_BackMap, sampler_PointClamp, uv, 0).rgb;
        litB = SAMPLE_TEXTURE2D_LOD(_BackLightMapB, sampler_PointClamp, uv, 0).rgb;
    }
    else if (nearCliff)
    {
        litA = SAMPLE_TEXTURE2D_LOD(_BaseMap, sampler_PointClamp, uv, 0).rgb;
        litB = SAMPLE_TEXTURE2D_LOD(_LightMapB, sampler_PointClamp, uv, 0).rgb;
    }
    else
    {
        // Marched UVs jump at silhouettes. A raw derivative there covers the whole
        // photo and the mip filter draws the object as a smear toward the back wall.
        float2 dx = ddx(uv);
        float2 dy = ddy(uv);
        float gradLimit = 8.0 * max(_BaseMap_TexelSize.x, _BaseMap_TexelSize.y);
        float gradExtent = max(length(dx), length(dy));
        if (gradExtent > gradLimit)
        {
            float gradScale = gradLimit / gradExtent;
            dx *= gradScale;
            dy *= gradScale;
        }

        litA = SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap, uv, dx, dy).rgb;
        litB = SAMPLE_TEXTURE2D_GRAD(_LightMapB, sampler_LightMapB, uv, dx, dy).rgb;
    }

    if (_DebugView > 3.5 && _DebugView < 4.5)
        return half4(litA, 1.0);
    if (_DebugView > 4.5 && _DebugView < 5.5)
        return half4(litB, 1.0);

    half3 color = lerp(litA, litB, blend);
    // Carry cast shadows from the room surface right outside the silhouette onto the
    // revealed background layer (where the foreground shadow-caster was removed),
    // using a single scalar luminance ratio stepped away from the object so it never
    // amplifies highlights or shifts hue.
    if (useBack)
    {
        float2 awayDir = openUv - meshUv;
        float awayLen = length(awayDir);
        float2 stepDir = awayLen > 1e-5 ? (awayDir / awayLen) : float2(1.0, 0.0);
        float2 texel = max(_BaseMap_TexelSize.xy, float2(0.0005, 0.0005));
        float2 refUv1 = openUv + stepDir * texel * 2.0;
        float2 refUv2 = openUv + stepDir * texel * 5.0;
        half3 frontRef = min(SampleFrontRoomColor(refUv1, blend), SampleFrontRoomColor(refUv2, blend));
        half3 backRef = min(SampleBackRoomColor(refUv1, blend), SampleBackRoomColor(refUv2, blend));
        half frontLum = dot(frontRef, half3(0.299, 0.587, 0.114));
        half backLum = dot(backRef, half3(0.299, 0.587, 0.114));
        half shadowScale = clamp(frontLum / max(backLum, (half)1e-3), (half)0.35, (half)1.0);
        color *= shadowScale;
    }

    return half4(color * _BaseColor.rgb, _BaseColor.a);
#endif
}

struct FragOut
{
    half4 color : SV_Target;
    float depth : SV_Depth;
};

FragOut frag(Varyings input)
{
    FragOut output;
    output.color = FragColor(input);
    // Raster depth follows the near-plane push. The glass is the unpushed plane, so the
    // card has to record that same plane or it wins the depth test as the camera moves.
    output.depth = WindowPlaneDeviceDepth(input.positionCS, input.positionCS.z);
    return output;
}

#endif
