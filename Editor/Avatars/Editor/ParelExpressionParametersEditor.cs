using System.Collections.Generic;
using ParelVR.AvatarSDK;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Inspectors
{
    /// <summary>
    /// VRChat-style Expression Parameters inspector: one row per parameter (Name, Type, Default,
    /// Saved, Synced) and the synced-memory meter out of 256 bits.
    /// </summary>
    [CustomEditor(typeof(ParelExpressionParameters))]
    public sealed class ParelExpressionParametersEditor : Editor
    {
        private static readonly string[] BuiltInParameters =
        {
            "IsLocal", "VRMode", "AvatarVersion", "IsAnimatorEnabled", "Grounded", "Upright",
            "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "TrackingType", "MuteSelf",
        };

        private static bool _showBuiltIns;

        private SerializedProperty _parameters;
        private ReorderableList _list;
        private readonly HashSet<string> _duplicates = new HashSet<string>();

        private void OnEnable()
        {
            _parameters = serializedObject.FindProperty("parameters");
            _list = new ReorderableList(serializedObject, _parameters, true, true, true, true)
            {
                elementHeight = EditorGUIUtility.singleLineHeight + 6f,
                drawHeaderCallback = DrawHeader,
                drawElementCallback = DrawElement,
                onCanAddCallback = list => _parameters.arraySize < ParelExpressionParameters.MaxParameters,
                onAddCallback = AddParameter,
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var asset = (ParelExpressionParameters)target;

            int cost = asset.CalcTotalCost();
            Rect bar = EditorGUILayout.GetControlRect(false, 20f);
            EditorGUI.ProgressBar(bar, Mathf.Clamp01(cost / (float)ParelExpressionParameters.MaxSyncedBits), $"Total Memory: {cost} / {ParelExpressionParameters.MaxSyncedBits} bits");
            if (cost > ParelExpressionParameters.MaxSyncedBits)
            {
                EditorGUILayout.HelpBox("Too many synced parameters: the avatar won't build. Untick Synced on parameters only you need, or turn Ints/Floats into Bools.", MessageType.Error);
            }
            EditorGUILayout.HelpBox("Bool costs 1 bit, Int and Float cost 8. Saved parameters keep their value between sessions; Synced parameters are sent to everyone in the instance.", MessageType.None);

            CollectDuplicates();
            _list.DoLayoutList();
            if (_duplicates.Count > 0)
            {
                EditorGUILayout.HelpBox("Duplicate names: " + string.Join(", ", _duplicates), MessageType.Error);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Clear Parameters", GUILayout.Width(120)) && _parameters.arraySize > 0 &&
                    EditorUtility.DisplayDialog("Clear Parameters", "Remove every parameter from this asset?", "Clear", "Cancel"))
                {
                    _parameters.ClearArray();
                }
            }

            _showBuiltIns = EditorGUILayout.Foldout(_showBuiltIns, "Built-in Parameters", true);
            if (_showBuiltIns)
            {
                EditorGUILayout.HelpBox("ParelVR fills these in on its own. Add any of them to your FX controller (same name) to use them -- don't add them here.\n\n" + string.Join(", ", BuiltInParameters), MessageType.Info);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void CollectDuplicates()
        {
            _duplicates.Clear();
            var seen = new HashSet<string>();
            for (int i = 0; i < _parameters.arraySize; i++)
            {
                string name = _parameters.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue;
                if (string.IsNullOrEmpty(name)) continue;
                if (!seen.Add(name)) _duplicates.Add(name);
            }
        }

        private static void Columns(Rect rect, out Rect name, out Rect type, out Rect value, out Rect saved, out Rect synced)
        {
            const float gap = 4f;
            float w = rect.width;
            name = new Rect(rect.x, rect.y, w * 0.38f - gap, rect.height);
            type = new Rect(name.xMax + gap, rect.y, w * 0.17f - gap, rect.height);
            value = new Rect(type.xMax + gap, rect.y, w * 0.25f - gap, rect.height);
            saved = new Rect(value.xMax + gap, rect.y, w * 0.10f - gap, rect.height);
            synced = new Rect(saved.xMax + gap, rect.y, w * 0.10f, rect.height);
        }

        private void DrawHeader(Rect rect)
        {
            rect.xMin += 14f; // line up with the rows, which sit right of the drag handle
            Columns(rect, out Rect name, out Rect type, out Rect value, out Rect saved, out Rect synced);
            EditorGUI.LabelField(name, "Name");
            EditorGUI.LabelField(type, "Type");
            EditorGUI.LabelField(value, "Default");
            EditorGUI.LabelField(saved, "Saved");
            EditorGUI.LabelField(synced, "Synced");
        }

        private void DrawElement(Rect rect, int index, bool active, bool focused)
        {
            SerializedProperty element = _parameters.GetArrayElementAtIndex(index);
            SerializedProperty nameProp = element.FindPropertyRelative("name");
            SerializedProperty typeProp = element.FindPropertyRelative("valueType");
            SerializedProperty valueProp = element.FindPropertyRelative("defaultValue");
            SerializedProperty savedProp = element.FindPropertyRelative("saved");
            SerializedProperty syncedProp = element.FindPropertyRelative("networkSynced");

            rect.y += 3f;
            rect.height = EditorGUIUtility.singleLineHeight;
            Columns(rect, out Rect name, out Rect type, out Rect value, out Rect saved, out Rect synced);

            Color previous = GUI.color;
            if (string.IsNullOrEmpty(nameProp.stringValue) || _duplicates.Contains(nameProp.stringValue)) GUI.color = new Color(1f, 0.55f, 0.55f);
            nameProp.stringValue = EditorGUI.TextField(name, nameProp.stringValue);
            GUI.color = previous;

            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(type, typeProp, GUIContent.none);
            var valueType = (ParelExpressionParameters.ValueType)typeProp.enumValueIndex;
            if (EditorGUI.EndChangeCheck())
            {
                valueProp.floatValue = ParelExpressionParameters.Sanitize(valueType, valueProp.floatValue);
            }

            switch (valueType)
            {
                case ParelExpressionParameters.ValueType.Bool:
                    valueProp.floatValue = EditorGUI.Toggle(value, valueProp.floatValue >= 0.5f) ? 1f : 0f;
                    break;
                case ParelExpressionParameters.ValueType.Int:
                    valueProp.floatValue = Mathf.Clamp(EditorGUI.IntField(value, Mathf.RoundToInt(valueProp.floatValue)), 0, 255);
                    break;
                default:
                    valueProp.floatValue = EditorGUI.Slider(value, valueProp.floatValue, -1f, 1f);
                    break;
            }

            savedProp.boolValue = EditorGUI.Toggle(saved, savedProp.boolValue);
            syncedProp.boolValue = EditorGUI.Toggle(synced, syncedProp.boolValue);
        }

        private void AddParameter(ReorderableList list)
        {
            int index = _parameters.arraySize;
            _parameters.arraySize++;
            // Growing a serialized list copies the last entry; start the new one clean.
            SerializedProperty element = _parameters.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("name").stringValue = string.Empty;
            element.FindPropertyRelative("valueType").enumValueIndex = (int)ParelExpressionParameters.ValueType.Bool;
            element.FindPropertyRelative("defaultValue").floatValue = 0f;
            element.FindPropertyRelative("saved").boolValue = true;
            element.FindPropertyRelative("networkSynced").boolValue = true;
            list.index = index;
        }
    }
}
