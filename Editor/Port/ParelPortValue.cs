using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Port
{
    /// <summary>
    /// The settings of a component from another platform, by field name, whichever way they were read: from the
    /// component itself while that platform's SDK is in the project, or from the scene / prefab / asset file when
    /// its script is missing. The writer copies them onto a ParelVR component.
    /// </summary>
    internal sealed class PortValue
    {
        public enum Kind { Null, Number, Bool, String, Object, Map, List, Curve, Enum }

        public Kind K;
        public double Number;
        public string Text;                       // String, or the member name of an Enum
        public UnityEngine.Object Obj;            // Object
        public long FileId;                       // Object read from a file: its id there (0 for none)
        public string Guid;                       // Object read from a file that lives in another file
        public Dictionary<string, PortValue> Map; // by normalised name
        public List<string> Order;                // the map's names as written, in order
        public List<PortValue> List;
        public AnimationCurve Curve;

        public static readonly PortValue Null = new PortValue { K = Kind.Null };

        public bool IsNull => K == Kind.Null || (K == Kind.Object && Obj == null && FileId == 0 && Guid == null);

        public static PortValue Num(double v) => new PortValue { K = Kind.Number, Number = v };
        public static PortValue Str(string v) => new PortValue { K = Kind.String, Text = v ?? string.Empty };
        public static PortValue Ref(UnityEngine.Object o) => new PortValue { K = Kind.Object, Obj = o };
        public static PortValue NewMap() => new PortValue { K = Kind.Map, Map = new Dictionary<string, PortValue>(), Order = new List<string>() };
        public static PortValue NewList() => new PortValue { K = Kind.List, List = new List<PortValue>() };

        public void Set(string name, PortValue value)
        {
            string key = Norm(name);
            if (!Map.ContainsKey(key)) Order.Add(name);
            Map[key] = value;
        }

        /// <summary>A field by name (case, underscores and a leading "m_" do not matter); Null when absent.</summary>
        public PortValue Get(string name)
        {
            if (K != Kind.Map || name == null) return Null;
            return Map.TryGetValue(Norm(name), out PortValue v) ? v : Null;
        }

        public bool Has(string name) => K == Kind.Map && Map.ContainsKey(Norm(name));

        public double AsNumber(double fallback = 0)
        {
            switch (K)
            {
                case Kind.Number:
                case Kind.Enum: return Number;
                case Kind.Bool: return Number;
                case Kind.String: return double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : fallback;
                default: return fallback;
            }
        }

        public bool AsBool(bool fallback = false) => K == Kind.Null ? fallback : AsNumber(fallback ? 1 : 0) != 0;

        public string AsString()
        {
            switch (K)
            {
                case Kind.String: return Text;
                case Kind.Number: return Number.ToString(CultureInfo.InvariantCulture);
                case Kind.Enum: return Text ?? Number.ToString(CultureInfo.InvariantCulture);
                case Kind.Map:
                    // Some platforms wrap a web address in a small struct of its own.
                    PortValue url = Get("url");
                    return url.K == Kind.String ? url.Text : null;
                default: return null;
            }
        }

        /// <summary>Lower case, without underscores, spaces or a leading "m_".</summary>
        public static string Norm(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            if (name.StartsWith("m_", StringComparison.Ordinal)) name = name.Substring(2);
            var chars = new char[name.Length];
            int n = 0;
            foreach (char c in name)
                if (c != '_' && c != ' ') chars[n++] = char.ToLowerInvariant(c);
            return new string(chars, 0, n);
        }

        // ── reading a component or asset that Unity can still load ───────────

        private static readonly HashSet<string> Housekeeping = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
            "m_Enabled", "m_Script", "m_Name", "m_EditorHideFlags", "m_EditorClassIdentifier"
        };

        public static PortValue FromObject(UnityEngine.Object o)
        {
            PortValue map = NewMap();
            if (o == null) return map;
            using (var so = new SerializedObject(o))
            {
                SerializedProperty it = so.GetIterator();
                if (!it.Next(true)) return map;
                do
                {
                    if (Housekeeping.Contains(it.name)) continue;
                    map.Set(it.name, FromProperty(it.Copy(), 0));
                } while (it.Next(false));
            }
            return map;
        }

        private static PortValue FromProperty(SerializedProperty p, int depth)
        {
            if (depth > 24) return Null;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return Num(p.longValue);
                case SerializedPropertyType.Boolean: return new PortValue { K = Kind.Bool, Number = p.boolValue ? 1 : 0 };
                case SerializedPropertyType.Float: return Num(p.doubleValue);
                case SerializedPropertyType.String: return Str(p.stringValue);
                case SerializedPropertyType.LayerMask: return Num(p.intValue);
                case SerializedPropertyType.Character: return Num(p.intValue);
                case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.ExposedReference:
                    return p.propertyType == SerializedPropertyType.ObjectReference ? Ref(p.objectReferenceValue) : Null;
                case SerializedPropertyType.Enum:
                {
                    string[] names = p.enumNames;
                    int index = p.enumValueIndex;
                    return new PortValue { K = Kind.Enum, Number = p.intValue, Text = index >= 0 && index < names.Length ? names[index] : null };
                }
                case SerializedPropertyType.Color:
                {
                    Color c = p.colorValue;
                    return Floats(("r", c.r), ("g", c.g), ("b", c.b), ("a", c.a));
                }
                case SerializedPropertyType.Vector2: { Vector2 v = p.vector2Value; return Floats(("x", v.x), ("y", v.y)); }
                case SerializedPropertyType.Vector3: { Vector3 v = p.vector3Value; return Floats(("x", v.x), ("y", v.y), ("z", v.z)); }
                case SerializedPropertyType.Vector4: { Vector4 v = p.vector4Value; return Floats(("x", v.x), ("y", v.y), ("z", v.z), ("w", v.w)); }
                case SerializedPropertyType.Quaternion: { Quaternion q = p.quaternionValue; return Floats(("x", q.x), ("y", q.y), ("z", q.z), ("w", q.w)); }
                case SerializedPropertyType.Vector2Int: { Vector2Int v = p.vector2IntValue; return Floats(("x", v.x), ("y", v.y)); }
                case SerializedPropertyType.Vector3Int: { Vector3Int v = p.vector3IntValue; return Floats(("x", v.x), ("y", v.y), ("z", v.z)); }
                case SerializedPropertyType.Rect: { Rect r = p.rectValue; return Floats(("x", r.x), ("y", r.y), ("width", r.width), ("height", r.height)); }
                case SerializedPropertyType.AnimationCurve: return new PortValue { K = Kind.Curve, Curve = p.animationCurveValue };
                case SerializedPropertyType.Generic:
                case SerializedPropertyType.ManagedReference:
                    if (p.isArray)
                    {
                        PortValue list = NewList();
                        int size = Math.Min(p.arraySize, 4096);
                        for (int i = 0; i < size; i++) list.List.Add(FromProperty(p.GetArrayElementAtIndex(i), depth + 1));
                        return list;
                    }
                    PortValue map = NewMap();
                    SerializedProperty child = p.Copy();
                    SerializedProperty end = p.GetEndProperty();
                    if (child.Next(true))
                    {
                        while (!SerializedProperty.EqualContents(child, end))
                        {
                            map.Set(child.name, FromProperty(child.Copy(), depth + 1));
                            if (!child.Next(false)) break;
                        }
                    }
                    return map;
                default:
                    return Null;
            }
        }

        private static PortValue Floats(params (string Name, float Value)[] parts)
        {
            PortValue map = NewMap();
            foreach ((string name, float value) in parts) map.Set(name, Num(value));
            return map;
        }

        public override string ToString()
        {
            switch (K)
            {
                case Kind.Map: return "{" + string.Join(", ", Order) + "}";
                case Kind.List: return "[" + List.Count + "]";
                case Kind.Object: return Obj != null ? Obj.name : FileId != 0 ? "#" + FileId : "null";
                default: return AsString() ?? K.ToString();
            }
        }
    }
}
