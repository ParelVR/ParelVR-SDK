using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Build
{
    public static class WorldBuilder
    {
        /// <summary>
        /// Lets another part of the SDK prepare what goes into the bundle. It is given the saved scene's asset
        /// path and returns the path of the scene to build (the same one, or a prepared copy).
        /// </summary>
        public static System.Func<string, string> PrepareScene;
        /// <summary>Called when the build is over, with the bundle's path or null when it failed.</summary>
        public static System.Action<string> Finished;
        /// <summary>Called after the bundle was uploaded: world id, platform, bundle path. What it uploads belongs to that bundle.</summary>
        public static System.Func<string, string, string, System.Threading.CancellationToken, System.Threading.Tasks.Task> AfterBundleUploaded;

        public static string BuildWorldBundle()
        {
            string built = null;
            try
            {
                built = Build();
                return built;
            }
            finally
            {
                Finished?.Invoke(built);
            }
        }

        private static string Build()
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

            var buildMap = new AssetBundleBuild[1];
            buildMap[0] = new AssetBundleBuild
            {
                assetBundleName = "worldbundle",
                assetNames = new[] { PrepareScene != null ? PrepareScene(activeScene.path) : activeScene.path }
            };

            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir, 
                buildMap, 
                BuildAssetBundleOptions.ForceRebuildAssetBundle, 
                EditorUserBuildSettings.activeBuildTarget
            );

            if (manifest == null)
            {
                throw new System.Exception("BuildAssetBundles failed. Check the Unity Console for errors.");
            }

            return Path.Combine(outputDir, "worldbundle");
        }
    }
}
