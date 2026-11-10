using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>Reads a UnityEngine.Material for the mapping rules. The material is never changed.</summary>
    public sealed class UnityMaterialSource : IMaterialPropertySource
    {
        private readonly Material _material;
        private readonly HashSet<string> _keptNames;

        public UnityMaterialSource(Material material)
        {
            _material = material ? material : throw new ArgumentNullException(nameof(material));
            // A material whose shader is missing still holds values for its old properties.
            ShaderInfo info = UnityShaderInspector.Inspect(material);
            if (info.IsMissing)
            {
                _keptNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (ShaderPropertyInfo p in info.Properties) _keptNames.Add(p.Name);
            }
        }

        public Material Material => _material;
        public string MaterialName => _material.name;
        public string ShaderName => _material.shader != null ? _material.shader.name : string.Empty;
        public int RenderQueue => _material.renderQueue;

        public bool HasProperty(string name) => _material.HasProperty(name) || (_keptNames != null && _keptNames.Contains(name));

        public float GetFloat(string name)
        {
            if (_material.HasFloat(name)) return _material.GetFloat(name);
            if (_material.HasInteger(name)) return _material.GetInteger(name);
            return 0f;
        }

        public ColorValue GetColor(string name)
        {
            if (_material.HasColor(name)) return From(_material.GetColor(name));
            if (_material.HasVector(name)) { Vector4 v = _material.GetVector(name); return new ColorValue(v.x, v.y, v.z, v.w); }
            return default;
        }

        public TextureSlot GetTexture(string name)
        {
            if (!_material.HasTexture(name)) return null;
            Vector2 scale = _material.GetTextureScale(name);
            Vector2 offset = _material.GetTextureOffset(name);
            return new TextureSlot(_material.GetTexture(name)) { ScaleX = scale.x, ScaleY = scale.y, OffsetX = offset.x, OffsetY = offset.y };
        }

        public bool IsKeywordEnabled(string keyword) => _material.IsKeywordEnabled(keyword);

        public string GetTag(string tag) => _material.GetTag(tag, false, string.Empty);

        internal static ColorValue From(Color c) => new ColorValue(c.r, c.g, c.b, c.a);
    }

    /// <summary>Writes mapped values into a new material. Properties the target shader doesn't have are skipped.</summary>
    public sealed class UnityMaterialSink : IMaterialPropertySink
    {
        private readonly Material _material;

        public UnityMaterialSink(Material material)
        {
            _material = material ? material : throw new ArgumentNullException(nameof(material));
        }

        public Material Material => _material;

        public bool HasProperty(string name) => _material.HasProperty(name);

        public void SetFloat(string name, float value)
        {
            if (_material.HasProperty(name)) _material.SetFloat(name, value);
        }

        public void SetColor(string name, ColorValue value)
        {
            if (_material.HasProperty(name)) _material.SetColor(name, new Color(value.R, value.G, value.B, value.A));
        }

        public void SetTexture(string name, TextureSlot slot)
        {
            if (!_material.HasProperty(name)) return;
            var texture = slot?.Texture as Texture;
            // A texture of the wrong shape (e.g. a 2D picture for a cubemap slot) is left out instead of raising an error.
            if (texture != null && !DimensionFits(name, texture)) return;
            _material.SetTexture(name, texture);
            if (slot == null) return;
            _material.SetTextureScale(name, new Vector2(slot.ScaleX, slot.ScaleY));
            _material.SetTextureOffset(name, new Vector2(slot.OffsetX, slot.OffsetY));
        }

        private bool DimensionFits(string name, Texture texture)
        {
            Shader shader = _material.shader;
            int index = shader != null ? shader.FindPropertyIndex(name) : -1;
            if (index < 0 || shader.GetPropertyType(index) != UnityEngine.Rendering.ShaderPropertyType.Texture) return true;
            UnityEngine.Rendering.TextureDimension wanted = shader.GetPropertyTextureDimension(index);
            return wanted == UnityEngine.Rendering.TextureDimension.Any || wanted == texture.dimension;
        }

        public void SetKeyword(string keyword, bool enabled)
        {
            if (enabled) _material.EnableKeyword(keyword);
            else _material.DisableKeyword(keyword);
        }

        public void SetRenderQueue(int queue) => _material.renderQueue = queue;

        public void SetOverrideTag(string tag, string value) => _material.SetOverrideTag(tag, value ?? string.Empty);
    }
}
