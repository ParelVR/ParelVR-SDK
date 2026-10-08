using System;
using System.Collections.Generic;
using System.IO;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Avatars.Validation;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Build
{
    public sealed class ParelAvatarBuildResult
    {
        public string BundlePath;
        public long BundleBytes;
        public ParelAvatarPerformance Performance;
        public List<string> RemovedComponents = new List<string>();
        /// <summary>What Avatar Tools did (merged clothing, generated toggles...), for the summary dialog.</summary>
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Builds an avatar into a ParelVR avatar bundle. The avatar in your scene is never modified: a
    /// copy is cleaned up (EditorOnly objects, missing scripts and components avatars may not carry are
    /// stripped, the root controller cleared because ParelVR supplies locomotion), saved as a temporary
    /// prefab at <see cref="ParelAvatarBundleFormat.PrefabAssetName"/>, built into one LZ4 bundle with
    /// everything it references, and the temporary prefab is deleted again.
    /// </summary>
    public static class ParelAvatarBuilder
    {
        private const string TempFolder = "Assets/ParelVR_Avatar_Build";
        private const string TempPrefabPath = TempFolder + "/avatar.prefab";
        private const string GeneratedFolder = TempFolder + "/Generated";

        public static string PlatformName(BuildTarget target) => target == BuildTarget.Android ? "android" : "windows";

        public static bool CanBuildFor(BuildTarget target)
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            return BuildPipeline.IsBuildTargetSupported(group, target);
        }

        /// <summary>Builds <paramref name="descriptor"/>'s avatar for <paramref name="target"/> into <paramref name="outputFile"/>.</summary>
        public static ParelAvatarBuildResult Build(ParelAvatarDescriptor descriptor, BuildTarget target, string outputFile)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrEmpty(outputFile)) throw new ArgumentNullException(nameof(outputFile));
            if (!CanBuildFor(target)) throw new Exception($"The {target} build module isn't installed. Add it in Unity Hub > Installs > Add Modules.");

            var result = new ParelAvatarBuildResult();
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string workDir = Path.Combine(projectRoot, "Temp", "ParelVRAvatarBuild", Guid.NewGuid().ToString("N"));

            try
            {
                // The bundle is built from what's on disk, so unsaved material / animation edits must land first.
                if (!descriptor.LayoutUpgraded)
                {
                    descriptor.UpgradeLayout();
                    EditorUtility.SetDirty(descriptor);
                }
                AssetDatabase.SaveAssets();
                SaveCleanCopy(descriptor, result);

                Directory.CreateDirectory(workDir);
                // Unity names a bundle's internal file after the bundle name, and refuses to load two
                // bundles with the same internal file at once -- so every avatar needs its own name.
                string bundleName = "avatar_" + Guid.NewGuid().ToString("N") + ".bundle";
                var builds = new[]
                {
                    new AssetBundleBuild
                    {
                        assetBundleName = bundleName,
                        assetNames = new[] { TempPrefabPath },
                    },
                };

                AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                    workDir,
                    builds,
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
                    target);

                string built = Path.Combine(workDir, bundleName);
                if (manifest == null || !File.Exists(built))
                {
                    throw new Exception("The avatar bundle failed to build. Check the Console for the error Unity reported.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile)));
                if (File.Exists(outputFile)) File.Delete(outputFile);
                File.Copy(built, outputFile);

                result.BundlePath = outputFile;
                result.BundleBytes = new FileInfo(outputFile).Length;
                if (result.Performance == null) result.Performance = ParelAvatarPerformance.Measure(descriptor.gameObject);
                return result;
            }
            finally
            {
                // The temporary prefab and everything Avatar Tools generated for it.
                if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
                try
                {
                    if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
                }
                catch
                {
                    // Temp/ is cleaned by Unity anyway.
                }
            }
        }

        private static void SaveCleanCopy(ParelAvatarDescriptor descriptor, ParelAvatarBuildResult result)
        {
            GameObject copy = UnityEngine.Object.Instantiate(descriptor.gameObject);
            copy.name = descriptor.gameObject.name;
            try
            {
                copy.SetActive(true);
                copy.transform.SetParent(null, false);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                StripEditorOnly(copy.transform);
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
                {
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                }

                if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "ParelVR_Avatar_Build");
                ParelAvatarDescriptor copiedDescriptor = copy.GetComponent<ParelAvatarDescriptor>();
                if (copiedDescriptor != null)
                {
                    // Toggles, outfits, merged clothing... applied to the copy only.
                    ParelAvatarToolsProcessor.Process(copy, copiedDescriptor, GeneratedFolder, result.Notes);
                    copiedDescriptor.UnityVersion = Application.unityVersion;
                    descriptor.UnityVersion = Application.unityVersion;
                }
                else
                {
                    ParelAvatarToolsProcessor.StripEditorOnly(copy);
                }

                result.RemovedComponents.AddRange(ParelAvatarComponentPolicy.RemoveDisallowed(copy));
                result.Performance = ParelAvatarPerformance.Measure(copy);

                Animator animator = copiedDescriptor != null ? copiedDescriptor.AvatarAnimator : copy.GetComponentInChildren<Animator>(true);
                if (animator != null) animator.runtimeAnimatorController = null;

                PrefabUtility.SaveAsPrefabAsset(copy, TempPrefabPath, out bool saved);
                if (!saved) throw new Exception("Couldn't save the build copy of the avatar as a prefab.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        private static void StripEditorOnly(Transform root)
        {
            var doomed = new List<GameObject>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != root && t.CompareTag("EditorOnly")) doomed.Add(t.gameObject);
            }
            foreach (GameObject go in doomed)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // =========================================================================================
        // Where "Build & Test" puts avatars so the game finds them
        // =========================================================================================

        /// <summary>The installed game's test-avatar folder (LocalLow/Parel LLC/ParelVR/SDKTestAvatars).</summary>
        public static string TestAvatarFolder
        {
            get
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(userProfile, "AppData", "LocalLow", ParelAvatarBundleFormat.CompanyName, ParelAvatarBundleFormat.ProductName, ParelAvatarBundleFormat.TestAvatarsFolderName);
            }
        }

        public static string TestAvatarPath(string avatarName)
        {
            string safe = string.IsNullOrWhiteSpace(avatarName) ? "Avatar" : avatarName.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(TestAvatarFolder, safe + ParelAvatarBundleFormat.TestBundleExtension);
        }
    }
}
