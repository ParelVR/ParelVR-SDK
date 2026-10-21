using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>
    /// Every value a material has saved, read straight from the asset. It works whatever the material's shader is,
    /// including a shader that is missing or failed to import, which is when the usual Material getters come back empty.
    /// </summary>
    public sealed class ParelMaterialSnapshot
    {
        public sealed class TextureSlot
        {
            public Texture Texture;
            public Vector2 Scale = Vector2.one;
            public Vector2 Offset = Vector2.zero;
        }

        private readonly Dictionary<string, TextureSlot> _textures = new Dictionary<string, TextureSlot>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _floats = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, Color> _colors = new Dictionary<string, Color>(StringComparer.Ordinal);

        public IEnumerable<KeyValuePair<string, TextureSlot>> Textures => _textures;

        public static ParelMaterialSnapshot Read(Material material)
        {
            var snapshot = new ParelMaterialSnapshot();
            if (material == null) return snapshot;
            var serialized = new SerializedObject(material);
            SerializedProperty saved = serialized.FindProperty("m_SavedProperties");
            if (saved == null) return snapshot;

            SerializedProperty textures = saved.FindPropertyRelative("m_TexEnvs");
            for (int i = 0; textures != null && i < textures.arraySize; i++)
            {
                SerializedProperty entry = textures.GetArrayElementAtIndex(i);
                string name = entry.FindPropertyRelative("first")?.stringValue;
                SerializedProperty value = entry.FindPropertyRelative("second");
                if (string.IsNullOrEmpty(name) || value == null) continue;
                snapshot._textures[name] = new TextureSlot
                {
                    Texture = value.FindPropertyRelative("m_Texture")?.objectReferenceValue as Texture,
                    Scale = value.FindPropertyRelative("m_Scale")?.vector2Value ?? Vector2.one,
                    Offset = value.FindPropertyRelative("m_Offset")?.vector2Value ?? Vector2.zero,
                };
            }

            SerializedProperty floats = saved.FindPropertyRelative("m_Floats");
            for (int i = 0; floats != null && i < floats.arraySize; i++)
            {
                SerializedProperty entry = floats.GetArrayElementAtIndex(i);
                string name = entry.FindPropertyRelative("first")?.stringValue;
                SerializedProperty value = entry.FindPropertyRelative("second");
                if (!string.IsNullOrEmpty(name) && value != null) snapshot._floats[name] = value.floatValue;
            }

            SerializedProperty colors = saved.FindPropertyRelative("m_Colors");
            for (int i = 0; colors != null && i < colors.arraySize; i++)
            {
                SerializedProperty entry = colors.GetArrayElementAtIndex(i);
                string name = entry.FindPropertyRelative("first")?.stringValue;
                SerializedProperty value = entry.FindPropertyRelative("second");
                if (!string.IsNullOrEmpty(name) && value != null) snapshot._colors[name] = value.colorValue;
            }
            return snapshot;
        }

        public TextureSlot Slot(params string[] names)
        {
            foreach (string name in names)
                if (_textures.TryGetValue(name, out TextureSlot slot) && slot.Texture != null) return slot;
            return null;
        }

        public float? Float(params string[] names)
        {
            foreach (string name in names)
                if (_floats.TryGetValue(name, out float value)) return value;
            return null;
        }

        public Color? ColorOf(params string[] names)
        {
            foreach (string name in names)
                if (_colors.TryGetValue(name, out Color value)) return value;
            return null;
        }

        public bool HasAnyFloat(params string[] names) => names.Any(_floats.ContainsKey);

        /// <summary>The main color texture: the usual property names first, then the first texture whose name says so.</summary>
        public TextureSlot MainTexture()
        {
            TextureSlot slot = Slot("_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_MainTexture", "_Diffuse", "_DiffuseMap", "_BaseTex");
            if (slot != null) return slot;
            string[] hints = { "main", "albedo", "base", "diffuse", "color" };
            foreach (KeyValuePair<string, TextureSlot> pair in _textures)
            {
                if (pair.Value.Texture is Texture2D && hints.Any(h => pair.Key.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    pair.Key.IndexOf("normal", StringComparison.OrdinalIgnoreCase) < 0 && pair.Key.IndexOf("mask", StringComparison.OrdinalIgnoreCase) < 0)
                    return pair.Value;
            }
            return null;
        }
    }
}
