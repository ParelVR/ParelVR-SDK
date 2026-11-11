// HDRP Water Excluder for ParelVR water: marks where the water must not be drawn (inside boats, under docks) in the
// stencil buffer, drawn just before the water. Invisible itself.
Shader "ParelVR/Water Excluder"
{
    SubShader
    {
        Tags { "Queue"="Transparent-101" "RenderType"="Transparent" "IgnoreProjector"="True" "ForceNoShadowCasting"="True" }
        Pass
        {
            ColorMask 0
            ZWrite Off
            Cull Off
            Stencil { Ref 128 WriteMask 128 Comp Always Pass Replace }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target { return 0; }
            ENDCG
        }
    }
}
