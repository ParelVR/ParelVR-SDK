using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>What a conversion changed, for the creator's Content Manager and the server log.</summary>
    [Serializable]
    public sealed class ParelQuestReport
    {
        public int materialsConverted;
        public int materialsKept;
        public int texturesCompressed;
        public int texturesDownsized;
        public int audioCompressed;
        public int lightsChanged;
        public int componentsRemoved;
        public int particleSystemsCapped;
        public int textureCap;
        public List<string> notes = new List<string>();

        public void Note(string line)
        {
            if (notes.Count < 200) notes.Add(line);
        }

        public string Summary()
        {
            var parts = new List<string>();
            if (materialsConverted > 0) parts.Add(materialsConverted + " material(s) moved to mobile shaders");
            if (texturesCompressed > 0) parts.Add(texturesCompressed + " texture(s) compressed" + (textureCap > 0 ? ", up to " + textureCap + " px" : string.Empty));
            if (audioCompressed > 0) parts.Add(audioCompressed + " sound(s) compressed");
            if (lightsChanged > 0) parts.Add(lightsChanged + " light(s) and probe(s) made cheaper");
            if (componentsRemoved > 0) parts.Add(componentsRemoved + " component(s) left out");
            if (particleSystemsCapped > 0) parts.Add(particleSystemsCapped + " particle system(s) capped");
            return parts.Count == 0 ? "Nothing needed changing." : string.Join("; ", parts) + ".";
        }
    }

    /// <summary>
    /// Turns a copy of a world or avatar into its Meta / Standalone VR version: mobile shaders, compressed and capped
    /// textures, compressed audio, no realtime shadows or realtime reflection probes, no post-processing, capped
    /// particles, and on avatars none of the components a headset can't afford. It edits the assets it is given, so it
    /// only ever runs on a throwaway copy (ParelVR's conversion server imports the creator's upload for that).
    /// </summary>
    public static class ParelQuestConverter
    {
        public const int AvatarTextureCap = 1024;
        public const int WorldTextureCap = 2048;
        public const int AvatarParticleCap = 200;
        public const int WorldParticleCap = 1000;
        public const int RenderTextureCap = 1024;

        private static readonly string[] AvatarComponentsToRemove =
        {
            "UnityEngine.Light", "UnityEngine.Cloth", "UnityEngine.Camera", "UnityEngine.ReflectionProbe", "UnityEngine.Projector",
            "UnityEngine.LensFlare", "UnityEngine.AudioReverbZone"
        };

        private static readonly string[] PostProcessingTypes =
        {
            "UnityEngine.Rendering.PostProcessing.PostProcessVolume", "UnityEngine.Rendering.PostProcessing.PostProcessLayer",
            "UnityEngine.Rendering.Volume"
        };

        // ── materials ──────────────────────────────────────────────────────────────────────────

        /// <summary>Converts every material at <paramref name="materialPaths"/>, including ones only animations swap in.</summary>
        public static void ConvertMaterials(IEnumerable<string> materialPaths, bool avatar, ParelQuestHints hints, ParelQuestReport report)
        {
            foreach (string path in materialPaths)
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) continue;
                ParelQuestMaterialHint hint = hints?.For(path);
                bool skybox = hints != null && hints.skybox == path;
                try
                {
                    if (skybox ? ConvertSkybox(material, report) : ConvertMaterial(material, path, hint, avatar, report))
                        EditorUtility.SetDirty(material);
                    else
                        report.materialsKept++;
                }
                catch (Exception ex)
                {
                    report.Note(path + ": not converted (" + ex.Message + ")");
                }
            }
            AssetDatabase.SaveAssets();
        }

        private static bool ConvertMaterial(Material material, string path, ParelQuestMaterialHint hint, bool avatar, ParelQuestReport report)
        {
            string current = material.shader != null ? material.shader.name : null;
            bool broken = current == null || current == ParelQuestShaders.ErrorShader;
            // The shader's name as the creator's editor saw it, also when the shader didn't import here.
            string original = broken && hint != null && !string.IsNullOrEmpty(hint.shader) ? hint.shader : current;
            string problem = broken ? "is missing" : ParelQuestShaders.Problem(material.shader);
            if (string.IsNullOrEmpty(problem) && hint != null && !string.IsNullOrEmpty(hint.problem)) problem = hint.problem;
            bool particle = hint != null && hint.particle;

            string target = ParelQuestShaders.Replacement(original, problem, avatar, particle);
            if (target == null && broken) target = avatar ? ParelQuestShaders.ToonLit : ParelQuestShaders.StandardLite;
            if (target == null) return false;

            Shader shader = Shader.Find(target);
            if (shader == null)
            {
                report.Note(path + ": the shader " + target + " is missing on the server");
                return false;
            }

            ParelMaterialSnapshot snapshot = ParelMaterialSnapshot.Read(material);
            int queue = hint != null && hint.queue > 0 ? hint.queue : material.renderQueue;
            string[] keywords = hint?.keywords ?? material.shaderKeywords;
            ParelQuestShaders.Mode mode = ParelQuestShaders.ModeOf(queue, keywords, snapshot);
            bool additive = ParelQuestShaders.IsAdditive(original, snapshot);

            material.shader = shader;
            material.shaderKeywords = Array.Empty<string>();
            if (target.StartsWith("ParelVR/Mobile/", StringComparison.Ordinal))
                ApplyMobile(material, snapshot, target, mode, additive, queue);
            report.materialsConverted++;
            report.Note(path + ": " + (original ?? "missing shader") + " -> " + target);
            return true;
        }

        private static void ApplyMobile(Material material, ParelMaterialSnapshot snapshot, string target, ParelQuestShaders.Mode mode, bool additive, int queue)
        {
            ParelMaterialSnapshot.TextureSlot main = snapshot.MainTexture();
            if (main != null)
            {
                material.SetTexture("_MainTex", main.Texture);
                material.SetTextureScale("_MainTex", main.Scale);
                material.SetTextureOffset("_MainTex", main.Offset);
            }
            Color color = snapshot.ColorOf("_Color", "_BaseColor", "_MainColor", "_TintColor", "_Tint", "_AlbedoColor") ?? Color.white;
            material.SetColor("_Color", color);

            if (target == ParelQuestShaders.Particles)
            {
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                material.renderQueue = queue >= 3000 ? queue : 3000;
                return;
            }

            float? cull = snapshot.Float("_Cull", "_CullMode", "_Culling");
            if (cull.HasValue && material.HasProperty("_Cull")) material.SetFloat("_Cull", Mathf.Clamp(Mathf.Round(cull.Value), 0, 2));
            material.SetFloat("_Cutoff", Mathf.Clamp01(snapshot.Float("_Cutoff", "_AlphaCutoff", "_Clip", "_AlphaClipThreshold") ?? 0.5f));

            if (material.HasProperty("_EmissionColor")) ApplyEmission(material, snapshot);
            if (target == ParelQuestShaders.StandardLite) ApplySurface(material, snapshot);

            switch (mode)
            {
                case ParelQuestShaders.Mode.Cutout:
                    SetBlend(material, 1, BlendMode.One, BlendMode.Zero, true);
                    material.EnableKeyword("_ALPHATEST_ON");
                    material.SetOverrideTag("RenderType", "TransparentCutout");
                    material.renderQueue = queue >= 2450 && queue <= 2500 ? queue : 2450;
                    break;
                case ParelQuestShaders.Mode.Transparent:
                    SetBlend(material, 2, additive ? BlendMode.One : BlendMode.SrcAlpha, additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha, false);
                    material.SetOverrideTag("RenderType", "Transparent");
                    material.renderQueue = queue > 2500 ? queue : 3000;
                    break;
                default:
                    SetBlend(material, 0, BlendMode.One, BlendMode.Zero, true);
                    material.SetOverrideTag("RenderType", string.Empty);
                    material.renderQueue = queue >= 1000 && queue < 2450 ? queue : -1;
                    break;
            }
        }

        private static void SetBlend(Material material, int mode, BlendMode src, BlendMode dst, bool zWrite)
        {
            material.SetFloat("_Mode", mode);
            material.SetFloat("_SrcBlend", (float)src);
            material.SetFloat("_DstBlend", (float)dst);
            material.SetFloat("_ZWrite", zWrite ? 1f : 0f);
        }

        private static void ApplyEmission(Material material, ParelMaterialSnapshot snapshot)
        {
            ParelMaterialSnapshot.TextureSlot map = snapshot.Slot("_EmissionMap", "_EmissiveMap", "_EmissionTex", "_EmissiveTex", "_EmissiveColorMap");
            Color color = snapshot.ColorOf("_EmissionColor", "_EmissiveColor", "_Emission") ?? Color.black;
            float strength = snapshot.Float("_EmissionStrength", "_EmissiveIntensity") ?? 1f;
            if (snapshot.HasAnyFloat("_EmissionStrength")) color *= Mathf.Max(0f, strength);

            string[] switches = { "_EnableEmission", "_UseEmission", "_EmissionEnabled", "_EmissionToggle" };
            bool switchedOn = !snapshot.HasAnyFloat(switches) || (snapshot.Float(switches) ?? 0f) > 0.5f;
            bool lit = color.maxColorComponent > 0.001f && switchedOn;
            if (!lit) return;

            if (map != null) material.SetTexture("_EmissionMap", map.Texture);
            material.SetColor("_EmissionColor", color);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
        }

        private static void ApplySurface(Material material, ParelMaterialSnapshot snapshot)
        {
            ParelMaterialSnapshot.TextureSlot normal = snapshot.Slot("_BumpMap", "_NormalMap", "_NormalTexture", "_Normal");
            if (normal != null)
            {
                material.SetTexture("_BumpMap", normal.Texture);
                material.SetFloat("_BumpScale", snapshot.Float("_BumpScale", "_NormalScale", "_NormalStrength") ?? 1f);
                material.EnableKeyword("_NORMALMAP");
            }
            ParelMaterialSnapshot.TextureSlot metallic = snapshot.Slot("_MetallicGlossMap", "_MetallicMap", "_MetallicSmoothness");
            if (metallic != null)
            {
                material.SetTexture("_MetallicGlossMap", metallic.Texture);
                material.EnableKeyword("_METALLICGLOSSMAP");
            }
            material.SetFloat("_Metallic", Mathf.Clamp01(snapshot.Float("_Metallic") ?? 0f));
            material.SetFloat("_Glossiness", Mathf.Clamp01(snapshot.Float("_Glossiness", "_Smoothness", "_GlossMapScale") ?? 0.5f));
            ParelMaterialSnapshot.TextureSlot occlusion = snapshot.Slot("_OcclusionMap", "_AOMap", "_OcclusionTex");
            if (occlusion != null)
            {
                material.SetTexture("_OcclusionMap", occlusion.Texture);
                material.SetFloat("_OcclusionStrength", Mathf.Clamp01(snapshot.Float("_OcclusionStrength") ?? 1f));
            }
        }

        /// <summary>Skyboxes keep a built-in sky shader: a cubemap or panorama sky when the original had one, else as they are.</summary>
        private static bool ConvertSkybox(Material material, ParelQuestReport report)
        {
            string name = material.shader != null ? material.shader.name : null;
            if (name != null && name != ParelQuestShaders.ErrorShader && (name.StartsWith("Skybox/", StringComparison.Ordinal) || ParelQuestShaders.Problem(material.shader) == null))
                return false;

            ParelMaterialSnapshot snapshot = ParelMaterialSnapshot.Read(material);
            KeyValuePair<string, ParelMaterialSnapshot.TextureSlot> cube = snapshot.Textures.FirstOrDefault(t => t.Value.Texture is Cubemap);
            KeyValuePair<string, ParelMaterialSnapshot.TextureSlot> flat = snapshot.Textures.FirstOrDefault(t => t.Value.Texture is Texture2D);
            Color tint = snapshot.ColorOf("_Tint", "_Color", "_SkyTint") ?? new Color(0.5f, 0.5f, 0.5f, 1f);
            Shader shader;
            if (cube.Value != null && (shader = Shader.Find("Skybox/Cubemap")) != null)
            {
                material.shader = shader;
                material.SetTexture("_Tex", cube.Value.Texture);
            }
            else if (flat.Value != null && (shader = Shader.Find("Skybox/Panoramic")) != null)
            {
                material.shader = shader;
                material.SetTexture("_MainTex", flat.Value.Texture);
            }
            else if ((shader = Shader.Find("Skybox/Procedural")) != null)
            {
                material.shader = shader;
            }
            else return false;
            if (material.HasProperty("_Tint")) material.SetColor("_Tint", tint);
            report.materialsConverted++;
            report.Note("skybox: " + (name ?? "missing shader") + " -> " + material.shader.name);
            return true;
        }

        // ── textures, audio, render textures ───────────────────────────────────────────────────

        /// <summary>Gives every texture an Android setting: compressed for the headset and no larger than <paramref name="cap"/>.</summary>
        public static void ConvertTextures(IEnumerable<string> texturePaths, int cap, ParelQuestReport report)
        {
            report.textureCap = cap;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in texturePaths)
                {
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
                    TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
                    int before = android.overridden ? android.maxTextureSize : importer.maxTextureSize;
                    int limit = importer.textureShape == TextureImporterShape.TextureCube ? Math.Min(cap, 1024) : cap;
                    int size = Math.Min(before, limit);

                    android.overridden = true;
                    android.maxTextureSize = size;
                    android.format = TextureImporterFormat.Automatic;
                    android.textureCompression = TextureImporterCompression.Compressed;
                    android.compressionQuality = (int)TextureCompressionQuality.Normal;
                    android.crunchedCompression = false;
                    importer.SetPlatformTextureSettings(android);
                    importer.SaveAndReimport();

                    report.texturesCompressed++;
                    if (size < before) report.texturesDownsized++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>Gives every sound an Android setting: Vorbis, compressed in memory.</summary>
        public static void ConvertAudio(IEnumerable<string> audioPaths, ParelQuestReport report)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in audioPaths)
                {
                    if (!(AssetImporter.GetAtPath(path) is AudioImporter importer)) continue;
                    AudioImporterSampleSettings android = importer.ContainsSampleSettingsOverride("Android")
                        ? importer.GetOverrideSampleSettings("Android")
                        : importer.defaultSampleSettings;
                    android.compressionFormat = AudioCompressionFormat.Vorbis;
                    android.quality = Mathf.Min(android.quality <= 0f ? 1f : android.quality, 0.5f);
                    if (android.loadType == AudioClipLoadType.DecompressOnLoad) android.loadType = AudioClipLoadType.CompressedInMemory;
                    importer.SetOverrideSampleSettings("Android", android);
                    importer.SaveAndReimport();
                    report.audioCompressed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>Render textures the world draws into: no bigger than <see cref="RenderTextureCap"/> a side, no MSAA.</summary>
        public static void ConvertRenderTextures(IEnumerable<string> paths, ParelQuestReport report)
        {
            foreach (string path in paths)
            {
                var texture = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
                if (texture == null) continue;
                int largest = Math.Max(texture.width, texture.height);
                if (largest <= RenderTextureCap && texture.antiAliasing <= 1) continue;
                texture.Release();
                if (largest > RenderTextureCap)
                {
                    float scale = RenderTextureCap / (float)largest;
                    texture.width = Math.Max(16, Mathf.RoundToInt(texture.width * scale));
                    texture.height = Math.Max(16, Mathf.RoundToInt(texture.height * scale));
                }
                texture.antiAliasing = 1;
                EditorUtility.SetDirty(texture);
                report.texturesDownsized++;
                report.Note(path + ": render texture now " + texture.width + "x" + texture.height);
            }
            AssetDatabase.SaveAssets();
        }

        // ── scenes and avatars ─────────────────────────────────────────────────────────────────

        /// <summary>World scene: realtime shadows off, realtime probes baked, post-processing out, lighter terrain, capped particles.</summary>
        public static void ConvertWorldScene(Scene scene, string generatedFolder, ParelQuestReport report)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Light light in root.GetComponentsInChildren<Light>(true))
                {
                    if (light.lightmapBakeType == LightmapBakeType.Baked || light.shadows == LightShadows.None) continue;
                    light.shadows = LightShadows.None;
                    report.lightsChanged++;
                }
                foreach (ReflectionProbe probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                {
                    if (probe.mode != ReflectionProbeMode.Realtime) continue;
                    probe.mode = ReflectionProbeMode.Baked;
                    report.lightsChanged++;
                }
                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true)) ConvertTerrain(terrain, generatedFolder, report);
                CapParticles(root, WorldParticleCap, report);
                RemoveByTypeName(root, PostProcessingTypes, report);
                RemoveMissingScripts(root);
            }
        }

        /// <summary>Avatar (prefab contents): no lights, cloth, cameras or probes; capped particles; four-bone skinning.</summary>
        public static void ConvertAvatar(GameObject root, ParelQuestReport report)
        {
            RemoveByTypeName(root, AvatarComponentsToRemove, report);
            CapParticles(root, AvatarParticleCap, report);
            foreach (SkinnedMeshRenderer skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skinned.quality == SkinQuality.Auto || skinned.quality == SkinQuality.Bone4) continue;
                skinned.quality = SkinQuality.Bone4;
            }
            RemoveMissingScripts(root);
        }

        private static void ConvertTerrain(Terrain terrain, string generatedFolder, ParelQuestReport report)
        {
            terrain.heightmapPixelError = Mathf.Max(terrain.heightmapPixelError, 8f);
            terrain.basemapDistance = Mathf.Min(terrain.basemapDistance, 200f);
            terrain.detailObjectDistance = Mathf.Min(terrain.detailObjectDistance, 40f);
            terrain.treeBillboardDistance = Mathf.Min(terrain.treeBillboardDistance, 50f);
            terrain.shadowCastingMode = ShadowCastingMode.Off;

            // A terrain without its own material draws with Unity's built-in Standard terrain material, the heavy one.
            Material template = terrain.materialTemplate;
            bool builtIn = template == null || !AssetDatabase.GetAssetPath(template).StartsWith("Assets/", StringComparison.Ordinal);
            if (!builtIn) return;
            Shader diffuse = Shader.Find("Nature/Terrain/Diffuse");
            if (diffuse == null) return;
            if (!AssetDatabase.IsValidFolder(generatedFolder)) CreateFolders(generatedFolder);
            var material = new Material(diffuse) { name = "Terrain (standalone)" };
            string path = AssetDatabase.GenerateUniqueAssetPath(generatedFolder + "/Terrain Standalone.mat");
            AssetDatabase.CreateAsset(material, path);
            terrain.materialTemplate = material;
            report.materialsConverted++;
            report.Note(terrain.name + ": terrain -> Nature/Terrain/Diffuse");
        }

        private static void CapParticles(GameObject root, int cap, ParelQuestReport report)
        {
            foreach (ParticleSystem system in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = system.main;
                if (main.maxParticles <= cap) continue;
                main.maxParticles = cap;
                report.particleSystemsCapped++;
            }
        }

        private static void RemoveByTypeName(GameObject root, string[] typeNames, ParelQuestReport report)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                string name = component.GetType().FullName;
                if (!typeNames.Contains(name)) continue;
                report.Note(component.gameObject.name + ": " + component.GetType().Name + " left out");
                UnityEngine.Object.DestroyImmediate(component, true);
                report.componentsRemoved++;
            }
        }

        private static void RemoveMissingScripts(GameObject root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
        }

        public static void CreateFolders(string folder)
        {
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        // ── preview for the SDK ────────────────────────────────────────────────────────────────

        /// <summary>How many of <paramref name="materials"/> the server will move to mobile shaders.</summary>
        public static int CountReplaced(IEnumerable<Material> materials, bool avatar, ISet<Material> particles = null)
        {
            int count = 0;
            foreach (Material material in materials.Where(m => m != null).Distinct())
            {
                string name = material.shader != null ? material.shader.name : null;
                string problem = avatar ? null : ParelQuestShaders.Problem(material.shader);
                if (ParelQuestShaders.Replacement(name, problem, avatar, particles != null && particles.Contains(material)) != null) count++;
            }
            return count;
        }
    }
}
