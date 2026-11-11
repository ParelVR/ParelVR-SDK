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
        /// <summary>The HDRP volume components themselves, for the sky converter to read every parameter.</summary>
        public object PhysicallyBasedSkyComponent, GradientSkyComponent, CloudLayerComponent, VolumetricCloudsComponent;
        /// <summary>HDRP Visual Environment: which sky is drawn (HDRP SkyType: 0 none, 1 HDRI, 2 procedural, 3 gradient, 4 physically based), when overridden.</summary>
        public int? SkyType;
        /// <summary>HDRP Visual Environment: which cloud type is drawn (0 none, 1 cloud layer), when overridden.</summary>
        public int? CloudType;
        /// <summary>Each sky override's own exposure (x, EV) and multiplier (y), by HDRP SkyType.</summary>
        public readonly Dictionary<int, Vector2> SkyIntensity = new Dictionary<int, Vector2>();

        /// <summary>The fixed exposure, when the scene's exposure is fixed.</summary>
        public float? FixedEv100;
        public bool PhysicalCameraExposure;
        public float ExposureCompensation;

        public bool Fog;
        public float FogMeanFreePath = 400f;
        public Color FogColor = new Color(0.5f, 0.5f, 0.5f);
        public bool FogColorFromSky = true;
        public bool VolumetricFog;
        public float FogBaseHeight;
        public float FogMaximumHeight = 50f;
        public float FogMaxDistance = 5000f;
        public Color FogAlbedo = Color.white;
        public Color FogTint = Color.white;
        public float FogAnisotropy;
        public float FogLightDimmer = 1f;
        public float FogVolumetricDistance = 64f;

        /// <summary>Screen-space lighting (reflections, GI, contact / micro shadows) drawn by the ParelVR lit shaders.</summary>
        public readonly ParelScreenEffectsSettings Screen = new ParelScreenEffectsSettings();

        /// <summary>Post-processing overrides ParelVR can show (through the game's post-processing).</summary>
        public readonly ParelPostSettings Post = new ParelPostSettings();

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
            settings.Post.Enabled = true;
            settings.Post.Enabled = settings.Post.Any;
            PickShownSky(settings);
            return settings;
        }

        // A profile can hold several sky overrides (HDRP's templates carry HDRI, gradient and physically based skies);
        // only the one the Visual Environment names is drawn. Without a Visual Environment override HDRP's default
        // profile draws the physically based sky.
        private const int SkyNone = 0, SkyHdri = 1, SkyGradient = 3, SkyPhysical = 4;

        private static void PickShownSky(SrpVolumeSettings s)
        {
            int shown;
            if (s.SkyType.HasValue) shown = s.SkyType.Value;
            else if (s.PhysicallyBasedSky) shown = SkyPhysical;
            else if (s.HdriSky) shown = SkyHdri;
            else if (s.GradientSky) shown = SkyGradient;
            else return;
            s.HdriSky &= shown == SkyHdri;
            s.GradientSky &= shown == SkyGradient;
            s.PhysicallyBasedSky &= shown == SkyPhysical;
            if (!s.HdriSky) s.SkyTexture = null;
            if (s.SkyIntensity.TryGetValue(shown, out Vector2 intensity))
            {
                s.SkyExposureEv = intensity.x;
                s.SkyMultiplier = intensity.y;
            }
            if (shown == SkyNone) s.NotCarried.Add("Visual Environment (no sky)");
            if (s.CloudType.HasValue && s.CloudType.Value != 1) s.CloudLayerComponent = null;
        }

        /// <summary>HDRP Local Volumetric Fog (and the older Density Volume) boxes in the scene.</summary>
        public static List<LocalFogVolume> LocalFog(Scene scene)
        {
            var list = new List<LocalFogVolume>();
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    string type = c.GetType().Name;
                    if (type != "LocalVolumetricFog" && type != "DensityVolume") continue;
                    object p = Get(c, "parameters");
                    if (p == null) continue;
                    float mfp = Float(Get(p, "meanFreePath"));
                    list.Add(new LocalFogVolume
                    {
                        Volume = c.transform,
                        Size = Get(p, "size") is Vector3 size ? size : Vector3.one,
                        Albedo = Get(p, "albedo") is Color a ? a : Color.white,
                        Extinction = mfp > 0f ? 1f / mfp : 0.05f,
                    });
                }
            return list;
        }

        private static void ReadComponent(object c, SrpVolumeSettings s)
        {
            string type = c.GetType().Name;
            switch (type)
            {
                case "HDRISky":
                    s.HdriSky = true;
                    s.SkyExposureEv = 0f;
                    s.SkyMultiplier = 1f;
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
                    s.SkyIntensity[SkyHdri] = new Vector2(s.SkyExposureEv, s.SkyMultiplier);
                    break;
                case "GradientSky":
                    s.GradientSky = true;
                    s.GradientSkyComponent = c;
                    if (Param(c, "top", out object top)) s.GradientTop = (Color)top;
                    if (Param(c, "middle", out object mid)) s.GradientMiddle = (Color)mid;
                    if (Param(c, "bottom", out object bot)) s.GradientBottom = (Color)bot;
                    s.SkyExposureEv = Param(c, "exposure", out object gev) ? Float(gev) : 0f;
                    s.SkyMultiplier = Param(c, "multiplier", out object gmul) ? Float(gmul) : 1f;
                    s.SkyIntensity[SkyGradient] = new Vector2(s.SkyExposureEv, s.SkyMultiplier);
                    break;
                case "PhysicallyBasedSky":
                    s.PhysicallyBasedSky = true;
                    s.PhysicallyBasedSkyComponent = c;
                    s.SkyExposureEv = Param(c, "exposure", out object pev) ? Float(pev) : 0f;
                    s.SkyMultiplier = Param(c, "multiplier", out object pmul) ? Float(pmul) : 1f;
                    s.SkyIntensity[SkyPhysical] = new Vector2(s.SkyExposureEv, s.SkyMultiplier);
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
                    if (Param(c, "tint", out object ft)) s.FogTint = (Color)ft;
                    if (Param(c, "enableVolumetricFog", out object vol)) s.VolumetricFog = Bool(vol, false);
                    if (Param(c, "baseHeight", out object bh)) s.FogBaseHeight = Float(bh);
                    if (Param(c, "maximumHeight", out object mh)) s.FogMaximumHeight = Float(mh);
                    if (Param(c, "maxFogDistance", out object md)) s.FogMaxDistance = Float(md);
                    if (Param(c, "albedo", out object fa)) s.FogAlbedo = (Color)fa;
                    if (Param(c, "anisotropy", out object an)) s.FogAnisotropy = Float(an);
                    if (Param(c, "globalLightProbeDimmer", out object dim)) s.FogLightDimmer = Float(dim);
                    if (Param(c, "depthExtent", out object de)) s.FogVolumetricDistance = Float(de);
                    break;
                case "Bloom":
                    s.Post.Bloom = true;
                    if (Param(c, "intensity", out object bi)) s.Post.BloomIntensity = Float(bi);
                    if (Param(c, "threshold", out object bt)) s.Post.BloomThreshold = Float(bt);
                    if (Param(c, "scatter", out object bs)) s.Post.BloomScatter = Float(bs);
                    if (Param(c, "tint", out object btn)) s.Post.BloomTint = (Color)btn;
                    break;
                case "Tonemapping":
                    string tm = Param(c, "mode", out object tmo) ? tmo?.ToString() ?? "" : "";
                    s.Post.Tonemapping = tm == "ACES" ? ParelPostSettings.ToneMapper.Aces
                        : tm == "Neutral" ? ParelPostSettings.ToneMapper.Neutral
                        : tm == "Custom" ? ParelPostSettings.ToneMapper.Custom
                        : tm == "External" ? ParelPostSettings.ToneMapper.External
                        : tm == "None" || tm == "" ? ParelPostSettings.ToneMapper.None
                        : ParelPostSettings.ToneMapper.Aces; // AgX: the closest filmic curve
                    s.Post.ToeStrength = Float(Value(c, "toeStrength"));
                    s.Post.ToeLength = Float(Value(c, "toeLength"));
                    s.Post.ShoulderStrength = Float(Value(c, "shoulderStrength"));
                    s.Post.ShoulderLength = Float(Value(c, "shoulderLength"));
                    s.Post.ShoulderAngle = Float(Value(c, "shoulderAngle"));
                    s.Post.TonemapGamma = Value(c, "gamma") is float g ? g : 1f;
                    s.Post.ExternalLut = Value(c, "lutTexture") as Texture;
                    if (s.Post.Tonemapping == ParelPostSettings.ToneMapper.External && s.Post.ExternalLut == null) s.Post.Tonemapping = ParelPostSettings.ToneMapper.Aces;
                    break;
                case "ChannelMixer":
                    s.Post.ChannelMixer = true;
                    s.Post.MixerRed = new Vector3(Float(Value(c, "redOutRedIn")), Float(Value(c, "redOutGreenIn")), Float(Value(c, "redOutBlueIn")));
                    s.Post.MixerGreen = new Vector3(Float(Value(c, "greenOutRedIn")), Float(Value(c, "greenOutGreenIn")), Float(Value(c, "greenOutBlueIn")));
                    s.Post.MixerBlue = new Vector3(Float(Value(c, "blueOutRedIn")), Float(Value(c, "blueOutGreenIn")), Float(Value(c, "blueOutBlueIn")));
                    break;
                case "ColorCurves":
                    s.Post.Curves = true;
                    s.Post.MasterCurve = Curve(Value(c, "master"));
                    s.Post.RedCurve = Curve(Value(c, "red"));
                    s.Post.GreenCurve = Curve(Value(c, "green"));
                    s.Post.BlueCurve = Curve(Value(c, "blue"));
                    s.Post.HueVsHue = Curve(Value(c, "hueVsHue"));
                    s.Post.HueVsSat = Curve(Value(c, "hueVsSat"));
                    s.Post.SatVsSat = Curve(Value(c, "satVsSat"));
                    s.Post.LumVsSat = Curve(Value(c, "lumVsSat"));
                    break;
                case "SplitToning":
                    s.Post.SplitToning = true;
                    if (Value(c, "shadows") is Color sh) s.Post.SplitShadows = sh;
                    if (Value(c, "highlights") is Color hi) s.Post.SplitHighlights = hi;
                    s.Post.SplitBalance = Float(Value(c, "balance"));
                    break;
                case "ShadowsMidtonesHighlights":
                    s.Post.ShadowsMidtonesHighlights = true;
                    if (Value(c, "shadows") is Vector4 sv) s.Post.Shadows = sv;
                    if (Value(c, "midtones") is Vector4 mv) s.Post.Midtones = mv;
                    if (Value(c, "highlights") is Vector4 hv) s.Post.Highlights = hv;
                    break;
                case "PaniniProjection":
                    s.Post.Pannini = true;
                    s.Post.PanniniDistance = Float(Value(c, "distance"));
                    s.Post.PanniniCropToFit = Value(c, "cropToFit") is float cf2 ? cf2 : 1f;
                    break;
                case "ColorAdjustments":
                    s.Post.ColorAdjustments = true;
                    if (Param(c, "postExposure", out object pe)) s.Post.PostExposure = Float(pe);
                    if (Param(c, "contrast", out object ct)) s.Post.Contrast = Float(ct);
                    if (Param(c, "hueShift", out object hs)) s.Post.HueShift = Float(hs);
                    if (Param(c, "saturation", out object sat)) s.Post.Saturation = Float(sat);
                    if (Param(c, "colorFilter", out object cf)) s.Post.ColorFilter = (Color)cf;
                    break;
                case "WhiteBalance":
                    s.Post.WhiteBalance = true;
                    if (Param(c, "temperature", out object temp)) s.Post.Temperature = Float(temp);
                    if (Param(c, "tint", out object wt)) s.Post.Tint = Float(wt);
                    break;
                case "LiftGammaGain":
                    s.Post.LiftGammaGain = true;
                    if (Param(c, "lift", out object lift)) s.Post.Lift = (Vector4)lift;
                    if (Param(c, "gamma", out object gam)) s.Post.Gamma = (Vector4)gam;
                    if (Param(c, "gain", out object gain)) s.Post.Gain = (Vector4)gain;
                    break;
                case "Vignette":
                    s.Post.Vignette = true;
                    if (Param(c, "color", out object vc)) s.Post.VignetteColor = (Color)vc;
                    if (Param(c, "center", out object vce)) s.Post.VignetteCenter = (Vector2)vce;
                    if (Param(c, "intensity", out object vi)) s.Post.VignetteIntensity = Float(vi);
                    if (Param(c, "smoothness", out object vs)) s.Post.VignetteSmoothness = Float(vs);
                    if (Param(c, "rounded", out object vr)) s.Post.VignetteRounded = Bool(vr, false);
                    break;
                case "ChromaticAberration":
                    s.Post.ChromaticAberration = true;
                    if (Param(c, "intensity", out object ca)) s.Post.ChromaticAberrationIntensity = Float(ca);
                    break;
                case "FilmGrain":
                    s.Post.FilmGrain = true;
                    if (Param(c, "intensity", out object gi)) s.Post.GrainIntensity = Float(gi);
                    if (Param(c, "response", out object gr)) s.Post.GrainResponse = Float(gr);
                    break;
                case "ScreenSpaceAmbientOcclusion":
                case "AmbientOcclusion":
                    s.Post.AmbientOcclusion = true;
                    if (Param(c, "intensity", out object ao)) s.Post.AmbientOcclusionIntensity = Float(ao);
                    break;
                case "DepthOfField":
                    string dm = Param(c, "focusMode", out object dmo) ? dmo?.ToString() ?? "" : "";
                    s.Post.DepthOfField = dm != "Off" && dm != "None";
                    if (Param(c, "focusDistance", out object fd)) s.Post.FocusDistance = Float(fd);
                    break;
                case "MotionBlur":
                    s.Post.MotionBlur = true;
                    if (Param(c, "intensity", out object mb)) s.Post.MotionBlurIntensity = Float(mb);
                    break;
                case "LensDistortion":
                    s.Post.LensDistortion = true;
                    if (Param(c, "intensity", out object ld)) s.Post.LensDistortionIntensity = Float(ld);
                    break;
                case "CloudLayer":
                    s.CloudLayerComponent = c;
                    break;
                case "VolumetricClouds":
                    if (Param(c, "enable", out object vcOn) && Bool(vcOn, false)) s.VolumetricCloudsComponent = c;
                    break;
                case "ScreenSpaceReflection":
                    if (!Param(c, "enabled", out object ssrOn) || Bool(ssrOn, true))
                    {
                        s.Screen.Reflections = true;
                        if (Param(c, "minSmoothness", out object ms)) s.Screen.ReflectionMinSmoothness = Float(ms);
                        if (Param(c, "smoothnessFadeStart", out object fs)) s.Screen.ReflectionFadeStart = Float(fs);
                    }
                    break;
                case "GlobalIllumination":
                    if (Param(c, "enable", out object giOn) && Bool(giOn, false))
                    {
                        s.Screen.GlobalIllumination = true;
                        if (Param(c, "maxRaySteps", out object steps)) s.Screen.GlobalIlluminationRayLength = Mathf.Clamp(Float(steps) / 16f, 0.5f, 6f);
                    }
                    break;
                case "ContactShadows":
                    if (Param(c, "enable", out object csOn) && Bool(csOn, false))
                    {
                        s.Screen.ContactShadows = true;
                        if (Param(c, "length", out object len)) s.Screen.ContactShadowLength = Float(len);
                        if (Param(c, "opacity", out object op)) s.Screen.ContactShadowOpacity = Float(op);
                        if (Param(c, "maxDistance", out object md2)) s.Screen.ContactShadowMaxDistance = Float(md2);
                    }
                    break;
                case "MicroShadowing":
                    if (Param(c, "enable", out object msOn) && Bool(msOn, false))
                        s.Screen.MicroShadowOpacity = Param(c, "opacity", out object mo) ? Float(mo) : 1f;
                    break;
                case "ScreenSpaceRefraction":
                    // Refraction is drawn per material (ParelVR lit shaders); nothing to carry from the volume.
                    break;
                case "VisualEnvironment":
                    if (Param(c, "skyType", out object skyType)) s.SkyType = (int)Float(skyType);
                    if (Param(c, "cloudType", out object cloudType)) s.CloudType = (int)Float(cloudType);
                    break;
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

        /// <summary>An HDRP TextureCurve sampled into an AnimationCurve (32 points); null when it is a straight default.</summary>
        internal static AnimationCurve Curve(object textureCurve)
        {
            if (textureCurve == null) return null;
            MethodInfo eval = textureCurve.GetType().GetMethod("Evaluate", new[] { typeof(float) });
            if (eval == null) return null;
            var keys = new Keyframe[32];
            for (int i = 0; i < 32; i++)
            {
                float t = i / 31f;
                keys[i] = new Keyframe(t, (float)eval.Invoke(textureCurve, new object[] { t }));
            }
            var curve = new AnimationCurve(keys);
            for (int i = 0; i < keys.Length; i++) curve.SmoothTangents(i, 0f);
            return curve;
        }

        /// <summary>A volume parameter's value whether or not its override is on (its default when off).</summary>
        internal static object Value(object component, string field) => Get(Get(component, field), "value");

        internal static float Float(object v) => v is float f ? f : v is int i ? i : v is double d ? (float)d : 0f;
        internal static bool Bool(object v, bool fallback) => v is bool b ? b : fallback;
    }
}
