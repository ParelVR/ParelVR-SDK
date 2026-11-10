using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Editor
{
    /// <summary>The HDRP/URP volume settings PVRP can carry over or must report, read without referencing either package.</summary>
    public sealed class SrpVolumeSettings
    {
        public bool HdriSky;
        public Texture SkyTexture;
        public float SkyExposureEv;
        public float SkyMultiplier = 1f;
        public float SkyRotation;
        public bool GradientSky;
        public Color GradientTop = Color.white, GradientMiddle = Color.gray, GradientBottom = Color.black;
        public bool PhysicallyBasedSky;

        /// <summary>The fixed exposure, when the scene's exposure is fixed.</summary>
        public float? FixedEv100;
        public bool PhysicalCameraExposure;
        public float ExposureCompensation;

        public bool Fog;
        public float FogMeanFreePath = 400f;
        public Color FogColor = new Color(0.5f, 0.5f, 0.5f);
        public bool FogColorFromSky = true;
        public bool VolumetricFog;

        /// <summary>Active volume effects ParelVR's Built-in renderer doesn't have, with their values, for the report.</summary>
        public readonly List<string> NotCarried = new List<string>();
    }

    /// <summary>
    /// Reads HDRP (and URP) global volumes through reflection, so the SDK compiles and runs in projects with either
    /// pipeline or neither. Only parameters whose override is switched on count, and the highest-priority volume wins.
    /// </summary>
    public static class SrpSceneReader
    {
        public static SrpVolumeSettings Read(Scene scene)
        {
            var settings = new SrpVolumeSettings();
            var volumes = new List<Component>();
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().FullName == "UnityEngine.Rendering.Volume" && c is Behaviour b && b.isActiveAndEnabled && Bool(Get(c, "isGlobal"), true))
                        volumes.Add(c);
            // Lowest priority first, so higher ones overwrite.
            volumes.Sort((a, b) => Float(Get(a, "priority")).CompareTo(Float(Get(b, "priority"))));

            foreach (Component volume in volumes)
            {
                object profile = Get(volume, "sharedProfile") ?? Get(volume, "profile");
                if (!(Get(profile, "components") is IList components)) continue;
                foreach (object component in components)
                {
                    if (component == null || !Bool(Get(component, "active"), true)) continue;
                    ReadComponent(component, settings);
                }
            }
            return settings;
        }

        private static void ReadComponent(object c, SrpVolumeSettings s)
        {
            string type = c.GetType().Name;
            switch (type)
            {
                case "HDRISky":
                    s.HdriSky = true;
                    if (Param(c, "hdriSky", out object tex)) s.SkyTexture = tex as Texture;
                    if (Param(c, "exposure", out object ev)) s.SkyExposureEv = Float(ev);
                    if (Param(c, "multiplier", out object mul)) s.SkyMultiplier = Float(mul);
                    if (Param(c, "rotation", out object rot)) s.SkyRotation = Float(rot);
                    if (Param(c, "skyIntensityMode", out object mode))
                    {
                        string m = mode?.ToString() ?? "";
                        if (m == "Multiplier") s.SkyExposureEv = 0f;
                        else if (m == "Exposure") s.SkyMultiplier = 1f;
                        else if (m == "Lux" && Param(c, "desiredLuxValue", out object want) && Param(c, "upperHemisphereLuxValue", out object have) && Float(have) > 0f)
                        {
                            s.SkyMultiplier = Float(want) / Float(have);
                            s.SkyExposureEv = 0f;
                        }
                    }
                    break;
                case "GradientSky":
                    s.GradientSky = true;
                    if (Param(c, "top", out object top)) s.GradientTop = (Color)top;
                    if (Param(c, "middle", out object mid)) s.GradientMiddle = (Color)mid;
                    if (Param(c, "bottom", out object bot)) s.GradientBottom = (Color)bot;
                    if (Param(c, "exposure", out object gev)) s.SkyExposureEv = Float(gev);
                    if (Param(c, "multiplier", out object gmul)) s.SkyMultiplier = Float(gmul);
                    break;
                case "PhysicallyBasedSky":
                    s.PhysicallyBasedSky = true;
                    if (Param(c, "exposure", out object pev)) s.SkyExposureEv = Float(pev);
                    if (Param(c, "multiplier", out object pmul)) s.SkyMultiplier = Float(pmul);
                    break;
                case "Exposure":
                    string modeName = Param(c, "mode", out object em) ? em?.ToString() ?? "" : "";
                    if (Param(c, "compensation", out object comp)) s.ExposureCompensation = Float(comp);
                    if (modeName == "Fixed" && Param(c, "fixedExposure", out object fixedEv)) s.FixedEv100 = Float(fixedEv);
                    else if (modeName == "UsePhysicalCamera") s.PhysicalCameraExposure = true;
                    break;
                case "Fog":
                    if (Param(c, "enabled", out object on)) s.Fog = Bool(on, false);
                    if (Param(c, "meanFreePath", out object mfp)) s.FogMeanFreePath = Float(mfp);
                    if (Param(c, "colorMode", out object cm)) s.FogColorFromSky = (cm?.ToString() ?? "SkyColor") != "ConstantColor";
                    if (Param(c, "color", out object fc)) s.FogColor = (Color)fc;
                    if (Param(c, "enableVolumetricFog", out object vol)) s.VolumetricFog = Bool(vol, false);
                    break;
                case "VisualEnvironment":
                case "HDShadowSettings":
                case "IndirectLightingController":
                    break;
                default:
                    // Post-processing and screen-space effects: named in the report with what is switched on.
                    s.NotCarried.Add(Describe(c, type));
                    break;
            }
        }

        private static string Describe(object c, string type)
        {
            var on = new List<string>();
            foreach (FieldInfo f in c.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object p = f.GetValue(c);
                if (p == null || !Bool(Get(p, "overrideState"), false)) continue;
                object v = Get(p, "value");
                string text = v is float fl ? fl.ToString("0.##") : v is bool bo ? (bo ? "on" : "off") : v is UnityEngine.Object o ? (o != null ? o.name : "none") : v?.ToString();
                on.Add(f.Name + " " + text);
                if (on.Count == 4) break;
            }
            return Readable(type) + (on.Count > 0 ? " (" + string.Join(", ", on) + ")" : "");
        }

        private static string Readable(string type)
        {
            switch (type)
            {
                case "ScreenSpaceReflection": return "screen-space reflections (reflection probes are used)";
                case "ScreenSpaceAmbientOcclusion": return "screen-space ambient occlusion (baked occlusion is used)";
                case "ContactShadows": return "contact shadows";
                case "MicroShadowing": return "micro shadows";
                case "ScreenSpaceRefraction": return "screen-space refraction";
                case "GlobalIllumination": return "screen-space global illumination (baked lighting is used)";
                case "Tonemapping": return "tonemapping";
                case "Bloom": return "bloom";
                case "ColorAdjustments": return "colour adjustments";
                case "Vignette": return "vignette";
                case "DepthOfField": return "depth of field";
                case "MotionBlur": return "motion blur";
                case "ChromaticAberration": return "chromatic aberration";
                case "FilmGrain": return "film grain";
                case "WhiteBalance": return "white balance";
                case "LensDistortion": return "lens distortion";
                case "PanniniProjection": return "Pannini projection";
                default: return type;
            }
        }

        /// <summary>The value of a volume parameter whose override is switched on.</summary>
        private static bool Param(object component, string field, out object value)
        {
            value = null;
            object p = Get(component, field);
            if (p == null || !Bool(Get(p, "overrideState"), false)) return false;
            value = Get(p, "value");
            return true;
        }

        internal static object Get(object target, string member)
        {
            if (target == null) return null;
            Type t = target.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type k = t; k != null; k = k.BaseType)
            {
                PropertyInfo p = k.GetProperty(member, flags | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
                FieldInfo f = k.GetField(member, flags | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
            }
            return null;
        }

        internal static float Float(object v) => v is float f ? f : v is int i ? i : v is double d ? (float)d : 0f;
        internal static bool Bool(object v, bool fallback) => v is bool b ? b : fallback;
    }
}
