using System.IO;
using System.Linq;
using NUnit.Framework;
using ParelVR.SDK.Build;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Tests
{
    /// <summary>
    /// The SDK upload step: worlds and avatars that need it are converted on the build's copy, the creator's files stay
    /// byte-for-byte the same, Built-in content that renders is built as it is. Everything lives in a temp folder.
    /// </summary>
    public sealed class UploadRenderingTests
    {
        private const string Folder = "Assets/__PvrpUploadTest";

        // A URP Lit material as it looks in a project without URP: the shader GUID resolves to nothing.
        private const string UrpMaterialYaml = @"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_Name: UrpLit
  m_Shader: {fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}
  m_ValidKeywords: []
  m_InvalidKeywords: []
  m_LightmapFlags: 4
  m_EnableInstancingVariants: 0
  m_DoubleSidedGI: 0
  m_CustomRenderQueue: -1
  stringTagMap:
    RenderType: Opaque
  disabledShaderPasses: []
  m_LockedProperties:
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs:
    - _BaseMap:
        m_Texture: {fileID: 0}
        m_Scale: {x: 1, y: 1}
        m_Offset: {x: 0, y: 0}
    m_Ints: []
    m_Floats:
    - _Metallic: 0.2
    - _Smoothness: 0.5
    - _Surface: 0
    - _WorkflowMode: 1
    m_Colors:
    - _BaseColor: {r: 1, g: 0.6, b: 0.6, a: 1}
  m_BuildTextureStacks: []
  m_AllowLocking: 1
";

        private Scene _previousActive;

        [SetUp]
        public void SetUp()
        {
            _previousActive = SceneManager.GetActiveScene();
            Directory.CreateDirectory(Folder);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.path.StartsWith(Folder) || s.path.StartsWith(ParelUploadRendering.WorldFolder)) EditorSceneManager.CloseScene(s, true);
            }
            if (_previousActive.IsValid() && _previousActive.isLoaded) SceneManager.SetActiveScene(_previousActive);
            ParelUploadRendering.FinishWorld();
            AssetDatabase.DeleteAsset(Folder);
        }

        private static Material UrpMaterial()
        {
            string path = Folder + "/UrpLit.mat";
            File.WriteAllText(path, UrpMaterialYaml);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        private static Material StandardMaterial()
        {
            var m = new Material(Shader.Find("Standard")) { color = Color.green };
            AssetDatabase.CreateAsset(m, Folder + "/Standard.mat");
            return m;
        }

        private static string SaveScene(string name, Material material)
        {
            // Unity can't add a new scene next to an unsaved "Untitled" one (a fresh project): replace that one instead,
            // when it holds no changes, and put an empty untitled scene back afterwards.
            Scene active = SceneManager.GetActiveScene();
            bool replaceUntitled = string.IsNullOrEmpty(active.path) && !active.isDirty && SceneManager.sceneCount == 1;
            if (string.IsNullOrEmpty(active.path) && !replaceUntitled) Assert.Ignore("An unsaved scene is open; save it to run this test.");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, replaceUntitled ? NewSceneMode.Single : NewSceneMode.Additive);
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.GetComponent<Renderer>().sharedMaterial = material;
            SceneManager.MoveGameObjectToScene(cube, scene);
            string path = Folder + "/" + name + ".unity";
            EditorSceneManager.SaveScene(scene, path);
            if (replaceUntitled) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            else EditorSceneManager.CloseScene(scene, true);
            return path;
        }

        [Test]
        public void A_world_with_a_urp_material_is_built_from_a_converted_copy_and_the_original_is_untouched()
        {
            Material urp = UrpMaterial();
            Assert.IsTrue(urp.shader == null || urp.shader.name == ShaderClassifier.ErrorShaderName, "URP isn't installed here: the shader is missing");
            string scenePath = SaveScene("UrpWorld", urp);
            byte[] sceneBefore = File.ReadAllBytes(scenePath);
            byte[] materialBefore = File.ReadAllBytes(Folder + "/UrpLit.mat");

            string[] built = ParelUploadRendering.PrepareWorld(new[] { scenePath });

            Assert.AreEqual(1, built.Length);
            StringAssert.StartsWith(ParelUploadRendering.WorldFolder + "/", built[0], "built from a copy");
            Scene copy = EditorSceneManager.OpenScene(built[0], OpenSceneMode.Additive);
            Renderer r = copy.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Renderer>(true)).Single();
            Material converted = r.sharedMaterial;
            Assert.AreNotEqual(urp, converted);
            Assert.AreEqual("ParelVR/Lit", converted.shader.name);
            Color c = converted.GetColor("_Color");
            Assert.AreEqual(1f, c.r, 0.02f, "the URP base colour came over");
            Assert.AreEqual(0.6f, c.g, 0.02f);
            Assert.AreEqual(0.2f, converted.GetFloat("_Metallic"), 0.01f);
            Assert.IsTrue(copy.GetRootGameObjects().Any(g => g.GetComponentInChildren<ParelWorldRenderingInfo>(true) != null), "load-time info added to the copy");
            EditorSceneManager.CloseScene(copy, true);

            CollectionAssert.AreEqual(sceneBefore, File.ReadAllBytes(scenePath), "the creator's scene is byte-for-byte the same");
            CollectionAssert.AreEqual(materialBefore, File.ReadAllBytes(Folder + "/UrpLit.mat"), "the creator's material is byte-for-byte the same");
            Assert.AreEqual(1, ParelUploadRendering.LastReports.Count);
            StringAssert.Contains("1 material(s) converted", ParelUploadRendering.LastSummary);

            ParelUploadRendering.FinishWorld();
            Assert.IsFalse(AssetDatabase.IsValidFolder(ParelUploadRendering.WorldFolder), "the build's copies are removed afterwards");
        }

        [Test]
        public void A_built_in_world_that_renders_is_built_as_it_is()
        {
            string scenePath = SaveScene("BuiltInWorld", StandardMaterial());
            string[] built = ParelUploadRendering.PrepareWorld(new[] { scenePath });
            Assert.AreEqual(scenePath, built[0], "no copy, no conversion");
            Assert.IsFalse(AssetDatabase.IsValidFolder(ParelUploadRendering.WorldFolder));
            Assert.AreEqual(0, ParelUploadRendering.LastReports.Count);
            Assert.IsNull(ParelUploadRendering.LastSummary);
        }

        [Test]
        public void An_avatar_copy_gets_parelvr_materials_and_a_built_in_one_is_left_alone()
        {
            Material urp = UrpMaterial();
            Material standard = StandardMaterial();
            var avatar = new GameObject("Avatar");
            try
            {
                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.transform.SetParent(avatar.transform);
                body.GetComponent<Renderer>().sharedMaterials = new[] { urp, standard };

                string note = ParelUploadRendering.ConvertAvatar(avatar, Folder + "/Generated");

                Material[] after = body.GetComponent<Renderer>().sharedMaterials;
                Assert.AreEqual("ParelVR/Lit", after[0].shader.name, "URP material converted");
                Assert.AreSame(standard, after[1], "Standard renders in ParelVR on PC: kept");
                StringAssert.Contains("Generated", AssetDatabase.GetAssetPath(after[0]));
                StringAssert.Contains("1 material(s) converted", note);

                var plain = new GameObject("Plain");
                GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(plain.transform);
                plain.GetComponentInChildren<Renderer>().sharedMaterial = standard;
                Assert.IsNull(ParelUploadRendering.ConvertAvatar(plain, Folder + "/Generated2"), "nothing to do for a Built-in avatar");
                Object.DestroyImmediate(plain);
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void Validation_reports_conversion_and_the_headset_budget()
        {
            Material urp = UrpMaterial();
            var report = new ParelVR.SDK.Core.Validation.ValidationReport();
            ParelRenderingValidation.AddMaterialIssues(report, new[] { urp, StandardMaterial() }, "test");
            Assert.IsTrue(report.Issues.Any(i => i.Message.Contains("will be converted for ParelVR at upload")));
            Assert.IsFalse(report.Issues.Any(i => i.Level == ParelVR.SDK.Core.Validation.ValidationIssueLevel.Error), "rendering never blocks an upload");

            var budget = new ParelVR.SDK.Core.Validation.ValidationReport();
            var root = new GameObject("Lights");
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var l = new GameObject("L" + i).AddComponent<Light>();
                    l.transform.SetParent(root.transform);
                    l.type = LightType.Point;
                    l.renderMode = LightRenderMode.ForcePixel;
                }
                ParelRenderingValidation.AddQuestBudgetIssues(budget, new[] { root }, false);
                Assert.IsTrue(budget.Issues.Any(i => i.Message.StartsWith("Meta / Standalone VR:") && i.Message.Contains("Important")));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
