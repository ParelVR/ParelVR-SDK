using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace ParelVR.Rendering.Tests
{
    // Needs the Unity Editor (EditMode test runner).
    public sealed class ValidatorTests
    {
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void Clean()
        {
            foreach (Object o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        private T Make<T>(T o) where T : Object { _made.Add(o); return o; }

        private MeshRenderer RendererWith(params Material[] materials)
        {
            var go = Make(new GameObject("PVRP validator test"));
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = materials;
            return r;
        }

        [Test]
        public void Error_shader_material_gets_a_parelvr_stand_in_and_the_original_is_untouched()
        {
            Shader error = Shader.Find(ShaderClassifier.ErrorShaderName);
            Assume.That(error != null);
            Material broken = Make(new Material(error) { name = "broken" });
            MeshRenderer r = RendererWith(broken);
            var v = new ParelRenderValidator(TargetPlatform.Pc, RepairPolicy.BrokenOnly);
            try
            {
                CompatibilityReport report = v.Validate(r.gameObject, "test");
                StringAssert.StartsWith("ParelVR/", r.sharedMaterial.shader.name);
                Assert.IsTrue(r.sharedMaterial.shader.isSupported);
                Assert.AreEqual(error, broken.shader, "the original material keeps its shader");
                Assert.AreEqual(1, report.Count(ConversionAction.Fallback));
            }
            finally { v.Release(); }
        }

        [Test]
        public void Working_standard_material_is_left_alone_on_pc()
        {
            Material standard = Make(new Material(Shader.Find("Standard")));
            MeshRenderer r = RendererWith(standard);
            var v = new ParelRenderValidator(TargetPlatform.Pc, RepairPolicy.ForeignPipelines);
            try
            {
                v.Validate(r.gameObject);
                Assert.AreSame(standard, r.sharedMaterial);
            }
            finally { v.Release(); }
        }

        [Test]
        public void Shared_broken_material_is_converted_once_for_every_renderer()
        {
            Material broken = Make(new Material(Shader.Find(ShaderClassifier.ErrorShaderName)));
            MeshRenderer a = RendererWith(broken), b = RendererWith(broken, broken);
            var v = new ParelRenderValidator(TargetPlatform.Pc, RepairPolicy.BrokenOnly);
            try
            {
                var report = new CompatibilityReport();
                v.Repair(a.gameObject, report);
                v.Repair(b.gameObject, report);
                Assert.AreSame(a.sharedMaterial, b.sharedMaterials[0]);
                Assert.AreSame(a.sharedMaterial, b.sharedMaterials[1]);
                Assert.AreEqual(1, v.Converter.CachedCount);
            }
            finally { v.Release(); }
        }

        [Test]
        public void Released_validator_destroys_the_materials_it_made()
        {
            Material broken = Make(new Material(Shader.Find(ShaderClassifier.ErrorShaderName)));
            MeshRenderer r = RendererWith(broken);
            var v = new ParelRenderValidator(TargetPlatform.Pc, RepairPolicy.BrokenOnly);
            v.Validate(r.gameObject);
            Material made = r.sharedMaterial;
            Assert.AreNotSame(broken, made);
            v.Release();
            Assert.IsTrue(made == null, "converted material destroyed with its content");
        }

        [Test]
        public void Sink_skips_a_texture_of_the_wrong_shape()
        {
            Shader sky = ParelShaderLibrary.Resolve(ShaderRole.Skybox, TargetPlatform.Pc, out _);
            Assume.That(sky != null && sky.name == ParelShaderNames.Skybox);
            Material m = Make(new Material(sky));
            Texture2D flat = Make(new Texture2D(2, 2));
            new UnityMaterialSink(m).SetTexture("_Tex", new TextureSlot(flat));
            Assert.AreNotSame(flat, m.GetTexture("_Tex"));
        }

        [Test]
        public void Self_test_passes_in_the_editor()
        {
            List<ParelShaderSelfTest.Result> results = ParelShaderSelfTest.Run(TargetPlatform.Pc);
            var failed = results.FindAll(x => !x.Passed);
            Assert.IsEmpty(failed, string.Join("\n", failed));
            Assert.Greater(results.Count, 15);
        }
    }
}
