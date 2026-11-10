using System;

namespace ParelVR.Rendering
{
    /// <summary>Surface, emission and feature settings shared by the HDRP Lit, Layered Lit and Unlit readers.</summary>
    internal static class HdrpSurface
    {
        public static void Read(IMaterialPropertySource s, MaterialDescription d)
        {
            bool transparent = s.FloatOr("_SurfaceType", 0f) > 0.5f || s.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT");
            if (transparent)
            {
                switch ((int)Math.Round(s.FloatOr("_BlendMode", 0f)))
                {
                    case 1: d.Surface = SurfaceType.Additive; break;
                    case 4: d.Surface = SurfaceType.Transparent; break;
                    default:
                        // "Alpha" with Preserve Specular Lighting (HDRP's default) keeps highlights: premultiplied.
                        d.Surface = s.FloatOr("_EnableBlendModePreserveSpecularLighting", 1f) > 0.5f && d.Shading != ShadingModel.Unlit ? SurfaceType.Transparent : SurfaceType.Fade;
                        break;
                }
                if (s.FloatOr("_RefractionModel", 0f) > 0.5f || s.IsKeywordEnabled("_REFRACTION_PLANE") || s.IsKeywordEnabled("_REFRACTION_SPHERE") || s.IsKeywordEnabled("_REFRACTION_THIN"))
                    d.Unmapped("refraction (shown as plain transparency)");
                if (s.FloatOr("_EnableFogOnTransparent", 1f) < 0.5f) d.Unmapped("fog switched off on this transparent surface");
            }
            if (s.FloatOr("_AlphaCutoffEnable", 0f) > 0.5f || s.IsKeywordEnabled("_ALPHATEST_ON"))
            {
                d.AlphaClip = true;
                d.AlphaCutoff = s.FloatOr("_AlphaCutoff", 0.5f);
                if (!transparent) d.Surface = SurfaceType.Cutout;
            }
            bool doubleSided = s.FloatOr("_DoubleSidedEnable", 0f) > 0.5f || s.IsKeywordEnabled("_DOUBLESIDED_ON");
            d.Cull = doubleSided ? FaceCull.Off : FaceCull.Back;
            if (doubleSided && d.Shading != ShadingModel.Unlit) d.Unmapped("double-sided normal flipping (back faces are lit like front faces)");
            d.RenderQueue = s.RenderQueue;
        }

        /// <summary>
        /// HDRP emission is linear and physical (nits). It is scaled by the scene's exposure as HDRP would (honouring the
        /// material's Exposure Weight), then re-encoded for ParelVR's plain colour property.
        /// </summary>
        public static void Emission(IMaterialPropertySource s, MaterialDescription d, string colorMap)
        {
            ColorValue c = s.ColorOr("_EmissiveColor", ColorValue.Black);
            if (c.IsBlack && s.HasProperty("_EmissiveColorLDR") && s.FloatOr("_UseEmissiveIntensity", 0f) > 0.5f)
            {
                float nits = s.FloatOr("_EmissiveIntensity", 1f);
                if (s.FloatOr("_EmissiveIntensityUnit", 0f) > 0.5f) nits = LightingConversion.NitsFromEv100(nits);
                ColorValue ldr = s.GetColor("_EmissiveColorLDR");
                c = new ColorValue(Linear(ldr.R) * nits, Linear(ldr.G) * nits, Linear(ldr.B) * nits);
            }
            if (c.IsBlack) return;

            float scale = LightingConversion.EmissionScale(s.FloatOr("_EmissiveExposureWeight", 1f), LightingConversion.SceneEv100);
            ColorValue lin = c.Scaled(scale);
            if (s.FloatOr("_AlbedoAffectEmissive", 0f) > 0.5f)
            {
                lin = new ColorValue(lin.R * Linear(d.BaseColor.R), lin.G * Linear(d.BaseColor.G), lin.B * Linear(d.BaseColor.B));
                d.Unmapped("emission tinted by the base colour texture (tinted by the base colour only)");
            }
            d.EmissionEnabled = true;
            d.EmissionColor = lin.WithAlpha(1f).LinearToGamma();
            d.EmissionMap = s.Tex(colorMap);
        }

        public static void Features(IMaterialPropertySource s, MaterialDescription d)
        {
            int id = (int)Math.Round(s.FloatOr("_MaterialID", 1f));
            switch (id)
            {
                case 0: d.Unmapped("subsurface scattering"); break;
                case 2: d.Unmapped("anisotropic highlights"); break;
                case 3: d.Unmapped("iridescence"); break;
                case 5: d.Unmapped("translucency"); break;
            }
            if (s.IsKeywordEnabled("_MATERIAL_FEATURE_TRANSMISSION") && id != 5) d.Unmapped("light transmission");
            if (s.FloatOr("_CoatMask", 0f) > 0f || s.IsKeywordEnabled("_MATERIAL_FEATURE_CLEAR_COAT")) d.Unmapped("clear coat");
            int uv = (int)Math.Round(s.FloatOr("_UVBase", 0f));
            if (uv == 4 || uv == 5) d.Unmapped(uv == 4 ? "planar UV mapping" : "triplanar UV mapping");
            else if (uv > 0) d.Unmapped("textures on UV set " + uv + " (the first UV set is used)");
            if (s.FloatOr("_NormalMapSpace", 0f) > 0.5f) d.Unmapped("object-space normal map");
            int displacement = (int)Math.Round(s.FloatOr("_DisplacementMode", 0f));
            if (displacement == 1 || displacement == 3 || s.IsKeywordEnabled("_VERTEX_DISPLACEMENT") || s.IsKeywordEnabled("_TESSELLATION_DISPLACEMENT"))
                d.Unmapped(displacement == 3 ? "tessellation displacement" : "vertex displacement");
        }

        internal static float Linear(float g) => g <= 0.04045f ? g / 12.92f : (float)Math.Pow((g + 0.055f) / 1.055f, 2.4);
    }

    /// <summary>
    /// HDRP Lit. The mask map (R metallic, G occlusion, B detail mask, A smoothness) goes to ParelVR Lit as it is: its
    /// metallic/smoothness slot reads R and A, its occlusion slot reads G. Remap ranges are approximated by strengths.
    /// </summary>
    public sealed class HdrpLitReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.HdrpLit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family) => ReadLit(s, "");

        internal static MaterialDescription ReadLit(IMaterialPropertySource s, string layer)
        {
            string P(string name) => name + layer;
            bool specularColor = (int)Math.Round(s.FloatOr("_MaterialID", 1f)) == 4 || s.IsKeywordEnabled("_MATERIAL_FEATURE_SPECULAR_COLOR");
            var d = new MaterialDescription
            {
                Shading = specularColor ? ShadingModel.Specular : ShadingModel.Metallic,
                BaseColor = s.ColorOr(P("_BaseColor"), ColorValue.White),
                BaseMap = s.Tex(P("_BaseColorMap")) ?? s.GetTexture(P("_BaseColorMap")) ?? new TextureSlot(),
                Metallic = s.FloatOr(P("_Metallic"), 0f),
                Smoothness = s.FloatOr(P("_Smoothness"), 0.5f),
            };
            HdrpSurface.Read(s, d);

            d.NormalMap = s.Tex(P("_NormalMap"));
            d.NormalScale = s.FloatOr(P("_NormalScale"), 1f);

            TextureSlot mask = s.Tex(P("_MaskMap"));
            if (mask != null && !specularColor)
            {
                d.MaskMap = mask;
                d.Metallic = 1f;
                d.MetallicRemapMin = s.FloatOr(P("_MetallicRemapMin"), 0f);
                d.MetallicRemapMax = s.FloatOr(P("_MetallicRemapMax"), 1f);
                d.SmoothnessRemapMin = s.FloatOr(P("_SmoothnessRemapMin"), 0f);
                d.SmoothnessRemapMax = s.FloatOr(P("_SmoothnessRemapMax"), 1f);
                d.OcclusionRemapMin = s.FloatOr(P("_AORemapMin"), 0f);
                d.OcclusionRemapMax = s.FloatOr(P("_AORemapMax"), 1f);
                d.Smoothness = d.SmoothnessRemapMax;
                d.SmoothnessFrom = SmoothnessSource.MaskMapAlpha;
                d.OcclusionStrength = Math.Max(0f, Math.Min(1f, d.OcclusionRemapMax - d.OcclusionRemapMin));
                if (d.MetallicRemapMin > 0.01f || d.MetallicRemapMax < 0.99f) d.Unmapped("metallic remapping (the mask map's metallic is used as is)");
                if (d.SmoothnessRemapMin > 0.01f) d.Unmapped("smoothness remap minimum");
            }
            else if (mask != null)
            {
                d.OcclusionMap = mask;
                d.OcclusionStrength = 1f;
                d.Unmapped("mask map smoothness with a specular colour (the smoothness value is used)");
            }

            if (specularColor)
            {
                d.SpecularColor = s.ColorOr("_SpecularColor", new ColorValue(1f, 1f, 1f, 1f));
                d.SpecularGlossMap = s.Tex("_SpecularColorMap");
                d.SmoothnessFrom = d.SpecularGlossMap != null ? SmoothnessSource.SpecularAlpha : SmoothnessSource.Value;
                if (d.SpecularGlossMap != null) d.Unmapped("specular colour map alpha is read as smoothness");
            }

            if (s.Tex(P("_DetailMap")) != null || s.IsKeywordEnabled("_DETAIL_MAP"))
                d.Unmapped("HDRP detail map (its packed albedo/normal/smoothness layout has no Built-in equivalent)");

            int displacement = (int)Math.Round(s.FloatOr("_DisplacementMode", 0f));
            TextureSlot height = s.Tex(P("_HeightMap"));
            if (height != null && (displacement == 2 || s.IsKeywordEnabled("_PIXEL_DISPLACEMENT")))
            {
                d.HeightMap = height;
                // HDRP gives the amplitude in centimetres; Built-in parallax takes a 0.005–0.08 strength.
                float cm = s.FloatOr(P("_HeightPoMAmplitude"), 2f);
                d.HeightScale = Math.Max(0.005f, Math.Min(0.08f, cm * 0.01f));
                d.Unmapped("pixel displacement (shown as parallax mapping)");
            }

            HdrpSurface.Emission(s, d, "_EmissiveColorMap");
            HdrpSurface.Features(s, d);
            if (s.FloatOr("_EnableGeometricSpecularAA", 0f) > 0.5f) d.Unmapped("geometric specular anti-aliasing");
            return d;
        }
    }

    /// <summary>HDRP Layered Lit: the main (first) layer is converted; the others are reported.</summary>
    public sealed class HdrpLayeredLitReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.HdrpLayeredLit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            MaterialDescription d = HdrpLitReader.ReadLit(s, "0");
            int layers = (int)Math.Round(s.FloatOr("_LayerCount", 2f));
            if (layers > 1) d.Unmapped("layers 2–" + layers + " of the layered material (the main layer is shown)");
            if (s.Tex("_LayerMaskMap") != null) d.Unmapped("layer mask");
            return d;
        }
    }

    /// <summary>HDRP Unlit.</summary>
    public sealed class HdrpUnlitReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.HdrpUnlit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            var d = new MaterialDescription
            {
                Shading = ShadingModel.Unlit,
                BaseColor = s.ColorOr("_UnlitColor", s.ColorOr("_BaseColor", ColorValue.White)),
                BaseMap = s.Tex("_UnlitColorMap") ?? s.GetTexture("_UnlitColorMap") ?? new TextureSlot(),
            };
            HdrpSurface.Read(s, d);
            d.ReceiveShadows = false;
            HdrpSurface.Emission(s, d, "_EmissiveColorMap");
            return d;
        }
    }
}
