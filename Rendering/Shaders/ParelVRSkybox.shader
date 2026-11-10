// ParelVR/Skybox: a cubemap or an equirectangular (latitude-longitude) panorama, with tint, exposure and
// rotation around the vertical axis. Used for skies converted from HDRP HDRI skies and URP/HDRP sky materials.
Shader "ParelVR/Skybox"
{
    Properties
    {
        _Tint ("Tint Color", Color) = (.5, .5, .5, .5)
        [Gamma] _Exposure ("Exposure", Range(0, 8)) = 1.0
        _Rotation ("Rotation", Range(0, 360)) = 0
        [NoScaleOffset] _Tex ("Cubemap (HDR)", Cube) = "grey" {}
        [NoScaleOffset] _MainTex ("Panorama (HDR)", 2D) = "grey" {}
        [Toggle(_SKYBOX_PANORAMIC)] _Panoramic ("Use Panorama", Float) = 0
    }

    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma shader_feature_local _SKYBOX_PANORAMIC
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            samplerCUBE _Tex;
            half4 _Tex_HDR;
            sampler2D _MainTex;
            half4 _MainTex_HDR;
            half4 _Tint;
            half _Exposure;
            float _Rotation;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 dir : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 RotateAroundY(float3 v, float degrees)
            {
                float a = degrees * UNITY_PI / 180.0;
                float s, c;
                sincos(a, s, c);
                return float3(c * v.x - s * v.z, v.y, s * v.x + c * v.z);
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(float4(RotateAroundY(v.vertex.xyz, _Rotation), v.vertex.w));
                o.dir = v.vertex.xyz;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 dir = normalize(i.dir);
            #if defined(_SKYBOX_PANORAMIC)
                float2 uv = float2(atan2(dir.x, dir.z) / (2.0 * UNITY_PI) + 0.5, asin(clamp(dir.y, -1.0, 1.0)) / UNITY_PI + 0.5);
                half3 c = DecodeHDR(tex2Dlod(_MainTex, float4(uv, 0, 0)), _MainTex_HDR);
            #else
                half3 c = DecodeHDR(texCUBE(_Tex, dir), _Tex_HDR);
            #endif
                c = c * _Tint.rgb * unity_ColorSpaceDouble.rgb * _Exposure;
                return half4(c, 1);
            }
            ENDCG
        }
    }

    FallBack Off
}
