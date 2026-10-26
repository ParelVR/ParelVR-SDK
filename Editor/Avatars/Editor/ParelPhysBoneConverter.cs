using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ParelVR.AvatarSDK;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Avatars.Tools
{
    /// <summary>
    /// Turns the PhysBones (and PhysBone colliders) of an avatar made for another platform into Motion Bones.
    /// Works while that platform's SDK is still in the project: its components are read by field name, and Motion
    /// Bones use the same names and value ranges, so a chain moves the same after converting. The originals are
    /// switched off (or removed, if asked) so the two never simulate the same bones.
    /// </summary>
    public static class ParelPhysBoneConverter
    {
        private const string BoneType = "VRCPhysBone";
        private const string ColliderType = "VRCPhysBoneCollider";

        [MenuItem("ParelVR SDK/Avatar Tools/Convert PhysBones to Motion Bones", priority = 120)]
        private static void ConvertSelected()
        {
            GameObject root = Selection.activeGameObject;
            if (root == null)
            {
                EditorUtility.DisplayDialog("Convert PhysBones", "Select the avatar (its root object) in the Hierarchy first.", "OK");
                return;
            }
            int choice = EditorUtility.DisplayDialogComplex("Convert PhysBones",
                $"Convert every PhysBone and PhysBone collider under '{root.name}' to Motion Bones?",
                "Convert, keep originals off", "Cancel", "Convert, remove originals");
            if (choice == 1) return;
            Convert(root, choice == 2, out int bones, out int colliders, out int found);
            if (found == 0)
            {
                EditorUtility.DisplayDialog("Convert PhysBones",
                    "No PhysBones were found. If they show as \"Missing Script\", import the original SDK into this project first, convert, then remove it.", "OK");
                return;
            }
            EditorUtility.DisplayDialog("Convert PhysBones", $"Converted {bones} bone chain(s) and {colliders} collider(s).", "OK");
        }

        [MenuItem("ParelVR SDK/Avatar Tools/Convert PhysBones to Motion Bones", true)]
        private static bool ConvertSelectedValid() => Selection.activeGameObject != null;

        public static void Convert(GameObject root, bool removeOriginals, out int boneCount, out int colliderCount, out int found)
        {
            boneCount = colliderCount = found = 0;
            Undo.SetCurrentGroupName("Convert PhysBones to Motion Bones");
            int group = Undo.GetCurrentGroup();

            var colliderMap = new Dictionary<Component, ParelMotionBoneCollider>();
            var originals = new List<Component>();

            // colliders first, so the bones can point at their new versions
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().Name != ColliderType) continue;
                found++;
                var target = c.GetComponent<ParelMotionBoneCollider>();
                if (target == null) target = Undo.AddComponent<ParelMotionBoneCollider>(c.gameObject);
                CopyByName(c, target, colliderMap);
                colliderMap[c] = target;
                originals.Add(c);
                colliderCount++;
            }
            foreach (Component c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().Name != BoneType) continue;
                found++;
                var target = c.GetComponent<ParelMotionBone>();
                if (target == null) target = Undo.AddComponent<ParelMotionBone>(c.gameObject);
                CopyByName(c, target, colliderMap);
                originals.Add(c);
                boneCount++;
            }

            foreach (Component c in originals)
            {
                if (removeOriginals) Undo.DestroyObjectImmediate(c);
                else if (c is Behaviour b)
                {
                    Undo.RecordObject(b, "Disable PhysBone");
                    b.enabled = false;
                }
            }
            Undo.CollapseUndoOperations(group);
            if (root.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
        }

        private const BindingFlags Fields = BindingFlags.Public | BindingFlags.Instance;

        private static void CopyByName(Component source, Component target, Dictionary<Component, ParelMotionBoneCollider> colliders)
        {
            Type from = source.GetType();
            foreach (FieldInfo field in target.GetType().GetFields(Fields))
            {
                if (field.IsNotSerialized || field.IsInitOnly || field.IsLiteral) continue;
                object value = Read(source, from, field.Name);
                if (value == null) continue;
                try
                {
                    object converted = ConvertValue(value, field.FieldType, colliders);
                    if (converted != null) field.SetValue(target, converted);
                }
                catch (Exception e) when (e is ArgumentException || e is InvalidCastException || e is FormatException)
                {
                    Debug.LogWarning($"[PhysBone Converter] {target.name}: '{field.Name}' was not copied ({e.Message}).");
                }
            }
            EditorUtility.SetDirty(target);
        }

        private static object Read(object source, Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Fields);
                if (f != null) return f.GetValue(source);
                PropertyInfo p = t.GetProperty(name, Fields);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return p.GetValue(source);
            }
            return null;
        }

        private static object ConvertValue(object value, Type to, Dictionary<Component, ParelMotionBoneCollider> colliders)
        {
            Type from = value.GetType();
            if (to.IsAssignableFrom(from))
                return value is AnimationCurve curve ? new AnimationCurve(curve.keys) : value;

            if (to == typeof(ParelDynamicsPermission))
            {
                // the other SDK's "advanced bool": False, True, Other (a filter list, treated as everyone)
                int raw = value is bool flag ? (flag ? 1 : 0) : System.Convert.ToInt32(value);
                return raw == 0 ? ParelDynamicsPermission.Nobody : ParelDynamicsPermission.Everyone;
            }
            if (to.IsEnum && (from.IsEnum || from == typeof(int)))
            {
                int raw = System.Convert.ToInt32(value);
                return Enum.IsDefined(to, raw) ? Enum.ToObject(to, raw) : null;
            }
            if (to == typeof(List<ParelMotionBoneCollider>) && value is IEnumerable list)
            {
                var result = new List<ParelMotionBoneCollider>();
                foreach (object item in list)
                    if (item is Component c && colliders.TryGetValue(c, out ParelMotionBoneCollider mapped)) result.Add(mapped);
                return result;
            }
            if (to == typeof(List<Transform>) && value is IEnumerable transforms)
            {
                var result = new List<Transform>();
                foreach (object item in transforms) if (item is Transform t) result.Add(t);
                return result;
            }
            return null;
        }
    }
}
