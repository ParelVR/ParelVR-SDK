using System.Collections.Generic;
using System.Linq;
using ParelVR.AvatarSDK;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Avatars.Inspectors
{
    /// <summary>Finds the parameters a state behaviour can drive: the avatar's Expression Parameters, its controller's parameters and the built-ins.</summary>
    internal static class BehaviourParameters
    {
        public enum Kind
        {
            Unknown,
            Bool,
            Int,
            Float,
        }

        public static Dictionary<string, Kind> For(Object behaviour)
        {
            var result = new Dictionary<string, Kind>();
            string path = AssetDatabase.GetAssetPath(behaviour);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller != null)
            {
                foreach (AnimatorControllerParameter p in controller.parameters)
                {
                    result[p.name] = p.type == AnimatorControllerParameterType.Bool || p.type == AnimatorControllerParameterType.Trigger ? Kind.Bool
                        : p.type == AnimatorControllerParameterType.Int ? Kind.Int : Kind.Float;
                }
            }

            foreach (ParelAvatarDescriptor descriptor in DescriptorsUsing(path))
            {
                ParelExpressionParameters parameters = descriptor.ExpressionParameters;
                if (parameters?.parameters == null) continue;
                foreach (ParelExpressionParameters.Parameter p in parameters.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    result[p.name] = p.valueType == ParelExpressionParameters.ValueType.Bool ? Kind.Bool
                        : p.valueType == ParelExpressionParameters.ValueType.Int ? Kind.Int : Kind.Float;
                }
            }
            return result;
        }

        private static IEnumerable<ParelAvatarDescriptor> DescriptorsUsing(string controllerPath)
        {
            if (string.IsNullOrEmpty(controllerPath)) yield break;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (ParelAvatarDescriptor descriptor in root.GetComponentsInChildren<ParelAvatarDescriptor>(true))
                    {
                        foreach (ParelAvatarLayer layer in ParelAvatarDescriptor.BaseLayerTypes.Concat(ParelAvatarDescriptor.SpecialLayerTypes))
                        {
                            RuntimeAnimatorController controller = descriptor.GetLayer(layer);
                            if (controller != null && AssetDatabase.GetAssetPath(controller) == controllerPath)
                            {
                                yield return descriptor;
                                break;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>A text field with a dropdown of known parameters next to it.</summary>
        public static void Field(SerializedProperty name, string label, Dictionary<string, Kind> known)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(name, new GUIContent(label));
                if (known.Count == 0) return;
                if (GUILayout.Button("▾", EditorStyles.miniButton, GUILayout.Width(22)))
                {
                    var menu = new GenericMenu();
                    SerializedObject so = name.serializedObject;
                    string path = name.propertyPath;
                    foreach (KeyValuePair<string, Kind> entry in known.OrderBy(k => k.Key))
                    {
                        string captured = entry.Key;
                        menu.AddItem(new GUIContent($"{entry.Key}  ({entry.Value})"), entry.Key == name.stringValue, () =>
                        {
                            so.Update();
                            so.FindProperty(path).stringValue = captured;
                            so.ApplyModifiedProperties();
                        });
                    }
                    menu.ShowAsContext();
                }
            }
        }
    }

    // =============================================================================================

    [CustomEditor(typeof(ParelAvatarParameterDriver))]
    public sealed class ParelAvatarParameterDriverEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            Dictionary<string, BehaviourParameters.Kind> known = BehaviourParameters.For(target);

            EditorGUILayout.PropertyField(serializedObject.FindProperty("localOnly"), new GUIContent("Local Only", "Only run for the avatar's wearer. Synced results still reach everyone."));
            EditorGUILayout.Space(4);

            SerializedProperty list = serializedObject.FindProperty("parameters");
            int remove = -1;
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty entry = list.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginVertical(GUI.skin.box);
                using (new EditorGUILayout.HorizontalScope())
                {
                    SerializedProperty type = entry.FindPropertyRelative("type");
                    EditorGUILayout.PropertyField(type, new GUIContent("Type"));
                    if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(56))) remove = i;
                }

                SerializedProperty name = entry.FindPropertyRelative("name");
                BehaviourParameters.Field(name, "Destination", known);
                known.TryGetValue(name.stringValue, out BehaviourParameters.Kind kind);

                switch ((ParelAvatarParameterDriver.ChangeType)entry.FindPropertyRelative("type").enumValueIndex)
                {
                    case ParelAvatarParameterDriver.ChangeType.Set:
                        Value(entry.FindPropertyRelative("value"), "Value", kind);
                        break;
                    case ParelAvatarParameterDriver.ChangeType.Add:
                        Value(entry.FindPropertyRelative("value"), "Value", kind == BehaviourParameters.Kind.Bool ? BehaviourParameters.Kind.Int : kind);
                        break;
                    case ParelAvatarParameterDriver.ChangeType.Random:
                        if (kind == BehaviourParameters.Kind.Bool)
                        {
                            SerializedProperty chance = entry.FindPropertyRelative("chance");
                            EditorGUILayout.Slider(chance, 0f, 1f, new GUIContent("Chance"));
                        }
                        else
                        {
                            Value(entry.FindPropertyRelative("valueMin"), "Min Value", kind);
                            Value(entry.FindPropertyRelative("valueMax"), "Max Value", kind);
                            if (kind == BehaviourParameters.Kind.Int || kind == BehaviourParameters.Kind.Unknown)
                            {
                                EditorGUILayout.PropertyField(entry.FindPropertyRelative("preventRepeats"), new GUIContent("Prevent Repeats"));
                            }
                        }
                        break;
                    case ParelAvatarParameterDriver.ChangeType.Copy:
                        BehaviourParameters.Field(entry.FindPropertyRelative("source"), "Source", known);
                        SerializedProperty convert = entry.FindPropertyRelative("convertRange");
                        EditorGUILayout.PropertyField(convert, new GUIContent("Convert Range"));
                        if (convert.boolValue)
                        {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.PropertyField(entry.FindPropertyRelative("sourceMin"), new GUIContent("Source Min"));
                            EditorGUILayout.PropertyField(entry.FindPropertyRelative("sourceMax"), new GUIContent("Source Max"));
                            EditorGUILayout.PropertyField(entry.FindPropertyRelative("destMin"), new GUIContent("Destination Min"));
                            EditorGUILayout.PropertyField(entry.FindPropertyRelative("destMax"), new GUIContent("Destination Max"));
                            EditorGUI.indentLevel--;
                        }
                        break;
                }
                if (!string.IsNullOrEmpty(name.stringValue) && known.Count > 0 && !known.ContainsKey(name.stringValue) && !ParelAvatarBuiltinParameters.IsBuiltin(name.stringValue))
                {
                    EditorGUILayout.HelpBox($"'{name.stringValue}' isn't in this avatar's Expression Parameters or this controller's parameters.", MessageType.Warning);
                }
                if (ParelAvatarBuiltinParameters.IsBuiltin(name.stringValue))
                {
                    EditorGUILayout.HelpBox($"'{name.stringValue}' is a built-in parameter; ParelVR sets it, so changes here are ignored.", MessageType.Warning);
                }
                EditorGUILayout.EndVertical();
            }
            if (remove >= 0) list.DeleteArrayElementAtIndex(remove);

            if (GUILayout.Button("Add Parameter"))
            {
                list.arraySize++;
                SerializedProperty added = list.GetArrayElementAtIndex(list.arraySize - 1);
                added.FindPropertyRelative("name").stringValue = string.Empty;
                added.FindPropertyRelative("source").stringValue = string.Empty;
                added.FindPropertyRelative("type").enumValueIndex = 0;
                added.FindPropertyRelative("value").floatValue = 0f;
                added.FindPropertyRelative("valueMin").floatValue = 0f;
                added.FindPropertyRelative("valueMax").floatValue = 1f;
                added.FindPropertyRelative("chance").floatValue = 0.5f;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("debugString"), new GUIContent("Debug String"));
            serializedObject.ApplyModifiedProperties();
        }

        private static void Value(SerializedProperty value, string label, BehaviourParameters.Kind kind)
        {
            switch (kind)
            {
                case BehaviourParameters.Kind.Bool:
                    value.floatValue = EditorGUILayout.Toggle(label, value.floatValue >= 0.5f) ? 1f : 0f;
                    break;
                case BehaviourParameters.Kind.Int:
                    value.floatValue = Mathf.Clamp(EditorGUILayout.IntField(label, Mathf.RoundToInt(value.floatValue)), 0, 255);
                    break;
                default:
                    EditorGUILayout.PropertyField(value, new GUIContent(label));
                    break;
            }
        }
    }

    // =============================================================================================

    [CustomEditor(typeof(ParelAnimatorTrackingControl))]
    public sealed class ParelAnimatorTrackingControlEditor : Editor
    {
        private static readonly (string field, string label)[] Parts =
        {
            ("trackingHead", "Head"), ("trackingLeftHand", "Left Hand"), ("trackingRightHand", "Right Hand"),
            ("trackingHip", "Hip"), ("trackingLeftFoot", "Left Foot"), ("trackingRightFoot", "Right Foot"),
            ("trackingLeftFingers", "Left Fingers"), ("trackingRightFingers", "Right Fingers"),
            ("trackingEyes", "Eyes & Eyelids"), ("trackingMouth", "Mouth & Jaw"),
        };

        private static readonly string[] Options = { "No Change", "Tracking", "Animation" };

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("All", GUILayout.Width(EditorGUIUtility.labelWidth - 4));
                for (int option = 0; option < Options.Length; option++)
                {
                    if (GUILayout.Button(Options[option], EditorStyles.miniButton)) SetAll(option);
                }
            }
            EditorGUILayout.Space(2);
            foreach ((string field, string label) in Parts)
            {
                SerializedProperty property = serializedObject.FindProperty(field);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth - 4));
                    property.enumValueIndex = GUILayout.Toolbar(property.enumValueIndex, Options, EditorStyles.miniButton);
                }
            }
            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("debugString"), new GUIContent("Debug String"));
            serializedObject.ApplyModifiedProperties();
        }

        private void SetAll(int option)
        {
            foreach ((string field, string _) in Parts) serializedObject.FindProperty(field).enumValueIndex = option;
        }
    }
}
