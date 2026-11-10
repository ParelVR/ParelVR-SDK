using System;

namespace ParelVR.Rendering
{
    /// <summary>Surface settings shared by the URP shaders (_Surface, _Blend, _AlphaClip, _Cull).</summary>
    internal static class UrpSurface
    {
        public static void Read(IMaterialPropertySource s, MaterialDescription d)
        {
            bool transparent = s.FloatOr("_Surface", 0f) > 0.5f || s.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT");
            if (transparent)
            {
                switch ((int)Math.Round(s.FloatOr("_Blend", 0f)))
                {
                    case 1: d.Surface = SurfaceType.Transparent; break;
                    case 2: d.Surface = SurfaceType.Additive; break;
                    case 3: d.Surface = SurfaceType.Multiply; break;
                    default:
                        // URP's "Alpha" keeps highlights at full strength unless Preserve Specular is off (URP 12+).
                        d.Surface = s.FloatOr("_BlendModePreserveSpecular", 1f) > 0.5f && d.Shading != ShadingModel.Unlit ? SurfaceType.Transparent : SurfaceType.Fade;
                        break;
                }
            }
            if (s.FloatOr("_AlphaClip", 0f) > 0.5f || s.IsKeywordEnabled("_ALPHATEST_ON"))
            {
                d.AlphaClip = true;
                d.AlphaCutoff = s.FloatOr("_Cutoff", 0.5f);
                if (!transparent) d.Surface = SurfaceType.Cutout;
            }
            d.Cull = s.CullOr(FaceCull.Back, "_Cull");
            d.ReceiveShadows = s.FloatOr("_ReceiveShadows", 1f) > 0.5f;
            d.RenderQueue = s.RenderQueue;
        }

        public static TextureSlot BaseMap(IMaterialPropertySource s) => s.Tex("_BaseMap") ?? s.Tex("_MainTex") ?? s.GetTexture("_BaseMap") ?? new TextureSlot();

        /// <summary>URP emission is an [HDR] (linear) colour; ParelVR's is a plain one, so it is re-encoded.</summary>
        public static void Emission(IMaterialPropertySource s, MaterialDescription d)
        {
            if (!s.IsKeywordEnabled("_EMISSION")) return;
            ColorValue c = s.ColorOr("_EmissionColor", ColorValue.Black);
            if (c.IsBlack) return;
            d.EmissionEnabled = true;
            d.EmissionColor = c.LinearToGamma();
            d.EmissionMap = s.Tex("_EmissionMap");
        }
    }

    /// <summary>URP Lit and Complex Lit.</summary>
    public sealed class UrpLitReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.UrpLit || family == ShaderFamily.UrpComplexLit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            bool specular = s.FloatOr("_WorkflowMode", 1f) < 0.5f || s.IsKeywordEnabled("_SPECULAR_SETUP");
            var d = new MaterialDescription
            {
                Shading = specular ? ShadingModel.Specular : ShadingModel.Metallic,
                BaseColor = s.ColorOr("_BaseColor", ColorValue.White),
                BaseMap = UrpSurface.BaseMap(s),
                Smoothness = s.FloatOr("_Smoothness", 0.5f),
            };
            UrpSurface.Read(s, d);

            d.NormalMap = s.Tex("_BumpMap");
            d.NormalScale = s.FloatOr("_BumpScale", 1f);

            bool fromAlbedo = s.FloatOr("_SmoothnessTextureChannel", 0f) > 0.5f && !d.IsTransparent && !d.AlphaClip;
            if (s.FloatOr("_SmoothnessTextureChannel", 0f) > 0.5f && !fromAlbedo) d.Unmapped("smoothness from albedo alpha on a see-through material (uses the smoothness value)");
            if (specular)
            {
                d.SpecularColor = s.ColorOr("_SpecColor", new ColorValue(0.2f, 0.2f, 0.2f, 1f));
                d.SpecularGlossMap = s.Tex("_SpecGlossMap");
                d.SmoothnessFrom = fromAlbedo ? SmoothnessSource.AlbedoAlpha : d.SpecularGlossMap != null ? SmoothnessSource.SpecularAlpha : SmoothnessSource.Value;
            }
            else
            {
                d.MetallicGlossMap = s.Tex("_MetallicGlossMap");
                d.Metallic = d.MetallicGlossMap != null ? 1f : s.FloatOr("_Metallic", 0f);
                d.SmoothnessFrom = fromAlbedo ? SmoothnessSource.AlbedoAlpha : d.MetallicGlossMap != null ? SmoothnessSource.MetallicAlpha : SmoothnessSource.Value;
            }

            d.OcclusionMap = s.Tex("_OcclusionMap");
            d.OcclusionStrength = s.FloatOr("_OcclusionStrength", 1f);
            UrpSurface.Emission(s, d);

            d.DetailMask = s.Tex("_DetailMask");
            d.DetailAlbedoMap = s.Tex("_DetailAlbedoMap");
            d.DetailNormalMap = s.Tex("_DetailNormalMap");
            d.DetailNormalScale = s.FloatOr("_DetailNormalMapScale", 1f);
            if (d.DetailAlbedoMap != null && Math.Abs(s.FloatOr("_DetailAlbedoMapScale", 1f) - 1f) > 0.01f) d.Unmapped("detail albedo strength (shown at full strength)");

            d.HeightMap = s.Tex("_ParallaxMap");
            d.HeightScale = s.FloatOr("_Parallax", 0.005f);

            d.SpecularHighlights = s.FloatOr("_SpecularHighlights", 1f) > 0.5f;
            d.GlossyReflections = s.FloatOr("_EnvironmentReflections", 1f) > 0.5f;

            if (s.FloatOr("_ClearCoatMask", 0f) > 0f || s.IsKeywordEnabled("_CLEARCOAT") || s.IsKeywordEnabled("_CLEARCOATMAP")) d.Unmapped("clear coat");
            return d;
        }
    }

    /// <summary>URP Simple Lit and Baked Lit (Blinn-Phong and lightmap-only shading).</summary>
    public sealed class UrpSimpleLitReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.UrpSimpleLit || family == ShaderFamily.UrpBakedLit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            var d = new MaterialDescription
            {
                Shading = ShadingModel.Simple,
                BaseColor = s.ColorOr("_BaseColor", ColorValue.White),
                BaseMap = UrpSurface.BaseMap(s),
            };
            UrpSurface.Read(s, d);
            d.NormalMap = s.Tex("_BumpMap");

            if (family == ShaderFamily.UrpBakedLit)
            {
                d.SpecularHighlights = false;
                d.SpecularColor = ColorValue.Black;
                d.Unmapped("baked-only lighting (real-time lights now light it too)");
                return d;
            }

            bool highlights = s.FloatOr("_SpecularHighlights", 0f) > 0.5f || s.IsKeywordEnabled("_SPECULAR_COLOR") || s.IsKeywordEnabled("_SPECGLOSSMAP");
            d.SpecularHighlights = highlights;
            d.SpecularColor = highlights ? s.ColorOr("_SpecColor", new ColorValue(0.5f, 0.5f, 0.5f, 0.5f)).WithAlpha(1f) : ColorValue.Black;
            // URP Simple Lit turns smoothness into a Blinn-Phong exponent as exp2(10 s + 1); ParelVR Simple Lit uses _Shininess × 128.
            float smoothness = s.FloatOr("_Smoothness", 0.5f);
            d.Shininess = Math.Max(0.03f, Math.Min(1f, (float)Math.Pow(2.0, 10.0 * smoothness + 1.0) / 128f));
            d.SmoothnessFrom = s.FloatOr("_SmoothnessSource", 0f) > 0.5f || s.IsKeywordEnabled("_GLOSSINESS_FROM_BASE_ALPHA") ? SmoothnessSource.AlbedoAlpha : SmoothnessSource.Value;
            if (highlights && s.Tex("_SpecGlossMap") != null) d.Unmapped("specular map (a single specular colour is used)");
            UrpSurface.Emission(s, d);
            return d;
        }
    }

    /// <summary>URP Unlit.</summary>
    public sealed class UrpUnlitReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) => family == ShaderFamily.UrpUnlit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            var d = new MaterialDescription
            {
                Shading = ShadingModel.Unlit,
                BaseColor = s.ColorOr("_BaseColor", ColorValue.White),
                BaseMap = UrpSurface.BaseMap(s),
            };
            UrpSurface.Read(s, d);
            d.ReceiveShadows = false;
            return d;
        }
    }

    /// <summary>URP Particles Lit, Simple Lit and Unlit.</summary>
    public sealed class UrpParticlesReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) =>
            family == ShaderFamily.UrpParticlesLit || family == ShaderFamily.UrpParticlesSimpleLit || family == ShaderFamily.UrpParticlesUnlit;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            var d = new MaterialDescription
            {
                Shading = family == ShaderFamily.UrpParticlesUnlit ? ShadingModel.Unlit : family == ShaderFamily.UrpParticlesSimpleLit ? ShadingModel.Simple : ShadingModel.Metallic,
                VertexColor = true,
                BaseColor = s.ColorOr("_BaseColor", ColorValue.White),
                BaseMap = UrpSurface.BaseMap(s),
            };
            UrpSurface.Read(s, d);
            d.ReceiveShadows = false;
            if (d.Shading == ShadingModel.Metallic)
            {
                d.Metallic = s.FloatOr("_Metallic", 0f);
                d.Smoothness = s.FloatOr("_Smoothness", 0.5f);
                d.NormalMap = s.Tex("_BumpMap");
                d.NormalScale = s.FloatOr("_BumpScale", 1f);
            }
            UrpSurface.Emission(s, d);

            if (s.FloatOr("_ColorMode", 0f) > 0.5f) d.Unmapped("particle colour mode (vertex colour multiplies)");
            if (s.FloatOr("_SoftParticlesEnabled", 0f) > 0.5f) d.Unmapped("soft particles");
            if (s.FloatOr("_CameraFadingEnabled", 0f) > 0.5f) d.Unmapped("camera fading");
            if (s.FloatOr("_DistortionEnabled", 0f) > 0.5f) d.Unmapped("distortion");
            if (s.FloatOr("_FlipbookBlending", 0f) > 0.5f) d.Unmapped("flipbook frame blending");
            return d;
        }
    }

    /// <summary>
    /// Terrain shaders (URP Terrain Lit, HDRP TerrainLit, Built-in): the layers live in TerrainData, so nothing is read
    /// from the material; the terrain shader for the target draws them.
    /// </summary>
    public sealed class TerrainReader : IMaterialReader
    {
        public bool CanRead(ShaderFamily family) =>
            family == ShaderFamily.UrpTerrain || family == ShaderFamily.HdrpTerrain || family == ShaderFamily.BuiltInTerrain;

        public MaterialDescription Read(IMaterialPropertySource s, ShaderFamily family)
        {
            var d = new MaterialDescription { Shading = ShadingModel.Metallic, RenderQueue = -1 };
            if (family == ShaderFamily.HdrpTerrain && s.FloatOr("_EnableHeightBlend", 0f) > 0.5f) d.Unmapped("height-based layer blending");
            if (family != ShaderFamily.BuiltInTerrain && s.FloatOr("_EnableInstancedPerPixelNormal", 0f) > 0.5f) d.Unmapped("per-pixel terrain normals");
            return d;
        }
    }

    /// <summary>Terrain role: Unity's terrain shaders take everything from TerrainData, so there is nothing to write.</summary>
    public sealed class TerrainWriter : IMaterialWriter
    {
        public bool CanWrite(ShaderRole role) => role == ShaderRole.Terrain;
        public void Write(MaterialDescription description, ShaderRole role, TargetPlatform target, IMaterialPropertySink sink) { }
    }
}
