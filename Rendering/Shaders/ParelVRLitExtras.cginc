#ifndef PARELVR_LIT_EXTRAS_INCLUDED
#define PARELVR_LIT_EXTRAS_INCLUDED

// HDRP features for the ParelVR lit shaders, on top of Unity's Standard lighting:
//   material: clear coat, iridescence, anisotropic highlights, double-sided normal modes, geometric specular
//             anti-aliasing, refraction (box / sphere / thin, transmittance colour, absorption), no-fog surfaces
//   world:    screen-space reflections, screen-space global illumination, contact shadows, micro shadows,
//             real-time area lights (rectangle / tube / disc)
// The world features read the camera depth texture and ParelVR's opaque picture (ParelOpaqueCapture); each one is a
// uniform branch that costs nothing while its world setting is off (and on standalone headsets).

// ---- material properties -------------------------------------------------------------------------------------------
half _ParelCoatMask, _ParelCoatSmoothness;
sampler2D _ParelCoatMap;
half _ParelIridescence, _ParelIridescenceThickness;
half _ParelAnisotropy;
half4 _ParelSpecularAA;          // x on, y screen-space variance, z threshold
half _ParelDoubleSidedNormals;   // 0 none, 1 flip, 2 mirror
half _ParelRefraction;           // 0 none, 1 box, 2 sphere, 3 thin
half _ParelIor, _ParelAbsorptionDistance, _ParelRefractionThickness;
half4 _ParelTransmittanceColor;
half _ParelNoFog;

// ---- world settings (globals) --------------------------------------------------------------------------------------
float4 _ParelSSR;               // x on, y max ray length (m), z min smoothness, w smoothness fade start
float4 _ParelSSGI;              // x on, y intensity, z ray length (m)
float4 _ParelContactShadows;    // x on, y length (m), z opacity, w max distance (m)
float4 _ParelMicroShadows;      // x opacity
float4 _ParelAreaLightCount;
float4 _ParelAreaLightPos[4];   // xyz centre, w shape 0 rectangle / 1 tube / 2 disc
float4 _ParelAreaLightRight[4]; // xyz half-width axis, w range
float4 _ParelAreaLightUp[4];    // xyz half-height axis (rect / disc radius), w two-sided
float4 _ParelAreaLightColor[4]; // rgb radiance × area normalisation
float4 _ParelOpaqueOn;
float4x4 _ParelPrevViewProj[2];

#ifndef PARELVR_DEPTH_DECLARED
#define PARELVR_DEPTH_DECLARED
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
#endif
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    UNITY_DECLARE_TEX2DARRAY(_ParelOpaqueTexture);
    #define PARELVR_OPAQUE_LOD(uv, lod) UNITY_SAMPLE_TEX2DARRAY_LOD(_ParelOpaqueTexture, float3(uv, unity_StereoEyeIndex), lod)
    #define PARELVR_EYE unity_StereoEyeIndex
#else
    sampler2D _ParelOpaqueTexture;
    #define PARELVR_OPAQUE_LOD(uv, lod) tex2Dlod(_ParelOpaqueTexture, float4(uv, 0, lod))
    #define PARELVR_EYE 0
#endif

// Screen position (0..1, row-aligned with the depth texture) and eye depth of a world point, this frame.
float3 ParelProject(float3 p)
{
    float4 c = mul(UNITY_MATRIX_VP, float4(p, 1.0));
    float4 s = ComputeScreenPos(c);
    return float3(s.xy / s.w, c.w);
}

float ParelSceneDepth(float2 uv) { return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0))); }

float ParelEdgeFade(float2 uv) { float2 e = saturate(min(uv, 1.0 - uv) * 10.0); return e.x * e.y; }

float ParelNoise(float2 pixel) { return frac(52.9829189 * frac(dot(pixel + _Time.y * 37.0, float2(0.06711056, 0.00583715)))); }

// ---- micro shadows (HDRP): ambient occlusion also shades direct light at grazing angles -----------------------------
half ParelMicroShadow(half occlusion, half NdotL)
{
    if (_ParelMicroShadows.x <= 0.0) return 1.0;
    half aperture = 2.0 * occlusion * occlusion;
    half ms = saturate(abs(NdotL) + aperture - 1.0);
    return lerp(1.0, ms, _ParelMicroShadows.x);
}

// ---- contact shadows (HDRP): a short ray towards the light through the depth buffer --------------------------------
half ParelContactShadow(float3 posWorld, float3 L, float2 pixel)
{
    UNITY_BRANCH
    if (_ParelContactShadows.x <= 0.0 || _ParelOpaqueOn.x <= 0.0) return 1.0;
    float dist = distance(posWorld, _WorldSpaceCameraPos);
    if (dist > _ParelContactShadows.w) return 1.0;
    const int STEPS = 12;
    float stepLen = _ParelContactShadows.y / STEPS;
    float t = stepLen * (0.5 + ParelNoise(pixel));
    half shadow = 1.0;
    [loop] for (int k = 0; k < STEPS; k++)
    {
        float3 q = ParelProject(posWorld + L * t);
        if (any(q.xy < 0.0) || any(q.xy > 1.0)) break;
        float scene = ParelSceneDepth(q.xy);
        float diff = q.z - scene;
        if (diff > 0.02 && diff < _ParelContactShadows.y * 2.0) { shadow = 0.0; break; }
        t += stepLen;
    }
    half fade = saturate(1.0 - dist / _ParelContactShadows.w) ;
    return lerp(1.0, shadow, _ParelContactShadows.z * fade);
}

// ---- screen-space reflections (HDRP SSR): march the reflected ray through the depth buffer, read last frame's
// picture where it hits (reprojected), fade at the screen edges, by distance and by roughness -------------------------
half3 ParelSSR(float3 posWorld, float3 N, float3 V, half smoothness, float2 pixel, out half weight)
{
    weight = 0;
    UNITY_BRANCH
    if (_ParelSSR.x <= 0.0 || _ParelOpaqueOn.x <= 0.0 || smoothness < _ParelSSR.z) return 0;
    float3 R = reflect(-V, N);
    // Rays coming back at the camera find nothing on screen.
    float facing = dot(R, -V);
    if (facing > 0.95) return 0;
    const int STEPS = 24;
    float maxLen = _ParelSSR.y;
    float t = 0.05 + 0.1 * ParelNoise(pixel);
    float stepLen = maxLen / STEPS;
    float3 hit = 0;
    bool found = false;
    float prevT = t;
    [loop] for (int k = 0; k < STEPS; k++)
    {
        float3 p = posWorld + R * t;
        float3 q = ParelProject(p);
        if (any(q.xy < 0.0) || any(q.xy > 1.0) || q.z <= 0.0) break;
        float scene = ParelSceneDepth(q.xy);
        float diff = q.z - scene;
        if (diff > 0.0 && diff < max(stepLen * 1.5, 0.1))
        {
            // Refine between the last two points.
            float a = prevT, b = t;
            [unroll] for (int r = 0; r < 4; r++)
            {
                float m = 0.5 * (a + b);
                float3 mq = ParelProject(posWorld + R * m);
                if (mq.z - ParelSceneDepth(mq.xy) > 0.0) b = m; else a = m;
            }
            hit = posWorld + R * b;
            found = true;
            break;
        }
        prevT = t;
        stepLen *= 1.08;
        t += stepLen;
    }
    if (!found) return 0;
    float4 prev = mul(_ParelPrevViewProj[PARELVR_EYE], float4(hit, 1.0));
    float2 puv = prev.xy / prev.w * 0.5 + 0.5;
    #if UNITY_UV_STARTS_AT_TOP
        if (_ProjectionParams.x < 0.0) puv.y = 1.0 - puv.y;
    #endif
    if (any(puv < 0.0) || any(puv > 1.0)) return 0;
    half rough = 1.0 - smoothness;
    half3 col = PARELVR_OPAQUE_LOD(puv, rough * 6.0).rgb;
    float fadeSmooth = saturate((smoothness - _ParelSSR.z) / max(_ParelSSR.w - _ParelSSR.z, 1e-3));
    weight = ParelEdgeFade(puv) * fadeSmooth * saturate(1.0 - facing) * saturate(1.0 - distance(hit, posWorld) / maxLen);
    return col;
}

// ---- screen-space global illumination (HDRP SSGI): light bouncing off what's on screen -----------------------------
half3 ParelSSGI(float3 posWorld, float3 N, float2 pixel, out half weight)
{
    weight = 0;
    UNITY_BRANCH
    if (_ParelSSGI.x <= 0.0 || _ParelOpaqueOn.x <= 0.0) return 0;
    // 4 directions around the normal (rotated per pixel), 6 steps each.
    float3 T = normalize(abs(N.y) < 0.99 ? cross(N, float3(0, 1, 0)) : cross(N, float3(1, 0, 0)));
    float3 B = cross(N, T);
    float rot = ParelNoise(pixel) * 6.2831853;
    half3 sum = 0;
    half hits = 0;
    [unroll] for (int d = 0; d < 4; d++)
    {
        float a = rot + d * 1.5707963;
        float3 dir = normalize(N * 0.7 + (T * cos(a) + B * sin(a)) * 0.7);
        float t = _ParelSSGI.z / 6.0;
        [loop] for (int k = 0; k < 6; k++)
        {
            float3 q = ParelProject(posWorld + dir * t);
            if (any(q.xy < 0.0) || any(q.xy > 1.0)) break;
            float diff = q.z - ParelSceneDepth(q.xy);
            if (diff > 0.02 && diff < _ParelSSGI.z)
            {
                float4 prev = mul(_ParelPrevViewProj[PARELVR_EYE], float4(posWorld + dir * t, 1.0));
                float2 puv = prev.xy / prev.w * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                    if (_ProjectionParams.x < 0.0) puv.y = 1.0 - puv.y;
                #endif
                // A blurred read (mip 5) keeps one-sample bounces from sparkling.
                sum += min(PARELVR_OPAQUE_LOD(saturate(puv), 5.0).rgb, 4.0) * dot(dir, N);
                hits += 1.0;
                break;
            }
            t += _ParelSSGI.z / 6.0;
        }
    }
    weight = hits / 4.0;
    return hits > 0 ? sum / hits * _ParelSSGI.y : 0;
}

// ---- iridescence (HDRP): a thin film over the surface tints its reflection with the viewing angle ------------------
half3 ParelIridescence(half3 specColor, half NdotV)
{
    if (_ParelIridescence <= 0.0) return specColor;
    // Thin-film interference: phase difference per wavelength (R G B) through a 200..1000 nm film.
    float thickness = lerp(200.0, 1000.0, _ParelIridescenceThickness);
    float cosT = sqrt(1.0 - (1.0 - NdotV * NdotV) / (1.33 * 1.33));
    float3 wavelengths = float3(650.0, 532.0, 450.0);
    float3 phase = 4.0 * UNITY_PI * 1.33 * thickness * cosT / wavelengths;
    half3 film = 0.5 + 0.5 * cos(phase);
    return lerp(specColor, specColor * 0.4 + film * 0.6, _ParelIridescence);
}

// ---- GGX for the extra lobes -----------------------------------------------------------------------------------------
float ParelGGX(float NdotH, float roughness)
{
    float a2 = max(roughness * roughness, 1e-4);
    a2 *= a2;
    float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (UNITY_PI * d * d + 1e-7);
}

float ParelAnisoGGX(float3 H, float3 N, float3 T, float3 B, float ax, float ay)
{
    float th = dot(T, H), bh = dot(B, H), nh = dot(N, H);
    float d = th * th / (ax * ax) + bh * bh / (ay * ay) + nh * nh;
    return 1.0 / (UNITY_PI * ax * ay * d * d + 1e-7);
}

// ---- real-time area lights (HDRP rectangle / tube / disc): the representative point on the light that reflects most
// (Karis 2013) for the highlight, the closest point for the diffuse light, energy-normalised ---------------------------
half3 ParelAreaLights(half3 diffColor, half3 specColor, half smoothness, float3 N, float3 V, float3 posWorld)
{
    int count = (int)_ParelAreaLightCount.x;
    if (count <= 0) return 0;
    half3 sum = 0;
    half rough = max(1.0 - smoothness, 0.04);
    float a = rough * rough;
    float3 R = reflect(-V, N);
    [loop] for (int l = 0; l < count; l++)
    {
        float3 c = _ParelAreaLightPos[l].xyz;
        float3 ax = _ParelAreaLightRight[l].xyz, ay = _ParelAreaLightUp[l].xyz;
        float range = _ParelAreaLightRight[l].w;
        float3 toC = c - posWorld;
        if (dot(toC, toC) > range * range) continue;
        float shape = _ParelAreaLightPos[l].w;
        float3 lightN = normalize(cross(ax, ay));
        bool twoSided = _ParelAreaLightUp[l].w > 0.5;
        if (!twoSided && shape != 1.0 && dot(-toC, lightN) <= 0.0) continue;

        // Diffuse: the closest point on the light's shape.
        float3 rel = posWorld - c;
        float ex = length(ax), ey = length(ay);
        float3 ux = ax / max(ex, 1e-4), uy = ay / max(ey, 1e-4);
        float3 closest;
        if (shape == 1.0) closest = c + ux * clamp(dot(rel, ux), -ex, ex);
        else if (shape == 2.0) { float2 d2 = float2(dot(rel, ux), dot(rel, uy)); float len = length(d2); closest = c + (len > ex ? (ux * d2.x + uy * d2.y) * (ex / len) : ux * d2.x + uy * d2.y); }
        else closest = c + ux * clamp(dot(rel, ux), -ex, ex) + uy * clamp(dot(rel, uy), -ey, ey);
        float3 Ld = closest - posWorld;
        float dd = max(dot(Ld, Ld), 1e-4);
        Ld *= rsqrt(dd);
        float NdotL = saturate(dot(N, Ld));
        float facing = shape == 1.0 || twoSided ? 1.0 : saturate(dot(-Ld, lightN));
        float window = saturate(1.0 - pow(dd / (range * range), 2.0));
        half3 radiance = _ParelAreaLightColor[l].rgb * window * window;
        sum += diffColor * radiance * NdotL * facing / (1.0 + dd) ;

        // Specular: the point on the light closest to the reflected ray.
        float tR = dot(c - posWorld, R);
        float3 onRay = posWorld + R * max(tR, 0.0);
        float3 rp = onRay - c;
        float3 spec;
        if (shape == 1.0) spec = c + ux * clamp(dot(rp, ux), -ex, ex);
        else spec = c + ux * clamp(dot(rp, ux), -ex, ex) + uy * clamp(dot(rp, uy), -(shape == 2.0 ? ex : ey), shape == 2.0 ? ex : ey);
        float3 Ls = spec - posWorld;
        float ds = max(dot(Ls, Ls), 1e-4);
        Ls *= rsqrt(ds);
        float3 H = normalize(Ls + V);
        float sizeAngle = saturate(max(ex, ey) / sqrt(ds));
        float norm = (a * a) / max(saturate(a + sizeAngle * 0.5) * saturate(a + sizeAngle * 0.5), 1e-4);  // energy kept as the light widens the lobe
        float D = ParelGGX(saturate(dot(N, H)), rough) * norm;
        half3 F = specColor + (1.0 - specColor) * pow(1.0 - saturate(dot(Ls, H)), 5.0);
        sum += F * D * saturate(dot(N, Ls)) * radiance * 0.25 * UNITY_PI / (1.0 + ds) * 4.0;
    }
    return sum;
}

#endif
