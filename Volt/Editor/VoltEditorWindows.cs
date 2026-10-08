using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ParelVR.SDK.Build;
using ParelVR.SDK.Core;
using ParelVR.Volt.Core;
using ParelVR.Volt.Core.Runtime;
using ParelVR.Volt.Objects;
using ParelVR.Volt.Simulator;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Volt.EditorTools
{
    using Scene = UnityEngine.SceneManagement.Scene;
    using VoltCapability = ParelVR.Volt.Core.VoltCapability;
    using VoltRuntime = ParelVR.Volt.Core.Runtime.VoltRuntime;
    using VoltProfiler = ParelVR.Volt.Core.Runtime.VoltProfiler;

    public static class VoltMenu
    {
        private const string Template = @"using ParelVR.Volt;
using UnityEngine;

public class #SCRIPTNAME# : VoltBehaviour
{
    public override void OnStart()
    {
        VoltDebug.Log(""Hello from #SCRIPTNAME#"");
    }

    public override void OnInteract()
    {
    }
}
";

        [MenuItem("ParelVR SDK/Volt/Create Volt Script", priority = 10)]
        [MenuItem("Assets/Create/ParelVR/Volt Script", priority = 80)]
        private static void CreateScript()
        {
            string folder = Path.Combine(VoltCompilerService.WorkFolder, "templates");
            Directory.CreateDirectory(folder);
            string template = Path.Combine(folder, "VoltScript.cs.txt");
            File.WriteAllText(template, Template);
            ProjectWindowUtil.CreateScriptAssetFromTemplateFile(template, "NewVoltScript.cs");
        }

        [MenuItem("ParelVR SDK/Volt/Compile", priority = 20)]
        public static void Compile()
        {
            VoltCompileOutput output = VoltCompilerService.Compile();
            if (output.nothingToCompile) Debug.Log("[Volt] This project has no world scripts yet (ParelVR SDK > Volt > Create Volt Script).");
            VoltConsoleWindow.Open();
        }

        [MenuItem("ParelVR SDK/Volt/Validate World", priority = 21)]
        public static void Validate()
        {
            Scene scene = SceneManager.GetActiveScene();
            VoltCompileOutput output = VoltCompilerService.Compile();
            if (!output.success) { VoltConsoleWindow.Open(); return; }
            int scripts = scene.GetRootGameObjects().Sum(r => r.GetComponentsInChildren<VoltBehaviour>(true).Length);
            var caps = (VoltCapability)output.usedCapabilities & VoltCapabilities.ResourceMask;
            var missing = caps & ~(VoltCapability)VoltSettings.instance.capabilities;
            bool web = (caps & (VoltCapability.Web | VoltCapability.Video)) != 0 && (VoltSettings.instance.webDomains == null || VoltSettings.instance.webDomains.Length == 0);
            string text = scripts + " script component(s) in '" + scene.name + "', module " + output.moduleSize + " bytes.\nCapabilities used: " + caps;
            if (missing != 0) text += "\n\nNot enabled in the Volt settings: " + missing;
            if (web) text += "\n\nThe scripts use web or video but no web domain is listed in the Volt settings.";
            EditorUtility.DisplayDialog("Volt: Validate World", text, "OK");
        }

        [MenuItem("ParelVR SDK/Volt/Build World", priority = 40)]
        public static void Build()
        {
            try
            {
                string bundle = WorldBuilder.BuildWorldBundle();
                Debug.Log("[Volt] Built " + bundle + (VoltWorldBuild.LastPackagePath != null ? " and " + VoltWorldBuild.LastPackagePath : " (the scene has no scripts)"));
                EditorUtility.RevealInFinder(bundle);
            }
            catch (Exception e)
            {
                Debug.LogError("[Volt] Build failed: " + e.Message);
            }
        }

        [MenuItem("ParelVR SDK/Volt/Build && Test", priority = 41)]
        public static void BuildAndTest()
        {
            if (EditorApplication.isPlaying) return;
            if (!VoltCompilerService.Compile().success) { VoltConsoleWindow.Open(); return; }
            EditorApplication.isPlaying = true;
        }

        [MenuItem("ParelVR SDK/Volt/Build && Upload", priority = 42)]
        public static void BuildAndUpload()
        {
            // Uploading needs the signed-in session and the world's details, which live in the control panel's Worlds tab.
            Type panel = Type.GetType("ParelVR.SDK.Core.ControlPanel.ParelControlPanel, ParelVR.SDK.Core");
            if (panel != null) EditorWindow.GetWindow(panel).Show();
            EditorUtility.DisplayDialog("Volt: Build & Upload", "Open the Worlds tab of the ParelVR control panel and press Publish. The scripts are compiled, built and uploaded together with the world.", "OK");
        }

        [MenuItem("ParelVR SDK/Volt/API Explorer", priority = 60)] private static void Api() => EditorWindow.GetWindow<VoltApiWindow>("Volt API").Show();
        [MenuItem("ParelVR SDK/Volt/Console", priority = 61)] private static void Console() => VoltConsoleWindow.Open();
        [MenuItem("ParelVR SDK/Volt/Debugger", priority = 62)] private static void Debugger() => EditorWindow.GetWindow<VoltDebuggerWindow>("Volt Debugger").Show();
        [MenuItem("ParelVR SDK/Volt/Profiler", priority = 63)] private static void Profiler() => EditorWindow.GetWindow<VoltProfilerWindow>("Volt Profiler").Show();
        [MenuItem("ParelVR SDK/Volt/Settings", priority = 80)] private static void Settings() => EditorWindow.GetWindow<VoltSettingsWindow>("Volt Settings").Show();
    }

    /// <summary>Compile diagnostics and what the scripts log while playing.</summary>
    public sealed class VoltConsoleWindow : EditorWindow
    {
        private static readonly List<(int Level, string Text, UnityEngine.Object Context)> Lines = new List<(int, string, UnityEngine.Object)>();
        private Vector2 _scroll;

        public static void Open() => GetWindow<VoltConsoleWindow>("Volt Console").Show();

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            VoltSimulator.Started += () =>
            {
                Lines.Clear();
                VoltSimulator.World.Logged += (level, text, context) => Add((int)level, text, context);
                VoltSimulator.World.ErrorReported += report => Add(2, report.ToString(), null);
            };
        }

        private static void Add(int level, string text, UnityEngine.Object context)
        {
            if (Lines.Count > 2000) Lines.RemoveRange(0, 500);
            Lines.Add((level, text, context));
        }

        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Compile", EditorStyles.toolbarButton, GUILayout.Width(70))) VoltCompilerService.Compile();
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50))) Lines.Clear();
                GUILayout.FlexibleSpace();
                VoltCompileOutput last = VoltCompilerService.Last;
                GUILayout.Label(last == null ? "Not compiled yet" : last.nothingToCompile ? "No scripts" : last.success ? "Compiled, " + last.moduleSize + " bytes" : "Errors", EditorStyles.miniLabel);
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            VoltCompileOutput output = VoltCompilerService.Last;
            if (output != null)
            {
                foreach (VoltDiagnostic d in output.diagnostics)
                {
                    string where = string.IsNullOrEmpty(d.file) ? string.Empty : Path.GetFileName(d.file) + "(" + d.line + "): ";
                    MessageType type = d.severity == "error" ? MessageType.Error : MessageType.Warning;
                    EditorGUILayout.HelpBox(where + d.code + ": " + d.message + (string.IsNullOrEmpty(d.suggestion) ? string.Empty : "\n" + d.suggestion), type);
                    Rect rect = GUILayoutUtility.GetLastRect();
                    if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && rect.Contains(Event.current.mousePosition))
                        AssetDatabase.OpenAsset(VoltCompilerService.Script(d.file), d.line);
                }
            }
            foreach (var line in Lines)
            {
                GUI.color = line.Level == 2 ? new Color(1f, 0.6f, 0.6f) : line.Level == 1 ? new Color(1f, 0.9f, 0.5f) : Color.white;
                if (GUILayout.Button(line.Text, EditorStyles.label) && line.Context != null) EditorGUIUtility.PingObject(line.Context);
            }
            GUI.color = Color.white;
            EditorGUILayout.EndScrollView();
        }
    }

    /// <summary>Everything a script can use, read from the registry this SDK was built with.</summary>
    public sealed class VoltApiWindow : EditorWindow
    {
        [Serializable] private sealed class Param { public string name; public string type; public string mod; }
        [Serializable] private sealed class Member { public string type; public string name; public string kind; public string returns; public string doc; public Param[] @params; public bool @static; }
        [Serializable] private sealed class ApiType { public string name; public string kind; public string doc; }
        [Serializable] private sealed class Registry { public ApiType[] types; public Member[] members; }

        private Registry _registry;
        private string _search = string.Empty;
        private string _selected;
        private Vector2 _left, _right;

        private void OnEnable()
        {
            string path = ParelPackagePaths.FullPath("API/volt-api.json");
            _registry = File.Exists(path) ? JsonUtility.FromJson<Registry>(File.ReadAllText(path)) : new Registry { types = new ApiType[0], members = new Member[0] };
        }

        private static string Short(string type) => string.IsNullOrEmpty(type) ? "void" : type.Substring(type.LastIndexOf('.') + 1);

        private void OnGUI()
        {
            _search = EditorGUILayout.TextField("Search", _search);
            using (new EditorGUILayout.HorizontalScope())
            {
                _left = EditorGUILayout.BeginScrollView(_left, GUILayout.Width(260));
                foreach (ApiType type in _registry.types.OrderBy(t => t.name))
                {
                    bool match = _search.Length == 0 || type.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0
                        || _registry.members.Any(m => m.type == type.name && m.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!match) continue;
                    if (GUILayout.Button(Short(type.name), _selected == type.name ? EditorStyles.boldLabel : EditorStyles.label)) _selected = type.name;
                }
                EditorGUILayout.EndScrollView();
                _right = EditorGUILayout.BeginScrollView(_right);
                ApiType selected = _registry.types.FirstOrDefault(t => t.name == _selected);
                if (selected != null)
                {
                    EditorGUILayout.LabelField(selected.name, EditorStyles.boldLabel);
                    if (!string.IsNullOrEmpty(selected.doc)) EditorGUILayout.LabelField(selected.doc, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.Space();
                    foreach (Member m in _registry.members.Where(m => m.type == _selected).OrderBy(m => m.name))
                    {
                        if (m.kind == "PropertySet") continue;
                        string signature = (m.@static ? "static " : string.Empty) + Short(m.returns) + " " + m.name;
                        if (m.kind != "PropertyGet" && m.kind != "FieldGet")
                            signature += "(" + string.Join(", ", (m.@params ?? new Param[0]).Select(p => (string.IsNullOrEmpty(p.mod) ? string.Empty : p.mod + " ") + Short(p.type) + " " + p.name)) + ")";
                        EditorGUILayout.SelectableLabel(signature, EditorStyles.boldLabel, GUILayout.Height(18));
                        if (!string.IsNullOrEmpty(m.doc)) EditorGUILayout.LabelField(m.doc, EditorStyles.wordWrappedMiniLabel);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }
    }

    /// <summary>Breakpoints, stepping and inspection of the scripts that run in play mode.</summary>
    public sealed class VoltDebuggerWindow : EditorWindow
    {
        private static readonly VoltDebugger Debugger = new VoltDebugger();
        private MonoScript _script;
        private int _line = 1;
        private Vector2 _scroll;

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            // The simulator attaches this debugger to every run, so breakpoints set before Play are hit from the first line.
            VoltSimulator.NextDebugger = Debugger;
            EditorApplication.playModeStateChanged += _ => VoltSimulator.NextDebugger = Debugger;
        }

        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _script = (MonoScript)EditorGUILayout.ObjectField(_script, typeof(MonoScript), false);
                _line = EditorGUILayout.IntField(_line, GUILayout.Width(60));
                if (GUILayout.Button("Add breakpoint", GUILayout.Width(110)) && _script != null)
                    Debugger.SetBreakpoint(Path.GetFullPath(AssetDatabase.GetAssetPath(_script)), _line);
                if (GUILayout.Button("Clear all", GUILayout.Width(70))) Debugger.ClearAllBreakpoints();
            }
            foreach (KeyValuePair<string, int> breakpoint in Debugger.Breakpoints.ToArray())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(Path.GetFileName(breakpoint.Key) + " : " + breakpoint.Value);
                    if (GUILayout.Button("x", GUILayout.Width(22))) Debugger.ClearBreakpoint(breakpoint.Key, breakpoint.Value);
                }
            }
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = VoltSimulator.IsRunning && !Debugger.IsPaused;
                if (GUILayout.Button("Pause")) Debugger.BreakAtNextLine();
                GUI.enabled = Debugger.IsPaused;
                if (GUILayout.Button("Continue")) Debugger.Resume();
                if (GUILayout.Button("Step over")) Debugger.Resume(VoltStepMode.Over);
                if (GUILayout.Button("Step into")) Debugger.Resume(VoltStepMode.Into);
                if (GUILayout.Button("Step out")) Debugger.Resume(VoltStepMode.Out);
                GUI.enabled = true;
            }
            if (!Debugger.IsPaused)
            {
                EditorGUILayout.HelpBox(VoltSimulator.IsRunning ? "Running." : "Enter play mode to debug the world's scripts.", MessageType.Info);
                return;
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Call stack", EditorStyles.boldLabel);
            List<VoltStackFrameInfo> stack = Debugger.GetCallStack();
            foreach (VoltStackFrameInfo frame in stack)
                EditorGUILayout.LabelField(frame.ClassName + "." + frame.MethodName + "  " + Path.GetFileName(frame.File ?? string.Empty) + ":" + frame.Line);
            if (stack.Count > 0)
            {
                EditorGUILayout.LabelField("Locals", EditorStyles.boldLabel);
                foreach (VoltVariableInfo v in Debugger.GetLocals(stack[0].FrameIndex)) EditorGUILayout.LabelField(v.Name, v.Value + "  (" + v.Kind + ")");
                VoltInstance self = Debugger.GetThis(stack[0].FrameIndex);
                if (self != null)
                {
                    EditorGUILayout.LabelField("Fields", EditorStyles.boldLabel);
                    foreach (VoltVariableInfo v in Debugger.GetFields(self)) EditorGUILayout.LabelField(v.Name, v.Value + "  (" + v.Kind + ")");
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    /// <summary>What the scripts cost while playing.</summary>
    public sealed class VoltProfilerWindow : EditorWindow
    {
        private Vector2 _scroll;

        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            if (!VoltSimulator.IsRunning)
            {
                EditorGUILayout.HelpBox("Enter play mode to profile the world's scripts.", MessageType.Info);
                return;
            }
            VoltRuntime vm = VoltSimulator.World.Vm;
            VoltProfiler p = vm.Profiler;
            p.Detailed = EditorGUILayout.Toggle("Per-method details", p.Detailed);
            EditorGUILayout.LabelField("Script time last frame", p.LastFrame.ScriptMs.ToString("F3") + " ms (peak " + p.Peak.ScriptMs.ToString("F3") + ")");
            EditorGUILayout.LabelField("Instructions last frame", p.LastFrame.Instructions.ToString("N0"));
            EditorGUILayout.LabelField("API calls last frame", p.LastFrame.ApiCalls.ToString("N0"));
            EditorGUILayout.LabelField("Memory (estimate)", (vm.LiveBytesEstimate / 1024).ToString("N0") + " KiB");
            EditorGUILayout.LabelField("Instances / coroutines / queued", p.InstanceCount + " / " + vm.ActiveCoroutineCount + " / " + vm.QueuedEventCount);
            EditorGUILayout.LabelField("Network sent", p.TotalNetworkEventsSent + " messages, " + p.TotalNetworkBytesSent + " bytes");
            EditorGUILayout.LabelField("Errors / throttled calls", p.Errors + " / " + p.ThrottledCalls);
            if (!p.Detailed) return;
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (VoltMethodStats m in p.GetMethodStats().OrderByDescending(m => m.TotalMs).Take(50))
                EditorGUILayout.LabelField(m.ClassName + "." + m.MethodName, m.TotalMs.ToString("F2") + " ms, " + m.Invocations + " calls, " + m.Instructions.ToString("N0") + " instr");
            EditorGUILayout.EndScrollView();
        }
    }

    public sealed class VoltSettingsWindow : EditorWindow
    {
        private static readonly (VoltCapability Bit, string Label)[] Capabilities =
        {
            (VoltCapability.World, "Change world objects"), (VoltCapability.Network, "Networking"), (VoltCapability.Players, "Act on players"),
            (VoltCapability.Input, "Read input"), (VoltCapability.Storage, "Storage"), (VoltCapability.Web, "Web requests"),
            (VoltCapability.Video, "Video"), (VoltCapability.Economy, "Purchases")
        };

        private void OnGUI()
        {
            VoltSettings s = VoltSettings.instance;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("What this world's scripts may use", EditorStyles.boldLabel);
            foreach (var capability in Capabilities)
            {
                bool on = (s.capabilities & (uint)capability.Bit) != 0;
                bool now = EditorGUILayout.ToggleLeft(capability.Label, on);
                if (now != on) s.capabilities = now ? s.capabilities | (uint)capability.Bit : s.capabilities & ~(uint)capability.Bit;
            }
            s.capabilities |= (uint)VoltCapability.Local;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Web domains (one per line, https only)", EditorStyles.boldLabel);
            string text = EditorGUILayout.TextArea(string.Join("\n", s.webDomains ?? new string[0]), GUILayout.MinHeight(60));
            s.webDomains = text.Split('\n').Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Take(16).ToArray();
            EditorGUILayout.Space();
            s.compileOnPlay = EditorGUILayout.ToggleLeft("Compile scripts when entering play mode", s.compileOnPlay);
            if (EditorGUI.EndChangeCheck()) s.SaveNow();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("SDK " + VoltVersion.Sdk + ", bytecode " + VoltVersion.BytecodeMajor + "." + VoltVersion.BytecodeMinor, EditorStyles.miniLabel);
        }
    }

    /// <summary>Shows an API object field (VoltTransform, VoltAudioSource, ...) as the plain object field creators expect.</summary>
    [CustomPropertyDrawer(typeof(VoltObject), true)]
    public sealed class VoltObjectDrawer : PropertyDrawer
    {
        private static readonly Dictionary<Type, Type> Targets = new Dictionary<Type, Type>();

        private static Type TargetOf(Type wrapper)
        {
            if (wrapper == null) return typeof(UnityEngine.Object);
            if (Targets.TryGetValue(wrapper, out Type target)) return target;
            target = typeof(UnityEngine.Object);
            foreach (object attribute in wrapper.GetCustomAttributes(false))
            {
                if (attribute.GetType().Name != "VoltTargetTypeAttribute") continue;
                foreach (System.Reflection.PropertyInfo property in attribute.GetType().GetProperties())
                {
                    if (!(property.GetValue(attribute) is string[] names) || names.Length != 1) continue;
                    foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type found = assembly.GetType(names[0]);
                        if (found != null) { target = found; break; }
                    }
                }
            }
            Targets[wrapper] = target;
            return target;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            SerializedProperty target = property.FindPropertyRelative("EditorTarget");
            if (target == null) { EditorGUI.PropertyField(position, property, label, true); return; }
            Type wrapper = fieldInfo.FieldType.IsArray ? fieldInfo.FieldType.GetElementType() : fieldInfo.FieldType;
            EditorGUI.BeginProperty(position, label, property);
            target.objectReferenceValue = EditorGUI.ObjectField(position, label, target.objectReferenceValue, TargetOf(wrapper), true);
            EditorGUI.EndProperty();
        }
    }
}
