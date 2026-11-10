using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Tests
{
    /// <summary>
    /// Loads a converted content bundle (path in Logs/PVRP/render-bundle.txt, prefab name on the second line), applies its
    /// ParelWorldRenderingInfo the way the game does when a world loads, and renders it with and without its fog:
    /// Logs/PVRP/BundleRender.png (left: as converted, right: fog cleared) and BundleRender.txt.
    /// Menu: ParelVR → Rendering → Tests → Render Converted Bundle.
    /// </summary>
    public static class PvrpBundleRenderCheck
    {
        [MenuItem("ParelVR/Rendering/Tests/Render Converted Bundle")]
        public static void Run()
        {
            string[] spec = File.ReadAllLines("Logs/PVRP/render-bundle.txt");
            AssetBundle bundle = AssetBundle.LoadFromFile(spec[0].Trim());
            var log = new System.Text.StringBuilder();
            Scene scene = EditorSceneManager.NewPreviewScene();
            bool async = ShaderUtil.allowAsyncCompilation;
            AmbientMode ambientMode = RenderSettings.ambientMode;
            Color ambient = RenderSettings.ambientLight;
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                GameObject prefab = bundle.LoadAllAssets<GameObject>().First(g => g.name == spec[1].Trim());
                GameObject world = Object.Instantiate(prefab);
                SceneManager.MoveGameObjectToScene(world, scene);
                var sun = new GameObject("Sun").AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 0.8f; // gentler than the HDRP-matched 1.6: no tonemapping in this check
                sun.shadows = LightShadows.Soft;
                sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                SceneManager.MoveGameObjectToScene(sun.gameObject, scene);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.2f, 0.22f, 0.26f);

                int applied = ParelWorldRenderingInfo.ApplyAll(scene, TargetPlatform.Pc);
                ParelWorldRenderingInfo info = world.GetComponentInChildren<ParelWorldRenderingInfo>(true);
                log.AppendLine("applied " + applied + ", fog " + (info != null && info.Fog.Enabled) + ", volumes " + (info != null ? info.Fog.Volumes.Length : 0) + ", post " + (info != null && info.Post.Any));
                foreach (Renderer r in world.GetComponentsInChildren<Renderer>(true))
                    log.AppendLine("  " + r.name + " → " + string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m => m.shader.name + (m.shader.isSupported ? "" : " (UNSUPPORTED)"))));

                // Left: as converted (fog on). Middle: fog cleared. Right: top-down (layers, decal).
                var strip = new Texture2D(1280 * 3, 720, TextureFormat.RGBA32, false);
                RenderInto(scene, strip, 0, false);
                ParelFogGlobals.Clear();
                RenderInto(scene, strip, 1280, false);
                RenderInto(scene, strip, 2560, true);
                int magenta = strip.GetPixels32().Count(c => c.r > 230 && c.g < 30 && c.b > 230);
                log.AppendLine("magenta pixels: " + magenta);
                File.WriteAllBytes("Logs/PVRP/BundleRender.png", strip.EncodeToPNG());
                Object.DestroyImmediate(strip);
            }
            finally
            {
                ParelFogGlobals.Clear();
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambient;
                ShaderUtil.allowAsyncCompilation = async;
                EditorSceneManager.ClosePreviewScene(scene);
                bundle.Unload(true);
            }
            File.WriteAllText("Logs/PVRP/BundleRender.txt", log.ToString());
            Debug.Log(RenderingLog.Prefix + log);
        }

        private static void RenderInto(Scene scene, Texture2D strip, int x, bool topDown)
        {
            var go = new GameObject("Cam");
            SceneManager.MoveGameObjectToScene(go, scene);
            Camera cam = go.AddComponent<Camera>();
            cam.scene = scene;
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.45f, 0.55f, 0.7f);
            cam.farClipPlane = 400f;
            if (topDown)
            {
                cam.orthographic = true;
                cam.orthographicSize = 5.5f;
                cam.transform.position = new Vector3(0f, 20f, -1f);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else
            {
                cam.transform.position = new Vector3(0f, 3.2f, -10f);
                cam.transform.LookAt(new Vector3(0f, 0.6f, 0f));
            }
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            strip.ReadPixels(new Rect(0, 0, 1280, 720), x, 0, false);
            strip.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(go);
        }
    }
}
