// Shared code for ParelVR/Simple Lit: Lambert diffuse + Blinn-Phong specular (the legacy Specular look),
// baked lightmaps / light probes through Unity's GI functions, fog, instancing and single-pass stereo.
#ifndef PARELVR_SIMPLE_LIT_INCLUDED
#define PARELVR_SIMPLE_LIT_INCLUDED

#include "UnityCG.cginc"
#include "Lighting.cginc"
#include "AutoLight.cginc"
#include "ParelVRFog.cginc"

sampler2D _MainTex;
float4 _MainTex_ST;
fixed4 _Color;
half _Cutoff;
sampler2D _BumpMap;
half _BumpScale;
half _Shininess;
sampler2D _EmissionMap;
half4 _EmissionColor;
// _SpecColor comes from UnityLightingCommon.cginc.

struct appdata
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float2 texcoord : TEXCOORD0;
    float2 texcoord1 : TEXCOORD1;
    float2 texcoord2 : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2f
{
    UNITY_POSITION(pos);
    float2 uv : TEXCOORD0;
    float3 worldPos : TEXCOORD1;
    half3 worldNormal : TEXCOORD2;
    half4 worldTangent : TEXCOORD3;
    half4 lmap : TEXCOORD4;
    UNITY_LIGHTING_COORDS(5, 6)
    UNITY_FOG_COORDS(7)
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

v2f vertSimple(appdata v)
{
    v2f o;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_OUTPUT(v2f, o);
    UNITY_TRANSFER_INSTANCE_ID(v, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    o.pos = UnityObjectToClipPos(v.vertex);
    o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
    o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
    o.worldNormal = UnityObjectToWorldNormal(v.normal);
    o.worldTangent = half4(UnityObjectToWorldDir(v.tangent.xyz), v.tangent.w * unity_WorldTransformParams.w);

    o.lmap = 0;
#if defined(PARELVR_PASS_BASE)
    #ifdef LIGHTMAP_ON
        o.lmap.xy = v.texcoord1.xy * unity_LightmapST.xy + unity_LightmapST.zw;
    #elif UNITY_SHOULD_SAMPLE_SH
        #ifdef VERTEXLIGHT_ON
            o.lmap.rgb = Shade4PointLights(unity_4LightPosX0, unity_4LightPosY0, unity_4LightPosZ0,
                unity_LightColor[0].rgb, unity_LightColor[1].rgb, unity_LightColor[2].rgb, unity_LightColor[3].rgb,
                unity_4LightAtten0, o.worldPos, o.worldNormal);
        #endif
        o.lmap.rgb = ShadeSHPerVertex(o.worldNormal, o.lmap.rgb);
    #endif
    #ifdef DYNAMICLIGHTMAP_ON
        o.lmap.zw = v.texcoord2.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
    #endif
#endif

    UNITY_TRANSFER_LIGHTING(o, v.texcoord1.xy);
    UNITY_TRANSFER_FOG(o, o.pos);
    return o;
}

half3 ParelBlinnPhong(half3 albedo, half gloss, half3 n, half3 viewDir, half3 lightDir, half3 lightColor)
{
    half3 h = normalize(lightDir + viewDir);
    half diffuse = max(0, dot(n, lightDir));
    float nh = max(0, dot(n, h));
    float spec = pow(nh, max(_Shininess, 0.01) * 128.0) * gloss;
    return albedo * lightColor * diffuse + lightColor * _SpecColor.rgb * spec;
}

fixed4 fragSimple(v2f i) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    fixed4 tex = tex2D(_MainTex, i.uv) * _Color;
#if defined(_ALPHATEST_ON)
    clip(tex.a - _Cutoff);
#endif

    half3 n = normalize(i.worldNormal);
#if defined(_NORMALMAP)
    half3 t = normalize(i.worldTangent.xyz);
    half3 b = cross(n, t) * i.worldTangent.w;
    half3 tn = UnpackScaleNormal(tex2D(_BumpMap, i.uv), _BumpScale);
    n = normalize(tn.x * t + tn.y * b + tn.z * n);
#endif

#if defined(_GLOSS_FROM_ALBEDO_ALPHA)
    half gloss = tex.a;
#else
    half gloss = 1;
#endif
#if defined(_SPECULARHIGHLIGHTS_OFF)
    gloss = 0;
#endif

    half3 viewDir = normalize(UnityWorldSpaceViewDir(i.worldPos));
    UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
    fixed4 c = 0;

#if defined(PARELVR_PASS_BASE)
    UnityGIInput giInput;
    UNITY_INITIALIZE_OUTPUT(UnityGIInput, giInput);
    giInput.light.color = _LightColor0.rgb;
    giInput.light.dir = _WorldSpaceLightPos0.xyz;
    giInput.worldPos = i.worldPos;
    giInput.worldViewDir = viewDir;
    giInput.atten = atten;
    #if defined(LIGHTMAP_ON) || defined(DYNAMICLIGHTMAP_ON)
        giInput.lightmapUV = i.lmap;
    #else
        giInput.lightmapUV = 0.0;
    #endif
    #if UNITY_SHOULD_SAMPLE_SH && !UNITY_SAMPLE_FULL_SH_PER_PIXEL
        giInput.ambient = i.lmap.rgb;
    #else
        giInput.ambient.rgb = 0.0;
    #endif
    UnityGI gi = UnityGlobalIllumination(giInput, 1.0, n);
    c.rgb = ParelBlinnPhong(tex.rgb, gloss, n, viewDir, gi.light.dir, gi.light.color) + tex.rgb * gi.indirect.diffuse;
    #if defined(_EMISSION)
        c.rgb += tex2D(_EmissionMap, i.uv).rgb * _EmissionColor.rgb;
    #endif
#else
    half3 lightDir = normalize(UnityWorldSpaceLightDir(i.worldPos));
    c.rgb = ParelBlinnPhong(tex.rgb, gloss, n, viewDir, lightDir, _LightColor0.rgb * atten);
#endif

    c.a = tex.a;
    UNITY_APPLY_FOG(i.fogCoord, c);
#if defined(PARELVR_PASS_ADD)
    c.rgb = ParelApplyFogAdditive(c.rgb, i.worldPos);
#elif defined(_ALPHAPREMULTIPLY_ON)
    c.rgb = ParelApplyFog(c.rgb, i.worldPos, c.a);
#else
    c.rgb = ParelApplyFog(c.rgb, i.worldPos, 1.0h);
#endif
#if !defined(_ALPHABLEND_ON) && !defined(_ALPHAPREMULTIPLY_ON)
    UNITY_OPAQUE_ALPHA(c.a);
#endif
    return c;
}

#endif
