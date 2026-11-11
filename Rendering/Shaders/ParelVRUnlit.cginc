// Shared code for ParelVR/Unlit, ParelVR/Emissive and ParelVR/Particles/*: texture × colour (× vertex colour for
// particles), optional alpha test, blend state from material properties, optional emission, fog, instancing and
// single-pass stereo. ParelVR/Particles/Lit adds light probes and the main directional light.
#ifndef PARELVR_UNLIT_INCLUDED
#define PARELVR_UNLIT_INCLUDED

#include "UnityCG.cginc"
#include "ParelVRFog.cginc"
#if defined(PARELVR_PARTICLES_LIT)
    #include "UnityLightingCommon.cginc"
#endif

sampler2D _MainTex;
float4 _MainTex_ST;
fixed4 _Color;
half _Cutoff;
sampler2D _EmissionMap;
half4 _EmissionColor;
half _DstBlend;

struct appdataUnlit
{
    float4 vertex : POSITION;
    float2 texcoord : TEXCOORD0;
#if defined(PARELVR_VERTEX_COLOR)
    fixed4 color : COLOR;
#endif
#if defined(PARELVR_PARTICLES_LIT)
    float3 normal : NORMAL;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2fUnlit
{
    UNITY_POSITION(pos);
    float2 uv : TEXCOORD0;
    UNITY_FOG_COORDS(1)
#if defined(PARELVR_VERTEX_COLOR)
    fixed4 color : COLOR;
#endif
#if defined(PARELVR_PARTICLES_LIT)
    half3 lighting : TEXCOORD2;
#endif
    float3 worldPos : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

v2fUnlit vertUnlit(appdataUnlit v)
{
    v2fUnlit o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(v2fUnlit, o);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
    o.pos = UnityObjectToClipPos(v.vertex);
    o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
    o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
#if defined(PARELVR_VERTEX_COLOR)
    o.color = v.color;
#endif
#if defined(PARELVR_PARTICLES_LIT)
    half3 n = UnityObjectToWorldNormal(v.normal);
    o.lighting = ShadeSH9(half4(n, 1)) + _LightColor0.rgb * max(0, dot(n, _WorldSpaceLightPos0.xyz));
#endif
    UNITY_TRANSFER_FOG(o, o.pos);
    return o;
}

fixed4 fragUnlit(v2fUnlit i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    fixed4 c = tex2D(_MainTex, i.uv) * _Color;
#if defined(PARELVR_VERTEX_COLOR)
    c *= i.color;
#endif
#if defined(_ALPHATEST_ON)
    clip(c.a - _Cutoff);
#endif
#if defined(PARELVR_PARTICLES_LIT)
    c.rgb *= i.lighting;
#endif
#if defined(_EMISSION) || defined(PARELVR_ALWAYS_EMISSIVE)
    c.rgb += tex2D(_EmissionMap, i.uv).rgb * _EmissionColor.rgb;
#endif
#if defined(_ALPHAPREMULTIPLY_ON)
    c.rgb *= c.a;
#endif
#if !defined(_ALPHABLEND_ON) && !defined(_ALPHAPREMULTIPLY_ON) && !defined(PARELVR_VERTEX_COLOR)
    UNITY_OPAQUE_ALPHA(c.a);
#endif

    UNITY_APPLY_FOG(i.fogCoord, c);
    // ParelVR volumetric fog: additive blending only dims, premultiplied adds its share of the fog light.
    if (_DstBlend == 1.0h) c.rgb = ParelApplyFogAdditiveSurface(c.rgb, i.worldPos);
#if defined(_ALPHAPREMULTIPLY_ON)
    else c.rgb = ParelApplyFog(c.rgb, i.worldPos, c.a);
#else
    else c.rgb = ParelApplyFog(c.rgb, i.worldPos, 1.0h);
#endif
    return c;
}

#endif
