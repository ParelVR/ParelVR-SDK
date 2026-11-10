using System.Linq;
using NUnit.Framework;
using ParelVR.Rendering.Editor;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Tests
{
    /// <summary>Stands in for HDRP / URP's DecalProjector (the baker finds projectors by type name and reads these members).</summary>
    public sealed class DecalProjector : MonoBehaviour
    {
        public Material material;
        public Vector3 size = new Vector3(2f, 2f, 1f);
        public Vector3 pivot = Vector3.zero;
        public Vector2 uvScale = Vector2.one;
        public Vector2 uvBias = Vector2.zero;
        public float fadeFactor = 1f;
        public float startAngleFade = 180f;
        public float endAngleFade = 180f;
    }

    /// <summary>The HDRP features PVRP carries over: subsurface, layered lit, volumetric fog, decals. Rendered for real.</summary>
    public sealed class HdrpFeatureTests
    {
        private const string Folder = "Assets/__PvrpFeatureTest";
        private Scene _scene;
        private bool _async;

        [SetUp]
        public void SetUp()
        {
            _async = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            _scene = EditorSceneManager.NewPreviewScene();
            ParelFogGlobals.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ParelFogGlobals.Clear();
            EditorSceneManager.ClosePreviewScene(_scene);
            ShaderUtil.allowAsyncCompilation = _async;
            AssetDatabase.DeleteAsset(Folder);
        }

        private T Add<T>(T c) where T : Component
        {
            SceneManager.MoveGameObjectToScene(c.transform.root.gameObject, _scene);
            return c;
        }

        private GameObject Primitive(PrimitiveType type, Material m, Vector3 pos)
        {
            GameObject g = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(g.GetComponent<Collider>());
            g.transform.position = pos;
            g.GetComponent<Renderer>().sharedMaterial = m;
            SceneManager.MoveGameObjectToScene(g, _scene);
            return g;
        }

        private Light Sun(Vector3 euler, float intensity = 1f)
        {
            var l = new GameObject("Sun").AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.shadows = LightShadows.None;
            l.transform.rotation = Quaternion.Euler(euler);
            return Add(l);
        }

        private Color Render(Vector3 camPos, Vector3 lookAt, Vector2 pixel, Color background)
        {
            var cam = new GameObject("Cam").AddComponent<Camera>();
            Add(cam);
            cam.scene = _scene;
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            cam.farClipPlane = 500f;
            cam.transform.position = camPos;
            cam.transform.LookAt(lookAt);
            var rt = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            Color c = tex.GetPixel((int)(pixel.x * 63), (int)(pixel.y * 63));
            cam.targetTexture = null;
            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(cam.gameObject);
            return c;
        }

        [Test]
        public void Every_parelvr_shader_compiles_without_errors()
        {
            foreach (string name in new[] { "ParelVR/Lit", "ParelVR/Cutout", "ParelVR/Transparent", "ParelVR/Simple Lit", "ParelVR/Unlit", "ParelVR/Emissive",
                         "ParelVR/Particles/Lit", "ParelVR/Particles/Unlit", "ParelVR/Skybox", "ParelVR/Subsurface", "ParelVR/Layered Lit" })
            {
                Shader s = Shader.Find(name);
                Assert.IsNotNull(s, name);
                Assert.IsFalse(ShaderUtil.ShaderHasError(s), name + ": " + string.Join(" | ", ShaderUtil.GetShaderMessages(s).Select(m => m.message)));
                Assert.IsTrue(s.isSupported, name + " supported");
                Assert.AreEqual(0, ShaderUtil.GetShaderMessages(s).Count(m => m.severity == ShaderCompilerMessageSeverity.Error), name);
            }
        }

        [Test]
        public void Subsurface_lets_light_through_from_behind()
        {
            // Sun behind the spheres, shining at the camera: the lit sphere's front is dark, the subsurface one glows.
            Sun(new Vector3(0f, 180f, 0f), 2f);
            var lit = new Material(Shader.Find("ParelVR/Lit")) { color = new Color(0.8f, 0.7f, 0.6f) };
            var sss = new Material(Shader.Find("ParelVR/Subsurface")) { color = new Color(0.8f, 0.7f, 0.6f) };
            sss.SetFloat("_TransmissionScale", 1f);
            sss.SetFloat("_ThicknessMin", 0f);
            sss.SetFloat("_ThicknessMax", 0f);
            sss.SetColor("_TransmissionTint", new Color(1f, 0.5f, 0.3f));
            RenderSettings.ambientMode = AmbientMode.Flat;
            GameObject a = Primitive(PrimitiveType.Sphere, lit, Vector3.zero);
            Color plain = Render(new Vector3(0f, 0f, -3f), Vector3.zero, new Vector2(0.5f, 0.5f), Color.black);
            a.GetComponent<Renderer>().sharedMaterial = sss;
            Color through = Render(new Vector3(0f, 0f, -3f), Vector3.zero, new Vector2(0.5f, 0.5f), Color.black);
            Assert.Greater(through.r, plain.r + 0.1f, $"transmission adds light: plain {plain}, subsurface {through}");
            Assert.Greater(through.r, through.b, "tinted warm by the transmission tint");
        }

        [Test]
        public void Layered_lit_switches_layers_with_the_mask()
        {
            Sun(new Vector3(30f, 0f, 0f));
            var mask = new Texture2D(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) mask.SetPixel(x, y, x < 2 ? Color.black : Color.red);
            mask.Apply();
            var m = new Material(Shader.Find("ParelVR/Layered Lit"));
            m.SetFloat("_LayerCount", 2f);
            m.SetTexture("_LayerMaskMap", mask);
            m.SetColor("_Layer0Color", new Color(1f, 0.05f, 0.05f));
            m.SetColor("_Layer1Color", new Color(0.05f, 1f, 0.05f));
            GameObject quad = Primitive(PrimitiveType.Quad, m, Vector3.zero);
            quad.transform.rotation = Quaternion.Euler(0f, 180f, 0f); // face the camera at -Z with UV x left → right
            Color left = Render(new Vector3(0f, 0f, 1.2f), Vector3.zero, new Vector2(0.25f, 0.5f), Color.black);
            Color right = Render(new Vector3(0f, 0f, 1.2f), Vector3.zero, new Vector2(0.75f, 0.5f), Color.black);
            bool leftRed = left.r > left.g, rightGreen = right.g > right.r;
            bool flipped = left.g > left.r && right.r > right.g;
            Assert.IsTrue((leftRed && rightGreen) || flipped, $"one half shows layer 0, the other layer 1: left {left}, right {right}");
            Object.DestroyImmediate(mask);
        }

        [Test]
        public void Volumetric_fog_fades_distant_surfaces_and_clears()
        {
            Sun(new Vector3(30f, 0f, 0f));
            var white = new Material(Shader.Find("ParelVR/Unlit")) { color = Color.white };
            Primitive(PrimitiveType.Cube, white, new Vector3(0f, 0f, 60f)).transform.localScale = Vector3.one * 20f;
            Color clear = Render(Vector3.zero, new Vector3(0f, 0f, 60f), new Vector2(0.5f, 0.5f), Color.black);

            var fog = new ParelFogSettings
            {
                Enabled = true, Extinction = 0.08f, BaseHeight = 100f, HeightFalloff = 0.01f, MaxDistance = 1000f,
                Albedo = Color.white, AmbientFromSky = false, Ambient = new Color(0f, 0f, 1f), SunScattering = 0f,
            };
            ParelFogGlobals.Apply(fog, null);
            Color fogged = Render(Vector3.zero, new Vector3(0f, 0f, 60f), new Vector2(0.5f, 0.5f), Color.black);
            ParelFogGlobals.Clear();
            Color after = Render(Vector3.zero, new Vector3(0f, 0f, 60f), new Vector2(0.5f, 0.5f), Color.black);

            Assert.Greater(clear.r, 0.9f);
            Assert.Less(fogged.r, 0.2f, $"50 m into thick fog the white wall is gone: {fogged}");
            Assert.Greater(fogged.b, fogged.r + 0.3f, "and shows the (blue) fog light instead");
            Assert.AreEqual(clear.r, after.r, 0.02f, "fog cleared: back to the original");
        }

        [Test]
        public void Local_fog_box_tints_only_what_is_seen_through_it()
        {
            Sun(new Vector3(30f, 0f, 0f));
            var white = new Material(Shader.Find("ParelVR/Unlit")) { color = Color.white };
            Primitive(PrimitiveType.Quad, white, new Vector3(-2f, 0f, 10f)).transform.localScale = Vector3.one * 3f;
            Primitive(PrimitiveType.Quad, white, new Vector3(2f, 0f, 10f)).transform.localScale = Vector3.one * 3f;
            var box = new GameObject("Box").transform;
            Add(box);
            box.position = new Vector3(-2f, 0f, 5f);
            var fog = new ParelFogSettings
            {
                Enabled = true, Extinction = 0f, MaxDistance = 1000f, AmbientFromSky = false, Ambient = Color.white, SunScattering = 0f,
                Volumes = new[] { new LocalFogVolume { Volume = box, Size = new Vector3(3f, 3f, 4f), Albedo = Color.red, Extinction = 1.5f } },
            };
            ParelFogGlobals.Apply(fog, null);
            Color throughBox = Render(Vector3.zero, new Vector3(0f, 0f, 10f), new Vector2(0.35f, 0.5f), Color.black);
            Color clear = Render(Vector3.zero, new Vector3(0f, 0f, 10f), new Vector2(0.65f, 0.5f), Color.black);
            Assert.Greater(throughBox.r, throughBox.g + 0.3f, $"seen through the red box: {throughBox}");
            Assert.Greater(clear.g, 0.9f, $"beside it, untouched: {clear}");
        }

        [Test]
        public void Decal_projector_is_baked_into_the_surface_under_it()
        {
            var groundMat = new Material(Shader.Find("ParelVR/Lit"));
            Primitive(PrimitiveType.Plane, groundMat, Vector3.zero); // 10 × 10 m, 200 triangles

            var decalSource = new Material(Shader.Find("ParelVR/Lit")) { name = "Sticker", color = new Color(1f, 0.8f, 0.1f, 1f) };
            var projector = new GameObject("Sticker Projector").AddComponent<DecalProjector>();
            Add(projector);
            projector.material = decalSource;
            projector.size = new Vector3(3f, 2f, 1f);
            projector.transform.position = new Vector3(1f, 0.3f, 0f);
            projector.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // projects straight down

            // (Normal-only decals need HDRP's decal shader to be recognised; that case is checked in the HDRP fixture.)
            GameObject projectorObject = projector.gameObject;
            var report = new CompatibilityReport();
            int baked = ParelDecalBaker.BakeAll(_scene, Folder + "/Decals", report);

            Assert.AreEqual(1, baked, string.Join(" | ", report.Notes.Select(n => n.Subject + ": " + n.Message)));
            Assert.IsNull(projectorObject.GetComponent<DecalProjector>(), "the projector component is removed from the build copy");
            MeshFilter mf = projectorObject.GetComponentInChildren<MeshFilter>();
            Assert.IsNotNull(mf, "baked decal mesh under the projector");
            Mesh mesh = mf.sharedMesh;
            Assert.Greater(mesh.triangles.Length / 3, 0);
            Bounds b = mf.GetComponent<Renderer>().bounds;
            Assert.AreEqual(3f, b.size.x, 0.05f, "clipped to the projector's 3 m width");
            Assert.AreEqual(2f, b.size.z, 0.05f, "and its 2 m height (world Z when projecting down)");
            Assert.AreEqual(1f, b.center.x, 0.05f);
            Assert.AreEqual(ParelDecalBaker.SurfaceOffset, b.center.y, 0.001f, "just above the ground");
            foreach (Vector2 uv in mesh.uv) Assert.That(uv.x >= -0.001f && uv.x <= 1.001f && uv.y >= -0.001f && uv.y <= 1.001f, "UVs inside the decal: " + uv);
            Material m = mf.GetComponent<Renderer>().sharedMaterial;
            Assert.AreEqual("ParelVR/Transparent", m.shader.name);
            Assert.Less(m.renderQueue, 3000, "drawn before other transparent surfaces");
        }
    }
}
