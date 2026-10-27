using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ParelVR.SDK.Build.Quest;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build.Protected
{
    public sealed class ParelProtectedContent
    {
        public string KeyId;
        public string Kind;
        public string ContentId;
        public string Name;
        public string Owner;
    }

    public sealed class ParelProtectedPackages
    {
        /// <summary>The creator's real files (no code): sent to ParelVR, sealed there, never given to buyers.</summary>
        public string SourcePackage;
        /// <summary>The stand-ins buyers import: goes inside the .pvrprotected file.</summary>
        public string StandInPackage;
        public ParelLockedMap Map;
        public int Meshes, Textures, Sounds, Copied;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Makes the two halves of a protected avatar from the creator's own: the real files for ParelVR's servers, and the
    /// stand-in copy (its own GUIDs, its own folder) that is sold. The creator's project is left as it was.
    /// </summary>
    public static class ParelProtectedPackager
    {
        private static readonly HashSet<string> NativeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mat", ".anim", ".controller", ".overrideController", ".mask", ".asset", ".prefab", ".physicMaterial",
            ".physicsMaterial", ".flare", ".playable", ".signal", ".preset", ".renderTexture",
        };

        public static ParelProtectedPackages Make(GameObject avatar, ParelProtectedContent content, string workDir)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            if (content == null || string.IsNullOrEmpty(content.KeyId)) throw new ArgumentException("The content has no protection key.");
            Directory.CreateDirectory(workDir);
            AssetDatabase.SaveAssets();

            var result = new ParelProtectedPackages
            {
                SourcePackage = Path.Combine(workDir, "source.unitypackage"),
                StandInPackage = Path.Combine(workDir, "standin.unitypackage"),
                Map = new ParelLockedMap
                {
                    keyId = content.KeyId,
                    kind = content.Kind,
                    contentId = content.ContentId,
                    name = content.Name,
                    owner = content.Owner,
                    sdkVersion = ParelQuestSource.SdkVersion(),
                },
            };
            string folder = ParelLocked.FolderFor(content.KeyId);
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            EnsureFolder(folder);
            foreach (string sub in new[] { "Meshes", "Textures", "Audio", "Assets" }) EnsureFolder(folder + "/" + sub);

            var work = new Work(folder, result);
            GameObject copy = UnityEngine.Object.Instantiate(avatar);
            copy.name = avatar.name;
            try
            {
                copy.SetActive(true);
                copy.transform.SetParent(null, false);
                copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                foreach (Transform t in copy.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

                // Humanoid / generic rigs: rebuilt from the copy's own bones, so the model file never goes along.
                foreach (Animator animator in copy.GetComponentsInChildren<Animator>(true))
                    if (animator.avatar != null && AssetDatabase.Contains(animator.avatar)) animator.avatar = (Avatar)work.StandInAvatar(animator);
                foreach (Component component in copy.GetComponentsInChildren<Component>(true))
                    if (component != null && !(component is Transform)) work.Remap(component);
                work.Drain();

                string prefabPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Safe(avatar.name) + ".prefab");
                PrefabUtility.SaveAsPrefabAsset(copy, prefabPath, out bool saved);
                if (!saved) throw new Exception("Couldn't save the stand-in avatar.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(copy);
            }

            string mapPath = folder + "/" + ParelLocked.MapFile;
            File.WriteAllText(mapPath, JsonUtility.ToJson(result.Map, true));
            AssetDatabase.ImportAsset(mapPath, ImportAssetOptions.ForceSynchronousImport);

            // Stand-in package: the folder, plus the shaders the materials use (buyers need those to see anything).
            var standIn = new HashSet<string>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;
                standIn.Add(path);
                AssetDatabase.SetLabels(AssetDatabase.LoadMainAssetAtPath(path), new[] { ParelLocked.Label });
            }
            foreach (string kept in work.Kept.Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && ParelQuestSource.IsShaderSource(p)).ToList())
            {
                standIn.Add(kept);
                ParelQuestSource.AddIncludes(kept, standIn, 0);
            }
            standIn.RemoveWhere(p => !p.StartsWith(folder + "/", StringComparison.Ordinal) && !ParelQuestSource.IsShaderSource(p));
            Export(standIn, result.StandInPackage);

            // Source package: every real file the stand-ins stand for, and what those use.
            var source = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in work.Originals)
            {
                source.Add(path);
                foreach (string dependency in AssetDatabase.GetDependencies(path, true)) source.Add(dependency);
            }
            foreach (string shader in source.Where(ParelQuestSource.IsShaderSource).ToList()) ParelQuestSource.AddIncludes(shader, source, 0);
            Export(source.Where(ParelQuestSource.IsPackable).Where(p => !p.StartsWith(ParelLocked.Root + "/", StringComparison.Ordinal)), result.SourcePackage);

            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            if (AssetDatabase.IsValidFolder(ParelLocked.Root) && AssetDatabase.GetSubFolders(ParelLocked.Root).Length == 0 &&
                AssetDatabase.FindAssets(string.Empty, new[] { ParelLocked.Root }).Length == 0)
                AssetDatabase.DeleteAsset(ParelLocked.Root);
            return result;
        }

        /// <summary>Removes a half-made stand-in folder after a failure.</summary>
        public static void CleanUp(string keyId)
        {
            string folder = ParelLocked.FolderFor(keyId);
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
        }

        private static void Export(IEnumerable<string> paths, string file)
        {
            string[] list = paths.Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (list.Length == 0) throw new Exception("There was nothing to pack.");
            if (File.Exists(file)) File.Delete(file);
            AssetDatabase.ExportPackage(list, file, ExportPackageOptions.Default);
            if (!File.Exists(file))
            {
                string[] own = list.Where(p => p.StartsWith("Assets/", StringComparison.Ordinal)).ToArray();
                if (own.Length > 0 && own.Length < list.Length) AssetDatabase.ExportPackage(own, file, ExportPackageOptions.Default);
            }
            if (!File.Exists(file)) throw new Exception("Unity didn't write " + Path.GetFileName(file) + ".");
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static string Safe(string name)
        {
            string clean = new string((name ?? "asset").Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray()).Trim();
            return clean.Length == 0 ? "asset" : clean.Length > 60 ? clean.Substring(0, 60) : clean;
        }

        // =========================================================================================
        // The walk: every asset the avatar uses is kept (shaders, scripts, packages), stood in for (meshes, textures,
        // sounds, rigs) or copied into the stand-in folder with its references walked in turn.
        // =========================================================================================

        private sealed class Work
        {
            private readonly string _folder;
            private readonly ParelProtectedPackages _result;
            private readonly Dictionary<UnityEngine.Object, UnityEngine.Object> _done = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
            private readonly Dictionary<string, string> _copiedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Queue<UnityEngine.Object> _queue = new Queue<UnityEngine.Object>();
            public readonly HashSet<string> Kept = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Originals = new HashSet<string>(StringComparer.Ordinal);

            public Work(string folder, ParelProtectedPackages result)
            {
                _folder = folder;
                _result = result;
            }

            public void Remap(UnityEngine.Object target)
            {
                if (target == null) return;
                var so = new SerializedObject(target);
                SerializedProperty it = so.GetIterator();
                bool changed = false;
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    UnityEngine.Object value = it.objectReferenceValue;
                    if (value == null) continue;
                    UnityEngine.Object replaced = Replace(value);
                    if (replaced == value) continue;
                    it.objectReferenceValue = replaced;
                    changed = true;
                }
                if (changed) so.ApplyModifiedPropertiesWithoutUndo();
            }

            public void Drain()
            {
                while (_queue.Count > 0)
                {
                    UnityEngine.Object next = _queue.Dequeue();
                    Remap(next);
                    if (next != null) EditorUtility.SetDirty(next);
                }
                AssetDatabase.SaveAssets();
            }

            private UnityEngine.Object Replace(UnityEngine.Object value)
            {
                if (_done.TryGetValue(value, out UnityEngine.Object known)) return known;
                if (!AssetDatabase.Contains(value)) return value; // part of the avatar itself
                string path = AssetDatabase.GetAssetPath(value);
                if (path.StartsWith(_folder + "/", StringComparison.Ordinal)) return value;
                if (IsKept(value, path))
                {
                    Kept.Add(path);
                    _done[value] = value;
                    return value;
                }
                Originals.Add(path);

                UnityEngine.Object result;
                switch (value)
                {
                    case Mesh mesh:
                        result = Save(ParelStandIns.Mesh(mesh), "Meshes", mesh.name, ".asset");
                        Entry(result, value, "mesh");
                        _result.Meshes++;
                        break;
                    case Texture2D texture:
                        result = StandInTexture(texture, path);
                        Entry(result, value, "texture");
                        _result.Textures++;
                        break;
                    case Cubemap cubemap:
                        result = Save(ParelStandIns.Cubemap(cubemap.name), "Textures", cubemap.name, ".cubemap");
                        Entry(result, value, "cubemap");
                        _result.Textures++;
                        break;
                    case AudioClip clip:
                        result = StandInAudio(clip);
                        Entry(result, value, "audio");
                        _result.Sounds++;
                        break;
                    case Avatar _:
                    case GameObject _ when !NativeExtensions.Contains(Path.GetExtension(path)):
                        // A rig or model prefab used outside an Animator: it can't be stood in for, so it's left out.
                        _result.Notes.Add("Left out of the stand-in: " + value.name + " (" + Path.GetFileName(path) + ")");
                        result = null;
                        break;
                    default:
                        result = Copy(value, path);
                        break;
                }
                _done[value] = result;
                return result;
            }

            private static bool IsKept(UnityEngine.Object value, string path)
            {
                if (value is MonoScript || value is Shader || value is ComputeShader || value is ShaderVariantCollection || value is Font) return true;
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) return true; // packages, built-in resources
                return ParelQuestSource.IsShaderSource(path);
            }

            public UnityEngine.Object StandInAvatar(Animator animator)
            {
                Avatar real = animator.avatar;
                if (_done.TryGetValue(real, out UnityEngine.Object known) && known != null) return known;
                Originals.Add(AssetDatabase.GetAssetPath(real));
                Avatar built = real.isHuman
                    ? UnityEngine.AvatarBuilder.BuildHumanAvatar(animator.gameObject, real.humanDescription)
                    : UnityEngine.AvatarBuilder.BuildGenericAvatar(animator.gameObject, string.Empty);
                if (built == null || !built.isValid)
                {
                    _result.Notes.Add("The rig of " + animator.name + " couldn't be rebuilt for the stand-in; buyers will need to set it in the Animator.");
                    _done[real] = null;
                    return null;
                }
                built.name = real.name;
                UnityEngine.Object saved = Save(built, "Meshes", real.name + " Rig", ".asset");
                Entry(saved, real, "avatar");
                _done[real] = saved;
                return saved;
            }

            private UnityEngine.Object StandInTexture(Texture2D texture, string realPath)
            {
                var importer = AssetImporter.GetAtPath(realPath) as TextureImporter;
                bool normal = importer != null && importer.textureType == TextureImporterType.NormalMap;
                bool linear = importer != null ? !importer.sRGBTexture : !GraphicsFormatUtilitySrgb(texture);
                string path = AssetDatabase.GenerateUniqueAssetPath(_folder + "/Textures/" + Safe(texture.name) + ".png");
                File.WriteAllBytes(path, ParelStandIns.TexturePng(texture, normal, linear));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                if (AssetImporter.GetAtPath(path) is TextureImporter standIn)
                {
                    TextureImporterType type = importer != null ? importer.textureType : TextureImporterType.Default;
                    if (type == TextureImporterType.Sprite || type == TextureImporterType.Cookie || type == TextureImporterType.Lightmap ||
                        type == TextureImporterType.DirectionalLightmap || type == TextureImporterType.Shadowmask) type = TextureImporterType.Default;
                    standIn.textureType = type;
                    standIn.sRGBTexture = !linear;
                    standIn.alphaIsTransparency = importer != null && importer.alphaIsTransparency;
                    standIn.wrapMode = texture.wrapMode;
                    standIn.mipmapEnabled = false;
                    standIn.maxTextureSize = ParelStandIns.TextureSize;
                    standIn.isReadable = false;
                    standIn.SaveAndReimport();
                }
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            private static bool GraphicsFormatUtilitySrgb(Texture texture) =>
                UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);

            private UnityEngine.Object StandInAudio(AudioClip clip)
            {
                string path = AssetDatabase.GenerateUniqueAssetPath(_folder + "/Audio/" + Safe(clip.name) + ".wav");
                File.WriteAllBytes(path, ParelStandIns.SilentWav(clip));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }

            private UnityEngine.Object Save(UnityEngine.Object asset, string sub, string name, string extension)
            {
                string path = AssetDatabase.GenerateUniqueAssetPath(_folder + "/" + sub + "/" + Safe(name) + extension);
                AssetDatabase.CreateAsset(asset, path);
                return asset;
            }

            private UnityEngine.Object Copy(UnityEngine.Object value, string path)
            {
                if (NativeExtensions.Contains(Path.GetExtension(path)))
                {
                    // The whole file once (sub-assets keep their local ids), then each object maps to its copy.
                    if (!_copiedFiles.TryGetValue(path, out string copyPath))
                    {
                        copyPath = AssetDatabase.GenerateUniqueAssetPath(_folder + "/Assets/" + Path.GetFileName(path));
                        if (!AssetDatabase.CopyAsset(path, copyPath)) throw new Exception("Couldn't copy " + path + " for the stand-in.");
                        _copiedFiles[path] = copyPath;
                        _result.Copied++;
                        var copies = new Dictionary<long, UnityEngine.Object>();
                        foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(copyPath))
                            if (o != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string _, out long id)) copies[id] = o;
                        foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(path))
                        {
                            if (o == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string _, out long id)) continue;
                            if (!copies.TryGetValue(id, out UnityEngine.Object twin)) continue;
                            _done[o] = twin;
                            _queue.Enqueue(twin);
                        }
                    }
                    return _done.TryGetValue(value, out UnityEngine.Object mapped) ? mapped : null;
                }

                // A material or clip inside a model file: its own copy.
                if (value is GameObject || value is Component) return null;
                UnityEngine.Object own = UnityEngine.Object.Instantiate(value);
                own.name = value.name;
                string extension = value is Material ? ".mat" : value is AnimationClip ? ".anim" : ".asset";
                Save(own, "Assets", value.name, extension);
                _result.Copied++;
                _queue.Enqueue(own);
                return own;
            }

            private void Entry(UnityEngine.Object standIn, UnityEngine.Object real, string type)
            {
                if (standIn == null) return;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(standIn, out string s, out long sl)) return;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(real, out string r, out long rl)) return;
                _result.Map.entries.Add(new ParelLockedEntry { s = s, sl = sl, r = r, rl = rl, t = type });
            }
        }
    }
}
