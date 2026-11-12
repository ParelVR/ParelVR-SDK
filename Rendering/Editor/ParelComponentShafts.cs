using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// God rays / sun shafts a URP world draws with an effect of its own: a renderer feature on the creator's URP renderer,
    /// an override in one of its volume profiles, or a script on a camera or light. The game has none of those scripts
    /// (a world bundle carries no code), so the feature loads as "missing" and the shafts are gone. Its settings are read
    /// here, by name, and the game draws the shafts itself (2026-11-11, world "Gods URP Trials").
    /// </summary>
    public static class ParelComponentShafts
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly string[] Tokens =
        {
            "godray", "god_ray", "sunshaft", "sun_shaft", "lightshaft", "light_shaft", "volumetriclight", "volumetric_light",
            "lightscatter", "light_scatter", "crepuscular", "sunray", "sun_ray", "lightray", "light_ray", "radialblur",
        };

        /// <summary>The first god-ray effect in the project's pipeline or in <paramref name="scene"/>; null when there is none.</summary>
        public static ParelLightShaftSettings Find(Scene scene)
        {
            // 1. A renderer feature of the pipeline the world is built with. Its strength usually sits in a volume
            // override (below); the feature alone means the default look.
            ScriptableObject featureFound = null;
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            if (asset != null && Field(asset, "m_RendererDataList") is IEnumerable datas)
                foreach (object data in datas)
                {
                    if (featureFound != null) break;
                    if (!(data is ScriptableObject so) || !(Prop(so, "rendererFeatures") is IEnumerable features)) continue;
                    foreach (object feature in features)
                    {
                        if (!(feature is ScriptableObject f)) continue;
                        if (Prop(f, "isActive") is bool on && !on) continue;
                        if (!Matches(f.GetType().Name) && !Matches(f.name)) continue;
                        featureFound = f;
                        break;
                    }
                }

            ParelLightShaftSettings fromScene = FindInScene(scene);
            if (fromScene != null)
            {
                if (featureFound != null) fromScene.Source = "renderer feature " + featureFound.GetType().Name + " + " + fromScene.Source;
                return fromScene;
            }
            return featureFound != null ? Read(featureFound, "renderer feature " + featureFound.GetType().Name + " (" + featureFound.name + ")") : null;
        }

        private static ParelLightShaftSettings FindInScene(Scene scene)
        {
            // 2. Volume overrides (global and local volumes of the scene, the project defaults embedded at upload).
            Type volumeType = Type.GetType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime");
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (volumeType != null)
                    foreach (Component v in root.GetComponentsInChildren(volumeType, true))
                    {
                        if (v == null || !v.gameObject.activeInHierarchy) continue;
                        if (!(Field(v, "sharedProfile") is ScriptableObject profile) || !(Field(profile, "components") is IEnumerable comps)) continue;
                        foreach (object c in comps)
                        {
                            if (!(c is ScriptableObject comp) || !Matches(comp.GetType().Name)) continue;
                            if (Field(comp, "active") is bool on && !on) continue;
                            if (Look(comp, "enabled") is bool en && !en) continue;
                            return Read(comp, "volume override " + comp.GetType().Name);
                        }
                    }

                // 3. A script on a camera, light or effect object.
                foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || !mb.enabled || !mb.gameObject.activeInHierarchy) continue;
                    Type t = mb.GetType();
                    if (t.Namespace != null && (t.Namespace.StartsWith("ParelVR", StringComparison.Ordinal) || t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))) continue;
                    if (!Matches(t.Name)) continue;
                    return Read(mb, "script " + t.Name + " on " + mb.name);
                }
            }
            return null;
        }

        private static bool Matches(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
            string squashed = n.Replace("_", "");
            foreach (string t in Tokens)
                if (n.Contains(t) || squashed.Contains(t.Replace("_", ""))) return true;
            return false;
        }

        private static ParelLightShaftSettings Read(object o, string source)
        {
            var s = new ParelLightShaftSettings { Enabled = true, Source = source };
            float? intensity = Num(o, "intensity", "Intensity", "m_Intensity", "strength", "exposure", "Exposure", "weight", "Weight", "power", "brightness");
            if (intensity != null) s.Intensity = Mathf.Clamp(intensity.Value, 0f, 8f);
            float? density = Num(o, "density", "Density", "m_Density", "fogDensity");
            if (density != null) s.Density = Mathf.Clamp(density.Value, 0f, 4f);
            float? reach = Num(o, "maxDistance", "MaxDistance", "m_MaxDistance", "distance", "maxRayLength", "rayLength", "range");
            if (reach != null && reach.Value > 0f) s.MaxDistance = Mathf.Clamp(reach.Value, 5f, 500f);
            float? g = Num(o, "scattering", "anisotropy", "Anisotropy", "mieG", "g", "phase");
            if (g != null) s.Anisotropy = Mathf.Clamp(g.Value, -0.95f, 0.95f);
            Color? tint = Col(o, "tint", "Tint", "color", "colour", "rayColor", "m_Color", "lightColor");
            if (tint != null) s.Tint = new Color(tint.Value.r, tint.Value.g, tint.Value.b, 1f);
            float? samples = Num(o, "steps", "samples", "sampleCount", "numSamples", "m_Samples", "maxSteps");
            if (samples != null) s.Samples = Mathf.Clamp(Mathf.RoundToInt(samples.Value), 8, 128);
            return s;
        }

        // ---- reflection (fields, properties, a nested "settings" object, VolumeParameter<T>.value) ------------------

        private static object Field(object o, string name)
        {
            for (Type t = o.GetType(); t != null && t != typeof(object) && t != typeof(UnityEngine.Object); t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, F);
                if (f != null) return f.GetValue(o);
            }
            return Prop(o, name);
        }

        private static object Prop(object o, string name)
        {
            for (Type t = o.GetType(); t != null && t != typeof(object) && t != typeof(UnityEngine.Object); t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, F | BindingFlags.DeclaredOnly);
                if (p == null || !p.CanRead || p.GetIndexParameters().Length != 0) continue;
                try { return p.GetValue(o); } catch (Exception) { return null; }
            }
            return null;
        }

        private static object Unwrap(object v)
        {
            if (v == null) return null;
            Type t = v.GetType();
            if (t.Name.IndexOf("Parameter", StringComparison.Ordinal) >= 0)
            {
                PropertyInfo p = t.GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
                if (p != null) try { return p.GetValue(v); } catch (Exception) { return null; }
            }
            return v;
        }

        private static object Look(object o, string name)
        {
            object v = Unwrap(Field(o, name));
            if (v != null) return v;
            // Most features keep their values in a nested settings object.
            foreach (string nest in new[] { "settings", "Settings", "m_Settings", "passSettings", "parameters" })
                if (Field(o, nest) is object inner && !(inner is UnityEngine.Object) && !inner.GetType().IsPrimitive)
                {
                    v = Unwrap(Field(inner, name));
                    if (v != null) return v;
                }
            return null;
        }

        private static float? Num(object o, params string[] names)
        {
            foreach (string n in names)
            {
                object v = Look(o, n);
                if (v is float f) return f;
                if (v is int i) return i;
                if (v is double d) return (float)d;
            }
            return null;
        }

        private static Color? Col(object o, params string[] names)
        {
            foreach (string n in names) if (Look(o, n) is Color c) return c;
            return null;
        }
    }
}
