using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Avatars.Build;
using ParelVR.SDK.Core.Auth;
using ParelVR.SDK.Core.Http;
using ParelVR.Volt.Simulator;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace ParelVR.Volt.EditorTools
{
    using Scene = UnityEngine.SceneManagement.Scene;

    /// <summary>
    /// ParelVR SDK > Avatar Simulator: wears the selected avatar in play mode inside the ParelVR Home Hub, the world
    /// fetched from the backend with its validated scripts, so expressions, toggles, gestures, Motion Bones and contacts
    /// can be tried where they will be used. The world is cached under Library/ParelVR/HomeHub and only fetched again
    /// when the backend has a newer build.
    /// </summary>
    [InitializeOnLoad]
    public static class VoltAvatarSimulatorLauncher
    {
        public const string HubName = "ParelVR Home Hub";
        private const string Pending = "ParelVR.AvatarSim.Pending";
        private const string AvatarScene = "ParelVR.AvatarSim.Scene";
        private const string AvatarPath = "ParelVR.AvatarSim.Path";
        private const string BundleKey = "ParelVR.AvatarSim.Bundle";
        private const string ModuleKey = "ParelVR.AvatarSim.Module";
        private const string CapabilitiesKey = "ParelVR.AvatarSim.Capabilities";
        private const string DomainsKey = "ParelVR.AvatarSim.Domains";
        private const string WorldNameKey = "ParelVR.AvatarSim.WorldName";
        private const string GeneratedFolder = "Assets/ParelVR_AvatarSimulator_Temp";

        private static bool _fetching;

        static VoltAvatarSimulatorLauncher()
        {
            // Entering play mode reloads the domain after the test was asked for: the simulator must not start the open scene's own world.
            if (SessionState.GetBool(Pending, false)) VoltSimulator.SuppressAutoStart = true;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("ParelVR SDK/Avatar Simulator/Test Avatar in Home Hub", priority = 30)]
        public static void TestSelected()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[Avatar Simulator] Leave play mode first.");
                return;
            }
            if (_fetching) return;
            ParelAvatarDescriptor avatar = PickAvatar();
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("Avatar Simulator", "Select an avatar (an object with a ParelVR Avatar Descriptor) in the open scene, then try again.", "OK");
                return;
            }
            _ = FetchAndPlay(avatar, true);
        }

        [MenuItem("ParelVR SDK/Avatar Simulator/Test Avatar on a Plain Floor", priority = 31)]
        public static void TestSelectedOffline()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || _fetching) return;
            ParelAvatarDescriptor avatar = PickAvatar();
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("Avatar Simulator", "Select an avatar (an object with a ParelVR Avatar Descriptor) in the open scene, then try again.", "OK");
                return;
            }
            Play(avatar, null, null, 0, Array.Empty<string>(), "Avatar Simulator");
        }

        [MenuItem("ParelVR SDK/Avatar Simulator/Forget the Downloaded Home Hub", priority = 50)]
        public static void ClearCache()
        {
            string folder = CacheFolder();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Debug.Log("[Avatar Simulator] The cached Home Hub was removed; the next test downloads it again.");
        }

        /// <summary>The selected avatar, or the only one in the open scenes.</summary>
        private static ParelAvatarDescriptor PickAvatar()
        {
            GameObject selected = Selection.activeGameObject;
            ParelAvatarDescriptor avatar = selected != null ? selected.GetComponentInParent<ParelAvatarDescriptor>(true) : null;
            if (avatar != null && !EditorUtility.IsPersistent(avatar)) return avatar;
            var found = new List<ParelAvatarDescriptor>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                    found.AddRange(root.GetComponentsInChildren<ParelAvatarDescriptor>(false));
            }
            return found.Count == 1 ? found[0] : null;
        }

        // ── the Home Hub from the backend ────────────────────────────────────

        [Serializable] private sealed class SearchResult { public string id; public string name; }
        [Serializable] private sealed class SearchResponse { public bool success; public SearchResult[] worlds; }
        [Serializable] private sealed class HomeResponse { public bool success; public string worldId; }
        [Serializable] private sealed class VoltInfo { public string moduleUrl; public string sha256; public uint capabilities; public string[] webDomains; }
        [Serializable] private sealed class PlatformInfo { public string bundleUrl; public string sha256; public VoltInfo volt; }
        [Serializable] private sealed class Platforms { public PlatformInfo windows; public PlatformInfo android; }
        [Serializable] private sealed class WorldData { public string id; public string name; public string bundleUpdatedAt; public Platforms platforms; }
        [Serializable] private sealed class WorldResponse { public bool success; public WorldData world; }
        [Serializable] private sealed class Stamp { public string worldId; public string name; public string bundleUpdatedAt; public uint capabilities; public string[] webDomains; public bool hasModule; }

        private static string CacheFolder() => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "ParelVR", "HomeHub"));

        private static string Absolute(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
            return ParelApiClient.BaseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
        }

        private static async Task<string> FindHubId(CancellationToken ct)
        {
            try
            {
                SearchResponse search = await ParelApiClient.GetJsonAsync<SearchResponse>("/api/parelvr/worlds/search?limit=20&query=" + UnityWebRequest.EscapeURL(HubName), ct);
                if (search?.worlds != null)
                {
                    foreach (SearchResult world in search.worlds)
                        if (world != null && string.Equals(world.name?.Trim(), HubName, StringComparison.OrdinalIgnoreCase)) return world.id;
                    foreach (SearchResult world in search.worlds)
                        if (world != null && world.name != null && world.name.IndexOf("Home Hub", StringComparison.OrdinalIgnoreCase) >= 0) return world.id;
                }
            }
            catch (ParelApiException e) { Debug.LogWarning("[Avatar Simulator] World search failed: " + e.Message); }
            // The backend's default home world is the Home Hub for everyone who has not picked their own.
            if (!ParelSession.IsLoggedIn) return null;
            try
            {
                HomeResponse home = await ParelApiClient.GetJsonAsync<HomeResponse>("/api/parelvr/users/" + UnityWebRequest.EscapeURL(ParelSession.UserId) + "/home-world", ct);
                return home?.worldId;
            }
            catch (ParelApiException e)
            {
                Debug.LogWarning("[Avatar Simulator] The default home world could not be read: " + e.Message);
                return null;
            }
        }

        private static async Task Download(string url, string path, string what, CancellationToken ct)
        {
            string temp = path + ".downloading";
            using (var request = new UnityWebRequest(Absolute(url), UnityWebRequest.kHttpVerbGET))
            {
                request.downloadHandler = new DownloadHandlerFile(temp) { removeFileOnAbort = true };
                if (ParelSession.IsLoggedIn)
                {
                    request.SetRequestHeader("X-ParelVR-Id", ParelSession.UserId);
                    request.SetRequestHeader("Authorization", "Bearer " + ParelSession.SessionTicket);
                }
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Avatar Simulator", "Downloading " + what + "... " + Mathf.RoundToInt(request.downloadProgress * 100f) + "%", request.downloadProgress))
                    {
                        request.Abort();
                        throw new OperationCanceledException();
                    }
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(50, ct);
                }
                if (request.result != UnityWebRequest.Result.Success) throw new IOException(what + " could not be downloaded: " + request.error);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                var hex = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(stream)) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        private static async Task FetchAndPlay(ParelAvatarDescriptor avatar, bool allowCache)
        {
            _fetching = true;
            string folder = CacheFolder();
            string bundlePath = Path.Combine(folder, "world.bundle"), modulePath = Path.Combine(folder, "world.voltm"), stampPath = Path.Combine(folder, "stamp.json");
            Stamp cached = null;
            try { if (File.Exists(stampPath)) cached = JsonUtility.FromJson<Stamp>(File.ReadAllText(stampPath)); } catch (Exception) { }
            bool haveCache = cached != null && File.Exists(bundlePath);

            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
            {
                try
                {
                    EditorUtility.DisplayProgressBar("Avatar Simulator", "Finding the " + HubName + "...", 0f);
                    string id = await FindHubId(cts.Token);
                    if (string.IsNullOrEmpty(id)) throw new IOException("The backend has no world called \"" + HubName + "\".");
                    WorldResponse response = await ParelApiClient.GetJsonAsync<WorldResponse>("/api/parelvr/worlds/" + UnityWebRequest.EscapeURL(id), cts.Token);
                    WorldData world = response?.world ?? throw new IOException("The backend did not describe the " + HubName + ".");
                    PlatformInfo platform = world.platforms?.windows;
                    if (platform == null || string.IsNullOrEmpty(platform.bundleUrl)) throw new IOException("The " + HubName + " has no Windows build.");

                    bool fresh = haveCache && cached.worldId == world.id && cached.bundleUpdatedAt == (world.bundleUpdatedAt ?? string.Empty);
                    if (!fresh)
                    {
                        Directory.CreateDirectory(folder);
                        await Download(platform.bundleUrl, bundlePath, "the " + HubName, cts.Token);
                        if (!string.IsNullOrEmpty(platform.sha256) && !string.Equals(Sha256(bundlePath), platform.sha256, StringComparison.OrdinalIgnoreCase))
                            Debug.LogWarning("[Avatar Simulator] The downloaded world does not match the backend's hash; it is used anyway for testing.");
                        bool hasModule = false;
                        if (platform.volt != null && !string.IsNullOrEmpty(platform.volt.moduleUrl))
                        {
                            await Download(platform.volt.moduleUrl, modulePath, "the world's scripts", cts.Token);
                            hasModule = string.IsNullOrEmpty(platform.volt.sha256) || string.Equals(Sha256(modulePath), platform.volt.sha256, StringComparison.OrdinalIgnoreCase);
                            if (!hasModule) Debug.LogWarning("[Avatar Simulator] The world's scripts do not match what ParelVR validated; the world runs without them.");
                        }
                        else if (File.Exists(modulePath)) File.Delete(modulePath);
                        cached = new Stamp
                        {
                            worldId = world.id, name = world.name ?? HubName, bundleUpdatedAt = world.bundleUpdatedAt ?? string.Empty,
                            capabilities = platform.volt != null ? platform.volt.capabilities : 0u, webDomains = platform.volt?.webDomains ?? Array.Empty<string>(),
                            hasModule = hasModule
                        };
                        File.WriteAllText(stampPath, JsonUtility.ToJson(cached));
                    }
                }
                catch (OperationCanceledException)
                {
                    EditorUtility.ClearProgressBar();
                    _fetching = false;
                    return;
                }
                catch (Exception e)
                {
                    if (!(allowCache && haveCache))
                    {
                        EditorUtility.ClearProgressBar();
                        _fetching = false;
                        if (EditorUtility.DisplayDialog("Avatar Simulator", "The " + HubName + " could not be fetched:\n" + e.Message + "\n\nTest the avatar on a plain floor instead?", "Plain floor", "Cancel"))
                            Play(avatar, null, null, 0, Array.Empty<string>(), "Avatar Simulator");
                        return;
                    }
                    Debug.LogWarning("[Avatar Simulator] The " + HubName + " could not be checked for updates (" + e.Message + "); using the copy from last time.");
                }
            }
            EditorUtility.ClearProgressBar();
            _fetching = false;
            if (avatar == null) return; // the scene changed while downloading
            Play(avatar, bundlePath, cached.hasModule && File.Exists(modulePath) ? modulePath : null, cached.capabilities, cached.webDomains ?? Array.Empty<string>(), cached.name ?? HubName);
        }

        // ── into play mode ───────────────────────────────────────────────────

        private static void Play(ParelAvatarDescriptor avatar, string bundlePath, string modulePath, uint capabilities, string[] webDomains, string worldName)
        {
            Scene scene = avatar.gameObject.scene;
            if (scene.isDirty && !UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            SessionState.SetBool(Pending, true);
            SessionState.SetString(AvatarScene, scene.path);
            SessionState.SetString(AvatarPath, PathOf(avatar.transform));
            SessionState.SetString(BundleKey, bundlePath ?? string.Empty);
            SessionState.SetString(ModuleKey, modulePath ?? string.Empty);
            SessionState.SetInt(CapabilitiesKey, unchecked((int)capabilities));
            SessionState.SetString(DomainsKey, string.Join("\n", webDomains ?? Array.Empty<string>()));
            SessionState.SetString(WorldNameKey, worldName ?? HubName);
            VoltSimulator.SuppressAutoStart = true;
            EditorApplication.isPlaying = true;
        }

        /// <summary>Sibling indices from the scene root: the same object in play mode.</summary>
        private static string PathOf(Transform t)
        {
            var parts = new List<int>();
            for (; t != null; t = t.parent) parts.Add(t.GetSiblingIndex());
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static GameObject Resolve(string scenePath, string path)
        {
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            if (!scene.IsValid()) scene = SceneManager.GetActiveScene();
            string[] parts = (path ?? string.Empty).Split('/');
            GameObject[] roots = scene.GetRootGameObjects();
            if (parts.Length > 0 && int.TryParse(parts[0], out int root) && root >= 0 && root < roots.Length)
            {
                Transform t = roots[root].transform;
                for (int i = 1; i < parts.Length && t != null; i++)
                    t = int.TryParse(parts[i], out int index) && index >= 0 && index < t.childCount ? t.GetChild(index) : null;
                if (t != null && t.GetComponent<ParelAvatarDescriptor>() != null) return t.gameObject;
            }
            foreach (GameObject go in roots)
            {
                ParelAvatarDescriptor any = go.GetComponentInChildren<ParelAvatarDescriptor>(false);
                if (any != null) return any.gameObject;
            }
            return null;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode)
            {
                VoltSimulator.SuppressAutoStart = false;
                if (AssetDatabase.IsValidFolder(GeneratedFolder)) AssetDatabase.DeleteAsset(GeneratedFolder);
                return;
            }
            if (change != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);

            GameObject avatar = Resolve(SessionState.GetString(AvatarScene, string.Empty), SessionState.GetString(AvatarPath, string.Empty));
            if (avatar == null)
            {
                Debug.LogError("[Avatar Simulator] The avatar was not found in play mode.");
                return;
            }
            // Avatar Tools (Object Toggle, Outfit Switcher, ...) become their animator layers, as when the avatar is built.
            var descriptor = avatar.GetComponent<ParelAvatarDescriptor>();
            if (ParelAvatarToolsProcessor.HasTools(avatar))
            {
                try
                {
                    var notes = new List<string>();
                    ParelAvatarToolsProcessor.Process(avatar, descriptor, GeneratedFolder, notes);
                    foreach (string note in notes) Debug.Log("[Avatar Simulator] " + note);
                }
                catch (Exception e) { Debug.LogWarning("[Avatar Simulator] Avatar Tools could not be applied: " + e.Message); }
            }

            string modulePath = SessionState.GetString(ModuleKey, string.Empty);
            byte[] module = null;
            try { if (!string.IsNullOrEmpty(modulePath) && File.Exists(modulePath)) module = File.ReadAllBytes(modulePath); }
            catch (Exception e) { Debug.LogWarning("[Avatar Simulator] The world's scripts could not be read: " + e.Message); }
            string domains = SessionState.GetString(DomainsKey, string.Empty);

            VoltAvatarSimulator.Begin(new VoltAvatarSimulator.Request
            {
                Avatar = avatar,
                BundlePath = SessionState.GetString(BundleKey, string.Empty),
                Module = module,
                Capabilities = unchecked((uint)SessionState.GetInt(CapabilitiesKey, 0)),
                WebDomains = string.IsNullOrEmpty(domains) ? Array.Empty<string>() : domains.Split('\n'),
                WorldName = SessionState.GetString(WorldNameKey, HubName),
                PlayerName = ParelSession.IsLoggedIn && !string.IsNullOrEmpty(ParelSession.DisplayName) ? ParelSession.DisplayName : null,
                VR = VoltSettings.instance.testInVR
            });
        }
    }
}
