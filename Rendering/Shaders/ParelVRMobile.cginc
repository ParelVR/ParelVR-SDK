#ifndef PARELVR_MOBILE_INCLUDED
#define PARELVR_MOBILE_INCLUDED

// Standalone headsets (GLES3 / Vulkan): the ParelVR lit shaders' Quest SubShader includes this before Unity's Standard
// code. Same properties and keywords as on PC, cheaper lighting:
//  - BRDF2 (Unity's mobile-quality Standard BRDF) instead of the full GGX BRDF1
//  - one reflection probe sample: no probe blending, no box projection
// Variants the headset never uses (realtime GI, directional lightmaps, parallax, detail maps, shadows from extra
// lights) are left out by the SubShader's pragmas.

#undef UNITY_SPECCUBE_BLENDING
#undef UNITY_SPECCUBE_BOX_PROJECTION
#undef UNITY_ENABLE_REFLECTION_BUFFERS
#define UNITY_BRDF_PBS BRDF2_Unity_PBS

#endif
