#ifndef PARELVR_LAYERED_INCLUDED
#define PARELVR_LAYERED_INCLUDED

// ParelVR/Layered Lit: up to 4 PBR layers (HDRP Layered Lit), each with its own colour map, normal map, mask map
// (R metallic, G occlusion, A smoothness), colour, tiling and remaps, blended by a layer mask (R → layer 1, G → layer 2,
// B → layer 3, each painted over the ones below) and optionally the vertex colours. Lighting is Unity's Standard
// (UnityStandardCore helpers + UNITY_BRDF_PBS), plus ParelVR fog.
// Cost control: the 12 layer textures share 3 samplers; the Quest SubShader (PARELVR_LAYERED_LITE) reads only the
// colour map of layers 1-3 (their normal and mask maps fall back to the layer's values).

#define _NORMALMAP 1
#include "ParelVRStandardForward.cginc"

UNITY_DECLARE_TEX2D(_Layer0Map);
UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer1Map);
UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer2Map);
UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer3Map);
UNITY_DECLARE_TEX2D(_Layer0NormalMap);
UNITY_DECLARE_TEX2D(_Layer0MaskMap);
#if !defined(PARELVR_LAYERED_LITE)
    UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer1NormalMap);
    UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer2NormalMap);
    UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer3NormalMap);
    UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer1MaskMap);
    UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer2MaskMap);
    UNITY_DECLARE_TEX2D_NOSAMPLER(_Layer3MaskMap);
#endif
sampler2D _LayerMaskMap;
float4 _LayerMaskMap_ST;
float4 _Layer0Map_ST, _Layer1Map_ST, _Layer2Map_ST, _Layer3Map_ST;
half4 _Layer0Color, _Layer1Color, _Layer2Color, _Layer3Color;
half _Layer0NormalScale, _Layer1NormalScale, _Layer2NormalScale, _Layer3NormalScale;
half _Layer0MetallicMin, _Layer1MetallicMin, _Layer2MetallicMin, _Layer3MetallicMin;
half _Layer0MetallicMax, _Layer1MetallicMax, _Layer2MetallicMax, _Layer3MetallicMax;
half _Layer0SmoothnessMin, _Layer1SmoothnessMin, _Layer2SmoothnessMin, _Layer3SmoothnessMin;
half _Layer0SmoothnessMax, _Layer1SmoothnessMax, _Layer2SmoothnessMax, _Layer3SmoothnessMax;
half _Layer0AOMin, _Layer1AOMin, _Layer2AOMin, _Layer3AOMin;
half _Layer0AOMax, _Layer1AOMax, _Layer2AOMax, _Layer3AOMax;
half _LayerCount;
half _LayerVertexColorMode; // 0 off, 1 multiply the mask, 2 add to the mask

struct ParelLayeredInput
{
    float4 vertex : POSITION;
    half3 normal : NORMAL;
    float2 uv0 : TEXCOORD0;
    float2 uv1 : TEXCOORD1;
#if defined(DYNAMICLIGHTMAP_ON)
    float2 uv2 : TEXCOORD2;
#endif
    half4 tangent : TANGENT;
    fixed4 color : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2fLayered
{
    UNITY_POSITION(pos);
    float4 uv : TEXCOORD0;                 // xy UV0, zw layer-mask UV
    float3 posWorld : TEXCOORD1;
    float4 tangentToWorld[3] : TEXCOORD2;  // tangent, binormal, normal | w: light direction (add pass)
    half4 ambientOrLightmapUV : TEXCOORD5;
    UNITY_LIGHTING_COORDS(6, 7)
    UNITY_FOG_COORDS(8)
    fixed4 color : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

v2fLayered vertLayered(ParelLayeredInput v)
{
    UNITY_SETUP_INSTANCE_ID(v);
    v2fLayered o;
    UNITY_INITIALIZE_OUTPUT(v2fLayered, o);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    float4 posWorld = mul(unity_ObjectToWorld, v.vertex);
    o.posWorld = posWorld.xyz;
    o.pos = UnityObjectToClipPos(v.vertex);
    o.uv = float4(v.uv0, TRANSFORM_TEX(v.uv0, _LayerMaskMap));
    o.color = v.color;

    float3 normalWorld = UnityObjectToWorldNormal(v.normal);
    float3 tangentWorld = UnityObjectToWorldDir(v.tangent.xyz);
    float3x3 t2w = CreateTangentToWorldPerVertex(normalWorld, tangentWorld, v.tangent.w);
    o.tangentToWorld[0].xyz = t2w[0];
    o.tangentToWorld[1].xyz = t2w[1];
    o.tangentToWorld[2].xyz = t2w[2];

    UNITY_TRANSFER_LIGHTING(o, v.uv1);

#if defined(PARELVR_LAYERED_ADD)
    float3 lightDir = _WorldSpaceLightPos0.xyz - posWorld.xyz * _WorldSpaceLightPos0.w;
    #ifndef USING_DIRECTIONAL_LIGHT
        lightDir = NormalizePerVertexNormal(lightDir);
    #endif
    o.tangentToWorld[0].w = lightDir.x;
    o.tangentToWorld[1].w = lightDir.y;
    o.tangentToWorld[2].w = lightDir.z;
#else
    VertexInput vi = (VertexInput)0;
    vi.uv1 = v.uv1;
    #if defined(DYNAMICLIGHTMAP_ON)
        vi.uv2 = v.uv2;
    #endif
    o.ambientOrLightmapUV = VertexGIForward(vi, posWorld, normalWorld);
#endif

    UNITY_TRANSFER_FOG(o, o.pos);
    return o;
}

struct ParelLayer
{
    half3 albedo;
    half alpha;
    half3 normalTS;
    half metallic;
    half smoothness;
    half occlusion;
};

#define PARELVR_LAYER_UV(n, uv) ((uv) * _Layer##n##Map_ST.xy + _Layer##n##Map_ST.zw)

#define PARELVR_LAYER_VALUES(n, l, mask) \
    l.metallic = lerp(_Layer##n##MetallicMin, _Layer##n##MetallicMax, mask.r); \
    l.smoothness = lerp(_Layer##n##SmoothnessMin, _Layer##n##SmoothnessMax, mask.a); \
    l.occlusion = lerp(_Layer##n##AOMin, _Layer##n##AOMax, mask.g);

ParelLayer ParelLayer0(float2 uv0)
{
    ParelLayer l;
    float2 uv = PARELVR_LAYER_UV(0, uv0);
    half4 c = UNITY_SAMPLE_TEX2D(_Layer0Map, uv) * _Layer0Color;
    l.albedo = c.rgb;
    l.alpha = c.a;
    l.normalTS = UnpackScaleNormal(UNITY_SAMPLE_TEX2D(_Layer0NormalMap, uv), _Layer0NormalScale);
    half4 mask = UNITY_SAMPLE_TEX2D(_Layer0MaskMap, uv);
    PARELVR_LAYER_VALUES(0, l, mask)
    return l;
}

#if defined(PARELVR_LAYERED_LITE)
    #define PARELVR_LAYER_FN(n) \
    ParelLayer ParelLayer##n(float2 uv0) \
    { \
        ParelLayer l; \
        float2 uv = PARELVR_LAYER_UV(n, uv0); \
        half4 c = UNITY_SAMPLE_TEX2D_SAMPLER(_Layer##n##Map, _Layer0Map, uv) * _Layer##n##Color; \
        l.albedo = c.rgb; l.alpha = c.a; l.normalTS = half3(0, 0, 1); \
        half4 mask = half4(1, 1, 1, 1); \
        PARELVR_LAYER_VALUES(n, l, mask) \
        return l; \
    }
#else
    #define PARELVR_LAYER_FN(n) \
    ParelLayer ParelLayer##n(float2 uv0) \
    { \
        ParelLayer l; \
        float2 uv = PARELVR_LAYER_UV(n, uv0); \
        half4 c = UNITY_SAMPLE_TEX2D_SAMPLER(_Layer##n##Map, _Layer0Map, uv) * _Layer##n##Color; \
        l.albedo = c.rgb; l.alpha = c.a; \
        l.normalTS = UnpackScaleNormal(UNITY_SAMPLE_TEX2D_SAMPLER(_Layer##n##NormalMap, _Layer0NormalMap, uv), _Layer##n##NormalScale); \
        half4 mask = UNITY_SAMPLE_TEX2D_SAMPLER(_Layer##n##MaskMap, _Layer0MaskMap, uv); \
        PARELVR_LAYER_VALUES(n, l, mask) \
        return l; \
    }
#endif

PARELVR_LAYER_FN(1)
PARELVR_LAYER_FN(2)
PARELVR_LAYER_FN(3)

ParelLayer ParelLerpLayer(ParelLayer a, ParelLayer b, half t)
{
    ParelLayer r;
    r.albedo = lerp(a.albedo, b.albedo, t);
    r.alpha = lerp(a.alpha, b.alpha, t);
    r.normalTS = lerp(a.normalTS, b.normalTS, t);
    r.metallic = lerp(a.metallic, b.metallic, t);
    r.smoothness = lerp(a.smoothness, b.smoothness, t);
    r.occlusion = lerp(a.occlusion, b.occlusion, t);
    return r;
}

ParelLayer ParelBlendLayers(v2fLayered i)
{
    half3 m = tex2D(_LayerMaskMap, i.uv.zw).rgb;
    if (_LayerVertexColorMode > 1.5h) m = saturate(m + (i.color.rgb - 0.5h) * 2.0h);
    else if (_LayerVertexColorMode > 0.5h) m *= i.color.rgb;
    m *= half3(_LayerCount > 1.5h, _LayerCount > 2.5h, _LayerCount > 3.5h);

    ParelLayer l = ParelLayer0(i.uv.xy);
    l = ParelLerpLayer(l, ParelLayer1(i.uv.xy), m.r);
    l = ParelLerpLayer(l, ParelLayer2(i.uv.xy), m.g);
    l = ParelLerpLayer(l, ParelLayer3(i.uv.xy), m.b);
    l.normalTS = normalize(l.normalTS);
    return l;
}

FragmentCommonData ParelLayeredSetup(v2fLayered i, out half occlusion)
{
    ParelLayer l = ParelBlendLayers(i);
#if defined(_ALPHATEST_ON)
    clip(l.alpha - _Cutoff);
#endif
    FragmentCommonData s = (FragmentCommonData)0;
    half oneMinusReflectivity;
    half3 specColor;
    s.diffColor = DiffuseAndSpecularFromMetallic(l.albedo, l.metallic, specColor, oneMinusReflectivity);
    s.specColor = specColor;
    s.oneMinusReflectivity = oneMinusReflectivity;
    s.smoothness = l.smoothness;
    half3 t = i.tangentToWorld[0].xyz, b = i.tangentToWorld[1].xyz, n = i.tangentToWorld[2].xyz;
    s.normalWorld = normalize(t * l.normalTS.x + b * l.normalTS.y + n * l.normalTS.z);
    s.eyeVec = normalize(i.posWorld - _WorldSpaceCameraPos);
    s.posWorld = i.posWorld;
    s.alpha = 1;
    occlusion = l.occlusion;
    return s;
}

half4 fragLayeredBase(v2fLayered i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half occlusion;
    FragmentCommonData s = ParelLayeredSetup(i, occlusion);

    UnityLight mainLight = MainLight();
    UNITY_LIGHT_ATTENUATION(atten, i, s.posWorld);
    UnityGI gi = FragmentGI(s, occlusion, i.ambientOrLightmapUV, atten, mainLight);
    half4 c = UNITY_BRDF_PBS(s.diffColor, s.specColor, s.oneMinusReflectivity, s.smoothness, s.normalWorld, -s.eyeVec, gi.light, gi.indirect);
    c.rgb += Emission(PARELVR_LAYER_UV(0, i.uv.xy));

    UNITY_APPLY_FOG(i.fogCoord, c.rgb);
    c.rgb = ParelApplyFog(c.rgb, i.posWorld, 1.0h);
    UNITY_OPAQUE_ALPHA(c.a);
    return c;
}

half4 fragLayeredAdd(v2fLayered i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
    half occlusion;
    FragmentCommonData s = ParelLayeredSetup(i, occlusion);

    UNITY_LIGHT_ATTENUATION(atten, i, s.posWorld)
    UnityLight light = AdditiveLight(half3(i.tangentToWorld[0].w, i.tangentToWorld[1].w, i.tangentToWorld[2].w), atten);
    half4 c = UNITY_BRDF_PBS(s.diffColor, s.specColor, s.oneMinusReflectivity, s.smoothness, s.normalWorld, -s.eyeVec, light, ZeroIndirect());

    UNITY_APPLY_FOG_COLOR(i.fogCoord, c.rgb, half4(0, 0, 0, 0));
    c.rgb = ParelApplyFogAdditive(c.rgb, i.posWorld);
    UNITY_OPAQUE_ALPHA(c.a);
    return c;
}

#endif
