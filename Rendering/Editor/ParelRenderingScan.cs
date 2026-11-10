using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// Read-only compatibility scans. Nothing is converted or saved: each scan classifies materials for a target,
    /// plans what PVRP would do, and writes a report to Logs/PVRP/ in the project folder.
    /// </summary>
    public static class ParelRenderingScan
    {
        public const string ReportFolder = "Logs/PVRP";

        /// <summary>Classifies one material asset the way the upload pipeline will, including missing-shader and missing-texture checks.</summary>
        public static CompatibilityEntry Inspect(Material material, TargetPlatform target)
        {
            var saved = new SavedMaterialProperties(material);
            ShaderInfo info = saved.ShaderMissing ? saved.ToMissingShaderInfo() : UnityShaderInspector.Inspect(material.shader);
            ShaderClassification c = ParelRenderManager.Classifier.Classify(info, target);
            IMaterialPropertySource source = saved.ShaderMissing ? (IMaterialPropertySource)saved : new UnityMaterialSource(material);
            CompatibilityEntry entry = MaterialConversionPlanner.Plan(c, source, ParelRenderManager.Mappers).Entry;

            entry.Subject = AssetDatabase.GetAssetPath(material);
            if (saved.ShaderMissing) entry.ShaderName = "(missing shader)";
            if (c.StereoUnverified && string.IsNullOrEmpty(entry.Message)) entry.Message = c.Reason;
            foreach (string missing in saved.MissingTextures)
            {
                entry.MissingTextures.Add(missing);
                if (entry.Severity < DiagnosticSeverity.Warning) entry.Severity = DiagnosticSeverity.Warning;
            }
            return entry;
        }

        public static CompatibilityReport ScanMaterials(IEnumerable<Material> materials, TargetPlatform target, string title)
        {
            ParelRenderManager.Initialize();
            var report = new CompatibilityReport { Title = title, Target = target };
            foreach (Material m in materials)
                if (m != null) report.Add(Inspect(m, target));
            report.DetectedPipeline = report.MajorityPipeline();
            return report;
        }

        /// <summary>Materials whose textures may stay loaded at once during a folder scan.</summary>
        private const int UnloadEvery = 25;

        /// <summary>
        /// Scans every material asset under <paramref name="folders"/>. Reading a material pulls its textures into memory,
        /// so the scan loads materials one at a time and unloads them (and their textures) every <see cref="UnloadEvery"/>
        /// materials. Loading a whole project's materials at once took a long-running Editor past its memory limit.
        /// </summary>
        public static CompatibilityReport ScanFolders(string[] folders, TargetPlatform target, string title)
        {
            ParelRenderManager.Initialize();
            var report = new CompatibilityReport { Title = title, Target = target };
            // One entry per file: a model's embedded materials can each be listed under the model's GUID.
            string[] guids = new HashSet<string>(AssetDatabase.FindAssets("t:Material", folders)).ToArray();
            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    if (i % 10 == 0 && EditorUtility.DisplayCancelableProgressBar("PVRP scan (" + target + ")", title, (float)i / guids.Length))
                    {
                        report.Note(DiagnosticSeverity.Warning, "Scan", $"Cancelled after {i} of {guids.Length} materials.");
                        break;
                    }
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    // A model file can hold several materials; each one is its own entry.
                    foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(path))
                        if (o is Material m) report.Add(Inspect(m, target));
                    if ((i + 1) % UnloadEvery == 0) Unload();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Unload();
            }
            report.DetectedPipeline = report.MajorityPipeline();
            return report;
        }

        private static void Unload()
        {
            EditorUtility.UnloadUnusedAssetsImmediate();
            GC.Collect();
        }

        /// <summary>The materials used by renderers (and the skybox) in the loaded scenes.</summary>
        public static CompatibilityReport ScanOpenScenes(TargetPlatform target)
        {
            var materials = new HashSet<Material>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                        foreach (Material m in r.sharedMaterials)
                            if (m != null) materials.Add(m);
            }
            if (RenderSettings.skybox != null) materials.Add(RenderSettings.skybox);
            CompatibilityReport report = ScanMaterials(materials, target, "Open scenes: " + EditorSceneManager.GetActiveScene().name);
            if (RenderSettings.skybox == null) report.Note(DiagnosticSeverity.Info, "Skybox", "No skybox material is set.");
            return report;
        }

        public static string Save(CompatibilityReport report, string fileName, bool detail = true)
        {
            Directory.CreateDirectory(ReportFolder);
            string path = Path.Combine(ReportFolder, fileName);
            File.WriteAllText(path, report.ToText(detail));
            return path;
        }

        [MenuItem("ParelVR/Rendering/Compatibility Report/Project Materials")]
        private static void MenuProject()
        {
            foreach (TargetPlatform target in new[] { TargetPlatform.Pc, TargetPlatform.Quest })
            {
                CompatibilityReport report = ScanFolders(new[] { "Assets" }, target, "Project materials (Assets)");
                string path = Save(report, "ProjectMaterials-" + target + ".txt");
                Debug.Log(Summary(report) + "\nFull report: " + Path.GetFullPath(path));
            }
        }

        [MenuItem("ParelVR/Rendering/Compatibility Report/Open Scenes")]
        private static void MenuScenes()
        {
            foreach (TargetPlatform target in new[] { TargetPlatform.Pc, TargetPlatform.Quest })
            {
                CompatibilityReport report = ScanOpenScenes(target);
                string path = Save(report, "OpenScenes-" + target + ".txt");
                Debug.Log(Summary(report) + "\nFull report: " + Path.GetFullPath(path));
            }
        }

        [MenuItem("ParelVR/Rendering/Compatibility Report/Selected Materials")]
        private static void MenuSelection()
        {
            var materials = new List<Material>();
            foreach (UnityEngine.Object o in Selection.objects)
                if (o is Material m) materials.Add(m);
            foreach (TargetPlatform target in new[] { TargetPlatform.Pc, TargetPlatform.Quest })
                Debug.Log(ScanMaterials(materials, target, "Selected materials").ToText(detail: true));
        }

        public static string Summary(CompatibilityReport report)
        {
            return RenderingLog.Prefix + report.Title + " (" + report.Target + "): " + report.Inspected + " materials, "
                + report.Count(ConversionAction.Preserved) + " kept, " + report.Count(ConversionAction.Converted) + " converted, "
                + report.Count(ConversionAction.Fallback) + " stand-ins, " + report.Count(ConversionAction.Unsupported) + " unsupported, "
                + report.MissingTextureCount + " missing textures, " + report.Count(DiagnosticSeverity.Warning) + " warnings, "
                + report.Count(DiagnosticSeverity.Error) + " errors.";
        }
    }
}
