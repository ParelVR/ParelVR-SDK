using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace ParelVR.Rendering.Tests
{
    /// <summary>
    /// Renders a shadow- and light-heavy test scene at each PC quality profile and times it, so the ladder can be seen
    /// to look right and to cost less at every step. Menu: ParelVR → Rendering → Tests → Adaptive Profiles Check.
    /// Results: Logs/PVRP/AdaptiveProfiles.txt and .png (one column per profile). The Editor's QualitySettings are put back.
    /// </summary>
    public static class PvrpAdaptiveCheck
    {
        private const int Width = 1920, Height = 1080, Warmup = 5, Frames = 40;

        [MenuItem("ParelVR/Rendering/Tests/Adaptive Profiles Check")]
        public static void Run()
        {
            QualityLimits saved = QualityLimits.Capture();
            var top = new QualityLimits
            {
                ShadowDistance = 150f, ShadowCascades = 4, ShadowResolution = ShadowResolution.VeryHigh, PixelLights = 8, LodBias = 2f,
                ParticleRaycastBudget = 4096, SoftParticles = true, RealtimeReflectionProbes = true, Msaa = 8, TextureMipLimit = 0,
            };
            Scene scene = EditorSceneManager.NewPreviewScene();
            var report = new StringBuilder("PVRP adaptive profiles check — " + SystemInfo.graphicsDeviceName + ", " + Width + "x" + Height + ", " + Frames + " frames each\n");
            var strip = new Texture2D(Width / 2 * 4, Height / 2, TextureFormat.RGBA32, false);
            bool asyncCompile = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                Camera cam = Build(scene);
                var applier = new ParelQualityApplier(top, top) { AdaptMsaa = true };
                var profiles = QualityProfiles.For(TargetPlatform.Pc);
                double first = 0;
                for (int i = 0; i < profiles.Count; i++)
                {
                    QualityProfile p = profiles[i];
                    applier.Apply(p);
                    int w = Mathf.RoundToInt(Width * p.RenderScale), h = Mathf.RoundToInt(Height * p.RenderScale);
                    var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { antiAliasing = Mathf.Max(1, QualitySettings.antiAliasing) };
                    cam.targetTexture = rt;
                    for (int f = 0; f < Warmup; f++) Frame(cam, rt);
                    var watch = Stopwatch.StartNew();
                    for (int f = 0; f < Frames; f++) Frame(cam, rt);
                    double ms = watch.Elapsed.TotalMilliseconds / Frames;
                    if (i == 0) first = ms;
                    report.AppendLine($"{p.Name,-15} {ms,7:0.00} ms/frame ({ms / first * 100,3:0}%)  render {w}x{h}  {applier.Applied}");
                    Blit(rt, strip, i);
                    cam.targetTexture = null;
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = asyncCompile;
                new ParelQualityApplier(saved, saved).Restore();
                EditorSceneManager.ClosePreviewScene(scene);
            }
            Directory.CreateDirectory("Logs/PVRP");
            File.WriteAllText("Logs/PVRP/AdaptiveProfiles.txt", report.ToString());
            File.WriteAllBytes("Logs/PVRP/AdaptiveProfiles.png", strip.EncodeToPNG());
            Object.DestroyImmediate(strip);
            Debug.Log(RenderingLog.Prefix + report);
        }

        // Render, then read one pixel back so the GPU work is finished inside the timed section.
        private static readonly Texture2D Probe = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };

        private static void Frame(Camera cam, RenderTexture rt)
        {
            cam.Render();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            Probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
            RenderTexture.active = previous;
        }

        private static void Blit(RenderTexture rt, Texture2D strip, int column)
        {
            var half = RenderTexture.GetTemporary(Width / 2, Height / 2, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(rt, half);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = half;
            strip.ReadPixels(new Rect(0, 0, Width / 2, Height / 2), column * Width / 2, 0, false);
            strip.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(half);
        }

        private static Camera Build(Scene scene)
        {
            Shader lit = ParelShaderLibrary.Active != null ? ParelShaderLibrary.Active.Get(ShaderRole.Lit, TargetPlatform.Pc) : Shader.Find("Standard");
            var floorMat = new Material(lit) { hideFlags = HideFlags.HideAndDontSave, color = new Color(0.55f, 0.55f, 0.6f) };
            var ballMat = new Material(lit) { hideFlags = HideFlags.HideAndDontSave, color = new Color(0.9f, 0.6f, 0.3f) };
            ballMat.SetFloat("_Glossiness", 0.7f);

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(20f, 1f, 20f);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;
            SceneManager.MoveGameObjectToScene(floor, scene);
            for (int x = -12; x <= 12; x++)
            for (int z = -2; z <= 40; z++)
            {
                GameObject ball = GameObject.CreatePrimitive((x + z) % 3 == 0 ? PrimitiveType.Cube : PrimitiveType.Sphere);
                Object.DestroyImmediate(ball.GetComponent<Collider>());
                ball.transform.position = new Vector3(x * 1.6f, 0.6f + Mathf.Abs(Mathf.Sin(x * 0.7f + z)) * 1.5f, z * 2.2f);
                ball.GetComponent<Renderer>().sharedMaterial = ballMat;
                SceneManager.MoveGameObjectToScene(ball, scene);
            }

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = 1.1f;
            sun.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
            SceneManager.MoveGameObjectToScene(sun.gameObject, scene);
            Color[] colours = { Color.red, Color.green, Color.blue, Color.cyan, Color.magenta, Color.yellow };
            for (int i = 0; i < 6; i++)
            {
                var point = new GameObject("Point " + i).AddComponent<Light>();
                point.type = LightType.Point;
                point.color = colours[i];
                point.range = 12f;
                point.intensity = 2f;
                point.shadows = i < 2 ? LightShadows.Hard : LightShadows.None;
                point.renderMode = LightRenderMode.Auto; // so the per-pixel light limit applies
                point.transform.position = new Vector3(-10f + i * 4f, 2.5f, 6f + i * 4f);
                SceneManager.MoveGameObjectToScene(point.gameObject, scene);
            }

            var camGo = new GameObject("Camera");
            Camera cam = camGo.AddComponent<Camera>();
            cam.scene = scene;
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.3f, 0.35f, 0.42f);
            cam.allowMSAA = true;
            cam.farClipPlane = 200f;
            cam.transform.position = new Vector3(0f, 6f, -10f);
            cam.transform.rotation = Quaternion.Euler(22f, 0f, 0f);
            SceneManager.MoveGameObjectToScene(camGo, scene);
            return cam;
        }
    }
}
