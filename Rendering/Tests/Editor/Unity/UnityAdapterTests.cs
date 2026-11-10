using NUnit.Framework;
using UnityEngine;

namespace ParelVR.Rendering.Tests
{
    // Needs the Unity Editor (EditMode test runner).
    public sealed class UnityAdapterTests
    {
        [Test]
        public void Standard_shader_is_inspected_as_builtin()
        {
            Shader standard = Shader.Find("Standard");
            Assume.That(standard != null, "Standard shader not available in this project");

            ShaderInfo info = UnityShaderInspector.Inspect(standard);

            Assert.AreEqual("Standard", info.Name);
            Assert.IsTrue(info.HasProperty("_MainTex"));
            Assert.IsTrue(info.HasProperty("_Glossiness"));
            Assert.IsTrue(info.HasLightMode("ForwardBase"));
            Assert.AreEqual(string.Empty, info.RenderPipelineTag);
            Assert.AreEqual(CompatibilityLevel.Direct, ParelRenderManager.Classify(standard, TargetPlatform.Pc).Level);
        }

        [Test]
        public void Material_source_reads_values_and_tiling()
        {
            var material = new Material(Shader.Find("Standard"));
            var texture = new Texture2D(4, 4);
            try
            {
                material.SetColor("_Color", new Color(0.25f, 0.5f, 0.75f, 1f));
                material.SetFloat("_Glossiness", 0.8f);
                material.SetTexture("_MainTex", texture);
                material.SetTextureScale("_MainTex", new Vector2(2f, 3f));

                var source = new UnityMaterialSource(material);
                Assert.AreEqual(0.5f, source.GetColor("_Color").G, 1e-4f);
                Assert.AreEqual(0.8f, source.GetFloat("_Glossiness"), 1e-4f);
                TextureSlot slot = source.GetTexture("_MainTex");
                Assert.AreSame(texture, slot.Texture);
                Assert.AreEqual(3f, slot.ScaleY, 1e-4f);
                Assert.IsNull(source.GetTexture("_NotAProperty"));
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void Material_api_hides_the_values_of_a_missing_shader()
        {
            // Verified 2026-11-07 on Unity 6.4.11: once a material's shader is missing (Unity's error shader), the
            // Material API no longer exposes the values saved for the old shader, whether the material was loaded
            // from disk or switched at runtime. Only Editor serialization (SavedMaterialProperties) can read them,
            // so values must be captured at upload time. If Unity changes this, this test flags it.
            var material = new Material(Shader.Find("Standard"));
            try
            {
                material.SetColor("_Color", Color.red);
                material.shader = Shader.Find(ShaderClassifier.ErrorShaderName);

                ShaderInfo info = UnityShaderInspector.Inspect(material);
                Assert.IsTrue(info.IsMissing);
                Assert.IsFalse(info.HasProperty("_Color"));
                Assert.IsFalse(material.HasColor("_Color"));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void Sink_skips_properties_the_target_lacks()
        {
            var material = new Material(Shader.Find("Unlit/Color"));
            try
            {
                var sink = new UnityMaterialSink(material);
                Assert.DoesNotThrow(() => sink.SetFloat("_DoesNotExist", 1f));
                sink.SetColor("_Color", new ColorValue(1f, 0f, 0f, 1f));
                Assert.AreEqual(Color.red, material.GetColor("_Color"));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }
    }
}
