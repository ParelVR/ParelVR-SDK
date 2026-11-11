using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.Rendering
{
    /// <summary>
    /// The sun of a world drawn with the ParelVR physically based sky (HDRP's Physically Based Sky), lit the way HDRP
    /// lights it: HDRP dims and reddens a directional light by the air between the ground and the sun, so a low sun is
    /// an orange glow and a sun below the horizon lights nothing. Built-in lights don't, so the light's colour is
    /// multiplied by that transmittance here. The sky itself still needs the unfiltered sun (it works the air out
    /// itself), so it is handed to ParelVR/Sky as shader globals; with those the sky also draws correctly when Unity
    /// renders it for the ambient light and reflections (where _LightColor0 is not set).
    /// </summary>
    public static class ParelSkySun
    {
        private static readonly int SunDirId = Shader.PropertyToID("_ParelSkySunDir");
        private static readonly int SunColorId = Shader.PropertyToID("_ParelSkySunColor");
        private static readonly int ModeId = Shader.PropertyToID("_SkyMode");

        private static Material _sky;
        private static System.Func<Light> _findSun;
        private static Light _light;
        private static Color _original;      // the creator's light colour, before the air
        private static Color _applied;       // the colour this class last wrote
        private static float _lastIntensity, _lastTemperature;
        private static Quaternion _lastRotation;
        private static Runner _runner;

        /// <summary>How much of the sun's light reaches the ground now (rgb, 0..1); white when not active.</summary>
        public static Color Transmittance { get; private set; } = Color.white;

        public static bool Active => _sky != null;

        /// <summary>True for a ParelVR/Sky material in physically based mode.</summary>
        public static bool IsPhysicalSky(Material sky) =>
            sky != null && sky.shader != null && sky.shader.name == ParelShaderNames.Sky && sky.HasProperty(ModeId) && sky.GetFloat(ModeId) < 0.5f;

        /// <summary>Starts lighting <paramref name="sun"/> through the air of <paramref name="sky"/>. Returns false when the sky is not physically based.</summary>
        public static bool Enable(Material sky, System.Func<Light> sun)
        {
            Disable();
            if (!IsPhysicalSky(sky)) return false;
            _sky = sky;
            _findSun = sun;
            if (_runner == null)
            {
                var go = new GameObject("[ParelVR Sky Sun]") { hideFlags = HideFlags.HideAndDontSave };
                if (Application.isPlaying) Object.DontDestroyOnLoad(go);
                _runner = go.AddComponent<Runner>();
            }
            UpdateNow(true);
            return true;
        }

        public static void Disable()
        {
            RestoreLight();
            _sky = null;
            _findSun = null;
            Transmittance = Color.white;
            Shader.SetGlobalVector(SunDirId, Vector4.zero);
            Shader.SetGlobalVector(SunColorId, Vector4.zero);
            if (_runner != null)
            {
                if (Application.isPlaying) Object.Destroy(_runner.gameObject);
                else Object.DestroyImmediate(_runner.gameObject);
            }
            _runner = null;
        }

        private static void RestoreLight()
        {
            if (_light != null && _light.color == _applied) _light.color = _original;
            _light = null;
        }

        /// <summary>Works the sun's colour out again (it moved, or a script changed it). <paramref name="force"/>: even if nothing changed.</summary>
        public static void UpdateNow(bool force = false)
        {
            if (_sky == null) return;
            Light sun = _findSun?.Invoke();
            if (sun != _light)
            {
                RestoreLight();
                _light = sun;
                if (sun != null) { _original = sun.color; _applied = sun.color; }
                force = true;
            }
            if (sun == null)
            {
                Shader.SetGlobalVector(SunDirId, Vector4.zero);
                Shader.SetGlobalVector(SunColorId, Vector4.zero);
                Transmittance = Color.white;
                return;
            }
            // A script that sets the colour itself (a day cycle) sets a new original.
            if (sun.color != _applied) { _original = sun.color; force = true; }
            if (!force && sun.transform.rotation == _lastRotation && Mathf.Approximately(sun.intensity, _lastIntensity) &&
                Mathf.Approximately(sun.colorTemperature, _lastTemperature)) return;
            _lastRotation = sun.transform.rotation;
            _lastIntensity = sun.intensity;
            _lastTemperature = sun.colorTemperature;

            Vector3 toSun = -sun.transform.forward;
            Vector3 t = SunTransmittance(_sky, toSun.y);
            Transmittance = new Color(t.x, t.y, t.z, 1f);

            Color full = _original.linear * sun.intensity;
            if (GraphicsSettings.lightsUseColorTemperature && sun.useColorTemperature)
                full *= Mathf.CorrelatedColorTemperatureToRGB(sun.colorTemperature);
            Shader.SetGlobalVector(SunDirId, new Vector4(toSun.x, toSun.y, toSun.z, 1f));
            Shader.SetGlobalVector(SunColorId, new Vector4(full.r, full.g, full.b, 0f));

            Color lit = new Color(_original.r * Mathf.LinearToGammaSpace(t.x), _original.g * Mathf.LinearToGammaSpace(t.y),
                                  _original.b * Mathf.LinearToGammaSpace(t.z), _original.a);
            sun.color = lit;
            _applied = sun.color;
        }

        // ---- the air (the same model as ParelVR/Sky's Transmittance) ----------------------------------------------

        private static readonly Vector3 Ozone = new Vector3(0.00065f, 0.001881f, 0.000085f);

        /// <summary>
        /// Light that gets from the top of the atmosphere to the ground along a direction whose cos(zenith) is
        /// <paramref name="mu"/>, with the planet's shadow fading in as the sun sets (0 well below the horizon).
        /// </summary>
        public static Vector3 SunTransmittance(Material sky, float mu)
        {
            float R = Prop(sky, "_PlanetRadius", 6378f);
            float airH = Mathf.Max(0.1f, Prop(sky, "_AirScaleHeight", 8f));
            float aerH = Mathf.Max(0.05f, Prop(sky, "_AerosolScaleHeight", 1.2f));
            Vector4 air = sky.HasProperty("_AirDensity") ? sky.GetVector("_AirDensity") : new Vector4(0.0058f, 0.0135f, 0.0331f, 0f);
            Color tint = sky.HasProperty("_AirTint") ? sky.GetColor("_AirTint").linear : Color.white;
            var airBeta = new Vector3(air.x * tint.r, air.y * tint.g, air.z * tint.b);
            float aerBeta = Prop(sky, "_AerosolDensity", 0.021f);

            const float h0 = 0.002f;
            float r = R + h0;
            float horizonCos = -Mathf.Sqrt(Mathf.Max(1f - R * R / (r * r), 0f));
            float shadow = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.015f, 0.015f, mu - horizonCos));
            if (shadow <= 0f) return Vector3.zero;
            mu = Mathf.Max(mu, horizonCos + 0.01f);

            float top = R + Mathf.Max(Mathf.Max(airH, aerH) * 8f, 60f);
            float b = r * mu;
            float len = -b + Mathf.Sqrt(Mathf.Max(b * b - r * r + top * top, 0f));
            float sinZ = Mathf.Sqrt(Mathf.Max(1f - mu * mu, 0f));
            Vector3 tau = Vector3.zero;
            float prev = 0f;
            const int Steps = 24;
            for (int i = 1; i <= Steps; i++)
            {
                float f = i / (float)Steps;
                float tt = len * f * f;
                float tm = 0.5f * (tt + prev);
                float hs = new Vector2(sinZ * tm, r + mu * tm).magnitude - R;
                float ozone = Mathf.Max(0f, 1f - Mathf.Abs(hs - 25f) / 15f);
                tau += (airBeta * Mathf.Exp(-hs / airH) + Vector3.one * (aerBeta * 1.11f * Mathf.Exp(-hs / aerH)) + Ozone * ozone) * (tt - prev);
                prev = tt;
            }
            return new Vector3(Mathf.Exp(-tau.x), Mathf.Exp(-tau.y), Mathf.Exp(-tau.z)) * shadow;
        }

        private static float Prop(Material m, string name, float fallback) => m.HasProperty(name) ? m.GetFloat(name) : fallback;

        private sealed class Runner : MonoBehaviour
        {
            private void LateUpdate() => UpdateNow();
            private void OnDestroy() { if (_runner == this) RestoreLight(); }
        }
    }
}
