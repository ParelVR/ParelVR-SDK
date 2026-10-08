using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ParelVR.SDK.Build;
using ParelVR.SDK.Core;
using ParelVR.Volt.Core;
using ParelVR.Volt.Core.Bytecode;
using ParelVR.Volt.Core.Runtime;
using ParelVR.Volt.Interaction;
using ParelVR.Volt.Networking;
using ParelVR.Volt.Objects;
using ParelVR.VoltHost;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace ParelVR.Volt.EditorTools
{
    using Scene = UnityEngine.SceneManagement.Scene;
    using VoltCapability = ParelVR.Volt.Core.VoltCapability;
    using VoltRuntime = ParelVR.Volt.Core.Runtime.VoltRuntime;
    using VoltProfiler = ParelVR.Volt.Core.Runtime.VoltProfiler;

    /// <summary>Per-project Volt settings, kept in ProjectSettings so they travel with the project.</summary>
    [FilePath("ProjectSettings/ParelVRVolt.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class VoltSettings : ScriptableSingleton<VoltSettings>
    {
        /// <summary>Capabilities the world asks for (VoltCapability bits).</summary>
        public uint capabilities = (uint)VoltCapabilities.DefaultGrant;
        /// <summary>Hosts scripts may reach with web requests and video.</summary>
        public string[] webDomains = Array.Empty<string>();
        public bool compileOnPlay = true;

        public void SaveNow() => Save(true);
    }

    [Serializable]
    public sealed class VoltDiagnostic
    {
        public string severity;
        public string code;
        public string message;
        public string suggestion;
        public string file;
        public int line;
        public int column;
    }

    [Serializable]
    public sealed class VoltCompileOutput
    {
        public bool success;
        public string compiler;
        public string module;
        public int moduleSize;
        public string moduleHash;
        public uint usedCapabilities;
        public VoltDiagnostic[] diagnostics = Array.Empty<VoltDiagnostic>();
        [NonSerialized] public byte[] bytes;
        [NonSerialized] public bool nothingToCompile;
    }

    /// <summary>Runs the VoltCS compiler over the project's world scripts.</summary>
    public static class VoltCompilerService
    {
        public static VoltCompileOutput Last { get; private set; }
        public static event Action<VoltCompileOutput> Compiled;

        public static string WorkFolder => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "ParelVR", "Volt"));
        public static string ModulePath => Path.Combine(WorkFolder, "world.voltm");

        private static string Dotnet()
        {
            string name = Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
            return Path.Combine(EditorApplication.applicationContentsPath, "NetCoreRuntime", name);
        }

        /// <summary>The compiler ships as a zip so Unity never imports its assemblies; it is unpacked once per version.</summary>
        private static string CompilerDll()
        {
            string zip = ParelPackagePaths.FullPath("Compiler/voltc.zip");
            if (!File.Exists(zip)) throw new FileNotFoundException("The VoltCS compiler is missing from the SDK (Compiler/voltc.zip).");
            string stamp = new FileInfo(zip).Length + "-" + File.GetLastWriteTimeUtc(zip).Ticks;
            string folder = Path.Combine(WorkFolder, "voltc");
            string marker = Path.Combine(folder, "unpacked.txt");
            if (!File.Exists(marker) || File.ReadAllText(marker) != stamp)
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Directory.CreateDirectory(folder);
                ZipFile.ExtractToDirectory(zip, folder);
                File.WriteAllText(marker, stamp);
            }
            return Path.Combine(folder, "voltc.dll");
        }

        public static bool ProjectHasScripts() => TypeCache.GetTypesDerivedFrom<VoltBehaviour>().Any(t => !t.IsAbstract);

        public static VoltCompileOutput Compile(bool logToConsole = true)
        {
            var output = new VoltCompileOutput();
            try
            {
                var scriptAssemblies = new HashSet<string>(TypeCache.GetTypesDerivedFrom<VoltBehaviour>().Select(t => t.Assembly.GetName().Name));
                if (scriptAssemblies.Count == 0)
                {
                    output.success = true;
                    output.nothingToCompile = true;
                    if (File.Exists(ModulePath)) File.Delete(ModulePath);
                    return Finish(output, logToConsole);
                }

                var sources = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var references = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var defines = new SortedSet<string>(StringComparer.Ordinal);
                // The player's view of the code: what is inside #if UNITY_EDITOR never reaches a world.
                foreach (UnityEditor.Compilation.Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
                {
                    if (!scriptAssemblies.Contains(assembly.name)) continue;
                    foreach (string source in assembly.sourceFiles) sources.Add(Path.GetFullPath(source));
                    foreach (string reference in assembly.allReferences) references.Add(Path.GetFullPath(reference));
                    foreach (string define in assembly.defines) defines.Add(define);
                }
                string data = EditorApplication.applicationContentsPath;
                references.Add(Path.Combine(data, "NetStandard", "ref", "2.1.0", "netstandard.dll"));

                Directory.CreateDirectory(WorkFolder);
                string json = Path.Combine(WorkFolder, "compile.json");
                string response = Path.Combine(WorkFolder, "compile.rsp");
                var lines = new List<string>
                {
                    "--registry", ParelPackagePaths.FullPath("API/volt-api.json"),
                    "--out", ModulePath, "--json", json, "--name", Application.productName,
                    "--grant", VoltSettings.instance.capabilities.ToString(),
                    "--ref-dir", Path.Combine(data, "NetStandard", "compat", "2.1.0", "shims", "netstandard"),
                    "--ref-dir", Path.Combine(data, "NetStandard", "compat", "2.1.0", "shims", "netfx")
                };
                foreach (string reference in references) { lines.Add("--ref"); lines.Add(reference); }
                foreach (string define in defines) { lines.Add("--define"); lines.Add(define); }
                lines.AddRange(sources);
                File.WriteAllLines(response, lines);
                if (File.Exists(json)) File.Delete(json);

                var start = new System.Diagnostics.ProcessStartInfo(Dotnet(), "\"" + CompilerDll() + "\" compile \"@" + response + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = WorkFolder
                };
                // Roll forward to whatever runtime the editor ships, whatever the user has installed.
                start.EnvironmentVariables["DOTNET_ROLL_FORWARD"] = "LatestMajor";
                string stdout, stderr;
                using (var process = System.Diagnostics.Process.Start(start))
                {
                    var errorTask = process.StandardError.ReadToEndAsync();
                    stdout = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    stderr = errorTask.Result;
                    if (process.ExitCode == 2 || !File.Exists(json))
                        throw new Exception("The VoltCS compiler could not run:\n" + stderr + stdout);
                }
                EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(json), output);
                if (output.diagnostics == null) output.diagnostics = Array.Empty<VoltDiagnostic>();
                if (output.success) output.bytes = File.ReadAllBytes(ModulePath);
            }
            catch (Exception e)
            {
                output.success = false;
                output.diagnostics = new[] { new VoltDiagnostic { severity = "error", code = "VLT0900", message = e.Message } };
            }
            return Finish(output, logToConsole);
        }

        private static VoltCompileOutput Finish(VoltCompileOutput output, bool log)
        {
            Last = output;
            if (log)
            {
                foreach (VoltDiagnostic d in output.diagnostics)
                {
                    string where = string.IsNullOrEmpty(d.file) ? string.Empty : d.file.Replace('\\', '/') + "(" + d.line + "," + d.column + "): ";
                    string text = where + d.severity + " " + d.code + ": " + d.message + (string.IsNullOrEmpty(d.suggestion) ? string.Empty : " " + d.suggestion);
                    UnityEngine.Object context = Script(d.file);
                    if (d.severity == "error") Debug.LogError(text, context);
                    else Debug.LogWarning(text, context);
                }
                if (output.success && !output.nothingToCompile) Debug.Log("[Volt] Compiled the world scripts: " + output.moduleSize + " bytes.");
            }
            Compiled?.Invoke(output);
            return output;
        }

        public static UnityEngine.Object Script(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/') + "/";
            string path = file.Replace('\\', '/');
            if (path.StartsWith(project, StringComparison.OrdinalIgnoreCase)) path = path.Substring(project.Length);
            return AssetDatabase.LoadAssetAtPath<MonoScript>(path);
        }
    }

    /// <summary>
    /// Turns a scene with creators' script components into what a world really is: every script component is
    /// replaced by a VoltBehaviourHost that names its class and carries its inspector values. Works on the copy
    /// of the scene that is built or played, never on the creator's own scene.
    /// </summary>
    public static class VoltSceneProcessor
    {
        public sealed class Result
        {
            public int Behaviours;
            public readonly List<(int NetId, int Group, string Class, string Path)> Entries = new List<(int, int, string, string)>();
            public readonly List<string> Warnings = new List<string>();
        }

        private sealed class Context
        {
            public VoltModule Module;
            public VoltBindings Bindings;
            public Dictionary<VoltBehaviour, VoltBehaviourHost> Hosts = new Dictionary<VoltBehaviour, VoltBehaviourHost>();
            public Dictionary<GameObject, GameObject> Templates = new Dictionary<GameObject, GameObject>();
            public Transform Root;
            public Result Result = new Result();
        }

        public static bool HasScripts(Scene scene) =>
            scene.GetRootGameObjects().Any(root => root.GetComponentInChildren<VoltBehaviour>(true) != null);

        public static Result Process(Scene scene, byte[] moduleBytes, bool playMode)
        {
            var context = new Context { Module = VoltModuleReader.Read(moduleBytes), Bindings = new VoltBindings() };
            VoltUnityDispatch.Register(context.Bindings);

            var rootObject = new GameObject("[Volt Scene]");
            SceneManager.MoveGameObjectToScene(rootObject, scene);
            context.Root = rootObject.transform;
            var data = rootObject.AddComponent<VoltWorldScene>();
            data.sdkVersion = VoltVersion.Sdk;
            using (SHA256 sha = SHA256.Create()) data.moduleSha256 = Hex(sha.ComputeHash(moduleBytes));
            if (playMode)
            {
                data.editorModule = moduleBytes;
                data.editorCapabilities = VoltSettings.instance.capabilities;
                data.editorWebDomains = VoltSettings.instance.webDomains ?? Array.Empty<string>();
            }

            // Scene order: the same on every machine that builds this scene, which makes the ids stable.
            var proxies = new List<VoltBehaviour>();
            foreach (GameObject root in scene.GetRootGameObjects())
                if (root != rootObject) proxies.AddRange(root.GetComponentsInChildren<VoltBehaviour>(true));

            var groups = new Dictionary<GameObject, int>();
            int Group(GameObject go)
            {
                if (!groups.TryGetValue(go, out int id)) groups.Add(go, id = groups.Count + 1);
                return id;
            }

            var hosts = new List<VoltBehaviourHost>();
            int netId = 0;
            foreach (VoltBehaviour proxy in proxies)
            {
                VoltBehaviourHost host = MakeHost(context, proxy);
                if (host == null) continue;
                host.netId = ++netId;
                host.ownerGroup = Group(host.gameObject);
                host.persistentId = PathOf(host.transform);
                hosts.Add(host);
                context.Result.Entries.Add((host.netId, host.ownerGroup, host.className, host.persistentId));
            }
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Component c in root.GetComponentsInChildren<VoltObjectSync>(true)) Group(c.gameObject);
                foreach (Component c in root.GetComponentsInChildren<VoltPickup>(true)) Group(c.gameObject);
                foreach (Component c in root.GetComponentsInChildren<VoltStation>(true)) Group(c.gameObject);
            }

            // Values second: a field may refer to a behaviour that comes later in the scene.
            foreach (VoltBehaviour proxy in proxies)
                if (context.Hosts.TryGetValue(proxy, out VoltBehaviourHost host)) host.fields = Fields(context, proxy);
            // Templates were added while reading fields; their scripts are prepared the same way but are not networked.
            var done = new HashSet<GameObject>();
            while (context.Templates.Values.Any(t => done.Add(t)))
            {
                foreach (GameObject template in context.Templates.Values.ToArray())
                {
                    var inside = template.GetComponentsInChildren<VoltBehaviour>(true);
                    foreach (VoltBehaviour proxy in inside) MakeHost(context, proxy);
                    foreach (VoltBehaviour proxy in inside)
                        if (context.Hosts.TryGetValue(proxy, out VoltBehaviourHost host)) host.fields = Fields(context, proxy);
                }
            }

            Rewire(scene, context);
            foreach (VoltBehaviour proxy in context.Hosts.Keys.ToArray())
                if (proxy != null) UnityEngine.Object.DestroyImmediate(proxy, true);

            data.behaviours = hosts.ToArray();
            data.groups = groups.Select(g => new VoltSceneGroup { target = g.Key, ownerGroup = g.Value }).ToArray();
            data.templates = context.Templates.Values.ToArray();
            context.Result.Behaviours = hosts.Count;
            foreach (string warning in context.Result.Warnings) Debug.LogWarning("[Volt] " + warning);
            return context.Result;
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static string PathOf(Transform t)
        {
            string path = t.name;
            for (t = t.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }

        private static VoltBehaviourHost MakeHost(Context context, VoltBehaviour proxy)
        {
            if (context.Hosts.ContainsKey(proxy)) return null;
            Component component = proxy; // the script's own gameObject is an API stand-in; Unity's is on the base type
            string className = proxy.GetType().FullName;
            int index = context.Module.FindClass(className);
            if (index < 0 || !context.Module.Classes[index].IsBehaviour || context.Module.Classes[index].IsAbstract)
            {
                context.Result.Warnings.Add("'" + className + "' on " + PathOf(component.transform) + " is not a class of the compiled scripts; the component was removed.");
                context.Hosts[proxy] = null;
                return null;
            }
            var host = component.gameObject.AddComponent<VoltBehaviourHost>();
            host.className = className;
            host.enabled = ((Behaviour)proxy).enabled;
            context.Hosts[proxy] = host;
            return host;
        }

        private static VoltFieldValue[] Fields(Context context, VoltBehaviour proxy)
        {
            VoltModule m = context.Module;
            var values = new List<VoltFieldValue>();
            Type type = proxy.GetType();
            var chain = new List<int>();
            for (int c = m.FindClass(type.FullName); c >= 0; c = m.Classes[c].BaseClass) chain.Add(c);
            foreach (VoltFieldDef field in m.Fields)
            {
                if (!field.IsSerialized || !chain.Contains(field.Class)) continue;
                string name = m.GetString(field.Name);
                FieldInfo info = null;
                for (Type t = type; t != null && info == null; t = t.BaseType)
                    info = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (info == null) continue;
                try
                {
                    values.Add(Value(context, name, m.Types[field.Type], info.GetValue(proxy)));
                }
                catch (Exception e)
                {
                    context.Result.Warnings.Add("The value of " + type.Name + "." + name + " could not be stored (" + e.Message + ").");
                }
            }
            return values.ToArray();
        }

        private static VoltFieldValue Value(Context context, string name, VoltTypeRef type, object value)
        {
            var ints = new List<long>();
            var numbers = new List<double>();
            var strings = new List<string>();
            var objects = new List<UnityEngine.Object>();
            var result = new VoltFieldValue { name = name };

            void One(VoltTypeRef t, object v)
            {
                switch (t.Kind)
                {
                    case VoltTypeKind.Bool: ints.Add(v is bool b && b ? 1 : 0); break;
                    case VoltTypeKind.Char: ints.Add(v is char ch ? ch : 0); break;
                    case VoltTypeKind.U64: ints.Add(unchecked((long)Convert.ToUInt64(v ?? 0UL))); break;
                    case VoltTypeKind.I8: case VoltTypeKind.U8: case VoltTypeKind.I16: case VoltTypeKind.U16: case VoltTypeKind.I32:
                    case VoltTypeKind.U32: case VoltTypeKind.I64: case VoltTypeKind.Enum: case VoltTypeKind.ApiEnum:
                        ints.Add(v == null ? 0 : Convert.ToInt64(v is Enum ? Convert.ChangeType(v, Enum.GetUnderlyingType(v.GetType())) : v)); break;
                    case VoltTypeKind.F32: case VoltTypeKind.F64: numbers.Add(v == null ? 0 : Convert.ToDouble(v)); break;
                    case VoltTypeKind.String: strings.Add(v as string ?? string.Empty); break;
                    case VoltTypeKind.Vec2: { var x = v is Vector2 a ? a : default; numbers.Add(x.x); numbers.Add(x.y); break; }
                    case VoltTypeKind.Vec3: { var x = v is Vector3 a ? a : default; numbers.Add(x.x); numbers.Add(x.y); numbers.Add(x.z); break; }
                    case VoltTypeKind.Vec4: { var x = v is Vector4 a ? a : default; numbers.Add(x.x); numbers.Add(x.y); numbers.Add(x.z); numbers.Add(x.w); break; }
                    case VoltTypeKind.Quat: { var x = v is Quaternion a ? a : Quaternion.identity; numbers.Add(x.x); numbers.Add(x.y); numbers.Add(x.z); numbers.Add(x.w); break; }
                    case VoltTypeKind.Color: { var x = v is Color a ? a : default; numbers.Add(x.r); numbers.Add(x.g); numbers.Add(x.b); numbers.Add(x.a); break; }
                    case VoltTypeKind.Class:
                    case VoltTypeKind.Object:
                    {
                        var other = v as VoltBehaviour;
                        VoltBehaviourHost host = null;
                        if (other != null) context.Hosts.TryGetValue(Live(context, other), out host);
                        objects.Add(host);
                        break;
                    }
                    case VoltTypeKind.Api: objects.Add(Target(context, unchecked((uint)t.A), v)); break;
                    default: throw new NotSupportedException("a field of this type cannot be set in the inspector");
                }
            }

            if (type.Kind == VoltTypeKind.Array)
            {
                result.isArray = true;
                VoltTypeRef element = context.Module.Types[type.A];
                if (value is Array array)
                {
                    result.count = array.Length;
                    foreach (object item in array) One(element, item);
                }
            }
            else One(type, value);

            result.integers = ints.ToArray();
            result.numbers = numbers.ToArray();
            result.strings = strings.ToArray();
            result.objects = objects.ToArray();
            return result;
        }

        /// <summary>A behaviour inside a prefab is reached through the prefab's copy in the scene.</summary>
        private static VoltBehaviour Live(Context context, VoltBehaviour proxy)
        {
            Component component = proxy;
            if (component == null || component.gameObject.scene.IsValid()) return proxy;
            GameObject template = Template(context, component.transform.root.gameObject);
            return template.GetComponentsInChildren(proxy.GetType(), true).FirstOrDefault() as VoltBehaviour ?? proxy;
        }

        private static GameObject Template(Context context, GameObject prefab)
        {
            if (context.Templates.TryGetValue(prefab, out GameObject template)) return template;
            template = UnityEngine.Object.Instantiate(prefab, context.Root);
            template.name = prefab.name;
            template.SetActive(false);
            context.Templates.Add(prefab, template);
            return template;
        }

        /// <summary>The Unity object an API-typed field stands for, as the type the runtime expects.</summary>
        private static UnityEngine.Object Target(Context context, uint apiTypeId, object value)
        {
            UnityEngine.Object target = value is VoltObject wrapper ? wrapper.EditorTarget : value as UnityEngine.Object;
            if (target == null) return null;
            GameObject go = target as GameObject ?? (target as Component)?.gameObject;
            if (go != null && !go.scene.IsValid())
            {
                // A prefab: scripts get a prepared copy that lives in the scene.
                GameObject template = Template(context, go.transform.root.gameObject);
                if (go.transform.root != go.transform) context.Result.Warnings.Add("A field refers to an object inside the prefab '" + template.name + "'; the prefab itself is used.");
                target = target is GameObject ? (UnityEngine.Object)template : template.GetComponent(target.GetType());
                go = template;
            }
            if (!context.Bindings.TryGetHostType(apiTypeId, out Type hostType) || hostType.IsInstanceOfType(target)) return target;
            if (go == null) return null;
            if (hostType == typeof(GameObject)) return go;
            return typeof(Component).IsAssignableFrom(hostType) ? go.GetComponent(hostType) : null;
        }

        /// <summary>Buttons and other UnityEvents that called a script method now call the host, which runs the method in the VM.</summary>
        private static void Rewire(Scene scene, Context context)
        {
            string hostType = typeof(VoltBehaviourHost).AssemblyQualifiedName;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is VoltBehaviour || component is VoltBehaviourHost || component is Transform) continue;
                    var serialized = new SerializedObject(component);
                    SerializedProperty property = serialized.GetIterator();
                    bool changed = false;
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.Generic || property.type != "PersistentCall") continue;
                        SerializedProperty target = property.FindPropertyRelative("m_Target");
                        var proxy = target != null ? target.objectReferenceValue as VoltBehaviour : null;
                        if (proxy == null || !context.Hosts.TryGetValue(proxy, out VoltBehaviourHost host) || host == null) continue;
                        string method = property.FindPropertyRelative("m_MethodName").stringValue;
                        target.objectReferenceValue = host;
                        property.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = hostType;
                        property.FindPropertyRelative("m_MethodName").stringValue = nameof(VoltBehaviourHost.SendCustomEvent);
                        property.FindPropertyRelative("m_Mode").intValue = 5; // a string argument
                        property.FindPropertyRelative("m_Arguments.m_StringArgument").stringValue = method;
                        changed = true;
                    }
                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }
    }

    /// <summary>Play mode: compile, then let the scene that is about to play be prepared like a built world.</summary>
    [InitializeOnLoad]
    public sealed class VoltPlayMode : IProcessSceneWithReport
    {
        static VoltPlayMode()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            WorldBuilder.PrepareScene = VoltWorldBuild.Prepare;
            WorldBuilder.Finished = VoltWorldBuild.Finished;
            WorldBuilder.AfterBundleUploaded = VoltWorldBuild.Upload;
        }

        public int callbackOrder => 0;

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode || !VoltSettings.instance.compileOnPlay) return;
            if (!VoltSceneProcessor.HasScripts(SceneManager.GetActiveScene())) return;
            VoltCompileOutput output = VoltCompilerService.Compile();
            if (output.success) return;
            EditorApplication.isPlaying = false;
            Debug.LogError("[Volt] The world scripts have errors; fix them before entering play mode (ParelVR SDK > Volt > Console).");
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer) return;
            if (!VoltSceneProcessor.HasScripts(scene) || !File.Exists(VoltCompilerService.ModulePath)) return;
            VoltSceneProcessor.Process(scene, File.ReadAllBytes(VoltCompilerService.ModulePath), true);
        }
    }

    /// <summary>Building and uploading a world with scripts: the bundle gets a prepared copy of the scene, the scripts travel as a Volt package.</summary>
    public static class VoltWorldBuild
    {
        private const string TempFolder = "Assets/ParelVR_Volt_Build";
        private static byte[] _module;
        private static VoltSceneProcessor.Result _scene;

        public static string LastPackagePath { get; private set; }

        public static string Prepare(string scenePath)
        {
            _module = null;
            _scene = null;
            if (!VoltSceneProcessor.HasScripts(SceneManager.GetActiveScene()))
            {
                if (SceneManager.GetActiveScene().GetRootGameObjects().Any(root =>
                        root.GetComponentInChildren<ParelVR.Volt.Interaction.VoltPickup>(true) != null ||
                        root.GetComponentInChildren<ParelVR.Volt.Interaction.VoltInteractable>(true) != null))
                    Debug.LogWarning("[Volt] The scene has pickups or interactables but no VoltBehaviour script. They only work in a world built with scripts: add a VoltBehaviour to an object in the scene.");
                return scenePath;
            }
            VoltCompileOutput output = VoltCompilerService.Compile();
            if (!output.success || output.bytes == null) throw new Exception("The world scripts have errors. See the Console.");

            if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "ParelVR_Volt_Build");
            string copyPath = TempFolder + "/" + Path.GetFileName(scenePath);
            AssetDatabase.DeleteAsset(copyPath);
            if (!AssetDatabase.CopyAsset(scenePath, copyPath)) throw new Exception("Could not copy the scene for building.");
            Scene copy = EditorSceneManager.OpenScene(copyPath, OpenSceneMode.Additive);
            try
            {
                _scene = VoltSceneProcessor.Process(copy, output.bytes, false);
                EditorSceneManager.SaveScene(copy);
            }
            finally
            {
                EditorSceneManager.CloseScene(copy, true);
            }
            _module = output.bytes;
            return copyPath;
        }

        public static void Finished(string bundlePath)
        {
            if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
            LastPackagePath = null;
            if (bundlePath == null || _module == null) return;
            LastPackagePath = bundlePath + ".voltpkg";
            File.WriteAllBytes(LastPackagePath, Package(File.ReadAllBytes(bundlePath), "windows"));
        }

        private static string Sha(byte[] data)
        {
            using (SHA256 sha = SHA256.Create()) return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        private static string Quote(string s) => "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        /// <summary>The .voltpkg container: see the world package section of the documentation.</summary>
        public static byte[] Package(byte[] bundle, string platform)
        {
            VoltSettings settings = VoltSettings.instance;
            var json = new StringBuilder();
            json.Append("{\"packageVersion\":1,\"sdkVersion\":").Append(Quote(VoltVersion.Sdk))
                .Append(",\"compiler\":").Append(Quote("voltc " + VoltVersion.Sdk))
                .Append(",\"apiVersion\":\"1.0\",\"bytecodeVersion\":").Append(Quote(VoltVersion.BytecodeMajor + "." + VoltVersion.BytecodeMinor))
                .Append(",\"platform\":").Append(Quote(platform))
                .Append(",\"bundleSha256\":").Append(Quote(Sha(bundle)))
                .Append(",\"moduleSha256\":").Append(Quote(Sha(_module)))
                .Append(",\"capabilities\":").Append(settings.capabilities)
                .Append(",\"webDomains\":[").Append(string.Join(",", (settings.webDomains ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Quote(d.Trim().ToLowerInvariant())))).Append("]")
                .Append(",\"behaviours\":[");
            for (int i = 0; _scene != null && i < _scene.Entries.Count; i++)
            {
                var e = _scene.Entries[i];
                if (i > 0) json.Append(',');
                json.Append("{\"netId\":").Append(e.NetId).Append(",\"ownerGroup\":").Append(e.Group).Append(",\"class\":").Append(Quote(e.Class)).Append(",\"path\":").Append(Quote(e.Path)).Append('}');
            }
            json.Append("]}");

            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Encoding.ASCII.GetBytes("VPKG"));
                w.Write((ushort)1);
                w.Write((ushort)2);
                void Entry(string name, byte[] data)
                {
                    w.Write((byte)name.Length);
                    w.Write(Encoding.ASCII.GetBytes(name));
                    w.Write((uint)data.Length);
                    w.Write(data);
                }
                Entry("manifest.json", Encoding.UTF8.GetBytes(json.ToString()));
                Entry("module.voltm", _module);
                w.Flush();
                return stream.ToArray();
            }
        }

        [Serializable]
        private sealed class UploadResponse
        {
            public bool ok;
            public string error;
            public string code;
            public string message;
        }

        public static async Task Upload(string worldId, string platform, string bundlePath, CancellationToken ct)
        {
            if (_module == null || LastPackagePath == null || !File.Exists(LastPackagePath)) return;
            byte[] package = File.ReadAllBytes(LastPackagePath);
            var response = await ParelVR.SDK.Core.Http.ParelApiClient.PutBytesAsync<UploadResponse>(
                "/api/parelvr/worlds/" + worldId + "/volt?platform=" + platform, package, "application/octet-stream", null, ct);
            if (response == null || !response.ok)
                throw new Exception("The world was uploaded, but ParelVR did not accept its scripts: " + (response?.message ?? response?.error ?? "no answer") +
                                    (string.IsNullOrEmpty(response?.code) ? string.Empty : " (" + response.code + ")"));
        }
    }
}
