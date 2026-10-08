using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ParelVR.AvatarSDK;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Build
{
    /// <summary>
    /// Applies the Avatar Tools components to the build copy of an avatar: merges clothing
    /// armatures, attaches props to bones, syncs blend shapes, and turns every Object Toggle and
    /// Outfit Switcher into Expression Parameters, Expressions Menu buttons and FX layers. The
    /// avatar's own controllers, menus and parameters are copied first and never modified, and the
    /// tool components are removed before the copy is saved.
    /// </summary>
    public static class ParelAvatarToolsProcessor
    {
        /// <summary>True if anything under the avatar uses an Avatar Tools component.</summary>
        public static bool HasTools(GameObject root)
        {
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IParelEditorOnly) return true;
            }
            return false;
        }

        public static void Process(GameObject root, ParelAvatarDescriptor descriptor, string generatedFolder, List<string> notes)
        {
            if (root == null || descriptor == null) return;

            foreach (ParelClothing clothing in root.GetComponentsInChildren<ParelClothing>(true)) ApplyClothing(descriptor, clothing, notes);
            foreach (ParelBoneAttachment attachment in root.GetComponentsInChildren<ParelBoneAttachment>(true)) ApplyAttachment(descriptor, attachment, notes);

            ParelBlendshapeSync[] syncs = root.GetComponentsInChildren<ParelBlendshapeSync>(true);
            foreach (ParelBlendshapeSync sync in syncs) ApplyStaticSync(sync);

            ParelObjectToggle[] toggles = root.GetComponentsInChildren<ParelObjectToggle>(true);
            ParelOutfitSwitcher[] switchers = root.GetComponentsInChildren<ParelOutfitSwitcher>(true);
            if (toggles.Length > 0 || switchers.Length > 0 || syncs.Length > 0)
            {
                var generator = new Generator(descriptor, generatedFolder, notes);
                generator.Prepare(toggles.Length > 0 || switchers.Length > 0);
                foreach (ParelObjectToggle toggle in toggles) generator.AddToggle(toggle);
                foreach (ParelOutfitSwitcher switcher in switchers) generator.AddOutfitSwitcher(switcher);
                if (syncs.Length > 0) generator.MirrorBlendShapeAnimations(syncs);
                generator.Finish();
            }

            StripEditorOnly(root);
        }

        /// <summary>Removes every editor-only (Avatar Tools) component from the build copy.</summary>
        public static void StripEditorOnly(GameObject root)
        {
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IParelEditorOnly) UnityEngine.Object.DestroyImmediate(behaviour);
            }
        }

        // =========================================================================================
        // Clothing
        // =========================================================================================

        private static void ApplyClothing(ParelAvatarDescriptor descriptor, ParelClothing clothing, List<string> notes)
        {
            ParelClothingMatcher.Result match = ParelClothingMatcher.Match(descriptor, clothing);
            if (!match.Ok) throw new Exception($"Clothing '{clothing.name}': {match.Error}");

            if (clothing.removeClothingAnimator)
            {
                foreach (Animator animator in clothing.GetComponentsInChildren<Animator>(true))
                {
                    if (animator != descriptor.AvatarAnimator) UnityEngine.Object.DestroyImmediate(animator);
                }
            }

            float scale = Mathf.Max(0.0001f, descriptor.transform.lossyScale.y);
            SkinnedMeshRenderer[] meshes = clothing.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var merged = new List<Transform>();
            var remap = new Dictionary<Transform, Transform>();
            int parented = 0;

            foreach (KeyValuePair<Transform, Transform> pair in match.Pairs)
            {
                Transform bone = pair.Key;
                Transform target = pair.Value;
                bool samePose = Vector3.Distance(bone.position, target.position) <= 0.004f * scale &&
                                Quaternion.Angle(bone.rotation, target.rotation) <= 2f &&
                                Mathf.Abs(bone.lossyScale.x / Mathf.Max(0.0001f, target.lossyScale.x) - 1f) <= 0.02f;

                if (clothing.mode == ParelClothing.MergeMode.MergeBones && samePose)
                {
                    remap[bone] = target;
                    merged.Add(bone);
                    // Extra bones (skirt, hood...) move under the avatar bone, keeping where they are.
                    var extras = new List<Transform>();
                    for (int i = 0; i < bone.childCount; i++)
                    {
                        Transform child = bone.GetChild(i);
                        if (!match.Pairs.Any(p => p.Key == child)) extras.Add(child);
                    }
                    foreach (Transform extra in extras) extra.SetParent(target, true);
                }
                else
                {
                    bone.SetParent(target, true);
                    parented++;
                }
            }

            if (remap.Count > 0)
            {
                foreach (SkinnedMeshRenderer mesh in meshes)
                {
                    Transform[] bones = mesh.bones;
                    bool changed = false;
                    for (int i = 0; i < bones.Length; i++)
                    {
                        if (bones[i] != null && remap.TryGetValue(bones[i], out Transform to))
                        {
                            bones[i] = to;
                            changed = true;
                        }
                    }
                    if (changed) mesh.bones = bones;
                    if (mesh.rootBone != null && remap.TryGetValue(mesh.rootBone, out Transform root)) mesh.rootBone = root;
                }
                RemapReferences(descriptor.gameObject, remap);
            }

            // Children first, so a merged bone is always empty by the time it's removed.
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i] != null) UnityEngine.Object.DestroyImmediate(merged[i].gameObject);
            }
            if (match.ClothingArmature != null && match.ClothingArmature != clothing.transform && match.ClothingArmature.childCount == 0 &&
                match.ClothingArmature.GetComponents<Component>().Length == 1)
            {
                UnityEngine.Object.DestroyImmediate(match.ClothingArmature.gameObject);
            }

            notes.Add($"Clothing '{clothing.name}': {merged.Count} bones merged, {parented} attached, {match.Unmatched.Count} extra bones kept.");
        }

        /// <summary>Points every Transform reference in the avatar at the bone that replaced it.</summary>
        private static void RemapReferences(GameObject root, Dictionary<Transform, Transform> remap)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform || component is SkinnedMeshRenderer) continue;
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool changed = false;
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    UnityEngine.Object value = property.objectReferenceValue;
                    if (value is Transform t && remap.TryGetValue(t, out Transform to))
                    {
                        property.objectReferenceValue = to;
                        changed = true;
                    }
                    else if (value is GameObject go && remap.TryGetValue(go.transform, out Transform toGo))
                    {
                        property.objectReferenceValue = toGo.gameObject;
                        changed = true;
                    }
                }
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // =========================================================================================
        // Attach To Bone, Blend Shape Sync
        // =========================================================================================

        private static void ApplyAttachment(ParelAvatarDescriptor descriptor, ParelBoneAttachment attachment, List<string> notes)
        {
            Animator animator = descriptor.AvatarAnimator;
            Transform bone = animator != null && animator.isHuman ? animator.GetBoneTransform(attachment.bone) : null;
            if (bone == null) throw new Exception($"Attach To Bone '{attachment.name}': the avatar has no {attachment.bone} bone.");
            if (bone.IsChildOf(attachment.transform)) throw new Exception($"Attach To Bone '{attachment.name}': it can't be attached to its own child.");
            attachment.transform.SetParent(bone, attachment.keepWorldPose);
            if (!attachment.keepWorldPose)
            {
                attachment.transform.localPosition = Vector3.zero;
                attachment.transform.localRotation = Quaternion.identity;
            }
        }

        /// <summary>Source blend shape index -> target blend shape index for one Blend Shape Sync.</summary>
        public static List<(string source, string target)> SyncPairs(ParelBlendshapeSync sync)
        {
            var pairs = new List<(string, string)>();
            SkinnedMeshRenderer target = sync.Target;
            if (sync.source == null || target == null || sync.source.sharedMesh == null || target.sharedMesh == null) return pairs;
            Mesh sourceMesh = sync.source.sharedMesh;
            Mesh targetMesh = target.sharedMesh;
            var seen = new HashSet<string>();
            if (sync.links != null)
            {
                foreach (ParelBlendShapeLink link in sync.links)
                {
                    if (link == null || string.IsNullOrEmpty(link.source)) continue;
                    string to = string.IsNullOrEmpty(link.target) ? link.source : link.target;
                    if (sourceMesh.GetBlendShapeIndex(link.source) < 0 || targetMesh.GetBlendShapeIndex(to) < 0) continue;
                    if (seen.Add(to)) pairs.Add((link.source, to));
                }
            }
            if (sync.matchAllByName)
            {
                for (int i = 0; i < sourceMesh.blendShapeCount; i++)
                {
                    string name = sourceMesh.GetBlendShapeName(i);
                    if (targetMesh.GetBlendShapeIndex(name) >= 0 && seen.Add(name)) pairs.Add((name, name));
                }
            }
            return pairs;
        }

        private static void ApplyStaticSync(ParelBlendshapeSync sync)
        {
            SkinnedMeshRenderer target = sync.Target;
            if (sync.source == null || target == null) return;
            foreach ((string source, string to) in SyncPairs(sync))
            {
                int from = sync.source.sharedMesh.GetBlendShapeIndex(source);
                int into = target.sharedMesh.GetBlendShapeIndex(to);
                target.SetBlendShapeWeight(into, sync.source.GetBlendShapeWeight(from));
            }
        }

        // =========================================================================================
        // Toggles, outfits and FX generation
        // =========================================================================================

        private sealed class Generator
        {
            private readonly ParelAvatarDescriptor _descriptor;
            private readonly string _folder;
            private readonly List<string> _notes;
            private readonly Transform _animationRoot;
            private readonly Dictionary<ParelExpressionsMenu, ParelExpressionsMenu> _clonedMenus = new Dictionary<ParelExpressionsMenu, ParelExpressionsMenu>();
            private readonly HashSet<ParelExpressionsMenu> _generatedMenus = new HashSet<ParelExpressionsMenu>();
            private AnimatorController _fx;
            private ParelExpressionParameters _parameters;
            private ParelExpressionsMenu _rootMenu;
            private int _assetIndex;

            public Generator(ParelAvatarDescriptor descriptor, string folder, List<string> notes)
            {
                _descriptor = descriptor;
                _folder = folder;
                _notes = notes;
                _animationRoot = descriptor.AvatarAnimator != null ? descriptor.AvatarAnimator.transform : descriptor.transform;
            }

            public void Prepare(bool needsExpressions)
            {
                if (!AssetDatabase.IsValidFolder(_folder))
                {
                    string parent = Path.GetDirectoryName(_folder)?.Replace('\\', '/');
                    AssetDatabase.CreateFolder(parent, Path.GetFileName(_folder));
                }

                RuntimeAnimatorController existing = _descriptor.FxController;
                string fxPath = _folder + "/FX.controller";
                if (existing is AnimatorController controller)
                {
                    if (!AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(controller), fxPath)) throw new Exception("Couldn't copy the FX controller for Avatar Tools.");
                    _fx = AssetDatabase.LoadAssetAtPath<AnimatorController>(fxPath);
                }
                else if (existing != null)
                {
                    throw new Exception("Avatar Tools can't add layers to an Animator Override Controller in the FX slot. Use a normal Animator Controller there.");
                }
                else
                {
                    _fx = AnimatorController.CreateAnimatorControllerAtPath(fxPath);
                }
                _descriptor.SetLayer(ParelAvatarLayer.FX, _fx);

                if (!needsExpressions) return;

                _parameters = _descriptor.ExpressionParameters != null
                    ? UnityEngine.Object.Instantiate(_descriptor.ExpressionParameters)
                    : ScriptableObject.CreateInstance<ParelExpressionParameters>();
                _parameters.name = "ExpressionParameters";
                if (_parameters.parameters == null) _parameters.parameters = new List<ParelExpressionParameters.Parameter>();
                AssetDatabase.CreateAsset(_parameters, _folder + "/ExpressionParameters.asset");

                _rootMenu = _descriptor.ExpressionsMenu != null ? CloneMenu(_descriptor.ExpressionsMenu) : NewMenu("ExpressionsMenu");

                _descriptor.ExpressionParameters = _parameters;
                _descriptor.ExpressionsMenu = _rootMenu;
            }

            public void Finish()
            {
                if (_parameters != null)
                {
                    int cost = _parameters.CalcTotalCost();
                    if (cost > ParelExpressionParameters.MaxSyncedBits)
                    {
                        throw new Exception($"Your Expression Parameters plus Avatar Tools need {cost} synced bits; the limit is {ParelExpressionParameters.MaxSyncedBits}. Turn Synced off on some toggles or remove parameters.");
                    }
                    EditorUtility.SetDirty(_parameters);
                }
                foreach (ParelExpressionsMenu menu in _clonedMenus.Values) EditorUtility.SetDirty(menu);
                foreach (ParelExpressionsMenu menu in _generatedMenus) EditorUtility.SetDirty(menu);
                EditorUtility.SetDirty(_fx);
                AssetDatabase.SaveAssets();
            }

            // ---- Object Toggle ---------------------------------------------------------------

            public void AddToggle(ParelObjectToggle toggle)
            {
                string label = toggle.MenuLabel;
                string parameter = Parameter(toggle.parameterName, label, ParelExpressionParameters.ValueType.Bool, toggle.defaultOn ? 1f : 0f, toggle.saved, toggle.synced, out bool shared);

                AnimationClip on = NewClip(label + " On");
                AnimationClip off = NewClip(label + " Off");
                foreach (ParelToggledObject target in toggle.Targets())
                {
                    string path = PathOf(target.target.transform, toggle.name);
                    if (path == null) continue;
                    bool activeWhenOn = !target.invert;
                    SetFloat(on, path, typeof(GameObject), "m_IsActive", activeWhenOn ? 1f : 0f);
                    SetFloat(off, path, typeof(GameObject), "m_IsActive", activeWhenOn ? 0f : 1f);
                    target.target.SetActive(toggle.defaultOn ? activeWhenOn : !activeWhenOn);
                }
                if (toggle.blendShapes != null)
                {
                    foreach (ParelBlendShapeChange change in toggle.blendShapes) AddBlendShape(change, on, off, toggle.defaultOn, toggle.name);
                }
                if (toggle.materialSwaps != null)
                {
                    foreach (ParelMaterialSwap swap in toggle.materialSwaps) AddMaterialSwap(swap, on, off, toggle.defaultOn, toggle.name);
                }

                // Every toggle gets its own layer, so toggles sharing a parameter all react to it.
                AddBoolLayer("Toggle: " + label, parameter, on, off, toggle.defaultOn);

                // ...but only one button: linked toggles (or a parameter your own menu already has) don't add another.
                bool linked = shared && (_toolParameters.Contains(parameter) || MenuUses(_rootMenu, parameter, new HashSet<ParelExpressionsMenu>()));
                _toolParameters.Add(parameter);
                if (linked) return;

                ParelExpressionsMenu menu = MenuAt(toggle.menuFolder);
                AddControl(menu, new ParelExpressionsMenu.Control
                {
                    name = label,
                    icon = toggle.icon,
                    type = ParelExpressionsMenu.ControlType.Toggle,
                    parameter = new ParelExpressionsMenu.ControlParameter { name = parameter },
                    value = 1f,
                });
            }

            private readonly HashSet<string> _toolParameters = new HashSet<string>();

            private static bool MenuUses(ParelExpressionsMenu menu, string parameter, HashSet<ParelExpressionsMenu> visited)
            {
                if (menu == null || !visited.Add(menu) || menu.controls == null) return false;
                foreach (ParelExpressionsMenu.Control control in menu.controls)
                {
                    if (control == null) continue;
                    if (control.parameter != null && control.parameter.name == parameter) return true;
                    if (control.type == ParelExpressionsMenu.ControlType.SubMenu && MenuUses(control.subMenu, parameter, visited)) return true;
                }
                return false;
            }

            // ---- Outfit Switcher -------------------------------------------------------------

            public void AddOutfitSwitcher(ParelOutfitSwitcher switcher)
            {
                List<ParelOutfit> outfits = (switcher.outfits ?? new List<ParelOutfit>()).Where(o => o != null).ToList();
                if (outfits.Count == 0)
                {
                    _notes.Add($"Outfit Switcher '{switcher.name}' has no outfits, so it was skipped.");
                    return;
                }
                if (outfits.Count > 255) throw new Exception($"Outfit Switcher '{switcher.name}' has more than 255 outfits.");

                int defaultIndex = Mathf.Clamp(switcher.defaultOutfit, 0, outfits.Count - 1);
                string label = switcher.MenuLabel;
                string parameter = Parameter(switcher.parameterName, label, ParelExpressionParameters.ValueType.Int, defaultIndex, switcher.saved, switcher.synced, out _);

                var allObjects = new List<GameObject>();
                var allShapes = new List<ParelBlendShapeChange>();
                foreach (ParelOutfit outfit in outfits)
                {
                    foreach (GameObject go in outfit.objects ?? Array.Empty<GameObject>())
                    {
                        if (go != null && !allObjects.Contains(go)) allObjects.Add(go);
                    }
                    foreach (ParelBlendShapeChange change in outfit.blendShapes ?? Array.Empty<ParelBlendShapeChange>())
                    {
                        if (change != null && change.renderer != null && !string.IsNullOrEmpty(change.blendShape)) allShapes.Add(change);
                    }
                }

                var clips = new AnimationClip[outfits.Count];
                for (int i = 0; i < outfits.Count; i++)
                {
                    ParelOutfit outfit = outfits[i];
                    AnimationClip clip = NewClip(label + " - " + (string.IsNullOrWhiteSpace(outfit.name) ? "Outfit " + (i + 1) : outfit.name));
                    var wearing = new HashSet<GameObject>((outfit.objects ?? Array.Empty<GameObject>()).Where(go => go != null));
                    foreach (GameObject go in allObjects)
                    {
                        string path = PathOf(go.transform, switcher.name);
                        if (path == null) continue;
                        SetFloat(clip, path, typeof(GameObject), "m_IsActive", wearing.Contains(go) ? 1f : 0f);
                        if (i == defaultIndex) go.SetActive(wearing.Contains(go));
                    }
                    var own = new HashSet<ParelBlendShapeChange>(outfit.blendShapes ?? Array.Empty<ParelBlendShapeChange>());
                    foreach (ParelBlendShapeChange change in allShapes)
                    {
                        string path = PathOf(change.renderer.transform, switcher.name);
                        if (path == null) continue;
                        // A shape set by this outfit wins over another outfit's "off" value for the same shape.
                        bool mine = own.Contains(change);
                        bool setByMine = own.Any(c => c != null && c.renderer == change.renderer && c.blendShape == change.blendShape);
                        if (!mine && setByMine) continue;
                        float value = mine ? change.onValue : change.offValue;
                        SetFloat(clip, path, typeof(SkinnedMeshRenderer), "blendShape." + change.blendShape, value);
                        if (i == defaultIndex) SetWeight(change.renderer, change.blendShape, value);
                    }
                    clips[i] = clip;
                }

                AddIntLayer("Outfits: " + label, parameter, clips, defaultIndex);

                string folder = string.IsNullOrWhiteSpace(switcher.menuFolder) ? label : switcher.menuFolder.Trim().TrimEnd('/') + "/" + label;
                ParelExpressionsMenu menu = MenuAt(folder, switcher.icon);
                for (int i = 0; i < outfits.Count; i++)
                {
                    AddControl(menu, new ParelExpressionsMenu.Control
                    {
                        name = string.IsNullOrWhiteSpace(outfits[i].name) ? "Outfit " + (i + 1) : outfits[i].name,
                        icon = outfits[i].icon,
                        type = ParelExpressionsMenu.ControlType.Toggle,
                        parameter = new ParelExpressionsMenu.ControlParameter { name = parameter },
                        value = i,
                    });
                }
            }

            // ---- Blend Shape Sync ------------------------------------------------------------

            public void MirrorBlendShapeAnimations(ParelBlendshapeSync[] syncs)
            {
                var links = new List<(string sourcePath, string sourceShape, string targetPath, string targetShape)>();
                foreach (ParelBlendshapeSync sync in syncs)
                {
                    SkinnedMeshRenderer target = sync.Target;
                    if (sync.source == null || target == null) continue;
                    string sourcePath = PathOf(sync.source.transform, sync.name);
                    string targetPath = PathOf(target.transform, sync.name);
                    if (sourcePath == null || targetPath == null) continue;
                    foreach ((string source, string to) in SyncPairs(sync)) links.Add((sourcePath, source, targetPath, to));
                }
                if (links.Count == 0) return;

                var replaced = new Dictionary<AnimationClip, AnimationClip>();
                foreach (AnimatorControllerLayer layer in _fx.layers) MirrorInMachine(layer.stateMachine, links, replaced);
                if (replaced.Count > 0) _notes.Add($"Blend Shape Sync: {replaced.Count} animation(s) now move the synced blend shapes too.");
            }

            private void MirrorInMachine(AnimatorStateMachine machine, List<(string, string, string, string)> links, Dictionary<AnimationClip, AnimationClip> replaced)
            {
                if (machine == null) return;
                foreach (ChildAnimatorState child in machine.states)
                {
                    child.state.motion = MirrorMotion(child.state.motion, links, replaced);
                }
                foreach (ChildAnimatorStateMachine sub in machine.stateMachines) MirrorInMachine(sub.stateMachine, links, replaced);
            }

            private Motion MirrorMotion(Motion motion, List<(string sourcePath, string sourceShape, string targetPath, string targetShape)> links, Dictionary<AnimationClip, AnimationClip> replaced)
            {
                if (motion is BlendTree tree)
                {
                    ChildMotion[] children = tree.children;
                    for (int i = 0; i < children.Length; i++) children[i].motion = MirrorMotion(children[i].motion, links, replaced);
                    tree.children = children;
                    return tree;
                }
                if (!(motion is AnimationClip clip)) return motion;
                if (replaced.TryGetValue(clip, out AnimationClip done)) return done;

                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                var additions = new List<(EditorCurveBinding binding, AnimationCurve curve)>();
                foreach (EditorCurveBinding binding in bindings)
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.")) continue;
                    string shape = binding.propertyName.Substring("blendShape.".Length);
                    foreach ((string sourcePath, string sourceShape, string targetPath, string targetShape) in links)
                    {
                        if (binding.path != sourcePath || shape != sourceShape) continue;
                        additions.Add((EditorCurveBinding.FloatCurve(targetPath, typeof(SkinnedMeshRenderer), "blendShape." + targetShape), AnimationUtility.GetEditorCurve(clip, binding)));
                    }
                }
                if (additions.Count == 0)
                {
                    replaced[clip] = clip;
                    return clip;
                }

                AnimationClip copy = AssetDatabase.Contains(clip) && AssetDatabase.GetAssetPath(clip).StartsWith(_folder) ? clip : UnityEngine.Object.Instantiate(clip);
                if (copy != clip)
                {
                    copy.name = clip.name;
                    AssetDatabase.CreateAsset(copy, NextAssetPath(clip.name, ".anim"));
                }
                foreach ((EditorCurveBinding binding, AnimationCurve curve) in additions) AnimationUtility.SetEditorCurve(copy, binding, curve);
                replaced[clip] = copy;
                return copy;
            }

            // ---- Parameters ------------------------------------------------------------------

            private string Parameter(string explicitName, string label, ParelExpressionParameters.ValueType type, float defaultValue, bool saved, bool synced, out bool shared)
            {
                shared = false;
                bool isExplicit = !string.IsNullOrWhiteSpace(explicitName);
                string name = isExplicit ? explicitName.Trim() : CleanName(label);
                ParelExpressionParameters.Parameter existing = _parameters.FindParameter(name);
                if (existing != null)
                {
                    if (existing.valueType == type)
                    {
                        shared = true;
                        return name;
                    }
                    if (isExplicit) throw new Exception($"The parameter '{name}' already exists as {existing.valueType}; '{label}' needs it to be {type}.");
                    string baseName = name;
                    int n = 2;
                    while (_parameters.FindParameter(name) != null) name = baseName + " " + n++;
                }
                if (_parameters.parameters.Count >= ParelExpressionParameters.MaxParameters) throw new Exception("Too many Expression Parameters (the limit is 256).");
                _parameters.parameters.Add(new ParelExpressionParameters.Parameter
                {
                    name = name,
                    valueType = type,
                    defaultValue = defaultValue,
                    saved = saved,
                    networkSynced = synced,
                });
                return name;
            }

            private static string CleanName(string label)
            {
                string name = (label ?? "Toggle").Trim().Replace('/', '_');
                return name.Length == 0 ? "Toggle" : name;
            }

            private void EnsureAnimatorParameter(string name, AnimatorControllerParameterType type, float defaultValue)
            {
                foreach (AnimatorControllerParameter p in _fx.parameters)
                {
                    if (p.name != name) continue;
                    if (p.type != type) throw new Exception($"The FX controller already has a parameter '{name}' of type {p.type}; Avatar Tools need it to be {type}.");
                    return;
                }
                _fx.AddParameter(new AnimatorControllerParameter
                {
                    name = name,
                    type = type,
                    defaultBool = defaultValue >= 0.5f,
                    defaultInt = Mathf.RoundToInt(defaultValue),
                    defaultFloat = defaultValue,
                });
            }

            // ---- FX layers -------------------------------------------------------------------

            private AnimatorStateMachine NewLayer(string name)
            {
                string layerName = _fx.MakeUniqueLayerName(name);
                var machine = new AnimatorStateMachine { name = layerName, hideFlags = HideFlags.HideInHierarchy };
                AssetDatabase.AddObjectToAsset(machine, _fx);
                _fx.AddLayer(new AnimatorControllerLayer
                {
                    name = layerName,
                    defaultWeight = 1f,
                    stateMachine = machine,
                });
                return machine;
            }

            private void AddBoolLayer(string name, string parameter, AnimationClip on, AnimationClip off, bool defaultOn)
            {
                EnsureAnimatorParameter(parameter, AnimatorControllerParameterType.Bool, defaultOn ? 1f : 0f);
                AnimatorStateMachine machine = NewLayer(name);
                AnimatorState offState = machine.AddState("Off", new Vector3(260, 60, 0));
                offState.motion = off;
                offState.writeDefaultValues = false;
                AnimatorState onState = machine.AddState("On", new Vector3(260, 160, 0));
                onState.motion = on;
                onState.writeDefaultValues = false;
                machine.defaultState = defaultOn ? onState : offState;

                AnimatorStateTransition toOn = offState.AddTransition(onState);
                Instant(toOn);
                toOn.AddCondition(AnimatorConditionMode.If, 0f, parameter);
                AnimatorStateTransition toOff = onState.AddTransition(offState);
                Instant(toOff);
                toOff.AddCondition(AnimatorConditionMode.IfNot, 0f, parameter);
            }

            private void AddIntLayer(string name, string parameter, AnimationClip[] clips, int defaultIndex)
            {
                EnsureAnimatorParameter(parameter, AnimatorControllerParameterType.Int, defaultIndex);
                AnimatorStateMachine machine = NewLayer(name);
                for (int i = 0; i < clips.Length; i++)
                {
                    AnimatorState state = machine.AddState(clips[i].name, new Vector3(320, 60 + i * 70, 0));
                    state.motion = clips[i];
                    state.writeDefaultValues = false;
                    if (i == defaultIndex) machine.defaultState = state;

                    AnimatorStateTransition transition = machine.AddAnyStateTransition(state);
                    Instant(transition);
                    transition.canTransitionToSelf = false;
                    transition.AddCondition(AnimatorConditionMode.Equals, i, parameter);
                }
            }

            private static void Instant(AnimatorStateTransition transition)
            {
                transition.hasExitTime = false;
                transition.hasFixedDuration = true;
                transition.duration = 0f;
                transition.exitTime = 0f;
            }

            // ---- Clips -------------------------------------------------------------------------

            private AnimationClip NewClip(string name)
            {
                var clip = new AnimationClip { name = name };
                AssetDatabase.CreateAsset(clip, NextAssetPath(name, ".anim"));
                return clip;
            }

            private static void SetFloat(AnimationClip clip, string path, Type type, string property, float value)
            {
                var curve = new AnimationCurve(new Keyframe(0f, value), new Keyframe(1f / 60f, value));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), curve);
            }

            private void AddBlendShape(ParelBlendShapeChange change, AnimationClip on, AnimationClip off, bool defaultOn, string owner)
            {
                if (change == null || change.renderer == null || string.IsNullOrEmpty(change.blendShape)) return;
                if (change.renderer.sharedMesh == null || change.renderer.sharedMesh.GetBlendShapeIndex(change.blendShape) < 0)
                {
                    _notes.Add($"'{owner}': '{change.renderer.name}' has no blend shape '{change.blendShape}', so it was skipped.");
                    return;
                }
                string path = PathOf(change.renderer.transform, owner);
                if (path == null) return;
                SetFloat(on, path, typeof(SkinnedMeshRenderer), "blendShape." + change.blendShape, change.onValue);
                SetFloat(off, path, typeof(SkinnedMeshRenderer), "blendShape." + change.blendShape, change.offValue);
                SetWeight(change.renderer, change.blendShape, defaultOn ? change.onValue : change.offValue);
            }

            private static void SetWeight(SkinnedMeshRenderer renderer, string shape, float value)
            {
                if (renderer == null || renderer.sharedMesh == null) return;
                int index = renderer.sharedMesh.GetBlendShapeIndex(shape);
                if (index >= 0) renderer.SetBlendShapeWeight(index, value);
            }

            private void AddMaterialSwap(ParelMaterialSwap swap, AnimationClip on, AnimationClip off, bool defaultOn, string owner)
            {
                if (swap == null || swap.renderer == null || swap.onMaterial == null) return;
                Material[] materials = swap.renderer.sharedMaterials;
                if (swap.slot < 0 || swap.slot >= materials.Length)
                {
                    _notes.Add($"'{owner}': '{swap.renderer.name}' has no material slot {swap.slot}, so that swap was skipped.");
                    return;
                }
                string path = PathOf(swap.renderer.transform, owner);
                if (path == null) return;
                Material original = materials[swap.slot];
                var binding = EditorCurveBinding.PPtrCurve(path, swap.renderer.GetType(), "m_Materials.Array.data[" + swap.slot + "]");
                AnimationUtility.SetObjectReferenceCurve(on, binding, new[]
                {
                    new ObjectReferenceKeyframe { time = 0f, value = swap.onMaterial },
                    new ObjectReferenceKeyframe { time = 1f / 60f, value = swap.onMaterial },
                });
                AnimationUtility.SetObjectReferenceCurve(off, binding, new[]
                {
                    new ObjectReferenceKeyframe { time = 0f, value = original },
                    new ObjectReferenceKeyframe { time = 1f / 60f, value = original },
                });
                if (defaultOn)
                {
                    materials[swap.slot] = swap.onMaterial;
                    swap.renderer.sharedMaterials = materials;
                }
            }

            private string PathOf(Transform target, string owner)
            {
                if (target == null) return null;
                if (target != _animationRoot && !target.IsChildOf(_animationRoot))
                {
                    _notes.Add($"'{owner}': '{target.name}' isn't part of the avatar, so it was skipped.");
                    return null;
                }
                return AnimationUtility.CalculateTransformPath(target, _animationRoot);
            }

            private string NextAssetPath(string name, string extension)
            {
                string safe = name ?? "asset";
                foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                if (safe.Length > 60) safe = safe.Substring(0, 60);
                return AssetDatabase.GenerateUniqueAssetPath($"{_folder}/{++_assetIndex:000} {safe}{extension}");
            }

            // ---- Menus -----------------------------------------------------------------------

            private ParelExpressionsMenu CloneMenu(ParelExpressionsMenu source)
            {
                if (_clonedMenus.TryGetValue(source, out ParelExpressionsMenu done)) return done;
                ParelExpressionsMenu copy = UnityEngine.Object.Instantiate(source);
                copy.name = source.name;
                _clonedMenus[source] = copy;
                AssetDatabase.CreateAsset(copy, NextAssetPath(source.name, ".asset"));
                if (copy.controls == null) copy.controls = new List<ParelExpressionsMenu.Control>();
                foreach (ParelExpressionsMenu.Control control in copy.controls)
                {
                    if (control != null && control.type == ParelExpressionsMenu.ControlType.SubMenu && control.subMenu != null)
                    {
                        control.subMenu = CloneMenu(control.subMenu);
                    }
                }
                return copy;
            }

            private ParelExpressionsMenu NewMenu(string name)
            {
                var menu = ScriptableObject.CreateInstance<ParelExpressionsMenu>();
                menu.name = name;
                menu.controls = new List<ParelExpressionsMenu.Control>();
                AssetDatabase.CreateAsset(menu, NextAssetPath(name, ".asset"));
                _generatedMenus.Add(menu);
                return menu;
            }

            /// <summary>The menu page at a "Folder/Sub" path, creating sub menus as needed.</summary>
            private ParelExpressionsMenu MenuAt(string folder, Texture2D lastIcon = null)
            {
                ParelExpressionsMenu menu = _rootMenu;
                if (string.IsNullOrWhiteSpace(folder)) return menu;
                string[] parts = folder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i].Trim();
                    if (part.Length == 0) continue;
                    ParelExpressionsMenu next = FindSubMenu(menu, part);
                    if (next == null)
                    {
                        next = NewMenu(part);
                        AddControl(menu, new ParelExpressionsMenu.Control
                        {
                            name = part,
                            icon = i == parts.Length - 1 ? lastIcon : null,
                            type = ParelExpressionsMenu.ControlType.SubMenu,
                            subMenu = next,
                            parameter = new ParelExpressionsMenu.ControlParameter(),
                        });
                    }
                    menu = next;
                }
                return menu;
            }

            private static ParelExpressionsMenu FindSubMenu(ParelExpressionsMenu menu, string name)
            {
                for (ParelExpressionsMenu page = menu; page != null; page = MorePage(page))
                {
                    foreach (ParelExpressionsMenu.Control control in page.controls)
                    {
                        if (control != null && control.type == ParelExpressionsMenu.ControlType.SubMenu && control.subMenu != null &&
                            string.Equals(control.name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            return control.subMenu;
                        }
                    }
                }
                return null;
            }

            private const string MoreName = "More...";

            private static ParelExpressionsMenu MorePage(ParelExpressionsMenu menu)
            {
                if (menu.controls.Count != ParelExpressionsMenu.MaxControls) return null;
                ParelExpressionsMenu.Control last = menu.controls[menu.controls.Count - 1];
                return last != null && last.type == ParelExpressionsMenu.ControlType.SubMenu && last.name == MoreName ? last.subMenu : null;
            }

            /// <summary>Adds a control, spilling onto a "More..." page when a page Avatar Tools made is full.</summary>
            private void AddControl(ParelExpressionsMenu menu, ParelExpressionsMenu.Control control)
            {
                while (true)
                {
                    if (menu.controls.Count < ParelExpressionsMenu.MaxControls)
                    {
                        menu.controls.Add(control);
                        return;
                    }
                    ParelExpressionsMenu more = MorePage(menu);
                    if (more != null)
                    {
                        menu = more;
                        continue;
                    }
                    if (!_generatedMenus.Contains(menu))
                    {
                        throw new Exception($"The Expressions Menu page '{menu.name}' is full ({ParelExpressionsMenu.MaxControls} controls), so '{control.name}' can't be added. Give your toggles a Menu Folder, or free a slot.");
                    }
                    ParelExpressionsMenu.Control moved = menu.controls[ParelExpressionsMenu.MaxControls - 1];
                    more = NewMenu(menu.name + " " + MoreName);
                    menu.controls[ParelExpressionsMenu.MaxControls - 1] = new ParelExpressionsMenu.Control
                    {
                        name = MoreName,
                        type = ParelExpressionsMenu.ControlType.SubMenu,
                        subMenu = more,
                        parameter = new ParelExpressionsMenu.ControlParameter(),
                    };
                    more.controls.Add(moved);
                    menu = more;
                }
            }
        }
    }
}
