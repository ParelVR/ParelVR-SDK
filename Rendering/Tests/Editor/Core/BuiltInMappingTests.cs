using NUnit.Framework;

namespace ParelVR.Rendering.Tests
{
    // Engine-free: these also run outside Unity through Rendering/Tests~/run-core-tests.sh.
    public sealed class BuiltInMappingTests
    {
        private readonly MaterialMapperRegistry _mappers = MaterialConversionPlanner.CreateDefaultRegistry();
        private readonly ShaderClassifier _classifier = new ShaderClassifier();

        private ConversionPlan PlanFor(FakeMaterial m, TargetPlatform target = TargetPlatform.Quest)
        {
            var info = ShaderInfo.Named(m.ShaderName);
            info.LightModes.Add("ForwardBase");
            return MaterialConversionPlanner.Plan(_classifier.Classify(info, target), m, _mappers);
        }

        private FakeMaterial Write(ConversionPlan plan)
        {
            var target = new FakeMaterial(ParelShaderNames.For(plan.Role.Value));
            _mappers.WriterFor(plan.Role.Value).Write(plan.Description, plan.Role.Value, TargetPlatform.Quest, target);
            return target;
        }

        private static FakeMaterial StandardOpaque() => new FakeMaterial("Standard")
            .C("_Color", 0.8f, 0.6f, 0.4f).T("_MainTex", "albedo", 2f, 3f).F("_Cutoff", 0.5f)
            .F("_Glossiness", 0.7f).F("_GlossMapScale", 1f).F("_Metallic", 0.25f).F("_Mode", 0f)
            .T("_BumpMap", "normal").F("_BumpScale", 1.5f).T("_OcclusionMap", "ao").F("_OcclusionStrength", 0.6f)
            .C("_EmissionColor", 2f, 1f, 0f).T("_EmissionMap", "glow")
            .K("_NORMALMAP", "_EMISSION");

        [Test]
        public void Standard_opaque_carries_every_setting_to_lit()
        {
            ConversionPlan plan = PlanFor(StandardOpaque());
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            Assert.AreEqual(ShaderRole.Lit, plan.Role);

            FakeMaterial lit = Write(plan);
            Assert.AreEqual(0.6f, lit.Colors["_Color"].G, 1e-5f);
            Assert.AreEqual("albedo", lit.Tex("_MainTex"));
            Assert.AreEqual(3f, lit.Textures["_MainTex"].ScaleY);
            Assert.AreEqual("normal", lit.Tex("_BumpMap"));
            Assert.AreEqual(1.5f, lit.Floats["_BumpScale"]);
            Assert.AreEqual(0.25f, lit.Floats["_Metallic"]);
            Assert.AreEqual(0.7f, lit.Floats["_Glossiness"], 1e-5f);
            Assert.AreEqual("ao", lit.Tex("_OcclusionMap"));
            Assert.AreEqual(0.6f, lit.Floats["_OcclusionStrength"], 1e-5f);
            Assert.AreEqual(2f, lit.Colors["_EmissionColor"].R);
            Assert.AreEqual("glow", lit.Tex("_EmissionMap"));
            CollectionAssert.IsSubsetOf(new[] { "_NORMALMAP", "_EMISSION" }, lit.Keywords);
            CollectionAssert.DoesNotContain(lit.Keywords, "_METALLICGLOSSMAP");
            Assert.AreEqual(2000, lit.RenderQueue);
            Assert.AreEqual(1f, lit.Floats["_ZWrite"]);
        }

        [Test]
        public void Standard_follows_keywords_not_leftover_textures()
        {
            // A normal map assigned while _NORMALMAP is off doesn't render in Standard, so it isn't carried over.
            FakeMaterial m = StandardOpaque();
            m.Keywords.Remove("_NORMALMAP");
            Assert.IsNull(PlanFor(m).Description.NormalMap);
        }

        [TestCase("_ALPHATEST_ON", ShaderRole.Cutout, SurfaceType.Cutout, 2450)]
        [TestCase("_ALPHABLEND_ON", ShaderRole.Transparent, SurfaceType.Fade, 3000)]
        [TestCase("_ALPHAPREMULTIPLY_ON", ShaderRole.Transparent, SurfaceType.Transparent, 3000)]
        public void Standard_blend_modes_pick_the_right_shader(string keyword, ShaderRole role, SurfaceType surface, int queue)
        {
            FakeMaterial m = StandardOpaque().K(keyword).Q(queue);
            ConversionPlan plan = PlanFor(m);
            Assert.AreEqual(role, plan.Role);
            Assert.AreEqual(surface, plan.Description.Surface);
            FakeMaterial written = Write(plan);
            Assert.AreEqual(queue, written.RenderQueue);
            if (surface == SurfaceType.Fade)
            {
                Assert.AreEqual(BlendFactor.SrcAlpha, written.Floats["_SrcBlend"]);
                Assert.AreEqual(BlendFactor.OneMinusSrcAlpha, written.Floats["_DstBlend"]);
                Assert.AreEqual(0f, written.Floats["_ZWrite"]);
                CollectionAssert.Contains(written.Keywords, "_ALPHABLEND_ON");
            }
            if (surface == SurfaceType.Transparent) CollectionAssert.Contains(written.Keywords, "_ALPHAPREMULTIPLY_ON");
            if (surface == SurfaceType.Cutout) CollectionAssert.Contains(written.Keywords, "_ALPHATEST_ON");
        }

        [Test]
        public void Standard_metallic_map_and_albedo_smoothness()
        {
            FakeMaterial m = StandardOpaque().T("_MetallicGlossMap", "metal").F("_GlossMapScale", 0.4f)
                .K("_METALLICGLOSSMAP", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            ConversionPlan plan = PlanFor(m);
            Assert.AreEqual(SmoothnessSource.AlbedoAlpha, plan.Description.SmoothnessFrom);
            FakeMaterial lit = Write(plan);
            Assert.AreEqual("metal", lit.Tex("_MetallicGlossMap"));
            Assert.AreEqual(0.4f, lit.Floats["_GlossMapScale"], 1e-5f);
            Assert.AreEqual(1f, lit.Floats["_SmoothnessTextureChannel"]);
            CollectionAssert.IsSubsetOf(new[] { "_METALLICGLOSSMAP", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A" }, lit.Keywords);
        }

        [Test]
        public void Standard_specular_setup_keeps_specular_workflow()
        {
            FakeMaterial m = new FakeMaterial("Standard (Specular setup)").C("_Color", 1, 1, 1).T("_MainTex", "a")
                .C("_SpecColor", 0.3f, 0.3f, 0.3f).T("_SpecGlossMap", "spec").F("_GlossMapScale", 0.9f).F("_Glossiness", 0.2f)
                .K("_SPECGLOSSMAP");
            ConversionPlan plan = PlanFor(m);
            Assert.AreEqual(ShadingModel.Specular, plan.Description.Shading);
            FakeMaterial lit = Write(plan);
            CollectionAssert.IsSubsetOf(new[] { "_SPECULAR_SETUP", "_SPECGLOSSMAP" }, lit.Keywords);
            Assert.AreEqual("spec", lit.Tex("_SpecGlossMap"));
            Assert.AreEqual(0.3f, lit.Colors["_SpecColor"].R, 1e-5f);
            Assert.AreEqual(0.9f, lit.Floats["_GlossMapScale"], 1e-5f);
        }

        [Test]
        public void Standard_detail_parallax_and_toggles()
        {
            FakeMaterial m = StandardOpaque().T("_DetailAlbedoMap", "detail").T("_DetailNormalMap", "dn").T("_DetailMask", "dm")
                .F("_DetailNormalMapScale", 0.5f).F("_UVSec", 1f).T("_ParallaxMap", "height").F("_Parallax", 0.05f)
                .K("_DETAIL_MULX2", "_PARALLAXMAP", "_SPECULARHIGHLIGHTS_OFF", "_GLOSSYREFLECTIONS_OFF");
            ConversionPlan plan = PlanFor(m);
            Assert.AreEqual(1, plan.Description.UnmappedFeatures.Count, "second UV set for details is reported");
            Assert.AreEqual(DiagnosticSeverity.Notice, plan.Entry.Severity);
            FakeMaterial lit = Write(plan);
            Assert.AreEqual("detail", lit.Tex("_DetailAlbedoMap"));
            Assert.AreEqual("dm", lit.Tex("_DetailMask"));
            Assert.AreEqual("height", lit.Tex("_ParallaxMap"));
            Assert.AreEqual(0.05f, lit.Floats["_Parallax"], 1e-5f);
            CollectionAssert.IsSubsetOf(new[] { "_DETAIL_MULX2", "_PARALLAXMAP", "_SPECULARHIGHLIGHTS_OFF", "_GLOSSYREFLECTIONS_OFF" }, lit.Keywords);
        }

        [TestCase("Legacy Shaders/Diffuse", ShaderRole.SimpleLit, SurfaceType.Opaque)]
        [TestCase("Legacy Shaders/Bumped Specular", ShaderRole.SimpleLit, SurfaceType.Opaque)]
        [TestCase("Legacy Shaders/Transparent/Diffuse", ShaderRole.SimpleLit, SurfaceType.Fade)]
        [TestCase("Legacy Shaders/Transparent/Cutout/Diffuse", ShaderRole.SimpleLit, SurfaceType.Cutout)]
        public void Legacy_shaders_map_to_simple_lit_family(string shader, ShaderRole role, SurfaceType surface)
        {
            FakeMaterial m = new FakeMaterial(shader).C("_Color", 1, 0, 0).T("_MainTex", "a").T("_BumpMap", "n")
                .C("_SpecColor", 0.5f, 0.5f, 0.5f).F("_Shininess", 0.2f).F("_Cutoff", 0.3f);
            ConversionPlan plan = PlanFor(m);
            Assert.AreEqual(role, plan.Role, shader);
            Assert.AreEqual(surface, plan.Description.Surface);
            FakeMaterial w = Write(plan);
            Assert.AreEqual("a", w.Tex("_MainTex"));
            if (shader.Contains("Bumped")) Assert.AreEqual("n", w.Tex("_BumpMap"));
            if (shader.Contains("Specular")) Assert.AreEqual(0.2f, w.Floats["_Shininess"], 1e-5f);
            if (surface == SurfaceType.Cutout) { Assert.AreEqual(0.3f, w.Floats["_Cutoff"], 1e-5f); CollectionAssert.Contains(w.Keywords, "_ALPHATEST_ON"); }
        }

        [Test]
        public void Legacy_diffuse_has_no_highlights()
        {
            ConversionPlan plan = PlanFor(new FakeMaterial("Legacy Shaders/Diffuse").C("_Color", 1, 1, 1).T("_MainTex", "a"));
            FakeMaterial w = Write(plan);
            Assert.IsTrue(w.Colors["_SpecColor"].IsBlack);
            CollectionAssert.Contains(w.Keywords, "_SPECULARHIGHLIGHTS_OFF");
        }

        [Test]
        public void Legacy_reflective_reports_what_it_drops()
        {
            ConversionPlan plan = PlanFor(new FakeMaterial("Legacy Shaders/Reflective/Bumped Diffuse").C("_Color", 1, 1, 1).T("_MainTex", "a").C("_ReflectColor", 1, 1, 1));
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            StringAssert.Contains("reflection", string.Join(",", plan.Entry.Unmapped));
        }

        [TestCase("Unlit/Texture", SurfaceType.Opaque)]
        [TestCase("Unlit/Transparent", SurfaceType.Fade)]
        [TestCase("Unlit/Transparent Cutout", SurfaceType.Cutout)]
        public void Unlit_shaders_stay_unlit(string shader, SurfaceType surface)
        {
            // Unlit/* is Direct on both targets; plan it as a stand-in to check the reader.
            var m = new FakeMaterial(shader).T("_MainTex", "a").C("_Color", 1, 1, 1).F("_Cutoff", 0.4f);
            MaterialDescription d = new BuiltInUnlitReader().Read(m, ShaderFamily.BuiltInUnlit);
            Assert.AreEqual(ShadingModel.Unlit, d.Shading);
            Assert.AreEqual(surface, d.Surface);
            Assert.AreEqual(ShaderRole.Unlit, RoleSelector.Select(d, ShaderRole.Lit));
        }

        [Test]
        public void Legacy_particle_tint_is_doubled()
        {
            var m = new FakeMaterial("Legacy Shaders/Particles/Additive").C("_TintColor", 0.5f, 0.25f, 0.5f, 0.5f).T("_MainTex", "spark");
            MaterialDescription d = new BuiltInParticlesReader().Read(m, ShaderFamily.BuiltInParticles);
            Assert.AreEqual(SurfaceType.Additive, d.Surface);
            Assert.AreEqual(1f, d.BaseColor.R, 1e-5f);
            Assert.AreEqual(0.5f, d.BaseColor.G, 1e-5f);
            Assert.AreEqual(1f, d.BaseColor.A, 1e-5f);
            Assert.IsTrue(d.VertexColor);
            Assert.AreEqual(ShaderRole.ParticlesUnlit, RoleSelector.Select(d, ShaderRole.ParticlesUnlit));
        }

        [Test]
        public void Standard_particles_on_quest_convert_with_mode()
        {
            var m = new FakeMaterial("Particles/Standard Unlit").C("_Color", 1, 1, 1).T("_MainTex", "smoke").F("_Mode", 4f).F("_SrcBlend", 5f).F("_DstBlend", 1f).Q(3000).K("_SOFTPARTICLES_ON");
            ConversionPlan plan = PlanFor(m);
            Assert.AreEqual(ShaderRole.ParticlesUnlit, plan.Role);
            Assert.AreEqual(SurfaceType.Additive, plan.Description.Surface);
            StringAssert.Contains("soft particles", string.Join(",", plan.Entry.Unmapped));
            FakeMaterial w = Write(plan);
            Assert.AreEqual(BlendFactor.SrcAlpha, w.Floats["_SrcBlend"]);
            Assert.AreEqual(BlendFactor.One, w.Floats["_DstBlend"]);
        }

        [Test]
        public void Standard_particles_follow_the_blend_that_renders()
        {
            // _Mode says fade, but nothing set the blend: Unity draws it opaque, so the conversion does too.
            var unset = new FakeMaterial("Particles/Standard Unlit").T("_MainTex", "p").F("_Mode", 2f).F("_SrcBlend", 1f).F("_DstBlend", 0f);
            Assert.AreEqual(SurfaceType.Opaque, new BuiltInParticlesReader().Read(unset, ShaderFamily.BuiltInParticles).Surface);
            var fade = new FakeMaterial("Particles/Standard Unlit").T("_MainTex", "p").F("_Mode", 2f).F("_SrcBlend", 5f).F("_DstBlend", 10f).Q(3000);
            Assert.AreEqual(SurfaceType.Fade, new BuiltInParticlesReader().Read(fade, ShaderFamily.BuiltInParticles).Surface);
        }

        [Test]
        public void Converted_queue_is_kept_only_when_it_fits_the_blend()
        {
            var opaqueQueueFade = new MaterialDescription { Surface = SurfaceType.Fade, RenderQueue = 2000, Shading = ShadingModel.Unlit };
            var tunedTransparent = new MaterialDescription { Surface = SurfaceType.Fade, RenderQueue = 3100, Shading = ShadingModel.Unlit };
            var a = new FakeMaterial("x");
            var b = new FakeMaterial("x");
            new ParelMaterialWriter().Write(opaqueQueueFade, ShaderRole.Unlit, TargetPlatform.Pc, a);
            new ParelMaterialWriter().Write(tunedTransparent, ShaderRole.Unlit, TargetPlatform.Pc, b);
            Assert.AreEqual(3000, a.RenderQueue);
            Assert.AreEqual(3100, b.RenderQueue);
        }

        [Test]
        public void Generic_reader_prefers_a_texture_that_is_set()
        {
            var m = new FakeMaterial("Studio/X").T("_BaseMap", "albedo");
            m.Textures["_MainTex"] = new TextureSlot();
            Assert.AreEqual("albedo", new GenericReader().Read(m, ShaderFamily.Unknown).BaseMap.Texture);
        }

        [Test]
        public void Standard_is_kept_on_pc()
        {
            ConversionPlan plan = PlanFor(StandardOpaque(), TargetPlatform.Pc);
            Assert.AreEqual(ConversionAction.Preserved, plan.Action);
            Assert.IsFalse(plan.NeedsNewMaterial);
        }

        [Test]
        public void Unknown_shader_gets_a_stand_in_that_keeps_its_texture_and_colour()
        {
            var m = new FakeMaterial("Studio/Fancy").C("_BaseColor", 0.2f, 0.4f, 0.6f).T("_BaseMap", "albedo").T("_NormalMap", "n")
                .C("_EmissiveColor", 1, 1, 1).F("_AlphaCutoff", 0.3f).Q(2450);
            var info = ShaderInfo.Named("Studio/Fancy");
            info.LightModes.Add("UniversalForward");
            ConversionPlan plan = MaterialConversionPlanner.Plan(_classifier.Classify(info, TargetPlatform.Pc), m, _mappers);

            Assert.AreEqual(ConversionAction.Fallback, plan.Action);
            Assert.AreEqual(DiagnosticSeverity.Warning, plan.Entry.Severity);
            Assert.AreEqual(ShaderRole.Cutout, plan.Role);
            FakeMaterial w = Write(plan);
            Assert.AreEqual("albedo", w.Tex("_MainTex"));
            Assert.AreEqual(0.4f, w.Colors["_Color"].G, 1e-5f);
            Assert.AreEqual("n", w.Tex("_BumpMap"));
            Assert.AreEqual(0.3f, w.Floats["_Cutoff"], 1e-5f);
            CollectionAssert.Contains(w.Keywords, "_EMISSION");
        }

        [Test]
        public void Writer_skips_properties_the_target_shader_lacks()
        {
            ConversionPlan plan = PlanFor(StandardOpaque());
            var unlitLike = new FakeMaterial("ParelVR/Mobile/Unlit") { SinkProperties = new System.Collections.Generic.HashSet<string> { "_Color", "_MainTex", "_Cutoff", "_Cull", "_SrcBlend", "_DstBlend", "_ZWrite" } };
            _mappers.WriterFor(ShaderRole.Lit).Write(plan.Description, ShaderRole.Lit, TargetPlatform.Quest, unlitLike);
            Assert.IsFalse(unlitLike.Floats.ContainsKey("_Metallic"));
            Assert.AreEqual("albedo", unlitLike.Tex("_MainTex"));
        }

        [Test]
        public void Additive_and_multiply_keep_real_alpha()
        {
            Assert.IsTrue(SurfaceState.For(SurfaceType.Additive).AlphaBlend);
            Assert.IsTrue(SurfaceState.For(SurfaceType.Multiply).AlphaBlend);
            Assert.IsFalse(SurfaceState.For(SurfaceType.Opaque).AlphaBlend);
            Assert.AreEqual(SurfaceType.Additive, SurfaceState.FromBlend(BlendFactor.SrcAlpha, BlendFactor.One, false));
            Assert.AreEqual(SurfaceType.Cutout, SurfaceState.FromBlend(BlendFactor.One, BlendFactor.Zero, true));
        }
    }
}
