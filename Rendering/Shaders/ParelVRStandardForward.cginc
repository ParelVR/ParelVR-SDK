#ifndef PARELVR_STANDARD_FORWARD_INCLUDED
#define PARELVR_STANDARD_FORWARD_INCLUDED

// Unity's Standard forward passes with HDRP's extra features (ParelVRLitExtras.cginc) and ParelVR volumetric fog. The
// base lighting is Unity's own BRDF (UnityStandardCore); ParelVR adds to it: screen-space reflections replace the probe
// reflection where they find something, screen-space GI adds bounced light, contact and micro shadows shade the sun,
// clear coat / iridescence / anisotropy / area lights / refraction come from the material and the world.

#include "UnityStandardCoreForward.cginc"
#include "ParelVRFog.cginc"
#include "ParelVRLitExtras.cginc"

half _DstBlend;

half4 parelFragBaseInternal(VertexOutputForwardBase i, half facing)
{
    FRAGMENT_SETUP(s)

    UNITY_SETUP_INSTANCE_ID(i);
    UNITY_APPLY_DITHER_CROSSFADE(i.pos.xy);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

    // Double-sided normal modes (HDRP): back faces flip or mirror their normal.
    if (facing < 0.0 && _ParelDoubleSidedNormals > 0.5)
    {
        float3 geo = normalize(i.tangentToWorldAndPackedData[2].xyz);
        s.normalWorld = _ParelDoubleSidedNormals < 1.5 ? -s.normalWorld : reflect(s.normalWorld, geo);
    }

    // Geometric specular anti-aliasing (HDRP): bumpy normals far away widen the highlight instead of sparkling.
    if (_ParelSpecularAA.x > 0.5)
    {
        float3 dn = abs(ddx(s.normalWorld)) + abs(ddy(s.normalWorld));
        float variance = _ParelSpecularAA.y * dot(dn, dn);
        float rough = 1.0 - s.smoothness;
        rough = sqrt(saturate(rough * rough + min(variance, _ParelSpecularAA.z)));
        s.smoothness = 1.0 - rough;
    }

    float3 V = -s.eyeVec;
    half NdotV = saturate(dot(s.normalWorld, V));
    s.specColor = ParelIridescence(s.specColor, NdotV);

    UnityLight mainLight = MainLight();
    UNITY_LIGHT_ATTENUATION(atten, i, s.posWorld);
    half occlusion = Occlusion(i.tex.xy);
    half NdotL = dot(s.normalWorld, mainLight.dir);
    atten *= ParelMicroShadow(occlusion, NdotL) * ParelContactShadow(s.posWorld, mainLight.dir, i.pos.xy);

    UnityGI gi = FragmentGI(s, occlusion, i.ambientOrLightmapUV, atten, mainLight);

    half ssrWeight;
    half3 ssr = ParelSSR(s.posWorld, s.normalWorld, V, s.smoothness, i.pos.xy, ssrWeight);
    gi.indirect.specular = lerp(gi.indirect.specular, ssr * occlusion, ssrWeight);
    half ssgiWeight;
    half3 ssgi = ParelSSGI(s.posWorld, s.normalWorld, i.pos.xy, ssgiWeight);
    gi.indirect.diffuse += ssgi * occlusion * ssgiWeight;

    half4 c = UNITY_BRDF_PBS(s.diffColor, s.specColor, s.oneMinusReflectivity, s.smoothness, s.normalWorld, V, gi.light, gi.indirect);

    // Anisotropic highlight (HDRP): the round highlight is swapped for one stretched along the tangent.
    if (abs(_ParelAnisotropy) > 0.01)
    {
        float3 T = i.tangentToWorldAndPackedData[0].xyz;
        T = dot(T, T) > 1e-4 ? normalize(T - s.normalWorld * dot(T, s.normalWorld)) : normalize(cross(s.normalWorld, float3(0, 1, 0.01)));
        float3 B = cross(s.normalWorld, T);
        float rough = max(1.0 - s.smoothness, 0.04);
        float r2 = rough * rough;
        float ax = max(r2 * (1.0 + _ParelAnisotropy), 0.002), ay = max(r2 * (1.0 - _ParelAnisotropy), 0.002);
        float3 H = normalize(mainLight.dir + V);
        float nl = saturate(NdotL);
        half3 F = s.specColor + (1.0 - s.specColor) * pow(1.0 - saturate(dot(mainLight.dir, H)), 5.0);
        float vis = 0.25 / max(nl * NdotV, 1e-3) * nl * NdotV;   // simple height-correlated approximation
        half3 aniso = F * (ParelAnisoGGX(H, s.normalWorld, T, B, ax, ay) - ParelGGX(saturate(dot(s.normalWorld, H)), rough)) * vis * nl * UNITY_PI;
        c.rgb += max(aniso * gi.light.color, -c.rgb * 0.5);
    }

    // Clear coat (HDRP): a smooth varnish over the surface, its own reflection and highlight on top.
    UNITY_BRANCH
    if (_ParelCoatMask > 0.001)
    {
        half mask = _ParelCoatMask * tex2D(_ParelCoatMap, i.tex.xy).r;
        half Fc = 0.04 + 0.96 * pow(1.0 - NdotV, 5.0);
        float3 Rc = reflect(-V, i.tangentToWorldAndPackedData[2].xyz);
        half coatRough = 1.0 - _ParelCoatSmoothness;
        half3 env = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, Rc, coatRough * 6.0), unity_SpecCube0_HDR) * occlusion;
        float3 Hc = normalize(mainLight.dir + V);
        half3 direct = gi.light.color * ParelGGX(saturate(dot(i.tangentToWorldAndPackedData[2].xyz, Hc)), max(coatRough, 0.03)) * saturate(NdotL) * Fc * 0.25 * UNITY_PI;
        c.rgb = c.rgb * (1.0 - Fc * mask) + mask * (env * Fc + direct);
    }

    c.rgb += ParelAreaLights(s.diffColor, s.specColor, s.smoothness, s.normalWorld, V, s.posWorld);
    c.rgb += Emission(i.tex.xy);

    UNITY_EXTRACT_FOG_FROM_EYE_VEC(i);
    UNITY_APPLY_FOG(_unity_fogCoord, c.rgb);
    half4 o = OutputForward(c, s.alpha);

    // Refraction (HDRP): the background seen through the surface, bent by it, coloured by what it absorbs.
    UNITY_BRANCH
    if (_ParelRefraction > 0.5 && _ParelOpaqueOn.x > 0.5)
    {
        float2 uv = i.pos.xy / _ScreenParams.xy;
        float3 viewN = mul((float3x3)UNITY_MATRIX_V, s.normalWorld);
        float thickness = _ParelRefraction > 2.5 ? 0.0 : _ParelRefractionThickness;   // thin: no bending
        float bend = (1.0 - 1.0 / max(_ParelIor, 1.0)) * (_ParelRefraction > 1.5 ? 2.0 : 1.0) * (0.05 + thickness);
        float depth = i.pos.w;
        float2 ruv = saturate(uv - viewN.xy * bend / max(depth, 0.3) * 2.0);
        half3 behind = PARELVR_OPAQUE_LOD(ruv, (1.0 - s.smoothness) * 5.0).rgb;
        float3 absorb = -log(max(_ParelTransmittanceColor.rgb, 0.01)) / max(_ParelAbsorptionDistance, 0.01);
        half3 through = behind * exp(-absorb * max(thickness, 0.01)) * _ParelTransmittanceColor.a;
        #if defined(_ALPHAPREMULTIPLY_ON)
            o.rgb = o.rgb + through * (1.0 - o.a);
        #else
            o.rgb = o.rgb * o.a + through * (1.0 - o.a);
        #endif
        o.a = 1.0;
    }
    return o;
}

half4 parelFragBase(VertexOutputForwardBase i, half facing : VFACE) : SV_Target
{
    half4 c = parelFragBaseInternal(i, facing);
    float3 posWorld = IN_WORLDPOS(i);
    if (_ParelNoFog > 0.5) return c;
    if (_DstBlend == 1.0h) c.rgb = ParelApplyFogAdditiveSurface(c.rgb, posWorld);     // additive surfaces only dim
    else
    {
    #if defined(_ALPHAPREMULTIPLY_ON)
        c.rgb = ParelApplyFog(c.rgb, posWorld, c.a);
    #else
        c.rgb = ParelApplyFog(c.rgb, posWorld, 1.0h);
    #endif
    }
    return c;
}

half4 parelFragAdd(VertexOutputForwardAdd i) : SV_Target
{
    half4 c = fragForwardAddInternal(i);
    if (_ParelNoFog < 0.5) c.rgb = ParelApplyFogAdditive(c.rgb, IN_WORLDPOS_FWDADD(i));
    return c;
}

#endif
