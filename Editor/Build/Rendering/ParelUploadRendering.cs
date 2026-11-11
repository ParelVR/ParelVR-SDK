using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ParelVR.Rendering;
using ParelVR.Rendering.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Build
{
    /// <summary>
    /// The upload step of the ParelVR rendering framework (PVRP). ParelVR renders with Unity's Built-in pipeline, so a
    /// world or avatar made in a URP or HDRP project, or with materials ParelVR can't draw (a missing shader, a shader
    /// with errors), is converted for ParelVR while it is built: materials become ParelVR materials that keep their
    /// colours and textures, and an HDRP world's lights, sky, fog and probes are brought to Built-in values with the
    /// scene's own exposure. It always works on the build's own copy. The creator's scenes, prefabs, materials,
    /// textures and lighting data are never changed. A Built-in world or avatar whose materials all render is built
    /// exactly as before, with no copy and no conversion.
    /// </summary>
    public static class ParelUploadRendering
    {
        public const string WorldFolder = "Assets/ParelVR_Rendering_Build";
        /// <summary>Scene copies other build steps made (Volt). They are already the build's own, so they are converted in place.</summary>
        private static readonly string[] BuildCopyFolders = { "Assets/ParelVR_Volt_Build/", WorldFolder + "/" };

        private static readonly List<CompatibilityReport> Reports = new List<CompatibilityReport>();

        /// <summary>The reports of the last world or avatar build (one per converted scene / avatar).</summary>
        public static IReadOnlyList<CompatibilityReport> LastReports => Reports;

        /// <summary>One line for the build summary, or null when nothing was converted.</summary>
        public static string LastSummary
        {
            get
            {
                if (Reports.Count == 0)
                {
                    RenderPipelineKind p = ParelSceneConverter.ProjectPipeline();
                    if (RunsNatively(p)) return "Rendering: runs on " + CompatibilityReport.PipelineLabel(p) + " in ParelVR, uploaded as it is (nothing converted).";
                }
                int converted = Reports.Sum(r => r.Count(ConversionAction.Converted) + r.Count(ConversionAction.Fallback));
                int hidden = Reports.Sum(r => r.Count(ConversionAction.Unsupported));
                if (converted == 0 && hidden == 0) return null;
                RenderPipelineKind pipeline = Reports.Select(r => r.DetectedPipeline).FirstOrDefault();
                return $"Rendering: {converted} material(s) converted for ParelVR" +
                       (pipeline == RenderPipelineKind.Universal || pipeline == RenderPipelineKind.HighDefinition ? " from " + CompatibilityReport.PipelineLabel(pipeline) : string.Empty) +
                       (hidden > 0 ? $", {hidden} can't be shown (see Logs/PVRP)" : string.Empty) + ". Your project's files were not changed.";
            }
        }

        // ---- worlds -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Called by <see cref="WorldBuilder"/> with the scenes about to be built (MainScene first). Returns the paths to
        /// build: the same scene when nothing needs converting, else a converted copy.
        /// </summary>
        public static string[] PrepareWorld(string[] scenePaths)
        {
            Reports.Clear();
            RenderPipelineKind pipeline = ParelSceneConverter.ProjectPipeline();
            if (RunsNatively(pipeline)) return PrepareNativeWorld(scenePaths, pipeline);
            var result = (string[])scenePaths.Clone();
            for (int i = 0; i < scenePaths.Length; i++)
            {
                string path = scenePaths[i];
                if (pipeline == RenderPipelineKind.BuiltIn && !NeedsConversion(AssetDatabase.GetDependencies(path, true))) continue;

                string copy = BuildCopyFolders.Any(f => path.StartsWith(f, StringComparison.Ordinal)) ? path : CopyScene(path);
                Scene scene = SceneManager.GetSceneByPath(copy);
                bool opened = !scene.IsValid() || !scene.isLoaded;
                if (opened) scene = EditorSceneManager.OpenScene(copy, OpenSceneMode.Additive);
                try
                {
                    CompatibilityReport report = ParelSceneConverter.ConvertScene(scene, new SceneConversionOptions
                    {
                        Target = TargetPlatform.Pc,
                        GeneratedFolder = WorldFolder + "/Generated",
                        SourcePipeline = pipeline,
                    });
                    report.Title = Path.GetFileNameWithoutExtension(path);
                    EditorSceneManager.SaveScene(scene);
                    Log(report, "Upload-World-" + report.Title);
                }
                finally
                {
                    if (opened) EditorSceneManager.CloseScene(scene, true);
                }
                result[i] = copy;
            }
            return result;
        }

        /// <summary>Called when the world build is over: removes the converted copies and their materials.</summary>
        public static void FinishWorld()
        {
            if (AssetDatabase.IsValidFolder(WorldFolder)) AssetDatabase.DeleteAsset(WorldFolder);
            if (AssetDatabase.IsValidFolder(ParelPipelineSwitch.UrpFolder)) AssetDatabase.DeleteAsset(ParelPipelineSwitch.UrpFolder);
            if (AssetDatabase.IsValidFolder(ParelPipelineSwitch.HdrpFolder)) AssetDatabase.DeleteAsset(ParelPipelineSwitch.HdrpFolder);
        }

        // ---- true URP / HDRP ----------------------------------------------------------------------------------------

        /// <summary>The URP / HDRP version the game runs. A world made with another version may not draw right.</summary>
        public const string GamePipelineVersion = "17.4.0";
        public const string GameUnityVersion = "6000.4";

        /// <summary>
        /// URP worlds, and HDRP worlds for PC, run in the game on their own pipeline: nothing is converted. HDRP can't
        /// run on standalone headsets, so an HDRP world built for Android is still converted for Built-in.
        /// </summary>
        public static bool RunsNatively(RenderPipelineKind pipeline) =>
            pipeline == RenderPipelineKind.Universal ||
            (pipeline == RenderPipelineKind.HighDefinition && EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android);

        /// <summary>
        /// Builds the world as it is, on its own pipeline. The scenes are copied into the pipeline's upload folder,
        /// which is how the game knows the pipeline before loading them.
        /// </summary>
        private static string[] PrepareNativeWorld(string[] scenePaths, RenderPipelineKind pipeline)
        {
            string folder = pipeline == RenderPipelineKind.HighDefinition ? ParelPipelineSwitch.HdrpFolder : ParelPipelineSwitch.UrpFolder;
            CheckPipelineVersion(pipeline);
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var result = new string[scenePaths.Length];
            for (int i = 0; i < scenePaths.Length; i++)
            {
                string copy = folder + "/" + Path.GetFileName(scenePaths[i]);
                if (!AssetDatabase.CopyAsset(scenePaths[i], copy)) throw new Exception("Could not copy " + Path.GetFileNameWithoutExtension(scenePaths[i]) + " for building.");
                result[i] = copy;
            }
            Debug.Log("[ParelVR SDK] Rendering: this world runs on " + CompatibilityReport.PipelineLabel(pipeline) +
                      " in ParelVR. Its materials, shaders, lights and volumes are uploaded as they are (nothing converted).");
            return result;
        }

        private static void CheckPipelineVersion(RenderPipelineKind pipeline)
        {
            string id = pipeline == RenderPipelineKind.HighDefinition ? "com.unity.render-pipelines.high-definition" : "com.unity.render-pipelines.universal";
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + id + "/package.json");
            string version = info != null ? info.version : "unknown";
            if (version != GamePipelineVersion)
                Debug.LogWarning("[ParelVR SDK] Rendering: your project has " + CompatibilityReport.PipelineLabel(pipeline) + " " + version + ", ParelVR runs " +
                                 GamePipelineVersion + ". Shaders made with another version may not draw right in the game; install " + GamePipelineVersion + " in the Package Manager.");
            if (!Application.unityVersion.StartsWith(GameUnityVersion, StringComparison.Ordinal))
                Debug.LogWarning("[ParelVR SDK] Rendering: your project uses Unity " + Application.unityVersion + ", ParelVR runs Unity " + GameUnityVersion +
                                 ". A " + CompatibilityReport.PipelineLabel(pipeline) + " world should be made with the same Unity version.");
        }

        private static string CopyScene(string path)
        {
            if (!AssetDatabase.IsValidFolder(WorldFolder)) AssetDatabase.CreateFolder("Assets", Path.GetFileName(WorldFolder));
            string copy = WorldFolder + "/" + Path.GetFileName(path);
            AssetDatabase.DeleteAsset(copy);
            if (!AssetDatabase.CopyAsset(path, copy)) throw new Exception("Could not copy " + Path.GetFileNameWithoutExtension(path) + " for building.");
            return copy;
        }

        // ---- avatars ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Converts the materials of an avatar's build copy (not yet saved as a prefab). Generated materials go to
        /// <paramref name="generatedFolder"/>, which the avatar build deletes afterwards. Returns a note for the build
        /// summary, or null when nothing changed.
        /// </summary>
        public static string ConvertAvatar(GameObject copy, string generatedFolder)
        {
            Reports.Clear();
            if (copy == null) return null;
            RenderPipelineKind pipeline = ParelSceneConverter.ProjectPipeline();
            var materials = copy.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            if (pipeline == RenderPipelineKind.BuiltIn && !NeedsConversion(materials)) return null;
            CompatibilityReport report = ParelSceneConverter.ConvertObject(copy, new SceneConversionOptions
            {
                Target = TargetPlatform.Pc,
                GeneratedFolder = generatedFolder,
                SourcePipeline = pipeline,
            });
            Log(report, "Upload-Avatar-" + copy.name);
            return LastSummary;
        }

        // ---- shared -------------------------------------------------------------------------------------------------

        /// <summary>True when one of the materials among <paramref name="assetPaths"/> would not render in ParelVR as it is.</summary>
        public static bool NeedsConversion(IEnumerable<string> assetPaths) =>
            NeedsConversion(assetPaths.Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)).Select(AssetDatabase.LoadAssetAtPath<Material>));

        public static bool NeedsConversion(IEnumerable<Material> materials)
        {
            ParelRenderManager.Initialize();
            foreach (Material m in materials)
            {
                if (m == null) continue;
                bool broken = m.shader == null || m.shader.name == ShaderClassifier.ErrorShaderName || ShaderUtil.ShaderHasError(m.shader);
                ShaderClassification c = ParelRenderManager.Classify(m, TargetPlatform.Pc);
                if (RuntimeRepair.Decide(c, broken, RepairPolicy.ForeignPipelines) != RepairAction.Keep) return true;
            }
            return false;
        }

        private static void Log(CompatibilityReport report, string fileName)
        {
            Reports.Add(report);
            string file = ParelRenderingScan.Save(report, fileName + ".txt");
            int converted = report.Count(ConversionAction.Converted) + report.Count(ConversionAction.Fallback);
            string line = $"[ParelVR SDK] Rendering ({report.Title}, made for {CompatibilityReport.PipelineLabel(report.DetectedPipeline)}): " +
                          $"{converted} material(s) converted, {report.Count(ConversionAction.Preserved)} kept, {report.Count(ConversionAction.Unsupported)} can't be shown. " +
                          "Your project's files were not changed. Report: " + Path.GetFullPath(file);
            if (report.HasErrors) Debug.LogWarning(line);
            else Debug.Log(line);
        }
    }
}
