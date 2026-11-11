// ParelVR Sky: HDRP's Physically Based Sky, Gradient Sky and HDRI Sky, with its Cloud Layer and Volumetric Clouds,
// in one skybox shader for the Built-in renderer.
//  - Physically based: single scattering through an air (Rayleigh) and aerosol (Mie) atmosphere around a planet, the
//    sun's light dimmed through it (sunsets redden by themselves), ground tint, HDRP's horizon / zenith tints, sun disk.
//  - Cloud Layer: HDRP's cloud map (lat-long, 4 channels), opacity per channel, tint, rotation, wind scroll, lit by the sun.
//  - Volumetric clouds: drawn by ParelSkyClouds into a panorama the sky reads (_ParelCloudPanorama), so they cost a
//    texture read here, the same in a headset.
//  - The world's fog covers the sky (ParelVRFog.cginc).
Shader "ParelVR/Sky"
{
    Properties
    {
        [Enum(Physically Based,0,Gradient,1,Cubemap,2)] _SkyMode ("Sky", Float) = 0
        _Exposure ("Exposure (linear)", Float) = 1

        [Header(Physically Based)]
        _PlanetRadius ("Planet Radius (km)", Float) = 6378
        _AirDensity ("Air Density (RGB, per km at sea level)", Vector) = (0.0058, 0.0135, 0.0331, 0)
        _AirTint ("Air Tint", Color) = (1, 1, 1, 1)
        _AirScaleHeight ("Air Scale Height (km)", Float) = 8
        _AerosolDensity ("Aerosol Density (per km)", Float) = 0.01
        _AerosolTint ("Aerosol Tint", Color) = (0.9, 0.9, 0.9, 1)
        _AerosolScaleHeight ("Aerosol Scale Height (km)", Float) = 1.2
        _AerosolAnisotropy ("Aerosol Anisotropy", Range(-0.99, 0.99)) = 0.76
        _GroundTint ("Ground Tint", Color) = (0.4, 0.25, 0.15, 1)
        _HorizonTint ("Horizon Tint", Color) = (1, 1, 1, 1)
        _ZenithTint ("Zenith Tint", Color) = (1, 1, 1, 1)
        _HorizonZenithShift ("Horizon Zenith Shift", Range(-1, 1)) = 0
        _ColorSaturation ("Saturation", Range(0, 1)) = 1
        _SunDiskSize ("Sun Angular Diameter (deg)", Float) = 0.53
        _SunDiskIntensity ("Sun Disk Brightness", Float) = 1

        [Header(Gradient)]
        _Top ("Top", Color) = (0.2, 0.4, 0.8, 1)
        _Middle ("Middle", Color) = (0.6, 0.7, 0.8, 1)
        _Bottom ("Bottom", Color) = (0.3, 0.3, 0.3, 1)
        _GradientDiffusion ("Gradient Diffusion", Float) = 1

        [Header(Cubemap)]
        [NoScaleOffset] _Tex ("Cubemap (HDR)", Cube) = "grey" {}
        _Rotation ("Rotation", Range(0, 360)) = 0
        _Tint ("Tint", Color) = (1, 1, 1, 1)

        [Header(Cloud Layer)]
        [Toggle] _CloudLayerOn ("Cloud Layer", Float) = 0
        [NoScaleOffset] _CloudMap ("Cloud Map (lat-long, 4 layers)", 2D) = "black" {}
        _CloudOpacity ("Opacity R G B A", Vector) = (1, 0, 0, 0)
        _CloudTint ("Tint", Color) = (1, 1, 1, 1)
        _CloudExposure ("Brightness", Float) = 1
        _CloudRotation ("Rotation", Range(0, 360)) = 0
        _CloudScroll ("Wind (xy direction × speed, km/h)", Vector) = (0, 0, 0, 0)
        _CloudThickness ("Thickness (lighting)", Range(0, 1)) = 0.5
        [Toggle] _CloudUpperOnly ("Upper Hemisphere Only", Float) = 1
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "ParelVRFog.cginc"

            float _SkyMode, _Exposure;
            float _PlanetRadius, _AirScaleHeight, _AerosolDensity, _AerosolScaleHeight, _AerosolAnisotropy;
            float4 _AirDensity;
            half4 _AirTint, _AerosolTint, _GroundTint, _HorizonTint, _ZenithTint;
            float _HorizonZenithShift, _ColorSaturation, _SunDiskSize, _SunDiskIntensity;
            half4 _Top, _Middle, _Bottom;
            float _GradientDiffusion;
            samplerCUBE _Tex;
            half4 _Tex_HDR;
            float _Rotation;
            half4 _Tint;
            float _CloudLayerOn, _CloudExposure, _CloudRotation, _CloudThickness, _CloudUpperOnly;
            sampler2D _CloudMap;
            float4 _CloudOpacity, _CloudScroll;
            half4 _CloudTint;

            sampler2D _ParelCloudPanorama;   // rgb light from the volumetric clouds, a transmittance (upper hemisphere)
            float4 _ParelCloudsOn;           // x on

            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            static const float PI = 3.14159265;

            float RayleighPhase(float c) { return 3.0 / (16.0 * PI) * (1.0 + c * c); }
            float MiePhase(float c, float g)
            {
                // Cornette-Shanks, as HDRP.
                float g2 = g * g;
                return 3.0 / (8.0 * PI) * (1.0 - g2) * (1.0 + c * c) / ((2.0 + g2) * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5));
            }

            // Distance through the atmosphere top along a ray from height h (km above ground) going up at mu = cos(zenith).
            float RayToTop(float r, float mu, float top)
            {
                float b = r * mu;
                float disc = b * b - r * r + top * top;
                return -b + sqrt(max(disc, 0.0));
            }

            // Optical depth of an exponential layer from height h along mu (Chapman approximation, Schüler 2012).
            float Chapman(float h, float mu, float H, float R)
            {
                float x = (R + h) / H;
                float c = sqrt(x * 0.5 * PI) ;
                float ch;
                if (mu >= 0.0) ch = c / ((c - 1.0) * mu + 1.0);
                else
                {
                    float c2 = c * exp(x - x * sqrt(max(1.0 - mu * mu, 0.0)));
                    ch = 2.0 * c2 - c / ((c - 1.0) * (-mu) + 1.0);
                    ch = max(ch, 0.0);
                }
                return H * exp(-h / H) * ch;
            }

            float3 AirBeta() { return _AirDensity.rgb * _AirTint.rgb; }
            // Ozone absorbs orange and green light high up (Chappuis band): why the sky overhead stays blue at sunset.
            static const float3 OZONE = float3(0.00065, 0.001881, 0.000085); // per km at the layer's peak
            float OzoneDensity(float h) { return max(0.0, 1.0 - abs(h - 25.0) / 15.0); }
            float AerosolBeta() { return _AerosolDensity; }

            // Transmittance from a point at height h (km) towards a direction with cos(zenith) mu, to the top of the
            // atmosphere: integrated in 6 steps (denser near the start, where the air is). 0 when the planet is in the way.
            float3 Transmittance(float h, float mu)
            {
                float R = _PlanetRadius;
                float r = R + max(h, 0.0);
                float top = R + max(max(_AirScaleHeight, _AerosolScaleHeight) * 8.0, 60.0);
                float b = r * mu;
                float discG = b * b - r * r + R * R;
                if (mu < 0.0 && discG > 0.0) return 0.0;
                float len = -b + sqrt(max(b * b - r * r + top * top, 0.0));
                float3 tau = 0;
                float prev = 0;
                [unroll] for (int i = 1; i <= 6; i++)
                {
                    float f = i / 6.0;
                    float t = len * f * f;
                    float tm = 0.5 * (t + prev);
                    float hs = length(float3(0, r, 0) + float3(sqrt(max(1.0 - mu * mu, 0.0)), mu, 0) * tm) - R;
                    tau += (AirBeta() * exp(-hs / _AirScaleHeight) + AerosolBeta() * 1.11 * exp(-hs / _AerosolScaleHeight) + OZONE * OzoneDensity(hs)) * (t - prev);
                    prev = t;
                }
                return exp(-tau);
            }

            half3 PhysicalSky(float3 dir, float3 sunDir, half3 sunColor)
            {
                const int STEPS = 16;
                float R = _PlanetRadius;
                float h0 = 0.002;                      // the viewer stands just above the ground
                float top = R + max(max(_AirScaleHeight, _AerosolScaleHeight) * 8.0, 60.0);
                float r0 = R + h0;
                float mu = dir.y;
                // Below the horizon the ray ends on the ground.
                float rayLen = RayToTop(r0, mu, top);
                float bGround = r0 * mu;
                float discG = bGround * bGround - r0 * r0 + R * R;
                bool hitsGround = mu < 0.0 && discG > 0.0;
                if (hitsGround) rayLen = -bGround - sqrt(discG);

                float cosSun = dot(dir, sunDir);
                float phaseR = RayleighPhase(cosSun);
                float phaseM = MiePhase(cosSun, _AerosolAnisotropy);
                float3 betaR = AirBeta();
                float betaM = AerosolBeta();
                half3 aerosolTint = _AerosolTint.rgb;

                float3 inscatter = 0;
                float3 tauView = 0;
                float prev = 0;
                for (int i = 1; i <= STEPS; i++)
                {
                    // Steps grow with distance: the air is densest close by.
                    float f = i / (float)STEPS;
                    float t = rayLen * f * f;
                    float ds = t - prev;
                    float3 p = float3(0, r0, 0) + dir * (0.5 * (t + prev));
                    prev = t;
                    float r = length(p);
                    float h = r - R;
                    float dR = exp(-h / _AirScaleHeight);
                    float dM = exp(-h / _AerosolScaleHeight);
                    float3 stepTau = (betaR * dR + betaM * 1.11 * dM + OZONE * OzoneDensity(h)) * ds;
                    float muS = dot(p / r, sunDir);
                    // Sun light reaching this point; the planet's shadow fades in softly as the sun sets below its horizon.
                    float horizonCos = -sqrt(max(1.0 - (R * R) / (r * r), 0.0));
                    float3 toSunUp = Transmittance(h, max(muS, horizonCos + 0.01));
                    float3 toSun = toSunUp * smoothstep(-0.015, 0.015, muS - horizonCos);
                    float3 scatter = betaR * dR * phaseR + betaM * dM * phaseM * aerosolTint;
                    // Light scattered more than once: roughly as much again, spread evenly (keeps sunsets and the
                    // shadowed side of the sky from going black as with single scattering alone).
                    float3 multiple = (betaR * dR + betaM * dM * aerosolTint) * (1.0 / (4.0 * PI)) * toSunUp * saturate(muS * 4.0 + 0.6);
                    inscatter += exp(-(tauView + 0.5 * stepTau)) * (toSun * scatter + multiple) * ds;
                    tauView += stepTau;
                }
                // Built-in light intensities are irradiance / π; the sky radiance from the sun's irradiance.
                float3 sky = inscatter * sunColor * PI;
                if (hitsGround)
                {
                    float3 gp = float3(0, r0, 0) + dir * rayLen;
                    float gmu = dot(normalize(gp), sunDir);
                    sky += exp(-tauView) * _GroundTint.rgb * Transmittance(0.0, gmu) * saturate(gmu) * sunColor;
                }
                // HDRP's art controls: tints blended from horizon to zenith, saturation.
                float zen = saturate(abs(mu) * (1.0 - _HorizonZenithShift) + max(_HorizonZenithShift, 0.0));
                sky *= lerp(_HorizonTint.rgb, _ZenithTint.rgb, zen);
                float lum = dot(sky, float3(0.2126, 0.7152, 0.0722));
                sky = lerp(lum.xxx, sky, _ColorSaturation);

                // Sun disk, dimmed by the air in front of it, with limb darkening.
                float sunCos = cos(radians(max(_SunDiskSize, 0.01) * 0.5));
                if (!hitsGround && cosSun > sunCos)
                {
                    float rr = saturate((1.0 - cosSun) / (1.0 - sunCos));
                    float limb = 1.0 - 0.6 * (1.0 - sqrt(1.0 - rr));
                    float solidAngle = 2.0 * PI * (1.0 - sunCos);
                    sky += Transmittance(h0, mu) * sunColor * PI / solidAngle * limb * _SunDiskIntensity * 1e-3;
                }
                return sky;
            }

            half3 GradientSky(float3 dir)
            {
                float y = dir.y;
                float d = max(_GradientDiffusion, 0.01);
                half3 c = lerp(_Middle.rgb, _Top.rgb, saturate(pow(saturate(y), 1.0 / d)));
                c = lerp(c, _Bottom.rgb, saturate(pow(saturate(-y), 1.0 / d)));
                return c;
            }

            half3 CubemapSky(float3 dir)
            {
                float a = radians(_Rotation);
                float s = sin(a), c = cos(a);
                float3 d = float3(c * dir.x - s * dir.z, dir.y, s * dir.x + c * dir.z);
                half4 t = texCUBE(_Tex, d);
                return DecodeHDR(t, _Tex_HDR) * _Tint.rgb * unity_ColorSpaceDouble.rgb;
            }

            float2 LatLong(float3 d, float rotationDeg)
            {
                float lon = atan2(d.x, d.z) + radians(rotationDeg);
                float lat = asin(clamp(d.y, -1.0, 1.0));
                return float2(frac(lon / (2.0 * PI) + 0.5), lat / PI + 0.5);
            }

            half4 CloudLayer(float3 dir, float3 sunDir, half3 sunColor, half3 ambient)
            {
                if (_CloudLayerOn < 0.5) return half4(0, 0, 0, 1);
                if (_CloudUpperOnly > 0.5 && dir.y < 0.0) return half4(0, 0, 0, 1);
                float3 d = dir;
                d.y = _CloudUpperOnly > 0.5 ? max(d.y, 0.0) : d.y;
                float2 uv = LatLong(normalize(d), _CloudRotation);
                if (_CloudUpperOnly > 0.5) uv.y = saturate(uv.y * 2.0 - 1.0);
                // Wind: scroll the map (HDRP scrolls it along the scroll orientation).
                uv.x += _CloudScroll.x * _Time.y * 2e-5;
                uv.y += _CloudScroll.y * _Time.y * 2e-5 * 0.5;
                half4 m = tex2D(_CloudMap, uv);
                half opacity = saturate(dot(m, _CloudOpacity));
                // Thin parts let the sun through, thick parts are darker underneath; the sun side is brighter.
                half forward = pow(saturate(dot(dir, sunDir)), 8.0);
                half density = lerp(0.3, 1.0, _CloudThickness) * opacity;
                half3 light = sunColor * PI * (0.25 * exp(-density * 3.0) + 0.6 * forward * (1.0 - density)) + ambient;
                half3 col = light * _CloudTint.rgb * _CloudExposure;
                return half4(col * opacity, 1.0 - opacity);
            }

            half4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                float3 sunDir = _WorldSpaceLightPos0.w < 0.5 && dot(_WorldSpaceLightPos0.xyz, _WorldSpaceLightPos0.xyz) > 0.0 ? normalize(_WorldSpaceLightPos0.xyz) : float3(0, 1, 0);
                half3 sunColor = _LightColor0.rgb;

                half3 sky;
                if (_SkyMode < 0.5) sky = PhysicalSky(dir, sunDir, sunColor);
                else if (_SkyMode < 1.5) sky = GradientSky(dir);
                else sky = CubemapSky(dir);
                sky *= _Exposure;

                half3 ambient = ShadeSH9(float4(0, 1, 0, 1));
                half4 layer = CloudLayer(dir, sunDir, sunColor, ambient);
                sky = sky * layer.a + layer.rgb * _Exposure;

                if (_ParelCloudsOn.x > 0.5 && dir.y > 0.0)
                {
                    float2 uv = float2(frac(atan2(dir.x, dir.z) / (2.0 * PI) + 0.5), sqrt(saturate(dir.y)));
                    half4 vc = tex2Dlod(_ParelCloudPanorama, float4(uv, 0, 0));
                    // Fade out towards the horizon, where the panorama gets thin.
                    half fade = saturate(dir.y * 12.0);
                    sky = lerp(sky, sky * vc.a + vc.rgb, fade);
                }

                sky = ParelApplyFogSky(sky, dir);
                return half4(sky, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
