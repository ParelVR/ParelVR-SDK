using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// A world's volumetric clouds (HDRP Volumetric Clouds): marched into a panorama of the upper sky that ParelVR/Sky
    /// shows (one texture read per sky pixel, so it costs the same in a headset), moved by the wind, and casting soft
    /// cloud shadows through the sun (a tiling light cookie). PC redraws the panorama every frame, standalone headsets
    /// a smaller one every few frames.
    /// </summary>
    public static class ParelSkyClouds
    {
        private static ParelCloudSettings _settings;
        private static System.Func<Light> _sun;
        private static Material _material;
        private static RenderTexture _panorama, _shadow;
        private static Texture3D _shape, _detail;
        private static Runner _runner;
        private static Vector2 _windOffsetKm;
        private static Light _cookieLight;
        private static Texture _previousCookie;
        private static float _previousCookieSize;
        private static int _frame;
        private static readonly Vector3[] Up = { Vector3.up };
        private static readonly Color[] UpResult = new Color[1];

        private static readonly int PanoramaId = Shader.PropertyToID("_ParelCloudPanorama");
        private static readonly int OnId = Shader.PropertyToID("_ParelCloudsOn");

        public static bool Active => _settings != null;

        public static void Enable(ParelCloudSettings settings, System.Func<Light> sun)
        {
            if (settings == null || !settings.Enabled) { Disable(); return; }
            _settings = settings;
            _sun = sun;
            if (_material == null)
            {
                Shader s = Resources.Load<Shader>("ParelVR/Rendering/ParelCloudPanorama") ?? Shader.Find("Hidden/ParelVR/CloudPanorama");
                if (s == null || !s.isSupported || SystemInfo.graphicsShaderLevel < 35)
                {
                    RenderingLog.Warning("Volumetric clouds can't be drawn on this device.");
                    return;
                }
                _material = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_shape == null) _shape = CloudNoise.Shape(Application.isMobilePlatform ? 48 : 64);
            if (_detail == null) _detail = CloudNoise.Detail(32);
            bool mobile = Application.isMobilePlatform;
            if (_panorama == null)
            {
                _panorama = new RenderTexture(mobile ? 512 : 1024, mobile ? 128 : 256, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                    { name = "ParelVR cloud panorama", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                _panorama.wrapModeU = TextureWrapMode.Repeat;
                _panorama.Create();
            }
            if (_runner == null)
            {
                var go = new GameObject("[ParelVR Sky Clouds]") { hideFlags = HideFlags.HideAndDontSave };
                if (Application.isPlaying) Object.DontDestroyOnLoad(go);
                _runner = go.AddComponent<Runner>();
            }
            Shader.SetGlobalTexture(PanoramaId, _panorama);
            Shader.SetGlobalVector(OnId, new Vector4(1f, 0f, 0f, 0f));
            _frame = 0;
            UpdateNow(0f);
        }

        public static void Disable()
        {
            _settings = null;
            Shader.SetGlobalVector(OnId, Vector4.zero);
            RestoreCookie();
            if (_runner != null) Kill(_runner.gameObject);
            _runner = null;
            if (_panorama != null) { _panorama.Release(); Kill(_panorama); _panorama = null; }
            if (_shadow != null) { _shadow.Release(); Kill(_shadow); _shadow = null; }
        }

        private static void Kill(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        /// <summary>Moves the clouds by <paramref name="deltaTime"/> seconds of wind and redraws them (checks call it directly).</summary>
        public static void UpdateNow(float deltaTime)
        {
            if (_settings == null || _material == null) return;
            ParelCloudSettings c = _settings;
            float rad = c.WindOrientation * Mathf.Deg2Rad;
            _windOffsetKm -= new Vector2(Mathf.Sin(rad), Mathf.Cos(rad)) * (c.WindSpeed / 3600f * deltaTime);
            float tile = Mathf.Max(0.5f, c.ShapeScaleKm);
            _windOffsetKm.x = Mathf.Repeat(_windOffsetKm.x, tile);
            _windOffsetKm.y = Mathf.Repeat(_windOffsetKm.y, tile);

            Light sun = _sun?.Invoke();
            Vector3 toSun = sun != null ? -sun.transform.forward : Vector3.up;
            Color sunColor = sun != null && sun.isActiveAndEnabled ? sun.color.linear * sun.intensity : Color.black;
            RenderSettings.ambientProbe.Evaluate(Up, UpResult);
            Color ambient = UpResult[0];

            _material.SetTexture("_ParelCloudShape", _shape);
            _material.SetTexture("_ParelCloudDetail", _detail);
            _material.SetVector("_ParelCloudSun", new Vector4(toSun.x, toSun.y, toSun.z, 0f));
            _material.SetVector("_ParelCloudSunColor", new Vector4(sunColor.r, sunColor.g, sunColor.b, c.SunLightDimmer));
            _material.SetVector("_ParelCloudAmbient", new Vector4(ambient.r, ambient.g, ambient.b, c.AmbientDimmer));
            _material.SetVector("_ParelCloudLayer", new Vector4(Mathf.Max(0.1f, c.BottomAltitudeKm), Mathf.Max(0.1f, c.ThicknessKm), 6378f, tile));
            _material.SetVector("_ParelCloudShapeParams", new Vector4(Mathf.Clamp01(c.Coverage), Mathf.Max(0f, c.Density) * 2.5f, Mathf.Clamp01(c.ShapeFactor), Mathf.Clamp01(c.Erosion)));
            _material.SetVector("_ParelCloudWind", new Vector4(_windOffsetKm.x, _windOffsetKm.y, 4f, _frame % 64));
            Color tint = c.ScatteringTint.linear;
            _material.SetVector("_ParelCloudTint", new Vector4(tint.r, tint.g, tint.b, 1f));
            _material.SetVector("_ParelCloudShadow", new Vector4(Mathf.Clamp01(c.ShadowOpacity), 0f, 0f, 0f));

            Draw(_panorama, 0);
            UpdateShadows(sun, tile);
            _frame++;
        }

        private static void Draw(RenderTexture target, int pass)
        {
            RenderTexture previous = RenderTexture.active;
            Graphics.SetRenderTarget(target);
            _material.SetPass(pass);
            Graphics.DrawProceduralNow(MeshTopology.Triangles, 3, 1);
            RenderTexture.active = previous;
        }

        private static void UpdateShadows(Light sun, float tileKm)
        {
            if (!_settings.Shadows || sun == null || sun.type != LightType.Directional)
            {
                RestoreCookie();
                return;
            }
            // A creator's own sun cookie stays.
            if (sun.cookie != null && sun.cookie != _shadow) return;
            if (_shadow == null)
            {
                _shadow = new RenderTexture(512, 512, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                    { name = "ParelVR cloud shadows", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                _shadow.Create();
            }
            Draw(_shadow, 1);
            if (_cookieLight != sun)
            {
                RestoreCookie();
                _cookieLight = sun;
                _previousCookie = sun.cookie;
                _previousCookieSize = sun.cookieSize;
            }
            sun.cookie = _shadow;
            sun.cookieSize = tileKm * 1000f;
        }

        private static void RestoreCookie()
        {
            if (_cookieLight != null && _cookieLight.cookie == _shadow)
            {
                _cookieLight.cookie = _previousCookie;
                _cookieLight.cookieSize = _previousCookieSize;
            }
            _cookieLight = null;
        }

        private sealed class Runner : MonoBehaviour
        {
            private void LateUpdate()
            {
                if (_settings == null) return;
                // Standalone headsets redraw every 4th frame (clouds move slowly), PC every frame.
                if (Application.isMobilePlatform && (Time.frameCount & 3) != 0) return;
                UpdateNow(Application.isMobilePlatform ? Time.deltaTime * 4f : Time.deltaTime);
            }

            private void OnDestroy()
            {
                if (_runner == this) RestoreCookie();
            }
        }
    }

    /// <summary>Tiling 3D noises for the clouds, made once per session.</summary>
    internal static class CloudNoise
    {
        public static Texture3D Shape(int size)
        {
            var px = new Color32[size * size * size];
            int i = 0;
            for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector3 p = new Vector3(x, y, z) / size;
                float perlin = 0.5f + 0.5f * (0.625f * Perlin(p * 4f, 4) + 0.25f * Perlin(p * 8f, 8) + 0.125f * Perlin(p * 16f, 16)) / 1.0f;
                float worley = 0.625f * Worley(p, 4) + 0.25f * Worley(p, 8) + 0.125f * Worley(p, 16);
                // Perlin-Worley: billowy Perlin clouds with Worley's rounded cells.
                float shape = Mathf.Clamp01(Remap(perlin, -(1f - worley) * 0.6f, 1f, 0f, 1f));
                byte b = (byte)(Mathf.Clamp01(shape) * 255f);
                px[i++] = new Color32(b, b, b, 255);
            }
            return Make(size, px, "ParelVR cloud shape");
        }

        public static Texture3D Detail(int size)
        {
            var px = new Color32[size * size * size];
            int i = 0;
            for (int z = 0; z < size; z++)
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector3 p = new Vector3(x, y, z) / size;
                float w = 0.625f * Worley(p, 4) + 0.25f * Worley(p, 8) + 0.125f * Worley(p, 16);
                byte b = (byte)(Mathf.Clamp01(1f - w) * 255f);
                px[i++] = new Color32(b, b, b, 255);
            }
            return Make(size, px, "ParelVR cloud detail");
        }

        private static Texture3D Make(int size, Color32[] px, string name)
        {
            var t = new Texture3D(size, size, size, TextureFormat.RGBA32, false)
                { name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        private static float Remap(float v, float a, float b, float c, float d) => c + (v - a) / Mathf.Max(b - a, 1e-4f) * (d - c);

        private static uint Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791) ^ (uint)(seed * 2654435761);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return h;
            }
        }

        private static float Rand(int x, int y, int z, int seed) => (Hash(x, y, z, seed) & 0xFFFFFF) / 16777216f;

        // Tiling Worley (cellular) noise: distance to the nearest feature point, period = cells.
        private static float Worley(Vector3 p, int cells)
        {
            Vector3 q = p * cells;
            int cx = Mathf.FloorToInt(q.x), cy = Mathf.FloorToInt(q.y), cz = Mathf.FloorToInt(q.z);
            float best = 10f;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int gx = cx + dx, gy = cy + dy, gz = cz + dz;
                int wx = ((gx % cells) + cells) % cells, wy = ((gy % cells) + cells) % cells, wz = ((gz % cells) + cells) % cells;
                Vector3 f = new Vector3(gx + Rand(wx, wy, wz, cells), gy + Rand(wx, wy, wz, cells + 7), gz + Rand(wx, wy, wz, cells + 13));
                best = Mathf.Min(best, (f - q).sqrMagnitude);
            }
            return Mathf.Clamp01(Mathf.Sqrt(best));
        }

        // Tiling gradient noise, -1..1, period = cells.
        private static float Perlin(Vector3 q, int period)
        {
            int x0 = Mathf.FloorToInt(q.x), y0 = Mathf.FloorToInt(q.y), z0 = Mathf.FloorToInt(q.z);
            Vector3 f = q - new Vector3(x0, y0, z0);
            Vector3 u = new Vector3(Fade(f.x), Fade(f.y), Fade(f.z));
            float Corner(int ox, int oy, int oz)
            {
                int gx = ((x0 + ox) % period + period) % period, gy = ((y0 + oy) % period + period) % period, gz = ((z0 + oz) % period + period) % period;
                Vector3 g = new Vector3(Rand(gx, gy, gz, 1) * 2f - 1f, Rand(gx, gy, gz, 2) * 2f - 1f, Rand(gx, gy, gz, 3) * 2f - 1f).normalized;
                return Vector3.Dot(g, f - new Vector3(ox, oy, oz));
            }
            float x00 = Mathf.Lerp(Corner(0, 0, 0), Corner(1, 0, 0), u.x);
            float x10 = Mathf.Lerp(Corner(0, 1, 0), Corner(1, 1, 0), u.x);
            float x01 = Mathf.Lerp(Corner(0, 0, 1), Corner(1, 0, 1), u.x);
            float x11 = Mathf.Lerp(Corner(0, 1, 1), Corner(1, 1, 1), u.x);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, u.y), Mathf.Lerp(x01, x11, u.y), u.z) * 1.6f;
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
    }
}
