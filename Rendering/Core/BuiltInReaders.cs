using System;

namespace ParelVR.Rendering
{
    /// <summary>Small helpers the readers share.</summary>
    public static class SourceExtensions
    {
        public static float FloatOr(this IMaterialPropertySource s, string name, float fallback) =>
            s.HasProperty(name) ? s.GetFloat(name) : fallback;

        public static ColorValue ColorOr(this IMaterialPropertySource s, string name, ColorValue fallback) =>
            s.HasProperty(name) ? s.GetColor(name) : fallback;

        /// <summary>The slot when the property exists and holds a texture; otherwise null.</summary>
        public static TextureSlot Tex(this IMaterialPropertySource s, string name)
        {
            if (!s.HasProperty(name)) return null;
            TextureSlot slot = s.GetTexture(name);
            return slot != null && slot.HasTexture ? slot : null;
        }

        /// <summary>The first property of <paramref name="names"/> that exists.</summary>
        public static string First(this IMaterialPropertySource s, params string[] names)
        {
            foreach (string n in names)
                if (s.HasProperty(n)) return n;
            return null;
        }

        public static FaceCull CullOr(this IMaterialPropertySource s, FaceCull fallback, params string[] names)
        {
            string n = s.First(names);
            if (n == null) return fallback;
            int v = (int)Math.Round(s.GetFloat(n));
            return v >= 0 && v <= 2 ? (FaceCull)v : fallback;
        }
    }

    /// <summary>
    /// Unity's Standard and Standard (Specular setup) shaders. What renders is decided by the material's keywords
    /// (the Standard inspector sets them from its settings), so the keywords are what this reader follows.
    /// </summary>
    public sealed class BuiltInStandardReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) =>
            family == ShaderFamily.BuiltInStandard || family == ShaderFamily.BuiltInStandardSpecular;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            var d = new MaterialDescription
            {
                Shading = family == ShaderFamily.BuiltInStandardSpecular ? ShadingModel.Specular : ShadingModel.Metallic,
                BaseColor = s.ColorOr("_Color", ColorValue.White),
                BaseMap = s.GetTexture("_MainTex") ?? new TextureSlot(),
                AlphaCutoff = s.FloatOr("_Cutoff", 0.5f),
                RenderQueue = s.RenderQueue,
            };

            // Blend mode
            if (s.IsKeywordEnabled("_ALPHATEST_ON")) { d.Surface = SurfaceType.Cutout; d.AlphaClip = true; }
            else if (s.IsKeywordEnabled("_ALPHABLEND_ON")) d.Surface = SurfaceType.Fade;
            else if (s.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")) d.Surface = SurfaceType.Transparent;
            else d.Surface = SurfaceType.Opaque;

            // Normal
            if (s.IsKeywordEnabled("_NORMALMAP"))
            {
                d.NormalMap = s.Tex("_BumpMap");
                d.NormalScale = s.FloatOr("_BumpScale", 1f);
            }

            bool smoothnessFromAlbedo = s.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            if (d.Shading == ShadingModel.Metallic)
            {
                if (s.IsKeywordEnabled("_METALLICGLOSSMAP") && (d.MetallicGlossMap = s.Tex("_MetallicGlossMap")) != null)
                {
                    d.Metallic = 1f;
                    d.Smoothness = s.FloatOr("_GlossMapScale", 1f);
                    d.SmoothnessFrom = smoothnessFromAlbedo ? SmoothnessSource.AlbedoAlpha : SmoothnessSource.MetallicAlpha;
                }
                else
                {
                    d.Metallic = s.FloatOr("_Metallic", 0f);
                    d.Smoothness = smoothnessFromAlbedo ? s.FloatOr("_GlossMapScale", 1f) : s.FloatOr("_Glossiness", 0.5f);
                    d.SmoothnessFrom = smoothnessFromAlbedo ? SmoothnessSource.AlbedoAlpha : SmoothnessSource.Value;
                }
            }
            else
            {
                d.SpecularColor = s.ColorOr("_SpecColor", new ColorValue(0.2f, 0.2f, 0.2f, 1f));
                if (s.IsKeywordEnabled("_SPECGLOSSMAP") && (d.SpecularGlossMap = s.Tex("_SpecGlossMap")) != null)
                {
                    d.Smoothness = s.FloatOr("_GlossMapScale", 1f);
                    d.SmoothnessFrom = smoothnessFromAlbedo ? SmoothnessSource.AlbedoAlpha : SmoothnessSource.SpecularAlpha;
                }
                else
                {
                    d.Smoothness = smoothnessFromAlbedo ? s.FloatOr("_GlossMapScale", 1f) : s.FloatOr("_Glossiness", 0.5f);
                    d.SmoothnessFrom = smoothnessFromAlbedo ? SmoothnessSource.AlbedoAlpha : SmoothnessSource.Value;
                }
            }

            d.OcclusionMap = s.Tex("_OcclusionMap");
            d.OcclusionStrength = s.FloatOr("_OcclusionStrength", 1f);

            if (s.IsKeywordEnabled("_EMISSION"))
            {
                d.EmissionColor = s.ColorOr("_EmissionColor", ColorValue.Black);
                d.EmissionMap = s.Tex("_EmissionMap");
                d.EmissionEnabled = !d.EmissionColor.IsBlack;
            }

            if (s.IsKeywordEnabled("_DETAIL_MULX2"))
            {
                d.DetailMask = s.Tex("_DetailMask");
                d.DetailAlbedoMap = s.Tex("_DetailAlbedoMap");
                d.DetailNormalMap = s.Tex("_DetailNormalMap");
                d.DetailNormalScale = s.FloatOr("_DetailNormalMapScale", 1f);
                if (s.FloatOr("_UVSec", 0f) > 0.5f) d.Unmapped("detail maps on the second UV set (the first UV set is used)");
            }

            if (s.IsKeywordEnabled("_PARALLAXMAP"))
            {
                d.HeightMap = s.Tex("_ParallaxMap");
                d.HeightScale = s.FloatOr("_Parallax", 0.02f);
            }

            d.SpecularHighlights = !s.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF");
            d.GlossyReflections = !s.IsKeywordEnabled("_GLOSSYREFLECTIONS_OFF");
            return d;
        }
    }

    /// <summary>The "Legacy Shaders/*" family (Diffuse, Specular, Bumped, Transparent, Cutout, VertexLit, Self-Illumin...).</summary>
    public sealed class BuiltInLegacyReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family)
        {
            switch (family)
            {
                case ShaderFamily.BuiltInLegacyDiffuse:
                case ShaderFamily.BuiltInLegacySpecular:
                case ShaderFamily.BuiltInLegacyBumpedDiffuse:
                case ShaderFamily.BuiltInLegacyBumpedSpecular:
                case ShaderFamily.BuiltInLegacyTransparent:
                case ShaderFamily.BuiltInLegacyCutout:
                case ShaderFamily.BuiltInLegacyOther:
                case ShaderFamily.BuiltInMobile:
                    return true;
                default:
                    return false;
            }
        }

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            string name = s.ShaderName ?? string.Empty;
            var d = new MaterialDescription
            {
                Shading = ShadingModel.Simple,
                BaseColor = s.ColorOr("_Color", ColorValue.White),
                BaseMap = s.GetTexture("_MainTex") ?? new TextureSlot(),
                RenderQueue = s.RenderQueue,
            };

            bool specular = Has(name, "Specular") || s.HasProperty("_Shininess");
            if (specular)
            {
                d.SpecularColor = s.ColorOr("_SpecColor", new ColorValue(0.5f, 0.5f, 0.5f, 1f));
                d.Shininess = s.FloatOr("_Shininess", 0.078125f);
                // Legacy specular shaders take their gloss from the main texture's alpha (except the transparent ones,
                // where alpha is opacity).
                d.SmoothnessFrom = family == ShaderFamily.BuiltInLegacyTransparent ? SmoothnessSource.Value : SmoothnessSource.AlbedoAlpha;
            }
            else
            {
                d.SpecularHighlights = false;
                d.SpecularColor = ColorValue.Black;
            }

            if (Has(name, "Bumped") || Has(name, "Parallax") || Has(name, "Normal"))
                d.NormalMap = s.Tex("_BumpMap");

            if (family == ShaderFamily.BuiltInLegacyCutout)
            {
                d.Surface = SurfaceType.Cutout;
                d.AlphaClip = true;
                d.AlphaCutoff = s.FloatOr("_Cutoff", 0.5f);
            }
            else if (family == ShaderFamily.BuiltInLegacyTransparent)
            {
                d.Surface = SurfaceType.Fade;
            }

            if (Has(name, "Parallax"))
            {
                d.HeightMap = s.Tex("_ParallaxMap");
                d.HeightScale = s.FloatOr("_Parallax", 0.02f);
            }

            if (Has(name, "Self-Illumin"))
            {
                // Glow = albedo × illumination-mask alpha × _Emission. Approximated as emission from the albedo map.
                float strength = s.FloatOr("_Emission", 1f);
                d.EmissionEnabled = strength > 0f;
                d.EmissionColor = ColorValue.White.Scaled(strength);
                d.EmissionMap = s.Tex("_Illum") ?? s.Tex("_MainTex");
                d.Unmapped("self-illumination mask (approximated with an emission map)");
            }
            else if (s.HasProperty("_Emission") && family == ShaderFamily.BuiltInLegacyOther)
            {
                // VertexLit: a flat emissive colour.
                ColorValue e = s.GetColor("_Emission");
                d.EmissionEnabled = !e.IsBlack;
                d.EmissionColor = e;
            }

            if (Has(name, "Reflective")) d.Unmapped("cubemap reflection (reflection probes are used instead)");
            if (Has(name, "Decal")) d.Unmapped("decal texture layer");
            if (Has(name, "Lightmapped")) d.Unmapped("legacy lightmap texture (baked lighting is used instead)");
            if (Has(name, "Detail")) d.Unmapped("legacy detail texture");
            return d;
        }

        private static bool Has(string name, string part) => name.IndexOf(part, StringComparison.Ordinal) >= 0;
    }

    /// <summary>"Unlit/*" shaders.</summary>
    public sealed class BuiltInUnlitReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.BuiltInUnlit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            string name = s.ShaderName ?? string.Empty;
            var d = new MaterialDescription
            {
                Shading = ShadingModel.Unlit,
                BaseColor = s.ColorOr("_Color", ColorValue.White),
                BaseMap = s.GetTexture("_MainTex") ?? new TextureSlot(),
                RenderQueue = s.RenderQueue,
                ReceiveShadows = false,
            };
            if (name.IndexOf("Cutout", StringComparison.Ordinal) >= 0)
            {
                d.Surface = SurfaceType.Cutout;
                d.AlphaClip = true;
                d.AlphaCutoff = s.FloatOr("_Cutoff", 0.5f);
            }
            else if (name.IndexOf("Transparent", StringComparison.Ordinal) >= 0)
            {
                d.Surface = SurfaceType.Fade;
            }
            return d;
        }
    }

    /// <summary>Legacy, mobile and Standard particle shaders.</summary>
    public sealed class BuiltInParticlesReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.BuiltInParticles;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            string name = s.ShaderName ?? string.Empty;
            var d = new MaterialDescription
            {
                Shading = ShadingModel.Unlit,
                VertexColor = true,
                Cull = FaceCull.Off,
                BaseMap = s.GetTexture("_MainTex") ?? new TextureSlot(),
                RenderQueue = s.RenderQueue,
                ReceiveShadows = false,
            };

            if (name.StartsWith("Particles/Standard", StringComparison.Ordinal))
            {
                d.BaseColor = s.ColorOr("_Color", ColorValue.White);
                d.Cull = s.CullOr(FaceCull.Back, "_Cull");
                if (name.IndexOf("Surface", StringComparison.Ordinal) >= 0)
                {
                    d.Shading = ShadingModel.Metallic;
                    d.Metallic = s.FloatOr("_Metallic", 0f);
                    d.Smoothness = s.FloatOr("_Glossiness", 0.5f);
                    d.NormalMap = s.Tex("_BumpMap");
                    d.NormalScale = s.FloatOr("_BumpScale", 1f);
                }
                if (s.IsKeywordEnabled("_EMISSION"))
                {
                    d.EmissionColor = s.ColorOr("_EmissionColor", ColorValue.Black);
                    d.EmissionMap = s.Tex("_EmissionMap");
                    d.EmissionEnabled = !d.EmissionColor.IsBlack;
                }
                // What renders is the blend state the inspector wrote from _Mode, so follow that.
                bool clip = s.IsKeywordEnabled("_ALPHATEST_ON");
                d.Surface = SurfaceState.FromBlend((int)s.FloatOr("_SrcBlend", 1f), (int)s.FloatOr("_DstBlend", 0f), clip);
                if (clip) { d.AlphaClip = true; d.AlphaCutoff = s.FloatOr("_Cutoff", 0.5f); }
                if (s.FloatOr("_BlendOp", 0f) >= 1f) d.Unmapped("subtractive blending (shown as alpha blending)");
                if (s.IsKeywordEnabled("_SOFTPARTICLES_ON")) d.Unmapped("soft particles");
                if (s.IsKeywordEnabled("_FADING_ON")) d.Unmapped("camera fading");
                if (s.IsKeywordEnabled("_DISTORTION_ON")) d.Unmapped("distortion");
                if (s.IsKeywordEnabled("_REQUIRE_UV2")) d.Unmapped("flipbook frame blending");
                return d;
            }

            // Legacy and mobile particle shaders. The legacy ones multiply by 2 × _TintColor (the default tint 0.5 means 1×).
            d.BaseColor = s.HasProperty("_TintColor") ? s.GetColor("_TintColor").Scaled(2f).WithAlpha(Math.Min(1f, s.GetColor("_TintColor").A * 2f)) : s.ColorOr("_Color", ColorValue.White);
            if (name.IndexOf("Premultiply", StringComparison.Ordinal) >= 0) d.Surface = SurfaceType.Transparent;
            else if (name.IndexOf("Additive", StringComparison.Ordinal) >= 0) d.Surface = SurfaceType.Additive;
            else if (name.IndexOf("Multiply", StringComparison.Ordinal) >= 0) d.Surface = SurfaceType.Multiply;
            else d.Surface = SurfaceType.Fade;

            if (name.IndexOf("VertexLit", StringComparison.Ordinal) >= 0) d.Shading = ShadingModel.Simple;
            if (name.IndexOf("Soft", StringComparison.Ordinal) >= 0) d.Unmapped("soft additive blend (shown as additive)");
            if (name.IndexOf("Double", StringComparison.Ordinal) >= 0) d.Unmapped("double-strength multiply (shown as multiply)");
            if (name.IndexOf("Anim", StringComparison.Ordinal) >= 0) d.Unmapped("flipbook frame blending");
            if (name.IndexOf("Additive-Multiply", StringComparison.Ordinal) >= 0) d.Unmapped("additive-multiply blend (shown as additive)");
            if (s.HasProperty("_InvFade")) d.Unmapped("soft particles");
            return d;
        }
    }

    /// <summary>
    /// Best effort for shaders no reader knows: picks up the colour, texture, normal map, emission, cutoff and cull
    /// under the property names most shaders use, so a stand-in keeps the look as far as it can.
    /// </summary>
    public sealed class GenericReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => true;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            string name = s.ShaderName ?? string.Empty;
            string color = s.First("_Color", "_BaseColor", "_MainColor", "_TintColor", "_UnlitColor");
            string tex = FirstWithTexture(s, "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_MainTexture", "_UnlitColorMap");
            string normal = FirstWithTexture(s, "_BumpMap", "_NormalMap");
            string emissionColor = s.First("_EmissionColor", "_EmissiveColor", "_Emission");
            string emissionMap = FirstWithTexture(s, "_EmissionMap", "_EmissiveColorMap", "_EmissionTex");
            string cutoff = s.First("_Cutoff", "_AlphaCutoff", "_Clip");

            var d = new MaterialDescription
            {
                Shading = name.IndexOf("unlit", StringComparison.OrdinalIgnoreCase) >= 0 ? ShadingModel.Unlit : ShadingModel.Metallic,
                BaseColor = color != null ? s.GetColor(color) : ColorValue.White,
                BaseMap = (tex != null ? s.GetTexture(tex) : null) ?? new TextureSlot(),
                NormalMap = normal != null ? s.Tex(normal) : null,
                Metallic = s.FloatOr("_Metallic", 0f),
                Smoothness = s.HasProperty("_Glossiness") ? s.GetFloat("_Glossiness") : s.FloatOr("_Smoothness", 0.3f),
                Cull = s.CullOr(FaceCull.Back, "_Cull", "_CullMode"),
                RenderQueue = s.RenderQueue,
            };
            if (color != null && d.BaseColor.A <= 0f && d.BaseColor.MaxRgb > 0f) d.BaseColor = d.BaseColor.WithAlpha(1f);

            if (emissionColor != null)
            {
                ColorValue e = s.GetColor(emissionColor);
                if (!e.IsBlack)
                {
                    d.EmissionEnabled = true;
                    d.EmissionColor = e;
                    d.EmissionMap = emissionMap != null ? s.Tex(emissionMap) : null;
                }
            }

            string renderType = s.GetTag("RenderType");
            int queue = s.RenderQueue;
            if (queue >= Queues.Transparent || renderType == "Transparent") d.Surface = SurfaceType.Fade;
            else if (queue >= Queues.AlphaTest || renderType == "TransparentCutout") { d.Surface = SurfaceType.Cutout; d.AlphaClip = true; }
            if (cutoff != null) d.AlphaCutoff = s.GetFloat(cutoff);

            d.Unmapped("custom shader effects (approximated with a standard look)");
            return d;
        }

        /// <summary>The first of <paramref name="names"/> that holds a texture, else the first that exists.</summary>
        private static string FirstWithTexture(IMaterialPropertySource s, params string[] names)
        {
            foreach (string n in names)
                if (s.Tex(n) != null) return n;
            return s.First(names);
        }
    }
}
