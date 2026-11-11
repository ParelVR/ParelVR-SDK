// ParelVR Water: HDRP's Water Surface for the Built-in renderer.
//  - Waves: 8 Gerstner waves worked out at upload from HDRP's wind speed, chaos, orientation, repetition size and
//    band multipliers (ocean, river or pool), plus two scrolling ripple normal maps (ripple wind).
//  - Reflections: the reflection probe, the sun's highlight (GGX) and ParelVR screen-space reflections when on.
//  - Refraction: what's under the surface (the opaque picture ParelOpaqueCapture keeps), bent by the waves, absorbed
//    and scattered by depth with HDRP's refraction colour, absorption distance and scattering colour.
//  - Foam on wave crests and along shores, caustics on the floor seen through the water, a Snell's window from below.
//  - Deformers (HDRP Water Deformer: sphere, box, shore wave, bow wave) and excluders (stencil) carried over.
// Without the opaque picture (standalone headsets) the water is drawn see-through by its fresnel and scattering colour.
Shader "ParelVR/Water"
{
    Properties
    {
        [Header(Surface)]
        _ScatteringColor ("Scattering Colour", Color) = (0.0, 0.25, 0.3, 1)
        _RefractionColor ("Refraction Colour", Color) = (0.1, 0.5, 0.5, 1)
        _AbsorptionDistance ("Absorption Distance (m)", Float) = 5
        _MaxRefractionDistance ("Max Refraction Distance (m)", Float) = 1
        _AmbientScattering ("Ambient Scattering", Range(0, 1)) = 0.2
        _HeightScattering ("Height Scattering", Range(0, 1)) = 0.2
        _TipScattering ("Direct Light Tip Scattering", Range(0, 1)) = 0.6
        _BodyScattering ("Direct Light Body Scattering", Range(0, 1)) = 0.5
        _Smoothness ("Smoothness (near)", Range(0, 1)) = 0.95
        _SmoothnessFar ("Smoothness (far)", Range(0, 1)) = 0.8
        _SmoothnessFadeStart ("Smoothness Fade Start (m)", Float) = 100
        _SmoothnessFadeDistance ("Smoothness Fade Distance (m)", Float) = 500

        [Header(Waves)]
        _Wave0 ("Wave 0 (dir xy, amplitude, wavelength)", Vector) = (1, 0, 0.3, 20)
        _Wave1 ("Wave 1", Vector) = (0.8, 0.6, 0.2, 12)
        _Wave2 ("Wave 2", Vector) = (0.6, -0.8, 0.12, 7)
        _Wave3 ("Wave 3", Vector) = (0.9, 0.4, 0.08, 4.5)
        _Wave4 ("Wave 4", Vector) = (0.3, 0.95, 0.05, 2.8)
        _Wave5 ("Wave 5", Vector) = (0.95, -0.3, 0.03, 1.7)
        _Wave6 ("Wave 6", Vector) = (-0.2, 0.98, 0.02, 1.1)
        _Wave7 ("Wave 7", Vector) = (0.7, 0.7, 0.012, 0.7)
        _WaveSteepness ("Wave Steepness", Range(0, 1)) = 0.6
        _WaveFadeDistance ("Small Waves Fade Distance (m)", Float) = 300
        _Current ("Current (xz m/s)", Vector) = (0, 0, 0, 0)

        [Header(Ripples)]
        [NoScaleOffset][Normal] _RippleNormal ("Ripple Normal Map", 2D) = "bump" {}
        _RippleScale ("Ripple Tile Size (m)", Float) = 6
        _RippleWind ("Ripple Wind (xy dir × m/s, z strength)", Vector) = (0.6, 0.3, 0.6, 0)

        [Header(Foam)]
        [NoScaleOffset] _FoamMap ("Foam (r) / Caustics (g)", 2D) = "black" {}
        _FoamColor ("Foam Colour", Color) = (0.95, 0.97, 1, 1)
        _FoamAmount ("Crest Foam", Range(0, 1)) = 0.3
        _ShoreFoamDistance ("Shore Foam Distance (m)", Float) = 0.6
        _FoamScale ("Foam Tile Size (m)", Float) = 8

        [Header(Caustics)]
        _CausticsIntensity ("Caustics Intensity", Float) = 0.5
        _CausticsScale ("Caustics Tile Size (m)", Float) = 4
        _CausticsDepthFade ("Caustics Fade Depth (m)", Float) = 10

        [Header(Deformers)]
        _DeformCount ("Count", Float) = 0
        _Deform0 ("Deformer 0 (pos xyz, type)", Vector) = (0, 0, 0, 0)
        _DeformSize0 ("Deformer 0 (size xz, amplitude, rotation)", Vector) = (1, 1, 0, 0)
        _Deform1 ("Deformer 1", Vector) = (0, 0, 0, 0)
        _DeformSize1 ("Deformer 1 size", Vector) = (1, 1, 0, 0)
        _Deform2 ("Deformer 2", Vector) = (0, 0, 0, 0)
        _DeformSize2 ("Deformer 2 size", Vector) = (1, 1, 0, 0)
        _Deform3 ("Deformer 3", Vector) = (0, 0, 0, 0)
        _DeformSize3 ("Deformer 3 size", Vector) = (1, 1, 0, 0)
    }

    SubShader
    {
        Tags { "Queue"="Transparent-100" "RenderType"="Transparent" "IgnoreProjector"="True" "ForceNoShadowCasting"="True" }

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Cull Off
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil { Ref 128 ReadMask 128 Comp NotEqual }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma multi_compile_fwdbase nolightmap nodynlightmap nodirlightmap
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"
            #define PARELVR_TRANSPARENT 1
            #include "ParelVRFog.cginc"

            half4 _ScatteringColor, _RefractionColor, _FoamColor;
            float _AbsorptionDistance, _MaxRefractionDistance, _AmbientScattering, _HeightScattering, _TipScattering, _BodyScattering;
            float _Smoothness, _SmoothnessFar, _SmoothnessFadeStart, _SmoothnessFadeDistance;
            float4 _Wave0, _Wave1, _Wave2, _Wave3, _Wave4, _Wave5, _Wave6, _Wave7;
            float _WaveSteepness, _WaveFadeDistance;
            float4 _Current;
            sampler2D _RippleNormal;
            float _RippleScale;
            float4 _RippleWind;
            sampler2D _FoamMap;
            float _FoamAmount, _ShoreFoamDistance, _FoamScale;
            float _CausticsIntensity, _CausticsScale, _CausticsDepthFade;
            float _DeformCount;
            float4 _Deform0, _Deform1, _Deform2, _Deform3, _DeformSize0, _DeformSize1, _DeformSize2, _DeformSize3;

            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                UNITY_DECLARE_TEX2DARRAY(_ParelOpaqueTexture);
                #define OPAQUE_LOD(uv, lod) UNITY_SAMPLE_TEX2DARRAY_LOD(_ParelOpaqueTexture, float3(uv, unity_StereoEyeIndex), lod)
            #else
                sampler2D _ParelOpaqueTexture;
                #define OPAQUE_LOD(uv, lod) tex2Dlod(_ParelOpaqueTexture, float4(uv, 0, lod))
            #endif
            float4 _ParelOpaqueOn;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float4 screen : TEXCOORD2;
                float crest : TEXCOORD3;      // how high on a wave crest this point is (foam, tip scattering)
                float2 flatXZ : TEXCOORD4;    // where on the flat surface (foam / ripples follow the water, not the waves)
                UNITY_VERTEX_OUTPUT_STEREO
            };

            static const float G = 9.81;

            void Gerstner(float4 w, float2 xz, float t, float fade, inout float3 offset, inout float3 tangent, inout float3 binormal, inout float crest)
            {
                float k = 2.0 * UNITY_PI / max(w.w, 0.01);
                float c = sqrt(G / k);
                float2 d = normalize(w.xy);
                float a = w.z * fade;
                // Gerstner steepness shared by the 8 waves so crests sharpen without looping over themselves.
                float q = saturate(_WaveSteepness) / (k * max(a, 1e-4) * 8.0);
                float f = k * (dot(d, xz) - c * t);
                float s = sin(f), co = cos(f);
                offset += float3(q * a * d.x * co, a * s, q * a * d.y * co);
                tangent += float3(-q * d.x * d.x * k * a * s, d.x * k * a * co, -q * d.x * d.y * k * a * s);
                binormal += float3(-q * d.x * d.y * k * a * s, d.y * k * a * co, -q * d.y * d.y * k * a * s);
                crest += s * a;
            }

            float Deform(float4 p, float4 size, float2 xz, float t)
            {
                float type = p.w;
                float2 local = xz - p.xz;
                float rot = size.w;
                float2 r = float2(cos(rot) * local.x - sin(rot) * local.y, sin(rot) * local.x + cos(rot) * local.y);
                float2 halfSize = max(size.xy * 0.5, 0.01);
                if (type < 0.5) return 0.0;
                if (type < 1.5) // sphere: a smooth bump (negative amplitude: a dip)
                {
                    float d = length(r / halfSize);
                    return size.z * smoothstep(1.0, 0.0, d);
                }
                if (type < 2.5) // box: raised or lowered water with soft edges
                {
                    float2 e = saturate((halfSize - abs(r)) / (halfSize * 0.2));
                    return size.z * e.x * e.y;
                }
                if (type < 3.5) // shore wave: crests running along the box towards the shore (local +y)
                {
                    float2 e = saturate((halfSize - abs(r)) / (halfSize * 0.2));
                    float wl = max(halfSize.y * 0.5, 0.5);
                    float wave = pow(saturate(sin((r.y / wl - t * 0.15) * 2.0 * UNITY_PI) * 0.5 + 0.5), 3.0);
                    return size.z * wave * e.x * e.y;
                }
                // bow wave: a V-shaped ridge trailing behind (local -y)
                float along = -r.y / halfSize.y;
                float spread = abs(r.x) / halfSize.x - along * 0.6;
                return along > 0.0 && along < 1.0 ? size.z * exp(-spread * spread * 30.0) * (1.0 - along) : 0.0;
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float t = _Time.y;
                float2 xz = wp.xz - _Current.xz * t;
                float dist = distance(wp, _WorldSpaceCameraPos);
                float3 offset = 0, tangent = float3(1, 0, 0), binormal = float3(0, 0, 1);
                float crest = 0;
                // Long waves everywhere, short ones fade with distance so they don't shimmer.
                float fadeShort = saturate(1.0 - dist / max(_WaveFadeDistance, 1.0));
                Gerstner(_Wave0, xz, t, 1.0, offset, tangent, binormal, crest);
                Gerstner(_Wave1, xz, t, 1.0, offset, tangent, binormal, crest);
                Gerstner(_Wave2, xz, t, 1.0, offset, tangent, binormal, crest);
                Gerstner(_Wave3, xz, t, lerp(0.5, 1.0, fadeShort), offset, tangent, binormal, crest);
                Gerstner(_Wave4, xz, t, fadeShort, offset, tangent, binormal, crest);
                Gerstner(_Wave5, xz, t, fadeShort, offset, tangent, binormal, crest);
                Gerstner(_Wave6, xz, t, fadeShort, offset, tangent, binormal, crest);
                Gerstner(_Wave7, xz, t, fadeShort, offset, tangent, binormal, crest);
                float deform = 0;
                if (_DeformCount > 0.5) deform += Deform(_Deform0, _DeformSize0, wp.xz, t);
                if (_DeformCount > 1.5) deform += Deform(_Deform1, _DeformSize1, wp.xz, t);
                if (_DeformCount > 2.5) deform += Deform(_Deform2, _DeformSize2, wp.xz, t);
                if (_DeformCount > 3.5) deform += Deform(_Deform3, _DeformSize3, wp.xz, t);
                wp += offset + float3(0, deform, 0);
                o.worldPos = wp;
                o.normal = normalize(cross(binormal, tangent));
                o.pos = UnityWorldToClipPos(wp);
                o.screen = ComputeScreenPos(o.pos);
                float amp = _Wave0.z + _Wave1.z + _Wave2.z + _Wave3.z;
                o.crest = crest / max(amp, 1e-3) + deform * 0.5;
                o.flatXZ = xz;
                return o;
            }

            half3 RippleNormal(float2 xz)
            {
                float2 wind = _RippleWind.xy;
                float2 uv1 = xz / max(_RippleScale, 0.01) + wind * _Time.y * 0.05;
                float2 uv2 = xz / max(_RippleScale * 0.37, 0.01) - wind.yx * _Time.y * 0.08;
                half3 n1 = UnpackNormal(tex2D(_RippleNormal, uv1));
                half3 n2 = UnpackNormal(tex2D(_RippleNormal, uv2));
                half3 n = normalize(half3(n1.xy + n2.xy, n1.z * n2.z));
                n.xy *= _RippleWind.z;
                return normalize(half3(n.x, n.z, n.y));   // tangent space (xy) to world (xz)
            }

            float GGX(float NdotH, float roughness)
            {
                float a2 = roughness * roughness * roughness * roughness;
                float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
                return a2 / (UNITY_PI * d * d + 1e-7);
            }

            half4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                bool below = facing < 0;
                float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
                float dist = distance(_WorldSpaceCameraPos, i.worldPos);
                half3 rip = RippleNormal(i.flatXZ);
                float3 N = normalize(i.normal + float3(rip.x, 0, rip.z) * saturate(1.0 - dist / 200.0));
                if (below) N = -N;
                float NdotV = saturate(dot(N, V));
                float smooth = lerp(_Smoothness, _SmoothnessFar, saturate((dist - _SmoothnessFadeStart) / max(_SmoothnessFadeDistance, 1.0)));
                float rough = 1.0 - smooth;

                // Reflection: probe + sun highlight.
                float3 R = reflect(-V, N);
                if (!below) R.y = abs(R.y);
                half4 probe = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, R, rough * 6.0);
                half3 reflection = DecodeHDR(probe, unity_SpecCube0_HDR);
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 H = normalize(L + V);
                half3 sun = _LightColor0.rgb * GGX(saturate(dot(N, H)), max(rough, 0.03)) * saturate(dot(N, L)) * UNITY_PI * 0.25;
                float fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);
                if (below) fresnel = NdotV < 0.66 ? 1.0 : fresnel;   // beyond Snell's window: total internal reflection

                // What the water's light looks like: ambient sky light scattered in it, the sun through the crests.
                half3 ambient = ShadeSH9(float4(0, 1, 0, 1));
                float tip = saturate(i.crest) * _TipScattering * pow(saturate(dot(V, -L) * 0.5 + 0.5), 4.0);
                half3 scatter = _ScatteringColor.rgb * (ambient * (_AmbientScattering + 0.5) + _LightColor0.rgb * (_BodyScattering * 0.25 + tip) * (1.0 + saturate(i.crest) * _HeightScattering));

                float2 uv = i.screen.xy / i.screen.w;
                float surfaceDepth = i.screen.w;
                half3 under = scatter;
                float alpha = 1.0;
                float thickness = 1e3;
                float3 floorPos = i.worldPos;
                if (_ParelOpaqueOn.x > 0.5)
                {
                    float2 bend = N.xz * _MaxRefractionDistance * 0.08 / max(surfaceDepth * 0.1, 1.0);
                    float2 ruv = uv + bend;
                    float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, ruv));
                    if (sceneDepth < surfaceDepth) { ruv = uv; sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv)); }
                    thickness = max(sceneDepth - surfaceDepth, 0.0);
                    float3 viewRay = (i.worldPos - _WorldSpaceCameraPos) / surfaceDepth;
                    floorPos = _WorldSpaceCameraPos + viewRay * sceneDepth;
                    half3 seen = OPAQUE_LOD(ruv, saturate(thickness / 8.0) * rough * 4.0).rgb;

                    // Caustics: light focused by the waves onto the floor, fading with depth.
                    float floorDepth = max(i.worldPos.y - floorPos.y, 0.0);
                    float2 cuv = floorPos.xz / max(_CausticsScale, 0.01);
                    half c1 = tex2D(_FoamMap, cuv + _Time.y * float2(0.03, 0.02)).g;
                    half c2 = tex2D(_FoamMap, cuv * 1.3 - _Time.y * float2(0.02, 0.035)).g;
                    half caustics = min(c1, c2) * _CausticsIntensity * saturate(1.0 - floorDepth / max(_CausticsDepthFade, 0.01)) * saturate(floorDepth * 4.0);
                    seen += seen * caustics * _LightColor0.rgb * 4.0 * saturate(L.y);

                    // Absorbed and scattered with depth (HDRP: refraction colour reached at the absorption distance).
                    float3 absorb = -log(max(_RefractionColor.rgb, 0.01)) / max(_AbsorptionDistance, 0.01);
                    half3 tr = exp(-absorb * thickness);
                    under = seen * tr + scatter * (1.0 - tr);
                }
                else
                {
                    alpha = lerp(0.75, 1.0, fresnel);
                }

                half3 col = lerp(under, reflection, fresnel) + sun * (below ? 0.0 : 1.0);

                // Foam on the crests and along the shore.
                float foamTex = tex2D(_FoamMap, i.flatXZ / max(_FoamScale, 0.01)).r;
                float crestFoam = saturate((i.crest - (1.0 - _FoamAmount)) * 3.0);
                float shoreFoam = _ShoreFoamDistance > 0.0 ? saturate(1.0 - thickness / _ShoreFoamDistance) : 0.0;
                float foam = saturate((crestFoam + shoreFoam) * foamTex * 1.5);
                half3 foamLight = _FoamColor.rgb * (ambient + _LightColor0.rgb * saturate(dot(N, L)));
                col = lerp(col, foamLight, foam);
                alpha = max(alpha, foam);

                col = ParelApplyFog(col, i.worldPos, 1.0h);
                return half4(col, alpha);
            }
            ENDCG
        }
    }
    Fallback Off
}
