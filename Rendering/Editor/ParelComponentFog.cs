using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// Volumetric / ground fog that a URP (or Built-in) world draws with a fog component of its own: a script on a scene
    /// object with a density and a fog colour, plus a height, a reach or a ground height (terrain ground fog,
    /// "volumetric fog" assets and the like). The game does not have that script, so without this the world shows no fog
    /// at all and far things stay sharp. Its settings become ParelVR volumetric fog (2026-11-11, world "Gods URP Trials").
    /// </summary>
    public static class ParelComponentFog
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>The first fog component found in <paramref name="scene"/>, as ParelVR fog; null when there is none.</summary>
        public static ParelFogSettings Find(Scene scene, CompatibilityReport report)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || !mb.enabled || !mb.gameObject.activeInHierarchy) continue;
                    Type t = mb.GetType();
                    if (t.Namespace != null && t.Namespace.StartsWith("ParelVR", StringComparison.Ordinal)) continue;
                    float? density = Num(mb, "density", "fogDensity", "_density");
                    Color? colour = Col(mb, "fogColor", "fogColour", "color", "albedo");
                    bool fogShaped = Num(mb, "groundFogHeight", "maximumFogDistance", "fogHeight", "heightFalloff", "baseHeight", "maxDistance") != null
                        || t.Name.IndexOf("Fog", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (density == null || colour == null || !fogShaped) continue;
                    ParelFogSettings f = Map(mb, density.Value, colour.Value);
                    report.Note(DiagnosticSeverity.Info, "Fog", t.Name + " on " + mb.name + " converted to ParelVR volumetric fog (density " + density.Value.ToString("0.###")
                        + ", up to " + f.MaxDistance.ToString("0") + " m, base " + f.BaseHeight.ToString("0.#") + " m). The world's own fog script does not run in the game.");
                    return f;
                }
            return null;
        }

        private static ParelFogSettings Map(MonoBehaviour mb, float density, Color colour)
        {
            // Where the ground is: the terrain the fog sits on, else the object.
            float ground = mb.transform.position.y;
            Terrain terrain = Terrain.activeTerrain;
            foreach (Terrain t in Terrain.activeTerrains) if (t.gameObject.scene == mb.gameObject.scene) { terrain = t; break; }
            if (terrain != null) ground = terrain.transform.position.y;

            float groundHeight = Num(mb, "groundFogHeight", "fogHeight", "baseHeight") ?? 1f;
            float top = (Num(mb, "topPadding") ?? 40f) * Mathf.Clamp(Num(mb, "topSoftness") ?? 0.2f, 0.05f, 1f);
            float reach = Num(mb, "maximumFogDistance", "maxDistance", "fogDistance") ?? 300f;

            // The fog's own light: its ambient colour (grey, so the fog keeps its own colour) and strength.
            Color amb = Col(mb, "ambientColor", "ambientColour") ?? new Color(0.5f, 0.5f, 0.5f);
            float ambStrength = Num(mb, "ambientStrength", "ambientIntensity") ?? 1f;
            float lum = amb.linear.grayscale * Mathf.Max(0f, ambStrength) * 0.35f;
            lum = Mathf.Clamp(lum, 0.08f, 1.5f);

            float sun = (Num(mb, "sunScattering") ?? 1f) * (Num(mb, "sunStrength") ?? 1f);
            bool shafts = Bool(mb, "enableVolumetricShadows", "volumetricShadows", "lightShafts") ?? false;

            return new ParelFogSettings
            {
                Enabled = true,
                // These assets' density is a 0–1 slider: 0.3 hazes things at 100 m to about half, like the creator's view.
                Extinction = Mathf.Clamp(density, 0f, 4f) * 0.02f,
                BaseHeight = ground + groundHeight,
                HeightFalloff = 6.9078f / Mathf.Max(10f, top * 2f),
                MaxDistance = Mathf.Max(20f, reach),
                VolumetricRange = shafts ? 64f : 0f,
                Albedo = new Color(colour.r, colour.g, colour.b),
                // The baked ambient probe of these worlds is near black (URP never wrote it): use the fog's own light.
                AmbientFromSky = false,
                Ambient = new Color(lum, lum, lum).gamma,
                Anisotropy = Mathf.Clamp(Num(mb, "anisotropy") ?? 0f, -0.9f, 0.9f),
                SunScattering = Mathf.Clamp(sun, 0f, 4f),
            };
        }

        private static object Field(object o, string name)
        {
            for (Type t = o.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, F);
                if (f != null) return f.GetValue(o);
                PropertyInfo p = t.GetProperty(name, F);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    try { return p.GetValue(o); } catch (Exception) { return null; }
                }
            }
            return null;
        }

        private static float? Num(object o, params string[] names)
        {
            foreach (string n in names)
            {
                object v = Field(o, n);
                if (v is float f) return f;
                if (v is int i) return i;
                if (v is double d) return (float)d;
            }
            return null;
        }

        private static Color? Col(object o, params string[] names)
        {
            foreach (string n in names) if (Field(o, n) is Color c) return c;
            return null;
        }

        private static bool? Bool(object o, params string[] names)
        {
            foreach (string n in names) if (Field(o, n) is bool b) return b;
            return null;
        }
    }
}
