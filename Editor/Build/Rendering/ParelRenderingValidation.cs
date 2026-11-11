using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ParelVR.Rendering;
using ParelVR.Rendering.Editor;
using ParelVR.SDK.Core.Validation;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Build
{
    /// <summary>
    /// Turns PVRP compatibility reports into the SDK's validation issues: what will be converted at upload, what can't
    /// be shown, missing textures, and what is over a standalone headset's budget. Nothing here changes the project.
    /// </summary>
    public static class ParelRenderingValidation
    {
        private const int MaxIssues = 25;

        public static void AddPipelineIssues(ValidationReport report, bool world = false)
        {
            RenderPipelineKind pipeline = ParelSceneConverter.ProjectPipeline();
            if (world && ParelUploadRendering.RunsNatively(pipeline))
                report.AddInfo($"This world runs on the {CompatibilityReport.PipelineLabel(pipeline)} in ParelVR: its materials, shaders, lights and " +
                               $"volumes are uploaded as they are. Use {CompatibilityReport.PipelineLabel(pipeline)} {ParelUploadRendering.GamePipelineVersion} " +
                               $"and Unity {ParelUploadRendering.GameUnityVersion} so its shaders match the game.");
            else if (pipeline == RenderPipelineKind.Universal || pipeline == RenderPipelineKind.HighDefinition)
                report.AddInfo($"This project uses the {CompatibilityReport.PipelineLabel(pipeline)}. ParelVR renders with Unity's Built-in pipeline, so your " +
                               "materials (and, for HDRP, lights, sky and fog) are converted when you upload. Your project's files are not changed.");
        }

        /// <summary>Adds the PC report's problems and the Meta / Standalone VR findings for <paramref name="materials"/>.</summary>
        public static void AddMaterialIssues(ValidationReport report, IEnumerable<Material> materials, string scope)
        {
            List<Material> list = materials.Where(m => m != null).Distinct().ToList();
            if (list.Count == 0) return;
            CompatibilityReport pc = ParelRenderingScan.ScanMaterials(list, TargetPlatform.Pc, scope);
            CompatibilityReport quest = ParelRenderingScan.ScanMaterials(list, TargetPlatform.Quest, scope);

            int converted = pc.Count(ConversionAction.Converted) + pc.Count(ConversionAction.Fallback);
            if (converted > 0)
                report.AddInfo($"Rendering: {converted} of {pc.Inspected} material(s) will be converted for ParelVR at upload.");

            int shown = 0;
            foreach (CompatibilityEntry e in pc.Entries.Where(e => e.Severity >= DiagnosticSeverity.Warning).OrderByDescending(e => e.Severity))
            {
                if (shown++ >= MaxIssues) break;
                Material context = AssetDatabase.LoadAssetAtPath<Material>(e.Subject);
                string message = $"Rendering: {e.MaterialName} ({(string.IsNullOrEmpty(e.ShaderName) ? "no shader" : e.ShaderName)}): {e.Message}" +
                                 (e.Unmapped.Count > 0 ? " Not carried over: " + string.Join(", ", e.Unmapped) + "." : string.Empty) +
                                 (e.MissingTextures.Count > 0 ? " Missing textures: " + string.Join(", ", e.MissingTextures) + "." : string.Empty);
                // A warning, never an error: nothing here stops an upload (it gets a stand-in, or is hidden and reported).
                report.AddWarning(message, null, context);
            }
            int more = pc.Entries.Count(e => e.Severity >= DiagnosticSeverity.Warning) - MaxIssues;
            if (more > 0) report.AddInfo($"Rendering: {more} more material note(s). ParelVR > Rendering > Compatibility Report has the full list.");

            int questStandIns = quest.Count(ConversionAction.Fallback);
            if (questStandIns > 0)
                report.AddInfo($"Meta / Standalone VR: {questStandIns} material(s) use shaders a headset can't run and get a simpler look there.");
        }

        /// <summary>Counts triangles, material slots and realtime lights under <paramref name="roots"/> against the headset budget.</summary>
        public static void AddQuestBudgetIssues(ValidationReport report, IEnumerable<GameObject> roots, bool avatar)
        {
            var stats = new QuestContentStats { Avatar = avatar };
            foreach (GameObject root in roots)
            {
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh mesh = r is SkinnedMeshRenderer s ? s.sharedMesh : r is MeshRenderer && r.TryGetComponent(out MeshFilter f) ? f.sharedMesh : null;
                    if (mesh != null)
                        for (int i = 0; i < mesh.subMeshCount; i++)
                            if (mesh.GetTopology(i) == MeshTopology.Triangles) stats.Triangles += mesh.GetIndexCount(i) / 3;
                    stats.MaterialSlots += r.sharedMaterials.Length;
                    if (r is SkinnedMeshRenderer) stats.SkinnedMeshes++;
                }
                foreach (Light l in root.GetComponentsInChildren<Light>(true))
                {
                    if (l.lightmapBakeType == LightmapBakeType.Baked) continue;
                    stats.RealtimeLights++;
                    if (l.renderMode == LightRenderMode.ForcePixel && l.type != LightType.Directional) stats.ForcedPixelLights++;
                }
            }
            foreach (string finding in QuestBudgets.Assess(stats))
                report.AddInfo("Meta / Standalone VR: " + finding);
        }
    }

    /// <summary>The Rendering section of the World checks (Settings &gt; Scan Now and the Builder).</summary>
    public sealed class WorldRenderingValidator : IValidator
    {
        public string Name => "World Rendering";
        public global::ParelVR.SDK.Core.Settings.ParelProjectType RequiredMode => global::ParelVR.SDK.Core.Settings.ParelProjectType.World;

        public Task<ValidationReport> ValidateAsync()
        {
            var report = new ValidationReport();
            ParelRenderingValidation.AddPipelineIssues(report, world: true);
            var roots = new List<GameObject>();
            var materials = new List<Material>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    roots.Add(root);
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) materials.AddRange(r.sharedMaterials);
                }
            }
            if (RenderSettings.skybox != null) materials.Add(RenderSettings.skybox);
            // A URP / HDRP world runs on its own pipeline: its materials aren't converted, so there is nothing to list.
            if (!ParelUploadRendering.RunsNatively(ParelSceneConverter.ProjectPipeline()))
                ParelRenderingValidation.AddMaterialIssues(report, materials, "World materials");
            ParelRenderingValidation.AddQuestBudgetIssues(report, roots, false);
            return Task.FromResult(report);
        }
    }
}
