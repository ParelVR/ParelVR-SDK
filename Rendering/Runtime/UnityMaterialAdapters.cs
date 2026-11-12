using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Reads a UnityEngine.Material for the mapping rules. The material is never changed.
    /// A material made for another render pipeline than the running one (a URP world seen from a Built-in one, through a
    /// portal) has no sub-shader the running pipeline uses: Unity then reports the error fallback's queue and tags. The
    /// queue and tags are read from the material's own sub-shader instead, so cut-out leaves stay cut out.
    /// Custom shaders (Shader Graphs, store shaders) name their main texture, colour, normal map, cut-off and emission
    /// their own way: those are found by the shader's own flags ([MainTexture], [MainColor], [Normal]) and by name, and
    /// read under the usual names, so a stand-in keeps its textures instead of coming out white.
    /// </summary>
    public sealed class UnityMaterialSource : IMaterialPropertySource
    {
        private readonly Material _material;
        private readonly HashSet<string> _keptNames;
        private readonly Dictionary<string, string> _alias;

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
            _alias = FindAliases(material);
        }

        public Material Material => _material;
        public string MaterialName => _material.name;
        public string ShaderName => _material.shader != null ? _material.shader.name : string.Empty;

        public int RenderQueue
        {
            get
            {
                // The material's own queue when it has one; else the queue its shader asks for in the sub-shader it was
                // made for (Unity's renderQueue would be the error fallback's 2000 under another pipeline).
                int raw = _material.rawRenderQueue;
                if (raw >= 0) return raw;
                int own = OwnQueue(_material.shader);
                return own >= 0 ? own : _material.renderQueue;
            }
        }

        public bool HasProperty(string name) => _material.HasProperty(Real(name)) || (_keptNames != null && _keptNames.Contains(name));

        public float GetFloat(string name)
        {
            name = Real(name);
            if (_material.HasFloat(name)) return _material.GetFloat(name);
            if (_material.HasInteger(name)) return _material.GetInteger(name);
            return 0f;
        }

        public ColorValue GetColor(string name)
        {
            name = Real(name);
            if (_material.HasColor(name)) return From(_material.GetColor(name));
            if (_material.HasVector(name)) { Vector4 v = _material.GetVector(name); return new ColorValue(v.x, v.y, v.z, v.w); }
            return default;
        }

        public TextureSlot GetTexture(string name)
        {
            name = Real(name);
            if (!_material.HasTexture(name)) return null;
            Vector2 scale = _material.GetTextureScale(name);
            Vector2 offset = _material.GetTextureOffset(name);
            return new TextureSlot(_material.GetTexture(name)) { ScaleX = scale.x, ScaleY = scale.y, OffsetX = offset.x, OffsetY = offset.y };
        }

        public bool IsKeywordEnabled(string keyword)
        {
            if (_material.IsKeywordEnabled(keyword)) return true;
            // Keywords the running pipeline's keyword space doesn't know are still stored on the material.
            string[] stored = _material.shaderKeywords;
            return stored != null && Array.IndexOf(stored, keyword) >= 0;
        }

        public string GetTag(string tag)
        {
            string value = _material.GetTag(tag, false, string.Empty);
            if (!string.IsNullOrEmpty(value)) return value;
            Shader shader = _material.shader;
            if (shader == null) return string.Empty;
            var id = new UnityEngine.Rendering.ShaderTagId(tag);
            for (int s = 0; s < shader.subshaderCount; s++)
            {
                string v = shader.FindSubshaderTagValue(s, id).name;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return string.Empty;
        }

        private string Real(string name) => _alias != null && _alias.TryGetValue(name, out string real) ? real : name;

        // ---- the material's own sub-shader -----------------------------------------------------------------------

        private static readonly UnityEngine.Rendering.ShaderTagId QueueTag = new UnityEngine.Rendering.ShaderTagId("Queue");

        private static int OwnQueue(Shader shader)
        {
            if (shader == null) return -1;
            for (int s = 0; s < shader.subshaderCount; s++)
            {
                string q = shader.FindSubshaderTagValue(s, QueueTag).name;
                if (string.IsNullOrEmpty(q)) continue;
                int parsed = ParseQueue(q);
                if (parsed >= 0) return parsed;
            }
            return -1;
        }

        /// <summary>"AlphaTest", "Geometry+10", "Transparent-1", "3000" as a render queue; -1 when unknown.</summary>
        public static int ParseQueue(string q)
        {
            if (string.IsNullOrEmpty(q)) return -1;
            q = q.Trim();
            int sign = 0, at = -1;
            for (int i = 1; i < q.Length; i++)
                if (q[i] == '+' || q[i] == '-') { sign = q[i] == '+' ? 1 : -1; at = i; break; }
            string name = at > 0 ? q.Substring(0, at).Trim() : q;
            int offset = 0;
            if (at > 0 && !int.TryParse(q.Substring(at + 1).Trim(), out offset)) offset = 0;
            int baseQueue;
            switch (name.ToLowerInvariant())
            {
                case "background": baseQueue = 1000; break;
                case "geometry": baseQueue = 2000; break;
                case "alphatest": baseQueue = 2450; break;
                case "geometrylast": baseQueue = 2500; break;
                case "transparent": baseQueue = 3000; break;
                case "overlay": baseQueue = 4000; break;
                default:
                    return int.TryParse(q, out int n) ? n : -1;
            }
            return baseQueue + sign * offset;
        }

        // ---- custom shaders' own names for the usual properties ----------------------------------------------------

        private static readonly string[] MainTexNames = { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_MainTexture", "_UnlitColorMap" };
        private static readonly string[] ColorNames = { "_Color", "_BaseColor", "_MainColor", "_TintColor", "_UnlitColor" };
        private static readonly string[] NormalNames = { "_BumpMap", "_NormalMap" };
        private static readonly string[] CutoffNames = { "_Cutoff", "_AlphaCutoff", "_Clip" };
        private static readonly string[] EmissionColorNames = { "_EmissionColor", "_EmissiveColor", "_Emission" };
        private static readonly string[] EmissionMapNames = { "_EmissionMap", "_EmissiveColorMap", "_EmissionTex" };

        private static Dictionary<string, string> FindAliases(Material m)
        {
            Shader shader = m.shader;
            if (shader == null) return null;
            int count;
            try { count = shader.GetPropertyCount(); }
            catch (Exception) { return null; }
            if (count == 0) return null;

            string mainTex = null, color = null, normal = null, cutoff = null, emissionColor = null, emissionMap = null;
            string flaggedTex = null, flaggedColor = null;
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                UnityEngine.Rendering.ShaderPropertyType type = shader.GetPropertyType(i);
                UnityEngine.Rendering.ShaderPropertyFlags flags = shader.GetPropertyFlags(i);
                if ((flags & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) != 0 && (flags & UnityEngine.Rendering.ShaderPropertyFlags.MainTexture) == 0) continue;
                string lower = (name + " " + shader.GetPropertyDescription(i)).ToLowerInvariant();
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        if (shader.GetPropertyTextureDimension(i) != UnityEngine.Rendering.TextureDimension.Tex2D) break;
                        bool hasTexture = m.GetTexture(name) != null;
                        if ((flags & UnityEngine.Rendering.ShaderPropertyFlags.MainTexture) != 0) flaggedTex = name;
                        if ((flags & UnityEngine.Rendering.ShaderPropertyFlags.Normal) != 0 || Has(lower, "normal", "bump"))
                        {
                            if (normal == null || (hasTexture && m.GetTexture(normal) == null)) normal = name;
                        }
                        else if (Has(lower, "emiss"))
                        {
                            if (emissionMap == null) emissionMap = name;
                        }
                        else if (Has(lower, "albedo", "basecolor", "base color", "base map", "basemap", "diffuse", "maintex", "main tex", "main texture", "_tex", "color map", "colormap", "colour")
                                 && !Has(lower, "mask", "metal", "rough", "smooth", "occlu", "_ao", "height", "detail", "noise", "flow", "spec", "lut", "ramp", "matcap", "dissolve", "wind", "extra", "subsurface", "thick", "hue"))
                        {
                            if (mainTex == null || (hasTexture && m.GetTexture(mainTex) == null)) mainTex = name;
                        }
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        if ((flags & UnityEngine.Rendering.ShaderPropertyFlags.MainColor) != 0) flaggedColor = name;
                        else if (Has(lower, "emiss")) { if (emissionColor == null) emissionColor = name; }
                        else if (color == null && Has(lower, "color", "colour", "tint", "albedo") && !Has(lower, "spec", "rim", "shadow", "fog", "outline", "subsurface", "hue", "emiss", "sss", "fresnel", "dry", "healthy"))
                            color = name;
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        if (cutoff == null && Has(lower, "cutoff", "alphaclip", "alpha clip", "clipthreshold", "clip threshold", "alphathreshold", "alpha threshold"))
                            cutoff = name;
                        break;
                }
            }

            var alias = new Dictionary<string, string>(StringComparer.Ordinal);
            void Map(string[] usual, string found)
            {
                if (found == null) return;
                foreach (string u in usual) if (m.HasProperty(u)) return; // the shader has a usual name already
                alias[usual[0]] = found;
            }
            Map(MainTexNames, flaggedTex ?? mainTex);
            Map(ColorNames, flaggedColor ?? color);
            Map(NormalNames, normal);
            Map(CutoffNames, cutoff);
            Map(EmissionColorNames, emissionColor);
            Map(EmissionMapNames, emissionMap);
            return alias.Count > 0 ? alias : null;
        }

        private static bool Has(string s, params string[] parts)
        {
            foreach (string p in parts) if (s.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

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
