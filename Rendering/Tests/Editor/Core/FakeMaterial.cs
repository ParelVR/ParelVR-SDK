using System.Collections.Generic;

namespace ParelVR.Rendering.Tests
{
    /// <summary>An engine-free material for mapping tests: reads like a source, records writes like a sink.</summary>
    internal sealed class FakeMaterial : IMaterialPropertySource, IMaterialPropertySink
    {
        public readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
        public readonly Dictionary<string, ColorValue> Colors = new Dictionary<string, ColorValue>();
        public readonly Dictionary<string, TextureSlot> Textures = new Dictionary<string, TextureSlot>();
        public readonly HashSet<string> Keywords = new HashSet<string>();
        public readonly Dictionary<string, string> Tags = new Dictionary<string, string>();
        /// <summary>When set, the sink only accepts these property names (like a real shader would).</summary>
        public HashSet<string> SinkProperties;

        public string MaterialName { get; set; } = "Test";
        public string ShaderName { get; set; } = "Standard";
        public int RenderQueue { get; set; } = 2000;

        public FakeMaterial(string shader = "Standard") { ShaderName = shader; }

        public FakeMaterial F(string n, float v) { Floats[n] = v; return this; }
        public FakeMaterial C(string n, float r, float g, float b, float a = 1f) { Colors[n] = new ColorValue(r, g, b, a); return this; }
        public FakeMaterial T(string n, string texture, float sx = 1f, float sy = 1f) { Textures[n] = new TextureSlot(texture) { ScaleX = sx, ScaleY = sy }; return this; }
        public FakeMaterial K(params string[] keywords) { foreach (string k in keywords) Keywords.Add(k); return this; }
        public FakeMaterial Q(int queue) { RenderQueue = queue; return this; }

        // source
        public bool HasProperty(string name) => SinkProperties != null ? SinkProperties.Contains(name) : Floats.ContainsKey(name) || Colors.ContainsKey(name) || Textures.ContainsKey(name);
        public float GetFloat(string name) => Floats.TryGetValue(name, out float v) ? v : 0f;
        public ColorValue GetColor(string name) => Colors.TryGetValue(name, out ColorValue c) ? c : default;
        public TextureSlot GetTexture(string name) => Textures.TryGetValue(name, out TextureSlot t) ? t : null;
        public bool IsKeywordEnabled(string keyword) => Keywords.Contains(keyword);
        public string GetTag(string tag) => Tags.TryGetValue(tag, out string v) ? v : string.Empty;

        // sink
        public void SetFloat(string name, float value) { if (Accepts(name)) Floats[name] = value; }
        public void SetColor(string name, ColorValue value) { if (Accepts(name)) Colors[name] = value; }
        public void SetTexture(string name, TextureSlot slot) { if (Accepts(name)) Textures[name] = slot; }
        public void SetKeyword(string keyword, bool enabled) { if (enabled) Keywords.Add(keyword); else Keywords.Remove(keyword); }
        public void SetRenderQueue(int queue) { RenderQueue = queue; }
        public void SetOverrideTag(string tag, string value) { Tags[tag] = value; }

        private bool Accepts(string name) => SinkProperties == null || SinkProperties.Contains(name);

        public object Tex(string name) => Textures.TryGetValue(name, out TextureSlot t) ? t?.Texture : null;
    }
}
