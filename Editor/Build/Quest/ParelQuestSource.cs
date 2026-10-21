using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>What the creator's editor knew about each material, for the server that converts it.</summary>
    [Serializable]
    public sealed class ParelQuestMaterialHint
    {
        public string path;
        public string shader;
        public int queue;
        public string[] keywords;
        public bool particle;
        /// <summary>Why the shader can't run on a standalone headset, or empty.</summary>
        public string problem;
    }

    [Serializable]
    public sealed class ParelQuestHints
    {
        public int version = 1;
        /// <summary>"world" or "avatar".</summary>
        public string kind;
        /// <summary>The scene (world) or prefab (avatar) the bundle is built from.</summary>
        public string mainAsset;
        public string skybox;
        public string unityVersion;
        public string sdkVersion;
        public List<ParelQuestMaterialHint> materials = new List<ParelQuestMaterialHint>();

        public ParelQuestMaterialHint For(string materialPath) => materials.FirstOrDefault(m => m.path == materialPath);
    }

    public sealed class ParelQuestSourceResult
    {
        public string PackagePath;
        public long Size;
        public int AssetCount;
    }

    /// <summary>
    /// Packs what a world or avatar bundle is built from (the scene or prefab and every asset it uses, never code) into
    /// a .unitypackage. ParelVR's servers import it and build the Meta / Standalone VR version from it.
    /// </summary>
    public static class ParelQuestSource
    {
        public const string HintsFolder = "Assets/ParelVR_Quest_Source";
        public const string HintsPath = HintsFolder + "/quest-hints.json";
        public const string WorldKind = "world";
        public const string AvatarKind = "avatar";

        // Code and native plugins never leave the creator's computer: the game can't run them from a bundle anyway.
        private static readonly HashSet<string> CodeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".dll", ".asmdef", ".asmref", ".rsp", ".js", ".jslib", ".jspre", ".boo", ".so", ".dylib", ".a", ".aar", ".jar",
            ".bundle", ".exe", ".bat", ".cmd", ".ps1", ".sh", ".py", ".pdb", ".mdb", ".unitypackage", ".framework", ".plugin"
        };

        private static readonly HashSet<string> ShaderSourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".shader", ".cginc", ".hlsl", ".glslinc", ".cg", ".compute", ".orlshader", ".poiinc"
        };

        private static readonly Regex IncludeLine = new Regex(@"^\s*#\s*include(?:_with_pragmas)?\s+""([^""]+)""", RegexOptions.Multiline);

        /// <summary>
        /// Assets under Assets, and those of other packages (a shader or texture package the creator installed). Never
        /// code, nor this SDK's or Unity's own packages: the server has those.
        /// </summary>
        public static bool IsPackable(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(HintsFolder + "/", StringComparison.Ordinal)) return false;
            if (CodeExtensions.Contains(Path.GetExtension(path))) return false;
            if (path.StartsWith("Assets/", StringComparison.Ordinal)) return true;
            return path.StartsWith("Packages/", StringComparison.Ordinal) &&
                   !path.StartsWith("Packages/com.parelvrsdk.pvr/", StringComparison.Ordinal) &&
                   !path.StartsWith("Packages/com.unity.", StringComparison.Ordinal);
        }

        /// <summary>
        /// Writes the package for <paramref name="mainAssetPath"/> to <paramref name="outputFile"/>. <paramref name="contentRoots"/>
        /// are the objects the bundle shows (to tell which materials are particles); <paramref name="extraAssets"/> are
        /// assets the scene uses without the asset database listing them (lighting data).
        /// </summary>
        public static ParelQuestSourceResult Export(string kind, string mainAssetPath, string outputFile,
            IEnumerable<GameObject> contentRoots, Material skybox = null, IEnumerable<string> extraAssets = null)
        {
            if (string.IsNullOrEmpty(mainAssetPath)) throw new ArgumentNullException(nameof(mainAssetPath));

            var assets = new HashSet<string>(StringComparer.Ordinal) { mainAssetPath };
            foreach (string dependency in AssetDatabase.GetDependencies(mainAssetPath, true)) assets.Add(dependency);
            foreach (string extra in extraAssets ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(extra)) continue;
                assets.Add(extra);
                foreach (string dependency in AssetDatabase.GetDependencies(extra, true)) assets.Add(dependency);
            }
            string skyboxPath = skybox != null ? AssetDatabase.GetAssetPath(skybox) : null;
            if (!string.IsNullOrEmpty(skyboxPath))
            {
                assets.Add(skyboxPath);
                foreach (string dependency in AssetDatabase.GetDependencies(skyboxPath, true)) assets.Add(dependency);
            }

            // Shader include files are not asset dependencies, but the server needs them to compile the shaders it keeps.
            foreach (string shader in assets.Where(a => ShaderSourceExtensions.Contains(Path.GetExtension(a))).ToList())
                AddIncludes(shader, assets, 0);

            List<string> packable = assets.Where(IsPackable).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();

            ParelQuestHints hints = BuildHints(kind, mainAssetPath, skyboxPath, packable, contentRoots);
            try
            {
                if (!AssetDatabase.IsValidFolder(HintsFolder)) AssetDatabase.CreateFolder("Assets", Path.GetFileName(HintsFolder));
                File.WriteAllText(HintsPath, JsonUtility.ToJson(hints, true));
                AssetDatabase.ImportAsset(HintsPath, ImportAssetOptions.ForceSynchronousImport);
                packable.Add(HintsPath);

                string folder = Path.GetDirectoryName(Path.GetFullPath(outputFile));
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                if (File.Exists(outputFile)) File.Delete(outputFile);
                AssetDatabase.ExportPackage(packable.ToArray(), outputFile, ExportPackageOptions.Default);
                if (!File.Exists(outputFile) && packable.Any(p => p.StartsWith("Packages/", StringComparison.Ordinal)))
                {
                    // Some packages can't be exported from; the project's own assets still go.
                    packable.RemoveAll(p => p.StartsWith("Packages/", StringComparison.Ordinal));
                    AssetDatabase.ExportPackage(packable.ToArray(), outputFile, ExportPackageOptions.Default);
                }
                if (!File.Exists(outputFile)) throw new Exception("Unity didn't write the package for the Meta / Standalone VR version.");
                return new ParelQuestSourceResult
                {
                    PackagePath = outputFile,
                    Size = new FileInfo(outputFile).Length,
                    AssetCount = packable.Count,
                };
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(HintsFolder)) AssetDatabase.DeleteAsset(HintsFolder);
            }
        }

        private static ParelQuestHints BuildHints(string kind, string mainAssetPath, string skyboxPath, IEnumerable<string> assets, IEnumerable<GameObject> contentRoots)
        {
            var particleMaterials = new HashSet<Material>();
            foreach (GameObject root in contentRoots ?? Enumerable.Empty<GameObject>())
            {
                if (root == null) continue;
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is ParticleSystemRenderer) && !(renderer is TrailRenderer) && !(renderer is LineRenderer)) continue;
                    foreach (Material m in renderer.sharedMaterials) if (m != null) particleMaterials.Add(m);
                    if (renderer is ParticleSystemRenderer particles && particles.trailMaterial != null) particleMaterials.Add(particles.trailMaterial);
                }
            }

            var hints = new ParelQuestHints
            {
                kind = kind,
                mainAsset = mainAssetPath,
                skybox = skyboxPath ?? string.Empty,
                unityVersion = Application.unityVersion,
                sdkVersion = SdkVersion(),
            };
            foreach (string path in assets.Where(a => a.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) continue;
                hints.materials.Add(new ParelQuestMaterialHint
                {
                    path = path,
                    shader = material.shader != null ? material.shader.name : string.Empty,
                    queue = material.renderQueue,
                    keywords = material.shaderKeywords,
                    particle = particleMaterials.Contains(material),
                    problem = ParelQuestShaders.Problem(material.shader) ?? string.Empty,
                });
            }
            return hints;
        }

        /// <summary>The installed SDK's version (package.json).</summary>
        public static string SdkVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ParelQuestSource).Assembly);
            return info != null ? info.version : "0.0.0";
        }

        private static void AddIncludes(string path, HashSet<string> into, int depth)
        {
            if (depth > 16 || !File.Exists(path)) return;
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { return; }

            string directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
            foreach (Match match in IncludeLine.Matches(text))
            {
                string include = match.Groups[1].Value.Replace('\\', '/');
                string candidate = include.StartsWith("Assets/", StringComparison.Ordinal) || include.StartsWith("Packages/", StringComparison.Ordinal)
                    ? include
                    : ToProjectPath(Path.Combine(directory, include));
                if (candidate == null || !File.Exists(candidate)) continue;
                if (into.Add(candidate)) AddIncludes(candidate, into, depth + 1);
            }
        }

        /// <summary>"Assets/a/../b.cginc" as "Assets/b.cginc", or null when it leaves the project.</summary>
        private static string ToProjectPath(string path)
        {
            string root = Path.GetFullPath(Directory.GetCurrentDirectory()).Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(path).Replace('\\', '/');
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : null;
        }
    }
}
