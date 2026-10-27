using System;
using System.IO;
using System.Linq;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Backend;
using ParelVR.SDK.Build.Protected;
using ParelVR.SDK.Build.Quest;
using ParelVR.SDK.Core.Auth;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.UI
{
    /// <summary>
    /// Protected avatars. The creator makes the file they sell: stand-ins only, while the real files go sealed to
    /// ParelVR. Buyers import that file; publishing it is built by ParelVR's server with the real files put back, so
    /// the real models and textures never reach a buyer's project and can't be exported from it.
    /// </summary>
    public static class ParelProtectedContentMenu
    {
        private const string Title = "Protected Content";

        [MenuItem("ParelVR SDK/Protected Content/Make Sellable File...", priority = 130)]
        private static async void MakeSellableFile()
        {
            ParelAvatarDescriptor descriptor = PickAvatar();
            if (descriptor == null)
            {
                EditorUtility.DisplayDialog(Title, "Select your avatar (the object with the ParelVR Avatar Descriptor) in the Hierarchy first.", "OK");
                return;
            }
            if (!ParelSession.IsLoggedIn)
            {
                EditorUtility.DisplayDialog(Title, "Sign in to the ParelVR SDK first.", "OK");
                return;
            }
            string id = descriptor.BlueprintId;
            if (string.IsNullOrEmpty(id))
            {
                EditorUtility.DisplayDialog(Title, "Publish this avatar first, then protect it from its Settings tab on the ParelVR website.", "OK");
                return;
            }

            string workDir = Path.Combine(Path.GetTempPath(), "ParelVRProtected", Guid.NewGuid().ToString("N"));
            string keyId = null;
            try
            {
                EditorUtility.DisplayProgressBar(Title, "Checking the protection...", 0.05f);
                ParelCopyrightStatus status = await ParelCopyrightApiClient.GetStatusAsync("avatar", id);
                if (status == null || !status.isOwner)
                    throw new Exception("Only the avatar's uploader can make its protected file.");
                if (status.status != "protected" || string.IsNullOrEmpty(status.keyId))
                    throw new Exception("This avatar isn't protected yet. Protect it from its Settings tab on the ParelVR website, then try again.");
                keyId = status.keyId;
                EditorUtility.ClearProgressBar();

                string save = EditorUtility.SaveFilePanel("Save the protected file", string.Empty, Safe(descriptor.gameObject.name) + ".pvrprotected", "pvrprotected");
                if (string.IsNullOrEmpty(save)) return;

                EditorUtility.DisplayProgressBar(Title, "Making the stand-ins...", 0.15f);
                ParelProtectedPackages packages = ParelProtectedPackager.Make(descriptor.gameObject, new ParelProtectedContent
                {
                    KeyId = keyId,
                    Kind = "avatar",
                    ContentId = id,
                    Name = descriptor.gameObject.name,
                    Owner = string.IsNullOrEmpty(status.protectedBy) ? ParelSession.Username : status.protectedBy,
                }, workDir);

                string standInSha = ParelLocked.Sha256File(packages.StandInPackage);
                long standInSize = new FileInfo(packages.StandInPackage).Length;
                ParelProtectedSealResponse sealedSource = await ParelCopyrightApiClient.SealSourceAsync("avatar", id, packages.SourcePackage,
                    standInSha, standInSize, ParelQuestSource.SdkVersion(),
                    new Progress<float>(p => EditorUtility.DisplayProgressBar(Title, $"Sending the real files to ParelVR (sealed there)... {(int)(p * 100)}%", 0.4f + p * 0.55f)));
                if (sealedSource == null || string.IsNullOrEmpty(sealedSource.h) || string.IsNullOrEmpty(sealedSource.sig))
                    throw new Exception(sealedSource?.message ?? "ParelVR didn't sign the file.");

                ParelLocked.WriteFile(save, new ParelLocked.FileSeal { h = sealedSource.h, sig = sealedSource.sig }, packages.StandInPackage);
                EditorUtility.ClearProgressBar();

                string message = $"Saved {Path.GetFileName(save)}. This is the file you sell.\n\n" +
                                 $"It holds stand-ins only ({packages.Meshes} meshes, {packages.Textures} textures, {packages.Sounds} sounds): same bones, blendshapes and material slots, " +
                                 "but blocky models and tiny textures, so it's worthless outside ParelVR. Your real files are sealed on ParelVR's servers.\n\n" +
                                 "Buyers import it with ParelVR SDK > Protected Content > Import. When they publish, ParelVR builds the avatar with your real files, " +
                                 "once you've approved them (whitelist) on the avatar's Settings tab.";
                if (packages.Notes.Count > 0) message += "\n\n" + string.Join("\n", packages.Notes.Distinct().Take(8));
                EditorUtility.DisplayDialog(Title, message, "OK");
                EditorUtility.RevealInFinder(save);
            }
            catch (Exception ex)
            {
                Debug.LogError("[ParelVR SDK] Making the protected file failed: " + ex);
                EditorUtility.DisplayDialog(Title, ex.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (!string.IsNullOrEmpty(keyId)) ParelProtectedPackager.CleanUp(keyId);
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch (IOException) { }
            }
        }

        [MenuItem("ParelVR SDK/Protected Content/Import Protected File...", priority = 131)]
        [MenuItem("Assets/Import ParelVR Protected File...", priority = 21)]
        private static async void ImportProtectedFile()
        {
            string path = EditorUtility.OpenFilePanel("Import a protected file", string.Empty, "pvrprotected");
            if (string.IsNullOrEmpty(path)) return;
            if (!ParelSession.IsLoggedIn)
            {
                EditorUtility.DisplayDialog(Title, "Sign in to the ParelVR SDK first: the creator approves you by your ParelVR account.", "OK");
                return;
            }

            string temp = Path.Combine(Path.GetTempPath(), "ParelVRProtected", Guid.NewGuid().ToString("N") + ".unitypackage");
            try
            {
                ParelLocked.ProtectedFile file = ParelLocked.ReadFile(path);
                if (!file.HashMatches) throw new Exception("The protected file is damaged or was changed. Download it again.");

                EditorUtility.DisplayProgressBar(Title, "Checking the file with ParelVR...", 0.3f);
                ParelProtectedKeyInfo info = await ParelCopyrightApiClient.VerifyAsync(file.Seal.h, file.Seal.sig);
                if (info == null || !info.valid) throw new Exception(info?.message ?? "ParelVR couldn't confirm this file. Get it again from the creator's store.");
                if (info.access == "blacklisted") throw new Exception($"{info.owner} hasn't allowed you to use '{info.name}'.");
                if (info.access == "none") info = await ParelCopyrightApiClient.RequestAccessAsync(info.keyId) ?? info;

                Directory.CreateDirectory(Path.GetDirectoryName(temp));
                File.WriteAllBytes(temp, file.Package);
                EditorUtility.ClearProgressBar();
                AssetDatabase.ImportPackage(temp, false);

                string access = info.MayPublish
                    ? "You're approved, so you can publish it."
                    : $"You're on {info.owner}'s pending list. You can set it up now and publish once they approve you.";
                EditorUtility.DisplayDialog(Title,
                    $"Imported '{info.name}' by {info.owner} to {ParelLocked.FolderFor(info.keyId)}.\n\n" +
                    "This copy has stand-ins (blocky models, tiny textures) with the real bones, blendshapes and material slots. " +
                    "Set it up and customize it as usual; when you publish, ParelVR's build server puts the real files in.\n\n" + access, "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError("[ParelVR SDK] Importing the protected file failed: " + ex);
                EditorUtility.DisplayDialog(Title, ex.Message, "OK");
            }
            finally
            {
                // The package stays in the temp folder: Unity may still be importing it when this returns.
                EditorUtility.ClearProgressBar();
            }
        }

        private static ParelAvatarDescriptor PickAvatar()
        {
            GameObject selected = Selection.activeGameObject;
            ParelAvatarDescriptor descriptor = selected != null ? selected.GetComponentInParent<ParelAvatarDescriptor>(true) : null;
            if (descriptor != null && !EditorUtility.IsPersistent(descriptor)) return descriptor;
            ParelAvatarDescriptor[] inScene = UnityEngine.Object.FindObjectsByType<ParelAvatarDescriptor>(FindObjectsInactive.Exclude);
            return inScene.Length == 1 ? inScene[0] : null;
        }

        private static string Safe(string name)
        {
            string clean = new string((name ?? "avatar").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            return clean.Length == 0 ? "avatar" : clean;
        }
    }
}
