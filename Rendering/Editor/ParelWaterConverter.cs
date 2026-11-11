using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// HDRP Water Surfaces → ParelVR water at upload (read through reflection, so the SDK needs no HDRP reference).
    /// Each surface gets a ParelVR/Water material carrying its look (refraction / scattering colours, absorption,
    /// smoothness, foam, caustics), 8 Gerstner waves worked out from its wind, chaos, orientation, repetition size and
    /// band multipliers, ripples from its ripple wind, and a mesh: an ocean grid that follows the player for an infinite
    /// surface, a grid for a quad, or the surface's own meshes. Water Deformers (up to 4 per surface) and Water
    /// Excluders come along. The underwater view keeps HDRP's absorption distance multiplier and volume.
    /// </summary>
    public static class ParelWaterConverter
    {
        public static int ConvertAll(Scene scene, string folder, CompatibilityReport report)
        {
            var surfaces = new List<Component>();
            var deformers = new List<Component>();
            var excluders = new List<Component>();
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    string n = c.GetType().Name;
                    if (n == "WaterSurface") surfaces.Add(c);
                    else if (n == "WaterDeformer") deformers.Add(c);
                    else if (n == "WaterExcluder") excluders.Add(c);
                }
            if (surfaces.Count == 0) return 0;
            Shader water = Shader.Find(ParelShaderNames.Water);
            if (water == null)
            {
                report.Note(DiagnosticSeverity.Error, "Water", "The ParelVR/Water shader is missing from the SDK; water isn't converted.");
                return 0;
            }

            Texture2D ripples = ParelWaterTextures.RippleNormal();
            Texture2D foam = ParelWaterTextures.FoamAndCaustics();
            Save(ripples, folder, "Water Ripples");
            Save(foam, folder, "Water Foam and Caustics");

            int done = 0;
            foreach (Component surface in surfaces)
            {
                if (!(surface is Behaviour b) || !b.enabled || !surface.gameObject.activeInHierarchy) continue;
                try
                {
                    var mat = new Material(water) { name = surface.name + " (ParelVR Water)" };
                    mat.SetTexture("_RippleNormal", ripples);
                    mat.SetTexture("_FoamMap", foam);
                    string type = Get(surface, "surfaceType")?.ToString() ?? "OceanSeaLake";
                    WriteLook(surface, mat);
                    WriteWaves(surface, mat, type);
                    WriteDeformers(surface, mat, deformers);
                    Save(mat, folder, mat.name);
                    BuildMesh(surface, mat, type, folder, report);
                    done++;
                }
                catch (Exception e)
                {
                    report.Note(DiagnosticSeverity.Warning, surface.name, "Water couldn't be converted: " + e.Message);
                }
            }
            Shader excluderShader = Shader.Find(ParelShaderNames.WaterExcluder);
            int ex = 0;
            if (excluderShader != null)
            {
                var exMat = new Material(excluderShader) { name = "Water Excluder (ParelVR)" };
                Save(exMat, folder, exMat.name);
                foreach (Component e in excluders)
                {
                    Mesh mesh = FirstMesh(e);
                    if (mesh == null) continue;
                    var go = new GameObject("ParelVR Water Excluder");
                    go.transform.SetParent(e.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = exMat;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    ex++;
                }
            }
            report.Note(DiagnosticSeverity.Info, "Water", done + " water surface(s) converted to ParelVR water" + (ex > 0 ? ", " + ex + " excluder(s)" : "") + (deformers.Count > 0 ? ", " + Math.Min(deformers.Count, 4 * done) + " deformer(s)" : "") + ".");
            return done;
        }

        private static void WriteLook(Component w, Material m)
        {
            m.SetColor("_ScatteringColor", Col(w, "scatteringColor", new Color(0f, 0.25f, 0.3f)));
            m.SetColor("_RefractionColor", Col(w, "refractionColor", new Color(0.1f, 0.5f, 0.5f)));
            m.SetFloat("_AbsorptionDistance", Num(w, "absorptionDistance", 5f));
            m.SetFloat("_MaxRefractionDistance", Num(w, "maxRefractionDistance", 1f));
            m.SetFloat("_AmbientScattering", Num(w, "ambientScattering", 0.2f));
            m.SetFloat("_HeightScattering", Num(w, "heightScattering", 0.2f));
            m.SetFloat("_TipScattering", Num(w, "directLightTipScattering", 0.6f));
            m.SetFloat("_BodyScattering", Num(w, "directLightBodyScattering", 0.5f));
            m.SetFloat("_Smoothness", Num(w, "startSmoothness", 0.95f));
            m.SetFloat("_SmoothnessFar", Num(w, "endSmoothness", 0.85f));
            m.SetFloat("_SmoothnessFadeStart", Num(w, "smoothnessFadeStart", 100f));
            m.SetFloat("_SmoothnessFadeDistance", Num(w, "smoothnessFadeDistance", 500f));
            bool foam = Bool(w, "foam", true);
            m.SetFloat("_FoamAmount", foam ? Mathf.Clamp01(Num(w, "simulationFoamAmount", 0.3f)) : 0f);
            m.SetColor("_FoamColor", Col(w, "foamColor", new Color(0.95f, 0.97f, 1f)));
            m.SetFloat("_FoamScale", 3f / Mathf.Max(0.01f, Num(w, "foamTextureTiling", 0.35f)));
            m.SetFloat("_ShoreFoamDistance", foam ? 0.6f : 0f);
            bool caustics = Bool(w, "caustics", true);
            m.SetFloat("_CausticsIntensity", caustics ? Num(w, "causticsIntensity", 0.5f) : 0f);
            m.SetFloat("_CausticsScale", 4f / Mathf.Max(0.1f, Num(w, "causticsTilingFactor", 1f)));
            m.SetFloat("_CausticsDepthFade", Mathf.Max(1f, Num(w, "virtualPlaneDistance", 4f) * 2.5f));
        }

        // Gerstner waves from HDRP's simulation settings: a wind sea (Pierson-Moskowitz height for the wind speed),
        // wavelengths from the repetition size down, directions spread around the orientation by the chaos.
        private static void WriteWaves(Component w, Material m, string type)
        {
            bool pool = type == "Pool";
            bool river = type == "River";
            float windKmh = pool ? Num(w, "ripplesWindSpeed", 8f) : Num(w, "largeWindSpeed", 30f);
            float u = windKmh / 3.6f;
            float repetition = Mathf.Max(1f, Num(w, "repetitionSize", pool ? 10f : river ? 75f : 500f));
            float chaos = Mathf.Clamp01(pool ? Num(w, "ripplesChaos", 0.8f) : Num(w, "largeChaos", 0.8f));
            float orientation = pool ? Num(w, "ripplesOrientationValue", 0f) : Num(w, "largeOrientationValue", 0f);
            float band0 = pool ? 1f : Num(w, "largeBand0Multiplier", 1f);
            float band1 = pool ? 1f : Num(w, "largeBand1Multiplier", 1f);

            float height = pool ? 0.02f * u : (float)WaterSpectrum.SignificantHeight(u);   // significant wave height (m)
            height = Mathf.Min(height, repetition / 14f);
            var amps = new double[WaterSpectrum.Count];
            var lengths = new double[WaterSpectrum.Count];
            if (pool)
            {
                float longest = Mathf.Min(repetition * 0.3f, 3f);
                double sum = 0;
                for (int i = 0; i < WaterSpectrum.Count; i++)
                {
                    lengths[i] = longest * Mathf.Pow(0.62f, i);
                    amps[i] = Math.Pow(lengths[i], 0.9);
                    sum += amps[i];
                }
                for (int i = 0; i < WaterSpectrum.Count; i++) amps[i] = sum > 0 ? amps[i] / sum * height * 0.5 : 0;
            }
            else WaterSpectrum.WindSea(u, height, river ? repetition * 0.25f : repetition * 0.45f, band0, band1, lengths, amps);
            var rng = new System.Random(w.GetInstanceID() & 0x7fffffff);
            for (int i = 0; i < WaterSpectrum.Count; i++)
            {
                float spread = chaos * (river ? 25f : 75f);
                float deg = orientation + (float)(rng.NextDouble() * 2.0 - 1.0) * spread;
                float rad = deg * Mathf.Deg2Rad;
                m.SetVector("_Wave" + i, new Vector4(Mathf.Cos(rad), Mathf.Sin(rad), (float)amps[i], Mathf.Max(0.05f, (float)lengths[i])));
            }
            m.SetFloat("_WaveSteepness", pool ? 0.3f : Mathf.Lerp(0.4f, 0.8f, chaos));
            m.SetFloat("_WaveFadeDistance", pool ? 60f : 400f);
            float current = river ? Num(w, "largeCurrentSpeedValue", 1f) / 3.6f : 0f;
            float orad = orientation * Mathf.Deg2Rad;
            m.SetVector("_Current", new Vector4(Mathf.Cos(orad) * current, 0f, Mathf.Sin(orad) * current, 0f));

            bool ripplesOn = Bool(w, "ripples", true);
            float ru = Num(w, "ripplesWindSpeed", 8f) / 3.6f;
            float rrad = Num(w, "ripplesOrientationValue", orientation) * Mathf.Deg2Rad;
            m.SetVector("_RippleWind", new Vector4(Mathf.Cos(rrad) * ru, Mathf.Sin(rrad) * ru, ripplesOn ? Mathf.Clamp(ru / 6f, 0.15f, 1f) : 0f, 0f));
            m.SetFloat("_RippleScale", pool ? 3f : 6f);
        }

        private static void WriteDeformers(Component surface, Material m, List<Component> deformers)
        {
            int n = 0;
            foreach (Component d in deformers)
            {
                if (n >= 4) break;
                if (!(d is Behaviour b) || !b.enabled) continue;
                string t = Get(d, "type")?.ToString() ?? "Sphere";
                float type = t == "Sphere" ? 1f : t == "Box" ? 2f : t == "ShoreWave" ? 3f : t == "BowWave" ? 4f : 2f;
                Vector2 size = Get(d, "regionSize") is Vector2 v ? v : new Vector2(d.transform.lossyScale.x, d.transform.lossyScale.z);
                float amplitude = Num(d, "amplitude", 1f);
                Vector3 p = d.transform.position;
                m.SetVector("_Deform" + n, new Vector4(p.x, p.y, p.z, type));
                m.SetVector("_DeformSize" + n, new Vector4(size.x, size.y, amplitude, -d.transform.eulerAngles.y * Mathf.Deg2Rad));
                n++;
            }
            m.SetFloat("_DeformCount", n);
        }

        private static void BuildMesh(Component w, Material m, string type, string folder, CompatibilityReport report)
        {
            string geometry = Get(w, "geometryType")?.ToString() ?? "Infinite";
            bool ocean = type == "OceanSeaLake" && (geometry == "Infinite" || geometry == "InstancedQuads");
            var underwater = Bool(w, "underWater", false);
            float underwaterDistance = Num(w, "absorptionDistance", 5f) * Num(w, "absorptionDistanceMultiplier", 1f);

            if (geometry == "Custom" && Get(w, "meshRenderers") is System.Collections.IEnumerable list)
            {
                int count = 0;
                foreach (object o in list)
                {
                    if (!(o is MeshRenderer r) || r == null) continue;
                    r.sharedMaterials = new[] { m };
                    AddSurface(r.gameObject, false, underwater, underwaterDistance, w);
                    count++;
                }
                if (count > 0) return;
            }

            var go = new GameObject("ParelVR Water");
            go.transform.SetParent(w.transform, false);
            Mesh mesh;
            if (ocean)
            {
                // The grid follows the player: keep it level, at the surface's height, unscaled.
                go.transform.SetParent(null, true);
                go.transform.SetPositionAndRotation(new Vector3(0f, w.transform.position.y, 0f), Quaternion.identity);
                go.transform.localScale = Vector3.one;
                go.transform.SetParent(w.transform, true);
                mesh = ParelWaterTextures.OceanGrid();
            }
            else
            {
                Vector3 s = w.transform.lossyScale;
                int cells = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(s.x, s.z) / 0.4f), 16, 220);
                mesh = ParelWaterTextures.Grid(Vector2.one, cells);
            }
            Save(mesh, folder, w.name + " water mesh");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            AddSurface(go, ocean, underwater, underwaterDistance, w);
        }

        private static void AddSurface(GameObject go, bool ocean, bool underwater, float distance, Component w)
        {
            var s = go.AddComponent<ParelWaterSurface>();
            s.Ocean = ocean;
            s.Underwater = underwater || ocean;
            s.UnderwaterAbsorptionDistance = Mathf.Max(0.5f, distance);
            if (Get(w, "volumeBounds") is Collider c && c != null)
            {
                Bounds wb = c.bounds;
                Vector3 centre = go.transform.InverseTransformPoint(wb.center);
                Vector3 size = go.transform.InverseTransformVector(wb.size);
                s.UnderwaterVolume = new Bounds(centre, new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)));
            }
        }

        private static Mesh FirstMesh(Component c)
        {
            foreach (FieldInfo f in c.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (typeof(Mesh).IsAssignableFrom(f.FieldType) && f.GetValue(c) is Mesh m && m != null) return m;
            MeshFilter mf = c.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static object Get(object target, string member)
        {
            object v = SrpSceneReader.Get(target, member);
            // Some water settings are volume-style parameters: take their value.
            object inner = v != null && v.GetType().Name.EndsWith("Parameter") ? SrpSceneReader.Get(v, "value") : null;
            return inner ?? v;
        }

        private static float Num(object target, string member, float fallback)
        {
            object v = Get(target, member);
            return v is float || v is int || v is double ? SrpSceneReader.Float(v) : fallback;
        }

        private static bool Bool(object target, string member, bool fallback) => Get(target, member) is bool b ? b : fallback;
        private static Color Col(object target, string member, Color fallback) => Get(target, member) is Color c ? c : fallback;

        private static void Save(UnityEngine.Object asset, string folder, string name)
        {
            if (!AssetDatabase.IsValidFolder(folder)) ParelSceneConverter.CreateFolder(folder);
            string ext = asset is Material ? ".mat" : ".asset";
            string safe = string.Join("_", name.Split(System.IO.Path.GetInvalidFileNameChars()));
            AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + safe + ext));
        }
    }
}
