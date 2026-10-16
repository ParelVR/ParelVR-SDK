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

    public abstract class ParelContactEditorBase : Editor
    {
        /// <summary>The tags ParelVR's automatic avatar colliders send.</summary>
        private static readonly string[] BuiltInTags =
        {
            "Head", "Torso", "Hand", "HandL", "HandR", "Foot", "FootL", "FootR",
            "Finger", "FingerL", "FingerR", "FingerIndex", "FingerIndexL", "FingerIndexR",
            "FingerMiddle", "FingerMiddleL", "FingerMiddleR", "FingerRing", "FingerRingL", "FingerRingR",
            "FingerLittle", "FingerLittleL", "FingerLittleR",
        };

        protected abstract bool IsReceiver { get; }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DynamicsGUI.Section("Shape");
            DynamicsGUI.Prop(serializedObject, "rootTransform", "Root Transform");
            SerializedProperty shape = serializedObject.FindProperty("shapeType");
            EditorGUILayout.PropertyField(shape, new GUIContent("Shape Type"));
            DynamicsGUI.Prop(serializedObject, "radius", "Radius");
            if (shape.enumValueIndex == (int)ParelContactBase.ShapeType.Capsule) DynamicsGUI.Prop(serializedObject, "height", "Height");
            DynamicsGUI.Prop(serializedObject, "position", "Position");
            SerializedProperty rotation = serializedObject.FindProperty("rotation");
            EditorGUI.BeginChangeCheck();
            Vector3 euler = EditorGUILayout.Vector3Field("Rotation", rotation.quaternionValue.eulerAngles);
            if (EditorGUI.EndChangeCheck()) rotation.quaternionValue = Quaternion.Euler(euler);

            DynamicsGUI.Section("Filtering");
            if (IsReceiver)
            {
                DynamicsGUI.Prop(serializedObject, "allowSelf", "Allow Self");
                DynamicsGUI.Prop(serializedObject, "allowOthers", "Allow Others");
            }
            DynamicsGUI.Prop(serializedObject, "localOnly", "Local Only");
            SerializedProperty tags = serializedObject.FindProperty("collisionTags");
            EditorGUILayout.PropertyField(tags, new GUIContent("Collision Tags"), true);
            if (GUILayout.Button("Add Built-in Tag ▾", EditorStyles.miniButton, GUILayout.Width(130))) ShowTagMenu(tags);

            if (IsReceiver)
            {
                DynamicsGUI.Section("Receiver");
                SerializedProperty type = serializedObject.FindProperty("receiverType");
                EditorGUILayout.PropertyField(type, new GUIContent("Receiver Type"));
                DynamicsGUI.Prop(serializedObject, "parameter", "Parameter");
                if (type.enumValueIndex == (int)ParelContactReceiver.ReceiverType.OnEnter) DynamicsGUI.Prop(serializedObject, "minVelocity", "Min Velocity");
                string kind = type.enumValueIndex == (int)ParelContactReceiver.ReceiverType.Proximity ? "a Float (0 at the edge, 1 at the center)" : "a Bool";
                EditorGUILayout.LabelField("The parameter is set as " + kind + ".", EditorStyles.miniLabel);
            }

            serializedObject.ApplyModifiedProperties();

            if (!serializedObject.isEditingMultipleObjects && target is ParelContactReceiver receiver && Application.isPlaying)
            {
                EditorGUILayout.LabelField("Current value", receiver.CurrentValue.ToString("0.00"));
                Repaint();
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

        private void OnSceneGUI()
        {
            var contact = (ParelContactBase)target;
            Transform root = contact.rootTransform != null ? contact.rootTransform : contact.transform;
            float scale = Mathf.Max(Mathf.Abs(root.lossyScale.x), Mathf.Max(Mathf.Abs(root.lossyScale.y), Mathf.Abs(root.lossyScale.z)));
            Vector3 center = root.TransformPoint(contact.position);
            Quaternion rotation = root.rotation * contact.rotation;
            Handles.color = IsReceiver ? new Color(1f, 0.4f, 0.7f, 0.9f) : new Color(0.4f, 1f, 0.6f, 0.9f);
            float height = contact.shapeType == ParelContactBase.ShapeType.Capsule ? contact.height * scale : 0f;
            ParelAvatarDescriptorEditor.DrawCapsule(center, rotation, contact.radius * scale, height);
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
