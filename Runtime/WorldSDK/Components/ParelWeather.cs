using System;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Weather for a world: Clear, Rain, Snow, Fog or Storm (rain with lightning and thunder). Rain and snow
    /// fall in a box around the player's own view, so they cost the same in any size of world. Pick one, call
    /// SetWeather from a button or Volt, or let it change on the shared clock (UTC) so everyone in the instance
    /// has the same weather without networking. Weather blends in over Fade Seconds.
    /// </summary>
    [AddComponentMenu("ParelVR/World SDK/Weather")]
    public sealed class ParelWeather : MonoBehaviour
    {
        public enum Kind { Clear = 0, Rain = 1, Snow = 2, Fog = 3, Storm = 4 }

        public Kind weather = Kind.Clear;
        [Tooltip("Change weather on the shared clock, picking from Cycle.")]
        public bool autoCycle;
        public Kind[] cycle = { Kind.Clear, Kind.Rain, Kind.Clear, Kind.Fog, Kind.Snow, Kind.Storm };
        [Min(30f)] public float secondsPerWeather = 900f;
        [Min(0f)] public float fadeSeconds = 6f;

        [Header("Look")]
        [Range(0f, 1f)] public float intensity = 0.7f;
        [Tooltip("Size of the falling area around the player, in metres.")]
        [Min(4f)] public float areaSize = 30f;
        public Color rainColor = new Color(0.75f, 0.8f, 0.9f, 0.45f);
        public Color snowColor = new Color(1f, 1f, 1f, 0.9f);
        public Color fogColor = new Color(0.62f, 0.66f, 0.7f);
        [Min(0f)] public float fogDensity = 0.035f;

        [Header("Storm")]
        [Tooltip("Optional: flashed for lightning. A new one is made when empty.")]
        public Light lightning;
        public AudioClip[] thunder;
        public AudioSource rainLoop;

        private ParticleSystem _rain, _snow;
        private float _blend;           // 0 = previous weather, 1 = current
        private Kind _from = Kind.Clear;
        private bool _fogWas;
        private Color _fogColorWas;
        private float _fogDensityWas;
        private FogMode _fogModeWas;
        private float _nextFlash = 5f;
        private float _flash;
        private AudioSource _thunderSource;

        private void OnEnable()
        {
            _fogWas = RenderSettings.fog;
            _fogColorWas = RenderSettings.fogColor;
            _fogDensityWas = RenderSettings.fogDensity;
            _fogModeWas = RenderSettings.fogMode;
            _rain = MakeFall("Rain", true);
            _snow = MakeFall("Snow", false);
            if (autoCycle && cycle != null && cycle.Length > 0) weather = ClockWeather();
            _from = weather;
            _blend = 1f;
        }

        private void OnDisable()
        {
            RenderSettings.fog = _fogWas;
            RenderSettings.fogColor = _fogColorWas;
            RenderSettings.fogDensity = _fogDensityWas;
            RenderSettings.fogMode = _fogModeWas;
            if (_rain != null) Destroy(_rain.gameObject);
            if (_snow != null) Destroy(_snow.gameObject);
        }

        public void SetWeather(Kind kind)
        {
            if (kind == weather) return;
            _from = weather;
            weather = kind;
            _blend = fadeSeconds > 0f ? 0f : 1f;
        }

        public void SetWeather(int kind) => SetWeather((Kind)Mathf.Clamp(kind, 0, 4));
        public void Clear() => SetWeather(Kind.Clear);
        public void Rain() => SetWeather(Kind.Rain);
        public void Snow() => SetWeather(Kind.Snow);
        public void Fog() => SetWeather(Kind.Fog);
        public void Storm() => SetWeather(Kind.Storm);

        private Kind ClockWeather()
        {
            double seconds = (DateTime.UtcNow - new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return cycle[(int)(Math.Floor(seconds / Math.Max(30f, secondsPerWeather)) % cycle.Length)];
        }

        private void Update()
        {
            if (autoCycle && cycle != null && cycle.Length > 0)
            {
                Kind due = ClockWeather();
                if (due != weather) SetWeather(due);
            }
            _blend = Mathf.MoveTowards(_blend, 1f, Time.deltaTime / Mathf.Max(0.01f, fadeSeconds));

            Camera view = Camera.main;
            if (view != null)
            {
                Vector3 above = view.transform.position + Vector3.up * (areaSize * 0.4f);
                if (_rain != null) _rain.transform.position = above;
                if (_snow != null) _snow.transform.position = above;
            }

            float rain = Mix(k => k == Kind.Rain ? 1f : k == Kind.Storm ? 1.6f : 0f);
            float snow = Mix(k => k == Kind.Snow ? 1f : 0f);
            float fog = Mix(k => k == Kind.Fog ? 1f : k == Kind.Storm ? 0.45f : k == Kind.Rain ? 0.25f : k == Kind.Snow ? 0.3f : 0f);
            Rate(_rain, rain * intensity * 4000f);
            Rate(_snow, snow * intensity * 1200f);
            if (rainLoop != null)
            {
                rainLoop.volume = Mathf.Clamp01(rain * intensity);
                if (rainLoop.volume > 0.01f && !rainLoop.isPlaying) rainLoop.Play();
                else if (rainLoop.volume <= 0.01f && rainLoop.isPlaying) rainLoop.Stop();
            }

            if (fog > 0.001f)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogColor = Color.Lerp(_fogWas ? _fogColorWas : fogColor, fogColor, fog);
                RenderSettings.fogDensity = Mathf.Lerp(_fogWas ? _fogDensityWas : 0f, fogDensity, fog * intensity + 0.0001f);
            }
            else
            {
                RenderSettings.fog = _fogWas;
                RenderSettings.fogColor = _fogColorWas;
                RenderSettings.fogDensity = _fogDensityWas;
                RenderSettings.fogMode = _fogModeWas;
            }

            UpdateLightning(Mix(k => k == Kind.Storm ? 1f : 0f));
        }

        private float Mix(Func<Kind, float> amount) => Mathf.Lerp(amount(_from), amount(weather), _blend);

        private static void Rate(ParticleSystem system, float perSecond)
        {
            if (system == null) return;
            var emission = system.emission;
            emission.rateOverTime = perSecond;
            if (perSecond > 0.5f && !system.isPlaying) system.Play();
        }

        private void UpdateLightning(float storm)
        {
            if (storm < 0.5f)
            {
                if (lightning != null && _flash > 0f) { _flash = 0f; lightning.intensity = 0f; }
                return;
            }
            if (lightning == null)
            {
                var go = new GameObject("Lightning");
                go.transform.SetParent(transform, false);
                go.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
                lightning = go.AddComponent<Light>();
                lightning.type = LightType.Directional;
                lightning.color = new Color(0.8f, 0.85f, 1f);
                lightning.shadows = LightShadows.None;
                lightning.intensity = 0f;
            }
            _nextFlash -= Time.deltaTime;
            if (_nextFlash <= 0f)
            {
                _flash = 1f;
                _nextFlash = UnityEngine.Random.Range(6f, 18f);
                if (thunder != null && thunder.Length > 0)
                {
                    if (_thunderSource == null)
                    {
                        _thunderSource = gameObject.AddComponent<AudioSource>();
                        _thunderSource.spatialBlend = 0f;
                    }
                    AudioClip clip = thunder[UnityEngine.Random.Range(0, thunder.Length)];
                    if (clip != null)
                    {
                        _thunderSource.clip = clip;
                        _thunderSource.PlayDelayed(UnityEngine.Random.Range(0.4f, 2.5f));   // thunder after the flash
                    }
                }
            }
            _flash = Mathf.MoveTowards(_flash, 0f, Time.deltaTime * 5f);
            // Two quick flickers then the fade.
            float flicker = _flash > 0.75f || (_flash > 0.45f && _flash < 0.6f) ? _flash : _flash * 0.3f;
            lightning.intensity = flicker * 2.2f * intensity;
        }

        private ParticleSystem MakeFall(string label, bool rain)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);
            var system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = rain ? 8000 : 3000;
            main.startLifetime = rain ? 1.1f : 7f;
            main.startSpeed = rain ? 18f : 1.2f;
            main.startSize = rain ? 0.035f : 0.06f;
            main.startColor = rain ? rainColor : snowColor;
            main.gravityModifier = rain ? 0.6f : 0.03f;

            var shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(areaSize, 0.5f, areaSize);
            shape.rotation = new Vector3(90f, 0f, 0f);   // emit downwards

            var emission = system.emission;
            emission.rateOverTime = 0f;

            if (!rain)
            {
                var noise = system.noise;
                noise.enabled = true;
                noise.strength = 0.4f;
                noise.frequency = 0.25f;
            }

            var collision = system.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.quality = ParticleSystemCollisionQuality.Low;
            collision.lifetimeLoss = 1f;   // gone where it lands (rain does not fall through roofs)
            collision.bounce = 0f;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = rain ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            renderer.lengthScale = rain ? 6f : 1f;
            renderer.velocityScale = rain ? 0.02f : 0f;
            Shader shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null) renderer.sharedMaterial = new Material(shader) { name = label + " (weather)" };
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return system;
        }
    }
}
