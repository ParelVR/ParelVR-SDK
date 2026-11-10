using NUnit.Framework;

namespace ParelVR.Rendering.Tests
{
    // Engine-free: these also run outside Unity through Rendering/Tests~/run-core-tests.sh.
    public sealed class RepairAndSkyTests
    {
        private static ShaderClassification C(CompatibilityLevel level, RenderPipelineKind pipeline, ShaderRole? role, ShaderFamily family = ShaderFamily.Unknown) =>
            new ShaderClassification { Level = level, Pipeline = pipeline, SuggestedRole = role, Family = family };

        [Test]
        public void Working_builtin_material_is_kept_under_every_policy()
        {
            var c = C(CompatibilityLevel.Direct, RenderPipelineKind.BuiltIn, null, ShaderFamily.BuiltInStandard);
            foreach (RepairPolicy p in new[] { RepairPolicy.BrokenOnly, RepairPolicy.ForeignPipelines, RepairPolicy.ConvertAll })
                Assert.AreEqual(RepairAction.Keep, RuntimeRepair.Decide(c, false, p));
        }

        [Test]
        public void Broken_material_is_always_converted_when_a_role_fits()
        {
            var c = C(CompatibilityLevel.Unsupported, RenderPipelineKind.Unknown, ShaderRole.Lit, ShaderFamily.ErrorShader);
            Assert.AreEqual(RepairAction.Convert, RuntimeRepair.Decide(c, false, RepairPolicy.BrokenOnly), "the error shader counts as broken by itself");
            var direct = C(CompatibilityLevel.Direct, RenderPipelineKind.BuiltIn, null, ShaderFamily.CustomBuiltIn);
            direct.SuggestedRole = ShaderRole.Unlit;
            Assert.AreEqual(RepairAction.Convert, RuntimeRepair.Decide(direct, true, RepairPolicy.BrokenOnly), "a shader the GPU can't run is repaired even if it is normally fine");
        }

        [Test]
        public void Broken_material_without_any_role_is_hidden_not_left_magenta()
        {
            var decal = C(CompatibilityLevel.Unsupported, RenderPipelineKind.HighDefinition, null, ShaderFamily.HdrpDecal);
            Assert.AreEqual(RepairAction.Hide, RuntimeRepair.Decide(decal, true, RepairPolicy.ForeignPipelines));
            Assert.AreEqual(RepairAction.Keep, RuntimeRepair.Decide(decal, false, RepairPolicy.ForeignPipelines), "a decal that still draws is reported, not hidden");
        }

        [Test]
        public void Foreign_pipelines_convert_by_default_but_builtin_convertibles_only_with_convert_all()
        {
            var urp = C(CompatibilityLevel.Convertible, RenderPipelineKind.Universal, ShaderRole.Lit, ShaderFamily.UrpLit);
            var standardOnQuest = C(CompatibilityLevel.Convertible, RenderPipelineKind.BuiltIn, ShaderRole.Lit, ShaderFamily.BuiltInStandard);
            var custom = C(CompatibilityLevel.Unsupported, RenderPipelineKind.BuiltIn, ShaderRole.Lit, ShaderFamily.CustomBuiltIn);

            Assert.AreEqual(RepairAction.Keep, RuntimeRepair.Decide(urp, false, RepairPolicy.BrokenOnly));
            Assert.AreEqual(RepairAction.Convert, RuntimeRepair.Decide(urp, false, RepairPolicy.ForeignPipelines));
            Assert.AreEqual(RepairAction.Keep, RuntimeRepair.Decide(standardOnQuest, false, RepairPolicy.ForeignPipelines));
            Assert.AreEqual(RepairAction.Convert, RuntimeRepair.Decide(standardOnQuest, false, RepairPolicy.ConvertAll));
            Assert.AreEqual(RepairAction.Keep, RuntimeRepair.Decide(custom, false, RepairPolicy.ConvertAll), "a working custom shader is never replaced");
        }

        [Test]
        public void Linear_to_gamma_inverts_unity_decoding_including_hdr()
        {
            foreach (float v in new[] { 0f, 0.002f, 0.18f, 0.5f, 1f, 4f })
            {
                float g = new ColorValue(v, v, v).LinearToGamma().R;
                // Unity's GammaToLinearSpace (sRGB curve, extended above 1).
                float back = g <= 0.04045f ? g / 12.92f : (float)System.Math.Pow((g + 0.055f) / 1.055f, 2.4);
                Assert.AreEqual(v, back, 1e-4f * System.Math.Max(1f, v), "value " + v);
            }
            Assert.AreEqual(0.5f, new ColorValue(1, 1, 1, 0.5f).LinearToGamma().A, "alpha is not encoded");
        }

        [Test]
        public void Cubemap_sky_maps_onto_parelvr_skybox()
        {
            var sky = new FakeMaterial("Skybox/Cubemap").C("_Tint", 0.4f, 0.5f, 0.6f, 0.5f).F("_Exposure", 1.3f).F("_Rotation", 90f).T("_Tex", "cube");
            MaterialDescription d = new SkyboxReader().Read(sky, ShaderFamily.BuiltInSkybox);
            var target = new FakeMaterial(ParelShaderNames.Skybox);
            new SkyboxWriter().Write(d, ShaderRole.Skybox, TargetPlatform.Pc, target);

            Assert.AreEqual("cube", target.Tex("_Tex"));
            Assert.IsFalse(target.Keywords.Contains("_SKYBOX_PANORAMIC"));
            Assert.AreEqual(1.3f, target.Floats["_Exposure"], 1e-5f);
            Assert.AreEqual(90f, target.Floats["_Rotation"], 1e-5f);
            Assert.AreEqual(0.5f, target.Colors["_Tint"].G, 1e-5f);
            Assert.AreEqual(Queues.Background, target.RenderQueue);
        }

        [Test]
        public void Panoramic_sky_uses_the_panorama_slot()
        {
            var sky = new FakeMaterial("Skybox/Panoramic").T("_MainTex", "pano").F("_Mapping", 1f);
            MaterialDescription d = new SkyboxReader().Read(sky, ShaderFamily.BuiltInSkybox);
            var target = new FakeMaterial(ParelShaderNames.Skybox);
            new SkyboxWriter().Write(d, ShaderRole.Skybox, TargetPlatform.Pc, target);

            Assert.AreEqual("pano", target.Tex("_MainTex"));
            Assert.IsTrue(target.Keywords.Contains("_SKYBOX_PANORAMIC"));
            Assert.AreEqual(0, d.UnmappedFeatures.Count);
        }

        [Test]
        public void Default_registry_has_a_sky_writer()
        {
            MaterialMapperRegistry r = MaterialConversionPlanner.CreateDefaultRegistry();
            Assert.IsInstanceOf<SkyboxWriter>(r.WriterFor(ShaderRole.Skybox));
            Assert.IsInstanceOf<SkyboxReader>(r.ReaderFor(ShaderFamily.BuiltInSkybox));
        }
    }
}
