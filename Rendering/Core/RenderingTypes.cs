namespace ParelVR.Rendering
{
    /// <summary>The render pipeline a shader, material or project was made for.</summary>
    public enum RenderPipelineKind
    {
        Unknown,
        BuiltIn,
        Universal,
        HighDefinition,
        /// <summary>Has no pipeline-specific passes, so it draws under any pipeline.</summary>
        Any,
    }

    /// <summary>Where the content is going to be shown.</summary>
    public enum TargetPlatform
    {
        Pc,
        Quest,
    }

    /// <summary>How far ParelVR can take a material as it is.</summary>
    public enum CompatibilityLevel
    {
        /// <summary>Runs in ParelVR's renderer without changes.</summary>
        Direct = 1,
        /// <summary>A known shader that maps onto a ParelVR shader.</summary>
        Convertible = 2,
        /// <summary>Unknown, advanced or broken: reported, and given a fallback when one fits.</summary>
        Unsupported = 3,
    }

    /// <summary>The shader groups PVRP knows how to read. Anything else is <see cref="Unknown"/>.</summary>
    public enum ShaderFamily
    {
        Unknown,
        /// <summary>Unity's magenta error shader: the real shader is missing or failed to compile.</summary>
        ErrorShader,
        ParelVR,
        ParelVRMobile,

        BuiltInStandard,
        BuiltInStandardSpecular,
        BuiltInLegacyDiffuse,
        BuiltInLegacySpecular,
        BuiltInLegacyBumpedDiffuse,
        BuiltInLegacyBumpedSpecular,
        BuiltInLegacyTransparent,
        BuiltInLegacyCutout,
        BuiltInLegacyOther,
        BuiltInUnlit,
        BuiltInMobile,
        BuiltInParticles,
        BuiltInTerrain,
        BuiltInNature,
        BuiltInSkybox,
        BuiltInUI,
        BuiltInSprites,
        BuiltInHidden,

        UrpLit,
        UrpSimpleLit,
        UrpComplexLit,
        UrpBakedLit,
        UrpUnlit,
        UrpParticlesLit,
        UrpParticlesSimpleLit,
        UrpParticlesUnlit,
        UrpTerrain,
        UrpOther,

        HdrpLit,
        HdrpLayeredLit,
        HdrpUnlit,
        HdrpTerrain,
        HdrpDecal,
        HdrpOther,

        TextMeshPro,
        Poiyomi,
        LilToon,
        MToon,
        UniUnlit,
        ShaderGraph,
        /// <summary>A shader PVRP doesn't know, written for the Built-in pipeline.</summary>
        CustomBuiltIn,
    }

    /// <summary>The jobs the ParelVR shader library covers. Conversions and fallbacks pick one of these.</summary>
    public enum ShaderRole
    {
        Lit,
        SimpleLit,
        Unlit,
        Transparent,
        Cutout,
        ParticlesLit,
        ParticlesUnlit,
        Terrain,
        Skybox,
        Emissive,
        Water,
        /// <summary>Cel-shaded avatar look (Poiyomi, lilToon, MToon) on targets that can't run those shaders.</summary>
        Toon,
        /// <summary>Lit plus subsurface scattering and transmission (HDRP Subsurface Scattering / Translucent).</summary>
        Subsurface,
        /// <summary>Up to 4 blended PBR layers (HDRP Layered Lit).</summary>
        LayeredLit,
    }

    /// <summary>How a surface is blended. Values follow the Built-in Standard shader's modes where they overlap.</summary>
    public enum SurfaceType
    {
        Opaque,
        Cutout,
        /// <summary>Plain alpha blending; the whole surface fades, highlights included.</summary>
        Fade,
        /// <summary>Premultiplied alpha; reflections and highlights stay visible.</summary>
        Transparent,
        Additive,
        Multiply,
    }

    public enum ShadingModel
    {
        Unlit,
        /// <summary>Lambert / Blinn-Phong, as in the legacy and Simple Lit shaders.</summary>
        Simple,
        Metallic,
        Specular,
    }

    /// <summary>Same values as UnityEngine.Rendering.CullMode.</summary>
    public enum FaceCull
    {
        Off = 0,
        Front = 1,
        Back = 2,
    }

    /// <summary>Where a lit surface reads its smoothness from.</summary>
    public enum SmoothnessSource
    {
        Value,
        MetallicAlpha,
        AlbedoAlpha,
        MaskMapAlpha,
        SpecularAlpha,
    }

    /// <summary>What PVRP did with a material.</summary>
    public enum ConversionAction
    {
        /// <summary>Left as it was; it already works.</summary>
        Preserved,
        /// <summary>Mapped onto a ParelVR shader with its settings carried over.</summary>
        Converted,
        /// <summary>Could not be mapped exactly; given the closest ParelVR shader so it doesn't render magenta.</summary>
        Fallback,
        /// <summary>Nothing suitable; reported only.</summary>
        Unsupported,
        Skipped,
    }

    public enum DiagnosticSeverity
    {
        Info,
        Notice,
        Warning,
        Error,
    }

    public enum ShaderPropertyKind
    {
        Color,
        Vector,
        Float,
        Range,
        Texture,
        Int,
    }
}
