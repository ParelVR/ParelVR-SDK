using System.Collections.Generic;
using ParelVR.AvatarSDK;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Inspectors
{
    /// <summary>
    /// VRChat-style Expressions Menu inspector: up to 8 controls per page, each with its type-specific
    /// fields (parameter + value, sub menu, puppet axes and direction labels). Parameters are picked
    /// from the Expression Parameters of the avatar in the scene that uses this menu.
    /// </summary>
    [CustomEditor(typeof(ParelExpressionsMenu))]
    public sealed class ParelExpressionsMenuEditor : Editor
    {
        private static readonly string[] TwoAxisNames = { "Horizontal", "Vertical" };
        private static readonly string[] FourAxisNames = { "Up", "Right", "Down", "Left" };
        private static readonly string[] RadialNames = { "Rotation" };
        private static readonly string[] LabelNames = { "Up", "Right", "Down", "Left" };

        private SerializedProperty _controls;
        private ParelExpressionParameters _parameters;
        private ParelAvatarDescriptor _owner;
        private readonly Dictionary<int, bool> _expanded = new Dictionary<int, bool>();

        private void OnEnable()
        {
            _controls = serializedObject.FindProperty("controls");
            FindOwner();
        }

        /// <summary>Finds the avatar in the open scenes whose menu tree includes this page.</summary>
        private void FindOwner()
        {
            var menu = (ParelExpressionsMenu)target;
            ParelAvatarDescriptor[] descriptors = Object.FindObjectsByType<ParelAvatarDescriptor>(FindObjectsInactive.Include);
            foreach (ParelAvatarDescriptor descriptor in descriptors)
            {
                if (descriptor.ExpressionsMenu == null) continue;
                if (!MenuContains(descriptor.ExpressionsMenu, menu, new HashSet<ParelExpressionsMenu>())) continue;
                _owner = descriptor;
                if (_parameters == null) _parameters = descriptor.ExpressionParameters;
                return;
            }
        }

        private static bool MenuContains(ParelExpressionsMenu root, ParelExpressionsMenu menu, HashSet<ParelExpressionsMenu> visited)
        {
            if (root == null || !visited.Add(root)) return false;
            if (root == menu) return true;
            if (root.controls == null) return false;
            foreach (ParelExpressionsMenu.Control control in root.controls)
            {
                if (control != null && control.type == ParelExpressionsMenu.ControlType.SubMenu && MenuContains(control.subMenu, menu, visited)) return true;
            }
            return false;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            _parameters = (ParelExpressionParameters)EditorGUILayout.ObjectField("Parameters", _parameters, typeof(ParelExpressionParameters), false);
            if (_owner != null)
            {
                EditorGUILayout.LabelField("Used by", _owner.name, EditorStyles.miniLabel);
            }
            else if (_parameters == null)
            {
                EditorGUILayout.HelpBox("This menu isn't on an avatar in the open scenes. Assign its Expression Parameters above to pick parameters from a list.", MessageType.Info);
            }

            int count = _controls.arraySize;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Controls  {count} / {ParelExpressionsMenu.MaxControls}", EditorStyles.boldLabel);
            if (count > ParelExpressionsMenu.MaxControls)
            {
                EditorGUILayout.HelpBox($"A page holds at most {ParelExpressionsMenu.MaxControls} controls. Move the extras into a Sub Menu.", MessageType.Error);
            }

            for (int i = 0; i < _controls.arraySize; i++)
            {
                if (!DrawControl(i)) break; // the list changed (moved / removed); redraw next frame
            }

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(_controls.arraySize >= ParelExpressionsMenu.MaxControls))
            {
                if (GUILayout.Button("Add Control", GUILayout.Height(24))) AddControl();
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <returns>false when the control list was reordered or shortened.</returns>
        private bool DrawControl(int index)
        {
            SerializedProperty control = _controls.GetArrayElementAtIndex(index);
            SerializedProperty nameProp = control.FindPropertyRelative("name");
            SerializedProperty iconProp = control.FindPropertyRelative("icon");
            SerializedProperty typeProp = control.FindPropertyRelative("type");
            SerializedProperty parameterProp = control.FindPropertyRelative("parameter").FindPropertyRelative("name");
            SerializedProperty valueProp = control.FindPropertyRelative("value");
            SerializedProperty subMenuProp = control.FindPropertyRelative("subMenu");
            SerializedProperty subParametersProp = control.FindPropertyRelative("subParameters");
            SerializedProperty labelsProp = control.FindPropertyRelative("labels");
            var type = (ParelExpressionsMenu.ControlType)typeProp.intValue;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool open = !_expanded.TryGetValue(index, out bool expanded) || expanded;
                    open = EditorGUILayout.Foldout(open, $"{nameProp.stringValue}   ({ObjectNames.NicifyVariableName(type.ToString())})", true);
                    _expanded[index] = open;

                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(index == 0))
                    {
                        if (GUILayout.Button("Up", EditorStyles.miniButtonLeft, GUILayout.Width(36)))
                        {
                            _controls.MoveArrayElement(index, index - 1);
                            SwapExpanded(index, index - 1);
                            return false;
                        }
                    }
                    using (new EditorGUI.DisabledScope(index == _controls.arraySize - 1))
                    {
                        if (GUILayout.Button("Down", EditorStyles.miniButtonMid, GUILayout.Width(44)))
                        {
                            _controls.MoveArrayElement(index, index + 1);
                            SwapExpanded(index, index + 1);
                            return false;
                        }
                    }
                    if (GUILayout.Button("Remove", EditorStyles.miniButtonRight, GUILayout.Width(56)))
                    {
                        _controls.DeleteArrayElementAtIndex(index);
                        _expanded.Clear();
                        return false;
                    }

                    if (!open) return true;
                }

                EditorGUILayout.PropertyField(nameProp, new GUIContent("Name"));
                iconProp.objectReferenceValue = EditorGUILayout.ObjectField("Icon", iconProp.objectReferenceValue, typeof(Texture2D), false, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.PropertyField(typeProp, new GUIContent("Type"));
                type = (ParelExpressionsMenu.ControlType)typeProp.intValue;

                ParameterField(new GUIContent("Parameter", ParameterTooltip(type)), parameterProp);
                if (!string.IsNullOrEmpty(parameterProp.stringValue)) ValueField(valueProp, parameterProp.stringValue);

                switch (type)
                {
                    case ParelExpressionsMenu.ControlType.SubMenu:
                        EditorGUILayout.PropertyField(subMenuProp, new GUIContent("Sub Menu"));
                        if (subMenuProp.objectReferenceValue == target)
                        {
                            EditorGUILayout.HelpBox("A Sub Menu that opens its own page goes nowhere. Pick a different menu.", MessageType.Warning);
                        }
                        break;
                    case ParelExpressionsMenu.ControlType.TwoAxisPuppet:
                        SubParameters(subParametersProp, TwoAxisNames);
                        Labels(labelsProp);
                        break;
                    case ParelExpressionsMenu.ControlType.FourAxisPuppet:
                        SubParameters(subParametersProp, FourAxisNames);
                        Labels(labelsProp);
                        break;
                    case ParelExpressionsMenu.ControlType.RadialPuppet:
                        SubParameters(subParametersProp, RadialNames);
                        break;
                }

                if (type != ParelExpressionsMenu.ControlType.SubMenu && subMenuProp.objectReferenceValue != null) subMenuProp.objectReferenceValue = null;
                if (type != ParelExpressionsMenu.ControlType.TwoAxisPuppet && type != ParelExpressionsMenu.ControlType.FourAxisPuppet && labelsProp.arraySize > 0) labelsProp.arraySize = 0;
                if (ParelExpressionsMenu.Control.SubParameterCount(type) == 0 && subParametersProp.arraySize > 0) subParametersProp.arraySize = 0;
            }
            return true;
        }

        private static string ParameterTooltip(ParelExpressionsMenu.ControlType type)
        {
            switch (type)
            {
                case ParelExpressionsMenu.ControlType.Button: return "Set to Value while the button is held.";
                case ParelExpressionsMenu.ControlType.Toggle: return "Set to Value while the toggle is on.";
                case ParelExpressionsMenu.ControlType.SubMenu: return "Optional: set to Value while the sub menu is open.";
                default: return "Optional: set to Value while the puppet is open.";
            }
        }

        private void ParameterField(GUIContent label, SerializedProperty nameProp)
        {
            if (_parameters == null || _parameters.parameters == null)
            {
                nameProp.stringValue = EditorGUILayout.TextField(label, nameProp.stringValue);
                return;
            }

            var names = new List<string> { "[None]" };
            var display = new List<GUIContent> { new GUIContent("[None]") };
            foreach (ParelExpressionParameters.Parameter p in _parameters.parameters)
            {
                if (p == null || string.IsNullOrEmpty(p.name) || names.Contains(p.name)) continue;
                names.Add(p.name);
                display.Add(new GUIContent($"{p.name}  ({p.valueType})"));
            }

            string current = nameProp.stringValue;
            int index = string.IsNullOrEmpty(current) ? 0 : names.IndexOf(current);
            if (index < 0)
            {
                names.Add(current);
                display.Add(new GUIContent($"{current}  (not in Expression Parameters)"));
                index = names.Count - 1;
            }

            int picked = EditorGUILayout.Popup(label, index, display.ToArray());
            if (picked != index) nameProp.stringValue = picked == 0 ? string.Empty : names[picked];
        }

        private void ValueField(SerializedProperty valueProp, string parameterName)
        {
            ParelExpressionParameters.Parameter definition = _parameters != null ? _parameters.FindParameter(parameterName) : null;
            if (definition == null)
            {
                valueProp.floatValue = EditorGUILayout.FloatField("Value", valueProp.floatValue);
                return;
            }

            switch (definition.valueType)
            {
                case ParelExpressionParameters.ValueType.Bool:
                    valueProp.floatValue = EditorGUILayout.Toggle("Value", valueProp.floatValue >= 0.5f) ? 1f : 0f;
                    break;
                case ParelExpressionParameters.ValueType.Int:
                    valueProp.floatValue = EditorGUILayout.IntSlider("Value", Mathf.RoundToInt(valueProp.floatValue), 0, 255);
                    break;
                default:
                    valueProp.floatValue = EditorGUILayout.Slider("Value", valueProp.floatValue, -1f, 1f);
                    break;
            }
        }

        private void SubParameters(SerializedProperty array, string[] slotNames)
        {
            if (array.arraySize != slotNames.Length)
            {
                int old = array.arraySize;
                array.arraySize = slotNames.Length;
                for (int i = old; i < slotNames.Length; i++) array.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue = string.Empty;
            }

            EditorGUILayout.LabelField("Puppet Parameters", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            for (int i = 0; i < slotNames.Length; i++)
            {
                ParameterField(new GUIContent(slotNames[i]), array.GetArrayElementAtIndex(i).FindPropertyRelative("name"));
            }
            EditorGUI.indentLevel--;
        }

        private static void Labels(SerializedProperty array)
        {
            if (array.arraySize != LabelNames.Length)
            {
                int old = array.arraySize;
                array.arraySize = LabelNames.Length;
                for (int i = old; i < LabelNames.Length; i++)
                {
                    SerializedProperty label = array.GetArrayElementAtIndex(i);
                    label.FindPropertyRelative("name").stringValue = string.Empty;
                    label.FindPropertyRelative("icon").objectReferenceValue = null;
                }
            }

            EditorGUILayout.LabelField("Direction Labels", EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            for (int i = 0; i < LabelNames.Length; i++)
            {
                SerializedProperty label = array.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(label.FindPropertyRelative("name"), new GUIContent(LabelNames[i]));
                    SerializedProperty icon = label.FindPropertyRelative("icon");
                    icon.objectReferenceValue = EditorGUILayout.ObjectField(icon.objectReferenceValue, typeof(Texture2D), false, GUILayout.Width(120));
                }
            }
            EditorGUI.indentLevel--;
        }

        private void AddControl()
        {
            int index = _controls.arraySize;
            _controls.arraySize++;
            // Growing a serialized list copies the last entry; start the new one clean.
            SerializedProperty control = _controls.GetArrayElementAtIndex(index);
            control.FindPropertyRelative("name").stringValue = "New Control";
            control.FindPropertyRelative("icon").objectReferenceValue = null;
            control.FindPropertyRelative("type").intValue = (int)ParelExpressionsMenu.ControlType.Toggle;
            control.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = string.Empty;
            control.FindPropertyRelative("value").floatValue = 1f;
            control.FindPropertyRelative("subMenu").objectReferenceValue = null;
            control.FindPropertyRelative("subParameters").arraySize = 0;
            control.FindPropertyRelative("labels").arraySize = 0;
            _expanded[index] = true;
        }

        private void SwapExpanded(int a, int b)
        {
            bool ea = !_expanded.TryGetValue(a, out bool va) || va;
            bool eb = !_expanded.TryGetValue(b, out bool vb) || vb;
            _expanded[a] = eb;
            _expanded[b] = ea;
        }
    }
}
