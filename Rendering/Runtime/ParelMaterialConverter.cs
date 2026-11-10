using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>The outcome of converting one material.</summary>
    public sealed class MaterialConversionResult
    {
        public Material Source;
        /// <summary>The material to render with: the source itself when it was kept.</summary>
        public Material Result;
        public ConversionPlan Plan;
        public CompatibilityEntry Entry => Plan?.Entry;
        public bool Changed => Result != null && Result != Source;
    }

    /// <summary>
    /// Converts materials to ParelVR shaders at runtime, without touching the originals: a converted material is a
    /// new object, cached per (source material, target), and shared by every renderer that used the source.
    /// </summary>
    public sealed class ParelMaterialConverter
    {
        private readonly Dictionary<Material, MaterialConversionResult> _cache = new Dictionary<Material, MaterialConversionResult>();
        private readonly List<Material> _created = new List<Material>();

        public TargetPlatform Target { get; }
        public MaterialMapperRegistry Mappers { get; }

        public ParelMaterialConverter(TargetPlatform target, MaterialMapperRegistry mappers = null)
        {
            Target = target;
            Mappers = mappers ?? ParelRenderManager.Mappers;
        }

        public int CachedCount => _cache.Count;

        /// <param name="roleHint">Overrides the classifier's guess for content whose job is known from where it is used
        /// (e.g. <see cref="ShaderRole.Skybox"/> for the scene's sky material). Ignored for materials that are kept.</param>
        public MaterialConversionResult Convert(Material source, ShaderRole? roleHint = null)
        {
            if (source == null) return new MaterialConversionResult();
            // A cached result whose converted material was destroyed is rebuilt.
            if (_cache.TryGetValue(source, out MaterialConversionResult cached) && cached.Result != null) return cached;

            ShaderClassification classification = ParelRenderManager.Classify(source, Target);
            if (roleHint.HasValue && classification.Level != CompatibilityLevel.Direct) classification.SuggestedRole = roleHint;
            ConversionPlan plan = MaterialConversionPlanner.Plan(classification, new UnityMaterialSource(source), Mappers);
            var result = new MaterialConversionResult { Source = source, Result = source, Plan = plan };

            if (plan.NeedsNewMaterial)
            {
                Shader shader = ParelShaderLibrary.Resolve(plan.Role.Value, Target, out ShaderRole resolved);
                IMaterialWriter writer = shader != null ? Mappers.WriterFor(resolved) : null;
                if (shader == null || writer == null)
                {
                    plan.Entry.Action = ConversionAction.Unsupported;
                    plan.Entry.Severity = DiagnosticSeverity.Error;
                    plan.Entry.Message = "No ParelVR shader is available for " + plan.Role.Value + "; the material is left as it was. " + plan.Entry.Message;
                    RenderingLog.Error("'" + source.name + "': no usable ParelVR shader for " + plan.Role.Value + ".");
                }
                else
                {
                    if (resolved != plan.Role.Value)
                    {
                        plan.Entry.Message += " (" + plan.Role.Value + " shader unavailable; used " + resolved + ")";
                        plan.Role = plan.Entry.TargetRole = resolved;
                    }
                    var material = new Material(shader)
                    {
                        name = source.name + " (ParelVR)",
                        enableInstancing = source.enableInstancing,
                        globalIlluminationFlags = source.globalIlluminationFlags,
                        doubleSidedGI = source.doubleSidedGI,
                    };
                    writer.Write(plan.Description, resolved, Target, new UnityMaterialSink(material));
                    _created.Add(material);
                    result.Result = material;
                }
            }

            _cache[source] = result;
            return result;
        }

        /// <summary>
        /// Converts the materials on every renderer under <paramref name="root"/> (inactive ones too). Renderers get
        /// new material arrays; the original material assets are not modified. Returns how many renderers changed.
        /// </summary>
        public int ApplyTo(GameObject root, CompatibilityReport report = null)
        {
            if (root == null) return 0;
            int changedRenderers = 0;
            var seen = report != null ? new HashSet<Material>() : null;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material source = materials[i];
                    if (source == null) continue;
                    MaterialConversionResult r = Convert(source);
                    if (seen != null && seen.Add(source))
                    {
                        CompatibilityEntry e = r.Entry;
                        if (e != null && string.IsNullOrEmpty(e.Subject)) e.Subject = PathOf(renderer.transform);
                        if (e != null) report.Add(e);
                    }
                    if (r.Changed)
                    {
                        materials[i] = r.Result;
                        changed = true;
                    }
                }
                if (changed)
                {
                    renderer.sharedMaterials = materials;
                    changedRenderers++;
                }
            }
            return changedRenderers;
        }

        /// <summary>
        /// Forgets conversions whose source material has been destroyed (its content was unloaded) and destroys the
        /// materials made for them. Returns how many were dropped.
        /// </summary>
        public int Prune()
        {
            List<Material> gone = null;
            foreach (KeyValuePair<Material, MaterialConversionResult> pair in _cache)
                if (pair.Key == null) (gone ??= new List<Material>()).Add(pair.Key);
            if (gone == null) return 0;
            foreach (Material key in gone)
            {
                MaterialConversionResult r = _cache[key];
                _cache.Remove(key);
                if (r.Changed)
                {
                    _created.Remove(r.Result);
                    if (Application.isPlaying) Object.Destroy(r.Result);
                    else Object.DestroyImmediate(r.Result);
                }
            }
            return gone.Count;
        }

        /// <summary>Destroys every material this converter made. Renderers still using them lose their material, so call it when their content is unloaded.</summary>
        public void Release()
        {
            foreach (Material m in _created)
            {
                if (m == null) continue;
                if (Application.isPlaying) Object.Destroy(m);
                else Object.DestroyImmediate(m);
            }
            _created.Clear();
            _cache.Clear();
        }

        internal static string PathOf(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }
}
