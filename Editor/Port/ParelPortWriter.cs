using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Port
{
    /// <summary>What the writer needs from the porter: references made to point at the right thing in ParelVR.</summary>
    internal interface IPortReferences
    {
        /// <summary>
        /// The object a reference should point at now: the ParelVR replacement of a ported component, a converted asset,
        /// or the object itself. <paramref name="expectedType"/> is the field's type name. Null when it cannot be found.
        /// </summary>
        UnityEngine.Object Resolve(PortValue reference, string expectedType);
    }

    /// <summary>
    /// Copies settings read from another platform's component onto a ParelVR component through Unity's serialized
    /// properties, by field name: numbers, switches, text, choices (by name where it can, by value through the
    /// rule's tables otherwise), vectors, rotations, colours, curves, references, lists and nested settings.
    /// </summary>
    internal static class PortWriter
    {
        private static readonly HashSet<string> NeverWritten = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
            "m_Enabled", "m_Script", "m_Name", "m_EditorHideFlags", "m_EditorClassIdentifier"
        };

        /// <summary>Every top-level setting of <paramref name="target"/> that the source has (or the rule names a source for).</summary>
        public static void CopyInto(UnityEngine.Object target, PortValue source, PortRule rule, IPortReferences refs)
        {
            if (target == null || source == null || source.K != PortValue.Kind.Map) return;
            using (var so = new SerializedObject(target))
            {
                SerializedProperty it = so.GetIterator();
                if (!it.Next(true)) return;
                do
                {
                    if (NeverWritten.Contains(it.name)) continue;
                    string key = PortValue.Norm(it.name);
                    if (rule != null && rule.Skip.Contains(key)) continue;
                    PortValue value = PortValue.Null;
                    if (rule != null && rule.Aliases.TryGetValue(key, out string[] from))
                    {
                        foreach (string name in from)
                        {
                            value = source.Get(name);
                            if (!value.IsNull) break;
                        }
                    }
                    if (value.IsNull) value = source.Get(it.name);
                    if (value.IsNull) continue;
                    Write(it.Copy(), value, rule, refs, 0);
                } while (it.Next(false));
                so.ApplyModifiedProperties();
            }
            EditorUtility.SetDirty(target);
        }

        /// <summary>Sets one named field, for the rules' own wiring.</summary>
        public static void Set(UnityEngine.Object target, string field, PortValue value, PortRule rule, IPortReferences refs)
        {
            if (target == null || value == null || value.IsNull) return;
            using (var so = new SerializedObject(target))
            {
                SerializedProperty p = so.FindProperty(field);
                if (p == null) return;
                Write(p, value, rule, refs, 0);
                so.ApplyModifiedProperties();
            }
            EditorUtility.SetDirty(target);
        }

        public static void Write(SerializedProperty p, PortValue v, PortRule rule, IPortReferences refs, int depth)
        {
            if (v == null || depth > 24) return;
            if (v.IsNull && p.propertyType != SerializedPropertyType.ObjectReference) return;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    if (v.K == PortValue.Kind.Map || v.K == PortValue.Kind.List) return;
                    p.longValue = (long)Math.Round(v.AsNumber());
                    break;
                case SerializedPropertyType.Boolean:
                    if (v.K == PortValue.Kind.Map || v.K == PortValue.Kind.List) return;
                    p.boolValue = v.AsBool();
                    break;
                case SerializedPropertyType.Float:
                    if (v.K == PortValue.Kind.Map || v.K == PortValue.Kind.List) return;
                    p.doubleValue = v.AsNumber();
                    break;
                case SerializedPropertyType.String:
                {
                    string s = v.K == PortValue.Kind.List && v.List.Count > 0 ? v.List[0].AsString() : v.AsString();
                    if (s != null) p.stringValue = s;
                    break;
                }
                case SerializedPropertyType.LayerMask:
                {
                    double bits = v.K == PortValue.Kind.Map ? v.Get("Bits").AsNumber(-1) : v.AsNumber(-1);
                    p.intValue = unchecked((int)(uint)(long)bits);
                    break;
                }
                case SerializedPropertyType.Enum:
                    WriteEnum(p, v, rule);
                    break;
                case SerializedPropertyType.ObjectReference:
                    WriteReference(p, v, refs);
                    break;
                case SerializedPropertyType.Color:
                    if (v.K == PortValue.Kind.Map) p.colorValue = new Color(F(v, "r"), F(v, "g"), F(v, "b"), F(v, "a", 1f));
                    break;
                case SerializedPropertyType.Vector2:
                    if (v.K == PortValue.Kind.Map) p.vector2Value = new Vector2(F(v, "x"), F(v, "y"));
                    break;
                case SerializedPropertyType.Vector3:
                    if (v.K != PortValue.Kind.Map) return;
                    // A rotation stored the other way round (a quaternion where angles are wanted) is turned into angles.
                    p.vector3Value = v.Has("w") ? Rotation(v).eulerAngles : new Vector3(F(v, "x"), F(v, "y"), F(v, "z"));
                    break;
                case SerializedPropertyType.Vector4:
                    if (v.K == PortValue.Kind.Map) p.vector4Value = new Vector4(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w"));
                    break;
                case SerializedPropertyType.Quaternion:
                    if (v.K != PortValue.Kind.Map) return;
                    p.quaternionValue = v.Has("w") ? Rotation(v) : Quaternion.Euler(F(v, "x"), F(v, "y"), F(v, "z"));
                    break;
                case SerializedPropertyType.Vector2Int:
                    if (v.K == PortValue.Kind.Map) p.vector2IntValue = new Vector2Int((int)F(v, "x"), (int)F(v, "y"));
                    break;
                case SerializedPropertyType.Vector3Int:
                    if (v.K == PortValue.Kind.Map) p.vector3IntValue = new Vector3Int((int)F(v, "x"), (int)F(v, "y"), (int)F(v, "z"));
                    break;
                case SerializedPropertyType.Rect:
                    if (v.K == PortValue.Kind.Map) p.rectValue = new Rect(F(v, "x"), F(v, "y"), F(v, "width"), F(v, "height"));
                    break;
                case SerializedPropertyType.AnimationCurve:
                {
                    AnimationCurve curve = v.K == PortValue.Kind.Curve ? v.Curve : v.K == PortValue.Kind.Map ? Curve(v) : null;
                    if (curve != null) p.animationCurveValue = new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
                    break;
                }
                case SerializedPropertyType.Generic:
                    if (p.isArray) WriteArray(p, v, rule, refs, depth);
                    else if (v.K == PortValue.Kind.Map) WriteChildren(p, v, rule, refs, depth);
                    break;
            }
        }

        private static void WriteChildren(SerializedProperty p, PortValue v, PortRule rule, IPortReferences refs, int depth)
        {
            SerializedProperty child = p.Copy();
            SerializedProperty end = p.GetEndProperty();
            if (!child.Next(true)) return;
            while (!SerializedProperty.EqualContents(child, end))
            {
                string key = PortValue.Norm(child.name);
                PortValue value = v.Get(child.name);
                if (value.IsNull && rule != null && rule.Aliases.TryGetValue(key, out string[] from))
                    foreach (string name in from) { value = v.Get(name); if (!value.IsNull) break; }
                if (!value.IsNull) Write(child.Copy(), value, rule, refs, depth + 1);
                if (!child.Next(false)) break;
            }
        }

        private static void WriteArray(SerializedProperty p, PortValue v, PortRule rule, IPortReferences refs, int depth)
        {
            List<PortValue> items;
            if (v.K == PortValue.Kind.List) items = v.List;
            else if (v.K == PortValue.Kind.String && IsHexNumbers(v.Text, p.arrayElementType)) items = Hex(v.Text);
            else items = new List<PortValue> { v };   // one value where a list is wanted: a list of one
            int count = Math.Min(items.Count, 4096);
            p.arraySize = count;
            for (int i = 0; i < count; i++) Write(p.GetArrayElementAtIndex(i), items[i], rule, refs, depth + 1);
        }

        // Unity writes some lists of numbers as one hex string, four bytes each, lowest byte first.
        private static bool IsHexNumbers(string text, string elementType)
        {
            if (string.IsNullOrEmpty(text) || text.Length % 8 != 0) return false;
            if (elementType != "int" && elementType != "float" && elementType != "uint") return false;
            foreach (char c in text) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static List<PortValue> Hex(string text)
        {
            var list = new List<PortValue>();
            for (int i = 0; i + 8 <= text.Length; i += 8)
            {
                uint value = 0;
                for (int b = 0; b < 4; b++)
                    value |= (uint)byte.Parse(text.Substring(i + b * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) << (8 * b);
                list.Add(PortValue.Num(unchecked((int)value)));
            }
            return list;
        }

        private static void WriteEnum(SerializedProperty p, PortValue v, PortRule rule)
        {
            string[] names = p.enumNames;
            string field = PortValue.Norm(p.name);
            string wanted = v.K == PortValue.Kind.Enum ? v.Text : null;
            if (wanted == null && rule != null && rule.EnumValues.TryGetValue(field, out string[] byValue))
            {
                int n = (int)v.AsNumber(-1);
                if (n >= 0 && n < byValue.Length) wanted = byValue[n];
            }
            if (wanted == null && v.K == PortValue.Kind.String && !double.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) wanted = v.Text;
            if (wanted != null)
            {
                if (rule != null && rule.EnumNames.TryGetValue(PortValue.Norm(wanted), out string renamed)) wanted = renamed;
                string norm = PortValue.Norm(wanted);
                for (int i = 0; i < names.Length; i++)
                {
                    if (PortValue.Norm(names[i]) != norm) continue;
                    p.enumValueIndex = i;
                    return;
                }
                return; // a choice ParelVR does not have: the default stays
            }
            double d = v.AsNumber(double.NaN);
            if (!double.IsNaN(d)) p.intValue = (int)d;
        }

        private static void WriteReference(SerializedProperty p, PortValue v, IPortReferences refs)
        {
            if (v.K != PortValue.Kind.Object) return;
            string expected = ExpectedType(p);
            UnityEngine.Object o = refs != null ? refs.Resolve(v, expected) : v.Obj;
            if (o == null) return;
            o = Fit(o, expected);
            if (o == null) return;
            p.objectReferenceValue = o;
        }

        /// <summary>The type name a reference field takes ("PPtr&lt;$Transform&gt;" → "Transform").</summary>
        public static string ExpectedType(SerializedProperty p)
        {
            string t = p.type ?? string.Empty;
            if (t.StartsWith("PPtr<", StringComparison.Ordinal)) t = t.Substring(5).TrimEnd('>');
            return t.TrimStart('$');
        }

        /// <summary>The object, or the thing on the same object of the type the field wants.</summary>
        public static UnityEngine.Object Fit(UnityEngine.Object o, string expected)
        {
            if (o == null || string.IsNullOrEmpty(expected) || IsA(o.GetType(), expected)) return o;
            GameObject go = o is Component c ? c.gameObject : o as GameObject;
            if (go == null) return IsA(o.GetType(), "Object") ? o : null;
            if (expected == "GameObject") return go;
            foreach (Component other in go.GetComponents<Component>())
                if (other != null && IsA(other.GetType(), expected)) return other;
            return null;
        }

        private static bool IsA(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.Name == name) return true;
            return name == "Object";
        }

        private static float F(PortValue v, string name, float fallback = 0f)
        {
            PortValue part = v.Get(name);
            return part.IsNull ? fallback : (float)part.AsNumber(fallback);
        }

        private static Quaternion Rotation(PortValue v)
        {
            var q = new Quaternion(F(v, "x"), F(v, "y"), F(v, "z"), F(v, "w", 1f));
            float length = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return length < 1e-6f || float.IsNaN(length) ? Quaternion.identity : Quaternion.Normalize(q);
        }

        private static AnimationCurve Curve(PortValue v)
        {
            PortValue keys = v.Get("Curve");
            if (keys.K != PortValue.Kind.List) return null;
            var frames = new List<Keyframe>();
            foreach (PortValue k in keys.List)
            {
                if (k.K != PortValue.Kind.Map) continue;
                var frame = new Keyframe(F(k, "time"), F(k, "value"), F(k, "inSlope"), F(k, "outSlope"), F(k, "inWeight", 1f / 3f), F(k, "outWeight", 1f / 3f))
                {
                    weightedMode = (WeightedMode)(int)F(k, "weightedMode")
                };
                frames.Add(frame);
            }
            return new AnimationCurve(frames.ToArray());
        }
    }
}
