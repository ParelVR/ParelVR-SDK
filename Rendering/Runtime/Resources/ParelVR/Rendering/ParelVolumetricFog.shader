// ParelVR screen-space volumetric fog (PC). Pass 0 marches the view ray at half resolution through the height fog and
// the local fog boxes, lighting each step with the sky's ambient light, the sun (shadowed by ParelVR's sun shadow map:
// light shafts) and up to 8 point / spot lights (lamps glowing in the fog). Pass 1 lays the result over everything
// drawn so far (opaque world, avatars and sky): colour × transmittance + scattered light. Transparent surfaces drawn
// afterwards fog themselves (ParelVRFog.cginc). Mono, multi-pass and single-pass instanced VR (one draw per eye).
Shader "Hidden/ParelVR/VolumetricFog"
{
    CGINCLUDE
    #include "UnityCG.cginc"
    #include "Packages/com.parelvrsdk.pvr/Rendering/Shaders/ParelVRFog.cginc"
    #pragma multi_compile_local __ PARELVR_STEREO_ARRAY
    #pragma target 3.5

    #if defined(PARELVR_STEREO_ARRAY)
        UNITY_DECLARE_TEX2DARRAY(_CameraDepthTexture);
        #define PARELVR_RAW_DEPTH(uv) UNITY_SAMPLE_TEX2DARRAY_LOD(_CameraDepthTexture, float3(uv, _ParelEye), 0).r
    #else
        UNITY_DECLARE_TEX2D_FLOAT(_CameraDepthTexture);
        #define PARELVR_RAW_DEPTH(uv) _CameraDepthTexture.SampleLevel(sampler_CameraDepthTexture, uv, 0).r
    #endif

    float _ParelEye;
    float4x4 _ParelInvViewProj[2];
    float4 _ParelEyePos[2];

    float4 _ParelVolParams;        // x volumetric range (m), y steps, z frame number, w sun shadows on (0/1)
    sampler2D _ParelSunShadowMap;  // linear depth 0..1 along the sun, 1 = nothing
    float4 _ParelSunShadowCenter;  // xyz centre of the shadowed area, w 1 / (2 * half size)
    float4 _ParelSunShadowRight;   // xyz light-space right
    float4 _ParelSunShadowUp;      // xyz light-space up
    float4 _ParelSunShadowForward; // xyz direction the sun shines, w 1 / far
    float4 _ParelSunShadowOrigin;  // xyz shadow camera position, w depth bias (0..1)

    float4 _ParelFogLightCount;
    float4 _ParelFogLightPos[8];    // xyz position, w 1 / range²
    float4 _ParelFogLightColor[8];  // rgb colour × intensity, w 1 spot / 0 point
    float4 _ParelFogLightDir[8];    // xyz direction the spot shines, w cos(outer half angle)
    float4 _ParelFogLightCone[8];   // x 1 / (cos inner - cos outer)

    float4 _ParelUnderwater;        // x camera under water (0/1), y water surface height, rgb in _ParelUnderwaterColor
    float4 _ParelUnderwaterColor;   // rgb scattering colour (lit), a absorption per metre
    float4 _ParelUnderwaterAbsorb;  // rgb absorption per metre per channel

    sampler2D _ParelFogMarch;       // pass 0 result: rgb scattered light, a transmittance
    float4 _ParelFogMarch_TexelSize;

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;   // row-aligned with every render texture of this camera
        float2 ndc : TEXCOORD1;  // where on the image (x right, y up), for the view ray
    };

    v2f vert(uint id : SV_VertexID)
    {
        v2f o;
        float2 p = float2((id << 1) & 2, id & 2) * 2.0 - 1.0;   // full-screen triangle
        o.pos = float4(p, 0.0, 1.0);
        o.uv = p * 0.5 + 0.5;
        #if UNITY_UV_STARTS_AT_TOP
            o.uv.y = 1.0 - o.uv.y;
        #endif
        o.ndc = float2(p.x, p.y * _ProjectionParams.x);
        return o;
    }

    int Eye() { return (int)_ParelEye; }

    float3 WorldAt(float2 ndc, float rawDepth)
    {
        #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES3) || defined(SHADER_API_GLES)
            rawDepth = rawDepth * 2.0 - 1.0;   // OpenGL clip depth runs -1..1
        #endif
        float4 w = mul(_ParelInvViewProj[Eye()], float4(ndc, rawDepth, 1.0));
        return w.xyz / w.w;
    }

    bool IsSky(float rawDepth)
    {
        #if defined(UNITY_REVERSED_Z)
            return rawDepth <= 1e-7;
        #else
            return rawDepth >= 1.0 - 1e-7;
        #endif
    }

    float SunShadow(float3 p)
    {
        if (_ParelVolParams.w < 0.5) return 1.0;
        float3 d = p - _ParelSunShadowCenter.xyz;
        float2 uv = float2(dot(d, _ParelSunShadowRight.xyz), dot(d, _ParelSunShadowUp.xyz)) * _ParelSunShadowCenter.w + 0.5;
        if (any(uv < 0.0) || any(uv > 1.0)) return 1.0;
        float depth = dot(p - _ParelSunShadowOrigin.xyz, _ParelSunShadowForward.xyz) * _ParelSunShadowForward.w;
        float stored = tex2Dlod(_ParelSunShadowMap, float4(uv, 0, 0)).r;
        return depth - _ParelSunShadowOrigin.w > stored ? 0.0 : 1.0;
    }

    float Density(float3 p, out half3 albedo)
    {
        float k = max(_ParelFogParams.w, 1e-5);
        float d = _ParelFogParams.y * exp(-k * max(p.y - _ParelFogParams.z, 0.0));
        albedo = _ParelFogColor.rgb * d;
        int count = (int)_ParelFogVolumeCount.x;
        [loop] for (int v = 0; v < count; v++)
        {
            float3 q = mul(_ParelFogVolumeWorldToLocal[v], float4(p, 1.0)).xyz;
            if (all(abs(q) <= 0.5))
            {
                d += _ParelFogVolumeParams[v].a;
                albedo += _ParelFogVolumeParams[v].rgb * _ParelFogVolumeParams[v].a;
            }
        }
        albedo /= max(d, 1e-6);
        return d;
    }

    half3 LocalLights(float3 p, float3 dir)
    {
        half3 sum = 0;
        int count = (int)_ParelFogLightCount.x;
        [loop] for (int l = 0; l < count; l++)
        {
            float3 toLight = _ParelFogLightPos[l].xyz - p;
            float dist2 = dot(toLight, toLight);
            float r2 = dist2 * _ParelFogLightPos[l].w;
            if (r2 >= 1.0) continue;
            float3 L = toLight * rsqrt(max(dist2, 1e-4));
            // Unity's falloff (smooth window to the range) with inverse square near the lamp.
            float atten = saturate(1.0 - r2 * r2);
            atten = atten * atten / (1.0 + 25.0 * r2);
            if (_ParelFogLightColor[l].w > 0.5)
            {
                float cd = dot(-L, _ParelFogLightDir[l].xyz);
                atten *= saturate((cd - _ParelFogLightDir[l].w) * _ParelFogLightCone[l].x);
            }
            sum += _ParelFogLightColor[l].rgb * atten * ParelFogPhase(dot(dir, -L), _ParelFogSunDir.w);
        }
        return sum;
    }

    // Interleaved gradient noise: a different start offset per pixel and frame, so few steps don't band.
    float Noise(float2 pixel, float frame)
    {
        pixel += frame * 5.588238;
        return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
    }

    half4 FragMarch(v2f i) : SV_Target
    {
        float raw = PARELVR_RAW_DEPTH(i.uv);
        float3 cam = _ParelEyePos[Eye()].xyz;
        float3 far = WorldAt(i.ndc, raw);
        float3 ray = far - cam;
        float len = IsSky(raw) ? _ParelFogColor.a : min(length(ray), _ParelFogColor.a);
        float3 dir = normalize(ray);

        int steps = (int)_ParelVolParams.y;
        float marchLen = min(len, _ParelVolParams.x);
        float stepLen = marchLen / steps;
        float t = stepLen * Noise(i.pos.xy, _ParelVolParams.z);
        half3 sunPhase = _ParelFogSunColor.rgb * ParelFogPhase(dot(dir, _ParelFogSunDir.xyz), _ParelFogSunDir.w);

        half3 light = 0;
        float transmittance = 1.0;
        [loop] for (int s = 0; s < steps; s++)
        {
            float3 p = cam + dir * t;
            half3 albedo;
            float sigma = Density(p, albedo);
            if (sigma > 1e-7)
            {
                half3 lit = (_ParelFogAmbient.rgb + sunPhase * SunShadow(p) + LocalLights(p, dir)) * albedo;
                float stepT = exp(-sigma * stepLen);
                // Energy-conserving step: light scattered over the step, dimmed by what came before it.
                light += transmittance * lit * (1.0 - stepT);
                transmittance *= stepT;
            }
            t += stepLen;
            if (transmittance < 0.003) break;
        }

        // Beyond the marched range: height fog, worked out in one go (ambient and the unshadowed sun).
        if (len > marchLen + 1e-3 && transmittance > 0.003)
        {
            float3 start = cam + dir * marchLen;
            float tailDepth = ParelHeightFogDepth(start, dir, len - marchLen);
            float tailT = exp(-tailDepth);
            light += transmittance * (_ParelFogAmbient.rgb + sunPhase) * _ParelFogColor.rgb * (1.0 - tailT);
            transmittance *= tailT;
        }

        // Under water: the water's own absorption and scattering instead of air fog, up to the surface.
        if (_ParelUnderwater.x > 0.5)
        {
            float wl = IsSky(raw) ? 200.0 : length(ray);
            if (dir.y > 1e-3) wl = min(wl, (_ParelUnderwater.y - cam.y) / dir.y);
            half3 wt = exp(-_ParelUnderwaterAbsorb.rgb * wl);
            light = light * wt + _ParelUnderwaterColor.rgb * (1.0 - wt);
            return half4(light, transmittance * dot(wt, 0.3333));
        }
        return half4(light, transmittance);
    }

    float LinearAt(float2 uv)
    {
        return LinearEyeDepth(PARELVR_RAW_DEPTH(uv));
    }

    // Depth-aware upsampling: of the 4 half-resolution samples around this pixel, the ones at a similar depth count most,
    // so fog doesn't bleed over the edges of things in front.
    half4 FragComposite(v2f i) : SV_Target
    {
        float2 halfSize = _ParelFogMarch_TexelSize.zw;
        float2 st = i.uv * halfSize - 0.5;
        float2 base = floor(st);
        float2 f = st - base;
        float centre = LinearAt(i.uv);
        half4 sum = 0;
        float weights = 0;
        [unroll] for (int y = 0; y < 2; y++)
        [unroll] for (int x = 0; x < 2; x++)
        {
            float2 uv = (base + float2(x, y) + 0.5) / halfSize;
            float bw = (x == 0 ? 1.0 - f.x : f.x) * (y == 0 ? 1.0 - f.y : f.y);
            float d = LinearAt(uv);
            float w = bw * (1.0 / (1e-3 + abs(d - centre) / max(centre, 1e-3))) + 1e-5;
            sum += tex2Dlod(_ParelFogMarch, float4(uv, 0, 0)) * w;
            weights += w;
        }
        return sum / weights;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass // 0: march (half resolution)
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragMarch
            ENDCG
        }

        Pass // 1: composite over the camera image
        {
            Blend One SrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragComposite
            ENDCG
        }
    }
    Fallback Off
}
