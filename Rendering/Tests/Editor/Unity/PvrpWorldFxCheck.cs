using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Tests
{
    /// <summary>
    /// Renders the HDRP carry-over effects (volumetric fog, sky, clouds, water...) in a preview scene, so nothing in the
    /// open scenes changes. Pictures and notes go to Logs/PVRP/WorldFx/. Menu: ParelVR → Rendering → Tests → World FX Check.
    /// </summary>
    public static class PvrpWorldFxCheck
    {
        public const string Folder = "Logs/PVRP/WorldFx";
        private static StringBuilder _log;

        [MenuItem("ParelVR/Rendering/Tests/World FX Check")]
        public static void Run()
        {
            Directory.CreateDirectory(Folder);
            _log = new StringBuilder();
            foreach (string name in new[] { "ParelVolumetricFog", "ParelSunShadowDepth" })
                Report(Resources.Load<Shader>("ParelVR/Rendering/" + name));
            Report(Shader.Find("ParelVR/Lit"));
            Report(Shader.Find("ParelVR/Transparent"));
            Report(Shader.Find("ParelVR/Sky"));
            Report(Resources.Load<Shader>("ParelVR/Rendering/ParelCloudPanorama"));
            Scenario("fog", FogScene);
            Scenario("sky", SkyScene);
            Report(Shader.Find("ParelVR/Water"));
            Report(Shader.Find("ParelVR/Water Excluder"));
            Scenario("water", WaterScene);
            Scenario("features", FeatureScene);
            File.WriteAllText(Folder + "/WorldFx.txt", _log.ToString());
            Debug.Log("[PVRP] World FX check written to " + Folder);
        }

        internal static void Report(Shader s)
        {
            if (s == null) { _log.AppendLine("MISSING shader"); return; }
            foreach (ShaderMessage m in ShaderUtil.GetShaderMessages(s))
                _log.AppendLine(s.name + " " + m.severity + ": " + m.message + " (line " + m.line + ", " + m.platform + ")");
            _log.AppendLine(s.name + " supported " + s.isSupported);
        }

        internal sealed class Ctx
        {
            public Scene Scene;
            public Camera Camera;
            public RenderTexture Target;

            public GameObject Make(PrimitiveType t, Vector3 p, Vector3 s, Material m)
            {
                GameObject g = GameObject.CreatePrimitive(t);
                SceneManager.MoveGameObjectToScene(g, Scene);
                g.transform.position = p;
                g.transform.localScale = s;
                g.GetComponent<Renderer>().sharedMaterial = m;
                return g;
            }

            public Light Light(LightType type, Vector3 pos, Vector3 euler, Color c, float intensity, float range = 10f)
            {
                var go = new GameObject(type + " light");
                SceneManager.MoveGameObjectToScene(go, Scene);
                go.transform.SetPositionAndRotation(pos, Quaternion.Euler(euler));
                Light l = go.AddComponent<Light>();
                l.type = type;
                l.color = c;
                l.intensity = intensity;
                l.range = range;
                l.shadows = LightShadows.Soft;
                return l;
            }

            public void Shot(string name)
            {
                Camera.Render();
                _log.AppendLine(name + ": path " + Camera.actualRenderingPath + ", fog cameras " + ParelVolumetricFog.CameraCount
                                + ", params " + Shader.GetGlobalVector("_ParelFogParams") + ", mode " + Shader.GetGlobalVector("_ParelFogMode")
                                + ", ambient " + Shader.GetGlobalVector("_ParelFogAmbient") + ", vol " + Shader.GetGlobalVector("_ParelVolParams"));
                RenderTexture ldr = RenderTexture.GetTemporary(Target.width, Target.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(Target, ldr);
                RenderTexture.active = ldr;
                var t = new Texture2D(Target.width, Target.height, TextureFormat.RGB24, false);
                t.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0);
                t.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Folder + "/" + name + ".png", t.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(t);
                RenderTexture.ReleaseTemporary(ldr);
                _log.AppendLine("shot " + name);
            }
        }

        internal static void Scenario(string name, Action<Ctx> body)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var ctx = new Ctx { Scene = scene };
            try
            {
                var camGo = new GameObject("Check camera");
                SceneManager.MoveGameObjectToScene(camGo, scene);
                ctx.Camera = camGo.AddComponent<Camera>();
                ctx.Camera.scene = scene;
                ctx.Camera.clearFlags = CameraClearFlags.SolidColor;
                ctx.Camera.backgroundColor = new Color(0.45f, 0.55f, 0.7f);
                ctx.Camera.fieldOfView = 70f;
                ctx.Camera.nearClipPlane = 0.1f;
                ctx.Camera.farClipPlane = 2000f;
                ctx.Camera.allowHDR = true;
                ctx.Target = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGBHalf);
                ctx.Camera.targetTexture = ctx.Target;
                body(ctx);
            }
            catch (Exception e) { _log.AppendLine(name + " FAILED: " + e); Debug.LogException(e); }
            finally
            {
                ParelVolumetricFog.Disable();
                ParelSkyClouds.Disable();
                ParelScreenEffects.Clear();
                ParelVolumetricFog.ShadowScene = null;
                ParelFogGlobals.Clear();
                if (ctx.Camera != null) ctx.Camera.targetTexture = null;
                if (ctx.Target != null) { ctx.Target.Release(); UnityEngine.Object.DestroyImmediate(ctx.Target); }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void SkyScene(Ctx c)
        {
            var sky = new Material(Shader.Find("ParelVR/Sky"));
            sky.SetFloat("_SkyMode", 0f);
            sky.SetFloat("_Exposure", 1f);
            c.Camera.clearFlags = CameraClearFlags.Skybox;
            c.Camera.gameObject.AddComponent<Skybox>().material = sky;
            var ground = new Material(Shader.Find("ParelVR/Lit"));
            ground.SetColor("_Color", new Color(0.35f, 0.4f, 0.3f));
            c.Make(PrimitiveType.Plane, Vector3.zero, new Vector3(200, 1, 200), ground);
            Light sun = c.Light(LightType.Directional, Vector3.zero, new Vector3(50f, 30f, 0f), Color.white, 1.3f);
            c.Camera.transform.SetPositionAndRotation(new Vector3(0, 1.7f, 0), Quaternion.Euler(-12f, 30f, 0));
            c.Shot("sky_noon");
            sun.transform.rotation = Quaternion.Euler(3f, 30f, 0f);
            c.Camera.transform.rotation = Quaternion.Euler(-6f, 30f, 0f);
            c.Shot("sky_sunset");
            c.Camera.transform.rotation = Quaternion.Euler(-6f, 210f, 0f);
            c.Shot("sky_sunset_away");

            sun.transform.rotation = Quaternion.Euler(35f, 30f, 0f);
            var clouds = new ParelCloudSettings { Enabled = true, Coverage = 0.5f, Density = 0.4f, ShapeFactor = 0.85f, Erosion = 0.7f, ShapeScaleKm = 10f,
                BottomAltitudeKm = 1.2f, ThicknessKm = 2f, WindSpeed = 30f, Shadows = true, ShadowOpacity = 0.8f };
            ParelSkyClouds.Enable(clouds, () => sun);
            ParelSkyClouds.UpdateNow(0f);
            c.Camera.transform.rotation = Quaternion.Euler(-20f, 60f, 0f);
            c.Shot("sky_volumetric_clouds");
            c.Camera.transform.SetPositionAndRotation(new Vector3(0, 30f, 0), Quaternion.Euler(60f, 60f, 0f));
            c.Shot("sky_cloud_shadows");
            ParelSkyClouds.Disable();
        }

        internal static Material WaterMaterial()
        {
            var m = new Material(Shader.Find("ParelVR/Water"));
            m.SetTexture("_RippleNormal", ParelWaterTextures.RippleNormal());
            m.SetTexture("_FoamMap", ParelWaterTextures.FoamAndCaustics());
            return m;
        }

        private static void WaterScene(Ctx c)
        {
            var sky = new Material(Shader.Find("ParelVR/Sky"));
            c.Camera.clearFlags = CameraClearFlags.Skybox;
            c.Camera.gameObject.AddComponent<Skybox>().material = sky;
            Light sun = c.Light(LightType.Directional, Vector3.zero, new Vector3(40f, 150f, 0f), new Color(1f, 0.95f, 0.88f), 1.3f);

            // A pool: tiled floor 2 m down, a sunken box, steps rising out of it.
            var tile = new Material(Shader.Find("ParelVR/Lit"));
            var tex = new Texture2D(64, 64) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) tex.SetPixel(x, y, ((x / 8 + y / 8) & 1) == 0 ? new Color(0.85f, 0.85f, 0.8f) : new Color(0.3f, 0.5f, 0.6f));
            tex.Apply();
            tile.SetTexture("_MainTex", tex);
            tile.mainTextureScale = new Vector2(6, 6);
            c.Make(PrimitiveType.Plane, new Vector3(0, -2f, 0), new Vector3(2, 1, 2), tile);
            c.Make(PrimitiveType.Cube, new Vector3(2f, -1.5f, 3f), new Vector3(1f, 1f, 1f), tile);
            c.Make(PrimitiveType.Cube, new Vector3(-6f, -0.6f, 0f), new Vector3(4f, 3f, 20f), tile);
            var water = WaterMaterial();
            water.SetVector("_Wave0", new Vector4(1f, 0.2f, 0.03f, 6f));
            water.SetVector("_Wave1", new Vector4(0.7f, 0.7f, 0.02f, 3.5f));
            water.SetVector("_Wave2", new Vector4(0.2f, 1f, 0.015f, 2f));
            for (int i = 3; i < 8; i++) water.SetVector("_Wave" + i, new Vector4(1f, 0f, 0f, 1f));
            water.SetFloat("_AbsorptionDistance", 6f);
            water.SetColor("_RefractionColor", new Color(0.3f, 0.75f, 0.8f));
            water.SetColor("_ScatteringColor", new Color(0.02f, 0.2f, 0.25f));
            var pool = new GameObject("Pool water");
            SceneManager.MoveGameObjectToScene(pool, c.Scene);
            pool.AddComponent<MeshFilter>().sharedMesh = ParelWaterTextures.Grid(new Vector2(20f, 20f), 100);
            pool.AddComponent<MeshRenderer>().sharedMaterial = water;
            ParelOpaqueCapture.Request();
            try
            {
                c.Camera.transform.SetPositionAndRotation(new Vector3(0, 2.2f, -7f), Quaternion.Euler(28f, 0, 0));
                c.Shot("water_pool");
                c.Camera.transform.SetPositionAndRotation(new Vector3(0, 0.6f, -9f), Quaternion.Euler(6f, 10f, 0));
                c.Shot("water_pool_low");
            }
            finally { ParelOpaqueCapture.Release(); }

            // An ocean to the horizon.
            UnityEngine.Object.DestroyImmediate(pool);
            var ocean = new GameObject("Ocean");
            SceneManager.MoveGameObjectToScene(ocean, c.Scene);
            ocean.AddComponent<MeshFilter>().sharedMesh = ParelWaterTextures.OceanGrid();
            var sea = WaterMaterial();
            sea.SetColor("_ScatteringColor", new Color(0.0f, 0.12f, 0.16f));
            ocean.AddComponent<MeshRenderer>().sharedMaterial = sea;
            ParelOpaqueCapture.Request();
            try
            {
                c.Camera.transform.SetPositionAndRotation(new Vector3(0, 4f, 0), Quaternion.Euler(8f, -30f, 0));
                c.Shot("water_ocean");
                c.Camera.transform.SetPositionAndRotation(new Vector3(0, 4f, 0), Quaternion.Euler(6f, 150f, 0));
                c.Shot("water_ocean_sun");
            }
            finally { ParelOpaqueCapture.Release(); }
        }

        private static Material Lit(Color color, float smoothness, float metallic = 0f)
        {
            var m = new Material(Shader.Find("ParelVR/Lit"));
            m.SetColor("_Color", color);
            m.SetFloat("_Glossiness", smoothness);
            m.SetFloat("_Metallic", metallic);
            return m;
        }

        private static void FeatureScene(Ctx c)
        {
            var sky = new Material(Shader.Find("ParelVR/Sky"));
            c.Camera.clearFlags = CameraClearFlags.Skybox;
            c.Camera.gameObject.AddComponent<Skybox>().material = sky;
            Light sun = c.Light(LightType.Directional, Vector3.zero, new Vector3(35f, 200f, 0f), Color.white, 1.2f);

            // A glossy floor (screen-space reflections) with spheres showing each material feature.
            c.Make(PrimitiveType.Plane, Vector3.zero, new Vector3(3, 1, 3), Lit(new Color(0.15f, 0.15f, 0.17f), 0.95f));
            var plain = Lit(new Color(0.8f, 0.1f, 0.1f), 0.4f);
            var coat = Lit(new Color(0.8f, 0.1f, 0.1f), 0.4f); coat.SetFloat("_ParelCoatMask", 1f);
            var irid = Lit(new Color(0.1f, 0.1f, 0.1f), 0.85f, 1f); irid.SetFloat("_ParelIridescence", 1f); irid.SetFloat("_ParelIridescenceThickness", 0.55f);
            var aniso = Lit(new Color(0.9f, 0.9f, 0.9f), 0.75f, 1f); aniso.SetFloat("_ParelAnisotropy", 0.85f);
            var glass = new Material(Shader.Find("ParelVR/Transparent"));
            glass.SetColor("_Color", new Color(1f, 1f, 1f, 0.15f));
            glass.SetFloat("_Glossiness", 0.95f);
            glass.SetFloat("_Mode", 3f); glass.SetFloat("_SrcBlend", 1f); glass.SetFloat("_DstBlend", 10f); glass.SetFloat("_ZWrite", 0f);
            glass.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            glass.renderQueue = 3000;
            glass.SetFloat("_ParelRefraction", 2f); glass.SetFloat("_ParelIor", 1.5f); glass.SetFloat("_ParelRefractionThickness", 0.5f);
            glass.SetColor("_ParelTransmittanceColor", new Color(0.6f, 0.9f, 0.8f, 1f)); glass.SetFloat("_ParelAbsorptionDistance", 1f);
            float x = -4.5f;
            foreach (Material m in new[] { plain, coat, irid, aniso, glass }) { c.Make(PrimitiveType.Sphere, new Vector3(x, 1f, 4f), Vector3.one * 1.6f, m); x += 2.2f; }
            // A checker wall behind the glass so refraction shows.
            var tex = new Texture2D(32, 32) { filterMode = FilterMode.Point };
            for (int yy = 0; yy < 32; yy++) for (int xx = 0; xx < 32; xx++) tex.SetPixel(xx, yy, ((xx / 4 + yy / 4) & 1) == 0 ? Color.white : new Color(0.1f, 0.3f, 0.8f));
            tex.Apply();
            var wall = Lit(Color.white, 0.2f); wall.SetTexture("_MainTex", tex);
            c.Make(PrimitiveType.Cube, new Vector3(0f, 2f, 7f), new Vector3(14f, 4f, 0.2f), wall);
            // A real-time rectangle area light over the floor.
            var areaGo = new GameObject("Area light");
            SceneManager.MoveGameObjectToScene(areaGo, c.Scene);
            areaGo.transform.SetPositionAndRotation(new Vector3(-3f, 3.5f, 1.5f), Quaternion.Euler(90f, 0f, 0f));
            var area = areaGo.AddComponent<ParelAreaLight>();
            area.Size = new Vector2(3f, 0.4f); area.Color = new Color(1f, 0.5f, 0.9f); area.Intensity = 6f; area.Range = 12f;
            c.Make(PrimitiveType.Cube, areaGo.transform.position + Vector3.up * 0.05f, new Vector3(3f, 0.05f, 0.4f), Lit(Color.white, 0f)).GetComponent<Renderer>().sharedMaterial.SetColor("_EmissionColor", Color.white);
            ParelAreaLight.Upload(Vector3.zero);

            ParelScreenEffects.Apply(new ParelScreenEffectsSettings { Reflections = true, ContactShadows = true, ContactShadowLength = 0.3f, MicroShadowOpacity = 1f, GlobalIllumination = true });
            c.Camera.transform.SetPositionAndRotation(new Vector3(0f, 2.2f, -4f), Quaternion.Euler(14f, 0f, 0f));
            c.Shot("features_frame1");   // first frame: last-frame picture empty
            c.Shot("features");
            ParelScreenEffects.Clear();
            c.Shot("features_no_screen_effects");
            Shader.SetGlobalVector("_ParelAreaLightCount", Vector4.zero);
            UnityEngine.Object.DestroyImmediate(area);
        }

        private static void FogScene(Ctx c)
        {
            var lit = new Material(Shader.Find("ParelVR/Lit"));
            lit.SetColor("_Color", new Color(0.6f, 0.6f, 0.6f));
            var avatar = new Material(Shader.Find("Standard")) { color = new Color(0.8f, 0.3f, 0.2f) };
            c.Make(PrimitiveType.Plane, Vector3.zero, new Vector3(30, 1, 30), lit);
            for (int i = -4; i <= 4; i++) c.Make(PrimitiveType.Cube, new Vector3(i * 3f, 6f, 12f), new Vector3(1.2f, 12f, 1.2f), lit);
            c.Make(PrimitiveType.Cube, new Vector3(0, 12.5f, 12f), new Vector3(30f, 1f, 4f), lit);
            c.Make(PrimitiveType.Capsule, new Vector3(1.5f, 1f, 4f), Vector3.one, avatar);
            c.Make(PrimitiveType.Capsule, new Vector3(-2f, 1f, 30f), Vector3.one, avatar);
            Light sun = c.Light(LightType.Directional, Vector3.zero, new Vector3(25f, 180f, 0f), new Color(1f, 0.92f, 0.8f), 1.2f);
            c.Light(LightType.Point, new Vector3(-5f, 2.5f, 3f), Vector3.zero, new Color(1f, 0.55f, 0.2f), 6f, 8f);
            c.Light(LightType.Spot, new Vector3(6f, 7f, 4f), new Vector3(90f, 0f, 0f), new Color(0.4f, 0.7f, 1f), 12f, 14f).spotAngle = 40f;

            var fog = new ParelFogSettings
            {
                Enabled = true, Extinction = 0.03f, BaseHeight = 0f, HeightFalloff = 0.08f, MaxDistance = 500f,
                Albedo = new Color(0.8f, 0.82f, 0.85f), AmbientFromSky = false, Ambient = new Color(0.25f, 0.28f, 0.33f),
                Anisotropy = 0.5f, SunScattering = 1f, VolumetricRange = 80f,
            };
            c.Camera.transform.SetPositionAndRotation(new Vector3(0, 1.7f, -4f), Quaternion.Euler(-8f, 0, 0));

            ParelFogGlobals.Apply(fog, sun, 1f);
            ParelVolumetricFog.ShadowScene = c.Scene;
            ParelVolumetricFog.Enable(fog, () => sun);
            ParelVolumetricFog.UpdateNow();
            c.Shot("fog_volumetric");
            c.Camera.transform.SetPositionAndRotation(new Vector3(-14f, 3f, -2f), Quaternion.Euler(5f, 50f, 0f));
            c.Shot("fog_volumetric_side");
            c.Camera.transform.SetPositionAndRotation(new Vector3(0, 1.7f, -4f), Quaternion.Euler(-8f, 0, 0));

            ParelVolumetricFog.Disable();
            ParelFogGlobals.Apply(fog, sun, 1f);
            c.Shot("fog_inshader");

            ParelFogGlobals.Clear();
            c.Shot("fog_none");
        }
    }
}
