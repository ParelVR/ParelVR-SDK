using NUnit.Framework;

namespace ParelVR.Rendering.Tests
{
    // Engine-free: these also run outside Unity through Rendering/Tests~/run-core-tests.sh.
    public sealed class ShaderClassifierTests
    {
        private readonly ShaderClassifier _classifier = new ShaderClassifier();

        private ShaderClassification Classify(ShaderInfo info, TargetPlatform target = TargetPlatform.Pc) => _classifier.Classify(info, target);

        private static ShaderInfo BuiltInShader(string name)
        {
            var info = ShaderInfo.Named(name);
            info.LightModes.Add("ForwardBase");
            info.LightModes.Add("ShadowCaster");
            return info;
        }

        [TestCase("Standard", ShaderFamily.BuiltInStandard)]
        [TestCase("Standard (Specular setup)", ShaderFamily.BuiltInStandardSpecular)]
        [TestCase("Legacy Shaders/Diffuse", ShaderFamily.BuiltInLegacyDiffuse)]
        [TestCase("Legacy Shaders/Specular", ShaderFamily.BuiltInLegacySpecular)]
        [TestCase("Legacy Shaders/Bumped Diffuse", ShaderFamily.BuiltInLegacyBumpedDiffuse)]
        [TestCase("Legacy Shaders/Bumped Specular", ShaderFamily.BuiltInLegacyBumpedSpecular)]
        [TestCase("Legacy Shaders/Transparent/Diffuse", ShaderFamily.BuiltInLegacyTransparent)]
        [TestCase("Legacy Shaders/Transparent/Cutout/Diffuse", ShaderFamily.BuiltInLegacyCutout)]
        [TestCase("Legacy Shaders/Particles/Additive", ShaderFamily.BuiltInParticles)]
        [TestCase("Legacy Shaders/VertexLit", ShaderFamily.BuiltInLegacyOther)]
        [TestCase("Unlit/Color", ShaderFamily.BuiltInUnlit)]
        [TestCase("Unlit/Texture", ShaderFamily.BuiltInUnlit)]
        [TestCase("Particles/Standard Unlit", ShaderFamily.BuiltInParticles)]
        [TestCase("Mobile/Particles/Additive", ShaderFamily.BuiltInParticles)]
        [TestCase("Mobile/Diffuse", ShaderFamily.BuiltInMobile)]
        [TestCase("Nature/Terrain/Standard", ShaderFamily.BuiltInTerrain)]
        [TestCase("Nature/Tree Soft Occlusion Leaves", ShaderFamily.BuiltInNature)]
        [TestCase("Skybox/Procedural", ShaderFamily.BuiltInSkybox)]
        [TestCase("UI/Default", ShaderFamily.BuiltInUI)]
        [TestCase("Sprites/Default", ShaderFamily.BuiltInSprites)]
        [TestCase("GUI/Text Shader", ShaderFamily.BuiltInUI)]
        [TestCase("Hidden/InternalErrorShader", ShaderFamily.ErrorShader)]
        [TestCase("Universal Render Pipeline/Lit", ShaderFamily.UrpLit)]
        [TestCase("Universal Render Pipeline/Simple Lit", ShaderFamily.UrpSimpleLit)]
        [TestCase("Universal Render Pipeline/Unlit", ShaderFamily.UrpUnlit)]
        [TestCase("Universal Render Pipeline/Particles/Unlit", ShaderFamily.UrpParticlesUnlit)]
        [TestCase("Universal Render Pipeline/Terrain/Lit", ShaderFamily.UrpTerrain)]
        [TestCase("Universal Render Pipeline/Nature/SpeedTree8", ShaderFamily.UrpOther)]
        [TestCase("HDRP/Lit", ShaderFamily.HdrpLit)]
        [TestCase("HDRP/LitTessellation", ShaderFamily.HdrpLit)]
        [TestCase("HDRP/LayeredLit", ShaderFamily.HdrpLayeredLit)]
        [TestCase("HDRP/Unlit", ShaderFamily.HdrpUnlit)]
        [TestCase("HDRP/TerrainLit", ShaderFamily.HdrpTerrain)]
        [TestCase("HDRP/Decal", ShaderFamily.HdrpDecal)]
        [TestCase("HDRP/StackLit", ShaderFamily.HdrpOther)]
        [TestCase("TextMeshPro/Distance Field", ShaderFamily.TextMeshPro)]
        [TestCase(".poiyomi/Poiyomi Toon", ShaderFamily.Poiyomi)]
        [TestCase("Hidden/Locked/.poiyomi/Poiyomi Toon/abc123", ShaderFamily.Poiyomi)]
        [TestCase("lilToon", ShaderFamily.LilToon)]
        [TestCase("Hidden/lilToonCutout", ShaderFamily.LilToon)]
        [TestCase("VRM10/MToon10", ShaderFamily.MToon)]
        [TestCase("UniGLTF/UniUnlit", ShaderFamily.UniUnlit)]
        [TestCase("Shader Graphs/MyGraph", ShaderFamily.ShaderGraph)]
        [TestCase("ParelVR/Lit", ShaderFamily.ParelVR)]
        [TestCase("ParelVR/Mobile/Toon Lit", ShaderFamily.ParelVRMobile)]
        public void Names_map_to_families(string name, ShaderFamily expected)
        {
            Assert.AreEqual(expected, ShaderClassifier.IdentifyByName(name));
        }

        [Test]
        public void Standard_is_direct_on_pc_and_converted_on_quest()
        {
            ShaderInfo standard = BuiltInShader("Standard");
            ShaderClassification pc = Classify(standard, TargetPlatform.Pc);
            ShaderClassification quest = Classify(standard, TargetPlatform.Quest);

            Assert.AreEqual(CompatibilityLevel.Direct, pc.Level);
            Assert.AreEqual(RenderPipelineKind.BuiltIn, pc.Pipeline);
            Assert.IsNull(pc.SuggestedRole);
            Assert.AreEqual(CompatibilityLevel.Convertible, quest.Level);
            Assert.AreEqual(ShaderRole.Lit, quest.SuggestedRole);
        }

        [TestCase("Universal Render Pipeline/Lit", ShaderRole.Lit)]
        [TestCase("Universal Render Pipeline/Simple Lit", ShaderRole.SimpleLit)]
        [TestCase("Universal Render Pipeline/Unlit", ShaderRole.Unlit)]
        [TestCase("Universal Render Pipeline/Particles/Lit", ShaderRole.ParticlesLit)]
        [TestCase("HDRP/Lit", ShaderRole.Lit)]
        [TestCase("HDRP/LayeredLit", ShaderRole.Lit)]
        [TestCase("HDRP/Unlit", ShaderRole.Unlit)]
        public void Known_srp_shaders_are_convertible(string name, ShaderRole role)
        {
            ShaderClassification c = Classify(ShaderInfo.Named(name));
            Assert.AreEqual(CompatibilityLevel.Convertible, c.Level);
            Assert.AreEqual(role, c.SuggestedRole);
        }

        [Test]
        public void Error_shader_is_unsupported_with_a_fallback_role()
        {
            ShaderClassification c = Classify(ShaderInfo.Named(ShaderClassifier.ErrorShaderName));
            Assert.AreEqual(CompatibilityLevel.Unsupported, c.Level);
            Assert.IsTrue(c.SuggestedRole.HasValue);
        }

        [Test]
        public void Unsupported_on_this_gpu_is_unsupported()
        {
            ShaderInfo info = BuiltInShader("Standard");
            info.IsSupported = false;
            Assert.AreEqual(CompatibilityLevel.Unsupported, Classify(info).Level);
        }

        [Test]
        public void Unknown_builtin_custom_shader_is_direct_but_stereo_unverified()
        {
            ShaderClassification c = Classify(BuiltInShader("Studio/Fancy Glass"));
            Assert.AreEqual(ShaderFamily.CustomBuiltIn, c.Family);
            Assert.AreEqual(CompatibilityLevel.Direct, c.Level);
            Assert.IsTrue(c.StereoUnverified);
        }

        [Test]
        public void Unknown_custom_shader_on_quest_gets_a_fallback()
        {
            ShaderClassification c = Classify(BuiltInShader("Studio/Fancy Glass"), TargetPlatform.Quest);
            Assert.AreEqual(CompatibilityLevel.Unsupported, c.Level);
            Assert.IsTrue(c.SuggestedRole.HasValue);
        }

        [Test]
        public void Unknown_urp_custom_shader_is_detected_from_light_modes()
        {
            var info = ShaderInfo.Named("Studio/URP Water");
            info.LightModes.Add("UniversalForward");
            ShaderClassification c = Classify(info);
            Assert.AreEqual(ShaderFamily.UrpOther, c.Family);
            Assert.AreEqual(RenderPipelineKind.Universal, c.Pipeline);
            Assert.AreEqual(CompatibilityLevel.Unsupported, c.Level);
            Assert.AreEqual(ShaderRole.Water, c.SuggestedRole);
        }

        [Test]
        public void Unknown_hdrp_custom_shader_is_detected_from_pipeline_tag()
        {
            var info = ShaderInfo.Named("Studio/HD Thing");
            info.RenderPipelineTag = "HDRenderPipeline";
            info.LightModes.Add("Forward");
            Assert.AreEqual(RenderPipelineKind.HighDefinition, Classify(info).Pipeline);
        }

        [Test]
        public void Builtin_light_mode_wins_over_an_srp_subshader_tag()
        {
            // A shader with a URP SubShader and a Built-in one: the inspector reports no pipeline tag,
            // and the Built-in passes mean it runs here.
            var info = ShaderInfo.Named("Studio/Dual");
            info.LightModes.Add("UniversalForward");
            info.LightModes.Add("ForwardBase");
            Assert.AreEqual(RenderPipelineKind.BuiltIn, ShaderClassifier.PipelineFromTags(info));
        }

        [Test]
        public void Pass_without_light_mode_runs_under_any_pipeline()
        {
            var info = ShaderInfo.Named("Studio/Plain Unlit");
            Assert.AreEqual(RenderPipelineKind.Any, ShaderClassifier.PipelineFromTags(info));
            Assert.AreEqual(CompatibilityLevel.Direct, Classify(info).Level);
        }

        [Test]
        public void Unlocked_poiyomi_needs_creator_action_and_locked_does_not()
        {
            ShaderClassification unlocked = Classify(BuiltInShader(".poiyomi/Poiyomi Toon"));
            ShaderClassification locked = Classify(BuiltInShader("Hidden/Locked/.poiyomi/Poiyomi Toon/abc"));
            Assert.IsTrue(unlocked.NeedsCreatorAction);
            Assert.IsFalse(locked.NeedsCreatorAction);
            Assert.AreEqual(CompatibilityLevel.Direct, locked.Level);
        }

        [Test]
        public void Avatar_toon_shaders_become_toon_on_quest()
        {
            Assert.AreEqual(ShaderRole.Toon, Classify(BuiltInShader("lilToon"), TargetPlatform.Quest).SuggestedRole);
            Assert.AreEqual(ShaderRole.Toon, Classify(BuiltInShader("VRM10/MToon10"), TargetPlatform.Quest).SuggestedRole);
        }

        [Test]
        public void Hdrp_decal_is_reported_without_a_stand_in()
        {
            ShaderClassification c = Classify(ShaderInfo.Named("HDRP/Decal"));
            Assert.AreEqual(CompatibilityLevel.Unsupported, c.Level);
            Assert.IsNull(c.SuggestedRole);
        }

        [Test]
        public void Shader_graph_without_builtin_target_is_unsupported()
        {
            var info = ShaderInfo.Named("Shader Graphs/Rim");
            info.RenderPipelineTag = "UniversalPipeline";
            info.LightModes.Add("UniversalForward");
            Assert.AreEqual(CompatibilityLevel.Unsupported, Classify(info).Level);

            var builtIn = ShaderInfo.Named("Shader Graphs/Rim BiRP");
            builtIn.LightModes.Add("ForwardBase");
            Assert.AreEqual(CompatibilityLevel.Direct, Classify(builtIn).Level);
        }

        // ---- missing shaders, recognised by their saved properties ----

        private static ShaderInfo Missing(params string[] savedProperties)
        {
            ShaderInfo info = ShaderInfo.Named(string.Empty, savedProperties);
            info.IsMissing = true;
            info.IsSupported = false;
            return info;
        }

        [Test]
        public void Missing_urp_lit_is_recognised_and_convertible()
        {
            ShaderClassification c = Classify(Missing("_BaseMap", "_BaseColor", "_Surface", "_WorkflowMode", "_Metallic", "_Smoothness", "_BumpMap"));
            Assert.AreEqual(ShaderFamily.UrpLit, c.Family);
            Assert.AreEqual(CompatibilityLevel.Convertible, c.Level);
            Assert.AreEqual(ShaderRole.Lit, c.SuggestedRole);
        }

        [Test]
        public void Missing_urp_unlit_is_recognised()
        {
            Assert.AreEqual(ShaderFamily.UrpUnlit, Classify(Missing("_BaseMap", "_BaseColor", "_Surface", "_Cutoff", "_Cull")).Family);
        }

        [Test]
        public void Missing_hdrp_lit_is_recognised()
        {
            ShaderClassification c = Classify(Missing("_BaseColorMap", "_BaseColor", "_MaskMap", "_NormalMap", "_SurfaceType", "_Smoothness"));
            Assert.AreEqual(ShaderFamily.HdrpLit, c.Family);
            Assert.AreEqual(ShaderRole.Lit, c.SuggestedRole);
        }

        [Test]
        public void Missing_hdrp_layered_and_unlit_are_recognised()
        {
            Assert.AreEqual(ShaderFamily.HdrpLayeredLit, Classify(Missing("_LayerCount", "_BaseColorMap0", "_BaseColorMap1", "_SurfaceType")).Family);
            Assert.AreEqual(ShaderFamily.HdrpUnlit, Classify(Missing("_UnlitColorMap", "_UnlitColor", "_SurfaceType")).Family);
        }

        [Test]
        public void Missing_standard_converts_instead_of_claiming_direct()
        {
            ShaderClassification c = Classify(Missing("_MainTex", "_Color", "_Mode", "_Glossiness", "_Metallic", "_MetallicGlossMap"));
            Assert.AreEqual(ShaderFamily.BuiltInStandard, c.Family);
            Assert.AreEqual(CompatibilityLevel.Convertible, c.Level);
        }

        [Test]
        public void Missing_and_unrecognised_is_unsupported()
        {
            ShaderClassification c = Classify(Missing("_Foo", "_Bar"));
            Assert.AreEqual(ShaderFamily.Unknown, c.Family);
            Assert.AreEqual(CompatibilityLevel.Unsupported, c.Level);
        }

        // ---- role guesses ----

        [Test]
        public void Role_guess_uses_queue_and_render_type()
        {
            var transparent = ShaderInfo.Named("Studio/A");
            transparent.RenderQueue = 3000;
            var cutout = ShaderInfo.Named("Studio/B");
            cutout.RenderTypeTag = "TransparentCutout";
            var sky = ShaderInfo.Named("Studio/C");
            sky.RenderQueue = 1000;

            Assert.AreEqual(ShaderRole.Transparent, ShaderClassifier.GuessRole(transparent));
            Assert.AreEqual(ShaderRole.Cutout, ShaderClassifier.GuessRole(cutout));
            Assert.AreEqual(ShaderRole.Skybox, ShaderClassifier.GuessRole(sky));
            Assert.AreEqual(ShaderRole.Lit, ShaderClassifier.GuessRole(ShaderInfo.Named("Studio/D")));
            Assert.AreEqual(ShaderRole.ParticlesUnlit, ShaderClassifier.GuessRole(ShaderInfo.Named("Studio/Particle Glow")));
        }
    }
}
