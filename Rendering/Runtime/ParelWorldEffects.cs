using System;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// A world's volumetric fog, converted from HDRP's Fog override and Local Volumetric Fog. Drawn inside the ParelVR
    /// shaders (ParelVRFog.cginc): height fog with sun scattering and up to 4 local fog boxes, no extra pass.
    /// </summary>
    [Serializable]
    public sealed class ParelFogSettings
    {
        public const int MaxVolumes = 4;

        public bool Enabled;
        [Tooltip("How thick the fog is at and below the base height, per metre (HDRP: 1 / mean free path).")]
        public float Extinction = 0.0025f;
        [Tooltip("Height (m) below which the fog has its full thickness.")]
        public float BaseHeight;
        [Tooltip("How fast the fog thins out above the base height, per metre.")]
        public float HeightFalloff = 0.1f;
        [Tooltip("Fog beyond this distance (m) is not added (HDRP: Max Fog Distance).")]
        public float MaxDistance = 1000f;
        [Tooltip("How far (m) the fog is marched with light shafts and lamps (HDRP: Volumetric Fog Distance); beyond it the fog is worked out in one go.")]
        public float VolumetricRange = 64f;
        [Tooltip("Colour of the fog particles (as picked in HDRP).")]
        public Color Albedo = Color.white;
        [Tooltip("Light the fog receives from all around: the sky's ambient light, or Ambient.")]
        public bool AmbientFromSky = true;
        public Color Ambient = new Color(0.5f, 0.55f, 0.6f);
        [Tooltip("Forward scattering of the sun's light: 0 even, towards 1 a bright glow around the sun.")]
        [Range(-0.99f, 0.99f)] public float Anisotropy;
        [Tooltip("Multiplier for the sun light scattered in the fog (0 switches it off).")]
        public float SunScattering = 1f;
        public LocalFogVolume[] Volumes = Array.Empty<LocalFogVolume>();
    }

    /// <summary>
    /// A world's volumetric clouds, converted from HDRP's Volumetric Clouds override. Drawn by ParelSkyClouds into a
    /// panorama ParelVR/Sky shows, with cloud shadows on the ground. Distances in km, as in HDRP's inspector.
    /// </summary>
    [Serializable]
    public sealed class ParelCloudSettings
    {
        public bool Enabled;
        [Tooltip("How much of the sky the clouds cover (0..1).")]
        [Range(0f, 1f)] public float Coverage = 0.45f;
        [Tooltip("How thick the clouds are (HDRP density multiplier).")]
        public float Density = 0.4f;
        [Tooltip("0 tall rounded clouds .. 1 flat layers (HDRP shape factor).")]
        [Range(0f, 1f)] public float ShapeFactor = 0.9f;
        [Tooltip("How much the edges are worn into wisps (HDRP erosion factor).")]
        [Range(0f, 1f)] public float Erosion = 0.8f;
        [Tooltip("Size of the cloud shapes (km per noise tile; HDRP shape scale × 2).")]
        public float ShapeScaleKm = 10f;
        public float BottomAltitudeKm = 1.2f;
        public float ThicknessKm = 2f;
        [Tooltip("Wind speed (km/h) and the direction it blows to (degrees, 0 = +Z).")]
        public float WindSpeed = 20f;
        public float WindOrientation;
        public float SunLightDimmer = 1f;
        public float AmbientDimmer = 1f;
        public Color ScatteringTint = Color.white;
        public bool Shadows = true;
        [Range(0f, 1f)] public float ShadowOpacity = 0.75f;
    }

    /// <summary>A box of extra fog (HDRP Local Volumetric Fog). Follows its transform if it moves.</summary>
    [Serializable]
    public sealed class LocalFogVolume
    {
        public Transform Volume;
        [Tooltip("Size of the box in the volume's local space (m).")]
        public Vector3 Size = Vector3.one;
        public Color Albedo = Color.white;
        [Tooltip("Thickness inside the box, per metre.")]
        public float Extinction = 0.05f;
    }

    /// <summary>
    /// A world's post-processing, converted from HDRP volume overrides. The game draws it with its own post-processing
    /// (it builds a profile from this when the world loads), so the player's Post Processing setting and Adaptive
    /// Quality still decide whether it shows. Values are HDRP's.
    /// </summary>
    [Serializable]
    public sealed class ParelPostSettings
    {
        public enum ToneMapper { None, Neutral, Aces, Custom, External }

        public bool Enabled;

        public bool Bloom;
        public float BloomIntensity = 0.2f;
        public float BloomThreshold;
        public float BloomScatter = 0.7f;
        public Color BloomTint = Color.white;

        public ToneMapper Tonemapping;

        public bool ColorAdjustments;
        public float PostExposure;
        public float Contrast;
        public float HueShift;
        public float Saturation;
        public Color ColorFilter = Color.white;

        public bool WhiteBalance;
        public float Temperature;
        public float Tint;

        public bool LiftGammaGain;
        public Vector4 Lift = new Vector4(1f, 1f, 1f, 0f);
        public Vector4 Gamma = new Vector4(1f, 1f, 1f, 0f);
        public Vector4 Gain = new Vector4(1f, 1f, 1f, 0f);

        public bool Vignette;
        public Color VignetteColor = Color.black;
        public Vector2 VignetteCenter = new Vector2(0.5f, 0.5f);
        public float VignetteIntensity;
        public float VignetteSmoothness = 0.2f;
        public bool VignetteRounded;

        public bool ChromaticAberration;
        public float ChromaticAberrationIntensity;

        public bool FilmGrain;
        public float GrainIntensity;
        public float GrainResponse = 0.8f;

        public bool AmbientOcclusion;
        public float AmbientOcclusionIntensity;

        public bool DepthOfField;
        public float FocusDistance = 10f;
        public float Aperture = 5.6f;
        public float FocalLength = 50f;

        public bool MotionBlur;
        public float MotionBlurIntensity = 0.5f;

        public bool LensDistortion;
        public float LensDistortionIntensity;

        [Header("Tonemapping (Custom / External)")]
        public float ToeStrength, ToeLength = 0.5f, ShoulderStrength, ShoulderLength = 0.5f, ShoulderAngle, TonemapGamma = 1f;
        [Tooltip("HDRP External tonemapping: the 3D LUT it uses.")]
        public Texture ExternalLut;

        [Header("Channel Mixer")]
        public bool ChannelMixer;
        public Vector3 MixerRed = new Vector3(100f, 0f, 0f), MixerGreen = new Vector3(0f, 100f, 0f), MixerBlue = new Vector3(0f, 0f, 100f);

        [Header("Colour Curves")]
        public bool Curves;
        public AnimationCurve MasterCurve, RedCurve, GreenCurve, BlueCurve, HueVsHue, HueVsSat, SatVsSat, LumVsSat;

        [Header("Split Toning")]
        public bool SplitToning;
        public Color SplitShadows = new Color(0.5f, 0.5f, 0.5f), SplitHighlights = new Color(0.5f, 0.5f, 0.5f);
        [Range(-100f, 100f)] public float SplitBalance;

        [Header("Shadows Midtones Highlights")]
        public bool ShadowsMidtonesHighlights;
        public Vector4 Shadows = new Vector4(1f, 1f, 1f, 0f), Midtones = new Vector4(1f, 1f, 1f, 0f), Highlights = new Vector4(1f, 1f, 1f, 0f);

        [Header("Pannini Projection")]
        public bool Pannini;
        [Range(0f, 1f)] public float PanniniDistance;
        [Range(0f, 1f)] public float PanniniCropToFit = 1f;

        /// <summary>True when at least one effect is switched on.</summary>
        public bool Any => Enabled && (Bloom || Tonemapping != ToneMapper.None || ColorAdjustments || WhiteBalance || LiftGammaGain || Vignette ||
                                       ChromaticAberration || FilmGrain || AmbientOcclusion || DepthOfField || MotionBlur || LensDistortion ||
                                       ChannelMixer || Curves || SplitToning || ShadowsMidtonesHighlights || Pannini);
    }

    /// <summary>Sets the ParelVR fog shader globals from a world's <see cref="ParelFogSettings"/>.</summary>
    public static class ParelFogGlobals
    {
        private static readonly int ParamsId = Shader.PropertyToID("_ParelFogParams");
        private static readonly int ModeId = Shader.PropertyToID("_ParelFogMode");
        private static readonly int ColorId = Shader.PropertyToID("_ParelFogColor");
        private static readonly int AmbientId = Shader.PropertyToID("_ParelFogAmbient");
        private static readonly int SunDirId = Shader.PropertyToID("_ParelFogSunDir");
        private static readonly int SunColorId = Shader.PropertyToID("_ParelFogSunColor");
        private static readonly int CountId = Shader.PropertyToID("_ParelFogVolumeCount");
        private static readonly int MatricesId = Shader.PropertyToID("_ParelFogVolumeWorldToLocal");
        private static readonly int VolumeParamsId = Shader.PropertyToID("_ParelFogVolumeParams");
        private static readonly Matrix4x4[] Matrices = new Matrix4x4[ParelFogSettings.MaxVolumes];
        private static readonly Vector4[] VolumeParams = new Vector4[ParelFogSettings.MaxVolumes];
        private static readonly Vector3[] AmbientDirections = { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
        private static readonly Color[] AmbientResults = new Color[6];

        /// <summary>Switches ParelVR fog off (a world without it, or the world closed).</summary>
        public static void Clear()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(ModeId, Vector4.zero);
            Shader.SetGlobalVector(CountId, Vector4.zero);
        }

        /// <summary>
        /// Sets every fog global. <paramref name="sun"/> may be null (no sun scattering). <paramref name="exposureScale"/>:
        /// the world's HDRP exposure factor, used when <see cref="ParelFogSettings.Ambient"/> holds HDRP physical values.
        /// </summary>
        public static void Apply(ParelFogSettings f, Light sun, float exposureScale = 1f)
        {
            if (f == null || !f.Enabled)
            {
                Clear();
                return;
            }
            if (sun != null && !sun.isActiveAndEnabled) sun = null;
            // x and the mode say every surface fogs itself; ParelVolumetricFog switches cameras it draws for to mode 2.
            Shader.SetGlobalVector(ParamsId, new Vector4(1f, Mathf.Max(0f, f.Extinction), f.BaseHeight, Mathf.Max(1e-5f, f.HeightFalloff)));
            Shader.SetGlobalVector(ModeId, new Vector4(1f, 0f, 0f, 0f));
            Color albedo = f.Albedo.linear;
            Shader.SetGlobalVector(ColorId, new Vector4(albedo.r, albedo.g, albedo.b, Mathf.Max(1f, f.MaxDistance)));
            Shader.SetGlobalVector(AmbientId, Ambient(f, exposureScale));

            if (sun != null && sun.isActiveAndEnabled && f.SunScattering > 0f)
            {
                Vector3 toSun = -sun.transform.forward;
                // HDRP divides the phase function by 4π and Built-in light values already carry 1/π: a quarter.
                // The fog is lit by the light that got through the clouds (sun.color already carries the air: ParelSkySun).
                Color c = sun.color.linear * sun.intensity * f.SunScattering * 0.25f * ParelSkyClouds.SunVisibility(toSun);
                Shader.SetGlobalVector(SunDirId, new Vector4(toSun.x, toSun.y, toSun.z, Mathf.Clamp(f.Anisotropy, -0.99f, 0.99f)));
                Shader.SetGlobalVector(SunColorId, new Vector4(c.r, c.g, c.b, 0f));
            }
            else Shader.SetGlobalVector(SunColorId, Vector4.zero);

            int n = 0;
            if (f.Volumes != null)
                foreach (LocalFogVolume v in f.Volumes)
                {
                    if (n >= ParelFogSettings.MaxVolumes) break;
                    if (v == null || v.Volume == null || !v.Volume.gameObject.activeInHierarchy) continue;
                    Matrices[n] = Matrix4x4.TRS(v.Volume.position, v.Volume.rotation, Vector3.Scale(v.Volume.lossyScale, v.Size)).inverse;
                    Color va = v.Albedo.linear;
                    VolumeParams[n] = new Vector4(va.r, va.g, va.b, Mathf.Max(0f, v.Extinction));
                    n++;
                }
            for (int i = n; i < ParelFogSettings.MaxVolumes; i++)
            {
                Matrices[i] = Matrix4x4.identity;
                VolumeParams[i] = Vector4.zero;
            }
            Shader.SetGlobalMatrixArray(MatricesId, Matrices);
            Shader.SetGlobalVectorArray(VolumeParamsId, VolumeParams);
            Shader.SetGlobalVector(CountId, new Vector4(n, 0f, 0f, 0f));
        }

        // Below this the ambient light is treated as missing (HDRP never writes Built-in's ambient probe, so a converted
        // world can arrive with a black one): fog lit only by the sun reads as dark smoke instead of haze.
        private const float MissingAmbient = 2e-3f;

        private static Vector4 Ambient(ParelFogSettings f, float exposureScale)
        {
            Color fixedAmbient = FixedAmbient(f.Ambient, exposureScale);
            if (!f.AmbientFromSky) return fixedAmbient;
            // The average of the ambient light from six directions: what fog all around receives.
            RenderSettings.ambientProbe.Evaluate(AmbientDirections, AmbientResults);
            Color sum = Color.black;
            foreach (Color c in AmbientResults) sum += c;
            sum /= AmbientResults.Length;
            // A physically based sky's ambient is real even when it is dark (dusk, night): only a missing one is replaced.
            if (!ParelSkySun.Active && sum.maxColorComponent < MissingAmbient && fixedAmbient.maxColorComponent > sum.maxColorComponent) sum = fixedAmbient;
            return new Vector4(sum.r, sum.g, sum.b, 0f);
        }

        // The fog's own ambient colour in linear values. Worlds uploaded before SDK 1.15 stored HDRP's physical fog colour
        // as is (hundreds): scale it with the world's exposure like every other HDRP light value.
        private static Color FixedAmbient(Color a, float exposureScale)
        {
            if (a.maxColorComponent > 4f) return new Color(a.r, a.g, a.b) * Mathf.Max(exposureScale, 0f);
            return a.linear;
        }
    }
}
