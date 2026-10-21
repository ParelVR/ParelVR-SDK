using System;
using System.Collections.Generic;
using ParelVR.AvatarSDK;
using ParelVR.SDK.Avatars.Build;
using ParelVR.SDK.Avatars.Validation;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Inspectors
{
    /// <summary>Shared drawing for the Avatar Dynamics inspectors.</summary>
    internal static class DynamicsGUI
    {
        private static readonly string[] AdvancedBool = { "False", "True", "Other" };

        public static void Section(string title)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        /// <summary>A value with an optional curve over the chain (the small "C" button).</summary>
        public static void CurveValue(SerializedObject so, string valueName, string curveName, string label, float min, float max)
        {
            SerializedProperty value = so.FindProperty(valueName);
            SerializedProperty curve = so.FindProperty(curveName);
            bool hasCurve = curve != null && curve.animationCurveValue != null && curve.animationCurveValue.length > 0;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.Slider(value, min, max, new GUIContent(label, value.tooltip));
                if (curve != null)
                {
                    bool want = GUILayout.Toggle(hasCurve, new GUIContent("C", "Vary this value along the chain with a curve (0 = root, 1 = tip)."), EditorStyles.miniButton, GUILayout.Width(22));
                    if (want != hasCurve) curve.animationCurveValue = want ? AnimationCurve.Linear(0f, 1f, 1f, 1f) : new AnimationCurve();
                }
            }
            if (curve != null && hasCurve)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(curve, new GUIContent(label + " Curve"));
                EditorGUI.indentLevel--;
            }
        }

        /// <summary>A True / False / Other permission dropdown, stored as a ParelDynamicsPermission.</summary>
        public static void Permission(SerializedProperty property, string label)
        {
            var value = (ParelDynamicsPermission)property.enumValueIndex;
            int mode = value == ParelDynamicsPermission.Nobody ? 0 : value == ParelDynamicsPermission.Everyone ? 1 : 2;
            int picked = EditorGUILayout.Popup(new GUIContent(label, property.tooltip), mode, AdvancedBool);
            if (picked != mode)
            {
                value = picked == 0 ? ParelDynamicsPermission.Nobody : picked == 1 ? ParelDynamicsPermission.Everyone : ParelDynamicsPermission.SelfOnly;
                property.enumValueIndex = (int)value;
            }
            if (picked != 2) return;

            EditorGUI.indentLevel++;
            bool self = value == ParelDynamicsPermission.SelfOnly || value == ParelDynamicsPermission.Everyone;
            bool others = value == ParelDynamicsPermission.OthersOnly || value == ParelDynamicsPermission.Everyone;
            bool newSelf = EditorGUILayout.Toggle(new GUIContent("Allow Self", "The wearer of this avatar."), self);
            bool newOthers = EditorGUILayout.Toggle(new GUIContent("Allow Others", "Everyone else."), others);
            if (newSelf != self || newOthers != others)
            {
                property.enumValueIndex = (int)(newSelf && newOthers ? ParelDynamicsPermission.Everyone
                    : newSelf ? ParelDynamicsPermission.SelfOnly
                    : newOthers ? ParelDynamicsPermission.OthersOnly
                    : ParelDynamicsPermission.Nobody);
            }
            EditorGUI.indentLevel--;
        }

        public static void Prop(SerializedObject so, string name, string label = null)
        {
            SerializedProperty property = so.FindProperty(name);
            if (property == null) return;
            EditorGUILayout.PropertyField(property, label == null ? null : new GUIContent(label, property.tooltip), true);
        }
    }

    // =============================================================================================

    [CustomEditor(typeof(ParelMotionBone)), CanEditMultipleObjects]
    public sealed class ParelMotionBoneEditor : Editor
    {
        private static readonly string[] Versions = { "Version 1.0", "Version 1.1" };

        private readonly List<string> _issues = new List<string>();
        private double _nextIssueCheck;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var bone = (ParelMotionBone)target;

            DrawPresetMenu();

            SerializedProperty version = serializedObject.FindProperty("version");
            version.enumValueIndex = EditorGUILayout.Popup(new GUIContent("Version", version.tooltip), version.enumValueIndex, Versions);
            bool v11 = version.enumValueIndex == (int)ParelMotionBone.Version.Version_1_1;

            DynamicsGUI.Section("Transforms");
            DynamicsGUI.Prop(serializedObject, "rootTransform", "Root Transform");
            DynamicsGUI.Prop(serializedObject, "ignoreTransforms", "Ignore Transforms");
            DynamicsGUI.Prop(serializedObject, "endpointPosition", "Endpoint Position");
            DynamicsGUI.Prop(serializedObject, "multiChildType", "Multi Child Type");

            DynamicsGUI.Section("Forces");
            SerializedProperty integration = serializedObject.FindProperty("integrationType");
            EditorGUILayout.PropertyField(integration, new GUIContent("Integration Type"));
            bool advanced = integration.enumValueIndex == (int)ParelMotionBone.IntegrationType.Advanced;
            DynamicsGUI.CurveValue(serializedObject, "pull", "pullCurve", "Pull", 0f, 1f);
            DynamicsGUI.CurveValue(serializedObject, "spring", "springCurve", advanced ? "Momentum" : "Spring", 0f, 1f);
            if (advanced) DynamicsGUI.CurveValue(serializedObject, "stiffness", "stiffnessCurve", "Stiffness", 0f, 1f);
            DynamicsGUI.CurveValue(serializedObject, "gravity", "gravityCurve", "Gravity", -1f, 1f);
            DynamicsGUI.CurveValue(serializedObject, "gravityFalloff", "gravityFalloffCurve", "Gravity Falloff", 0f, 1f);
            DynamicsGUI.Prop(serializedObject, "immobileType", "Immobile Type");
            DynamicsGUI.CurveValue(serializedObject, "immobile", "immobileCurve", "Immobile", 0f, 1f);

            DynamicsGUI.Section("Limits");
            SerializedProperty limit = serializedObject.FindProperty("limitType");
            EditorGUILayout.PropertyField(limit, new GUIContent("Limit Type"));
            var limitType = (ParelMotionBone.LimitType)limit.enumValueIndex;
            if (limitType != ParelMotionBone.LimitType.None)
            {
                DynamicsGUI.CurveValue(serializedObject, "maxAngleX", "maxAngleXCurve", limitType == ParelMotionBone.LimitType.Polar ? "Max Angle X" : "Max Angle", 0f, 180f);
                if (limitType == ParelMotionBone.LimitType.Polar) DynamicsGUI.CurveValue(serializedObject, "maxAngleZ", "maxAngleZCurve", "Max Angle Z", 0f, 90f);
                DynamicsGUI.Prop(serializedObject, "limitRotation", "Rotation");
            }

            DynamicsGUI.Section("Collision");
            DynamicsGUI.CurveValue(serializedObject, "radius", "radiusCurve", "Radius", 0f, 1f);
            DynamicsGUI.Permission(serializedObject.FindProperty("allowCollision"), "Allow Collision");
            DynamicsGUI.Prop(serializedObject, "colliders", "Colliders");

            DynamicsGUI.Section("Stretch & Squish");
            DynamicsGUI.CurveValue(serializedObject, "maxStretch", "maxStretchCurve", "Max Stretch", 0f, 5f);
            if (v11)
            {
                DynamicsGUI.CurveValue(serializedObject, "maxSquish", "maxSquishCurve", "Max Squish", 0f, 1f);
                DynamicsGUI.CurveValue(serializedObject, "stretchMotion", "stretchMotionCurve", "Stretch Motion", 0f, 1f);
            }

            DynamicsGUI.Section("Grab & Pose");
            DynamicsGUI.Permission(serializedObject.FindProperty("allowGrabbing"), "Allow Grabbing");
            DynamicsGUI.Permission(serializedObject.FindProperty("allowPosing"), "Allow Posing");
            SerializedProperty grab = serializedObject.FindProperty("grabMovement");
            EditorGUILayout.Slider(grab, 0f, 1f, new GUIContent("Grab Movement", grab.tooltip));
            DynamicsGUI.Prop(serializedObject, "snapToHand", "Snap To Hand");

            DynamicsGUI.Section("Options");
            DynamicsGUI.Prop(serializedObject, "parameter", "Parameter");
            string parameter = serializedObject.FindProperty("parameter").stringValue;
            if (!string.IsNullOrEmpty(parameter))
            {
                EditorGUILayout.LabelField($"Sets {parameter}_IsGrabbed, {parameter}_IsPosed, {parameter}_Angle, {parameter}_Stretch, {parameter}_Squish", EditorStyles.miniLabel);
            }
            DynamicsGUI.Prop(serializedObject, "isAnimated", "Is Animated");
            DynamicsGUI.Prop(serializedObject, "resetWhenDisabled", "Reset When Disabled");

            DynamicsGUI.Section("Gizmos");
            DynamicsGUI.Prop(serializedObject, "showGizmos", "Show Gizmos");
            SerializedProperty boneOpacity = serializedObject.FindProperty("boneOpacity");
            EditorGUILayout.Slider(boneOpacity, 0f, 1f, new GUIContent("Bone Opacity"));
            SerializedProperty limitOpacity = serializedObject.FindProperty("limitOpacity");
            EditorGUILayout.Slider(limitOpacity, 0f, 1f, new GUIContent("Limit Opacity"));

            serializedObject.ApplyModifiedProperties();

            if (!serializedObject.isEditingMultipleObjects)
            {
                int count = bone.Root != null ? bone.Root.GetComponentsInChildren<Transform>(true).Length : 0;
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox($"Affected transforms: {count}" + (count > ParelMotionBone.MaxParticles ? $" -- only the first {ParelMotionBone.MaxParticles} move. Split it into several Motion Bones." : string.Empty),
                    count > ParelMotionBone.MaxParticles ? MessageType.Warning : MessageType.None);
                DrawIssues(bone);
            }

            if (Application.isPlaying) DrawRuntime(bone);
        }

        private void DrawPresetMenu()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Preset", "Fills Forces, Limits and Stretch & Squish with a starting point for a common use (and clears their curves). Transforms, radius, colliders, permissions and the parameter are left alone."));
                if (!GUILayout.Button("Apply Preset...", EditorStyles.popup)) return;
            }

            var menu = new GenericMenu();
            foreach (ParelMotionBonePreset preset in Enum.GetValues(typeof(ParelMotionBonePreset)))
            {
                ParelMotionBonePreset captured = preset;
                menu.AddItem(new GUIContent(ObjectNames.NicifyVariableName(preset.ToString())), false, () => ApplyPreset(captured));
            }
            menu.ShowAsContext();
        }

        private void ApplyPreset(ParelMotionBonePreset preset)
        {
            Undo.RecordObjects(targets, "Apply Motion Bone Preset");
            foreach (UnityEngine.Object item in targets)
            {
                if (!(item is ParelMotionBone motionBone)) continue;
                motionBone.ApplyPreset(preset);
                EditorUtility.SetDirty(motionBone);
                PrefabUtility.RecordPrefabInstancePropertyModifications(motionBone);
            }
        }

        /// <summary>The same set-up checks the avatar Builder runs, refreshed about once a second.</summary>
        private void DrawIssues(ParelMotionBone bone)
        {
            if (EditorApplication.timeSinceStartup >= _nextIssueCheck)
            {
                _nextIssueCheck = EditorApplication.timeSinceStartup + 1.0;
                _issues.Clear();
                ParelAvatarDescriptorValidator.CollectMotionBoneIssues(bone, _issues);
            }
            foreach (string issue in _issues) EditorGUILayout.HelpBox(issue, MessageType.Warning);
        }

        private void DrawRuntime(ParelMotionBone bone)
        {
            DynamicsGUI.Section("Runtime");
            if (!serializedObject.isEditingMultipleObjects)
            {
                string state = bone.IsSuspended ? "Paused (over budget, out of range, or stopped after errors)"
                    : bone.IsGrabbed ? "Grabbed"
                    : bone.IsPosed ? "Posed"
                    : "Simulating";
                EditorGUILayout.LabelField("State", state);
                EditorGUILayout.LabelField("Simulated Bones", bone.ParticleCount.ToString());
            }
            EditorGUILayout.LabelField("All Motion Bones", $"{ParelMotionBoneSystem.SimulatedLastFrame} running, {ParelMotionBoneSystem.SkippedLastFrame} paused, {ParelMotionBoneSystem.SimulatedParticlesLastFrame} bones");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Reset Simulation", "Drop all motion and start again from the animated pose.")))
                {
                    foreach (UnityEngine.Object item in targets) (item as ParelMotionBone)?.ResetSimulation();
                }
                if (GUILayout.Button(new GUIContent("Clear Pose", "Let posed bones fall back to normal physics.")))
                {
                    foreach (UnityEngine.Object item in targets) (item as ParelMotionBone)?.ClearPose();
                }
            }
            Repaint();
        }
    }

    // =============================================================================================

    [CustomEditor(typeof(ParelMotionBoneCollider)), CanEditMultipleObjects]
    public sealed class ParelMotionBoneColliderEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DynamicsGUI.Prop(serializedObject, "rootTransform", "Root Transform");
            SerializedProperty shape = serializedObject.FindProperty("shapeType");
            EditorGUILayout.PropertyField(shape, new GUIContent("Shape Type"));
            var type = (ParelMotionBoneCollider.ShapeType)shape.enumValueIndex;
            if (type != ParelMotionBoneCollider.ShapeType.Plane)
            {
                DynamicsGUI.Prop(serializedObject, "insideBounds", "Inside Bounds");
                DynamicsGUI.Prop(serializedObject, "radius", "Radius");
            }
            if (type == ParelMotionBoneCollider.ShapeType.Capsule) DynamicsGUI.Prop(serializedObject, "height", "Height");
            DynamicsGUI.Prop(serializedObject, "position", "Position");
            SerializedProperty rotation = serializedObject.FindProperty("rotation");
            EditorGUI.BeginChangeCheck();
            Vector3 euler = EditorGUILayout.Vector3Field("Rotation", rotation.quaternionValue.eulerAngles);
            if (EditorGUI.EndChangeCheck()) rotation.quaternionValue = Quaternion.Euler(euler);
            DynamicsGUI.Prop(serializedObject, "bonesAsSpheres", "Bones As Spheres");
            serializedObject.ApplyModifiedProperties();
        }

        private void OnSceneGUI()
        {
            var collider = (ParelMotionBoneCollider)target;
            Transform root = collider.Root;
            float scale = Mathf.Max(Mathf.Abs(root.lossyScale.x), Mathf.Max(Mathf.Abs(root.lossyScale.y), Mathf.Abs(root.lossyScale.z)));
            Vector3 center = root.TransformPoint(collider.position);
            Quaternion rotation = root.rotation * collider.rotation;
            Handles.color = collider.insideBounds ? new Color(1f, 0.55f, 0.2f, 0.9f) : new Color(0.4f, 0.8f, 1f, 0.9f);
            switch (collider.shapeType)
            {
                case ParelMotionBoneCollider.ShapeType.Plane:
                    Handles.DrawWireDisc(center, rotation * Vector3.up, 0.1f * scale);
                    Handles.DrawLine(center, center + rotation * Vector3.up * 0.1f * scale);
                    break;
                case ParelMotionBoneCollider.ShapeType.Capsule:
                    ParelAvatarDescriptorEditor.DrawCapsule(center, rotation, collider.radius * scale, collider.height * scale);
                    break;
                default:
                    ParelAvatarDescriptorEditor.DrawCapsule(center, rotation, collider.radius * scale, 0f);
                    break;
            }
        }
    }

    // =============================================================================================

    [CustomEditor(typeof(ParelContactSender)), CanEditMultipleObjects]
    public sealed class ParelContactSenderEditor : ParelContactEditorBase
    {
        protected override bool IsReceiver => false;
    }

    [CustomEditor(typeof(ParelContactReceiver)), CanEditMultipleObjects]
    public sealed class ParelContactReceiverEditor : ParelContactEditorBase
    {
        protected override bool IsReceiver => true;
    }

    /// <summary>
    /// Contacts made simple: pick a preset (Headpat, Boop, Hug, High Five, Stomp, Hand Touch), choose who can touch it
    /// with body-part buttons instead of typing tags, add the parameters you want from one receiver, and let the
    /// inspector add those parameters to the FX controller.
    /// </summary>
    public abstract class ParelContactEditorBase : Editor
    {
        /// <summary>The tags ParelVR's automatic avatar contacts send.</summary>
        internal static readonly string[] BuiltInTags =
        {
            "Head", "Torso", "Hand", "HandL", "HandR", "Foot", "FootL", "FootR",
            "Finger", "FingerL", "FingerR", "FingerIndex", "FingerIndexL", "FingerIndexR",
            "FingerMiddle", "FingerMiddleL", "FingerMiddleR", "FingerRing", "FingerRingL", "FingerRingR",
            "FingerLittle", "FingerLittleL", "FingerLittleR",
        };

        private static readonly (string Label, string Tag)[] BodyParts =
        {
            ("Head", "Head"), ("Torso", "Torso"), ("Hands", "Hand"), ("Fingers", "Finger"), ("Index fingers", "FingerIndex"), ("Feet", "Foot"),
        };

        private enum Preset { Choose, Headpat, Boop, Hug, HighFive, Stomp, HandTouch }

        private static readonly string[] PresetNames = { "Apply a preset...", "Headpat (head, hands and fingers, proximity + taps)", "Boop (nose, fingertips, pulse + taps)",
            "Hug (torso, hands, held)", "High Five (hand, other people's hands, pulse)", "Stomp (feet, pulse + speed)", "Hand Touch (anything a hand touches, held)" };

        private bool _showAdvanced;

        protected abstract bool IsReceiver { get; }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var contact = (ParelContactBase)target;
            bool onAvatar = contact.GetComponentInParent<ParelAvatarDescriptor>() != null;

            if (IsReceiver && !serializedObject.isEditingMultipleObjects)
            {
                int preset = EditorGUILayout.Popup(0, PresetNames);
                if (preset != 0)
                {
                    serializedObject.ApplyModifiedProperties();
                    ApplyPreset((ParelContactReceiver)target, (Preset)preset);
                    serializedObject.Update();
                }
            }

            DynamicsGUI.Section("Shape");
            DynamicsGUI.Prop(serializedObject, "rootTransform", "Follow");
            SerializedProperty shape = serializedObject.FindProperty("shapeType");
            EditorGUILayout.PropertyField(shape, new GUIContent("Shape"));
            var shapeType = (ParelContactBase.ShapeType)shape.enumValueIndex;
            if (shapeType == ParelContactBase.ShapeType.Box) DynamicsGUI.Prop(serializedObject, "size", "Size");
            else DynamicsGUI.Prop(serializedObject, "radius", "Radius");
            if (shapeType == ParelContactBase.ShapeType.Capsule) DynamicsGUI.Prop(serializedObject, "height", "Height");
            DynamicsGUI.Prop(serializedObject, "position", "Position");
            SerializedProperty rotation = serializedObject.FindProperty("rotation");
            EditorGUI.BeginChangeCheck();
            Vector3 euler = EditorGUILayout.Vector3Field("Rotation", rotation.quaternionValue.eulerAngles);
            if (EditorGUI.EndChangeCheck()) rotation.quaternionValue = Quaternion.Euler(euler);

            DynamicsGUI.Section(IsReceiver ? "Touched by" : "Touches as");
            SerializedProperty tags = serializedObject.FindProperty("collisionTags");
            BodyPartButtons(tags);
            EditorGUILayout.PropertyField(tags, new GUIContent("Tags"), true);
            if (GUILayout.Button("Add a standard tag ▾", EditorStyles.miniButton, GUILayout.Width(150))) ShowTagMenu(tags);
            if (tags.arraySize > ParelContactBase.MaxTags) EditorGUILayout.HelpBox("Only the first " + ParelContactBase.MaxTags + " tags are used.", MessageType.Warning);
            if (tags.arraySize == 0) EditorGUILayout.HelpBox(IsReceiver ? "No tags: nothing can touch this receiver." : "No tags: no receiver can feel this sender.", MessageType.Warning);

            if (IsReceiver)
            {
                if (onAvatar)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DynamicsGUI.Prop(serializedObject, "allowSelf", "Me");
                        DynamicsGUI.Prop(serializedObject, "allowOthers", "Others");
                    }
                }
                SerializedProperty sources = serializedObject.FindProperty("sources");
                sources.intValue = (int)(ParelContactReceiver.Sources)EditorGUILayout.EnumFlagsField(new GUIContent("From", "Other avatars, the world's own senders, or both."), (ParelContactReceiver.Sources)sources.intValue);

                DynamicsGUI.Section("What it sets");
                if (onAvatar)
                {
                    SerializedProperty type = serializedObject.FindProperty("receiverType");
                    EditorGUILayout.PropertyField(type, new GUIContent("Main", "Constant: while touched. On Enter: a short pulse as a touch starts. Proximity: 0 at the edge to 1 at the centre."));
                    DynamicsGUI.Prop(serializedObject, "parameter", "Parameter");
                    var receiverType = (ParelContactReceiver.ReceiverType)type.enumValueIndex;
                    if (receiverType != ParelContactReceiver.ReceiverType.Proximity) DynamicsGUI.Prop(serializedObject, "value", "Value");
                    Outputs(serializedObject.FindProperty("outputs"), serializedObject.FindProperty("parameter").stringValue);
                    if (!serializedObject.isEditingMultipleObjects && GUILayout.Button("Add these parameters to the FX controller")) AddParametersToFx((ParelContactReceiver)target);
                }
                else
                {
                    EditorGUILayout.HelpBox("In a world a receiver calls its events. Wire them to a Volt script's public method, like a UI button.", MessageType.Info);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("onContactEnter"), new GUIContent("On Contact Enter"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("onContactExit"), new GUIContent("On Contact Exit"));
                }

                DynamicsGUI.Section("Feel");
                DynamicsGUI.Prop(serializedObject, "hold", "Hold pulses (s)");
                DynamicsGUI.Prop(serializedObject, "smoothing", "Smoothing (s)");
                DynamicsGUI.Prop(serializedObject, "minVelocity", "Min touch speed");
                if (shapeType == ParelContactBase.ShapeType.Box) DynamicsGUI.Prop(serializedObject, "proximityFromFront", "Proximity from front");
                DynamicsGUI.Prop(serializedObject, "haptics", "Haptics");
            }

            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                DynamicsGUI.Prop(serializedObject, "localOnly", "Local Only");
                if (onAvatar) DynamicsGUI.Prop(serializedObject, "activeWhen", "Active When");
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();

            if (!serializedObject.isEditingMultipleObjects && target is ParelContactReceiver receiver && Application.isPlaying)
            {
                DynamicsGUI.Section("Live");
                EditorGUILayout.LabelField("Value", receiver.CurrentValue.ToString("0.00"));
                EditorGUILayout.LabelField("Touching", receiver.TouchCount.ToString());
                EditorGUILayout.LabelField("Taps", receiver.TapCount.ToString());
                Repaint();
            }
        }

        // ── tags as body parts ───────────────────────────────────────────────

        private static void BodyPartButtons(SerializedProperty tags)
        {
            var present = new HashSet<string>();
            for (int i = 0; i < tags.arraySize; i++) present.Add(tags.GetArrayElementAtIndex(i).stringValue);
            using (new EditorGUILayout.HorizontalScope())
            {
                foreach ((string label, string tag) in BodyParts)
                {
                    bool on = present.Contains(tag);
                    bool want = GUILayout.Toggle(on, label, EditorStyles.miniButton);
                    if (want == on) continue;
                    if (want)
                    {
                        tags.arraySize++;
                        tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
                    }
                    else
                    {
                        for (int i = tags.arraySize - 1; i >= 0; i--)
                            if (tags.GetArrayElementAtIndex(i).stringValue == tag) tags.DeleteArrayElementAtIndex(i);
                    }
                }
            }
        }

        private void ShowTagMenu(SerializedProperty tags)
        {
            var menu = new GenericMenu();
            var existing = new HashSet<string>();
            for (int i = 0; i < tags.arraySize; i++) existing.Add(tags.GetArrayElementAtIndex(i).stringValue);
            SerializedObject so = serializedObject;
            foreach (string tag in BuiltInTags)
            {
                string captured = tag;
                if (existing.Contains(tag))
                {
                    menu.AddDisabledItem(new GUIContent(tag));
                    continue;
                }
                menu.AddItem(new GUIContent(tag), false, () =>
                {
                    so.Update();
                    SerializedProperty list = so.FindProperty("collisionTags");
                    list.arraySize++;
                    list.GetArrayElementAtIndex(list.arraySize - 1).stringValue = captured;
                    so.ApplyModifiedProperties();
                });
            }
            menu.ShowAsContext();
        }

        // ── outputs ──────────────────────────────────────────────────────────

        private static void Outputs(SerializedProperty outputs, string mainParameter)
        {
            for (int i = 0; i < outputs.arraySize; i++)
            {
                SerializedProperty output = outputs.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(output.FindPropertyRelative("kind"), GUIContent.none, GUILayout.Width(110));
                    EditorGUILayout.PropertyField(output.FindPropertyRelative("parameter"), GUIContent.none);
                    if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22)))
                    {
                        outputs.DeleteArrayElementAtIndex(i);
                        break;
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("+ Also set ▾", EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    var menu = new GenericMenu();
                    SerializedObject so = outputs.serializedObject;
                    string path = outputs.propertyPath;
                    foreach (ParelContactReceiver.OutputKind kind in Enum.GetValues(typeof(ParelContactReceiver.OutputKind)))
                    {
                        ParelContactReceiver.OutputKind captured = kind;
                        menu.AddItem(new GUIContent(Describe(kind)), false, () =>
                        {
                            so.Update();
                            SerializedProperty list = so.FindProperty(path);
                            list.arraySize++;
                            SerializedProperty added = list.GetArrayElementAtIndex(list.arraySize - 1);
                            added.FindPropertyRelative("kind").enumValueIndex = (int)captured;
                            string stem = string.IsNullOrEmpty(mainParameter) ? "Contact" : mainParameter;
                            added.FindPropertyRelative("parameter").stringValue = stem + "_" + captured;
                            so.ApplyModifiedProperties();
                        });
                    }
                    menu.ShowAsContext();
                }
            }
        }

        private static string Describe(ParelContactReceiver.OutputKind kind)
        {
            switch (kind)
            {
                case ParelContactReceiver.OutputKind.Touching: return "Touching (Bool)";
                case ParelContactReceiver.OutputKind.Entered: return "Entered pulse (Bool)";
                case ParelContactReceiver.OutputKind.Exited: return "Exited pulse (Bool)";
                case ParelContactReceiver.OutputKind.Proximity: return "Proximity 0-1 (Float)";
                case ParelContactReceiver.OutputKind.Count: return "How many touch it (Int)";
                case ParelContactReceiver.OutputKind.Taps: return "Taps counter (Int)";
                case ParelContactReceiver.OutputKind.Speed: return "Touch speed 0-1 (Float)";
                case ParelContactReceiver.OutputKind.DirectionX: return "Direction left-right (Float)";
                case ParelContactReceiver.OutputKind.DirectionY: return "Direction down-up (Float)";
                case ParelContactReceiver.OutputKind.DirectionZ: return "Direction back-front (Float)";
                case ParelContactReceiver.OutputKind.FromSelf: return "Touched by me (Bool)";
                default: return "Touched by others (Bool)";
            }
        }

        private static UnityEngine.AnimatorControllerParameterType TypeOf(ParelContactReceiver.OutputKind kind)
        {
            switch (kind)
            {
                case ParelContactReceiver.OutputKind.Proximity:
                case ParelContactReceiver.OutputKind.Speed:
                case ParelContactReceiver.OutputKind.DirectionX:
                case ParelContactReceiver.OutputKind.DirectionY:
                case ParelContactReceiver.OutputKind.DirectionZ: return UnityEngine.AnimatorControllerParameterType.Float;
                case ParelContactReceiver.OutputKind.Count:
                case ParelContactReceiver.OutputKind.Taps: return UnityEngine.AnimatorControllerParameterType.Int;
                default: return UnityEngine.AnimatorControllerParameterType.Bool;
            }
        }

        /// <summary>Adds every parameter the receiver sets to the avatar's FX controller, with the right type, if it is not there yet.</summary>
        private static void AddParametersToFx(ParelContactReceiver receiver)
        {
            ParelAvatarDescriptor descriptor = receiver.GetComponentInParent<ParelAvatarDescriptor>();
            var fx = descriptor != null ? descriptor.FxController as UnityEditor.Animations.AnimatorController : null;
            if (fx == null)
            {
                EditorUtility.DisplayDialog("Contact Receiver", "The avatar has no FX controller yet. Set one under Playable Layers on the Avatar Descriptor first.", "OK");
                return;
            }
            var wanted = new List<(string, UnityEngine.AnimatorControllerParameterType)>();
            if (!string.IsNullOrEmpty(receiver.parameter))
            {
                bool flt = receiver.receiverType == ParelContactReceiver.ReceiverType.Proximity || !Mathf.Approximately(receiver.value, Mathf.Round(receiver.value));
                bool integer = !flt && !Mathf.Approximately(receiver.value, 1f) && !Mathf.Approximately(receiver.value, 0f);
                wanted.Add((receiver.parameter, flt ? UnityEngine.AnimatorControllerParameterType.Float : integer ? UnityEngine.AnimatorControllerParameterType.Int : UnityEngine.AnimatorControllerParameterType.Bool));
            }
            foreach (ParelContactReceiver.Output output in receiver.outputs)
                if (output != null && !string.IsNullOrEmpty(output.parameter)) wanted.Add((output.parameter, TypeOf(output.kind)));
            int added = 0;
            Undo.RecordObject(fx, "Add contact parameters");
            foreach ((string name, UnityEngine.AnimatorControllerParameterType type) in wanted)
            {
                bool exists = false;
                foreach (UnityEngine.AnimatorControllerParameter p in fx.parameters) if (p.name == name) { exists = true; break; }
                if (exists) continue;
                fx.AddParameter(name, type);
                added++;
            }
            EditorUtility.SetDirty(fx);
            AssetDatabase.SaveAssets();
            Debug.Log("[Contacts] Added " + added + " parameter" + (added == 1 ? "" : "s") + " to " + fx.name + ".", fx);
        }

        // ── presets ──────────────────────────────────────────────────────────

        private static void ApplyPreset(ParelContactReceiver receiver, Preset preset)
        {
            Undo.RecordObject(receiver, "Contact preset");
            ParelAvatarDescriptor descriptor = receiver.GetComponentInParent<ParelAvatarDescriptor>();
            Animator animator = descriptor != null ? (descriptor.AvatarAnimator != null ? descriptor.AvatarAnimator : descriptor.GetComponentInChildren<Animator>()) : null;
            Transform Bone(HumanBodyBones bone) => animator != null && animator.isHuman ? animator.GetBoneTransform(bone) : null;
            float scale = descriptor != null ? Mathf.Max(0.01f, descriptor.transform.lossyScale.y) : 1f;
            string stem = Stem(receiver, preset);
            receiver.outputs = new List<ParelContactReceiver.Output>();
            receiver.allowSelf = false;
            receiver.allowOthers = true;
            receiver.rotation = Quaternion.identity;
            receiver.hold = 0.1f;
            switch (preset)
            {
                case Preset.Headpat:
                    receiver.rootTransform = Bone(HumanBodyBones.Head) ?? receiver.rootTransform;
                    receiver.shapeType = ParelContactBase.ShapeType.Sphere;
                    receiver.radius = 0.16f;
                    receiver.position = new Vector3(0f, 0.14f, 0f) / scale;
                    receiver.collisionTags = new List<string> { "Hand", "Finger" };
                    receiver.receiverType = ParelContactReceiver.ReceiverType.Proximity;
                    receiver.smoothing = 0.1f;
                    receiver.outputs.Add(new ParelContactReceiver.Output { kind = ParelContactReceiver.OutputKind.Touching, parameter = stem + "_Touching" });
                    receiver.outputs.Add(new ParelContactReceiver.Output { kind = ParelContactReceiver.OutputKind.Taps, parameter = stem + "_Taps" });
                    break;
                case Preset.Boop:
                    receiver.rootTransform = Bone(HumanBodyBones.Head) ?? receiver.rootTransform;
                    receiver.shapeType = ParelContactBase.ShapeType.Sphere;
                    receiver.radius = 0.03f;
                    receiver.position = new Vector3(0f, 0.06f, 0.1f) / scale;
                    receiver.collisionTags = new List<string> { "FingerIndex" };
                    receiver.receiverType = ParelContactReceiver.ReceiverType.OnEnter;
                    receiver.hold = 0.2f;
                    receiver.outputs.Add(new ParelContactReceiver.Output { kind = ParelContactReceiver.OutputKind.Taps, parameter = stem + "_Taps" });
                    receiver.haptics = 0.4f;
                    break;
                case Preset.Hug:
                    receiver.rootTransform = Bone(HumanBodyBones.Chest) ?? receiver.rootTransform;
                    receiver.shapeType = ParelContactBase.ShapeType.Capsule;
                    receiver.radius = 0.18f;
                    receiver.height = 0.5f;
                    receiver.position = Vector3.zero;
                    receiver.collisionTags = new List<string> { "Hand" };
                    receiver.receiverType = ParelContactReceiver.ReceiverType.Constant;
                    receiver.outputs.Add(new ParelContactReceiver.Output { kind = ParelContactReceiver.OutputKind.Count, parameter = stem + "_Hands" });
                    break;
                case Preset.HighFive:
                    receiver.rootTransform = Bone(HumanBodyBones.RightHand) ?? receiver.rootTransform;
                    receiver.shapeType = ParelContactBase.ShapeType.Sphere;
                    receiver.radius = 0.08f;
                    receiver.position = new Vector3(0f, 0.06f, 0f) / scale;
                    receiver.collisionTags = new List<string> { "Hand" };
                    receiver.receiverType = ParelContactReceiver.ReceiverType.OnEnter;
                    receiver.minVelocity = 0.8f;
                    receiver.hold = 0.25f;
                    receiver.haptics = 0.8f;
                    receiver.outputs.Add(new ParelContactReceiver.Output { kind = ParelContactReceiver.OutputKind.Speed, parameter = stem + "_Speed" });
                    break;
                case Preset.Stomp:
                    receiver.rootTransform = null;
                    receiver.shapeType = ParelContactBase.ShapeType.Box;
                    receiver.size = new Vector3(1f, 0.1f, 1f) / scale;
                    receiver.position = Vector3.zero;
                    receiver.collisionTags = new List<string> { "Foot" };
                    receiver.receiverType = ParelContactReceiver.ReceiverType.OnEnter;
                    receiver.minVelocity = 0.5f;
                    receiver.allowSelf = true;
                    receiver.outputs.Add(new ParelContactReceiver.Output { kind = ParelContactReceiver.OutputKind.Speed, parameter = stem + "_Speed" });
                    break;
                default:
                    receiver.shapeType = ParelContactBase.ShapeType.Sphere;
                    receiver.radius = 0.1f;
                    receiver.collisionTags = new List<string> { "Hand", "Finger" };
                    receiver.receiverType = ParelContactReceiver.ReceiverType.Constant;
                    receiver.allowSelf = true;
                    receiver.outputs.Add(new ParelContactReceiver.Output { kind = ParelContactReceiver.OutputKind.Proximity, parameter = stem + "_Proximity" });
                    break;
            }
            if (string.IsNullOrEmpty(receiver.parameter)) receiver.parameter = stem;
            EditorUtility.SetDirty(receiver);
        }

        private static string Stem(ParelContactReceiver receiver, Preset preset)
        {
            if (!string.IsNullOrEmpty(receiver.parameter)) return receiver.parameter;
            switch (preset)
            {
                case Preset.Headpat: return "Headpat";
                case Preset.Boop: return "Boop";
                case Preset.Hug: return "Hug";
                case Preset.HighFive: return "HighFive";
                case Preset.Stomp: return "Stomp";
                default: return "Touch";
            }
        }

        private void OnSceneGUI()
        {
            var contact = (ParelContactBase)target;
            Transform root = contact.rootTransform != null ? contact.rootTransform : contact.transform;
            float scale = Mathf.Max(Mathf.Abs(root.lossyScale.x), Mathf.Max(Mathf.Abs(root.lossyScale.y), Mathf.Abs(root.lossyScale.z)));
            Vector3 center = root.TransformPoint(contact.position);
            Quaternion rotation = root.rotation * contact.rotation;
            Handles.color = IsReceiver ? new Color(1f, 0.4f, 0.7f, 0.9f) : new Color(0.4f, 1f, 0.6f, 0.9f);
            if (contact.shapeType == ParelContactBase.ShapeType.Box)
            {
                Matrix4x4 previous = Handles.matrix;
                Handles.matrix = Matrix4x4.TRS(center, rotation, Vector3.one);
                Handles.DrawWireCube(Vector3.zero, Vector3.Scale(contact.size, root.lossyScale));
                Handles.matrix = previous;
                return;
            }
            float height = contact.shapeType == ParelContactBase.ShapeType.Capsule ? contact.height * scale : 0f;
            ParelAvatarDescriptorEditor.DrawCapsule(center, rotation, contact.radius * scale, height);
            // Drag the radius in the Scene view.
            EditorGUI.BeginChangeCheck();
            float radius = Handles.RadiusHandle(rotation, center, contact.radius * scale);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(contact, "Contact radius");
                contact.radius = Mathf.Max(0.001f, radius / Mathf.Max(0.0001f, scale));
            }
        }
    }

    // =============================================================================================

    [CustomEditor(typeof(ParelPipelineManager))]
    public sealed class ParelPipelineManagerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var pipeline = (ParelPipelineManager)target;
            serializedObject.Update();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("Blueprint ID", pipeline.HasBlueprint ? pipeline.blueprintId : "(assigned on first publish)");
                }
                using (new EditorGUI.DisabledScope(!pipeline.HasBlueprint))
                {
                    if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(44))) EditorGUIUtility.systemCopyBuffer = pipeline.blueprintId;
                }
            }

            using (new EditorGUI.DisabledScope(!pipeline.HasBlueprint))
            {
                if (GUILayout.Button("Detach (Optional)") &&
                    EditorUtility.DisplayDialog("Detach Blueprint ID", "The next Build & Publish will create a NEW " + pipeline.contentType.ToString().ToLowerInvariant() + " instead of updating this one. Continue?", "Detach", "Cancel"))
                {
                    var descriptor = pipeline.GetComponent<ParelAvatarDescriptor>();
                    if (descriptor != null)
                    {
                        ParelBlueprint.Detach(descriptor);
                    }
                    else
                    {
                        Undo.RecordObject(pipeline, "Detach Blueprint ID");
                        pipeline.Detach();
                        EditorUtility.SetDirty(pipeline);
                    }
                }
            }
            EditorGUILayout.LabelField("Assigned the first time you Build & Publish. Publishing again updates the same content.", EditorStyles.wordWrappedMiniLabel);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
