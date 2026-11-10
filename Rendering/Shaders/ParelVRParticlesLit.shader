// ParelVR/Particles/Lit: ParelVR/Particles/Unlit lit per vertex by light probes and the main directional light.
// Cheap enough for standalone headsets; point lights don't affect it.
Shader "ParelVR/Particles/Lit"
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
        LOD 150

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }
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
            #define PARELVR_PARTICLES_LIT 1
            #include "ParelVRUnlit.cginc"
            ENDCG
        }
    }
}
