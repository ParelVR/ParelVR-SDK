using System.IO;
using ParelVR.SDK.Build.Quest;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Build
{
    public static class WorldBuilder
    {
        /// <summary>Worlds are always built for PC. The Meta / Standalone VR version is made by ParelVR's servers.</summary>
        public const BuildTarget Target = BuildTarget.StandaloneWindows64;

        /// <summary>
        /// Lets another part of the SDK prepare what goes into the bundle. It is given the saved scene's asset
        /// path and returns the path of the scene to build (the same one, or a prepared copy).
        /// </summary>
        public static System.Func<string, string> PrepareScene;
        /// <summary>Called when the build is over, with the bundle's path or null when it failed.</summary>
        public static System.Action<string> Finished;
        /// <summary>Called after the bundle was uploaded: world id, platform, bundle path. What it uploads belongs to that bundle.</summary>
        public static System.Func<string, string, string, System.Threading.CancellationToken, System.Threading.Tasks.Task> AfterBundleUploaded;

        /// <summary>The platform the last bundle was built for ("windows").</summary>
        public static string BuiltPlatform { get; private set; }

        /// <summary>
        /// Set before <see cref="BuildWorldBundle"/> to also pack what the bundle was built from for the Meta / Standalone VR
        /// version. After the build, <see cref="QuestSource"/> is that package (null when it wasn't asked for or failed).
        /// </summary>
        public static bool ExportQuestSource;
        public static ParelQuestSourceResult QuestSource { get; private set; }
        public static string QuestSourceError { get; private set; }

        /// <summary>The platform name the backend files a bundle under, or null when ParelVR has no client for the target.</summary>
        public static string PlatformName(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64: return "windows";
                case BuildTarget.Android: return "android";
                default: return null;
            }
        }

        public static string DisplayName(BuildTarget target)
        {
            switch (PlatformName(target))
            {
                case "windows": return "PC / PCVR";
                case "android": return "Meta / Standalone VR";
                default: return target.ToString();
            }
        }

        /// <summary>Where Volt writes a bundle's script package (see VoltWorldBuild.Finished); only there when the world has scripts.</summary>
        public static string VoltPackagePathFor(string bundlePath) => bundlePath + ".voltpkg";

        /// <summary>True when the editor is set to build for PC, which is what worlds are built on.</summary>
        public static bool EditorOnPc => PlatformName(EditorUserBuildSettings.activeBuildTarget) == "windows";

        public static string BuildWorldBundle()
        {
            string built = null;
            BuiltPlatform = PlatformName(Target);
            QuestSource = null;
            QuestSourceError = null;
            try
            {
                built = Build(Target);
                return built;
            }
            finally
            {
                ExportQuestSource = false;
                Finished?.Invoke(built);
            }
        }

        private static string Build(BuildTarget target)
        {
            var activeScene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(activeScene.path))
            {
                throw new System.Exception("Scene is not saved. Please save your scene before building.");
            }

            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveScene(activeScene))
            {
                throw new System.Exception("Failed to save scene.");
            }

            string outputDir = Path.Combine(Application.dataPath, "..", "Builds", "ParelVR");
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            // A script package left from an earlier build would otherwise be taken for this build's.
            string oldVoltPackage = VoltPackagePathFor(Path.Combine(outputDir, "worldbundle"));
            if (File.Exists(oldVoltPackage)) File.Delete(oldVoltPackage);

            string scenePath = PrepareScene != null ? PrepareScene(activeScene.path) : activeScene.path;
            ParelMipStreamingOff.TurnOffFor(new[] { scenePath });

            var buildMap = new AssetBundleBuild[1];
            buildMap[0] = new AssetBundleBuild
            {
                assetBundleName = "worldbundle",
                assetNames = new[] { scenePath }
            };

            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                buildMap,
                BuildAssetBundleOptions.ForceRebuildAssetBundle,
                target
            );

            if (manifest == null)
            {
                throw new System.Exception("BuildAssetBundles failed. Check the Unity Console for errors.");
            }

            // The scene that was built (a prepared copy when the world has scripts) still exists here: Finished deletes it.
            if (ExportQuestSource)
            {
                try
                {
                    string lighting = UnityEditor.Lightmapping.lightingDataAsset != null
                        ? AssetDatabase.GetAssetPath(UnityEditor.Lightmapping.lightingDataAsset)
                        : null;
                    QuestSource = ParelQuestSource.Export(
                        ParelQuestSource.WorldKind,
                        scenePath,
                        Path.Combine(outputDir, "worldbundle.questsource.unitypackage"),
                        activeScene.GetRootGameObjects(),
                        RenderSettings.skybox,
                        string.IsNullOrEmpty(lighting) ? null : new[] { lighting });
                }
                catch (System.Exception ex)
                {
                    // The PC world still publishes; only the Meta / Standalone VR version waits for the next publish.
                    QuestSourceError = ex.Message;
                    Debug.LogError("[ParelVR SDK] Could not pack the world for Meta / Standalone VR: " + ex);
                }
            }

            return Path.Combine(outputDir, "worldbundle");
        }
    }
}
