using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.Rendering
{
    /// <summary>Builds a <see cref="ShaderInfo"/> from a loaded shader or material. Results per shader are cached until <see cref="ClearCache"/>.</summary>
    public static class UnityShaderInspector
    {
        private static readonly ShaderTagId RenderPipelineTag = new ShaderTagId("RenderPipeline");
        private static readonly ShaderTagId RenderTypeTag = new ShaderTagId("RenderType");
        private static readonly ShaderTagId LightModeTag = new ShaderTagId("LightMode");

        private static readonly Dictionary<Shader, ShaderInfo> Cache = new Dictionary<Shader, ShaderInfo>();

        public static void ClearCache() => Cache.Clear();

        /// <summary>The shader as it is. Null gives a "missing" info with no properties.</summary>
        public static ShaderInfo Inspect(Shader shader)
        {
            if (shader == null) return new ShaderInfo { IsMissing = true, IsSupported = false };
            if (Cache.TryGetValue(shader, out ShaderInfo cached)) return cached;

            var info = new ShaderInfo
            {
                Name = shader.name ?? string.Empty,
                IsSupported = shader.isSupported,
                RenderQueue = shader.renderQueue,
            };

            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                info.Properties.Add(new ShaderPropertyInfo(shader.GetPropertyName(i), Kind(shader.GetPropertyType(i))));

            bool anyWithoutPipelineTag = false;
            string firstPipelineTag = string.Empty;
            for (int s = 0; s < shader.subshaderCount; s++)
            {
                string pipeline = shader.FindSubshaderTagValue(s, RenderPipelineTag).name;
                if (string.IsNullOrEmpty(pipeline)) anyWithoutPipelineTag = true;
                else if (firstPipelineTag.Length == 0) firstPipelineTag = pipeline;

                if (info.RenderTypeTag.Length == 0)
                    info.RenderTypeTag = shader.FindSubshaderTagValue(s, RenderTypeTag).name ?? string.Empty;

                int passes = shader.GetPassCountInSubshader(s);
                for (int p = 0; p < passes; p++)
                {
                    string mode = shader.FindPassTagValue(s, p, LightModeTag).name;
                    if (!string.IsNullOrEmpty(mode) && !info.LightModes.Contains(mode)) info.LightModes.Add(mode);
                }
            }
            info.RenderPipelineTag = anyWithoutPipelineTag || shader.subshaderCount == 0 ? string.Empty : firstPipelineTag;

            foreach (string keyword in shader.keywordSpace.keywordNames)
                info.Keywords.Add(keyword);

#if UNITY_EDITOR
            info.AssetPath = UnityEditor.AssetDatabase.GetAssetPath(shader) ?? string.Empty;
#endif
            Cache[shader] = info;
            return info;
        }

        /// <summary>
        /// The material's shader. When that is missing or Unity's error shader, the result is marked missing and
        /// carries the property names the material still holds, so the classifier can recognise what it used to be.
        /// </summary>
        public static ShaderInfo Inspect(Material material)
        {
            if (material == null) return new ShaderInfo { IsMissing = true, IsSupported = false };
            Shader shader = material.shader;
            if (shader != null && shader.name != ShaderClassifier.ErrorShaderName) return Inspect(shader);

            var info = new ShaderInfo { IsMissing = true, IsSupported = false, RenderQueue = material.renderQueue };
            AddNames(info, material, MaterialPropertyType.Texture, ShaderPropertyKind.Texture);
            AddNames(info, material, MaterialPropertyType.Float, ShaderPropertyKind.Float);
            AddNames(info, material, MaterialPropertyType.Int, ShaderPropertyKind.Int);
            AddNames(info, material, MaterialPropertyType.Vector, ShaderPropertyKind.Vector);
            info.RenderTypeTag = material.GetTag("RenderType", false, string.Empty);
            return info;
        }

        private static void AddNames(ShaderInfo info, Material material, MaterialPropertyType type, ShaderPropertyKind kind)
        {
            string[] names = material.GetPropertyNames(type);
            if (names == null) return;
            foreach (string name in names)
                if (!info.HasProperty(name)) info.Properties.Add(new ShaderPropertyInfo(name, kind));
        }

        private static ShaderPropertyKind Kind(ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color: return ShaderPropertyKind.Color;
                case ShaderPropertyType.Vector: return ShaderPropertyKind.Vector;
                case ShaderPropertyType.Range: return ShaderPropertyKind.Range;
                case ShaderPropertyType.Texture: return ShaderPropertyKind.Texture;
                case ShaderPropertyType.Int: return ShaderPropertyKind.Int;
                default: return ShaderPropertyKind.Float;
            }
        }
    }
}
