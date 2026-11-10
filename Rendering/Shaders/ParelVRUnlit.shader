// ParelVR/Unlit: texture × colour with no lighting. Opaque, cutout, fade, premultiplied, additive and multiply
// through _SrcBlend/_DstBlend/_ZWrite and the alpha keywords; optional emission.
Shader "ParelVR/Unlit"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _EmissionColor ("Emission Color", Color) = (0,0,0)
        _EmissionMap ("Emission", 2D) = "white" {}
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [HideInInspector] _Mode ("__mode", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "IgnoreProjector"="True" }
        LOD 100

        Pass
        {
            Name "UNLIT"
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            CGPROGRAM
            #pragma target 3.0
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _ALPHABLEND_ON
            #pragma shader_feature_local _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _EMISSION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertUnlit
            #pragma fragment fragUnlit
            #include "ParelVRUnlit.cginc"
            ENDCG
        }

        Pass
        {
            Name "META"
            Tags { "LightMode" = "Meta" }
            Cull Off

            CGPROGRAM
            #pragma vertex vert_meta
            #pragma fragment frag_meta
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature EDITOR_VISUALIZATION
            #include "UnityStandardMeta.cginc"
            ENDCG
        }
    }
}
