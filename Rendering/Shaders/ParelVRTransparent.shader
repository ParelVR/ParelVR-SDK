// ParelVR/Transparent: ParelVR/Lit with blending. _ALPHABLEND_ON is "fade" (the whole surface fades),
// _ALPHAPREMULTIPLY_ON is "transparent" (glass: reflections and highlights stay). Additive and multiply use the
// fade path with their own _SrcBlend/_DstBlend.
Shader "ParelVR/Transparent"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        _Glossiness ("Smoothness", Range(0.0, 1.0)) = 0.5
        _GlossMapScale ("Smoothness Scale", Range(0.0, 1.0)) = 1.0
        [Enum(Metallic Alpha,0,Albedo Alpha,1)] _SmoothnessTextureChannel ("Smoothness Source", Float) = 0
        [Gamma] _Metallic ("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap ("Metallic (R) Smoothness (A)", 2D) = "white" {}
        _SpecColor ("Specular", Color) = (0.2,0.2,0.2)
        _SpecGlossMap ("Specular (RGB) Smoothness (A)", 2D) = "white" {}
        [ToggleOff] _SpecularHighlights ("Specular Highlights", Float) = 1.0
        [ToggleOff] _GlossyReflections ("Glossy Reflections", Float) = 1.0

        _BumpScale ("Normal Scale", Float) = 1.0
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _OcclusionStrength ("Occlusion Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap ("Occlusion (G)", 2D) = "white" {}
        _EmissionColor ("Emission Color", Color) = (0,0,0)
        _EmissionMap ("Emission", 2D) = "white" {}

        _DetailMask ("Detail Mask (A)", 2D) = "white" {}
        _DetailAlbedoMap ("Detail Albedo x2", 2D) = "grey" {}
        _DetailNormalMapScale ("Detail Normal Scale", Float) = 1.0
        [Normal] _DetailNormalMap ("Detail Normal Map", 2D) = "bump" {}
        [Enum(UV0,0,UV1,1)] _UVSec ("Detail UV Set", Float) = 0

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [HideInInspector] _Mode ("__mode", Float) = 2.0
        [HideInInspector] _SrcBlend ("__src", Float) = 5.0
        [HideInInspector] _DstBlend ("__dst", Float) = 10.0
        [HideInInspector] _ZWrite ("__zw", Float) = 0.0
    }

    CGINCLUDE
        #if defined(_SPECULAR_SETUP)
            #define UNITY_SETUP_BRDF_INPUT SpecularSetup
        #else
            #define UNITY_SETUP_BRDF_INPUT MetallicSetup
        #endif
    ENDCG

    // Quest / standalone headsets: same properties and keywords, cheaper lighting (ParelVRMobile.cginc).
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "PerformanceChecks"="False" }
        LOD 300

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            CGPROGRAM
            #pragma only_renderers gles3 vulkan
            #include "ParelVRMobile.cginc"
            #pragma target 3.0
            #pragma shader_feature_local _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _SPECULAR_SETUP
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local _GLOSSYREFLECTIONS_OFF
            #pragma multi_compile_fwdbase nodynlightmap nodirlightmap
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertBase
            #pragma fragment fragBase
            #include "UnityStandardCoreForward.cginc"
            ENDCG
        }

        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode" = "ForwardAdd" }
            Blend [_SrcBlend] One
            Fog { Color (0,0,0,0) }
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

            CGPROGRAM
            #pragma only_renderers gles3 vulkan
            #include "ParelVRMobile.cginc"
            #pragma target 3.0
            #pragma shader_feature_local _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _SPECULAR_SETUP
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local _SPECULARHIGHLIGHTS_OFF
            #pragma multi_compile_fwdadd
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertAdd
            #pragma fragment fragAdd
            #include "UnityStandardCoreForward.cginc"
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            CGPROGRAM
            #pragma only_renderers gles3 vulkan
            #include "ParelVRMobile.cginc"
            #pragma target 3.0
            #pragma shader_feature_local _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _SPECULAR_SETUP
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            #pragma vertex vertShadowCaster
            #pragma fragment fragShadowCaster
            #include "UnityStandardShadow.cginc"
            ENDCG
        }

    }

    // PC (Direct3D, Metal): the full Standard lighting.
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" "PerformanceChecks"="False" }
        LOD 300

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            Cull [_Cull]

            CGPROGRAM
            #pragma exclude_renderers gles3 vulkan
            #pragma target 3.0
            #pragma shader_feature_local _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _SPECULAR_SETUP
            #pragma shader_feature_local _DETAIL_MULX2
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local _GLOSSYREFLECTIONS_OFF
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertBase
            #pragma fragment fragBase
            #include "UnityStandardCoreForward.cginc"
            ENDCG
        }

        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode" = "ForwardAdd" }
            Blend [_SrcBlend] One
            Fog { Color (0,0,0,0) }
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

            CGPROGRAM
            #pragma exclude_renderers gles3 vulkan
            #pragma target 3.0
            #pragma shader_feature_local _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _SPECULAR_SETUP
            #pragma shader_feature_local _DETAIL_MULX2
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local _SPECULARHIGHLIGHTS_OFF
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertAdd
            #pragma fragment fragAdd
            #include "UnityStandardCoreForward.cginc"
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            CGPROGRAM
            #pragma exclude_renderers gles3 vulkan
            #pragma target 3.0
            #pragma shader_feature_local _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _SPECULAR_SETUP
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            #pragma vertex vertShadowCaster
            #pragma fragment fragShadowCaster
            #include "UnityStandardShadow.cginc"
            ENDCG
        }

        Pass
        {
            Name "META"
            Tags { "LightMode" = "Meta" }
            Cull Off

            CGPROGRAM
            #pragma exclude_renderers gles3 vulkan
            #pragma vertex vert_meta
            #pragma fragment frag_meta
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SPECGLOSSMAP
            #pragma shader_feature_local _SPECULAR_SETUP
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local _DETAIL_MULX2
            #pragma shader_feature EDITOR_VISUALIZATION
            #include "UnityStandardMeta.cginc"
            ENDCG
        }
    }

    FallBack "Legacy Shaders/Transparent/VertexLit"
}
