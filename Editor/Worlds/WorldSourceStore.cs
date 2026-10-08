using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Core.Auth;
using ParelVR.SDK.Core.Http;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;

namespace ParelVR.SDK.Worlds
{
    /// <summary>
    /// The project behind a published world. What ParelVR hosts for a world is a built bundle, which cannot be
    /// turned back into an editable project, so every publish also saves the project itself (everything under
    /// Assets, as a .unitypackage). Import World brings the latest saved project back into an empty project.
    ///
    /// The package is always kept on this computer. When the server offers the world-source routes it is also
    /// uploaded there, which is what makes a world importable on another computer.
    /// </summary>
    [InitializeOnLoad]
    public static class WorldSourceStore
    {
        [Serializable]
        public sealed class Info
        {
            public bool success;
            public bool exists;
            public string worldId;
            public string worldName;
            public string scenePath;
            public string savedAt;
            public long size;
            public string unityVersion;
        }

        private const long UploadLimitBytes = 512L * 1024 * 1024;
        private const string PendingPackageKey = "ParelVR.SDK.ImportWorld.Package";
        private const string PendingSceneKey = "ParelVR.SDK.ImportWorld.Scene";

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string Folder(string worldId) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParelVR SDK", "WorldSources", Clean(worldId));

        public static string PackagePath(string worldId) => Path.Combine(Folder(worldId), "source.unitypackage");
        private static string InfoPath(string worldId) => Path.Combine(Folder(worldId), "source.json");

        private static string Clean(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = (name ?? string.Empty).Replace(c, '_');
            return string.IsNullOrEmpty(name) ? "world" : name;
        }

        static WorldSourceStore()
        {
            AssetDatabase.importPackageCompleted += _ => EditorApplication.delayCall += FinishImport;
            AssetDatabase.importPackageFailed += (_, error) =>
            {
                if (string.IsNullOrEmpty(SessionState.GetString(PendingPackageKey, string.Empty))) return;
                ClearPending();
                EditorUtility.DisplayDialog("Import World", "The saved project could not be imported: " + error, "OK");
            };
            AssetDatabase.importPackageCancelled += _ => ClearPending();
            // Deleting and importing scripts reloads the editor's code; carry on from where that left off.
            EditorApplication.delayCall += ResumeAfterReload;
        }

        // ── saving ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The project saved on this computer for a world, or null.</summary>
        public static Info LocalInfo(string worldId)
        {
            try
            {
                if (!File.Exists(PackagePath(worldId)) || !File.Exists(InfoPath(worldId))) return null;
                Info info = JsonUtility.FromJson<Info>(File.ReadAllText(InfoPath(worldId)));
                return info != null && !string.IsNullOrEmpty(info.savedAt) ? info : null;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Saves everything under Assets as the latest project of a world. Called after a publish.</summary>
        public static Info Save(string worldId, string worldName, string scenePath)
        {
            if (!HasAssets()) return null;
            Directory.CreateDirectory(Folder(worldId));
            string package = PackagePath(worldId);
            string writing = package + ".writing";
            AssetDatabase.ExportPackage("Assets", writing, ExportPackageOptions.Recurse);
            if (!File.Exists(writing)) return null;
            if (File.Exists(package)) File.Delete(package);
            File.Move(writing, package);

            var info = new Info
            {
                success = true,
                exists = true,
                worldId = worldId,
                worldName = worldName ?? string.Empty,
                scenePath = scenePath ?? string.Empty,
                savedAt = DateTime.UtcNow.ToString("o"),
                size = new FileInfo(package).Length,
                unityVersion = Application.unityVersion,
            };
            File.WriteAllText(InfoPath(worldId), JsonUtility.ToJson(info, true));
            return info;
        }

        /// <summary>Uploads the saved project when the server takes world sources. False when it does not, or the package is too large.</summary>
        public static async Task<bool> UploadAsync(Info info, CancellationToken ct = default)
        {
            if (info == null || info.size > UploadLimitBytes) return false;
            if (await ServerInfoAsync(info.worldId, ct) == null) return false;
            byte[] bytes = File.ReadAllBytes(PackagePath(info.worldId));
            string url = "/api/parelvr/worlds/" + info.worldId + "/source?scene=" + Uri.EscapeDataString(info.scenePath ?? string.Empty) +
                         "&savedAt=" + Uri.EscapeDataString(info.savedAt) + "&unity=" + Uri.EscapeDataString(info.unityVersion ?? string.Empty);
            Info answer = await ParelApiClient.PutBytesAsync<Info>(url, bytes, "application/octet-stream", null, ct);
            return answer != null && answer.success;
        }

        /// <summary>What the server holds for a world, or null when it has no world-source routes (or no source).</summary>
        private static async Task<Info> ServerInfoAsync(string worldId, CancellationToken ct)
        {
            try
            {
                Info info = await ParelApiClient.GetJsonAsync<Info>("/api/parelvr/worlds/" + worldId + "/source/info", ct);
                return info != null && info.success ? info : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return null; // an older server answers 404 or a page that is not JSON
            }
        }

        // ── fetching ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The latest saved project of a world: the server's copy is downloaded when it is newer than the one on
        /// this computer. Null when the world has never been saved.
        /// </summary>
        public static async Task<Info> FetchLatestAsync(string worldId, Action<string> status = null, CancellationToken ct = default)
        {
            Info local = LocalInfo(worldId);
            Info remote = await ServerInfoAsync(worldId, ct);
            bool remoteNewer = remote != null && remote.exists &&
                               (local == null || string.CompareOrdinal(remote.savedAt ?? string.Empty, local.savedAt ?? string.Empty) > 0);
            if (!remoteNewer) return local;

            status?.Invoke("Downloading the saved project...");
            Directory.CreateDirectory(Folder(worldId));
            string downloading = PackagePath(worldId) + ".downloading";
            using (var request = new UnityWebRequest(ParelApiClient.BaseUrl + "/api/parelvr/worlds/" + worldId + "/source", "GET"))
            {
                request.downloadHandler = new DownloadHandlerFile(downloading) { removeFileOnAbort = true };
                if (!string.IsNullOrEmpty(ParelSession.UserId)) request.SetRequestHeader("X-ParelVR-Id", ParelSession.UserId);
                if (!string.IsNullOrEmpty(ParelSession.SessionTicket)) request.SetRequestHeader("Authorization", "Bearer " + ParelSession.SessionTicket);
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (ct.IsCancellationRequested) { request.Abort(); ct.ThrowIfCancellationRequested(); }
                    status?.Invoke("Downloading the saved project... " + Mathf.RoundToInt(request.downloadProgress * 100f) + "%");
                    await Task.Yield();
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (File.Exists(downloading)) File.Delete(downloading);
                    if (local != null) return local; // the copy on this computer is older, but it is something
                    throw new Exception("The saved project could not be downloaded: " + request.error);
                }
            }

            string package = PackagePath(worldId);
            if (File.Exists(package)) File.Delete(package);
            File.Move(downloading, package);
            remote.worldId = worldId;
            remote.size = new FileInfo(package).Length;
            File.WriteAllText(InfoPath(worldId), JsonUtility.ToJson(remote, true));
            return remote;
        }

        // ── importing ───────────────────────────────────────────────────────────────────────────

        public static bool HasAssets() =>
            Directory.Exists(Application.dataPath) &&
            Directory.EnumerateFileSystemEntries(Application.dataPath).Any(entry => !entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Replaces this project's content with a world's saved project: backs up what is under Assets, closes the
        /// open scene, deletes everything under Assets and imports the package. Ask the user first; nothing here does.
        /// Returns where the backup was written (null when there was nothing to back up).
        /// </summary>
        public static string Import(Info info)
        {
            string package = PackagePath(info.worldId);
            if (!File.Exists(package)) throw new FileNotFoundException("The saved project is missing.", package);

            // 1. Everything about to be deleted is kept as a package outside Assets.
            string backup = null;
            if (HasAssets())
            {
                string folder = Path.Combine(ProjectRoot, "Backups", "ParelVR");
                Directory.CreateDirectory(folder);
                backup = Path.Combine(folder, "Assets before import " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".unitypackage");
                AssetDatabase.ExportPackage("Assets", backup, ExportPackageOptions.Recurse);
                if (!File.Exists(backup)) throw new Exception("The backup of this project's Assets could not be written, so nothing was changed.");
            }

            // 2. An empty scene, so nothing that is about to be deleted is open.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 3. Empty Assets, then bring the saved project in. Both steps are remembered across the code reload they cause.
            SessionState.SetString(PendingPackageKey, package);
            SessionState.SetString(PendingSceneKey, info.scenePath ?? string.Empty);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string entry in Directory.GetFileSystemEntries(Application.dataPath))
                {
                    if (entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    string asset = "Assets/" + Path.GetFileName(entry);
                    if (!AssetDatabase.DeleteAsset(asset)) Debug.LogWarning("[ParelVR SDK] Could not delete " + asset + " before the import.");
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();
            AssetDatabase.ImportPackage(package, false);
            return backup;
        }

        private static void ClearPending()
        {
            SessionState.EraseString(PendingPackageKey);
            SessionState.EraseString(PendingSceneKey);
        }

        /// <summary>The editor reloaded its code in the middle of an import: import what is still missing, then open the scene.</summary>
        private static void ResumeAfterReload()
        {
            string package = SessionState.GetString(PendingPackageKey, string.Empty);
            if (string.IsNullOrEmpty(package)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += ResumeAfterReload;
                return;
            }
            if (FindScene() == null && File.Exists(package)) AssetDatabase.ImportPackage(package, false);
            else FinishImport();
        }

        private static void FinishImport()
        {
            if (string.IsNullOrEmpty(SessionState.GetString(PendingPackageKey, string.Empty))) return;
            string scene = FindScene();
            ClearPending();
            if (scene != null) EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            Debug.Log("[ParelVR SDK] The world's project was imported" + (scene != null ? " and " + scene + " was opened." : "."));
        }

        /// <summary>The scene the world was published from, or the project's first scene when that is not known.</summary>
        private static string FindScene()
        {
            string wanted = SessionState.GetString(PendingSceneKey, string.Empty);
            if (!string.IsNullOrEmpty(wanted) && File.Exists(Path.Combine(ProjectRoot, wanted))) return wanted;
            string first = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
            return string.IsNullOrEmpty(first) ? null : first;
        }
    }
}
