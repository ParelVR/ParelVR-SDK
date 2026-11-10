using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// Reads a material asset's saved values straight from its serialized data (m_SavedProperties). This works even
    /// when the material's shader is missing (e.g. an HDRP or URP material in this project), and it can tell a texture
    /// that was never set apart from one whose asset is missing.
    /// </summary>
    public sealed class SavedMaterialProperties : IMaterialPropertySource
    {
        private readonly Material _material;
        private readonly Dictionary<string, float> _floats = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly Dictionary<string, ColorValue> _colors = new Dictionary<string, ColorValue>(StringComparer.Ordinal);
        private readonly Dictionary<string, TextureSlot> _textures = new Dictionary<string, TextureSlot>(StringComparer.Ordinal);
        private readonly HashSet<string> _keywords = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _tags = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Texture properties that point at an asset that no longer exists.</summary>
        public readonly List<string> MissingTextures = new List<string>();
        public bool ShaderMissing { get; }

        public SavedMaterialProperties(Material material)
        {
            _material = material ? material : throw new ArgumentNullException(nameof(material));
            ShaderMissing = material.shader == null || material.shader.name == ShaderClassifier.ErrorShaderName;

            var so = new SerializedObject(material);
            SerializedProperty saved = so.FindProperty("m_SavedProperties");
            if (saved != null)
            {
                ReadTextures(saved.FindPropertyRelative("m_TexEnvs"));
                ReadPairs(saved.FindPropertyRelative("m_Floats"), p => _floats[p.Key] = p.Value.floatValue);
                ReadPairs(saved.FindPropertyRelative("m_Ints"), p => _floats[p.Key] = p.Value.intValue);
                ReadPairs(saved.FindPropertyRelative("m_Colors"), p => { Color c = p.Value.colorValue; _colors[p.Key] = new ColorValue(c.r, c.g, c.b, c.a); });
            }
            foreach (string k in material.shaderKeywords) _keywords.Add(k);
            ReadKeywordList(so.FindProperty("m_ValidKeywords"));
            ReadKeywordList(so.FindProperty("m_InvalidKeywords"));
            SerializedProperty tags = so.FindProperty("stringTagMap");
            ReadPairs(tags, p => _tags[p.Key] = p.Value.stringValue);
        }

        public string MaterialName => _material.name;
        public string ShaderName => ShaderMissing ? string.Empty : _material.shader.name;
        public int RenderQueue => _material.renderQueue;

        public bool HasProperty(string name) => _floats.ContainsKey(name) || _colors.ContainsKey(name) || _textures.ContainsKey(name);
        public float GetFloat(string name) => _floats.TryGetValue(name, out float v) ? v : 0f;
        public ColorValue GetColor(string name) => _colors.TryGetValue(name, out ColorValue c) ? c : default;
        public TextureSlot GetTexture(string name) => _textures.TryGetValue(name, out TextureSlot t) ? t : null;
        public bool IsKeywordEnabled(string keyword) => _keywords.Contains(keyword);
        public string GetTag(string tag) => _tags.TryGetValue(tag, out string v) ? v : string.Empty;

        /// <summary>A <see cref="ShaderInfo"/> built from the saved property names, for a material whose shader is missing.</summary>
        public ShaderInfo ToMissingShaderInfo()
        {
            var info = new ShaderInfo { IsMissing = true, IsSupported = false, RenderQueue = RenderQueue, RenderTypeTag = GetTag("RenderType") };
            foreach (string n in _textures.Keys) info.Properties.Add(new ShaderPropertyInfo(n, ShaderPropertyKind.Texture));
            foreach (string n in _floats.Keys) info.Properties.Add(new ShaderPropertyInfo(n, ShaderPropertyKind.Float));
            foreach (string n in _colors.Keys) info.Properties.Add(new ShaderPropertyInfo(n, ShaderPropertyKind.Color));
            return info;
        }

        private void ReadTextures(SerializedProperty array)
        {
            if (array == null || !array.isArray) return;
            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty pair = array.GetArrayElementAtIndex(i);
                string name = pair.FindPropertyRelative("first")?.stringValue;
                SerializedProperty env = pair.FindPropertyRelative("second");
                if (string.IsNullOrEmpty(name) || env == null) continue;
                SerializedProperty texture = env.FindPropertyRelative("m_Texture");
                Vector2 scale = env.FindPropertyRelative("m_Scale")?.vector2Value ?? Vector2.one;
                Vector2 offset = env.FindPropertyRelative("m_Offset")?.vector2Value ?? Vector2.zero;
                UnityEngine.Object value = texture?.objectReferenceValue;
                // A missing asset still has a non-zero reference id. The instance-id accessor is obsolete in 6.4 but
                // still present, and the SDK supports 6.0, which doesn't have its replacement.
#pragma warning disable CS0618
                if (value == null && texture != null && texture.objectReferenceInstanceIDValue != 0) MissingTextures.Add(name);
#pragma warning restore CS0618
                _textures[name] = new TextureSlot(value) { ScaleX = scale.x, ScaleY = scale.y, OffsetX = offset.x, OffsetY = offset.y };
            }
        }

        private static void ReadPairs(SerializedProperty array, Action<KeyValuePair<string, SerializedProperty>> read)
        {
            if (array == null || !array.isArray) return;
            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty pair = array.GetArrayElementAtIndex(i);
                string name = pair.FindPropertyRelative("first")?.stringValue;
                SerializedProperty value = pair.FindPropertyRelative("second");
                if (!string.IsNullOrEmpty(name) && value != null) read(new KeyValuePair<string, SerializedProperty>(name, value));
            }
        }

        private void ReadKeywordList(SerializedProperty list)
        {
            if (list == null || !list.isArray) return;
            for (int i = 0; i < list.arraySize; i++)
            {
                string k = list.GetArrayElementAtIndex(i).stringValue;
                if (!string.IsNullOrEmpty(k)) _keywords.Add(k);
            }
        }
    }
}
