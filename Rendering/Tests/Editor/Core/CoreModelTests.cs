using System.Collections.Generic;
using NUnit.Framework;

namespace ParelVR.Rendering.Tests
{
    // Engine-free: these also run outside Unity through Rendering/Tests~/run-core-tests.sh.
    public sealed class CoreModelTests
    {
        private sealed class FakeReader : IMaterialReader
        {
            private readonly ShaderFamily _family;
            public readonly string Tag;
            public FakeReader(ShaderFamily family, string tag) { _family = family; Tag = tag; }
            public bool CanRead(ShaderFamily family) => family == _family;
            public MaterialDescription Read(IMaterialPropertySource source, ShaderFamily family) => new MaterialDescription { Name = Tag };
        }

        private sealed class FakeSource : IMaterialPropertySource
        {
            public string MaterialName => "Brick";
            public string ShaderName => "Standard";
            public int RenderQueue => 2000;
            public bool HasProperty(string name) => false;
            public float GetFloat(string name) => 0f;
            public ColorValue GetColor(string name) => default;
            public TextureSlot GetTexture(string name) => null;
            public bool IsKeywordEnabled(string keyword) => false;
            public string GetTag(string tag) => string.Empty;
        }

        [Test]
        public void Registry_picks_the_latest_reader_for_a_family()
        {
            var registry = new MaterialMapperRegistry();
            registry.Register(new FakeReader(ShaderFamily.BuiltInStandard, "first"));
            registry.Register(new FakeReader(ShaderFamily.BuiltInStandard, "override"));
            registry.Register(new FakeReader(ShaderFamily.UrpLit, "urp"));

            Assert.IsTrue(registry.TryRead(new FakeSource(), ShaderFamily.BuiltInStandard, out MaterialDescription d));
            Assert.AreEqual("override", d.Name);
            Assert.AreEqual(ShaderFamily.BuiltInStandard, d.SourceFamily);
            Assert.AreEqual("Standard", d.SourceShader);
            Assert.IsFalse(registry.TryRead(new FakeSource(), ShaderFamily.HdrpLit, out _));
        }

        [Test]
        public void Report_counts_and_hides_clean_entries_by_default()
        {
            var report = new CompatibilityReport { Title = "Test World", Target = TargetPlatform.Pc };
            report.Add(new CompatibilityEntry { MaterialName = "Floor", ShaderName = "Standard", Pipeline = RenderPipelineKind.BuiltIn, Action = ConversionAction.Preserved, Severity = DiagnosticSeverity.Info });
            report.Add(new CompatibilityEntry { MaterialName = "Wall", ShaderName = "Standard", Pipeline = RenderPipelineKind.BuiltIn, Action = ConversionAction.Preserved, Severity = DiagnosticSeverity.Info });
            var converted = report.Add(new CompatibilityEntry { MaterialName = "Glass", ShaderName = "HDRP/Lit", Pipeline = RenderPipelineKind.HighDefinition, Action = ConversionAction.Converted, TargetRole = ShaderRole.Transparent, Severity = DiagnosticSeverity.Notice });
            converted.Unmapped.Add("refraction");
            var missing = report.Add(new CompatibilityEntry { MaterialName = "Sign", ShaderName = "Unlit/Texture", Pipeline = RenderPipelineKind.BuiltIn, Action = ConversionAction.Preserved, Severity = DiagnosticSeverity.Warning });
            missing.MissingTextures.Add("Base");

            Assert.AreEqual(4, report.Inspected);
            Assert.AreEqual(3, report.Count(ConversionAction.Preserved));
            Assert.AreEqual(1, report.Count(ConversionAction.Converted));
            Assert.AreEqual(1, report.MissingTextureCount);
            Assert.AreEqual(RenderPipelineKind.BuiltIn, report.MajorityPipeline());

            string text = report.ToText();
            StringAssert.Contains("Materials checked: 4", text);
            StringAssert.Contains("Glass", text);
            StringAssert.Contains("refraction", text);
            StringAssert.Contains("Sign", text);
            StringAssert.DoesNotContain("Floor", text);
            StringAssert.Contains("Floor", report.ToText(detail: true));
        }

        [Test]
        public void Description_lists_its_textures_and_dedupes_unmapped_features()
        {
            var d = new MaterialDescription { BaseMap = new TextureSlot("albedo"), NormalMap = new TextureSlot() };
            d.Unmapped("clear coat");
            d.Unmapped("clear coat");

            var names = new List<string>();
            foreach (var pair in d.Textures()) names.Add(pair.Key);

            CollectionAssert.AreEqual(new[] { "Base", "Normal" }, names);
            Assert.AreEqual(1, d.UnmappedFeatures.Count);
            Assert.IsTrue(d.BaseMap.HasTexture);
            Assert.IsFalse(d.NormalMap.HasTexture);
        }

        [Test]
        public void Fallback_roles_always_end_at_unlit()
        {
            foreach (ShaderRole role in System.Enum.GetValues(typeof(ShaderRole)))
            {
                ShaderRole? current = role;
                int steps = 0;
                while (current.HasValue && current.Value != ShaderRole.Unlit && steps < 10)
                {
                    ShaderRole? parent = ParelShaderNames.Parent(current.Value);
                    if (!parent.HasValue) break;
                    current = parent;
                    steps++;
                }
                Assert.Less(steps, 10, role + " loops");
                Assert.AreEqual(ShaderRole.Unlit, current, role + " ends at " + current);
            }
            Assert.IsNull(ParelShaderNames.Parent(ShaderRole.Unlit));
        }

        [Test]
        public void Log_respects_minimum_severity_and_prefix()
        {
            var lines = new List<string>();
            var oldSink = RenderingLog.Sink;
            var oldMin = RenderingLog.MinimumSeverity;
            try
            {
                RenderingLog.Sink = (s, m) => lines.Add(m);
                RenderingLog.MinimumSeverity = DiagnosticSeverity.Warning;
                RenderingLog.Notice("quiet");
                RenderingLog.Warning("loud");
            }
            finally
            {
                RenderingLog.Sink = oldSink;
                RenderingLog.MinimumSeverity = oldMin;
            }
            CollectionAssert.AreEqual(new[] { "[PVRP] loud" }, lines);
        }
    }
}
