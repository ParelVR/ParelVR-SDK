using System.Collections.Generic;
using System.Linq;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Avatars.Build;
using ParelVR.SDK.Avatars.Validation;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Inspectors
{
    /// <summary>Shared helpers for the Avatar Tools: the descriptor summary and the GameObject menu.</summary>
    public static class ParelAvatarToolsGUI
    {
        public static int ExtraSyncedBits(ParelAvatarDescriptor descriptor) => ParelAvatarToolsValidator.ExtraSyncedBits(descriptor);

        public static void DrawSummary(ParelAvatarDescriptor descriptor)
        {
            GameObject root = descriptor.gameObject;
            int toggles = root.GetComponentsInChildren<ParelObjectToggle>(true).Length;
            int outfits = root.GetComponentsInChildren<ParelOutfitSwitcher>(true).Length;
            int clothing = root.GetComponentsInChildren<ParelClothing>(true).Length;
            int attachments = root.GetComponentsInChildren<ParelBoneAttachment>(true).Length;
            int syncs = root.GetComponentsInChildren<ParelBlendshapeSync>(true).Length;

            EditorGUILayout.LabelField($"{toggles} toggle(s) · {outfits} outfit switcher(s) · {clothing} clothing · {attachments} attachment(s) · {syncs} blend shape sync(s)", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("Avatar Tools are applied when you build: they create the toggles' parameters, menu buttons and FX layers and join clothing to the avatar on a copy. Your scene isn't changed.\nRight-click objects in the Hierarchy > ParelVR to add them.", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Toggle For Selection", EditorStyles.miniButton)) ParelAvatarToolsMenu.MakeToggles();
                if (GUILayout.Button("Outfits From Selection", EditorStyles.miniButton)) ParelAvatarToolsMenu.MakeOutfitSwitcher();
                if (GUILayout.Button("Set Up Clothing", EditorStyles.miniButton)) ParelAvatarToolsMenu.SetUpClothing();
            }
        }

        internal static ParelAvatarDescriptor AvatarOf(Component component) => component != null ? component.GetComponentInParent<ParelAvatarDescriptor>(true) : null;

        internal static void HeaderHelp(string text)
        {
            EditorGUILayout.HelpBox(text, MessageType.None);
            EditorGUILayout.Space(2);
        }

        internal static string[] BlendShapeNames(SkinnedMeshRenderer renderer)
        {
            if (renderer == null || renderer.sharedMesh == null) return new string[0];
            var names = new string[renderer.sharedMesh.blendShapeCount];
            for (int i = 0; i < names.Length; i++) names[i] = renderer.sharedMesh.GetBlendShapeName(i);
            return names;
        }
    }

    // =============================================================================================
    // GameObject > ParelVR menu
    // =============================================================================================

    public static class ParelAvatarToolsMenu
    {
        private const string Root = "GameObject/ParelVR/";

        [MenuItem(Root + "Make Toggle", false, 20)]
        public static void MakeToggles()
        {
            var created = new List<Object>();
            foreach (GameObject go in Selection.gameObjects)
            {
                ParelAvatarDescriptor avatar = go.GetComponentInParent<ParelAvatarDescriptor>(true);
                if (avatar == null || avatar.gameObject == go || go.GetComponent<ParelObjectToggle>() != null) continue;
                var toggle = Undo.AddComponent<ParelObjectToggle>(go);
                toggle.defaultOn = go.activeSelf;
                created.Add(toggle);
            }
            if (created.Count == 0)
            {
                EditorUtility.DisplayDialog("Make Toggle", "Select the objects to toggle first -- they need to be inside an avatar that has a Parel Avatar Descriptor.", "OK");
                return;
            }
            Selection.objects = created.Select(c => (Object)((Component)c).gameObject).ToArray();
        }

        [MenuItem(Root + "Make One Toggle For Selection", false, 21)]
        public static void MakeOneToggle()
        {
            GameObject[] selected = Selection.gameObjects;
            ParelAvatarDescriptor avatar = selected.Select(g => g.GetComponentInParent<ParelAvatarDescriptor>(true)).FirstOrDefault(d => d != null);
            if (avatar == null || selected.Length == 0)
            {
                EditorUtility.DisplayDialog("Make Toggle", "Select the objects to toggle first -- they need to be inside an avatar.", "OK");
                return;
            }
            var holder = new GameObject("Toggle - " + selected[0].name);
            Undo.RegisterCreatedObjectUndo(holder, "Make Toggle");
            holder.transform.SetParent(avatar.transform, false);
            var toggle = holder.AddComponent<ParelObjectToggle>();
            toggle.menuName = selected[0].name;
            toggle.defaultOn = selected[0].activeSelf;
            foreach (GameObject go in selected)
            {
                if (go != avatar.gameObject) toggle.objects.Add(new ParelToggledObject { target = go });
            }
            Selection.activeGameObject = holder;
        }

        [MenuItem(Root + "Make Outfit Switcher From Selection", false, 22)]
        public static void MakeOutfitSwitcher()
        {
            GameObject[] selected = Selection.gameObjects;
            ParelAvatarDescriptor avatar = selected.Select(g => g.GetComponentInParent<ParelAvatarDescriptor>(true)).FirstOrDefault(d => d != null);
            if (avatar == null || selected.Length == 0)
            {
                EditorUtility.DisplayDialog("Outfit Switcher", "Select each outfit's root object (they need to be inside an avatar), then run this again. Each one becomes an outfit.", "OK");
                return;
            }
            var holder = new GameObject("Outfits");
            Undo.RegisterCreatedObjectUndo(holder, "Make Outfit Switcher");
            holder.transform.SetParent(avatar.transform, false);
            var switcher = holder.AddComponent<ParelOutfitSwitcher>();
            List<GameObject> ordered = selected.Where(g => g != avatar.gameObject).OrderBy(g => g.transform.GetSiblingIndex()).ToList();
            foreach (GameObject go in ordered) switcher.outfits.Add(new ParelOutfit { name = go.name, objects = new[] { go } });
            // Whatever is showing right now becomes the default outfit.
            switcher.defaultOutfit = Mathf.Max(0, ordered.FindIndex(g => g.activeSelf));
            Selection.activeGameObject = holder;
        }

        [MenuItem(Root + "Set Up As Clothing", false, 40)]
        public static void SetUpClothing()
        {
            GameObject clothing = Selection.activeGameObject;
            if (clothing == null || EditorUtility.IsPersistent(clothing))
            {
                EditorUtility.DisplayDialog("Set Up Clothing", "Drag the clothing into the scene (onto your avatar), select its root object, and run this again.", "OK");
                return;
            }

            ParelAvatarDescriptor avatar = clothing.GetComponentInParent<ParelAvatarDescriptor>(true);
            if (avatar == null)
            {
                ParelAvatarDescriptor[] avatars = Object.FindObjectsByType<ParelAvatarDescriptor>(FindObjectsInactive.Include);
                if (avatars.Length != 1)
                {
                    EditorUtility.DisplayDialog("Set Up Clothing", "Drag the clothing onto your avatar in the Hierarchy first (so it's a child of the avatar), then run this again.", "OK");
                    return;
                }
                avatar = avatars[0];
                Undo.SetTransformParent(clothing.transform, avatar.transform, "Set Up Clothing");
            }
            if (avatar.gameObject == clothing)
            {
                EditorUtility.DisplayDialog("Set Up Clothing", "Select the clothing's root object, not the avatar.", "OK");
                return;
            }

            ParelClothing component = clothing.GetComponent<ParelClothing>();
            if (component == null) component = Undo.AddComponent<ParelClothing>(clothing);

            ParelClothingMatcher.Result match = ParelClothingMatcher.Match(avatar, component);
            string summary = match.Ok
                ? $"'{clothing.name}' matches {match.Pairs.Count} of the avatar's bones" + (match.Prefix.Length + match.Suffix.Length > 0 ? $" (bone names \"{match.Prefix}…{match.Suffix}\")" : string.Empty) + $"; {match.Unmatched.Count} extra bone(s) stay with the clothing.\n\nIt's joined to the avatar when you build."
                : $"'{clothing.name}' couldn't be matched yet: {match.Error}\n\nCheck the Clothing component in the Inspector.";

            bool addToggle = clothing.GetComponent<ParelObjectToggle>() == null &&
                             EditorUtility.DisplayDialog("Set Up Clothing", summary + "\n\nAdd an on/off button for it to the Expressions Menu (under \"Clothing\")?", "Add Toggle", "No Thanks");
            if (addToggle)
            {
                var toggle = Undo.AddComponent<ParelObjectToggle>(clothing);
                toggle.menuFolder = "Clothing";
                toggle.defaultOn = clothing.activeSelf;
            }
            else if (!match.Ok)
            {
                EditorUtility.DisplayDialog("Set Up Clothing", summary, "OK");
            }
            Selection.activeGameObject = clothing;
        }

        [MenuItem(Root + "Attach To Nearest Bone", false, 41)]
        public static void AttachToNearestBone()
        {
            foreach (GameObject go in Selection.gameObjects)
            {
                ParelAvatarDescriptor avatar = go.GetComponentInParent<ParelAvatarDescriptor>(true);
                Animator animator = avatar != null ? avatar.AvatarAnimator : null;
                if (animator == null || !animator.isHuman || avatar.gameObject == go) continue;

                HumanBodyBones best = HumanBodyBones.Head;
                float bestDistance = float.MaxValue;
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    Transform bone = animator.GetBoneTransform((HumanBodyBones)i);
                    if (bone == null || bone.IsChildOf(go.transform)) continue;
                    float d = Vector3.Distance(bone.position, go.transform.position);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = (HumanBodyBones)i;
                    }
                }
                ParelBoneAttachment attachment = go.GetComponent<ParelBoneAttachment>();
                if (attachment == null) attachment = Undo.AddComponent<ParelBoneAttachment>(go);
                Undo.RecordObject(attachment, "Attach To Bone");
                attachment.bone = best;
            }
        }

        [MenuItem("CONTEXT/SkinnedMeshRenderer/ParelVR: Sync Blend Shapes With Body")]
        private static void SyncWithBody(MenuCommand command)
        {
            var renderer = command.context as SkinnedMeshRenderer;
            if (renderer == null) return;
            ParelAvatarDescriptor avatar = renderer.GetComponentInParent<ParelAvatarDescriptor>(true);
            SkinnedMeshRenderer body = FindBody(avatar, renderer);
            if (body == null)
            {
                EditorUtility.DisplayDialog("Blend Shape Sync", "Couldn't find the avatar's body mesh. Add a Blend Shape Sync and set its Source by hand.", "OK");
                return;
            }
            ParelBlendshapeSync sync = renderer.GetComponent<ParelBlendshapeSync>();
            if (sync == null) sync = Undo.AddComponent<ParelBlendshapeSync>(renderer.gameObject);
            Undo.RecordObject(sync, "Sync Blend Shapes");
            sync.source = body;
        }

        internal static SkinnedMeshRenderer FindBody(ParelAvatarDescriptor avatar, SkinnedMeshRenderer except)
        {
            if (avatar == null) return null;
            SkinnedMeshRenderer[] meshes = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(m => m != except && m.sharedMesh != null).ToArray();
            SkinnedMeshRenderer named = meshes.FirstOrDefault(m => m.name.ToLowerInvariant() == "body");
            if (named != null) return named;
            if (avatar.VisemeSkinnedMesh != null && avatar.VisemeSkinnedMesh != except) return avatar.VisemeSkinnedMesh;
            return meshes.OrderByDescending(m => m.sharedMesh.blendShapeCount).FirstOrDefault();
        }

        [MenuItem(Root + "Make Toggle", true)]
        [MenuItem(Root + "Make One Toggle For Selection", true)]
        [MenuItem(Root + "Make Outfit Switcher From Selection", true)]
        [MenuItem(Root + "Set Up As Clothing", true)]
        [MenuItem(Root + "Attach To Nearest Bone", true)]
        private static bool HasSelection() => Selection.activeGameObject != null;
    }

    // =============================================================================================
    // Blend shape change drawer (Object Toggle / Outfit Switcher lists)
    // =============================================================================================

    [CustomPropertyDrawer(typeof(ParelBlendShapeChange))]
    public sealed class ParelBlendShapeChangeDrawer : PropertyDrawer
    {
        private const float Line = 20f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => Line * 4f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty renderer = property.FindPropertyRelative("renderer");
            SerializedProperty shape = property.FindPropertyRelative("blendShape");
            SerializedProperty on = property.FindPropertyRelative("onValue");
            SerializedProperty off = property.FindPropertyRelative("offValue");

            var row = new Rect(position.x, position.y + 1, position.width, EditorGUIUtility.singleLineHeight);
            EditorGUI.PropertyField(row, renderer, new GUIContent("Mesh"));
            row.y += Line;
            string[] names = ParelAvatarToolsGUI.BlendShapeNames(renderer.objectReferenceValue as SkinnedMeshRenderer);
            if (names.Length > 0)
            {
                int index = System.Array.IndexOf(names, shape.stringValue);
                int picked = EditorGUI.Popup(row, "Blend Shape", index, names);
                if (picked >= 0 && picked != index) shape.stringValue = names[picked];
            }
            else
            {
                EditorGUI.PropertyField(row, shape, new GUIContent("Blend Shape"));
            }
            row.y += Line;
            EditorGUI.Slider(row, on, 0f, 100f, new GUIContent("When On"));
            row.y += Line;
            EditorGUI.Slider(row, off, 0f, 100f, new GUIContent("When Off"));
        }
    }

    // =============================================================================================
    // Inspectors
    // =============================================================================================

    [CustomEditor(typeof(ParelObjectToggle))]
    public sealed class ParelObjectToggleEditor : Editor
    {
        private static bool _showMore;

        public override void OnInspectorGUI()
        {
            var toggle = (ParelObjectToggle)target;
            serializedObject.Update();
            ParelAvatarToolsGUI.HeaderHelp("Adds an on/off button to the Expressions Menu. The parameter, the menu button and the FX layer are made for you when the avatar is built.");

            if (ParelAvatarToolsGUI.AvatarOf(toggle) == null) EditorGUILayout.HelpBox("This toggle isn't inside an avatar with a Parel Avatar Descriptor, so it does nothing.", MessageType.Warning);

            EditorGUILayout.LabelField("Menu", EditorStyles.boldLabel);
            SerializedProperty menuName = serializedObject.FindProperty("menuName");
            EditorGUILayout.PropertyField(menuName, new GUIContent("Name", "Empty = this object's name."));
            if (string.IsNullOrWhiteSpace(menuName.stringValue))
            {
                Rect last = GUILayoutUtility.GetLastRect();
                var hint = new Rect(last.x + EditorGUIUtility.labelWidth + 4, last.y, last.width - EditorGUIUtility.labelWidth, last.height);
                GUI.Label(hint, toggle.gameObject.name, EditorStyles.centeredGreyMiniLabel);
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("menuFolder"), new GUIContent("Folder", "Sub menu path, e.g. Clothing/Hats. Empty = top of the menu."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("icon"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Behaviour", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultOn"), new GUIContent("On By Default"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("saved"), new GUIContent("Remember Setting"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("synced"), new GUIContent("Others See It"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("What It Toggles", EditorStyles.boldLabel);
            SerializedProperty objects = serializedObject.FindProperty("objects");
            EditorGUILayout.PropertyField(objects, new GUIContent("Objects"), true);
            if (objects.arraySize == 0 && toggle.blendShapes.Count == 0 && toggle.materialSwaps.Count == 0)
            {
                EditorGUILayout.LabelField("Nothing listed: it toggles this object.", EditorStyles.miniLabel);
            }

            _showMore = EditorGUILayout.Foldout(_showMore, "Blend Shapes, Materials & Parameter", true);
            if (_showMore)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("blendShapes"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("materialSwaps"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("parameterName"), new GUIContent("Parameter", "Empty = made from the name. Give toggles the same parameter to link them."));
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            string folder = string.IsNullOrWhiteSpace(toggle.menuFolder) ? string.Empty : toggle.menuFolder.Trim().TrimEnd('/') + "/";
            string parameter = string.IsNullOrWhiteSpace(toggle.parameterName) ? toggle.MenuLabel : toggle.parameterName.Trim();
            EditorGUILayout.LabelField($"Menu: {folder}{toggle.MenuLabel}    Parameter: {parameter} (Bool{(toggle.synced ? ", 1 synced bit" : ", local")})", EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview On")) Preview(toggle, true);
                if (GUILayout.Button("Preview Off")) Preview(toggle, false);
            }
        }

        private static void Preview(ParelObjectToggle toggle, bool on)
        {
            foreach (ParelToggledObject entry in toggle.Targets())
            {
                Undo.RecordObject(entry.target, "Preview Toggle");
                entry.target.SetActive(on != entry.invert);
            }
            foreach (ParelBlendShapeChange change in toggle.blendShapes)
            {
                if (change?.renderer == null || change.renderer.sharedMesh == null) continue;
                int index = change.renderer.sharedMesh.GetBlendShapeIndex(change.blendShape);
                if (index < 0) continue;
                Undo.RecordObject(change.renderer, "Preview Toggle");
                change.renderer.SetBlendShapeWeight(index, on ? change.onValue : change.offValue);
            }
        }
    }

    [CustomEditor(typeof(ParelOutfitSwitcher))]
    public sealed class ParelOutfitSwitcherEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var switcher = (ParelOutfitSwitcher)target;
            serializedObject.Update();
            ParelAvatarToolsGUI.HeaderHelp("Switch between whole outfits from one sub menu. Wearing one outfit hides the others. One Int parameter, the menu and the FX layer are made for you when the avatar is built.");

            EditorGUILayout.PropertyField(serializedObject.FindProperty("menuName"), new GUIContent("Menu Name"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("menuFolder"), new GUIContent("Folder"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("icon"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("saved"), new GUIContent("Remember Outfit"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("synced"), new GUIContent("Others See It"));

            string[] names = switcher.outfits.Select((o, i) => o == null || string.IsNullOrWhiteSpace(o.name) ? "Outfit " + (i + 1) : o.name).ToArray();
            SerializedProperty defaultOutfit = serializedObject.FindProperty("defaultOutfit");
            if (names.Length > 0) defaultOutfit.intValue = EditorGUILayout.Popup("Default Outfit", Mathf.Clamp(defaultOutfit.intValue, 0, names.Length - 1), names);

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("outfits"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("parameterName"), new GUIContent("Parameter", "Empty = made from the menu name."));
            serializedObject.ApplyModifiedProperties();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Outfit From Selection"))
                {
                    GameObject[] selected = Selection.gameObjects.Where(g => g != switcher.gameObject).ToArray();
                    if (selected.Length > 0)
                    {
                        Undo.RecordObject(switcher, "Add Outfit");
                        switcher.outfits.Add(new ParelOutfit { name = selected[0].name, objects = selected });
                    }
                }
            }
            if (names.Length > 0)
            {
                EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (GUILayout.Button(names[i], EditorStyles.miniButton)) Preview(switcher, i);
                    }
                }
            }
        }

        private static void Preview(ParelOutfitSwitcher switcher, int index)
        {
            for (int i = 0; i < switcher.outfits.Count; i++)
            {
                ParelOutfit outfit = switcher.outfits[i];
                if (outfit?.objects == null) continue;
                foreach (GameObject go in outfit.objects)
                {
                    if (go == null) continue;
                    Undo.RecordObject(go, "Preview Outfit");
                    go.SetActive(false);
                }
            }
            ParelOutfit worn = index >= 0 && index < switcher.outfits.Count ? switcher.outfits[index] : null;
            if (worn?.objects == null) return;
            foreach (GameObject go in worn.objects)
            {
                if (go != null) go.SetActive(true);
            }
        }
    }

    [CustomEditor(typeof(ParelClothing))]
    public sealed class ParelClothingEditor : Editor
    {
        private bool _showBones;

        public override void OnInspectorGUI()
        {
            var clothing = (ParelClothing)target;
            serializedObject.Update();
            ParelAvatarToolsGUI.HeaderHelp("Joins this clothing's armature to the avatar's when the avatar is built -- no moving bones by hand. Put the clothing inside the avatar and leave it in its original pose.");

            ParelAvatarDescriptor avatar = ParelAvatarToolsGUI.AvatarOf(clothing);
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Drag this clothing onto your avatar in the Hierarchy (make it a child of the avatar).", MessageType.Warning);
            }
            else
            {
                ParelClothingMatcher.Result match = ParelClothingMatcher.Match(avatar, clothing);
                if (!match.Ok)
                {
                    EditorGUILayout.HelpBox(match.Error, MessageType.Error);
                }
                else
                {
                    string naming = match.Prefix.Length + match.Suffix.Length > 0 ? $"  Bone names: \"{match.Prefix}…{match.Suffix}\"." : string.Empty;
                    EditorGUILayout.HelpBox($"Matches {match.Pairs.Count} bones of '{avatar.name}'. {match.Unmatched.Count} extra bone(s) stay with the clothing.{naming}", MessageType.Info);
                    _showBones = EditorGUILayout.Foldout(_showBones, "Matched Bones", true);
                    if (_showBones)
                    {
                        EditorGUI.indentLevel++;
                        foreach (KeyValuePair<Transform, Transform> pair in match.Pairs.Take(120))
                        {
                            EditorGUILayout.LabelField(pair.Key.name, "→ " + pair.Value.name, EditorStyles.miniLabel);
                        }
                        EditorGUI.indentLevel--;
                    }
                }
            }

            EditorGUILayout.PropertyField(serializedObject.FindProperty("mode"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("armatureRoot"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("bonePrefix"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("boneSuffix"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("excludedBones"), true);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("removeClothingAnimator"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(clothing.GetComponent<ParelObjectToggle>() != null))
                {
                    if (GUILayout.Button("Add On/Off Toggle"))
                    {
                        var toggle = Undo.AddComponent<ParelObjectToggle>(clothing.gameObject);
                        toggle.menuFolder = "Clothing";
                        toggle.defaultOn = clothing.gameObject.activeSelf;
                    }
                }
                if (GUILayout.Button("Sync Blend Shapes With Body"))
                {
                    SkinnedMeshRenderer mesh = clothing.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    SkinnedMeshRenderer body = ParelAvatarToolsMenu.FindBody(avatar, mesh);
                    if (mesh != null && body != null)
                    {
                        ParelBlendshapeSync sync = mesh.GetComponent<ParelBlendshapeSync>();
                        if (sync == null) sync = Undo.AddComponent<ParelBlendshapeSync>(mesh.gameObject);
                        Undo.RecordObject(sync, "Sync Blend Shapes");
                        sync.source = body;
                    }
                }
            }
        }
    }

    [CustomEditor(typeof(ParelBlendshapeSync))]
    public sealed class ParelBlendshapeSyncEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var sync = (ParelBlendshapeSync)target;
            ParelAvatarToolsGUI.HeaderHelp("This mesh's blend shapes follow the Source's -- their current values and every animation of them -- e.g. a shirt following the body's chest size.");
            DrawDefaultInspector();
            int count = ParelAvatarToolsProcessor.SyncPairs(sync).Count;
            if (sync.source != null) EditorGUILayout.LabelField($"{count} blend shape(s) will follow '{sync.source.name}'.", EditorStyles.miniLabel);
            if (sync.source == null && GUILayout.Button("Use The Body Mesh"))
            {
                SkinnedMeshRenderer body = ParelAvatarToolsMenu.FindBody(ParelAvatarToolsGUI.AvatarOf(sync), sync.Target);
                if (body != null)
                {
                    Undo.RecordObject(sync, "Pick Body");
                    sync.source = body;
                }
            }
        }
    }

    [CustomEditor(typeof(ParelBoneAttachment))]
    public sealed class ParelBoneAttachmentEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            ParelAvatarToolsGUI.HeaderHelp("Follows the chosen bone once the avatar is built -- put a hat on the Head, a sword in a Hand. Position it where you want it now.");
            DrawDefaultInspector();
        }
    }
}
