using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ParelVR.SDK.Core;
using ParelVR.SDK.Worlds.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using ValueType = ParelVR.SDK.Worlds.Components.ParelPlayerParameters.ValueType;

namespace ParelVR.Volt.EditorTools
{
    using Scene = UnityEngine.SceneManagement.Scene;

    /// <summary>
    /// Connects the world's Player Parameters asset to the PlayerParameters world script: installs the script, keeps
    /// one object with it in the world scene and copies the definitions into it before every play and build.
    /// </summary>
    public static class VoltPlayerParameterSync
    {
        public const string ScriptTypeName = "PlayerParameters";
        public const string InstallFolder = "Assets/ParelVR/Volt";
        public const string ObjectName = "Player Parameters";
        private const string Marker = "ParelVR Player Parameters, script version ";

        private static string TemplatePath => ParelPackagePaths.FullPath("Volt/Editor/Templates/PlayerParameters.cs.txt");

        public static Type ScriptType() => TypeCache.GetTypesDerivedFrom<VoltBehaviour>().FirstOrDefault(t => t.Name == ScriptTypeName && !t.IsAbstract);

        /// <summary>The project path of the installed script, or null.</summary>
        public static string InstalledPath()
        {
            foreach (string guid in AssetDatabase.FindAssets(ScriptTypeName + " t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == ScriptTypeName && File.Exists(path) && File.ReadAllText(path).Contains(Marker)) return path;
            }
            return null;
        }

        public static int VersionOf(string text)
        {
            int at = text?.IndexOf(Marker, StringComparison.Ordinal) ?? -1;
            if (at < 0) return 0;
            at += Marker.Length;
            int end = at;
            while (end < text.Length && char.IsDigit(text[end])) end++;
            return int.TryParse(text.Substring(at, end - at), out int v) ? v : 0;
        }

        public static int TemplateVersion => File.Exists(TemplatePath) ? VersionOf(File.ReadAllText(TemplatePath)) : 0;
        public static int InstalledVersion { get { string p = InstalledPath(); return p == null ? 0 : VersionOf(File.ReadAllText(p)); } }

        /// <summary>Copies the script into the project (or over an older copy of it). Unity then compiles it.</summary>
        public static void Install()
        {
            if (!File.Exists(TemplatePath)) { Debug.LogError("[Volt] The Player Parameters script is missing from the SDK (" + TemplatePath + ")."); return; }
            string path = InstalledPath();
            if (path == null)
            {
                if (ScriptType() != null)
                {
                    Debug.LogError("[Volt] The project already has its own class named PlayerParameters. Rename it, then install the Player Parameters script.");
                    return;
                }
                Directory.CreateDirectory(InstallFolder);
                path = InstallFolder + "/" + ScriptTypeName + ".cs";
            }
            File.WriteAllText(path, File.ReadAllText(TemplatePath));
            AssetDatabase.ImportAsset(path);
            EnsureCapabilities();
            Debug.Log("[Volt] Installed the Player Parameters script at " + path + ". Read values with PlayerParameters.Get(player, \"Name\").");
        }

        /// <summary>The script sends network events and keeps saved values in storage: the world needs both capabilities to compile.</summary>
        public static void EnsureCapabilities()
        {
            if (ScriptType() == null && InstalledPath() == null) return;
            VoltSettings s = VoltSettings.instance;
            uint need = (uint)ParelVR.Volt.Core.VoltCapability.Network | (uint)ParelVR.Volt.Core.VoltCapability.Storage;
            if ((s.capabilities & need) == need) return;
            s.capabilities |= need;
            s.SaveNow();
            Debug.Log("[Volt] Turned on the Networking and Storage capabilities: the Player Parameters script uses them.");
        }

        public static ParelWorldDescriptor FindDescriptor(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                ParelWorldDescriptor d = root.GetComponentInChildren<ParelWorldDescriptor>(true);
                if (d != null) return d;
            }
            return null;
        }

        public static Component FindObject(Scene scene, Type type)
        {
            if (type == null || !scene.IsValid() || !scene.isLoaded) return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Component c = root.GetComponentInChildren(type, true);
                if (c != null) return c;
            }
            return null;
        }

        /// <summary>
        /// Puts the definitions into the scene's PlayerParameters object, adding the object when the scene has the asset
        /// but not the object yet. Returns false when there is nothing to do (no asset, or the script is not compiled yet).
        /// </summary>
        public static bool Apply(Scene scene, bool editTime)
        {
            ParelWorldDescriptor descriptor = FindDescriptor(scene);
            ParelPlayerParameters asset = descriptor != null ? descriptor.playerParameters : null;
            Type type = ScriptType();
            if (asset == null || type == null) return false;

            Component target = FindObject(scene, type);
            if (target == null)
            {
                var go = new GameObject(ObjectName);
                SceneManager.MoveGameObjectToScene(go, scene);
                if (editTime) Undo.RegisterCreatedObjectUndo(go, "Add Player Parameters");
                target = go.AddComponent(type);
            }

            var rows = (asset.parameters ?? new List<ParelPlayerParameters.Parameter>()).Where(p => p != null && !string.IsNullOrEmpty(p.name)).ToList();
            var so = new SerializedObject(target);
            bool changed = false;
            changed |= SetArray(so, "names", rows.Count, (e, i) => Set(e, rows[i].name));
            changed |= SetArray(so, "types", rows.Count, (e, i) => Set(e, (int)rows[i].valueType));
            changed |= SetArray(so, "defaults", rows.Count, (e, i) => Set(e, ParelPlayerParameters.Sanitize(rows[i].valueType, rows[i].defaultValue)));
            changed |= SetArray(so, "saved", rows.Count, (e, i) => Set(e, rows[i].saved));
            changed |= SetArray(so, "synced", rows.Count, (e, i) => Set(e, rows[i].synced));
            SerializedProperty hash = so.FindProperty("schemaHash");
            int schema = asset.ComputeSchemaHash();
            if (hash != null && hash.intValue != schema) { hash.intValue = schema; changed = true; }
            if (!changed) return true;
            if (editTime) so.ApplyModifiedProperties();
            else so.ApplyModifiedPropertiesWithoutUndo();
            if (editTime && !Application.isPlaying) EditorSceneManager.MarkSceneDirty(scene);
            return true;
        }

        private static bool SetArray(SerializedObject so, string name, int count, Func<SerializedProperty, int, bool> set)
        {
            SerializedProperty array = so.FindProperty(name);
            if (array == null || !array.isArray) return false;
            bool changed = array.arraySize != count;
            array.arraySize = count;
            for (int i = 0; i < count; i++) changed |= set(array.GetArrayElementAtIndex(i), i);
            return changed;
        }

        private static bool Set(SerializedProperty p, string v) { if (p.stringValue == v) return false; p.stringValue = v; return true; }
        private static bool Set(SerializedProperty p, int v) { if (p.intValue == v) return false; p.intValue = v; return true; }
        private static bool Set(SerializedProperty p, float v) { if (Mathf.Approximately(p.floatValue, v)) return false; p.floatValue = v; return true; }
        private static bool Set(SerializedProperty p, bool v) { if (p.boolValue == v) return false; p.boolValue = v; return true; }
    }

    /// <summary>
    /// The Player Parameters page of the Volt settings window, laid out like the Animator's Parameters list: a search
    /// field and a + menu on top, one row per parameter with its default on the right, the synced memory below.
    /// </summary>
    internal sealed class VoltPlayerParametersPage
    {
        private ReorderableList _list;
        private ParelPlayerParameters _bound;
        private string _search = string.Empty;
        private Vector2 _scroll;
        private GUIStyle _nameStyle, _typeStyle, _flag;

        private void Styles()
        {
            if (_nameStyle != null) return;
            _nameStyle = new GUIStyle(EditorStyles.textField) { normal = { background = null }, focused = { background = EditorStyles.textField.focused.background } };
            _typeStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            _flag = new GUIStyle(EditorStyles.miniButton) { fixedHeight = 0f, padding = new RectOffset(4, 4, 1, 1) };
        }

        public void OnGUI()
        {
            Styles();
            Scene scene = SceneManager.GetActiveScene();
            ParelWorldDescriptor descriptor = VoltPlayerParameterSync.FindDescriptor(scene);
            if (descriptor == null)
            {
                EditorGUILayout.HelpBox("Open your world scene: Player Parameters belong to the scene with the ParelVR World Descriptor.", MessageType.Info);
                return;
            }

            EditorGUI.BeginChangeCheck();
            var asset = (ParelPlayerParameters)EditorGUILayout.ObjectField("Parameters Asset", descriptor.playerParameters, typeof(ParelPlayerParameters), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(descriptor, "Player Parameters Asset");
                descriptor.playerParameters = asset;
                EditorUtility.SetDirty(descriptor);
                AfterChange(scene);
            }
            if (asset == null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("Named values every player carries in this world (team, role, VIP, toggles), like animator parameters. " +
                                        "World scripts read them with PlayerParameters.Get(player, \"Name\").", MessageType.None);
                if (GUILayout.Button("Create Player Parameters", GUILayout.Height(24))) Create(descriptor, scene);
                return;
            }

            ScriptStatus(scene);
            EditorGUILayout.Space(4);
            Bind(asset);
            Toolbar(asset);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (string.IsNullOrEmpty(_search)) _list.DoLayoutList();
            else Filtered(asset);
            EditorGUILayout.EndScrollView();
            Footer(asset);
        }

        // ── top ──────────────────────────────────────────────────────────────

        private void Toolbar(ParelPlayerParameters asset)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(60));
                using (new EditorGUI.DisabledScope(asset.parameters.Count >= ParelPlayerParameters.MaxParameters))
                {
                    if (EditorGUILayout.DropdownButton(new GUIContent("+"), FocusType.Passive, EditorStyles.toolbarDropDown, GUILayout.Width(36)))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Float"), false, () => Add(asset, ValueType.Float));
                        menu.AddItem(new GUIContent("Int"), false, () => Add(asset, ValueType.Int));
                        menu.AddItem(new GUIContent("Bool"), false, () => Add(asset, ValueType.Bool));
                        menu.ShowAsContext();
                    }
                }
            }
        }

        private void ScriptStatus(Scene scene)
        {
            int installed = VoltPlayerParameterSync.InstalledVersion, template = VoltPlayerParameterSync.TemplateVersion;
            Type type = VoltPlayerParameterSync.ScriptType();
            if (installed == 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox("The PlayerParameters world script is not in the project yet.", MessageType.Warning);
                    if (GUILayout.Button("Install Script", GUILayout.Width(110), GUILayout.Height(38))) VoltPlayerParameterSync.Install();
                }
                return;
            }
            if (installed < template)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox("A newer PlayerParameters script comes with this SDK (version " + template + ", the project has " + installed + ").", MessageType.Info);
                    if (GUILayout.Button("Update Script", GUILayout.Width(110), GUILayout.Height(38))) VoltPlayerParameterSync.Install();
                }
            }
            if (type == null)
            {
                EditorGUILayout.HelpBox("The PlayerParameters script is waiting for Unity to compile it.", MessageType.None);
                return;
            }
            if (VoltPlayerParameterSync.FindObject(scene, type) == null && !Application.isPlaying)
                EditorApplication.delayCall += () => VoltPlayerParameterSync.Apply(SceneManager.GetActiveScene(), true);
        }

        // ── the list ─────────────────────────────────────────────────────────

        private void Bind(ParelPlayerParameters asset)
        {
            if (_list != null && _bound == asset) return;
            _bound = asset;
            _list = new ReorderableList(asset.parameters, typeof(ParelPlayerParameters.Parameter), true, false, false, false)
            {
                headerHeight = 0f,
                footerHeight = 0f,
                elementHeight = EditorGUIUtility.singleLineHeight + 6f,
                showDefaultBackground = true,
                drawElementCallback = (rect, index, active, focused) => Row(asset, rect, index),
                onReorderCallback = _ => Changed(asset, "Reorder Player Parameters", false),
                drawNoneElementCallback = rect => EditorGUI.LabelField(rect, "List is Empty", EditorStyles.centeredGreyMiniLabel),
            };
        }

        private void Filtered(ParelPlayerParameters asset)
        {
            float h = EditorGUIUtility.singleLineHeight + 6f;
            bool any = false;
            for (int i = 0; i < asset.parameters.Count; i++)
            {
                ParelPlayerParameters.Parameter p = asset.parameters[i];
                if (p == null || p.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                any = true;
                Rect r = GUILayoutUtility.GetRect(0f, h, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint && ReorderableList.defaultBehaviours != null) ReorderableList.defaultBehaviours.elementBackground.Draw(r, false, false, false, false);
                Row(asset, new Rect(r.x + 18f, r.y, r.width - 22f, r.height), i);
            }
            if (!any) EditorGUILayout.LabelField("No parameter matches \"" + _search + "\"", EditorStyles.centeredGreyMiniLabel);
        }

        private void Row(ParelPlayerParameters asset, Rect rect, int index)
        {
            if (index < 0 || index >= asset.parameters.Count) return;
            ParelPlayerParameters.Parameter p = asset.parameters[index];
            if (p == null) return;
            float line = EditorGUIUtility.singleLineHeight;
            rect.y += 3f;
            rect.height = line;

            const float valueW = 64f, flagW = 50f, typeW = 34f, gap = 4f;
            var value = new Rect(rect.xMax - valueW, rect.y, valueW, line);
            var synced = new Rect(value.x - gap - flagW, rect.y, flagW, line);
            var saved = new Rect(synced.x - 2f - flagW, rect.y, flagW, line);
            var type = new Rect(saved.x - gap - typeW, rect.y, typeW, line);
            var name = new Rect(rect.x, rect.y, Mathf.Max(40f, type.x - gap - rect.x), line);

            Event e = Event.current;
            if (e.type == EventType.ContextClick && rect.Contains(e.mousePosition))
            {
                RowMenu(asset, index);
                e.Use();
            }

            bool bad = string.IsNullOrEmpty(p.name) || asset.parameters.Count(o => o != null && o.name == p.name) > 1;
            Color was = GUI.color;
            if (bad) GUI.color = new Color(1f, 0.55f, 0.5f);
            EditorGUI.BeginChangeCheck();
            string newName = EditorGUI.DelayedTextField(name, p.name, _nameStyle);
            GUI.color = was;
            if (EditorGUI.EndChangeCheck()) Edit(asset, "Rename Player Parameter", () => p.name = Clean(newName));

            EditorGUI.LabelField(type, p.valueType.ToString(), _typeStyle);

            EditorGUI.BeginChangeCheck();
            bool s = GUI.Toggle(saved, p.saved, new GUIContent("Saved", "Kept on the player's account and restored on their next visit."), _flag);
            bool y = GUI.Toggle(synced, p.synced, new GUIContent("Synced", "Sent to everyone in the instance. Bool 1 bit, Int and Float 8 bits."), _flag);
            if (EditorGUI.EndChangeCheck()) Edit(asset, "Player Parameter Options", () => { p.saved = s; p.synced = y; });

            EditorGUI.BeginChangeCheck();
            float v = p.defaultValue;
            switch (p.valueType)
            {
                case ValueType.Bool:
                    v = EditorGUI.Toggle(new Rect(value.xMax - 16f, value.y, 16f, line), v >= 0.5f) ? 1f : 0f;
                    break;
                case ValueType.Int:
                    v = Mathf.Clamp(EditorGUI.IntField(value, Mathf.RoundToInt(v)), 0, 255);
                    break;
                default:
                    v = Mathf.Clamp(EditorGUI.FloatField(value, v), -1f, 1f);
                    break;
            }
            if (EditorGUI.EndChangeCheck()) Edit(asset, "Player Parameter Default", () => p.defaultValue = ParelPlayerParameters.Sanitize(p.valueType, v));
        }

        private void RowMenu(ParelPlayerParameters asset, int index)
        {
            ParelPlayerParameters.Parameter p = asset.parameters[index];
            var menu = new GenericMenu();
            foreach (ValueType t in new[] { ValueType.Float, ValueType.Int, ValueType.Bool })
            {
                ValueType to = t;
                menu.AddItem(new GUIContent("Type/" + t), p.valueType == t, () => Edit(asset, "Player Parameter Type", () =>
                {
                    p.valueType = to;
                    p.defaultValue = ParelPlayerParameters.Sanitize(to, p.defaultValue);
                }));
            }
            menu.AddItem(new GUIContent("Duplicate"), false, () => Edit(asset, "Duplicate Player Parameter", () =>
            {
                ParelPlayerParameters.Parameter copy = p.Clone();
                copy.name = Unique(asset, p.name);
                asset.parameters.Insert(index + 1, copy);
            }));
            menu.AddItem(new GUIContent("Delete"), false, () => Edit(asset, "Delete Player Parameter", () => asset.parameters.RemoveAt(index)));
            menu.ShowAsContext();
        }

        // ── below ────────────────────────────────────────────────────────────

        private static void Footer(ParelPlayerParameters asset)
        {
            int cost = asset.CalcTotalCost();
            Rect bar = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.ProgressBar(bar, Mathf.Clamp01(cost / (float)ParelPlayerParameters.MaxSyncedBits), "Synced memory: " + cost + " / " + ParelPlayerParameters.MaxSyncedBits + " bits");
            List<string> problems = asset.FindProblems();
            if (problems.Count > 0) EditorGUILayout.HelpBox(string.Join("\n", problems), MessageType.Error);
            EditorGUILayout.LabelField("Right-click a row to change its type, duplicate or delete it. Defaults are never sent: every client starts from this list.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Schema " + asset.ComputeSchemaHash().ToString("X8") + " · clients with a different list ignore each other's values", EditorStyles.miniLabel);
        }

        // ── changes ──────────────────────────────────────────────────────────

        private static void Create(ParelWorldDescriptor descriptor, Scene scene)
        {
            Directory.CreateDirectory("Assets/ParelVR");
            AssetDatabase.Refresh();
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/ParelVR/PlayerParameters.asset");
            var asset = ScriptableObject.CreateInstance<ParelPlayerParameters>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Undo.RecordObject(descriptor, "Create Player Parameters");
            descriptor.playerParameters = asset;
            EditorUtility.SetDirty(descriptor);
            EditorSceneManager.MarkSceneDirty(scene);
            if (VoltPlayerParameterSync.InstalledVersion == 0) VoltPlayerParameterSync.Install();
            AfterChange(scene);
        }

        private void Add(ParelPlayerParameters asset, ValueType type)
        {
            Edit(asset, "Add Player Parameter", () => asset.parameters.Add(new ParelPlayerParameters.Parameter
            {
                name = Unique(asset, "New " + type),
                valueType = type,
                synced = true,
            }));
        }

        private static void Edit(ParelPlayerParameters asset, string undo, Action change)
        {
            Undo.RecordObject(asset, undo);
            change();
            Changed(asset, undo, true);
        }

        private static void Changed(ParelPlayerParameters asset, string undo, bool recorded)
        {
            if (!recorded) Undo.RecordObject(asset, undo);
            EditorUtility.SetDirty(asset);
            AfterChange(SceneManager.GetActiveScene());
        }

        private static void AfterChange(Scene scene)
        {
            if (!Application.isPlaying) VoltPlayerParameterSync.Apply(scene, true);
        }

        private static string Unique(ParelPlayerParameters asset, string wanted)
        {
            string name = Clean(wanted);
            if (asset.FindParameter(name) == null) return name;
            for (int i = 0; ; i++)
            {
                string candidate = Clean(name + "_" + i);
                if (asset.FindParameter(candidate) == null) return candidate;
            }
        }

        /// <summary>Spaces become underscores; anything else a storage key cannot hold is dropped.</summary>
        private static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var chars = name.Trim().Replace(' ', '_').Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.').Take(ParelPlayerParameters.MaxNameLength).ToArray();
            return new string(chars);
        }
    }
}
