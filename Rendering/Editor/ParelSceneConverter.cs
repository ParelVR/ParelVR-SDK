using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Editor
{
    public sealed class SceneConversionOptions
    {
        public TargetPlatform Target = TargetPlatform.Pc;
        /// <summary>Where generated materials go. Must be a folder only the build uses (it is deleted afterwards).</summary>
        public string GeneratedFolder = "Assets/ParelVR_Rendering_Build";
        /// <summary>Null picks <see cref="RepairPolicy.ForeignPipelines"/> on PC and <see cref="RepairPolicy.ConvertAll"/> on Quest.</summary>
        public RepairPolicy? Policy;
        /// <summary>The pipeline the creator's project renders with. Unknown means: read it from the project.</summary>
        public RenderPipelineKind SourcePipeline = RenderPipelineKind.Unknown;
    }

    /// <summary>
    /// Converts a world scene for ParelVR at upload. It works on the build copy of the scene: materials that ParelVR
    /// can't render are replaced by generated ParelVR material assets, and for HDRP scenes the lights, sky, fog and
    /// reflection probes are brought from physical units to Built-in ones with the scene's own exposure. The creator's
    /// materials, textures, lighting data and source scene are never modified. Everything done goes into the report.
    /// </summary>
    public static class ParelSceneConverter
    {
        public static RenderPipelineKind ProjectPipeline()
        {
            RenderPipelineAsset rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null) return RenderPipelineKind.BuiltIn;
            string type = rp.GetType().Name;
            if (type.IndexOf("HDRenderPipeline", StringComparison.Ordinal) >= 0) return RenderPipelineKind.HighDefinition;
            if (type.IndexOf("Universal", StringComparison.Ordinal) >= 0) return RenderPipelineKind.Universal;
            return RenderPipelineKind.Unknown;
        }

        /// <summary>Converts <paramref name="scene"/> in place. Only call it on a copy made for the build.</summary>
        public static CompatibilityReport ConvertScene(Scene scene, SceneConversionOptions options)
        {
            options = options ?? new SceneConversionOptions();
            ParelRenderManager.Initialize();
            RenderPipelineKind pipeline = options.SourcePipeline != RenderPipelineKind.Unknown ? options.SourcePipeline : ProjectPipeline();
            var report = new CompatibilityReport { Title = scene.name, Target = options.Target, DetectedPipeline = pipeline };
            var job = new Job(options, report);
            _generatedFolder = options.GeneratedFolder;
            _target = options.Target;

            Scene previousActive = SceneManager.GetActiveScene();
            SceneManager.SetActiveScene(scene); // RenderSettings belong to the active scene
            float previousEv = LightingConversion.SceneEv100;
            try
            {
                SrpVolumeSettings volumes = SrpSceneReader.Read(scene);
                bool hdrp = pipeline == RenderPipelineKind.HighDefinition;
                float ev = hdrp ? SceneExposure(scene, volumes, report) : LightingConversion.DefaultEv100;
                LightingConversion.SceneEv100 = ev;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) job.ConvertRenderer(r);
                    foreach (Terrain t in root.GetComponentsInChildren<Terrain>(true))
                        if (t.materialTemplate != null) t.materialTemplate = job.Convert(t.materialTemplate, ShaderRole.Terrain, Path(t.transform));
                }

                bool skyChanged = false;
                if (hdrp) skyChanged = AdaptHdrpLighting(scene, volumes, ev, job, report);
                ParelFogSettings fog = hdrp ? FogFrom(scene, volumes, ev, report) : null;
                int decals = pipeline != RenderPipelineKind.BuiltIn ? ParelDecalBaker.BakeAll(scene, options.GeneratedFolder + "/Decals", report) : 0;
                if (volumes.Post.Enabled)
                    report.Note(DiagnosticSeverity.Info, "Post-processing", "Converted for the game's post-processing: " + PostSummary(volumes.Post) + ".");
                else if (RenderSettings.skybox != null)
                {
                    Material sky = job.Convert(RenderSettings.skybox, ShaderRole.Skybox, "Lighting settings (skybox)");
                    skyChanged = sky != RenderSettings.skybox;
                    RenderSettings.skybox = sky;
                }
                ReportPipelineOnlyComponents(scene, report);
                if (volumes.NotCarried.Count > 0 && pipeline != RenderPipelineKind.BuiltIn)
                    foreach (string effect in volumes.NotCarried)
                        report.Note(DiagnosticSeverity.Notice, "Volume effect", effect + " isn't available in ParelVR and is not shown.");

                if (pipeline != RenderPipelineKind.BuiltIn || job.Converted > 0 || decals > 0)
                {
                    var holder = new GameObject("[ParelVR Rendering]");
                    SceneManager.MoveGameObjectToScene(holder, scene);
                    var info = holder.AddComponent<ParelWorldRenderingInfo>();
                    info.SourcePipeline = pipeline;
                    info.SceneEv100 = ev;
                    info.LightProbeScale = hdrp ? LightingConversion.BakedScale(ev) : 1f;
                    info.AmbientFromSky = skyChanged;
                    info.MaterialsConverted = job.Converted;
                    info.MaterialsKept = job.Kept;
                    if (fog != null) info.Fog = fog;
                    info.Post = volumes.Post;
                    // A creator's own settings component (device overrides) is kept: copy them over and drop the duplicate.
                    foreach (GameObject root in scene.GetRootGameObjects())
                        foreach (ParelWorldRenderingInfo own in root.GetComponentsInChildren<ParelWorldRenderingInfo>(true))
                            if (own != info)
                            {
                                info.DeviceOverrides = own.DeviceOverrides;
                                UnityEngine.Object.DestroyImmediate(own);
                            }
                }
                EditorSceneManager.MarkSceneDirty(scene);
            }
            finally
            {
                LightingConversion.SceneEv100 = previousEv;
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
            }
            AssetDatabase.SaveAssets();
            return report;
        }

        /// <summary>
        /// Converts the materials of <paramref name="root"/>'s renderers in place, e.g. an avatar's build copy. Lighting
        /// belongs to worlds, so only materials change (HDRP emission uses an indoor exposure). Only call it on a copy
        /// made for the build; generated materials go to <see cref="SceneConversionOptions.GeneratedFolder"/>.
        /// </summary>
        public static CompatibilityReport ConvertObject(GameObject root, SceneConversionOptions options)
        {
            options = options ?? new SceneConversionOptions();
            ParelRenderManager.Initialize();
            RenderPipelineKind pipeline = options.SourcePipeline != RenderPipelineKind.Unknown ? options.SourcePipeline : ProjectPipeline();
            var report = new CompatibilityReport { Title = root != null ? root.name : "(none)", Target = options.Target, DetectedPipeline = pipeline };
            if (root == null) return report;
            var job = new Job(options, report);
            _generatedFolder = options.GeneratedFolder;
            _target = options.Target;
            float previousEv = LightingConversion.SceneEv100;
            try
            {
                LightingConversion.SceneEv100 = LightingConversion.DefaultEv100;
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) job.ConvertRenderer(r);
            }
            finally
            {
                LightingConversion.SceneEv100 = previousEv;
            }
            AssetDatabase.SaveAssets();
            return report;
        }

        // ---- materials -------------------------------------------------------------------------------------------

        private sealed class Job
        {
            private readonly SceneConversionOptions _o;
            private readonly CompatibilityReport _report;
            private readonly RepairPolicy _policy;
            private readonly Dictionary<Material, Material> _done = new Dictionary<Material, Material>();
            public int Converted, Kept;

            public Job(SceneConversionOptions o, CompatibilityReport report)
            {
                _o = o;
                _report = report;
                _policy = o.Policy ?? (o.Target == TargetPlatform.Quest ? RepairPolicy.ConvertAll : RepairPolicy.ForeignPipelines);
            }

            public void ConvertRenderer(Renderer r)
            {
                Material[] materials = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) continue;
                    Material m = Convert(materials[i], r is ParticleSystemRenderer ? ShaderRole.ParticlesUnlit : (ShaderRole?)null, Path(r.transform));
                    if (m != materials[i]) { materials[i] = m; changed = true; }
                }
                if (changed) r.sharedMaterials = materials;
            }

            public Material Convert(Material source, ShaderRole? roleHint, string subject)
            {
                if (_done.TryGetValue(source, out Material known)) return known;
                var saved = new SavedMaterialProperties(source);
                ShaderInfo info = saved.ShaderMissing ? saved.ToMissingShaderInfo() : UnityShaderInspector.Inspect(source.shader);
                ShaderClassification c = ParelRenderManager.Classifier.Classify(info, _o.Target);
                // A hint only replaces the classifier's guess (particles keep their lit/unlit split).
                if (roleHint.HasValue && c.Level != CompatibilityLevel.Direct && (roleHint != ShaderRole.ParticlesUnlit || c.SuggestedRole != ShaderRole.ParticlesLit)) c.SuggestedRole = roleHint;
                bool broken = saved.ShaderMissing || ShaderUtil.ShaderHasError(source.shader);
                RepairAction action = RuntimeRepair.Decide(c, broken, _policy);

                ConversionPlan plan = MaterialConversionPlanner.Plan(c, saved, ParelRenderManager.Mappers);
                CompatibilityEntry entry = plan.Entry;
                entry.Subject = subject;
                entry.MissingTextures.AddRange(saved.MissingTextures);
                Material result = source;

                if (action == RepairAction.Convert && plan.NeedsNewMaterial)
                {
                    if (plan.Description != null && plan.Description.Subsurface && ParelDiffusionProfiles.Resolve(source, plan.Description))
                        entry.Message = (entry.Message + " Scattering colours from its diffusion profile.").Trim();
                    ShaderRole role = plan.Role.Value;
                    Shader shader = ShaderFor(ref role);
                    IMaterialWriter writer = shader != null ? ParelRenderManager.Mappers.WriterFor(role) : null;
                    if (shader != null && writer != null)
                    {
                        plan.Entry.TargetRole = role;
                        var m = new Material(shader)
                        {
                            name = source.name + " (ParelVR)",
                            enableInstancing = source.enableInstancing,
                            globalIlluminationFlags = source.globalIlluminationFlags,
                            doubleSidedGI = source.doubleSidedGI,
                        };
                        writer.Write(plan.Description, role, _o.Target, new UnityMaterialSink(m));
                        if (!AssetDatabase.IsValidFolder(_o.GeneratedFolder)) CreateFolder(_o.GeneratedFolder);
                        AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath(_o.GeneratedFolder + "/" + Safe(source.name) + ".mat"));
                        result = m;
                        Converted++;
                    }
                    else
                    {
                        entry.Action = ConversionAction.Unsupported;
                        entry.Severity = DiagnosticSeverity.Error;
                        entry.Message = "No ParelVR shader is available for " + plan.Role.Value + ". " + entry.Message;
                    }
                }
                else if (action == RepairAction.Hide)
                {
                    entry.Action = ConversionAction.Unsupported;
                    entry.Severity = DiagnosticSeverity.Error;
                    entry.Message += " ParelVR hides objects with this material so they don't render magenta. Replace the material to show them.";
                }
                else
                {
                    // Kept as it is: report it as preserved, whatever the plan would have done under another policy.
                    entry.Action = ConversionAction.Preserved;
                    entry.TargetRole = null;
                    if (entry.Severity < DiagnosticSeverity.Warning) entry.Severity = DiagnosticSeverity.Info;
                    Kept++;
                }
                if (entry.MissingTextures.Count > 0 && entry.Severity < DiagnosticSeverity.Warning) entry.Severity = DiagnosticSeverity.Warning;
                _report.Add(entry);
                _done[source] = result;
                return result;
            }

            /// <summary>The library shader for the role on the target. GPU support is not checked: the target is the player, not this Editor.</summary>
            private Shader ShaderFor(ref ShaderRole role)
            {
                ParelShaderLibrary library = ParelShaderLibrary.Active;
                for (ShaderRole? r = role; r.HasValue; r = ParelShaderNames.Parent(r.Value))
                {
                    Shader s = library != null ? library.Get(r.Value, _o.Target) : null;
                    if (s == null) s = Shader.Find(ParelShaderNames.For(r.Value));
                    if (s != null && !ShaderUtil.ShaderHasError(s)) { role = r.Value; return s; }
                }
                return null;
            }
        }

        // ---- HDRP lighting ---------------------------------------------------------------------------------------

        /// <summary>The EV100 the scene was shown at in HDRP: fixed exposure, the physical camera, or a light meter on the sun.</summary>
        private static float SceneExposure(Scene scene, SrpVolumeSettings v, CompatibilityReport report)
        {
            float ev;
            string how;
            if (v.FixedEv100.HasValue)
            {
                ev = v.FixedEv100.Value;
                how = "fixed exposure";
            }
            else if (v.PhysicalCameraExposure && FirstCamera(scene) is Camera cam && cam.shutterSpeed > 0f && cam.iso > 0)
            {
                ev = Mathf.Log(cam.aperture * cam.aperture / cam.shutterSpeed * 100f / cam.iso, 2f);
                how = "the physical camera";
            }
            else
            {
                Light sun = Sun(scene);
                ev = sun != null ? LightingConversion.Ev100FromIlluminance(sun.intensity) : LightingConversion.DefaultEv100;
                how = sun != null ? "automatic exposure, estimated from the sun (" + sun.intensity.ToString("0") + " lux)" : "automatic exposure, estimated for an interior";
            }
            ev -= v.ExposureCompensation;
            report.Note(DiagnosticSeverity.Info, "Exposure", "Physical light values were converted at EV " + ev.ToString("0.0") + " (" + how + ").");
            return ev;
        }

        private static bool AdaptHdrpLighting(Scene scene, SrpVolumeSettings v, float ev, Job job, CompatibilityReport report)
        {
            int lights = 0, probes = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Light light in root.GetComponentsInChildren<Light>(true))
                {
                    float before = light.intensity;
                    switch (light.type)
                    {
                        case LightType.Directional:
                            light.intensity = LightingConversion.DirectionalIntensity(before, ev);
                            break;
                        case LightType.Point:
                        case LightType.Spot:
                            light.intensity = LightingConversion.PunctualIntensity(Candela(light), light.range, ev);
                            break;
                        case LightType.Rectangle:
                        case LightType.Disc:
                            float area = light.type == LightType.Rectangle ? light.areaSize.x * light.areaSize.y : Mathf.PI * light.areaSize.x * light.areaSize.x;
                            float candela = before * Mathf.Max(area, 1e-4f); // nits over the area, seen face on
                            if (light.lightmapBakeType != LightmapBakeType.Baked)
                            {
                                report.Note(DiagnosticSeverity.Warning, Path(light.transform), "Real-time area light: the Built-in renderer only bakes area lights, so it is shown as a point light.");
                                light.type = LightType.Point;
                                light.range = Mathf.Max(light.range, Mathf.Sqrt(area) * 4f);
                            }
                            light.intensity = LightingConversion.PunctualIntensity(candela, Mathf.Max(light.range, 1f), ev);
                            break;
                    }
                    lights++;
                }
                foreach (ReflectionProbe probe in root.GetComponentsInChildren<ReflectionProbe>(true))
                {
                    probe.intensity *= LightingConversion.BakedScale(ev);
                    probes++;
                }
            }
            if (lights > 0) report.Note(DiagnosticSeverity.Info, "Lights", lights + " light(s) converted from physical units.");
            if (probes > 0) report.Note(DiagnosticSeverity.Notice, "Reflection probes", probes + " probe(s) scaled to the scene's exposure. They were baked by HDRP; rebaking them in a Built-in project gives the most accurate reflections.");
            if (LightmapSettings.lightmaps != null && LightmapSettings.lightmaps.Length > 0)
                report.Note(DiagnosticSeverity.Warning, "Lightmaps", "This scene's lightmaps were baked by HDRP in physical units, which the Built-in renderer shows too bright. They need rebaking in a Built-in project (the ParelVR conversion environment); until then the baked light is shown as baked. Light probes are scaled when the world loads.");

            bool skyChanged = false;
            if (v.HdriSky && v.SkyTexture != null)
            {
                Shader shader = (ParelShaderLibrary.Active != null ? ParelShaderLibrary.Active.Get(ShaderRole.Skybox, _target) : null) ?? Shader.Find(ParelShaderNames.Skybox);
                if (shader != null)
                {
                    var sky = new Material(shader) { name = "HDRI Sky (ParelVR)" };
                    var d = new MaterialDescription
                    {
                        BaseMap = new TextureSlot(v.SkyTexture),
                        SkyCubemap = v.SkyTexture is Cubemap,
                        BaseColor = new ColorValue(0.5f, 0.5f, 0.5f, 0.5f),
                        SkyExposure = LightingConversion.LinearToGamma(LightingConversion.SkyExposure(v.SkyMultiplier, v.SkyExposureEv, ev)),
                        SkyRotation = Mathf.Repeat(v.SkyRotation, 360f),
                    };
                    new SkyboxWriter().Write(d, ShaderRole.Skybox, TargetPlatform.Pc, new UnityMaterialSink(sky));
                    SaveGenerated(sky, "HDRI Sky");
                    RenderSettings.skybox = sky;
                    RenderSettings.ambientMode = AmbientMode.Skybox;
                    skyChanged = true;
                    report.Note(DiagnosticSeverity.Info, "Sky", "HDRI sky converted to ParelVR Skybox (exposure " + v.SkyExposureEv.ToString("0.#") + " EV, multiplier " + v.SkyMultiplier.ToString("0.##") + ").");
                }
            }
            else if (v.GradientSky || v.PhysicallyBasedSky)
            {
                float scale = LightingConversion.SkyExposure(v.SkyMultiplier, v.SkyExposureEv, ev);
                Shader procedural = Shader.Find("Skybox/Procedural");
                if (procedural != null)
                {
                    var sky = new Material(procedural) { name = (v.GradientSky ? "Gradient" : "Physically Based") + " Sky (ParelVR)" };
                    if (v.GradientSky)
                    {
                        sky.SetColor("_SkyTint", v.GradientTop);
                        sky.SetColor("_GroundColor", v.GradientBottom);
                    }
                    sky.SetFloat("_Exposure", Mathf.Clamp(scale, 0f, 8f));
                    SaveGenerated(sky, "Sky");
                    RenderSettings.skybox = sky;
                    skyChanged = true;
                }
                if (v.GradientSky)
                {
                    RenderSettings.ambientMode = AmbientMode.Trilight;
                    RenderSettings.ambientSkyColor = v.GradientTop * scale;
                    RenderSettings.ambientEquatorColor = v.GradientMiddle * scale;
                    RenderSettings.ambientGroundColor = v.GradientBottom * scale;
                }
                report.Note(DiagnosticSeverity.Notice, "Sky", (v.GradientSky ? "Gradient" : "Physically based") + " sky shown with Unity's procedural sky" + (v.GradientSky ? " and gradient ambient light" : "") + "; the look is close, not identical.");
            }
            else report.Note(DiagnosticSeverity.Notice, "Sky", "No HDRP sky was found to convert; the scene's skybox setting is used.");

            return skyChanged;
        }

        /// <summary>
        /// HDRP Fog (and Local Volumetric Fog) → ParelVR volumetric fog: the same thickness (1 / mean free path), base and
        /// maximum height (density falls to 1/1000 at the maximum height, as in HDRP), max distance, albedo, anisotropy and
        /// light dimmer; up to 4 local fog boxes. Unity's own fog is switched off so the two don't add up.
        /// </summary>
        private static ParelFogSettings FogFrom(Scene scene, SrpVolumeSettings v, float ev, CompatibilityReport report)
        {
            if (!v.Fog) return null;
            RenderSettings.fog = false;
            var f = new ParelFogSettings
            {
                Enabled = true,
                Extinction = 1f / Mathf.Max(v.FogMeanFreePath, 0.01f),
                BaseHeight = v.FogBaseHeight,
                HeightFalloff = 6.9078f / Mathf.Max(0.01f, v.FogMaximumHeight - v.FogBaseHeight),
                MaxDistance = v.FogMaxDistance,
                Albedo = v.VolumetricFog ? v.FogAlbedo : v.FogColorFromSky ? v.FogTint : Color.white,
                AmbientFromSky = v.FogColorFromSky || v.VolumetricFog,
                // HDRP keeps the fog colour in physical units: scale it like every other light value. Stored as a
                // gamma colour so ParelFogGlobals reads the same linear value back.
                Ambient = (new Color(v.FogColor.r, v.FogColor.g, v.FogColor.b) * LightingConversion.ExposureScale(ev)).gamma,
                Anisotropy = v.VolumetricFog ? v.FogAnisotropy : 0f,
                // Non-volumetric HDRP fog doesn't scatter the sun; volumetric fog does, scaled by its light dimmer.
                SunScattering = v.VolumetricFog ? v.FogLightDimmer : 0f,
            };
            if (v.VolumetricFog)
            {
                var local = SrpSceneReader.LocalFog(scene);
                // The biggest boxes first: the shader takes 4.
                local.Sort((a, b) => Vol(b).CompareTo(Vol(a)));
                if (local.Count > ParelFogSettings.MaxVolumes)
                    report.Note(DiagnosticSeverity.Warning, "Local fog", local.Count + " Local Volumetric Fog boxes; ParelVR shows the " + ParelFogSettings.MaxVolumes + " biggest.");
                f.Volumes = local.GetRange(0, Mathf.Min(local.Count, ParelFogSettings.MaxVolumes)).ToArray();
            }
            report.Note(DiagnosticSeverity.Info, "Fog", "HDRP fog converted to ParelVR volumetric fog (mean free path " + v.FogMeanFreePath.ToString("0.#") + " m, height "
                + v.FogBaseHeight.ToString("0.#") + "–" + v.FogMaximumHeight.ToString("0.#") + " m" + (v.VolumetricFog ? ", sun scattering" : "")
                + (f.Volumes.Length > 0 ? ", " + f.Volumes.Length + " local fog box(es)" : "") + "). It is drawn by the ParelVR shaders: no extra pass, the same in VR.");
            return f;
        }

        private static float Vol(LocalFogVolume v) => v.Volume == null ? 0f : Mathf.Abs(Vector3.Scale(v.Size, v.Volume.lossyScale).x * Vector3.Scale(v.Size, v.Volume.lossyScale).y * Vector3.Scale(v.Size, v.Volume.lossyScale).z);

        private static string PostSummary(ParelPostSettings p)
        {
            var parts = new List<string>();
            if (p.Bloom) parts.Add("bloom");
            if (p.Tonemapping != ParelPostSettings.ToneMapper.None) parts.Add(p.Tonemapping + " tonemapping");
            if (p.ColorAdjustments) parts.Add("colour adjustments");
            if (p.WhiteBalance) parts.Add("white balance");
            if (p.LiftGammaGain) parts.Add("lift / gamma / gain");
            if (p.Vignette) parts.Add("vignette");
            if (p.ChromaticAberration) parts.Add("chromatic aberration");
            if (p.FilmGrain) parts.Add("film grain");
            if (p.AmbientOcclusion) parts.Add("ambient occlusion");
            if (p.DepthOfField) parts.Add("depth of field");
            if (p.MotionBlur) parts.Add("motion blur");
            if (p.LensDistortion) parts.Add("lens distortion");
            return string.Join(", ", parts);
        }

        /// <summary>The light's intensity in candela, from whatever unit it is set in (Unity 6 keeps HDRP units on the Light).</summary>
        private static float Candela(Light light)
        {
            string unit = Member(light, "lightUnit")?.ToString() ?? "Candela";
            float i = light.intensity;
            switch (unit)
            {
                case "Lumen":
                    bool reflector = light.type == LightType.Spot && Member(light, "enableSpotReflector") is bool b && b;
                    double solid = reflector ? 2.0 * Math.PI * (1.0 - Math.Cos(light.spotAngle * Mathf.Deg2Rad * 0.5)) : 4.0 * Math.PI;
                    return (float)(i / solid);
                case "Ev100": return LightingConversion.NitsFromEv100(i);
                case "Lux":
                    float at = Member(light, "luxAtDistance") is float dist ? dist : 1f;
                    return i * at * at;
                default: return i;
            }
        }

        private static object Member(object o, string name)
        {
            PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p != null ? p.GetValue(o) : null;
        }

        private static Light Sun(Scene scene)
        {
            Light best = RenderSettings.sun;
            if (best != null && best.type == LightType.Directional && best.isActiveAndEnabled) return best;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Light l in root.GetComponentsInChildren<Light>(false))
                    if (l.type == LightType.Directional && l.isActiveAndEnabled && (best == null || l.intensity > best.intensity)) best = l;
            return best;
        }

        private static Camera FirstCamera(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Camera c = root.GetComponentInChildren<Camera>(true);
                if (c != null) return c;
            }
            return null;
        }

        private static readonly Dictionary<string, string> PipelineOnly = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "DecalProjector", null },
            { "LocalVolumetricFog", null },
            { "DensityVolume", null },
            { "PlanarReflectionProbe", "planar reflection probe: use a ParelVR mirror or a reflection probe instead." },
            { "VisualEffect", "Visual Effect Graph effects need compute shaders and an SRP; use a Particle System instead." },
            { "WaterSurface", "HDRP water: not available in ParelVR." },
            { "HDAdditionalCameraData", null },
            { "HDAdditionalLightData", null },
            { "HDAdditionalReflectionData", null },
            { "UniversalAdditionalCameraData", null },
            { "UniversalAdditionalLightData", null },
            { "Volume", null },
        };

        private static void ReportPipelineOnlyComponents(Scene scene, CompatibilityReport report)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    string name = c.GetType().Name;
                    if (!PipelineOnly.TryGetValue(name, out string message) || message == null) continue;
                    counts.TryGetValue(name, out int n);
                    counts[name] = n + 1;
                }
            foreach (KeyValuePair<string, int> pair in counts)
                report.Note(DiagnosticSeverity.Warning, pair.Value + " × " + pair.Key, PipelineOnly[pair.Key]);
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        private static string _generatedFolder = "Assets/ParelVR_Rendering_Build";
        private static TargetPlatform _target;

        private static void SaveGenerated(UnityEngine.Object asset, string name)
        {
            if (!AssetDatabase.IsValidFolder(_generatedFolder)) CreateFolder(_generatedFolder);
            AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(_generatedFolder + "/" + Safe(name) + ".mat"));
        }

        internal static void CreateFolder(string folder)
        {
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string Safe(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrEmpty(name) ? "Material" : name;
        }

        internal static string Path(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
