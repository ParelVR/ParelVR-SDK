using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace ParelVR.SDK.Core
{
    /// <summary>Top-level "ParelVR SDK" menu bar entry, laid out like the VRChat SDK's menu.</summary>
    internal static class ParelSDKMenu
    {
        private const string TestAvatarFolderName = "SDKTestAvatars";

        [MenuItem("ParelVR SDK/Show Control Panel", priority = 0)]
        private static void ShowControlPanel()
        {
            ParelVR.SDK.Core.ControlPanel.ParelControlPanel.Open();
        }

        // ---- Utilities -----------------------------------------------------------------------

        [MenuItem("ParelVR SDK/Utilities/Open Test Avatars Folder", priority = 100)]
        private static void OpenTestAvatars()
        {
            string folder = TestAvatarsFolder();
            Directory.CreateDirectory(folder);
            EditorUtility.RevealInFinder(folder);
        }

        [MenuItem("ParelVR SDK/Utilities/Clear Test Avatars", priority = 101)]
        private static void ClearTestAvatars()
        {
            string folder = TestAvatarsFolder();
            if (!Directory.Exists(folder))
            {
                EditorUtility.DisplayDialog("Clear Test Avatars", "There are no test avatars to clear.", "OK");
                return;
            }
            string[] files = Directory.GetFiles(folder);
            if (files.Length == 0)
            {
                EditorUtility.DisplayDialog("Clear Test Avatars", "There are no test avatars to clear.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Clear Test Avatars", $"Delete all {files.Length} Build & Test avatar file(s)? They disappear from the Avatars page in ParelVR on this PC.", "Delete", "Cancel")) return;
            foreach (string file in files)
            {
                try { File.Delete(file); }
                catch (IOException ex) { Debug.LogWarning($"[ParelVR SDK] Couldn't delete {file}: {ex.Message}"); }
            }
        }

        [MenuItem("ParelVR SDK/Utilities/Clear SDK Cache", priority = 120)]
        private static void ClearSdkCache()
        {
            string temp = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "Temp", "ParelVRAvatarBuild");
            if (Directory.Exists(temp))
            {
                try { Directory.Delete(temp, true); }
                catch (IOException ex) { Debug.LogWarning("[ParelVR SDK] " + ex.Message); }
            }
            if (AssetDatabase.IsValidFolder("Assets/ParelVR_Avatar_Build")) AssetDatabase.DeleteAsset("Assets/ParelVR_Avatar_Build");
            EditorUtility.DisplayDialog("Clear SDK Cache", "Temporary build files were removed.", "OK");
        }

        // ---- Help ---------------------------------------------------------------------------

        [MenuItem("ParelVR SDK/Help/Documentation", priority = 200)]
        private static void OpenDocumentation()
        {
            Application.OpenURL("https://docs-parelvr.parelllc.com/");
        }

        [MenuItem("ParelVR SDK/Help/Support", priority = 201)]
        private static void OpenSupport()
        {
            Application.OpenURL("https://support-parelvr.parelllc.com/");
        }

        [MenuItem("ParelVR SDK/Help/Discord", priority = 202)]
        private static void OpenDiscord()
        {
            Application.OpenURL("https://discord.gg/79XABdnbej");
        }

        // ---- Remove SDK --------------------------------------------------------------------

        [MenuItem("ParelVR SDK/Remove SDK", priority = 1000)]
        private static void RemoveSdk()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                EditorUtility.DisplayDialog("Remove SDK", "Leave play mode and wait for scripts to finish compiling, then try again.", "OK");
                return;
            }

            PackageInfo package = PackageInfo.FindForAssembly(typeof(ParelSDKMenu).Assembly);
            bool embedded = package == null || package.source == PackageSource.Embedded;
            string packageFolder = package != null ? package.resolvedPath : null;

            string what = "This removes the ParelVR SDK from this project:\n\n" +
                          "  \u2022 the SDK package" + (embedded && packageFolder != null ? " (the folder " + ProjectRelative(packageFolder) + " is deleted)" : string.Empty) + "\n" +
                          "  \u2022 its settings, sign-in and caches for this project\n" +
                          "  \u2022 its temporary build folders\n\n" +
                          "Your scenes, models and scripts are kept. SDK components in your scenes will show as missing scripts, and your own Volt scripts will " +
                          "no longer compile until the SDK is installed again. Finished builds in Builds/ParelVR are kept.\n\n" +
                          "This cannot be undone.";
            if (!EditorUtility.DisplayDialog("Remove ParelVR SDK", what, "Remove SDK", "Cancel")) return;
            if (embedded && !EditorUtility.DisplayDialog("Remove ParelVR SDK",
                    "The SDK in this project is a local copy inside the Packages folder, not a download. Deleting it deletes those files for good.\n\nDelete them?",
                    "Delete the SDK", "Cancel")) return;

            // The SDK's windows hold on to its code and its stylesheets; close them before anything goes.
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                string assembly = window.GetType().Assembly.GetName().Name;
                if (!assembly.StartsWith("ParelVR.", StringComparison.Ordinal)) continue;
                try { window.Close(); }
                catch (Exception ex) { Debug.LogWarning("[ParelVR SDK] Could not close " + window.titleContent.text + ": " + ex.Message); }
            }

            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string packageName = package != null ? package.name : "com.parelvrsdk.pvr";
            var failed = new List<string>();

            foreach (string folder in new[] { "Assets/ParelVR_Avatar_Build", "Assets/ParelVR_Volt_Build" })
                if (AssetDatabase.IsValidFolder(folder) && !AssetDatabase.DeleteAsset(folder)) failed.Add(folder);

            foreach (string path in new[]
                     {
                         Path.Combine(project, "Library", "ParelVR"),
                         Path.Combine(project, "Library", "BuildKit"),
                         Path.Combine(project, "Temp", "ParelVRAvatarBuild"),
                         Path.Combine(project, "Temp", "ParelSdkUpdate"),
                         Path.Combine(project, "ProjectSettings", "ParelVRVolt.asset"),
                         Path.Combine(project, "ProjectSettings", "ParelSdkInstall.json"),
                     })
                if (!DeletePath(path)) failed.Add(ProjectRelative(path));

            int waiting = 0;
            if (embedded)
            {
                // The updater puts the SDK in Packages/ ahead of a Git or registry install. Without the manifest entry
                // gone too, Package Manager downloads the SDK again the moment the copy is deleted.
                RemoveFromManifest(project, packageName);
                if (packageFolder != null)
                {
                    // File by file: Unity keeps the SDK's DLLs open, and one locked file must not stop the rest going.
                    var locked = new List<string>();
                    DeleteEverythingPossible(packageFolder, locked);
                    DeletePath(packageFolder + ".meta");
                    if (locked.Count > 0)
                    {
                        waiting = locked.Count;
                        DeleteAfterUnityCloses(packageFolder);
                    }
                }
                Client.Resolve();
            }
            else
            {
                Client.Remove(packageName);
            }

            AssetDatabase.Refresh();
            if (failed.Count == 0 && waiting == 0)
                Debug.Log("[ParelVR SDK] The SDK was removed from this project.");
            else if (failed.Count == 0)
                EditorUtility.DisplayDialog("Remove ParelVR SDK",
                    "The SDK was removed. Unity still has " + waiting + " of its files open (its DLLs); they are deleted as soon as you close Unity.\n\n" +
                    "If anything is left after that, delete " + ProjectRelative(packageFolder) + " by hand.", "OK");
            else
                EditorUtility.DisplayDialog("Remove ParelVR SDK",
                    "Some files are in use and could not be deleted. Close Unity and delete them by hand:\n\n" + string.Join("\n", failed), "OK");
        }

        /// <summary>Deletes a file or folder if it is there. False when something is left behind.</summary>
        private static bool DeletePath(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
                foreach (string meta in new[] { path + ".meta" })
                    if (File.Exists(meta)) File.Delete(meta);
                return !Directory.Exists(path) && !File.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning("[ParelVR SDK] Could not delete " + path + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Deletes every file and folder under a folder that can be deleted; what is open goes into <paramref name="locked"/>.</summary>
        private static void DeleteEverythingPossible(string folder, List<string> locked)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string file in Directory.GetFiles(folder))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    locked.Add(file);
                }
            }
            foreach (string sub in Directory.GetDirectories(folder)) DeleteEverythingPossible(sub, locked);
            try { if (Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Takes the SDK out of Packages/manifest.json, where a Git or registry install of it is listed.</summary>
        private static void RemoveFromManifest(string project, string packageName)
        {
            string manifest = Path.Combine(project, "Packages", "manifest.json");
            try
            {
                if (!File.Exists(manifest)) return;
                string text = File.ReadAllText(manifest);
                string name = Regex.Escape(packageName);
                string edited = Regex.Replace(text, "^[ \\t]*\"" + name + "\"[ \\t]*:[ \\t]*\"[^\"]*\"[ \\t]*,?[ \\t]*\\r?\\n", string.Empty, RegexOptions.Multiline);
                if (edited == text) edited = Regex.Replace(text, "\"" + name + "\"\\s*:\\s*\"[^\"]*\"\\s*,?", string.Empty);
                if (edited == text) return;
                // If it was the last dependency, the one before it now ends in a comma that JSON does not allow.
                edited = Regex.Replace(edited, ",(\\s*})", "$1");
                File.WriteAllText(manifest, edited);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Debug.LogWarning("[ParelVR SDK] Could not take the SDK out of Packages/manifest.json; remove the \"" + packageName + "\" line by hand: " + ex.Message);
            }
        }

        /// <summary>Starts a small hidden process that waits for this editor to close and then deletes what it kept open.</summary>
        private static void DeleteAfterUnityCloses(string folder)
        {
            int editor = System.Diagnostics.Process.GetCurrentProcess().Id;
            try
            {
                System.Diagnostics.ProcessStartInfo start;
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    string script = "Wait-Process -Id " + editor + " -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
                                    "Remove-Item -LiteralPath '" + folder.Replace("'", "''") + "' -Recurse -Force -ErrorAction SilentlyContinue";
                    start = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                        "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                }
                else
                {
                    start = new System.Diagnostics.ProcessStartInfo("/bin/sh");
                    start.ArgumentList.Add("-c");
                    start.ArgumentList.Add("while kill -0 " + editor + " 2>/dev/null; do sleep 1; done; rm -rf \"$0\"");
                    start.ArgumentList.Add(folder);
                }
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                System.Diagnostics.Process.Start(start);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ParelVR SDK] Could not arrange for the rest of the SDK to be deleted when Unity closes; delete " + folder + " by hand: " + ex.Message);
            }
        }

        private static string ProjectRelative(string path)
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(path).Replace('\\', '/');
            return full.StartsWith(project, StringComparison.OrdinalIgnoreCase) ? full.Substring(project.Length) : full;
        }

        private static string TestAvatarsFolder()
        {
            string userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "AppData", "LocalLow", "Parel LLC", "ParelVR", TestAvatarFolderName);
        }
    }
}
