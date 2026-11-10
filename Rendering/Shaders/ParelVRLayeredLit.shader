// ParelVR/Layered Lit: up to 4 blended PBR layers (HDRP Layered Lit). See ParelVRLayered.cginc. _MainTex/_Color mirror
// layer 0 for the shadow, meta and fallback passes.
Shader "ParelVR/Layered Lit"
{
    Properties
    {
        _Color ("Color (layer 0)", Color) = (1,1,1,1)
        _MainTex ("Albedo (layer 0)", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _EmissionColor ("Emission Color", Color) = (0,0,0)
        _EmissionMap ("Emission", 2D) = "white" {}

        _LayerCount ("Layer Count", Range(1, 4)) = 2
        _LayerMaskMap ("Layer Mask (R 1, G 2, B 3)", 2D) = "black" {}
        [Enum(Off,0,Multiply,1,Add,2)] _LayerVertexColorMode ("Vertex Colour Mask", Float) = 0

        [Header(Layer 0)]
        _Layer0Map ("Base Map", 2D) = "white" {}
        _Layer0Color ("Base Colour", Color) = (1,1,1,1)
        [Normal] _Layer0NormalMap ("Normal Map", 2D) = "bump" {}
        _Layer0NormalScale ("Normal Scale", Float) = 1
        _Layer0MaskMap ("Mask Map (R metal, G AO, A smooth)", 2D) = "white" {}
        _Layer0MetallicMin ("Metallic Min", Range(0,1)) = 0
        _Layer0MetallicMax ("Metallic Max", Range(0,1)) = 0
        _Layer0SmoothnessMin ("Smoothness Min", Range(0,1)) = 0.5
        _Layer0SmoothnessMax ("Smoothness Max", Range(0,1)) = 0.5
        _Layer0AOMin ("Occlusion Min", Range(0,1)) = 1
        _Layer0AOMax ("Occlusion Max", Range(0,1)) = 1

        [Header(Layer 1)]
        _Layer1Map ("Base Map", 2D) = "white" {}
        _Layer1Color ("Base Colour", Color) = (1,1,1,1)
        [Normal] _Layer1NormalMap ("Normal Map", 2D) = "bump" {}
        _Layer1NormalScale ("Normal Scale", Float) = 1
        _Layer1MaskMap ("Mask Map", 2D) = "white" {}
        _Layer1MetallicMin ("Metallic Min", Range(0,1)) = 0
        _Layer1MetallicMax ("Metallic Max", Range(0,1)) = 0
        _Layer1SmoothnessMin ("Smoothness Min", Range(0,1)) = 0.5
        _Layer1SmoothnessMax ("Smoothness Max", Range(0,1)) = 0.5
        _Layer1AOMin ("Occlusion Min", Range(0,1)) = 1
        _Layer1AOMax ("Occlusion Max", Range(0,1)) = 1

        [Header(Layer 2)]
        _Layer2Map ("Base Map", 2D) = "white" {}
        _Layer2Color ("Base Colour", Color) = (1,1,1,1)
        [Normal] _Layer2NormalMap ("Normal Map", 2D) = "bump" {}
        _Layer2NormalScale ("Normal Scale", Float) = 1
        _Layer2MaskMap ("Mask Map", 2D) = "white" {}
        _Layer2MetallicMin ("Metallic Min", Range(0,1)) = 0
        _Layer2MetallicMax ("Metallic Max", Range(0,1)) = 0
        _Layer2SmoothnessMin ("Smoothness Min", Range(0,1)) = 0.5
        _Layer2SmoothnessMax ("Smoothness Max", Range(0,1)) = 0.5
        _Layer2AOMin ("Occlusion Min", Range(0,1)) = 1
        _Layer2AOMax ("Occlusion Max", Range(0,1)) = 1

        [Header(Layer 3)]
        _Layer3Map ("Base Map", 2D) = "white" {}
        _Layer3Color ("Base Colour", Color) = (1,1,1,1)
        [Normal] _Layer3NormalMap ("Normal Map", 2D) = "bump" {}
        _Layer3NormalScale ("Normal Scale", Float) = 1
        _Layer3MaskMap ("Mask Map", 2D) = "white" {}
        _Layer3MetallicMin ("Metallic Min", Range(0,1)) = 0
        _Layer3MetallicMax ("Metallic Max", Range(0,1)) = 0
        _Layer3SmoothnessMin ("Smoothness Min", Range(0,1)) = 0.5
        _Layer3SmoothnessMax ("Smoothness Max", Range(0,1)) = 0.5
        _Layer3AOMin ("Occlusion Min", Range(0,1)) = 1
        _Layer3AOMax ("Occlusion Max", Range(0,1)) = 1

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [HideInInspector] _Mode ("__mode", Float) = 0.0
        [HideInInspector] _SrcBlend ("__src", Float) = 1.0
        [HideInInspector] _DstBlend ("__dst", Float) = 0.0
        [HideInInspector] _ZWrite ("__zw", Float) = 1.0
    }

    CGINCLUDE
        #define UNITY_SETUP_BRDF_INPUT MetallicSetup
    ENDCG

    // Quest / standalone headsets: layers 1-3 read only their colour map; mobile BRDF.
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "PerformanceChecks"="False" }
        LOD 300

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }
            Cull [_Cull]
            ZWrite On

            CGPROGRAM
            #pragma only_renderers gles3 vulkan
            #include "ParelVRMobile.cginc"
            #pragma target 3.5
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_fwdbase nodynlightmap nodirlightmap
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertLayered
            #pragma fragment fragLayeredBase
            #define PARELVR_LAYERED_LITE 1
            #include "ParelVRLayered.cginc"
            ENDCG
        }

        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            Fog { Color (0,0,0,0) }
            Cull [_Cull]
            ZWrite Off
            ZTest LEqual

            CGPROGRAM
            #pragma only_renderers gles3 vulkan
            #include "ParelVRMobile.cginc"
            #pragma target 3.5
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_fwdadd
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertLayered
            #pragma fragment fragLayeredAdd
            #define PARELVR_LAYERED_LITE 1
            #define PARELVR_LAYERED_ADD 1
            #include "ParelVRLayered.cginc"
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma only_renderers gles3 vulkan
            #pragma target 3.0
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing
            #pragma vertex vertShadowCaster
            #pragma fragment fragShadowCaster
            #include "UnityStandardShadow.cginc"
            ENDCG
        }
    }

    // PC (Direct3D, Metal): all 4 layers with their normal and mask maps.
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "PerformanceChecks"="False" }
        LOD 300

        Pass
        {
            Name "FORWARD"
            Tags { "LightMode" = "ForwardBase" }
            Cull [_Cull]
            ZWrite On

            CGPROGRAM
            #pragma exclude_renderers gles3 vulkan
            #pragma target 3.5
            #pragma shader_feature_local _EMISSION
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertLayered
            #pragma fragment fragLayeredBase
            #include "ParelVRLayered.cginc"
            ENDCG
        }

        Pass
        {
            Name "FORWARD_DELTA"
            Tags { "LightMode" = "ForwardAdd" }
            Blend One One
            Fog { Color (0,0,0,0) }
            Cull [_Cull]
            ZWrite Off
            ZTest LEqual

            CGPROGRAM
            #pragma exclude_renderers gles3 vulkan
            #pragma target 3.5
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma vertex vertLayered
            #pragma fragment fragLayeredAdd
            #define PARELVR_LAYERED_ADD 1
            #include "ParelVRLayered.cginc"
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma exclude_renderers gles3 vulkan
            #pragma target 3.0
            #pragma shader_feature_local _ALPHATEST_ON
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
            #pragma shader_feature EDITOR_VISUALIZATION
            #include "UnityStandardMeta.cginc"
            ENDCG
        }
    }

    FallBack "ParelVR/Lit"
}
