// Replacement shader for ParelVR's sun shadow map (volumetric light shafts): linear depth along the sun, 0..1.
// Opaque and alpha-tested surfaces (avatars too) cast; transparent ones don't.
Shader "Hidden/ParelVR/SunShadowDepth"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _Cutoff ("", Float) = 0.5
        _Color ("", Color) = (1,1,1,1)
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    struct v2f { float4 pos : SV_POSITION; float depth : TEXCOORD0; float2 uv : TEXCOORD1; };
    sampler2D _MainTex;
    float4 _MainTex_ST;
    float _Cutoff;
    fixed4 _Color;

    v2f vert(appdata_base v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.depth = -UnityObjectToViewPos(v.vertex).z * _ProjectionParams.w;
        o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
        return o;
    }
    float4 fragOpaque(v2f i) : SV_Target { return i.depth; }
    float4 fragCutout(v2f i) : SV_Target
    {
        clip(tex2D(_MainTex, i.uv).a * _Color.a - _Cutoff);
        return i.depth;
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOpaque
            ENDCG }
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" }
        Cull Off
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragCutout
            ENDCG }
    }
    Fallback Off
}
