// Plain grey "clay" look for the Geometry picture sent with an Upload for someone else request
// (ParelBuildPictures): the model's shape with no textures or materials. Used as a replacement shader.
Shader "Hidden/ParelVR/Clay"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 world : TEXCOORD1;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 n = normalize(i.normal) * (facing > 0 ? 1 : -1);
                float3 v = normalize(_WorldSpaceCameraPos - i.world);
                float key = saturate(dot(n, normalize(float3(0.35, 0.8, 0.5))));
                float fill = saturate(dot(n, normalize(float3(-0.6, 0.2, -0.4)))) * 0.25;
                float rim = pow(1 - saturate(dot(n, v)), 3) * 0.2;
                float c = 0.22 + key * 0.6 + fill + rim;
                return fixed4(c * 0.88, c * 0.88, c * 0.92, 1);
            }
            ENDCG
        }
    }
}
