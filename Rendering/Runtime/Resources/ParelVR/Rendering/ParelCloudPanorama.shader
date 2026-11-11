// ParelVR volumetric clouds (HDRP Volumetric Clouds). Pass 0 marches the cloud layer for each direction of the upper
// hemisphere into a panorama (rgb light, a transmittance) that ParelVR/Sky reads; pass 1 writes the cloud shadows the
// sun casts (a tiling cookie). Clouds: a 3D shape noise cut by coverage, shaped by height, eroded by detail noise, lit
// by the sun (shadowed through the cloud, beer-powder, two-lobe phase) and the sky's ambient light. Moved by the wind.
Shader "Hidden/ParelVR/CloudPanorama"
{
    CGINCLUDE
    #include "UnityCG.cginc"
    #pragma target 3.5

    sampler3D _ParelCloudShape;    // tiling shape noise (r)
    sampler3D _ParelCloudDetail;   // tiling detail noise (r)
    float4 _ParelCloudSun;         // xyz direction towards the sun, w steps
    float4 _ParelCloudSunColor;    // rgb sun light (Built-in units), w sun light dimmer
    float4 _ParelCloudAmbient;     // rgb sky ambient, w ambient dimmer
    float4 _ParelCloudLayer;       // x bottom altitude (km), y thickness (km), z planet radius (km), w tile size (km)
    float4 _ParelCloudShapeParams; // x coverage 0..1, y density, z shape factor, w erosion factor
    float4 _ParelCloudWind;        // xy offset (km) moved by the wind, z detail scale, w frame
    float4 _ParelCloudTint;        // rgb scattering tint
    float4 _ParelCloudShadow;      // x shadow opacity

    float Remap(float v, float a, float b, float c, float d) { return c + (v - a) / max(b - a, 1e-4) * (d - c); }

    float DensityAt(float3 pKm, float heightFrac)
    {
        float tile = _ParelCloudLayer.w;
        float3 q = float3(pKm.x + _ParelCloudWind.x, pKm.y, pKm.z + _ParelCloudWind.y) / tile;
        float shape = tex3Dlod(_ParelCloudShape, float4(q, 0)).r;
        // Height profile: cumulus rounded at the bottom, thinning at the top; the shape factor flattens it.
        float bottom = saturate(Remap(heightFrac, 0.0, 0.08, 0.0, 1.0));
        float topEnd = lerp(1.0, 0.45, _ParelCloudShapeParams.z);
        float top = saturate(Remap(heightFrac, topEnd * 0.6, topEnd, 1.0, 0.0));
        float coverage = _ParelCloudShapeParams.x;
        float c = saturate(Remap(shape * bottom * top, 1.0 - coverage, 1.0, 0.0, 1.0));
        if (c <= 0.0) return 0.0;
        float detail = tex3Dlod(_ParelCloudDetail, float4(q * _ParelCloudWind.z, 0)).r;
        c = saturate(Remap(c, detail * _ParelCloudShapeParams.w * 0.6, 1.0, 0.0, 1.0));
        return c * _ParelCloudShapeParams.y;
    }

    // Distances along a ray from just above the planet to the bottom and top of the cloud layer.
    float2 Shell(float3 dir)
    {
        float R = _ParelCloudLayer.z;
        float r0 = R + 0.002;
        float rb = R + _ParelCloudLayer.x;
        float rt = rb + _ParelCloudLayer.y;
        float b = r0 * dir.y;
        float tb = -b + sqrt(max(b * b - r0 * r0 + rb * rb, 0.0));
        float tt = -b + sqrt(max(b * b - r0 * r0 + rt * rt, 0.0));
        return float2(tb, min(tt, tb + 60.0));
    }

    float HG(float c, float g) { float g2 = g * g; return (1.0 - g2) / (4.0 * 3.14159265 * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5)); }

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(uint id : SV_VertexID)
    {
        v2f o;
        float2 p = float2((id << 1) & 2, id & 2) * 2.0 - 1.0;
        o.pos = float4(p, 0.0, 1.0);
        o.uv = p * 0.5 + 0.5;
        #if UNITY_UV_STARTS_AT_TOP
            if (_ProjectionParams.x < 0.0) o.uv.y = 1.0 - o.uv.y;
        #endif
        return o;
    }

    half4 FragPanorama(v2f i) : SV_Target
    {
        // Rows run from the horizon (v 0) to the zenith (v 1), squared so the horizon gets more of them.
        float lon = (i.uv.x - 0.5) * 2.0 * 3.14159265;
        float y = i.uv.y * i.uv.y;
        float s = sqrt(max(1.0 - y * y, 0.0));
        float3 dir = float3(sin(lon) * s, max(y, 0.004), cos(lon) * s);
        dir = normalize(dir);
        float2 seg = Shell(dir);
        int steps = 48;
        float ds = (seg.y - seg.x) / steps;
        float jitter = frac(52.9829189 * frac(dot(i.pos.xy + _ParelCloudWind.w * 5.588, float2(0.06711056, 0.00583715))));
        float t = seg.x + ds * jitter;

        float3 sun = _ParelCloudSun.xyz;
        float cosA = dot(dir, sun);
        float phase = lerp(HG(cosA, -0.2), HG(cosA, 0.65), 0.7) * 4.0 * 3.14159265;
        float3 sunLight = _ParelCloudSunColor.rgb * _ParelCloudSunColor.w;
        float3 ambient = _ParelCloudAmbient.rgb * _ParelCloudAmbient.w;
        const float SIGMA = 30.0;   // extinction per km at full density

        float3 light = 0;
        float trans = 1.0;
        float R = _ParelCloudLayer.z;
        [loop] for (int k = 0; k < steps; k++)
        {
            float3 p = float3(0, R + 0.002, 0) + dir * t;
            float h = length(p) - R;
            float hf = saturate((h - _ParelCloudLayer.x) / _ParelCloudLayer.y);
            float d = DensityAt(p, hf);
            if (d > 0.001)
            {
                // Light reaching this point from the sun through the cloud above it.
                float od = 0;
                float ls = _ParelCloudLayer.y / 6.0;
                [unroll] for (int j = 1; j <= 4; j++)
                {
                    float3 lp = p + sun * ls * j;
                    float lh = saturate((length(lp) - R - _ParelCloudLayer.x) / _ParelCloudLayer.y);
                    od += DensityAt(lp, lh) * ls;
                }
                // Multiple scattering inside the cloud (a second, softer octave): undersides stay grey-white, not black.
                float beer = exp(-od * SIGMA) + 0.45 * exp(-od * SIGMA * 0.2) + 0.15 * exp(-od * SIGMA * 0.05);
                float powder = 1.0 - exp(-d * SIGMA * 2.0 * ds);
                float3 lit = sunLight * phase * beer * lerp(1.0, powder * 2.0, 0.5) + ambient * lerp(0.4, 1.0, hf);
                lit *= _ParelCloudTint.rgb;
                float stepT = exp(-d * SIGMA * ds);
                light += trans * lit * (1.0 - stepT);
                trans *= stepT;
                if (trans < 0.01) break;
            }
            t += ds;
        }
        // Far clouds sink into the haze of the atmosphere.
        float haze = saturate((seg.x - 4.0) / 26.0);
        return half4(light * (1.0 - haze), lerp(trans, 1.0, haze));
    }

    // Cloud shadows: how much sun gets through the cloud layer straight above each point of one tile.
    half4 FragShadow(v2f i) : SV_Target
    {
        float tile = _ParelCloudLayer.w;
        float3 sun = _ParelCloudSun.xyz;
        float od = 0;
        float ls = _ParelCloudLayer.y / 6.0;
        [unroll] for (int j = 0; j < 6; j++)
        {
            float h = _ParelCloudLayer.x + ls * (j + 0.5);
            float3 p = float3(i.uv.x * tile, 0, i.uv.y * tile) + sun / max(sun.y, 0.1) * h;
            p.y = _ParelCloudLayer.z + h;
            od += DensityAt(p, (j + 0.5) / 6.0) * ls;
        }
        half lightThrough = exp(-od * 30.0);
        return lerp(1.0, lightThrough, _ParelCloudShadow.x).xxxx;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragPanorama
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragShadow
            ENDCG }
    }
    Fallback Off
}
