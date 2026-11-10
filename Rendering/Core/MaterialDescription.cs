using System.Collections.Generic;

namespace ParelVR.Rendering
{
    /// <summary>An RGBA colour without engine types. Values are stored exactly as the material stores them.</summary>
    public struct ColorValue
    {
        public float R, G, B, A;

        public ColorValue(float r, float g, float b, float a = 1f)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static ColorValue White => new ColorValue(1f, 1f, 1f, 1f);
        public static ColorValue Black => new ColorValue(0f, 0f, 0f, 1f);
        public static ColorValue Clear => new ColorValue(0f, 0f, 0f, 0f);

        public float MaxRgb => R > G ? (R > B ? R : B) : (G > B ? G : B);
        public bool IsBlack => R <= 0f && G <= 0f && B <= 0f;

        public ColorValue WithAlpha(float a) => new ColorValue(R, G, B, a);
        public ColorValue Scaled(float s) => new ColorValue(R * s, G * s, B * s, A);

        /// <summary>
        /// Encodes a linear colour (an [HDR] property, as URP and HDRP store emission) for a plain colour property, which
        /// Unity decodes from sRGB before the shader sees it in a linear project such as ParelVR. Values above 1 follow
        /// the same curve, matching Unity's own GammaToLinearSpace, so the shader receives the original linear value.
        /// </summary>
        public ColorValue LinearToGamma() => new ColorValue(Encode(R), Encode(G), Encode(B), A);

        private static float Encode(float v)
        {
            if (v <= 0f) return 0f;
            if (v <= 0.0031308f) return v * 12.92f;
            return 1.055f * (float)System.Math.Pow(v, 1.0 / 2.4) - 0.055f;
        }

        public override string ToString() => $"({R:0.###}, {G:0.###}, {B:0.###}, {A:0.###})";
    }

    /// <summary>
    /// A texture reference plus its tiling and offset. <see cref="Texture"/> is the engine object (a UnityEngine.Texture
    /// at runtime); Core never looks inside it, it only carries it from the source material to the new one.
    /// </summary>
    public sealed class TextureSlot
    {
        public object Texture;
        public float ScaleX = 1f, ScaleY = 1f, OffsetX, OffsetY;

        public TextureSlot() { }

        public TextureSlot(object texture)
        {
            Texture = texture;
        }

        public bool HasTexture => Texture != null;

        public TextureSlot Copy() => new TextureSlot(Texture) { ScaleX = ScaleX, ScaleY = ScaleY, OffsetX = OffsetX, OffsetY = OffsetY };
    }

    /// <summary>
    /// A pipeline-neutral description of what a material looks like. Readers fill it from Built-in, URP and HDRP
    /// materials; writers turn it into a ParelVR shader material. Anything a reader can't express here goes into
    /// <see cref="UnmappedFeatures"/> so the report can tell the creator what changed.
    /// </summary>
    public sealed class MaterialDescription
    {
        public string Name = string.Empty;
        public string SourceShader = string.Empty;
        public ShaderFamily SourceFamily;

        public ShadingModel Shading = ShadingModel.Metallic;
        public SurfaceType Surface = SurfaceType.Opaque;
        public FaceCull Cull = FaceCull.Back;
        /// <summary>-1 means the target shader's own queue for its surface type.</summary>
        public int RenderQueue = -1;
        public bool AlphaClip;
        public float AlphaCutoff = 0.5f;
        public bool ReceiveShadows = true;
        public bool VertexColor;

        // Base
        public ColorValue BaseColor = ColorValue.White;
        public TextureSlot BaseMap;

        // Normal
        public TextureSlot NormalMap;
        public float NormalScale = 1f;

        // Metallic workflow. Either MetallicGlossMap (R metal, A smoothness) + OcclusionMap (G),
        // or a packed MaskMap in the HDRP layout (R metal, G occlusion, B detail mask, A smoothness).
        public float Metallic;
        public float Smoothness = 0.5f;
        public SmoothnessSource SmoothnessFrom = SmoothnessSource.Value;
        public TextureSlot MetallicGlossMap;
        public TextureSlot MaskMap;
        public float MetallicRemapMin, MetallicRemapMax = 1f;
        public float SmoothnessRemapMin, SmoothnessRemapMax = 1f;
        public float OcclusionRemapMin, OcclusionRemapMax = 1f;

        // Specular workflow
        public ColorValue SpecularColor = new ColorValue(0.2f, 0.2f, 0.2f, 1f);
        public TextureSlot SpecularGlossMap;
        /// <summary>Blinn-Phong exponent / 128, as the legacy and Simple Lit shaders use it.</summary>
        public float Shininess = 0.078125f;
        public bool SpecularHighlights = true;
        public bool GlossyReflections = true;

        // Occlusion
        public TextureSlot OcclusionMap;
        public float OcclusionStrength = 1f;

        // Emission (HDR colour)
        public bool EmissionEnabled;
        public ColorValue EmissionColor = ColorValue.Black;
        public TextureSlot EmissionMap;

        // Detail
        public TextureSlot DetailMask;
        public TextureSlot DetailAlbedoMap;
        public TextureSlot DetailNormalMap;
        public float DetailNormalScale = 1f;

        // Height / parallax
        public TextureSlot HeightMap;
        public float HeightScale = 0.02f;

        // Subsurface scattering and transmission (HDRP Subsurface Scattering / Translucent → ParelVR/Subsurface)
        public bool Subsurface;
        public bool Transmission;
        public float SubsurfaceMask = 1f;
        public TextureSlot SubsurfaceMaskMap;
        public TextureSlot ThicknessMap;
        public float ThicknessMin;
        public float ThicknessMax = 1f;
        /// <summary>Colour of the light that scatters under the surface (HDRP: the diffusion profile's scattering distance).</summary>
        public ColorValue SubsurfaceColor = new ColorValue(1f, 0.4f, 0.25f, 1f);
        public ColorValue TransmissionTint = new ColorValue(1f, 0.5f, 0.35f, 1f);
        /// <summary>HDRP's packed reference to the material's diffusion profile asset; the Editor resolves it into the colours above.</summary>
        public ColorValue DiffusionProfileRef;

        // Layers (HDRP Layered Lit → ParelVR/Layered Lit). Empty for single-layer materials.
        public readonly List<LayerDescription> Layers = new List<LayerDescription>();
        public TextureSlot LayerMask;
        /// <summary>0 off, 1 the vertex colour multiplies the layer mask, 2 it is added to it.</summary>
        public int LayerVertexColorMode;

        // Skybox (ParelVR/Skybox): BaseMap holds the sky texture, BaseColor the tint.
        /// <summary>True when the sky texture is a cubemap; false for a 2D panorama.</summary>
        public bool SkyCubemap = true;
        public float SkyExposure = 1f;
        public float SkyRotation;

        /// <summary>Settings the source material uses that the description can't carry, in plain words.</summary>
        public readonly List<string> UnmappedFeatures = new List<string>();

        public bool IsTransparent => Surface == SurfaceType.Fade || Surface == SurfaceType.Transparent || Surface == SurfaceType.Additive || Surface == SurfaceType.Multiply;

        public void Unmapped(string feature)
        {
            if (!string.IsNullOrEmpty(feature) && !UnmappedFeatures.Contains(feature)) UnmappedFeatures.Add(feature);
        }

        /// <summary>Every texture this description refers to, for missing-texture checks.</summary>
        public IEnumerable<KeyValuePair<string, TextureSlot>> Textures()
        {
            if (BaseMap != null) yield return new KeyValuePair<string, TextureSlot>("Base", BaseMap);
            if (NormalMap != null) yield return new KeyValuePair<string, TextureSlot>("Normal", NormalMap);
            if (MetallicGlossMap != null) yield return new KeyValuePair<string, TextureSlot>("Metallic", MetallicGlossMap);
            if (MaskMap != null) yield return new KeyValuePair<string, TextureSlot>("Mask", MaskMap);
            if (SpecularGlossMap != null) yield return new KeyValuePair<string, TextureSlot>("Specular", SpecularGlossMap);
            if (OcclusionMap != null) yield return new KeyValuePair<string, TextureSlot>("Occlusion", OcclusionMap);
            if (EmissionMap != null) yield return new KeyValuePair<string, TextureSlot>("Emission", EmissionMap);
            if (DetailMask != null) yield return new KeyValuePair<string, TextureSlot>("Detail mask", DetailMask);
            if (DetailAlbedoMap != null) yield return new KeyValuePair<string, TextureSlot>("Detail albedo", DetailAlbedoMap);
            if (DetailNormalMap != null) yield return new KeyValuePair<string, TextureSlot>("Detail normal", DetailNormalMap);
            if (HeightMap != null) yield return new KeyValuePair<string, TextureSlot>("Height", HeightMap);
            if (ThicknessMap != null) yield return new KeyValuePair<string, TextureSlot>("Thickness", ThicknessMap);
            if (SubsurfaceMaskMap != null) yield return new KeyValuePair<string, TextureSlot>("Subsurface mask", SubsurfaceMaskMap);
            if (LayerMask != null) yield return new KeyValuePair<string, TextureSlot>("Layer mask", LayerMask);
            for (int i = 0; i < Layers.Count; i++)
            {
                LayerDescription l = Layers[i];
                if (l.BaseMap != null) yield return new KeyValuePair<string, TextureSlot>("Layer " + i + " base", l.BaseMap);
                if (l.NormalMap != null) yield return new KeyValuePair<string, TextureSlot>("Layer " + i + " normal", l.NormalMap);
                if (l.MaskMap != null) yield return new KeyValuePair<string, TextureSlot>("Layer " + i + " mask", l.MaskMap);
            }
        }
    }

    /// <summary>One layer of a layered material. Values are what the layer shows where its mask map is white.</summary>
    public sealed class LayerDescription
    {
        public ColorValue BaseColor = ColorValue.White;
        public TextureSlot BaseMap;
        public TextureSlot NormalMap;
        public float NormalScale = 1f;
        /// <summary>R metallic, G occlusion, A smoothness (HDRP layout).</summary>
        public TextureSlot MaskMap;
        public float MetallicMin, MetallicMax;
        public float SmoothnessMin = 0.5f, SmoothnessMax = 0.5f;
        public float OcclusionMin = 1f, OcclusionMax = 1f;
    }
}
