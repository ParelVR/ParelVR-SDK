using System;

namespace ParelVR.Rendering
{
    /// <summary>Sky materials: Unity's Skybox/Cubemap and Skybox/Panoramic, URP/HDRP sky materials, and stand-ins.</summary>
    public sealed class SkyboxReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.BuiltInSkybox;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            string name = s.ShaderName ?? string.Empty;
            var d = new MaterialDescription
            {
                Shading = ShadingModel.Unlit,
                Cull = FaceCull.Off,
                BaseColor = s.ColorOr("_Tint", new ColorValue(0.5f, 0.5f, 0.5f, 0.5f)),
                SkyExposure = s.FloatOr("_Exposure", 1f),
                SkyRotation = s.FloatOr("_Rotation", 0f),
                ReceiveShadows = false,
            };

            TextureSlot cube = s.Tex("_Tex") ?? s.Tex("_Cubemap") ?? s.Tex("_SkyCubemap");
            TextureSlot flat = s.Tex("_MainTex") ?? s.Tex("_PanoramaTex");
            if (cube != null) { d.BaseMap = cube; d.SkyCubemap = true; }
            else if (flat != null) { d.BaseMap = flat; d.SkyCubemap = false; }
            else d.BaseMap = new TextureSlot();

            if (name.IndexOf("6 Sided", StringComparison.Ordinal) >= 0) d.Unmapped("six-sided sky textures (a cubemap is needed)");
            if (name.IndexOf("Panoramic", StringComparison.Ordinal) >= 0)
            {
                if (s.FloatOr("_Mapping", 1f) < 0.5f) d.Unmapped("6-frame layout panorama (shown as latitude-longitude)");
                if (s.FloatOr("_ImageType", 0f) > 0.5f) d.Unmapped("180-degree panorama (shown as 360 degrees)");
            }
            return d;
        }
    }

    /// <summary>Writes a sky description into ParelVR/Skybox.</summary>
    public sealed class SkyboxWriter : IMaterialWriter
    {
        public bool CanWrite(ShaderRole role) => role == ShaderRole.Skybox;

        public void Write(MaterialDescription d, ShaderRole role, TargetPlatform target, IMaterialPropertySink sink)
        {
            sink.SetColor("_Tint", d.BaseColor);
            sink.SetFloat("_Exposure", d.SkyExposure);
            sink.SetFloat("_Rotation", d.SkyRotation);
            bool panorama = !d.SkyCubemap && d.BaseMap != null && d.BaseMap.HasTexture;
            sink.SetFloat("_Panoramic", panorama ? 1f : 0f);
            sink.SetKeyword("_SKYBOX_PANORAMIC", panorama);
            if (d.BaseMap != null && d.BaseMap.HasTexture) sink.SetTexture(panorama ? "_MainTex" : "_Tex", d.BaseMap);
            sink.SetRenderQueue(Queues.Background);
        }
    }
}
