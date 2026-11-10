using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// Creates or refreshes the ParelShaderLibrary asset in the SDK's Resources folder, so every ParelVR shader is
    /// referenced explicitly (and therefore kept in builds). Re-run after adding a shader to the library.
    /// </summary>
    public static class ParelShaderLibraryBuilder
    {
        public const string AssetPath = "Packages/com.parelvrsdk.pvr/Rendering/Runtime/Resources/ParelVR/Rendering/ParelShaderLibrary.asset";
        public const string VariantsPath = "Packages/com.parelvrsdk.pvr/Rendering/Runtime/Resources/ParelVR/Rendering/ParelShaderVariants.shadervariants";

        /// <summary>
        /// The material keywords <see cref="ParelMaterialWriter"/> and <see cref="SkyboxWriter"/> can set at runtime. Every
        /// combination of these (that the shader's pass has) goes into the variant collection; detail, parallax and the
        /// highlight/reflection toggles only come from upload-time conversion, whose materials travel with their content.
        /// </summary>
        public static readonly string[] RuntimeKeywords =
        {
            "_NORMALMAP", "_EMISSION", "_METALLICGLOSSMAP", "_SPECGLOSSMAP", "_SPECULAR_SETUP",
            "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON",
            "_GLOSS_FROM_ALBEDO_ALPHA", "_SKYBOX_PANORAMIC",
        };
        private const string Shaders = "Packages/com.parelvrsdk.pvr/Rendering/Shaders/";
        private const string Mobile = "Packages/com.parelvrsdk.pvr/Runtime/Shaders/Mobile/";

        [MenuItem("ParelVR/Rendering/Rebuild Shader Library")]
        public static ParelShaderLibrary Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            AssetDatabase.Refresh();

            var library = AssetDatabase.LoadAssetAtPath<ParelShaderLibrary>(AssetPath);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<ParelShaderLibrary>();
                AssetDatabase.CreateAsset(library, AssetPath);
            }

            // Quest columns stay empty until the Quest shader set is measured on hardware (PVRP phase 8);
            // an empty Quest entry means the PC shader is used there too.
            var rows = new[]
            {
                Row(ShaderRole.Lit, Load(Shaders + "ParelVRLit.shader"), null),
                Row(ShaderRole.SimpleLit, Load(Shaders + "ParelVRSimpleLit.shader"), null),
                Row(ShaderRole.Unlit, Load(Shaders + "ParelVRUnlit.shader"), null),
                Row(ShaderRole.Transparent, Load(Shaders + "ParelVRTransparent.shader"), null),
                Row(ShaderRole.Cutout, Load(Shaders + "ParelVRCutout.shader"), null),
                Row(ShaderRole.ParticlesLit, Load(Shaders + "ParelVRParticlesLit.shader"), null),
                Row(ShaderRole.ParticlesUnlit, Load(Shaders + "ParelVRParticlesUnlit.shader"), null),
                Row(ShaderRole.Emissive, Load(Shaders + "ParelVREmissive.shader"), null),
                Row(ShaderRole.Skybox, Load(Shaders + "ParelVRSkybox.shader"), null),
                // Terrain keeps Unity's own terrain shaders: they are what TerrainData's splat setup is built for.
                Row(ShaderRole.Terrain, Shader.Find("Nature/Terrain/Standard"), Shader.Find("Nature/Terrain/Diffuse")),
                Row(ShaderRole.Toon, Load(Mobile + "ParelMobileToonLit.shader"), null),
            };

            ShaderVariantCollection variants = BuildVariants(rows.SelectMany(r => new[] { r.Pc, r.Quest }));
            var so = new SerializedObject(library);
            so.FindProperty("variants").objectReferenceValue = variants;
            SerializedProperty entries = so.FindProperty("entries");
            entries.arraySize = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                SerializedProperty e = entries.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("Role").intValue = (int)rows[i].Role;
                e.FindPropertyRelative("Pc").objectReferenceValue = rows[i].Pc;
                e.FindPropertyRelative("Quest").objectReferenceValue = rows[i].Quest;
                if (rows[i].Pc == null) Debug.LogWarning(RenderingLog.Prefix + "Shader library: no shader found for " + rows[i].Role);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            ParelShaderLibrary.Active = library;
            Debug.Log(RenderingLog.Prefix + "Shader library written: " + AssetPath + " (" + rows.Length + " roles)");
            return library;
        }

        /// <summary>Writes the variant collection: every runtime keyword combination of every ParelVR shader's passes.</summary>
        public static ShaderVariantCollection BuildVariants(IEnumerable<Shader> shaders)
        {
            var svc = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(VariantsPath);
            if (svc == null)
            {
                svc = new ShaderVariantCollection();
                AssetDatabase.CreateAsset(svc, VariantsPath);
            }
            svc.Clear();
            var allowed = new HashSet<string>(RuntimeKeywords, StringComparer.Ordinal);
            var missed = new List<string>();
            foreach (Shader shader in shaders.Where(s => s != null && s.name.StartsWith("ParelVR/", StringComparison.Ordinal)).Distinct())
            {
                for (int pass = 0; pass < shader.passCount; pass++)
                {
                    PassType? type = PassTypeOf(shader.FindPassTagValue(pass, new ShaderTagId("LightMode")).name);
                    if (!type.HasValue) continue;
                    string[] keywords = ShaderUtil.GetPassKeywords(shader, new PassIdentifier(0, (uint)pass))
                        .Select(k => k.name).Where(allowed.Contains).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
                    for (int mask = 0; mask < 1 << keywords.Length; mask++)
                    {
                        var set = new List<string>();
                        for (int b = 0; b < keywords.Length; b++) if ((mask & (1 << b)) != 0) set.Add(keywords[b]);
                        if (!Coherent(set)) continue;
                        if (!TryAdd(svc, shader, type.Value, set))
                            missed.Add(shader.name + " " + type.Value + " [" + string.Join(" ", set) + "]");
                    }
                }
            }
            EditorUtility.SetDirty(svc);
            Debug.Log(RenderingLog.Prefix + "Variant collection written: " + VariantsPath + " (" + svc.variantCount + " variants, " + svc.shaderCount + " shaders)");
            if (missed.Count > 0)
                Debug.LogWarning(RenderingLog.Prefix + missed.Count + " keyword set(s) matched no variant of their pass and are not kept:\n" + string.Join("\n", missed));
            return svc;
        }

        /// <summary>
        /// A lit pass has no variant without its light keywords (ForwardBase always has DIRECTIONAL or similar), and Unity
        /// rejects an entry that names no real variant. One entry per feature-keyword set is enough: a build keeps every
        /// multi_compile combination of the shader_feature keywords a collection uses.
        /// </summary>
        private static readonly string[][] LightKeywordSets =
        {
            new string[0],
            new[] { "DIRECTIONAL" },
            new[] { "DIRECTIONAL", "LIGHTPROBE_SH" },
            new[] { "DIRECTIONAL", "LIGHTPROBE_SH", "SHADOWS_SCREEN" },
            new[] { "POINT" },
            new[] { "SHADOWS_DEPTH" },
        };

        private static bool TryAdd(ShaderVariantCollection svc, Shader shader, PassType type, List<string> features)
        {
            foreach (string[] light in LightKeywordSets)
            {
                try
                {
                    svc.Add(new ShaderVariantCollection.ShaderVariant(shader, type, features.Concat(light).ToArray()));
                    return true;
                }
                catch (ArgumentException) { } // names no variant of this pass; try the next light keywords
            }
            return false;
        }

        /// <summary>Combinations the writer can't produce are left out.</summary>
        private static bool Coherent(List<string> set)
        {
            bool setup = set.Contains("_SPECULAR_SETUP");
            if (set.Contains("_SPECGLOSSMAP") && !setup) return false;
            if (set.Contains("_METALLICGLOSSMAP") && setup) return false;
            if (set.Contains("_ALPHABLEND_ON") && set.Contains("_ALPHAPREMULTIPLY_ON")) return false;
            return true;
        }

        private static PassType? PassTypeOf(string lightMode)
        {
            // Unity reports pass tags upper-cased ("FORWARDBASE"), whatever case the shader source uses.
            switch ((lightMode ?? string.Empty).ToUpperInvariant())
            {
                case "": case "ALWAYS": return PassType.Normal;
                case "FORWARDBASE": return PassType.ForwardBase;
                case "FORWARDADD": return PassType.ForwardAdd;
                case "SHADOWCASTER": return PassType.ShadowCaster;
                case "VERTEX": return PassType.Vertex;
                default: return null; // Meta and Editor-only passes don't run in players
            }
        }

        private static ParelShaderLibrary.Entry Row(ShaderRole role, Shader pc, Shader quest) =>
            new ParelShaderLibrary.Entry { Role = role, Pc = pc, Quest = quest };

        private static Shader Load(string path) => AssetDatabase.LoadAssetAtPath<Shader>(path);
    }
}
