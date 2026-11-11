#ifndef PARELVR_SUBSURFACE_INCLUDED
#define PARELVR_SUBSURFACE_INCLUDED

// ParelVR/Subsurface: Unity's Standard lighting plus an inexpensive subsurface term, for HDRP's Subsurface Scattering
// and Translucent materials (skin, wax, leaves, marble...).
//  - Scattering: light wraps past the shadow terminator, tinted by the scattering colour, so the edge between lit and
//    unlit goes soft and warm the way blurred diffuse light does in HDRP.
//  - Transmission: light from behind shines through thin parts (thickness map), tinted by the transmission colour.
// Both are a few ALU per light and one extra texture read (thickness + mask share it), no extra pass.

#include "UnityStandardBRDF.cginc"

half4 _SubsurfaceColor;
half4 _TransmissionTint;
half _SubsurfaceMask;
half _SubsurfaceWrap;
half _ThicknessMin;
half _ThicknessMax;
half _TransmissionScale;
half _TransmissionPower;
half _TransmissionDistortion;
sampler2D _ThicknessMap;       // R thickness (HDRP layout)
sampler2D _SubsurfaceMaskMap;  // R subsurface mask

static half g_parelThickness = 1.0h;
static half g_parelSubsurface = 1.0h;

#ifndef PARELVR_BASE_BRDF
    #define PARELVR_BASE_BRDF BRDF1_Unity_PBS
#endif

half4 ParelBRDF_Subsurface(half3 diffColor, half3 specColor, half oneMinusReflectivity, half smoothness,
    float3 normal, float3 viewDir, UnityLight light, UnityIndirect gi)
{
    half4 c = PARELVR_BASE_BRDF(diffColor, specColor, oneMinusReflectivity, smoothness, normal, viewDir, light, gi);

    half nl = dot(normal, light.dir);
    half w = _SubsurfaceWrap;
    half scatter = max(saturate((nl + w) / (1.0h + w)) - saturate(nl), 0.0h);

    half3 h = normalize(light.dir + normal * _TransmissionDistortion);
    half back = pow(saturate(dot(viewDir, -h)), _TransmissionPower) * _TransmissionScale * (1.0h - g_parelThickness);
#if defined(PARELVR_SSS_MAIN_LIGHT)
    // The main light without the object's own shadow: light coming through from behind is exactly what that shadow hides.
    half3 backLight = _LightColor0.rgb;
#else
    half3 backLight = light.color;
#endif
    c.rgb += diffColor * (scatter * _SubsurfaceColor.rgb * light.color + back * _TransmissionTint.rgb * backLight) * g_parelSubsurface;
    return c;
}

#define UNITY_BRDF_PBS ParelBRDF_Subsurface
#include "ParelVRStandardForward.cginc"

void ParelSubsurfaceSetup(float2 uv)
{
    // Always read (white when unset: thickness = max, mask = 1). Two cheap reads instead of twice the shader variants.
    g_parelSubsurface = _SubsurfaceMask * tex2D(_SubsurfaceMaskMap, uv).r;
    g_parelThickness = lerp(_ThicknessMin, _ThicknessMax, tex2D(_ThicknessMap, uv).r);
}

half4 parelFragSubsurfaceBase(VertexOutputForwardBase i, half facing : VFACE) : SV_Target
{
    ParelSubsurfaceSetup(i.tex.xy);
    return parelFragBase(i, facing);
}

half4 parelFragSubsurfaceAdd(VertexOutputForwardAdd i) : SV_Target
{
    ParelSubsurfaceSetup(i.tex.xy);
    return parelFragAdd(i);
}

#endif
