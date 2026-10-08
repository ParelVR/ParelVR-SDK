using System.IO;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build
{
    public static class AvatarBuilder
    {
        public static string BuildAvatarBundle(GameObject avatarPrefab)
        {
            if (avatarPrefab == null) throw new System.ArgumentNullException(nameof(avatarPrefab));
            
            string path = AssetDatabase.GetAssetPath(avatarPrefab);
            if (string.IsNullOrEmpty(path))
            {
                throw new System.Exception("Avatar must be a saved Prefab asset in your project.");
            }

            string outputDir = Path.Combine(Application.dataPath, "..", "Builds", "ParelVR");
            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            var buildMap = new AssetBundleBuild[1];
            buildMap[0] = new AssetBundleBuild
            {
                assetBundleName = "avatarbundle",
                assetNames = new[] { path }
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

            return Path.Combine(outputDir, "avatarbundle");
        }
    }
}
