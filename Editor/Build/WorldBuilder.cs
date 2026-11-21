using System.IO;
using System.Linq;
using ParelVR.SDK.Build.Quest;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Build
{
    public static class WorldBuilder
    {
        /// <summary>
        /// Worlds are always built for PC here. The Meta / Standalone VR version is built on this PC too, by a background
        /// Unity in Android mode (<see cref="ParelQuestLocalBuild"/>), so the project never switches platform.
        /// </summary>
        public const BuildTarget Target = BuildTarget.StandaloneWindows64;

        /// <summary>
        /// Lets another part of the SDK prepare what goes into the bundle. It is given the asset paths of the world's
        /// saved scenes (MainScene first, then the subrooms) and returns the paths to build, in the same order: the
        /// same scenes, or prepared copies.
        /// </summary>
        public static System.Func<string[], string[]> PrepareScenes;
        /// <summary>Called when the build is over, with the bundle's path or null when it failed.</summary>
        public static System.Action<string> Finished;
        /// <summary>Called after the bundle was uploaded: world id, platform, bundle path. What it uploads belongs to that bundle.</summary>
        public static System.Func<string, string, string, System.Threading.CancellationToken, System.Threading.Tasks.Task> AfterBundleUploaded;

        /// <summary>The platform the last bundle was built for ("windows").</summary>
        public static string BuiltPlatform { get; private set; }

        /// <summary>
        /// Set before <see cref="BuildWorldBundle"/> to also get the Meta / Standalone VR version ready: what the bundle was
        /// built from is copied into the background Android project. After the build, <see cref="QuestJob"/> is that job,
        /// for <see cref="ParelQuestLocalBuild.RunAsync"/> (null when it wasn't asked for or failed: <see cref="QuestJobError"/>).
        /// </summary>
        public static bool BuildQuestVersion;
        public static ParelQuestLocalJob QuestJob { get; private set; }
        public static string QuestJobError { get; private set; }

        /// <summary>
        /// Packs the world's scripts for another platform's bundle (Volt: bundle path, platform -> the package's path, or
        /// null when the world has no scripts). The package is written to <see cref="VoltPackagePathFor"/> of that bundle.
        /// </summary>
        public static System.Func<string, string, string> PackageScripts;

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
            QuestJob = null;
            QuestJobError = null;
            try
            {
                built = Build(Target);
                return built;
            }
            finally
            {
                BuildQuestVersion = false;
                ParelUploadRendering.FinishWorld();
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

            // Auto Port Content: other platforms' components are replaced with ParelVR's before the scene is saved and built.
            ParelVR.SDK.Core.Settings.ParelAutoPortHook.BeforeWorldBuild();

            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveScene(activeScene))
            {
                throw new System.Exception("Failed to save scene.");
            }
            // Subrooms open next to it are built from disk too: their changes are saved first.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene open = SceneManager.GetSceneAt(i);
                if (open.isLoaded && open.isDirty && !string.IsNullOrEmpty(open.path)) UnityEditor.SceneManagement.EditorSceneManager.SaveScene(open);
            }

            // Every enabled scene in Build Profiles goes into the one bundle: MainScene, where players arrive, and the
            // subrooms VoltScene.Switch moves them into.
            var problems = WorldScenes.Problems(activeScene.path);
            if (problems.Count > 0) throw new System.Exception(string.Join("\n", problems));
            string[] worldScenes = WorldScenes.BuildOrder().ToArray();

            string outputDir = Path.Combine(Application.dataPath, "..", "Builds", "ParelVR");
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            // A script package left from an earlier build would otherwise be taken for this build's.
            string oldVoltPackage = VoltPackagePathFor(Path.Combine(outputDir, "worldbundle"));
            if (File.Exists(oldVoltPackage)) File.Delete(oldVoltPackage);

            string[] scenePaths = PrepareScenes != null ? PrepareScenes(worldScenes) : worldScenes;
            if (scenePaths == null || scenePaths.Length != worldScenes.Length) throw new System.Exception("The world's scenes could not be prepared for building.");
            // URP / HDRP worlds and materials ParelVR can't draw: converted on the build's copy (nothing to do for a Built-in world).
            scenePaths = ParelUploadRendering.PrepareWorld(scenePaths);
            string scenePath = scenePaths[0];
            ParelMipStreamingOff.TurnOffFor(scenePaths);
            if (scenePaths.Length > 1)
                Debug.Log("[ParelVR SDK] Building MainScene with " + (scenePaths.Length - 1) + " subroom(s): " +
                          string.Join(", ", worldScenes.Skip(1).Select(WorldScenes.NameOf)) + ".");

            var buildMap = new AssetBundleBuild[1];
            buildMap[0] = new AssetBundleBuild
            {
                assetBundleName = "worldbundle",
                assetNames = scenePaths
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
            if (BuildQuestVersion)
            {
                try
                {
                    string lighting = UnityEditor.Lightmapping.lightingDataAsset != null
                        ? AssetDatabase.GetAssetPath(UnityEditor.Lightmapping.lightingDataAsset)
                        : null;
                    QuestJob = ParelQuestLocalBuild.Prepare(
                        ParelQuestSource.WorldKind,
                        scenePaths,
                        activeScene.GetRootGameObjects(),
                        RenderSettings.skybox,
                        string.IsNullOrEmpty(lighting) ? null : new[] { lighting });
                }
                catch (System.Exception ex)
                {
                    // The PC world still publishes; only the Meta / Standalone VR version waits for the next publish.
                    QuestJobError = ex.Message;
                    Debug.LogError("[ParelVR SDK] Could not get the world ready for Meta / Standalone VR: " + ex);
                }
            }

            return Path.Combine(outputDir, "worldbundle");
        }
    }
}
