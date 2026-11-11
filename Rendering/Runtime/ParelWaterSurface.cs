using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// A ParelVR water surface (converted from HDRP's Water Surface at upload, or added by a creator with a ParelVR/Water
    /// material). Keeps an ocean's grid around the player, turns on the opaque picture the water refracts, and when the
    /// camera dips below the surface, fills the view with the water's own absorption and scattering (underwater).
    /// </summary>
    [AddComponentMenu("ParelVR/Rendering/Water Surface")]
    [DisallowMultipleComponent]
    public sealed class ParelWaterSurface : MonoBehaviour
    {
        [Tooltip("An ocean / sea / lake: the surface follows the player and reaches the horizon.")]
        public bool Ocean;
        [Tooltip("Grid step the ocean moves in (m), so its waves don't swim.")]
        public float SnapStep = 4f;
        [Tooltip("Show the underwater view when the camera is below the surface.")]
        public bool Underwater = true;
        [Tooltip("Where the underwater view applies (not an ocean): this box in the surface's space. Empty = the surface's bounds, 50 m deep.")]
        public Bounds UnderwaterVolume;
        [Tooltip("How far you see under water (m): HDRP absorption distance × its underwater multiplier.")]
        public float UnderwaterAbsorptionDistance = 10f;

        private static readonly List<ParelWaterSurface> Active = new List<ParelWaterSurface>();
        private static readonly int UnderwaterId = Shader.PropertyToID("_ParelUnderwater");
        private static readonly int UnderwaterColorId = Shader.PropertyToID("_ParelUnderwaterColor");
        private static readonly int UnderwaterAbsorbId = Shader.PropertyToID("_ParelUnderwaterAbsorb");
        private static bool _ownsFog;
        private Renderer _renderer;

        private void OnEnable()
        {
            _renderer = GetComponent<Renderer>();
            Active.Add(this);
            ParelOpaqueCapture.Request();
        }

        private void OnDisable()
        {
            Active.Remove(this);
            ParelOpaqueCapture.Release();
            if (Active.Count == 0)
            {
                Shader.SetGlobalVector(UnderwaterId, Vector4.zero);
                if (_ownsFog) { ParelVolumetricFog.Disable(); ParelFogGlobals.Clear(); _ownsFog = false; }
            }
        }

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            if (Ocean)
            {
                float step = Mathf.Max(0.5f, SnapStep);
                Vector3 p = transform.position;
                p.x = Mathf.Round(eye.x / step) * step;
                p.z = Mathf.Round(eye.z / step) * step;
                transform.position = p;
            }
            if (Active.Count > 0 && Active[0] == this) UpdateUnderwater(eye);
        }

        private static void UpdateUnderwater(Vector3 eye)
        {
            ParelWaterSurface inside = null;
            foreach (ParelWaterSurface w in Active)
                if (w != null && w.Underwater && w.Contains(eye)) { inside = w; break; }
            if (inside == null)
            {
                Shader.SetGlobalVector(UnderwaterId, Vector4.zero);
                return;
            }
            // The underwater view is drawn by the screen-space fog pass; a world without fog gets an empty one.
            if (!ParelVolumetricFog.Active)
            {
                var none = new ParelFogSettings { Enabled = true, Extinction = 0f, VolumetricRange = 1f, MaxDistance = 1000f, SunScattering = 0f };
                ParelFogGlobals.Apply(none, null);
                ParelVolumetricFog.Enable(none, null);
                _ownsFog = true;
            }
            Material m = inside._renderer != null ? inside._renderer.sharedMaterial : null;
            Color scatter = m != null && m.HasProperty("_ScatteringColor") ? m.GetColor("_ScatteringColor").linear : new Color(0f, 0.2f, 0.25f);
            Color refraction = m != null && m.HasProperty("_RefractionColor") ? m.GetColor("_RefractionColor").linear : new Color(0.1f, 0.5f, 0.5f);
            float dist = Mathf.Max(0.1f, inside.UnderwaterAbsorptionDistance);
            var absorb = new Vector4(-Mathf.Log(Mathf.Max(refraction.r, 0.01f)) / dist, -Mathf.Log(Mathf.Max(refraction.g, 0.01f)) / dist, -Mathf.Log(Mathf.Max(refraction.b, 0.01f)) / dist, 0f);
            Color ambient = RenderSettings.ambientProbe[0, 0] > 0f ? new Color(RenderSettings.ambientProbe[0, 0], RenderSettings.ambientProbe[1, 0], RenderSettings.ambientProbe[2, 0]) * 0.886f : RenderSettings.ambientLight;
            Color lit = scatter * (ambient + (RenderSettings.sun != null ? RenderSettings.sun.color.linear * RenderSettings.sun.intensity * 0.25f : Color.black));
            Shader.SetGlobalVector(UnderwaterId, new Vector4(1f, inside.SurfaceHeight, 0f, 0f));
            Shader.SetGlobalVector(UnderwaterColorId, new Vector4(lit.r, lit.g, lit.b, 0f));
            Shader.SetGlobalVector(UnderwaterAbsorbId, absorb);
        }

        public float SurfaceHeight => transform.position.y;

        private bool Contains(Vector3 eye)
        {
            if (eye.y >= SurfaceHeight) return false;
            if (Ocean) return true;
            Bounds b = UnderwaterVolume.size.sqrMagnitude > 0f ? UnderwaterVolume
                : _renderer != null ? new Bounds(transform.InverseTransformPoint(_renderer.bounds.center) - new Vector3(0f, 25f, 0f), new Vector3(_renderer.bounds.size.x / Mathf.Max(transform.lossyScale.x, 1e-4f), 50f, _renderer.bounds.size.z / Mathf.Max(transform.lossyScale.z, 1e-4f)))
                : new Bounds(Vector3.zero, Vector3.zero);
            return b.Contains(transform.InverseTransformPoint(eye));
        }
    }

    /// <summary>The textures ParelVR water uses: ripple normals and foam / caustics, made once (tiling).</summary>
    public static class ParelWaterTextures
    {
        /// <summary>A tiling ripple normal map (from layered gradient noise).</summary>
        public static Texture2D RippleNormal(int size = 256)
        {
            var heights = new float[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float h = 0f, amp = 1f;
                for (int o = 0; o < 4; o++)
                {
                    int period = 4 << o;
                    h += amp * Noise(x * period / (float)size, y * period / (float)size, period, o);
                    amp *= 0.5f;
                }
                heights[y * size + x] = h;
            }
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "ParelVR water ripples", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = heights[y * size + (x + 1) % size] - heights[y * size + (x + size - 1) % size];
                float dy = heights[((y + 1) % size) * size + x] - heights[((y + size - 1) % size) * size + x];
                Vector3 n = new Vector3(-dx * 2.5f, -dy * 2.5f, 1f).normalized;
                px[y * size + x] = new Color32((byte)((n.x * 0.5f + 0.5f) * 255f), (byte)((n.y * 0.5f + 0.5f) * 255f), (byte)((n.z * 0.5f + 0.5f) * 255f), 255);
            }
            t.SetPixels32(px);
            t.Apply(true);
            return t;
        }

        /// <summary>Foam noise (r) and a caustics web (g, bright cell edges), tiling.</summary>
        public static Texture2D FoamAndCaustics(int size = 256)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "ParelVR water foam and caustics", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
            var px = new Color32[size * size];
            const int cells = 8;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                // Caustics: the gap between the nearest and second-nearest cell point, thin bright lines.
                float f1 = 9f, f2 = 9f;
                for (int cy = -1; cy <= cells; cy++)
                for (int cx = -1; cx <= cells; cx++)
                {
                    int wx = (cx + cells) % cells, wy = (cy + cells) % cells;
                    float px0 = (cx + Rand(wx, wy, 11)) / cells, py0 = (cy + Rand(wx, wy, 23)) / cells;
                    float d = (px0 - u) * (px0 - u) + (py0 - v) * (py0 - v);
                    if (d < f1) { f2 = f1; f1 = d; } else if (d < f2) f2 = d;
                }
                float edge = Mathf.Sqrt(f2) - Mathf.Sqrt(f1);
                float caustic = Mathf.Pow(Mathf.Clamp01(1f - edge * cells * 3f), 6f);
                float foam = 0f, amp = 1f;
                for (int o = 0; o < 4; o++)
                {
                    int period = 4 << o;
                    foam += amp * (0.5f + 0.5f * Noise(u * period, v * period, period, o + 5));
                    amp *= 0.5f;
                }
                foam = Mathf.Clamp01((foam / 1.875f - 0.35f) * 2.2f);
                px[y * size + x] = new Color32((byte)(foam * 255f), (byte)(caustic * 255f), 0, 255);
            }
            t.SetPixels32(px);
            t.Apply(true);
            return t;
        }

        private static float Rand(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2246822519);
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
            }
        }

        // Tiling value noise, -1..1.
        private static float Noise(float x, float y, int period, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float R(int a, int b) => Rand(((a % period) + period) % period, ((b % period) + period) % period, seed) * 2f - 1f;
            return Mathf.Lerp(Mathf.Lerp(R(x0, y0), R(x0 + 1, y0), fx), Mathf.Lerp(R(x0, y0 + 1), R(x0 + 1, y0 + 1), fx), fy);
        }

        /// <summary>A flat grid for a pool or river (size in m, cells per side).</summary>
        public static Mesh Grid(Vector2 size, int cells)
        {
            cells = Mathf.Clamp(cells, 1, 250);
            var verts = new Vector3[(cells + 1) * (cells + 1)];
            var tris = new int[cells * cells * 6];
            for (int z = 0, i = 0; z <= cells; z++)
            for (int x = 0; x <= cells; x++, i++)
                verts[i] = new Vector3((x / (float)cells - 0.5f) * size.x, 0f, (z / (float)cells - 0.5f) * size.y);
            for (int z = 0, t = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int a = z * (cells + 1) + x;
                tris[t++] = a; tris[t++] = a + cells + 1; tris[t++] = a + 1;
                tris[t++] = a + 1; tris[t++] = a + cells + 1; tris[t++] = a + cells + 2;
            }
            var m = new Mesh { name = "ParelVR water grid", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = verts, triangles = tris };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>An ocean grid around the player: fine near the middle, coarser outwards, reaching <paramref name="radius"/> m.</summary>
        public static Mesh OceanGrid(float radius = 4000f, int rings = 96, int segments = 128)
        {
            var verts = new List<Vector3> { Vector3.zero };
            var tris = new List<int>();
            for (int r = 1; r <= rings; r++)
            {
                // Rings grow geometrically: 0.5 m apart at the centre.
                float dist = 0.5f * (Mathf.Pow(radius / 0.5f, r / (float)rings));
                for (int s = 0; s < segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(a) * dist, 0f, Mathf.Sin(a) * dist));
                }
            }
            for (int s = 0; s < segments; s++) { tris.Add(0); tris.Add(1 + (s + 1) % segments); tris.Add(1 + s); }
            for (int r = 1; r < rings; r++)
            {
                int inner = 1 + (r - 1) * segments, outer = 1 + r * segments;
                for (int s = 0; s < segments; s++)
                {
                    int s1 = (s + 1) % segments;
                    tris.Add(inner + s); tris.Add(inner + s1); tris.Add(outer + s);
                    tris.Add(inner + s1); tris.Add(outer + s1); tris.Add(outer + s);
                }
            }
            var m = new Mesh { name = "ParelVR ocean grid", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.bounds = new Bounds(Vector3.zero, new Vector3(radius * 2f, 50f, radius * 2f));
            return m;
        }
    }
}
