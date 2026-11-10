using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using ParelVR.SDK.Build.Quest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Tests
{
    /// <summary>
    /// The Quest conversion's PVRP steps (Important lights, Mesh LODs, budgets) on a throwaway scene and a generated
    /// model in a temporary folder, which is deleted afterwards. Nothing else in the project is touched.
    /// </summary>
    public sealed class QuestConverterTests
    {
        private const string Folder = "Assets/__PvrpQuestTest";
        private Scene _scene;

        [TearDown]
        public void CleanUp()
        {
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            AssetDatabase.DeleteAsset(Folder);
        }

        private static string WriteSphereObj(string name, int rings, int segments)
        {
            Directory.CreateDirectory(Folder);
            var obj = new StringBuilder();
            for (int r = 0; r <= rings; r++)
            for (int s = 0; s <= segments; s++)
            {
                float t = Mathf.PI * r / rings, p = 2f * Mathf.PI * s / segments;
                obj.AppendFormat(CultureInfo.InvariantCulture, "v {0} {1} {2}\n", Mathf.Sin(t) * Mathf.Cos(p), Mathf.Cos(t), Mathf.Sin(t) * Mathf.Sin(p));
            }
            for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int a = r * (segments + 1) + s + 1, b = a + segments + 1;
                obj.AppendFormat("f {0} {1} {2}\nf {2} {1} {3}\n", a, b, a + 1, b + 1);
            }
            string path = Folder + "/" + name + ".obj";
            File.WriteAllText(path, obj.ToString());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }

        private GameObject Add(GameObject go)
        {
            SceneManager.MoveGameObjectToScene(go, _scene);
            return go;
        }

        [Test]
        public void World_conversion_relaxes_important_lights_adds_mesh_lods_and_measures()
        {
            string heavy = WriteSphereObj("Heavy", 60, 60); // 7,200 triangles
            string light = WriteSphereObj("Light", 10, 10); //   200 triangles
            _scene = EditorSceneManager.NewPreviewScene();

            long expectedTriangles = 0;
            foreach (string path in new[] { heavy, light })
            {
                Mesh mesh = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().First();
                expectedTriangles += mesh.GetIndexCount(0) / 3; // as imported (the importer drops degenerate pole triangles)
                var go = Add(new GameObject(Path.GetFileNameWithoutExtension(path), typeof(MeshFilter), typeof(MeshRenderer)));
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.GetComponent<MeshRenderer>().sharedMaterials = new Material[2];
            }
            for (int i = 0; i < 3; i++)
            {
                var l = Add(new GameObject("Important " + i)).AddComponent<Light>();
                l.type = LightType.Point;
                l.renderMode = LightRenderMode.ForcePixel;
                l.intensity = 1f + i;
                l.range = 10f;
            }
            var sun = Add(new GameObject("Sun")).AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.renderMode = LightRenderMode.ForcePixel;

            var report = new ParelQuestReport();
            ParelQuestConverter.ConvertWorldScene(_scene, Folder + "/Generated", report);

            Light[] lights = _scene.GetRootGameObjects().SelectMany(g => g.GetComponents<Light>()).ToArray();
            Assert.AreEqual(LightRenderMode.ForcePixel, lights.First(l => l.name == "Important 2").renderMode, "the brightest keeps Important");
            Assert.AreEqual(LightRenderMode.Auto, lights.First(l => l.name == "Important 0").renderMode);
            Assert.AreEqual(LightRenderMode.Auto, lights.First(l => l.name == "Important 1").renderMode);
            Assert.AreEqual(LightRenderMode.ForcePixel, sun.renderMode, "directional lights are left alone");
            Assert.AreEqual(3, report.forcedPixelLights);
            Assert.AreEqual(4, report.realtimeLights);

            var heavyImporter = (ModelImporter)AssetImporter.GetAtPath(heavy);
            var lightImporter = (ModelImporter)AssetImporter.GetAtPath(light);
            var generate = typeof(ModelImporter).GetProperty("generateMeshLods");
            if (generate != null)
            {
                Assert.IsTrue((bool)generate.GetValue(heavyImporter), "heavy model gets Mesh LODs");
                Assert.IsFalse((bool)generate.GetValue(lightImporter), "light model is left alone");
                Assert.AreEqual(1, report.meshesWithLods);
            }
            Assert.AreEqual(expectedTriangles, report.triangles, "full-detail triangles only, LODs not counted");
            Assert.Greater(report.triangles, 7000);
            Assert.AreEqual(4, report.materialSlots);
            StringAssert.Contains("Over the headset budget", report.Summary(), "3 Important lights is a finding");
        }

        [Test]
        public void Texture_conversion_estimates_headset_memory()
        {
            Directory.CreateDirectory(Folder);
            string path = Folder + "/Tex.png";
            var t = new Texture2D(1024, 512, TextureFormat.RGBA32, false);
            File.WriteAllBytes(path, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var report = new ParelQuestReport();
            ParelQuestConverter.ConvertTextures(new[] { path }, 512, report);
            // Capped to 512 x 256, ASTC 6x6 with mips.
            Assert.AreEqual(QuestBudgets.TextureBytes(512, 256, true), report.textureBytes);
            Assert.AreEqual(1, report.texturesDownsized);
        }
    }
}
