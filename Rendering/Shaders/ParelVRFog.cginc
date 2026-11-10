#ifndef PARELVR_FOG_INCLUDED
#define PARELVR_FOG_INCLUDED

// ParelVR volumetric fog: HDRP-style height fog (exponential falloff above a base height), light from the sun scattered
// in the fog (Henyey-Greenstein phase), and up to 4 local fog boxes (HDRP Local Volumetric Fog). Everything is worked
// out analytically along the view ray inside the shader that draws the surface: no extra pass, no depth texture, the
// same cost in VR as on a monitor. A world without it pays one uniform branch. The values are set as globals by
// ParelWorldRenderingInfo when a world converted from HDRP loads.

float4 _ParelFogParams;      // x on (0/1), y extinction at the base height (1/m), z base height (m), w height falloff (1/m)
float4 _ParelFogColor;       // rgb fog albedo, a max distance (m)
float4 _ParelFogAmbient;     // rgb ambient light reaching the fog
float4 _ParelFogSunDir;      // xyz direction towards the sun, w anisotropy (-1..1)
float4 _ParelFogSunColor;    // rgb sun light reaching the fog
float4 _ParelFogVolumeCount; // x number of local fog boxes (0..4)
float4x4 _ParelFogVolumeWorldToLocal[4]; // world → unit box (-0.5..0.5)
float4 _ParelFogVolumeParams[4];         // rgb albedo, a extinction (1/m)

// Henyey-Greenstein phase function, normalised so g = 0 gives 1.
half ParelFogPhase(half cosTheta, half g)
{
    half g2 = g * g;
    half d = max(1.0h + g2 - 2.0h * g * cosTheta, 1e-3h);
    return (1.0h - g2) / (d * sqrt(d));
}

// Optical depth of the height fog between the camera and a point dist along dir.
float ParelHeightFogDepth(float3 cam, float3 dir, float dist)
{
    float k = max(_ParelFogParams.w, 1e-5);
    float h0 = max(cam.y - _ParelFogParams.z, 0.0);           // below the base height the fog is uniform
    float dy = dir.y * dist;
    float start = _ParelFogParams.y * exp(-k * h0);
    float depth = abs(k * dy) > 1e-4 ? start * (1.0 - exp(-k * dy)) / (k * dir.y) : start * dist;
    return min(max(depth, 0.0), _ParelFogParams.y * dist);  // never denser than the base everywhere
}

// Adds the local fog boxes the ray passes through: optical depth, and their colour weighted by it.
void ParelFogVolumes(float3 cam, float3 p, inout float depth, inout half3 albedoDepth)
{
    int count = (int)_ParelFogVolumeCount.x;
    [loop] for (int v = 0; v < count; v++)
    {
        float3 a = mul(_ParelFogVolumeWorldToLocal[v], float4(cam, 1.0)).xyz;
        float3 b = mul(_ParelFogVolumeWorldToLocal[v], float4(p, 1.0)).xyz;
        float3 d = b - a;
        float3 inv = 1.0 / (abs(d) > 1e-6 ? d : 1e-6);
        float3 t0 = (-0.5 - a) * inv, t1 = (0.5 - a) * inv;
        float3 tmin = min(t0, t1), tmax = max(t0, t1);
        float enter = max(max(tmin.x, tmin.y), max(tmin.z, 0.0));
        float exit = min(min(tmax.x, tmax.y), min(tmax.z, 1.0));
        float inside = max(exit - enter, 0.0) * length(p - cam);
        float vd = inside * _ParelFogVolumeParams[v].a;
        depth += vd;
        albedoDepth += _ParelFogVolumeParams[v].rgb * vd;
    }
}

// Fogs a lit (or unlit) surface colour seen at posWorld. premultipliedAlpha: the alpha the colour was premultiplied
// with (1 for opaque and straight alpha), so a premultiplied surface only adds its share of the fog light.
half3 ParelApplyFog(half3 color, float3 posWorld, half premultipliedAlpha)
{
    UNITY_BRANCH
    if (_ParelFogParams.x <= 0.0) return color;
    float3 cam = _WorldSpaceCameraPos;
    float3 ray = posWorld - cam;
    float len = length(ray);
    float3 dir = ray / max(len, 1e-4);
    float dist = min(len, _ParelFogColor.a);
    float3 p = cam + dir * dist;

    float heightDepth = ParelHeightFogDepth(cam, dir, dist);
    float depth = heightDepth;
    half3 albedoDepth = 0;
    ParelFogVolumes(cam, p, depth, albedoDepth);
    half transmittance = exp(-depth);

    // Ambient and sun light scattered towards the eye, coloured by the albedo of whatever fog the ray went through.
    half phase = ParelFogPhase(dot(dir, _ParelFogSunDir.xyz), _ParelFogSunDir.w);
    half3 light = _ParelFogAmbient.rgb + _ParelFogSunColor.rgb * phase;
    half3 albedo = depth > 1e-5 ? (_ParelFogColor.rgb * heightDepth + albedoDepth) / depth : _ParelFogColor.rgb;
    half3 inscatter = light * albedo;
    return color * transmittance + inscatter * (1.0h - transmittance) * premultipliedAlpha;
}

// For additive passes (extra lights, additive surfaces): the fog only dims what is added. Every extra light re-draws
// what it touches, so this uses the height fog alone (a few ALU): local fog boxes dim only the main pass. The light
// of an extra lamp seen through a fog box is a little brighter than in HDRP; the cost stays flat with many lights.
half3 ParelApplyFogAdditive(half3 color, float3 posWorld)
{
    UNITY_BRANCH
    if (_ParelFogParams.x <= 0.0) return color;
    float3 cam = _WorldSpaceCameraPos;
    float3 ray = posWorld - cam;
    float len = length(ray);
    float3 dir = ray / max(len, 1e-4);
    return color * exp(-ParelHeightFogDepth(cam, dir, min(len, _ParelFogColor.a)));
}

// The sky: the fog seen along a view direction out to the max distance.
half3 ParelApplyFogSky(half3 color, float3 viewDir)
{
    UNITY_BRANCH
    if (_ParelFogParams.x <= 0.0) return color;
    return ParelApplyFog(color, _WorldSpaceCameraPos + normalize(viewDir) * _ParelFogColor.a, 1.0h);
}

#endif
