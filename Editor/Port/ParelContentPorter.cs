using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ParelVR.AvatarSDK;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Port
{
    /// <summary>One component (or saved settings of one) from another platform, and what became of it.</summary>
    internal sealed class PortItem
    {
        public PortRule Rule;
        public GameObject Go;
        public Component Live;            // the component itself, when its SDK is installed
        public PortYamlDoc Doc;           // its saved settings, when its script is missing
        public PortValue Value;
        public Component Target;
        public bool OnAvatar;
    }

    /// <summary>The replacement being wired up, for a rule's own code.</summary>
    internal sealed class PortJob
    {
        public PortItem Item;
        public Component Target;
        public PortSession Session;
        public PortValue Source => Item.Value;
        public GameObject Go => Item.Go;
        public void Set(string field, PortValue value) => PortWriter.Set(Target, field, value, Item.Rule, Session);
    }

    /// <summary>What a port did: shown in the Control Panel and the console.</summary>
    public sealed class ParelPortReport
    {
        public readonly Dictionary<string, int> Ported = new Dictionary<string, int>();
        public readonly Dictionary<string, int> Removed = new Dictionary<string, int>();
        public readonly List<string> Assets = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public int Failed;

        public int PortedCount => Ported.Values.Sum();
        public int RemovedCount => Removed.Values.Sum();
        public bool Any => PortedCount > 0 || RemovedCount > 0 || Assets.Count > 0;

        internal void Count(Dictionary<string, int> into, string what)
        {
            into.TryGetValue(what, out int n);
            into[what] = n + 1;
        }

        public static string Describe(Dictionary<string, int> counts) =>
            string.Join(", ", counts.OrderByDescending(p => p.Value).Select(p => p.Value > 1 ? p.Key + " ×" + p.Value : p.Key));

        public override string ToString()
        {
            var sb = new StringBuilder();
            if (Ported.Count > 0) sb.Append("Replaced with ParelVR components (same settings): ").Append(Describe(Ported)).Append(".\n");
            if (Removed.Count > 0) sb.Append("Removed (no ParelVR counterpart): ").Append(Describe(Removed)).Append(".\n");
            if (Assets.Count > 0) sb.Append("Converted assets: ").Append(string.Join(", ", Assets.Select(Path.GetFileName))).Append(".\n");
            foreach (string note in Notes.Distinct()) sb.Append(note).Append('\n');
            if (sb.Length == 0) sb.Append("Nothing from other platforms was found.");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Auto Port: takes components from other platforms out of a scene or an object and puts ParelVR's in their
    /// place, with the same settings wherever ParelVR has them (avatar descriptors, PhysBones, contacts, stations,
    /// pickups, object sync, mirrors, video players, spawn points, constraints, animator state behaviours and
    /// Expressions menus / parameters). Works with the other platform's SDK installed or not: without it the
    /// settings are read from the scene / prefab file. What ParelVR has nothing for is removed.
    /// </summary>
    public static class ParelContentPorter
    {
        [MenuItem("ParelVR SDK/Auto Port/Port Open Scenes", priority = 140)]
        private static void PortScenesMenu()
        {
            ParelPortReport report = PortOpenScenes();
            Show(report);
        }

        [MenuItem("ParelVR SDK/Auto Port/Port Selected Object", priority = 141)]
        [MenuItem("GameObject/ParelVR/Auto Port This Object", false, 40)]
        private static void PortSelectedMenu()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null) return;
            ParelPortReport report = Port(go);
            Show(report);
        }

        [MenuItem("ParelVR SDK/Auto Port/Port Selected Object", true)]
        [MenuItem("GameObject/ParelVR/Auto Port This Object", true)]
        private static bool PortSelectedValid() => Selection.activeGameObject != null;

        private static void Show(ParelPortReport report)
        {
            Debug.Log("[ParelVR Auto Port] " + report);
            EditorUtility.DisplayDialog("Auto Port", report.ToString(), "OK");
        }

        /// <summary>Ports every open scene (and the project prefabs placed in them).</summary>
        public static ParelPortReport PortOpenScenes()
        {
            var roots = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) roots.AddRange(scene.GetRootGameObjects());
            }
            return Run(roots);
        }

        /// <summary>Ports one object and everything under it, such as an avatar.</summary>
        public static ParelPortReport Port(GameObject root) => Run(root != null ? new List<GameObject> { root } : new List<GameObject>());

        /// <summary>What a port of the open scenes would replace and remove; changes nothing.</summary>
        public static ParelPortReport ScanOpenScenes()
        {
            var report = new ParelPortReport();
            var session = new PortSession(report, dryRun: true);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects()) session.Survey(root);
            }
            return report;
        }

        private static ParelPortReport Run(List<GameObject> roots)
        {
            var report = new ParelPortReport();
            if (EditorUtility.scriptCompilationFailed)
            {
                // With compile errors every script of the project looks missing: nothing is touched.
                report.Notes.Add("Auto Port did nothing: the project has compile errors. Fix them (Console window) and run it again.");
                return report;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                report.Notes.Add("Auto Port does not run in Play Mode.");
                return report;
            }
            Undo.SetCurrentGroupName("Auto Port");
            int group = Undo.GetCurrentGroup();
            var session = new PortSession(report, dryRun: false);
            try
            {
                session.Run(roots);
            }
            catch (Exception e)
            {
                report.Failed++;
                report.Notes.Add("Auto Port stopped part way: " + e.Message);
                Debug.LogException(e);
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
                AssetDatabase.SaveAssets();
            }
            return report;
        }
    }

    /// <summary>One run of Auto Port.</summary>
    internal sealed class PortSession : IPortReferences
    {
        private sealed class FileContext
        {
            public string Path;
            public bool Contents;                 // a prefab loaded for editing: its objects are copies of the asset's
            public PortYamlFile Yaml;
            public readonly Dictionary<long, UnityEngine.Object> ById = new Dictionary<long, UnityEngine.Object>();
            public readonly Dictionary<UnityEngine.Object, long> IdOf = new Dictionary<UnityEngine.Object, long>();
            public bool Indexed;
        }

        private readonly ParelPortReport _report;
        private readonly bool _dryRun;
        private readonly Dictionary<Component, Component> _replacement = new Dictionary<Component, Component>();
        private readonly Dictionary<string, Component> _replacementById = new Dictionary<string, Component>();
        private readonly Dictionary<string, UnityEngine.Object> _converted = new Dictionary<string, UnityEngine.Object>();
        private readonly Dictionary<string, PortYamlFile> _yaml = new Dictionary<string, PortYamlFile>();
        private readonly HashSet<string> _prefabsDone = new HashSet<string>();
        private readonly HashSet<AnimatorController> _controllers = new HashSet<AnimatorController>();
        private FileContext _ctx;
        private bool _udonNoted;

        public readonly List<PortItem> Items = new List<PortItem>();

        public PortSession(ParelPortReport report, bool dryRun)
        {
            _report = report;
            _dryRun = dryRun;
        }

        public void Note(string text) => _report.Notes.Add(text);

        public void UdonRemoved()
        {
            if (_udonNoted) return;
            _udonNoted = true;
            Note("Udon scripts were removed: their logic does not run in ParelVR. Rebuild it as Volt scripts (or with the Volt Graph).");
        }

        // ── running ──────────────────────────────────────────────────────────

        public void Run(List<GameObject> roots)
        {
            // Project prefabs placed in the scene are ported in the prefab itself, so every copy of it is fixed.
            var prefabs = new List<string>();
            foreach (GameObject root in roots)
            {
                // A prefab picked in the Project window is ported as it is.
                if (root != null && EditorUtility.IsPersistent(root))
                {
                    string path = AssetDatabase.GetAssetPath(root);
                    if (path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && !prefabs.Contains(path)) prefabs.Add(path);
                    continue;
                }
                FindPrefabs(root, prefabs);
            }
            foreach (string path in prefabs) PortPrefab(path);

            var byScene = roots.Where(r => r != null && !EditorUtility.IsPersistent(r)).GroupBy(r => r.scene.path);
            foreach (var scene in byScene)
            {
                _ctx = new FileContext { Path = scene.Key, Yaml = Yaml(scene.Key) };
                Items.Clear();
                foreach (GameObject root in scene) Collect(root, deferPrefabs: true);
                Apply();
                foreach (GameObject root in scene)
                    if (root != null && root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
            }
            _ctx = null;
        }

        /// <summary>Counts what a port would do, without changing anything.</summary>
        public void Survey(GameObject root)
        {
            _ctx = new FileContext { Path = root.scene.path, Yaml = Yaml(root.scene.path) };
            Items.Clear();
            Collect(root, deferPrefabs: false);
            foreach (PortItem item in Items)
            {
                bool replaced = item.Rule.Target != null || item.Rule.Custom != null;
                _report.Count(replaced ? _report.Ported : _report.Removed, NameOf(item));
            }
            _ctx = null;
        }

        private void FindPrefabs(GameObject root, List<string> found)
        {
            if (root == null) return;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject go = t.gameObject;
                if (!PrefabUtility.IsPartOfPrefabInstance(go)) continue;
                bool foreign = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0;
                if (!foreign)
                    foreach (Component c in go.GetComponents<Component>())
                        if (c != null && !PrefabUtility.IsAddedComponentOverride(c) && IsPortable(c.GetType())) { foreign = true; break; }
                if (!foreign) continue;
                string path = SourcePrefab(go);
                if (path != null && !found.Contains(path)) found.Add(path);
            }
        }

        private static bool IsPortable(Type type) => PortRules.ForType(type, PortKind.Component) != null || PortRules.IsForeign(type);

        /// <summary>The project prefab the object comes from, when it can be edited (not a model, not inside a package).</summary>
        private static string SourcePrefab(UnityEngine.Object o)
        {
            UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(o);
            string path = source != null ? AssetDatabase.GetAssetPath(source) : null;
            return path != null && path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        private void PortPrefab(string path)
        {
            if (!_prefabsDone.Add(path)) return;
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) return;
            // Prefabs inside this one first: what they hold is theirs to change.
            var nested = new List<string>();
            FindPrefabs(asset, nested);
            foreach (string inner in nested) if (inner != path) PortPrefab(inner);
            asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);   // as it is now the prefabs inside it changed
            if (asset == null) return;

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                FileContext previous = _ctx;
                _ctx = new FileContext { Path = path, Contents = true, Yaml = Yaml(path), Indexed = true };
                Index(asset.transform, contents.transform);
                Items.Clear();
                Collect(contents, deferPrefabs: false);
                bool changed = Items.Count > 0;
                Apply();
                if (changed) PrefabUtility.SaveAsPrefabAsset(contents, path);
                _ctx = previous;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>The prefab's file ids, carried over to the copy being edited (same hierarchy, same component order).</summary>
        private void Index(Transform asset, Transform copy)
        {
            Add(asset.gameObject, copy.gameObject);
            Component[] a = asset.GetComponents<Component>(), b = copy.GetComponents<Component>();
            for (int i = 0; i < a.Length && i < b.Length; i++)
                if (a[i] != null && b[i] != null) Add(a[i], b[i]);
            for (int i = 0; i < asset.childCount && i < copy.childCount; i++) Index(asset.GetChild(i), copy.GetChild(i));

            void Add(UnityEngine.Object from, UnityEngine.Object to)
            {
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(from, out string _, out long id)) return;
                _ctx.ById[id] = to;
                _ctx.IdOf[to] = id;
            }
        }

        // ── finding what to port ─────────────────────────────────────────────

        private void Collect(GameObject root, bool deferPrefabs)
        {
            if (root == null) return;
            var avatarRoots = new HashSet<GameObject>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject go = t.gameObject;
                bool inInstance = !_ctx.Contents && PrefabUtility.IsPartOfPrefabInstance(go);
                if (go.GetComponent<ParelAvatarDescriptor>() != null) avatarRoots.Add(go);

                foreach (Component c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    Type type = c.GetType();
                    PortRule rule = PortRules.ForType(type, PortKind.Component) ?? (PortRules.IsForeign(type) ? PortRules.Unmatched : null);
                    if (rule == null) continue;
                    // Ported in its prefab instead (see Run), unless it was added to this copy only.
                    if (deferPrefabs && inInstance && !PrefabUtility.IsAddedComponentOverride(c) && SourcePrefab(go) != null) continue;
                    Items.Add(new PortItem { Rule = rule, Go = go, Live = c, Value = PortValue.FromObject(c) });
                    if (rule.Name == "Avatar descriptor") avatarRoots.Add(go);
                }

                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing == 0) continue;
                int found = 0;
                foreach (PortYamlDoc doc in MissingOn(go))
                {
                    found++;
                    if (ScriptInProject(doc))
                    {
                        Note("'" + go.name + "' has a missing script that is part of this project (" + AssetDatabase.GUIDToAssetPath(doc.ScriptGuid) + "); it was left alone.");
                        continue;
                    }
                    PortValue value = _ctx.Yaml.Body(doc);
                    PortRule rule = PortRules.ForFields(value, PortKind.Component) ?? PortRules.UnknownMissing;
                    Items.Add(new PortItem { Rule = rule, Go = go, Doc = doc, Value = value });
                    if (rule.Name == "Avatar descriptor") avatarRoots.Add(go);
                }
                if (found < missing && !(deferPrefabs && inInstance && SourcePrefab(go) != null))
                    Note("'" + go.name + "' has " + (missing - found) + " missing script(s) whose settings could not be read (save the scene and run Auto Port again).");
            }
            foreach (PortItem item in Items)
                if (item.Go != null && !item.OnAvatar)
                    for (Transform t = item.Go.transform; t != null; t = t.parent)
                        if (avatarRoots.Contains(t.gameObject)) { item.OnAvatar = true; break; }
        }

        private static bool ScriptInProject(PortYamlDoc doc) =>
            doc.ScriptGuid != null && !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(doc.ScriptGuid));

        /// <summary>The saved settings of the components on the object whose scripts are missing.</summary>
        private IEnumerable<PortYamlDoc> MissingOn(GameObject go)
        {
            if (_ctx.Yaml == null) yield break;
            long id = IdOf(go);
            if (id == 0) yield break;
            var present = new HashSet<string>();
            foreach (MonoBehaviour mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                MonoScript script = MonoScript.FromMonoBehaviour(mb);
                if (script != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out string guid, out long local))
                    present.Add(guid + ":" + local);
            }
            foreach (PortYamlDoc doc in _ctx.Yaml.BehavioursOn(id))
                if (!present.Remove((doc.ScriptGuid ?? string.Empty) + ":" + doc.ScriptFileId)) yield return doc;
        }

        private long IdOf(UnityEngine.Object o)
        {
            if (o == null) return 0;
            if (_ctx.IdOf.TryGetValue(o, out long known)) return known;
            long id = 0;
            if (EditorUtility.IsPersistent(o)) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string _, out id);
            else
            {
                GlobalObjectId gid = GlobalObjectId.GetGlobalObjectIdSlow(o);
                if (gid.identifierType == 2)
                    id = gid.targetPrefabId == 0 ? (long)gid.targetObjectId : (long)((gid.targetObjectId ^ gid.targetPrefabId) & 0x7FFFFFFFFFFFFFFFUL);
            }
            _ctx.IdOf[o] = id;
            return id;
        }

        private UnityEngine.Object ObjectById(long id)
        {
            if (_ctx == null) return null;
            if (!_ctx.Indexed)
            {
                _ctx.Indexed = true;
                Scene scene = SceneManager.GetSceneByPath(_ctx.Path);
                if (scene.IsValid() && scene.isLoaded)
                    foreach (GameObject root in scene.GetRootGameObjects())
                        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                        {
                            long goId = IdOf(t.gameObject);
                            if (goId != 0) _ctx.ById[goId] = t.gameObject;
                            foreach (Component c in t.GetComponents<Component>())
                            {
                                if (c == null) continue;
                                long cId = IdOf(c);
                                if (cId != 0) _ctx.ById[cId] = c;
                            }
                        }
            }
            return _ctx.ById.TryGetValue(id, out UnityEngine.Object o) ? o : null;
        }

        private PortYamlFile Yaml(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!_yaml.TryGetValue(path, out PortYamlFile file)) _yaml[path] = file = PortYamlFile.Load(path);
            return file;
        }

        // ── porting ──────────────────────────────────────────────────────────

        private void Apply()
        {
            if (Items.Count == 0) return;
            Items.Sort((a, b) => a.Rule.Order.CompareTo(b.Rule.Order));

            // 1. The replacements, so references between ported components can be pointed at them.
            foreach (PortItem item in Items)
            {
                if (item.Rule.Custom != null || item.Rule.Target == null) continue;
                Type type = item.Rule.Target(item);
                if (type == null) continue;
                item.Target = Reuse(item.Go, type) ?? Add(item.Go, type);
                Register(item);
            }

            // 2. Their settings.
            foreach (PortItem item in Items)
            {
                try
                {
                    if (item.Rule.Custom != null)
                    {
                        item.Target = item.Rule.Custom(new PortJob { Item = item, Session = this });
                        Register(item);
                    }
                    else if (item.Target != null) PortWriter.CopyInto(item.Target, item.Value, item.Rule, this);
                    item.Rule.After?.Invoke(new PortJob { Item = item, Target = item.Target, Session = this });
                }
                catch (Exception e)
                {
                    _report.Failed++;
                    Note("'" + item.Go.name + "': the " + item.Rule.Name + " could not be carried over fully (" + e.Message + ").");
                }
            }

            // 3. Anything else that pointed at a ported component points at its replacement.
            Rewire();

            // 4. The originals go.
            RemoveOriginals();

            foreach (PortItem item in Items)
            {
                bool replaced = item.Target != null;
                _report.Count(replaced ? _report.Ported : _report.Removed, NameOf(item));
            }
        }

        /// <summary>For the report: the rule's name, or the component's own type when no rule covers it.</summary>
        internal static string NameOf(PortItem item) =>
            item.Rule == PortRules.Unmatched && item.Live != null ? item.Live.GetType().Name : item.Rule.Name;

        private void Register(PortItem item)
        {
            if (item.Target == null) return;
            if (item.Live != null) _replacement[item.Live] = item.Target;
            if (item.Doc != null) _replacementById[_ctx.Path + "#" + item.Doc.FileId] = item.Target;
        }

        /// <summary>A component of that type already there that the port should fill instead of adding a second one.</summary>
        private static Component Reuse(GameObject go, Type type)
        {
            if (type == typeof(ParelVR.SDK.Worlds.Components.ParelWorldDescriptor))
            {
                // One per world, wherever it is.
                var existing = UnityEngine.Object.FindObjectsByType<ParelVR.SDK.Worlds.Components.ParelWorldDescriptor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var d in existing) if (d.gameObject.scene == go.scene) return d;
            }
            return Attribute.IsDefined(type, typeof(DisallowMultipleComponent)) ? go.GetComponent(type) : null;
        }

        public T Add<T>(GameObject go) where T : Component => Add(go, typeof(T)) as T;

        public Component Add(GameObject go, Type type)
        {
            if (go == null || type == null) return null;
            try
            {
                return _ctx != null && _ctx.Contents ? go.AddComponent(type) : Undo.AddComponent(go, type);
            }
            catch (Exception e)
            {
                Note("Could not add " + type.Name + " to '" + go.name + "': " + e.Message);
                return null;
            }
        }

        private void Rewire()
        {
            if (_replacement.Count == 0) return;
            var removed = new HashSet<Component>(_replacement.Keys);
            var seen = new HashSet<GameObject>();
            foreach (PortItem item in Items)
            {
                Transform top = item.Go != null ? item.Go.transform.root : null;
                if (top == null || !seen.Add(top.gameObject)) continue;
                foreach (Component c in top.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || removed.Contains(c) || c is Transform) continue;
                    using (var so = new SerializedObject(c))
                    {
                        bool changed = false;
                        SerializedProperty it = so.GetIterator();
                        while (it.Next(true))
                        {
                            if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                            if (!(it.objectReferenceValue is Component old) || !_replacement.TryGetValue(old, out Component now)) continue;
                            UnityEngine.Object fitted = PortWriter.Fit(now, PortWriter.ExpectedType(it));
                            if (fitted == null) continue;
                            it.objectReferenceValue = fitted;
                            changed = true;
                        }
                        if (changed)
                        {
                            if (_ctx == null || !_ctx.Contents) Undo.RecordObject(c, "Auto Port");
                            so.ApplyModifiedProperties();
                        }
                    }
                }
            }
        }

        private void RemoveOriginals()
        {
            var live = Items.Where(i => i.Live != null).Select(i => i.Live).ToList();
            // A component another one needs cannot go first; a few rounds settle the order.
            for (int round = 0; round < 4 && live.Count > 0; round++)
            {
                foreach (Component c in live.ToArray())
                {
                    if (c == null) { live.Remove(c); continue; }
                    try
                    {
                        if (_ctx != null && _ctx.Contents) UnityEngine.Object.DestroyImmediate(c);
                        else Undo.DestroyObjectImmediate(c);
                    }
                    catch (Exception) { }
                    if (c == null) live.Remove(c);
                }
            }
            foreach (Component c in live)
                if (c != null) Note("Could not remove " + c.GetType().Name + " from '" + c.gameObject.name + "'; remove it by hand.");

            // Missing scripts: only on objects where every one of them was the other platform's.
            foreach (var group in Items.Where(i => i.Doc != null).GroupBy(i => i.Go))
            {
                GameObject go = group.Key;
                if (go == null) continue;
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing != group.Count())
                {
                    Note("'" + go.name + "' keeps " + missing + " missing script(s): some are not from another platform.");
                    continue;
                }
                try
                {
                    if (_ctx == null || !_ctx.Contents) Undo.RegisterCompleteObjectUndo(go, "Auto Port");
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                }
                catch (Exception e)
                {
                    Note("The missing scripts on '" + go.name + "' could not be removed (" + e.Message + "). Open its prefab and run Auto Port there.");
                }
            }
        }

        // ── references ───────────────────────────────────────────────────────

        public UnityEngine.Object Resolve(PortValue reference, string expectedType)
        {
            if (reference == null || reference.K != PortValue.Kind.Object) return null;
            if (reference.Obj != null) return Remap(reference.Obj, expectedType);
            if (reference.FileId == 0) return null;
            if (reference.Guid == null)
            {
                if (_ctx == null) return null;
                if (_replacementById.TryGetValue(_ctx.Path + "#" + reference.FileId, out Component ported)) return ported;
                UnityEngine.Object local = ObjectById(reference.FileId);
                return local != null ? Remap(local, expectedType) : null;
            }
            string path = AssetDatabase.GUIDToAssetPath(reference.Guid);
            if (string.IsNullOrEmpty(path)) return null;
            UnityEngine.Object found = Load(path, reference.FileId);
            if (found != null) return Remap(found, expectedType);
            // An asset whose script is missing (an Expressions menu or parameters list): rebuilt from its file.
            return ConvertFromFile(path, reference.FileId);
        }

        private static UnityEngine.Object Load(string path, long fileId)
        {
            foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string _, out long id) && id == fileId) return o;
            return null;
        }

        private UnityEngine.Object Remap(UnityEngine.Object o, string expectedType)
        {
            if (o is Component c && _replacement.TryGetValue(c, out Component now)) return now;
            if (o is ScriptableObject so && !(o is StateMachineBehaviour))
            {
                PortRule rule = PortRules.ForType(so.GetType(), PortKind.Asset);
                if (rule != null) return Convert(PortValue.FromObject(so), rule, AssetDatabase.GetAssetPath(so), so.name, "obj:" + GlobalObjectId.GetGlobalObjectIdSlow(so));
            }
            return o;
        }

        /// <summary>True when the reference (from another component's settings) names the item's component.</summary>
        public bool SameSource(PortValue reference, PortItem item)
        {
            if (reference == null || reference.K != PortValue.Kind.Object) return false;
            if (reference.Obj != null) return item.Live != null && reference.Obj == item.Live;
            return item.Doc != null && reference.Guid == null && reference.FileId == item.Doc.FileId;
        }

        // ── assets: Expressions menus and parameters ─────────────────────────

        private UnityEngine.Object ConvertFromFile(string path, long fileId)
        {
            PortYamlFile file = Yaml(path);
            PortYamlDoc doc = file?.Doc(fileId);
            if (doc == null || doc.ClassId != 114) return null;
            PortValue value = file.Body(doc);
            PortRule rule = PortRules.ForFields(value, PortKind.Asset);
            if (rule == null) return null;
            string name = value.Get("m_Name").AsString();
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(path);
            return Convert(value, rule, path, name, path + "#" + fileId);
        }

        private UnityEngine.Object Convert(PortValue value, PortRule rule, string sourcePath, string name, string key)
        {
            if (_converted.TryGetValue(key, out UnityEngine.Object done)) return done;
            Type type = rule.Target?.Invoke(null);
            if (type == null || _dryRun) return null;
            var made = ScriptableObject.CreateInstance(type);
            string folder = !string.IsNullOrEmpty(sourcePath) && sourcePath.StartsWith("Assets/", StringComparison.Ordinal)
                ? Path.GetDirectoryName(sourcePath).Replace('\\', '/')
                : "Assets/ParelVR Ported";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets", "ParelVR Ported");
                folder = "Assets/ParelVR Ported";
            }
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + " (ParelVR).asset");
            AssetDatabase.CreateAsset(made, path);
            _converted[key] = made;      // before its settings: a submenu can lead back to it

            // Its references (submenus, icons) are resolved in its own file.
            FileContext previous = _ctx;
            if (key.StartsWith("Assets/", StringComparison.Ordinal) || key.StartsWith("Packages/", StringComparison.Ordinal))
                _ctx = new FileContext { Path = sourcePath, Yaml = Yaml(sourcePath), Indexed = true };
            try { PortWriter.CopyInto(made, value, rule, this); }
            finally { _ctx = previous; }
            EditorUtility.SetDirty(made);
            _report.Assets.Add(path);
            return made;
        }

        // ── animator controllers ─────────────────────────────────────────────

        /// <summary>Replaces the other platform's state behaviours in a controller with ParelVR's, same settings.</summary>
        public void PortController(RuntimeAnimatorController runtime)
        {
            AnimatorController controller = runtime as AnimatorController;
            if (controller == null && runtime is AnimatorOverrideController over) controller = over.runtimeAnimatorController as AnimatorController;
            if (controller == null || _dryRun || !_controllers.Add(controller)) return;
            string path = AssetDatabase.GetAssetPath(controller);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                Note("The controller '" + controller.name + "' is inside a package and was left as it is.");
                return;
            }
            FileContext previous = _ctx;
            _ctx = new FileContext { Path = path, Yaml = null, Indexed = true };
            try
            {
                foreach (AnimatorControllerLayer layer in controller.layers) Walk(layer.stateMachine);
            }
            finally { _ctx = previous; }
            EditorUtility.SetDirty(controller);
        }

        private void Walk(AnimatorStateMachine machine)
        {
            if (machine == null) return;
            PortBehaviours(machine, machine.behaviours, t => machine.AddStateMachineBehaviour(t), b => machine.behaviours = b);
            foreach (ChildAnimatorState child in machine.states)
            {
                AnimatorState state = child.state;
                if (state != null) PortBehaviours(state, state.behaviours, t => state.AddStateMachineBehaviour(t), b => state.behaviours = b);
            }
            foreach (ChildAnimatorStateMachine child in machine.stateMachines) Walk(child.stateMachine);
        }

        private void PortBehaviours(UnityEngine.Object owner, StateMachineBehaviour[] current, Func<Type, StateMachineBehaviour> add, Action<StateMachineBehaviour[]> set)
        {
            if (current == null || current.Length == 0) return;
            var keep = new List<StateMachineBehaviour>();
            var gone = new List<(StateMachineBehaviour Old, PortRule Rule)>();
            var missing = new List<(PortValue Value, PortRule Rule)>();
            bool hasMissing = false;
            foreach (StateMachineBehaviour b in current)
            {
                if (b == null) { hasMissing = true; continue; }
                Type type = b.GetType();
                PortRule rule = PortRules.ForType(type, PortKind.Behaviour) ?? (PortRules.IsForeign(type) ? PortRules.Unmatched : null);
                if (rule == null) keep.Add(b);
                else gone.Add((b, rule));
            }

            if (hasMissing)
            {
                // Saved in the controller file; read from there. Only touched when every missing one is another platform's:
                // behaviours that come back from Unity as null cannot be kept through the list they are in.
                if (_ctx.Yaml == null) _ctx.Yaml = Yaml(_ctx.Path);
                PortYamlDoc ownerDoc = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(owner, out string _, out long ownerId) ? _ctx.Yaml?.Doc(ownerId) : null;
                PortValue refs = ownerDoc != null ? _ctx.Yaml.Body(ownerDoc).Get("StateMachineBehaviours") : PortValue.Null;
                var present = new HashSet<long>();
                foreach (StateMachineBehaviour b in current)
                    if (b != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(b, out string _, out long id)) present.Add(id);
                bool removable = refs.K == PortValue.Kind.List;
                if (removable)
                    foreach (PortValue r in refs.List)
                    {
                        if (r.K != PortValue.Kind.Object || r.FileId == 0 || present.Contains(r.FileId)) continue;
                        PortYamlDoc doc = _ctx.Yaml.Doc(r.FileId);
                        if (doc == null || ScriptInProject(doc)) { removable = false; break; }
                        PortValue value = _ctx.Yaml.Body(doc);
                        missing.Add((value, PortRules.ForFields(value, PortKind.Behaviour)));
                    }
                if (!removable)
                {
                    Note("A state in '" + _ctx.Path + "' has missing behaviours that are not from another platform; that state was left alone.");
                    return;
                }
            }
            if (gone.Count == 0 && missing.Count == 0 && !hasMissing) return;

            var made = new List<StateMachineBehaviour>();
            foreach ((StateMachineBehaviour old, PortRule rule) in gone) Replace(PortValue.FromObject(old), rule, rule.Name);
            foreach ((PortValue value, PortRule rule) in missing) Replace(value, rule, rule != null ? rule.Name : "Missing state behaviour");

            void Replace(PortValue value, PortRule rule, string name)
            {
                Type target = rule?.Target?.Invoke(null);
                if (target == null)
                {
                    _report.Count(_report.Removed, name);
                    return;
                }
                StateMachineBehaviour replacement = add(target);
                PortWriter.CopyInto(replacement, value, rule, this);
                made.Add(replacement);
                _report.Count(_report.Ported, name);
            }

            // The kept ones in their order, then ParelVR's; the replaced ones are deleted from the file.
            var result = new List<StateMachineBehaviour>(keep);
            result.AddRange(made);
            set(result.ToArray());
            foreach ((StateMachineBehaviour old, PortRule _) in gone) UnityEngine.Object.DestroyImmediate(old, true);
        }
    }
}
