using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Checks content as it loads and repairs what can't render: a material whose shader is missing, is Unity's error
    /// shader, can't run on this GPU, or was written for URP/HDRP gets a ParelVR material (an exact conversion when a
    /// reader knows the shader, otherwise a stand-in that keeps the colour and textures it can). Materials that work
    /// are left alone. Original materials are never changed: renderers get new material arrays.
    /// One validator per piece of content (a world scene, an avatar set); call <see cref="Release"/> when it unloads.
    /// </summary>
    public sealed class ParelRenderValidator
    {
        private readonly Dictionary<Material, Material> _decided = new Dictionary<Material, Material>();
        private readonly HashSet<Material> _hidden = new HashSet<Material>();
        private readonly List<Material> _terrainMaterials = new List<Material>();

        public TargetPlatform Target { get; }
        public RepairPolicy Policy { get; }
        public ParelMaterialConverter Converter { get; }
        /// <summary>The report of the last <see cref="ValidateScene"/> or <see cref="Validate"/> call.</summary>
        public CompatibilityReport LastReport { get; private set; }

        public ParelRenderValidator(TargetPlatform target, RepairPolicy policy)
        {
            ParelRenderManager.Initialize();
            Target = target;
            Policy = policy;
            Converter = new ParelMaterialConverter(target);
        }

        public ParelRenderValidator() : this(ParelRenderManager.CurrentTarget, RuntimeRepair.DefaultPolicy(ParelRenderManager.CurrentTarget)) { }

        /// <summary>True when the material can't draw as it is.</summary>
        public static bool IsBroken(Material material)
        {
            if (material == null) return false;
            Shader shader = material.shader;
            return shader == null || !shader.isSupported || shader.name == ShaderClassifier.ErrorShaderName;
        }

        /// <summary>Validates every renderer and terrain in the scene, and its sky when it is the active scene.</summary>
        public CompatibilityReport ValidateScene(Scene scene, string label = null)
        {
            var report = new CompatibilityReport { Title = label ?? scene.name, Target = Target };
            if (!scene.IsValid() || !scene.isLoaded) return LastReport = report;

            int repaired = 0;
            var roots = new List<GameObject>();
            scene.GetRootGameObjects(roots);
            foreach (GameObject root in roots) repaired += Repair(root, report);

            if (scene == SceneManager.GetActiveScene() && RenderSettings.skybox != null)
            {
                Material sky = RenderSettings.skybox;
                Material fixedSky = RepairOne(sky, ShaderRole.Skybox, report, "Lighting settings (skybox)", out RepairAction action);
                if (action == RepairAction.Convert && fixedSky != sky) { RenderSettings.skybox = fixedSky; repaired++; }
                else if (action == RepairAction.Hide) { RenderSettings.skybox = null; repaired++; }
                // Ambient light taken from the sky is recalculated from the sky now shown.
                if (RenderSettings.skybox != sky && RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox) DynamicGI.UpdateEnvironment();
            }

            Summarise(report, repaired);
            // Load-time steps the SDK left for content it converted at upload (light probe scale, ambient, device overrides).
            ParelWorldRenderingInfo.ApplyAll(scene, Target);
            return LastReport = report;
        }

        /// <summary>Validates everything under <paramref name="root"/> (inactive objects too).</summary>
        public CompatibilityReport Validate(GameObject root, string label = null)
        {
            var report = new CompatibilityReport { Title = label ?? (root != null ? root.name : string.Empty), Target = Target };
            int repaired = root != null ? Repair(root, report) : 0;
            Summarise(report, repaired);
            return LastReport = report;
        }

        /// <summary>Repairs under <paramref name="root"/> into <paramref name="report"/>; returns how many renderers and terrains changed.</summary>
        public int Repair(GameObject root, CompatibilityReport report)
        {
            if (root == null) return 0;
            int changed = 0;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                bool swapped = false, hide = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material source = materials[i];
                    if (source == null) continue;
                    Material result = RepairOne(source, null, report, null, out RepairAction action, renderer);
                    if (action == RepairAction.Hide) hide = true;
                    else if (result != source) { materials[i] = result; swapped = true; }
                }
                if (swapped) renderer.sharedMaterials = materials;
                if (hide && renderer.enabled) renderer.enabled = false;
                if (swapped || hide) changed++;
            }

            foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
            {
                if (terrain == null || !IsBroken(terrain.materialTemplate)) continue;
                Shader shader = ParelShaderLibrary.Resolve(ShaderRole.Terrain, Target, out _);
                if (shader == null) continue;
                string was = terrain.materialTemplate.shader != null ? terrain.materialTemplate.shader.name : "(missing shader)";
                var material = new Material(shader) { name = terrain.materialTemplate.name + " (ParelVR)" };
                _terrainMaterials.Add(material);
                terrain.materialTemplate = material;
                report.Add(new CompatibilityEntry
                {
                    Subject = ParelMaterialConverter.PathOf(terrain.transform),
                    MaterialName = material.name,
                    ShaderName = was,
                    Family = ShaderFamily.ErrorShader,
                    Level = CompatibilityLevel.Unsupported,
                    Action = ConversionAction.Fallback,
                    TargetRole = ShaderRole.Terrain,
                    Severity = DiagnosticSeverity.Warning,
                    Message = "Terrain material couldn't render; Unity's terrain shader is used with the terrain's own layers.",
                });
                RenderingLog.Warning("'" + terrain.name + "': terrain material on '" + was + "' couldn't render; using " + shader.name + ".");
                changed++;
            }
            return changed;
        }

        private Material RepairOne(Material source, ShaderRole? roleHint, CompatibilityReport report, string subject, out RepairAction action, Renderer renderer = null)
        {
            if (_hidden.Contains(source))
            {
                action = RepairAction.Hide;
                return source;
            }
            bool broken = IsBroken(source);
            if (_decided.TryGetValue(source, out Material known) && known != null)
            {
                action = known == source ? RepairAction.Keep : RepairAction.Convert;
                return known;
            }

            ShaderClassification c = ParelRenderManager.Classify(source, Target);
            if (roleHint.HasValue && c.Level != CompatibilityLevel.Direct) c.SuggestedRole = roleHint;
            action = RuntimeRepair.Decide(c, broken, Policy);
            string where = subject ?? (renderer != null ? ParelMaterialConverter.PathOf(renderer.transform) : string.Empty);
            string shaderName = source.shader != null ? source.shader.name : "(missing shader)";

            if (action == RepairAction.Keep)
            {
                _decided[source] = source;
                return source;
            }

            if (action == RepairAction.Hide)
            {
                report?.Add(new CompatibilityEntry
                {
                    Subject = where, MaterialName = source.name, ShaderName = shaderName, Family = c.Family, Pipeline = c.Pipeline,
                    Level = c.Level, Action = ConversionAction.Unsupported, Severity = DiagnosticSeverity.Error,
                    Message = (string.IsNullOrEmpty(c.Reason) ? "Nothing can draw this material." : c.Reason) + " The object is hidden instead of rendering magenta.",
                });
                RenderingLog.Error("'" + source.name + "' (" + shaderName + ") can't render and has no ParelVR equivalent; hidden at " + where + ".");
                _hidden.Add(source);
                return source;
            }

            MaterialConversionResult r = Converter.Convert(source, roleHint);
            CompatibilityEntry e = r.Entry;
            if (e != null)
            {
                if (string.IsNullOrEmpty(e.Subject)) e.Subject = where;
                report?.Add(e);
            }
            if (!r.Changed)
            {
                // No ParelVR shader could be made: hide a broken one rather than leave it magenta.
                action = broken ? RepairAction.Hide : RepairAction.Keep;
                if (broken)
                {
                    RenderingLog.Error("'" + source.name + "' (" + shaderName + ") can't render and no ParelVR shader is available; hidden at " + where + ".");
                    _hidden.Add(source);
                }
                else _decided[source] = source;
                return source;
            }

            string verb = r.Plan.Action == ConversionAction.Converted ? "converted to" : "stand-in";
            string line = "'" + source.name + "' (" + shaderName + (broken ? ", can't render" : "") + ") → " + verb + " " + r.Result.shader.name + (string.IsNullOrEmpty(where) ? "" : " at " + where);
            if (r.Plan.Action == ConversionAction.Converted) RenderingLog.Notice(line);
            else RenderingLog.Warning(line);
            _decided[source] = r.Result;
            return r.Result;
        }

        private static void Summarise(CompatibilityReport report, int repaired)
        {
            int converted = report.Count(ConversionAction.Converted);
            int standIns = report.Count(ConversionAction.Fallback);
            int hidden = report.Count(ConversionAction.Unsupported);
            if (repaired == 0 && converted == 0 && standIns == 0 && hidden == 0) return;
            RenderingLog.Notice("'" + report.Title + "': " + converted + " converted, " + standIns + " stand-in(s), " + hidden + " hidden, across " + repaired + " object(s).");
        }

        /// <summary>Forgets materials whose content was unloaded (see <see cref="ParelMaterialConverter.Prune"/>).</summary>
        public int Prune()
        {
            var gone = new List<Material>();
            foreach (Material key in _decided.Keys) if (key == null) gone.Add(key);
            foreach (Material key in gone) _decided.Remove(key);
            _hidden.RemoveWhere(m => m == null);
            return Converter.Prune();
        }

        /// <summary>Destroys every material this validator made. Call when its content is unloaded.</summary>
        public void Release()
        {
            Converter.Release();
            foreach (Material m in _terrainMaterials)
            {
                if (m == null) continue;
                if (Application.isPlaying) Object.Destroy(m);
                else Object.DestroyImmediate(m);
            }
            _terrainMaterials.Clear();
            _decided.Clear();
            _hidden.Clear();
        }
    }
}
