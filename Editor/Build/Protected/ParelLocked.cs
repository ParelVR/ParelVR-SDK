using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ParelVR.SDK.Build.Quest;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build.Protected
{
    /// <summary>One stand-in object and the real object ParelVR's build server puts back in its place.</summary>
    [Serializable]
    public sealed class ParelLockedEntry
    {
        /// <summary>Stand-in GUID and local file id.</summary>
        public string s;
        public long sl;
        /// <summary>Real GUID and local file id (in the creator's sealed source).</summary>
        public string r;
        public long rl;
        /// <summary>mesh, texture, cubemap, audio or avatar.</summary>
        public string t;
    }

    /// <summary>locked-map.json, next to the stand-ins of one piece of protected content.</summary>
    [Serializable]
    public sealed class ParelLockedMap
    {
        public int format = 1;
        public string keyId;
        public string kind;
        public string contentId;
        public string name;
        public string owner;
        public string sdkVersion;
        public List<ParelLockedEntry> entries = new List<ParelLockedEntry>();

        [NonSerialized] public string Folder;
    }

    /// <summary>What a buyer's locked upload is built from, for the build server.</summary>
    [Serializable]
    public sealed class ParelLockedHints
    {
        public int version = 1;
        public string kind;
        public string mainAsset;
        public string sdkVersion;
        public string unityVersion;
        public List<string> keys = new List<string>();
    }

    /// <summary>
    /// Protected content. A buyer's project only ever holds stand-ins (blocky meshes, tiny textures, silent audio) with
    /// the same bones, blendshapes and material slots as the real thing, so it can be set up and customized but is
    /// worthless outside ParelVR. The real files stay sealed on ParelVR's servers; when the buyer publishes, the build
    /// server swaps the stand-ins for the real files and builds the bundle there.
    /// </summary>
    public static class ParelLocked
    {
        public const string Root = "Assets/ParelVR_Locked";
        public const string MapFile = "locked-map.json";
        public const string Label = "ParelLocked";
        public const string UploadFolder = "Assets/ParelVR_Locked_Upload";
        public const string UploadPrefab = UploadFolder + "/avatar.prefab";
        public const string HintsPath = UploadFolder + "/locked-hints.json";

        public static string FolderFor(string keyId) => Root + "/" + keyId;

        /// <summary>Every locked map in the project.</summary>
        public static List<ParelLockedMap> AllMaps()
        {
            var maps = new List<ParelLockedMap>();
            if (!AssetDatabase.IsValidFolder(Root)) return maps;
            foreach (string folder in AssetDatabase.GetSubFolders(Root))
            {
                string file = folder + "/" + MapFile;
                if (!File.Exists(file)) continue;
                try
                {
                    var map = JsonUtility.FromJson<ParelLockedMap>(File.ReadAllText(file));
                    if (map == null || string.IsNullOrEmpty(map.keyId)) continue;
                    map.Folder = folder;
                    maps.Add(map);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ParelVR SDK] Couldn't read " + file + ": " + ex.Message);
                }
            }
            return maps;
        }

        /// <summary>The protected content whose stand-ins <paramref name="root"/> uses (directly or through its materials, animations...).</summary>
        public static List<ParelLockedMap> MapsUsedBy(GameObject root)
        {
            List<ParelLockedMap> maps = AllMaps();
            if (root == null || maps.Count == 0) return new List<ParelLockedMap>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnityEngine.Object o in EditorUtility.CollectDependencies(new UnityEngine.Object[] { root }))
            {
                if (o == null || !AssetDatabase.Contains(o)) continue;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long _)) used.Add(guid);
            }
            return maps.Where(m => m.entries.Any(e => used.Contains(e.s))).ToList();
        }

        /// <summary>
        /// Packs a buyer's avatar for the build server: a copy of it saved as a prefab, everything it uses (stand-ins
        /// included, never code) and the locked maps. Avatar Tools run on the server, on the real files.
        /// </summary>
        public static ParelQuestSourceResult ExportLockedSource(GameObject avatar, IEnumerable<ParelLockedMap> maps, string outputFile)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            List<ParelLockedMap> list = maps.ToList();
            GameObject copy = UnityEngine.Object.Instantiate(avatar);
            copy.name = avatar.name;
            try
            {
                copy.SetActive(true);
                copy.transform.SetParent(null, false);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
                if (AssetDatabase.IsValidFolder(UploadFolder)) AssetDatabase.DeleteAsset(UploadFolder);
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(UploadFolder));
                PrefabUtility.SaveAsPrefabAsset(copy, UploadPrefab, out bool saved);
                if (!saved) throw new Exception("Couldn't save a copy of the avatar to send to ParelVR.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }

            try
            {
                var hints = new ParelLockedHints
                {
                    kind = ParelQuestSource.AvatarKind,
                    mainAsset = UploadPrefab,
                    sdkVersion = ParelQuestSource.SdkVersion(),
                    unityVersion = Application.unityVersion,
                    keys = list.Select(m => m.keyId).Distinct().ToList(),
                };
                File.WriteAllText(HintsPath, JsonUtility.ToJson(hints, true));
                AssetDatabase.ImportAsset(HintsPath, ImportAssetOptions.ForceSynchronousImport);

                var assets = new HashSet<string>(StringComparer.Ordinal) { UploadPrefab, HintsPath };
                foreach (string dependency in AssetDatabase.GetDependencies(UploadPrefab, true)) assets.Add(dependency);
                foreach (ParelLockedMap map in list) assets.Add(map.Folder + "/" + MapFile);
                foreach (string shader in assets.Where(ParelQuestSource.IsShaderSource).ToList()) ParelQuestSource.AddIncludes(shader, assets, 0);
                string[] packable = assets.Where(p => p.StartsWith(UploadFolder + "/", StringComparison.Ordinal) || ParelQuestSource.IsPackable(p))
                    .Where(p => !IsRealFile(p, list)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile)));
                if (File.Exists(outputFile)) File.Delete(outputFile);
                AssetDatabase.ExportPackage(packable, outputFile, ExportPackageOptions.Default);
                if (!File.Exists(outputFile)) throw new Exception("Unity didn't write the package for ParelVR's build server.");
                return new ParelQuestSourceResult { PackagePath = outputFile, Size = new FileInfo(outputFile).Length, AssetCount = packable.Length };
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(UploadFolder)) AssetDatabase.DeleteAsset(UploadFolder);
            }
        }

        // A creator testing their own package has the real files too; those never go up with a buyer-style upload.
        private static bool IsRealFile(string path, List<ParelLockedMap> maps)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            return maps.Any(m => m.entries.Any(e => e.r == guid));
        }

        // =========================================================================================
        // The .pvrprotected file: "PVRCP2", a big-endian length, {"h": header json signed by ParelVR, "sig": base64},
        // then the stand-in .unitypackage.
        // =========================================================================================

        public const string FileMagic = "PVRCP2";

        [Serializable]
        public sealed class FileHeader
        {
            public string format;
            public string kind;
            public string contentId;
            public string keyId;
            public string owner;
            public string name;
            public int version;
            public string createdAt;
            public string standInSha256;
            public long standInSize;
            public string sdkVersion;
        }

        [Serializable]
        public sealed class FileSeal
        {
            public string h;
            public string sig;
        }

        public sealed class ProtectedFile
        {
            public FileSeal Seal;
            public FileHeader Header;
            public byte[] Package;
            public bool HashMatches;
        }

        public static void WriteFile(string path, FileSeal seal, string packagePath)
        {
            byte[] head = Encoding.UTF8.GetBytes(JsonUtility.ToJson(seal));
            using (FileStream output = File.Create(path))
            {
                byte[] magic = Encoding.ASCII.GetBytes(FileMagic);
                output.Write(magic, 0, magic.Length);
                output.Write(new[] { (byte)(head.Length >> 24), (byte)(head.Length >> 16), (byte)(head.Length >> 8), (byte)head.Length }, 0, 4);
                output.Write(head, 0, head.Length);
                using (FileStream package = File.OpenRead(packagePath)) package.CopyTo(output);
            }
        }

        public static ProtectedFile ReadFile(string path)
        {
            byte[] all = File.ReadAllBytes(path);
            if (all.Length > 6 && Encoding.ASCII.GetString(all, 0, 6) == "PVRCP1")
                throw new Exception("This is an old protected file. Ask the creator for the new one: they make it in the ParelVR SDK (ParelVR SDK > Protected Content).");
            if (all.Length < 10 || Encoding.ASCII.GetString(all, 0, 6) != FileMagic) throw new Exception("This isn't a ParelVR protected file.");
            int length = (all[6] << 24) | (all[7] << 16) | (all[8] << 8) | all[9];
            if (length <= 0 || 10 + length > all.Length) throw new Exception("The protected file is damaged.");
            var seal = JsonUtility.FromJson<FileSeal>(Encoding.UTF8.GetString(all, 10, length));
            if (seal == null || string.IsNullOrEmpty(seal.h)) throw new Exception("The protected file is damaged.");
            var header = JsonUtility.FromJson<FileHeader>(seal.h);
            var package = new byte[all.Length - 10 - length];
            Buffer.BlockCopy(all, 10 + length, package, 0, package.Length);
            return new ProtectedFile
            {
                Seal = seal,
                Header = header,
                Package = package,
                HashMatches = header != null && string.Equals(Sha256(package), header.standInSha256, StringComparison.OrdinalIgnoreCase),
            };
        }

        public static string Sha256(byte[] data)
        {
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static string Sha256File(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
