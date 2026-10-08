using System;
using System.Collections.Generic;
using System.IO;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Avatars.Build;
using ParelVR.SDK.Avatars.UI;
using ParelVR.SDK.Core.ControlPanel;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Inspectors
{
    /// <summary>
    /// Inspector for the Parel Avatar Descriptor, laid out the way VRChat creators already know: View,
    /// LipSync, Eye Look, Playable Layers, Lower Body, Expressions and Colliders, with Edit / Preview
    /// buttons that pose the avatar in the Scene view. ParelVR's own extras (scaling, overrides and
    /// the Avatar Tools summary) sit in one section at the bottom.
    /// </summary>
    [CustomEditor(typeof(ParelAvatarDescriptor))]
    public sealed class ParelAvatarDescriptorEditor : Editor
    {
        private const float PreviewButtonWidth = 58f;
        private static readonly Color ActiveButtonColor = new Color(0.55f, 0.75f, 1f);

        private static readonly (string label, int value)[] LipSyncModes =
        {
            ("Default", (int)ParelAvatarDescriptor.LipSyncStyle.Default),
            ("Jaw Flap Bone", (int)ParelAvatarDescriptor.LipSyncStyle.JawFlapBone),
            ("Jaw Flap Blend Shape", (int)ParelAvatarDescriptor.LipSyncStyle.JawFlapBlendShape),
            ("Viseme Blend Shape", (int)ParelAvatarDescriptor.LipSyncStyle.VisemeBlendShape),
            ("Viseme Parameter Only", (int)ParelAvatarDescriptor.LipSyncStyle.VisemeParameterOnly),
        };

        private static readonly Dictionary<ParelAvatarLayer, string> DefaultLayerNames = new Dictionary<ParelAvatarLayer, string>
        {
            { ParelAvatarLayer.Base, "Default Locomotion" },
            { ParelAvatarLayer.Additive, "Default Idle" },
            { ParelAvatarLayer.Gesture, "Default Gesture" },
            { ParelAvatarLayer.Action, "Default Action" },
            { ParelAvatarLayer.FX, "Default FX" },
            { ParelAvatarLayer.Sitting, "Default Sitting" },
            { ParelAvatarLayer.TPose, "Default TPose" },
            { ParelAvatarLayer.IKPose, "Default IKPose" },
        };

        private static readonly (string label, string left, string right, ParelAvatarColliderSlot slotL, ParelAvatarColliderSlot slotR)[] Colliders =
        {
            ("Head", "colliderHead", null, ParelAvatarColliderSlot.Head, ParelAvatarColliderSlot.Head),
            ("Torso", "colliderTorso", null, ParelAvatarColliderSlot.Torso, ParelAvatarColliderSlot.Torso),
            ("Hand", "colliderHandL", "colliderHandR", ParelAvatarColliderSlot.HandL, ParelAvatarColliderSlot.HandR),
            ("Foot", "colliderFootL", "colliderFootR", ParelAvatarColliderSlot.FootL, ParelAvatarColliderSlot.FootR),
            ("Finger Index", "colliderFingerIndexL", "colliderFingerIndexR", ParelAvatarColliderSlot.FingerIndexL, ParelAvatarColliderSlot.FingerIndexR),
            ("Finger Middle", "colliderFingerMiddleL", "colliderFingerMiddleR", ParelAvatarColliderSlot.FingerMiddleL, ParelAvatarColliderSlot.FingerMiddleR),
            ("Finger Ring", "colliderFingerRingL", "colliderFingerRingR", ParelAvatarColliderSlot.FingerRingL, ParelAvatarColliderSlot.FingerRingR),
            ("Finger Little", "colliderFingerLittleL", "colliderFingerLittleR", ParelAvatarColliderSlot.FingerLittleL, ParelAvatarColliderSlot.FingerLittleR),
        };

        private ParelAvatarDescriptor _descriptor;
        private string _activeKey;
        private readonly List<Action> _restore = new List<Action>();
        private SerializedProperty _activeRotation;
        private Transform _activeLeft;
        private Transform _activeRight;
        private bool _activeLinked;
        private ParelAvatarColliderSlot _activeCollider;
        private GUIStyle _boxTitle;

        private SerializedProperty P(string name) => serializedObject.FindProperty(name);

        // =========================================================================================
        // Lifecycle
        // =========================================================================================

        private void OnEnable()
        {
            _descriptor = (ParelAvatarDescriptor)target;
            if (_descriptor == null) return;

            if (!_descriptor.LayoutUpgraded)
            {
                Undo.RecordObject(_descriptor, "Upgrade Avatar Descriptor");
                _descriptor.UpgradeLayout();
                EditorUtility.SetDirty(_descriptor);
            }

            bool inScene = _descriptor.gameObject.scene.IsValid();
            if (inScene && _descriptor.GetComponent<ParelPipelineManager>() == null)
            {
                ParelBlueprint.EnsurePipelineManager(_descriptor);
            }

            if (inScene && _descriptor.AvatarAnimator == null)
            {
                Undo.RecordObject(_descriptor, "Auto Detect Avatar");
                _descriptor.AutoDetect();
                EditorUtility.SetDirty(_descriptor);
            }
        }

        private void OnDisable()
        {
            ClearActive();
        }

        public override void OnInspectorGUI()
        {
            _boxTitle ??= new GUIStyle(EditorStyles.boldLabel);
            serializedObject.Update();

            if (ParelControlPanel.IsOpen && GUILayout.Button("Select this avatar in the SDK control panel"))
            {
                AvatarsTab.SelectAvatar(_descriptor);
            }

            DrawView();
            DrawLipSync();
            DrawEyeLook();
            DrawPlayableLayers();
            DrawLowerBody();
            DrawExpressions();
            DrawColliders();
            DrawParelVR();
            DrawFooter();

            serializedObject.ApplyModifiedProperties();
        }

        // =========================================================================================
        // View
        // =========================================================================================

        private void DrawView()
        {
            if (!Foldout("View", true)) return;
            SerializedProperty view = P("viewPoint");
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                Vector3 value = EditorGUILayout.Vector3Field("View Position", view.vector3Value);
                if (EditorGUI.EndChangeCheck())
                {
                    view.vector3Value = value;
                    P("viewPointInitialized").boolValue = true;
                }
                if (ActiveButton("view", "Edit")) SetActive("view");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Auto Detect", EditorStyles.miniButton, GUILayout.Width(90)))
                {
                    serializedObject.ApplyModifiedProperties();
                    Undo.RecordObject(_descriptor, "Auto Detect View Position");
                    _descriptor.AutoDetectViewPoint();
                    EditorUtility.SetDirty(_descriptor);
                    serializedObject.Update();
                }
            }
            Separator();
        }

        // =========================================================================================
        // LipSync
        // =========================================================================================

        private void DrawLipSync()
        {
            if (!Foldout("LipSync", true)) return;

            SerializedProperty mode = P("lipSync");
            var labels = new List<string>();
            var values = new List<int>();
            foreach ((string label, int value) in LipSyncModes)
            {
                labels.Add(label);
                values.Add(value);
            }
            if (mode.intValue == (int)ParelAvatarDescriptor.LipSyncStyle.None)
            {
                labels.Add("None");
                values.Add((int)ParelAvatarDescriptor.LipSyncStyle.None);
            }
            int index = Mathf.Max(0, values.IndexOf(mode.intValue));
            int picked = EditorGUILayout.Popup("Mode", index, labels.ToArray());
            if (picked != index) mode.intValue = values[picked];

            switch ((ParelAvatarDescriptor.LipSyncStyle)mode.intValue)
            {
                case ParelAvatarDescriptor.LipSyncStyle.Default:
                    if (GUILayout.Button("Auto Detect!"))
                    {
                        serializedObject.ApplyModifiedProperties();
                        Undo.RecordObject(_descriptor, "Auto Detect Lip Sync");
                        _descriptor.AutoDetectLipSync(true);
                        EditorUtility.SetDirty(_descriptor);
                        serializedObject.Update();
                    }
                    break;

                case ParelAvatarDescriptor.LipSyncStyle.JawFlapBlendShape:
                {
                    SerializedProperty face = P("visemeSkinnedMesh");
                    EditorGUILayout.PropertyField(face, new GUIContent("Face Mesh"));
                    if (face.objectReferenceValue is SkinnedMeshRenderer mesh && mesh.sharedMesh != null)
                    {
                        SerializedProperty shape = P("jawFlapBlendShape");
                        shape.stringValue = BlendShapePopup("Jaw Flap Blend Shape", shape.stringValue, mesh.sharedMesh);
                    }
                    break;
                }

                case ParelAvatarDescriptor.LipSyncStyle.JawFlapBone:
                    DrawJawBone();
                    break;

                case ParelAvatarDescriptor.LipSyncStyle.VisemeBlendShape:
                {
                    SerializedProperty face = P("visemeSkinnedMesh");
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(face, new GUIContent("Face Mesh"));
                    bool meshChanged = EditorGUI.EndChangeCheck();
                    if (face.objectReferenceValue is SkinnedMeshRenderer mesh && mesh.sharedMesh != null)
                    {
                        SerializedProperty shapes = P("visemeBlendShapes");
                        if (shapes.arraySize != ParelVisemeAutoMapper.VisemeCount) shapes.arraySize = ParelVisemeAutoMapper.VisemeCount;
                        if (meshChanged)
                        {
                            string[] detected = ParelVisemeAutoMapper.DetectVisemes(mesh.sharedMesh);
                            for (int i = 0; i < ParelVisemeAutoMapper.VisemeCount; i++)
                            {
                                SerializedProperty shape = shapes.GetArrayElementAtIndex(i);
                                if (string.IsNullOrEmpty(shape.stringValue) && !string.IsNullOrEmpty(detected[i])) shape.stringValue = detected[i];
                            }
                        }
                        for (int i = 0; i < ParelVisemeAutoMapper.VisemeCount; i++)
                        {
                            SerializedProperty shape = shapes.GetArrayElementAtIndex(i);
                            shape.stringValue = BlendShapePopup("Viseme: " + ParelVisemeAutoMapper.VisemeNames[i], shape.stringValue, mesh.sharedMesh);
                        }
                    }
                    break;
                }

                case ParelAvatarDescriptor.LipSyncStyle.VisemeParameterOnly:
                    break;

                case ParelAvatarDescriptor.LipSyncStyle.None:
                    EditorGUILayout.HelpBox("This avatar's mouth doesn't move when it talks.", MessageType.None);
                    break;
            }
            Separator();
        }

        private void DrawJawBone()
        {
            SerializedProperty jaw = P("jawBone");
            SerializedProperty closed = P("jawClosedRotation");
            SerializedProperty open = P("jawOpenRotation");

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(jaw, new GUIContent("Jaw Bone"));
            if (EditorGUI.EndChangeCheck() && jaw.objectReferenceValue is Transform bone)
            {
                Vector3 rest = bone.localRotation.eulerAngles;
                closed.vector3Value = rest;
                open.vector3Value = rest;
            }

            EditorGUILayout.LabelField("Rotation States");
            EditorGUI.indentLevel++;
            using (new EditorGUI.DisabledScope(jaw.objectReferenceValue == null))
            {
                DrawJawState("Closed", closed, "jaw.closed");
                DrawJawState("Open", open, "jaw.open");
            }
            EditorGUI.indentLevel--;
        }

        private void DrawJawState(string label, SerializedProperty euler, string key)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                Vector3 value = EditorGUILayout.Vector3Field(label, euler.vector3Value);
                bool changed = EditorGUI.EndChangeCheck();
                if (changed) euler.vector3Value = value;
                bool activate = ActiveButton(key, "Preview");
                if (activate || changed)
                {
                    SetActive(key);
                    PreviewRotation(P("jawBone").objectReferenceValue as Transform, Quaternion.Euler(euler.vector3Value));
                }
            }
        }

        // =========================================================================================
        // Eye Look
        // =========================================================================================

        private void DrawEyeLook()
        {
            if (!Foldout("Eye Look", true)) return;

            SerializedProperty enabled = P("enableEyeLook");
            SerializedProperty settings = P("customEyeLookSettings");
            if (!enabled.boolValue)
            {
                if (GUILayout.Button("Enable")) EnableEyeLook();
                EditorGUILayout.HelpBox("Off: ParelVR moves the rig's eye bones and blinks with whatever it can find on its own.", MessageType.None);
                Separator();
                return;
            }
            if (GUILayout.Button("Disable"))
            {
                ClearActive();
                enabled.boolValue = false;
                Separator();
                return;
            }

            // General
            BeginBox("General");
            if (SubFoldout("EyeMovements", "Eye Movements"))
            {
                SerializedProperty movement = settings.FindPropertyRelative("eyeMovement");
                LabeledSlider("Calm", "Excited", movement.FindPropertyRelative("excitement"));
                LabeledSlider("Shy", "Confident", movement.FindPropertyRelative("confidence"));
            }
            EndBox();

            // Eyes
            BeginBox("Eyes");
            if (SubFoldout("EyeTransforms", "Transforms"))
            {
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("leftEye"), new GUIContent("Left Eye Bone"));
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("rightEye"), new GUIContent("Right Eye Bone"));
            }
            Transform leftEye = settings.FindPropertyRelative("leftEye").objectReferenceValue as Transform;
            Transform rightEye = settings.FindPropertyRelative("rightEye").objectReferenceValue as Transform;
            if (SubFoldout("EyeRotations", "Rotation States"))
            {
                using (new EditorGUI.DisabledScope(leftEye == null && rightEye == null))
                {
                    DrawEyeState("Looking Straight", settings.FindPropertyRelative("eyesLookingStraight"), leftEye, rightEye, "eye.straight");
                    DrawEyeState("Looking Up", settings.FindPropertyRelative("eyesLookingUp"), leftEye, rightEye, "eye.up");
                    DrawEyeState("Looking Down", settings.FindPropertyRelative("eyesLookingDown"), leftEye, rightEye, "eye.down");
                    DrawEyeState("Looking Left", settings.FindPropertyRelative("eyesLookingLeft"), leftEye, rightEye, "eye.left");
                    DrawEyeState("Looking Right", settings.FindPropertyRelative("eyesLookingRight"), leftEye, rightEye, "eye.right");
                }
                if (GUILayout.Button("Reset Rotation States From Current Pose", EditorStyles.miniButton))
                {
                    serializedObject.ApplyModifiedProperties();
                    ClearActive();
                    Undo.RecordObject(_descriptor, "Reset Eye Rotation States");
                    _descriptor.ResetEyeRotationStates();
                    EditorUtility.SetDirty(_descriptor);
                    serializedObject.Update();
                }
            }
            EndBox();

            // Eyelids
            BeginBox("Eyelids");
            SerializedProperty type = settings.FindPropertyRelative("eyelidType");
            EditorGUILayout.PropertyField(type, new GUIContent("Eyelid Type"));
            switch ((ParelAvatarDescriptor.EyelidType)type.intValue)
            {
                case ParelAvatarDescriptor.EyelidType.Bones:
                    DrawEyelidBones(settings);
                    break;
                case ParelAvatarDescriptor.EyelidType.Blendshapes:
                    DrawEyelidBlendshapes(settings);
                    break;
            }
            EndBox();
            Separator();
        }

        private void EnableEyeLook()
        {
            serializedObject.ApplyModifiedProperties();
            Undo.RecordObject(_descriptor, "Enable Eye Look");
            _descriptor.EnableEyeLook = true;
            ParelAvatarDescriptor.CustomEyeLookSettings settings = _descriptor.EyeLookSettings;
            Animator animator = _descriptor.AvatarAnimator;
            if (animator != null && animator.isHuman)
            {
                if (settings.leftEye == null) settings.leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
                if (settings.rightEye == null) settings.rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
            }
            _descriptor.ResetEyeRotationStates();
            if (settings.eyelidType == ParelAvatarDescriptor.EyelidType.None) _descriptor.AutoDetectBlink(false);
            EditorUtility.SetDirty(_descriptor);
            serializedObject.Update();
        }

        private void DrawEyeState(string label, SerializedProperty state, Transform leftEye, Transform rightEye, string key)
        {
            SerializedProperty linked = state.FindPropertyRelative("linked");
            SerializedProperty left = state.FindPropertyRelative("left");
            SerializedProperty right = state.FindPropertyRelative("right");

            bool changed = false;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth - 4));
                bool link = GUILayout.Toggle(linked.boolValue, new GUIContent(linked.boolValue ? "Linked" : "L / R", "Use the same rotation for both eyes."), EditorStyles.miniButton, GUILayout.Width(52));
                if (link != linked.boolValue)
                {
                    linked.boolValue = link;
                    if (link) right.quaternionValue = left.quaternionValue;
                    changed = true;
                }
                GUILayout.FlexibleSpace();
                if (ActiveButton(key, "Preview")) changed = true;
            }

            EditorGUI.indentLevel++;
            changed |= QuaternionField(linked.boolValue ? "Both Eyes" : "Left Eye", left);
            if (linked.boolValue)
            {
                right.quaternionValue = left.quaternionValue;
            }
            else
            {
                changed |= QuaternionField("Right Eye", right);
            }
            EditorGUI.indentLevel--;

            if (changed)
            {
                SetActive(key);
                _activeRotation = state;
                _activeLeft = leftEye;
                _activeRight = rightEye;
                _activeLinked = linked.boolValue;
                PreviewRotation(leftEye, left.quaternionValue);
                PreviewRotation(rightEye, right.quaternionValue);
            }
        }

        private void DrawEyelidBones(SerializedProperty settings)
        {
            if (SubFoldout("EyelidTransforms", "Transforms"))
            {
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("upperLeftEyelid"), new GUIContent("Upper Left Eyelid"));
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("upperRightEyelid"), new GUIContent("Upper Right Eyelid"));
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("lowerLeftEyelid"), new GUIContent("Lower Left Eyelid"));
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("lowerRightEyelid"), new GUIContent("Lower Right Eyelid"));
            }
            if (!SubFoldout("EyelidRotations", "Rotation States")) return;

            Transform ul = settings.FindPropertyRelative("upperLeftEyelid").objectReferenceValue as Transform;
            Transform ur = settings.FindPropertyRelative("upperRightEyelid").objectReferenceValue as Transform;
            Transform ll = settings.FindPropertyRelative("lowerLeftEyelid").objectReferenceValue as Transform;
            Transform lr = settings.FindPropertyRelative("lowerRightEyelid").objectReferenceValue as Transform;
            DrawEyelidState("Default", settings.FindPropertyRelative("eyelidsDefault"), ul, ur, ll, lr, "lid.default");
            DrawEyelidState("Closed", settings.FindPropertyRelative("eyelidsClosed"), ul, ur, ll, lr, "lid.closed");
            DrawEyelidState("Looking Up", settings.FindPropertyRelative("eyelidsLookingUp"), ul, ur, ll, lr, "lid.up");
            DrawEyelidState("Looking Down", settings.FindPropertyRelative("eyelidsLookingDown"), ul, ur, ll, lr, "lid.down");
        }

        private void DrawEyelidState(string label, SerializedProperty state, Transform ul, Transform ur, Transform ll, Transform lr, string key)
        {
            bool activate;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                activate = ActiveButton(key, "Preview");
            }

            SerializedProperty upper = state.FindPropertyRelative("upper");
            SerializedProperty lower = state.FindPropertyRelative("lower");
            EditorGUI.indentLevel++;
            bool changed = DrawLidPair("Upper Eyelids", upper);
            changed |= DrawLidPair("Lower Eyelids", lower);
            EditorGUI.indentLevel--;

            if (activate || changed)
            {
                SetActive(key);
                PreviewRotation(ul, upper.FindPropertyRelative("left").quaternionValue);
                PreviewRotation(ur, upper.FindPropertyRelative("right").quaternionValue);
                PreviewRotation(ll, lower.FindPropertyRelative("left").quaternionValue);
                PreviewRotation(lr, lower.FindPropertyRelative("right").quaternionValue);
            }
        }

        private bool DrawLidPair(string label, SerializedProperty rotations)
        {
            SerializedProperty linked = rotations.FindPropertyRelative("linked");
            SerializedProperty left = rotations.FindPropertyRelative("left");
            SerializedProperty right = rotations.FindPropertyRelative("right");
            bool changed;
            using (new EditorGUILayout.HorizontalScope())
            {
                changed = QuaternionField(linked.boolValue ? label : label + " (L)", left);
                bool link = GUILayout.Toggle(linked.boolValue, new GUIContent("🔗", "Link left and right"), EditorStyles.miniButton, GUILayout.Width(26));
                if (link != linked.boolValue)
                {
                    linked.boolValue = link;
                    changed = true;
                }
            }
            if (linked.boolValue) right.quaternionValue = left.quaternionValue;
            else changed |= QuaternionField(label + " (R)", right);
            return changed;
        }

        private void DrawEyelidBlendshapes(SerializedProperty settings)
        {
            SerializedProperty meshProperty = settings.FindPropertyRelative("eyelidsSkinnedMesh");
            EditorGUILayout.PropertyField(meshProperty, new GUIContent("Eyelids Mesh"));
            if (!(meshProperty.objectReferenceValue is SkinnedMeshRenderer mesh) || mesh.sharedMesh == null) return;
            if (!SubFoldout("EyelidBlendshapes", "Blendshape States")) return;

            SerializedProperty indices = settings.FindPropertyRelative("eyelidsBlendshapes");
            if (indices.arraySize != 3)
            {
                indices.arraySize = 3;
                for (int i = 0; i < 3; i++) indices.GetArrayElementAtIndex(i).intValue = -1;
            }
            string[] names = { "Blink", "Looking Up", "Looking Down" };
            for (int i = 0; i < 3; i++)
            {
                SerializedProperty element = indices.GetArrayElementAtIndex(i);
                element.intValue = BlendShapeIndexPopup(names[i], element.intValue, mesh.sharedMesh);
            }
        }

        // =========================================================================================
        // Playable Layers
        // =========================================================================================

        private void DrawPlayableLayers()
        {
            if (!Foldout("Playable Layers", true)) return;
            SerializedProperty customize = P("customizeAnimationLayers");
            if (!customize.boolValue)
            {
                if (GUILayout.Button("Customize"))
                {
                    ResetLayers(P("baseAnimationLayers"), ParelAvatarDescriptor.BaseLayerTypes);
                    ResetLayers(P("specialAnimationLayers"), ParelAvatarDescriptor.SpecialLayerTypes);
                    customize.boolValue = true;
                }
                Separator();
                return;
            }

            if (GUILayout.Button("Reset to Default") &&
                EditorUtility.DisplayDialog("Reset to Default", "This erases your custom playable layer settings. Are you sure?", "OK", "Cancel"))
            {
                ResetLayers(P("baseAnimationLayers"), ParelAvatarDescriptor.BaseLayerTypes);
                ResetLayers(P("specialAnimationLayers"), ParelAvatarDescriptor.SpecialLayerTypes);
                customize.boolValue = false;
                Separator();
                return;
            }

            bool human = _descriptor.AvatarAnimator != null && _descriptor.AvatarAnimator.isHuman;
            BeginBox("Base");
            DrawLayerList(P("baseAnimationLayers"), human);
            EndBox();
            BeginBox("Special");
            DrawLayerList(P("specialAnimationLayers"), human);
            EndBox();
            Separator();
        }

        private static void ResetLayers(SerializedProperty list, ParelAvatarLayer[] types)
        {
            list.arraySize = types.Length;
            for (int i = 0; i < types.Length; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("isEnabled").boolValue = true;
                element.FindPropertyRelative("type").enumValueIndex = (int)types[i];
                element.FindPropertyRelative("animatorController").objectReferenceValue = null;
                element.FindPropertyRelative("mask").objectReferenceValue = null;
                element.FindPropertyRelative("isDefault").boolValue = true;
            }
        }

        private void DrawLayerList(SerializedProperty list, bool human)
        {
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                var type = (ParelAvatarLayer)element.FindPropertyRelative("type").enumValueIndex;
                if (!human && (type == ParelAvatarLayer.Additive || type == ParelAvatarLayer.Gesture)) continue;
                SerializedProperty isDefault = element.FindPropertyRelative("isDefault");
                SerializedProperty controller = element.FindPropertyRelative("animatorController");

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(" " + type, GUILayout.Width(100));
                    if (isDefault.boolValue)
                    {
                        string name = DefaultLayerNames.TryGetValue(type, out string n) ? n : "Default";
                        if (GUILayout.Button(name, GUILayout.MinWidth(0))) isDefault.boolValue = false;
                    }
                    else
                    {
                        UnityEngine.Object previous = controller.objectReferenceValue;
                        EditorGUILayout.PropertyField(controller, GUIContent.none, GUILayout.MinWidth(0));
                        if (controller.objectReferenceValue != previous) SetMaskFromController(element);
                        if (controller.objectReferenceValue == null && GUILayout.Button("Create", EditorStyles.miniButton, GUILayout.Width(48)))
                        {
                            AnimatorController created = AnimatorController.CreateAnimatorControllerAtPath(UniqueAssetPath(_descriptor, type + ".controller"));
                            if (type == ParelAvatarLayer.Base) EnableIkPass(created);
                            controller.objectReferenceValue = created;
                            EditorGUIUtility.PingObject(created);
                        }
                    }
                    using (new EditorGUI.DisabledScope(isDefault.boolValue))
                    {
                        if (GUILayout.Button("x", EditorStyles.miniButton, GUILayout.Width(20)))
                        {
                            controller.objectReferenceValue = null;
                            element.FindPropertyRelative("mask").objectReferenceValue = null;
                            isDefault.boolValue = true;
                        }
                    }
                }

                if (type == ParelAvatarLayer.Base && !isDefault.boolValue && controller.objectReferenceValue is AnimatorController baseController &&
                    baseController.layers.Length > 0 && !baseController.layers[0].iKPass)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.HelpBox("The Base layer's first animator layer needs IK Pass on, or tracking won't move the body.", MessageType.Warning);
                        if (GUILayout.Button("Fix", GUILayout.Width(40), GUILayout.Height(38))) EnableIkPass(baseController);
                    }
                }
            }
        }

        private static void SetMaskFromController(SerializedProperty element)
        {
            SerializedProperty mask = element.FindPropertyRelative("mask");
            var controller = element.FindPropertyRelative("animatorController").objectReferenceValue as AnimatorController;
            element.FindPropertyRelative("isDefault").boolValue = element.FindPropertyRelative("animatorController").objectReferenceValue == null;
            mask.objectReferenceValue = controller != null && controller.layers.Length > 0 ? controller.layers[0].avatarMask : null;
        }

        /// <summary>ParelVR's IK runs in the Base layer's IK pass, so layer 0 needs it switched on.</summary>
        internal static void EnableIkPass(AnimatorController controller)
        {
            if (controller == null || controller.layers.Length == 0) return;
            AnimatorControllerLayer[] layers = controller.layers;
            if (layers[0].iKPass) return;
            layers[0].iKPass = true;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
        }

        // =========================================================================================
        // Lower Body
        // =========================================================================================

        private void DrawLowerBody()
        {
            if (_descriptor.AvatarAnimator == null || !_descriptor.AvatarAnimator.isHuman) return;
            if (!Foldout("Lower Body", true)) return;
            SerializedProperty footsteps = P("useAutoFootstep");
            SerializedProperty locomotion = P("forceLocomotionAnimations");
            footsteps.boolValue = EditorGUILayout.ToggleLeft("Use Auto-Footsteps for 3 and 4 point tracking", footsteps.boolValue);
            locomotion.boolValue = EditorGUILayout.ToggleLeft("Force Locomotion animations for 6 point tracking", locomotion.boolValue);
            Separator();
        }

        // =========================================================================================
        // Expressions
        // =========================================================================================

        private void DrawExpressions()
        {
            if (!Foldout("Expressions", true)) return;
            SerializedProperty custom = P("customExpressions");
            SerializedProperty menu = P("expressionsMenu");
            SerializedProperty parameters = P("expressionParameters");

            if (!custom.boolValue)
            {
                if (GUILayout.Button("Customize")) custom.boolValue = true;
                Separator();
                return;
            }

            if (GUILayout.Button("Reset To Default") &&
                EditorUtility.DisplayDialog("Reset to Default", "This erases your custom expression settings. Are you sure?", "OK", "Cancel"))
            {
                menu.objectReferenceValue = null;
                parameters.objectReferenceValue = null;
                custom.boolValue = false;
                Separator();
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(menu, new GUIContent("Menu"));
                if (menu.objectReferenceValue == null)
                {
                    if (GUILayout.Button("Create", EditorStyles.miniButton, GUILayout.Width(52)))
                    {
                        var created = CreateInstance<ParelExpressionsMenu>();
                        AssetDatabase.CreateAsset(created, UniqueAssetPath(_descriptor, "ExpressionsMenu.asset"));
                        menu.objectReferenceValue = created;
                        EditorGUIUtility.PingObject(created);
                    }
                }
                else if (GUILayout.Button("Edit", EditorStyles.miniButton, GUILayout.Width(52)))
                {
                    Selection.activeObject = menu.objectReferenceValue;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(parameters, new GUIContent("Parameters"));
                if (parameters.objectReferenceValue == null)
                {
                    if (GUILayout.Button("Create", EditorStyles.miniButton, GUILayout.Width(52)))
                    {
                        var created = CreateInstance<ParelExpressionParameters>();
                        AssetDatabase.CreateAsset(created, UniqueAssetPath(_descriptor, "ExpressionParameters.asset"));
                        parameters.objectReferenceValue = created;
                        EditorGUIUtility.PingObject(created);
                    }
                }
                else if (GUILayout.Button("Edit", EditorStyles.miniButton, GUILayout.Width(52)))
                {
                    Selection.activeObject = parameters.objectReferenceValue;
                }
            }

            if (parameters.objectReferenceValue is ParelExpressionParameters assigned)
            {
                int cost = assigned.CalcTotalCost();
                int toolCost = ParelAvatarToolsGUI.ExtraSyncedBits(_descriptor);
                Rect bar = EditorGUILayout.GetControlRect(false, 18);
                string text = toolCost > 0
                    ? $"Parameters: {cost} / {ParelExpressionParameters.MaxSyncedBits} bits  (+{toolCost} from Avatar Tools)"
                    : $"Parameters: {cost} / {ParelExpressionParameters.MaxSyncedBits} bits";
                EditorGUI.ProgressBar(bar, Mathf.Clamp01((cost + toolCost) / (float)ParelExpressionParameters.MaxSyncedBits), text);
            }
            Separator();
        }

        // =========================================================================================
        // Colliders
        // =========================================================================================

        private void DrawColliders()
        {
            if (!Foldout("Colliders", false)) return;
            foreach ((string label, string left, string right, ParelAvatarColliderSlot slotL, ParelAvatarColliderSlot slotR) in Colliders)
            {
                SerializedProperty l = P(left);
                if (l == null) continue;
                if (right == null)
                {
                    DrawCollider(label, l, slotL);
                    continue;
                }

                SerializedProperty r = P(right);
                SerializedProperty mirrored = l.FindPropertyRelative("isMirrored");
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    bool mirror = GUILayout.Toggle(mirrored.boolValue, new GUIContent("Mirror " + label, "Edit both sides together."), EditorStyles.miniButton, GUILayout.Width(120));
                    if (mirror != mirrored.boolValue)
                    {
                        mirrored.boolValue = mirror;
                        r.FindPropertyRelative("isMirrored").boolValue = mirror;
                    }
                }

                if (mirrored.boolValue)
                {
                    bool changed = DrawCollider(label, l, slotL);
                    if (changed || r.FindPropertyRelative("state").intValue != l.FindPropertyRelative("state").intValue) MirrorCollider(l, r, slotL, slotR);
                }
                else
                {
                    DrawCollider(label + " L", l, slotL);
                    DrawCollider(label + " R", r, slotR);
                }
            }
            Separator();
        }

        private bool DrawCollider(string label, SerializedProperty config, ParelAvatarColliderSlot slot)
        {
            SerializedProperty state = config.FindPropertyRelative("state");
            string key = "col." + slot;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(state, new GUIContent(label));
                using (new EditorGUI.DisabledScope(state.intValue == (int)ParelAvatarDescriptor.ColliderState.Disabled))
                {
                    if (ActiveButton(key, "Edit"))
                    {
                        SetActive(key);
                        _activeCollider = slot;
                    }
                }
            }

            if (state.intValue != (int)ParelAvatarDescriptor.ColliderState.Disabled)
            {
                EditorGUI.indentLevel++;
                if (state.intValue == (int)ParelAvatarDescriptor.ColliderState.Custom)
                {
                    EditorGUILayout.PropertyField(config.FindPropertyRelative("transform"));
                    EditorGUILayout.PropertyField(config.FindPropertyRelative("position"));
                    QuaternionField("Rotation", config.FindPropertyRelative("rotation"));
                }
                EditorGUILayout.PropertyField(config.FindPropertyRelative("radius"));
                if (slot != ParelAvatarColliderSlot.Head) EditorGUILayout.PropertyField(config.FindPropertyRelative("height"));
                EditorGUI.indentLevel--;
            }
            return EditorGUI.EndChangeCheck();
        }

        /// <summary>Copies the left collider to the right side, mirrored across the avatar's center.</summary>
        private void MirrorCollider(SerializedProperty left, SerializedProperty right, ParelAvatarColliderSlot slotL, ParelAvatarColliderSlot slotR)
        {
            right.FindPropertyRelative("state").intValue = left.FindPropertyRelative("state").intValue;
            right.FindPropertyRelative("radius").floatValue = left.FindPropertyRelative("radius").floatValue;
            right.FindPropertyRelative("height").floatValue = left.FindPropertyRelative("height").floatValue;
            if (left.FindPropertyRelative("state").intValue != (int)ParelAvatarDescriptor.ColliderState.Custom) return;

            Animator animator = _descriptor.AvatarAnimator;
            Transform root = _descriptor.transform;
            var leftTransform = left.FindPropertyRelative("transform").objectReferenceValue as Transform;
            Transform leftBone = leftTransform != null ? leftTransform : HumanBone(animator, ParelAvatarDescriptor.ColliderBone(slotL));
            Transform rightBone = MirroredBone(animator, leftTransform) ?? HumanBone(animator, ParelAvatarDescriptor.ColliderBone(slotR));
            right.FindPropertyRelative("transform").objectReferenceValue = leftTransform != null ? rightBone : null;
            if (leftBone == null || rightBone == null) return;

            Vector3 worldL = leftBone.TransformPoint(left.FindPropertyRelative("position").vector3Value);
            Vector3 localL = root.InverseTransformPoint(worldL);
            Vector3 worldR = root.TransformPoint(new Vector3(-localL.x, localL.y, localL.z));
            right.FindPropertyRelative("position").vector3Value = rightBone.InverseTransformPoint(worldR);

            Quaternion worldRotL = leftBone.rotation * left.FindPropertyRelative("rotation").quaternionValue;
            Quaternion avatarL = Quaternion.Inverse(root.rotation) * worldRotL;
            var mirroredRot = new Quaternion(avatarL.x, -avatarL.y, -avatarL.z, avatarL.w);
            right.FindPropertyRelative("rotation").quaternionValue = Quaternion.Inverse(rightBone.rotation) * (root.rotation * mirroredRot);
        }

        private static Transform HumanBone(Animator animator, HumanBodyBones bone) => animator != null && animator.isHuman ? animator.GetBoneTransform(bone) : null;

        private static Transform MirroredBone(Animator animator, Transform bone)
        {
            if (animator == null || !animator.isHuman || bone == null) return null;
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                if (animator.GetBoneTransform((HumanBodyBones)i) != bone) continue;
                string name = ((HumanBodyBones)i).ToString();
                string mirrored = name.StartsWith("Left") ? "Right" + name.Substring(4) : name.StartsWith("Right") ? "Left" + name.Substring(5) : name;
                return Enum.TryParse(mirrored, out HumanBodyBones other) ? animator.GetBoneTransform(other) : null;
            }
            return null;
        }

        // =========================================================================================
        // ParelVR extras + footer
        // =========================================================================================

        private void DrawParelVR()
        {
            if (!Foldout("ParelVR", false)) return;

            EditorGUILayout.LabelField("Avatar Tools", EditorStyles.boldLabel);
            ParelAvatarToolsGUI.DrawSummary(_descriptor);
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Scaling", EditorStyles.boldLabel);
            SerializedProperty allow = P("allowUserScaling");
            EditorGUILayout.PropertyField(allow, new GUIContent("Allow User Scaling", "Let the wearer resize this avatar in game (radial menu / RSP)."));
            if (allow.boolValue)
            {
                EditorGUILayout.PropertyField(P("minUserScale"), new GUIContent("Smallest (x uploaded size)"));
                EditorGUILayout.PropertyField(P("maxUserScale"), new GUIContent("Largest (x uploaded size)"));
            }
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(P("avatarAnimator"), new GUIContent("Animator"));
            EditorGUILayout.PropertyField(P("headOverride"), new GUIContent("Head Override"));
            EditorGUILayout.PropertyField(P("leftHandOverride"), new GUIContent("Left Hand Override"));
            EditorGUILayout.PropertyField(P("rightHandOverride"), new GUIContent("Right Hand Override"));
            Separator();
        }

        private void DrawFooter()
        {
            string unity = P("unityVersion").stringValue;
            if (!string.IsNullOrEmpty(unity)) EditorGUILayout.LabelField("Unity Version: ", unity);
            Animator animator = _descriptor.AvatarAnimator;
            if (animator != null) EditorGUILayout.LabelField("Rig Type: ", animator.isHuman ? "Humanoid" : "Non-humanoid");
            GUILayout.Space(5);
        }

        // =========================================================================================
        // Scene view
        // =========================================================================================

        private void OnSceneGUI()
        {
            if (_descriptor == null || string.IsNullOrEmpty(_activeKey)) return;
            serializedObject.Update();

            if (_activeKey == "view") SceneView_View();
            else if (_activeKey.StartsWith("eye.") && _activeRotation != null) SceneView_Eyes();
            else if (_activeKey.StartsWith("col.")) SceneView_Collider();

            serializedObject.ApplyModifiedProperties();
        }

        private void SceneView_View()
        {
            Vector3 world = _descriptor.transform.TransformPoint(_descriptor.ViewPoint);
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(world, _descriptor.transform.rotation);
            if (EditorGUI.EndChangeCheck())
            {
                P("viewPoint").vector3Value = _descriptor.transform.InverseTransformPoint(moved);
                P("viewPointInitialized").boolValue = true;
            }
            Handles.color = new Color(0.55f, 0.35f, 1f, 0.9f);
            float size = HandleUtility.GetHandleSize(world) * 0.08f;
            Handles.SphereHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
            Handles.Label(world + _descriptor.transform.up * size * 2f, "View Position");
        }

        private void SceneView_Eyes()
        {
            SerializedProperty left = _activeRotation.FindPropertyRelative("left");
            SerializedProperty right = _activeRotation.FindPropertyRelative("right");
            EditEyeHandle(_activeLeft, left, _activeLinked ? right : null);
            if (!_activeLinked) EditEyeHandle(_activeRight, right, null);
        }

        private static void EditEyeHandle(Transform eye, SerializedProperty property, SerializedProperty mirror)
        {
            if (eye == null) return;
            EditorGUI.BeginChangeCheck();
            Quaternion world = Handles.RotationHandle(eye.rotation, eye.position);
            if (!EditorGUI.EndChangeCheck()) return;
            Quaternion local = eye.parent != null ? Quaternion.Inverse(eye.parent.rotation) * world : world;
            eye.localRotation = local;
            property.quaternionValue = local;
            if (mirror != null) mirror.quaternionValue = local;
        }

        private void SceneView_Collider()
        {
            ParelAvatarDescriptor.ColliderConfig config = _descriptor.GetCollider(_activeCollider);
            if (config == null || config.state == ParelAvatarDescriptor.ColliderState.Disabled) return;
            Animator animator = _descriptor.AvatarAnimator;
            Transform bone = config.state == ParelAvatarDescriptor.ColliderState.Custom && config.transform != null
                ? config.transform
                : HumanBone(animator, ParelAvatarDescriptor.ColliderBone(_activeCollider));
            if (bone == null) return;

            float avatarScale = Mathf.Max(0.0001f, _descriptor.transform.lossyScale.y);
            bool custom = config.state == ParelAvatarDescriptor.ColliderState.Custom;
            Vector3 center = custom ? bone.TransformPoint(config.position) : bone.position;
            Quaternion rotation = custom ? bone.rotation * config.rotation : bone.rotation;
            float radius = config.radius * avatarScale;
            float height = _activeCollider == ParelAvatarColliderSlot.Head ? 0f : config.height * avatarScale;

            Handles.color = new Color(0.4f, 0.8f, 1f, 0.9f);
            DrawCapsule(center, rotation, radius, height);

            if (!custom) return;
            string field = ColliderField(_activeCollider);
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(center, rotation);
            if (EditorGUI.EndChangeCheck() && field != null)
            {
                P(field).FindPropertyRelative("position").vector3Value = bone.InverseTransformPoint(moved);
            }
        }

        private static string ColliderField(ParelAvatarColliderSlot slot)
        {
            foreach ((string _, string left, string right, ParelAvatarColliderSlot slotL, ParelAvatarColliderSlot slotR) in Colliders)
            {
                if (slotL == slot) return left;
                if (right != null && slotR == slot) return right;
            }
            return null;
        }

        internal static void DrawCapsule(Vector3 center, Quaternion rotation, float radius, float height)
        {
            Vector3 up = rotation * Vector3.up;
            float half = Mathf.Max(0f, height * 0.5f - radius);
            Vector3 a = center + up * half;
            Vector3 b = center - up * half;
            Vector3 side = rotation * Vector3.right;
            Vector3 forward = rotation * Vector3.forward;
            Handles.DrawWireDisc(a, up, radius);
            Handles.DrawWireDisc(b, up, radius);
            Handles.DrawWireArc(a, side, forward, 180f, radius);
            Handles.DrawWireArc(a, forward, -side, 180f, radius);
            Handles.DrawWireArc(b, side, -forward, 180f, radius);
            Handles.DrawWireArc(b, forward, side, 180f, radius);
            Handles.DrawLine(a + side * radius, b + side * radius);
            Handles.DrawLine(a - side * radius, b - side * radius);
            Handles.DrawLine(a + forward * radius, b + forward * radius);
            Handles.DrawLine(a - forward * radius, b - forward * radius);
        }

        // =========================================================================================
        // Preview state
        // =========================================================================================

        /// <summary>An Edit / Preview button. True when it was just switched on; pressed again ("Return") it switches off.</summary>
        private bool ActiveButton(string key, string idleLabel)
        {
            bool active = _activeKey == key;
            Color previous = GUI.backgroundColor;
            if (active) GUI.backgroundColor = ActiveButtonColor;
            bool pressed = GUILayout.Button(active ? "Return" : idleLabel, EditorStyles.miniButton, GUILayout.Width(PreviewButtonWidth));
            GUI.backgroundColor = previous;
            if (!pressed) return false;
            if (active)
            {
                ClearActive();
                SceneView.RepaintAll();
                return false;
            }
            return true;
        }

        private void SetActive(string key)
        {
            if (_activeKey == key) return;
            ClearActive();
            _activeKey = key;
            SceneView.RepaintAll();
        }

        private void ClearActive()
        {
            for (int i = _restore.Count - 1; i >= 0; i--)
            {
                try { _restore[i](); }
                catch (Exception) { /* the bone was deleted */ }
            }
            _restore.Clear();
            _previewOriginals.Clear();
            _activeKey = null;
            _activeRotation = null;
            _activeLeft = null;
            _activeRight = null;
        }

        /// <summary>Poses a bone for a preview, remembering its original rotation so Return puts it back.</summary>
        private void PreviewRotation(Transform bone, Quaternion local)
        {
            if (bone == null) return;
            if (!_previewOriginals.ContainsKey(bone))
            {
                Quaternion original = bone.localRotation;
                _previewOriginals[bone] = original;
                _restore.Add(() =>
                {
                    if (bone != null) bone.localRotation = original;
                });
            }
            bone.localRotation = local;
            SceneView.RepaintAll();
        }

        private readonly Dictionary<Transform, Quaternion> _previewOriginals = new Dictionary<Transform, Quaternion>();

        // =========================================================================================
        // Small GUI helpers
        // =========================================================================================

        private static bool Foldout(string section, bool defaultOpen)
        {
            string key = "ParelVR.SDK.AvatarDescriptor.Foldout." + section;
            bool open = EditorPrefs.GetBool(key, defaultOpen);
            bool now = EditorGUILayout.Foldout(open, section, true, EditorStyles.foldoutHeader);
            if (now != open) EditorPrefs.SetBool(key, now);
            return now;
        }

        private static bool SubFoldout(string id, string label)
        {
            string key = "ParelVR.SDK.AvatarDescriptor.Sub." + id;
            bool open = EditorPrefs.GetBool(key, true);
            bool now = EditorGUILayout.Foldout(open, label, true);
            if (now != open) EditorPrefs.SetBool(key, now);
            return now;
        }

        private void BeginBox(string title)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);
            if (!string.IsNullOrEmpty(title)) EditorGUILayout.LabelField(title, _boxTitle);
        }

        private static void EndBox()
        {
            EditorGUILayout.EndVertical();
            GUILayout.Space(2);
        }

        private static void Separator()
        {
            GUILayout.Space(4);
            Rect r = EditorGUILayout.GetControlRect(false, 2);
            r.x -= 10;
            r.width += 20;
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.15f));
            GUILayout.Space(4);
        }

        private static void LabeledSlider(string leftLabel, string rightLabel, SerializedProperty value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(leftLabel, GUILayout.Width(70));
                value.floatValue = GUILayout.HorizontalSlider(value.floatValue, 0f, 1f);
                GUILayout.Label(rightLabel, GUILayout.Width(70));
            }
        }

        private static bool QuaternionField(string label, SerializedProperty quaternion)
        {
            Quaternion q = quaternion.quaternionValue;
            if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f) q = Quaternion.identity;
            EditorGUI.BeginChangeCheck();
            Vector3 euler = EditorGUILayout.Vector3Field(label, Round(q.eulerAngles));
            if (!EditorGUI.EndChangeCheck()) return false;
            quaternion.quaternionValue = Quaternion.Euler(euler);
            return true;
        }

        private static Vector3 Round(Vector3 v) => new Vector3((float)Math.Round(v.x, 3), (float)Math.Round(v.y, 3), (float)Math.Round(v.z, 3));

        private static string BlendShapePopup(string label, string current, Mesh mesh)
        {
            var options = new List<string> { "-none-" };
            for (int i = 0; i < mesh.blendShapeCount; i++) options.Add(mesh.GetBlendShapeName(i));
            int index = string.IsNullOrEmpty(current) ? 0 : options.IndexOf(current);
            if (index < 0) index = 0;
            int picked = EditorGUILayout.Popup(label, index, options.ToArray());
            return picked <= 0 ? string.Empty : options[picked];
        }

        private static int BlendShapeIndexPopup(string label, int current, Mesh mesh)
        {
            var options = new string[mesh.blendShapeCount + 1];
            options[0] = "-none-";
            for (int i = 0; i < mesh.blendShapeCount; i++) options[i + 1] = mesh.GetBlendShapeName(i);
            int picked = EditorGUILayout.Popup(label, Mathf.Clamp(current + 1, 0, options.Length - 1), options);
            return picked - 1;
        }

        /// <summary>A new asset path next to the avatar (or under Assets/ParelVR Avatars/&lt;name&gt;).</summary>
        internal static string UniqueAssetPath(ParelAvatarDescriptor descriptor, string fileName)
        {
            string folder = null;
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(descriptor.gameObject);
            string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            if (!string.IsNullOrEmpty(sourcePath)) folder = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');

            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets"))
            {
                if (!AssetDatabase.IsValidFolder("Assets/ParelVR Avatars")) AssetDatabase.CreateFolder("Assets", "ParelVR Avatars");
                string safe = descriptor.gameObject.name;
                foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                folder = "Assets/ParelVR Avatars/" + safe;
                if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/ParelVR Avatars", safe);
            }

            return AssetDatabase.GenerateUniqueAssetPath(folder + "/" + descriptor.gameObject.name + " " + fileName);
        }
    }
}
