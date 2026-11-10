using System;
using System.Collections.Generic;

namespace ParelVR.Rendering
{
    public struct ShaderPropertyInfo
    {
        public string Name;
        public ShaderPropertyKind Kind;

        public ShaderPropertyInfo(string name, ShaderPropertyKind kind)
        {
            Name = name;
            Kind = kind;
        }
    }

    /// <summary>
    /// What PVRP knows about a shader, without any engine types, so the classifier and the mapping rules can be tested
    /// outside Unity. The runtime fills it from a UnityEngine.Shader; the Editor can also fill it from a material whose
    /// shader is missing, using the material's saved properties.
    /// </summary>
    public sealed class ShaderInfo
    {
        public string Name = string.Empty;
        /// <summary>False when the shader can't run on this GPU / graphics API, or failed to compile.</summary>
        public bool IsSupported = true;
        /// <summary>True when the shader object itself is missing (only the material's saved properties are known).</summary>
        public bool IsMissing;
        public int RenderQueue = 2000;
        /// <summary>
        /// The SubShader "RenderPipeline" tag, e.g. "UniversalPipeline" or "HDRenderPipeline". Empty when at least one
        /// SubShader has no such tag, because that SubShader can run under the Built-in pipeline.
        /// </summary>
        public string RenderPipelineTag = string.Empty;
        public string RenderTypeTag = string.Empty;
        /// <summary>Distinct pass "LightMode" tags over every SubShader.</summary>
        public readonly List<string> LightModes = new List<string>();
        public readonly List<ShaderPropertyInfo> Properties = new List<ShaderPropertyInfo>();
        public readonly List<string> Keywords = new List<string>();
        /// <summary>Editor only: where the shader file lives. Empty for built-in shaders and in players.</summary>
        public string AssetPath = string.Empty;

        public bool HasProperty(string name)
        {
            for (int i = 0; i < Properties.Count; i++)
                if (string.Equals(Properties[i].Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        public bool HasLightMode(string lightMode)
        {
            for (int i = 0; i < LightModes.Count; i++)
                if (string.Equals(LightModes[i], lightMode, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public bool HasKeyword(string keyword)
        {
            for (int i = 0; i < Keywords.Count; i++)
                if (string.Equals(Keywords[i], keyword, StringComparison.Ordinal)) return true;
            return false;
        }

        public static ShaderInfo Named(string name, params string[] properties)
        {
            var info = new ShaderInfo { Name = name ?? string.Empty };
            if (properties != null)
                foreach (string p in properties) info.Properties.Add(new ShaderPropertyInfo(p, ShaderPropertyKind.Float));
            return info;
        }
    }

    /// <summary>The classifier's verdict on one shader for one target.</summary>
    public sealed class ShaderClassification
    {
        public ShaderFamily Family;
        public RenderPipelineKind Pipeline;
        public CompatibilityLevel Level;
        /// <summary>The ParelVR role a conversion or fallback would use. Null when the shader is kept as it is.</summary>
        public ShaderRole? SuggestedRole;
        /// <summary>True for shaders whose single-pass-instanced (stereo) support PVRP can't confirm.</summary>
        public bool StereoUnverified;
        /// <summary>True when only the creator can fix it (e.g. an unlocked Poiyomi shader, which builds strip).</summary>
        public bool NeedsCreatorAction;
        /// <summary>One plain sentence a creator can read.</summary>
        public string Reason = string.Empty;

        public override string ToString() =>
            $"{Family} ({Pipeline}) level {(int)Level}{(SuggestedRole.HasValue ? " -> " + SuggestedRole.Value : string.Empty)}: {Reason}";
    }
}
