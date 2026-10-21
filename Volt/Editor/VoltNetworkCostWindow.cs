using System;
using System.Collections.Generic;
using System.IO;
using ParelVR.Volt.Core;
using ParelVR.Volt.Core.Bytecode;
using UnityEditor;
using UnityEngine;

namespace ParelVR.Volt.EditorTools
{
    /// <summary>
    /// What each script's synced fields cost on the network, read from the last compiled module: bytes per update,
    /// updates a second at most, and how that compares with the per-object and per-player budgets.
    /// </summary>
    public sealed class VoltNetworkCostWindow : EditorWindow
    {
        private sealed class FieldCost { public string Name; public string Type; public int Bytes; public bool Varies; }

        private sealed class ClassCost
        {
            public string Name;
            public ParelVR.Volt.Core.Bytecode.VoltSyncMode Mode;
            public readonly List<FieldCost> Fields = new List<FieldCost>();
            public int Bytes;
            public bool Varies;
            public int InScene;
            public bool Open;
        }

        // Header of a state message (type, tag, net id, field mask) as the session writes it.
        private const int StateHeaderBytes = 12;

        private readonly List<ClassCost> _classes = new List<ClassCost>();
        private string _status;
        private Vector2 _scroll;

        [MenuItem("ParelVR SDK/Volt/Network Cost", priority = 64)]
        private static void Open() => GetWindow<VoltNetworkCostWindow>("Volt Network Cost").Refresh();

        private void OnEnable() => Refresh();

        private void Refresh()
        {
            _classes.Clear();
            string path = VoltCompilerService.ModulePath;
            if (!File.Exists(path)) { _status = "Compile the scripts first (ParelVR SDK > Volt > Compile)."; return; }
            VoltModule module;
            try { module = VoltModuleReader.Read(File.ReadAllBytes(path)); }
            catch (Exception e) { _status = "The compiled module could not be read: " + e.Message; return; }

            var byClass = new Dictionary<int, ClassCost>();
            foreach (VoltFieldDef field in module.Fields)
            {
                if (!field.IsSynced || field.Class < 0 || field.Class >= module.Classes.Length) continue;
                if (!byClass.TryGetValue(field.Class, out ClassCost cls))
                {
                    VoltClassDef def = module.Classes[field.Class];
                    byClass[field.Class] = cls = new ClassCost { Name = module.GetString(def.Name), Mode = def.SyncMode };
                }
                VoltTypeKind kind = field.Type >= 0 && field.Type < module.Types.Length ? module.Types[field.Type].Kind : VoltTypeKind.Object;
                int bytes = Size(kind, out bool varies);
                cls.Fields.Add(new FieldCost { Name = module.GetString(field.Name), Type = kind.ToString(), Bytes = bytes, Varies = varies });
                cls.Bytes += bytes;
                cls.Varies |= varies;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (VoltBehaviour behaviour in FindObjectsByType<VoltBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string name = behaviour.GetType().Name;
                counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
            }
            foreach (ClassCost cls in byClass.Values)
            {
                cls.Bytes += StateHeaderBytes;
                string shortName = cls.Name.Substring(cls.Name.LastIndexOf('.') + 1);
                counts.TryGetValue(shortName, out cls.InScene);
                _classes.Add(cls);
            }
            _classes.Sort((a, b) => (b.Bytes * Math.Max(1, b.InScene)).CompareTo(a.Bytes * Math.Max(1, a.InScene)));
            _status = _classes.Count == 0 ? "No script syncs any field." : null;
        }

        /// <summary>Bytes a value of this kind takes in a state message; strings and arrays are counted at a typical size.</summary>
        private static int Size(VoltTypeKind kind, out bool varies)
        {
            varies = false;
            switch (kind)
            {
                case VoltTypeKind.Bool: case VoltTypeKind.I8: case VoltTypeKind.U8: return 1;
                case VoltTypeKind.Char: case VoltTypeKind.I16: case VoltTypeKind.U16: return 2;
                case VoltTypeKind.I32: case VoltTypeKind.U32: case VoltTypeKind.F32: case VoltTypeKind.Enum: case VoltTypeKind.ApiEnum: return 4;
                case VoltTypeKind.I64: case VoltTypeKind.U64: case VoltTypeKind.F64: return 8;
                case VoltTypeKind.Vec2: return 8;
                case VoltTypeKind.Vec3: return 12;
                case VoltTypeKind.Vec4: case VoltTypeKind.Quat: case VoltTypeKind.Color: return 16;
                case VoltTypeKind.String: varies = true; return 2 + 16;
                case VoltTypeKind.Array: varies = true; return 4 + 8 * 4;
                default: return 4;
            }
        }

        private void OnGUI()
        {
            VoltLimits limits = VoltLimits.Default;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Compile && Refresh", EditorStyles.toolbarButton, GUILayout.Width(130))) { VoltCompilerService.Compile(); Refresh(); }
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70))) Refresh();
                GUILayout.FlexibleSpace();
                GUILayout.Label("Budget: " + limits.MaxSyncedBytesPerObject + " B per object, " + limits.MaxNetworkBytesPerSecond / 1024 + " KB/s per player", EditorStyles.miniLabel);
            }
            if (_status != null) { EditorGUILayout.HelpBox(_status, MessageType.Info); return; }

            int total = 0;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (ClassCost cls in _classes)
            {
                // Continuous sync sends at most this often while values change; manual sync only when the script asks.
                int perSecond = cls.Mode == ParelVR.Volt.Core.Bytecode.VoltSyncMode.Continuous ? limits.MaxSerializationsPerSecondPerObject : 0;
                int copies = Math.Max(1, cls.InScene);
                int rate = cls.Bytes * perSecond * copies;
                total += rate;
                string size = (cls.Varies ? "~" : "") + cls.Bytes + " B";
                string line = cls.Name + "  ·  " + cls.Mode + "  ·  " + size + " per update  ·  " + cls.InScene + " in scene  ·  " +
                              (perSecond > 0 ? "up to " + rate + " B/s" : "on RequestSerialization");
                cls.Open = EditorGUILayout.Foldout(cls.Open, line, true);
                if (cls.Bytes > limits.MaxSyncedBytesPerObject)
                    EditorGUILayout.HelpBox("Over the " + limits.MaxSyncedBytesPerObject + " byte limit per object: updates will be refused.", MessageType.Error);
                if (!cls.Open) continue;
                EditorGUI.indentLevel++;
                foreach (FieldCost field in cls.Fields)
                    EditorGUILayout.LabelField(field.Name, field.Type + "  " + (field.Varies ? "~" : "") + field.Bytes + " B");
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndScrollView();

            MessageType type = total > limits.MaxNetworkBytesPerSecond ? MessageType.Warning : MessageType.None;
            EditorGUILayout.HelpBox("Continuous scripts together: up to " + total + " B/s of " + limits.MaxNetworkBytesPerSecond + " B/s per player" +
                (type == MessageType.Warning ? ". Over budget: switch some to Manual, sync fewer fields, or set VoltNetworking.InterestDistance." : "."), type);
        }
    }
}
