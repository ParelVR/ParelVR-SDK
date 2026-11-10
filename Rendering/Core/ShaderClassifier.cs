using System;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Sorts a shader into a <see cref="ShaderFamily"/>, works out which pipeline it was written for, and decides how
    /// ParelVR can use it on a given target. Name rules come first (Unity's and known third-party shader names), then the
    /// shader's pipeline tags and pass light modes, and for a missing shader the material's saved property names.
    /// </summary>
    public sealed class ShaderClassifier
    {
        public const string ErrorShaderName = "Hidden/InternalErrorShader";

        public ShaderClassification Classify(ShaderInfo shader, TargetPlatform target)
        {
            if (shader == null) throw new ArgumentNullException(nameof(shader));

            ShaderFamily family = IdentifyFamily(shader);
            RenderPipelineKind pipeline = PipelineOf(family, shader);
            var result = new ShaderClassification { Family = family, Pipeline = pipeline };

            if (family == ShaderFamily.ErrorShader)
                return Unsupported(result, shader, "The shader is missing or failed to compile, so this would render magenta.");
            if (shader.IsMissing)
            {
                // Only the saved properties are left. A family recognised from them can still be converted.
                if (family == ShaderFamily.Unknown)
                    return Unsupported(result, shader, "The material's shader is missing and its settings don't match a known shader; the closest ParelVR shader is used.");
                if (pipeline == RenderPipelineKind.BuiltIn)
                    return Convert(result, DefaultRole(family, shader), "The material's shader is missing; its saved settings are carried over to a ParelVR shader.");
            }
            else if (!shader.IsSupported)
            {
                return Unsupported(result, shader, "The shader can't run on this device's graphics hardware.");
            }

            switch (family)
            {
                case ShaderFamily.ParelVR:
                case ShaderFamily.ParelVRMobile:
                    return Direct(result, "ParelVR shader.");

                case ShaderFamily.BuiltInStandard:
                case ShaderFamily.BuiltInStandardSpecular:
                    return target == TargetPlatform.Pc
                        ? Direct(result, "Unity Built-in shader.")
                        : Convert(result, ShaderRole.Lit, "Standard is too heavy for standalone headsets; it maps onto ParelVR Lit.");

                case ShaderFamily.BuiltInLegacyDiffuse:
                case ShaderFamily.BuiltInLegacySpecular:
                case ShaderFamily.BuiltInLegacyBumpedDiffuse:
                case ShaderFamily.BuiltInLegacyBumpedSpecular:
                    return target == TargetPlatform.Pc
                        ? Direct(result, "Unity Built-in legacy shader.")
                        : Convert(result, ShaderRole.SimpleLit, "Legacy lit shader; maps onto ParelVR Simple Lit for standalone headsets.");

                case ShaderFamily.BuiltInLegacyTransparent:
                    return target == TargetPlatform.Pc
                        ? Direct(result, "Unity Built-in legacy shader.")
                        : Convert(result, ShaderRole.Transparent, "Legacy transparent shader; maps onto ParelVR Transparent.");

                case ShaderFamily.BuiltInLegacyCutout:
                    return target == TargetPlatform.Pc
                        ? Direct(result, "Unity Built-in legacy shader.")
                        : Convert(result, ShaderRole.Cutout, "Legacy cutout shader; maps onto ParelVR Cutout.");

                case ShaderFamily.BuiltInLegacyOther:
                    return target == TargetPlatform.Pc
                        ? Direct(result, "Unity Built-in legacy shader.")
                        : Convert(result, GuessRole(shader), "Legacy shader; the closest ParelVR shader is used on standalone headsets.");

                case ShaderFamily.BuiltInParticles:
                    if (target == TargetPlatform.Quest && shader.Name.StartsWith("Particles/Standard", StringComparison.Ordinal))
                        return Convert(result, shader.Name.IndexOf("Unlit", StringComparison.Ordinal) >= 0 ? ShaderRole.ParticlesUnlit : ShaderRole.ParticlesLit,
                            "Standard particle shader; maps onto ParelVR Particles for standalone headsets.");
                    return Direct(result, "Unity Built-in particle shader.");

                case ShaderFamily.BuiltInTerrain:
                    if (target == TargetPlatform.Quest && shader.Name.IndexOf("Diffuse", StringComparison.Ordinal) < 0)
                        return Convert(result, ShaderRole.Terrain, "Terrain shader; maps onto ParelVR Terrain for standalone headsets.");
                    return Direct(result, "Unity Built-in terrain shader.");

                case ShaderFamily.BuiltInUnlit:
                case ShaderFamily.BuiltInMobile:
                case ShaderFamily.BuiltInNature:
                case ShaderFamily.BuiltInSkybox:
                case ShaderFamily.BuiltInUI:
                case ShaderFamily.BuiltInSprites:
                case ShaderFamily.BuiltInHidden:
                    return Direct(result, "Unity Built-in shader.");

                case ShaderFamily.TextMeshPro:
                case ShaderFamily.UniUnlit:
                    return Direct(result, "Supported text / unlit shader.");

                case ShaderFamily.Poiyomi:
                    if (target == TargetPlatform.Quest)
                        return Convert(result, ShaderRole.Toon, "Poiyomi can't run on standalone headsets; maps onto the ParelVR toon shader.");
                    if (!IsLockedPoiyomi(shader.Name))
                    {
                        result.NeedsCreatorAction = true;
                        return Direct(result, "Poiyomi shader is not locked. Unlocked Poiyomi shaders are removed from builds and render magenta; lock the material before uploading.");
                    }
                    return Direct(result, "Locked Poiyomi shader.");

                case ShaderFamily.LilToon:
                    return target == TargetPlatform.Pc
                        ? Direct(result, "lilToon shader.")
                        : Convert(result, ShaderRole.Toon, "lilToon can't run on standalone headsets; maps onto the ParelVR toon shader.");

                case ShaderFamily.MToon:
                    if (pipeline == RenderPipelineKind.Universal)
                        return Convert(result, target == TargetPlatform.Pc ? ShaderRole.Lit : ShaderRole.Toon,
                            "URP version of MToon; ParelVR uses the Built-in MToon or its own toon shader.");
                    return target == TargetPlatform.Pc
                        ? Direct(result, "MToon shader.")
                        : Convert(result, ShaderRole.Toon, "MToon maps onto the ParelVR toon shader on standalone headsets.");

                case ShaderFamily.UrpLit:
                case ShaderFamily.UrpComplexLit:
                    return Convert(result, ShaderRole.Lit, "URP shader; ParelVR converts it to ParelVR Lit.");
                case ShaderFamily.UrpSimpleLit:
                case ShaderFamily.UrpBakedLit:
                    return Convert(result, ShaderRole.SimpleLit, "URP shader; ParelVR converts it to ParelVR Simple Lit.");
                case ShaderFamily.UrpUnlit:
                    return Convert(result, ShaderRole.Unlit, "URP shader; ParelVR converts it to ParelVR Unlit.");
                case ShaderFamily.UrpParticlesLit:
                case ShaderFamily.UrpParticlesSimpleLit:
                    return Convert(result, ShaderRole.ParticlesLit, "URP particle shader; ParelVR converts it to ParelVR Particles Lit.");
                case ShaderFamily.UrpParticlesUnlit:
                    return Convert(result, ShaderRole.ParticlesUnlit, "URP particle shader; ParelVR converts it to ParelVR Particles Unlit.");
                case ShaderFamily.UrpTerrain:
                    return Convert(result, ShaderRole.Terrain, "URP terrain shader; ParelVR converts it to ParelVR Terrain.");

                case ShaderFamily.HdrpLit:
                case ShaderFamily.HdrpLayeredLit:
                    return Convert(result, ShaderRole.Lit, "HDRP shader; ParelVR converts it to ParelVR Lit.");
                case ShaderFamily.HdrpUnlit:
                    return Convert(result, ShaderRole.Unlit, "HDRP shader; ParelVR converts it to ParelVR Unlit.");
                case ShaderFamily.HdrpTerrain:
                    return Convert(result, ShaderRole.Terrain, "HDRP terrain shader; ParelVR converts it to ParelVR Terrain.");
                case ShaderFamily.HdrpDecal:
                    result.SuggestedRole = null;
                    result.Level = CompatibilityLevel.Unsupported;
                    result.Reason = "HDRP decals have no Built-in equivalent; the decal is not shown. Bake it into the surface texture or use a transparent quad.";
                    return result;

                case ShaderFamily.UrpOther:
                case ShaderFamily.HdrpOther:
                    return Unsupported(result, shader, "This " + (family == ShaderFamily.UrpOther ? "URP" : "HDRP") + " shader has no automatic conversion; the closest ParelVR shader is used.");

                case ShaderFamily.ShaderGraph:
                    if (pipeline == RenderPipelineKind.BuiltIn || pipeline == RenderPipelineKind.Any)
                        return target == TargetPlatform.Pc ? DirectStereoUnverified(result, "Shader Graph with a Built-in target.") : Unsupported(result, shader, "Custom Shader Graph; the closest ParelVR shader is used on standalone headsets.");
                    return Unsupported(result, shader, "This Shader Graph was made for another render pipeline; add a Built-in target to the graph, or the closest ParelVR shader is used.");

                case ShaderFamily.CustomBuiltIn:
                    return target == TargetPlatform.Pc
                        ? DirectStereoUnverified(result, "Custom Built-in shader.")
                        : Unsupported(result, shader, "Custom shader; its cost on standalone headsets is unknown, so the closest ParelVR shader is used.");

                default:
                    if (pipeline == RenderPipelineKind.BuiltIn || pipeline == RenderPipelineKind.Any)
                        return target == TargetPlatform.Pc
                            ? DirectStereoUnverified(result, "Custom shader.")
                            : Unsupported(result, shader, "Custom shader; its cost on standalone headsets is unknown, so the closest ParelVR shader is used.");
                    return Unsupported(result, shader, "This shader was made for another render pipeline and has no automatic conversion; the closest ParelVR shader is used.");
            }
        }

        // ---- family ---------------------------------------------------------------------------------------------

        public static ShaderFamily IdentifyFamily(ShaderInfo shader)
        {
            string name = shader.Name ?? string.Empty;
            if (name.Length == 0 || shader.IsMissing)
            {
                ShaderFamily bySignature = IdentifyBySavedProperties(shader);
                if (bySignature != ShaderFamily.Unknown || name.Length == 0) return bySignature;
            }
            ShaderFamily byName = IdentifyByName(name);
            if (byName != ShaderFamily.Unknown) return byName;

            switch (PipelineFromTags(shader))
            {
                case RenderPipelineKind.BuiltIn: return ShaderFamily.CustomBuiltIn;
                case RenderPipelineKind.Universal: return ShaderFamily.UrpOther;
                case RenderPipelineKind.HighDefinition: return ShaderFamily.HdrpOther;
                default: return ShaderFamily.Unknown;
            }
        }

        public static ShaderFamily IdentifyByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return ShaderFamily.Unknown;
            if (name == ErrorShaderName) return ShaderFamily.ErrorShader;

            // Third-party names first: some of them live under Hidden/ or Unlit/ style prefixes.
            if (Contains(name, ".poiyomi/") || name.StartsWith("poiyomi", StringComparison.OrdinalIgnoreCase)) return ShaderFamily.Poiyomi;
            if (Contains(name, "liltoon") || name.StartsWith("_lil/", StringComparison.Ordinal)) return ShaderFamily.LilToon;
            if (Contains(name, "mtoon")) return ShaderFamily.MToon;
            if (name.StartsWith("TextMeshPro/", StringComparison.Ordinal)) return ShaderFamily.TextMeshPro;
            if (name == "UniGLTF/UniUnlit") return ShaderFamily.UniUnlit;
            if (name.StartsWith("Shader Graphs/", StringComparison.Ordinal)) return ShaderFamily.ShaderGraph;

            if (name.StartsWith("ParelVR/Mobile/", StringComparison.Ordinal)) return ShaderFamily.ParelVRMobile;
            if (name.StartsWith("ParelVR/", StringComparison.Ordinal)) return ShaderFamily.ParelVR;

            if (name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
            {
                string rest = name.Substring("Universal Render Pipeline/".Length);
                switch (rest)
                {
                    case "Lit": return ShaderFamily.UrpLit;
                    case "Simple Lit": return ShaderFamily.UrpSimpleLit;
                    case "Complex Lit": return ShaderFamily.UrpComplexLit;
                    case "Baked Lit": return ShaderFamily.UrpBakedLit;
                    case "Unlit": return ShaderFamily.UrpUnlit;
                    case "Particles/Lit": return ShaderFamily.UrpParticlesLit;
                    case "Particles/Simple Lit": return ShaderFamily.UrpParticlesSimpleLit;
                    case "Particles/Unlit": return ShaderFamily.UrpParticlesUnlit;
                    case "Terrain/Lit": return ShaderFamily.UrpTerrain;
                    default: return ShaderFamily.UrpOther;
                }
            }

            if (name.StartsWith("HDRP/", StringComparison.Ordinal))
            {
                string rest = name.Substring("HDRP/".Length);
                switch (rest)
                {
                    case "Lit":
                    case "LitTessellation": return ShaderFamily.HdrpLit;
                    case "LayeredLit":
                    case "LayeredLitTessellation": return ShaderFamily.HdrpLayeredLit;
                    case "Unlit": return ShaderFamily.HdrpUnlit;
                    case "TerrainLit": return ShaderFamily.HdrpTerrain;
                    case "Decal": return ShaderFamily.HdrpDecal;
                    default: return ShaderFamily.HdrpOther;
                }
            }

            switch (name)
            {
                case "Standard": return ShaderFamily.BuiltInStandard;
                case "Standard (Specular setup)": return ShaderFamily.BuiltInStandardSpecular;
                case "Legacy Shaders/Diffuse": return ShaderFamily.BuiltInLegacyDiffuse;
                case "Legacy Shaders/Specular": return ShaderFamily.BuiltInLegacySpecular;
                case "Legacy Shaders/Bumped Diffuse": return ShaderFamily.BuiltInLegacyBumpedDiffuse;
                case "Legacy Shaders/Bumped Specular": return ShaderFamily.BuiltInLegacyBumpedSpecular;
            }
            if (name.StartsWith("Legacy Shaders/Particles/", StringComparison.Ordinal)) return ShaderFamily.BuiltInParticles;
            if (name.StartsWith("Legacy Shaders/Transparent/Cutout/", StringComparison.Ordinal)) return ShaderFamily.BuiltInLegacyCutout;
            if (name.StartsWith("Legacy Shaders/Transparent/", StringComparison.Ordinal)) return ShaderFamily.BuiltInLegacyTransparent;
            if (name.StartsWith("Legacy Shaders/", StringComparison.Ordinal)) return ShaderFamily.BuiltInLegacyOther;
            if (name.StartsWith("Mobile/Particles/", StringComparison.Ordinal)) return ShaderFamily.BuiltInParticles;
            if (name.StartsWith("Mobile/", StringComparison.Ordinal)) return ShaderFamily.BuiltInMobile;
            if (name.StartsWith("Particles/", StringComparison.Ordinal)) return ShaderFamily.BuiltInParticles;
            if (name.StartsWith("Unlit/", StringComparison.Ordinal)) return ShaderFamily.BuiltInUnlit;
            if (name.StartsWith("Nature/Terrain/", StringComparison.Ordinal)) return ShaderFamily.BuiltInTerrain;
            if (name.StartsWith("Nature/", StringComparison.Ordinal)) return ShaderFamily.BuiltInNature;
            if (name.StartsWith("Skybox/", StringComparison.Ordinal)) return ShaderFamily.BuiltInSkybox;
            if (name.StartsWith("UI/", StringComparison.Ordinal) || name.StartsWith("GUI/", StringComparison.Ordinal)) return ShaderFamily.BuiltInUI;
            if (name.StartsWith("Sprites/", StringComparison.Ordinal)) return ShaderFamily.BuiltInSprites;
            if (name.StartsWith("Hidden/", StringComparison.Ordinal)) return ShaderFamily.BuiltInHidden;
            return ShaderFamily.Unknown;
        }

        /// <summary>
        /// For a material whose shader is gone, the saved property names still tell the main shader families apart.
        /// Only the distinctive names each family is known to save are used.
        /// </summary>
        public static ShaderFamily IdentifyBySavedProperties(ShaderInfo shader)
        {
            bool Has(string p) => shader.HasProperty(p);

            // HDRP
            if (Has("_LayerCount") && Has("_BaseColorMap0")) return ShaderFamily.HdrpLayeredLit;
            if (Has("_UnlitColorMap") || (Has("_UnlitColor") && Has("_SurfaceType"))) return ShaderFamily.HdrpUnlit;
            if (Has("_BaseColorMap") && (Has("_MaskMap") || Has("_NormalMap")) && Has("_SurfaceType")) return ShaderFamily.HdrpLit;

            // URP
            if (Has("_BaseMap") && Has("_Surface"))
            {
                if (Has("_SoftParticlesNearFadeDistance") || Has("_ColorMode"))
                    return Has("_Metallic") || Has("_BumpMap") || Has("_SpecColor") ? ShaderFamily.UrpParticlesLit : ShaderFamily.UrpParticlesUnlit;
                if (Has("_WorkflowMode")) return Has("_ClearCoatMask") ? ShaderFamily.UrpComplexLit : ShaderFamily.UrpLit;
                if (Has("_SpecColor") || Has("_SpecGlossMap")) return ShaderFamily.UrpSimpleLit;
                if (!Has("_Metallic") && !Has("_BumpMap")) return ShaderFamily.UrpUnlit;
                return ShaderFamily.UrpLit;
            }

            // Built-in Standard
            if (Has("_MainTex") && Has("_Mode") && Has("_Glossiness"))
                return Has("_SpecGlossMap") && !Has("_MetallicGlossMap") ? ShaderFamily.BuiltInStandardSpecular : ShaderFamily.BuiltInStandard;

            return ShaderFamily.Unknown;
        }

        // ---- pipeline -------------------------------------------------------------------------------------------

        public static RenderPipelineKind PipelineOf(ShaderFamily family, ShaderInfo shader)
        {
            switch (family)
            {
                case ShaderFamily.UrpLit:
                case ShaderFamily.UrpSimpleLit:
                case ShaderFamily.UrpComplexLit:
                case ShaderFamily.UrpBakedLit:
                case ShaderFamily.UrpUnlit:
                case ShaderFamily.UrpParticlesLit:
                case ShaderFamily.UrpParticlesSimpleLit:
                case ShaderFamily.UrpParticlesUnlit:
                case ShaderFamily.UrpTerrain:
                case ShaderFamily.UrpOther:
                    return RenderPipelineKind.Universal;
                case ShaderFamily.HdrpLit:
                case ShaderFamily.HdrpLayeredLit:
                case ShaderFamily.HdrpUnlit:
                case ShaderFamily.HdrpTerrain:
                case ShaderFamily.HdrpDecal:
                case ShaderFamily.HdrpOther:
                    return RenderPipelineKind.HighDefinition;
                case ShaderFamily.MToon:
                    return Contains(shader.Name, "Universal Render Pipeline") || Contains(shader.Name, "URP")
                        ? RenderPipelineKind.Universal
                        : RenderPipelineKind.BuiltIn;
                case ShaderFamily.ShaderGraph:
                case ShaderFamily.Unknown:
                case ShaderFamily.CustomBuiltIn:
                    return PipelineFromTags(shader);
                case ShaderFamily.ErrorShader:
                    return RenderPipelineKind.Unknown;
                default:
                    return RenderPipelineKind.BuiltIn;
            }
        }

        private static readonly string[] BuiltInLightModes = { "ForwardBase", "ForwardAdd", "Deferred", "PrepassBase", "PrepassFinal", "Vertex", "VertexLM", "VertexLMRGBM" };
        private static readonly string[] UniversalLightModes = { "UniversalForward", "UniversalForwardOnly", "UniversalGBuffer", "Universal2D" };
        private static readonly string[] HdrpLightModes = { "ForwardOnly", "GBuffer", "Forward", "TransparentDepthPrepass", "TransparentBackface", "DistortionVectors" };

        /// <summary>Which pipeline a shader's own tags and pass light modes point to.</summary>
        public static RenderPipelineKind PipelineFromTags(ShaderInfo shader)
        {
            foreach (string mode in BuiltInLightModes)
                if (shader.HasLightMode(mode)) return RenderPipelineKind.BuiltIn;

            string tag = shader.RenderPipelineTag ?? string.Empty;
            if (Contains(tag, "Universal")) return RenderPipelineKind.Universal;
            if (Contains(tag, "HDRenderPipeline") || Contains(tag, "HighDefinition")) return RenderPipelineKind.HighDefinition;

            foreach (string mode in UniversalLightModes)
                if (shader.HasLightMode(mode)) return RenderPipelineKind.Universal;
            foreach (string mode in HdrpLightModes)
                if (shader.HasLightMode(mode)) return RenderPipelineKind.HighDefinition;

            // No pipeline tag and only pipeline-neutral passes (Always, ShadowCaster, Meta, or none at all).
            return shader.IsMissing ? RenderPipelineKind.Unknown : RenderPipelineKind.Any;
        }

        // ---- roles ----------------------------------------------------------------------------------------------

        /// <summary>The ParelVR role that looks closest, from the shader's name, queue, tags and properties.</summary>
        public static ShaderRole GuessRole(ShaderInfo shader)
        {
            string name = shader.Name ?? string.Empty;
            string type = shader.RenderTypeTag ?? string.Empty;

            if (Contains(name, "skybox") || type == "Background" || (shader.RenderQueue > 0 && shader.RenderQueue <= 1000)) return ShaderRole.Skybox;
            if (Contains(name, "particle")) return Contains(name, "lit") && !Contains(name, "unlit") ? ShaderRole.ParticlesLit : ShaderRole.ParticlesUnlit;
            if (Contains(name, "terrain")) return ShaderRole.Terrain;
            if (Contains(name, "water")) return ShaderRole.Water;
            if (shader.RenderQueue >= 3000 || type == "Transparent") return ShaderRole.Transparent;
            if ((shader.RenderQueue >= 2450 && shader.RenderQueue < 3000) || type == "TransparentCutout") return ShaderRole.Cutout;
            if (Contains(name, "unlit")) return ShaderRole.Unlit;
            return ShaderRole.Lit;
        }

        // ---- helpers --------------------------------------------------------------------------------------------

        public static bool IsLockedPoiyomi(string name) =>
            name != null && name.StartsWith("Hidden/Locked/", StringComparison.Ordinal);

        /// <summary>The role a known Built-in family converts to before the material's own blend mode refines it.</summary>
        public static ShaderRole DefaultRole(ShaderFamily family, ShaderInfo shader)
        {
            switch (family)
            {
                case ShaderFamily.BuiltInStandard:
                case ShaderFamily.BuiltInStandardSpecular: return ShaderRole.Lit;
                case ShaderFamily.BuiltInLegacyDiffuse:
                case ShaderFamily.BuiltInLegacySpecular:
                case ShaderFamily.BuiltInLegacyBumpedDiffuse:
                case ShaderFamily.BuiltInLegacyBumpedSpecular: return ShaderRole.SimpleLit;
                case ShaderFamily.BuiltInLegacyTransparent: return ShaderRole.Transparent;
                case ShaderFamily.BuiltInLegacyCutout: return ShaderRole.Cutout;
                case ShaderFamily.BuiltInUnlit:
                case ShaderFamily.BuiltInMobile: return ShaderRole.Unlit;
                case ShaderFamily.BuiltInParticles: return ShaderRole.ParticlesUnlit;
                case ShaderFamily.BuiltInTerrain: return ShaderRole.Terrain;
                case ShaderFamily.BuiltInSkybox: return ShaderRole.Skybox;
                case ShaderFamily.Poiyomi:
                case ShaderFamily.LilToon:
                case ShaderFamily.MToon: return ShaderRole.Toon;
                default: return GuessRole(shader);
            }
        }

        private static bool Contains(string text, string part) =>
            text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private static ShaderClassification Direct(ShaderClassification r, string reason)
        {
            r.Level = CompatibilityLevel.Direct;
            r.SuggestedRole = null;
            r.Reason = reason;
            return r;
        }

        private static ShaderClassification DirectStereoUnverified(ShaderClassification r, string reason)
        {
            Direct(r, reason + " Its single-pass stereo support has not been checked.");
            r.StereoUnverified = true;
            return r;
        }

        private static ShaderClassification Convert(ShaderClassification r, ShaderRole role, string reason)
        {
            r.Level = CompatibilityLevel.Convertible;
            r.SuggestedRole = role;
            r.Reason = reason;
            return r;
        }

        private static ShaderClassification Unsupported(ShaderClassification r, ShaderInfo shader, string reason)
        {
            r.Level = CompatibilityLevel.Unsupported;
            r.SuggestedRole = GuessRole(shader);
            r.Reason = reason;
            return r;
        }
    }
}
