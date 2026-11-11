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
                int refraction = (int)Math.Round(s.FloatOr("_RefractionModel", 0f));
                if (refraction == 0 && s.IsKeywordEnabled("_REFRACTION_PLANE")) refraction = 1;
                if (refraction == 0 && s.IsKeywordEnabled("_REFRACTION_SPHERE")) refraction = 2;
                if (refraction == 0 && s.IsKeywordEnabled("_REFRACTION_THIN")) refraction = 3;
                if (refraction > 0)
                {
                    d.RefractionModel = Math.Min(refraction, 3);
                    d.Ior = s.FloatOr("_Ior", 1.5f);
                    d.TransmittanceColor = s.ColorOr("_TransmittanceColor", ColorValue.White);
                    d.AbsorptionDistance = s.FloatOr("_ATDistance", 1f);
                    d.RefractionThickness = s.FloatOr("_Thickness", 0.1f);
                }
                if (s.FloatOr("_EnableFogOnTransparent", 1f) < 0.5f) d.NoFog = true;
            }
            if (s.FloatOr("_AlphaCutoffEnable", 0f) > 0.5f || s.IsKeywordEnabled("_ALPHATEST_ON"))
            {
                d.AlphaClip = true;
                d.AlphaCutoff = s.FloatOr("_AlphaCutoff", 0.5f);
                if (!transparent) d.Surface = SurfaceType.Cutout;
            }
            bool doubleSided = s.FloatOr("_DoubleSidedEnable", 0f) > 0.5f || s.IsKeywordEnabled("_DOUBLESIDED_ON");
            d.Cull = doubleSided ? FaceCull.Off : FaceCull.Back;
            if (doubleSided && d.Shading != ShadingModel.Unlit)
            {
                // HDRP: 0 flip, 1 mirror, 2 none.
                int mode = (int)Math.Round(s.FloatOr("_DoubleSidedNormalMode", 1f));
                d.DoubleSidedNormalMode = mode == 0 ? 1 : mode == 1 ? 2 : 0;
            }
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
                case 2: d.Anisotropy = s.FloatOr("_Anisotropy", 0f); break;
                case 3:
                    d.Iridescence = s.FloatOr("_IridescenceMask", 1f);
                    d.IridescenceThickness = s.FloatOr("_IridescenceThickness", 0.5f);
                    break;
            }
            Subsurface(s, d, id, "");
            if (s.FloatOr("_CoatMask", 0f) > 0f || s.IsKeywordEnabled("_MATERIAL_FEATURE_CLEAR_COAT"))
            {
                d.CoatMask = s.FloatOr("_CoatMask", 1f);
                d.CoatMaskMap = s.Tex("_CoatMaskMap");
            }
            int uv = (int)Math.Round(s.FloatOr("_UVBase", 0f));
            if (uv == 4 || uv == 5) d.Unmapped(uv == 4 ? "planar UV mapping" : "triplanar UV mapping");
            else if (uv > 0) d.Unmapped("textures on UV set " + uv + " (the first UV set is used)");
            if (s.FloatOr("_NormalMapSpace", 0f) > 0.5f) d.Unmapped("object-space normal map");
            int displacement = (int)Math.Round(s.FloatOr("_DisplacementMode", 0f));
            if (displacement == 1 || displacement == 3 || s.IsKeywordEnabled("_VERTEX_DISPLACEMENT") || s.IsKeywordEnabled("_TESSELLATION_DISPLACEMENT"))
                d.Unmapped(displacement == 3 ? "tessellation displacement" : "vertex displacement");
        }

        /// <summary>
        /// Subsurface Scattering (material type 0) and Translucent (5): ParelVR/Subsurface's scattering and transmission.
        /// The colours come from the diffusion profile, which the Editor resolves from <see cref="MaterialDescription.DiffusionProfileRef"/>;
        /// until then they are derived from the base colour.
        /// </summary>
        public static void Subsurface(IMaterialPropertySource s, MaterialDescription d, int materialId, string layer)
        {
            bool sss = materialId == 0 || s.IsKeywordEnabled("_MATERIAL_FEATURE_SUBSURFACE_SCATTERING");
            bool translucent = materialId == 5;
            bool transmission = translucent || s.IsKeywordEnabled("_MATERIAL_FEATURE_TRANSMISSION") || s.FloatOr("_TransmissionEnable", 0f) > 0.5f && sss;
            if (!sss && !translucent) return;
            d.Subsurface = true;
            d.Transmission = transmission;
            d.SubsurfaceMask = translucent ? 0f : s.FloatOr("_SubsurfaceMask" + layer, 1f);
            d.SubsurfaceMaskMap = translucent ? null : s.Tex("_SubsurfaceMaskMap" + layer);
            float thickness = s.FloatOr("_Thickness" + layer, 1f);
            d.ThicknessMap = s.Tex("_ThicknessMap" + layer);
            ColorValue remap = s.ColorOr("_ThicknessRemap" + layer, new ColorValue(0f, 1f, 0f, 0f));
            if (d.ThicknessMap != null)
            {
                d.ThicknessMin = remap.R;
                d.ThicknessMax = remap.G;
            }
            else d.ThicknessMin = d.ThicknessMax = thickness;
            if (!transmission) d.ThicknessMin = d.ThicknessMax = 1f; // no light through
            d.DiffusionProfileRef = s.ColorOr("_DiffusionProfileAsset" + layer, ColorValue.Clear);
            // Until the profile is resolved: scattering in a brighter, more saturated version of the base colour.
            ColorValue b = d.BaseColor;
            float max = Math.Max(1e-3f, b.MaxRgb);
            d.SubsurfaceColor = new ColorValue(Math.Min(1f, b.R / max), Math.Min(1f, b.G / max * 0.8f), Math.Min(1f, b.B / max * 0.7f), 1f);
            d.TransmissionTint = d.SubsurfaceColor;
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
            if (s.FloatOr("_EnableGeometricSpecularAA", 0f) > 0.5f)
            {
                d.SpecularAA = true;
                d.SpecularAAVariance = s.FloatOr("_SpecularAAScreenSpaceVariance", 0.1f);
                d.SpecularAAThreshold = s.FloatOr("_SpecularAAThreshold", 0.2f);
            }
            return d;
        }
    }

    /// <summary>
    /// HDRP Layered Lit: every layer (up to 4) with its colour, normal and mask maps, tiling and remaps, the layer mask
    /// and the vertex-colour mode go to ParelVR/Layered Lit. The main layer's values also fill the plain fields, which
    /// fallbacks and the Meta / Standalone VR toon conversion use.
    /// </summary>
    public sealed class HdrpLayeredLitReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.HdrpLayeredLit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            MaterialDescription d = HdrpLitReader.ReadLit(s, "0");
            int count = Math.Max(1, Math.Min(4, (int)Math.Round(s.FloatOr("_LayerCount", 2f))));
            for (int i = 0; i < count; i++) d.Layers.Add(Layer(s, i.ToString()));
            d.LayerMask = s.Tex("_LayerMaskMap");
            d.LayerVertexColorMode = (int)Math.Round(s.FloatOr("_VertexColorMode", 0f));
            if (s.FloatOr("_UseHeightBasedBlend", 0f) > 0.5f) d.Unmapped("height-based layer blending (layers blend by the mask only)");
            if (s.FloatOr("_UseMainLayerInfluence", 0f) > 0.5f) d.Unmapped("main layer influence");
            if (count > 1) d.UnmappedFeatures.RemoveAll(f => f.StartsWith("HDRP detail map", StringComparison.Ordinal));
            return d;
        }

        private static LayerDescription Layer(IMaterialPropertySource s, string n)
        {
            var l = new LayerDescription
            {
                BaseColor = s.ColorOr("_BaseColor" + n, ColorValue.White),
                BaseMap = s.Tex("_BaseColorMap" + n) ?? s.GetTexture("_BaseColorMap" + n),
                NormalMap = s.Tex("_NormalMap" + n),
                NormalScale = s.FloatOr("_NormalScale" + n, 1f),
                MaskMap = s.Tex("_MaskMap" + n),
            };
            float metallic = s.FloatOr("_Metallic" + n, 0f), smoothness = s.FloatOr("_Smoothness" + n, 0.5f);
            if (l.MaskMap != null)
            {
                l.MetallicMin = s.FloatOr("_MetallicRemapMin" + n, 0f);
                l.MetallicMax = s.FloatOr("_MetallicRemapMax" + n, 1f);
                l.SmoothnessMin = s.FloatOr("_SmoothnessRemapMin" + n, 0f);
                l.SmoothnessMax = s.FloatOr("_SmoothnessRemapMax" + n, 1f);
                l.OcclusionMin = s.FloatOr("_AORemapMin" + n, 0f);
                l.OcclusionMax = s.FloatOr("_AORemapMax" + n, 1f);
            }
            else
            {
                l.MetallicMin = l.MetallicMax = metallic;
                l.SmoothnessMin = l.SmoothnessMax = smoothness;
            }
            return l;
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
