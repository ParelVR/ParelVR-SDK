using NUnit.Framework;

namespace ParelVR.Rendering.Tests
{
    // Engine-free: these also run outside Unity through Rendering/Tests~/run-core-tests.sh.
    // Property names and defaults are the ones URP 17 and HDRP 17 (Unity 6.4) write into materials.
    public sealed class SrpMappingTests
    {
        private readonly MaterialMapperRegistry _mappers = MaterialConversionPlanner.CreateDefaultRegistry();
        private readonly ShaderClassifier _classifier = new ShaderClassifier();

        [TearDown]
        public void ResetExposure() => LightingConversion.SceneEv100 = LightingConversion.DefaultEv100;

        private ConversionPlan Plan(FakeMaterial m, string pipelineTag, TargetPlatform target = TargetPlatform.Pc)
        {
            var info = ShaderInfo.Named(m.ShaderName);
            info.RenderPipelineTag = pipelineTag;
            return MaterialConversionPlanner.Plan(_classifier.Classify(info, target), m, _mappers);
        }

        private FakeMaterial Write(ConversionPlan plan)
        {
            var target = new FakeMaterial(ParelShaderNames.For(plan.Role.Value));
            _mappers.WriterFor(plan.Role.Value).Write(plan.Description, plan.Role.Value, TargetPlatform.Pc, target);
            return target;
        }

        // ---- URP ------------------------------------------------------------------------------------------------

        private static FakeMaterial UrpLit() => new FakeMaterial("Universal Render Pipeline/Lit")
            .F("_WorkflowMode", 1f).F("_Surface", 0f).F("_Blend", 0f).F("_AlphaClip", 0f).F("_Cutoff", 0.5f).F("_Cull", 2f)
            .C("_BaseColor", 0.9f, 0.5f, 0.25f).T("_BaseMap", "albedo", 4f, 4f).F("_Smoothness", 0.6f).F("_Metallic", 0.3f)
            .F("_SmoothnessTextureChannel", 0f).T("_BumpMap", "normal").F("_BumpScale", 0.8f)
            .T("_OcclusionMap", "ao").F("_OcclusionStrength", 0.7f).C("_EmissionColor", 0.5f, 0f, 0f).T("_EmissionMap", "glow")
            .F("_SpecularHighlights", 1f).F("_EnvironmentReflections", 1f).F("_ReceiveShadows", 1f)
            .K("_NORMALMAP", "_EMISSION");

        [Test]
        public void Urp_lit_opaque_converts_exactly_to_parelvr_lit()
        {
            ConversionPlan plan = Plan(UrpLit(), "UniversalPipeline");
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            Assert.AreEqual(ShaderRole.Lit, plan.Role);
            Assert.IsEmpty(plan.Description.UnmappedFeatures);

            FakeMaterial lit = Write(plan);
            Assert.AreEqual("albedo", lit.Tex("_MainTex"));
            Assert.AreEqual(4f, lit.Textures["_MainTex"].ScaleX);
            Assert.AreEqual(0.5f, lit.Colors["_Color"].G, 1e-5f);
            Assert.AreEqual(0.3f, lit.Floats["_Metallic"], 1e-5f);
            Assert.AreEqual(0.6f, lit.Floats["_Glossiness"], 1e-5f);
            Assert.AreEqual("normal", lit.Tex("_BumpMap"));
            Assert.AreEqual(0.8f, lit.Floats["_BumpScale"], 1e-5f);
            Assert.AreEqual("ao", lit.Tex("_OcclusionMap"));
            Assert.IsTrue(lit.Keywords.Contains("_NORMALMAP"));
            Assert.IsTrue(lit.Keywords.Contains("_EMISSION"));
            // URP's [HDR] emission 0.5 (linear) is stored so that Unity's sRGB decode gives 0.5 back.
            Assert.AreEqual(new ColorValue(0.5f, 0, 0).LinearToGamma().R, lit.Colors["_EmissionColor"].R, 1e-5f);
            Assert.Greater(lit.Colors["_EmissionColor"].R, 0.7f);
        }

        [Test]
        public void Urp_metallic_map_and_albedo_smoothness()
        {
            ConversionPlan plan = Plan(UrpLit().T("_MetallicGlossMap", "metal").F("_SmoothnessTextureChannel", 1f), "UniversalPipeline");
            FakeMaterial lit = Write(plan);
            Assert.AreEqual("metal", lit.Tex("_MetallicGlossMap"));
            Assert.IsTrue(lit.Keywords.Contains("_METALLICGLOSSMAP"));
            Assert.IsTrue(lit.Keywords.Contains("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"));
            Assert.AreEqual(0.6f, lit.Floats["_GlossMapScale"], 1e-5f);
        }

        [Test]
        public void Urp_specular_workflow_uses_the_specular_setup()
        {
            ConversionPlan plan = Plan(UrpLit().F("_WorkflowMode", 0f).C("_SpecColor", 0.3f, 0.3f, 0.3f).T("_SpecGlossMap", "spec"), "UniversalPipeline");
            FakeMaterial lit = Write(plan);
            Assert.IsTrue(lit.Keywords.Contains("_SPECULAR_SETUP"));
            Assert.IsTrue(lit.Keywords.Contains("_SPECGLOSSMAP"));
            Assert.AreEqual("spec", lit.Tex("_SpecGlossMap"));
            Assert.AreEqual(0.3f, lit.Colors["_SpecColor"].R, 1e-5f);
        }

        [TestCase(0f, 1f, SurfaceType.Transparent)]
        [TestCase(0f, 0f, SurfaceType.Fade)]
        [TestCase(1f, 1f, SurfaceType.Transparent)]
        [TestCase(2f, 1f, SurfaceType.Additive)]
        [TestCase(3f, 1f, SurfaceType.Multiply)]
        public void Urp_transparent_blend_modes(float blend, float preserveSpecular, SurfaceType expected)
        {
            ConversionPlan plan = Plan(UrpLit().F("_Surface", 1f).F("_Blend", blend).F("_BlendModePreserveSpecular", preserveSpecular).Q(3000), "UniversalPipeline");
            Assert.AreEqual(expected, plan.Description.Surface);
            Assert.AreEqual(ShaderRole.Transparent, plan.Role);
            Assert.AreEqual(3000, Write(plan).RenderQueue);
        }

        [Test]
        public void Urp_alpha_clip_becomes_cutout()
        {
            ConversionPlan plan = Plan(UrpLit().F("_AlphaClip", 1f).F("_Cutoff", 0.3f).F("_Cull", 0f).Q(2450), "UniversalPipeline");
            Assert.AreEqual(ShaderRole.Cutout, plan.Role);
            FakeMaterial cut = Write(plan);
            Assert.AreEqual(0.3f, cut.Floats["_Cutoff"], 1e-5f);
            Assert.AreEqual(0f, cut.Floats["_Cull"]);
            Assert.AreEqual("TransparentCutout", cut.Tags["RenderType"]);
        }

        [Test]
        public void Urp_emission_switched_off_is_not_carried()
        {
            FakeMaterial m = UrpLit();
            m.Keywords.Remove("_EMISSION");
            Assert.IsFalse(Plan(m, "UniversalPipeline").Description.EmissionEnabled);
        }

        [Test]
        public void Urp_simple_lit_maps_smoothness_to_shininess()
        {
            var m = new FakeMaterial("Universal Render Pipeline/Simple Lit").C("_BaseColor", 1, 1, 1).T("_BaseMap", "a")
                .C("_SpecColor", 0.4f, 0.4f, 0.4f).F("_Smoothness", 0.5f).F("_SpecularHighlights", 1f).F("_SmoothnessSource", 0f);
            ConversionPlan plan = Plan(m, "UniversalPipeline");
            Assert.AreEqual(ShaderRole.SimpleLit, plan.Role);
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            FakeMaterial simple = Write(plan);
            Assert.AreEqual(System.Math.Pow(2, 6) / 128.0, simple.Floats["_Shininess"], 1e-4);
            Assert.AreEqual(0.4f, simple.Colors["_SpecColor"].R, 1e-5f);
        }

        [Test]
        public void Urp_unlit_and_particles()
        {
            ConversionPlan unlit = Plan(new FakeMaterial("Universal Render Pipeline/Unlit").C("_BaseColor", 0, 1, 0).T("_BaseMap", "u").F("_Surface", 1f).F("_Blend", 0f).Q(3000), "UniversalPipeline");
            Assert.AreEqual(ShaderRole.Unlit, unlit.Role);
            Assert.AreEqual(SurfaceType.Fade, unlit.Description.Surface);
            Assert.AreEqual("u", Write(unlit).Tex("_MainTex"));

            ConversionPlan particles = Plan(new FakeMaterial("Universal Render Pipeline/Particles/Unlit").C("_BaseColor", 1, 1, 1).T("_BaseMap", "p")
                .F("_Surface", 1f).F("_Blend", 2f).F("_SoftParticlesEnabled", 1f).Q(3000), "UniversalPipeline");
            Assert.AreEqual(ShaderRole.ParticlesUnlit, particles.Role);
            Assert.AreEqual(SurfaceType.Additive, particles.Description.Surface);
            Assert.IsTrue(particles.Description.VertexColor);
            CollectionAssert.Contains(particles.Description.UnmappedFeatures, "soft particles");
        }

        [Test]
        public void Urp_material_with_a_missing_shader_is_recognised_from_its_properties()
        {
            FakeMaterial m = UrpLit();
            m.ShaderName = string.Empty;
            var info = new ShaderInfo { Name = string.Empty, IsMissing = true };
            foreach (string p in new[] { "_BaseMap", "_BaseColor", "_WorkflowMode", "_Smoothness", "_Metallic", "_Surface", "_BumpMap" }) info.Properties.Add(new ShaderPropertyInfo(p, ShaderPropertyKind.Float));
            ShaderClassification c = _classifier.Classify(info, TargetPlatform.Pc);
            Assert.AreEqual(ShaderFamily.UrpLit, c.Family);
            ConversionPlan plan = MaterialConversionPlanner.Plan(c, m, _mappers);
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            Assert.AreEqual("albedo", Write(plan).Tex("_MainTex"));
        }

        // ---- HDRP -----------------------------------------------------------------------------------------------

        private static FakeMaterial HdrpLit() => new FakeMaterial("HDRP/Lit")
            .F("_SurfaceType", 0f).F("_BlendMode", 0f).F("_AlphaCutoffEnable", 0f).F("_AlphaCutoff", 0.5f).F("_DoubleSidedEnable", 0f)
            .F("_MaterialID", 1f).C("_BaseColor", 1f, 0.8f, 0.6f).T("_BaseColorMap", "base", 2f, 2f).F("_Metallic", 0f).F("_Smoothness", 0.5f)
            .T("_MaskMap", "mask").F("_MetallicRemapMin", 0f).F("_MetallicRemapMax", 1f).F("_SmoothnessRemapMin", 0f).F("_SmoothnessRemapMax", 0.9f)
            .F("_AORemapMin", 0f).F("_AORemapMax", 1f).T("_NormalMap", "normal").F("_NormalScale", 1.2f)
            .C("_EmissiveColor", 0f, 0f, 0f).F("_EmissiveExposureWeight", 1f).F("_UVBase", 0f)
            .K("_NORMALMAP", "_MASKMAP");

        [Test]
        public void Hdrp_lit_mask_map_goes_into_both_slots_without_rebaking()
        {
            ConversionPlan plan = Plan(HdrpLit(), "HDRenderPipeline");
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            Assert.AreEqual(ShaderRole.Lit, plan.Role);
            Assert.IsEmpty(plan.Description.UnmappedFeatures);

            FakeMaterial lit = Write(plan);
            Assert.AreEqual("base", lit.Tex("_MainTex"));
            Assert.AreEqual(2f, lit.Textures["_MainTex"].ScaleX);
            Assert.AreEqual("mask", lit.Tex("_MetallicGlossMap"), "R metallic, A smoothness");
            Assert.AreEqual("mask", lit.Tex("_OcclusionMap"), "G occlusion");
            Assert.IsTrue(lit.Keywords.Contains("_METALLICGLOSSMAP"));
            Assert.AreEqual(0.9f, lit.Floats["_GlossMapScale"], 1e-5f);
            Assert.AreEqual(1f, lit.Floats["_OcclusionStrength"], 1e-5f);
            Assert.AreEqual("normal", lit.Tex("_BumpMap"));
            Assert.AreEqual(1.2f, lit.Floats["_BumpScale"], 1e-5f);
            Assert.IsFalse(lit.Keywords.Contains("_EMISSION"));
        }

        [Test]
        public void Hdrp_lit_without_mask_map_uses_its_values()
        {
            FakeMaterial m = HdrpLit().F("_Metallic", 0.4f).F("_Smoothness", 0.3f);
            m.Textures.Remove("_MaskMap");
            FakeMaterial lit = Write(Plan(m, "HDRenderPipeline"));
            Assert.IsFalse(lit.Keywords.Contains("_METALLICGLOSSMAP"));
            Assert.AreEqual(0.4f, lit.Floats["_Metallic"], 1e-5f);
            Assert.AreEqual(0.3f, lit.Floats["_Glossiness"], 1e-5f);
        }

        [Test]
        public void Hdrp_emission_is_brought_to_screen_brightness_by_the_scene_exposure()
        {
            // 1000 nits of white at EV 9: HDRP shows 1000 / (1.2 × 512) ≈ 1.63 on screen.
            LightingConversion.SceneEv100 = 9f;
            ConversionPlan plan = Plan(HdrpLit().C("_EmissiveColor", 1000f, 1000f, 1000f).T("_EmissiveColorMap", "e"), "HDRenderPipeline");
            Assert.IsTrue(plan.Description.EmissionEnabled);
            FakeMaterial lit = Write(plan);
            float stored = lit.Colors["_EmissionColor"].R;
            float decoded = stored <= 0.04045f ? stored / 12.92f : (float)System.Math.Pow((stored + 0.055f) / 1.055f, 2.4);
            Assert.AreEqual(1000f / (1.2f * 512f), decoded, 1e-3f);
            Assert.AreEqual("e", lit.Tex("_EmissionMap"));

            LightingConversion.SceneEv100 = 9f;
            ConversionPlan raw = Plan(HdrpLit().C("_EmissiveColor", 2f, 2f, 2f).F("_EmissiveExposureWeight", 0f), "HDRenderPipeline");
            Assert.AreEqual(new ColorValue(2f, 2f, 2f).LinearToGamma().R, raw.Description.EmissionColor.R, 1e-5f, "exposure weight 0 shows the value as it is");
        }

        [TestCase(0f, 1f, SurfaceType.Transparent)]
        [TestCase(0f, 0f, SurfaceType.Fade)]
        [TestCase(1f, 1f, SurfaceType.Additive)]
        [TestCase(4f, 1f, SurfaceType.Transparent)]
        public void Hdrp_transparent_blend_modes(float blend, float preserve, SurfaceType expected)
        {
            ConversionPlan plan = Plan(HdrpLit().F("_SurfaceType", 1f).F("_BlendMode", blend).F("_EnableBlendModePreserveSpecularLighting", preserve).Q(3000), "HDRenderPipeline");
            Assert.AreEqual(expected, plan.Description.Surface);
            Assert.AreEqual(ShaderRole.Transparent, plan.Role);
        }

        [Test]
        public void Hdrp_alpha_clip_double_sided_and_features_are_reported()
        {
            ConversionPlan plan = Plan(HdrpLit().F("_AlphaCutoffEnable", 1f).F("_AlphaCutoff", 0.4f).F("_DoubleSidedEnable", 1f).F("_MaterialID", 2f).T("_DetailMap", "d").F("_UVBase", 5f), "HDRenderPipeline");
            Assert.AreEqual(ShaderRole.Cutout, plan.Role);
            Assert.AreEqual(FaceCull.Off, plan.Description.Cull);
            Assert.AreEqual(0.4f, plan.Description.AlphaCutoff, 1e-5f);
            Assert.AreEqual(DiagnosticSeverity.Notice, plan.Entry.Severity, "converted, with differences to look at");
            string all = string.Join("|", plan.Description.UnmappedFeatures);
            StringAssert.Contains("anisotropic", all);
            StringAssert.Contains("detail map", all);
            StringAssert.Contains("triplanar", all);
            StringAssert.Contains("double-sided", all);
        }

        [Test]
        public void Hdrp_specular_colour_material_uses_the_specular_setup()
        {
            ConversionPlan plan = Plan(HdrpLit().F("_MaterialID", 4f).C("_SpecularColor", 0.5f, 0.4f, 0.3f), "HDRenderPipeline");
            Assert.AreEqual(ShadingModel.Specular, plan.Description.Shading);
            FakeMaterial lit = Write(plan);
            Assert.IsTrue(lit.Keywords.Contains("_SPECULAR_SETUP"));
            Assert.AreEqual(0.4f, lit.Colors["_SpecColor"].G, 1e-5f);
        }

        [Test]
        public void Hdrp_layered_lit_keeps_every_layer()
        {
            var m = new FakeMaterial("HDRP/LayeredLit").F("_LayerCount", 3f).C("_BaseColor0", 0.2f, 0.4f, 0.6f).T("_BaseColorMap0", "layer0")
                .T("_BaseColorMap1", "layer1").T("_NormalMap0", "n0").F("_Smoothness0", 0.7f).F("_Metallic1", 0.9f)
                .T("_MaskMap2", "mask2").F("_SmoothnessRemapMin2", 0.2f).F("_SmoothnessRemapMax2", 0.8f)
                .T("_LayerMaskMap", "lm").F("_VertexColorMode", 1f);
            ConversionPlan plan = Plan(m, "HDRenderPipeline");
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            Assert.AreEqual(ShaderRole.LayeredLit, plan.Role);
            Assert.AreEqual(3, plan.Description.Layers.Count);
            FakeMaterial lit = Write(plan);
            Assert.AreEqual(3f, lit.Floats["_LayerCount"]);
            Assert.AreEqual(1f, lit.Floats["_LayerVertexColorMode"]);
            Assert.AreEqual("lm", lit.Tex("_LayerMaskMap"));
            Assert.AreEqual("layer0", lit.Tex("_Layer0Map"));
            Assert.AreEqual("layer1", lit.Tex("_Layer1Map"));
            Assert.AreEqual("n0", lit.Tex("_Layer0NormalMap"));
            Assert.AreEqual("layer0", lit.Tex("_MainTex"), "layer 0 also fills the fallback slots");
            Assert.AreEqual(0.4f, lit.Colors["_Layer0Color"].G, 1e-5f);
            Assert.AreEqual(0.7f, lit.Floats["_Layer0SmoothnessMax"], 1e-5f, "no mask map: min = max = the value");
            Assert.AreEqual(0.9f, lit.Floats["_Layer1MetallicMin"], 1e-5f);
            Assert.AreEqual("mask2", lit.Tex("_Layer2MaskMap"));
            Assert.AreEqual(0.2f, lit.Floats["_Layer2SmoothnessMin"], 1e-5f);
            Assert.AreEqual(0.8f, lit.Floats["_Layer2SmoothnessMax"], 1e-5f);
            StringAssert.DoesNotContain("layers 2", string.Join("|", plan.Description.UnmappedFeatures));
        }

        [Test]
        public void Hdrp_subsurface_scattering_uses_the_subsurface_shader()
        {
            ConversionPlan plan = Plan(HdrpLit().F("_MaterialID", 0f).F("_SubsurfaceMask", 0.8f).T("_ThicknessMap", "thick")
                .C("_ThicknessRemap", 0.1f, 0.9f, 0f, 0f).F("_TransmissionEnable", 1f).F("_AlphaCutoffEnable", 1f), "HDRenderPipeline");
            Assert.AreEqual(ShaderRole.Subsurface, plan.Role, "cutout subsurface stays subsurface (the shader has alpha test)");
            Assert.IsTrue(plan.Description.Subsurface);
            Assert.IsTrue(plan.Description.Transmission);
            FakeMaterial m = Write(plan);
            Assert.AreEqual(0.8f, m.Floats["_SubsurfaceMask"], 1e-5f);
            Assert.AreEqual(0.1f, m.Floats["_ThicknessMin"], 1e-5f);
            Assert.AreEqual(0.9f, m.Floats["_ThicknessMax"], 1e-5f);
            Assert.AreEqual(1f, m.Floats["_TransmissionScale"]);
            Assert.AreEqual("thick", m.Tex("_ThicknessMap"));
            Assert.IsTrue(m.Keywords.Contains("_ALPHATEST_ON"));
            StringAssert.DoesNotContain("subsurface", string.Join("|", plan.Description.UnmappedFeatures));
        }

        [Test]
        public void Hdrp_translucent_transmits_without_scattering_and_transparent_subsurface_is_reported()
        {
            ConversionPlan leaf = Plan(HdrpLit().F("_MaterialID", 5f).F("_Thickness", 0.2f), "HDRenderPipeline");
            Assert.AreEqual(ShaderRole.Subsurface, leaf.Role);
            FakeMaterial m = Write(leaf);
            Assert.AreEqual(0f, m.Floats["_SubsurfaceMask"]);
            Assert.AreEqual(1f, m.Floats["_TransmissionScale"]);
            Assert.AreEqual(0.2f, m.Floats["_ThicknessMax"], 1e-5f);
            Assert.IsFalse(m.Textures.ContainsKey("_ThicknessMap"), "no thickness map: the value is used");

            ConversionPlan glass = Plan(HdrpLit().F("_MaterialID", 0f).F("_SurfaceType", 1f), "HDRenderPipeline");
            Assert.AreEqual(ShaderRole.Transparent, glass.Role);
            StringAssert.Contains("transparent surface", string.Join("|", glass.Description.UnmappedFeatures));
        }

        [Test]
        public void Hdrp_unlit_converts_to_unlit()
        {
            ConversionPlan plan = Plan(new FakeMaterial("HDRP/Unlit").C("_UnlitColor", 0.1f, 0.2f, 0.3f).T("_UnlitColorMap", "u").F("_SurfaceType", 0f), "HDRenderPipeline");
            Assert.AreEqual(ShaderRole.Unlit, plan.Role);
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            FakeMaterial unlit = Write(plan);
            Assert.AreEqual("u", unlit.Tex("_MainTex"));
            Assert.AreEqual(0.3f, unlit.Colors["_Color"].B, 1e-5f);
        }

        [Test]
        public void Terrain_materials_convert_to_the_terrain_role()
        {
            ConversionPlan plan = Plan(new FakeMaterial("HDRP/TerrainLit"), "HDRenderPipeline");
            Assert.AreEqual(ShaderRole.Terrain, plan.Role);
            Assert.AreEqual(ConversionAction.Converted, plan.Action);
            Assert.IsNotNull(_mappers.WriterFor(ShaderRole.Terrain));
        }

        // ---- lighting -------------------------------------------------------------------------------------------

        [Test]
        public void Sunlight_converts_to_about_one()
        {
            float ev = LightingConversion.Ev100FromIlluminance(100000f);
            Assert.AreEqual(15.29f, ev, 0.01f);
            float intensity = LightingConversion.DirectionalIntensity(100000f, ev);
            // E / π × 1 / (1.2 × E / 2.5) = 2.5 / (1.2 π), whatever the sun's strength: exposure follows the light.
            Assert.AreEqual(2.5f / (1.2f * (float)System.Math.PI), intensity, 0.001f);
            Assert.That(intensity, Is.InRange(0.05f, 2f));
        }

        [Test]
        public void Punctual_and_sky_and_fog_conversions()
        {
            // A 600 lm bulb (≈ 48 cd) with a 10 m range in a dim room (EV 4); one EV brighter halves it.
            float i = LightingConversion.PunctualIntensity(47.7f, 10f, 4f);
            Assert.That(i, Is.InRange(0.05f, 3f));
            Assert.AreEqual(i / 2f, LightingConversion.PunctualIntensity(47.7f, 10f, 5f), 1e-5f);
            Assert.AreEqual(1f, LightingConversion.SkyExposure(1f, 9f, 9f) * 1.2f, 1e-4f);
            Assert.AreEqual(0.01f, LightingConversion.FogDensity(100f), 1e-6f);
            Assert.AreEqual(1f, LightingConversion.EmissionScale(0f, 12f), 1e-6f);
        }
    }
}
