// ParelVR/Particles/Unlit: vertex colour × colour × texture. Blend mode from _SrcBlend/_DstBlend
// (alpha blend by default; additive, premultiplied and multiply are set by the material).
Shader "ParelVR/Particles/Unlit"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Particle Texture", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _EmissionColor ("Emission Color", Color) = (0,0,0)
        _EmissionMap ("Emission", 2D) = "white" {}
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [HideInInspector] _Mode ("__mode", Float) = 2.0
        [HideInInspector] _SrcBlend ("__src", Float) = 5.0
        [HideInInspector] _DstBlend ("__dst", Float) = 10.0
        [HideInInspector] _ZWrite ("__zw", Float) = 0.0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        LOD 100

        Pass
        {
            Name "PARTICLES"
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            CGPROGRAM
            #pragma target 3.0
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _EMISSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertUnlit
            #pragma fragment fragUnlit
            #define PARELVR_VERTEX_COLOR 1
            #include "ParelVRUnlit.cginc"
            ENDCG
        }
    }
}
