using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Several skyboxes in one world. Each entry is a skybox material with the ambient light, fog and sun that
    /// go with it. Switch by hand (Next / Previous / SetSky, e.g. from a button or Volt) or let the world cycle
    /// on its own. Cycling follows the shared clock (UTC), so every player in the instance sees the same sky
    /// without any networking. Changes fade over Fade Seconds: lighting, fog and the sun blend; the skybox
    /// material itself switches halfway, while the exposure dips so the swap is not seen.
    /// </summary>
    [AddComponentMenu("ParelVR/World SDK/Skybox Cycle")]
    public sealed class ParelSkyboxCycle : MonoBehaviour
    {
        [Serializable]
        public sealed class Sky
        {
            public string name = "Day";
            public Material skybox;
            [ColorUsage(false, true)] public Color ambient = new Color(0.5f, 0.5f, 0.55f);
            public bool fog;
            public Color fogColor = new Color(0.6f, 0.65f, 0.7f);
            [Min(0f)] public float fogDensity = 0.01f;
            [Tooltip("Optional: the scene's sun turned and coloured for this sky.")]
            public Vector3 sunAngles = new Vector3(50f, -30f, 0f);
            public Color sunColor = Color.white;
            [Min(0f)] public float sunIntensity = 1f;
        }

        public Sky[] skies = new Sky[0];
        [Tooltip("Optional: the directional light that is the sun.")]
        public Light sun;
        [Tooltip("Cycle through the skies on the shared clock.")]
        public bool autoCycle;
        [Tooltip("Seconds each sky stays up while cycling.")]
        [Min(10f)] public float secondsPerSky = 600f;
        [Min(0f)] public float fadeSeconds = 4f;
        public int startSky;

        public int Current { get; private set; } = -1;

        private int _from = -1;
        private float _fade = 1f;
        private bool _swapped;
        private float _exposure = 1f;
        private Material _shown;

        private void OnEnable()
        {
            if (skies == null || skies.Length == 0) return;
            SetSky(autoCycle ? ClockSky() : Mathf.Clamp(startSky, 0, skies.Length - 1), true);
        }

        public void Next() => SetSky((Current + 1) % Mathf.Max(1, skies.Length));
        public void Previous() => SetSky((Current - 1 + skies.Length) % Mathf.Max(1, skies.Length));
        public void SetSkyByName(string skyName)
        {
            for (int i = 0; i < skies.Length; i++) if (skies[i] != null && skies[i].name == skyName) { SetSky(i); return; }
        }

        public void SetSky(int index) => SetSky(index, false);

        private void SetSky(int index, bool instant)
        {
            if (skies == null || skies.Length == 0) return;
            index = Mathf.Clamp(index, 0, skies.Length - 1);
            if (index == Current && !instant) return;
            _from = instant || Current < 0 ? index : Current;
            Current = index;
            _fade = instant || fadeSeconds <= 0f ? 1f : 0f;
            _swapped = false;
            if (_fade >= 1f) Apply(1f);
        }

        private int ClockSky()
        {
            double seconds = (DateTime.UtcNow - new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return (int)(Math.Floor(seconds / Math.Max(10f, secondsPerSky)) % skies.Length);
        }

        private void Update()
        {
            if (skies == null || skies.Length == 0 || Current < 0) return;
            if (autoCycle)
            {
                int due = ClockSky();
                if (due != Current) SetSky(due);
            }
            if (_fade < 1f)
            {
                _fade = Mathf.MoveTowards(_fade, 1f, Time.deltaTime / Mathf.Max(0.01f, fadeSeconds));
                Apply(_fade);
            }
        }

        private void Apply(float t)
        {
            Sky a = skies[Mathf.Clamp(_from, 0, skies.Length - 1)];
            Sky b = skies[Current];
            if (a == null || b == null) return;

            // The skybox swaps at the midpoint, under a short dip in exposure.
            Material target = t >= 0.5f || _from == Current ? b.skybox : a.skybox;
            if (target != _shown)
            {
                _shown = target;
                RenderSettings.skybox = target;
                _swapped = true;
            }
            float dip = _from == Current ? 1f : Mathf.Lerp(0.35f, 1f, Mathf.Abs(t - 0.5f) * 2f);
            if (_shown != null && _shown.HasProperty("_Exposure"))
            {
                if (_swapped) { _exposure = _shown.GetFloat("_Exposure"); _swapped = false; }
                _shown.SetFloat("_Exposure", _exposure * dip);
                if (t >= 1f) _shown.SetFloat("_Exposure", _exposure);
            }

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.Lerp(a.ambient, b.ambient, t);
            bool fogOn = a.fog || b.fog;
            RenderSettings.fog = fogOn;
            if (fogOn)
            {
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = Color.Lerp(a.fogColor, b.fogColor, t);
                RenderSettings.fogDensity = Mathf.Lerp(a.fog ? a.fogDensity : 0f, b.fog ? b.fogDensity : 0f, t);
            }
            if (sun != null)
            {
                sun.transform.rotation = Quaternion.Slerp(Quaternion.Euler(a.sunAngles), Quaternion.Euler(b.sunAngles), t);
                sun.color = Color.Lerp(a.sunColor, b.sunColor, t);
                sun.intensity = Mathf.Lerp(a.sunIntensity, b.sunIntensity, t);
            }
            if (t >= 1f) DynamicGI.UpdateEnvironment();
        }
    }
}
