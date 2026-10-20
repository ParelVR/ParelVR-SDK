using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ParelVR.SDK.Build;
using ParelVR.SDK.Core.Validation;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Worlds.Validation
{
    /// <summary>
    /// Checks the scene for what breaks or slows down a world on Quest. It only reports while Android is the build target.
    /// </summary>
    public class QuestCompatibilityValidator : IValidator
    {
        public string Name => "Quest Compatibility";
        public global::ParelVR.SDK.Core.Settings.ParelProjectType RequiredMode => global::ParelVR.SDK.Core.Settings.ParelProjectType.World;

        private const int MaxTextureSize = 2048;

        public static bool Applies => WorldBuilder.PlatformName(EditorUserBuildSettings.activeBuildTarget) == "android";

        private static string FallbackShaderName => GraphicsSettings.currentRenderPipeline != null
            ? "Universal Render Pipeline/Simple Lit"
            : "Legacy Shaders/Diffuse";

        public Task<ValidationReport> ValidateAsync()
        {
            var report = new ValidationReport();
            if (!Applies) return Task.FromResult(report);

            var renderers = new List<Renderer>();
            var lights = new List<Light>();
            var probes = new List<ReflectionProbe>();
            var objects = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    renderers.AddRange(root.GetComponentsInChildren<Renderer>(true));
                    lights.AddRange(root.GetComponentsInChildren<Light>(true));
                    probes.AddRange(root.GetComponentsInChildren<ReflectionProbe>(true));
                    objects.AddRange(root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject));
                }
            }

            var materials = new HashSet<Material>(renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null));

            CheckBuildSettings(report);
            CheckShaders(report, materials);
            if (RenderSettings.skybox != null) materials.Add(RenderSettings.skybox);
            CheckTextures(report, materials);
            CheckLighting(report, lights, probes);
            CheckMissingScripts(report, objects);

            return Task.FromResult(report);
        }

        private static void CheckBuildSettings(ValidationReport report)
        {
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            if (!apis.Contains(GraphicsDeviceType.Vulkan) || !apis.Contains(GraphicsDeviceType.OpenGLES3))
            {
                report.AddError("Android Graphics APIs must include Vulkan and OpenGLES3, or the world's shaders can show up pink on Quest.", () =>
                {
                    PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                    PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
                });
            }

            if (EditorUserBuildSettings.androidBuildSubtarget != MobileTextureSubtarget.ASTC)
            {
                report.AddWarning("Android texture compression isn't ASTC. Other formats look worse or use more memory on Quest.",
                    () => EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC);
            }
        }

        // ── Shaders ──

        private static void CheckShaders(ValidationReport report, IEnumerable<Material> materials)
        {
            foreach (var group in materials.GroupBy(m => m.shader))
            {
                var problem = ShaderProblem(group.Key);
                if (problem.reason == null) continue;

                var mats = group.ToList();
                string shaderName = group.Key != null ? group.Key.name : "(none)";
                string message = $"Shader '{shaderName}' {problem.reason} ({mats.Count} material(s), e.g. '{mats[0].name}'). " +
                                 $"Auto Fix switches them to {FallbackShaderName}, which also changes how they look on Windows.";
                if (problem.breaks) report.AddError(message, () => SwapToFallback(mats), mats[0]);
                else report.AddWarning(message, () => SwapToFallback(mats), mats[0]);
            }
        }

        private static (string reason, bool breaks) ShaderProblem(Shader shader)
        {
            if (shader == null || shader.name == "Hidden/InternalErrorShader") return ("is missing", true);
            if (ShaderUtil.ShaderHasError(shader)) return ("has compile errors", true);

            string path = AssetDatabase.GetAssetPath(shader);
            if (!path.EndsWith(".shader") || !File.Exists(path)) return (null, false);
            string source = File.ReadAllText(path);
            if (Regex.IsMatch(source, @"#pragma\s+geometry")) return ("uses a geometry stage, which renders broken or very slowly on Quest", true);
            if (Regex.IsMatch(source, @"#pragma\s+(hull|domain)")) return ("uses tessellation, which Quest doesn't support", true);
            if (Regex.IsMatch(source, @"\bGrabPass\b")) return ("uses GrabPass, which is very slow on Quest", false);
            return (null, false);
        }

        private static void SwapToFallback(List<Material> mats)
        {
            var fallback = Shader.Find(FallbackShaderName);
            if (fallback == null)
            {
                Debug.LogError($"[ParelVR] Can't find the shader '{FallbackShaderName}' to switch materials to.");
                return;
            }

            foreach (var mat in mats)
            {
                if (mat == null) continue;
                Texture texture = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                Color? color = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : mat.HasProperty("_Color") ? mat.GetColor("_Color") : (Color?)null;

                Undo.RecordObject(mat, "Make Material Quest Compatible");
                mat.shader = fallback;
                foreach (string prop in new[] { "_BaseMap", "_MainTex" })
                    if (texture != null && mat.HasProperty(prop)) mat.SetTexture(prop, texture);
                foreach (string prop in new[] { "_BaseColor", "_Color" })
                    if (color.HasValue && mat.HasProperty(prop)) mat.SetColor(prop, color.Value);
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
        }

        // ── Textures ──

        private static void CheckTextures(ValidationReport report, IEnumerable<Material> materials)
        {
            var oversized = new List<TextureImporter>();
            var seen = new HashSet<string>();
            foreach (var mat in materials)
            foreach (int id in mat.GetTexturePropertyNameIDs())
            {
                // While Android is the target, a texture's imported size already reflects its Android settings.
                var texture = mat.GetTexture(id) as Texture2D;
                if (texture == null || Mathf.Max(texture.width, texture.height) <= MaxTextureSize) continue;
                string path = AssetDatabase.GetAssetPath(texture);
                if (seen.Add(path) && AssetImporter.GetAtPath(path) is TextureImporter importer)
                    oversized.Add(importer);
            }
            if (oversized.Count == 0) return;

            report.AddWarning($"{oversized.Count} texture(s) are bigger than {MaxTextureSize}px, which can run Quest out of memory. " +
                              $"Auto Fix caps them at {MaxTextureSize} for Android only (Windows is unchanged).", () =>
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var importer in oversized)
                    {
                        var settings = importer.GetPlatformTextureSettings("Android");
                        settings.overridden = true;
                        settings.maxTextureSize = MaxTextureSize;
                        importer.SetPlatformTextureSettings(settings);
                        importer.SaveAndReimport();
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }, AssetDatabase.LoadMainAssetAtPath(oversized[0].assetPath));
        }

        // ── Lighting ──

        private static void CheckLighting(ValidationReport report, List<Light> lights, List<ReflectionProbe> probes)
        {
            var shadowed = lights.Where(l => (l.type == LightType.Point || l.type == LightType.Spot)
                                             && l.lightmapBakeType != LightmapBakeType.Baked
                                             && l.shadows != LightShadows.None).ToList();
            if (shadowed.Count > 0)
            {
                report.AddWarning($"{shadowed.Count} point/spot light(s) cast realtime shadows, which tank Quest's frame rate. Auto Fix turns those shadows off.", () =>
                {
                    foreach (var light in shadowed)
                    {
                        if (light == null) continue;
                        Undo.RecordObject(light, "Disable Realtime Shadows");
                        light.shadows = LightShadows.None;
                    }
                }, shadowed[0]);
            }

            var realtimeProbes = probes.Where(p => p.mode == ReflectionProbeMode.Realtime).ToList();
            if (realtimeProbes.Count > 0)
            {
                report.AddWarning($"{realtimeProbes.Count} reflection probe(s) are Realtime, which re-renders the scene on Quest. Auto Fix sets them to Baked.", () =>
                {
                    foreach (var probe in realtimeProbes)
                    {
                        if (probe == null) continue;
                        Undo.RecordObject(probe, "Bake Reflection Probes");
                        probe.mode = ReflectionProbeMode.Baked;
                    }
                }, realtimeProbes[0]);
            }
        }

        // ── Scripts ──

        private static void CheckMissingScripts(ValidationReport report, List<GameObject> objects)
        {
            var broken = objects.Where(go => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0).ToList();
            if (broken.Count == 0) return;

            report.AddWarning($"{broken.Count} object(s) have missing scripts, which log errors when the world loads. Auto Fix removes them.", () =>
            {
                foreach (var go in broken)
                {
                    if (go == null) continue;
                    Undo.RegisterCompleteObjectUndo(go, "Remove Missing Scripts");
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                }
            }, broken[0]);
        }
    }
}
