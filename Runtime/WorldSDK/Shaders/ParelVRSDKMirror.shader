// Built-in RP surface for the ParelVR Mirror component (ParelVRMirror.cs). The component
// renders each eye's reflection into _ReflectionTexLeft / _ReflectionTexRight with that eye's projection, so the
// surface samples the matching texture at its own (horizontally flipped) screen position.
Shader "ParelVR/SDK Mirror"
{
    Properties
    {
        [HideInInspector] _ReflectionTexLeft ("Reflection (Left / Desktop)", 2D) = "black" {}
        [HideInInspector] _ReflectionTexRight ("Reflection (Right)", 2D) = "black" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _ReflectionTexLeft;
            sampler2D _ReflectionTexRight;
            fixed4 _Tint;
            // World-space eye positions the mirror rendered each texture from (equal on desktop).
            float4 _ParelEyePosLeft;
            float4 _ParelEyePosRight;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                // Per-eye (not packed side-by-side) coordinates: each eye has its own texture.
                o.screenPos = ComputeNonStereoScreenPos(o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.screenPos.xy / i.screenPos.w;
                // The mirror renders with xFlip * proj * xFlip to keep triangle winding, which
                // leaves the reflection mirrored left-right in the texture; undo it here.
                uv.x = 1.0 - uv.x;
            #if defined(UNITY_SINGLE_PASS_STEREO) || defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                bool rightEye = unity_StereoEyeIndex != 0;
            #else
                // Multi-pass XR has no eye index (and the projection's off-centre sign isn't
                // reliable across runtimes), so use whichever eye position is nearest the camera
                // drawing this pass. On desktop both are equal and the left texture wins.
                float3 toLeft = _WorldSpaceCameraPos - _ParelEyePosLeft.xyz;
                float3 toRight = _WorldSpaceCameraPos - _ParelEyePosRight.xyz;
                bool rightEye = dot(toRight, toRight) < dot(toLeft, toLeft);
            #endif
                fixed3 col = rightEye ? tex2D(_ReflectionTexRight, uv).rgb : tex2D(_ReflectionTexLeft, uv).rgb;
                return fixed4(col * _Tint.rgb, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
