#ifndef PARELVR_STANDARD_FORWARD_INCLUDED
#define PARELVR_STANDARD_FORWARD_INCLUDED

// Unity's Standard forward passes plus ParelVR volumetric fog. The lighting is Unity's own (UnityStandardCore); the fog
// is applied to its result, so Unity's fog (RenderSettings) and ParelVR's both work, and a world uses one or the other.

#include "UnityStandardCoreForward.cginc"
#include "ParelVRFog.cginc"

half _DstBlend;

half4 parelFragBase(VertexOutputForwardBase i) : SV_Target
{
    half4 c = fragForwardBaseInternal(i);
    float3 posWorld = IN_WORLDPOS(i);
    if (_DstBlend == 1.0h) c.rgb = ParelApplyFogAdditive(c.rgb, posWorld);     // additive surfaces only dim
    else
    {
    #if defined(_ALPHAPREMULTIPLY_ON)
        c.rgb = ParelApplyFog(c.rgb, posWorld, c.a);
    #else
        c.rgb = ParelApplyFog(c.rgb, posWorld, 1.0h);
    #endif
    }
    return c;
}

half4 parelFragAdd(VertexOutputForwardAdd i) : SV_Target
{
    half4 c = fragForwardAddInternal(i);
    c.rgb = ParelApplyFogAdditive(c.rgb, IN_WORLDPOS_FWDADD(i));
    return c;
}

#endif
