using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Tests
{
    /// <summary>
    /// Visual and property check of the Built-in → ParelVR conversion. Builds one material per Built-in case, converts
    /// it for Quest (where Built-in lit shaders are converted), renders the originals (top row) above the
    /// conversions (bottom row) in a preview scene, and logs every converted material. Nothing is saved to the
    /// project. Output: Logs/PVRP/ConversionCheck.png and ConversionCheck.txt.
    /// </summary>
    public static class PvrpConversionCheck
    {
        public const string Folder = "Logs/PVRP";

        [MenuItem("ParelVR/Rendering/Tests/Render Conversion Check")]
        public static void Run()
        {
            Directory.CreateDirectory(Folder);
            var log = new StringBuilder();
            var temp = new List<Object>();
            bool asyncWas = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            Scene scene = EditorSceneManager.NewPreviewScene();
            var converter = new ParelMaterialConverter(TargetPlatform.Quest);
            try
            {
                ParelRenderManager.Initialize();
                Texture2D albedo = Checker(temp);
                Texture2D normal = Bumps(temp);
                List<KeyValuePair<string, Material>> cases = Cases(albedo, normal, temp);

                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Mesh mesh = Object.Instantiate(sphere.GetComponent<MeshFilter>().sharedMesh);
                Object.DestroyImmediate(sphere);
                var colors = new Color[mesh.vertexCount];
                for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
                mesh.colors = colors;
                temp.Add(mesh);

                for (int i = 0; i < cases.Count; i++)
                {
                    Material original = cases[i].Value;
                    MaterialConversionResult r = converter.Convert(original);
                    Place(scene, mesh, original, new Vector3(i * 1.2f, 0.65f, 0f));
                    Place(scene, mesh, r.Result, new Vector3(i * 1.2f, -0.65f, 0f));
                    Describe(log, i, cases[i].Key, original, r);
                }

                var lightGo = new GameObject("Sun");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                light.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.Euler(35f, -30f, 0f);
                SceneManager.MoveGameObjectToScene(lightGo, scene);

                var camGo = new GameObject("Camera");
                SceneManager.MoveGameObjectToScene(camGo, scene);
                var cam = camGo.AddComponent<Camera>();
                cam.scene = scene;
                cam.orthographic = true;
                cam.orthographicSize = 1.4f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.32f, 0.34f, 0.38f, 1f);
                cam.transform.position = new Vector3((cases.Count - 1) * 0.6f, 0f, -10f);
                int width = cases.Count * 150, height = 340;
                cam.aspect = (float)width / height;
                var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                temp.Add(rt);
                cam.targetTexture = rt;
                cam.Render();
                cam.Render();

                RenderTexture.active = rt;
                var png = new Texture2D(width, height, TextureFormat.RGB24, false);
                temp.Add(png);
                png.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                png.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(Folder, "ConversionCheck.png"), png.EncodeToPNG());

                log.AppendLine().AppendLine("Shader messages after rendering:");
                int problems = 0;
                foreach (Shader s in ParelShaderLibrary.Active != null ? ParelShaderLibrary.Active.AllShaders() : new Shader[0])
                {
                    foreach (ShaderMessage m in ShaderUtil.GetShaderMessages(s))
                    {
                        problems++;
                        log.Append("  ").Append(s.name).Append(": ").Append(m.severity).Append(' ').Append(m.message).Append(" (").Append(m.platform).AppendLine(")");
                    }
                }
                if (problems == 0) log.AppendLine("  none");
            }
            finally
            {
                converter.Release();
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (Object o in temp) if (o != null) Object.DestroyImmediate(o);
                ShaderUtil.allowAsyncCompilation = asyncWas;
            }
            File.WriteAllText(Path.Combine(Folder, "ConversionCheck.txt"), log.ToString());
            Debug.Log(RenderingLog.Prefix + "Conversion check written to " + Path.GetFullPath(Folder));
        }

        private static void Place(Scene scene, Mesh mesh, Material material, Vector3 position)
        {
            var go = new GameObject(material != null ? material.name : "null", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.transform.position = position;
            SceneManager.MoveGameObjectToScene(go, scene);
        }

        private static void Describe(StringBuilder log, int index, string label, Material original, MaterialConversionResult r)
        {
            ConversionPlan p = r.Plan;
            Material m = r.Result;
            log.Append(index).Append(". ").Append(label).Append(" [").Append(original.shader.name).Append("] -> ")
               .Append(p.Action).Append(p.Role.HasValue ? " " + p.Role.Value : "").Append(" [").Append(m.shader.name).Append("] ")
               .Append(p.Entry.Severity).AppendLine();
            log.Append("     queue ").Append(m.renderQueue).Append(" keywords ").Append(string.Join(" ", m.shaderKeywords));
            Prop(log, m, "_Color"); Prop(log, m, "_Metallic"); Prop(log, m, "_Glossiness"); Prop(log, m, "_Cutoff");
            Prop(log, m, "_SrcBlend"); Prop(log, m, "_DstBlend"); Prop(log, m, "_ZWrite"); Prop(log, m, "_Shininess");
            if (m.HasProperty("_MainTex")) log.Append(" _MainTex=").Append(m.GetTexture("_MainTex") ? m.GetTexture("_MainTex").name : "none").Append(" x").Append(m.GetTextureScale("_MainTex"));
            if (m.HasProperty("_BumpMap") && m.GetTexture("_BumpMap")) log.Append(" _BumpMap=").Append(m.GetTexture("_BumpMap").name);
            if (m.HasProperty("_EmissionColor")) log.Append(" _EmissionColor=").Append(m.GetColor("_EmissionColor"));
            log.AppendLine();
            if (p.Entry.Unmapped.Count > 0) log.Append("     not carried over: ").AppendLine(string.Join(", ", p.Entry.Unmapped));
            if (!string.IsNullOrEmpty(p.Entry.Message)) log.Append("     ").AppendLine(p.Entry.Message);
        }

        private static void Prop(StringBuilder log, Material m, string name)
        {
            if (!m.HasProperty(name)) return;
            if (name == "_Color") log.Append(' ').Append(name).Append('=').Append(m.GetColor(name));
            else log.Append(' ').Append(name).Append('=').Append(m.GetFloat(name).ToString("0.###"));
        }

        // ---- test content ----

        private static List<KeyValuePair<string, Material>> Cases(Texture2D albedo, Texture2D normal, List<Object> temp)
        {
            var list = new List<KeyValuePair<string, Material>>();
            void Add(string label, Material m) { m.name = label; temp.Add(m); list.Add(new KeyValuePair<string, Material>(label, m)); }

            Material Standard(string shader = "Standard")
            {
                var m = new Material(Shader.Find(shader));
                m.SetColor("_Color", new Color(0.9f, 0.85f, 0.8f, 1f));
                m.SetTexture("_MainTex", albedo);
                m.SetTextureScale("_MainTex", new Vector2(2f, 1f));
                m.SetTexture("_BumpMap", normal);
                m.EnableKeyword("_NORMALMAP");
                m.SetFloat("_Metallic", 0.5f);
                m.SetFloat("_Glossiness", 0.6f);
                return m;
            }

            Add("Standard opaque", Standard());

            Material cut = Standard();
            cut.SetFloat("_Mode", 1); cut.EnableKeyword("_ALPHATEST_ON"); cut.SetFloat("_Cutoff", 0.5f);
            cut.SetOverrideTag("RenderType", "TransparentCutout"); cut.renderQueue = (int)RenderQueue.AlphaTest;
            Add("Standard cutout", cut);

            Material fade = Standard();
            Blend(fade, 2, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, "_ALPHABLEND_ON");
            Add("Standard fade", fade);

            Material glass = Standard();
            Blend(glass, 3, BlendMode.One, BlendMode.OneMinusSrcAlpha, "_ALPHAPREMULTIPLY_ON");
            Add("Standard transparent", glass);

            Material glow = Standard();
            glow.EnableKeyword("_EMISSION");
            glow.SetColor("_EmissionColor", new Color(2f, 0.5f, 0f, 1f));
            glow.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            Add("Standard emission", glow);

            Material spec = Standard("Standard (Specular setup)");
            spec.SetColor("_SpecColor", new Color(0.6f, 0.5f, 0.2f, 1f));
            Add("Standard specular", spec);

            var diffuse = new Material(Shader.Find("Legacy Shaders/Diffuse"));
            diffuse.SetColor("_Color", new Color(0.4f, 0.8f, 0.4f, 1f)); diffuse.SetTexture("_MainTex", albedo);
            Add("Legacy Diffuse", diffuse);

            var bumpSpec = new Material(Shader.Find("Legacy Shaders/Bumped Specular"));
            bumpSpec.SetTexture("_MainTex", albedo); bumpSpec.SetTexture("_BumpMap", normal); bumpSpec.SetFloat("_Shininess", 0.15f);
            Add("Legacy Bumped Specular", bumpSpec);

            var legacyCut = new Material(Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse"));
            legacyCut.SetTexture("_MainTex", albedo); legacyCut.SetFloat("_Cutoff", 0.5f);
            Add("Legacy Cutout", legacyCut);

            var unlit = new Material(Shader.Find("Unlit/Texture"));
            unlit.SetTexture("_MainTex", albedo);
            Add("Unlit Texture", unlit);

            var additive = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
            additive.SetTexture("_MainTex", albedo); additive.SetColor("_TintColor", new Color(0.5f, 0.3f, 0.1f, 0.5f));
            Add("Legacy Particles Additive", additive);

            var stdParticles = new Material(Shader.Find("Particles/Standard Unlit"));
            stdParticles.SetTexture("_MainTex", albedo); stdParticles.SetColor("_Color", new Color(0.5f, 0.7f, 1f, 1f));
            Blend(stdParticles, 2, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, "_ALPHABLEND_ON");
            Add("Particles Standard Unlit", stdParticles);

            Material broken = Standard();
            broken.shader = Shader.Find(ShaderClassifier.ErrorShaderName);
            Add("Missing shader (was Standard)", broken);

            var urp = new Material(Shader.Find(ShaderClassifier.ErrorShaderName));
            urp.SetTexture("_BaseMap", albedo); urp.SetColor("_BaseColor", new Color(1f, 0.6f, 0.6f, 1f));
            urp.SetFloat("_Surface", 0); urp.SetFloat("_WorkflowMode", 1); urp.SetFloat("_Metallic", 0.2f); urp.SetFloat("_Smoothness", 0.5f);
            Add("Missing shader (URP Lit values)", urp);
            return list;
        }

        private static void Blend(Material m, int mode, BlendMode src, BlendMode dst, string keyword)
        {
            m.SetFloat("_Mode", mode);
            m.SetFloat("_SrcBlend", (float)src);
            m.SetFloat("_DstBlend", (float)dst);
            m.SetFloat("_ZWrite", 0);
            m.EnableKeyword(keyword);
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        private static Texture2D Checker(List<Object> temp)
        {
            var t = new Texture2D(128, 128, TextureFormat.RGBA32, true) { name = "PVRP Checker" };
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    bool on = ((x / 16) + (y / 16)) % 2 == 0;
                    Color c = on ? new Color(0.95f, 0.75f, 0.35f) : new Color(0.25f, 0.45f, 0.85f);
                    c.a = Mathf.Clamp01(x / 127f * 1.4f - 0.2f);
                    t.SetPixel(x, y, c);
                }
            t.Apply();
            temp.Add(t);
            return t;
        }

        private static Texture2D Bumps(List<Object> temp)
        {
            var t = new Texture2D(128, 128, TextureFormat.RGBA32, true, true) { name = "PVRP Bumps" };
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    float nx = Mathf.Sin(x / 128f * Mathf.PI * 8f) * 0.5f;
                    float ny = Mathf.Sin(y / 128f * Mathf.PI * 8f) * 0.5f;
                    var n = new Vector3(nx, ny, 1f).normalized;
                    t.SetPixel(x, y, new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f));
                }
            t.Apply();
            temp.Add(t);
            return t;
        }
    }
}
